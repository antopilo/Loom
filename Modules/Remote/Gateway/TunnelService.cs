using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Lokn.Modules.Remote;

/// <summary>Tunnels TCP from a paired PC's browser to the host's own loopback services, such as a dev server on <c>localhost:5173</c>: the
/// <c>tcp.open</c>, <c>tcp.data</c>, <c>tcp.eof</c> and <c>tcp.close</c> requests, the <c>tcp.data</c> and <c>tcp.close</c> pushes and the client's
/// <c>tcp.ack</c> pushes (docs/remote.md, Localhost forwarding).</summary>
/// <remarks>
/// Every frame needs <c>control</c>. A stream connects only to a loopback address of the host, never to the ports <paramref name="deniedPorts"/> names,
/// which are Loom's own gateway; a connection holds at most <see cref="MaxStreams"/> streams, counting those still connecting, and each sends at most
/// <see cref="WindowBytes"/> the client has not acknowledged. Streams end when their connection closes.
/// </remarks>
/// <param name="deniedPorts">Gets the ports no stream may reach: the gateway's WebSocket port.</param>
internal sealed class TunnelService( Func<IReadOnlyCollection<int>> deniedPorts ) : IHostService
{
	/// <summary>The most streams one connection holds at once.</summary>
	public const int MaxStreams = 64;

	/// <summary>The most bytes one stream sends before the client acknowledges them.</summary>
	public const int WindowBytes = 512 * 1024;

	/// <summary>The most bytes one <c>tcp.data</c> frame carries.</summary>
	public const int ChunkBytes = 64 * 1024;

	/// <summary>The most pushes of one connection waiting to be sent; a connection further behind is closed.</summary>
	private const int MaxPendingPushes = 4096;

	/// <summary>Defines how long connecting to a loopback port may take.</summary>
	private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds( 10 );

	/// <summary>Registers the tunnel handlers for one connection.</summary>
	public void Register( RemoteConnection connection )
	{
		var session = new Session( connection, deniedPorts );
		connection.Peer
			.On( "tcp.open", session.OpenAsync )
			.On( "tcp.data", session.WriteAsync )
			.On( "tcp.eof", session.EndWriteAsync )
			.On( "tcp.close", session.CloseAsync )
			.OnPush( "tcp.ack", session.Acknowledge );
		connection.Peer.Closed += _ => session.Dispose();
	}

	/// <summary>Returns the loopback addresses a <c>tcp.open</c> host names: <c>localhost</c> or a loopback IP address, by default 127.0.0.1.</summary>
	/// <exception cref="RemoteException">The host is not a loopback name or address (<c>scope</c>).</exception>
	public static IReadOnlyList<IPAddress> ResolveLoopback( string? host )
	{
		if ( host is null or "localhost" )
			return host is null ? [IPAddress.Loopback] : [IPAddress.Loopback, IPAddress.IPv6Loopback];

		if ( IPAddress.TryParse( host.Trim( '[', ']' ), out var address ) && IPAddress.IsLoopback( address ) )
			return [address];

		throw new RemoteException( RemoteErrors.Scope, "The host opens tunnels only to its own loopback addresses." );
	}

	/// <summary>One connection's streams.</summary>
	/// <param name="connection">The connection.</param>
	/// <param name="deniedPorts">Gets the ports no stream may reach.</param>
	private sealed class Session( RemoteConnection connection, Func<IReadOnlyCollection<int>> deniedPorts ) : IDisposable
	{
		/// <summary>Holds the open streams by the client's id.</summary>
		private readonly ConcurrentDictionary<long, Stream> _streams = new();

		/// <summary>Holds the ids of streams still connecting, which count against <see cref="MaxStreams"/>; under <see cref="_gate"/>.</summary>
		private readonly HashSet<long> _opening = [];

		/// <summary>Guards the check and reservation of a stream's place.</summary>
		private readonly Lock _gate = new();

		/// <summary>Sends the data and close pushes in order.</summary>
		private readonly PushQueue _pushes = new( connection.Peer, MaxPendingPushes );

		/// <summary>Ends every stream when the connection closes.</summary>
		private readonly CancellationTokenSource _closing = new();

