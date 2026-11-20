using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Goo;

namespace Lokn;

/// <summary>Keeps Lokn's window able to take clicks and keys when Windows leaves the keyboard or the mouse capture somewhere else.</summary>
/// <remarks>
/// The window sometimes stopped answering clicks and keys until an Alt+Tab, while it still drew. Three states cause that: the keyboard
/// stays on a native child window, such as a browser page, which Windows does not move back when Lokn's own window is clicked; the
/// mouse capture stays on after its button went up, when the release reached another window; and a tooltip, a native popup that opens
/// over the element under the pointer, takes the click and with it Windows' activation and keyboard. The guard watches the window's
/// messages before Goo sees them: a press takes the keyboard back, and a move with no button held drops a capture left on. Each popup
/// window the main window owns is made click-through and never active: menus show inside the window, so those popups are tooltips.
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

	/// <summary>Defines EVENT_OBJECT_SHOW, sent when a window becomes visible.</summary>
	private const uint ShowEvent = 0x8002;

	/// <summary>Defines WM_NCHITTEST.</summary>
	private const uint HitTestMessage = 0x0084;

	/// <summary>Defines WM_MOUSEACTIVATE.</summary>
	private const uint MouseActivateMessage = 0x0021;

	/// <summary>Defines HTTRANSPARENT, which passes a press to the window below on the same thread.</summary>
	private const nint TransparentHit = -1;

	/// <summary>Defines MA_NOACTIVATE.</summary>
	private const nint NoActivate = 3;

	/// <summary>Defines GWL_EXSTYLE.</summary>
	private const int ExStyleIndex = -20;

	/// <summary>Defines WS_EX_TRANSPARENT and WS_EX_NOACTIVATE.</summary>
	private const nint InertStyle = 0x00000020 | 0x08000000;

	/// <summary>Defines GW_OWNER.</summary>
	private const uint OwnerRelation = 4;

	/// <summary>Defines WM_KEYDOWN and WM_SYSKEYDOWN.</summary>
	private const uint KeyDownMessage = 0x0100, SystemKeyDownMessage = 0x0104;

	/// <summary>Pairs each SDL modifier bit (SDL_Keymod) with the Windows virtual key of the same side.</summary>
	private static readonly (ushort Modifier, int VirtualKey)[] Modifiers =
	[
		(0x0001, 0xA0), (0x0002, 0xA1), // Shift
		(0x0040, 0xA2), (0x0080, 0xA3), // Ctrl
		(0x0100, 0xA4), (0x0200, 0xA5), // Alt
		(0x0400, 0x5B), (0x0800, 0x5C), // Windows key
	];

	/// <summary>Stores the guarded main window, the owner of the popups made inert.</summary>
	private static nint _main;

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

	/// <summary>Adds the guard's subclass procedure to the window and watches this thread for popups it owns.</summary>
	private static unsafe void Install( nint handle )
	{
		_main = handle;
		_ = SetWindowSubclass( handle, &UpdateMessage, SubclassId, 0 );
		var thread = GetWindowThreadProcessId( handle, out var process );
		_ = SetWinEventHook( ShowEvent, ShowEvent, 0, &UpdateShown, process, thread, 0 );
	}

	/// <summary>Makes a newly shown popup of the main window click-through and never active, so it cannot take the keyboard.</summary>
	/// <remarks>Only Lokn's own SDL windows count; a file dialog the main window owns has another class and keeps its input.</remarks>
	[UnmanagedCallersOnly]
	private static unsafe void UpdateShown( nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time )
	{
		if ( objectId != 0 || window == _main || GetWindow( window, OwnerRelation ) != _main )
			return;

		var name = stackalloc char[16];
		if ( !new ReadOnlySpan<char>( name, GetClassName( window, name, 16 ) ).SequenceEqual( "SDL_app" ) )
			return;

		_ = SetWindowLongPtr( window, ExStyleIndex, GetWindowLongPtr( window, ExStyleIndex ) | InertStyle );
		_ = SetWindowSubclass( window, &UpdatePopupMessage, SubclassId, 0 );
	}

	/// <summary>Passes presses on a popup to the window below and keeps the popup from being activated.</summary>
	[UnmanagedCallersOnly]
	private static nint UpdatePopupMessage( nint window, uint message, nint wParam, nint lParam, nuint id, nuint data ) => message switch
	{
		HitTestMessage => TransparentHit,
		MouseActivateMessage => NoActivate,
		_ => DefSubclassProc( window, message, wParam, lParam ),
	};

	/// <summary>Takes the keyboard back on a press in the window, drops a capture no held button explains, and clears modifiers SDL still holds after their release, then passes the message on.</summary>
	[UnmanagedCallersOnly]
	private static nint UpdateMessage( nint window, uint message, nint wParam, nint lParam, nuint id, nuint data )
	{
		// WM_LBUTTONDOWN, WM_RBUTTONDOWN, WM_MBUTTONDOWN, WM_XBUTTONDOWN and WM_POINTERDOWN.
		if ( message is 0x0201 or 0x0204 or 0x0207 or 0x020B or 0x0246 && GetFocus() != window )
			SetFocus( window );
		else if ( message == MouseMoveMessage && (wParam & ButtonFlags) == 0 && GetCapture() == window )
			ReleaseCapture();
		else if ( message is KeyDownMessage or SystemKeyDownMessage )
			ReleaseStaleModifiers();

		return DefSubclassProc( window, message, wParam, lParam );
	}

	/// <summary>Clears each modifier SDL reports held while Windows reports its key up.</summary>
	/// <remarks>
	/// A modifier's release can reach another window, such as a browser page or the shell during Alt+Tab, so SDL never sees it and sends
	/// every later key with that modifier: typing still works, but Backspace arrives as Alt+Backspace and does nothing. GetKeyState reads
	/// the key state as of the message being handled, so a modifier held for this key press is kept.
	/// </remarks>
	private static void ReleaseStaleModifiers()
	{
		var held = SDL_GetModState();
		var kept = held;
		foreach ( var (modifier, virtualKey) in Modifiers )
		{
			if ( (held & modifier) != 0 && GetKeyState( virtualKey ) >= 0 )
				kept &= (ushort)~modifier;
		}

		if ( kept != held )
			SDL_SetModState( kept );
	}

	/// <summary>Gets SDL's current keyboard modifier state.</summary>
	[LibraryImport( "SDL3" )]
	private static partial ushort SDL_GetModState();

	/// <summary>Sets SDL's keyboard modifier state.</summary>
	[LibraryImport( "SDL3" )]
	private static partial void SDL_SetModState( ushort modifiers );

	/// <summary>Gets a key's state as of the message being handled; negative while the key is down.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial short GetKeyState( int virtualKey );

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

	/// <summary>Watches out-of-context window events of one thread.</summary>
	[LibraryImport( "user32.dll" )]
	private static unsafe partial nint SetWinEventHook( uint eventMin, uint eventMax, nint module, delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> procedure, uint process, uint thread, uint flags );

	/// <summary>Gets the thread and process that created a window.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial uint GetWindowThreadProcessId( nint window, out uint process );

	/// <summary>Gets a window related to another, such as its owner.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial nint GetWindow( nint window, uint relation );

	/// <summary>Gets a window's class name.</summary>
	[LibraryImport( "user32.dll", EntryPoint = "GetClassNameW" )]
	private static unsafe partial int GetClassName( nint window, char* name, int capacity );

	/// <summary>Gets a window value, such as its extended style.</summary>
	[LibraryImport( "user32.dll", EntryPoint = "GetWindowLongPtrW" )]
	private static partial nint GetWindowLongPtr( nint window, int index );

	/// <summary>Sets a window value, such as its extended style.</summary>
	[LibraryImport( "user32.dll", EntryPoint = "SetWindowLongPtrW" )]
	private static partial nint SetWindowLongPtr( nint window, int index, nint value );
}
