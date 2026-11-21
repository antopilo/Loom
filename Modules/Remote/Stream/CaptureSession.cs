using System.Diagnostics;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>Holds the <c>cap.start</c> options of one stream.</summary>
/// <param name="Fps">The most frames per second.</param>
/// <param name="Quality">The picture quality from 0 to 1: the JPEG quality, or the H.264 bitrate it sets.</param>
/// <param name="MaxWidth">The widest frame in pixels, such as the viewer's panel width in physical pixels; wider sources are scaled down, and 0 keeps the source's width.</param>
public sealed record CaptureOptions( int Fps, double Quality, int MaxWidth )
{
	/// <summary>Defines the most frames per second a stream may ask for.</summary>
	public const int MaxFps = 60;

	/// <summary>Defines the widest frame a stream sends.</summary>
	public const int MaxFrameWidth = 7680;

	/// <summary>Defines the tallest frame a stream sends.</summary>
	public const int MaxFrameHeight = 4320;

	/// <summary>Defines the narrowest frame cap a stream may ask for.</summary>
	public const int MinFrameWidth = 64;

	/// <summary>Defines the widest or tallest H.264 frame the host sends, 4096 pixels, which Media Foundation's encoders take.</summary>
	public const int MaxH264Side = 4096;

	/// <summary>Defines the most pixels in an H.264 frame: the largest frame of H.264 levels 5.1 and 5.2, 36,864 macroblocks, as 4096×2304.</summary>
	public const int MaxH264Pixels = 4096 * 2304;

	/// <summary>Defines <see cref="Codec"/> for one JPEG per frame, which every client decodes.</summary>
	public const string Jpeg = "jpeg";

	/// <summary>Defines <see cref="Codec"/> for an H.264 stream, which sends only what changed and is encoded by the GPU when it can.</summary>
	public const string H264 = "h264";

	/// <summary>Gets the options the stream panel starts with: 30 frames per second at quality 0.7, at the source's full size, as JPEG.</summary>
	public static CaptureOptions Default
	{
		get;
	} = new( 30, 0.7, 0 );

	/// <summary>Gets the tallest frame in pixels, such as the viewer's panel height in physical pixels; taller sources are scaled down, and 0 keeps the source's height.</summary>
	public int MaxHeight
	{
		get; init;
	}

	/// <summary>Gets the size in physical pixels a streamed screen should switch to, such as the viewer's own screen, or null to leave it as it is; windows ignore it.</summary>
	public (int Width, int Height)? Resolution
	{
		get; init;
	}

	/// <summary>Gets the codec the stream asks for, <see cref="Jpeg"/> or <see cref="H264"/>; a host that cannot encode H.264 sends JPEG.</summary>
	public string Codec
	{
		get; init;
	} = Jpeg;

	/// <summary>Returns these options brought into range: 1 to <see cref="MaxFps"/> frames per second, quality 0 to 1, size caps 0 (none) or
	/// <see cref="MinFrameWidth"/> to <see cref="MaxFrameWidth"/> and <see cref="MaxFrameHeight"/>, and a known codec.</summary>
	public CaptureOptions Clamp() => this with
	{
		Fps = Math.Clamp( Fps, 1, MaxFps ),
		Quality = double.IsFinite( Quality ) ? Math.Clamp( Quality, 0, 1 ) : Default.Quality,
		MaxWidth = ClampCap( MaxWidth, MaxFrameWidth ),
		MaxHeight = ClampCap( MaxHeight, MaxFrameHeight ),
		Codec = Codec == H264 ? H264 : Jpeg,
	};

	/// <summary>Brings a size cap into range: 0 or less means none.</summary>
	internal static int ClampCap( int cap, int most ) => cap <= 0 ? 0 : Math.Clamp( cap, MinFrameWidth, most );
}

/// <summary>Holds one encoded stream frame, the payload of <c>cap.frame</c>.</summary>
/// <param name="Width">The frame width in pixels, <c>w</c>.</param>
/// <param name="Height">The frame height in pixels, <c>h</c>.</param>
/// <param name="Data">The encoded bytes: a JPEG, or the H.264 Annex B bytes of one frame.</param>
/// <param name="Codec">The codec of <paramref name="Data"/>, <see cref="CaptureOptions.Jpeg"/> or <see cref="CaptureOptions.H264"/>.</param>
public sealed record StreamFrame( int Width, int Height, byte[] Data, string Codec = CaptureOptions.Jpeg );

