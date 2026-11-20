using System.Runtime.InteropServices;

namespace Lokn.Modules.Remote;

/// <summary>Holds one captured image as 32-bit BGRA pixels, rows top first with no padding.</summary>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
/// <param name="Pixels">The pixels, <c>Width * Height * 4</c> bytes; alpha is not meaningful.</param>
public sealed record BgraFrame( int Width, int Height, byte[] Pixels );

/// <summary>Captures images of one source for a <see cref="CaptureSession"/>.</summary>
public interface IWindowCapturer : IDisposable
{
	/// <summary>Gets the size, in physical pixels, of what the capturer captures before scaling, or (0, 0) when there is nothing to capture.</summary>
	(int Width, int Height) SourceSize
	{
		get;
	}

	/// <summary>Captures the current image, scaled down to at most <paramref name="maxWidth"/> pixels wide, or returns null when there is nothing to capture, such as a minimized or closed window.</summary>
	/// <remarks>The returned pixels stay unchanged until the capture after the next one, so a caller can compare two frames in a row.</remarks>
	/// <param name="maxWidth">The widest image to return; 0 or less keeps the source size.</param>
	BgraFrame? Capture( int maxWidth );
}

/// <summary>Captures one window's client area with GDI: <c>PrintWindow</c> with full-content rendering, else a copy of the screen where the window is.</summary>
/// <remarks>Uses one thread at a time. GDI surfaces are kept between captures and rebuilt when the size changes.</remarks>
public sealed class WindowCapture : IWindowCapturer
{
	/// <summary>Defines how many points per side the blank check samples.</summary>
	private const int BlankSamples = 16;

	/// <summary>Stores the captured window.</summary>
	private readonly nint _window;

	/// <summary>Stores the two output buffers captures alternate between, so the previous frame stays readable.</summary>
	private readonly byte[]?[] _buffers = new byte[2][];

	/// <summary>Stores the full-size surface the window draws into.</summary>
	private Surface? _source;

	/// <summary>Stores the scaled surface, when frames are narrower than the window.</summary>
	private Surface? _scaled;

	/// <summary>Stores which output buffer the next capture fills.</summary>
	private int _next;

	/// <summary>Creates a capturer for a window and restores the window without activating it when it is minimized, since a minimized window has nothing to capture.</summary>
	/// <param name="window">The top-level window handle, such as one from <see cref="WindowCatalog.List"/>.</param>
	public WindowCapture( nint window )
	{
		_window = window;
		if ( StreamNative.IsIconic( window ) )
			_ = StreamNative.ShowWindow( window, StreamNative.ShowNoActivate );
	}

	/// <summary>Gets the window's client size in physical pixels, or (0, 0) when it is closed or minimized.</summary>
	public (int Width, int Height) SourceSize
	{
		get
		{
			using var dpi = StreamNative.EnterPhysicalPixels();
			return ReadClientSize();
		}
	}

	/// <summary>Captures the client area, scaled down to <paramref name="maxWidth"/>.</summary>
	/// <exception cref="InvalidOperationException">The window no longer exists.</exception>
	public BgraFrame? Capture( int maxWidth )
	{
		if ( !StreamNative.IsWindow( _window ) )
			throw new InvalidOperationException( "The window was closed." );

		using var dpi = StreamNative.EnterPhysicalPixels();
		var (width, height) = ReadClientSize();
		if ( width <= 0 || height <= 0 )
			return null;

		var source = UpdateSurface( ref _source, width, height );
		var isDrawn = StreamNative.PrintWindow( _window, source.Dc, StreamNative.PrintClientOnly | StreamNative.PrintRenderFullContent );
		_ = StreamNative.GdiFlush();
		// Some windows ignore PrintWindow and leave the surface black; what is on screen is the next best thing.
		if ( !isDrawn || IsBlank( source ) )
			CopyScreen( source );

		var (frameWidth, frameHeight) = ComputeScaledSize( width, height, maxWidth );
		var output = source;
		if ( frameWidth != width )
		{
			output = UpdateSurface( ref _scaled, frameWidth, frameHeight );
			_ = StreamNative.SetStretchBltMode( output.Dc, StreamNative.HalftoneMode );
			_ = StreamNative.SetBrushOrgEx( output.Dc, 0, 0, 0 );
			_ = StreamNative.StretchBlt( output.Dc, 0, 0, frameWidth, frameHeight, source.Dc, 0, 0, width, height, StreamNative.SourceCopy );
			_ = StreamNative.GdiFlush();
		}

		var length = frameWidth * frameHeight * 4;
		var pixels = _buffers[_next];
		if ( pixels is null || pixels.Length != length )
			_buffers[_next] = pixels = new byte[length];

		_next ^= 1;
		Marshal.Copy( output.Bits, pixels, 0, length );
		return new( frameWidth, frameHeight, pixels );
	}

	/// <summary>Releases the GDI surfaces.</summary>
	public void Dispose()
	{
		_source?.Dispose();
		_scaled?.Dispose();
		_source = null;
		_scaled = null;
	}

