using System.Text.Json;
using System.Text.RegularExpressions;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Conversations;

namespace Lokn.Modules.Design;

/// <summary>Follows the chats that run in this process: a chat whose agent starts working on a design gets its studio beside it at once,
/// and the studio shows whether the agent works and sends the user's comments to it.</summary>
/// <remarks>
/// Only sessions that run here are followed (<see cref="ChatSession"/>): Loom in role <c>local</c>, or the daemon, which serves the designs of its chats.
/// A window that mirrors the daemon's chats leaves them to the daemon, whose studio it shows through the lent browser tools.
/// </remarks>
public sealed partial class DesignModule
{
	/// <summary>Stores the running module context, or null while stopped.</summary>
	private ModuleContext? _context;

	/// <summary>Stores the chats followed, by session, with the handlers attached to them.</summary>
	private readonly Dictionary<ChatSession, FollowedChat> _chats = [];

	/// <summary>Stores the chat host whose sessions are followed, or null while stopped.</summary>
	private ChatHost? _host;

	/// <summary>Starts following this PC's chat sessions.</summary>
	private void StartFollowing( ModuleContext context )
	{
		_context = context;
		_host = context.App.GetModule<ConversationsModule>().Hosts.Local;
		_host.Sessions.WaitingChanged += UpdateFollowed;
		_host.Sessions.SourceChanged += UpdateFollowed;
		UpdateFollowed();
	}

	/// <summary>Stops following chat sessions and unlinks every design.</summary>
	private void StopFollowing()
	{
		if ( _host is { } host )
		{
			host.Sessions.WaitingChanged -= UpdateFollowed;
			host.Sessions.SourceChanged -= UpdateFollowed;
		}
		foreach ( var (session, chat) in _chats )
			chat.Detach( session );
		_chats.Clear();
		_host = null;
		_context = null;
	}

	/// <summary>Follows sessions created since the last call and forgets those gone; <see cref="ChatSessionService.WaitingChanged"/> is raised when one is created or forgotten.</summary>
	private void UpdateFollowed()
	{
		if ( _host is not { } host )
			return;

		var current = host.Sessions.Sessions.OfType<ChatSession>().ToHashSet();
		foreach ( var gone in _chats.Keys.Where( session => !current.Contains( session ) ).ToArray() )
		{
			_chats[gone].Detach( gone );
			_chats.Remove( gone );
		}
		foreach ( var session in current.Where( session => !_chats.ContainsKey( session ) ) )
		{
			var chat = new FollowedChat( id => OnToolChanged( session, id ), () => OnChatChanged( session ) );
			session.ToolChanged += chat.ToolChanged;
			session.Changed += chat.Changed;
			_chats.Add( session, chat );
		}
	}

	/// <summary>Opens the studio of a design a chat's agent starts a tool call on, such as writing its files, unless the chat already showed it.</summary>
	private void OnToolChanged( ChatSession session, string id )
	{
		if ( _host is not { } host || !session.Tools.TryGetValue( id, out var frame ) || ReadText( frame, "type" ) != "tool_execution_start"
			|| ReadText( frame, "toolName" ).StartsWith( "design_", StringComparison.Ordinal ) )
			return;

		var directory = ComputeAgentDirectory( host.Store, session.ConversationId );
		foreach ( var store in FindDesigns( frame, directory ) )
		{
			LinkChat( session, store );
			if ( MarkShown( session, store ) )
				_ = ShowAsync( session, store );
		}
	}

	/// <summary>Tells the studios of a chat's designs when its agent starts or stops working.</summary>
	private void OnChatChanged( ChatSession session )
	{
		if ( _server is not { } server || !_chats.TryGetValue( session, out var chat ) )
			return;

		var isBusy = session.IsBusy;
		foreach ( var (store, link) in chat.Designs.Values )
		{
			if ( link.IsBusy == isBusy )
				continue;
			link.IsBusy = isBusy;
			server.NotifyChat( store );
		}
	}

	/// <summary>Links a design to a chat, so its studio shows the chat and sends comments to it.</summary>
	private void LinkChat( ChatSession session, DesignStore store )
	{
		if ( _host is not { } host || _context is not { } context || !_chats.TryGetValue( session, out var chat ) || chat.Designs.ContainsKey( store.Token ) )
			return;

		var title = host.Store.Get( session.ConversationId ).Title;
		var link = new DesignChatLink( title, message => context.Post( () => session.UpdateCardPrompt( message ) ) ) { IsBusy = session.IsBusy };
		chat.Designs[store.Token] = (store, link);
		StartServer().Link( store, link );
		Remember( store, session.ConversationId );
	}

	/// <summary>Records that a chat shows a design's studio; false when it already did.</summary>
	private bool MarkShown( ChatSession session, DesignStore store ) =>
		_chats.TryGetValue( session, out var chat ) && chat.Shown.Add( store.Token );

