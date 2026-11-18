using System.Runtime.InteropServices;
using Goo;

namespace Lokn.Modules.Terminal;

/// <summary>Encodes keys, pointer events and pasted text as the byte sequences an xterm sends.</summary>
internal static partial class TerminalInput
{
	/// <summary>Defines the button code of a pointer release in the original xterm encoding.</summary>
	internal const int ReleaseButton = 3;

	/// <summary>Defines the button code of a wheel step away from the user.</summary>
	internal const int WheelUpButton = 64;

	/// <summary>Defines the button code of a wheel step toward the user.</summary>
	internal const int WheelDownButton = 65;

	/// <summary>Defines the flag added to a button code for motion.</summary>
	internal const int MotionFlag = 32;

	/// <summary>Defines the largest coordinate the original xterm pointer encoding can carry.</summary>
	private const int MaxLegacyCoordinate = 223;

	/// <summary>Defines the Windows virtual-key code of Shift for keyboard state arrays.</summary>
	private const int ShiftKey = 0x10;

	/// <summary>Defines the Windows virtual-key code of Ctrl for keyboard state arrays.</summary>
	private const int ControlKey = 0x11;

	/// <summary>Defines the Windows virtual-key code of Alt for keyboard state arrays.</summary>
	private const int MenuKey = 0x12;

	/// <summary>Defines the key-state bit that marks a key as held.</summary>
	private const byte KeyDown = 0x80;

	/// <summary>Defines the translation flag that leaves dead-key state untouched.</summary>
	private const uint KeepKeyboardState = 0x4;

	/// <summary>Defines the scan-code to virtual-key mapping type.</summary>
	private const uint ScanCodeToVirtualKey = 1;

	/// <summary>Maps physical character keys to their PC set-1 scan codes, so the active layout can name the character.</summary>
	private static readonly Dictionary<Key, uint> ScanCodes = new()
	{
		[Key.Number1] = 0x02, [Key.Number2] = 0x03, [Key.Number3] = 0x04, [Key.Number4] = 0x05, [Key.Number5] = 0x06,
		[Key.Number6] = 0x07, [Key.Number7] = 0x08, [Key.Number8] = 0x09, [Key.Number9] = 0x0A, [Key.Number0] = 0x0B,
		[Key.Minus] = 0x0C, [Key.Equal] = 0x0D, [Key.Q] = 0x10, [Key.W] = 0x11, [Key.E] = 0x12, [Key.R] = 0x13,
		[Key.T] = 0x14, [Key.Y] = 0x15, [Key.U] = 0x16, [Key.I] = 0x17, [Key.O] = 0x18, [Key.P] = 0x19,
		[Key.LeftBracket] = 0x1A, [Key.RightBracket] = 0x1B, [Key.A] = 0x1E, [Key.S] = 0x1F, [Key.D] = 0x20,
		[Key.F] = 0x21, [Key.G] = 0x22, [Key.H] = 0x23, [Key.J] = 0x24, [Key.K] = 0x25, [Key.L] = 0x26,
		[Key.Semicolon] = 0x27, [Key.Apostrophe] = 0x28, [Key.GraveAccent] = 0x29, [Key.BackSlash] = 0x2B,
		[Key.Z] = 0x2C, [Key.X] = 0x2D, [Key.C] = 0x2E, [Key.V] = 0x2F, [Key.B] = 0x30, [Key.N] = 0x31,
		[Key.M] = 0x32, [Key.Comma] = 0x33, [Key.Period] = 0x34, [Key.Slash] = 0x35, [Key.Space] = 0x39,
		[Key.World1] = 0x56,
	};

	/// <summary>Maps physical keys to the US-layout character used when no layout translation is available.</summary>
	private static readonly Dictionary<Key, char> UsCharacters = new()
	{
		[Key.Number1] = '1', [Key.Number2] = '2', [Key.Number3] = '3', [Key.Number4] = '4', [Key.Number5] = '5',
		[Key.Number6] = '6', [Key.Number7] = '7', [Key.Number8] = '8', [Key.Number9] = '9', [Key.Number0] = '0',
		[Key.Minus] = '-', [Key.Equal] = '=', [Key.LeftBracket] = '[', [Key.RightBracket] = ']', [Key.Semicolon] = ';',
		[Key.Apostrophe] = '\'', [Key.GraveAccent] = '`', [Key.BackSlash] = '\\', [Key.Comma] = ',', [Key.Period] = '.',
		[Key.Slash] = '/', [Key.Space] = ' ',
	};

