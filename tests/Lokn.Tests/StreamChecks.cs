using System.Diagnostics;
using System.Text.Json;
using Goo;
using Lokn.Modules.Remote;
using StreamContent = Lokn.Modules.Remote.StreamContent;
using WindowCapture = Lokn.Modules.Remote.WindowCapture;

namespace Lokn.Tests;

/// <summary>Exercises the window stream: the window list, JPEG encoding, frame pacing by acknowledgement, and the pointer and key mapping, without opening a window.</summary>
internal static class StreamChecks
{
	/// <summary>Defines how long a check waits for the capture task, in milliseconds.</summary>
	private const int WaitMilliseconds = 3000;

	/// <summary>Defines how long a check watches for frames that must not come, in milliseconds.</summary>
	private const int QuietMilliseconds = 250;

	/// <summary>Defines the wire options the stream frames use.</summary>
	private static readonly JsonSerializerOptions Wire = new( JsonSerializerDefaults.Web );

	/// <summary>Runs the stream checks.</summary>
	internal static void Run()
	{
		CheckWindows();
		CheckWire();
		CheckJpeg();
		CheckAckGating();
		CheckFrameRate();
		CheckMapping();
		Console.WriteLine( "PASS window stream: window list, cap.* field names, JPEG round trip, frames paced by acknowledgement and frame rate with unchanged frames skipped, and frame-to-window input mapping." );
	}

	/// <summary>Verifies the window list on a desktop session, and reports a skip where there is no desktop, such as in CI.</summary>
	private static void CheckWindows()
	{
		var windows = WindowCatalog.List();
		if ( windows.Count == 0 )
		{
			var isCi = Environment.GetEnvironmentVariable( "CI" ) is { Length: > 0 } || Environment.GetEnvironmentVariable( "GITHUB_ACTIONS" ) is { Length: > 0 };
			CoreChecks.Check( isCi || !Environment.UserInteractive, "A desktop session lists at least one window to stream." );
			Console.WriteLine( "SKIP window stream: no visible top-level windows in this session (no desktop, as in CI); the window list was not checked." );
			return;
		}

		CoreChecks.Check( windows.All( window => window.Hwnd != 0 && window.Title.Length > 0 && window.Width > 0 && window.Height > 0 ), "Every listed window has a handle, a title and a size." );
		CoreChecks.Check( windows.Select( window => window.Hwnd ).Distinct().Count() == windows.Count, "Each window is listed once." );
	}

	/// <summary>Verifies the stream records use the field names of docs/remote.md.</summary>
	private static void CheckWire()
	{
		var window = JsonSerializer.SerializeToElement( new CaptureWindow( 42, "Editor", "sbox", 1280, 720 ), Wire );
		CoreChecks.Check( window.GetProperty( "hwnd" ).GetInt64() == 42 && window.GetProperty( "title" ).GetString() == "Editor" && window.GetProperty( "process" ).GetString() == "sbox"
			&& window.GetProperty( "w" ).GetInt32() == 1280 && window.GetProperty( "h" ).GetInt32() == 720, "A listed window serializes as { hwnd, title, process, w, h }." );

		var down = JsonSerializer.Deserialize<StreamInput>( """{ "kind": "down", "x": 10.5, "y": 3, "button": 2 }""", Wire )!;
		CoreChecks.Check( down is { Kind: StreamInput.Down, X: 10.5, Y: 3, Button: 2 }, "A cap.input press reads its kind, position and button." );
		var key = JsonSerializer.Deserialize<StreamInput>( """{ "kind": "key", "vk": 65, "down": true }""", Wire )!;
		CoreChecks.Check( key is { Kind: StreamInput.Key, Vk: 65, IsDown: true }, "A cap.input key reads its virtual key and down." );
		CoreChecks.Check( JsonSerializer.SerializeToElement( key, Wire ).GetProperty( "down" ).GetBoolean(), "A key event writes its state as down." );

		var options = new CaptureOptions( 500, 3, 10 ).Clamp();
		CoreChecks.Check( options is { Fps: CaptureOptions.MaxFps, Quality: 1, MaxWidth: CaptureOptions.MinFrameWidth }, "cap.start options are brought into range." );
	}

