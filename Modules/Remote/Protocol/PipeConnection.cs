using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json.Nodes;

namespace Lokn.Modules.Remote;

/// <summary>Carries plaintext frames over a named pipe, either end: each frame is a 4-byte little-endian length and its UTF-8 JSON.</summary>
/// <param name="pipe">The connected pipe; the connection owns it.</param>
public sealed class PipeConnection( PipeStream pipe ) : IFrameConnection
{
	/// <summary>Keeps one frame's length and bytes together when several threads send.</summary>
	private readonly SemaphoreSlim _sending = new( 1, 1 );

	/// <summary>Records whether <see cref="DisposeAsync"/> ran, so a read it interrupts ends as a close.</summary>
	private volatile bool _disposed;

	/// <summary>Sends one frame.</summary>
	/// <exception cref="IOException">The pipe broke.</exception>
	public async Task SendAsync( JsonObject frame )
	{
		var body = Frame.Serialize( frame );
		if ( body.Length > Frame.MaxBytes )
			throw new InvalidDataException( "The frame is too large to send." );

		var data = new byte[4 + body.Length];
		BinaryPrimitives.WriteInt32LittleEndian( data, body.Length );
		body.CopyTo( data, 4 );
		await _sending.WaitAsync().ConfigureAwait( false );
		try
		{
			ObjectDisposedException.ThrowIf( _disposed, this );
			await pipe.WriteAsync( data ).ConfigureAwait( false );
			await pipe.FlushAsync().ConfigureAwait( false );
		}
		finally
		{
			_sending.Release();
		}
	}

	/// <summary>Receives the next frame, or null once the pipe closed.</summary>
	/// <exception cref="InvalidDataException">The length is out of range or the bytes are not a frame.</exception>
	public async Task<JsonObject?> ReceiveAsync( CancellationToken cancellationToken )
	{
		try
		{
			var header = new byte[4];
			await pipe.ReadExactlyAsync( header, cancellationToken ).ConfigureAwait( false );
			var length = BinaryPrimitives.ReadInt32LittleEndian( header );
			if ( length is <= 0 or > Frame.MaxBytes )
				throw new InvalidDataException( "A frame length is out of range." );

			var body = new byte[length];
			await pipe.ReadExactlyAsync( body, cancellationToken ).ConfigureAwait( false );
			return Frame.Parse( body );
		}
		catch ( EndOfStreamException )
		{
			return null;
		}
		catch ( Exception exception ) when ( _disposed && exception is IOException or ObjectDisposedException or OperationCanceledException )
		{
			return null;
		}
	}

	/// <summary>Closes the pipe; the other end reads the close.</summary>
	public async ValueTask DisposeAsync()
	{
		_disposed = true;
		await pipe.DisposeAsync().ConfigureAwait( false );
	}
}
