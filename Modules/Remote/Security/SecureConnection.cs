using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Lokn.Modules.Remote;

/// <summary>Carries frames encrypted with the keys of a <see cref="SecureChannel"/> handshake, as binary messages over a <see cref="MessageConnection"/>.</summary>
/// <remarks>
/// Each message is <c>counter (8 bytes, little-endian) ‖ AES-256-GCM ciphertext ‖ tag (16)</c>, with nonce <c>4 zero bytes ‖ counter</c>.
/// Counters start at 0 per direction and increase by one; a message with another counter, a failed decrypt, or a text message closes the connection.
/// </remarks>
public sealed class SecureConnection : IFrameConnection
{
	/// <summary>The size of the counter prefix.</summary>
	private const int CounterSize = 8;

	/// <summary>The size of the GCM tag suffix.</summary>
	private const int TagSize = 16;

	/// <summary>The size of the GCM nonce.</summary>
	private const int NonceSize = 12;

	/// <summary>Stores the carrier; this connection owns it.</summary>
	private readonly MessageConnection _carrier;

	/// <summary>Encrypts outgoing frames.</summary>
	private readonly AesGcm _sealing;

	/// <summary>Decrypts incoming frames.</summary>
	private readonly AesGcm _opening;

	/// <summary>Keeps counters and messages in the same order when several threads send.</summary>
	private readonly SemaphoreSlim _sending = new( 1, 1 );

	/// <summary>Stores the next outgoing counter.</summary>
	private ulong _sendCounter;

	/// <summary>Stores the counter the next incoming message must carry.</summary>
	private ulong _receiveCounter;

	/// <summary>Creates the connection over a carrier with the derived keys of its direction.</summary>
	/// <param name="carrier">The carrier the handshake ran over.</param>
	/// <param name="sendKey">The 32-byte key of the outgoing direction.</param>
	/// <param name="receiveKey">The 32-byte key of the incoming direction.</param>
	internal SecureConnection( MessageConnection carrier, byte[] sendKey, byte[] receiveKey )
	{
		_carrier = carrier;
		_sealing = new( sendKey, TagSize );
		_opening = new( receiveKey, TagSize );
	}

	/// <summary>Gets why the connection closed itself, such as a failed decrypt, or null.</summary>
	public string? CloseReason
	{
		get; private set;
	}

	/// <summary>Gets the carrier underneath.</summary>
	public MessageConnection Carrier => _carrier;

	/// <summary>Encrypts and sends one frame.</summary>
	public async Task SendAsync( JsonObject frame )
	{
		var plaintext = Frame.Serialize( frame );
		if ( plaintext.Length > Frame.MaxBytes - CounterSize - TagSize )
			throw new InvalidDataException( "The frame is too large to send." );

		var message = new byte[CounterSize + plaintext.Length + TagSize];
		_carrier.ReserveSend( message.Length );
		try
		{
			await _sending.WaitAsync().ConfigureAwait( false );
			try
			{
				var counter = _sendCounter++;
				BinaryPrimitives.WriteUInt64LittleEndian( message, counter );
				_sealing.Encrypt( BuildNonce( counter ), plaintext, message.AsSpan( CounterSize, plaintext.Length ), message.AsSpan( CounterSize + plaintext.Length ) );
				await _carrier.SendMessageAsync( new( false, message ) ).ConfigureAwait( false );
			}
			finally
			{
				_sending.Release();
			}
		}
		finally
		{
			_carrier.ReleaseSend( message.Length );
		}
	}

	/// <summary>Receives and decrypts the next frame, or null once the connection closed, including when this message broke the rules and closed it.</summary>
	public async Task<JsonObject?> ReceiveAsync( CancellationToken cancellationToken )
	{
		if ( CloseReason is not null )
			return null;

		var message = await _carrier.ReceiveMessageAsync( cancellationToken ).ConfigureAwait( false );
		if ( message is null )
			return null;
		if ( message.IsText || message.Data.Length < CounterSize + TagSize )
			return await FailAsync( "A message is not an encrypted frame." ).ConfigureAwait( false );

		var counter = BinaryPrimitives.ReadUInt64LittleEndian( message.Data );
		if ( counter != _receiveCounter )
			return await FailAsync( $"A message carries counter {counter} where {_receiveCounter} was due." ).ConfigureAwait( false );

		var length = message.Data.Length - CounterSize - TagSize;
		var plaintext = new byte[length];
		try
		{
			_opening.Decrypt( BuildNonce( counter ), message.Data.AsSpan( CounterSize, length ), message.Data.AsSpan( CounterSize + length ), plaintext );
		}
		catch ( AuthenticationTagMismatchException )
		{
			return await FailAsync( "A message failed to decrypt." ).ConfigureAwait( false );
		}
		_receiveCounter++;

		try
		{
			return Frame.Parse( plaintext );
		}
		catch ( InvalidDataException )
		{
			return await FailAsync( "A decrypted message is not a frame." ).ConfigureAwait( false );
		}
	}

	/// <summary>Closes the carrier and releases the keys.</summary>
	public async ValueTask DisposeAsync()
	{
		await _carrier.DisposeAsync().ConfigureAwait( false );
		_sealing.Dispose();
		_opening.Dispose();
	}

	/// <summary>Builds the nonce: 4 zero bytes, then the counter in little-endian order as the message carries it.</summary>
	private static byte[] BuildNonce( ulong counter )
	{
		var nonce = new byte[NonceSize];
		BinaryPrimitives.WriteUInt64LittleEndian( nonce.AsSpan( 4 ), counter );
		return nonce;
	}

	/// <summary>Records why the connection broke and closes the carrier, so both sides see the close.</summary>
	private async Task<JsonObject?> FailAsync( string reason )
	{
		CloseReason = reason;
		await _carrier.DisposeAsync().ConfigureAwait( false );
		return null;
	}
}