	/// <summary>Gets the folder a chat's agent works in, against which its relative paths resolve: the folder its Omp session started in, else the chat's folder.</summary>
	/// <remarks>Omp may run a chat elsewhere than the folder Loom gave it, such as a chat outside projects in the home folder, which it moves to a temporary folder;
	/// the newest session file's <c>session</c> entry records where.</remarks>
	internal static string ComputeAgentDirectory( ConversationStore store, Guid conversationId )
	{
		var fallback = store.GetDirectory( conversationId );
		try
		{
			if ( store.Get( conversationId ).BackendSessionPath is not { Length: > 0 } sessions || !Directory.Exists( sessions ) )
				return fallback;
			var newest = new DirectoryInfo( sessions ).EnumerateFiles( "*.jsonl" ).MaxBy( file => file.LastWriteTimeUtc );
			if ( newest is null )
				return fallback;

			using var reader = new StreamReader( new FileStream( newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete ) );
			for ( var count = 0; count < 8 && reader.ReadLine() is { } line; count++ )
			{
				using var entry = JsonDocument.Parse( line );
				if ( ReadText( entry.RootElement, "type" ) == "session" && ReadText( entry.RootElement, "cwd" ) is { Length: > 0 } cwd && Path.IsPathFullyQualified( cwd ) )
					return cwd;
			}
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException )
		{
		}
		return fallback;
	}

	/// <summary>Shows a design's studio beside its chat, as the chat's own browser tool would.</summary>
	private async Task ShowAsync( ChatSession session, DesignStore store )
	{
		if ( _context is not { } context )
			return;

		Directory.CreateDirectory( store.Folder );
		var address = StartServer().Serve( store );
		await ShowStudioAsync( context.App.Tools, store, address, session.PanelId, context.Lifetime, session.ConversationId, null );
	}

	/// <summary>Finds the designs a tool call names in its arguments: any path into <c>.lokn/design/&lt;name&gt;</c>, relative ones to the chat's folder.</summary>
	internal static IReadOnlyList<DesignStore> FindDesigns( JsonElement frame, string directory )
	{
		var found = new Dictionary<string, DesignStore>( StringComparer.Ordinal );
		foreach ( var text in ReadStrings( frame ) )
		{
			foreach ( Match match in DesignPathPattern().Matches( text ) )
			{
				var root = match.Groups["root"].Value;
				string project;
				try
				{
					project = Path.IsPathFullyQualified( root ) ? root : Path.GetFullPath( Path.Combine( directory, root ) );
				}
				catch ( Exception error ) when ( error is ArgumentException or NotSupportedException or PathTooLongException )
				{
					continue;
				}
				var store = new DesignStore( match.Groups["name"].Value, DesignStore.GetFolder( project, match.Groups["name"].Value ) );
				found.TryAdd( store.Token, store );
			}
		}
		return [.. found.Values];
	}

	/// <summary>Lists every string value in a JSON value, at any depth.</summary>
	private static IEnumerable<string> ReadStrings( JsonElement value )
	{
		switch ( value.ValueKind )
		{
			case JsonValueKind.String:
				yield return value.GetString() ?? "";
				break;
			case JsonValueKind.Object:
				foreach ( var property in value.EnumerateObject() )
				{
					foreach ( var text in ReadStrings( property.Value ) )
						yield return text;
				}
				break;
			case JsonValueKind.Array:
				foreach ( var item in value.EnumerateArray() )
				{
					foreach ( var text in ReadStrings( item ) )
						yield return text;
				}
				break;
		}
	}

	/// <summary>Reads a string property, or empty text.</summary>
	private static string ReadText( JsonElement value, string name ) =>
		value.ValueKind == JsonValueKind.Object && value.TryGetProperty( name, out var property ) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";

	/// <summary>Matches a path into a design folder: the project folder before <c>.lokn</c>, which may be empty or relative, and the design's name.</summary>
	[GeneratedRegex( @"(?<root>(?:[A-Za-z]:)?[^\s""'`*?<>|:\[\]()=;&]*?)\.lokn[\\/]+design[\\/]+(?<name>[a-z0-9][a-z0-9_-]{0,63})(?=[\\/""'`\s#\]]|$)" )]
	private static partial Regex DesignPathPattern();

	/// <summary>Holds what the module attached to one chat session and the designs linked to it.</summary>
	/// <param name="ToolChanged">The handler of the session's tool calls.</param>
	/// <param name="Changed">The handler of the session's state.</param>
	private sealed record FollowedChat( Action<string> ToolChanged, Action Changed )
	{
		/// <summary>Gets the designs linked to the chat, by token.</summary>
		internal Dictionary<string, (DesignStore Store, DesignChatLink Link)> Designs { get; } = new( StringComparer.Ordinal );

		/// <summary>Gets the tokens of the designs whose studio the chat showed.</summary>
		internal HashSet<string> Shown { get; } = new( StringComparer.Ordinal );

		/// <summary>Detaches the handlers from the session.</summary>
		internal void Detach( ChatSession session )
		{
			session.ToolChanged -= ToolChanged;
			session.Changed -= Changed;
		}
	}
}
