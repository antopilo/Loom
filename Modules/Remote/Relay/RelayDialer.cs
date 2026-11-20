using System.Net.WebSockets;

namespace Lokn.Modules.Remote;

/// <summary>Opens WebSocket connections to a relay (docs/remote.md, Relay): a device's socket to its host, and the host's own control and data sockets.</summary>
public static class RelayDialer
{
	/// <summary>How often an idle relay socket pings the relay.</summary>
	private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds( 20 );

	/// <summary>How long a ping may go unanswered before the socket counts as dead, so a vanished relay is noticed without waiting for TCP.</summary>
	private static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds( 20 );

	/// <summary>Connects a device to host <paramref name="hostId"/> through the relay: <c>/device/{hostId}</c>.</summary>
	/// <param name="relayUrl">The relay's address, as <see cref="BuildUri"/> accepts it.</param>
	/// <param name="hostId">The host's id, from its pairing link.</param>
	/// <param name="cancellationToken">Cancels connecting.</param>
	/// <returns>The device's socket; the relay copies its messages to the host once the host answers, or closes it with status 1001 after 10 s.</returns>
	/// <exception cref="ArgumentException"><paramref name="relayUrl"/> is not an address.</exception>
	/// <exception cref="WebSocketException">The relay did not accept the connection.</exception>
	public static Task<WebSocketConnection> ConnectAsync( string relayUrl, string hostId, CancellationToken cancellationToken ) =>
		OpenAsync( BuildUri( relayUrl, "/device/" + Uri.EscapeDataString( hostId ) ), cancellationToken );

	/// <summary>Builds a relay endpoint from the relay's address and an endpoint path such as <c>/device/{hostId}</c>.</summary>
	/// <remarks>
	/// The address may be <c>https://</c> or <c>wss://</c> (secure WebSocket), <c>http://</c> or <c>ws://</c> (plain, only for a relay at <c>localhost</c> or a
	/// loopback, private, link-local or Tailscale address, since host proofs and the web client travel in the clear), or a bare host[:port], which means
	/// <c>wss://</c>. A path in the address is kept as a prefix, for a relay behind a reverse proxy.
	/// </remarks>
	/// <exception cref="ArgumentException"><paramref name="relayUrl"/> is not an address, or is plain to a public host.</exception>
	public static Uri BuildUri( string relayUrl, string path )
	{
		var text = relayUrl.Trim();
		if ( !text.Contains( "://", StringComparison.Ordinal ) )
			text = "wss://" + text;
		if ( !Uri.TryCreate( text, UriKind.Absolute, out var parsed ) || parsed.Host.Length == 0 )
			throw new ArgumentException( $"'{relayUrl}' is not a relay address.", nameof( relayUrl ) );
		var scheme = parsed.Scheme switch
		{
			"https" or "wss" => "wss",
			"http" or "ws" => "ws",
			_ => throw new ArgumentException( $"'{relayUrl}' is not an http, https, ws or wss address.", nameof( relayUrl ) ),
		};
		if ( scheme == "ws" && !IsPrivateHost( parsed ) )
			throw new ArgumentException( $"'{relayUrl}' is plain http on a public host; use https so the relay's traffic is encrypted.", nameof( relayUrl ) );
		var builder = new UriBuilder( parsed )
		{
			Scheme = scheme,
			Path = parsed.AbsolutePath.TrimEnd( '/' ) + path,
			Query = "",
			Fragment = "",
		};
		return builder.Uri;
	}

	/// <summary>Determines whether an address names this PC or a private network: <c>localhost</c>, or a loopback, private, link-local or shared IP address.</summary>
	private static bool IsPrivateHost( Uri address ) =>
		address.IsLoopback || string.Equals( address.DnsSafeHost, "localhost", StringComparison.OrdinalIgnoreCase )
		|| System.Net.IPAddress.TryParse( address.DnsSafeHost, out var ip ) && NetworkAddresses.IsPrivate( ip );

	/// <summary>Builds the address of the web client the relay serves for a host, <c>https://&lt;relay&gt;/h/{hostId}/</c> (<c>http://</c> for a plain
	/// relay), keeping a path prefix as <see cref="BuildUri"/> does.</summary>
	/// <exception cref="ArgumentException"><paramref name="relayUrl"/> is not an address.</exception>
	public static string BuildPageUrl( string relayUrl, string hostId )
	{
		var socket = BuildUri( relayUrl, "/h/" + Uri.EscapeDataString( hostId ) + "/" );
		return new UriBuilder( socket ) { Scheme = socket.Scheme == "wss" ? "https" : "http" }.Uri.AbsoluteUri;
	}

	/// <summary>Opens one relay socket with pings that detect a dead relay.</summary>
	/// <exception cref="WebSocketException">The relay did not accept the connection.</exception>
	internal static async Task<WebSocketConnection> OpenAsync( Uri uri, CancellationToken cancellationToken )
	{
		var socket = new ClientWebSocket();
		socket.Options.KeepAliveInterval = KeepAlive;
		socket.Options.KeepAliveTimeout = KeepAliveTimeout;
		try
		{
			await socket.ConnectAsync( uri, cancellationToken ).ConfigureAwait( false );
			return new( socket, "relay " + uri.Authority );
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}
}
