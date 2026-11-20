using System.Diagnostics;

namespace Lokn.Modules.Remote;

/// <summary>Holds the <c>cap.start</c> options of one stream.</summary>
/// <param name="Fps">The most frames per second.</param>
/// <param name="Quality">The JPEG quality from 0 to 1.</param>
/// <param name="MaxWidth">The widest frame in pixels; wider windows are scaled down.</param>
public sealed record CaptureOptions( int Fps, double Quality, int MaxWidth )
{
	/// <summary>Defines the most frames per second a stream may ask for.</summary>
	public const int MaxFps = 60;

	/// <summary>Defines the widest frame a stream may ask for.</summary>
	public const int MaxFrameWidth = 7680;

	/// <summary>Defines the narrowest frame a stream may ask for.</summary>
	public const int MinFrameWidth = 64;

	/// <summary>Gets the options the stream panel starts with: 15 frames per second at quality 0.7, at most 1600 pixels wide.</summary>
	public static CaptureOptions Default
	{
		get;
	} = new( 15, 0.7, 1600 );

	/// <summary>Returns these options brought into range: 1 to <see cref="MaxFps"/> frames per second, quality 0 to 1, width <see cref="MinFrameWidth"/> to <see cref="MaxFrameWidth"/>.</summary>
	public CaptureOptions Clamp() => new( Math.Clamp( Fps, 1, MaxFps ), double.IsFinite( Quality ) ? Math.Clamp( Quality, 0, 1 ) : Default.Quality, Math.Clamp( MaxWidth, MinFrameWidth, MaxFrameWidth ) );
}

/// <summary>Holds one encoded stream frame, the payload of <c>cap.frame</c>.</summary>
/// <param name="Width">The frame width in pixels, <c>w</c>.</param>
/// <param name="Height">The frame height in pixels, <c>h</c>.</param>
/// <param name="Jpeg">The JPEG bytes, sent as base64 <c>jpeg</c>.</param>
public sealed record StreamFrame( int Width, int Height, byte[] Jpeg );

/// <summary>Captures, encodes and emits frames of one source on a background task, following the <c>cap.frame</c> rules of docs/remote.md.</summary>
/// <remarks>
/// A frame is emitted at most <see cref="CaptureOptions.Fps"/> times per second, and only after <see cref="Ack"/> answered the previous one,
/// so a slow client or link gets fewer frames instead of a backlog. A capture identical to the last emitted frame is skipped.
/// The frame callback runs on the capture task; it must not block.
/// </remarks>
public sealed class CaptureSession : IDisposable
{
	/// <summary>Stores the source of images; the session disposes it.</summary>
	private readonly IWindowCapturer _capturer;

	/// <summary>Stores the clamped options.</summary>
	private readonly CaptureOptions _options;

	/// <summary>Stores the encoder: JPEG through <see cref="JpegEncoder"/> unless a test supplies another.</summary>
	private readonly Func<BgraFrame, double, CancellationToken, Task<byte[]>> _encode;

	/// <summary>Stores the callback for each emitted frame.</summary>
	private readonly Action<StreamFrame> _emit;

	/// <summary>Stores the callback for a failure that ends the session, such as the window closing.</summary>
	private readonly Action<Exception>? _failed;

	/// <summary>Stops the capture task.</summary>
	private readonly CancellationTokenSource _stop = new();

	/// <summary>Stores the capture task, or null before <see cref="Start"/>.</summary>
	private Task? _task;

	/// <summary>Completes when the client acknowledges the outstanding frame; null while no frame is outstanding.</summary>
	private TaskCompletionSource? _pendingAck;

	/// <summary>Stores the size of the last emitted frame, packed as width in the high and height in the low 32 bits.</summary>
	private long _frameSize;

