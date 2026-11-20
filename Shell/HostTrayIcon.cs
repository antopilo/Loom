using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lokn.Core;

namespace Lokn;

/// <summary>Draws the daemon's icon in the notification area: Loom's icon from an executable's icon resource at the size asked for, with a dot in
/// its bottom-right corner whose colour tells the daemon's state, green running, orange needs you, red error.</summary>
/// <remarks>
/// Without GDI+ the icon is composed in managed memory: the base icon's frame nearest above the size is read as 32-bit straight-alpha pixels,
/// box-filtered down to the size, cut out around the dot so it reads on any taskbar, and the anti-aliased dot drawn over it. The pixels become an
/// icon through a 32-bit DIB section. The caller destroys the returned icon with <see cref="DestroyIcon"/>.
/// </remarks>
[SupportedOSPlatform( "windows" )]
internal static unsafe class HostTrayIcon
{
	/// <summary>Defines the icon group the C# compiler gives an application icon, <c>IDI_APPLICATION</c>'s number.</summary>
	private const int IconGroupId = 32512;

	/// <summary>Defines the <c>RT_ICON</c> resource type.</summary>
	private const int IconResourceType = 3;

	/// <summary>Defines the <c>RT_GROUP_ICON</c> resource type.</summary>
	private const int IconGroupResourceType = 14;

	/// <summary>Defines the size of one <c>GRPICONDIRENTRY</c>.</summary>
	private const int GroupEntrySize = 14;

	/// <summary>Defines the icon format version <c>CreateIconFromResourceEx</c> expects.</summary>
	private const uint IconVersion = 0x00030000;

	/// <summary>Defines the size of <c>BITMAPINFOHEADER</c> alone, without the room for a colour table after it.</summary>
	private const uint HeaderSize = 40;

	/// <summary>Defines how many samples per side decide a pixel's coverage by the dot.</summary>
	private const int Samples = 4;

	/// <summary>Gets the dot's colour for a state, as straight-alpha <c>0xAARRGGBB</c>.</summary>
	internal static uint ComputeColor( HostState state ) => state switch
	{
		HostState.NeedsYou => 0xFFF28C28,
		HostState.Error => 0xFFE5484D,
		_ => 0xFF23A55A,
	};

	/// <summary>Gets where the dot sits in an icon of a size: its centre and radius in pixels, touching the bottom and right edges.</summary>
	internal static (double X, double Y, double Radius) ComputeDot( int size )
	{
		var radius = size * 0.22;
		return (size - radius, size - radius, radius);
	}

	/// <summary>Creates the icon for a state at a size, with the base icon from a module's application icon resource.</summary>
	/// <param name="module">The executable or a module loaded as a data file that holds the icon group <see cref="IconGroupId"/>.</param>
	/// <param name="size">The width and height in pixels.</param>
	/// <param name="state">The state the dot shows.</param>
	/// <returns>The icon, or 0 when it could not be created.</returns>
	internal static nint Create( nint module, int size, HostState state )
	{
		var pixels = LoadBase( module, size ) ?? new uint[size * size];
		DrawDot( pixels, size, ComputeColor( state ) );
		return CreateIcon( pixels, size );
	}

	/// <summary>Reads an icon's pixels as straight-alpha <c>0xAARRGGBB</c>, top row first.</summary>
	/// <returns>The pixels and the icon's size, or null for an icon without a colour bitmap.</returns>
	internal static (uint[] Pixels, int Width, int Height)? ReadPixels( nint icon )
	{
		IconInfo info;
		if ( GetIconInfo( icon, &info ) == 0 )
			return null;

		try
		{
			if ( info.Color == 0 )
				return null;

			Bitmap bitmap;
			if ( GetObjectW( info.Color, sizeof( Bitmap ), &bitmap ) == 0 )
				return null;

			var pixels = new uint[bitmap.Width * bitmap.Height];
			var header = BuildHeader( bitmap.Width, bitmap.Height );
			var context = CreateCompatibleDC( 0 );
			try
			{
				fixed ( uint* bits = pixels )
				{
					if ( GetDIBits( context, info.Color, 0, (uint)bitmap.Height, bits, &header, 0 ) == 0 )
						return null;
				}
			}
			finally
			{
				DeleteDC( context );
			}
			return (pixels, bitmap.Width, bitmap.Height);
		}
		finally
		{
			if ( info.Mask != 0 )
				DeleteObject( info.Mask );
			if ( info.Color != 0 )
				DeleteObject( info.Color );
		}
	}