/// <summary>Captures, encodes and emits frames of one source on a background task, following the <c>cap.frame</c> rules of docs/remote.md.</summary>
/// <remarks>
/// A frame is emitted at most <see cref="CaptureOptions.Fps"/> times per second, and only while fewer than <see cref="MaxInFlight"/> frames wait for
/// <see cref="Ack"/>, so the next frame is captured and sent while earlier ones travel, and a slow client or link still gets fewer frames instead of a
/// backlog. A capture identical to the last emitted frame is skipped.
/// The frame callback runs on the capture task; it must not block.
/// </remarks>
public sealed class CaptureSession : IDisposable
{
	/// <summary>Defines how many emitted frames may wait for their acknowledgement at once; more than one keeps the link's round trip out of the frame time.</summary>
	public const int MaxInFlight = 3;

	/// <summary>Stores the source of images; the session disposes it.</summary>
	private readonly IWindowCapturer _capturer;

	/// <summary>Stores the clamped options.</summary>
	private readonly CaptureOptions _options;

	/// <summary>Stores the JPEG encoder: <see cref="JpegEncoder"/> unless a test supplies another.</summary>
	private readonly Func<BgraFrame, double, CancellationToken, Task<byte[]>> _encode;

	/// <summary>Stores whether frames go out as H.264: the stream asked for it and no test encoder was given; false once H.264 failed, for the rest of the stream.</summary>
	private bool _isH264;

	/// <summary>Stores the H.264 encoder for the current frame size, or null before the first frame and after a size change.</summary>
	private H264Encoder? _h264;

	/// <summary>Stores the callback for each emitted frame.</summary>
	private readonly Action<StreamFrame> _emit;

	/// <summary>Stores the callback for a failure that ends the session, such as the window closing.</summary>
	private readonly Action<Exception>? _failed;

	/// <summary>Stops the capture task.</summary>
	private readonly CancellationTokenSource _stop = new();

	/// <summary>Stores the capture task, or null before <see cref="Start"/>.</summary>
	private Task? _task;

	/// <summary>Counts the frames that may still be sent before an acknowledgement: <see cref="MaxInFlight"/> less the frames waiting for one.</summary>
	private readonly SemaphoreSlim _credits = new( MaxInFlight, MaxInFlight );

	/// <summary>Stores the size of the last emitted frame, packed as width in the high and height in the low 32 bits.</summary>
	private long _frameSize;

	/// <summary>Stores the frame size cap, packed as the most width in the high and the most height in the low 32 bits; 0 on an axis means none.</summary>
	private long _limit;

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
		_isH264 = encode is null && _options.Codec == CaptureOptions.H264 && OperatingSystem.IsWindows();
		_limit = Pack( _options.MaxWidth, _options.MaxHeight );
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

	/// <summary>Changes the frame size cap, such as when the viewer's panel was resized (<c>cap.view</c>); the next capture uses it.</summary>
	/// <remarks>Safe on any thread.</remarks>
	/// <param name="maxWidth">The widest frame in pixels, or 0 for none.</param>
	/// <param name="maxHeight">The tallest frame in pixels, or 0 for none.</param>
	public void Limit( int maxWidth, int maxHeight ) =>
		Interlocked.Exchange( ref _limit, Pack( CaptureOptions.ClampCap( maxWidth, CaptureOptions.MaxFrameWidth ), CaptureOptions.ClampCap( maxHeight, CaptureOptions.MaxFrameHeight ) ) );

