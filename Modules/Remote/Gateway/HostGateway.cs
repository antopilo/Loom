using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Conversations;

namespace Lokn.Modules.Remote;

/// <summary>Where the host keeps its keys, devices, push key and uploads, and the pipe it listens on.</summary>
/// <param name="DataFolder">The data folder: <see cref="LoknPaths.DataRoot"/>, which is the bare folder of a bare daemon.</param>
/// <param name="PipeName">The named pipe, or null for none, such as in checks that hand connections in themselves.</param>
internal sealed record GatewayPaths( string DataFolder, string? PipeName )
{
	/// <summary>Gets the host's static key file.</summary>
	public string HostKey => Path.Combine( DataFolder, "host-key.pem" );

	/// <summary>Gets the paired devices file.</summary>
	public string Devices => Path.Combine( DataFolder, "devices.json" );

	/// <summary>Gets the VAPID key file.</summary>
	public string Vapid => Path.Combine( DataFolder, "vapid-key.pem" );

	/// <summary>Gets the folder remote uploads go to.</summary>
	public string Uploads => Path.Combine( DataFolder, "attachments", "remote" );
}

/// <summary>The network mode, port and relay the listeners were last asked to run with.</summary>
/// <param name="Mode">The network mode.</param>
/// <param name="Port">The WebSocket port.</param>
/// <param name="RelayUrl">The relay's address, or empty.</param>
internal readonly record struct NetworkRequest( string Mode, int Port, string RelayUrl );

/// <summary>A device that paired with view only and waits for the user to allow control or keep it so (docs/remote.md, Pairing confirm).</summary>
/// <param name="DeviceId">The device's id.</param>
/// <param name="Name">The name the device gave.</param>
/// <param name="Paired">When it paired.</param>
/// <param name="Code">For a device on this network that asks to pair without a link, the code both screens show; null for one that paired with a link.</param>
internal sealed record PendingDevice( string DeviceId, string Name, DateTimeOffset Paired, string? Code = null );

/// <summary>Runs the host role (<c>Loom.exe --host</c>): accepts Loom on this PC over the named pipe and paired devices over WebSocket, the relay or
/// Tailscale, completes the secure channel, and hands each connection to the host's services (docs/remote.md).</summary>
/// <remarks>
/// <see cref="Start"/> and <see cref="Stop"/> run on the host's UI thread; connections are accepted on the thread pool and registered with the
/// services on the UI thread before their peers start. The network listeners follow the <c>remote.network</c>, <c>remote.port</c> and
/// <c>remote.relay-url</c> settings, including changes another process saves, restart on <c>admin.network</c>, and start again when the PC's
/// addresses change or, after a failure, with a growing pause.
/// </remarks>
public sealed class HostGateway
{
	/// <summary>Names the device of Loom on this PC, connected over the named pipe.</summary>
	public const string LocalDevice = "local";

	/// <summary>Names Loom on this PC in device lists.</summary>
	public const string LocalName = "This PC";

	/// <summary>Defines how long stopping waits for the listeners and connections to close.</summary>
	private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds( 5 );

	/// <summary>Defines the first pause before a failed network mode starts again.</summary>
	private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds( 5 );

	/// <summary>Defines the longest pause before a failed network mode starts again.</summary>
	private static readonly TimeSpan LastRetry = TimeSpan.FromMinutes( 5 );

	/// <summary>Defines the quiet period after an address change before the addresses are read, in milliseconds.</summary>
	private const int AddressQuietMilliseconds = 2000;

	/// <summary>Stores the host application.</summary>
	private readonly LoknApplication _app;

	/// <summary>Posts to the host's UI thread.</summary>
	private readonly Func<Action, bool> _post;

	/// <summary>Stores where the host keeps its files.</summary>
	private readonly GatewayPaths _paths;

	/// <summary>Stores the host's conversation store.</summary>
	private readonly ConversationStore _store;

	/// <summary>Stores the host's chat sessions.</summary>
	private readonly ChatSessionService _sessions;

	/// <summary>Holds the open connections; lock it to read or change it.</summary>
	private readonly List<RemoteConnection> _connections = [];

	/// <summary>Holds the services every connection is registered with.</summary>
	private readonly List<IHostService> _services = [];

	/// <summary>Serializes network restarts.</summary>
	private readonly SemaphoreSlim _networkGate = new( 1, 1 );

	/// <summary>Ends the accept loops, handshakes and connections when the gateway stops.</summary>
	private readonly CancellationTokenSource _stopping = new();

	/// <summary>Starts a failed network mode again after a pause.</summary>
	private readonly Timer _retry;

	/// <summary>Coalesces the address changes of one network change.</summary>
	private readonly Timer _addressQuiet;

	/// <summary>Stores the pipe listener, or null.</summary>
	private IAsyncDisposable? _pipe;

	/// <summary>Stores what answers discovery queries for this host on the LAN, or null when its port was taken.</summary>
	private LanDiscovery? _discovery;

	/// <summary>Stores the running network listeners and what they reach; replaced with <see cref="Interlocked"/>.</summary>
	private NetworkState _network = NetworkState.Off;

	/// <summary>Stores the network the listeners were last asked for, or null before the start; on the UI thread.</summary>
	private NetworkRequest? _requested;

	/// <summary>Stores the last network restart; on the UI thread.</summary>
	private Task<NetworkState> _restart = Task.FromResult( NetworkState.Off );