	/// <summary>Destroys an icon this class created.</summary>
	[DllImport( "user32.dll" )]
	internal static extern int DestroyIcon( nint icon );

	/// <summary>Reads the base icon at a size from the module's icon group: the smallest frame not smaller than the size, else the largest, scaled down.</summary>
	/// <returns>The pixels, or null when the module has no such icon.</returns>
	private static uint[]? LoadBase( nint module, int size )
	{
		var group = ReadResource( module, IconGroupId, IconGroupResourceType );
		if ( group.Length < 6 )
			return null;

		var count = BitConverter.ToUInt16( group, 4 );
		int? bestId = null;
		var bestWidth = 0;
		for ( var index = 0; index < count && 6 + (index + 1) * GroupEntrySize <= group.Length; index++ )
		{
			var entry = 6 + index * GroupEntrySize;
			var width = group[entry] == 0 ? 256 : group[entry];
			var isBetter = bestId is null || (width >= size ? bestWidth < size || width < bestWidth : bestWidth < size && width > bestWidth);
			if ( isBetter )
			{
				bestId = BitConverter.ToUInt16( group, entry + 12 );
				bestWidth = width;
			}
		}
		if ( bestId is not { } id )
			return null;

		var frame = ReadResource( module, id, IconResourceType );
		if ( frame.Length == 0 )
			return null;

		nint icon;
		fixed ( byte* bits = frame )
			icon = CreateIconFromResourceEx( bits, (uint)frame.Length, 1, IconVersion, bestWidth, bestWidth, 0 );
		if ( icon == 0 )
			return null;

		try
		{
			return ReadPixels( icon ) is { } read ? Resample( read.Pixels, read.Width, read.Height, size ) : null;
		}
		finally
		{
			DestroyIcon( icon );
		}
	}

	/// <summary>Copies a resource's bytes, or returns none when the module lacks it.</summary>
	private static byte[] ReadResource( nint module, int id, int type )
	{
		var resource = FindResourceW( module, id, type );
		if ( resource == 0 )
			return [];

		var loaded = LoadResource( module, resource );
		var length = SizeofResource( module, resource );
		var data = loaded == 0 ? null : LockResource( loaded );
		return data is null ? [] : new ReadOnlySpan<byte>( data, (int)length ).ToArray();
	}

	/// <summary>Scales straight-alpha pixels to a square size with a box filter over premultiplied colour, so edges keep their colour.</summary>
	private static uint[] Resample( uint[] source, int width, int height, int size )
	{
		if ( width == size && height == size )
			return source;

		var result = new uint[size * size];
		var scaleX = (double)width / size;
		var scaleY = (double)height / size;
		for ( var y = 0; y < size; y++ )
		{
			var top = y * scaleY;
			var bottom = top + scaleY;
			for ( var x = 0; x < size; x++ )
			{
				var left = x * scaleX;
				var right = left + scaleX;
				double alpha = 0, red = 0, green = 0, blue = 0, area = 0;
				for ( var sourceY = (int)top; sourceY < Math.Min( height, (int)Math.Ceiling( bottom ) ); sourceY++ )
				{
					var weightY = Math.Min( bottom, sourceY + 1 ) - Math.Max( top, sourceY );
					for ( var sourceX = (int)left; sourceX < Math.Min( width, (int)Math.Ceiling( right ) ); sourceX++ )
					{
						var weight = weightY * (Math.Min( right, sourceX + 1 ) - Math.Max( left, sourceX ));
						var pixel = source[sourceY * width + sourceX];
						var pixelAlpha = (pixel >> 24) / 255.0 * weight;
						alpha += pixelAlpha;
						red += ((pixel >> 16) & 0xFF) * pixelAlpha;
						green += ((pixel >> 8) & 0xFF) * pixelAlpha;
						blue += (pixel & 0xFF) * pixelAlpha;
						area += weight;
					}
				}
				result[y * size + x] = alpha <= 0 ? 0 : Pack( alpha / area, red / alpha, green / alpha, blue / alpha );
			}
		}
		return result;
	}

