using System.Buffers;
using System.Net.WebSockets;

namespace Lokn.Relay;

/// <summary>Copies WebSocket messages both ways between two sockets without reading them.</summary>
internal static class RelayPipe
{
	/// <summary>Defines how long the relay waits for a peer to answer a close frame before it drops the connection.</summary>
	internal static readonly TimeSpan CloseWait = TimeSpan.FromSeconds( 5 );

	/// <summary>Defines the size of each direction's copy buffer in bytes.</summary>
	private const int BufferSize = 16 * 1024;

	/// <summary>Copies messages between <paramref name="first"/> and <paramref name="second"/> until either closes, then closes the other.</summary>
	internal static async Task RunAsync( WebSocket first, WebSocket second )
	{
		var forward = PumpAsync( first, second );
		var backward = PumpAsync( second, first );
		var ended = await Task.WhenAny( forward, backward );
		// A clean close travels through the other pump as its peer answers; give that a moment.
		if ( ended.IsCompletedSuccessfully )
			await Task.WhenAny( ended == forward ? backward : forward, Task.Delay( CloseWait ) );
		if ( !forward.IsCompletedSuccessfully || !backward.IsCompletedSuccessfully )
		{
			first.Abort();
			second.Abort();
		}
		try
		{
			await Task.WhenAll( forward, backward );
		}
		catch ( Exception exception ) when ( exception is WebSocketException or OperationCanceledException or ObjectDisposedException )
		{
			// A dropped connection ends the pipe; both sockets are already aborted.
		}
	}

	/// <summary>Closes <paramref name="socket"/> with <paramref name="status"/>, or drops it when the peer does not answer in time.</summary>
	internal static async Task CloseAsync( WebSocket socket, WebSocketCloseStatus status, string? reason )
	{
		using var timeout = new CancellationTokenSource( CloseWait );
		try
		{
			if ( socket.State == WebSocketState.Open )
				await socket.CloseAsync( status, reason, timeout.Token );
			else if ( socket.State == WebSocketState.CloseReceived )
				await socket.CloseOutputAsync( status, reason, timeout.Token );
		}
		catch ( Exception exception ) when ( exception is WebSocketException or OperationCanceledException or ObjectDisposedException )
		{
			socket.Abort();
		}
	}

	/// <summary>Copies frames from <paramref name="source"/> to <paramref name="target"/> with their type and message boundaries, and passes on the close.</summary>
	private static async Task PumpAsync( WebSocket source, WebSocket target )
	{
		var buffer = ArrayPool<byte>.Shared.Rent( BufferSize );
		try
		{
			while ( true )
			{
				var result = await source.ReceiveAsync( buffer.AsMemory(), CancellationToken.None );
				if ( result.MessageType == WebSocketMessageType.Close )
				{
					if ( target.State is WebSocketState.Open or WebSocketState.CloseReceived )
					{
						// 1005 (no status) may not be sent, so a close without a status passes on as a normal close.
						var status = source.CloseStatus ?? WebSocketCloseStatus.Empty;
						var hasStatus = status != WebSocketCloseStatus.Empty;
						await target.CloseOutputAsync( hasStatus ? status : WebSocketCloseStatus.NormalClosure, hasStatus ? source.CloseStatusDescription : null, CancellationToken.None );
					}
					return;
				}
				await target.SendAsync( buffer.AsMemory( 0, result.Count ), result.MessageType, result.EndOfMessage, CancellationToken.None );
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return( buffer );
		}
	}
}