	/// <summary>Stores whether <see cref="ApplyNetworkSettingsAsync"/> is saving the settings, so their change events start nothing of their own.</summary>
	private bool _isSaving;

	/// <summary>Stores the pause before the next retry; changed under the network gate.</summary>
	private TimeSpan _retryDelay = FirstRetry;

	/// <summary>Stores the port of a Tailscale proxy that could not be removed, which later restarts try again; changed under the network gate.</summary>
	private int? _staleServePort;

	/// <summary>Stores the WebSocket port the listeners were last asked for, which tunnels may not reach; written on the UI thread, read on any.</summary>
	private volatile int _requestedPort;

	/// <summary>Stores the live pairing offer, or null.</summary>
	private PairingOffer? _liveOffer;

	/// <summary>Holds the devices that paired with view only and wait for the user's answer, by id; changed on the UI thread, read under its lock.</summary>
	/// <remarks>In memory only: a restarted daemon forgets them, and the devices stay view-only, which Remote access can still change.</remarks>
	private readonly Dictionary<string, PendingDevice> _pending = new( StringComparer.Ordinal );

	/// <summary>Holds the answer each pairing request without a link waits for, by device id: the scopes granted, or null for a refusal.</summary>
	private readonly Dictionary<string, TaskCompletionSource<IReadOnlyList<string>?>> _knocks = new( StringComparer.Ordinal );

	/// <summary>Holds the devices the user allowed from a pairing request, until they are stored, so storing them asks no second question.</summary>
	private readonly HashSet<string> _allowedKnocks = new( StringComparer.Ordinal );

	/// <summary>Creates the gateway; <see cref="Start"/> opens its keys, devices and listeners.</summary>
	/// <param name="app">The host application.</param>
	/// <param name="local">This PC's chats, which the gateway serves.</param>
	/// <param name="post">Posts to the host's UI thread and reports whether it was accepted.</param>
	/// <param name="paths">Where the host keeps its files, and its pipe.</param>
	internal HostGateway( LoknApplication app, ChatHost local, Func<Action, bool> post, GatewayPaths paths )
	{
		_app = app;
		_store = local.Store;
		_sessions = local.Sessions;
		_post = post;
		_paths = paths;
		Keys = RemoteKeys.LoadOrCreate( paths.HostKey );
		Devices = new DeviceStore( paths.Devices );
		_retry = new Timer( _ => _post( RetryNetwork ), null, Timeout.Infinite, Timeout.Infinite );
		_addressQuiet = new Timer( _ => _post( CheckAddresses ), null, Timeout.Infinite, Timeout.Infinite );
	}

	/// <summary>Gets the host's static keys.</summary>
	public RemoteKeys Keys
	{
		get;
	}

	/// <summary>Gets the paired devices.</summary>
	public DeviceStore Devices
	{
		get;
	}

	/// <summary>Gets the host application.</summary>
	internal LoknApplication App => _app;

	/// <summary>Gets what posts to the host's UI thread.</summary>
	internal Func<Action, bool> Post => _post;

	/// <summary>Gets the live pairing offers.</summary>
	public PairingOffers Offers { get; } = new();

	/// <summary>Gets the pairing offer the Remote access panel shows, or null when none is live: it expired, was withdrawn or a device used it.</summary>
	internal PairingOffer? LiveOffer => Volatile.Read( ref _liveOffer ) is { } offer && offer.Expires > DateTimeOffset.UtcNow ? offer : null;

	/// <summary>Gets the chat service, once started.</summary>
	internal ChatService? Chats
	{
		get; private set;
	}

	/// <summary>Raised on the UI thread after a connection joined or left <see cref="Connections"/>, the network listeners' state changed, or a
	/// pairing started or stopped waiting for the user's answer.</summary>
	internal event Action? Changed;

	/// <summary>Gets the devices waiting for the user's answer to their pairing, oldest first; on any thread.</summary>
	internal IReadOnlyList<PendingDevice> Pending
	{
		get
		{
			lock ( _pending )
				return [.. _pending.Values.OrderBy( device => device.Paired )];
		}
	}

	/// <summary>Gets the open connections.</summary>
	public IReadOnlyList<RemoteConnection> Connections
	{
		get
		{
			lock ( _connections )
				return [.. _connections];
		}
	}

	/// <summary>Gets the network listeners' current state.</summary>
	internal NetworkState Network => Volatile.Read( ref _network );

