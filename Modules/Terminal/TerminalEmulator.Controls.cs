using System.Globalization;
using System.Text;
using Goo;

namespace Lokn.Modules.Terminal;

/// <summary>Dispatches C0 controls, escape sequences, control sequences and operating system commands.</summary>
internal sealed partial class TerminalEmulator
{
	/// <summary>Defines the primary device attributes reply: a VT220 with ANSI color.</summary>
	private const string PrimaryAttributes = "\x1b[?62;22c";

	/// <summary>Defines the secondary device attributes reply.</summary>
	private const string SecondaryAttributes = "\x1b[>0;10;1c";

	/// <summary>Executes a C0 control character.</summary>
	private void Execute( char control )
	{
		switch ( control )
		{
			case '\b':
				if ( _isWrapPending )
					_isWrapPending = false;
				else if ( CursorColumn > 0 )
					CursorColumn--;
				break;
			case '\t':
				TabForward( 1 );
				break;
			case '\n' or '\v' or '\f':
				LineFeed();
				if ( IsNewLineMode )
					CursorColumn = 0;
				break;
			case '\r':
				CursorColumn = 0;
				_isWrapPending = false;
				break;
			case '\x0e':
				_charset = 1;
				break;
			case '\x0f':
				_charset = 0;
				break;
		}
	}

	/// <summary>Executes an escape sequence.</summary>
	private void DispatchEscape( char final )
	{
		if ( _intermediates.Length > 0 )
		{
			switch ( _intermediates[0] )
			{
				case '(':
					_isGraphicsCharset[0] = final == '0';
					break;
				case ')':
					_isGraphicsCharset[1] = final == '0';
					break;
				case '#' when final == '8':
					// DECALN fills the screen with E for alignment tests.
					foreach ( var line in _lines )
					{
						for ( var column = 0; column < Columns; column++ )
							line.Cells[column] = new( "E", CellStyle.Default );
					}

					_top = 0;
					_bottom = Rows - 1;
					MoveTo( 0, 0 );
					break;
			}

			return;
		}

		switch ( final )
		{
			case '7':
				SaveCursor();
				break;
			case '8':
				RestoreCursor();
				break;
			case 'D':
				LineFeed();
				break;
			case 'E':
				CursorColumn = 0;
				LineFeed();
				break;
			case 'H':
				_tabStops[CursorColumn] = true;
				break;
			case 'M':
				ReverseIndex();
				break;
			case 'c':
				Reset();
				break;
			case '=':
				IsApplicationKeypad = true;
				break;
			case '>':
				IsApplicationKeypad = false;
				break;
		}
	}

