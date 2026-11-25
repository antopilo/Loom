using System.Text;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Terminal;

/// <summary>Draws the terminal grid: backgrounds, text runs, selection, cursor and scroll position.</summary>
internal sealed partial class TerminalContent
{
	/// <summary>Defines the row height as a multiple of the font size.</summary>
	private const double LineHeight = 1.2;

	/// <summary>Defines the inset around the grid in logical pixels.</summary>
	private const int GridPadding = 6;

	/// <summary>Defines how many characters measure the cell width, so rounding is spread thin.</summary>
	private const int MeasureLength = 256;

	/// <summary>Defines the font weight of bold text.</summary>
	private const int BoldWeight = 700;

	/// <summary>Defines the font weight of other text.</summary>
	private const int RegularWeight = 400;

	/// <summary>Defines the selection fill opacity over the cell background.</summary>
	private const double SelectionOpacity = 0.35;

	/// <summary>Defines how far dim text moves toward the background.</summary>
	private const float DimBlend = 0.5f;

	/// <summary>Defines the thickness of bar and underline cursors and the unfocused cursor outline.</summary>
	private const int CursorLine = 2;

	/// <summary>Defines the scroll indicator width in logical pixels.</summary>
	private const int ScrollbarWidth = 4;

	/// <summary>Defines the shortest scroll indicator in logical pixels.</summary>
	private const int MinimumThumb = 16;

	/// <summary>Defines the scroll indicator opacity.</summary>
	private const double ScrollbarOpacity = 0.6;

	/// <summary>Defines the color channel levels of the 6x6x6 cube in the 256-color palette.</summary>
	private static readonly int[] CubeLevels = [0, 95, 135, 175, 215, 255];

	/// <summary>Retains the registered monospace faces and Unicode fallbacks the grid draws with.</summary>
	private static readonly FontSource[] Fonts =
	[
		SystemFonts.Register( "Lokn Terminal", RegularWeight, italic: false, "consola.ttf" ),
		SystemFonts.Register( "Lokn Terminal", BoldWeight, italic: false, "consolab.ttf" ),
		SystemFonts.Register( "Lokn Terminal", RegularWeight, italic: true, "consolai.ttf" ),
		SystemFonts.Register( "Lokn Terminal", BoldWeight, italic: true, "consolaz.ttf" ),
		SystemFonts.Register( "Lokn Terminal Symbols", RegularWeight, italic: false, "seguisym.ttf" ),
		SystemFonts.Register( "Lokn Terminal Emoji", RegularWeight, italic: false, "seguiemj.ttf" ),
	];

	/// <summary>Retains the grid element for focus, geometry and the input method window.</summary>
	private readonly ElementHandle _grid = new();

	/// <summary>Stores the cell width in logical pixels.</summary>
	private double _cellWidth;

	/// <summary>Stores the cell height in logical pixels.</summary>
	private double _cellHeight;

	/// <summary>Stores the font size the cell size was measured for.</summary>
	private int _measuredSize;

	/// <summary>Stores how many lines the view is scrolled back from the bottom.</summary>
	private int _scrollOffset;

	/// <summary>Builds the focusable grid, which sizes the terminal to the panel.</summary>
	public override Blob Build()
	{
		var theme = Theme.Current;
		var grid = Blob.Sized( BuildGrid );
		grid.FlexGrow = 1;
		grid.MinHeight = 0;
		return new Container
		{
			FlexGrow = 1,
			MinHeight = 0,
			Padding = GridPadding,
			Children =
			[
				new Container
				{
					Handle = _grid,
					FlexGrow = 1,
					MinHeight = 0,
					MinWidth = 0,
					Overflow = Overflow.Hidden,
					Focusable = true,
					AutoFocus = true,
					Cursor = Cursor.Text,
					AccessibilityRole = AccessibilityRole.Group,
					AccessibilityName = $"Terminal: {_profile.Name}",
					FontFamily = string.Join( ", ", Fonts.Select( font => font.Family ).Distinct() ),
					OnKeyDown = e => _context.Run( () => UpdateKey( e ) ),
					OnTextInput = text => _context.Run( () => UpdateText( text ) ),
					OnFocus = _ => _context.Run( () => UpdateFocus( true ) ),
					OnBlur = _ => _context.Run( () => UpdateFocus( false ) ),
					OnPointerDown = e => _context.Run( () => UpdatePointerDown( e ) ),
					OnPointerMove = e => _context.Run( () => UpdatePointerMove( e ) ),
					OnPointerUp = e => _context.Run( () => UpdatePointerUp( e ) ),
					OnWheel = e => _context.Run( () => UpdateWheel( e ) ),
					Children = [grid],
				},
			],
		};
	}