	/// <summary>Creates the services, opens the pipe and starts the network listeners the settings ask for; on the UI thread.</summary>
	/// <param name="settings">The remote settings to start with.</param>
	internal void Start( RemoteSettings settings )
	{
		var push = new PushService( Devices, _paths.Vapid );
		push.Failed += message => _post( () => _app.ReportError( "Web push", new InvalidOperationException( message ) ) );
		var chats = new ChatService( _store, _sessions, _post, _paths.Uploads, push, error => _app.ReportError( "Web push", error ), () => Network.Addresses );
		Chats = chats;
		var tools = new ToolService( _app, _post );
		var tabs = _app.Hosts.Local.BrowserTabs;
		tabs.Windows = tools;
		_services.AddRange( [chats, tools, new BrowserService( tabs, _post ), new TunnelService( ReadGatewayPorts ), new WorkspaceService( _app ), new StreamService(), push, new AdminService( this )] );
		Devices.Revoked += CloseDevice;
		Devices.Revoked += ForgetRevoked;
		Devices.Added += FollowAdded;
		Offers.Used += ForgetOffer;
		_app.Settings.Changed += FollowSettings;
		NetworkChange.NetworkAddressChanged += MarkAddressesChanged;

		if ( _paths.PipeName is { } pipeName && OperatingSystem.IsWindows() )
		{
			var pipe = new PipeHost( pipeName, connection => RunConnectionAsync( connection, LocalDevice, LocalName, DeviceScopes.Controlling ) );
			pipe.Failed += error => _post( () => _app.ReportError( "Remote pipe", error ) );
			pipe.Start();
			_pipe = pipe;
		}
		var discovery = new LanDiscovery( Keys.HostId, Keys.PublicKeyText, Environment.MachineName, () => [.. ListLan( Network )] );
		_discovery = discovery.Start() ? discovery : null;

		RequestNetwork( new( settings.Network, settings.Port, settings.RelayUrl ), isForced: true );
	}

	/// <summary>Stops the services on the UI thread and saves the chats, then closes the listeners and every connection, waiting a few seconds for them.</summary>
	/// <remarks>The history is written first: at a session end Windows gives the process only a few seconds, and Tailscale's command line may use them.</remarks>
	internal void Stop()
	{
		_stopping.Cancel();
		Devices.Revoked -= CloseDevice;
		Devices.Revoked -= ForgetRevoked;
		Devices.Added -= FollowAdded;
		Offers.Used -= ForgetOffer;
		_app.Settings.Changed -= FollowSettings;
		_discovery?.Dispose();
		_discovery = null;
		NetworkChange.NetworkAddressChanged -= MarkAddressesChanged;
		_retry.Dispose();
		_addressQuiet.Dispose();
		foreach ( var service in _services )
		{
			if ( service is IDisposable disposable )
				disposable.Dispose();
		}
		_services.Clear();
		_store.Flush();
		// The transports finish on the thread pool, so nothing they await waits on this thread.
		try
		{
			Task.Run( CloseTransportsAsync ).Wait( StopTimeout );
		}
		catch ( AggregateException exception )
		{
			_app.ReportError( "Remote", exception.GetBaseException() );
		}
		Keys.Dispose();
	}

	/// <summary>Gets the ports a tunnel may not reach: the WebSocket ports the gateway listens on and the one it was last asked for; on any thread.</summary>
	private IReadOnlyCollection<int> ReadGatewayPorts()
	{
		var network = Network;
		return [_requestedPort, network.Port, .. network.Listener?.Endpoints.Select( endpoint => endpoint.Port ) ?? []];
	}

	/// <summary>Runs one connection: builds its peer, registers it with every service on the UI thread, starts it and waits until it closes.</summary>
	/// <param name="connection">The connection, plaintext over the pipe or encrypted after the handshake.</param>
	/// <param name="deviceId">The device, or <see cref="LocalDevice"/>.</param>
	/// <param name="name">The device's name.</param>
	/// <param name="scopes">The device's scopes.</param>
	/// <param name="deviceKey">The device key the handshake proved, or null for the pipe and checks.</param>
	/// <remarks>
	/// The connection joins <see cref="Connections"/> before its peer starts, and then the device's record is read again: a device revoked, given
	/// other scopes or paired again with another key during the handshake is closed before any of its requests runs, and a change after that
	/// finds the connection through <see cref="CloseDevice"/>.
	/// </remarks>
	internal async Task RunConnectionAsync( IFrameConnection connection, string deviceId, string name, IReadOnlyList<string> scopes, string? deviceKey = null )
	{
		var peer = new RpcPeer( connection );
		var closed = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
		peer.Closed += _ => closed.TrySetResult();
		var remote = new RemoteConnection( deviceId, name, scopes, peer );
		try
		{
			try
			{
				await UiInvoke.RunAsync( _post, () =>
				{
					foreach ( var service in _services )
						service.Register( remote );
					lock ( _connections )
						_connections.Add( remote );
					Changed?.Invoke();
					if ( _stopping.IsCancellationRequested || !IsCurrent( deviceId, scopes, deviceKey ) )
						_ = peer.DisposeAsync();
					else
						peer.Start();
				} ).ConfigureAwait( false );
			}
			catch
			{
				await peer.DisposeAsync().ConfigureAwait( false );
				throw;
			}

			using ( _stopping.Token.Register( () => _ = peer.DisposeAsync() ) )
				await closed.Task.ConfigureAwait( false );
		}
		finally
		{
			lock ( _connections )
				_connections.Remove( remote );
			_post( () => Changed?.Invoke() );
		}
	}

	/// <summary>Checks that a device still holds the scopes and key its connection was made with; Loom on this PC always does.</summary>
	private bool IsCurrent( string deviceId, IReadOnlyList<string> scopes, string? deviceKey ) =>
		deviceId == LocalDevice
		|| Devices.Find( deviceId ) is { } device && device.Scopes.ToHashSet( StringComparer.Ordinal ).SetEquals( scopes ) && (deviceKey is null || device.DeviceKey == deviceKey);

	/// <summary>Closes every connection of a device, such as after it was revoked or its scopes changed, so it reconnects with what it now holds.</summary>
	internal void CloseDevice( string deviceId )
	{
		foreach ( var connection in Connections.Where( connection => connection.DeviceId == deviceId ) )
			_ = connection.Peer.DisposeAsync();
	}

