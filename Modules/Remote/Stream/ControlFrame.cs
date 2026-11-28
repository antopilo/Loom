using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lokn.Modules.Remote;

/// <summary>Draws a frame around the window an agent uses: the same light as the prompt's while the agent works (<c>glow.slang</c>), a bright head
/// that travels clockwise around the window's rounded edge with a fading tail, over a faint steady line and a soft halo outside the edge.</summary>
/// <remarks>
/// The frame is four layered strips around the window, top-most, never activated, let clicks through and left out of captures, so the agent's own
/// captures never show it. It lives on the control bar's thread, which calls <see cref="Update"/> on every frame while the bar shows. What depends
/// only on the window's size (how far each pixel is from the edge, and where it lies along it) is computed once per size; each frame only moves the
/// light, through a table of the tail's falloff.
/// </remarks>
[SupportedOSPlatform( "windows" )]
internal sealed unsafe partial class ControlFrame : IDisposable
{
	/// <summary>Defines, at 96 DPI, how far the halo reaches out from the edge, the corner radius, and the line's half width.</summary>
	private const double Reach = 7, Radius = 8, Line = 1.4;

	/// <summary>Defines the seconds per lap, as the prompt's light.</summary>
	private const double LapSeconds = 3.2;

	/// <summary>Defines the tail's length as a share of a lap, as the prompt's light.</summary>
	private const double Tail = 0.12;

	/// <summary>Defines the glow the whole edge keeps behind the light, so the frame shows the window in use even where the light is not.</summary>
	private const float Steady = 0.55f;

	/// <summary>Defines how many steps the tail's falloff table has.</summary>
	private const int TailSteps = 1024;

	/// <summary>Holds the tail's falloff by distance behind the head, as a share of a lap.</summary>
	private static readonly float[] TailTable = BuildTailTable();

	/// <summary>Holds the four strips: top, bottom, left and right.</summary>
	private readonly Strip[] _strips = new Strip[4];

	/// <summary>Stores the light's color as blue, green and red from 0 to 1.</summary>
	private readonly (float B, float G, float R) _color;

	/// <summary>Stores the window's bounds and scale the strips' geometry was computed for.</summary>
	private (Rect Bounds, double Scale) _shape;

	/// <summary>Stores whether the strips show.</summary>
	private bool _isShown;

	/// <summary>Creates the four strips, hidden, on the calling thread.</summary>
	/// <param name="className">A registered window class whose procedure gives default handling.</param>
	/// <param name="instance">The module that registered it.</param>
	/// <param name="color">The light's color as 0x00BBGGRR.</param>
	internal ControlFrame( char* className, nint instance, uint color )
	{
		_color = (((color >> 16) & 0xFF) / 255f, ((color >> 8) & 0xFF) / 255f, (color & 0xFF) / 255f);
		for ( var index = 0; index < _strips.Length; index++ )
		{
			// Layered, click-through, top-most, not in the taskbar and never activated.
			var window = CreateWindowExW( 0x00080000 | 0x00000020 | 0x00000008 | 0x00000080 | 0x08000000, className, className, 0x80000000, 0, 0, 1, 1, 0, 0, instance, 0 );
			if ( window != 0 )
				SetWindowDisplayAffinity( window, 0x11 /* WDA_EXCLUDEFROMCAPTURE */ );
			_strips[index] = new Strip( window );
		}
	}

	/// <summary>Moves the frame to the window and draws the light where it is now, or hides the frame while the window is closed or minimized.</summary>
	/// <param name="target">The window the agent uses.</param>
	/// <param name="scale">The scale of the window's screen, 1 at 96 DPI.</param>
	/// <param name="seconds">The seconds since the frame first showed, which place the light.</param>
	internal void Update( nint target, double scale, double seconds )
	{
		if ( target == 0 || IsWindowVisible( target ) == 0 || IsIconic( target ) != 0 || !TryReadBounds( target, out var bounds ) )
		{
			Hide();
			return;
		}

		if ( _shape.Bounds.Left != bounds.Left || _shape.Bounds.Top != bounds.Top || _shape.Bounds.Right != bounds.Right || _shape.Bounds.Bottom != bounds.Bottom || _shape.Scale != scale )
		{
			_shape = (bounds, scale);
			Layout( bounds, scale );
		}

		var head = seconds / LapSeconds % 1.0;
		foreach ( var strip in _strips )
			strip.Draw( head, _color );
		_isShown = true;
	}

