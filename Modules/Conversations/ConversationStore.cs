using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Projects;

namespace Lokn.Modules.Conversations;

/// <summary>Owns conversation history on the UI thread with profile-isolated queries; the projects chats belong to come from a <see cref="Projects.ProjectStore"/>.</summary>
/// <remarks>
/// Readers of conversations rebuild when that list changes; readers of a conversation's messages rebuild only
/// when its own messages change, so a streaming reply does not rebuild the sidebar or other chats.
/// </remarks>
public sealed partial class ConversationStore
{
	/// <summary>Stores the projects chats belong to, or null until the Conversations module attaches the Projects module's.</summary>
	private ProjectStore? _projects;

	/// <summary>Reports whether this store follows project deletions, which it does while attached.</summary>
	private bool _isFollowingProjects;

	/// <summary>Stores conversations by stable identifier.</summary>
	private readonly Dictionary<Guid, ConversationInfo> _conversations = [];

	/// <summary>Stores the message history of each conversation read so far, by conversation identifier.</summary>
	private readonly Dictionary<Guid, List<ConversationMessage>> _messages = [];

	/// <summary>Stores the message snapshot handed to readers per conversation until its messages change.</summary>
	private readonly Dictionary<Guid, ConversationMessage[]> _snapshots = [];

	/// <summary>Tracks each conversation's message changes apart from the conversation list.</summary>
	private readonly Dictionary<Guid, State<int>> _messageVersions = [];

	/// <summary>Tracks changes to conversations, read by history and conversation panels.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Stores the conversations whose agent is working, shown with an orb in the sidebar.</summary>
	private readonly HashSet<Guid> _working = [];

	/// <summary>Tracks changes to the working conversations apart from history, so a turn starting or ending rebuilds only their readers.</summary>
	private readonly State<int> _workingVersion = new( 0 );

	/// <summary>Reports a failed local history read or write; a write failure is reported on the thread that wrote.</summary>
	public event Action<string>? Failed;

	/// <summary>Creates a store over an optional local history file, which is read on first use, before its projects are attached.</summary>
	/// <remarks>
	/// Reading late lets a module replacing a running one flush the old store before the new one reads. The Conversations module
	/// attaches the Projects module's store when it starts, before the history is read.
	/// </remarks>
	public ConversationStore( string? path = null )
	{
		_path = path is null ? null : Path.GetFullPath( path );
		_saveTimer = new( _ => WritePending(), null, Timeout.Infinite, Timeout.Infinite );
	}

	/// <summary>Creates a store whose chats belong to the given projects, over an optional local history file, which is read on first use.</summary>
	/// <remarks>The store follows project deletions at once, as it does once a module attaches it.</remarks>
	public ConversationStore( ProjectStore projects, string? path = null ) : this( path ) => Attach( projects );

	/// <summary>Gets the projects chats belong to.</summary>
	/// <exception cref="InvalidOperationException">No project store is attached yet.</exception>
	public ProjectStore ProjectStore => _projects ?? throw new InvalidOperationException( "The conversation store has no projects attached." );

	/// <summary>Attaches the projects chats belong to and follows their deletions, which take the chats out of the deleted project.</summary>
	/// <exception cref="InvalidOperationException">The store already belongs to other projects.</exception>
	public void Attach( ProjectStore projects )
	{
		if ( _projects is not null && _projects != projects )
			throw new InvalidOperationException( "The conversation store already belongs to other projects." );

		_projects = projects;
		if ( _isFollowingProjects )
			return;

		projects.Deleted += DetachProject;
		_isFollowingProjects = true;
	}

	/// <summary>Stops following project deletions, such as when the module that owns this store stops; the projects stay attached for reading.</summary>
	public void Detach()
	{
		if ( !_isFollowingProjects )
			return;

		_projects!.Deleted -= DetachProject;
		_isFollowingProjects = false;
	}

	/// <summary>Gets conversations in one profile and project in the order the user arranged them, so activity never reorders them: tasks started
	/// since the user last moved one come first, newest first, then the ones placed, in their <see cref="ConversationInfo.Position"/> order.</summary>
	public IReadOnlyList<ConversationInfo> Conversations( SettingsProfile profile, Guid? projectId = null )
	{
		EnsureLoaded();
		_ = _version.Value;
		return _conversations.Values
			.Where( conversation => conversation.Profile == profile && conversation.ProjectId == projectId )
			.OrderBy( conversation => conversation.Position ?? -1 )
			.ThenByDescending( conversation => conversation.CreatedAt )
			.ToArray();
	}

