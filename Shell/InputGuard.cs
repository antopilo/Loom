using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Goo;

namespace Lokn;

/// <summary>Keeps Lokn's window able to take clicks and keys when Windows leaves the keyboard or the mouse capture somewhere else.</summary>
/// <remarks>
/// The window sometimes stopped answering clicks and keys until an Alt+Tab, while it still drew. Two states cause that, and an Alt+Tab
/// clears both: the keyboard stays on a native child window, such as a browser page, which Windows does not move back when Lokn's own
/// window is clicked; and the mouse capture stays on after its button went up, when the release reached another window. The guard
/// watches the window's messages before Goo sees them: a press takes the keyboard back, and a move with no button held drops a
/// capture left on.
/// </remarks>
[SupportedOSPlatform( "windows" )]
internal static partial class InputGuard
{
	/// <summary>Defines the id of the guard's window subclass.</summary>
	private const nuint SubclassId = 0x4C6F6B6E;

	/// <summary>Defines WM_MOUSEMOVE.</summary>
	private const uint MouseMoveMessage = 0x0200;

	/// <summary>Defines the WM_MOUSEMOVE flags of the mouse buttons: MK_LBUTTON, MK_RBUTTON, MK_MBUTTON, MK_XBUTTON1 and MK_XBUTTON2.</summary>
	private const nint ButtonFlags = 0x0001 | 0x0002 | 0x0010 | 0x0020 | 0x0040;

	/// <summary>Starts guarding the window's input for the rest of the process.</summary>
	/// <remarks>Failures leave the window unguarded; the guard only recovers from a lost keyboard or capture.</remarks>
	internal static async Task ApplyAsync( Window window, CancellationToken cancellation )
	{
		// A native dialog owner lease is the public way to read the window handle; it is released at once.
		var lease = await window.AcquireNativeDialogOwnerAsync( cancellation );
		if ( lease.Owner is not { } owner )
			return;

		var handle = owner.Handle;
		owner.Dispose();
		Install( handle );
	}

	/// <summary>Adds the guard's subclass procedure to the window.</summary>
	private static unsafe void Install( nint handle ) => _ = SetWindowSubclass( handle, &UpdateMessage, SubclassId, 0 );

	/// <summary>Takes the keyboard back on a press in the window and drops a capture no held button explains, then passes the message on.</summary>
	[UnmanagedCallersOnly]
	private static nint UpdateMessage( nint window, uint message, nint wParam, nint lParam, nuint id, nuint data )
	{
		// WM_LBUTTONDOWN, WM_RBUTTONDOWN, WM_MBUTTONDOWN, WM_XBUTTONDOWN and WM_POINTERDOWN.
		if ( message is 0x0201 or 0x0204 or 0x0207 or 0x020B or 0x0246 && GetFocus() != window )
			SetFocus( window );
		else if ( message == MouseMoveMessage && (wParam & ButtonFlags) == 0 && GetCapture() == window )
			ReleaseCapture();

		return DefSubclassProc( window, message, wParam, lParam );
	}

	/// <summary>Installs a window subclass procedure.</summary>
	[LibraryImport( "comctl32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static unsafe partial bool SetWindowSubclass( nint window, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> procedure, nuint id, nuint data );

	/// <summary>Passes a message to the next window procedure.</summary>
	[LibraryImport( "comctl32.dll" )]
	private static partial nint DefSubclassProc( nint window, uint message, nint wParam, nint lParam );

	/// <summary>Sets the keyboard focus.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial nint SetFocus( nint window );

	/// <summary>Gets the window holding the keyboard on this thread's input queue.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial nint GetFocus();

	/// <summary>Gets the window holding the mouse capture on this thread.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial nint GetCapture();

	/// <summary>Releases the mouse capture held by this thread.</summary>
	[LibraryImport( "user32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static partial bool ReleaseCapture();
}
