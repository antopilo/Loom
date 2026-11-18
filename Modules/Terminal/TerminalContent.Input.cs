using Goo;

namespace Lokn.Modules.Terminal;

/// <summary>Names a cell by absolute line number and column.</summary>
/// <param name="Line">The absolute line number; see <see cref="TerminalEmulator.GetLine"/>.</param>
/// <param name="Column">The column.</param>
internal readonly record struct TerminalPosition( long Line, int Column ) : IComparable<TerminalPosition>
{
	/// <summary>Orders positions by line, then column.</summary>
	public int CompareTo( TerminalPosition other ) => Line != other.Line ? Line.CompareTo( other.Line ) : Column.CompareTo( other.Column );
}

/// <summary>Turns keys, text, pointer and wheel input into shell input, selection, clipboard use and scrolling.</summary>
internal sealed partial class TerminalContent
{
	/// <summary>Defines how many lines one wheel step scrolls.</summary>
	private const int WheelLines = 3;

	/// <summary>Defines the characters that end a word for double-click selection.</summary>
	private const string WordDelimiters = " \t()[]{}'\"`,;|<>";

	/// <summary>Selects what a pointer drag selects.</summary>
	private enum SelectionUnit
	{
		/// <summary>Cell boundaries.</summary>
		Cell,

		/// <summary>Whole words, after a double click.</summary>
		Word,

		/// <summary>Whole lines, after a triple click.</summary>
		Line,
	}

	/// <summary>Stores where the selection started.</summary>
	private TerminalPosition _anchor;

	/// <summary>Stores where the selection currently ends.</summary>
	private TerminalPosition _caret;

	/// <summary>Stores what the current selection selects.</summary>
	private SelectionUnit _unit;

	/// <summary>Tracks whether a selection is shown.</summary>
	private bool _hasSelection;

	/// <summary>Tracks whether the primary button is dragging a selection.</summary>
	private bool _isSelecting;

	/// <summary>Tracks whether the grid has keyboard focus.</summary>
	private bool _isFocused;

	/// <summary>Stores the button reported as held to a program that tracks the pointer, or -1.</summary>
	private int _reportedButton = -1;

	/// <summary>Stores the last cell a pointer motion report named, so reports go out once per cell.</summary>
	private (int Column, int Row) _reportedCell = (-1, -1);

	/// <summary>Accumulates fractional wheel movement from precise touchpads.</summary>
	private double _wheelSteps;

	/// <summary>Claims every key while the grid is focused, so shells receive keys such as Ctrl+W and Ctrl+K; Ctrl+Shift combinations stay with the application.</summary>
	public override bool ClaimsKey( Key key, KeyModifiers modifiers ) => _isFocused && !(modifiers.Ctrl && modifiers.Shift) && !modifiers.Super;

	/// <summary>Handles clipboard and scrollback keys, then sends the key's terminal sequence when it has one.</summary>
	private void UpdateKey( KeyEvent e )
	{
		if ( e.IsComposing )
			return;

		var modifiers = e.Modifiers;
		var isCtrl = modifiers.Ctrl && !modifiers.Alt && !modifiers.Super;
		var isShift = modifiers.Shift && !modifiers.Ctrl && !modifiers.Alt && !modifiers.Super;
		if ( isCtrl && e.Key == Key.C && (modifiers.Shift || _hasSelection) )
		{
			UpdateCopy();
			Consume( e );
			return;
		}

		if ( (isCtrl && e.Key == Key.V) || (isShift && e.Key == Key.Insert) )
		{
			UpdatePaste();
			Consume( e );
			return;
		}

		if ( isShift && e.Key is Key.PageUp or Key.PageDown && !_terminal.IsAlternateScreen )
		{
			UpdateScroll( e.Key == Key.PageUp ? _terminal.Rows - 1 : 1 - _terminal.Rows );
			Consume( e );
			return;
		}

		if ( _hasExited )
		{
			if ( e.Key is Key.Enter or Key.KeypadEnter )
				UpdateRestart();

			Consume( e );
			return;
		}

		var sequence = TerminalInput.EncodeKey( e.Key, modifiers, _terminal.IsApplicationCursorKeys, _terminal.IsNewLineMode, TerminalInput.TranslateKey );
		if ( sequence is null )
			return;

		Consume( e );
		Send( sequence );
	}

	/// <summary>Sends committed text, including input method results.</summary>
	private void UpdateText( string text )
	{
		if ( !_hasExited )
			Send( text );
	}

	/// <summary>Tracks focus for the cursor and reports it to programs that asked.</summary>
	private void UpdateFocus( bool isFocused )
	{
		if ( _isFocused == isFocused )
			return;

		_isFocused = isFocused;
		if ( _terminal.IsFocusReporting )
			Write( isFocused ? "\x1b[I" : "\x1b[O" );

		UpdateInputArea();
		UpdateVersion();
	}