	/// <summary>Hides the frame.</summary>
	internal void Hide()
	{
		if ( !_isShown )
			return;

		_isShown = false;
		foreach ( var strip in _strips )
			ShowWindow( strip.Window, 0 /* SW_HIDE */ );
	}

	/// <summary>Destroys the strips' windows and drawing surfaces; on the bar's thread.</summary>
	public void Dispose()
	{
		foreach ( var strip in _strips )
			strip.Dispose();
	}

	/// <summary>Places the four strips around the window's bounds and computes, for each of their pixels, how lit the edge leaves it and where it
	/// lies along the edge.</summary>
	private void Layout( Rect bounds, double scale )
	{
		int outside = (int)Math.Ceiling( Reach * 5 * scale ), inside = (int)Math.Ceiling( Reach * 1.5 * scale );
		int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
		var geometry = new Geometry( bounds, scale );
		_strips[0].Place( bounds.Left - outside, bounds.Top - outside, width + 2 * outside, outside + inside, geometry );
		_strips[1].Place( bounds.Left - outside, bounds.Bottom - inside, width + 2 * outside, outside + inside, geometry );
		_strips[2].Place( bounds.Left - outside, bounds.Top + inside, outside + inside, Math.Max( 0, height - 2 * inside ), geometry );
		_strips[3].Place( bounds.Right - inside, bounds.Top + inside, outside + inside, Math.Max( 0, height - 2 * inside ), geometry );
	}

	/// <summary>Reads a window's visible bounds in screen pixels, without the invisible resize borders Windows adds around it.</summary>
	private static bool TryReadBounds( nint window, out Rect bounds )
	{
		fixed ( Rect* pointer = &bounds )
		{
			if ( DwmGetWindowAttribute( window, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, pointer, sizeof( Rect ) ) == 0 )
				return bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
			return GetWindowRect( window, pointer ) != 0 && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
		}
	}

	/// <summary>Builds the tail's falloff: full at the head, fading behind it, with a short soft front ahead of it, as the prompt's light.</summary>
	private static float[] BuildTailTable()
	{
		var table = new float[TailSteps];
		for ( var step = 0; step < TailSteps; step++ )
		{
			var behind = (double)step / TailSteps;
			// A window is far bigger than the prompt, so the soft front ahead of the head is a little longer than the prompt's, or it reads as a hard cut.
			table[step] = (float)Math.Max( Math.Exp( -behind / Tail ), Math.Exp( -(1 - behind) / 0.02 ) );
		}
		return table;
	}

	/// <summary>Computes how a point relates to the window's rounded edge, as the prompt's shader does.</summary>
	/// <param name="Bounds">The window's bounds in screen pixels.</param>
	/// <param name="Scale">The screen's scale, 1 at 96 DPI.</param>
	private readonly record struct Geometry( Rect Bounds, double Scale )
	{
		/// <summary>Computes, for a screen pixel, how lit the edge leaves it, how much of the head's bloom reaches it, and where it lies along the edge
		/// as a share of the way round, clockwise from the top left corner.</summary>
		internal (float Edge, float Bloom, float Along) Measure( int x, int y )
		{
			double halfWidth = (Bounds.Right - Bounds.Left) / 2.0, halfHeight = (Bounds.Bottom - Bounds.Top) / 2.0;
			double px = x + 0.5 - (Bounds.Left + halfWidth), py = y + 0.5 - (Bounds.Top + halfHeight);
			var radius = Math.Min( Radius * Scale, Math.Min( halfWidth, halfHeight ) );
			double qx = Math.Abs( px ) - halfWidth + radius, qy = Math.Abs( py ) - halfHeight + radius;
			var distance = Math.Sqrt( Math.Pow( Math.Max( qx, 0 ), 2 ) + Math.Pow( Math.Max( qy, 0 ), 2 ) ) + Math.Min( Math.Max( qx, qy ), 0 ) - radius;

			// A crisp line on the edge, a halo fading outward and a short one inward, so a maximized window still shows its frame.
			var reach = Reach * Scale;
			var line = Math.Max( 0, 1 - Math.Abs( distance ) / (Line * Scale) );
			var halo = distance > 0 ? Math.Exp( -distance / reach ) * 0.45 : Math.Exp( distance / (reach * 0.5) ) * 0.25;
			// The head's bloom falls off as a bell, so it is gone well before the strips end, outward and inward, and leaves no hard edge.
			var spread = distance / (reach * (distance > 0 ? 1.4 : 0.5));
			var bloom = Math.Exp( -spread * spread ) * 0.9;

			double w = halfWidth * 2, h = halfHeight * 2, along;
			if ( halfHeight - Math.Abs( py ) < halfWidth - Math.Abs( px ) )
				along = py < 0 ? px + halfWidth : w + h + (halfWidth - px);
			else
				along = px > 0 ? w + (py + halfHeight) : 2 * w + h + (halfHeight - py);
			// Pixels beyond a corner, outside the window, fall just before or after the way round; wrap them onto it.
			var share = along / (2 * (w + h));
			share -= Math.Floor( share );
			return ((float)Math.Max( line, halo ), (float)bloom, (float)share);
		}
	}