	/// <summary>Creates a session over a capturer; call <see cref="Start"/> to begin.</summary>
	/// <param name="capturer">The image source, such as a <see cref="WindowCapture"/>; the session disposes it.</param>
	/// <param name="options">The stream options, clamped into range.</param>
	/// <param name="emit">Receives each frame on the capture task.</param>
	/// <param name="failed">Receives the error that ended the session, such as the window closing; null ignores it.</param>
	/// <param name="encode">Encodes a frame at a quality; null uses <see cref="JpegEncoder.EncodeAsync"/>.</param>
	public CaptureSession( IWindowCapturer capturer, CaptureOptions options, Action<StreamFrame> emit, Action<Exception>? failed = null, Func<BgraFrame, double, CancellationToken, Task<byte[]>>? encode = null )
	{
		_capturer = capturer;
		_options = options.Clamp();
		_emit = emit;
		_failed = failed;
		_encode = encode ?? JpegEncoder.EncodeAsync;
	}

	/// <summary>Gets the size of the last emitted frame, which <c>cap.input</c> coordinates refer to, or (0, 0) before the first.</summary>
	public (int Width, int Height) FrameSize
	{
		get
		{
			var packed = Interlocked.Read( ref _frameSize );
			return ((int)(packed >> 32), (int)packed);
		}
	}

	/// <summary>Gets the options in effect, after clamping.</summary>
	public CaptureOptions Options => _options;

	/// <summary>Starts the capture task; later calls do nothing.</summary>
	public void Start() => _task ??= Task.Run( () => RunAsync( _stop.Token ) );

	/// <summary>Acknowledges the outstanding frame, which lets the next one be sent; an acknowledgement with no frame outstanding is ignored.</summary>
	/// <remarks>Safe on any thread.</remarks>
	public void Ack() => Interlocked.Exchange( ref _pendingAck, null )?.TrySetResult();

	/// <summary>Stops capturing; the capture task ends at its next wait and releases the capturer.</summary>
	public void Dispose()
	{
		if ( _stop.IsCancellationRequested )
			return;

		_stop.Cancel();
		if ( _task is null )
			_capturer.Dispose();
	}

	/// <summary>Captures, skips unchanged images, encodes, emits and waits for the acknowledgement, paced by the frame rate.</summary>
	private async Task RunAsync( CancellationToken stop )
	{
		var interval = TimeSpan.FromSeconds( 1.0 / _options.Fps );
		BgraFrame? previous = null;
		try
		{
			while ( !stop.IsCancellationRequested )
			{
				var started = Stopwatch.GetTimestamp();
				if ( _capturer.Capture( _options.MaxWidth ) is { } frame )
				{
					var isChanged = !IsSame( frame, previous );
					// Always the latest capture, never an older one: a capturer may reuse the buffer of the capture before last.
					previous = frame;
					if ( isChanged )
					{
						var jpeg = await _encode( frame, _options.Quality, stop );
						var ack = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
						Volatile.Write( ref _pendingAck, ack );
						Interlocked.Exchange( ref _frameSize, ((long)frame.Width << 32) | (uint)frame.Height );
						_emit( new( frame.Width, frame.Height, jpeg ) );
						await ack.Task.WaitAsync( stop );
					}
				}

				var remaining = interval - Stopwatch.GetElapsedTime( started );
				if ( remaining > TimeSpan.Zero )
					await Task.Delay( remaining, stop );
			}
		}
		catch ( OperationCanceledException ) when ( stop.IsCancellationRequested )
		{
			// Stopped.
		}
		catch ( Exception error )
		{
			_failed?.Invoke( error );
		}
		finally
		{
			_capturer.Dispose();
		}
	}

	/// <summary>Reports whether a capture has the same size and pixels as the previous capture.</summary>
	internal static bool IsSame( BgraFrame frame, BgraFrame? previous ) =>
		previous is not null
		&& previous.Width == frame.Width
		&& previous.Height == frame.Height
		&& frame.Pixels.AsSpan().SequenceEqual( previous.Pixels );
}