	/// <summary>Executes a control sequence.</summary>
	private void DispatchCsi( char final )
	{
		if ( _intermediates.Length > 0 )
		{
			DispatchCsiIntermediate( final );
			return;
		}

		if ( _marker == '?' )
		{
			DispatchPrivate( final );
			return;
		}

		if ( _marker == '>' )
		{
			if ( final == 'c' )
				Reply( SecondaryAttributes );

			return;
		}

		if ( _marker != '\0' )
			return;

		switch ( final )
		{
			case '@':
				InsertCells( ReadCount( 0 ) );
				break;
			case 'A':
				MoveVertically( -ReadCount( 0 ) );
				break;
			case 'B' or 'e':
				MoveVertically( ReadCount( 0 ) );
				break;
			case 'C' or 'a':
				MoveTo( CursorRow, CursorColumn + ReadCount( 0 ) );
				break;
			case 'D':
				MoveTo( CursorRow, CursorColumn - ReadCount( 0 ) );
				break;
			case 'E':
				MoveVertically( ReadCount( 0 ) );
				CursorColumn = 0;
				break;
			case 'F':
				MoveVertically( -ReadCount( 0 ) );
				CursorColumn = 0;
				break;
			case 'G' or '`':
				MoveTo( CursorRow, ReadCount( 0 ) - 1 );
				break;
			case 'H' or 'f':
				MoveTo( ReadCount( 0 ) - 1 + (_isOrigin ? _top : 0), ReadCount( 1 ) - 1 );
				break;
			case 'I':
				TabForward( ReadCount( 0 ) );
				break;
			case 'J':
				EraseDisplay( ReadParameter( 0, 0 ) );
				break;
			case 'K':
				EraseLine( ReadParameter( 0, 0 ) );
				break;
			case 'L':
				InsertLines( ReadCount( 0 ) );
				break;
			case 'M':
				DeleteLines( ReadCount( 0 ) );
				break;
			case 'P':
				DeleteCells( ReadCount( 0 ) );
				break;
			case 'S':
				ScrollUp( ReadCount( 0 ) );
				break;
			case 'T':
				ScrollDown( ReadCount( 0 ) );
				break;
			case 'X':
				EraseCells( CursorColumn, CursorColumn + ReadCount( 0 ) );
				_isWrapPending = false;
				break;
			case 'Z':
				TabBackward( ReadCount( 0 ) );
				break;
			case 'b':
				for ( var count = Math.Min( ReadCount( 0 ), Columns * Rows ); count > 0; count-- )
					Print( _lastPrinted );
				break;
			case 'c':
				if ( ReadParameter( 0, 0 ) == 0 )
					Reply( PrimaryAttributes );
				break;
			case 'd':
				MoveTo( ReadCount( 0 ) - 1 + (_isOrigin ? _top : 0), CursorColumn );
				break;
			case 'g':
				ClearTabStops( ReadParameter( 0, 0 ) );
				break;
			case 'h' or 'l':
				UpdateAnsiModes( final == 'h' );
				break;
			case 'm':
				UpdateStyle();
				break;
			case 'n':
				ReportStatus( isPrivate: false );
				break;
			case 'r':
				UpdateScrollRegion();
				break;
			case 's':
				SaveCursor();
				break;
			case 't':
				if ( ReadParameter( 0, 0 ) == 18 )
					Reply( $"\x1b[8;{Rows};{Columns}t" );
				break;
			case 'u':
				RestoreCursor();
				break;
		}
	}

	/// <summary>Executes a control sequence with intermediate characters.</summary>
	private void DispatchCsiIntermediate( char final )
	{
		switch ( _intermediates, final, _marker )
		{
			case (" ", 'q', '\0'):
				// DECSCUSR: 0-2 block, 3-4 underline, 5-6 bar; odd values blink, drawn steady here.
				CursorShape = ReadParameter( 0, 0 ) switch
				{
					3 or 4 => CursorShape.Underline,
					5 or 6 => CursorShape.Bar,
					_ => CursorShape.Block,
				};
				break;
			case ("!", 'p', '\0'):
				ResetModes();
				_savedPrimary = SavedCursor.Home;
				_savedAlternate = SavedCursor.Home;
				break;
			case ("$", 'p', _):
				ReportMode( ReadParameter( 0, 0 ), _marker == '?' );
				break;
		}
	}

	/// <summary>Executes a DEC private control sequence.</summary>
	private void DispatchPrivate( char final )
	{
		switch ( final )
		{
			case 'h' or 'l':
				for ( var index = 0; index < Math.Max( 1, _parameterCount ); index++ )
					UpdatePrivateMode( ReadParameter( index, 0 ), final == 'h' );
				break;
			case 'n':
				ReportStatus( isPrivate: true );
				break;
			case 'J':
				EraseDisplay( ReadParameter( 0, 0 ) );
				break;
			case 'K':
				EraseLine( ReadParameter( 0, 0 ) );
				break;
		}
	}

	/// <summary>Sets or resets ANSI modes, SM and RM.</summary>
	private void UpdateAnsiModes( bool isSet )
	{
		for ( var index = 0; index < _parameterCount; index++ )
		{
			switch ( ReadParameter( index, 0 ) )
			{
				case 4:
					_isInsert = isSet;
					break;
				case 20:
					IsNewLineMode = isSet;
					break;
			}
		}
	}

