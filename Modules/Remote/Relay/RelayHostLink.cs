using System.Diagnostics;
using System.Net.WebSockets;

namespace Lokn.Modules.Remote;

/// <summary>Keeps a host reachable through a relay (docs/remote.md, Relay): holds the control socket <c>/host/{hostId}</c> open, reconnecting with backoff, and opens a data socket <c>/host/{hostId}/{sid}</c> for each device the relay announces.</summary>
/// <remarks>
/// Every socket answers the relay's fresh challenge with a <see cref="RelayProof"/> for that socket alone. At most <see cref="MaxPendingSessions"/> device sessions
/// may be opening or in their handshake at once; further announcements are ignored and those devices time out at the relay. Events are raised on
/// thread-pool threads; post to the UI thread before touching state.
/// </remarks>
/// <param name="relayUrl">The relay's address, as <see cref="RelayDialer.BuildUri"/> accepts it.</param>
/// <param name="keys">The host's static keys; they sign every proof and give the host id.</param>
/// <param name="accept">Runs one device's data socket, such as <see cref="SecureChannel.AcceptAsync"/> and the session after it; it owns the connection, which closes when it ends.</param>
public sealed class RelayHostLink( string relayUrl, RemoteKeys keys, Func<WebSocketConnection, Task> accept ) : IAsyncDisposable
{
	/// <summary>The wait before the first reconnect.</summary>
	private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds( 1 );

	/// <summary>The longest wait between reconnects; each failure doubles the wait up to this.</summary>
	private static readonly TimeSpan LastRetry = TimeSpan.FromSeconds( 30 );

	/// <summary>How long a control socket must stay open before its loss counts as new trouble and the backoff starts over.</summary>
	private static readonly TimeSpan StableTime = TimeSpan.FromSeconds( 30 );

	/// <summary>How long the relay may take to send its challenge on a new socket.</summary>
	private static readonly TimeSpan ChallengeTimeout = TimeSpan.FromSeconds( 10 );

	/// <summary>The largest frame the relay sends the host itself: challenges and announcements.</summary>
	private const int MaxRelayFrameBytes = 4 * 1024;

	/// <summary>The most device sessions opening or in their handshake at once; anyone who knows the host id can make the relay announce one.</summary>
	public const int MaxPendingSessions = 16;

	/// <summary>Counts the device sessions opening or in their handshake.</summary>
	private int _pending;

	/// <summary>The host's static keys.</summary>
	private readonly RemoteKeys _keys = keys;

	/// <summary>The control socket's endpoint; built first, so a malformed address fails in the constructor. A data socket's endpoint adds <c>/{sid}</c>.</summary>
	private readonly Uri _control = RelayDialer.BuildUri( relayUrl, "/host/" + Uri.EscapeDataString( keys.HostId ) );

	/// <summary>Stops the control loop and data sockets still opening.</summary>
	private readonly CancellationTokenSource _stop = new();

	/// <summary>Stores the control loop once started.</summary>
	private Task? _loop;

	/// <summary>Holds the latest status text.</summary>
	private volatile string _status = "Not started";

	/// <summary>Holds whether the control socket is open.</summary>
	private volatile bool _connected;

	/// <summary>Raised with a short, user-facing text when the link's state changes, such as connected, refused or retrying.</summary>
	public event Action<string>? StatusChanged;

	/// <summary>Raised when a device's data socket fails to open or its <c>accept</c> handler throws.</summary>
	public event Action<Exception>? Failed;

	/// <summary>Gets the latest status text, as <see cref="StatusChanged"/> last reported it.</summary>
	public string Status => _status;

	/// <summary>Gets whether the control socket is open and has sent its proof.</summary>
	public bool IsConnected => _connected;

	/// <summary>Starts connecting; the link keeps reconnecting until disposed.</summary>
	/// <exception cref="InvalidOperationException">The link has already started.</exception>
	public void Start()
	{
		if ( _loop is not null )
			throw new InvalidOperationException( "The relay link has already started." );
		_loop = Task.Run( () => RunAsync( _stop.Token ) );
	}

	/// <summary>Closes the control socket and stops reconnecting; devices already connected keep running until their handlers end.</summary>
	public async ValueTask DisposeAsync()
	{
		if ( _stop.IsCancellationRequested )
			return;
		await _stop.CancelAsync().ConfigureAwait( false );
		if ( _loop is { } loop )
			await loop.ConfigureAwait( false );
		_connected = false;
	}

