using System.Text;

namespace Lokn.Modules.Terminal;

/// <summary>Holds the grid and the operations controls apply to it.</summary>
internal sealed partial class TerminalEmulator
{
	/// <summary>Defines the default distance between tab stops.</summary>
	private const int TabWidth = 8;

	/// <summary>Caches one-character strings for ASCII so printing allocates nothing for them.</summary>
	private static readonly string[] AsciiText = Enumerable.Range( 0, 128 ).Select( code => ((char)code).ToString() ).ToArray();

	/// <summary>Stores the main screen.</summary>
	private TerminalLine[] _primary;

	/// <summary>Stores the alternate screen.</summary>
	private TerminalLine[] _alternate;

	/// <summary>Stores the screen being shown and written.</summary>
	private TerminalLine[] _lines;

	/// <summary>Stores the first row of the scroll region.</summary>
	private int _top;

	/// <summary>Stores the last row of the scroll region.</summary>
	private int _bottom;

	/// <summary>Stores which columns hold a tab stop.</summary>
	private bool[] _tabStops;

	/// <summary>Stores the style applied to printed text.</summary>
	private CellStyle _style = CellStyle.Default;

	/// <summary>Tracks a print in the last column, which wraps only when the next character arrives.</summary>
	private bool _isWrapPending;

	/// <summary>Tracks whether printing past the last column wraps, DECAWM.</summary>
	private bool _isAutoWrap = true;

	/// <summary>Tracks whether rows are relative to the scroll region, DECOM.</summary>
	private bool _isOrigin;

	/// <summary>Tracks whether printing inserts rather than replaces, IRM.</summary>
	private bool _isInsert;

	/// <summary>Tracks whether G0 and G1 use DEC line drawing.</summary>
	private readonly bool[] _isGraphicsCharset = new bool[2];

	/// <summary>Stores which of G0 and G1 prints.</summary>
	private int _charset;

	/// <summary>Stores the last printed grapheme for REP.</summary>
	private int _lastPrinted = ' ';

	/// <summary>Stores DECSC state for the main screen.</summary>
	private SavedCursor _savedPrimary = SavedCursor.Home;

	/// <summary>Stores DECSC state for the alternate screen.</summary>
	private SavedCursor _savedAlternate = SavedCursor.Home;

	/// <summary>Gets the absolute number of the first screen row; history lines have smaller numbers.</summary>
	internal long FirstScreenLine => History.Dropped + History.Count;

	/// <summary>Gets a screen row.</summary>
	internal TerminalLine GetScreenLine( int row ) => _lines[row];

	/// <summary>Gets a line by absolute number from history or the screen, or null once it has scrolled out of history.</summary>
	internal TerminalLine? GetLine( long number )
	{
		var index = number - History.Dropped;
		if ( index < 0 )
			return null;
		if ( index < History.Count )
			return History[(int)index];

		var row = index - History.Count;
		return row < Rows ? _lines[row] : null;
	}

	/// <summary>Builds the text between two cell positions, joining wrapped lines and ending other lines with a line break.</summary>
	internal string BuildText( long startLine, int startColumn, long endLine, int endColumn )
	{
		var text = new StringBuilder();
		for ( var number = startLine; number <= endLine; number++ )
		{
			if ( GetLine( number ) is not { } line )
				continue;

			var first = number == startLine ? startColumn : 0;
			var last = number == endLine ? endColumn + 1 : Math.Max( Columns, line.Cells.Length );
			text.Append( line.BuildText( first, last ) );
			if ( number < endLine && !line.IsWrapped )
				text.Append( "\r\n" );
		}

		return text.ToString();
	}