	/// <summary>Sizes the terminal to the space given and draws the visible lines.</summary>
	private Blob BuildGrid( LayoutSize size )
	{
		_ = _version.Value;
		var theme = Theme.Current;
		var fontSize = _context.App.Settings.Get( TerminalModule.FontSize );
		_terminal.History.UpdateCapacity( _context.App.Settings.Get( TerminalModule.Scrollback ) );
		_terminal.DefaultForeground = theme.Text;
		_terminal.DefaultBackground = theme.Panel;
		UpdateMetrics( fontSize );

		var columns = Math.Max( 2, (int)Math.Floor( size.Width / _cellWidth ) );
		var rows = Math.Max( 1, (int)Math.Floor( size.Height / _cellHeight ) );
		if ( double.IsFinite( size.Width ) && double.IsFinite( size.Height ) && size.Width > 0 && size.Height > 0 )
		{
			if ( columns != _terminal.Columns || rows != _terminal.Rows )
			{
				_terminal.Resize( columns, rows );
				_session?.Resize( columns, rows );
				_scrollOffset = Math.Min( _scrollOffset, _terminal.History.Count );
			}

			if ( !_isStarted )
			{
				// Start once the real size is known, so the shell's first prompt fits.
				_isStarted = true;
				_context.Post( Start );
			}
		}

		var children = new List<Blob>( _terminal.Rows + 1 );
		var top = _terminal.FirstScreenLine - _scrollOffset;
		var selection = ComputeSelection();
		for ( var row = 0; row < _terminal.Rows; row++ )
			children.Add( BuildRow( row, top + row, selection, fontSize, theme ) );

		if ( BuildScrollbar( size, theme ) is { } scrollbar )
			children.Add( scrollbar );

		return new Container
		{
			Width = Length.Percent( 100 ),
			Height = Length.Percent( 100 ),
			FontSize = fontSize,
			Children = children,
		};
	}

	/// <summary>Builds one visible line: background fills, selection, text runs and the cursor.</summary>
	private Blob BuildRow( int row, long number, (TerminalPosition Start, TerminalPosition End)? selection, int fontSize, Theme theme )
	{
		var cells = _terminal.GetLine( number )?.Cells ?? [];
		var children = new List<Blob>();
		var columns = _terminal.Columns;

		// Backgrounds first, merged across cells of one color.
		var start = 0;
		Color? fill = null;
		for ( var column = 0; column <= columns; column++ )
		{
			Color? background = column < columns ? ResolveColors( ReadCell( cells, column ).Style, theme ).Background : null;
			if ( column < columns && background == fill )
				continue;

			if ( fill is { } color )
				children.Add( BuildRectangle( start, column - start, color ) );

			start = column;
			fill = background;
		}

		if ( selection is { } range && number >= range.Start.Line && number <= range.End.Line )
		{
			var first = number == range.Start.Line ? range.Start.Column : 0;
			var last = number == range.End.Line ? Math.Min( range.End.Column, columns - 1 ) : columns - 1;
			if ( last >= first )
				children.Add( BuildRectangle( first, last - first + 1, theme.Accent.WithAlpha( SelectionOpacity ) ) );
		}

		BuildRuns( cells, columns, fontSize, theme, children );
		if ( number == _terminal.FirstScreenLine + _terminal.CursorRow && _terminal.IsCursorVisible && !_hasExited )
			BuildCursor( cells, fontSize, theme, children );

		return new Container
		{
			Key = $"row{row}",
			Position = PositionType.Absolute,
			Left = 0,
			Top = row * _cellHeight,
			Width = columns * _cellWidth,
			Height = _cellHeight,
			Children = children,
		};
	}

	/// <summary>Adds text runs: cells of one style merge, while wide and fallback-font characters stand alone at their own column.</summary>
	private void BuildRuns( TerminalCell[] cells, int columns, int fontSize, Theme theme, List<Blob> children )
	{
		var text = new StringBuilder();
		var start = 0;
		CellStyle? runStyle = null;
		for ( var column = 0; column <= columns; column++ )
		{
			var cell = column < columns ? ReadCell( cells, column ) : TerminalCell.Blank;
			if ( column < columns && cell.IsWideTail )
				continue;

			var isAlone = column < columns && IsAlone( cells, column );
			if ( column < columns && !isAlone && runStyle == cell.Style && text.Length > 0 )
			{
				text.Append( cell.Text );
				continue;
			}

			AddRun( text, start, runStyle, fontSize, theme, children );
			text.Clear();
			start = column;
			runStyle = cell.Style;
			text.Append( cell.Text );
			if ( isAlone )
			{
				AddRun( text, start, runStyle, fontSize, theme, children );
				text.Clear();
				runStyle = null;
			}
		}
	}

