using System.Runtime.InteropServices;
using Goo;

namespace Lokn.Modules.Browser;

/// <summary>Maps Windows virtual keys to Goo keys, so Lokn's shortcuts still work while a web page has the keyboard, and moves Windows' keyboard focus between Lokn's window and its pages.</summary>
internal static partial class BrowserKeys
{
	/// <summary>Defines the virtual key of Shift.</summary>
	private const int ShiftKey = 0x10;

	/// <summary>Defines the virtual key of Ctrl.</summary>
	private const int ControlKey = 0x11;

	/// <summary>Defines the virtual key of Alt.</summary>
	private const int MenuKey = 0x12;

	/// <summary>Maps the virtual keys outside letters, digits and function keys that Lokn shortcuts use.</summary>
	private static readonly Dictionary<uint, Key> Others = new()
	{
		[0x09] = Key.Tab,
		[0x0D] = Key.Enter,
		[0xBA] = Key.Semicolon,
		[0xBB] = Key.Equal,
		[0xBC] = Key.Comma,
		[0xBD] = Key.Minus,
		[0xBE] = Key.Period,
		[0xBF] = Key.Slash,
		[0xC0] = Key.GraveAccent,
		[0xDB] = Key.LeftBracket,
		[0xDC] = Key.BackSlash,
		[0xDD] = Key.RightBracket,
		[0xDE] = Key.Apostrophe,
	};

	/// <summary>Maps a virtual key to a Goo key, or null for keys no shortcut uses.</summary>
	internal static Key? Map( uint virtualKey ) => virtualKey switch
	{
		>= 0x41 and <= 0x5A => Key.A + (int)(virtualKey - 0x41),
		>= 0x30 and <= 0x39 => Key.Number0 + (int)(virtualKey - 0x30),
		>= 0x70 and <= 0x7B => Key.F1 + (int)(virtualKey - 0x70),
		_ => Others.TryGetValue( virtualKey, out var key ) ? key : null,
	};

	/// <summary>Reads the modifiers held now.</summary>
	internal static KeyModifiers ReadModifiers() => new()
	{
		Ctrl = IsDown( ControlKey ),
		Shift = IsDown( ShiftKey ),
		Alt = IsDown( MenuKey ),
	};

	/// <summary>Gives the keyboard to a window.</summary>
	internal static void Focus( nint window ) => SetFocus( window );

	/// <summary>Gives the keyboard back to Lokn's window when a page, or anything else, holds it.</summary>
	internal static void Reclaim( nint window )
	{
		if ( GetFocus() != window )
			SetFocus( window );
	}

	/// <summary>Gives the keyboard back to Lokn's window after a page was hidden or closed, when the page held it or Windows left it in a hidden window inside Lokn's.</summary>
	/// <param name="window">Lokn's window.</param>
	/// <param name="wasFocused">Whether the page reported holding the keyboard before it was hidden or closed.</param>
	/// <remarks>Windows keeps the keyboard on a window that is hidden and drops it when that window closes, so typing would go nowhere. A page still shown, such as another panel's, keeps it.</remarks>
	internal static void Release( nint window, bool wasFocused )
	{
		var focus = GetFocus();
		if ( focus == window )
			return;

		var isInside = focus != 0 && IsChild( window, focus );
		if ( wasFocused ? focus == 0 || isInside : isInside && !IsWindowVisible( focus ) )
			SetFocus( window );
	}

	/// <summary>Reports whether a key is held.</summary>
	private static bool IsDown( int virtualKey ) => (GetKeyState( virtualKey ) & 0x8000) != 0;

	/// <summary>Reads a key's state for the current message.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial short GetKeyState( int virtualKey );

	/// <summary>Sets the keyboard focus.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial nint SetFocus( nint window );

	/// <summary>Gets the window holding the keyboard on this thread's input queue.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial nint GetFocus();

	/// <summary>Reports whether a window is a descendant of another.</summary>
	[LibraryImport( "user32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static partial bool IsChild( nint parent, nint window );

	/// <summary>Reports whether a window and all its ancestors are shown.</summary>
	[LibraryImport( "user32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static partial bool IsWindowVisible( nint window );
}
