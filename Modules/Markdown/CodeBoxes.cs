using System.Runtime.CompilerServices;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Markdown;

/// <summary>Describes one range of a text that gets drawn under or beside it: inline code in a box, or a mention led by its icon.</summary>
/// <param name="Range">The range, in UTF-16 offsets of the shown text.</param>
/// <param name="Text">The range's text, measured to size its box.</param>
/// <param name="IsCode">Whether the range shows in the code font, whose width Goo misjudges.</param>
/// <param name="Boxed">Whether the range gets a box behind it.</param>
/// <param name="PadBefore">Whether the character before the range is followed by a box's room, <see cref="MarkdownView.CodePad"/>.</param>
/// <param name="Glyph">The icon drawn in the room before the range, <see cref="MarkdownView.MentionGap"/>, or null.</param>
/// <param name="IsLink">Whether the range is a link, which shows a hover effect under the pointer: a background for a mention, a thicker underline for others.</param>
/// <param name="Weight">The font weight of bold text that draws nothing and only moves what follows it on its line; else 0.</param>
internal sealed record CodeMark( TextRange Range, string Text, bool IsCode, bool Boxed, bool PadBefore, string? Glyph, bool IsLink = false, int Weight = 0 );

/// <summary>Supplies a text, the handle its layout is read through, and what to draw under or beside it.</summary>
/// <param name="Text">The text.</param>
/// <param name="Handle">The text's handle.</param>
/// <param name="Marks">The boxed code and the mentions, in text order.</param>
internal sealed record CodeBoxesInput( Text Text, ElementHandle Handle, CodeMark[] Marks );

/// <summary>Holds what <see cref="CodeBoxes.Remember"/> recorded for one text.</summary>
/// <param name="Handle">The text's handle.</param>
/// <param name="Marks">The boxed code and the mentions.</param>
internal sealed record CodeBoxesFound( ElementHandle Handle, CodeMark[] Marks );

/// <summary>Places one mention's icon in the text's coordinates.</summary>
/// <param name="X">The icon's left edge.</param>
/// <param name="Y">The top of the mention's line.</param>
/// <param name="Height">The height of the mention's line.</param>
/// <param name="Glyph">The icon.</param>
internal readonly record struct MentionIcon( double X, double Y, double Height, string Glyph );

/// <summary>Draws a rounded box behind each piece of inline code in a text, and the icon before each mention of a file or a web page, as Tern does,
/// once layout has placed its lines.</summary>
/// <remarks>
/// Goo draws no background behind part of a text and fails on one, and a text holds no icons, so both are drawn here from the rectangles of each
/// range: under the text for boxes, in the room letter spacing leaves before a mention for icons. Copied text stays as written. Code that wraps gets
/// one box per line. Both follow the text's layout, so they show from the frame after it is laid out.
/// </remarks>
internal sealed class CodeBoxes : GuardedCell<CodeBoxesInput>
{
	/// <summary>Defines how far a box reaches past its code on the left and right, in logical pixels: about the room set beside the code.</summary>
	private const double InsetX = 4;

	/// <summary>Defines a box's height in logical pixels: the glyphs with a little room, not the whole line, which is taller.</summary>
	private const double BoxHeight = 19;

	/// <summary>Defines how far below the line's middle a box's middle sits, in logical pixels: glyphs sit low in their line, over its descent.</summary>
	private const double BoxDrop = 1.5;

	/// <summary>Defines a mention icon's size in logical pixels.</summary>
	private const int IconSize = 13;

	/// <summary>Defines the font size code is measured at to compare its fonts; the ratio of their widths barely depends on it.</summary>
	private const int MeasureSize = 14;

	/// <summary>Remembers the marks of each text <see cref="MarkdownView"/> built, until the text is placed.</summary>
	private static readonly ConditionalWeakTable<Text, CodeBoxesFound> _found = [];

	/// <summary>Stores the boxes of the latest layout, in the text's coordinates; a change rebuilds the view.</summary>
	private readonly State<ElementRect[]> _boxes = new( [] );

	/// <summary>Stores the mention icons of the latest layout, in the text's coordinates; a change rebuilds the view.</summary>
	private readonly State<MentionIcon[]> _icons = new( [] );