	/// <summary>Adds one text run unless it draws nothing visible.</summary>
	private void AddRun( StringBuilder text, int column, CellStyle? style, int fontSize, Theme theme, List<Blob> children )
	{
		if ( text.Length == 0 || style is not { } cellStyle )
			return;

		var decorations = cellStyle.Flags & (CellFlags.Underline | CellFlags.Strikethrough);
		// Undecorated trailing spaces draw nothing; dropping them keeps each run's text to what is visible.
		var content = decorations == CellFlags.None ? text.ToString().TrimEnd( ' ' ) : text.ToString();
		if ( content.Length == 0 )
			return;

		children.Add( BuildText( content, column, cellStyle, ResolveColors( cellStyle, theme ).Foreground, fontSize ) );
	}

	/// <summary>Builds text placed at its column.</summary>
	private Text BuildText( string content, int column, CellStyle style, Color color, int fontSize )
	{
		var decoration = TextDecoration.None;
		if ( style.Flags.HasFlag( CellFlags.Underline ) )
			decoration |= TextDecoration.Underline;
		if ( style.Flags.HasFlag( CellFlags.Strikethrough ) )
			decoration |= TextDecoration.LineThrough;

		return new Text
		{
			Content = content,
			Position = PositionType.Absolute,
			Left = column * _cellWidth,
			Top = 0,
			Height = _cellHeight,
			Color = color,
			FontWeight = style.Flags.HasFlag( CellFlags.Bold ) ? BoldWeight : RegularWeight,
			FontStyle = style.Flags.HasFlag( CellFlags.Italic ) ? FontStyle.Italic : FontStyle.Normal,
			TextDecoration = decoration,
			TextWrap = TextWrap.NoWrap,
			LineHeight = _cellHeight / fontSize,
		};
	}

	/// <summary>Adds the cursor in its shape; a focused block cursor redraws its character over the fill.</summary>
	private void BuildCursor( TerminalCell[] cells, int fontSize, Theme theme, List<Blob> children )
	{
		var column = Math.Min( _terminal.CursorColumn, _terminal.Columns - 1 );
		var cell = ReadCell( cells, column );
		var width = column + 1 < _terminal.Columns && ReadCell( cells, column + 1 ).IsWideTail ? 2 : 1;
		var left = column * _cellWidth;
		if ( !_isFocused )
		{
			children.Add( new Container
			{
				Position = PositionType.Absolute,
				Left = left,
				Top = 0,
				Width = width * _cellWidth,
				Height = _cellHeight,
				BorderWidth = 1,
				BorderColor = theme.Accent,
			} );
			return;
		}

		switch ( _terminal.CursorShape )
		{
			case CursorShape.Bar:
				children.Add( BuildFill( left, 0, CursorLine, _cellHeight, theme.Accent ) );
				break;
			case CursorShape.Underline:
				children.Add( BuildFill( left, _cellHeight - CursorLine, width * _cellWidth, CursorLine, theme.Accent ) );
				break;
			default:
				children.Add( BuildFill( left, 0, width * _cellWidth, _cellHeight, theme.Accent ) );
				if ( !string.IsNullOrWhiteSpace( cell.Text ) )
					children.Add( BuildText( cell.Text, column, cell.Style, theme.Panel, fontSize ) );
				break;
		}
	}

	/// <summary>Builds the scroll position indicator while the view is scrolled back.</summary>
	private Blob? BuildScrollbar( LayoutSize size, Theme theme )
	{
		var history = _terminal.History.Count;
		if ( _scrollOffset <= 0 || history <= 0 || !double.IsFinite( size.Height ) )
			return null;

		var total = history + _terminal.Rows;
		var thumb = Math.Max( MinimumThumb, size.Height * _terminal.Rows / total );
		var position = (double)(history - _scrollOffset) / history;
		return new Container
		{
			Key = "scrollbar",
			Position = PositionType.Absolute,
			Right = 0,
			Top = (size.Height - thumb) * position,
			Width = ScrollbarWidth,
			Height = thumb,
			BorderRadius = ScrollbarWidth / 2,
			BackgroundColor = theme.Muted.WithAlpha( ScrollbarOpacity ),
		};
	}

	/// <summary>Builds a fill spanning cells of the row.</summary>
	private Container BuildRectangle( int column, int count, Color color ) => BuildFill( column * _cellWidth, 0, count * _cellWidth, _cellHeight, color );