	/// <summary>Verifies a synthetic BGRA frame encodes to JPEG and decodes back, through Windows and through Goo, at its size and close to its colors.</summary>
	private static void CheckJpeg()
	{
		const int width = 96;
		const int height = 54;
		var pixels = new byte[width * height * 4];
		for ( var y = 0; y < height; y++ )
		{
			for ( var x = 0; x < width; x++ )
			{
				var offset = (y * width + x) * 4;
				pixels[offset] = (byte)(x * 255 / (width - 1));
				pixels[offset + 1] = (byte)(y * 255 / (height - 1));
				pixels[offset + 2] = 128;
				pixels[offset + 3] = 255;
			}
		}

		var jpeg = JpegEncoder.EncodeAsync( new( width, height, pixels ), 0.9 ).GetAwaiter().GetResult();
		CoreChecks.Check( jpeg.Length > 2 && jpeg[0] == 0xFF && jpeg[1] == 0xD8, "A frame encodes as JPEG." );
		var small = JpegEncoder.EncodeAsync( new( width, height, pixels ), 0.1 ).GetAwaiter().GetResult();
		CoreChecks.Check( small.Length < jpeg.Length, "A lower quality encodes smaller." );

		var decoded = JpegEncoder.DecodeAsync( jpeg ).GetAwaiter().GetResult();
		CoreChecks.Check( decoded.Width == width && decoded.Height == height && decoded.Pixels.Length == pixels.Length, "The JPEG decodes back at the frame's size." );
		var center = (height / 2 * width + width / 2) * 4;
		CoreChecks.Check( Enumerable.Range( 0, 3 ).All( channel => Math.Abs( decoded.Pixels[center + channel] - pixels[center + channel] ) <= 16 ), "The decoded colors are close to the frame's." );

		using var image = ImageSource.Decode( jpeg );
		CoreChecks.Check( image.Width == width && image.Height == height, "Goo decodes the JPEG the panel shows at the frame's size." );
	}

	/// <summary>Verifies one frame goes out until it is acknowledged, a stray acknowledgement does not open the gate, unchanged captures are skipped, and a capture failure ends the session.</summary>
	private static void CheckAckGating()
	{
		var capturer = new FakeCapturer();
		var frames = new List<StreamFrame>();
		Exception? failure = null;
		var session = new CaptureSession( capturer, new( CaptureOptions.MaxFps, 0.5, 640 ), frame =>
		{
			lock ( frames )
				frames.Add( frame );
		}, error => failure = error, ( frame, _, _ ) => Task.FromResult( new[] { frame.Pixels[0] } ) );

		int Count()
		{
			lock ( frames )
				return frames.Count;
		}

		// No frame is outstanding yet, so this acknowledgement must not let a second frame through later.
		session.Ack();
		session.Start();
		CoreChecks.Check( WaitFor( () => Count() == 1 ), "The first frame goes out without an acknowledgement." );
		var captures = capturer.Captures;
		Thread.Sleep( QuietMilliseconds );
		CoreChecks.Check( Count() == 1 && capturer.Captures == captures, "Without an acknowledgement no further frame is captured or sent." );
		CoreChecks.Check( session.FrameSize == (FakeCapturer.Width, FakeCapturer.Height), "The session reports the size of the frame sent, for input mapping." );

		session.Ack();
		CoreChecks.Check( WaitFor( () => Count() == 2 ), "An acknowledgement lets the next frame out." );
		lock ( frames )
			CoreChecks.Check( frames[1].Jpeg[0] != frames[0].Jpeg[0], "Each frame carries its own capture." );

		capturer.IsChanging = false;
		session.Ack();
		captures = capturer.Captures;
		CoreChecks.Check( WaitFor( () => capturer.Captures >= captures + 3 ), "Capturing continues after an acknowledgement." );
		CoreChecks.Check( Count() == 2, "Unchanged captures are not sent." );
		var settled = Count();
		Thread.Sleep( QuietMilliseconds );
		CoreChecks.Check( Count() == settled, "An unchanged window sends nothing." );

		capturer.Error = new InvalidOperationException( "The window was closed." );
		CoreChecks.Check( WaitFor( () => failure is not null && capturer.IsDisposed ), "A capture failure ends the session, reports it, and releases the capturer." );
		session.Dispose();
	}