	/// <summary>Creates the pairing offer the Remote access panel shows, which withdraws any earlier one.</summary>
	/// <param name="addresses">The addresses devices reach this PC at.</param>
	/// <param name="relay">The relay's address, or null.</param>
	/// <param name="control">Whether the device that pairs through it gets control at once.</param>
	internal PairingOffer CreateOffer( IReadOnlyList<string> addresses, string? relay, bool control = false )
	{
		var offer = Offers.Create( Keys, addresses, relay, control );
		Volatile.Write( ref _liveOffer, offer );
		return offer;
	}

	/// <summary>Withdraws the pairing offers, such as when the Remote access panel closes its code.</summary>
	internal void CancelOffers()
	{
		Volatile.Write( ref _liveOffer, null );
		Offers.CancelAll();
	}

	/// <summary>Withdraws one pairing offer, unless a device already used it, such as when the connection of the Loom that showed it closed; on any thread.</summary>
	internal void WithdrawOffer( PairingOffer offer )
	{
		Interlocked.CompareExchange( ref _liveOffer, null, offer );
		Offers.Cancel( offer );
	}

	/// <summary>Forgets the live offer once a device used it; on the handshake's thread.</summary>
	private void ForgetOffer( PairingOffer offer ) => Interlocked.CompareExchange( ref _liveOffer, null, offer );

	/// <summary>Answers a pending pairing for the user. A device that paired with a link: <paramref name="control"/> grants it control and closes its
	/// connections, so it reconnects with it. A device that asked without a link: it is paired with control, view only, or not at all when
	/// <paramref name="deny"/>. Either way the device stops waiting and Loom on this PC hears <c>admin.pairing.done</c>. On the UI thread.</summary>
	/// <exception cref="RemoteException">The device does not wait for an answer.</exception>
	internal void Confirm( string deviceId, bool control, bool deny = false )
	{
		TaskCompletionSource<IReadOnlyList<string>?>? knock;
		lock ( _pending )
			_knocks.TryGetValue( deviceId, out knock );
		if ( knock is not null )
		{
			if ( !deny )
			{
				lock ( _pending )
					_allowedKnocks.Add( deviceId );
			}
			knock.TrySetResult( deny ? null : control ? DeviceScopes.Controlling : DeviceScopes.Paired );
			ClearPending( deviceId );
			return;
		}

		bool isPending;
		lock ( _pending )
			isPending = _pending.ContainsKey( deviceId );
		if ( !isPending || Devices.Find( deviceId ) is null )
			throw new RemoteException( RemoteErrors.BadRequest, "That device does not wait for an answer." );

		if ( deny )
			Devices.Revoke( deviceId );
		else if ( control )
		{
			Devices.SetScopes( deviceId, DeviceScopes.Controlling );
			CloseDevice( deviceId );
		}
		ClearPending( deviceId );
	}

	/// <summary>Asks the user about a device on this network that wants to pair without a link: it waits in <see cref="Pending"/> with its code, and Loom
	/// on this PC hears <c>admin.pairing</c>, until the user answers through <see cref="Confirm"/> or the request ends. On the handshake's thread.</summary>
	/// <returns>The scopes the user granted, or null for a refusal.</returns>
	private async Task<IReadOnlyList<string>?> AskKnockAsync( KnockRequest request, CancellationToken cancellationToken )
	{
		var answer = new TaskCompletionSource<IReadOnlyList<string>?>( TaskCreationOptions.RunContinuationsAsynchronously );
		var waiting = new PendingDevice( request.DeviceId, request.Name, DateTimeOffset.UtcNow, request.Code );
		lock ( _pending )
		{
			if ( !_knocks.TryAdd( request.DeviceId, answer ) )
				return null;
			_pending[request.DeviceId] = waiting;
		}
		_post( () =>
		{
			PushLocal( BuildPairingPush( waiting ) );
			Changed?.Invoke();
		} );
		try
		{
			return await answer.Task.WaitAsync( cancellationToken ).ConfigureAwait( false );
		}
		finally
		{
			lock ( _pending )
				_knocks.Remove( request.DeviceId );
			_post( () => ClearPending( request.DeviceId ) );
		}
	}

	/// <summary>Stops a device waiting for an answer, such as after the user answered, changed its scopes or revoked it, and tells Loom on this PC
	/// with <c>admin.pairing.done</c>; nothing happens for a device that does not wait. On the UI thread.</summary>
	internal void ClearPending( string deviceId )
	{
		lock ( _pending )
		{
			if ( !_pending.Remove( deviceId ) )
				return;
		}

		var done = Frame.Create( "admin.pairing.done" );
		done["deviceId"] = deviceId;
		PushLocal( done );
		Changed?.Invoke();
	}

