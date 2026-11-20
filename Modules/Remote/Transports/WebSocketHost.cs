using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Lokn.Modules.Remote;

/// <summary>Accepts WebSocket connections on <c>/loom</c> and serves GET handlers mapped to other paths, with a minimal HTTP/1.1 server on TCP listeners.</summary>
/// <remarks>
/// It binds only the endpoints it is given, such as 127.0.0.1:47390 and each LAN address; a wildcard (<see cref="IPAddress.Any"/>) must be passed
/// explicitly. It needs no URL reservation or administrator rights and does not match the <c>Host</c> header, so a proxy such as <c>tailscale serve</c>
/// reaches it under any name. The request line and headers may take <see cref="HostRequest.MaxHeadBytes"/>; a request with a body, malformed, or too
/// large is answered 400. Each connection carries one request and closes after the answer, unless it upgrades to a WebSocket.
/// </remarks>
/// <param name="endpoints">The addresses and ports to listen on; port 0 takes a free port, which <see cref="Endpoints"/> gives once started.</param>
/// <param name="accept">Runs each WebSocket connection; it owns the connection, which closes when it ends.</param>
public sealed class WebSocketHost( IReadOnlyList<IPEndPoint> endpoints, Func<WebSocketConnection, Task> accept ) : IAsyncDisposable
{
	/// <summary>The path of the protocol endpoint.</summary>
	public const string Path = "/loom";

	/// <summary>The GUID RFC 6455 appends to <c>Sec-WebSocket-Key</c> before hashing it into <c>Sec-WebSocket-Accept</c>.</summary>
	private const string HandshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

	/// <summary>Bounds how long a client may take to send its request line and headers.</summary>
	private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds( 10 );

	/// <summary>Defines how often an idle WebSocket pings, so NATs and proxies keep it open.</summary>
	private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds( 20 );

	/// <summary>Defines how long a ping may go unanswered before the connection counts as dead and is dropped.</summary>
	private static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds( 30 );

	/// <summary>Bounds how long a closing connection waits for the client to close its side.</summary>
	private static readonly TimeSpan LingerTimeout = TimeSpan.FromSeconds( 1 );

	/// <summary>The most connections open at once; further ones are closed as soon as they are accepted.</summary>
	public const int MaxConnections = 512;

	/// <summary>The most connections open at once from one remote address other than loopback, where proxies such as <c>tailscale serve</c> connect from.</summary>
	public const int MaxConnectionsPerAddress = 32;

	/// <summary>Counts the open connections, in total and per remote address.</summary>
	private readonly ConnectionLimiter _connections = new( MaxConnections, MaxConnectionsPerAddress );

	/// <summary>Holds the started listeners.</summary>
	private readonly List<TcpListener> _listeners = [];

	/// <summary>Holds the GET handlers by path, matched without case.</summary>
	private readonly Dictionary<string, Func<HostRequest, HostResponse>> _routes = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Stores the accept loops once started.</summary>
	private Task? _loops;

	/// <summary>Records whether <see cref="DisposeAsync"/> ran, so the accept loops end quietly.</summary>
	private volatile bool _stopped;

	/// <summary>Raised when a handler, a WebSocket connection or an accept loop fails, on a thread-pool thread.</summary>
	public event Action<Exception>? Failed;

	/// <summary>Gets the endpoints the host listens on: the given ones until started, then the bound ones, with the ports chosen for port 0.</summary>
	public IReadOnlyList<IPEndPoint> Endpoints { get; private set; } = endpoints;

	/// <summary>Serves GET and HEAD requests for a path other than <see cref="Path"/>, such as the web client's files; the host writes the answer.</summary>
	public WebSocketHost MapGet( string path, Func<HostRequest, HostResponse> handler )
	{
		lock ( _routes )
			_routes[path] = handler;
		return this;
	}

	/// <summary>Starts listening on every endpoint, or on none when one fails.</summary>
	/// <exception cref="IOException">An endpoint's port is taken or its address is not this PC's.</exception>
	public void Start()
	{
		if ( _loops is not null )
			throw new InvalidOperationException( "The WebSocket host has already started." );
		foreach ( var endpoint in Endpoints )
		{
			var listener = new TcpListener( endpoint );
			try
			{
				listener.Start();
			}
			catch ( SocketException exception )
			{
				listener.Dispose();
				foreach ( var started in _listeners )
					started.Dispose();
				_listeners.Clear();
				throw new IOException( $"Loom cannot listen on {endpoint}: {exception.Message}", exception );
			}
			_listeners.Add( listener );
		}
		Endpoints = [.. _listeners.Select( listener => (IPEndPoint)listener.LocalEndpoint )];
		_loops = Task.WhenAll( _listeners.Select( listener => Task.Run( () => AcceptLoopAsync( listener ) ) ) );
	}