	/// <summary>Encodes a key press, or returns null when the key produces text instead or has no terminal meaning.</summary>
	/// <param name="key">The physical key.</param>
	/// <param name="modifiers">The held modifiers.</param>
	/// <param name="isApplicationCursor">Whether cursor keys use application mode, DECCKM.</param>
	/// <param name="isNewLineMode">Whether Enter sends CR LF, LNM.</param>
	/// <param name="translate">Names the character a key types in the active layout, with Shift and with AltGr.</param>
	internal static string? EncodeKey( Key key, KeyModifiers modifiers, bool isApplicationCursor, bool isNewLineMode, Func<Key, KeyChord, string?> translate )
	{
		var parameter = 1 + (modifiers.Shift ? 1 : 0) + (modifiers.Alt ? 2 : 0) + (modifiers.Ctrl ? 4 : 0);
		var escape = modifiers.Alt ? "\x1b" : "";
		switch ( key )
		{
			case Key.Up:
				return EncodeCursor( 'A', parameter, isApplicationCursor );
			case Key.Down:
				return EncodeCursor( 'B', parameter, isApplicationCursor );
			case Key.Right:
				return EncodeCursor( 'C', parameter, isApplicationCursor );
			case Key.Left:
				return EncodeCursor( 'D', parameter, isApplicationCursor );
			case Key.Home:
				return EncodeCursor( 'H', parameter, isApplicationCursor );
			case Key.End:
				return EncodeCursor( 'F', parameter, isApplicationCursor );
			case Key.Insert:
				return EncodeTilde( 2, parameter );
			case Key.Delete:
				return EncodeTilde( 3, parameter );
			case Key.PageUp:
				return EncodeTilde( 5, parameter );
			case Key.PageDown:
				return EncodeTilde( 6, parameter );
			case >= Key.F1 and <= Key.F4:
				var letter = (char)('P' + (key - Key.F1));
				return parameter == 1 ? $"\x1bO{letter}" : $"\x1b[1;{parameter}{letter}";
			case >= Key.F5 and <= Key.F12:
				int[] codes = [15, 17, 18, 19, 20, 21, 23, 24];
				return EncodeTilde( codes[key - Key.F5], parameter );
			case Key.Enter or Key.KeypadEnter:
				return escape + (isNewLineMode ? "\r\n" : "\r");
			case Key.Tab:
				if ( modifiers.Ctrl )
					return null;

				return modifiers.Shift ? "\x1b[Z" : escape + "\t";
			case Key.Backspace:
				return escape + (modifiers.Ctrl ? "\b" : "\x7f");
			case Key.Escape:
				return escape + "\x1b";
		}

		if ( !modifiers.Ctrl && !modifiers.Alt )
			return null;

		// AltGr arrives as Ctrl+Alt; when the layout types a character with it, that character comes as text input.
		if ( modifiers.Ctrl && modifiers.Alt && translate( key, KeyChord.AltGraph ) is { Length: > 0 } )
			return null;

		if ( modifiers.Ctrl )
		{
			var typed = translate( key, KeyChord.Control );
			return typed is { Length: 1 } && ComputeControl( typed[0] ) is { } control ? escape + control : null;
		}

		return translate( key, modifiers.Shift ? KeyChord.Shift : KeyChord.Plain ) is { Length: > 0 } text ? escape + text : null;
	}

	/// <summary>Names the character a physical key types in the active Windows keyboard layout.</summary>
	internal static unsafe string? TranslateKey( Key key, KeyChord chord )
	{
		if ( !ScanCodes.TryGetValue( key, out var scanCode ) )
			return null;

		var layout = GetKeyboardLayout( 0 );
		var virtualKey = MapVirtualKeyExW( scanCode, ScanCodeToVirtualKey, layout );

		// Ctrl combines with the key's layout letter, so Ctrl+C works on Cyrillic or Greek layouts and follows AZERTY.
		if ( chord == KeyChord.Control && virtualKey is >= 'A' and <= 'Z' )
			return ((char)char.ToLowerInvariant( (char)virtualKey )).ToString();

		var state = stackalloc byte[256];
		new Span<byte>( state, 256 ).Clear();
		if ( chord == KeyChord.Shift )
			state[ShiftKey] = KeyDown;
		if ( chord == KeyChord.AltGraph )
		{
			state[ControlKey] = KeyDown;
			state[MenuKey] = KeyDown;
		}

		const int BufferSize = 8;
		var buffer = stackalloc char[BufferSize];
		var count = virtualKey == 0 ? 0 : ToUnicodeEx( virtualKey, scanCode, state, buffer, BufferSize, KeepKeyboardState, layout );
		if ( count > 0 )
			return new string( buffer, 0, count );

		// A dead key types nothing by itself; with no layout mapping, fall back to the US character.
		if ( count < 0 || chord == KeyChord.AltGraph )
			return null;
		if ( key is >= Key.A and <= Key.Z )
			return ((char)((chord == KeyChord.Shift ? 'A' : 'a') + (key - Key.A))).ToString();

		return UsCharacters.TryGetValue( key, out var character ) ? character.ToString() : null;
	}