	/// <summary>Computes the width to capture a source at: the source's own (0) unless the size cap or the codec's largest frame needs it smaller, keeping
	/// its aspect ratio; never larger than the source.</summary>
	/// <param name="sourceWidth">The source width in pixels, or 0 when unknown.</param>
	/// <param name="sourceHeight">The source height in pixels, or 0 when unknown.</param>
	/// <param name="maxWidth">The widest frame asked for, or 0 for none.</param>
	/// <param name="maxHeight">The tallest frame asked for, or 0 for none.</param>
	/// <param name="isH264">Whether frames are encoded as H.264, whose frames are at most <see cref="CaptureOptions.MaxH264Side"/> a side and
	/// <see cref="CaptureOptions.MaxH264Pixels"/> in all.</param>
	/// <returns>The width to pass to <see cref="IWindowCapturer.Capture"/>, 0 for the source's own.</returns>
	internal static int ComputeCaptureWidth( int sourceWidth, int sourceHeight, int maxWidth, int maxHeight, bool isH264 )
	{
		if ( sourceWidth <= 0 || sourceHeight <= 0 )
			return maxWidth;

		var scale = 1.0;
		if ( maxWidth > 0 )
			scale = Math.Min( scale, (double)maxWidth / sourceWidth );
		if ( maxHeight > 0 )
			scale = Math.Min( scale, (double)maxHeight / sourceHeight );
		var side = isH264 ? CaptureOptions.MaxH264Side : int.MaxValue;
		scale = Math.Min( scale, Math.Min( (double)Math.Min( side, CaptureOptions.MaxFrameWidth ) / sourceWidth, (double)Math.Min( side, CaptureOptions.MaxFrameHeight ) / sourceHeight ) );
		if ( isH264 )
			scale = Math.Min( scale, Math.Sqrt( (double)CaptureOptions.MaxH264Pixels / ((long)sourceWidth * sourceHeight) ) );

		// The small margin keeps a scale such as 4096 / 7680 from flooring to one pixel less.
		return scale >= 1 ? 0 : Math.Max( 1, (int)Math.Floor( sourceWidth * scale + 1e-6 ) );
	}

	/// <summary>Packs a width and a height into one value.</summary>
	private static long Pack( int width, int height ) => ((long)width << 32) | (uint)height;

	/// <summary>Acknowledges the oldest outstanding frame, which lets another be sent; an acknowledgement with no frame outstanding is ignored.</summary>
	/// <remarks>Safe on any thread.</remarks>
	public void Ack()
	{
		try
		{
			_credits.Release();
		}
		catch ( SemaphoreFullException )
		{
			// No frame is outstanding.
		}
	}

	/// <summary>Stops capturing; the capture task ends at its next wait and releases the capturer.</summary>
	public void Dispose()
	{
		if ( _stop.IsCancellationRequested )
			return;

		_stop.Cancel();
		if ( _task is null )
			_capturer.Dispose();
	}

	/// <summary>Captures, skips unchanged images, waits for a free slot, encodes and emits, paced by the frame rate.</summary>
	private async Task RunAsync( CancellationToken stop )
	{
		var interval = TimeSpan.FromSeconds( 1.0 / _options.Fps );
		BgraFrame? previous = null;
		var timing = new StreamTiming( _capturer.GetType().Name, _options );
		try
		{
			while ( !stop.IsCancellationRequested )
			{
				// The slot is taken before the capture, so the frame sent is the newest image, not one that waited for the link.
				var waited = Stopwatch.GetTimestamp();
				await _credits.WaitAsync( stop );
				var started = Stopwatch.GetTimestamp();
				timing.AddWait( started - waited );
				var isSent = false;
				var limit = Interlocked.Read( ref _limit );
				var (sourceWidth, sourceHeight) = _capturer.SourceSize;
				var frame = _capturer.Capture( ComputeCaptureWidth( sourceWidth, sourceHeight, (int)(limit >> 32), (int)limit, _isH264 ) );
				var captured = Stopwatch.GetTimestamp();
				timing.AddCapture( captured - started );
				if ( frame is not null )
				{
					var isChanged = !IsSame( frame, previous );
					// Always the latest capture, never an older one: a capturer may reuse the buffer of the capture before last.
					previous = frame;
					if ( isChanged )
					{
						var encoded = await EncodeAsync( frame, stop );
						timing.AddFrame( Stopwatch.GetTimestamp() - captured, encoded.Data.Length, encoded.Width, encoded.Height );
						if ( encoded.Data.Length > 0 )
						{
							Interlocked.Exchange( ref _frameSize, ((long)encoded.Width << 32) | (uint)encoded.Height );
							_emit( encoded );
							isSent = true;
						}
					}
				}
				if ( !isSent )
					_credits.Release();

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
			_h264?.Dispose();
		}
	}

	/// <summary>Encodes a frame as H.264 while that works, else as JPEG.</summary>
	/// <remarks>An H.264 encoder serves one size, so a new one starts, with a key frame, when the window's size changes.</remarks>
	private async Task<StreamFrame> EncodeAsync( BgraFrame frame, CancellationToken stop )
	{
		if ( _isH264 )
		{
			try
			{
				if ( _h264 is { } current && (current.Width != (frame.Width & ~1) || current.Height != (frame.Height & ~1)) )
				{
					current.Dispose();
					_h264 = null;
				}

				_h264 ??= new H264Encoder( frame.Width, frame.Height, _options.Fps, ComputeBitrate( frame.Width, frame.Height ) );
				return new( _h264.Width, _h264.Height, _h264.Encode( frame ), CaptureOptions.H264 );
			}
			catch ( Exception error ) when ( error is NotSupportedException or System.Runtime.InteropServices.COMException or DllNotFoundException or InvalidCastException )
			{
				// No encoder, or one that failed mid-stream: JPEG frames carry on, and the client decodes each by its codec.
				_h264?.Dispose();
				_h264 = null;
				_isH264 = false;
			}
		}

		return new( frame.Width, frame.Height, await _encode( frame, _options.Quality, stop ), CaptureOptions.Jpeg );
	}

	/// <summary>Computes the H.264 bitrate from the size, frame rate and quality: 0.05 to 0.2 bits per pixel per frame.</summary>
	private int ComputeBitrate( int width, int height ) => (int)Math.Clamp( width * (long)height * _options.Fps * (0.05 + 0.15 * _options.Quality), 500_000, 50_000_000 );

	/// <summary>Reports whether a capture has the same size and pixels as the previous capture.</summary>
	internal static bool IsSame( BgraFrame frame, BgraFrame? previous ) =>
		previous is not null
		&& previous.Width == frame.Width
		&& previous.Height == frame.Height
		&& frame.Pixels.AsSpan().SequenceEqual( previous.Pixels );
}

/// <summary>Sums the time each step of a stream takes and writes it to <c>%TEMP%\Lokn\stream.log</c> every five seconds, to find what limits the frame rate.</summary>
internal sealed class StreamTiming( string capturer, CaptureOptions options )
{
	/// <summary>Defines how often a line is written.</summary>
	private static readonly long Period = Stopwatch.Frequency * 5;