	/// <summary>Changes the grid size, keeping the cursor's line visible and history in order.</summary>
	internal void Resize( int columns, int rows )
	{
		columns = Math.Max( 1, columns );
		rows = Math.Max( 1, rows );
		if ( columns == Columns && rows == Rows )
			return;

		var isAlternate = IsAlternateScreen;
		var primaryRow = isAlternate ? _savedPrimary.Row : CursorRow;
		_primary = ResizeLines( _primary, columns, rows, ref primaryRow, History );
		var alternateRow = isAlternate ? CursorRow : _savedAlternate.Row;
		_alternate = ResizeLines( _alternate, columns, rows, ref alternateRow, null );
		_lines = isAlternate ? _alternate : _primary;

		Columns = columns;
		Rows = rows;
		CursorRow = isAlternate ? alternateRow : primaryRow;
		CursorColumn = Math.Min( CursorColumn, columns - 1 );
		_savedPrimary = _savedPrimary with { Row = isAlternate ? primaryRow : Math.Min( _savedPrimary.Row, rows - 1 ), Column = Math.Min( _savedPrimary.Column, columns - 1 ) };
		_savedAlternate = _savedAlternate with { Row = Math.Min( _savedAlternate.Row, rows - 1 ), Column = Math.Min( _savedAlternate.Column, columns - 1 ) };
		_isWrapPending = false;
		_top = 0;
		_bottom = rows - 1;
		_tabStops = BuildTabStops( columns );
	}

	/// <summary>Clears the screen, history and modes, RIS.</summary>
	internal void Reset()
	{
		History.Clear();
		_primary = BuildLines( Rows );
		_alternate = BuildLines( Rows );
		_lines = _primary;
		_tabStops = BuildTabStops( Columns );
		_savedPrimary = SavedCursor.Home;
		_savedAlternate = SavedCursor.Home;
		CursorRow = 0;
		CursorColumn = 0;
		ResetModes();
		Title = "";
	}

	/// <summary>Restores the modes a soft reset clears, DECSTR.</summary>
	private void ResetModes()
	{
		_style = CellStyle.Default;
		_isWrapPending = false;
		_isAutoWrap = true;
		_isOrigin = false;
		_isInsert = false;
		_isGraphicsCharset[0] = false;
		_isGraphicsCharset[1] = false;
		_charset = 0;
		_top = 0;
		_bottom = Rows - 1;
		IsCursorVisible = true;
		CursorShape = CursorShape.Block;
		IsApplicationCursorKeys = false;
		IsApplicationKeypad = false;
		IsNewLineMode = false;
		IsBracketedPaste = false;
		IsFocusReporting = false;
		MouseTracking = MouseTracking.None;
		IsSgrMouse = false;
	}

	/// <summary>Prints one code point at the cursor, wrapping and joining combining characters to the previous cell.</summary>
	private void Print( int codePoint )
	{
		if ( codePoint < 0x80 && _isGraphicsCharset[_charset] )
			codePoint = TerminalWidth.MapDecGraphics( codePoint );

		var width = TerminalWidth.Measure( codePoint );
		if ( width == 0 )
		{
			JoinPrevious( codePoint );
			return;
		}

		_lastPrinted = codePoint;
		var text = codePoint < 0x80 ? AsciiText[codePoint] : char.ConvertFromUtf32( codePoint );
		if ( _isWrapPending )
		{
			_isWrapPending = false;
			if ( _isAutoWrap )
				WrapLine();
		}

		if ( width == 2 && CursorColumn == Columns - 1 )
		{
			if ( !_isAutoWrap || Columns < 2 )
				return;

			_lines[CursorRow].Cells[CursorColumn] = new( " ", _style );
			WrapLine();
		}

		if ( _isInsert )
			InsertCells( width );

		var line = _lines[CursorRow];
		SplitWide( line, CursorColumn );
		line.Cells[CursorColumn] = new( text, _style );
		if ( width == 2 )
		{
			SplitWide( line, CursorColumn + 1 );
			line.Cells[CursorColumn + 1] = new( "", _style );
		}

		CursorColumn += width;
		if ( CursorColumn >= Columns )
		{
			CursorColumn = Columns - 1;
			_isWrapPending = true;
		}
	}

	/// <summary>Appends a zero-width code point to the grapheme before the cursor.</summary>
	private void JoinPrevious( int codePoint )
	{
		var column = _isWrapPending ? CursorColumn : CursorColumn - 1;
		var cells = _lines[CursorRow].Cells;
		if ( column > 0 && cells[column].IsWideTail )
			column--;
		if ( column < 0 )
			return;

		cells[column] = cells[column] with { Text = cells[column].Text + char.ConvertFromUtf32( codePoint ) };
	}

