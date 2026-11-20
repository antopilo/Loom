using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Lokn.Relay;

/// <summary>Keeps each host's control socket and pairs every device socket with a data socket its host opens on request.</summary>
/// <remarks>
/// Logs name hosts and outcomes only; the relay never reads, parses or logs device or host payloads. Every host socket must answer a fresh challenge
/// (<see cref="HostProof"/>). Limits keep one client or host id from exhausting the relay: <see cref="MaxSocketsPerAddress"/> sockets per client address,
/// <see cref="MaxWaitingPerHost"/> devices waiting for one host, <see cref="MaxWaiting"/> waiting in total, and <see cref="MaxPipesPerHost"/> devices
/// connected to one host. <c>Relay:AllowedHosts</c> in the configuration, when set, lists the only host ids the relay serves.
/// </remarks>
internal sealed class RelayHub( ILogger<RelayHub> logger, IConfiguration configuration )
{
	/// <summary>Defines the most sockets of any kind one client address may hold open.</summary>
	internal const int MaxSocketsPerAddress = 64;

	/// <summary>Defines the most device sockets waiting for one host's data socket.</summary>
	internal const int MaxWaitingPerHost = 16;

	/// <summary>Defines the most device sockets waiting for a data socket, over all hosts.</summary>
	internal const int MaxWaiting = 1024;

	/// <summary>Defines the most devices connected to one host at once.</summary>
	internal const int MaxPipesPerHost = 64;

	/// <summary>Defines the largest <c>relay.host</c> frame the relay reads, in bytes.</summary>
	private const int MaxProofLength = 4096;

	/// <summary>Defines the size of the buffer that drains a host's control socket.</summary>
	private const int DrainBufferSize = 256;

	/// <summary>Defines how long a new host socket has to send its proof.</summary>
	private static readonly TimeSpan ProofWait = TimeSpan.FromSeconds( 10 );

	/// <summary>Guards <see cref="_hosts"/> and the counters.</summary>
	private readonly Lock _gate = new();

	/// <summary>Holds each connected host's control socket by host id.</summary>
	private readonly Dictionary<string, HostLink> _hosts = new( StringComparer.Ordinal );

	/// <summary>Holds each device socket waiting for its host's data socket, by <c>hostId/sid</c>.</summary>
	private readonly ConcurrentDictionary<string, DeviceLink> _devices = new( StringComparer.Ordinal );

	/// <summary>Counts the open sockets by client address.</summary>
	private readonly Dictionary<string, int> _sockets = new( StringComparer.Ordinal );

	/// <summary>Counts the waiting device sockets by host id.</summary>
	private readonly Dictionary<string, int> _waiting = new( StringComparer.Ordinal );

	/// <summary>Counts the connected devices by host id.</summary>
	private readonly Dictionary<string, int> _pipes = new( StringComparer.Ordinal );

	/// <summary>Holds the host ids the relay serves, or null for any.</summary>
	private readonly HashSet<string>? _allowed = configuration.GetSection( "Relay:AllowedHosts" ).Get<string[]>() is { Length: > 0 } allowed
		? new HashSet<string>( allowed, StringComparer.Ordinal )
		: null;

	/// <summary>Stores the waiting device sockets over all hosts.</summary>
	private int _waitingTotal;

	/// <summary>Gets or sets how long a device socket waits for its host's data socket.</summary>
	internal TimeSpan DeviceWait { get; set; } = TimeSpan.FromSeconds( 10 );

	/// <summary>Determines whether a host with <paramref name="hostId"/> holds a proven control socket.</summary>
	internal bool HasHost( string hostId )
	{
		lock ( _gate )
			return _hosts.ContainsKey( hostId );
	}

	/// <summary>Runs a host's control socket: checks its proof, then keeps it to announce devices until it closes.</summary>
	internal async Task RunHostAsync( HttpContext context, string hostId )
	{
		await AdmitAsync( context, hostId, async () =>
		{
			using var socket = await AcceptAsync( context );
			if ( socket is null || !await ReadProofAsync( socket, hostId, HostProof.ControlPurpose, context.RequestAborted ) )
				return;
			await KeepHostAsync( context, hostId, socket );
		} );
	}

