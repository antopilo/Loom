using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>Carries a paired PC's web view connections to the host's loopback services over <c>tcp.*</c> frames: the <see cref="IBrowserTunnel"/> the
/// browser proxy (<see cref="BrowserTabs.ProxyServer"/>) hands every loopback destination of the host's pages (docs/remote.md, Localhost forwarding).</summary>
/// <remarks>
/// Streams run on the thread pool. A connection while no host is connected, or with a host that lets this PC only view, is refused. Each stream sends one
/// chunk at a time and acknowledges what it wrote, so neither side buffers without bound; the web view ending what it sends becomes <c>tcp.eof</c>, so a
/// response still on its way arrives whole, and the stream ends once the host closed it and every byte it sent was written.
/// </remarks>
internal sealed class TunnelClient : IBrowserTunnel, IDisposable
{
	/// <summary>Holds the open streams by id, with the peer they tunnel through.</summary>
	private readonly ConcurrentDictionary<long, Tunnel> _tunnels = new();

	/// <summary>Ends every stream when the client stops.</summary>
	private readonly CancellationTokenSource _stopping = new();

	/// <summary>Stores the connected host's peer and whether it lets this PC control, or null while disconnected.</summary>
	private volatile Binding? _binding;

	/// <summary>Stores the last stream id used.</summary>
	private long _lastId;

	/// <summary>Uses a host's connection for new streams and takes its tunnel pushes; null while disconnected. Streams of an earlier connection end with it.</summary>
	/// <param name="peer">The connected peer, or null.</param>
	/// <param name="canControl">Whether the host lets this PC control; a view-only PC tunnels nothing.</param>
	public void Bind( RpcPeer? peer, bool canControl )
	{
		_binding = peer is null ? null : new Binding( peer, canControl );
		if ( peer is null )
			return;

		peer.OnPush( "tcp.data", ApplyData ).OnPush( "tcp.close", ApplyClose );
		peer.Closed += _ =>
		{
			foreach ( var tunnel in _tunnels.Values.Where( tunnel => tunnel.Peer == peer ) )
				End( tunnel, isTold: true );
		};
	}

	/// <summary>Opens a stream on the host for one web view connection, answers whether it opened, then copies both ways until both end.</summary>
	public async Task RunAsync( Socket connection, string host, int port, Func<bool, Task> answer )
	{
		var network = new NetworkStream( connection, ownsSocket: true );
		if ( _binding is not { CanControl: true } binding || _stopping.IsCancellationRequested )
		{
			await answer( false ).ConfigureAwait( false );
			await network.DisposeAsync().ConfigureAwait( false );
			return;
		}

		var tunnel = new Tunnel( Interlocked.Increment( ref _lastId ), binding.Peer, network );
		_tunnels[tunnel.Id] = tunnel;
		try
		{
			var open = Frame.Create( "tcp.open" );
			open["tcp"] = tunnel.Id;
			open["port"] = port;
			open["host"] = host;
			await tunnel.Peer.RequestAsync( open, _stopping.Token ).ConfigureAwait( false );
		}
		catch ( Exception exception ) when ( exception is IOException or ObjectDisposedException or OperationCanceledException or RemoteException or InvalidOperationException )
		{
			// The host refused the port or nothing answers there; it holds no stream.
			await answer( false ).ConfigureAwait( false );
			End( tunnel, isTold: true );
			return;
		}

		await answer( true ).ConfigureAwait( false );
		var writing = WriteAsync( tunnel );
		try
		{
			var buffer = new byte[TunnelService.ChunkBytes];
			while ( true )
			{
				var read = await network.ReadAsync( buffer, tunnel.Closing.Token ).ConfigureAwait( false );
				if ( read == 0 )
					break;

				var data = Frame.Create( "tcp.data" );
				data["tcp"] = tunnel.Id;
				data["data"] = Convert.ToBase64String( buffer, 0, read );
				await tunnel.Peer.RequestAsync( data, tunnel.Closing.Token ).ConfigureAwait( false );
			}

			// The web view sent all it will; the host's service still answers.
			var eof = Frame.Create( "tcp.eof" );
			eof["tcp"] = tunnel.Id;
			await tunnel.Peer.RequestAsync( eof, tunnel.Closing.Token ).ConfigureAwait( false );
		}
		catch ( Exception exception ) when ( exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException or RemoteException or InvalidOperationException )
		{
			// The page, the host's service or the connection closed the stream.
			End( tunnel, isTold: false );
		}
		await writing.ConfigureAwait( false );
	}