	/// <summary>Holds one strip: its window, its drawing surface and the geometry of its pixels.</summary>
	private sealed class Strip( nint window ) : IDisposable
	{
		/// <summary>Gets the strip's window.</summary>
		internal nint Window { get; } = window;

		/// <summary>Stores the strip's place and size in screen pixels.</summary>
		private int _left, _top, _width, _height;

		/// <summary>Stores how lit the edge leaves each pixel.</summary>
		private float[] _edge = [];

		/// <summary>Stores how much of the head's bloom reaches each pixel.</summary>
		private float[] _bloom = [];

		/// <summary>Stores where each pixel lies along the edge, as a share of the way round.</summary>
		private float[] _along = [];

		/// <summary>Stores the memory device context the surface is selected into, its bitmap, and its pixels.</summary>
		private nint _dc, _bitmap;

		/// <summary>Stores the surface's pixels, premultiplied BGRA, top-down.</summary>
		private uint* _pixels;

		/// <summary>Places the strip and computes its pixels' geometry, making a new surface when its size changed.</summary>
		internal void Place( int left, int top, int width, int height, Geometry geometry )
		{
			(_left, _top) = (left, top);
			if ( width != _width || height != _height )
			{
				(_width, _height) = (width, height);
				ReleaseSurface();
				if ( width > 0 && height > 0 )
				{
					var info = new BitmapInfo { Size = (uint)sizeof( BitmapInfo ), Width = width, Height = -height, Planes = 1, BitCount = 32 };
					_dc = CreateCompatibleDC( 0 );
					void* bits;
					_bitmap = CreateDIBSection( _dc, &info, 0, &bits, 0, 0 );
					_pixels = (uint*)bits;
					SelectObject( _dc, _bitmap );
				}
			}

			var count = Math.Max( 0, width * height );
			_edge = new float[count];
			_bloom = new float[count];
			_along = new float[count];
			for ( var y = 0; y < height; y++ )
			{
				for ( var x = 0; x < width; x++ )
				{
					var (edge, bloom, along) = geometry.Measure( left + x, top + y );
					var index = y * width + x;
					(_edge[index], _bloom[index], _along[index]) = (edge, bloom, along);
				}
			}
		}

		/// <summary>Draws the light with its head at a share of the way round, and shows the strip over every window.</summary>
		internal void Draw( double head, (float B, float G, float R) color )
		{
			if ( _pixels is null || Window == 0 )
				return;

			var count = _width * _height;
			for ( var index = 0; index < count; index++ )
			{
				var behind = head - _along[index];
				if ( behind < 0 )
					behind += 1;
				var tail = TailTable[Math.Clamp( (int)(behind * TailSteps), 0, TailSteps - 1 )];
				var alpha = Math.Min( 1f, Math.Max( _edge[index] * (Steady + (1 - Steady) * tail), _bloom[index] * tail ) );
				var a = (uint)(alpha * 255);
				_pixels[index] = (a << 24) | ((uint)(color.R * a) << 16) | ((uint)(color.G * a) << 8) | (uint)(color.B * a);
			}

			var position = new Point { X = _left, Y = _top };
			var size = new Size { Width = _width, Height = _height };
			var origin = new Point();
			var blend = new Blend { Operation = 0 /* AC_SRC_OVER */, Alpha = 255, Format = 1 /* AC_SRC_ALPHA */ };
			UpdateLayeredWindow( Window, 0, &position, &size, _dc, &origin, 0, &blend, 2 /* ULW_ALPHA */ );
			SetWindowPos( Window, -1 /* HWND_TOPMOST */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040 /* NOSIZE | NOMOVE | NOACTIVATE | SHOWWINDOW */ );
		}

		/// <summary>Frees the drawing surface.</summary>
		private void ReleaseSurface()
		{
			if ( _dc != 0 )
				DeleteDC( _dc );
			if ( _bitmap != 0 )
				DeleteObject( _bitmap );
			(_dc, _bitmap) = (0, 0);
			_pixels = null;
		}