	/// <summary>Follows a newly paired device on the UI thread: one that paired with view only waits for the user's answer and Loom on this PC hears
	/// <c>admin.pairing</c>; one that paired through a control link is announced with <c>admin.paired</c>. On the handshake's thread.</summary>
	private void FollowAdded( PairedDevice device ) => _post( () =>
	{
		if ( _stopping.IsCancellationRequested )
			return;

		// A device the user just allowed from its pairing request needs no second question.
		bool isAllowed;
		lock ( _pending )
			isAllowed = _allowedKnocks.Remove( device.DeviceId );
		if ( isAllowed )
			return;

		if ( device.Scopes.Contains( DeviceScopes.Control ) )
		{
			var paired = Frame.Create( "admin.paired" );
			paired["deviceId"] = device.DeviceId;
			paired["name"] = device.Name;
			paired["control"] = true;
			PushLocal( paired );
			return;
		}

		lock ( _pending )
			_pending[device.DeviceId] = new( device.DeviceId, device.Name, device.Paired );
		PushLocal( BuildPairingPush( new( device.DeviceId, device.Name, device.Paired ) ) );
		Changed?.Invoke();
	} );

	/// <summary>Stops a revoked device waiting for an answer; on the thread that revoked it.</summary>
	private void ForgetRevoked( string deviceId ) => _post( () => ClearPending( deviceId ) );

	/// <summary>Builds an <c>admin.pairing</c> push, or one entry of <c>admin.status</c>'s <c>pending</c>, with the code of a request without a link.</summary>
	internal static JsonObject BuildPairingPush( PendingDevice device )
	{
		var frame = Frame.Create( "admin.pairing" );
		frame["deviceId"] = device.DeviceId;
		frame["name"] = device.Name;
		frame["paired"] = device.Paired.ToUnixTimeSeconds();
		if ( device.Code is { } code )
			frame["code"] = code;
		return frame;
	}

	/// <summary>Sends a push to every connection of Loom on this PC; a connection that closes meanwhile misses it.</summary>
	private void PushLocal( JsonObject frame )
	{
		foreach ( var connection in Connections.Where( connection => connection.DeviceId == LocalDevice ) )
			_ = PushAsync( connection, (JsonObject)frame.DeepClone() );
	}

	/// <summary>Sends one push, ignoring a connection that is closing.</summary>
	private static async Task PushAsync( RemoteConnection connection, JsonObject frame )
	{
		try
		{
			await connection.Peer.PushAsync( frame ).ConfigureAwait( false );
		}
		catch ( Exception exception ) when ( exception is IOException or ObjectDisposedException or InvalidOperationException or InvalidDataException )
		{
			// The connection is closing; Loom on this PC asks for admin.status when it connects again.
		}
	}

	/// <summary>Saves the network settings and restarts the listeners with them, even when they did not change, as the Apply button does; on the UI thread.</summary>
	/// <exception cref="RemoteException">A value is not valid.</exception>
	internal Task<NetworkState> ApplyNetworkSettingsAsync( string mode, int port, string relayUrl )
	{
		var settings = _app.Settings;
		_isSaving = true;
		try
		{
			if ( !settings.Set( RemoteModule.Network, SettingsScope.Global, mode )
				|| !settings.Set( RemoteModule.Port, SettingsScope.Global, port )
				|| !settings.Set( RemoteModule.RelayUrl, SettingsScope.Global, relayUrl ) )
				throw new RemoteException( RemoteErrors.BadRequest, settings.LastError ?? "The network settings are not valid." );
		}
		finally
		{
			_isSaving = false;
		}
		return RequestNetwork( new( mode, port, relayUrl ), isForced: true );
	}

	/// <summary>Restarts the listeners when the network settings changed, such as when Loom on this PC saved them in Settings; on the UI thread.</summary>
	private void FollowSettings()
	{
		if ( _isSaving || _stopping.IsCancellationRequested )
			return;

		var settings = RemoteSettings.Read( _app.Settings );
		RequestNetwork( new( settings.Network, settings.Port, settings.RelayUrl ), isForced: false );
	}

	/// <summary>Starts the listeners a request asks for, unless it is the one last asked for and <paramref name="isForced"/> is false; on the UI thread.</summary>
	/// <returns>The restart, or the last one when nothing changed.</returns>
	private Task<NetworkState> RequestNetwork( NetworkRequest request, bool isForced )
	{
		if ( !isForced && _requested == request )
			return _restart;

		_requested = request;
		_requestedPort = request.Port;
		_restart = UpdateNetworkAsync( request );
		return _restart;
	}

	/// <summary>Starts the requested mode again after a failure; on the UI thread.</summary>
	private void RetryNetwork()
	{
		if ( _requested is { } request && !_stopping.IsCancellationRequested && Network.Error is not null )
			RequestNetwork( request, isForced: true );
	}

	/// <summary>Restarts the quiet period after the PC's addresses changed; on a system thread.</summary>
	private void MarkAddressesChanged( object? sender, EventArgs change )
	{
		try
		{
			_addressQuiet.Change( AddressQuietMilliseconds, Timeout.Infinite );
		}
		catch ( ObjectDisposedException )
		{
			// The gateway stopped meanwhile.
		}
	}

	/// <summary>Starts the mode again when the network change fixed what failed, or moved the LAN addresses it listens on; on the UI thread.</summary>
	private void CheckAddresses()
	{
		if ( _requested is not { } request || _stopping.IsCancellationRequested || request.Mode == "off" )
			return;

		var network = Network;
		// Every mode listens on the LAN too, so a changed LAN address restarts any of them.
		if ( network.Error is not null || !network.LanAddresses.SequenceEqual( ReadLanAddresses() ) )
			RequestNetwork( request, isForced: true );
	}