	/// <summary>Marks the cursor line as wrapped and moves to the start of the next line.</summary>
	private void WrapLine()
	{
		_lines[CursorRow].IsWrapped = true;
		CursorColumn = 0;
		LineFeed();
	}

	/// <summary>Blanks the other half of a wide character that a write at this column splits.</summary>
	private void SplitWide( TerminalLine line, int column )
	{
		var cells = line.Cells;
		if ( cells[column].IsWideTail && column > 0 )
			cells[column - 1] = cells[column - 1] with { Text = " " };
		if ( column + 1 < cells.Length && cells[column + 1].IsWideTail )
			cells[column + 1] = cells[column + 1] with { Text = " " };
	}

	/// <summary>Moves down one line, scrolling at the bottom of the scroll region, IND.</summary>
	private void LineFeed()
	{
		_isWrapPending = false;
		if ( CursorRow == _bottom )
			ScrollUp( 1 );
		else if ( CursorRow < Rows - 1 )
			CursorRow++;
	}

	/// <summary>Moves up one line, scrolling at the top of the scroll region, RI.</summary>
	private void ReverseIndex()
	{
		_isWrapPending = false;
		if ( CursorRow == _top )
			ScrollDown( 1 );
		else if ( CursorRow > 0 )
			CursorRow--;
	}

	/// <summary>Scrolls the region up; lines leaving the top of a full-height main screen go to history.</summary>
	private void ScrollUp( int count )
	{
		count = Math.Min( count, _bottom - _top + 1 );
		var keepsHistory = _top == 0 && !IsAlternateScreen;
		for ( var index = 0; index < count; index++ )
		{
			var line = _lines[_top];
			Array.Copy( _lines, _top + 1, _lines, _top, _bottom - _top );
			if ( keepsHistory )
			{
				History.Add( line );
				line = new( Columns, EraseStyle );
			}
			else
			{
				line.Fill( 0, Columns, EraseStyle );
				line.IsWrapped = false;
			}

			_lines[_bottom] = line;
		}
	}

	/// <summary>Scrolls the region down, inserting blank lines at its top.</summary>
	private void ScrollDown( int count )
	{
		count = Math.Min( count, _bottom - _top + 1 );
		for ( var index = 0; index < count; index++ )
		{
			var line = _lines[_bottom];
			Array.Copy( _lines, _top, _lines, _top + 1, _bottom - _top );
			line.Fill( 0, Columns, EraseStyle );
			line.IsWrapped = false;
			_lines[_top] = line;
		}
	}

	/// <summary>Inserts blank lines at the cursor within the scroll region, IL.</summary>
	private void InsertLines( int count )
	{
		if ( CursorRow < _top || CursorRow > _bottom )
			return;

		var top = _top;
		_top = CursorRow;
		ScrollDown( count );
		_top = top;
		CursorColumn = 0;
		_isWrapPending = false;
	}

	/// <summary>Deletes lines at the cursor within the scroll region, DL.</summary>
	private void DeleteLines( int count )
	{
		if ( CursorRow < _top || CursorRow > _bottom )
			return;

		var top = _top;
		_top = CursorRow;
		var lines = _lines;

		// Deleted lines never go to history, even when the region starts at the top.
		count = Math.Min( count, _bottom - _top + 1 );
		for ( var index = 0; index < count; index++ )
		{
			var line = lines[_top];
			Array.Copy( lines, _top + 1, lines, _top, _bottom - _top );
			line.Fill( 0, Columns, EraseStyle );
			line.IsWrapped = false;
			lines[_bottom] = line;
		}

		_top = top;
		CursorColumn = 0;
		_isWrapPending = false;
	}

