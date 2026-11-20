using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Goo;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Conversations;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;

namespace Lokn.Modules.Remote;

/// <summary>A connection to a host made by <see cref="RemoteClient"/>'s dialer, before the protocol starts on it.</summary>
/// <param name="Connection">The connection: the plaintext pipe, or an encrypted connection after the handshake.</param>
/// <param name="Scopes">The scopes the host grants this device.</param>
/// <param name="Via">How it was reached, for the status line, such as <c>pipe</c> or an address.</param>
public sealed record ClientCarrier( IFrameConnection Connection, IReadOnlyList<string> Scopes, string Via );

/// <summary>What a <see cref="RemoteClient"/> is doing.</summary>
public enum ClientState
{
	/// <summary>Dialing the host, or starting the daemon.</summary>
	Connecting,

	/// <summary>Connected and in sync with the host.</summary>
	Connected,

	/// <summary>The last attempt failed; it retries after a pause.</summary>
	Failed,

	/// <summary>The user stopped the daemon; nothing is dialed until it starts again.</summary>
	Stopped,
}

/// <summary>Which machine a <see cref="RemoteClient"/> links this Loom with: this PC's daemon over the named pipe, or a paired host over the network.</summary>
public enum HostLinkKind
{
	/// <summary>This PC's daemon, which runs This PC's chats in the <c>daemon</c> role; same disk, same browser tabs file.</summary>
	Daemon,

	/// <summary>A host this PC paired with; its chats, disk, browser tabs and windows are the host's.</summary>
	Paired,
}

/// <summary>Shows one host's chats in this Loom: keeps one connection to the host with reconnects, mirrors its stores into its <see cref="ChatHost"/>,
/// supplies <see cref="RemoteChatSession"/>s, and for a paired host its workspace, browser tunnel and window stream (docs/remote.md).</summary>
/// <remarks>
/// <see cref="Start"/>, <see cref="Stop"/> and every member but the dialer run on the UI thread. Pushes are posted there in arrival order. The
/// <c>store</c> and <c>working</c> pushes that arrive before the <c>sync</c> snapshot is applied are held and applied after it, unless the
/// snapshot already covers them; a chat's pushes that arrive while its <c>attach</c> awaits the answer are held and applied after it, so none is
/// lost or applied out of order. A paired host's workspace fails every call while it is not connected, so nothing acts on this PC's disk in place
/// of the host's. Each client acts on its own <see cref="ChatHost"/> and <see cref="LoknHost"/> only, so links to several hosts never mix.
/// </remarks>
public sealed class RemoteClient
{
	/// <summary>Defines the first reconnect pause.</summary>
	private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds( 1 );

	/// <summary>Defines the longest reconnect pause.</summary>
	private static readonly TimeSpan LastDelay = TimeSpan.FromSeconds( 30 );

	/// <summary>Defines how long the daemon may take to open its pipe after Loom started it.</summary>
	private static readonly TimeSpan DaemonStartTimeout = TimeSpan.FromSeconds( 15 );

	/// <summary>Defines how long the daemon may take to stop before a restart starts it again.</summary>
	private static readonly TimeSpan DaemonStopTimeout = TimeSpan.FromSeconds( 15 );

	/// <summary>Defines how long a dropped connection waits for the daemon's process to end before it counts as the daemon's failure.</summary>
	private static readonly TimeSpan DaemonExitTimeout = TimeSpan.FromSeconds( 2 );

	/// <summary>Defines how long the host may take to answer <c>sync</c> before the connection counts as failed.</summary>
	private static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds( 15 );

	/// <summary>Defines the upload chunk size, 192 KiB, which stays under the host's 256 KiB limit.</summary>
	private const int UploadChunk = 192 * 1024;

	/// <summary>Gets the JSON null a call without a result answers.</summary>
	private static readonly JsonElement None = JsonSerializer.SerializeToElement<object?>( null );

	/// <summary>Stores the application.</summary>
	private readonly LoknApplication _app;

	/// <summary>Stores the mirror conversation store.</summary>
	private readonly ConversationStore _store;

	/// <summary>Stores the mirror project store.</summary>
	private readonly ProjectStore _projects;

	/// <summary>Stores the chat sessions whose source this client supplies.</summary>
	private readonly ChatSessionService _sessions;

	/// <summary>Gets this process's Omp service, for model names in proxies.</summary>
	private readonly Func<OmpService> _backend;

	/// <summary>Posts to the UI thread.</summary>
	private readonly Func<Action, bool> _post;

	/// <summary>Stores the data folder the paired hosts list and the device key live in.</summary>
	private readonly string _dataFolder;

	/// <summary>Stores the dialer that replaces the link's own, for checks; null dials the pipe or the paired host.</summary>
	private readonly Func<CancellationToken, Task<ClientCarrier>>? _dial;

	/// <summary>Stores the Loom executable that starts the daemon, for checks; null for this process's own.</summary>
	private readonly string? _daemonExecutable;

	/// <summary>Stores the paired host's entry, whose addresses are dialed and whose name wins over the host's own; null for the daemon link.</summary>
	private PairedHost? _paired;

	/// <summary>Ends the connection loop when the client stops.</summary>
	private readonly CancellationTokenSource _stopping = new();

	/// <summary>Holds the attach state of each chat a panel shows.</summary>
	private readonly Dictionary<Guid, ClientChat> _chats = [];

	/// <summary>Counts status changes, so views reading the status rebuild.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Holds the host's tool calls this client runs, by call id, so <c>tool.cancel</c> reaches them.</summary>
	private readonly Dictionary<string, CancellationTokenSource> _toolCalls = new( StringComparer.Ordinal );

	/// <summary>Ends a reconnect pause early, such as after pairing; replaced after each pause.</summary>
	private CancellationTokenSource _wake = new();

	/// <summary>Stores the connected peer, or null while disconnected.</summary>
	private RpcPeer? _peer;

	/// <summary>Stores the boot id of the host process last synced, which tells whether chat sequence numbers carried over.</summary>
	private string? _boot;

	/// <summary>Stores whether the user stopped the daemon, which pauses dialing.</summary>
	private volatile bool _isPaused;

	/// <summary>Stores the paired host's address that answered last, which the next dial tries first.</summary>
	private volatile string? _lastAddress;

	/// <summary>Stores whether this client already restarted a daemon of another build once, so a mismatch that stays never loops.</summary>
	private bool _isVersionRestarted;

	/// <summary>Stores the tools offer last sent on the current connection, as JSON, so an unchanged offer is not sent again.</summary>
	private string? _lastOffer;

	/// <summary>Stores the device keys once loaded.</summary>
	private RemoteKeys? _keys;

	/// <summary>Stores what carries the browser's loopback connections to a paired host; null for the daemon link, whose localhost is this PC's.</summary>
	private readonly TunnelClient? _tunnel;