	/// <summary>Stores the rectangles of each link of the latest layout by its mark's index, in the text's coordinates; a change rebuilds the view.</summary>
	private readonly State<(int Mark, ElementRect Rect)[]> _links = new( [] );

	/// <summary>Stores the index of the link mark under the pointer, or -1.</summary>
	private readonly State<int> _hovered = new( -1 );

	/// <summary>Stores the text handle followed while mounted.</summary>
	private ElementHandle? _followed;

	/// <summary>Holds the rectangles one range returns; a range rarely spans more lines.</summary>
	private ElementRect[] _scratch = new ElementRect[8];

	/// <summary>Records the marks of a text and the handle the text was given, for <see cref="Wrap"/>.</summary>
	internal static void Remember( Text text, ElementHandle handle, CodeMark[] marks ) => _found.AddOrUpdate( text, new( handle, marks ) );

	/// <summary>Returns the text itself when it has nothing to draw beside it, or a container whose cell draws its boxes and icons.</summary>
	/// <remarks>
	/// The container takes the text's place in its parent's layout and the text fills it, so the layout the caller wants goes to whichever is
	/// outermost. A container, not the cell, because a mounted cell drops the margin <see cref="MarkdownView"/> spaces blocks with.
	/// </remarks>
	/// <param name="text">The text <see cref="MarkdownView"/> built.</param>
	/// <param name="key">The key of the container, unique among its siblings.</param>
	/// <param name="grow">Whether the outer element grows along its parent's main axis.</param>
	/// <param name="layout">Sets the outer element's other place in its parent, such as a table column's width.</param>
	internal static Blob Wrap( Text text, string key, bool grow = false, Action<Blob>? layout = null )
	{
		if ( !_found.TryGetValue( text, out var found ) )
		{
			layout?.Invoke( text );
			return text;
		}

		text.FlexGrow = 0;
		var outer = new Container
		{
			Key = key,
			FlexGrow = grow ? 1 : 0,
			MinWidth = 0,
			Children = [Cell.Mount<CodeBoxesInput, CodeBoxes>( "code-boxes", new( text, found.Handle, found.Marks ) )],
		};
		layout?.Invoke( outer );
		return outer;
	}

	/// <summary>Follows the new text's layout.</summary>
	protected override void InputChanged( CodeBoxesInput input )
	{
		_measuredSize = null;
		Follow( input.Handle );
		UpdateBoxes();
	}

	/// <summary>Stores the text's size the boxes were last measured at, or null to measure again.</summary>
	private (double Width, double Height)? _measuredSize;

	/// <summary>Starts following the text's layout.</summary>
	protected override void Mounted() => Follow( Input.Handle );

	/// <summary>Stops following the text's layout.</summary>
	protected override void Unmounting() => Follow( null );

