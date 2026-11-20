using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Lokn.Core;

/// <summary>A SOCKS5 proxy on this PC's 127.0.0.1 that every web view of a paired host's session connects through (<see cref="BrowserTabs.IsRemote"/>):
/// a loopback destination, by name or by address, goes to the host through <see cref="BrowserTabs.Tunnel"/>, since <c>localhost</c> in the host's pages
/// means the host; another address of this PC is refused; anything else connects directly (docs/remote.md, Localhost forwarding).</summary>
/// <remarks>
/// Chromium sends a SOCKS5 proxy the names it does not resolve itself, so the proxy resolves every name and a name that resolves to loopback reaches the
/// host as well, never this PC. Connections run on the thread pool until both directions end; one direction ending is passed on as a half-close. The proxy
/// lives as long as the process, across reloads of the modules that use it, since a WebView2 environment keeps the proxy it started with.
/// </remarks>
internal sealed class BrowserProxy : IDisposable
{
	/// <summary>Defines the reply that the destination was reached.</summary>
	private const byte Succeeded = 0;

	/// <summary>Defines the reply that the destination is not allowed, such as this PC's own address.</summary>
	private const byte NotAllowed = 2;

	/// <summary>Defines the reply that the destination's name or address cannot be reached.</summary>
	private const byte HostUnreachable = 4;

	/// <summary>Defines the reply that nothing answered on the destination's port.</summary>
	private const byte ConnectionRefused = 5;

	/// <summary>Defines the reply to a request other than connect.</summary>
	private const byte CommandNotSupported = 7;

	/// <summary>Defines the reply to an address type SOCKS5 does not define.</summary>
	private const byte AddressNotSupported = 8;

	/// <summary>Defines how long a client may take to name its destination.</summary>
	private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds( 10 );

	/// <summary>Defines how long one direct connection attempt may take.</summary>
	private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds( 20 );

	/// <summary>Defines how long this PC's own addresses are kept before they are read again.</summary>
	private static readonly TimeSpan AddressesLifetime = TimeSpan.FromSeconds( 10 );

	/// <summary>Stores the listener on 127.0.0.1.</summary>
	private readonly TcpListener _listener;

	/// <summary>Gets what reaches the paired host's loopback, or null while nothing can.</summary>
	private readonly Func<IBrowserTunnel?> _tunnel;

	/// <summary>Ends the accept loop.</summary>
	private readonly CancellationTokenSource _stopping = new();

	/// <summary>Guards <see cref="_own"/>.</summary>
	private readonly Lock _gate = new();

	/// <summary>Stores this PC's own addresses and when they were read, or null before the first read.</summary>
	private (DateTime Read, HashSet<IPAddress> Addresses)? _own;

	/// <summary>Starts the proxy on a free port of 127.0.0.1.</summary>
	/// <param name="tunnel">Gets what reaches the paired host's loopback, or null while nothing can.</param>
	/// <exception cref="SocketException">No port could be bound.</exception>
	public BrowserProxy( Func<IBrowserTunnel?> tunnel )
	{
		_tunnel = tunnel;
		_listener = new TcpListener( IPAddress.Loopback, 0 );
		_listener.Start();
		Server = $"socks5://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
		_ = AcceptAsync();
	}

	/// <summary>Gets the proxy as a Chromium <c>--proxy-server</c> value.</summary>
	public string Server { get; }

	/// <summary>Stops accepting connections; those already open run to their end.</summary>
	public void Dispose()
	{
		_stopping.Cancel();
		_listener.Stop();
	}

	/// <summary>Reports whether a host name is loopback by name alone: <c>localhost</c> or a name under it, with or without a final dot.</summary>
	public static bool IsLoopbackName( string host )
	{
		var name = host.TrimEnd( '.' );
		return name.Equals( "localhost", StringComparison.OrdinalIgnoreCase ) || name.EndsWith( ".localhost", StringComparison.OrdinalIgnoreCase );
	}

	/// <summary>Accepts connections until the proxy stops.</summary>
	private async Task AcceptAsync()
	{
		while ( !_stopping.IsCancellationRequested )
		{
			Socket client;
			try
			{
				client = await _listener.AcceptSocketAsync( _stopping.Token ).ConfigureAwait( false );
			}
			catch ( Exception exception ) when ( exception is SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException )
			{
				return;
			}
			_ = ServeAsync( client );
		}
	}

