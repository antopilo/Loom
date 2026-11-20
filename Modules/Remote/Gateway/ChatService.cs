using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Conversations;
using Lokn.Modules.Projects;

namespace Lokn.Modules.Remote;

/// <summary>Serves the host's chats and stores to every connection: <c>sync</c>, <c>attach</c>, <c>attach.page</c>, <c>detach</c>, <c>call</c>,
/// <c>store</c> and <c>upload</c> requests, and the <c>store</c>, <c>state</c>, <c>msg</c>, <c>msgs</c>, <c>tool</c>, <c>evt</c> and <c>working</c>
/// pushes (docs/remote.md, Chat and store frames).</summary>
/// <remarks>
/// Create, register and dispose it on the host's UI thread; its handlers run there too. Each chat has one sequence number for the life of the
/// host process, shared by every connection: it rises with each push of the chat and with each message change, even while no connection is
/// attached, so an <c>attach</c> whose <c>since</c> differs gets the messages again. Each connection's pushes go out in order through one queue,
/// where a newer <c>msg</c> of a message replaces one not sent yet; a connection that falls too far behind, or a push too large to send, closes
/// the connection, and the client syncs again when it reconnects. A session that asks for the user sends a web push to the devices that do not
/// show its chat.
/// </remarks>
public sealed partial class ChatService : IHostService, IDisposable
{
	/// <summary>Defines the largest upload chunk, 256 KiB.</summary>
	private const int MaxChunkBytes = 256 * 1024;

	/// <summary>Defines how many uploaded bytes one device may keep on the host, 1 GiB.</summary>
	private const long MaxDeviceUploadBytes = 1L << 30;

	/// <summary>Defines the most bytes of messages one <c>attach</c> or <c>attach.page</c> answer carries, 4 MiB, well under a frame's limit.</summary>
	private const int AttachPageBytes = 4 * 1024 * 1024;

	/// <summary>Defines the most pushes waiting for one connection before it counts as too slow and is closed.</summary>
	private const int MaxPendingPushes = 4096;

	/// <summary>Stores the host's conversation store.</summary>
	private readonly ConversationStore _store;

	/// <summary>Stores the host's project store.</summary>
	private readonly ProjectStore _projects;

	/// <summary>Stores the host's chat sessions.</summary>
	private readonly ChatSessionService _sessions;

	/// <summary>Posts to the host's UI thread.</summary>
	private readonly Func<Action, bool> _post;

	/// <summary>Stores the folder uploads go to, one subfolder per device.</summary>
	private readonly string _uploads;

	/// <summary>Stores the web push sender, or null without one.</summary>
	private readonly PushService? _push;

	/// <summary>Reports a failure the host should show, on the UI thread.</summary>
	private readonly Action<Exception> _report;

	/// <summary>Gets the addresses devices dial now, for the <c>sync</c> answer, or null without a network.</summary>
	private readonly Func<IReadOnlyList<string>>? _addresses;

	/// <summary>Holds the open connections.</summary>
	private readonly List<Link> _links = [];

	/// <summary>Holds each chat's sequence number, feed and attached connections.</summary>
	private readonly Dictionary<Guid, HostChat> _chats = [];

	/// <summary>Holds the chat and attention handler of each session followed for web push.</summary>
	private readonly Dictionary<IChatSession, (Guid Chat, Action<string, string> Handler)> _watched = [];

	/// <summary>Counts the <c>store</c> and <c>working</c> pushes, so a client tells which of them a <c>sync</c> snapshot already holds.</summary>
	private long _revision;

	/// <summary>Creates the service over the host's stores and sessions and follows their changes; call on the UI thread.</summary>
	/// <param name="store">The host's conversation store.</param>
	/// <param name="sessions">The host's chat sessions.</param>
	/// <param name="post">Posts to the host's UI thread and reports whether it was accepted.</param>
	/// <param name="uploads">The folder uploads go to, such as <c>attachments\remote</c> in the data folder.</param>
	/// <param name="push">The web push sender, or null.</param>
	/// <param name="report">Reports a failure, on the UI thread.</param>
	/// <param name="addresses">Gets the addresses devices dial now, which <c>sync</c> sends so a paired PC follows the host's address changes; null for none.</param>
	public ChatService( ConversationStore store, ChatSessionService sessions, Func<Action, bool> post, string uploads, PushService? push, Action<Exception> report,
		Func<IReadOnlyList<string>>? addresses = null )
	{
		_store = store;
		_projects = store.ProjectStore;
		_sessions = sessions;
		_post = post;
		_uploads = uploads;
		_push = push;
		_report = report;
		_addresses = addresses;
		_store.Mutated += BroadcastStore;
		_projects.Mutated += BroadcastStore;
		_store.MessageApplied += BroadcastMessage;
		_store.MessagesReplaced += BroadcastMessages;
		_store.WorkingChanged += BroadcastWorking;
		_store.Deleted += ForgetChat;
	}