	/// <summary>Creates the client; <see cref="Start"/> turns the host's stores into mirrors and connects.</summary>
	/// <param name="app">The application.</param>
	/// <param name="host">The host's chats, whose stores become mirrors and whose sessions this client supplies.</param>
	/// <param name="backend">Gets this process's Omp service.</param>
	/// <param name="post">Posts to the UI thread and reports whether it was accepted.</param>
	/// <param name="kind">Whether this links with this PC's daemon or a paired host.</param>
	/// <param name="dataFolder">The data folder the paired hosts list and the device key live in.</param>
	/// <param name="paired">The paired host's entry; null for the daemon link.</param>
	/// <param name="dial">A dialer that replaces the link's own, for checks; null for the link's own.</param>
	/// <param name="daemonExecutable">The Loom executable that starts the daemon, for checks; null for this process's own.</param>
	/// <param name="stream">The window stream of an earlier link with the same paired host, such as before pairing with it again, so stream panels open
	/// on it carry on over this link; null starts a new one.</param>
	public RemoteClient( LoknApplication app, ChatHost host, Func<OmpService> backend, Func<Action, bool> post, HostLinkKind kind,
		string dataFolder, PairedHost? paired, Func<CancellationToken, Task<ClientCarrier>>? dial = null, string? daemonExecutable = null, ClientStreamSource? stream = null )
	{
		_app = app;
		Host = host;
		_store = host.Store;
		_projects = host.Projects;
		_sessions = host.Sessions;
		_backend = backend;
		_post = post;
		Kind = kind;
		_dataFolder = dataFolder;
		_paired = paired;
		_dial = dial;
		_daemonExecutable = daemonExecutable;
		Stream = kind == HostLinkKind.Paired ? stream ?? new ClientStreamSource() : null;
		_tunnel = kind == HostLinkKind.Paired ? new TunnelClient() : null;
	}

	/// <summary>Gets the host whose chats this client shows: This PC's for the daemon link, else the paired host's.</summary>
	public ChatHost Host
	{
		get;
	}

	/// <summary>Gets whether this links with this PC's daemon or a paired host.</summary>
	public HostLinkKind Kind
	{
		get;
	}

	/// <summary>Raised on the UI thread with each <c>admin.pairing</c>, <c>admin.pairing.done</c> and <c>admin.paired</c> push of the daemon link.</summary>
	public event Action<JsonObject>? AdminPushed;

	/// <summary>Raised on the UI thread when this PC's daemon failed, with what happened: the process Loom started ended before it opened its pipe or
	/// did not open it in time, or a connection dropped while dialing was not paused and the daemon no longer runs. Only a daemon link that starts and
	/// stops the real daemon raises it.</summary>
	public event Action<string>? DaemonFailed;

	/// <summary>Gets what the client is doing; reading it rebuilds the reader when it changes.</summary>
	public ClientState Status
	{
		get
		{
			_ = _version.Value;
			return field;
		}
		private set;
	} = ClientState.Connecting;

	/// <summary>Gets the status line: what the client does, the host it reached or why it failed.</summary>
	public string StatusText
	{
		get
		{
			_ = _version.Value;
			return field;
		}
		private set;
	} = "Connecting…";

	/// <summary>Gets the connected host's name, or empty.</summary>
	public string HostName { get; private set; } = "";

	/// <summary>Gets the build the connected daemon runs, or null when it did not say (an older daemon) or nothing is connected.</summary>
	public string? HostVersion
	{
		get
		{
			_ = _version.Value;
			return field;
		}
		private set;
	}

	/// <summary>Gets whether the connected daemon runs another build of Loom than this one, so it should be restarted to update it.</summary>
	public bool IsOutdated => Kind == HostLinkKind.Daemon && Status == ClientState.Connected && HostVersion != RemoteProtocol.AppVersion;

	/// <summary>Gets the scopes the connected host grants, or none while disconnected.</summary>
	public IReadOnlyList<string> Scopes { get; private set; } = [];

	/// <summary>Gets a paired host's window stream, the same across reconnects; null for the daemon link.</summary>
	public ClientStreamSource? Stream
	{
		get;
	}

	/// <summary>Gets the paired host's entry as this client dials it; null for the daemon link.</summary>
	internal PairedHost? Paired => _paired;

	/// <summary>Gets the daemon's log: <c>%TEMP%\Lokn\host.log</c>, or <c>host.log</c> in a bare folder, as the shell's HostRunner writes it.</summary>
	public static string DaemonLogPath => LoknPaths.IsCustom ? Path.Combine( LoknPaths.DataRoot, "host.log" ) : Path.Combine( Path.GetTempPath(), "Lokn", "host.log" );

	/// <summary>Gets the browser tabs of this client's host.</summary>
	private BrowserTabs Tabs => Host.Host.BrowserTabs;

	/// <summary>Gets whether <see cref="Stop"/> ran, after which nothing posted late changes the host's mirrors or state.</summary>
	private bool IsStopped => _stopping.IsCancellationRequested;

	/// <summary>Gets whether this link starts and stops this PC's real daemon: the daemon link on Windows without a check's dialer, so a check's link
	/// never touches the user's daemon.</summary>
	[SupportedOSPlatformGuard( "windows" )]
	private bool UsesDaemonControl => Kind == HostLinkKind.Daemon && _dial is null && OperatingSystem.IsWindows();

	/// <summary>Turns the host's stores and browser tabs into mirrors that forward their changes, supplies its chat sessions, installs an offline
	/// workspace, the browser's tunnel to the host's loopback and the upload of browser files for a paired host, follows the tools this Loom lends the
	/// host, and starts connecting.</summary>
	public void Start()
	{
		_projects.Forward = ForwardStore;
		_store.Forward = ForwardStore;
		// The daemon's tabs come from the same file, so they stay meanwhile; a paired host's are not this PC's, nor are its files and loopback.
		Tabs.BeginMirror( ForwardBrowser, isRemote: Kind == HostLinkKind.Paired );
		if ( Kind == HostLinkKind.Paired )
		{
			if ( Host.Host.Workspace is not OfflineWorkspaceHost )
				ReplaceWorkspace( new OfflineWorkspaceHost( Host.Name ) );
			Tabs.Tunnel = _tunnel;
			Tabs.ShareFile = ShareFileAsync;
			_app.Settings.Changed += OfferTools;
		}
		_sessions.Source = CreateSession;
		_app.Tools.Changed += OfferTools;
		UpdateStatus( ClientState.Connecting, ConnectingText );
		_ = Task.Run( RunAsync );
	}

	/// <summary>Stops connecting and closes the connection. The host's mirrors keep refusing changes and its sessions stay offline proxies
	/// (<see cref="ChatHost.Detach"/>), its browser tabs wait (<see cref="BrowserTabs.Detach"/>) and a paired host's workspace becomes an offline
	/// one, with no delegate of this module left behind; neither falls back to this PC's own chats or disk. The host shows as stopped until a
	/// client starts again.</summary>
	public void Stop()
	{
		_stopping.Cancel();
		_app.Tools.Changed -= OfferTools;
		_app.Settings.Changed -= OfferTools;
		if ( _peer is { } peer )
			_ = peer.DisposeAsync();
		_peer = null;
		Scopes = [];
		Stream?.Bind( null );
		Host.Detach();
		Tabs.Detach();
		_tunnel?.Dispose();
		if ( Kind == HostLinkKind.Paired )
			ReplaceWorkspace( new OfflineWorkspaceHost( Host.Name ) );
		Status = ClientState.Stopped;
		StatusText = "Remote access is off in this Loom.";
		_version.Value++;
		Host.Host.Update( LoknHostState.Stopped, StatusText, Kind == HostLinkKind.Daemon );
	}

