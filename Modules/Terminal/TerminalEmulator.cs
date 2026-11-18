using System.Text;
using Goo;

namespace Lokn.Modules.Terminal;

/// <summary>Selects which pointer events a program asked to receive.</summary>
internal enum MouseTracking
{
	/// <summary>The terminal keeps the pointer for selection and scrolling.</summary>
	None,

	/// <summary>Button presses only, mode 9.</summary>
	Press,

	/// <summary>Presses and releases, mode 1000.</summary>
	PressRelease,

	/// <summary>Presses, releases and motion while a button is held, mode 1002.</summary>
	Drag,

	/// <summary>Every event including motion without buttons, mode 1003.</summary>
	Motion,
}

/// <summary>Selects how the cursor is drawn, DECSCUSR.</summary>
internal enum CursorShape
{
	/// <summary>A filled cell.</summary>
	Block,

	/// <summary>A line under the cell.</summary>
	Underline,

	/// <summary>A line at the cell's left edge.</summary>
	Bar,
}

/// <summary>Emulates an xterm-compatible VT terminal: parses output into a cell grid with history, modes and replies.</summary>
/// <remarks>Not thread-safe; feed it and read it on one thread.</remarks>
internal sealed partial class TerminalEmulator
{
	/// <summary>Defines the most parameters kept for one control sequence.</summary>
	private const int MaxParameters = 32;

	/// <summary>Defines the largest numeric parameter kept.</summary>
	private const int MaxParameterValue = 65535;

	/// <summary>Defines the longest operating system command kept, enough for clipboard writes.</summary>
	private const int MaxStringLength = 1 << 20;

	/// <summary>Selects the parser state between input characters.</summary>
	private enum ParseState
	{
		/// <summary>Printing text and executing controls.</summary>
		Ground,

		/// <summary>After ESC.</summary>
		Escape,

		/// <summary>After ESC and intermediate characters.</summary>
		EscapeIntermediate,

		/// <summary>After CSI, before parameters.</summary>
		CsiEntry,

		/// <summary>Reading CSI parameters.</summary>
		CsiParameter,

		/// <summary>Reading CSI intermediate characters.</summary>
		CsiIntermediate,

		/// <summary>Skipping a malformed CSI sequence to its final character.</summary>
		CsiIgnore,

		/// <summary>Reading an operating system command.</summary>
		OscString,

		/// <summary>Skipping a device control, privacy or application string.</summary>
		StringIgnore,
	}

	/// <summary>Stores the numeric parameters of the current sequence; -1 marks an omitted value.</summary>
	private readonly int[] _parameters = new int[MaxParameters];

	/// <summary>Stores which parameters were introduced by a colon, as sub-parameters of the one before.</summary>
	private readonly bool[] _isSubParameter = new bool[MaxParameters];

	/// <summary>Collects the current operating system command.</summary>
	private readonly StringBuilder _osc = new();

	/// <summary>Stores the parser state.</summary>
	private ParseState _state;

	/// <summary>Stores the number of complete parameters.</summary>
	private int _parameterCount;

	/// <summary>Stores the parameter being read, or -1 while it has no digits.</summary>
	private int _parameter = -1;

	/// <summary>Tracks whether the parameter being read follows a colon.</summary>
	private bool _isNextSubParameter;

	/// <summary>Stores the private marker of the current CSI sequence, such as '?', or zero.</summary>
	private char _marker;

	/// <summary>Stores the intermediate characters of the current sequence; at most two are kept.</summary>
	private string _intermediates = "";

	/// <summary>Stores a high surrogate awaiting its pair.</summary>
	private char _highSurrogate;

	/// <summary>Gets the width in cells.</summary>
	internal int Columns
	{
		get; private set;
	}

	/// <summary>Gets the height in cells.</summary>
	internal int Rows
	{
		get; private set;
	}

	/// <summary>Gets the main screen's scrolled-off lines.</summary>
	internal TerminalHistory History
	{
		get;
	}

	/// <summary>Gets the cursor row within the screen.</summary>
	internal int CursorRow
	{
		get; private set;
	}

	/// <summary>Gets the cursor column within the screen.</summary>
	internal int CursorColumn
	{
		get; private set;
	}

	/// <summary>Gets whether the cursor is shown, DECTCEM.</summary>
	internal bool IsCursorVisible { get; private set; } = true;

	/// <summary>Gets the cursor's drawn shape.</summary>
	internal CursorShape CursorShape
	{
		get; private set;
	}