	/// <summary>Stops the network listeners and starts the ones a request asks for: a loopback listener for every mode but <c>off</c>, the PC's LAN or
	/// tailnet addresses, Tailscale Serve or Funnel, or the relay link. A change of mode, port or relay, and <c>off</c>, also closes every device's
	/// connection, so a device reaches the host only the way the settings now allow; Loom on this PC keeps its pipe.</summary>
	/// <param name="request">The network mode, port and relay.</param>
	/// <returns>The new state; its <see cref="NetworkState.Error"/> tells what failed, and a failure is retried after a pause.</returns>
	private async Task<NetworkState> UpdateNetworkAsync( NetworkRequest request )
	{
		var cancellationToken = _stopping.Token;
		await _networkGate.WaitAsync( cancellationToken ).ConfigureAwait( false );
		try
		{
			var previous = Network;
			var cleanup = await CloseNetworkAsync( previous, request.Mode ).ConfigureAwait( false );
			Volatile.Write( ref _network, NetworkState.Off );
			if ( previous.Mode != request.Mode || previous.Port != request.Port || previous.RelayUrl != request.RelayUrl || request.Mode == "off" )
				CloseDevices();

			var state = await StartNetworkAsync( request, cancellationToken ).ConfigureAwait( false );
			if ( cleanup is not null )
				state = state with { Error = state.Error is null ? cleanup : state.Error + " " + cleanup };
			Volatile.Write( ref _network, state );
			_post( () => Changed?.Invoke() );
			// Devices still connected learn the new addresses at once, so they can come back after the next change too.
			if ( !state.Addresses.SequenceEqual( previous.Addresses ) )
			{
				foreach ( var connection in Connections.Where( connection => connection.DeviceId != LocalDevice ) )
					_ = PushAsync( connection, new JsonObject { ["t"] = "host.addrs", ["addrs"] = Frame.BuildStrings( state.Addresses ) } );
			}
			if ( state.Relay is { } relay )
				UpdateRelayStatus( relay, relay.Status );

			if ( state.Error is { } error )
			{
				if ( error != previous.Error )
					_post( () => _app.ReportError( "Remote network", new InvalidOperationException( error ) ) );
				ScheduleRetry( _retryDelay );
				_retryDelay = TimeSpan.FromTicks( Math.Min( _retryDelay.Ticks * 2, LastRetry.Ticks ) );
			}
			else
			{
				ScheduleRetry( null );
				_retryDelay = FirstRetry;
			}
			return state;
		}
		finally
		{
			_networkGate.Release();
		}
	}

	/// <summary>Arms the retry after a pause, or disarms it with null; a gateway stopped meanwhile has no timer left to arm.</summary>
	private void ScheduleRetry( TimeSpan? delay )
	{
		try
		{
			_retry.Change( delay ?? Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan );
		}
		catch ( ObjectDisposedException )
		{
		}
	}

	/// <summary>Closes the connections of every device but Loom on this PC.</summary>
	private void CloseDevices()
	{
		foreach ( var connection in Connections.Where( connection => connection.DeviceId != LocalDevice ) )
			_ = connection.Peer.DisposeAsync();
	}

	/// <summary>Starts one mode's listeners, reporting a failure in the state rather than throwing, so the pipe keeps working; a stop meanwhile closes
	/// what already started.</summary>
	private async Task<NetworkState> StartNetworkAsync( NetworkRequest request, CancellationToken cancellationToken )
	{
		var (mode, port, relayUrl) = request;
		var state = new NetworkState( mode, port, relayUrl );
		try
		{
			switch ( mode )
			{
				case "lan":
				{
					var addresses = ReadLanAddresses();
					state = StartListener( state with { LanAddresses = addresses }, addresses );
					// Addresses the host could not listen on are left out of pairing links.
					if ( state.Error is null && addresses.Count > 0 )
						state = state with { Addresses = [.. addresses.Select( address => BuildAddress( address, port ) )], WebUrl = $"http://{BuildAddress( addresses[0], port )}/" };
					else if ( addresses.Count == 0 )
						state = state with { Error = "This PC has no LAN address yet. Loom listens on 127.0.0.1 and starts again when the network changes." };
					break;
				}
				case "tailscale":
				{
					var addresses = await TailscaleCli.GetAddressesAsync( cancellationToken ).ConfigureAwait( false );
					var dns = await TailscaleCli.GetDnsNameAsync( cancellationToken ).ConfigureAwait( false );
					var lan = ReadLanAddresses();
					state = StartListener( state with { LanAddresses = lan }, [.. lan, .. addresses] );
					await TailscaleCli.ServeAsync( port, cancellationToken ).ConfigureAwait( false );
					state = state with { IsServed = true };
					// A Loom PC on the same network tries the LAN first; Serve's HTTPS address works even where the host could not listen on the
					// tailnet addresses, so it comes before them.
					var reached = new List<string>();
					if ( state.Error is null )
						reached.AddRange( lan.Select( address => BuildAddress( address, port ) ) );
					if ( dns is not null )
						reached.Add( $"wss://{dns}{WebSocketHost.Path}" );
					if ( state.Error is null )
						reached.AddRange( addresses.Select( address => BuildAddress( address, port ) ) );
					state = state with { Addresses = reached, WebUrl = dns is null ? null : $"https://{dns}/" };
					break;
				}
				case "funnel":
				{
					var lan = ReadLanAddresses();
					state = StartLocalListener( state with { LanAddresses = lan }, lan );
					var dns = await TailscaleCli.GetDnsNameAsync( cancellationToken ).ConfigureAwait( false )
						?? throw new TailscaleException( "Tailscale gives this PC no DNS name; turn on MagicDNS in the tailnet." );
					await TailscaleCli.FunnelAsync( port, true, cancellationToken ).ConfigureAwait( false );
					// A Loom PC on the same network tries the LAN first, and Funnel's public address when it is away.
					state = state with { IsServed = true, IsFunnel = true, Addresses = [.. ListLan( state ), $"wss://{dns}{WebSocketHost.Path}"], WebUrl = $"https://{dns}/" };
					break;
				}
				case "relay":
				{
					if ( relayUrl.Length == 0 )
						throw new InvalidOperationException( "The relay mode needs the relay's address." );
					var lan = ReadLanAddresses();
					state = StartLocalListener( state with { LanAddresses = lan }, lan );
					var relay = new RelayHostLink( relayUrl, Keys, AcceptWebSocketAsync );
					relay.StatusChanged += status => UpdateRelayStatus( relay, status );
					relay.Failed += error => _post( () => _app.ReportError( "Remote relay", error ) );
					relay.Start();
					// A Loom PC on the same network tries the LAN first; the relay is the fallback dialers try last.
					state = state with { Relay = relay, RelayStatus = relay.Status, WebUrl = RelayDialer.BuildPageUrl( relayUrl, Keys.HostId ), Addresses = [.. ListLan( state )] };
					break;
				}
			}
		}
		catch ( OperationCanceledException )
		{
			await CloseNetworkAsync( state, "off" ).ConfigureAwait( false );
			throw;
		}
		catch ( Exception exception )
		{
			state = state with { Error = exception.Message };
			// Tailscale that is not running yet still leaves the loopback listener for this PC's browser and a later Serve.
			if ( state.Listener is null && mode != "off" )
			{
				try
				{
					state = state with { Listener = StartWebSocketHost( [new IPEndPoint( IPAddress.Loopback, port )] ) };
				}
				catch ( IOException listenError )
				{
					state = state with { Error = state.Error + " " + listenError.Message };
				}
			}
		}
		return state;
	}