	/// <summary>Gets a random id of this host process, which the <c>sync</c> answer carries so a client knows sequence numbers restarted.</summary>
	public string Boot { get; } = Guid.NewGuid().ToString( "N" );

	/// <summary>Registers the chat and store handlers for a connection; on the UI thread, before the peer starts.</summary>
	public void Register( RemoteConnection connection )
	{
		var link = new Link( connection );
		_links.Add( link );
		connection.Peer
			.On( "sync", _ => UiInvoke.RunAsync<JsonObject?>( _post, BuildSync ) )
			.On( "attach", frame => UiInvoke.RunAsync<JsonObject?>( _post, () => UpdateAttach( link, frame ) ) )
			.On( "attach.page", frame => UiInvoke.RunAsync<JsonObject?>( _post, () => BuildAttachPage( link, frame ) ) )
			.On( "detach", frame => UiInvoke.RunAsync<JsonObject?>( _post, () => UpdateDetach( link, frame ) ) )
			.On( "call", frame => UpdateCallAsync( link, frame ) )
			.On( "store", frame => UpdateStoreAsync( link, frame ) )
			.On( "upload", frame => UpdateUploadAsync( link, frame ) );
		connection.Peer.Closed += _ => _post( () => RemoveLink( link ) );
	}

	/// <summary>Stops following the stores and sessions and detaches every connection from its chats; call on the UI thread.</summary>
	public void Dispose()
	{
		_store.Mutated -= BroadcastStore;
		_projects.Mutated -= BroadcastStore;
		_store.MessageApplied -= BroadcastMessage;
		_store.MessagesReplaced -= BroadcastMessages;
		_store.WorkingChanged -= BroadcastWorking;
		_store.Deleted -= ForgetChat;
		foreach ( var link in _links.ToArray() )
			RemoveLink( link );
		foreach ( var (session, watch) in _watched )
			session.AttentionRequested -= watch.Handler;
		_watched.Clear();
	}

	/// <summary>Forgets a deleted chat: detaches every connection from it, stops its feed and stops following its session for web push.</summary>
	private void ForgetChat( Guid id )
	{
		if ( _chats.Remove( id, out var chat ) )
		{
			foreach ( var link in chat.Subscribers )
			{
				link.Attached.Remove( id );
				link.Pages.Remove( id );
			}
			chat.Subscribers.Clear();
			chat.Feed?.Dispose();
		}
		foreach ( var (session, watch) in _watched.Where( item => item.Value.Chat == id ).ToArray() )
		{
			session.AttentionRequested -= watch.Handler;
			_watched.Remove( session );
		}
	}

	/// <summary>Answers <c>sync</c>: every project and conversation (without messages), the host's name, home folder, role and build, the working chats,
	/// the boot id, the list revision and the addresses devices dial now.</summary>
	private JsonObject? BuildSync()
	{
		var profiles = Enum.GetValues<SettingsProfile>();
		var projects = profiles.SelectMany( _projects.Projects ).ToArray();
		var conversations = profiles
			.SelectMany( profile => _store.Conversations( profile ).Concat( _projects.Projects( profile ).SelectMany( project => _store.Conversations( profile, project.Id ) ) ) )
			.ToArray();
		var sync = new JsonObject
		{
			["projects"] = WireJson.BuildNode( projects ),
			["conversations"] = WireJson.BuildNode( conversations ),
			["hostName"] = Environment.MachineName,
			["home"] = ConversationStore.DefaultDirectory,
			["role"] = RemoteModule.HostRole,
			["version"] = RemoteProtocol.AppVersion,
			["working"] = Frame.BuildStrings( conversations.Where( conversation => _store.IsWorking( conversation.Id ) ).Select( conversation => conversation.Id.ToString( "N" ) ) ),
			["boot"] = Boot,
			["rev"] = _revision,
		};
		if ( _addresses?.Invoke() is { Count: > 0 } addresses )
			sync["addrs"] = Frame.BuildStrings( addresses );
		return sync;
	}

