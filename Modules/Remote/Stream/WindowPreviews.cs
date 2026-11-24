using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Lokn.Modules.Remote;

/// <summary>Shows what a window or screen looks like in the stream panel's picker, as answered by <c>cap.preview</c>.</summary>
/// <param name="Jpeg">One small capture as JPEG, or null when there is nothing to capture, such as while the window is minimized.</param>
/// <param name="IsMinimized">Whether the window is minimized, so it has no capture.</param>
/// <param name="Icon">The window's icon as <see cref="WindowPreviews.IconSize"/> squared premultiplied RGBA pixels, or null when it has none or none was asked for.</param>
public sealed record WindowPreview(
	byte[]? Jpeg,
	[property: JsonPropertyName( "minimized" )] bool IsMinimized,
	byte[]? Icon );

/// <summary>Reads previews of the host's windows and screens: one capture through the stream's own capturer, and the window's icon.</summary>
public static class WindowPreviews
{
	/// <summary>Defines the side of a preview icon in physical pixels.</summary>
	public const int IconSize = 32;

	/// <summary>Defines the widest preview capture in physical pixels.</summary>
	public const int MaxWidth = 400;

	/// <summary>Defines the preview's JPEG quality.</summary>
	private const double Quality = 0.6;

	/// <summary>Defines how long to wait for a new capture's first frame.</summary>
	private static readonly TimeSpan FirstFrameWait = TimeSpan.FromSeconds( 1 );

	/// <summary>Defines how often to look for the first frame.</summary>
	private static readonly TimeSpan FirstFramePoll = TimeSpan.FromMilliseconds( 30 );

	/// <summary>Reads a preview of a window or screen of this PC.</summary>
	/// <param name="window">The window's handle, or a screen's negative id.</param>
	/// <param name="withIcon">Whether to read the window's icon too; screens have none.</param>
	/// <param name="open">Opens the capturer, as a stream of the window would.</param>
	/// <param name="encode">Encodes the capture; null uses <see cref="JpegEncoder.EncodeAsync"/>.</param>
	/// <param name="cancellation">Cancels the read.</param>
	public static async Task<WindowPreview> ReadAsync( long window, bool withIcon, Func<long, IWindowCapturer> open, Func<BgraFrame, double, CancellationToken, Task<byte[]>>? encode, CancellationToken cancellation )
	{
		if ( !OperatingSystem.IsWindows() )
			return new( null, false, null );

		var isScreen = ScreenCatalog.IsScreen( window );
		var icon = withIcon && !isScreen ? ReadIcon( (nint)window ) : null;
		if ( !isScreen && StreamNative.IsIconic( (nint)window ) )
			return new( null, true, icon );

		BgraFrame? frame;
		using ( var capturer = await Task.Run( () => open( window ), cancellation ).ConfigureAwait( false ) )
		{
			var deadline = DateTime.UtcNow + FirstFrameWait;
			while ( (frame = await Task.Run( () => capturer.Capture( MaxWidth ), cancellation ).ConfigureAwait( false )) is null && DateTime.UtcNow < deadline )
				await Task.Delay( FirstFramePoll, cancellation ).ConfigureAwait( false );
		}

		var jpeg = frame is null ? null : await (encode ?? JpegEncoder.EncodeAsync)( frame, Quality, cancellation ).ConfigureAwait( false );
		return new( jpeg, false, icon );
	}

	/// <summary>Draws a window's icon as premultiplied RGBA, or returns null when it has none.</summary>
	private static byte[]? ReadIcon( nint window )
	{
		var icon = ReadIconHandle( window );
		if ( icon == 0 )
			return null;

		var info = new StreamNative.BitmapInfo
		{
			Size = Marshal.SizeOf<StreamNative.BitmapInfo>() - sizeof( int ),
			Width = IconSize,
			Height = -IconSize,
			Planes = 1,
			BitCount = 32,
		};
		var screen = StreamNative.GetDC( 0 );
		var dc = StreamNative.CreateCompatibleDC( screen );
		var bitmap = StreamNative.CreateDIBSection( screen, ref info, StreamNative.RgbColors, out var bits, 0, 0 );
		_ = StreamNative.ReleaseDC( 0, screen );
		try
		{
			if ( dc == 0 || bitmap == 0 || bits == 0 )
				return null;

			var previous = StreamNative.SelectObject( dc, bitmap );
			var isDrawn = StreamNative.DrawIconEx( dc, 0, 0, icon, IconSize, IconSize, 0, 0, StreamNative.DrawIconNormal );
			_ = StreamNative.GdiFlush();
			_ = StreamNative.SelectObject( dc, previous );
			if ( !isDrawn )
				return null;

			var pixels = new byte[IconSize * IconSize * 4];
			Marshal.Copy( bits, pixels, 0, pixels.Length );
			return ToRgba( pixels );
		}
		finally
		{
			if ( bitmap != 0 )
				_ = StreamNative.DeleteObject( bitmap );
			if ( dc != 0 )
				_ = StreamNative.DeleteDC( dc );
		}
	}

	/// <summary>Turns drawn BGRA pixels into RGBA in place; an icon drawn without alpha, an old masked one, is opaque wherever it drew.</summary>
	private static byte[] ToRgba( byte[] pixels )
	{
		var hasAlpha = false;
		for ( var i = 3; i < pixels.Length && !hasAlpha; i += 4 )
			hasAlpha = pixels[i] != 0;

		for ( var i = 0; i < pixels.Length; i += 4 )
		{
			(pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
			if ( !hasAlpha && (pixels[i] | pixels[i + 1] | pixels[i + 2]) != 0 )
				pixels[i + 3] = 255;
		}

		return pixels;
	}

	/// <summary>Reads the icon a window shows in the taskbar: the one it set, else its class's; 0 when it has none or does not answer.</summary>
	private static nint ReadIconHandle( nint window )
	{
		foreach ( var kind in (ReadOnlySpan<nint>)[StreamNative.IconSmall2, StreamNative.IconBig] )
		{
			if ( StreamNative.SendMessageTimeout( window, StreamNative.GetIconMessage, kind, 0, StreamNative.AbortIfHung, 100, out var answer ) != 0 && answer != 0 )
				return answer;
		}

		var small = StreamNative.GetClassLongPtr( window, StreamNative.ClassSmallIcon );
		return small != 0 ? small : StreamNative.GetClassLongPtr( window, StreamNative.ClassIcon );
	}
}