	/// <summary>Builds the boxes and the hovered link's background, the text over them, then the icons and the hovered link's underline.</summary>
	protected override Blob BuildView( CodeBoxesInput input )
	{
		var theme = Theme.Current;
		var children = new List<Blob>( _boxes.Value.Length + _icons.Value.Length + 3 );
		foreach ( var (index, box) in _boxes.Value.Index() )
		{
			children.Add( new Container
			{
				Key = "box:" + index,
				Position = PositionType.Absolute,
				Left = box.X - InsetX,
				Top = box.Y + (box.Height - BoxHeight) / 2 + BoxDrop,
				Width = box.Width + 2 * InsetX,
				Height = BoxHeight,
				// A soft fill without an outline, as Tern draws it.
				BorderRadius = theme.Radius + 1,
				BackgroundColor = theme.Raised,
				HitTestSelf = false,
			} );
		}
		var hovered = _hovered.Value is var mark and >= 0 && mark < input.Marks.Length ? input.Marks[mark] : null;
		var hoveredRects = hovered is null ? [] : _links.Value.Where( link => link.Mark == _hovered.Value ).Select( link => link.Rect ).ToArray();
		// A mention, such as a file, sits in a soft pill like a read's file chip, brighter under the pointer.
		foreach ( var (index, (markIndex, rect)) in _links.Value.Index() )
		{
			if ( markIndex >= input.Marks.Length || input.Marks[markIndex].Glyph is null )
				continue;
			children.Add( new Container
			{
				Key = "pill:" + index,
				Position = PositionType.Absolute,
				Left = rect.X - InsetX,
				Top = rect.Y + (rect.Height - BoxHeight) / 2 + BoxDrop,
				Width = rect.Width + 2 * InsetX,
				Height = BoxHeight,
				BorderRadius = BoxHeight / 2,
				BackgroundColor = markIndex == _hovered.Value ? theme.Hover : theme.Text.WithAlpha( 0.045 ),
				HitTestSelf = false,
			} );
		}
		children.Add( input.Text );
		foreach ( var (index, icon) in _icons.Value.Index() )
		{
			// The icon centers in a box the pill's height and place, so it sits in the pill's middle whatever the line height.
			children.Add( new Container
			{
				Key = "icon:" + index,
				Position = PositionType.Absolute,
				Left = icon.X,
				Top = icon.Y + (icon.Height - BoxHeight) / 2 + BoxDrop,
				Width = IconSize,
				Height = BoxHeight,
				AlignItems = AlignItems.Center,
				JustifyContent = JustifyContent.Center,
				HitTestSelf = false,
				Children =
				[
					new Text
					{
						Content = icon.Glyph,
						FontFamily = ViewStyle.IconFontFamily,
						FontSize = IconSize - 1,
						LineHeight = 1,
						Color = theme.Muted,
					},
				],
			} );
		}
		// Another link already underlined gets a thicker underline under the pointer.
		if ( hovered is { Glyph: null } )
		{
			foreach ( var (index, rect) in hoveredRects.Index() )
				children.Add( new Container
				{
					Key = "underline:" + index,
					Position = PositionType.Absolute,
					Left = rect.X,
					Top = rect.Y + rect.Height - 3,
					Width = rect.Width,
					Height = 2,
					BackgroundColor = theme.AnsiGreen,
					HitTestSelf = false,
				} );
		}
		return new Container
		{
			MinWidth = 0,
			OnPointerMove = e => UpdateHovered( e.WindowPosition ),
			OnPointerLeave = _ => _hovered.Value = -1,
			Children = children,
		};
	}

	/// <summary>Finds the link mark under the pointer, by the text offset under it.</summary>
	private void UpdateHovered( Point position )
	{
		var hovered = -1;
		if ( _links.Value.Length > 0 && (FindLinkAt( Input.Handle, position ) is { } drawn ? (int?)drawn
			: Input.Handle.TryGetTextPositionAt( position, TextCoordinateSpace.Window, out var found ) ? found.Offset : null) is { } offset )
		{
			for ( var index = 0; index < Input.Marks.Length; index++ )
			{
				var range = Input.Marks[index].Range;
				if ( Input.Marks[index].IsLink && offset >= range.Start && offset < range.Start + range.Length )
				{
					hovered = index;
					break;
				}
			}
		}
		if ( hovered != _hovered.Value )
			_hovered.Value = hovered;
	}

	/// <summary>Moves the layout subscription to another text handle.</summary>
	private void Follow( ElementHandle? handle )
	{
		if ( ReferenceEquals( handle, _followed ) )
			return;
		if ( _followed is not null )
			_followed.MetricsChanged -= UpdateMetrics;
		_followed = handle;
		if ( handle is not null )
		{
			handle.MetricsChanged += UpdateMetrics;
			UpdateBoxes();
		}
	}

	/// <summary>Reads the boxes again after the text wrapped differently: only a change of its size can move them, since they are in its own
	/// coordinates; a scroll moves the text, not its lines, and measuring every text on screen at each scroll step made scrolling stall.</summary>
	private void UpdateMetrics( ElementMetrics metrics )
	{
		var size = (metrics.BorderBox.Width, metrics.BorderBox.Height);
		if ( _measuredSize == size )
			return;
		_measuredSize = size;
		UpdateBoxes();
	}