	/// <summary>Cuts the base icon out around the dot's place, then draws the dot over it, both anti-aliased.</summary>
	private static void DrawDot( uint[] pixels, int size, uint color )
	{
		var (centerX, centerY, radius) = ComputeDot( size );
		var cut = radius + Math.Max( 1.0, size / 16.0 );
		for ( var y = Math.Max( 0, (int)(centerY - cut) ); y < size; y++ )
		{
			for ( var x = Math.Max( 0, (int)(centerX - cut) ); x < size; x++ )
			{
				var cutCover = Cover( x, y, centerX, centerY, cut );
				if ( cutCover <= 0 )
					continue;

				var dotAlpha = Cover( x, y, centerX, centerY, radius );
				var pixel = pixels[y * size + x];
				var baseAlpha = (pixel >> 24) / 255.0 * (1 - cutCover);
				var alpha = dotAlpha + baseAlpha * (1 - dotAlpha);
				if ( alpha <= 0 )
				{
					pixels[y * size + x] = 0;
					continue;
				}

				double Blend( int shift ) => (((color >> shift) & 0xFF) * dotAlpha + ((pixel >> shift) & 0xFF) * baseAlpha * (1 - dotAlpha)) / alpha;
				pixels[y * size + x] = Pack( alpha, Blend( 16 ), Blend( 8 ), Blend( 0 ) );
			}
		}
	}

	/// <summary>Gets how much of a pixel a circle covers, from 0 to 1, by sampling a grid inside it.</summary>
	private static double Cover( int x, int y, double centerX, double centerY, double radius )
	{
		var inside = 0;
		for ( var sampleY = 0; sampleY < Samples; sampleY++ )
		{
			for ( var sampleX = 0; sampleX < Samples; sampleX++ )
			{
				var dx = x + (sampleX + 0.5) / Samples - centerX;
				var dy = y + (sampleY + 0.5) / Samples - centerY;
				if ( dx * dx + dy * dy <= radius * radius )
					inside++;
			}
		}
		return inside / (double)(Samples * Samples);
	}

	/// <summary>Packs an alpha from 0 to 1 and channels from 0 to 255 into <c>0xAARRGGBB</c>.</summary>
	private static uint Pack( double alpha, double red, double green, double blue )
	{
		static uint Channel( double value ) => (uint)Math.Clamp( Math.Round( value ), 0, 255 );
		return Channel( alpha * 255 ) << 24 | Channel( red ) << 16 | Channel( green ) << 8 | Channel( blue );
	}

	/// <summary>Creates an icon from straight-alpha pixels through a 32-bit DIB section and an empty mask.</summary>
	private static nint CreateIcon( uint[] pixels, int size )
	{
		var header = BuildHeader( size, size );
		void* bits;
		var color = CreateDIBSection( 0, &header, 0, &bits, 0, 0 );
		if ( color == 0 )
			return 0;

		var maskBits = new byte[(size + 15) / 16 * 2 * size];
		nint mask;
		fixed ( byte* maskData = maskBits )
			mask = CreateBitmap( size, size, 1, 1, maskData );
		try
		{
			pixels.AsSpan().CopyTo( new Span<uint>( bits, pixels.Length ) );
			var info = new IconInfo { IsIcon = 1, Mask = mask, Color = color };
			return mask == 0 ? 0 : CreateIconIndirect( &info );
		}
		finally
		{
			DeleteObject( color );
			if ( mask != 0 )
				DeleteObject( mask );
		}
	}

	/// <summary>Describes top-down 32-bit pixels of a size.</summary>
	private static BitmapInfoHeader BuildHeader( int width, int height ) => new()
	{
		Size = HeaderSize,
		Width = width,
		Height = -height,
		Planes = 1,
		BitCount = 32,
	};

	/// <summary>Finds a resource by number and type number.</summary>
	[DllImport( "kernel32.dll" )]
	private static extern nint FindResourceW( nint module, nint name, nint type );

	/// <summary>Loads a found resource.</summary>
	[DllImport( "kernel32.dll" )]
	private static extern nint LoadResource( nint module, nint resource );

	/// <summary>Gets a loaded resource's bytes.</summary>
	[DllImport( "kernel32.dll" )]
	private static extern byte* LockResource( nint resource );

	/// <summary>Gets a found resource's length in bytes.</summary>
	[DllImport( "kernel32.dll" )]
	private static extern uint SizeofResource( nint module, nint resource );