	/// <summary>Runs a host's data socket for session <paramref name="sid"/>: checks its proof, then hands it to the waiting device.</summary>
	internal async Task RunHostDataAsync( HttpContext context, string hostId, string sid )
	{
		await AdmitAsync( context, hostId, async () =>
		{
			using var socket = await AcceptAsync( context );
			if ( socket is null || !await ReadProofAsync( socket, hostId, HostProof.BuildDataPurpose( sid ), context.RequestAborted ) )
				return;
			if ( !_devices.TryRemove( BuildDeviceKey( hostId, sid ), out var link ) )
			{
				logger.LogWarning( "Host {HostId} opened an unknown or expired session.", hostId );
				await RelayPipe.CloseAsync( socket, WebSocketCloseStatus.PolicyViolation, "Unknown session." );
				return;
			}
			link.Host.SetResult( socket );
			// The device's request copies the bytes; this request only keeps the host's socket open until the pipe ends.
			await link.Done.Task;
		} );
	}

	/// <summary>Runs a device's socket: asks the host for a data socket, waits for it, then copies bytes both ways.</summary>
	internal async Task RunDeviceAsync( HttpContext context, string hostId )
	{
		await AdmitAsync( context, hostId, async () =>
		{
			if ( !TryCount( _waiting, hostId, MaxWaitingPerHost, true ) )
			{
				logger.LogWarning( "Refused a device for host {HostId}: too many devices are waiting.", hostId );
				context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
				return;
			}
			var waiting = true;
			try
			{
				using var device = await AcceptAsync( context );
				if ( device is null )
					return;
				await PairDeviceAsync( context, hostId, device, () =>
				{
					Uncount( _waiting, hostId, true );
					waiting = false;
				} );
			}
			finally
			{
				if ( waiting )
					Uncount( _waiting, hostId, true );
			}
		} );
	}

	/// <summary>Keeps a proven control socket registered until it closes.</summary>
	private async Task KeepHostAsync( HttpContext context, string hostId, WebSocket socket )
	{
		var link = new HostLink( socket );
		HostLink? replaced;
		lock ( _gate )
		{
			_hosts.TryGetValue( hostId, out replaced );
			_hosts[hostId] = link;
		}
		// A host that reconnects replaces its stale control socket; only the key's owner can, since each proof answers a fresh challenge.
		replaced?.Socket.Abort();
		logger.LogInformation( "Host {HostId} connected.", hostId );
		try
		{
			await DrainAsync( socket, context.RequestAborted );
		}
		catch ( Exception exception ) when ( exception is WebSocketException or OperationCanceledException )
		{
			// The host dropped or was replaced.
		}
		finally
		{
			lock ( _gate )
			{
				if ( _hosts.TryGetValue( hostId, out var current ) && current == link )
					_hosts.Remove( hostId );
			}
		}
		await RelayPipe.CloseAsync( socket, WebSocketCloseStatus.NormalClosure, null );
		logger.LogInformation( "Host {HostId} disconnected.", hostId );
	}

