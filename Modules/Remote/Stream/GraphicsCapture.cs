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

	/// <summary>Starts capturing a window.</summary>
	/// <exception cref="NotSupportedException">This Windows has no Windows.Graphics.Capture, or the window cannot be captured.</exception>
	public GraphicsCapture( nint window )
	{
		if ( !GraphicsCaptureSession.IsSupported() )
			throw new NotSupportedException( "Windows.Graphics.Capture is not available." );

		_window = window;
		if ( StreamNative.IsIconic( window ) )
			_ = StreamNative.ShowWindow( window, StreamNative.ShowNoActivate );

		var item = CreateItem( window );
		_device = CreateDevice();
		_poolSize = item.Size;
		_pool = Direct3D11CaptureFramePool.CreateFreeThreaded( _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolFrames, _poolSize );
		_session = _pool.CreateCaptureSession( item );
		_session.IsCursorCaptureEnabled = false;
		_session.StartCapture();
	}

	/// <summary>Gets the window's client size in physical pixels, or (0, 0) when it is closed or minimized.</summary>
	public (int Width, int Height) SourceSize
	{
		get
		{
			using var dpi = StreamNative.EnterPhysicalPixels();
			return StreamNative.IsIconic( _window ) || !StreamNative.GetClientRect( _window, out var rect ) ? (0, 0) : (rect.Right - rect.Left, rect.Bottom - rect.Top);
		}
	}

	/// <summary>Takes the newest frame, crops it to the client area and scales it down to <paramref name="maxWidth"/>; returns the last image while nothing new arrived.</summary>
	/// <exception cref="InvalidOperationException">The window no longer exists.</exception>
	public BgraFrame? Capture( int maxWidth )
	{
		if ( !StreamNative.IsWindow( _window ) )
			throw new InvalidOperationException( "The window was closed." );

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

	/// <summary>Copies the client area out of the full window image into the next output buffer, scaled to <paramref name="maxWidth"/> by averaging the source pixels each output pixel covers.</summary>
	private BgraFrame? Crop( int width, int height, int maxWidth )
	{
		using var dpi = StreamNative.EnterPhysicalPixels();
		var origin = new StreamNative.NativePoint();
		if ( !StreamNative.GetClientRect( _window, out var client ) || !StreamNative.ClientToScreen( _window, ref origin )
			|| DwmGetWindowAttribute( _window, ExtendedFrameBounds, out var bounds, Marshal.SizeOf<StreamNative.NativeRect>() ) != 0 )
			return null;

		var left = Math.Clamp( origin.X - bounds.Left, 0, width );
		var top = Math.Clamp( origin.Y - bounds.Top, 0, height );
		var clientWidth = Math.Min( client.Right - client.Left, width - left );
		var clientHeight = Math.Min( client.Bottom - client.Top, height - top );
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

	/// <summary>Creates the capture item of a window through IGraphicsCaptureItemInterop.</summary>
	private static unsafe GraphicsCaptureItem CreateItem( nint window )
	{
		var interopId = new Guid( "3628E81B-3CAC-4C60-B7F4-23CE0E0C3356" );
		var itemId = new Guid( "79C3F95B-31F7-4EC2-A464-632EF5D30760" );
		using var factory = ActivationFactory.Get( "Windows.Graphics.Capture.GraphicsCaptureItem", interopId );
		var vtable = *(nint**)factory.ThisPtr;
		// IGraphicsCaptureItemInterop::CreateForWindow follows IUnknown's three methods.
		var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)vtable[3];
		nint result;
		Marshal.ThrowExceptionForHR( create( factory.ThisPtr, window, &itemId, &result ) );
		try
		{
			return GraphicsCaptureItem.FromAbi( result );
		}
		finally
		{
			Marshal.Release( result );
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