	/// <summary>Answers <c>attach</c>: starts the chat's pushes to the connection and attaches the connection's panel to the session, so it keeps
	/// running; the frame's <c>panel</c>, the client's panel that shows the chat, is the caller of the agent's tool calls while it is the last attached.</summary>
	/// <remarks>
	/// The messages come along when <c>since</c> is 0 or less, or differs from the chat's sequence number. A transcript larger than one page is
	/// cut into pages of whole messages, taken now: the answer carries the first and their count, and <c>attach.page</c> answers the others.
	/// </remarks>
	private JsonObject? UpdateAttach( Link link, JsonObject frame )
	{
		var id = WireJson.RequireChannel( frame );
		if ( _store.Find( id ) is null )
			throw new RemoteException( RemoteErrors.UnknownChat, "The host has no such chat." );

		var since = Frame.ReadLong( frame, "since" ) ?? -1;
		var chat = FindChat( id );
		var session = Watch( id, _sessions.GetOrCreate( id ) );
		if ( chat.Session != session )
		{
			// The session was replaced since the feed started, such as after the chat was cleared away; follow the new one.
			chat.Feed?.Dispose();
			chat.Feed = null;
			chat.Session = session;
		}
		chat.Feed ??= new ChatSessionFeed( session,
			state => Broadcast( chat, "state", push => push["s"] = WireJson.BuildNode( state ) ),
			( tool, toolFrame ) => Broadcast( chat, "tool", push =>
			{
				push["id"] = tool;
				push["f"] = WireJson.BuildNode( toolFrame );
			} ),
			sessionEvent => Broadcast( chat, "evt", push => push["e"] = WireJson.BuildNode( sessionEvent ) ) );

		if ( !chat.Subscribers.Contains( link ) )
		{
			var panel = Frame.ReadString( frame, "panel" ) is { } text && Guid.TryParseExact( text, "N", out var parsed ) ? parsed : link.PanelId;
			chat.Subscribers.Add( link );
			link.Attached.Add( id, panel );
			session.Attach( panel );
		}

		var answer = new JsonObject
		{
			["seq"] = chat.Seq,
			["state"] = WireJson.BuildNode( session.CaptureState() ),
			["tools"] = WireJson.BuildNode( session.Tools ),
			["intents"] = WireJson.BuildNode( session.Intents ),
		};
		link.Pages.Remove( id );
		if ( since <= 0 || since != chat.Seq )
		{
			var pages = BuildPages( _store.GetMessages( id ) );
			answer["messages"] = pages[0];
			if ( pages.Count > 1 )
			{
				answer["pages"] = pages.Count;
				link.Pages[id] = pages;
			}
		}
		return answer;
	}

	/// <summary>Answers <c>attach.page { ch, page }</c>: one more page of the transcript the last <c>attach</c> of the chat took, from 1; the last page
	/// forgets the transcript.</summary>
	private JsonObject? BuildAttachPage( Link link, JsonObject frame )
	{
		var id = WireJson.RequireChannel( frame );
		var page = Frame.ReadLong( frame, "page" ) ?? -1;
		if ( !link.Pages.TryGetValue( id, out var pages ) || page < 1 || page >= pages.Count )
			throw new RemoteException( RemoteErrors.BadRequest, "There is no such page of the chat's transcript; attach again." );

		var messages = pages[(int)page];
		// A page is sent once; the node then belongs to the answer.
		pages[(int)page] = [];
		if ( page == pages.Count - 1 )
			link.Pages.Remove( id );
		return new JsonObject { ["messages"] = messages };
	}

	/// <summary>Cuts a transcript into pages of whole messages of at most <see cref="AttachPageBytes"/> each; a larger message is a page of its own.</summary>
	private static List<JsonArray> BuildPages( IReadOnlyList<ConversationMessage> messages )
	{
		var pages = new List<JsonArray> { new() };
		long bytes = 0;
		foreach ( var message in messages )
		{
			var node = WireJson.BuildNode( message );
			var size = node is null ? 4 : Encoding.UTF8.GetByteCount( node.ToJsonString() );
			if ( bytes > 0 && bytes + size > AttachPageBytes )
			{
				pages.Add( [] );
				bytes = 0;
			}
			pages[^1].Add( node );
			bytes += size;
		}
		return pages;
	}