	/// <summary>Gets whether the alternate screen, which has no history, is shown.</summary>
	internal bool IsAlternateScreen => _lines == _alternate;

	/// <summary>Gets whether cursor keys send application sequences, DECCKM.</summary>
	internal bool IsApplicationCursorKeys
	{
		get; private set;
	}

	/// <summary>Gets whether the keypad sends application sequences, DECKPAM.</summary>
	internal bool IsApplicationKeypad
	{
		get; private set;
	}

	/// <summary>Gets whether Enter sends a carriage return and a line feed, LNM.</summary>
	internal bool IsNewLineMode
	{
		get; private set;
	}

	/// <summary>Gets whether pasted text is bracketed by markers, mode 2004.</summary>
	internal bool IsBracketedPaste
	{
		get; private set;
	}

	/// <summary>Gets whether focus changes are reported, mode 1004.</summary>
	internal bool IsFocusReporting
	{
		get; private set;
	}

	/// <summary>Gets which pointer events are reported.</summary>
	internal MouseTracking MouseTracking
	{
		get; private set;
	}

	/// <summary>Gets whether pointer reports use the SGR encoding, mode 1006.</summary>
	internal bool IsSgrMouse
	{
		get; private set;
	}

	/// <summary>Gets the window title set by the program, or empty.</summary>
	internal string Title { get; private set; } = "";

	/// <summary>Gets the working directory the shell last reported, or null.</summary>
	internal string? WorkingDirectory
	{
		get; private set;
	}

	/// <summary>Gets or sets the default foreground reported to color queries.</summary>
	internal Color DefaultForeground { get; set; } = Color.White;

	/// <summary>Gets or sets the default background reported to color queries.</summary>
	internal Color DefaultBackground { get; set; } = Color.Black;

	/// <summary>Receives replies to status and capability queries, to be written back to the program.</summary>
	internal event Action<string>? Replied;

	/// <summary>Receives text a program asked to copy to the clipboard.</summary>
	internal event Action<string>? Copied;

	/// <summary>Creates a blank terminal.</summary>
	internal TerminalEmulator( int columns, int rows, int historyCapacity )
	{
		Columns = Math.Max( 1, columns );
		Rows = Math.Max( 1, rows );
		History = new( historyCapacity );
		_primary = BuildLines( Rows );
		_alternate = BuildLines( Rows );
		_lines = _primary;
		_bottom = Rows - 1;
		_tabStops = BuildTabStops( Columns );
	}

	/// <summary>Parses program output, updating the grid and raising replies.</summary>
	internal void Feed( string text )
	{
		foreach ( var character in text )
			Feed( character );
	}

	/// <summary>Advances the parser by one UTF-16 unit.</summary>
	private void Feed( char character )
	{
		// CAN and SUB abort any sequence; ESC starts one from any state.
		if ( character is '\x18' or '\x1a' )
		{
			_state = ParseState.Ground;
			return;
		}

		if ( character == '\x1b' )
		{
			if ( _state == ParseState.OscString )
				DispatchOsc();

			BeginSequence( ParseState.Escape );
			return;
		}

		switch ( _state )
		{
			case ParseState.Ground:
				FeedGround( character );
				break;
			case ParseState.Escape:
				FeedEscape( character );
				break;
			case ParseState.EscapeIntermediate:
				FeedEscapeIntermediate( character );
				break;
			case ParseState.CsiEntry:
			case ParseState.CsiParameter:
			case ParseState.CsiIntermediate:
			case ParseState.CsiIgnore:
				FeedCsi( character );
				break;
			case ParseState.OscString:
				FeedOsc( character );
				break;
			case ParseState.StringIgnore:
				break;
		}
	}

	/// <summary>Prints text, joining surrogate pairs, and executes controls.</summary>
	private void FeedGround( char character )
	{
		if ( character < ' ' )
		{
			Execute( character );
			return;
		}

		if ( character is '\x7f' or (>= '\x80' and <= '\x9f') )
			return;

		if ( char.IsHighSurrogate( character ) )
		{
			_highSurrogate = character;
			return;
		}

		if ( char.IsLowSurrogate( character ) )
		{
			if ( _highSurrogate != 0 )
				Print( char.ConvertToUtf32( _highSurrogate, character ) );

			_highSurrogate = '\0';
			return;
		}

		_highSurrogate = '\0';
		Print( character );
	}

