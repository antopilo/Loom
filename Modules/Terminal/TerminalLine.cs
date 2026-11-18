namespace Lokn.Modules.Terminal;

/// <summary>Selects the rendition flags of one terminal cell.</summary>
[Flags]
internal enum CellFlags : ushort
{
	/// <summary>Plain text.</summary>
	None = 0,

	/// <summary>Bold or increased intensity, SGR 1.</summary>
	Bold = 1,

	/// <summary>Faint or decreased intensity, SGR 2.</summary>
	Dim = 2,

	/// <summary>Italic, SGR 3.</summary>
	Italic = 4,

	/// <summary>Underlined, SGR 4.</summary>
	Underline = 8,

	/// <summary>Blinking, SGR 5; drawn steady.</summary>
	Blink = 16,

	/// <summary>Foreground and background swapped, SGR 7.</summary>
	Inverse = 32,

	/// <summary>Concealed, SGR 8.</summary>
	Hidden = 64,

	/// <summary>Crossed out, SGR 9.</summary>
	Strikethrough = 128,
}

/// <summary>Encodes a terminal color as the default, a 256-color palette index, or a 24-bit value.</summary>
internal static class TerminalColor
{
	/// <summary>Defines the default foreground or background color.</summary>
	internal const int Default = -1;

	/// <summary>Defines the marker bit of a 24-bit color; the low 24 bits hold red, green and blue.</summary>
	internal const int RgbFlag = 0x1000000;

	/// <summary>Encodes a 24-bit color.</summary>
	internal static int FromRgb( int red, int green, int blue ) => RgbFlag | (Math.Clamp( red, 0, 255 ) << 16) | (Math.Clamp( green, 0, 255 ) << 8) | Math.Clamp( blue, 0, 255 );
}

/// <summary>Describes the colors and rendition of one cell.</summary>
/// <param name="Foreground">The encoded foreground color.</param>
/// <param name="Background">The encoded background color.</param>
/// <param name="Flags">The rendition flags.</param>
internal readonly record struct CellStyle( int Foreground, int Background, CellFlags Flags )
{
	/// <summary>Gets the default colors without rendition.</summary>
	internal static CellStyle Default { get; } = new( TerminalColor.Default, TerminalColor.Default, CellFlags.None );
}

/// <summary>Holds one grid cell: its grapheme and style.</summary>
/// <param name="Text">The displayed grapheme; empty for the right half of a wide character.</param>
/// <param name="Style">The cell's colors and rendition.</param>
internal readonly record struct TerminalCell( string Text, CellStyle Style )
{
	/// <summary>Gets a blank cell with default style.</summary>
	internal static TerminalCell Blank { get; } = new( " ", CellStyle.Default );

	/// <summary>Gets whether this cell is the right half of a wide character.</summary>
	internal bool IsWideTail => Text.Length == 0;
}

/// <summary>Holds one row of cells and whether it continues on the next row.</summary>
internal sealed class TerminalLine
{
	/// <summary>Gets the cells; history lines may be shorter or longer than the current width.</summary>
	internal TerminalCell[] Cells
	{
		get; private set;
	}

	/// <summary>Gets or sets whether the text wrapped onto the next line rather than ending with a line break.</summary>
	internal bool IsWrapped
	{
		get; set;
	}

	/// <summary>Creates a blank line.</summary>
	internal TerminalLine( int columns, CellStyle style )
	{
		Cells = new TerminalCell[columns];
		Fill( 0, columns, style );
	}

	/// <summary>Blanks cells from start up to but excluding end with a style.</summary>
	internal void Fill( int start, int end, CellStyle style )
	{
		var blank = new TerminalCell( " ", style );
		for ( var column = Math.Max( 0, start ); column < Math.Min( end, Cells.Length ); column++ )
			Cells[column] = blank;
	}

	/// <summary>Changes the width, blanking new cells and a wide character the new edge cuts in half.</summary>
	internal void Resize( int columns )
	{
		var previous = Cells.Length;
		if ( previous == columns )
			return;

		var isLeadCut = columns > 0 && columns < previous && Cells[columns].IsWideTail;
		var cells = Cells;
		Array.Resize( ref cells, columns );
		Cells = cells;
		Fill( previous, columns, CellStyle.Default );
		if ( isLeadCut )
			Cells[columns - 1] = TerminalCell.Blank;
	}

	/// <summary>Drops trailing default blanks so a line kept in history holds only its text.</summary>
	internal void Trim()
	{
		var length = Cells.Length;
		while ( length > 0 && Cells[length - 1] == TerminalCell.Blank )
			length--;

		if ( length < Cells.Length )
		{
			var cells = Cells;
			Array.Resize( ref cells, length );
			Cells = cells;
		}
	}

	/// <summary>Builds the text of a column range, skipping wide-character halves and trimming trailing spaces.</summary>
	internal string BuildText( int start, int end )
	{
		var text = new System.Text.StringBuilder();
		for ( var column = Math.Max( 0, start ); column < Math.Min( end, Cells.Length ); column++ )
			text.Append( Cells[column].Text );

		return text.ToString().TrimEnd( ' ' );
	}
}