	/// <summary>Inserts blanks at the cursor, shifting the rest of the line right, ICH.</summary>
	private void InsertCells( int count )
	{
		var cells = _lines[CursorRow].Cells;
		count = Math.Min( count, Columns - CursorColumn );
		SplitWide( _lines[CursorRow], CursorColumn );
		Array.Copy( cells, CursorColumn, cells, CursorColumn + count, Columns - CursorColumn - count );
		_lines[CursorRow].Fill( CursorColumn, CursorColumn + count, EraseStyle );
		if ( cells[Columns - 1].Text.Length > 0 && Columns - 1 > 0 && TerminalWidth.Measure( char.ConvertToUtf32( cells[Columns - 1].Text, 0 ) ) == 2 )
			cells[Columns - 1] = cells[Columns - 1] with { Text = " " };
	}

	/// <summary>Deletes cells at the cursor, shifting the rest of the line left, DCH.</summary>
	private void DeleteCells( int count )
	{
		var cells = _lines[CursorRow].Cells;
		count = Math.Min( count, Columns - CursorColumn );
		SplitWide( _lines[CursorRow], CursorColumn );
		if ( CursorColumn + count < Columns )
			SplitWide( _lines[CursorRow], CursorColumn + count );

		Array.Copy( cells, CursorColumn + count, cells, CursorColumn, Columns - CursorColumn - count );
		_lines[CursorRow].Fill( Columns - count, Columns, EraseStyle );
		_isWrapPending = false;
	}

	/// <summary>Erases cells in the cursor line from start up to but excluding end.</summary>
	private void EraseCells( int start, int end )
	{
		var line = _lines[CursorRow];
		start = Math.Max( 0, start );
		end = Math.Min( Columns, end );
		if ( start >= end )
			return;

		SplitWide( line, start );
		SplitWide( line, end - 1 );
		line.Fill( start, end, EraseStyle );
		if ( end == Columns )
			line.IsWrapped = false;
	}

	/// <summary>Erases whole screen rows from start up to but excluding end.</summary>
	private void EraseRows( int start, int end )
	{
		for ( var row = Math.Max( 0, start ); row < Math.Min( Rows, end ); row++ )
		{
			_lines[row].Fill( 0, Columns, EraseStyle );
			_lines[row].IsWrapped = false;
		}
	}

	/// <summary>Moves the cursor, clamping it to the screen and, in origin mode, to the scroll region.</summary>
	private void MoveTo( int row, int column )
	{
		var top = _isOrigin ? _top : 0;
		var bottom = _isOrigin ? _bottom : Rows - 1;
		CursorRow = Math.Clamp( row, top, bottom );
		CursorColumn = Math.Clamp( column, 0, Columns - 1 );
		_isWrapPending = false;
	}

	/// <summary>Moves the cursor vertically, stopping at the scroll region edge when it starts inside the region.</summary>
	private void MoveVertically( int delta )
	{
		var top = CursorRow >= _top ? _top : 0;
		var bottom = CursorRow <= _bottom ? _bottom : Rows - 1;
		CursorRow = Math.Clamp( CursorRow + delta, top, bottom );
		_isWrapPending = false;
	}

	/// <summary>Moves to the next tab stop, or the last column.</summary>
	private void TabForward( int count )
	{
		for ( var index = 0; index < count; index++ )
		{
			var column = CursorColumn + 1;
			while ( column < Columns - 1 && !_tabStops[column] )
				column++;

			CursorColumn = Math.Min( column, Columns - 1 );
		}

		_isWrapPending = false;
	}

	/// <summary>Moves to the previous tab stop, or the first column, CBT.</summary>
	private void TabBackward( int count )
	{
		for ( var index = 0; index < count; index++ )
		{
			var column = CursorColumn - 1;
			while ( column > 0 && !_tabStops[column] )
				column--;

			CursorColumn = Math.Max( column, 0 );
		}

		_isWrapPending = false;
	}

	/// <summary>Saves the cursor, rendition and character sets, DECSC.</summary>
	private void SaveCursor()
	{
		var saved = new SavedCursor( CursorRow, CursorColumn, _style, _isOrigin, _isWrapPending, _isGraphicsCharset[0], _isGraphicsCharset[1], _charset );
		if ( IsAlternateScreen )
			_savedAlternate = saved;
		else
			_savedPrimary = saved;
	}