	/// <summary>Stores the box rectangles and icon places of every mark, rebuilding only when they changed.</summary>
	/// <remarks>
	/// Goo 0.32.9 measures a range's rectangles, and its caret positions, in the text's own font rather than in a span's code font or bold weight,
	/// and leaves out letter spacing. So each range on a line is moved right by what Goo left out before it on that line: the extra width of earlier
	/// code and bold text, the room after the character before a box, and the room before a mention; and code is widened by its width in the code
	/// font over its width in the text's.
	/// </remarks>
	private void UpdateBoxes()
	{
		if ( _followed is not { IsMounted: true } handle )
			return;

		var boxes = new List<ElementRect>();
		var icons = new List<MentionIcon>();
		var links = new List<(int Mark, ElementRect Rect)>();
		var line = double.NaN;
		var shift = 0.0;
		foreach ( var (markIndex, mark) in Input.Marks.Index() )
		{
			if ( !handle.TryCopyTextRangeRects( mark.Range, TextCoordinateSpace.Element, _scratch, out var count ) )
				continue;
			if ( count > _scratch.Length )
			{
				_scratch = new ElementRect[count];
				handle.TryCopyTextRangeRects( mark.Range, TextCoordinateSpace.Element, _scratch, out count );
			}

			// Goo returns one rectangle per styled run, and code split for its padded last character is two runs on one line: join them per line,
			// or the code's width is counted once per run and everything after it on the line moves too far.
			var merged = new List<ElementRect>( 2 );
			for ( var i = 0; i < Math.Min( count, _scratch.Length ); i++ )
			{
				var part = _scratch[i];
				if ( part.Width <= 0 )
					continue;
				if ( merged.Count > 0 && Math.Abs( merged[^1].Y - part.Y ) < 1 )
				{
					var last = merged[^1];
					var left = Math.Min( last.X, part.X );
					merged[^1] = last with { X = left, Width = Math.Max( last.X + last.Width, part.X + part.Width ) - left };
				}
				else
					merged.Add( part );
			}
			var rects = merged.Count;
			var first = true;
			foreach ( var rect in merged )
			{
				if ( rect.Y != line )
					(line, shift) = (rect.Y, 0);
				if ( first )
				{
					if ( mark.PadBefore )
						shift += MarkdownView.CodePad;
					// The room for the icon follows the character before the mention; a mention that wraps to a new line leaves that room at the
					// end of the line before, so it shows without its icon.
					if ( mark.Glyph is { } glyph && IsOnLine( handle, mark.Range.Start - 1, rect.Y ) )
					{
						shift += MarkdownView.MentionGap;
						icons.Add( new( rect.X + shift - MarkdownView.MentionGap + 3, rect.Y, rect.Height, glyph ) );
						// The hover background covers the icon too.
						if ( mark.IsLink )
							links.Add( (markIndex, rect with { X = rect.X + shift - MarkdownView.MentionGap, Width = MarkdownView.MentionGap }) );
					}
				}
				first = false;
				// Goo measures the code font narrower than it draws it, so code's width comes from its characters on this line; bold text's from
				// the window's measure of it in both weights.
				var width = mark.IsCode ? ComputeCodeWidth( handle, mark, rect, rects )
					: mark.Weight > 0 ? rect.Width + ComputeBoldExtra( handle, mark, rect, rects )
					: rect.Width;
				if ( mark.Boxed )
					boxes.Add( rect with { X = rect.X + shift, Width = width } );
				if ( mark.IsLink )
					links.Add( (markIndex, rect with { X = rect.X + shift, Width = width }) );
				shift += width - rect.Width;
			}
			if ( mark.Boxed )
				shift += MarkdownView.CodePad;
		}
		if ( !boxes.SequenceEqual( _boxes.Value ) )
			_boxes.Value = [.. boxes];
		if ( !icons.SequenceEqual( _icons.Value ) )
			_icons.Value = [.. icons];
		// A mention's icon room and its text on one line join into one rectangle.
		var joined = new List<(int Mark, ElementRect Rect)>();
		foreach ( var link in links )
		{
			if ( joined.Count > 0 && joined[^1] is var last && last.Mark == link.Mark && Math.Abs( last.Rect.Y - link.Rect.Y ) < 1 )
				joined[^1] = (last.Mark, last.Rect with { Width = link.Rect.X + link.Rect.Width - last.Rect.X });
			else
				joined.Add( link );
		}
		if ( !joined.SequenceEqual( _links.Value ) )
			_links.Value = [.. joined];
		_drawnLinks.AddOrUpdate( handle, [.. joined.Select( link => (Input.Marks[link.Mark].Range.Start, link.Rect) )] );
	}