	/// <summary>Reads one client's destination and connects it: to the tunnel, directly, or not at all.</summary>
	private async Task ServeAsync( Socket client )
	{
		client.NoDelay = true;
		var stream = new NetworkStream( client, ownsSocket: false );
		var isHandedOver = false;
		try
		{
			Request? request;
			Target target;
			using ( var timeout = CancellationTokenSource.CreateLinkedTokenSource( _stopping.Token ) )
			{
				timeout.CancelAfter( HandshakeTimeout );
				request = await ReadRequestAsync( stream, timeout.Token ).ConfigureAwait( false );
				if ( request is null )
					return;
				target = await ResolveAsync( request, timeout.Token ).ConfigureAwait( false );
			}

			if ( target.TunnelHost is { } host )
			{
				if ( _tunnel() is not { } tunnel )
				{
					await ReplyAsync( stream, NotAllowed ).ConfigureAwait( false );
					return;
				}

				isHandedOver = true;
				await tunnel.RunAsync( client, host, request.Port, isReached => ReplyAsync( stream, isReached ? Succeeded : ConnectionRefused ) ).ConfigureAwait( false );
				return;
			}

			if ( target.Addresses is not { } addresses )
			{
				await ReplyAsync( stream, target.Refusal ).ConfigureAwait( false );
				return;
			}

			var remote = await ConnectAsync( addresses, request.Port ).ConfigureAwait( false );
			if ( remote is null )
			{
				await ReplyAsync( stream, ConnectionRefused ).ConfigureAwait( false );
				return;
			}

			await ReplyAsync( stream, Succeeded ).ConfigureAwait( false );
			isHandedOver = true;
			await PumpAsync( client, remote ).ConfigureAwait( false );
		}
		catch ( Exception exception ) when ( exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException )
		{
			// The client left or took too long to name its destination.
		}
		finally
		{
			if ( !isHandedOver )
				client.Dispose();
		}
	}

	/// <summary>Reads the client's greeting and connect request, answering the greeting; null after refusing what the proxy does not support.</summary>
	private static async Task<Request?> ReadRequestAsync( NetworkStream stream, CancellationToken cancellationToken )
	{
		var head = new byte[2];
		await stream.ReadExactlyAsync( head, cancellationToken ).ConfigureAwait( false );
		if ( head[0] != 5 )
			return null;

		var methods = new byte[head[1]];
		await stream.ReadExactlyAsync( methods, cancellationToken ).ConfigureAwait( false );
		// Only "no authentication", which is all Chromium speaks.
		var isNoAuthentication = methods.Contains( (byte)0 );
		await stream.WriteAsync( new byte[] { 5, isNoAuthentication ? (byte)0 : (byte)0xFF }, cancellationToken ).ConfigureAwait( false );
		if ( !isNoAuthentication )
			return null;

		var request = new byte[4];
		await stream.ReadExactlyAsync( request, cancellationToken ).ConfigureAwait( false );
		if ( request[0] != 5 )
			return null;

		string? name = null;
		IPAddress? address = null;
		switch ( request[3] )
		{
			case 1:
				var v4 = new byte[4];
				await stream.ReadExactlyAsync( v4, cancellationToken ).ConfigureAwait( false );
				address = new IPAddress( v4 );
				break;
			case 3:
				var length = new byte[1];
				await stream.ReadExactlyAsync( length, cancellationToken ).ConfigureAwait( false );
				var text = new byte[length[0]];
				await stream.ReadExactlyAsync( text, cancellationToken ).ConfigureAwait( false );
				name = Encoding.ASCII.GetString( text );
				break;
			case 4:
				var v6 = new byte[16];
				await stream.ReadExactlyAsync( v6, cancellationToken ).ConfigureAwait( false );
				address = new IPAddress( v6 );
				break;
			default:
				await ReplyAsync( stream, AddressNotSupported ).ConfigureAwait( false );
				return null;
		}

		var port = new byte[2];
		await stream.ReadExactlyAsync( port, cancellationToken ).ConfigureAwait( false );
		if ( request[1] != 1 )
		{
			await ReplyAsync( stream, CommandNotSupported ).ConfigureAwait( false );
			return null;
		}

		return new Request( name, address, BinaryPrimitives.ReadUInt16BigEndian( port ) );
	}

