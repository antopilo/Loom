using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lokn.Modules.Remote;

/// <summary>Marks the pointer while an agent uses this PC's windows, so the user can follow it: a soft accent glow with a ring around the pointer's tip,
/// and a ripple that spreads from it on each click.</summary>
/// <remarks>One layered window centered on the pointer, top-most, click-through, never activated and left out of captures; it lives on the control bar's
/// thread, which calls <see cref="Update"/> on every frame while the bar shows.</remarks>
[SupportedOSPlatform( "windows" )]
internal sealed unsafe partial class ControlPointer : IDisposable
{
	/// <summary>Defines, at 96 DPI, the window's size, the ring's radius and the glow's reach.</summary>
	private const double Size = 120, Ring = 12, Glow = 22;

	/// <summary>Defines how long a click's ripple spreads, and how far.</summary>
	private const double RippleMilliseconds = 550, RippleGrowth = 34;

	/// <summary>Stores the window.</summary>
	private readonly nint _window;

	/// <summary>Stores the light's color as blue, green and red from 0 to 1.</summary>
	private readonly (float B, float G, float R) _color;

	/// <summary>Measures time since the last click, or is stopped when no ripple runs.</summary>
	private readonly System.Diagnostics.Stopwatch _ripple = new();

	/// <summary>Stores the memory device context, its bitmap and pixels, and the side they were made for.</summary>
	private nint _dc, _bitmap;

	/// <summary>Stores the surface's pixels, premultiplied BGRA, top-down.</summary>
	private uint* _pixels;

	/// <summary>Stores the surface's side in pixels.</summary>
	private int _side;

	/// <summary>Stores whether the window shows.</summary>
	private bool _isShown;

	/// <summary>Creates the window, hidden, on the calling thread.</summary>
	internal ControlPointer( char* className, nint instance, uint color )
	{
		_color = (((color >> 16) & 0xFF) / 255f, ((color >> 8) & 0xFF) / 255f, (color & 0xFF) / 255f);
		_window = CreateWindowExW( 0x00080000 | 0x00000020 | 0x00000008 | 0x00000080 | 0x08000000, className, className, 0x80000000, 0, 0, 1, 1, 0, 0, instance, 0 );
		if ( _window != 0 )
			SetWindowDisplayAffinity( _window, 0x11 /* WDA_EXCLUDEFROMCAPTURE */ );
	}

	/// <summary>Starts a ripple at the pointer, for a click.</summary>
	internal void Pulse() => _ripple.Restart();

	/// <summary>Draws the mark around the pointer where it is now.</summary>
	/// <param name="scale">The scale of the pointer's screen, 1 at 96 DPI.</param>
	internal void Update( double scale )
	{
		if ( _window == 0 || GetPhysicalCursorPos( out var cursor ) == 0 )
			return;

		var side = (int)Math.Ceiling( Size * scale );
		if ( side != _side )
			Allocate( side );
		if ( _pixels is null )
			return;

		double center = side / 2.0, ring = Ring * scale, glow = Glow * scale, line = 1.6 * scale;
		var ripple = _ripple.IsRunning ? _ripple.Elapsed.TotalMilliseconds / RippleMilliseconds : 2;
		if ( ripple > 1 )
			_ripple.Reset();
		var eased = 1 - Math.Pow( 1 - Math.Min( ripple, 1 ), 3 );
		var rippleRadius = ring + RippleGrowth * scale * eased;
		var rippleAlpha = ripple <= 1 ? (1 - ripple) * 0.9 : 0;
		for ( var y = 0; y < side; y++ )
		{
			for ( var x = 0; x < side; x++ )
			{
				var distance = Math.Sqrt( Math.Pow( x + 0.5 - center, 2 ) + Math.Pow( y + 0.5 - center, 2 ) );
				var spread = distance / glow;
				var alpha = Math.Exp( -spread * spread ) * 0.32;
				alpha = Math.Max( alpha, Math.Max( 0, 1 - Math.Abs( distance - ring ) / line ) * 0.95 );
				if ( rippleAlpha > 0 )
					alpha = Math.Max( alpha, Math.Max( 0, 1 - Math.Abs( distance - rippleRadius ) / (line * 1.3) ) * rippleAlpha );
				var a = (uint)(Math.Min( 1, alpha ) * 255);
				_pixels[y * side + x] = (a << 24) | ((uint)(_color.R * a) << 16) | ((uint)(_color.G * a) << 8) | (uint)(_color.B * a);
			}
		}

		var position = new Point { X = cursor.X - side / 2, Y = cursor.Y - side / 2 };
		var size = new Point { X = side, Y = side };
		var origin = new Point();
		var blend = new Blend { Operation = 0, Alpha = 255, Format = 1 };
		UpdateLayeredWindow( _window, 0, &position, &size, _dc, &origin, 0, &blend, 2 /* ULW_ALPHA */ );
		SetWindowPos( _window, -1 /* HWND_TOPMOST */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040 );
		_isShown = true;
	}

	/// <summary>Hides the mark.</summary>
	internal void Hide()
	{
		if ( !_isShown )
			return;

		_isShown = false;
		_ripple.Reset();
		ShowWindow( _window, 0 );
	}

	/// <summary>Destroys the window and frees the surface.</summary>
	public void Dispose()
	{
		Release();
		if ( _window != 0 )
			DestroyWindow( _window );
	}

	/// <summary>Makes the drawing surface for a side.</summary>
	private void Allocate( int side )
	{
		Release();
		_side = side;
		var info = new BitmapInfo { Size = (uint)sizeof( BitmapInfo ), Width = side, Height = -side, Planes = 1, BitCount = 32 };
		_dc = CreateCompatibleDC( 0 );
		void* bits;
		_bitmap = CreateDIBSection( _dc, &info, 0, &bits, 0, 0 );
		_pixels = (uint*)bits;
		SelectObject( _dc, _bitmap );
	}

	/// <summary>Frees the drawing surface.</summary>
	private void Release()
	{
		if ( _dc != 0 )
			DeleteDC( _dc );
		if ( _bitmap != 0 )
			DeleteObject( _bitmap );
		(_dc, _bitmap, _side) = (0, 0, 0);
		_pixels = null;
	}

	/// <summary>Mirrors <c>POINT</c> and <c>SIZE</c>.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct Point
	{
		/// <summary>The horizontal coordinate or width.</summary>
		public int X;

		/// <summary>The vertical coordinate or height.</summary>
		public int Y;
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

	/// <summary>Mirrors <c>BITMAPINFOHEADER</c>.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct BitmapInfo
	{
		/// <summary>The header's size.</summary>
		public uint Size;

		/// <summary>The width.</summary>
		public int Width;

		/// <summary>The height, negative for top-down.</summary>
		public int Height;

		/// <summary>The planes.</summary>
		public ushort Planes;

		/// <summary>The bits per pixel.</summary>
		public ushort BitCount;

		/// <summary>The compression.</summary>
		public uint Compression;

		/// <summary>The image size.</summary>
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

	/// <summary>Reads the pointer's position in physical pixels.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int GetPhysicalCursorPos( out Point point );

	/// <summary>Updates a layered window's picture and place.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int UpdateLayeredWindow( nint window, nint screenDc, Point* position, Point* size, nint sourceDc, Point* sourcePosition, uint key, Blend* blend, uint flags );

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