	/// <summary>Announces a device to its host, waits for the host's data socket and copies bytes until either side closes.</summary>
	/// <param name="context">The device's request.</param>
	/// <param name="hostId">The host the device asked for.</param>
	/// <param name="device">The device's socket.</param>
	/// <param name="paired">Called once the wait ends, whether or not the host answered.</param>
	private async Task PairDeviceAsync( HttpContext context, string hostId, WebSocket device, Action paired )
	{
		var sid = Base64Url.EncodeToString( RandomNumberGenerator.GetBytes( 16 ) );
		var key = BuildDeviceKey( hostId, sid );
		var link = new DeviceLink();
		_devices[key] = link;
		try
		{
			await OpenAsync( hostId, sid );
			WebSocket? host;
			try
			{
				host = await link.Host.Task.WaitAsync( DeviceWait, context.RequestAborted );
			}
			catch ( Exception exception ) when ( exception is TimeoutException or OperationCanceledException )
			{
				// The host may have claimed the session just now; then its socket is already on the way.
				host = _devices.TryRemove( new KeyValuePair<string, DeviceLink>( key, link ) ) ? null : await link.Host.Task;
			}
			paired();
			if ( host is null )
			{
				logger.LogInformation( "A device for host {HostId} timed out waiting for the host.", hostId );
				await RelayPipe.CloseAsync( device, WebSocketCloseStatus.EndpointUnavailable, "The host did not answer." );
				return;
			}
			if ( !TryCount( _pipes, hostId, MaxPipesPerHost, false ) )
			{
				logger.LogWarning( "Refused a device for host {HostId}: too many devices are connected.", hostId );
				await RelayPipe.CloseAsync( device, WebSocketCloseStatus.EndpointUnavailable, "The host has too many devices connected." );
				host.Abort();
				return;
			}
			try
			{
				logger.LogInformation( "A device and host {HostId} are paired.", hostId );
				await RelayPipe.RunAsync( device, host );
				logger.LogInformation( "A device and host {HostId} are unpaired.", hostId );
			}
			finally
			{
				Uncount( _pipes, hostId, false );
			}
		}
		finally
		{
			_devices.TryRemove( new KeyValuePair<string, DeviceLink>( key, link ) );
			link.Done.TrySetResult();
		}
	}

	/// <summary>Runs <paramref name="serve"/> when the host id is served and the client address has sockets left; otherwise answers 403 or 429.</summary>
	private async Task AdmitAsync( HttpContext context, string hostId, Func<Task> serve )
	{
		if ( _allowed is not null && !_allowed.Contains( hostId ) )
		{
			context.Response.StatusCode = StatusCodes.Status403Forbidden;
			return;
		}
		var address = context.Connection.RemoteIpAddress?.ToString() ?? "";
		if ( !TryCount( _sockets, address, MaxSocketsPerAddress, false ) )
		{
			logger.LogWarning( "Refused a socket from a client over its limit of {Limit}.", MaxSocketsPerAddress );
			context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
			return;
		}
		try
		{
			await serve();
		}
		finally
		{
			Uncount( _sockets, address, false );
		}
	}

	/// <summary>Counts one more for <paramref name="key"/> unless it reached <paramref name="limit"/>, or, for waiting devices, the relay reached <see cref="MaxWaiting"/>.</summary>
	private bool TryCount( Dictionary<string, int> counts, string key, int limit, bool isWaiting )
	{
		lock ( _gate )
		{
			var count = counts.GetValueOrDefault( key );
			if ( count >= limit || isWaiting && _waitingTotal >= MaxWaiting )
				return false;
			counts[key] = count + 1;
			if ( isWaiting )
				_waitingTotal++;
			return true;
		}
	}

	/// <summary>Counts one less for <paramref name="key"/>.</summary>
	private void Uncount( Dictionary<string, int> counts, string key, bool isWaiting )
	{
		lock ( _gate )
		{
			var count = counts.GetValueOrDefault( key );
			if ( count <= 1 )
				counts.Remove( key );
			else
				counts[key] = count - 1;
			if ( isWaiting )
				_waitingTotal--;
		}
	}

	/// <summary>Accepts the WebSocket of <paramref name="context"/>, or answers 400 when the request is not one.</summary>
	private static async Task<WebSocket?> AcceptAsync( HttpContext context )
	{
		if ( context.WebSockets.IsWebSocketRequest )
			return await context.WebSockets.AcceptWebSocketAsync();
		context.Response.StatusCode = StatusCodes.Status400BadRequest;
		return null;
	}