		/// <summary>Answers <c>tcp.open { tcp, port, host? }</c>: reserves the stream's place, connects to the host's loopback port and starts sending
		/// what it says.</summary>
		public async Task<JsonObject?> OpenAsync( JsonObject frame )
		{
			connection.RequireControl();
			var id = WorkspaceFrames.ReadRequiredLong( frame, "tcp" );
			var port = WorkspaceFrames.ReadRequiredLong( frame, "port" );
			if ( port is < 1 or > 65535 )
				throw new RemoteException( RemoteErrors.BadRequest, "The port is not valid." );
			var addresses = ResolveLoopback( Frame.ReadString( frame, "host" ) );
			if ( deniedPorts().Contains( (int)port ) )
				throw new RemoteException( RemoteErrors.Scope, "The tunnel may not reach Loom's own gateway." );

			// The place is taken before connecting, which may take seconds, so streams opened together cannot pass the limit.
			lock ( _gate )
			{
				if ( _streams.ContainsKey( id ) || _opening.Contains( id ) )
					throw new RemoteException( RemoteErrors.BadRequest, "The stream id is in use." );
				if ( _streams.Count + _opening.Count >= MaxStreams )
					throw new RemoteException( RemoteErrors.Busy, $"A connection may tunnel at most {MaxStreams} streams at once." );
				_opening.Add( id );
			}

			Stream stream;
			try
			{
				stream = new Stream( id, await ConnectAsync( addresses, (int)port ).ConfigureAwait( false ) );
			}
			catch
			{
				lock ( _gate )
					_opening.Remove( id );
				throw;
			}

			lock ( _gate )
			{
				_opening.Remove( id );
				if ( _closing.IsCancellationRequested || !_streams.TryAdd( id, stream ) )
				{
					stream.Dispose();
					throw new RemoteException( RemoteErrors.Failed, "The connection closed." );
				}
			}
			_ = PumpAsync( stream );
			return null;
		}

		/// <summary>Answers <c>tcp.data { tcp, data }</c>: writes the client's bytes to the stream, answering once they were written.</summary>
		public async Task<JsonObject?> WriteAsync( JsonObject frame )
		{
			connection.RequireControl();
			var stream = Find( frame );
			var data = WorkspaceFrames.ReadData( frame, "data" );
			try
			{
				await stream.Client.GetStream().WriteAsync( data, _closing.Token ).ConfigureAwait( false );
			}
			catch ( Exception exception ) when ( exception is IOException or SocketException or ObjectDisposedException or InvalidOperationException )
			{
				End( stream );
				throw new RemoteException( RemoteErrors.Failed, "The stream closed." );
			}
			return null;
		}

		/// <summary>Answers <c>tcp.eof { tcp }</c>: the client sent all it will, so the service sees the end of its input while its answer still flows back.</summary>
		public Task<JsonObject?> EndWriteAsync( JsonObject frame )
		{
			connection.RequireControl();
			var stream = Find( frame );
			try
			{
				stream.Client.Client.Shutdown( SocketShutdown.Send );
			}
			catch ( Exception exception ) when ( exception is SocketException or ObjectDisposedException )
			{
				End( stream );
				throw new RemoteException( RemoteErrors.Failed, "The stream closed." );
			}
			return Task.FromResult<JsonObject?>( null );
		}

		/// <summary>Answers <c>tcp.close { tcp }</c>: closes the stream; an unknown one is closed already.</summary>
		public Task<JsonObject?> CloseAsync( JsonObject frame )
		{
			if ( _streams.TryRemove( WorkspaceFrames.ReadRequiredLong( frame, "tcp" ), out var stream ) )
				stream.Dispose();
			return Task.FromResult<JsonObject?>( null );
		}

		/// <summary>Takes the client's <c>tcp.ack { tcp, bytes }</c>: it wrote that many more bytes, so the stream may send more.</summary>
		public void Acknowledge( JsonObject frame )
		{
			if ( Frame.ReadLong( frame, "tcp" ) is { } id && Frame.ReadLong( frame, "bytes" ) is { } bytes && _streams.TryGetValue( id, out var stream ) )
				stream.Acknowledge( bytes );
		}

		/// <summary>Ends every stream.</summary>
		public void Dispose()
		{
			_closing.Cancel();
			foreach ( var stream in _streams.Values )
				stream.Dispose();
			_streams.Clear();
		}

		/// <summary>Finds the stream a frame names.</summary>
		private Stream Find( JsonObject frame ) => _streams.TryGetValue( WorkspaceFrames.ReadRequiredLong( frame, "tcp" ), out var stream )
			? stream
			: throw new RemoteException( RemoteErrors.Failed, "The stream closed." );