	/// <summary>Stops listening; connections already accepted keep running until their handlers end.</summary>
	public async ValueTask DisposeAsync()
	{
		if ( _stopped )
			return;
		_stopped = true;
		foreach ( var listener in _listeners )
			listener.Dispose();
		if ( _loops is { } loops )
			await loops.ConfigureAwait( false );
	}

	/// <summary>Takes connections until the listener stops.</summary>
	private async Task AcceptLoopAsync( TcpListener listener )
	{
		while ( true )
		{
			Socket socket;
			try
			{
				socket = await listener.AcceptSocketAsync().ConfigureAwait( false );
			}
			catch ( SocketException exception ) when ( !_stopped && exception.SocketErrorCode == SocketError.ConnectionReset )
			{
				// A client that gave up before it was accepted.
				continue;
			}
			catch ( Exception exception )
			{
				// Stopping the listener ends the wait with an exception; anything else stops this listener and is reported.
				if ( !_stopped )
					Failed?.Invoke( exception );
				return;
			}
			var address = socket.RemoteEndPoint is IPEndPoint { Address: var ip } && !IPAddress.IsLoopback( ip ) ? ip.ToString() : null;
			if ( !_connections.TryEnter( address ) )
			{
				socket.Dispose();
				continue;
			}
			_ = ServeAsync( socket, address );
		}
	}

	/// <summary>Reads one request and answers it, or upgrades it and runs the WebSocket connection; a client that breaks off is closed quietly.</summary>
	/// <param name="socket">The accepted socket.</param>
	/// <param name="address">The address it counts toward in the connection limit, or null.</param>
	private async Task ServeAsync( Socket socket, string? address )
	{
		var remote = socket.RemoteEndPoint?.ToString() ?? "";
		var stream = new NetworkStream( socket, true );
		try
		{
			HostRequest request;
			try
			{
				request = await ReadRequestAsync( stream, remote ).ConfigureAwait( false );
			}
			catch ( InvalidDataException )
			{
				await AnswerAsync( stream, new HostResponse( 400 ), false ).ConfigureAwait( false );
				return;
			}

			if ( !string.Equals( request.Path, Path, StringComparison.OrdinalIgnoreCase ) )
			{
				await AnswerAsync( stream, Route( request ), request.Method == "HEAD" ).ConfigureAwait( false );
				return;
			}

			var handshake = BuildHandshake( request );
			if ( handshake.StatusCode != 101 )
			{
				await AnswerAsync( stream, handshake, false ).ConfigureAwait( false );
				return;
			}
			await stream.WriteAsync( handshake.FormatHead( false ) ).ConfigureAwait( false );
			await RunWebSocketAsync( stream, remote, ReadClientAddress( socket, request ) ).ConfigureAwait( false );
		}
		catch ( Exception exception ) when ( exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException )
		{
			// The client went away, or sent its request too slowly.
		}
		finally
		{
			await stream.DisposeAsync().ConfigureAwait( false );
			_connections.Exit( address );
		}
	}

	/// <summary>Reads the client's IP address for per-address limits: the socket's, or for a proxy on loopback such as <c>tailscale serve</c> the last
	/// <c>X-Forwarded-For</c> entry, which the proxy appends.</summary>
	private static string? ReadClientAddress( Socket socket, HostRequest request )
	{
		if ( socket.RemoteEndPoint is not IPEndPoint { Address: var ip } )
			return null;
		if ( IPAddress.IsLoopback( ip ) && request["X-Forwarded-For"] is { } forwarded
			&& IPAddress.TryParse( forwarded.Split( ',' )[^1].Trim(), out var client ) )
			return client.ToString();
		return ip.ToString();
	}

	/// <summary>Reads the request line and headers, which must end the data the client sent so far.</summary>
	/// <exception cref="InvalidDataException">The request is malformed, its headers are too large, or it has a body.</exception>
	/// <exception cref="EndOfStreamException">The client closed before the headers ended.</exception>
	/// <exception cref="OperationCanceledException">The client took longer than <see cref="HeadTimeout"/>.</exception>
	private static async Task<HostRequest> ReadRequestAsync( NetworkStream stream, string remote )
	{
		var buffer = new byte[HostRequest.MaxHeadBytes];
		var count = 0;
		using var timeout = new CancellationTokenSource( HeadTimeout );
		while ( true )
		{
			var end = buffer.AsSpan( 0, count ).IndexOf( "\r\n\r\n"u8 );
			if ( end >= 0 )
			{
				// Bytes after the headers are a body or a pipelined request, which the host takes neither of.
				if ( end + 4 != count )
					throw new InvalidDataException( "The request has a body." );
				var request = HostRequest.Parse( buffer.AsSpan( 0, end ), remote ) ?? throw new InvalidDataException( "The request is malformed." );
				return request.HasBody ? throw new InvalidDataException( "The request has a body." ) : request;
			}
			if ( count == buffer.Length )
				throw new InvalidDataException( "The request's headers are too large." );
			var read = await stream.ReadAsync( buffer.AsMemory( count ), timeout.Token ).ConfigureAwait( false );
			if ( read == 0 )
				throw new EndOfStreamException( "The client closed before its request ended." );
			count += read;
		}
	}