	/// <summary>Reconnects at once instead of after the current pause, such as after pairing or starting the daemon again.</summary>
	public void Reconnect()
	{
		_isPaused = false;
		if ( _peer is { } peer )
			_ = peer.DisposeAsync();
		_wake.Cancel();
	}

	/// <summary>Stops this PC's daemon and pauses dialing, so Loom does not start it again until <see cref="Reconnect"/>; with a check's dialer it only
	/// pauses, since that link has no real daemon.</summary>
	/// <remarks>Its running turns stop and every device disconnects; the Remote access panel asks first, and moving the chats back into this Loom pauses
	/// the link this way before it waits for the daemon to exit.</remarks>
	public void StopDaemon()
	{
		_isPaused = true;
		if ( UsesDaemonControl )
			DaemonControl.Stop();
		if ( _peer is { } peer )
			_ = peer.DisposeAsync();
		UpdateStatus( ClientState.Stopped, "The daemon is stopped." );
	}

	/// <summary>Stops this PC's daemon, waits until it saved and exited, and connects again, which starts the current build; nothing with a check's dialer.</summary>
	public void RestartDaemon()
	{
		if ( !UsesDaemonControl )
			return;

		_isPaused = true;
		UpdateStatus( ClientState.Connecting, "Restarting the daemon…" );
		_ = Task.Run( () =>
		{
			var isStopped = OperatingSystem.IsWindows() && DaemonControl.StopAndWait( DaemonStopTimeout );
			_post( () =>
			{
				if ( isStopped )
					Reconnect();
				else
					UpdateStatus( ClientState.Failed, $"The daemon did not stop. Its log is {DaemonLogPath}." );
			} );
		} );
	}

	/// <summary>Sends a request to the connected host, such as an <c>admin.*</c> request of the Remote access panel.</summary>
	/// <exception cref="IOException">The client is not connected.</exception>
	/// <exception cref="RemoteException">The host refused.</exception>
	public Task<JsonObject> RequestAsync( JsonObject frame, CancellationToken cancellationToken ) =>
		(_peer ?? throw new IOException( "Not connected to the host." )).RequestAsync( frame, cancellationToken );

	/// <summary>Replaces the paired host's entry, such as after the user renamed the host, and shows its name, or the host's own when it has none.</summary>
	internal void UpdatePaired( PairedHost paired )
	{
		_paired = paired;
		Host.Host.Name = ComputeName( paired, HostName );
	}

	/// <summary>Gets the name a paired host shows under: the user's, else the one the host reports now, else the one it reported when it last connected,
	/// else the start of its id.</summary>
	internal static string ComputeName( PairedHost paired, string hostName ) =>
		paired.Name.Length > 0 ? paired.Name
		: hostName.Length > 0 ? hostName
		: paired.HostName.Length > 0 ? paired.HostName
		: paired.HostId[..Math.Min( 6, paired.HostId.Length )];

	/// <summary>Gets the line shown while this client dials.</summary>
	private string ConnectingText => Kind == HostLinkKind.Daemon ? "Connecting to the daemon…" : $"Connecting to {Host.Name}…";

	/// <summary>Connects, serves and reconnects with a pause from 1 to 30 seconds that doubles after each failure, until stopped.</summary>
	private async Task RunAsync()
	{
		var token = _stopping.Token;
		var delay = FirstDelay;
		while ( !token.IsCancellationRequested )
		{
			if ( !_isPaused )
			{
				try
				{
					_post( () => UpdateStatus( ClientState.Connecting, ConnectingText ) );
					var carrier = await (_dial ?? DialAsync)( token ).ConfigureAwait( false );
					await ServeAsync( carrier, token ).ConfigureAwait( false );
					delay = FirstDelay;
					if ( await IsDaemonGoneAsync( token ).ConfigureAwait( false ) )
						ReportDaemonFailure( "The daemon stopped while Loom was connected to it." );
					_post( () =>
					{
						if ( !_isPaused )
							UpdateStatus( ClientState.Connecting, "Disconnected. Reconnecting…" );
					} );
				}
				catch ( OperationCanceledException ) when ( token.IsCancellationRequested )
				{
					return;
				}
				catch ( Exception exception )
				{
					var message = exception.Message;
					var wait = delay;
					_post( () =>
					{
						if ( !_isPaused )
							UpdateStatus( ClientState.Failed, $"{message} Retrying in {wait.TotalSeconds:0} s." );
					} );
				}
			}

			var wake = _wake;
			using ( var pause = CancellationTokenSource.CreateLinkedTokenSource( token, wake.Token ) )
			{
				try
				{
					await Task.Delay( _isPaused ? Timeout.InfiniteTimeSpan : delay, pause.Token ).ConfigureAwait( false );
					delay = TimeSpan.FromTicks( Math.Min( delay.Ticks * 2, LastDelay.Ticks ) );
				}
				catch ( OperationCanceledException )
				{
					delay = FirstDelay;
				}
			}
			if ( wake.IsCancellationRequested )
				Interlocked.CompareExchange( ref _wake, new CancellationTokenSource(), wake );
		}
	}