	/// <summary>Connects, serves and reconnects the control socket until <paramref name="cancellationToken"/> is cancelled.</summary>
	private async Task RunAsync( CancellationToken cancellationToken )
	{
		var relay = _control.Authority;
		var retry = FirstRetry;
		while ( true )
		{
			string problem;
			var openedAt = 0L;
			try
			{
				UpdateStatus( $"Connecting to the relay at {relay}…", false );
				await using var control = await RelayDialer.OpenAsync( _control, cancellationToken ).ConfigureAwait( false );
				control.MaxMessageBytes = MaxRelayFrameBytes;
				await ProveAsync( control, RelayProof.ControlPurpose, cancellationToken ).ConfigureAwait( false );
				openedAt = Stopwatch.GetTimestamp();
				UpdateStatus( $"Connected to the relay at {relay}", true );
				await ReadOpensAsync( control, cancellationToken ).ConfigureAwait( false );
				problem = control.Socket.CloseStatus == WebSocketCloseStatus.PolicyViolation
					? $"The relay at {relay} refused this host ({control.Socket.CloseStatusDescription})"
					: $"Lost the relay at {relay}";
			}
			catch ( OperationCanceledException ) when ( cancellationToken.IsCancellationRequested )
			{
				return;
			}
			catch ( Exception exception ) when ( exception is WebSocketException or IOException or InvalidDataException or HttpRequestException or OperationCanceledException )
			{
				problem = openedAt == 0 ? $"Cannot reach the relay at {relay} ({exception.Message})" : $"Lost the relay at {relay} ({exception.Message})";
			}

			if ( openedAt != 0 && Stopwatch.GetElapsedTime( openedAt ) >= StableTime )
				retry = FirstRetry;
			UpdateStatus( $"{problem}; retrying in {retry.TotalSeconds:0} s", false );
			try
			{
				await Task.Delay( retry, cancellationToken ).ConfigureAwait( false );
			}
			catch ( OperationCanceledException )
			{
				return;
			}

			retry = retry * 2 < LastRetry ? retry * 2 : LastRetry;
		}
	}

	/// <summary>Reads the control socket until it closes, opening a data socket for each <c>relay.open</c> while fewer than <see cref="MaxPendingSessions"/> are pending.</summary>
	private async Task ReadOpensAsync( WebSocketConnection control, CancellationToken cancellationToken )
	{
		while ( await control.ReceiveAsync( cancellationToken ).ConfigureAwait( false ) is { } frame )
		{
			if ( Frame.ReadType( frame ) != "relay.open" || Frame.ReadString( frame, "sid" ) is not { Length: > 0 } sid )
				continue;
			if ( Interlocked.Increment( ref _pending ) > MaxPendingSessions )
			{
				Interlocked.Decrement( ref _pending );
				continue;
			}
			_ = RunSessionAsync( sid, cancellationToken );
		}
	}

	/// <summary>Waits for the relay's challenge on a new socket and answers it with the proof for <paramref name="purpose"/>.</summary>
	/// <exception cref="InvalidDataException">The relay sent something other than a challenge.</exception>
	/// <exception cref="IOException">The relay closed the socket first.</exception>
	private async Task ProveAsync( WebSocketConnection connection, string purpose, CancellationToken cancellationToken )
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
		timeout.CancelAfter( ChallengeTimeout );
		var challenge = await connection.ReceiveAsync( timeout.Token ).ConfigureAwait( false )
			?? throw new IOException( connection.Socket.CloseStatusDescription ?? "The relay closed the socket before its challenge." );
		if ( Frame.ReadType( challenge ) != RelayProof.Challenge || Frame.ReadString( challenge, "nonce" ) is not { Length: > 0 } nonce )
			throw new InvalidDataException( "The relay did not send a challenge; it may run an older version." );
		await connection.SendAsync( RelayProof.BuildHello( _keys, nonce, purpose ) ).ConfigureAwait( false );
	}

	/// <summary>Opens the data socket for session <paramref name="sid"/>, proves it, and hands it to <c>accept</c> until it ends; it stops counting as pending
	/// once its handshake succeeded or it ended.</summary>
	private async Task RunSessionAsync( string sid, CancellationToken cancellationToken )
	{
		var released = 0;
		void Release()
		{
			if ( Interlocked.Exchange( ref released, 1 ) == 0 )
				Interlocked.Decrement( ref _pending );
		}

		var uri = new Uri( _control.AbsoluteUri + "/" + Uri.EscapeDataString( sid ) );
		WebSocketConnection connection;
		try
		{
			connection = await RelayDialer.OpenAsync( uri, cancellationToken ).ConfigureAwait( false );
		}
		catch ( Exception exception ) when ( exception is WebSocketException or HttpRequestException or OperationCanceledException )
		{
			Release();
			// The device times out at the relay.
			if ( !cancellationToken.IsCancellationRequested )
				Failed?.Invoke( exception );
			return;
		}

		_ = connection.Secured.ContinueWith( _ => Release(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default );
		try
		{
			connection.MaxMessageBytes = MaxRelayFrameBytes;
			await ProveAsync( connection, RelayProof.BuildDataPurpose( sid ), cancellationToken ).ConfigureAwait( false );
			await accept( connection ).ConfigureAwait( false );
		}
		catch ( Exception exception )
		{
			// Nothing awaits this session; report its failure instead of losing it.
			Failed?.Invoke( exception );
		}
		finally
		{
			Release();
			await connection.DisposeAsync().ConfigureAwait( false );
		}
	}

	/// <summary>Records and reports a new status.</summary>
	private void UpdateStatus( string status, bool connected )
	{
		_connected = connected;
		_status = status;
		StatusChanged?.Invoke( status );
	}
}
