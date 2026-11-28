using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace Lokn.Modules.Remote;

/// <summary>Captures one window's client area with Windows.Graphics.Capture: the compositor hands over each new frame on the GPU, so the window is never asked to draw again as <c>PrintWindow</c> does.</summary>
/// <remarks>
/// Frames arrive on a free-threaded pool; <see cref="Capture"/> takes the newest and returns the previous image while nothing changed, which the session then skips.
/// The window frame is cropped away, so frames and <c>cap.input</c> coordinates match <see cref="WindowCapture"/>'s client area. Uses one thread at a time.
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform( "windows10.0.19041.0" )]
public sealed class GraphicsCapture : IWindowCapturer
{
	/// <summary>Defines how many frames the pool holds.</summary>
	private const int PoolFrames = 2;

	/// <summary>Defines DWMWA_EXTENDED_FRAME_BOUNDS, the window's visible bounds, which the capture covers.</summary>
	private const int ExtendedFrameBounds = 9;

	/// <summary>Stores the captured window.</summary>
	private readonly nint _window;

	/// <summary>Stores the Direct3D device the pool allocates on.</summary>
	private readonly IDirect3DDevice _device;

	/// <summary>Stores the frame pool.</summary>
	private readonly Direct3D11CaptureFramePool _pool;

	/// <summary>Stores the running capture.</summary>
	private readonly GraphicsCaptureSession _session;

	/// <summary>Stores the two output buffers captures alternate between, so the previous frame stays readable.</summary>
	private readonly byte[]?[] _buffers = new byte[2][];

	/// <summary>Stores the full-size pixels of the newest frame.</summary>
	private byte[] _source = [];

	/// <summary>Stores the size the pool was made for.</summary>
	private Windows.Graphics.SizeInt32 _poolSize;

	/// <summary>Stores the last image returned, given again while no new frame arrived.</summary>
	private BgraFrame? _last;

	/// <summary>Stores which output buffer the next capture fills.</summary>
	private int _next;

	/// <summary>Stores the captured screen's negative id (<see cref="ScreenCatalog"/>), or 0 for a window.</summary>
	private readonly long _screen;

	/// <summary>Starts capturing a window.</summary>
	/// <exception cref="NotSupportedException">This Windows has no Windows.Graphics.Capture, or the window cannot be captured.</exception>
	public GraphicsCapture( nint window ) : this( window, 0 )
	{
	}

	/// <summary>Starts capturing a window, or a whole screen with the pointer drawn in.</summary>
	/// <param name="window">The window, or 0 for a screen.</param>
	/// <param name="screen">The screen's negative id, or 0 for a window.</param>
	/// <exception cref="NotSupportedException">This Windows has no Windows.Graphics.Capture, or the source cannot be captured.</exception>
	/// <exception cref="ArgumentException">The screen is not connected.</exception>
	private GraphicsCapture( nint window, long screen )
	{
		if ( !GraphicsCaptureSession.IsSupported() )
			throw new NotSupportedException( "Windows.Graphics.Capture is not available." );

		_window = window;
		_screen = screen;
		GraphicsCaptureItem item;
		if ( screen != 0 )
		{
			item = CreateItem( ScreenCatalog.Find( screen )?.Monitor ?? throw new ArgumentException( "The screen is not connected." ), isScreen: true );
		}
		else
		{
			if ( StreamNative.IsIconic( window ) )
				_ = StreamNative.ShowWindow( window, StreamNative.ShowNoActivate );
			item = CreateItem( window, isScreen: false );
		}

		_device = CreateDevice();
		_poolSize = item.Size;
		_pool = Direct3D11CaptureFramePool.CreateFreeThreaded( _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolFrames, _poolSize );
		_session = _pool.CreateCaptureSession( item );
		// A screen is used as a whole desktop, so its pointer shows; a window's stream draws no pointer of its own.
		_session.IsCursorCaptureEnabled = screen != 0;
		HideBorder( _session );
		_session.StartCapture();
	}

