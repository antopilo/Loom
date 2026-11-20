using System.Net.WebSockets;

namespace Lokn.Modules.Remote;

/// <summary>Carries messages over a WebSocket, either end: plaintext text frames during the handshake, then <see cref="SecureChannel"/>'s encrypted binary frames.</summary>
/// <param name="socket">The open WebSocket; the connection owns it.</param>
/// <param name="peer">Describes the other end, such as its address, for logs and the device list.</param>
public sealed class WebSocketConnection( WebSocket socket, string peer ) : MessageConnection
{
	/// <summary>Bounds how long a close handshake may take before the socket is aborted.</summary>
	private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds( 2 );

	/// <summary>Bounds how long a message may wait for earlier ones and then take to send; a peer slower than this is dropped.</summary>
	public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds( 60 );

	/// <summary>Keeps sends whole and in order; a WebSocket allows one send at a time.</summary>
	private readonly SemaphoreSlim _sending = new( 1, 1 );

	/// <summary>Records whether <see cref="DisposeAsync"/> ran, so a receive it interrupts ends as a close.</summary>
	private volatile bool _disposed;

	/// <summary>Gets the other end's description, such as its address.</summary>
	public string Peer => peer;

	/// <summary>Gets the WebSocket, for carriers such as the relay that copy its bytes.</summary>
	public WebSocket Socket => socket;

	/// <summary>Sends one message within <see cref="SendTimeout"/>, aborting the socket when the peer does not take it in time.</summary>
	/// <exception cref="WebSocketException">The socket broke.</exception>
	/// <exception cref="IOException">The send timed out and the socket was aborted.</exception>
	public override async Task SendMessageAsync( CarrierMessage message )
	{
		using var timeout = new CancellationTokenSource( SendTimeout );
		try
		{
			await _sending.WaitAsync( timeout.Token ).ConfigureAwait( false );
		}
		catch ( OperationCanceledException exception )
		{
			Abort();
			throw new IOException( "The other side stopped reading; the connection was dropped.", exception );
		}

		try
		{
			ObjectDisposedException.ThrowIf( _disposed, this );
			await socket.SendAsync( message.Data, message.IsText ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, timeout.Token ).ConfigureAwait( false );
		}
		catch ( OperationCanceledException exception ) when ( timeout.IsCancellationRequested )
		{
			Abort();
			throw new IOException( "The other side stopped reading; the connection was dropped.", exception );
		}
		finally
		{
			_sending.Release();
		}
	}

	/// <summary>Aborts the socket at once.</summary>
	public override void Abort() => socket.Abort();

	/// <summary>Receives the next whole message, or null once the socket closed.</summary>
	/// <exception cref="InvalidDataException">The message is larger than <see cref="MessageConnection.MaxMessageBytes"/>.</exception>
	public override async Task<CarrierMessage?> ReceiveMessageAsync( CancellationToken cancellationToken )
	{
		try
		{
			using var buffer = new MemoryStream();
			var chunk = new byte[16 * 1024];
			while ( true )
			{
				var result = await socket.ReceiveAsync( chunk.AsMemory(), cancellationToken ).ConfigureAwait( false );
				if ( result.MessageType == WebSocketMessageType.Close )
				{
					await CloseAsync().ConfigureAwait( false );
					return null;
				}

				if ( buffer.Length + result.Count > MaxMessageBytes )
					throw new InvalidDataException( "A message is too large." );
				buffer.Write( chunk, 0, result.Count );
				if ( result.EndOfMessage )
					return new( result.MessageType == WebSocketMessageType.Text, buffer.ToArray() );
			}
		}
		catch ( WebSocketException ) when ( _disposed || socket.State is not WebSocketState.Open )
		{
			return null;
		}
		catch ( Exception exception ) when ( _disposed && exception is ObjectDisposedException or OperationCanceledException )
		{
			return null;
		}
	}

	/// <summary>Closes the socket, politely when it is still open.</summary>
	public override async ValueTask DisposeAsync()
	{
		if ( _disposed )
			return;
		_disposed = true;
		await CloseAsync().ConfigureAwait( false );
		socket.Dispose();
	}

	/// <summary>Sends or answers the close handshake within <see cref="CloseTimeout"/>, after any send in progress, then aborts whatever is left.</summary>
	private async Task CloseAsync()
	{
		if ( socket.State is WebSocketState.Open or WebSocketState.CloseReceived && await _sending.WaitAsync( CloseTimeout ).ConfigureAwait( false ) )
		{
			try
			{
				using var timeout = new CancellationTokenSource( CloseTimeout );
				await socket.CloseOutputAsync( WebSocketCloseStatus.NormalClosure, null, timeout.Token ).ConfigureAwait( false );
			}
			catch ( Exception exception ) when ( exception is WebSocketException or OperationCanceledException or ObjectDisposedException )
			{
			}
			finally
			{
				_sending.Release();
			}
		}
		socket.Abort();
	}
}