	/// <summary>Serializes writes from concurrent streams.</summary>
	private static readonly Lock Gate = new();

	/// <summary>Stores when the current period started.</summary>
	private long _start = Stopwatch.GetTimestamp();

	/// <summary>Stores the period's summed ticks and bytes.</summary>
	private long _wait, _capture, _encode, _bytes;

	/// <summary>Stores the period's counts and the last frame size.</summary>
	private int _loops, _frames, _width, _height;

	/// <summary>Adds the time spent waiting for a frame acknowledgement.</summary>
	public void AddWait( long ticks ) => _wait += ticks;

	/// <summary>Adds one capture, sent or not.</summary>
	public void AddCapture( long ticks )
	{
		_capture += ticks;
		_loops++;
		Flush();
	}

	/// <summary>Adds one encoded and sent frame.</summary>
	public void AddFrame( long encodeTicks, int bytes, int width, int height )
	{
		_encode += encodeTicks;
		_bytes += bytes;
		_frames++;
		(_width, _height) = (width, height);
	}

	/// <summary>Writes a line when a period ended, then starts the next.</summary>
	private void Flush()
	{
		var now = Stopwatch.GetTimestamp();
		if ( now - _start < Period )
			return;

		var seconds = (double)(now - _start) / Stopwatch.Frequency;
		static double Ms( long ticks, int count ) => count == 0 ? 0 : ticks * 1000.0 / Stopwatch.Frequency / count;
		var line = $"{DateTime.Now:HH:mm:ss} {capturer} {_width}x{_height} cap {options.Fps}fps: sent {_frames / seconds:0.0}fps of {_loops / seconds:0.0} captures/s"
			+ $" | capture {Ms( _capture, _loops ):0.0}ms encode {Ms( _encode, _frames ):0.0}ms ack-wait {Ms( _wait, _loops ):0.0}ms | {(_frames == 0 ? 0 : _bytes / _frames / 1024)}KB/frame {_bytes * 8 / seconds / 1e6:0.0}Mbit/s";
		try
		{
			var directory = LoknPaths.TempRoot;
			Directory.CreateDirectory( directory );
			lock ( Gate )
				File.AppendAllText( Path.Combine( directory, "stream.log" ), line + Environment.NewLine );
		}
		catch ( IOException )
		{
			// Timing is diagnostic; a locked or full disk only loses the line.
		}

		_start = now;
		_wait = _capture = _encode = _bytes = 0;
		_loops = _frames = 0;
	}
}
