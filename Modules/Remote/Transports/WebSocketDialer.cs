using System.Net.WebSockets;

namespace Lokn.Modules.Remote;

/// <summary>Opens WebSocket connections to a host's <c>/loom</c> endpoint or a relay.</summary>
public static class WebSocketDialer
{
	/// <summary>How often an idle socket pings, so NATs and proxies keep it open.</summary>
	private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds( 20 );

	/// <summary>Builds a host's endpoint, <c>ws://&lt;address&gt;/loom</c>, from host:port (the default port when missing) or a full ws/wss address.</summary>
	public static Uri BuildHostUri( string address, int defaultPort )
	{
		if ( address.StartsWith( "ws://", StringComparison.OrdinalIgnoreCase ) || address.StartsWith( "wss://", StringComparison.OrdinalIgnoreCase ) )
			return new( address );
		var builder = new UriBuilder( "ws://" + address ) { Path = WebSocketHost.Path };
		if ( !address.Contains( ':' ) || address.EndsWith( ']' ) )
			builder.Port = defaultPort;
		return builder.Uri;
	}

	/// <summary>Connects to a WebSocket endpoint.</summary>
	/// <exception cref="WebSocketException">The endpoint did not accept the connection.</exception>
	public static async Task<WebSocketConnection> ConnectAsync( Uri uri, CancellationToken cancellationToken )
	{
		var socket = new ClientWebSocket();
		socket.Options.KeepAliveInterval = KeepAlive;
		try
		{
			await socket.ConnectAsync( uri, cancellationToken ).ConfigureAwait( false );
			return new( socket, uri.Authority );
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}
}