	/// <summary>Sets or resets one DEC private mode, DECSET and DECRST.</summary>
	private void UpdatePrivateMode( int mode, bool isSet )
	{
		switch ( mode )
		{
			case 1:
				IsApplicationCursorKeys = isSet;
				break;
			case 6:
				_isOrigin = isSet;
				MoveTo( isSet ? _top : 0, 0 );
				break;
			case 7:
				_isAutoWrap = isSet;
				break;
			case 9:
				MouseTracking = isSet ? MouseTracking.Press : MouseTracking.None;
				break;
			case 25:
				IsCursorVisible = isSet;
				break;
			case 47:
				UpdateAlternateScreen( isSet, clears: false );
				break;
			case 1000:
				MouseTracking = isSet ? MouseTracking.PressRelease : MouseTracking.None;
				break;
			case 1002:
				MouseTracking = isSet ? MouseTracking.Drag : MouseTracking.None;
				break;
			case 1003:
				MouseTracking = isSet ? MouseTracking.Motion : MouseTracking.None;
				break;
			case 1004:
				IsFocusReporting = isSet;
				break;
			case 1006:
				IsSgrMouse = isSet;
				break;
			case 1047:
				if ( !isSet && IsAlternateScreen )
					EraseRows( 0, Rows );

				UpdateAlternateScreen( isSet, clears: false );
				break;
			case 1048:
				if ( isSet )
					SaveCursor();
				else
					RestoreCursor();
				break;
			case 1049:
				if ( isSet )
				{
					if ( !IsAlternateScreen )
					{
						SaveCursor();
						UpdateAlternateScreen( true, clears: true );
					}
				}
				else if ( IsAlternateScreen )
				{
					UpdateAlternateScreen( false, clears: false );
					RestoreCursor();
				}
				break;
			case 2004:
				IsBracketedPaste = isSet;
				break;
		}
	}

	/// <summary>Replies whether a mode is set, DECRQM.</summary>
	private void ReportMode( int mode, bool isPrivate )
	{
		bool? state = isPrivate
			? mode switch
			{
				1 => IsApplicationCursorKeys,
				6 => _isOrigin,
				7 => _isAutoWrap,
				9 => MouseTracking == MouseTracking.Press,
				25 => IsCursorVisible,
				47 or 1047 or 1049 => IsAlternateScreen,
				1000 => MouseTracking == MouseTracking.PressRelease,
				1002 => MouseTracking == MouseTracking.Drag,
				1003 => MouseTracking == MouseTracking.Motion,
				1004 => IsFocusReporting,
				1006 => IsSgrMouse,
				2004 => IsBracketedPaste,
				_ => null,
			}
			: mode switch
			{
				4 => _isInsert,
				20 => IsNewLineMode,
				_ => null,
			};
		var value = state switch
		{
			true => 1,
			false => 2,
			null => 0,
		};
		Reply( $"\x1b[{(isPrivate ? "?" : "")}{mode};{value}$y" );
	}

	/// <summary>Replies to a device status report, DSR.</summary>
	private void ReportStatus( bool isPrivate )
	{
		switch ( ReadParameter( 0, 0 ) )
		{
			case 5:
				Reply( "\x1b[0n" );
				break;
			case 6:
				var row = CursorRow - (_isOrigin ? _top : 0) + 1;
				Reply( $"\x1b[{(isPrivate ? "?" : "")}{row};{CursorColumn + 1}R" );
				break;
		}
	}

	/// <summary>Sets the scroll region and homes the cursor, DECSTBM.</summary>
	private void UpdateScrollRegion()
	{
		var top = ReadCount( 0 ) - 1;
		var bottom = Math.Min( ReadParameter( 1, Rows ), Rows ) - 1;
		if ( bottom <= 0 )
			bottom = Rows - 1;
		if ( top >= bottom )
			return;

		_top = top;
		_bottom = bottom;
		MoveTo( _isOrigin ? _top : 0, 0 );
	}

	/// <summary>Clears tab stops, TBC.</summary>
	private void ClearTabStops( int mode )
	{
		if ( mode == 0 )
			_tabStops[CursorColumn] = false;
		else if ( mode == 3 )
			Array.Clear( _tabStops );
	}