		/// <summary>Destroys the window and frees the surface.</summary>
		public void Dispose()
		{
			ReleaseSurface();
			if ( Window != 0 )
				DestroyWindow( Window );
		}
	}

	/// <summary>Mirrors <c>RECT</c>.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct Rect
	{
		/// <summary>The left edge.</summary>
		public int Left;

		/// <summary>The top edge.</summary>
		public int Top;

		/// <summary>The right edge.</summary>
		public int Right;

		/// <summary>The bottom edge.</summary>
		public int Bottom;
	}

	/// <summary>Mirrors <c>POINT</c>.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct Point
	{
		/// <summary>The horizontal coordinate.</summary>
		public int X;

		/// <summary>The vertical coordinate.</summary>
		public int Y;
	}

	/// <summary>Mirrors <c>SIZE</c>.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct Size
	{
		/// <summary>The width.</summary>
		public int Width;

		/// <summary>The height.</summary>
		public int Height;
	}

	/// <summary>Mirrors <c>BLENDFUNCTION</c>.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct Blend
	{
		/// <summary>The blend operation.</summary>
		public byte Operation;

		/// <summary>Reserved.</summary>
		public byte Flags;

		/// <summary>The constant alpha.</summary>
		public byte Alpha;

		/// <summary>Whether the source has per-pixel alpha.</summary>
		public byte Format;
	}

	/// <summary>Mirrors <c>BITMAPINFOHEADER</c>, all <c>BITMAPINFO</c> needs for 32-bit pixels.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct BitmapInfo
	{
		/// <summary>The header's size.</summary>
		public uint Size;

		/// <summary>The width.</summary>
		public int Width;

		/// <summary>The height, negative for top-down.</summary>
		public int Height;

		/// <summary>The planes, 1.</summary>
		public ushort Planes;

		/// <summary>The bits per pixel.</summary>
		public ushort BitCount;

		/// <summary>The compression, none.</summary>
		public uint Compression;

		/// <summary>The image size, 0 for uncompressed.</summary>
		public uint SizeImage;

		/// <summary>The horizontal resolution.</summary>
		public int XPixelsPerMeter;

		/// <summary>The vertical resolution.</summary>
		public int YPixelsPerMeter;

		/// <summary>The colors used.</summary>
		public uint ColorsUsed;

		/// <summary>The important colors.</summary>
		public uint ColorsImportant;
	}

	/// <summary>Creates a window.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial nint CreateWindowExW( uint extendedStyle, char* className, char* title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter );

	/// <summary>Destroys a window.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int DestroyWindow( nint window );

	/// <summary>Keeps a window out of screen captures.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int SetWindowDisplayAffinity( nint window, uint affinity );

	/// <summary>Shows or hides a window.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int ShowWindow( nint window, int command );

	/// <summary>Moves, sizes and orders a window.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int SetWindowPos( nint window, nint after, int x, int y, int width, int height, uint flags );

	/// <summary>Reports whether a window is visible.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int IsWindowVisible( nint window );

	/// <summary>Reports whether a window is minimized.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int IsIconic( nint window );

	/// <summary>Reads a window's rectangle.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int GetWindowRect( nint window, Rect* rect );

	/// <summary>Reads a window attribute from the compositor.</summary>
	[LibraryImport( "dwmapi.dll" )]
	private static partial int DwmGetWindowAttribute( nint window, uint attribute, void* value, int size );

	/// <summary>Updates a layered window's picture and place.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int UpdateLayeredWindow( nint window, nint screenDc, Point* position, Size* size, nint sourceDc, Point* sourcePosition, uint key, Blend* blend, uint flags );

	/// <summary>Makes a memory device context.</summary>
	[LibraryImport( "gdi32.dll" )]
	private static partial nint CreateCompatibleDC( nint dc );

	/// <summary>Deletes a memory device context.</summary>
	[LibraryImport( "gdi32.dll" )]
	private static partial int DeleteDC( nint dc );

	/// <summary>Makes a bitmap whose pixels this process writes.</summary>
	[LibraryImport( "gdi32.dll" )]
	private static partial nint CreateDIBSection( nint dc, BitmapInfo* info, uint usage, void** bits, nint section, uint offset );

	/// <summary>Selects an object into a device context.</summary>
	[LibraryImport( "gdi32.dll" )]
	private static partial nint SelectObject( nint dc, nint item );

	/// <summary>Deletes a GDI object.</summary>
	[LibraryImport( "gdi32.dll" )]
	private static partial int DeleteObject( nint item );
}