	/// <summary>Sends a host socket a fresh challenge, reads its answer and checks it proves <paramref name="hostId"/> for <paramref name="purpose"/>;
	/// closes the socket when it does not.</summary>
	private async Task<bool> ReadProofAsync( WebSocket socket, string hostId, string purpose, CancellationToken cancellation )
	{
		var nonce = HostProof.CreateNonce();
		var buffer = new byte[MaxProofLength];
		var length = 0;
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource( cancellation );
		timeout.CancelAfter( ProofWait );
		try
		{
			var challenge = Encoding.UTF8.GetBytes( new JsonObject { ["t"] = "relay.challenge", ["nonce"] = nonce }.ToJsonString() );
			await socket.SendAsync( challenge.AsMemory(), WebSocketMessageType.Text, true, timeout.Token );
			while ( true )
			{
				if ( length == buffer.Length )
					return await RejectAsync( socket, hostId, "The proof is too long." );
				var result = await socket.ReceiveAsync( buffer.AsMemory( length ), timeout.Token );
				if ( result.MessageType != WebSocketMessageType.Text )
					return await RejectAsync( socket, hostId, "The first frame is not text." );
				length += result.Count;
				if ( result.EndOfMessage )
					break;
			}
		}
		catch ( Exception exception ) when ( exception is WebSocketException or OperationCanceledException )
		{
			return false;
		}
		if ( HostProof.Verify( hostId, buffer.AsMemory( 0, length ), nonce, purpose, out var reason ) )
			return true;
		return await RejectAsync( socket, hostId, reason );
	}

	/// <summary>Logs and closes a host socket whose proof failed with <paramref name="reason"/>.</summary>
	private async Task<bool> RejectAsync( WebSocket socket, string hostId, string reason )
	{
		logger.LogWarning( "Rejected a socket for host {HostId}: {Reason}", hostId, reason );
		await RelayPipe.CloseAsync( socket, WebSocketCloseStatus.PolicyViolation, reason );
		return false;
	}

	/// <summary>Asks host <paramref name="hostId"/> over its control socket to open a data socket for <paramref name="sid"/>.</summary>
	/// <remarks>Does nothing when the host is not connected; the device then times out.</remarks>
	private async Task OpenAsync( string hostId, string sid )
	{
		HostLink? link;
		lock ( _gate )
			_hosts.TryGetValue( hostId, out link );
		if ( link is null )
			return;
		var frame = Encoding.UTF8.GetBytes( new JsonObject { ["t"] = "relay.open", ["sid"] = sid }.ToJsonString() );
		using var timeout = new CancellationTokenSource( DeviceWait );
		try
		{
			await link.Send.WaitAsync( timeout.Token );
			try
			{
				await link.Socket.SendAsync( frame.AsMemory(), WebSocketMessageType.Text, true, timeout.Token );
			}
			finally
			{
				link.Send.Release();
			}
		}
		catch ( Exception exception ) when ( exception is WebSocketException or OperationCanceledException or ObjectDisposedException )
		{
			// The control socket is gone; the device times out.
		}
	}

	/// <summary>Reads and discards frames from a host's control socket until it closes.</summary>
	private static async Task DrainAsync( WebSocket socket, CancellationToken cancellation )
	{
		var buffer = new byte[DrainBufferSize];
		while ( ( await socket.ReceiveAsync( buffer.AsMemory(), cancellation ) ).MessageType != WebSocketMessageType.Close )
		{
		}
	}

	/// <summary>Builds the key of a waiting device from its host and session ids.</summary>
	private static string BuildDeviceKey( string hostId, string sid ) => hostId + "/" + sid;

	/// <summary>Holds a host's control socket and serializes the frames the relay sends on it.</summary>
	private sealed class HostLink( WebSocket socket )
	{
		/// <summary>Gets the host's control socket.</summary>
		public WebSocket Socket { get; } = socket;

		/// <summary>Gets the gate that lets one <c>relay.open</c> send at a time.</summary>
		public SemaphoreSlim Send { get; } = new( 1, 1 );
	}

	/// <summary>Holds a device's wait for its host's data socket and the end of their pipe.</summary>
	private sealed class DeviceLink
	{
		/// <summary>Gets the host's data socket once the host opens it.</summary>
		public TaskCompletionSource<WebSocket> Host { get; } = new( TaskCreationOptions.RunContinuationsAsynchronously );

		/// <summary>Gets the signal that the pipe has ended, so the host's data request may finish.</summary>
		public TaskCompletionSource Done { get; } = new( TaskCreationOptions.RunContinuationsAsynchronously );
	}
}