	/// <summary>Writes a relay link's status into the state, unless the state no longer holds that link; on the link's thread.</summary>
	private void UpdateRelayStatus( RelayHostLink relay, string status )
	{
		while ( true )
		{
			var current = Volatile.Read( ref _network );
			if ( current.Relay != relay || current.RelayStatus == status )
				return;
			if ( Interlocked.CompareExchange( ref _network, current with { RelayStatus = status }, current ) == current )
				return;
		}
	}

	/// <summary>Starts one WebSocket listener on the loopback address and each of the given ones, with the web client mapped; when one of the
	/// others cannot be bound, it keeps the loopback one and records why.</summary>
	private NetworkState StartListener( NetworkState state, IReadOnlyList<string> addresses )
	{
		var endpoints = new[] { IPAddress.Loopback }
			.Concat( addresses.Select( address => IPAddress.TryParse( address, out var ip ) ? ip : null ).OfType<IPAddress>() )
			.Distinct()
			.Select( ip => new IPEndPoint( ip, state.Port ) )
			.ToArray();
		try
		{
			return state with { Listener = StartWebSocketHost( endpoints ) };
		}
		catch ( IOException exception ) when ( endpoints.Length > 1 )
		{
			var listener = StartWebSocketHost( [new IPEndPoint( IPAddress.Loopback, state.Port )] );
			return state with { Listener = listener, Error = exception.Message + " Loom listens on 127.0.0.1 only." };
		}
	}

	/// <summary>Starts the listener on loopback and the LAN for a mode that reaches devices another way, such as Funnel or the relay: when the LAN
	/// addresses cannot be listened on, it listens on loopback only and records no error, since the mode itself still works.</summary>
	private NetworkState StartLocalListener( NetworkState state, IReadOnlyList<string> lan )
	{
		var started = StartListener( state, lan );
		return started.Error is null ? started : started with { Error = null, LanAddresses = [] };
	}

	/// <summary>Lists the LAN addresses a state listens on, as host:port, for Loom PCs on the same network to try first.</summary>
	private static IEnumerable<string> ListLan( NetworkState state ) => state.LanAddresses.Select( address => BuildAddress( address, state.Port ) );

	/// <summary>Creates and starts a WebSocket listener with the web client mapped.</summary>
	/// <exception cref="IOException">An endpoint cannot be bound.</exception>
	private WebSocketHost StartWebSocketHost( IReadOnlyList<IPEndPoint> endpoints )
	{
		var host = new WebSocketHost( endpoints, AcceptWebSocketAsync );
		WebClientAssets.Map( host );
		host.Failed += error => _post( () => _app.ReportError( "Remote network", error ) );
		try
		{
			host.Start();
		}
		catch
		{
			_ = host.DisposeAsync();
			throw;
		}
		return host;
	}

	/// <summary>Runs the secure channel's host side on a new WebSocket and, for a paired device that completed it, the connection.</summary>
	private async Task AcceptWebSocketAsync( WebSocketConnection carrier )
	{
		if ( await SecureChannel.AcceptAsync( carrier, Keys, Devices, Offers, _stopping.Token, AskKnockAsync ).ConfigureAwait( false ) is not { } session )
			return;

		var device = session.Device;
		await RunConnectionAsync( session.Connection, device.DeviceId, device.Name, device.Scopes, device.DeviceKey ).ConfigureAwait( false );
	}

