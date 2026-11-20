using System.Text.Json.Nodes;

namespace Lokn.Modules.Remote;

/// <summary>Carries whole frames between two peers; <see cref="RpcPeer"/> speaks the protocol over it.</summary>
/// <remarks>Sends are safe from any thread and keep their order; one reader at a time calls <see cref="ReceiveAsync"/>.</remarks>
public interface IFrameConnection : IAsyncDisposable
{
	/// <summary>Sends one frame.</summary>
	Task SendAsync( JsonObject frame );

	/// <summary>Receives the next frame, or null once the connection closed.</summary>
	/// <exception cref="InvalidDataException">The other side sent something that is not a frame.</exception>
	Task<JsonObject?> ReceiveAsync( CancellationToken cancellationToken );
}

/// <summary>One message of a message-based carrier: a text message holds plaintext JSON, a binary message holds an encrypted frame.</summary>
/// <param name="IsText">Whether it is a text message.</param>
/// <param name="Data">The message bytes.</param>
public sealed record CarrierMessage( bool IsText, byte[] Data );

/// <summary>A connection made of whole text and binary messages, such as a WebSocket; as an <see cref="IFrameConnection"/> it carries plaintext JSON text frames, and <see cref="SecureChannel"/> encrypts over it.</summary>
/// <remarks>
/// Outgoing messages queue for at most <see cref="MaxQueuedBytes"/>; a peer that stops reading overflows the queue, which aborts the connection rather
/// than letting the sender's memory grow. Incoming messages may take <see cref="MaxMessageBytes"/>, which the handshake lowers until it completes.
/// </remarks>
public abstract class MessageConnection : IFrameConnection
{
	/// <summary>The most bytes of outgoing messages that may wait to be sent before the connection is aborted.</summary>
	public const int MaxQueuedBytes = 4 * Frame.MaxBytes;

	/// <summary>Completes once <see cref="SecureChannel"/> finished the handshake over this connection.</summary>
	private readonly TaskCompletionSource _secured = new( TaskCreationOptions.RunContinuationsAsynchronously );

	/// <summary>Stores the bytes of the outgoing messages queued or being sent.</summary>
	private long _queuedBytes;

	/// <summary>Gets or sets the largest incoming message accepted; larger ones fail the receive with <see cref="InvalidDataException"/>.</summary>
	public int MaxMessageBytes { get; set; } = Frame.MaxBytes;

	/// <summary>Gets the other end's IP address for per-address limits, or null when the carrier has none, such as a relay session.</summary>
	public string? RemoteAddress
	{
		get; init;
	}

	/// <summary>Gets a task that completes once the secure handshake over this connection succeeded, and never when it fails.</summary>
	public Task Secured => _secured.Task;

	/// <summary>Sends one message.</summary>
	public abstract Task SendMessageAsync( CarrierMessage message );

	/// <summary>Receives the next message, or null once the connection closed.</summary>
	public abstract Task<CarrierMessage?> ReceiveMessageAsync( CancellationToken cancellationToken );

	/// <summary>Closes the connection; a pending receive on either side ends with null.</summary>
	public abstract ValueTask DisposeAsync();

	/// <summary>Drops the connection at once, without a close handshake; a pending receive ends with null.</summary>
	public virtual void Abort() => _ = DisposeAsync().AsTask();

	/// <summary>Sends one plaintext frame as a text message.</summary>
	public async Task SendAsync( JsonObject frame )
	{
		var data = Frame.Serialize( frame );
		ReserveSend( data.Length );
		try
		{
			await SendMessageAsync( new( true, data ) ).ConfigureAwait( false );
		}
		finally
		{
			ReleaseSend( data.Length );
		}
	}

	/// <summary>Receives the next plaintext frame, or null once the connection closed.</summary>
	/// <exception cref="InvalidDataException">The message is binary or not a frame.</exception>
	public async Task<JsonObject?> ReceiveAsync( CancellationToken cancellationToken )
	{
		var message = await ReceiveMessageAsync( cancellationToken ).ConfigureAwait( false );
		if ( message is null )
			return null;
		if ( !message.IsText )
			throw new InvalidDataException( "A plaintext frame arrived as a binary message." );
		return Frame.Parse( message.Data );
	}

	/// <summary>Counts an outgoing message into the queue; when the queue is full, the peer is not reading, so the connection is aborted.</summary>
	/// <exception cref="IOException">The queue is full.</exception>
	internal void ReserveSend( int bytes )
	{
		var queued = Interlocked.Add( ref _queuedBytes, bytes );
		// One message always fits, so a large answer to an idle peer still goes out.
		if ( queued > MaxQueuedBytes && queued != bytes )
		{
			Interlocked.Add( ref _queuedBytes, -bytes );
			Abort();
			throw new IOException( "The other side stopped reading; the connection was dropped." );
		}
	}

	/// <summary>Takes a sent or failed message out of the queue count.</summary>
	internal void ReleaseSend( int bytes ) => Interlocked.Add( ref _queuedBytes, -bytes );

	/// <summary>Records that the secure handshake completed, lifting the handshake's message limit.</summary>
	internal void MarkSecured()
	{
		MaxMessageBytes = Frame.MaxBytes;
		_secured.TrySetResult();
	}
}
