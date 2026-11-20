using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lokn.Core;

namespace Lokn.Modules.Conversations;

/// <summary>Persists conversations in one index file and each conversation's messages in a file of its own.</summary>
/// <remarks>
/// Changes are collected on the UI thread and written on a background thread after a short quiet period, so a long history
/// never blocks the window. Only the index and the conversations that changed are written. A file that fails to read is
/// never overwritten.
/// </remarks>
public sealed partial class ConversationStore
{
	/// <summary>Defines the history file format version: an index file plus one messages file per conversation.</summary>
	private const int FileVersion = 2;

	/// <summary>Defines the older format that kept every conversation's messages in the index file; it is read and rewritten as the current one.</summary>
	private const int LegacyFileVersion = 1;

	/// <summary>Defines the quiet period after a change before it is written, in milliseconds.</summary>
	private const int SaveDelay = 500;

	/// <summary>Defines the longest a change waits while further changes keep arriving, in milliseconds.</summary>
	private const int MaxSaveDelay = 2000;

	/// <summary>Stores the optional local history index file path.</summary>
	private readonly string? _path;

	/// <summary>Reports whether the index file was read, which happens on first use.</summary>
	private bool _isLoaded;

	/// <summary>Tracks whether the loaded index can safely be replaced.</summary>
	private bool _canSave = true;

	/// <summary>Stores the conversations whose messages file could not be read, so it is never replaced.</summary>
	private readonly HashSet<Guid> _unreadable = [];

	/// <summary>Reports whether conversations changed since they were last handed to the writer.</summary>
	private bool _isIndexDirty;

	/// <summary>Stores the projects an index from before projects had a file of their own held, written back until the Projects module's file holds them; null after.</summary>
	private JsonElement? _legacyProjects;

	/// <summary>Stores the conversations whose messages changed since they were last handed to the writer.</summary>
	private readonly HashSet<Guid> _dirtyMessages = [];

	/// <summary>Stores the deleted conversations whose messages file is still to be removed.</summary>
	private readonly HashSet<Guid> _deletedMessages = [];

	/// <summary>Writes pending changes once the quiet period ends.</summary>
	private readonly Timer _saveTimer;

	/// <summary>Guards <see cref="_pending"/> and <see cref="_pendingSince"/>, shared by the UI thread and the writer.</summary>
	private readonly Lock _pendingLock = new();

	/// <summary>Serializes writes, so a flush waits for a write already under way.</summary>
	private readonly Lock _writeLock = new();

	/// <summary>Stores the snapshots waiting to be written.</summary>
	private PendingWrites _pending = new();

	/// <summary>Stores when the oldest pending snapshot was taken, as a <see cref="Stopwatch"/> timestamp, or zero when none waits.</summary>
	private long _pendingSince;

	/// <summary>Stores the most recent history read or write error.</summary>
	private volatile string? _lastError;