	/// <summary>Answers <c>detach</c>: stops the chat's pushes to the connection and detaches its panel.</summary>
	private JsonObject? UpdateDetach( Link link, JsonObject frame )
	{
		RemoveSubscriber( link, WireJson.RequireChannel( frame ) );
		return new JsonObject();
	}

	/// <summary>Answers <c>call</c>: invokes a whitelisted session method on the UI thread; every call needs control, since even the task check runs a
	/// paid model request on the host's account.</summary>
	private async Task<JsonObject?> UpdateCallAsync( Link link, JsonObject frame )
	{
		var id = WireJson.RequireChannel( frame );
		var method = Frame.ReadString( frame, "m" ) ?? throw new RemoteException( RemoteErrors.BadRequest, "The call names no method m." );
		if ( !ChatSessionCall.Methods.Contains( method ) )
			throw new RemoteException( RemoteErrors.BadRequest, $"The chat session has no method {method} to call." );
		if ( method is ChatSessionCall.Attach or ChatSessionCall.Detach )
			throw new RemoteException( RemoteErrors.BadRequest, "Panels attach and detach with the attach and detach frames." );
		link.Connection.RequireControl();

		JsonElement[] args = frame["a"] is JsonArray array ? [.. array.Select( WireJson.BuildElement )] : [];
		var result = await UiInvoke.RunAsync( _post, async () =>
		{
			if ( _store.Find( id ) is null )
				throw new RemoteException( RemoteErrors.UnknownChat, "The host has no such chat." );

			var session = Watch( id, _sessions.GetOrCreate( id ) );
			try
			{
				return await ChatSessionCall.InvokeAsync( session, method, args );
			}
			catch ( ArgumentException exception )
			{
				throw new RemoteException( RemoteErrors.BadRequest, exception.Message );
			}
		} ).ConfigureAwait( false );

		var answer = new JsonObject();
		if ( result.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) )
			answer["r"] = JsonSerializer.SerializeToNode( result );
		return answer;
	}

	/// <summary>Answers <c>store</c>: applies a client's store operation on the host's store, which broadcasts it back; host-only operations are refused.</summary>
	private async Task<JsonObject?> UpdateStoreAsync( Link link, JsonObject frame )
	{
		link.Connection.RequireControl();
		var op = WireJson.Require<StoreOp>( frame, "op" );
		await UiInvoke.RunAsync( _post, () =>
		{
			try
			{
				switch ( op.S )
				{
					case StoreOp.Conversations:
						_store.Apply( op );
						break;
					case StoreOp.Projects:
						_projects.Apply( op );
						break;
					default:
						throw new ArgumentException( $"There is no store {op.S}." );
				}
			}
			catch ( ArgumentException exception )
			{
				throw new RemoteException( RemoteErrors.BadRequest, exception.Message );
			}
			catch ( KeyNotFoundException exception )
			{
				throw new RemoteException( RemoteErrors.UnknownChat, exception.Message );
			}
		} ).ConfigureAwait( false );
		return new JsonObject();
	}

	/// <summary>Answers <c>upload</c>: writes one chunk of an attachment under the device's upload folder, answering the file's path with the final chunk.</summary>
	/// <remarks>Uploads stay as long as chats may show them; a device may keep at most <see cref="MaxDeviceUploadBytes"/> on the host.</remarks>
	private async Task<JsonObject?> UpdateUploadAsync( Link link, JsonObject frame )
	{
		link.Connection.RequireControl();
		var name = BuildSafeName( Frame.ReadString( frame, "name" ) ?? "" );
		var offset = Frame.ReadLong( frame, "offset" ) ?? throw new RemoteException( RemoteErrors.BadRequest, "The upload needs its offset." );
		byte[] data;
		try
		{
			data = Convert.FromBase64String( Frame.ReadString( frame, "data" ) ?? "" );
		}
		catch ( FormatException )
		{
			throw new RemoteException( RemoteErrors.BadRequest, "The upload data is not base64." );
		}
		if ( name.Length == 0 || offset < 0 || data.Length > MaxChunkBytes )
			throw new RemoteException( RemoteErrors.BadRequest, "The upload needs a file name, an offset and at most 256 KiB of data." );

		var folder = Path.Combine( _uploads, BuildSafeName( link.Connection.DeviceId ) );
		var path = Path.Combine( folder, name );
		await link.Uploads.WaitAsync().ConfigureAwait( false );
		try
		{
			Directory.CreateDirectory( folder );
			if ( MeasureFolder( folder ) + data.Length > MaxDeviceUploadBytes )
				throw new RemoteException( RemoteErrors.Failed, "This device's uploads on the host reached 1 GiB. Delete old files in its attachments folder on the host." );

			await using var file = new FileStream( path, offset == 0 ? FileMode.Create : FileMode.Open, FileAccess.Write, FileShare.None, 4096, useAsync: true );
			if ( file.Length != offset )
				throw new RemoteException( RemoteErrors.BadRequest, $"The upload continues at {offset}, but the host holds {file.Length} bytes of it." );
			file.Seek( offset, SeekOrigin.Begin );
			await file.WriteAsync( data ).ConfigureAwait( false );
		}
		catch ( FileNotFoundException )
		{
			throw new RemoteException( RemoteErrors.BadRequest, "The upload must start at offset 0." );
		}
		finally
		{
			link.Uploads.Release();
		}

		var answer = new JsonObject();
		if ( Frame.ReadBool( frame, "final" ) == true )
			answer["path"] = path;
		return answer;
	}

	/// <summary>Adds up the sizes of the files in a folder.</summary>
	private static long MeasureFolder( string folder ) =>
		new DirectoryInfo( folder ).EnumerateFiles( "*", SearchOption.TopDirectoryOnly ).Sum( file => file.Length );

	/// <summary>Sends a store operation the host applied to every connection.</summary>
	private void BroadcastStore( StoreOp op )
	{
		_revision++;
		foreach ( var link in _links )
		{
			var push = Frame.Create( "store" );
			push["op"] = WireJson.BuildNode( op );
			push["rev"] = _revision;
			link.Send( push );
		}
	}

	/// <summary>Sends a chat's new or changed message to the connections attached to it, raising its sequence number either way.</summary>
	private void BroadcastMessage( Guid id, ConversationMessage message, bool persist ) => Broadcast( FindChat( id ), "msg", push =>
	{
		push["m"] = WireJson.BuildNode( message );
		push["persist"] = persist;
	} );

	/// <summary>Sends a chat's replaced messages to the connections attached to it, raising its sequence number either way.</summary>
	private void BroadcastMessages( Guid id, IReadOnlyList<ConversationMessage> messages ) =>
		Broadcast( FindChat( id ), "msgs", push => push["m"] = WireJson.BuildNode( messages ) );

	/// <summary>Sends whether a chat's agent works to every connection.</summary>
	private void BroadcastWorking( Guid id, bool isBusy )
	{
		_revision++;
		foreach ( var link in _links )
		{
			var push = Frame.Create( "working" ).WithChannel( id );
			push["busy"] = isBusy;
			push["rev"] = _revision;
			link.Send( push );
		}
	}

	/// <summary>Asks Loom to show a window of this PC streamed beside a chat, for an agent that works with it: pushes <c>window.show</c> to every
	/// controlling connection attached to the chat, with the client's panel that shows it, else to Loom on this PC (<see cref="HostGateway.LocalDevice"/>)
	/// when it is connected; on the UI thread.</summary>
	/// <param name="chat">The chat whose agent works with the window, or null when no chat's agent asked.</param>
	/// <param name="window">The window's handle.</param>
	/// <param name="title">The window's title.</param>
	/// <returns>How many connections it went to.</returns>
	internal int ShowWindow( Guid? chat, long window, string title )
	{
		JsonObject Build( Guid? panel )
		{
			var push = Frame.Create( "window.show" );
			if ( chat is { } id )
				push = push.WithChannel( id );
			push["hwnd"] = window;
			push["title"] = title;
			if ( panel is { } shown )
				push["panel"] = shown.ToString( "N" );
			return push;
		}

		var sent = 0;
		if ( chat is { } attached && _chats.TryGetValue( attached, out var record ) )
		{
			foreach ( var link in record.Subscribers.Where( link => link.Connection.CanControl ) )
			{
				link.Send( Build( link.Attached.TryGetValue( attached, out var panel ) && panel != link.PanelId ? panel : null ) );
				sent++;
			}
		}
		if ( sent > 0 )
			return sent;

		foreach ( var link in _links.Where( link => link.Connection.DeviceId == HostGateway.LocalDevice ) )
		{
			link.Send( Build( null ) );
			sent++;
		}
		return sent;
	}

	/// <summary>Raises a chat's sequence number and sends one push, filled per connection, to each connection attached to it.</summary>
	private void Broadcast( HostChat chat, string type, Action<JsonObject> fill )
	{
		chat.Seq++;
		foreach ( var link in chat.Subscribers )
		{
			var push = Frame.Create( type ).WithChannel( chat.Id ).WithSeq( chat.Seq );
			fill( push );
			link.Send( push );
		}
	}

	/// <summary>Gets a chat's record, creating it on first use.</summary>
	private HostChat FindChat( Guid id )
	{
		if ( !_chats.TryGetValue( id, out var chat ) )
			_chats.Add( id, chat = new HostChat( id ) );
		return chat;
	}

	/// <summary>Stops a connection's pushes for a chat and detaches its panel; the chat's feed stops once no connection is attached.</summary>
	private void RemoveSubscriber( Link link, Guid id )
	{
		link.Pages.Remove( id );
		if ( !_chats.TryGetValue( id, out var chat ) || !chat.Subscribers.Remove( link ) )
			return;

		link.Attached.Remove( id, out var panel );
		chat.Session?.Detach( panel );
		if ( chat.Subscribers.Count > 0 )
			return;

		chat.Feed?.Dispose();
		chat.Feed = null;
		chat.Session = null;
	}

	/// <summary>Forgets a closed connection and detaches it from every chat.</summary>
	private void RemoveLink( Link link )
	{
		if ( !_links.Remove( link ) )
			return;

		foreach ( var id in link.Attached.Keys.ToArray() )
			RemoveSubscriber( link, id );
		link.Pages.Clear();
	}

	/// <summary>Follows a session's requests for the user, for web push, once per session.</summary>
	private IChatSession Watch( Guid id, IChatSession session )
	{
		if ( _push is null || _watched.ContainsKey( session ) )
			return session;

		Action<string, string> handler = ( title, message ) => UpdateAttention( id, title, message );
		session.AttentionRequested += handler;
		_watched.Add( session, (id, handler) );
		return session;
	}

	/// <summary>Sends a web push when a chat finishes or asks a question, to every device but those with a connection that shows the chat.</summary>
	private void UpdateAttention( Guid id, string title, string message )
	{
		if ( _push is null )
			return;

		var showing = _chats.TryGetValue( id, out var chat )
			? chat.Subscribers.Select( link => link.Connection.DeviceId ).ToHashSet( StringComparer.Ordinal )
			: new HashSet<string>( StringComparer.Ordinal );
		_ = NotifyAsync( _push, id, title, message, showing );
	}

	/// <summary>Sends a web push and reports a failure on the UI thread.</summary>
	private async Task NotifyAsync( PushService push, Guid id, string title, string message, IReadOnlySet<string> skipDevices )
	{
		try
		{
			await push.NotifyAsync( id, title, message, skipDevices ).ConfigureAwait( false );
		}
		catch ( Exception exception )
		{
			_post( () => _report( exception ) );
		}
	}

	/// <summary>Keeps a file or folder name to its last segment with only valid characters, never a Windows device name such as <c>NUL</c> or <c>COM1</c>.</summary>
	internal static string BuildSafeName( string name )
	{
		var invalid = Path.GetInvalidFileNameChars();
		var segment = Path.GetFileName( name.Replace( '\\', '/' ).Split( '/' )[^1] );
		var safe = new string( [.. segment.Select( character => invalid.Contains( character ) ? '_' : character )] ).Trim().TrimStart( '.' ).TrimEnd( '.', ' ' );
		return ReservedName().IsMatch( safe ) ? "_" + safe : safe;
	}

	/// <summary>Matches the names Windows keeps for devices, with or without an extension.</summary>
	[GeneratedRegex( @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9\u00b9\u00b2\u00b3]|LPT[0-9\u00b9\u00b2\u00b3])(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant )]
	private static partial Regex ReservedName();

	/// <summary>One connection's chat state: its panel id on the host's sessions, the chats it attached, the transcript pages it has yet to read,
	/// its upload gate and its push queue.</summary>
	/// <param name="connection">The connection.</param>
	private sealed class Link( RemoteConnection connection )
	{
		/// <summary>Holds the pushes not sent yet; lock it to read or change it and <see cref="_isSending"/>.</summary>
		private readonly List<JsonObject> _pending = [];

		/// <summary>Stores whether a send loop runs.</summary>
		private bool _isSending;

		/// <summary>Gets the connection.</summary>
		public RemoteConnection Connection => connection;

		/// <summary>Gets the panel id the connection attaches to sessions as when its <c>attach</c> names none, so they keep running while it shows them.</summary>
		public Guid PanelId { get; } = Guid.NewGuid();

		/// <summary>Gets the chats the connection attached, with the panel id each was attached as.</summary>
		public Dictionary<Guid, Guid> Attached { get; } = [];

		/// <summary>Gets the transcript pages of each chat whose <c>attach</c> answer had more than one; on the UI thread.</summary>
		public Dictionary<Guid, List<JsonArray>> Pages { get; } = [];

		/// <summary>Gets the gate that writes one upload chunk at a time.</summary>
		public SemaphoreSlim Uploads { get; } = new( 1, 1 );

		/// <summary>Queues a push behind the earlier ones; a <c>msg</c> replaces the same message's <c>msg</c> not sent yet. Closes the connection when
		/// it fell <see cref="MaxPendingPushes"/> behind.</summary>
		public void Send( JsonObject push )
		{
			var peer = connection.Peer;
			if ( peer.IsClosed )
				return;

			bool isStarting;
			lock ( _pending )
			{
				if ( Frame.ReadType( push ) == "msg" && FindMessageKey( push ) is { } key )
				{
					var index = _pending.FindIndex( pending => Frame.ReadType( pending ) == "msg" && FindMessageKey( pending ) == key );
					if ( index >= 0 )
					{
						// A message saved once stays saved, whatever the later update says.
						if ( Frame.ReadBool( _pending[index], "persist" ) == true )
							push["persist"] = true;
						_pending.RemoveAt( index );
					}
				}
				if ( _pending.Count >= MaxPendingPushes )
				{
					_pending.Clear();
					_ = peer.DisposeAsync();
					return;
				}
				_pending.Add( push );
				isStarting = !_isSending;
				_isSending = true;
			}
			if ( isStarting )
				_ = DrainAsync( peer );
		}

		/// <summary>Sends the queued pushes in order until none is left; a push too large for the carrier closes the connection.</summary>
		private async Task DrainAsync( RpcPeer peer )
		{
			while ( true )
			{
				JsonObject push;
				lock ( _pending )
				{
					if ( _pending.Count == 0 || peer.IsClosed )
					{
						_pending.Clear();
						_isSending = false;
						return;
					}
					push = _pending[0];
					_pending.RemoveAt( 0 );
				}

				try
				{
					await peer.PushAsync( push ).ConfigureAwait( false );
				}
				catch ( InvalidDataException )
				{
					// The client would miss a change it cannot notice; closing makes it sync and attach again.
					await peer.DisposeAsync().ConfigureAwait( false );
				}
				catch ( Exception exception ) when ( peer.IsClosed || exception is IOException or ObjectDisposedException or OperationCanceledException )
				{
					await peer.DisposeAsync().ConfigureAwait( false );
				}
			}
		}

		/// <summary>Gets a <c>msg</c> push's chat and message id, or null.</summary>
		private static (Guid Chat, string Message)? FindMessageKey( JsonObject push ) =>
			Frame.ReadChannel( push ) is { } chat && push["m"] is JsonObject message && Frame.ReadString( message, "id" ) is { } id ? (chat, id) : null;
	}

	/// <summary>One chat's host-wide push state.</summary>
	/// <param name="id">The conversation id.</param>
	private sealed class HostChat( Guid id )
	{
		/// <summary>Gets the conversation id.</summary>
		public Guid Id => id;

		/// <summary>Gets or sets the last sequence number used.</summary>
		public long Seq { get; set; }

		/// <summary>Gets or sets the feed that follows the session while a connection is attached, or null.</summary>
		public ChatSessionFeed? Feed { get; set; }

		/// <summary>Gets or sets the session the feed follows, or null.</summary>
		public IChatSession? Session { get; set; }

		/// <summary>Gets the connections attached to the chat.</summary>
		public List<Link> Subscribers { get; } = [];
	}
}