	/// <summary>Stops a state's listeners and relay link, and removes Tailscale's HTTPS proxy when the next mode does not use it.</summary>
	/// <param name="state">The state to close.</param>
	/// <param name="nextMode">The mode that follows, <c>off</c> when the gateway stops.</param>
	/// <returns>Why the proxy is still there, or null.</returns>
	/// <remarks>The proxy is persistent in Tailscale, so it would outlive the mode, the daemon and Loom itself; one that could not be removed is
	/// tried again at each later restart.</remarks>
	private async Task<string?> CloseNetworkAsync( NetworkState state, string nextMode )
	{
		if ( state.Listener is { } listener )
			await listener.DisposeAsync().ConfigureAwait( false );
		if ( state.Relay is { } relay )
			await relay.DisposeAsync().ConfigureAwait( false );

		if ( nextMode is "tailscale" or "funnel" )
			return null;

		var port = state.IsServed ? state.Port : _staleServePort;
		if ( port is not { } served )
			return null;

		try
		{
			using var timeout = new CancellationTokenSource( StopTimeout );
			await TailscaleCli.ServeOffAsync( served, timeout.Token ).ConfigureAwait( false );
			_staleServePort = null;
			return null;
		}
		catch ( Exception exception ) when ( exception is TailscaleException or OperationCanceledException or IOException or System.ComponentModel.Win32Exception )
		{
			_staleServePort = served;
			return $"Tailscale still serves this PC's port {served} over HTTPS ({exception.Message}); Loom removes it at the next restart, or run 'tailscale serve --https=443 off'.";
		}
	}

	/// <summary>Closes the pipe, every connection and the network listeners, then removes Tailscale's proxy.</summary>
	private async Task CloseTransportsAsync()
	{
		if ( _pipe is { } pipe )
			await pipe.DisposeAsync().ConfigureAwait( false );
		await Task.WhenAll( Connections.Select( connection => connection.Peer.DisposeAsync().AsTask() ) ).ConfigureAwait( false );
		await _networkGate.WaitAsync().ConfigureAwait( false );
		try
		{
			await CloseNetworkAsync( Network, "off" ).ConfigureAwait( false );
			Volatile.Write( ref _network, NetworkState.Off );
		}
		finally
		{
			_networkGate.Release();
		}
	}

	/// <summary>Reads the up, non-loopback IPv4 addresses of the PC's physical Ethernet and Wi-Fi interfaces.</summary>
	internal static IReadOnlyList<string> ReadLanAddresses()
	{
		string[] virtualMarks = ["Virtual", "Hyper-V", "VMware", "VirtualBox", "Tailscale", "WSL"];
		return [.. NetworkInterface.GetAllNetworkInterfaces()
			.Where( network => network.OperationalStatus == OperationalStatus.Up
				&& network.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet
					or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx
				&& !virtualMarks.Any( mark => network.Description.Contains( mark, StringComparison.OrdinalIgnoreCase ) || network.Name.Contains( mark, StringComparison.OrdinalIgnoreCase ) ) )
			.SelectMany( network => network.GetIPProperties().UnicastAddresses )
			.Select( unicast => unicast.Address )
			.Where( address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback( address ) )
			.Select( address => address.ToString() )
			.Distinct()
			.Order( StringComparer.Ordinal )];
	}

	/// <summary>Builds a host:port address, bracketing an IPv6 address.</summary>
	private static string BuildAddress( string address, int port ) =>
		IPAddress.TryParse( address, out var ip ) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]:{port}" : $"{address}:{port}";
}

/// <summary>The host's network listeners and what they reach, replaced as a whole when the mode changes.</summary>
/// <param name="Mode">The network mode.</param>
/// <param name="Port">The WebSocket port.</param>
/// <param name="RelayUrl">The relay's address, or empty.</param>
internal sealed record NetworkState( string Mode, int Port, string RelayUrl )
{
	/// <summary>Gets the state with no network listener.</summary>
	public static NetworkState Off { get; } = new( "off", 0, "" );

	/// <summary>Gets the addresses devices dial, as host:port or a full wss address, for pairing links.</summary>
	public IReadOnlyList<string> Addresses { get; init; } = [];

	/// <summary>Gets the LAN addresses the <c>lan</c> mode read when it started, sorted, so an address change can be told apart.</summary>
	public IReadOnlyList<string> LanAddresses { get; init; } = [];

	/// <summary>Gets the web client's address, or null when none reaches it.</summary>
	public string? WebUrl { get; init; }

	/// <summary>Gets the WebSocket listener, or null.</summary>
	public WebSocketHost? Listener { get; init; }

	/// <summary>Gets the relay link, or null.</summary>
	public RelayHostLink? Relay { get; init; }

	/// <summary>Gets the relay link's status text, or null.</summary>
	public string? RelayStatus { get; init; }

	/// <summary>Gets whether Tailscale Serve or Funnel was turned on for the mode, so leaving it turns the proxy off.</summary>
	public bool IsServed { get; init; }

	/// <summary>Gets whether Funnel was turned on for the mode.</summary>
	public bool IsFunnel { get; init; }

	/// <summary>Gets what failed when the mode started, or null.</summary>
	public string? Error { get; init; }
}