	/// <summary>Gets a conversation by identifier or throws when it no longer exists.</summary>
	public ConversationInfo Get( Guid id ) =>
		Find( id ) ?? throw new KeyNotFoundException( "The conversation no longer exists." );

	/// <summary>Gets a conversation by identifier, or null when it no longer exists.</summary>
	public ConversationInfo? Find( Guid id )
	{
		EnsureLoaded();
		_ = _version.Value;
		return _conversations.GetValueOrDefault( id );
	}

	/// <summary>Gets This PC's folder a chat outside projects works in until one is chosen for it: the user's home folder.</summary>
	public static string DefaultDirectory => Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );

	/// <summary>Gets or sets the home folder of the PC a mirror shows the chats of, as its host reported it in <c>sync</c>; null for This PC's own.</summary>
	/// <remarks>A paired host's chat outside projects works in that host's home folder, which a path of This PC's disk would not name.</remarks>
	public string? HostHome
	{
		get; set;
	}

	/// <summary>Gets the folder a chat outside projects works in until one is chosen for it: the home folder of the PC whose chats these are.</summary>
	public string HomeDirectory => HostHome ?? DefaultDirectory;

	/// <summary>Gets the folder a chat works in: its project's worktree or directory, else the folder chosen for it, else <see cref="HomeDirectory"/>.</summary>
	public string GetDirectory( Guid id )
	{
		var conversation = Get( id );
		return conversation.ProjectId is { } projectId && _projects?.Find( projectId ) is { } project ? conversation.Worktree ?? project.Directory
			: conversation.Directory ?? HomeDirectory;
	}

	/// <summary>Gets a snapshot of a conversation's chronological messages, reading them from disk the first time.</summary>
	/// <remarks>Reading it rebuilds the reader when this conversation's messages change, and only then.</remarks>
	public IReadOnlyList<ConversationMessage> GetMessages( Guid conversationId )
	{
		_ = Get( conversationId );
		if ( !_messageVersions.TryGetValue( conversationId, out var version ) )
			_messageVersions.Add( conversationId, version = new( 0 ) );
		_ = version.Value;
		if ( !_snapshots.TryGetValue( conversationId, out var snapshot ) )
			_snapshots.Add( conversationId, snapshot = [.. EnsureMessages( conversationId )] );
		return snapshot;
	}

	/// <summary>Takes a deleted project's chats out of it, keeping them and their messages in the profile's ungrouped history.</summary>
	private void DetachProject( ProjectInfo project )
	{
		EnsureLoaded();
		var chats = _conversations.Values.Where( item => item.ProjectId == project.Id ).ToArray();
		foreach ( var conversation in chats )
			_conversations[conversation.Id] = conversation with
			{
				ProjectId = null,
				Worktree = null,
				Position = null,
			};

		if ( chats.Length > 0 )
			MarkChanged();
	}

	/// <summary>Creates an empty conversation pinned to its account profile; without a chat, it is a task of other panels only.</summary>
	/// <remarks>A mirror adds it at once, so its caller can open it, and sends it to the host, which creates it under the same identifier and time.</remarks>
	public ConversationInfo CreateConversation( SettingsProfile profile, Guid? projectId = null, string title = "New chat", bool hasChat = true ) =>
		CreateConversation( Guid.NewGuid(), DateTimeOffset.UtcNow, profile, projectId, title, hasChat );

	/// <summary>Creates an empty conversation with a given identifier and start, unless one with the identifier exists, such as a mirror's own creation coming back from the host.</summary>
	private ConversationInfo CreateConversation( Guid id, DateTimeOffset createdAt, SettingsProfile profile, Guid? projectId, string title, bool hasChat )
	{
		EnsureLoaded();
		ArgumentException.ThrowIfNullOrWhiteSpace( title );
		if ( _conversations.TryGetValue( id, out var known ) )
			return known;

		EnsureProjectProfile( profile, projectId );
		var args = new { id, createdAt, profile, projectId, title, hasChat };
		var conversation = new ConversationInfo( id, profile, projectId, title.Trim(), createdAt, CreatedAt: createdAt, HasChat: hasChat );
		_conversations.Add( conversation.Id, conversation );
		_messages.Add( conversation.Id, [] );
		MarkChanged();
		// A mirror shows it at once, so its caller can open it, and the host's copy coming back finds it here; the host creates it under the same identifier.
		MarkMutated( nameof( CreateConversation ), args );
		return conversation;
	}

	/// <summary>Gives a task without a chat one, keeping its title and layout.</summary>
	public void AddChat( Guid id )
	{
		var conversation = Get( id );
		if ( conversation.HasChat )
			return;

		_conversations[id] = conversation with
		{
			HasChat = true,
		};
		MarkChanged();
		MarkMutated( nameof( AddChat ), new { id } );
	}

	/// <summary>Names a task as the user chose; no generated title replaces it afterwards, even one equal to it.</summary>
	public void RenameConversation( Guid id, string title )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( title );
		var conversation = Get( id );
		if ( conversation.Title == title.Trim() && conversation.IsTitleChosen )
			return;

		_conversations[id] = conversation with
		{
			Title = title.Trim(),
			IsTitleChosen = true,
		};
		MarkChanged();
		MarkMutated( nameof( RenameConversation ), new { id, title } );
	}

	/// <summary>Updates a conversation title and project as the user chose them; a changed title is never replaced by a generated one.</summary>
	public void UpdateConversation( Guid id, string title, Guid? projectId )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( title );
		var conversation = Get( id );
		EnsureProjectProfile( conversation.Profile, projectId );
		var updated = conversation with
		{
			Title = title.Trim(),
			ProjectId = projectId,
			Worktree = projectId == conversation.ProjectId ? conversation.Worktree : null,
			Position = projectId == conversation.ProjectId ? conversation.Position : null,
			IsTitleChosen = conversation.IsTitleChosen || title.Trim() != conversation.Title,
		};
		// The host's echo of a mirror's own change finds it applied already.
		if ( updated == conversation )
			return;

		_conversations[id] = updated;
		MarkChanged();
		MarkMutated( nameof( UpdateConversation ), new { id, title, projectId } );
	}

	/// <summary>Defines the most words a generated title keeps.</summary>
	public const int GeneratedTitleWords = 4;

	/// <summary>Sets a title Lokn made up, from the first prompt or a model's summary, unless the user named the chat.</summary>
	/// <param name="id">The conversation.</param>
	/// <param name="title">The new title; a model's is cleaned by <see cref="ComputeGeneratedTitle"/>.</param>
	/// <param name="isGenerated">Whether a model summarized it, so no further summary is asked for.</param>
	/// <returns>Whether the title is set, false when the user named the chat or the title is empty.</returns>
	public bool SetAutomaticTitle( Guid id, string title, bool isGenerated )
	{
		var conversation = Get( id );
		var args = new { id, title, isGenerated };
		title = isGenerated ? ComputeGeneratedTitle( title ) : title.Trim();
		if ( conversation.IsTitleChosen || title.Length == 0 )
			return false;
		if ( conversation.Title == title && (conversation.IsTitleGenerated || !isGenerated) )
			return true;

		_conversations[id] = conversation with
		{
			Title = title,
			IsTitleGenerated = conversation.IsTitleGenerated || isGenerated,
		};
		MarkChanged();
		MarkMutated( nameof( SetAutomaticTitle ), args );
		return true;
	}

	/// <summary>Cleans a model's title into a few plain words: its first non-blank line, without an icon, Markdown, a <c>Title:</c> label,
	/// a category code such as Omp's <c>FEED:</c>, quotes or closing punctuation, cut to <see cref="GeneratedTitleWords"/> words.</summary>
	public static string ComputeGeneratedTitle( string text )
	{
		var line = text.Split( '\n' ).Select( part => part.Trim() ).FirstOrDefault( part => part.Length > 0 ) ?? "";
		line = SkipLeadIn( line.Replace( "**", "" ).Replace( "`", "" ) );
		if ( line.StartsWith( "Title:", StringComparison.OrdinalIgnoreCase ) )
			line = SkipLeadIn( line["Title:".Length..] );

		var words = line.Split( ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
		if ( words is [var code, _, ..] && code.Length > 2 && code[^1] == ':' && code[..^1].All( letter => char.IsUpper( letter ) || char.IsDigit( letter ) || letter is '-' or '_' ) )
			words = words[1..];

		return string.Join( ' ', words.Take( GeneratedTitleWords ) ).TrimEnd( '.', '!', '?', ':', ';', ',', '"', '\'', '“', '”' ).Trim();

		// An icon, Markdown marks, quotes and spaces before the first word.
		static string SkipLeadIn( string value )
		{
			var start = 0;
			while ( start < value.Length && !char.IsLetterOrDigit( value[start] ) )
				start++;
			return value[start..];
		}
	}

	/// <summary>Pins a chat to the top of its project, or of the chats outside projects, or unpins it.</summary>
	public void SetPinned( Guid id, bool isPinned )
	{
		var conversation = Get( id );
		if ( conversation.IsPinned == isPinned )
			return;

		_conversations[id] = conversation with
		{
			IsPinned = isPinned,
		};
		MarkChanged();
		MarkMutated( nameof( SetPinned ), new { id, isPinned } );
	}

	/// <summary>Archives a chat, which leaves the sidebar's list and unloads its messages once they are handed to the writer; or brings it back.</summary>
	/// <remarks>A mirror only sends the change; its host's store, which owns the messages, unloads them.</remarks>
	public void SetArchived( Guid id, bool isArchived )
	{
		var conversation = Get( id );
		if ( conversation.IsArchived == isArchived )
			return;

		_conversations[id] = conversation with
		{
			IsArchived = isArchived,
		};
		MarkChanged();
		MarkMutated( nameof( SetArchived ), new { id, isArchived } );
		if ( isArchived )
			UnloadMessages( id );
	}

	/// <summary>Unloads a chat's messages from memory once what is not written yet is handed to the writer; they are read from its file when next needed.</summary>
	/// <remarks>A mirror keeps its messages, which only its host can read again; so does a chat whose file could not be read.</remarks>
	private void UnloadMessages( Guid id )
	{
		if ( _isMirror || _path is null || _unreadable.Contains( id ) || !_messages.ContainsKey( id ) )
			return;

		if ( _dirtyMessages.Contains( id ) )
			Save();
		_messages.Remove( id );
	}

	/// <summary>Moves a task to another's place among its project's tasks, or the chats outside projects; the tasks between shift toward where
	/// it was, and every one of them keeps its place from then on.</summary>
	/// <remarks>Moving again is not a no-op, so a mirror only sends the move and shows it once the host's operation comes back.</remarks>
	/// <exception cref="ArgumentException">The two tasks are in different projects or profiles.</exception>
	public void MoveConversation( Guid id, Guid target )
	{
		var moved = Get( id );
		var other = Get( target );
		if ( moved.Profile != other.Profile || moved.ProjectId != other.ProjectId )
			throw new ArgumentException( "A task moves only among the tasks of its own project." );
		if ( id == target )
			return;

		var args = new { id, target };
		if ( TryForward( nameof( MoveConversation ), args ) )
			return;

		var order = Conversations( moved.Profile, moved.ProjectId ).Select( conversation => conversation.Id ).ToList();
		var to = order.IndexOf( target );
		order.Remove( id );
		order.Insert( to, id );
		for ( var index = 0; index < order.Count; index++ )
		{
			var conversation = _conversations[order[index]];
			if ( conversation.Position != index )
				_conversations[conversation.Id] = conversation with { Position = index };
		}
		MarkChanged();
		Mutated?.Invoke( StoreOp.Build( StoreOp.Conversations, nameof( MoveConversation ), args ) );
	}

	/// <summary>Stops a chat from suggesting a new chat when a prompt looks like a new task.</summary>
	public void SetTaskCheckOff( Guid id )
	{
		var conversation = Get( id );
		if ( conversation.IsTaskCheckOff )
			return;

		_conversations[id] = conversation with
		{
			IsTaskCheckOff = true,
		};
		MarkChanged();
		MarkMutated( nameof( SetTaskCheckOff ), new { id } );
	}

	/// <summary>Stores the Omp session directory used to resume this conversation.</summary>
	public void SaveBackendSession( Guid id, string? sessionDirectory )
	{
		var conversation = Get( id );
		if ( conversation.BackendSessionPath == sessionDirectory )
			return;

		_conversations[id] = conversation with
		{
			BackendSessionPath = sessionDirectory,
		};
		MarkChanged();
		MarkMutated( nameof( SaveBackendSession ), new { id, sessionDirectory } );
	}

	/// <summary>Sets the folder a chat outside projects works in, or clears it back to the application's current directory.</summary>
	/// <exception cref="InvalidOperationException">The chat belongs to a project, which decides its folder.</exception>
	public void SetDirectory( Guid id, string? directory )
	{
		var conversation = Get( id );
		if ( conversation.ProjectId is not null )
			throw new InvalidOperationException( "A project chat works in its project's directory." );

		var value = string.IsNullOrWhiteSpace( directory ) ? null : Path.GetFullPath( directory );
		if ( conversation.Directory == value )
			return;

		_conversations[id] = conversation with
		{
			Directory = value,
		};
		MarkChanged();
		MarkMutated( nameof( SetDirectory ), new { id, directory } );
	}

	/// <summary>Moves a chat into a git worktree: a project chat records it as its worktree, and the project's own directory clears it; a chat outside projects takes it as its folder.</summary>
	public void SetWorktree( Guid id, string worktree )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( worktree );
		var conversation = Get( id );
		if ( conversation.ProjectId is not { } projectId )
		{
			SetDirectory( id, worktree );
			return;
		}

		var value = IsSameFolder( worktree, ProjectStore.Get( projectId ).Directory ) ? null : Path.GetFullPath( worktree );
		if ( conversation.Worktree == value )
			return;

		_conversations[id] = conversation with
		{
			Worktree = value,
		};
		MarkChanged();
		MarkMutated( nameof( SetWorktree ), new { id, worktree } );
	}

	/// <summary>Moves the chats working in a removed worktree back to the main checkout: project chats to their project's directory, others to <paramref name="mainPath"/>.</summary>
	public void ReleaseWorktree( string worktree, string mainPath )
	{
		EnsureLoaded();

		var isChanged = false;
		foreach ( var conversation in _conversations.Values.ToArray() )
		{
			if ( conversation.Worktree is { } inside && IsSameFolder( inside, worktree ) )
				_conversations[conversation.Id] = conversation with { Worktree = null };
			else if ( conversation is { ProjectId: null, Directory: { } folder } && IsSameFolder( folder, worktree ) )
				_conversations[conversation.Id] = conversation with { Directory = Path.GetFullPath( mainPath ) };
			else
				continue;

			isChanged = true;
		}

		if ( !isChanged )
			return;

		MarkChanged();
		MarkMutated( nameof( ReleaseWorktree ), new { worktree, mainPath } );
	}

	/// <summary>Reports whether two paths name the same folder, whatever their separators, trailing separator or letter case.</summary>
	public static bool IsSameFolder( string first, string second ) => string.Equals(
		Path.TrimEndingDirectorySeparator( Path.GetFullPath( first ) ),
		Path.TrimEndingDirectorySeparator( Path.GetFullPath( second ) ),
		StringComparison.OrdinalIgnoreCase );

	/// <summary>Stores the unsent prompt text so it survives closing the chat or the application.</summary>
	public void SaveDraft( Guid id, string draft )
	{
		var conversation = Get( id );
		var value = draft.Length == 0 ? null : draft;
		if ( conversation.Draft == value )
			return;

		_conversations[id] = conversation with
		{
			Draft = value,
		};
		MarkChanged();
		MarkMutated( nameof( SaveDraft ), new { id, draft } );
	}

	/// <summary>Deletes a conversation and its local message history, then raises <see cref="Deleted"/>.</summary>
	/// <returns>Whether the conversation existed.</returns>
	public bool DeleteConversation( Guid id )
	{
		EnsureLoaded();
		if ( !_conversations.ContainsKey( id ) )
			return false;

		_conversations.Remove( id );
		_messages.Remove( id );
		_snapshots.Remove( id );
		_unreadable.Remove( id );
		_dirtyMessages.Remove( id );
		if ( _messageVersions.Remove( id, out var version ) )
			version.Value++;
		_deletedMessages.Add( id );
		MarkChanged();
		MarkMutated( nameof( DeleteConversation ), new { id } );
		Deleted?.Invoke( id );
		return true;
	}

	/// <summary>Appends a message and updates the conversation's activity timestamp; only the host's chat session, or <see cref="ApplyMessage"/> in a mirror, calls it.</summary>
	public void AppendMessage( Guid conversationId, ConversationMessage message, bool persist = true )
	{
		var conversation = Get( conversationId );
		var messages = EnsureMessages( conversationId );
		if ( messages.Any( item => item.Id == message.Id ) )
			throw new ArgumentException( "The message identifier already exists.", nameof( message ) );

		messages.Add( BuildOwnedMessage( message ) );
		_conversations[conversationId] = conversation with
		{
			UpdatedAt = DateTimeOffset.UtcNow,
		};
		MarkMessagesChanged( conversationId, persist: false );
		MarkChanged( persist );
		MessageApplied?.Invoke( conversationId, message, persist );
	}

	/// <summary>Replaces an existing message as streaming output arrives; only readers of this conversation's messages rebuild. Host-only, as <see cref="AppendMessage"/>.</summary>
	public void UpdateMessage( Guid conversationId, ConversationMessage message, bool persist = true )
	{
		_ = Get( conversationId );
		var messages = EnsureMessages( conversationId );
		var index = messages.FindIndex( item => item.Id == message.Id );
		if ( index < 0 )
			throw new KeyNotFoundException( "The message no longer exists." );

		messages[index] = BuildOwnedMessage( message );
		MarkMessagesChanged( conversationId, persist );
		MessageApplied?.Invoke( conversationId, message, persist );
	}

	/// <summary>Replaces a conversation's cached messages with authoritative backend history. Host-only, as <see cref="AppendMessage"/>.</summary>
	public void ReplaceMessages( Guid conversationId, IReadOnlyList<ConversationMessage> messages )
	{
		_ = Get( conversationId );
		if ( messages.Select( message => message.Id ).Distinct().Count() != messages.Count )
			throw new ArgumentException( "Message identifiers must be unique.", nameof( messages ) );

		_messages[conversationId] = messages
			.Select( BuildOwnedMessage )
			.ToList();
		MarkMessagesChanged( conversationId, persist: true );
		MessagesReplaced?.Invoke( conversationId, messages );
	}

	/// <summary>Reports whether a conversation's agent is working now; reading it rebuilds the reader only when the working set changes.</summary>
	public bool IsWorking( Guid conversationId )
	{
		_ = _workingVersion.Value;
		return _working.Contains( conversationId );
	}

	/// <summary>Gets how many conversations have an agent working now; reading it rebuilds the reader only when the working set changes.</summary>
	public int WorkingCount
	{
		get
		{
			_ = _workingVersion.Value;
			return _working.Count;
		}
	}

	/// <summary>Records whether a conversation's agent is working; not saved, since no agent outlives the window.</summary>
	public void SetWorking( Guid conversationId, bool isWorking )
	{
		if ( !(isWorking ? _working.Add( conversationId ) : _working.Remove( conversationId )) )
			return;

		_workingVersion.Value++;
		WorkingChanged?.Invoke( conversationId, isWorking );
	}

	/// <summary>Clones rich JSON so message storage owns it independently of a backend event document, without what Loom never shows.</summary>
	/// <remarks>Cloning JSON that is already a clone, such as a backend frame's, returns it without copying.</remarks>
	private static ConversationMessage BuildOwnedMessage( ConversationMessage message ) => message with
	{
		RichContent = CompactRichContent( message.RichContent?.Clone() ),
	};

	/// <summary>Drops the parts of a message's rich JSON that Loom never reads and that can be megabytes, such as a bash result's <c>details.trace</c>,
	/// so an open chat does not keep them in memory; anything else is returned as it is.</summary>
	internal static JsonElement? CompactRichContent( JsonElement? rich )
	{
		if ( rich is not { ValueKind: JsonValueKind.Object } element || !element.TryGetProperty( "details", out var details )
			|| details.ValueKind != JsonValueKind.Object || !details.TryGetProperty( "trace", out _ ) )
			return rich;

		var node = System.Text.Json.Nodes.JsonNode.Parse( element.GetRawText() )!.AsObject();
		node["details"]!.AsObject().Remove( "trace" );
		return JsonSerializer.SerializeToElement( node );
	}

	/// <summary>Rejects assignments to a project that does not exist or would cross account profiles.</summary>
	private void EnsureProjectProfile( SettingsProfile profile, Guid? projectId )
	{
		if ( projectId is { } id && ProjectStore.Get( id ).Profile != profile )
			throw new ArgumentException( "A conversation and its project must use the same profile." );
	}

	/// <summary>Notifies readers of the conversation list, and saves it unless told otherwise.</summary>
	private void MarkChanged( bool persist = true )
	{
		_version.Value++;
		_isIndexDirty = true;
		if ( persist )
			Save();
	}

	/// <summary>Notifies readers of one conversation's messages, and saves them unless told otherwise.</summary>
	private void MarkMessagesChanged( Guid conversationId, bool persist )
	{
		_snapshots.Remove( conversationId );
		if ( _messageVersions.TryGetValue( conversationId, out var version ) )
			version.Value++;
		_dirtyMessages.Add( conversationId );
		if ( persist )
			Save();
	}
}