	/// <summary>Creates an icon from one icon frame's resource bytes, PNG or DIB.</summary>
	[DllImport( "user32.dll" )]
	private static extern nint CreateIconFromResourceEx( byte* bits, uint length, int isIcon, uint version, int width, int height, uint flags );

	/// <summary>Creates an icon from a colour bitmap and a mask.</summary>
	[DllImport( "user32.dll" )]
	private static extern nint CreateIconIndirect( IconInfo* info );

	/// <summary>Gets copies of an icon's bitmaps, which the caller deletes.</summary>
	[DllImport( "user32.dll" )]
	private static extern int GetIconInfo( nint icon, IconInfo* info );

	/// <summary>Creates a DIB section the caller writes through its bits.</summary>
	[DllImport( "gdi32.dll" )]
	private static extern nint CreateDIBSection( nint context, BitmapInfoHeader* info, uint usage, void** bits, nint section, uint offset );

	/// <summary>Creates a bitmap, here a monochrome mask.</summary>
	[DllImport( "gdi32.dll" )]
	private static extern nint CreateBitmap( int width, int height, uint planes, uint bitCount, void* bits );

	/// <summary>Copies a bitmap's pixels in the format a header asks for.</summary>
	[DllImport( "gdi32.dll" )]
	private static extern int GetDIBits( nint context, nint bitmap, uint start, uint lines, void* bits, BitmapInfoHeader* info, uint usage );

	/// <summary>Describes a GDI object, here a bitmap.</summary>
	[DllImport( "gdi32.dll" )]
	private static extern int GetObjectW( nint handle, int size, void* data );

	/// <summary>Creates a memory device context.</summary>
	[DllImport( "gdi32.dll" )]
	private static extern nint CreateCompatibleDC( nint context );

	/// <summary>Deletes a memory device context.</summary>
	[DllImport( "gdi32.dll" )]
	private static extern int DeleteDC( nint context );

	/// <summary>Deletes a bitmap.</summary>
	[DllImport( "gdi32.dll" )]
	private static extern int DeleteObject( nint handle );

	/// <summary>Mirrors <c>ICONINFO</c>.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct IconInfo
	{
		/// <summary>1 for an icon, 0 for a cursor.</summary>
		public int IsIcon;

		/// <summary>The cursor's hot spot, horizontally.</summary>
		public uint HotspotX;

		/// <summary>The cursor's hot spot, vertically.</summary>
		public uint HotspotY;

		/// <summary>The mask bitmap.</summary>
		public nint Mask;

		/// <summary>The colour bitmap.</summary>
		public nint Color;
	}

	/// <summary>Mirrors <c>BITMAP</c>.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct Bitmap
	{
		/// <summary>The bitmap type, 0.</summary>
		public int Type;

		/// <summary>The width in pixels.</summary>
		public int Width;

		/// <summary>The height in pixels.</summary>
		public int Height;

		/// <summary>The bytes per row.</summary>
		public int WidthBytes;

		/// <summary>The colour planes.</summary>
		public ushort Planes;

		/// <summary>The bits per pixel.</summary>
		public ushort BitsPixel;

		/// <summary>The pixels of a DIB section, else null.</summary>
		public void* Bits;
	}

	/// <summary>Mirrors <c>BITMAPINFOHEADER</c>, followed by room for a colour table <c>GetDIBits</c> might fill.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct BitmapInfoHeader
	{
		/// <summary>The header's size in bytes, without the colour table.</summary>
		public uint Size;

		/// <summary>The width in pixels.</summary>
		public int Width;

		/// <summary>The height in pixels, negative for rows top first.</summary>
		public int Height;

		/// <summary>The colour planes, 1.</summary>
		public ushort Planes;

		/// <summary>The bits per pixel.</summary>
		public ushort BitCount;

		/// <summary>The compression, 0 for none.</summary>
		public uint Compression;

		/// <summary>The image's size in bytes, or 0.</summary>
		public uint SizeImage;

		/// <summary>The horizontal resolution.</summary>
		public int XPelsPerMeter;

		/// <summary>The vertical resolution.</summary>
		public int YPelsPerMeter;

		/// <summary>The colours used.</summary>
		public uint ClrUsed;

		/// <summary>The important colours.</summary>
		public uint ClrImportant;

		/// <summary>Room for three bit-field masks or a short colour table.</summary>
		public fixed uint Colors[4];
	}
}