	/// <summary>Closes every stream and refuses new ones.</summary>
	public void Dispose()
	{
		_stopping.Cancel();
		foreach ( var tunnel in _tunnels.Values )
			End( tunnel, isTold: true );
	}

	/// <summary>Writes the host's bytes to the web view's connection in order, acknowledging each chunk once written; ends the stream once the host closed it.</summary>
	private async Task WriteAsync( Tunnel tunnel )
	{
		try
		{
			await foreach ( var chunk in tunnel.Incoming.Reader.ReadAllAsync( tunnel.Closing.Token ).ConfigureAwait( false ) )
			{
				await tunnel.Network.WriteAsync( chunk, tunnel.Closing.Token ).ConfigureAwait( false );
				var ack = Frame.Create( "tcp.ack" );
				ack["tcp"] = tunnel.Id;
				ack["bytes"] = chunk.Length;
				await tunnel.Peer.PushAsync( ack ).ConfigureAwait( false );
			}
		}
		catch ( Exception exception ) when ( exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException )
		{
			End( tunnel, isTold: false );
			return;
		}

		// The host closed the stream and every byte it sent was written.
		End( tunnel, isTold: true );
	}

	/// <summary>Takes a <c>tcp.data</c> push: queues the bytes for the web view's connection.</summary>
	private void ApplyData( JsonObject frame )
	{
		if ( Frame.ReadLong( frame, "tcp" ) is not { } id || !_tunnels.TryGetValue( id, out var tunnel ) )
			return;

		try
		{
			tunnel.Incoming.Writer.TryWrite( Convert.FromBase64String( Frame.ReadString( frame, "data" ) ?? "" ) );
		}
		catch ( FormatException )
		{
			End( tunnel, isTold: false );
		}
	}

	/// <summary>Takes a <c>tcp.close</c> push: the host's service closed, so the web view's connection closes once the bytes before it are written.</summary>
	private void ApplyClose( JsonObject frame )
	{
		if ( Frame.ReadLong( frame, "tcp" ) is { } id && _tunnels.TryGetValue( id, out var tunnel ) )
			tunnel.Incoming.Writer.TryComplete();
	}

	/// <summary>Ends a stream once: closes the web view's connection and, unless the host already knows, tells it.</summary>
	private void End( Tunnel tunnel, bool isTold )
	{
		if ( !_tunnels.TryRemove( new KeyValuePair<long, Tunnel>( tunnel.Id, tunnel ) ) )
			return;

		tunnel.Closing.Cancel();
		tunnel.Incoming.Writer.TryComplete();
		tunnel.Network.Dispose();
		if ( isTold || tunnel.Peer.IsClosed )
			return;

		var close = Frame.Create( "tcp.close" );
		close["tcp"] = tunnel.Id;
		_ = SendCloseAsync( tunnel.Peer, close );
	}

	/// <summary>Sends <c>tcp.close</c>, ignoring a connection that closed.</summary>
	private async Task SendCloseAsync( RpcPeer peer, JsonObject close )
	{
		try
		{
			await peer.RequestAsync( close, _stopping.Token ).ConfigureAwait( false );
		}
		catch ( Exception exception ) when ( exception is IOException or RemoteException or OperationCanceledException or ObjectDisposedException )
		{
		}
	}

	/// <summary>The host connection new streams use.</summary>
	/// <param name="Peer">The peer.</param>
	/// <param name="CanControl">Whether the host lets this PC control.</param>
	private sealed record Binding( RpcPeer Peer, bool CanControl );

	/// <summary>One stream: the web view's connection, the peer it tunnels through, and the host's bytes waiting to be written.</summary>
	/// <param name="Id">The stream's id on the connection.</param>
	/// <param name="Peer">The peer it tunnels through.</param>
	/// <param name="Network">The web view's connection.</param>
	private sealed record Tunnel( long Id, RpcPeer Peer, NetworkStream Network )
	{
		/// <summary>Gets the host's bytes waiting to be written.</summary>
		public Channel<byte[]> Incoming { get; } = Channel.CreateUnbounded<byte[]>( new() { SingleReader = true, SingleWriter = true } );

		/// <summary>Gets the signal that ends the stream.</summary>
		public CancellationTokenSource Closing { get; } = new();
	}
}
