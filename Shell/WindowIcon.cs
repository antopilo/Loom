using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Goo;

namespace Lokn;

/// <summary>Gives the window the executable's own multi-size icon, so the taskbar and title show a crisp mark at every size.</summary>
/// <remarks>
/// Goo takes one PNG for the window icon, and Windows shrinks that 256 pixel image to taskbar size without smoothing, which
/// leaves jagged edges. The executable embeds <c>loom.ico</c>, whose small frames are drawn for their size; Windows picks
/// the frame for the display's scale and scales it down well. The PNG stays as the icon until this runs, and on other systems.
/// </remarks>
[SupportedOSPlatform( "windows" )]
internal static class WindowIcon
{
	/// <summary>Defines the resource id the .NET SDK gives the <c>ApplicationIcon</c>.</summary>
	private const nint ApplicationIconId = 32512;

	/// <summary>Defines the message that sets a window's icon.</summary>
	private const uint SetIconMessage = 0x0080;

	/// <summary>Defines the small icon, used in the title bar and by Alt+Tab at small sizes.</summary>
	private const nint SmallIcon = 0;

	/// <summary>Defines the large icon, used by the taskbar and Alt+Tab.</summary>
	private const nint LargeIcon = 1;

	/// <summary>Defines the system metric of the large icon's width.</summary>
	private const int LargeIconMetric = 11;

	/// <summary>Defines the system metric of the small icon's width.</summary>
	private const int SmallIconMetric = 49;

	/// <summary>Defines the image type of an icon for <see cref="LoadImageW"/>.</summary>
	private const uint IconImage = 1;

	/// <summary>Sets the window's small and large icons from the executable's icon, at the sizes of the window's display.</summary>
	/// <remarks>Failures leave the PNG icon in place; the icon is cosmetic.</remarks>
	internal static async Task ApplyAsync( Window window, CancellationToken cancellation )
	{
		// A native dialog owner lease is the public way to read the window handle; it is released at once.
		var lease = await window.AcquireNativeDialogOwnerAsync( cancellation );
		if ( lease.Owner is not { } owner )
			return;

		var handle = owner.Handle;
		owner.Dispose();
		var instance = GetModuleHandleW( 0 );
		var dpi = GetDpiForWindow( handle );
		foreach ( var (kind, metric) in new[] { (SmallIcon, SmallIconMetric), (LargeIcon, LargeIconMetric) } )
		{
			var size = GetSystemMetricsForDpi( metric, dpi );
			// The .ico holds a frame for every common size, so Windows uses one drawn for it rather than scaling.
			var icon = LoadImageW( instance, ApplicationIconId, IconImage, size, size, 0 );
			if ( icon != 0 )
				_ = SendMessageW( handle, SetIconMessage, kind, icon );
		}
	}

	/// <summary>Gets the executable's module handle.</summary>
	[DllImport( "kernel32.dll" )]
	private static extern nint GetModuleHandleW( nint name );

	/// <summary>Gets the dots per inch of the display a window is on.</summary>
	[DllImport( "user32.dll" )]
	private static extern uint GetDpiForWindow( nint window );

	/// <summary>Gets a system metric at a given dots per inch.</summary>
	[DllImport( "user32.dll" )]
	private static extern int GetSystemMetricsForDpi( int index, uint dpi );

	/// <summary>Loads an icon resource, choosing the frame closest to the size asked for.</summary>
	[DllImport( "user32.dll" )]
	private static extern nint LoadImageW( nint instance, nint name, uint type, int width, int height, uint flags );

	/// <summary>Sends a message to a window.</summary>
	[DllImport( "user32.dll" )]
	private static extern nint SendMessageW( nint window, uint message, nint wParam, nint lParam );
}