	/// <summary>Computes the frame size for a source scaled down to fit a maximum width, keeping its aspect ratio; a source already narrow enough keeps its size.</summary>
	/// <param name="width">The source width in pixels.</param>
	/// <param name="height">The source height in pixels.</param>
	/// <param name="maxWidth">The widest frame; 0 or less keeps the source size.</param>
	public static (int Width, int Height) ComputeScaledSize( int width, int height, int maxWidth )
	{
		if ( maxWidth <= 0 || width <= maxWidth )
			return (width, height);

		return (maxWidth, Math.Max( 1, (int)Math.Round( (double)height * maxWidth / width ) ));
	}

	/// <summary>Reads the client size in the current DPI context, or (0, 0) when the window is closed or minimized.</summary>
	private (int Width, int Height) ReadClientSize()
	{
		if ( StreamNative.IsIconic( _window ) || !StreamNative.GetClientRect( _window, out var rect ) )
			return (0, 0);

		return (rect.Right - rect.Left, rect.Bottom - rect.Top);
	}

	/// <summary>Copies the window's client area from the screen into a surface.</summary>
	private void CopyScreen( Surface target )
	{
		var origin = new StreamNative.NativePoint();
		if ( !StreamNative.ClientToScreen( _window, ref origin ) )
			return;

		var screen = StreamNative.GetDC( 0 );
		try
		{
			_ = StreamNative.BitBlt( target.Dc, 0, 0, target.Width, target.Height, screen, origin.X, origin.Y, StreamNative.SourceCopy | StreamNative.CaptureLayered );
			_ = StreamNative.GdiFlush();
		}
		finally
		{
			_ = StreamNative.ReleaseDC( 0, screen );
		}
	}

	/// <summary>Reports whether a surface is black at every sampled point, which is how a window that ignores PrintWindow comes back.</summary>
	private static bool IsBlank( Surface surface )
	{
		for ( var row = 0; row < BlankSamples; row++ )
		{
			var y = (int)((long)surface.Height * row / BlankSamples);
			for ( var column = 0; column < BlankSamples; column++ )
			{
				var x = (int)((long)surface.Width * column / BlankSamples);
				// Alpha is ignored: PrintWindow leaves it 0 or 255 depending on the window.
				if ( (Marshal.ReadInt32( surface.Bits, (y * surface.Width + x) * 4 ) & 0x00FFFFFF) != 0 )
					return false;
			}
		}

		return true;
	}

	/// <summary>Returns a surface of the given size, reusing the current one when it already fits.</summary>
	private static Surface UpdateSurface( ref Surface? surface, int width, int height )
	{
		if ( surface is { } current && current.Width == width && current.Height == height )
			return current;

		surface?.Dispose();
		surface = Surface.Create( width, height );
		return surface;
	}

	/// <summary>Owns a memory device context with a top-down 32-bit bitmap selected into it.</summary>
	/// <param name="Dc">The memory device context.</param>
	/// <param name="Bitmap">The DIB section.</param>
	/// <param name="Previous">The bitmap the device context held before, restored on dispose.</param>
	/// <param name="Bits">The pixels of the DIB section.</param>
	/// <param name="Width">The width in pixels.</param>
	/// <param name="Height">The height in pixels.</param>
	private sealed record Surface( nint Dc, nint Bitmap, nint Previous, nint Bits, int Width, int Height ) : IDisposable
	{
		/// <summary>Creates a surface compatible with the screen.</summary>
		/// <exception cref="InvalidOperationException">GDI could not create it, such as when out of resources.</exception>
		public static Surface Create( int width, int height )
		{
			var info = new StreamNative.BitmapInfo
			{
				Size = Marshal.SizeOf<StreamNative.BitmapInfo>() - sizeof( int ),
				Width = width,
				Height = -height,
				Planes = 1,
				BitCount = 32,
			};
			var screen = StreamNative.GetDC( 0 );
			try
			{
				var dc = StreamNative.CreateCompatibleDC( screen );
				var bitmap = StreamNative.CreateDIBSection( screen, ref info, StreamNative.RgbColors, out var bits, 0, 0 );
				if ( dc == 0 || bitmap == 0 || bits == 0 )
				{
					if ( bitmap != 0 )
						_ = StreamNative.DeleteObject( bitmap );

					if ( dc != 0 )
						_ = StreamNative.DeleteDC( dc );

					throw new InvalidOperationException( $"Could not create a {width}x{height} capture surface." );
				}

				return new( dc, bitmap, StreamNative.SelectObject( dc, bitmap ), bits, width, height );
			}
			finally
			{
				_ = StreamNative.ReleaseDC( 0, screen );
			}
		}

		/// <summary>Releases the bitmap and device context.</summary>
		public void Dispose()
		{
			_ = StreamNative.SelectObject( Dc, Previous );
			_ = StreamNative.DeleteObject( Bitmap );
			_ = StreamNative.DeleteDC( Dc );
		}
	}
}