	/// <summary>Reads the character after ESC.</summary>
	private void FeedEscape( char character )
	{
		if ( character < ' ' )
		{
			Execute( character );
			return;
		}

		switch ( character )
		{
			case '[':
				BeginSequence( ParseState.CsiEntry );
				return;
			case ']':
				BeginSequence( ParseState.OscString );
				return;
			case 'P' or 'X' or '^' or '_':
				BeginSequence( ParseState.StringIgnore );
				return;
		}

		if ( character is >= ' ' and <= '/' )
		{
			_intermediates += character;
			_state = ParseState.EscapeIntermediate;
			return;
		}

		_state = ParseState.Ground;
		DispatchEscape( character );
	}

	/// <summary>Reads an escape sequence's intermediate and final characters.</summary>
	private void FeedEscapeIntermediate( char character )
	{
		if ( character < ' ' )
		{
			Execute( character );
			return;
		}

		if ( character is >= ' ' and <= '/' )
		{
			if ( _intermediates.Length < 2 )
				_intermediates += character;

			return;
		}

		_state = ParseState.Ground;
		DispatchEscape( character );
	}

	/// <summary>Reads a control sequence's marker, parameters, intermediates and final character.</summary>
	private void FeedCsi( char character )
	{
		if ( character < ' ' )
		{
			Execute( character );
			return;
		}

		if ( character is >= '@' and <= '~' )
		{
			var isIgnored = _state == ParseState.CsiIgnore;
			_state = ParseState.Ground;
			if ( !isIgnored )
			{
				EndParameter();
				DispatchCsi( character );
			}

			return;
		}

		if ( _state == ParseState.CsiIgnore )
			return;

		if ( character is >= ' ' and <= '/' )
		{
			if ( _intermediates.Length < 2 )
				_intermediates += character;

			_state = ParseState.CsiIntermediate;
			return;
		}

		if ( _state == ParseState.CsiIntermediate )
		{
			_state = ParseState.CsiIgnore;
			return;
		}

		if ( character is >= '<' and <= '?' )
		{
			if ( _state == ParseState.CsiEntry )
			{
				_marker = character;
				_state = ParseState.CsiParameter;
			}
			else
			{
				_state = ParseState.CsiIgnore;
			}

			return;
		}

		_state = ParseState.CsiParameter;
		if ( character is >= '0' and <= '9' )
		{
			_parameter = Math.Min( Math.Max( _parameter, 0 ) * 10 + (character - '0'), MaxParameterValue );
			return;
		}

		// A semicolon separates parameters; a colon separates sub-parameters of the one before it.
		EndParameter( isSeparator: true );
		_isNextSubParameter = character == ':';
	}

	/// <summary>Collects an operating system command until BEL; ESC ends it in <see cref="Feed(char)"/>.</summary>
	private void FeedOsc( char character )
	{
		if ( character == '\a' )
		{
			DispatchOsc();
			_state = ParseState.Ground;
			return;
		}

		if ( character >= ' ' && _osc.Length < MaxStringLength )
			_osc.Append( character );
	}

	/// <summary>Resets sequence collection and enters a state.</summary>
	private void BeginSequence( ParseState state )
	{
		_state = state;
		_parameterCount = 0;
		_parameter = -1;
		_isNextSubParameter = false;
		_marker = '\0';
		_intermediates = "";
		_osc.Clear();
	}

	/// <summary>Completes the parameter being read; a separator always completes one, even an omitted one.</summary>
	private void EndParameter( bool isSeparator = false )
	{
		if ( _parameterCount < MaxParameters && (isSeparator || _parameter >= 0 || _parameterCount > 0) )
		{
			_parameters[_parameterCount] = _parameter;
			_isSubParameter[_parameterCount] = _isNextSubParameter;
			_parameterCount++;
		}

		_parameter = -1;
		_isNextSubParameter = false;
	}

	/// <summary>Gets a parameter, or a default when it is missing or omitted.</summary>
	private int ReadParameter( int index, int defaultValue ) => index < _parameterCount && _parameters[index] >= 0 ? _parameters[index] : defaultValue;

	/// <summary>Gets a count parameter, where zero and omission both mean one.</summary>
	private int ReadCount( int index ) => Math.Max( 1, ReadParameter( index, 1 ) );

	/// <summary>Sends a reply to the program.</summary>
	private void Reply( string text ) => Replied?.Invoke( text );
}