	/// <summary>Starts capturing a whole screen, the pointer included.</summary>
	/// <param name="screen">The screen's negative id (<see cref="ScreenCatalog"/>).</param>
	/// <exception cref="ArgumentException">The screen is not connected.</exception>
	public static GraphicsCapture ForScreen( long screen ) => new( 0, screen );

	/// <summary>Gets the window's client size, or the screen's size, in physical pixels; (0, 0) when the window is closed or minimized or the screen is gone.</summary>
	public (int Width, int Height) SourceSize
	{
		get
		{
			if ( _screen != 0 )
				return ScreenCatalog.Find( _screen ) is { } found ? (found.Bounds.Right - found.Bounds.Left, found.Bounds.Bottom - found.Bounds.Top) : (0, 0);

			using var dpi = StreamNative.EnterPhysicalPixels();
			return StreamNative.IsIconic( _window ) || !StreamNative.GetClientRect( _window, out var rect ) ? (0, 0) : (rect.Right - rect.Left, rect.Bottom - rect.Top);
		}
	}

	/// <summary>Takes the newest frame, crops a window's to its client area and scales it down to <paramref name="maxWidth"/>; returns the last image while nothing new arrived.</summary>
	/// <exception cref="InvalidOperationException">The window no longer exists, or the screen was disconnected.</exception>
	public BgraFrame? Capture( int maxWidth )
	{
		if ( !ScreenCatalog.Exists( _screen != 0 ? _screen : _window ) )
			throw new InvalidOperationException( _screen != 0 ? "The screen was disconnected." : "The window was closed." );

		Direct3D11CaptureFrame? frame = null;
		while ( _pool.TryGetNextFrame() is { } newer )
		{
			frame?.Dispose();
			frame = newer;
		}
		if ( frame is null )
			return _last;

		using ( frame )
		{
			if ( frame.ContentSize.Width != _poolSize.Width || frame.ContentSize.Height != _poolSize.Height )
			{
				// The window was resized: frames from now on have the new size; this one is skipped.
				_poolSize = frame.ContentSize;
				_pool.Recreate( _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolFrames, _poolSize );
				return _last;
			}

			using var bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync( frame.Surface, BitmapAlphaMode.Ignore ).AsTask().GetAwaiter().GetResult();
			var width = bitmap.PixelWidth;
			var height = bitmap.PixelHeight;
			if ( _source.Length != width * height * 4 )
				_source = new byte[width * height * 4];
			bitmap.CopyToBuffer( _source.AsBuffer() );
			_last = Crop( width, height, maxWidth ) ?? _last;
			return _last;
		}
	}

	/// <summary>Stops the capture.</summary>
	public void Dispose()
	{
		_session.Dispose();
		_pool.Dispose();
		_device.Dispose();
	}