	/// <summary>Defines compact JSON with named account profiles.</summary>
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		Converters =
		{
			new JsonStringEnumConverter<SettingsProfile>( allowIntegerValues: false ),
		},
	};

	/// <summary>Gets the most recent history read or write error.</summary>
	public string? LastError => _lastError;

	/// <summary>Gets the per-user conversation history index file path, under the process's data folder.</summary>
	public static string DefaultPath => LoknPaths.Combine( "conversations.json" );

	/// <summary>Saves every change not saved yet; the files are written off the UI thread after a short quiet period.</summary>
	public void Save()
	{
		if ( !TakeChanges() )
			return;

		long since;
		lock ( _pendingLock )
			since = _pendingSince;
		var waited = Stopwatch.GetElapsedTime( since ).TotalMilliseconds;
		_saveTimer.Change( (int)Math.Clamp( MaxSaveDelay - waited, 0, SaveDelay ), Timeout.Infinite );
	}

	/// <summary>Writes every change not saved yet before returning, after any write already under way.</summary>
	public void Flush()
	{
		TakeChanges();
		WritePending();
	}

	/// <summary>Reads the history files again in place of what was read, such as after another process that kept them, a daemon, wrote them and exited.</summary>
	/// <remarks>
	/// Call it on the UI thread. Changes this store had not written yet were made to the copy the other process replaced, so they are dropped rather than
	/// written over its files. Messages read so far are read again on their next use, readers of the conversations and of each conversation's messages
	/// rebuild, and <see cref="Deleted"/> is raised for each conversation the files no longer hold. A mirror or an in-memory store has no files and is left alone.
	/// </remarks>
	public void Reload()
	{
		if ( _path is null || _isMirror )
			return;

		ClearChanges();
		_saveTimer.Change( Timeout.Infinite, Timeout.Infinite );
		// A write under way finishes first; what it had not taken yet is dropped.
		lock ( _writeLock )
		{
			lock ( _pendingLock )
			{
				_pending = new();
				_pendingSince = 0;
			}
		}

		var previous = _conversations.Keys.ToArray();
		_conversations.Clear();
		_messages.Clear();
		_snapshots.Clear();
		_unreadable.Clear();
		_legacyProjects = null;
		_canSave = true;
		_lastError = null;
		_isLoaded = false;
		EnsureLoaded();
		foreach ( var version in _messageVersions.Values )
			version.Value++;
		_version.Value++;
		foreach ( var id in previous.Where( id => !_conversations.ContainsKey( id ) ) )
			Deleted?.Invoke( id );
	}

	/// <summary>Reads the index file once, before the first query or change.</summary>
	/// <remarks>
	/// History in the older single-file format is loaded whole and queued to be rewritten in the current one. Projects are the
	/// Projects module's: an index from before they had a file of their own still holds them, and keeps them until that file does.
	/// A chat whose project no longer exists, or belongs to another profile, leaves it, unless the projects could not all be read.
	/// </remarks>
	internal void EnsureLoaded()
	{
		if ( _isLoaded )
			return;

		_isLoaded = true;
		if ( _path is null || !File.Exists( _path ) )
			return;

		try
		{
			HistoryDocument document;
			using ( var stream = File.OpenRead( _path ) )
				document = JsonSerializer.Deserialize<HistoryDocument>( stream, JsonOptions ) ?? throw new JsonException( "The history document is empty." );
			var isLegacy = document.Version == LegacyFileVersion;
			if ( (document.Version != FileVersion && !isLegacy) || document.Conversations is null || (isLegacy && document.Messages is null) )
				throw new JsonException( "The history document version or collections are unsupported." );

			if ( document.Conversations.Any( item => item is null ) )
				throw new JsonException( "The history document contains an empty conversation." );

			var conversations = document.Conversations.ToDictionary( item => item.Id );
			foreach ( var conversation in conversations.Values )
			{
				if ( conversation.Id == Guid.Empty || !Enum.IsDefined( conversation.Profile ) || string.IsNullOrWhiteSpace( conversation.Title ) )
					throw new JsonException( "The history document contains an invalid conversation." );
			}

			var messages = isLegacy ? document.Messages! : [];
			foreach ( var item in messages )
			{
				if ( !conversations.ContainsKey( item.Key ) || !IsValid( item.Value ) )
					throw new JsonException( "The history document contains invalid message references." );
			}

			if ( document.Projects is { ValueKind: JsonValueKind.Array } projects )
				_legacyProjects = projects;

			var isDetached = false;
			foreach ( var item in conversations )
			{
				var conversation = BuildLoadedConversation( item.Value, messages.GetValueOrDefault( item.Key ) );
				if ( conversation.ProjectId is { } projectId && _projects is { IsComplete: true } known && known.Find( projectId )?.Profile != conversation.Profile )
				{
					conversation = conversation with { ProjectId = null, Worktree = null };
					isDetached = true;
				}
				_conversations.Add( item.Key, conversation );
			}

			// Every conversation's messages move into a file of their own; the index is replaced only after they are all written.
			foreach ( var item in messages )
			{
				_messages.Add( item.Key, item.Value );
				_dirtyMessages.Add( item.Key );
			}

			if ( !isLegacy && !isDetached )
				return;

			_isIndexDirty = true;
			Save();
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or JsonException or ArgumentException )
		{
			_canSave = false;
			MarkFailed( $"Could not load conversation history: {error.Message} The original file is preserved; changes stay in memory." );
		}
	}

	/// <summary>Gets a conversation's loaded messages, reading its messages file the first time.</summary>
	private List<ConversationMessage> EnsureMessages( Guid conversationId )
	{
		if ( !_messages.TryGetValue( conversationId, out var messages ) )
			_messages.Add( conversationId, messages = ReadMessages( conversationId ) );
		return messages;
	}

	/// <summary>Reads one conversation's messages file, or none when it has none or it cannot be read.</summary>
	private List<ConversationMessage> ReadMessages( Guid conversationId )
	{
		if ( _path is null || _isMirror || BuildMessagesPath( conversationId ) is not { } path || !File.Exists( path ) )
			return [];

		try
		{
			MessagesDocument document;
			using ( var stream = File.OpenRead( path ) )
				document = JsonSerializer.Deserialize<MessagesDocument>( stream, JsonOptions ) ?? throw new JsonException( "The messages document is empty." );
			if ( document.Version != FileVersion || document.ConversationId != conversationId || document.Messages is null || !IsValid( document.Messages ) )
				throw new JsonException( "The messages document is unsupported or belongs to another conversation." );

			// Files from before messages were compacted still hold the parts Loom never reads; they leave memory as the chat loads.
			return [.. document.Messages.Select( message => message.RichContent is null ? message : message with { RichContent = CompactRichContent( message.RichContent ) } )];
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or JsonException or ArgumentException )
		{
			_unreadable.Add( conversationId );
			MarkFailed( $"Could not load a conversation's messages: {error.Message} The original file is preserved; changes stay in memory." );
			return [];
		}
	}

	/// <summary>Reads the prompts the user sent in each of the given chats, oldest first.</summary>
	/// <remarks>
	/// Call it on the UI thread. Messages already loaded are copied at once; the files of the rest are read on a worker, never
	/// kept, so a chat that is not open stays unloaded. A chat whose messages cannot be read has no prompts.
	/// </remarks>
	public Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> ReadPromptsAsync( IReadOnlyList<Guid> conversationIds, CancellationToken cancellationToken )
	{
		var prompts = new Dictionary<Guid, IReadOnlyList<string>>();
		var unread = new List<(Guid Id, string Path)>();
		foreach ( var id in conversationIds )
		{
			if ( _messages.TryGetValue( id, out var messages ) )
				prompts[id] = [.. messages.Where( message => message.Role == "user" && message.Text.Length > 0 ).Select( message => message.Text )];
			else if ( !_isMirror && BuildMessagesPath( id ) is { } path )
				unread.Add( (id, path) );
		}

		return Task.Run<IReadOnlyDictionary<Guid, IReadOnlyList<string>>>( () =>
		{
			foreach ( var (id, path) in unread )
			{
				cancellationToken.ThrowIfCancellationRequested();
				prompts[id] = ReadPrompts( path );
			}
			return prompts;
		}, cancellationToken );
	}

	/// <summary>Reads the user's prompts from a messages file without its rich content; none when it is missing or cannot be read.</summary>
	private static List<string> ReadPrompts( string path )
	{
		try
		{
			// The writer may replace the file meanwhile; sharing delete lets it, while this read finishes on the old file.
			using var stream = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete );
			var document = JsonSerializer.Deserialize<PromptsDocument>( stream, JsonOptions );
			return [.. (document?.Messages ?? []).Where( message => message?.Role == "user" && message.Text is { Length: > 0 } ).Select( message => message!.Text! )];
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or JsonException )
		{
			return [];
		}
	}

	/// <summary>Reports whether a stored message list is complete: no empty entries and unique identifiers.</summary>
	private static bool IsValid( List<ConversationMessage>? messages ) =>
		messages is not null
		&& !messages.Any( message => message is null || message.Id == Guid.Empty || message.Role is null || message.Text is null )
		&& messages.Select( message => message.Id ).Distinct().Count() == messages.Count;

	/// <summary>Brings a conversation saved by an older Lokn up to date.</summary>
	/// <remarks>
	/// One saved before chats recorded their start takes its first message's time, or the last activity of an empty chat.
	/// A generated title in an older form, such as Omp's <c>🪟 PANEL: Hide panel outline with single panel</c>, is cut down
	/// to the current form at once, and marked ungenerated so the chat's next turn asks for a proper one. A blank chosen folder
	/// means none. A chat outside projects that started Omp before chats defaulted to the home folder ran in the application's
	/// current directory, and keeps it, so its agent does not wake up in another folder.
	/// </remarks>
	private static ConversationInfo BuildLoadedConversation( ConversationInfo conversation, List<ConversationMessage>? messages )
	{
		if ( conversation.CreatedAt == default )
			conversation = conversation with { CreatedAt = messages is [var first, ..] ? first.CreatedAt : conversation.UpdatedAt };

		if ( conversation.IsTitleGenerated && ComputeGeneratedTitle( conversation.Title ) is { Length: > 0 } title && title != conversation.Title )
			conversation = conversation with { Title = title, IsTitleGenerated = false };

		if ( conversation.Directory is { } directory && string.IsNullOrWhiteSpace( directory ) )
			conversation = conversation with { Directory = null };

		if ( conversation.Worktree is { } worktree && (conversation.ProjectId is null || string.IsNullOrWhiteSpace( worktree )) )
			conversation = conversation with { Worktree = null };

		if ( conversation is { ProjectId: null, Directory: null, BackendSessionPath: not null } )
			conversation = conversation with { Directory = Environment.CurrentDirectory };

		return conversation;
	}

	/// <summary>Gets the messages file of a conversation: a folder named after the index file holds one per conversation.</summary>
	private string? BuildMessagesPath( Guid conversationId ) => _path is null ? null
		: Path.Combine( Path.GetDirectoryName( _path )!, Path.GetFileNameWithoutExtension( _path ), conversationId.ToString( "N" ) + ".json" );

	/// <summary>Hands snapshots of everything changed to the writer; the snapshots share the immutable records, so taking them is cheap.</summary>
	/// <returns>Whether anything waits to be written.</returns>
	private bool TakeChanges()
	{
		var isDirty = _isIndexDirty || _dirtyMessages.Count > 0 || _deletedMessages.Count > 0;
		if ( _path is null || _isMirror || !isDirty )
		{
			ClearChanges();
			return false;
		}

		if ( !_canSave )
		{
			ClearChanges();
			MarkFailed( "Conversation changes stay in memory. Correct the history file and restart Lokn to save them." );
			return false;
		}

		var messages = new Dictionary<Guid, List<ConversationMessage>?>();
		foreach ( var id in _dirtyMessages )
		{
			if ( !_unreadable.Contains( id ) && _messages.TryGetValue( id, out var list ) )
				messages[id] = [.. list];
		}
		foreach ( var id in _deletedMessages )
			messages[id] = null;
		// Projects an older index held stay in it until the projects file holds them, so a failed import can run again.
		if ( _legacyProjects is not null && _projects is { IsSaved: true } )
			_legacyProjects = null;
		IndexDocument? index = _isIndexDirty ? new() { Projects = _legacyProjects, Conversations = [.. _conversations.Values] } : null;
		ClearChanges();

		lock ( _pendingLock )
		{
			if ( index is not null )
				_pending.Index = index;
			foreach ( var (id, list) in messages )
				_pending.Messages[id] = list;
			if ( _pendingSince == 0 )
				_pendingSince = Stopwatch.GetTimestamp();
		}
		return true;
	}

	/// <summary>Forgets what changed once it is handed to the writer or cannot be saved.</summary>
	private void ClearChanges()
	{
		_isIndexDirty = false;
		_dirtyMessages.Clear();
		_deletedMessages.Clear();
	}

	/// <summary>Writes the pending snapshots: changed messages files first, then the index, which waits until they all succeed.</summary>
	/// <remarks>Runs on the timer thread, or on the UI thread for <see cref="Flush"/>. A failed write stays pending for the next save.</remarks>
	private void WritePending()
	{
		lock ( _writeLock )
		{
			PendingWrites batch;
			lock ( _pendingLock )
			{
				batch = _pending;
				_pending = new();
				_pendingSince = 0;
			}
			if ( batch.Index is null && batch.Messages.Count == 0 )
				return;

			string? failure = null;
			foreach ( var (id, messages) in batch.Messages )
			{
				var path = BuildMessagesPath( id )!;
				try
				{
					if ( messages is not null )
						WriteFile( path, new MessagesDocument { ConversationId = id, Messages = messages } );
					else if ( File.Exists( path ) )
						File.Delete( path );
				}
				catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
				{
					failure ??= error.Message;
					lock ( _pendingLock )
						_pending.Messages.TryAdd( id, messages );
				}
			}

			if ( batch.Index is { } index )
			{
				try
				{
					if ( failure is null )
						WriteFile( _path!, index );
				}
				catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
				{
					failure = error.Message;
				}

				if ( failure is not null )
				{
					lock ( _pendingLock )
						_pending.Index ??= index;
				}
			}

			if ( failure is null )
				_lastError = null;
			else
				MarkFailed( $"Conversation changes stay in memory but could not be saved: {failure}" );
		}
	}

	/// <summary>Writes one document through a sibling temporary file, so the previous complete file survives a failed write.</summary>
	private static void WriteFile<T>( string path, T document )
	{
		Directory.CreateDirectory( Path.GetDirectoryName( path )! );
		var temporary = path + ".tmp";
		using ( var stream = File.Create( temporary ) )
			JsonSerializer.Serialize( stream, document, JsonOptions );
		File.Move( temporary, path, overwrite: true );
	}

	/// <summary>Reports a history error while keeping the live session available.</summary>
	private void MarkFailed( string error )
	{
		_lastError = error;
		Failed?.Invoke( error );
	}

	/// <summary>Collects the snapshots waiting for the writer; the newest snapshot of each file replaces older ones.</summary>
	private sealed class PendingWrites
	{
		/// <summary>Gets or sets the index to write, or null when it did not change.</summary>
		internal IndexDocument? Index
		{
			get; set;
		}

		/// <summary>Gets the messages to write per conversation; null deletes the conversation's file.</summary>
		internal Dictionary<Guid, List<ConversationMessage>?> Messages { get; } = [];
	}

	/// <summary>Reads a history index in the current format, or the older one with every conversation's messages.</summary>
	private sealed record HistoryDocument
	{
		/// <summary>Gets the file format version.</summary>
		public int Version { get; init; }

		/// <summary>Gets the projects an index held before they had a file of their own, or null; the Projects module imports them.</summary>
		public JsonElement? Projects { get; init; }

		/// <summary>Gets all profile-pinned conversations.</summary>
		public List<ConversationInfo> Conversations { get; init; } = [];

		/// <summary>Gets messages keyed by their owning conversation identifier; only the older format has them.</summary>
		public Dictionary<Guid, List<ConversationMessage>>? Messages { get; init; }
	}

	/// <summary>Stores every project and conversation, without their messages.</summary>
	private sealed record IndexDocument
	{
		/// <summary>Gets the file format version.</summary>
		public int Version { get; init; } = FileVersion;

		/// <summary>Gets the projects an older index held while the projects file does not hold them yet, or null, which is left out.</summary>
		[JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
		public JsonElement? Projects { get; init; }

		/// <summary>Gets all profile-pinned conversations.</summary>
		public List<ConversationInfo> Conversations { get; init; } = [];
	}

	/// <summary>Stores one conversation's chronological messages.</summary>
	private sealed record MessagesDocument
	{
		/// <summary>Gets the file format version.</summary>
		public int Version { get; init; } = FileVersion;

		/// <summary>Gets the conversation the messages belong to.</summary>
		public Guid ConversationId { get; init; }

		/// <summary>Gets the messages, oldest first.</summary>
		public List<ConversationMessage> Messages { get; init; } = [];
	}

	/// <summary>Reads only who sent each message and its text from a messages file, skipping the rich content.</summary>
	private sealed record PromptsDocument
	{
		/// <summary>Gets the messages, oldest first.</summary>
		public List<PromptEntry?>? Messages { get; init; }
	}

	/// <summary>Holds who sent one message and its plain text.</summary>
	private sealed record PromptEntry
	{
		/// <summary>Gets the sender's role, such as <c>user</c>.</summary>
		public string? Role { get; init; }

		/// <summary>Gets the message's plain text.</summary>
		public string? Text { get; init; }
	}
}