	/// <summary>Restores what <see cref="SaveCursor"/> saved, DECRC.</summary>
	private void RestoreCursor()
	{
		var saved = IsAlternateScreen ? _savedAlternate : _savedPrimary;
		_style = saved.Style;
		_isOrigin = saved.IsOrigin;
		_isGraphicsCharset[0] = saved.IsG0Graphics;
		_isGraphicsCharset[1] = saved.IsG1Graphics;
		_charset = saved.Charset;
		CursorRow = Math.Clamp( saved.Row, 0, Rows - 1 );
		CursorColumn = Math.Clamp( saved.Column, 0, Columns - 1 );
		_isWrapPending = saved.IsWrapPending;
	}

	/// <summary>Switches between the main and alternate screens, optionally clearing the alternate one.</summary>
	private void UpdateAlternateScreen( bool isAlternate, bool clears )
	{
		if ( isAlternate == IsAlternateScreen )
		{
			if ( isAlternate && clears )
				EraseRows( 0, Rows );

			return;
		}

		_lines = isAlternate ? _alternate : _primary;
		if ( clears && isAlternate )
			EraseRows( 0, Rows );

		_isWrapPending = false;
	}

	/// <summary>Gets the style erased cells take: the current background without rendition, as xterm does.</summary>
	private CellStyle EraseStyle => new( TerminalColor.Default, _style.Background, CellFlags.None );

	/// <summary>Resizes one screen; the main screen trades lines with history so the cursor line stays in view.</summary>
	private TerminalLine[] ResizeLines( TerminalLine[] lines, int columns, int rows, ref int cursorRow, TerminalHistory? history )
	{
		var list = lines.ToList();
		if ( rows < list.Count )
		{
			// Rows below the cursor go first; then rows above it scroll into history.
			var below = Math.Min( list.Count - rows, Math.Max( 0, list.Count - 1 - cursorRow ) );
			list.RemoveRange( list.Count - below, below );
			var above = list.Count - rows;
			for ( var index = 0; index < above; index++ )
			{
				if ( history is not null )
					history.Add( list[index] );
			}

			list.RemoveRange( 0, above );
			cursorRow -= above;
		}

		while ( list.Count < rows )
		{
			// Growing pulls lines back from history, as the shell's own buffer reveals them, when the cursor is at the bottom.
			if ( history is not null && cursorRow == list.Count - 1 && history.RemoveNewest() is { } restored )
			{
				list.Insert( 0, restored );
				cursorRow++;
			}
			else
			{
				list.Add( new( columns, CellStyle.Default ) );
			}
		}

		foreach ( var line in list )
			line.Resize( columns );

		cursorRow = Math.Clamp( cursorRow, 0, rows - 1 );
		return [.. list];
	}

	/// <summary>Builds blank screen rows.</summary>
	private TerminalLine[] BuildLines( int rows ) => Enumerable.Range( 0, rows ).Select( _ => new TerminalLine( Columns, CellStyle.Default ) ).ToArray();

	/// <summary>Builds the default tab stops.</summary>
	private static bool[] BuildTabStops( int columns ) => Enumerable.Range( 0, columns ).Select( column => column > 0 && column % TabWidth == 0 ).ToArray();

	/// <summary>Holds DECSC state.</summary>
	/// <param name="Row">The cursor row.</param>
	/// <param name="Column">The cursor column.</param>
	/// <param name="Style">The rendition.</param>
	/// <param name="IsOrigin">Whether origin mode was set.</param>
	/// <param name="IsWrapPending">Whether a wrap was pending.</param>
	/// <param name="IsG0Graphics">Whether G0 used line drawing.</param>
	/// <param name="IsG1Graphics">Whether G1 used line drawing.</param>
	/// <param name="Charset">Which of G0 and G1 printed.</param>
	private readonly record struct SavedCursor( int Row, int Column, CellStyle Style, bool IsOrigin, bool IsWrapPending, bool IsG0Graphics, bool IsG1Graphics, int Charset )
	{
		/// <summary>Gets the state of a fresh terminal.</summary>
		internal static SavedCursor Home { get; } = new( 0, 0, CellStyle.Default, false, false, false, false, 0 );
	}
}