	/// <summary>Erases part or all of the screen, or history, ED.</summary>
	private void EraseDisplay( int mode )
	{
		switch ( mode )
		{
			case 0:
				EraseCells( CursorColumn, Columns );
				EraseRows( CursorRow + 1, Rows );
				break;
			case 1:
				EraseRows( 0, CursorRow );
				EraseCells( 0, CursorColumn + 1 );
				break;
			case 2:
				EraseRows( 0, Rows );
				break;
			case 3:
				History.Clear();
				break;
		}

		_isWrapPending = false;
	}

	/// <summary>Erases part or all of the cursor line, EL.</summary>
	private void EraseLine( int mode )
	{
		switch ( mode )
		{
			case 0:
				EraseCells( CursorColumn, Columns );
				break;
			case 1:
				EraseCells( 0, CursorColumn + 1 );
				break;
			case 2:
				EraseCells( 0, Columns );
				break;
		}

		_isWrapPending = false;
	}

	/// <summary>Applies select graphic rendition parameters, SGR.</summary>
	private void UpdateStyle()
	{
		if ( _parameterCount == 0 )
		{
			_style = CellStyle.Default;
			return;
		}

		var style = _style;
		for ( var index = 0; index < _parameterCount; index++ )
		{
			if ( _isSubParameter[index] )
				continue;

			var parameter = ReadParameter( index, 0 );
			switch ( parameter )
			{
				case 0:
					style = CellStyle.Default;
					break;
				case 1:
					style = style with { Flags = style.Flags | CellFlags.Bold };
					break;
				case 2:
					style = style with { Flags = style.Flags | CellFlags.Dim };
					break;
				case 3:
					style = style with { Flags = style.Flags | CellFlags.Italic };
					break;
				case 4:
					// 4:0 turns underline off; other underline styles draw a single line.
					var isOff = index + 1 < _parameterCount && _isSubParameter[index + 1] && ReadParameter( index + 1, 1 ) == 0;
					style = style with { Flags = isOff ? style.Flags & ~CellFlags.Underline : style.Flags | CellFlags.Underline };
					break;
				case 5 or 6:
					style = style with { Flags = style.Flags | CellFlags.Blink };
					break;
				case 7:
					style = style with { Flags = style.Flags | CellFlags.Inverse };
					break;
				case 8:
					style = style with { Flags = style.Flags | CellFlags.Hidden };
					break;
				case 9:
					style = style with { Flags = style.Flags | CellFlags.Strikethrough };
					break;
				case 21:
					style = style with { Flags = style.Flags | CellFlags.Underline };
					break;
				case 22:
					style = style with { Flags = style.Flags & ~(CellFlags.Bold | CellFlags.Dim) };
					break;
				case 23:
					style = style with { Flags = style.Flags & ~CellFlags.Italic };
					break;
				case 24:
					style = style with { Flags = style.Flags & ~CellFlags.Underline };
					break;
				case 25:
					style = style with { Flags = style.Flags & ~CellFlags.Blink };
					break;
				case 27:
					style = style with { Flags = style.Flags & ~CellFlags.Inverse };
					break;
				case 28:
					style = style with { Flags = style.Flags & ~CellFlags.Hidden };
					break;
				case 29:
					style = style with { Flags = style.Flags & ~CellFlags.Strikethrough };
					break;
				case >= 30 and <= 37:
					style = style with { Foreground = parameter - 30 };
					break;
				case 38:
					style = style with { Foreground = ReadExtendedColor( ref index, style.Foreground ) };
					break;
				case 39:
					style = style with { Foreground = TerminalColor.Default };
					break;
				case >= 40 and <= 47:
					style = style with { Background = parameter - 40 };
					break;
				case 48:
					style = style with { Background = ReadExtendedColor( ref index, style.Background ) };
					break;
				case 49:
					style = style with { Background = TerminalColor.Default };
					break;
				case 58:
					// Underline color is read so its parameters are skipped; underlines take the text color.
					_ = ReadExtendedColor( ref index, TerminalColor.Default );
					break;
				case >= 90 and <= 97:
					style = style with { Foreground = parameter - 90 + 8 };
					break;
				case >= 100 and <= 107:
					style = style with { Background = parameter - 100 + 8 };
					break;
			}
		}

		_style = style;
	}