	/// <summary>Copies a window's client area, or the whole screen, out of the full image into the next output buffer, scaled to <paramref name="maxWidth"/>
	/// by averaging the source pixels each output pixel covers.</summary>
	private BgraFrame? Crop( int width, int height, int maxWidth )
	{
		int left = 0, top = 0, clientWidth = width, clientHeight = height;
		if ( _screen == 0 )
		{
			using var dpi = StreamNative.EnterPhysicalPixels();
			var origin = new StreamNative.NativePoint();
			if ( !StreamNative.GetClientRect( _window, out var client ) || !StreamNative.ClientToScreen( _window, ref origin )
				|| DwmGetWindowAttribute( _window, ExtendedFrameBounds, out var bounds, Marshal.SizeOf<StreamNative.NativeRect>() ) != 0 )
				return null;

			left = Math.Clamp( origin.X - bounds.Left, 0, width );
			top = Math.Clamp( origin.Y - bounds.Top, 0, height );
			clientWidth = Math.Min( client.Right - client.Left, width - left );
			clientHeight = Math.Min( client.Bottom - client.Top, height - top );
		}
		if ( clientWidth <= 0 || clientHeight <= 0 )
			return null;

		var scale = maxWidth > 0 && clientWidth > maxWidth ? (double)maxWidth / clientWidth : 1.0;
		var outWidth = Math.Max( 1, (int)Math.Round( clientWidth * scale ) );
		var outHeight = Math.Max( 1, (int)Math.Round( clientHeight * scale ) );
		var size = outWidth * outHeight * 4;
		var output = _buffers[_next] is { } buffer && buffer.Length == size ? buffer : _buffers[_next] = new byte[size];
		_next ^= 1;

		var source = _source;
		var stride = width * 4;
		if ( scale == 1.0 )
		{
			for ( var y = 0; y < outHeight; y++ )
				Buffer.BlockCopy( source, (top + y) * stride + left * 4, output, y * outWidth * 4, outWidth * 4 );
			return new( outWidth, outHeight, output );
		}

		for ( var y = 0; y < outHeight; y++ )
		{
			var y0 = top + (int)((long)y * clientHeight / outHeight);
			var y1 = Math.Max( y0 + 1, top + (int)((long)(y + 1) * clientHeight / outHeight) );
			for ( var x = 0; x < outWidth; x++ )
			{
				var x0 = left + (int)((long)x * clientWidth / outWidth);
				var x1 = Math.Max( x0 + 1, left + (int)((long)(x + 1) * clientWidth / outWidth) );
				int b = 0, g = 0, r = 0, count = 0;
				for ( var sy = y0; sy < y1; sy++ )
				{
					var row = sy * stride;
					for ( var sx = x0; sx < x1; sx++ )
					{
						var index = row + sx * 4;
						b += source[index];
						g += source[index + 1];
						r += source[index + 2];
						count++;
					}
				}
				var target = (y * outWidth + x) * 4;
				output[target] = (byte)(b / count);
				output[target + 1] = (byte)(g / count);
				output[target + 2] = (byte)(r / count);
				output[target + 3] = 255;
			}
		}
		return new( outWidth, outHeight, output );
	}