	/// <summary>Reports a press to a tracking program, or starts a selection, copies or pastes.</summary>
	private void UpdatePointerDown( PointerEvent e )
	{
		_grid.Focus();
		if ( IsReportingPointer( e.Modifiers ) )
		{
			var button = e.Button switch
			{
				PointerButton.Primary => 0,
				PointerButton.Middle => 1,
				PointerButton.Secondary => 2,
				_ => -1,
			};
			if ( button < 0 )
				return;

			e.Capture();
			_reportedButton = button;
			var (column, row) = ComputeCell( e.Position );
			_reportedCell = (column, row);
			SendPointer( button, e.Modifiers, column, row, isRelease: false );
			return;
		}

		if ( e.Button == PointerButton.Secondary )
		{
			if ( _hasSelection )
				UpdateCopy();
			else
				UpdatePaste();

			return;
		}

		if ( e.Button != PointerButton.Primary )
			return;

		e.Capture();
		_isSelecting = true;
		if ( e.Modifiers.Shift && _hasSelection )
		{
			_caret = ComputePosition( e.Position, _unit );
			UpdateVersion();
			return;
		}

		_unit = e.ClickCount switch
		{
			2 => SelectionUnit.Word,
			>= 3 => SelectionUnit.Line,
			_ => SelectionUnit.Cell,
		};
		_anchor = ComputePosition( e.Position, _unit );
		_caret = _anchor;
		_hasSelection = _unit != SelectionUnit.Cell;
		UpdateVersion();
	}

	/// <summary>Reports motion to a tracking program, or extends the selection, scrolling at the edges.</summary>
	private void UpdatePointerMove( PointerEvent e )
	{
		if ( IsReportingPointer( e.Modifiers ) || _reportedButton >= 0 )
		{
			var tracking = _terminal.MouseTracking;
			if ( tracking == MouseTracking.Motion || (tracking == MouseTracking.Drag && _reportedButton >= 0) )
			{
				var cell = ComputeCell( e.Position );
				if ( cell != _reportedCell )
				{
					_reportedCell = cell;
					SendPointer( (_reportedButton >= 0 ? _reportedButton : TerminalInput.ReleaseButton) + TerminalInput.MotionFlag, e.Modifiers, cell.Column, cell.Row, isRelease: false );
				}
			}

			return;
		}

		if ( !_isSelecting )
			return;

		var row = (int)Math.Floor( e.Position.Y / _cellHeight );
		if ( row < 0 )
			UpdateScroll( 1 );
		else if ( row >= _terminal.Rows )
			UpdateScroll( -1 );

		_caret = ComputePosition( e.Position, _unit );
		_hasSelection = _unit != SelectionUnit.Cell || _caret != _anchor;
		UpdateVersion();
	}

	/// <summary>Reports a release to a tracking program, or ends the selection drag.</summary>
	private void UpdatePointerUp( PointerEvent e )
	{
		if ( _reportedButton >= 0 )
		{
			if ( _terminal.MouseTracking is not (MouseTracking.None or MouseTracking.Press) )
			{
				var (column, row) = ComputeCell( e.Position );
				SendPointer( _reportedButton, e.Modifiers, column, row, isRelease: true );
			}

			_reportedButton = -1;
			e.ReleaseCapture();
			return;
		}

		if ( _isSelecting )
		{
			_isSelecting = false;
			e.ReleaseCapture();
		}
	}

	/// <summary>Scrolls history, or sends wheel reports or arrow keys to full-screen programs.</summary>
	private void UpdateWheel( WheelEvent e )
	{
		_wheelSteps += e.Delta.Y;
		var steps = (int)Math.Truncate( _wheelSteps );
		_wheelSteps -= steps;
		if ( steps == 0 )
			return;

		e.StopPropagation();
		if ( IsReportingPointer( e.Modifiers ) )
		{
			var (column, row) = ComputeCell( e.Position );
			for ( var step = 0; step < Math.Abs( steps ); step++ )
				SendPointer( steps > 0 ? TerminalInput.WheelUpButton : TerminalInput.WheelDownButton, e.Modifiers, column, row, isRelease: false );

			return;
		}

		if ( _terminal.IsAlternateScreen )
		{
			// Full-screen programs without pointer tracking scroll with the arrow keys, as other terminals do.
			var key = TerminalInput.EncodeKey( steps > 0 ? Key.Up : Key.Down, KeyModifiers.None, _terminal.IsApplicationCursorKeys, false, TerminalInput.TranslateKey );
			Write( string.Concat( Enumerable.Repeat( key, Math.Abs( steps ) * WheelLines ) ) );
			return;
		}

		UpdateScroll( steps * WheelLines );
	}

	/// <summary>Scrolls the view back by a number of lines, or forward when negative.</summary>
	private void UpdateScroll( int lines )
	{
		var offset = Math.Clamp( _scrollOffset + lines, 0, _terminal.History.Count );
		if ( offset == _scrollOffset )
			return;

		_scrollOffset = offset;
		UpdateVersion();
	}

	/// <summary>Copies the selection and clears it.</summary>
	private void UpdateCopy()
	{
		if ( ComputeSelection() is not { } selection )
			return;

		var text = _terminal.BuildText( selection.Start.Line, selection.Start.Column, selection.End.Line, selection.End.Column );
		if ( text.Length > 0 )
			_context.App.Window?.Clipboard.WriteText( text );

		_hasSelection = false;
		UpdateVersion();
	}