		/// <summary>Connects to the first loopback address that answers.</summary>
		private async Task<TcpClient> ConnectAsync( IReadOnlyList<IPAddress> addresses, int port )
		{
			var failures = new List<string>();
			foreach ( var address in addresses )
			{
				var client = new TcpClient( address.AddressFamily ) { NoDelay = true };
				using var timeout = CancellationTokenSource.CreateLinkedTokenSource( _closing.Token );
				timeout.CancelAfter( ConnectTimeout );
				try
				{
					await client.ConnectAsync( address, port, timeout.Token ).ConfigureAwait( false );
					return client;
				}
				catch ( Exception exception ) when ( exception is SocketException or OperationCanceledException )
				{
					client.Dispose();
					failures.Add( $"{address}: {exception.Message}" );
				}
			}
			throw new RemoteException( RemoteErrors.Failed, $"Nothing answers on port {port} of the host. {string.Join( "; ", failures )}" );
		}

		/// <summary>Sends what the host's service says as <c>tcp.data</c> pushes, never more than <see cref="WindowBytes"/> ahead of the client's
		/// acknowledgements, and <c>tcp.close</c> once it closes.</summary>
		private async Task PumpAsync( Stream stream )
		{
			var buffer = new byte[ChunkBytes];
			try
			{
				var network = stream.Client.GetStream();
				while ( true )
				{
					await stream.WaitForWindowAsync( _closing.Token ).ConfigureAwait( false );
					var read = await network.ReadAsync( buffer, _closing.Token ).ConfigureAwait( false );
					if ( read == 0 )
						break;

					stream.Spend( read );
					var push = Frame.Create( "tcp.data" );
					push["tcp"] = stream.Id;
					push["data"] = Convert.ToBase64String( buffer, 0, read );
					_pushes.Send( push );
				}
			}
			catch ( Exception exception ) when ( exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException )
			{
				// The service, the client or the connection closed the stream.
			}
			End( stream );
		}

		/// <summary>Closes a stream and tells the client, once.</summary>
		private void End( Stream stream )
		{
			if ( !_streams.TryRemove( new KeyValuePair<long, Stream>( stream.Id, stream ) ) )
				return;

			stream.Dispose();
			var push = Frame.Create( "tcp.close" );
			push["tcp"] = stream.Id;
			_pushes.Send( push );
		}
	}

	/// <summary>One tunnelled TCP stream and the bytes it sent that the client has not acknowledged.</summary>
	/// <param name="id">The client's id of the stream.</param>
	/// <param name="client">The connection to the host's service.</param>
	private sealed class Stream( long id, TcpClient client ) : IDisposable
	{
		/// <summary>Guards <see cref="_unacknowledged"/> and <see cref="_room"/>.</summary>
		private readonly Lock _gate = new();

		/// <summary>Stores the bytes sent and not acknowledged.</summary>
		private long _unacknowledged;

		/// <summary>Completes when the window has room again.</summary>
		private TaskCompletionSource _room = CreateRoom( isOpen: true );

		/// <summary>Gets the client's id of the stream.</summary>
		public long Id => id;

		/// <summary>Gets the connection to the host's service.</summary>
		public TcpClient Client => client;

		/// <summary>Waits until fewer than <see cref="WindowBytes"/> sent bytes wait for acknowledgement.</summary>
		public Task WaitForWindowAsync( CancellationToken cancellationToken )
		{
			lock ( _gate )
				return _room.Task.WaitAsync( cancellationToken );
		}

		/// <summary>Counts bytes sent, closing the window once it is full.</summary>
		public void Spend( int bytes )
		{
			lock ( _gate )
			{
				_unacknowledged += bytes;
				if ( _unacknowledged >= WindowBytes && _room.Task.IsCompleted )
					_room = CreateRoom( isOpen: false );
			}
		}

		/// <summary>Counts bytes the client wrote, opening the window once it has room.</summary>
		public void Acknowledge( long bytes )
		{
			TaskCompletionSource? opened = null;
			lock ( _gate )
			{
				_unacknowledged = Math.Max( 0, _unacknowledged - bytes );
				if ( _unacknowledged < WindowBytes && !_room.Task.IsCompleted )
					opened = _room;
			}
			opened?.TrySetResult();
		}

		/// <summary>Closes the connection to the service.</summary>
		public void Dispose()
		{
			client.Dispose();
			lock ( _gate )
				_room.TrySetResult();
		}

		/// <summary>Creates the signal of room in the window, already set when <paramref name="isOpen"/>.</summary>
		private static TaskCompletionSource CreateRoom( bool isOpen )
		{
			var room = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
			if ( isOpen )
				room.TrySetResult();
			return room;
		}
	}
}