	/// <summary>Verifies an acknowledged stream stays under its frame rate.</summary>
	private static void CheckFrameRate()
	{
		const int fps = 10;
		var capturer = new FakeCapturer();
		var count = 0;
		CaptureSession? session = null;
		session = new CaptureSession( capturer, new( fps, 0.5, 640 ), _ =>
		{
			Interlocked.Increment( ref count );
			session?.Ack();
		}, encode: ( frame, _, _ ) => Task.FromResult( new[] { frame.Pixels[0] } ) );
		var timer = Stopwatch.StartNew();
		session.Start();
		Thread.Sleep( 1000 );
		var sent = Volatile.Read( ref count );
		var elapsed = timer.Elapsed.TotalSeconds;
		session.Dispose();
		CoreChecks.Check( sent >= 3 && sent <= Math.Ceiling( elapsed * fps ) + 1, $"An acknowledged stream sends at most its frame rate ({sent} frames in {elapsed:0.00} s at {fps} fps)." );
		CoreChecks.Check( WaitFor( () => capturer.IsDisposed ), "Stopping a session releases its capturer." );
	}

	/// <summary>Verifies frame pixels map to client and screen pixels, the panel maps its surface to frame pixels, and keys and buttons get their Windows codes.</summary>
	private static void CheckMapping()
	{
		CoreChecks.Check( WindowCapture.ComputeScaledSize( 3840, 2160, 1600 ) == (1600, 900) && WindowCapture.ComputeScaledSize( 800, 600, 1600 ) == (800, 600), "A wide window is scaled to the width cap; a narrow one keeps its size." );

		CoreChecks.Check( InputInjector.ComputeClientPoint( 800, 450, 1600, 900, 3840, 2160 ) == (1920, 1080), "A frame point scales up to the window's client pixels." );
		CoreChecks.Check( InputInjector.ComputeClientPoint( 0, 0, 1600, 900, 3840, 2160 ) == (0, 0) && InputInjector.ComputeClientPoint( 1599.9, 899.9, 1600, 900, 3840, 2160 ) == (3839, 2159), "Frame corners land on the client corners." );
		CoreChecks.Check( InputInjector.ComputeClientPoint( -20, 5000, 1600, 900, 3840, 2160 ) == (0, 2159), "Points outside the frame are clamped into the client area." );

		CoreChecks.Check( InputInjector.ComputeAbsolute( 0, 0, 0, 0, 1920, 1080 ) == (0, 0) && InputInjector.ComputeAbsolute( 1919, 1079, 0, 0, 1920, 1080 ) == (65535, 65535), "Screen corners map to the ends of SendInput's range." );
		var (middleX, middleY) = InputInjector.ComputeAbsolute( 0, 540, -1920, 0, 3840, 1080 );
		CoreChecks.Check( Math.Abs( middleX - 32768 ) <= 16 && Math.Abs( middleY - 32768 ) <= 32, "A desktop with a monitor left of the primary maps the primary's left edge to the middle." );

		CoreChecks.Check( InputInjector.ComputeButtonFlags( 0, true ) == (0x0002u, 0) && InputInjector.ComputeButtonFlags( 2, false ) == (0x0010u, 0) && InputInjector.ComputeButtonFlags( 4, true ) == (0x0080u, 2) && InputInjector.ComputeButtonFlags( 9, true ) is null,
			"DOM buttons map to their Windows mouse flags." );

		var fit = StreamContent.ComputeFit( 1000, 500, 1600, 900 );
		CoreChecks.Check( Math.Abs( fit.Scale - 500.0 / 900 ) < 1e-9 && Math.Abs( fit.Left - (1000 - 1600 * fit.Scale) / 2 ) < 1e-9 && fit.Top == 0, "A frame is scaled to fit the surface and centered." );
		CoreChecks.Check( fit.ComputeFramePoint( 500, 250, 1600, 900, clamp: false ) is { } middle && Math.Abs( middle.X - 800 ) < 1e-6 && Math.Abs( middle.Y - 450 ) < 1e-6, "The surface's center is the frame's center." );
		CoreChecks.Check( fit.ComputeFramePoint( 10, 250, 1600, 900, clamp: false ) is null, "A point beside the frame sends nothing." );
		CoreChecks.Check( fit.ComputeFramePoint( 10, 250, 1600, 900, clamp: true ) is { X: 0 }, "A release beside the frame lands on its edge." );

		CoreChecks.Check( StreamContent.ComputeVirtualKey( Key.A ) == 0x41 && StreamContent.ComputeVirtualKey( Key.Z ) == 0x5A && StreamContent.ComputeVirtualKey( Key.Number7 ) == 0x37
			&& StreamContent.ComputeVirtualKey( Key.F5 ) == 0x74 && StreamContent.ComputeVirtualKey( Key.Keypad3 ) == 0x63 && StreamContent.ComputeVirtualKey( Key.Left ) == 0x25
			&& StreamContent.ComputeVirtualKey( Key.ControlRight ) == 0xA3 && StreamContent.ComputeVirtualKey( Key.Unknown ) == 0, "Keys map to Windows virtual keys." );
		CoreChecks.Check( StreamContent.ComputeButton( PointerButton.Secondary ) == 2 && StreamContent.ComputeButton( PointerButton.None ) is null, "Panel buttons map to DOM button numbers." );
	}