	/// <summary>Speaks the protocol on one connection: registers the pushes, syncs within <see cref="SyncTimeout"/>, applies the snapshot and
	/// re-attaches open chats, then waits until it closes.</summary>
	private async Task ServeAsync( ClientCarrier carrier, CancellationToken cancellationToken )
	{
		var peer = new RpcPeer( carrier.Connection );
		var link = new ClientLink();
		var closed = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
		peer.Closed += _ => closed.TrySetResult();
		foreach ( var type in (string[])["store", "working"] )
			peer.OnPush( type, frame => _post( () => ApplyListPush( link, frame ) ) );
		foreach ( var type in (string[])["state", "msg", "msgs", "tool", "evt"] )
			peer.OnPush( type, frame => _post( () => ApplyChatPush( frame ) ) );
		foreach ( var type in (string[])["browser.tab", "browser.closed"] )
			peer.OnPush( type, frame => _post( () => ApplyBrowserPush( link, frame ) ) );
		// The daemon tells Loom on this PC about devices that paired, so the user answers whether they may control this PC.
		if ( Kind == HostLinkKind.Daemon )
		{
			foreach ( var type in (string[])["admin.pairing", "admin.pairing.done", "admin.paired"] )
				peer.OnPush( type, frame => _post( () => ApplyAdminPush( frame ) ) );
		}
		// Loom on this PC runs the daemon's tool calls; a paired PC runs only its browser tools, which act on its own windows' views of the session's tabs.
		peer.OnPush( "host.addrs", frame => _post( () => ApplyAddressPush( frame ) ) );
		peer.OnPush( "tool.call", frame => _post( () => _ = RunToolAsync( peer, frame ) ) )
			.OnPush( "tool.cancel", frame => _post( () => CancelTool( frame ) ) );
		var stream = Kind == HostLinkKind.Paired ? new RemoteStreamSource( peer, carrier.Scopes.Contains( DeviceScopes.Control ) ) : null;
		var workspace = Kind == HostLinkKind.Paired ? new RemoteWorkspaceHost( peer ) : null;
		peer.Start();
		try
		{
			using ( cancellationToken.Register( () => _ = peer.DisposeAsync() ) )
			{
				JsonObject sync;
				JsonObject? browser;
				using ( var timeout = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken ) )
				{
					timeout.CancelAfter( SyncTimeout );
					try
					{
						sync = await peer.RequestAsync( Frame.Create( "sync" ), timeout.Token ).ConfigureAwait( false );
						browser = await RequestBrowserAsync( peer, timeout.Token ).ConfigureAwait( false );
					}
					catch ( OperationCanceledException ) when ( !cancellationToken.IsCancellationRequested )
					{
						throw new IOException( $"The host did not answer within {SyncTimeout.TotalSeconds:0} s." );
					}
				}
				await UiInvoke.RunAsync( _post, () => ApplyConnected( peer, carrier, sync, browser, link, stream, workspace ) ).ConfigureAwait( false );
				await closed.Task.ConfigureAwait( false );
			}
		}
		finally
		{
			await peer.DisposeAsync().ConfigureAwait( false );
			_post( () =>
			{
				ApplyDisconnected( peer );
				if ( workspace is not null && Host.Host.Workspace != workspace )
					workspace.Dispose();
			} );
		}
	}

	/// <summary>Takes a new connection's snapshot: loads the mirrors, the browser tabs and the working chats, applies the pushes held meanwhile, swaps in
	/// the host's workspace, stream and tunnels, re-attaches every chat a panel shows, offers the tools this Loom lends, and marks the host synced. A
	/// daemon of another build is restarted once while no chat works.</summary>
	private void ApplyConnected( RpcPeer peer, ClientCarrier carrier, JsonObject sync, JsonObject? browser, ClientLink link, RemoteStreamSource? stream, RemoteWorkspaceHost? workspace )
	{
		// The user stopped the daemon, or this client stopped, while this connection was being made.
		if ( _isPaused || IsStopped || peer.IsClosed )
		{
			_ = peer.DisposeAsync();
			return;
		}

		var conversations = WireJson.Read<ConversationInfo[]>( sync, "conversations" ) ?? [];
		_projects.Load( WireJson.Read<ProjectInfo[]>( sync, "projects" ) ?? [] );
		_store.Load( conversations );
		var working = (Frame.ReadStrings( sync, "working" ) ?? []).ToHashSet( StringComparer.Ordinal );
		foreach ( var conversation in conversations )
			_store.SetWorking( conversation.Id, working.Contains( conversation.Id.ToString( "N" ) ) );
		link.Revision = Frame.ReadLong( sync, "rev" ) ?? -1;
		link.IsSynced = true;
		foreach ( var push in link.Held )
			ApplyListPush( link, push );
		link.Held.Clear();

		_peer = peer;
		_boot = Frame.ReadString( sync, "boot" );
		HostName = Frame.ReadString( sync, "hostName" ) ?? "";
		HostVersion = Frame.ReadString( sync, "version" );
		if ( _paired is { } paired )
		{
			UpdatePairedReport( paired, Frame.ReadStrings( sync, "addrs" ), HostName );
			Host.Host.Name = ComputeName( _paired ?? paired, HostName );
			// A chat outside projects works in the host's home folder, never in a folder named after This PC's.
			_store.HostHome = Frame.ReadString( sync, "home" );
		}
		Scopes = carrier.Scopes;
		Stream?.Bind( stream );
		_tunnel?.Bind( peer, carrier.Scopes.Contains( DeviceScopes.Control ) );
		// An older host without browser tabs leaves the mirror waiting; changes made meanwhile are sent once a host has them.
		if ( browser is not null )
			Tabs.Load( WireJson.Read<BrowserTabInfo[]>( browser, "tabs" ) ?? [] );
		link.IsBrowserSynced = true;
		foreach ( var push in link.BrowserHeld )
			ApplyBrowserPush( link, push );
		link.BrowserHeld.Clear();
		if ( workspace is not null )
			ReplaceWorkspace( workspace );

		foreach ( var (id, chat) in _chats )
		{
			if ( chat.Panels.Count > 0 )
				_ = ReattachAsync( id, chat, peer );
		}
		OfferTools();
		UpdateStatus( ClientState.Connected, $"Connected to {(HostName.Length > 0 ? HostName : "the host")}{(carrier.Via == "pipe" ? "" : $" via {carrier.Via}")}." );
		Host.MarkSynced();

		// Loom was updated or rebuilt since the daemon started; a daemon with no running turn is brought to this build at once.
		if ( IsOutdated && _dial is null && working.Count == 0 && !_isVersionRestarted )
		{
			_isVersionRestarted = true;
			RestartDaemon();
		}
	}

	/// <summary>Installs a workspace on the paired host and disposes the remote one it replaces, unless it is the one installed.</summary>
	private void ReplaceWorkspace( IWorkspaceHost workspace )
	{
		var previous = Host.Host.Workspace;
		if ( previous == workspace )
			return;

		Host.Host.SetWorkspace( workspace );
		(previous as RemoteWorkspaceHost)?.Dispose();
	}

	/// <summary>Saves what the paired host reported as it connected or when its network changed: the addresses it listens on now, first, then the ones
	/// known before, so the next dial reaches it after its addresses changed without pairing again; and its name, which then shows while it is offline.</summary>
	/// <remarks>Earlier addresses stay after the reported ones, up to <see cref="MaxAddresses"/>, since a host that switched modes, such as from the LAN to
	/// Funnel, closes the connection before a device could learn the new addresses, and may switch back.</remarks>
	private void UpdatePairedReport( PairedHost paired, IReadOnlyList<string>? addresses, string hostName )
	{
		IReadOnlyList<string> addrs = addresses is { Count: > 0 }
			? [.. addresses.Concat( paired.Addrs ).Distinct( StringComparer.Ordinal ).Take( MaxAddresses )]
			: paired.Addrs;
		var name = hostName.Length > 0 ? hostName : paired.HostName;
		if ( paired.Addrs.SequenceEqual( addrs, StringComparer.Ordinal ) && paired.HostName == name )
			return;

		_paired = paired with { Addrs = [.. addrs], HostName = name };
		try
		{
			PairedHosts.UpdateReported( _dataFolder, paired.HostId, addrs, name );
		}
		catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or InvalidDataException )
		{
			_app.ReportError( "Remote", exception );
		}
	}

	/// <summary>Defines how many host addresses a paired PC keeps, the newest reported first.</summary>
	private const int MaxAddresses = 8;

	/// <summary>Follows <c>host.addrs</c>, which the host pushes when its addresses change while this PC stays connected.</summary>
	private void ApplyAddressPush( JsonObject frame )
	{
		if ( _paired is { } paired )
			UpdatePairedReport( paired, Frame.ReadStrings( frame, "addrs" ), HostName );
	}

	/// <summary>Raises <see cref="AdminPushed"/> for an <c>admin.*</c> push of the daemon link.</summary>
	private void ApplyAdminPush( JsonObject frame )
	{
		if ( !IsStopped )
			AdminPushed?.Invoke( frame );
	}

	/// <summary>Forgets a closed connection, dropping chat pushes held for attaches that will not be answered; a paired host's workspace goes offline
	/// until the next connection, and the closed connection's one is disposed by its owner.</summary>
	private void ApplyDisconnected( RpcPeer peer )
	{
		if ( _peer != peer )
			return;

		_peer = null;
		_lastOffer = null;
		Stream?.Bind( null );
		_tunnel?.Bind( null, canControl: false );
		Scopes = [];
		if ( Kind == HostLinkKind.Paired )
			Host.Host.SetWorkspace( new OfflineWorkspaceHost( Host.Name ) );
		foreach ( var chat in _chats.Values )
		{
			chat.Generation++;
			chat.IsAttaching = false;
			chat.Held.Clear();
		}
		foreach ( var call in _toolCalls.Values )
			call.Cancel();
		_toolCalls.Clear();
		_version.Value++;
	}

	/// <summary>Offers the tools this Loom lends the host, which publishes them to its agents: every tool to the daemon on this PC, and to a paired host
	/// only the browser tools while the user lets its agents use this device's browser (<see cref="RemoteModule.LendBrowser"/>) and the host lets this
	/// PC control. Sent on connecting and whenever the tools or the settings change, when the offer differs from the last one sent.</summary>
	private void OfferTools()
	{
		if ( _peer is not { } peer || (Kind == HostLinkKind.Paired && !Scopes.Contains( DeviceScopes.Control )) )
			return;

		AgentToolInfo[] tools = Kind == HostLinkKind.Daemon ? [.. _app.Tools.Items]
			: IsLendingBrowser ? [.. _app.Tools.Items.Where( tool => tool.ModuleId == ToolService.BrowserModuleId )]
			: [];
		var offer = WireJson.BuildNode( tools );
		var key = offer?.ToJsonString();
		if ( key == _lastOffer )
			return;

		_lastOffer = key;
		var frame = Frame.Create( "tools.offer" );
		frame["tools"] = offer;
		_ = SendOfferAsync( peer, frame );
	}

	/// <summary>Gets whether the user lets a paired host's agents use this device's browser.</summary>
	private bool IsLendingBrowser => _app.Settings.Get( RemoteModule.LendBrowser, SettingsScope.Global );

	/// <summary>Sends a <c>tools.offer</c> and reports a refusal on the UI thread.</summary>
	private async Task SendOfferAsync( RpcPeer peer, JsonObject frame )
	{
		try
		{
			await peer.RequestAsync( frame, _stopping.Token ).ConfigureAwait( false );
		}
		catch ( RemoteException exception ) when ( Kind == HostLinkKind.Daemon || exception.Code != RemoteErrors.Scope )
		{
			_post( () => _app.ReportError( "Remote tools", exception ) );
		}
		catch ( RemoteException )
		{
			// A paired host that takes no browser tools from devices; its agents do without this device's browser.
		}
		catch ( Exception exception ) when ( exception is IOException or OperationCanceledException )
		{
			// The next connection offers the tools again.
		}
	}

	/// <summary>Runs a <c>tool.call</c> of the host's agent with this Loom's tools and answers its <c>tool.result</c>, or its <c>error</c> when the
	/// tool failed or was cancelled, so the host's call never waits for nothing; on the UI thread. The call names the agent's conversation, so a tool finds
	/// the chat even when no window shows it. A paired PC runs only the browser tools it lends, and names the host as the call's host, so the tool acts on
	/// that host's browser tabs.</summary>
	private async Task RunToolAsync( RpcPeer peer, JsonObject frame )
	{
		var call = Frame.ReadString( frame, "call" ) ?? "";
		var name = Frame.ReadString( frame, "name" ) ?? "";
		Guid? caller = Frame.ReadString( frame, "caller" ) is { } text && Guid.TryParseExact( text, "N", out var panel ) ? panel : null;
		Guid? conversation = Frame.ReadString( frame, "conversation" ) is { } chat && Guid.TryParseExact( chat, "N", out var chatId ) ? chatId : null;
		var cancellation = CancellationTokenSource.CreateLinkedTokenSource( _stopping.Token );
		_toolCalls[call] = cancellation;
		var answer = Frame.Create( "tool.result" );
		answer["call"] = call;
		try
		{
			if ( Kind == HostLinkKind.Paired && (!IsLendingBrowser || _app.Tools.Items.FirstOrDefault( tool => tool.Name == name )?.ModuleId != ToolService.BrowserModuleId) )
				throw new UnauthorizedAccessException( "This device does not let the host's agents use that tool." );

			var result = await _app.Tools.InvokeAsync( name, WireJson.BuildElement( frame["args"] ), caller, cancellation.Token, call,
				conversation: conversation, host: Host.IsLocal ? null : Host.Id ).ConfigureAwait( false );
			answer["result"] = new JsonObject
			{
				["text"] = result.Text,
				["isError"] = result.IsError,
				["details"] = result.SerializeDetails() is { } details ? JsonSerializer.SerializeToNode( details ) : null,
			};
		}
		catch ( OperationCanceledException )
		{
			answer["error"] = "The call was cancelled.";
		}
		catch ( Exception exception )
		{
			answer["error"] = $"The tool failed in Loom on this PC: {exception.Message}";
		}
		finally
		{
			// The call's record changes on the UI thread only, where a tool.cancel or a disconnect reads it.
			_post( () =>
			{
				if ( _toolCalls.TryGetValue( call, out var current ) && current == cancellation )
					_toolCalls.Remove( call );
				cancellation.Dispose();
			} );
		}

		try
		{
			await peer.RequestAsync( answer, _stopping.Token ).ConfigureAwait( false );
		}
		catch ( Exception exception ) when ( exception is RemoteException or IOException or OperationCanceledException )
		{
			// The daemon stopped waiting, such as after its connection closed.
		}
	}

	/// <summary>Cancels a running <c>tool.call</c> the daemon's agent abandoned.</summary>
	private void CancelTool( JsonObject frame )
	{
		if ( Frame.ReadString( frame, "call" ) is { } call && _toolCalls.TryGetValue( call, out var cancellation ) )
			cancellation.Cancel();
	}

	/// <summary>Creates the proxy session of a chat the host runs.</summary>
	private IChatSession CreateSession( Guid conversationId ) =>
		new RemoteChatSession( _app, _store, conversationId, _backend(), new Channel( this, conversationId ), _post );

	/// <summary>Carries a proxy's call: <c>Attach</c> and <c>Detach</c> become <c>attach</c> and <c>detach</c> for the first and last panel, the rest <c>call</c>.</summary>
	private async Task<JsonElement> CallAsync( Guid id, string method, JsonElement[] args, CancellationToken cancellationToken )
	{
		if ( !_chats.TryGetValue( id, out var chat ) )
			_chats.Add( id, chat = new ClientChat() );

		switch ( method )
		{
			case ChatSessionCall.Attach:
				if ( chat.Panels.Add( args[0].Deserialize<Guid>() ) && chat.Panels.Count == 1 && _peer is { } attachPeer )
					await AttachAsync( id, chat, attachPeer ).ConfigureAwait( false );
				return None;
			case ChatSessionCall.Detach:
				if ( chat.Panels.Remove( args[0].Deserialize<Guid>() ) && chat.Panels.Count == 0 && _peer is { } detachPeer )
				{
					// A detach makes any attach still under way stale.
					chat.Generation++;
					chat.IsAttaching = false;
					chat.Held.Clear();
					await detachPeer.RequestAsync( Frame.Create( "detach" ).WithChannel( id ), cancellationToken ).ConfigureAwait( false );
				}
				return None;
		}

		var peer = _peer ?? throw new IOException( "Not connected to the host." );
		if ( Kind == HostLinkKind.Paired && method is ChatSessionCall.Send or ChatSessionCall.Interject && args.Length > 1 )
			args = [args[0], await UploadAsync( peer, args[1], cancellationToken ).ConfigureAwait( false ), .. args[2..]];

		var frame = Frame.Create( "call" ).WithChannel( id );
		frame["m"] = method;
		frame["a"] = new JsonArray( [.. args.Select( arg => JsonSerializer.SerializeToNode( arg ) )] );
		var answer = await peer.RequestAsync( frame, cancellationToken ).ConfigureAwait( false );
		return WireJson.BuildElement( answer["r"] );
	}

	/// <summary>Attaches a chat on the host: holds its pushes until the answer and any further transcript pages, applies them, then the held pushes
	/// the answer did not cover. A newer attach or a detach meanwhile makes this one stale, so it changes nothing.</summary>
	private async Task AttachAsync( Guid id, ClientChat chat, RpcPeer peer )
	{
		var generation = ++chat.Generation;
		chat.IsAttaching = true;
		chat.Held.Clear();
		var since = _sessions.Find( id ) is RemoteChatSession current && chat.Boot is not null && chat.Boot == _boot ? current.Seq : -1;
		var frame = Frame.Create( "attach" ).WithChannel( id );
		frame["since"] = since;
		frame["panel"] = chat.Panels.First().ToString( "N" );
		JsonObject answer;
		try
		{
			answer = await peer.RequestAsync( frame, _stopping.Token ).ConfigureAwait( false );
			if ( answer["messages"] is JsonArray messages && Frame.ReadLong( answer, "pages" ) is { } pages )
			{
				for ( var page = 1L; page < pages; page++ )
				{
					var request = Frame.Create( "attach.page" ).WithChannel( id );
					request["page"] = page;
					if ( (await peer.RequestAsync( request, _stopping.Token ).ConfigureAwait( false ))["messages"] is JsonArray more )
					{
						while ( more.Count > 0 )
						{
							var message = more[0];
							more.RemoveAt( 0 );
							messages.Add( message );
						}
					}
				}
			}
		}
		catch
		{
			_post( () =>
			{
				if ( chat.Generation != generation )
					return;
				chat.IsAttaching = false;
				chat.Held.Clear();
			} );
			throw;
		}

		await UiInvoke.RunAsync( _post, () =>
		{
			if ( chat.Generation != generation )
				return;

			chat.IsAttaching = false;
			JsonObject[] held = [.. chat.Held];
			chat.Held.Clear();
			if ( _peer != peer || _sessions.Find( id ) is not RemoteChatSession session )
				return;

			session.ApplyAttach(
				Frame.ReadSeq( answer ) ?? 0,
				WireJson.Require<ChatSessionState>( answer, "state" ),
				WireJson.Read<Dictionary<string, JsonElement>>( answer, "tools" ) ?? [],
				WireJson.Read<Dictionary<string, string>>( answer, "intents" ) ?? [],
				WireJson.Read<ConversationMessage[]>( answer, "messages" ) );
			chat.Boot = _boot;
			foreach ( var push in held )
				ApplyChatPush( session, push );
		} ).ConfigureAwait( false );
	}

	/// <summary>Attaches a chat again after a reconnect; a refusal shows as an error, a connection that closed meanwhile is retried by the next connect.</summary>
	private async Task ReattachAsync( Guid id, ClientChat chat, RpcPeer peer )
	{
		try
		{
			await AttachAsync( id, chat, peer ).ConfigureAwait( false );
		}
		catch ( RemoteException exception )
		{
			_post( () => _app.ReportError( "Remote", exception ) );
		}
		catch ( Exception exception ) when ( exception is IOException or OperationCanceledException )
		{
		}
	}

	/// <summary>Uploads a prompt's attachments to a paired host in chunks and answers their paths there.</summary>
	private static async Task<JsonElement> UploadAsync( RpcPeer peer, JsonElement attachments, CancellationToken cancellationToken )
	{
		var paths = attachments.Deserialize<string[]>( StoreOp.JsonOptions ) ?? [];
		var hosted = new List<string>( paths.Length );
		foreach ( var path in paths )
			hosted.Add( File.Exists( path ) ? await UploadFileAsync( peer, path, cancellationToken ).ConfigureAwait( false ) : path );
		return JsonSerializer.SerializeToElement( hosted, StoreOp.JsonOptions );
	}

	/// <summary>Uploads one file to the host in chunks and answers its path there.</summary>
	private static async Task<string> UploadFileAsync( RpcPeer peer, string path, CancellationToken cancellationToken )
	{
		var buffer = new byte[UploadChunk];
		var name = Guid.NewGuid().ToString( "N" ) + "-" + Path.GetFileName( path );
		await using var file = File.OpenRead( path );
		long offset = 0;
		while ( true )
		{
			var read = await file.ReadAsync( buffer, cancellationToken ).ConfigureAwait( false );
			var isFinal = offset + read >= file.Length;
			var chunk = Frame.Create( "upload" );
			chunk["name"] = name;
			chunk["offset"] = offset;
			chunk["data"] = Convert.ToBase64String( buffer, 0, read );
			chunk["final"] = isFinal;
			var answer = await peer.RequestAsync( chunk, cancellationToken ).ConfigureAwait( false );
			offset += read;
			if ( isFinal )
				return Frame.ReadString( answer, "path" ) ?? throw new IOException( "The host did not say where it put the file." );
		}
	}

	/// <summary>Uploads a file a lent browser tool saved on this PC, such as a screenshot, to the host, whose agent called the tool, and answers its path
	/// there (<see cref="BrowserTabs.ShareFile"/>).</summary>
	/// <exception cref="IOException">No host is connected.</exception>
	private Task<string> ShareFileAsync( string path, CancellationToken cancellationToken ) =>
		UploadFileAsync( _peer ?? throw new IOException( "Not connected to the host." ), path, cancellationToken );

	/// <summary>Sends a browser tabs mirror's change to the host, whose answer reaches the mirror (<see cref="BrowserTabs.Answer"/>): false while
	/// disconnected, so the mirror sends it again after the next snapshot; a view-only PC's change is refused at once, and the next snapshot undoes it.</summary>
	private bool ForwardBrowser( BrowserOp op )
	{
		if ( _peer is not { } peer )
			return false;
		if ( !Scopes.Contains( DeviceScopes.Control ) )
		{
			// A page's own reports are not the user's doing; only report what the user tried.
			if ( op.Op != BrowserOp.ReportKind )
				_app.ReportError( "Remote", new UnauthorizedAccessException( $"This PC can only view {Host.Name}'s browser tabs. Allow control for it in Remote access on that PC." ) );
			var tabs = Tabs;
			_post( () => tabs.Answer( op, null ) );
			return true;
		}

		var frame = BrowserService.BuildFields( op );
		frame["t"] = $"browser.{op.Op}";
		_ = SendBrowserAsync( peer, frame, op );
		return true;
	}

	/// <summary>Sends a browser change and gives the mirror the host's answer on the UI thread: the tab's revision, or none after a refusal, which is
	/// reported, or a lost connection.</summary>
	private async Task SendBrowserAsync( RpcPeer peer, JsonObject frame, BrowserOp op )
	{
		long? rev = null;
		try
		{
			rev = Frame.ReadLong( await peer.RequestAsync( frame, _stopping.Token ).ConfigureAwait( false ), "rev" );
		}
		catch ( RemoteException exception )
		{
			_post( () => _app.ReportError( "Remote browser", exception ) );
		}
		catch ( Exception exception ) when ( exception is IOException or OperationCanceledException )
		{
			// The next snapshot shows what the host has.
		}
		var tabs = Tabs;
		_post( () => tabs.Answer( op, rev ) );
	}

	/// <summary>Asks for the host's browser tabs, or null from a host that has none to give.</summary>
	private static async Task<JsonObject?> RequestBrowserAsync( RpcPeer peer, CancellationToken cancellationToken )
	{
		try
		{
			return await peer.RequestAsync( Frame.Create( "browser.sync" ), cancellationToken ).ConfigureAwait( false );
		}
		catch ( RemoteException )
		{
			return null;
		}
	}

	/// <summary>Applies a <c>browser.tab</c> or <c>browser.closed</c> push: held until the browser snapshot is loaded, then applied when newer than the
	/// mirror's tab.</summary>
	private void ApplyBrowserPush( ClientLink link, JsonObject frame )
	{
		if ( IsStopped )
			return;
		if ( !link.IsBrowserSynced )
		{
			link.BrowserHeld.Add( frame );
			return;
		}

		try
		{
			if ( Frame.ReadType( frame ) == "browser.closed" )
				Tabs.ApplyClosed( WireJson.Require<Guid>( frame, "tab" ) );
			else
				Tabs.ApplyTab( WireJson.Require<BrowserTabInfo>( frame, "tab" ) );
		}
		catch ( RemoteException exception )
		{
			_app.ReportError( "Remote", exception );
		}
	}

	/// <summary>Sends a mirror's change to the host, refusing it at once while disconnected or when the host lets this PC only view.</summary>
	private bool ForwardStore( StoreOp op )
	{
		if ( _peer is not { } peer )
		{
			_app.ReportError( "Remote", new IOException( "Not connected to the host, so the change was not made." ) );
			return false;
		}
		if ( !Scopes.Contains( DeviceScopes.Control ) )
		{
			_app.ReportError( "Remote", new UnauthorizedAccessException( $"This PC can only view {Host.Name}'s chats. Allow control for it in Remote access on that PC." ) );
			return false;
		}

		_ = SendStoreAsync( peer, op );
		return true;
	}

	/// <summary>Sends a store operation and reports a refusal on the UI thread.</summary>
	private async Task SendStoreAsync( RpcPeer peer, StoreOp op )
	{
		var frame = Frame.Create( "store" );
		frame["op"] = WireJson.BuildNode( op );
		try
		{
			await peer.RequestAsync( frame, _stopping.Token ).ConfigureAwait( false );
		}
		catch ( Exception exception ) when ( exception is RemoteException or IOException or OperationCanceledException )
		{
			_post( () => _app.ReportError( "Remote", exception ) );
		}
	}

	/// <summary>Applies a <c>store</c> or <c>working</c> push of a connection: held until its snapshot is loaded, then applied unless the snapshot's
	/// revision already covers it.</summary>
	private void ApplyListPush( ClientLink link, JsonObject frame )
	{
		if ( IsStopped )
			return;
		if ( !link.IsSynced )
		{
			link.Held.Add( frame );
			return;
		}
		if ( Frame.ReadLong( frame, "rev" ) is { } revision && revision <= link.Revision )
			return;

		if ( Frame.ReadType( frame ) == "working" )
			ApplyWorkingPush( frame );
		else
			ApplyStorePush( frame );
	}

	/// <summary>Applies a <c>store</c> push to the mirror it is for.</summary>
	private void ApplyStorePush( JsonObject frame )
	{
		try
		{
			var op = WireJson.Require<StoreOp>( frame, "op" );
			if ( op.S == StoreOp.Projects )
				_projects.Apply( op );
			else
				_store.Apply( op );
		}
		catch ( Exception exception ) when ( exception is ArgumentException or KeyNotFoundException or InvalidOperationException or RemoteException )
		{
			_app.ReportError( "Remote", exception );
		}
	}

	/// <summary>Applies a <c>working</c> push to the mirror's working chats.</summary>
	private void ApplyWorkingPush( JsonObject frame )
	{
		if ( Frame.ReadChannel( frame ) is { } id && Frame.ReadBool( frame, "busy" ) is { } isBusy )
			_store.SetWorking( id, isBusy );
	}

	/// <summary>Applies a chat push to its proxy, or holds it while the chat's attach awaits its answer; a push for a chat no panel shows any more, sent
	/// before the host took its <c>detach</c>, is dropped, so a closed chat raises nothing here and its next attach asks from the last push applied.</summary>
	private void ApplyChatPush( JsonObject frame )
	{
		if ( IsStopped || Frame.ReadChannel( frame ) is not { } id || !_chats.TryGetValue( id, out var chat ) || chat.Panels.Count == 0 )
			return;

		if ( chat.IsAttaching )
		{
			chat.Held.Add( frame );
			return;
		}
		if ( _sessions.Find( id ) is RemoteChatSession session )
			ApplyChatPush( session, frame );
	}

	/// <summary>Applies one <c>state</c>, <c>msg</c>, <c>msgs</c>, <c>tool</c> or <c>evt</c> push to a proxy; one the proxy has seen is ignored by its sequence number.</summary>
	private void ApplyChatPush( RemoteChatSession session, JsonObject frame )
	{
		var seq = Frame.ReadSeq( frame ) ?? 0;
		try
		{
			switch ( Frame.ReadType( frame ) )
			{
				case "state":
					session.ApplyState( seq, WireJson.Require<ChatSessionState>( frame, "s" ) );
					break;
				case "msg":
					session.ApplyMessage( seq, WireJson.Require<ConversationMessage>( frame, "m" ), Frame.ReadBool( frame, "persist" ) ?? true );
					break;
				case "msgs":
					session.ApplyMessages( seq, WireJson.Require<ConversationMessage[]>( frame, "m" ) );
					break;
				case "tool":
					session.ApplyTool( seq, Frame.ReadString( frame, "id" ) ?? "", WireJson.BuildElement( frame["f"] ) );
					break;
				case "evt":
					session.ApplyEvent( seq, WireJson.Require<ChatSessionEvent>( frame, "e" ) );
					break;
			}
		}
		catch ( Exception exception ) when ( exception is RemoteException or ArgumentException or KeyNotFoundException )
		{
			_app.ReportError( "Remote", exception );
		}
	}

	/// <summary>Sets the state and status line, tells the views, and shows them on the host; nothing changes once the client stopped.</summary>
	private void UpdateStatus( ClientState state, string text )
	{
		if ( IsStopped )
			return;

		Status = state;
		StatusText = text;
		_version.Value++;
		Host.Host.Update( state switch
		{
			ClientState.Connected => LoknHostState.Connected,
			ClientState.Failed => LoknHostState.Failed,
			ClientState.Stopped => LoknHostState.Stopped,
			_ => LoknHostState.Connecting,
		}, text, Kind == HostLinkKind.Daemon || Scopes.Contains( DeviceScopes.Control ) );
	}

	/// <summary>Dials the link's host: the daemon's pipe, starting the daemon when it is absent, or the paired host's addresses, the one that answered
	/// last first, and then its relay. A daemon that ends before it opened its pipe, or does not open it in time, is the daemon's failure.</summary>
	private async Task<ClientCarrier> DialAsync( CancellationToken cancellationToken )
	{
		if ( Kind == HostLinkKind.Daemon )
		{
			if ( !OperatingSystem.IsWindows() )
				throw new PlatformNotSupportedException( "The daemon runs on Windows only." );

			var pipeName = DaemonControl.PipeName;
			if ( await PipeDialer.ConnectAsync( pipeName, TimeSpan.FromSeconds( 1 ), cancellationToken ).ConfigureAwait( false ) is { } connection )
				return new( connection, DeviceScopes.Controlling, "pipe" );

			// The user stopped the daemon while this dial ran; it stays stopped.
			if ( _isPaused )
				throw new IOException( "The daemon is stopped." );

			_post( () => UpdateStatus( ClientState.Connecting, "Starting the daemon…" ) );
			Process process;
			try
			{
				process = DaemonControl.Start( _daemonExecutable );
			}
			catch ( System.ComponentModel.Win32Exception exception )
			{
				throw FailDaemon( $"The daemon could not start: {exception.Message}" );
			}

			using ( process )
			{
				var deadline = DateTime.UtcNow + DaemonStartTimeout;
				while ( DateTime.UtcNow < deadline )
				{
					if ( await PipeDialer.ConnectAsync( pipeName, TimeSpan.FromSeconds( 1 ), cancellationToken ).ConfigureAwait( false ) is { } started )
						return new( started, DeviceScopes.Controlling, "pipe" );

					// The process ended without opening the pipe, and no daemon another Loom started meanwhile holds the mutex and opens it instead.
					if ( process.HasExited && !DaemonControl.IsRunning )
						throw FailDaemon( $"The daemon ended with exit code {process.ExitCode} before it opened its pipe." );
				}
			}
			throw FailDaemon( $"The daemon did not start within {DaemonStartTimeout.TotalSeconds:0} s." );
		}

		var host = _paired ?? throw new InvalidOperationException( "This host is not paired. Add it again in Remote access." );
		var keys = ReadKeys();
		var preferred = _lastAddress;
		IReadOnlyList<string> addrs = [.. host.Addrs.OrderBy( addr => addr == preferred ? 0 : 1 )];
		var (session, address) = await HostPairing.DialEachAsync( addrs, host.Relay, host.HostId,
			( carrier, token ) => SecureChannel.ConnectAsync( carrier, keys, host.DeviceId, host.HostKey, token ), cancellationToken ).ConfigureAwait( false );
		_lastAddress = address;
		return new( session.Connection, session.Scopes, session.Connection.Carrier is WebSocketConnection socket ? socket.Peer : "relay" );
	}

	/// <summary>Tells <see cref="DaemonFailed"/> why the daemon failed and builds the dial's error, which adds where its log is.</summary>
	private IOException FailDaemon( string reason )
	{
		ReportDaemonFailure( reason );
		return new IOException( $"{reason} Its log is {DaemonLogPath}." );
	}

	/// <summary>Raises <see cref="DaemonFailed"/> on the UI thread, unless the client stopped or dialing was paused meanwhile.</summary>
	private void ReportDaemonFailure( string reason ) => _post( () =>
	{
		if ( !IsStopped && !_isPaused )
			DaemonFailed?.Invoke( reason );
	} );

	/// <summary>Reports whether this PC's daemon ended under a connection that dropped while nobody stopped it: dialing is not paused, the client runs,
	/// and the daemon's mutex is gone within <see cref="DaemonExitTimeout"/>. A daemon that keeps running, such as while its Remote module reloads, did
	/// not fail.</summary>
	private async Task<bool> IsDaemonGoneAsync( CancellationToken cancellationToken )
	{
		if ( !UsesDaemonControl )
			return false;

		var deadline = DateTime.UtcNow + DaemonExitTimeout;
		while ( !_isPaused && !cancellationToken.IsCancellationRequested && DaemonControl.IsRunning )
		{
			if ( DateTime.UtcNow >= deadline )
				return false;

			await Task.Delay( 100, cancellationToken ).ConfigureAwait( false );
		}
		return !_isPaused && !cancellationToken.IsCancellationRequested;
	}

	/// <summary>Loads this PC's device keys, creating them on first use.</summary>
	private RemoteKeys ReadKeys() => _keys ??= RemoteKeys.LoadOrCreate( HostPairing.ComputeDeviceKeyPath( _dataFolder ) );

	/// <summary>One connection's list state: whether its snapshots are loaded, the store snapshot's revision, and the pushes held until then.</summary>
	private sealed class ClientLink
	{
		/// <summary>Gets or sets whether the <c>sync</c> snapshot is loaded.</summary>
		public bool IsSynced { get; set; }

		/// <summary>Gets or sets the revision of the store and working state the snapshot holds; pushes at or below it are already in it.</summary>
		public long Revision { get; set; } = -1;

		/// <summary>Gets the <c>store</c> and <c>working</c> pushes that arrived before the snapshot was loaded.</summary>
		public List<JsonObject> Held { get; } = [];

		/// <summary>Gets or sets whether the <c>browser.sync</c> snapshot is loaded.</summary>
		public bool IsBrowserSynced { get; set; }

		/// <summary>Gets the <c>browser.tab</c> and <c>browser.closed</c> pushes that arrived before the browser snapshot was loaded.</summary>
		public List<JsonObject> BrowserHeld { get; } = [];
	}

	/// <summary>One chat's attach state on the client.</summary>
	private sealed class ClientChat
	{
		/// <summary>Gets the panels that show the chat.</summary>
		public HashSet<Guid> Panels { get; } = [];

		/// <summary>Gets or sets the number of the latest attach or detach, so an older attach's outcome is ignored.</summary>
		public int Generation { get; set; }

		/// <summary>Gets or sets whether an attach awaits its answer.</summary>
		public bool IsAttaching { get; set; }

		/// <summary>Gets the pushes held while an attach awaits its answer.</summary>
		public List<JsonObject> Held { get; } = [];

		/// <summary>Gets or sets the host boot the proxy's sequence number belongs to, or null before the first attach.</summary>
		public string? Boot { get; set; }
	}

	/// <summary>Carries one chat's calls through the client.</summary>
	/// <param name="client">The client.</param>
	/// <param name="conversationId">The chat.</param>
	private sealed class Channel( RemoteClient client, Guid conversationId ) : IChatChannel
	{
		/// <summary>Sends a call for the chat; called on the UI thread.</summary>
		public Task<JsonElement> CallAsync( string method, JsonElement[] args, CancellationToken cancellationToken ) =>
			client.CallAsync( conversationId, method, args, cancellationToken );
	}
}