	/// <summary>Builds an absolutely placed fill.</summary>
	private static Container BuildFill( double left, double top, double width, double height, Color color ) => new()
	{
		Position = PositionType.Absolute,
		Left = left,
		Top = top,
		Width = width,
		Height = height,
		BackgroundColor = color,
	};

	/// <summary>Measures the cell size for a font size once per change.</summary>
	private void UpdateMetrics( int fontSize )
	{
		if ( _measuredSize == fontSize && _cellWidth > 0 )
			return;

		_measuredSize = fontSize;
		_cellHeight = Math.Ceiling( fontSize * LineHeight );
		var style = new Style
		{
			FontFamily = Fonts[0].Family,
			FontSize = fontSize,
		};

		// Without a window, as in headless checks, Consolas' advance of 0.55 em stands in.
		var width = _context.App.Window?.MeasureText( new string( 'M', MeasureLength ), style ) / MeasureLength;
		_cellWidth = width is > 0 ? width.Value : fontSize * 0.55;
	}

	/// <summary>Resolves a style's drawn colors: inverse swaps them, bold brightens the first eight, dim fades and hidden matches the background.</summary>
	private static (Color Foreground, Color? Background) ResolveColors( CellStyle style, Theme theme )
	{
		var foregroundIndex = style.Foreground;
		if ( style.Flags.HasFlag( CellFlags.Bold ) && foregroundIndex is >= 0 and < 8 )
			foregroundIndex += 8;

		var foreground = ResolveColor( foregroundIndex, theme ) ?? theme.Text;
		var background = ResolveColor( style.Background, theme );
		if ( style.Flags.HasFlag( CellFlags.Inverse ) )
			(foreground, background) = (background ?? theme.Panel, foreground);

		var backdrop = background ?? theme.Panel;
		if ( style.Flags.HasFlag( CellFlags.Dim ) )
			foreground = Color.FromNormalized( Blend( foreground.R, backdrop.R ), Blend( foreground.G, backdrop.G ), Blend( foreground.B, backdrop.B ), foreground.A );
		if ( style.Flags.HasFlag( CellFlags.Hidden ) )
			foreground = backdrop;

		return (foreground, background);
	}

	/// <summary>Resolves an encoded color through the theme's ANSI palette and the xterm 256-color table, or null for the default.</summary>
	private static Color? ResolveColor( int color, Theme theme )
	{
		if ( color == TerminalColor.Default )
			return null;

		if ( (color & TerminalColor.RgbFlag) != 0 )
			return Color.Rgb( (color >> 16) & 0xFF, (color >> 8) & 0xFF, color & 0xFF );

		if ( color < 16 )
		{
			return color switch
			{
				0 => theme.AnsiBlack,
				1 => theme.AnsiRed,
				2 => theme.AnsiGreen,
				3 => theme.AnsiYellow,
				4 => theme.AnsiBlue,
				5 => theme.AnsiMagenta,
				6 => theme.AnsiCyan,
				7 => theme.AnsiWhite,
				8 => theme.AnsiBrightBlack,
				9 => theme.AnsiBrightRed,
				10 => theme.AnsiBrightGreen,
				11 => theme.AnsiBrightYellow,
				12 => theme.AnsiBrightBlue,
				13 => theme.AnsiBrightMagenta,
				14 => theme.AnsiBrightCyan,
				_ => theme.AnsiBrightWhite,
			};
		}

		if ( color < 232 )
		{
			var index = color - 16;
			return Color.Rgb( CubeLevels[index / 36], CubeLevels[index / 6 % 6], CubeLevels[index % 6] );
		}

		var gray = 8 + (color - 232) * 10;
		return Color.Rgb( gray, gray, gray );
	}

	/// <summary>Moves a channel toward the background for dim text.</summary>
	private static float Blend( float value, float backdrop ) => value + (backdrop - value) * DimBlend;

	/// <summary>Reports whether a cell must be drawn alone: wide characters, joined graphemes and symbols the monospace face may lack, whose fallback widths would shift the text after them.</summary>
	private static bool IsAlone( TerminalCell[] cells, int column )
	{
		var cell = ReadCell( cells, column );
		if ( ReadCell( cells, column + 1 ).IsWideTail )
			return true;
		if ( cell.Text.Length != 1 )
			return true;

		var code = cell.Text[0];
		return code >= 0x2190 && code is not (>= '\u2500' and <= '\u259f');
	}

	/// <summary>Reads a cell, treating cells past a short history line as blank.</summary>
	private static TerminalCell ReadCell( TerminalCell[] cells, int column ) => column < cells.Length ? cells[column] : TerminalCell.Blank;
}