	/// <summary>Encodes a pointer event, or returns null when the position cannot be encoded.</summary>
	/// <param name="button">The button code: 0 to 2 for buttons, 64 and 65 for the wheel, plus <see cref="MotionFlag"/> for motion.</param>
	/// <param name="modifiers">The held modifiers.</param>
	/// <param name="column">The zero-based column.</param>
	/// <param name="row">The zero-based row.</param>
	/// <param name="isRelease">Whether a button was released.</param>
	/// <param name="isSgr">Whether the SGR encoding is active.</param>
	internal static string? EncodePointer( int button, KeyModifiers modifiers, int column, int row, bool isRelease, bool isSgr )
	{
		var code = button + (modifiers.Shift ? 4 : 0) + (modifiers.Alt ? 8 : 0) + (modifiers.Ctrl ? 16 : 0);
		if ( isSgr )
			return $"\x1b[<{code};{column + 1};{row + 1}{(isRelease ? 'm' : 'M')}";

		if ( column + 1 > MaxLegacyCoordinate || row + 1 > MaxLegacyCoordinate )
			return null;

		if ( isRelease )
			code = ReleaseButton + (code & ~3 & ~MotionFlag);

		return $"\x1b[M{(char)(32 + code)}{(char)(33 + column)}{(char)(33 + row)}";
	}

	/// <summary>Encodes pasted text: line breaks become carriage returns, and bracketed paste wraps it so it cannot end the bracket early.</summary>
	internal static string EncodePaste( string text, bool isBracketed )
	{
		var normalized = text.Replace( "\r\n", "\r" ).Replace( '\n', '\r' );
		if ( !isBracketed )
			return normalized;

		return $"\x1b[200~{normalized.Replace( "\x1b[201~", "" )}\x1b[201~";
	}

	/// <summary>Encodes a cursor or editing key with the xterm modifier parameter.</summary>
	private static string EncodeCursor( char letter, int parameter, bool isApplicationCursor )
	{
		if ( parameter != 1 )
			return $"\x1b[1;{parameter}{letter}";

		return isApplicationCursor ? $"\x1bO{letter}" : $"\x1b[{letter}";
	}

	/// <summary>Encodes a key reported as a number followed by a tilde.</summary>
	private static string EncodeTilde( int code, int parameter ) => parameter == 1 ? $"\x1b[{code}~" : $"\x1b[{code};{parameter}~";

	/// <summary>Maps a character typed with Ctrl to its control code, as xterm does.</summary>
	private static string? ComputeControl( char character )
	{
		character = char.ToLowerInvariant( character );
		var code = character switch
		{
			>= 'a' and <= 'z' => character - 'a' + 1,
			'@' or '2' or ' ' or '`' => 0,
			'[' or '3' => 0x1B,
			'\\' or '4' => 0x1C,
			']' or '5' => 0x1D,
			'^' or '6' or '~' => 0x1E,
			'_' or '-' or '/' or '7' => 0x1F,
			'8' or '?' => 0x7F,
			_ => -1,
		};
		return code < 0 ? null : ((char)code).ToString();
	}

	/// <summary>Gets the active keyboard layout of a thread; zero is the calling thread.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial nint GetKeyboardLayout( uint thread );

	/// <summary>Maps a scan code to a virtual key in a layout.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial uint MapVirtualKeyExW( uint code, uint mapType, nint layout );

	/// <summary>Translates a key and keyboard state to characters in a layout.</summary>
	[LibraryImport( "user32.dll" )]
	private static unsafe partial int ToUnicodeEx( uint virtualKey, uint scanCode, byte* keyState, char* buffer, int bufferSize, uint flags, nint layout );
}

/// <summary>Selects which modifiers a layout translation applies.</summary>
internal enum KeyChord
{
	/// <summary>No modifiers.</summary>
	Plain,

	/// <summary>Shift.</summary>
	Shift,

	/// <summary>AltGr, which Windows reports as Ctrl and Alt together.</summary>
	AltGraph,

	/// <summary>Ctrl: the layout's letter for the key, falling back to its unshifted character.</summary>
	Control,
}