	/// <summary>Pastes clipboard text as typed input, bracketed when the program asked.</summary>
	private void UpdatePaste()
	{
		if ( _hasExited || _context.App.Window?.Clipboard.ReadText() is not { Status: ClipboardReadStatus.Success, Text.Length: > 0 } result )
			return;

		Send( TerminalInput.EncodePaste( result.Text, _terminal.IsBracketedPaste ) );
	}

	/// <summary>Sends input, returning the view to the bottom and clearing the selection as typing does.</summary>
	private void Send( string text )
	{
		Write( text );
		if ( _scrollOffset == 0 && !_hasSelection )
			return;

		_scrollOffset = 0;
		_hasSelection = false;
		UpdateVersion();
	}

	/// <summary>Sends one pointer report.</summary>
	private void SendPointer( int button, KeyModifiers modifiers, int column, int row, bool isRelease )
	{
		if ( TerminalInput.EncodePointer( button, modifiers, column, row, isRelease, _terminal.IsSgrMouse ) is { } report )
			Write( report );
	}

	/// <summary>Places the input method's candidate window at the cursor.</summary>
	private void UpdateInputArea()
	{
		if ( !_isFocused || !_grid.IsMounted || _cellWidth <= 0 )
			return;

		var box = _grid.ContentBox;
		_grid.SetTextInputArea( new ElementRect
		{
			X = box.X + _terminal.CursorColumn * _cellWidth,
			Y = box.Y + (_terminal.CursorRow + _scrollOffset) * _cellHeight,
			Width = _cellWidth,
			Height = _cellHeight,
		} );
	}

	/// <summary>Computes the ordered, inclusive selected cells, expanded to words or lines, or null when nothing is selected.</summary>
	private (TerminalPosition Start, TerminalPosition End)? ComputeSelection()
	{
		if ( !_hasSelection )
			return null;

		var (start, end) = _anchor.CompareTo( _caret ) <= 0 ? (_anchor, _caret) : (_caret, _anchor);
		switch ( _unit )
		{
			case SelectionUnit.Word:
				return (start with { Column = FindWordEdge( start, -1 ) }, end with { Column = FindWordEdge( end, 1 ) });
			case SelectionUnit.Line:
				return (start with { Column = 0 }, end with { Column = _terminal.Columns - 1 });
		}

		// Cell selections run between boundaries, so the end boundary excludes the cell after it.
		end = end with { Column = end.Column - 1 };
		return start.Line == end.Line && end.Column < start.Column ? null : (start, end);
	}

	/// <summary>Finds the first or last column of the word at a position; a delimiter is a word by itself.</summary>
	private int FindWordEdge( TerminalPosition position, int direction )
	{
		var cells = _terminal.GetLine( position.Line )?.Cells ?? [];
		var column = Math.Clamp( position.Column, 0, _terminal.Columns - 1 );
		if ( !IsWordCell( cells, column ) )
			return column;

		while ( column + direction >= 0 && column + direction < _terminal.Columns && IsWordCell( cells, column + direction ) )
			column += direction;

		return column;
	}

	/// <summary>Maps a pointer position to a selection position: a cell boundary, or the cell under it for words and lines.</summary>
	private TerminalPosition ComputePosition( Point point, SelectionUnit unit )
	{
		var row = Math.Clamp( (int)Math.Floor( point.Y / _cellHeight ), 0, _terminal.Rows - 1 );
		var column = unit == SelectionUnit.Cell
			? Math.Clamp( (int)Math.Round( point.X / _cellWidth ), 0, _terminal.Columns )
			: Math.Clamp( (int)Math.Floor( point.X / _cellWidth ), 0, _terminal.Columns - 1 );
		return new( _terminal.FirstScreenLine - _scrollOffset + row, column );
	}

	/// <summary>Maps a pointer position to the screen cell under it.</summary>
	private (int Column, int Row) ComputeCell( Point point ) => (
		Math.Clamp( (int)Math.Floor( point.X / _cellWidth ), 0, _terminal.Columns - 1 ),
		Math.Clamp( (int)Math.Floor( point.Y / _cellHeight ), 0, _terminal.Rows - 1 ));

	/// <summary>Reports whether pointer input goes to the program; Shift keeps it for selection.</summary>
	private bool IsReportingPointer( KeyModifiers modifiers ) => _terminal.MouseTracking != MouseTracking.None && !modifiers.Shift && !_hasExited;

	/// <summary>Reports whether a cell belongs to a word.</summary>
	private static bool IsWordCell( TerminalCell[] cells, int column ) => ReadCell( cells, column ) is var cell && (cell.IsWideTail || !WordDelimiters.Contains( cell.Text[0] ));

	/// <summary>Stops a handled key from reaching bindings, text input and focus navigation.</summary>
	private static void Consume( KeyEvent e )
	{
		e.PreventDefault();
		e.StopPropagation();
	}
}