	/// <summary>Creates the capture item of a window or a screen through IGraphicsCaptureItemInterop.</summary>
	/// <param name="handle">The window, or the screen's monitor handle.</param>
	/// <param name="isScreen">Whether <paramref name="handle"/> is a monitor.</param>
	private static unsafe GraphicsCaptureItem CreateItem( nint handle, bool isScreen )
	{
		var interopId = new Guid( "3628E81B-3CAC-4C60-B7F4-23CE0E0C3356" );
		var itemId = new Guid( "79C3F95B-31F7-4EC2-A464-632EF5D30760" );
		using var factory = ActivationFactory.Get( "Windows.Graphics.Capture.GraphicsCaptureItem", interopId );
		var vtable = *(nint**)factory.ThisPtr;
		// IGraphicsCaptureItemInterop::CreateForWindow follows IUnknown's three methods, then CreateForMonitor.
		var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)vtable[isScreen ? 4 : 3];
		nint result;
		Marshal.ThrowExceptionForHR( create( factory.ThisPtr, handle, &itemId, &result ) );
		try
		{
			return GraphicsCaptureItem.FromAbi( result );
		}
		finally
		{
			Marshal.Release( result );
		}
	}

	/// <summary>Asks Windows not to draw its yellow border around the captured window or screen, which would show the user an outline around every window
	/// an agent looks at, since Loom draws its own frame there: <c>IGraphicsCaptureSession3.IsBorderRequired</c>, from Windows 11, which the SDK this
	/// module builds against predates. Windows honors it only once the user allowed this app to capture without the border (<see cref="RequestBorderless"/>);
	/// older Windows, or one the user said no on, keeps the border.</summary>
	private static unsafe void HideBorder( GraphicsCaptureSession session )
	{
		RequestBorderless();
		var sessionId = new Guid( "F2CDD966-22AE-5EA1-9596-3A289344C3BE" );
		var unknown = MarshalInspectable<object>.FromManaged( session );
		try
		{
			if ( Marshal.QueryInterface( unknown, in sessionId, out var third ) != 0 )
				return;

			try
			{
				// IInspectable's six methods, then get_IsBorderRequired and put_IsBorderRequired.
				var put = (delegate* unmanaged[Stdcall]<nint, byte, int>)( *(nint**)third )[7];
				_ = put( third, 0 );
			}
			finally
			{
				Marshal.Release( third );
			}
		}
		finally
		{
			Marshal.Release( unknown );
		}
	}

	/// <summary>Tracks whether this process asked the user for borderless capture already.</summary>
	private static int s_isBorderlessAsked;

	/// <summary>Asks the user, once per process, to let Loom capture without Windows' border: <c>GraphicsCaptureAccess.RequestAccessAsync(Borderless)</c>,
	/// from Windows 11. Windows asks only the first time and remembers the answer; until the user answers, captures keep the border. Nothing happens
	/// on Windows without it.</summary>
	private static unsafe void RequestBorderless()
	{
		if ( Interlocked.Exchange( ref s_isBorderlessAsked, 1 ) != 0 )
			return;

		try
		{
			using var access = ActivationFactory.Get( "Windows.Graphics.Capture.GraphicsCaptureAccess", new Guid( "743ED370-06EC-5040-A58A-901F0F757095" ) );
			// IGraphicsCaptureAccessStatics: IInspectable's six methods, then RequestAccessAsync( GraphicsCaptureAccessKind.Borderless = 0 ).
			var request = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)( *(nint**)access.ThisPtr )[6];
			nint operation;
			if ( request( access.ThisPtr, 0, &operation ) == 0 )
				Marshal.Release( operation );
		}
		catch ( Exception error ) when ( error is COMException or TypeLoadException or EntryPointNotFoundException )
		{
			// This Windows has no borderless capture.
		}
	}

	/// <summary>Creates a hardware Direct3D 11 device with BGRA support and wraps it for WinRT.</summary>
	private static IDirect3DDevice CreateDevice()
	{
		const int HardwareDriver = 1;
		const uint BgraSupport = 0x20;
		const uint SdkVersion = 7;
		Marshal.ThrowExceptionForHR( D3D11CreateDevice( 0, HardwareDriver, 0, BgraSupport, 0, 0, SdkVersion, out var device, out _, out var context ) );
		try
		{
			var dxgiId = new Guid( "54EC77FA-1377-44E6-8C32-88FD5F44C84C" );
			Marshal.ThrowExceptionForHR( Marshal.QueryInterface( device, in dxgiId, out var dxgi ) );
			try
			{
				Marshal.ThrowExceptionForHR( CreateDirect3D11DeviceFromDXGIDevice( dxgi, out var inspectable ) );
				try
				{
					return MarshalInterface<IDirect3DDevice>.FromAbi( inspectable );
				}
				finally
				{
					Marshal.Release( inspectable );
				}
			}
			finally
			{
				Marshal.Release( dxgi );
			}
		}
		finally
		{
			Marshal.Release( context );
			Marshal.Release( device );
		}
	}

	/// <summary>Creates a Direct3D 11 device.</summary>
	[DllImport( "d3d11.dll" )]
	private static extern int D3D11CreateDevice( nint adapter, int driverType, nint software, uint flags, nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out int featureLevel, out nint context );

	/// <summary>Wraps a DXGI device as a WinRT IDirect3DDevice.</summary>
	[DllImport( "d3d11.dll" )]
	private static extern int CreateDirect3D11DeviceFromDXGIDevice( nint dxgiDevice, out nint graphicsDevice );

	/// <summary>Reads a rectangle attribute of a window from the compositor.</summary>
	[DllImport( "dwmapi.dll" )]
	private static extern int DwmGetWindowAttribute( nint window, int attribute, out StreamNative.NativeRect value, int size );
}