	/// <summary>Waits until a condition holds or the wait runs out, and reports whether it held.</summary>
	private static bool WaitFor( Func<bool> condition )
	{
		var timer = Stopwatch.StartNew();
		while ( !condition() )
		{
			if ( timer.ElapsedMilliseconds > WaitMilliseconds )
				return false;

			Thread.Sleep( 5 );
		}

		return true;
	}

	/// <summary>Returns small frames whose first byte changes with every capture while <see cref="IsChanging"/> is set.</summary>
	private sealed class FakeCapturer : IWindowCapturer
	{
		/// <summary>Defines the frame width.</summary>
		internal const int Width = 4;

		/// <summary>Defines the frame height.</summary>
		internal const int Height = 2;

		/// <summary>Stores the value the next frame is filled with.</summary>
		private int _value;

		/// <summary>Stores the capture count.</summary>
		private int _captures;

		/// <summary>Gets the capture count.</summary>
		internal int Captures => Volatile.Read( ref _captures );

		/// <summary>Gets or sets whether each capture differs from the one before.</summary>
		internal volatile bool IsChanging = true;

		/// <summary>Gets or sets an error the next capture throws, or null.</summary>
		internal volatile Exception? Error;

		/// <summary>Gets whether the session released the capturer.</summary>
		internal volatile bool IsDisposed;

		/// <summary>Gets the frame size.</summary>
		public (int Width, int Height) SourceSize => (Width, Height);

		/// <summary>Returns a new frame, filled with a new value while changing.</summary>
		public BgraFrame? Capture( int maxWidth )
		{
			if ( Error is { } error )
				throw error;

			Interlocked.Increment( ref _captures );
			if ( IsChanging )
				_value++;

			var pixels = new byte[Width * Height * 4];
			Array.Fill( pixels, (byte)_value );
			return new( Width, Height, pixels );
		}

		/// <summary>Records the release.</summary>
		public void Dispose() => IsDisposed = true;
	}
}