	/// <summary>Decides where a destination goes: loopback, by name or address, to the tunnel; this PC's other addresses nowhere; the rest directly.</summary>
	private async Task<Target> ResolveAsync( Request request, CancellationToken cancellationToken )
	{
		IPAddress[] addresses;
		if ( request.Name is { } name )
		{
			if ( IsLoopbackName( name ) )
				return new( "localhost", null, Succeeded );

			if ( IPAddress.TryParse( name.Trim( '[', ']' ), out var literal ) )
			{
				addresses = [literal];
			}
			else
			{
				try
				{
					addresses = await Dns.GetHostAddressesAsync( name, cancellationToken ).ConfigureAwait( false );
				}
				catch ( SocketException )
				{
					return new( null, null, HostUnreachable );
				}
			}
		}
		else
		{
			addresses = [request.Address!];
		}

		addresses = [.. addresses.Select( address => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address )];
		if ( addresses.Length == 0 )
			return new( null, null, HostUnreachable );

		// The unspecified address reaches this PC's own services as well, so it stands for loopback.
		if ( addresses.FirstOrDefault( address => IPAddress.IsLoopback( address ) || address.Equals( IPAddress.Any ) || address.Equals( IPAddress.IPv6Any ) ) is { } local )
			return new( IPAddress.IsLoopback( local ) ? local.ToString() : local.AddressFamily == AddressFamily.InterNetworkV6 ? "::1" : "127.0.0.1", null, Succeeded );

		var own = ReadOwnAddresses();
		return addresses.Any( own.Contains ) ? new( null, null, NotAllowed ) : new( null, addresses, Succeeded );
	}

	/// <summary>Reads this PC's own addresses, at most every <see cref="AddressesLifetime"/>; none when the network interfaces cannot be read.</summary>
	private HashSet<IPAddress> ReadOwnAddresses()
	{
		lock ( _gate )
		{
			if ( _own is { } known && DateTime.UtcNow - known.Read < AddressesLifetime )
				return known.Addresses;

			HashSet<IPAddress> addresses;
			try
			{
				addresses = [.. NetworkInterface.GetAllNetworkInterfaces().SelectMany( adapter => adapter.GetIPProperties().UnicastAddresses ).Select( unicast => unicast.Address )];
			}
			catch ( NetworkInformationException )
			{
				addresses = [];
			}
			_own = (DateTime.UtcNow, addresses);
			return addresses;
		}
	}

	/// <summary>Connects to the first address that answers on the port, or returns null.</summary>
	private async Task<Socket?> ConnectAsync( IPAddress[] addresses, int port )
	{
		foreach ( var address in addresses )
		{
			var socket = new Socket( address.AddressFamily, SocketType.Stream, ProtocolType.Tcp ) { NoDelay = true };
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource( _stopping.Token );
			timeout.CancelAfter( ConnectTimeout );
			try
			{
				await socket.ConnectAsync( address, port, timeout.Token ).ConfigureAwait( false );
				return socket;
			}
			catch ( Exception exception ) when ( exception is SocketException or OperationCanceledException )
			{
				socket.Dispose();
			}
		}
		return null;
	}

	/// <summary>Answers the client's request with a reply code and no bound address.</summary>
	private static async Task ReplyAsync( NetworkStream stream, byte code )
	{
		try
		{
			await stream.WriteAsync( new byte[] { 5, code, 0, 1, 0, 0, 0, 0, 0, 0 } ).ConfigureAwait( false );
		}
		catch ( Exception exception ) when ( exception is IOException or ObjectDisposedException )
		{
			// The client left; closing its connection follows.
		}
	}

	/// <summary>Copies bytes both ways until both directions end, then closes both connections.</summary>
	private static async Task PumpAsync( Socket client, Socket remote )
	{
		using ( client )
		using ( remote )
			await Task.WhenAll( CopyAsync( client, remote ), CopyAsync( remote, client ) ).ConfigureAwait( false );
	}

	/// <summary>Copies one direction and passes its end on as a half-close; a failure closes both connections, which ends the other direction too.</summary>
	private static async Task CopyAsync( Socket from, Socket to )
	{
		var buffer = new byte[64 * 1024];
		try
		{
			while ( true )
			{
				var read = await from.ReceiveAsync( buffer, SocketFlags.None ).ConfigureAwait( false );
				if ( read == 0 )
					break;

				for ( var sent = 0; sent < read; )
					sent += await to.SendAsync( buffer.AsMemory( sent, read - sent ), SocketFlags.None ).ConfigureAwait( false );
			}
			to.Shutdown( SocketShutdown.Send );
		}
		catch ( Exception exception ) when ( exception is SocketException or ObjectDisposedException )
		{
			from.Close();
			to.Close();
		}
	}

	/// <summary>A client's destination: a name or an address, and a port.</summary>
	/// <param name="Name">The name to resolve, or null.</param>
	/// <param name="Address">The address, or null with a name.</param>
	/// <param name="Port">The port.</param>
	private sealed record Request( string? Name, IPAddress? Address, int Port );

	/// <summary>Where a destination goes: the tunnel to a loopback host, addresses to connect directly, or neither with the refusal's reply code.</summary>
	/// <param name="TunnelHost">The loopback host to reach through the tunnel, or null.</param>
	/// <param name="Addresses">The addresses to connect to directly, or null.</param>
	/// <param name="Refusal">The reply code when neither is set.</param>
	private sealed record Target( string? TunnelHost, IPAddress[]? Addresses, byte Refusal );
}