	/// <summary>Reads a 256-color or 24-bit color after 38, 48 or 58, in colon or semicolon form, advancing past it.</summary>
	private int ReadExtendedColor( ref int index, int fallback )
	{
		if ( index + 1 < _parameterCount && _isSubParameter[index + 1] )
		{
			var last = index + 1;
			while ( last + 1 < _parameterCount && _isSubParameter[last + 1] )
				last++;

			var kind = ReadParameter( index + 1, 0 );
			var count = last - index;
			index = last;
			return kind switch
			{
				5 when count >= 2 => Math.Clamp( ReadParameter( last, 0 ), 0, 255 ),

				// 38:2:colorspace:r:g:b names a color space first; 38:2:r:g:b does not.
				2 when count >= 4 => TerminalColor.FromRgb( ReadParameter( last - 2, 0 ), ReadParameter( last - 1, 0 ), ReadParameter( last, 0 ) ),
				_ => fallback,
			};
		}

		switch ( ReadParameter( index + 1, 0 ) )
		{
			case 5:
				var color = Math.Clamp( ReadParameter( index + 2, 0 ), 0, 255 );
				index += 2;
				return color;
			case 2:
				var rgb = TerminalColor.FromRgb( ReadParameter( index + 2, 0 ), ReadParameter( index + 3, 0 ), ReadParameter( index + 4, 0 ) );
				index += 4;
				return rgb;
			default:
				index++;
				return fallback;
		}
	}

	/// <summary>Executes an operating system command: title, working directory, color queries and clipboard writes.</summary>
	private void DispatchOsc()
	{
		var command = _osc.ToString();
		_osc.Clear();
		var separator = command.IndexOf( ';' );
		if ( separator < 0 || !int.TryParse( command.AsSpan( 0, separator ), NumberStyles.None, CultureInfo.InvariantCulture, out var code ) )
			return;

		var argument = command[(separator + 1)..];
		switch ( code )
		{
			case 0 or 2:
				Title = argument;
				break;
			case 7:
				if ( Uri.TryCreate( argument, UriKind.Absolute, out var uri ) && uri.IsFile )
				{
					// file://host/C:/dir names a local path; the host is the shell's machine, not a share.
					var path = Uri.UnescapeDataString( uri.AbsolutePath );
					if ( path is ['/', _, ':', ..] )
						path = path[1..];

					WorkingDirectory = path is [_, ':', ..] ? Path.GetFullPath( path ) : path;
				}
				break;
			case 9 when argument.StartsWith( "9;", StringComparison.Ordinal ):
				// ConEmu's current directory report, which PowerShell prompts commonly emit.
				WorkingDirectory = argument[2..].Trim( '"' );
				break;
			case 10 when argument == "?":
				Reply( $"\x1b]10;{FormatColor( DefaultForeground )}\x1b\\" );
				break;
			case 11 when argument == "?":
				Reply( $"\x1b]11;{FormatColor( DefaultBackground )}\x1b\\" );
				break;
			case 52:
				var data = argument[(argument.IndexOf( ';' ) + 1)..];
				if ( data != "?" && TryDecodeBase64( data ) is { } text )
					Copied?.Invoke( text );
				break;
		}
	}

	/// <summary>Formats a color as an X11 rgb: specification.</summary>
	private static string FormatColor( Color color )
	{
		static string Channel( float value )
		{
			var byteValue = (int)Math.Round( value * 255 );
			return $"{byteValue:x2}{byteValue:x2}";
		}

		return $"rgb:{Channel( color.R )}/{Channel( color.G )}/{Channel( color.B )}";
	}

	/// <summary>Decodes base64 UTF-8 text, or returns null when it is malformed.</summary>
	private static string? TryDecodeBase64( string data )
	{
		var bytes = new byte[data.Length * 3 / 4 + 3];
		return Convert.TryFromBase64String( data, bytes, out var count ) ? Encoding.UTF8.GetString( bytes, 0, count ) : null;
	}
}