	/// <summary>Runs a mapped GET handler, or answers 404 for an unmapped path and 405 for another method.</summary>
	private HostResponse Route( HostRequest request )
	{
		Func<HostRequest, HostResponse>? handler;
		lock ( _routes )
			_routes.TryGetValue( request.Path, out handler );
		if ( handler is null )
			return new( 404 );
		if ( request.Method is not ("GET" or "HEAD") )
			return new( 405 ) { Headers = { ["Allow"] = "GET, HEAD" } };
		try
		{
			return handler( request );
		}
		catch ( Exception exception )
		{
			Failed?.Invoke( exception );
			return new( 500 );
		}
	}

	/// <summary>Answers a request to <see cref="Path"/>: 101 for a valid RFC 6455 upgrade, 426 for another WebSocket version, else 400.</summary>
	private static HostResponse BuildHandshake( HostRequest request )
	{
		if ( request.Method != "GET" || request.Version != "HTTP/1.1" || !request.HasToken( "Upgrade", "websocket" ) || !request.HasToken( "Connection", "Upgrade" )
			|| request["Sec-WebSocket-Key"] is not { } key || !IsHandshakeKey( key ) )
			return new( 400 );
		if ( request["Sec-WebSocket-Version"] != "13" )
			return new( 426 ) { Headers = { ["Sec-WebSocket-Version"] = "13" } };
		return new( 101 )
		{
			Headers =
			{
				["Upgrade"] = "websocket",
				["Connection"] = "Upgrade",
				["Sec-WebSocket-Accept"] = ComputeAccept( key ),
			},
		};
	}

	/// <summary>Gets whether a <c>Sec-WebSocket-Key</c> is base64 of 16 bytes.</summary>
	private static bool IsHandshakeKey( string key )
	{
		Span<byte> nonce = stackalloc byte[24];
		return Convert.TryFromBase64String( key, nonce, out var written ) && written == 16;
	}

	/// <summary>Computes <c>Sec-WebSocket-Accept</c>: base64( SHA-1( key + the RFC 6455 GUID ) ).</summary>
	internal static string ComputeAccept( string key ) =>
		// RFC 6455 fixes SHA-1 here; the value only proves the server understood the handshake.
#pragma warning disable CA5350
		Convert.ToBase64String( SHA1.HashData( Encoding.ASCII.GetBytes( key + HandshakeGuid ) ) );
#pragma warning restore CA5350

	/// <summary>Runs an upgraded connection's WebSocket with the accept handler, reporting what the handler throws.</summary>
	private async Task RunWebSocketAsync( NetworkStream stream, string remote, string? address )
	{
		var socket = WebSocket.CreateFromStream( stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = KeepAlive, KeepAliveTimeout = KeepAliveTimeout } );
		var connection = new WebSocketConnection( socket, remote ) { RemoteAddress = address };
		try
		{
			await accept( connection ).ConfigureAwait( false );
		}
		catch ( Exception exception )
		{
			Failed?.Invoke( exception );
		}
		finally
		{
			await connection.DisposeAsync().ConfigureAwait( false );
		}
	}

	/// <summary>Writes an answer with <c>Connection: close</c>, then closes the sending side and reads until the client closes, so unread request bytes
	/// do not reset the connection before the client reads the answer.</summary>
	private static async Task AnswerAsync( NetworkStream stream, HostResponse response, bool headOnly )
	{
		await stream.WriteAsync( response.FormatHead( true ) ).ConfigureAwait( false );
		if ( !headOnly && response.HasContent && !response.Body.IsEmpty )
			await stream.WriteAsync( response.Body ).ConfigureAwait( false );
		stream.Socket.Shutdown( SocketShutdown.Send );
		using var linger = new CancellationTokenSource( LingerTimeout );
		var discard = new byte[1024];
		while ( await stream.ReadAsync( discard, linger.Token ).ConfigureAwait( false ) > 0 )
		{
		}
	}
}