	/// <summary>Holds where each text's links are drawn, by the text's handle: each link's first offset and a rectangle of it, in the text's coordinates.</summary>
	private static readonly ConditionalWeakTable<ElementHandle, (int Start, ElementRect Rect)[]> _drawnLinks = [];

	/// <summary>Finds the link drawn under a point of a text and returns its first offset, or null.</summary>
	/// <remarks>Goo maps a point to text with the widths it lays out, which ignore the code font and letter spacing, so a click on a drawn mention
	/// landed on later text; the drawn rectangles say where the mention really is.</remarks>
	/// <param name="handle">The text's handle.</param>
	/// <param name="window">The point, in window coordinates.</param>
	internal static int? FindLinkAt( ElementHandle handle, Point window )
	{
		if ( !handle.IsMounted || !_drawnLinks.TryGetValue( handle, out var links ) )
			return null;
		var box = handle.BorderBox;
		var (x, y) = (window.X - box.X, window.Y - box.Y);
		foreach ( var (start, rect) in links )
		{
			if ( x >= rect.X - InsetX && x < rect.X + rect.Width + InsetX && y >= rect.Y && y < rect.Y + rect.Height )
				return start;
		}
		return null;
	}

	/// <summary>Reports whether the character at an offset sits on the line at a given top, in the text's coordinates.</summary>
	private static bool IsOnLine( ElementHandle handle, int offset, double top )
	{
		Span<ElementRect> rects = stackalloc ElementRect[2];
		return offset >= 0 && handle.TryCopyTextRangeRects( new TextRange( offset, 1 ), TextCoordinateSpace.Element, rects, out var count ) && count > 0
			&& Math.Abs( rects[0].Y - top ) < 1;
	}

	/// <summary>Defines the advance of one character of Loom's own code font, Consolas, in ems: 1126 of its 2048 units.</summary>
	private const double CodeAdvanceEms = 1126.0 / 2048;

	/// <summary>Gets how wide code on one line is drawn: its characters on that line, trailing spaces of a wrapped line left out, times the advance of
	/// the code font drawn, the one Settings picks or Loom's own.</summary>
	/// <remarks>Goo 0.32.9 lays out and measures the registered code font narrower than it draws it, so the width comes from the font's advance:
	/// every character of a monospace font is equally wide. A picked font such as Berkeley Mono is wider than Consolas, so its own advance is read.</remarks>
	private static double ComputeCodeWidth( ElementHandle handle, CodeMark mark, ElementRect rect, int rects )
	{
		var text = ComputeTextOnLine( handle, mark, rect, rects );
		var advance = Theme.Current.CodeFont is { Length: > 0 } picked && SystemFonts.GetAdvanceEms( picked ) is { } read ? read : CodeAdvanceEms;
		return new System.Globalization.StringInfo( text ).LengthInTextElements * advance * MarkdownView.BodySize;
	}

	/// <summary>Gets how much wider bold text on one line is drawn than Goo placed it: its width in its weight less its width in the regular one.</summary>
	private double ComputeBoldExtra( ElementHandle handle, CodeMark mark, ElementRect rect, int rects )
	{
		var text = ComputeTextOnLine( handle, mark, rect, rects );
		var regular = new Style { FontFamily = ViewStyle.FontFamily, FontSize = MarkdownView.BodySize };
		var bold = new Style { FontFamily = ViewStyle.FontFamily, FontSize = MarkdownView.BodySize, FontWeight = mark.Weight };
		return Host.MeasureText( text, bold ) - Host.MeasureText( text, regular );
	}

	/// <summary>Gets a mark's characters on one line, trailing spaces of a wrapped line left out.</summary>
	private static string ComputeTextOnLine( ElementHandle handle, CodeMark mark, ElementRect rect, int rects )
	{
		if ( rects <= 1 )
			return mark.Text;
		var onLine = new System.Text.StringBuilder();
		for ( var index = 0; index < mark.Text.Length; index++ )
		{
			if ( IsOnLine( handle, mark.Range.Start + index, rect.Y ) )
				onLine.Append( mark.Text[index] );
		}
		return onLine.ToString().TrimEnd();
	}
}
