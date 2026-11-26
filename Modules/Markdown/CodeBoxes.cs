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
internal sealed record CodeMark( TextRange Range, string Text, bool IsCode, bool Boxed, bool PadBefore, string? Glyph, bool IsLink = false );

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

	/// <summary>Defines how far a box reaches past its code above and below, in logical pixels.</summary>
	private const double InsetY = 2;

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
	protected override void InputChanged( CodeBoxesInput input ) => Follow( input.Handle );

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
				Top = box.Y - InsetY,
				Width = box.Width + 2 * InsetX,
				Height = box.Height + 2 * InsetY,
				// A soft fill without an outline, as Tern draws it.
				BorderRadius = theme.Radius + 1,
				BackgroundColor = theme.Raised,
				HitTestSelf = false,
			} );
		}
		var hovered = _hovered.Value is var mark and >= 0 && mark < input.Marks.Length ? input.Marks[mark] : null;
		var hoveredRects = hovered is null ? [] : _links.Value.Where( link => link.Mark == _hovered.Value ).Select( link => link.Rect ).ToArray();
		// A mention, such as a file, gets a background under the pointer, as a chip does.
		if ( hovered is { Glyph: not null } )
		{
			foreach ( var (index, rect) in hoveredRects.Index() )
				children.Add( new Container
				{
					Key = "hover:" + index,
					Position = PositionType.Absolute,
					Left = rect.X - InsetX,
					Top = rect.Y - InsetY,
					Width = rect.Width + 2 * InsetX,
					Height = rect.Height + 2 * InsetY,
					BorderRadius = theme.Radius + 1,
					BackgroundColor = theme.Hover,
					HitTestSelf = false,
				} );
		}
		foreach ( var (index, icon) in _icons.Value.Index() )
		{
			children.Add( new Text
			{
				Key = "icon:" + index,
				Content = icon.Glyph,
				Position = PositionType.Absolute,
				Left = icon.X,
				Top = icon.Y + (icon.Height - IconSize) / 2,
				Height = IconSize,
				FontFamily = ViewStyle.IconFontFamily,
				FontSize = IconSize - 1,
				LineHeight = 1,
				Color = theme.Muted,
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
		if ( _links.Value.Length > 0 && Input.Handle.TryGetTextPositionAt( position, TextCoordinateSpace.Window, out var at ) )
		{
			for ( var index = 0; index < Input.Marks.Length; index++ )
			{
				var range = Input.Marks[index].Range;
				if ( Input.Marks[index].IsLink && at.Offset >= range.Start && at.Offset < range.Start + range.Length )
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

	/// <summary>Reads the boxes again after the text moved or wrapped differently.</summary>
	private void UpdateMetrics( ElementMetrics _ ) => UpdateBoxes();

	/// <summary>Stores the box rectangles and icon places of every mark, rebuilding only when they changed.</summary>
	/// <remarks>
	/// Goo 0.32.9 measures a range's rectangles, and its caret positions, in the text's own font rather than in a span's code font, and leaves out
	/// letter spacing. So each range on a line is moved right by what Goo left out before it on that line: the extra width of earlier code, the room
	/// after the character before a box, and the room before a mention; and code is widened by its width in the code font over its width in the text's.
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

			var rects = Math.Min( count, _scratch.Length );
			var first = true;
			for ( var i = 0; i < rects; i++ )
			{
				var rect = _scratch[i];
				if ( rect.Width <= 0 )
					continue;
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
						icons.Add( new( rect.X + shift - MarkdownView.MentionGap + 2, rect.Y, rect.Height, glyph ) );
						// The hover background covers the icon too.
						if ( mark.IsLink )
							links.Add( (markIndex, rect with { X = rect.X + shift - MarkdownView.MentionGap, Width = MarkdownView.MentionGap }) );
					}
				}
				first = false;
				// Goo measures the code font narrower than it draws it, so code's width comes from its characters on this line.
				var width = mark.IsCode ? ComputeCodeWidth( handle, mark, rect, rects ) : rect.Width;
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
	}

	/// <summary>Reports whether the character at an offset sits on the line at a given top, in the text's coordinates.</summary>
	private static bool IsOnLine( ElementHandle handle, int offset, double top )
	{
		Span<ElementRect> rects = stackalloc ElementRect[2];
		return offset >= 0 && handle.TryCopyTextRangeRects( new TextRange( offset, 1 ), TextCoordinateSpace.Element, rects, out var count ) && count > 0
			&& Math.Abs( rects[0].Y - top ) < 1;
	}

	/// <summary>Defines the advance of one character of the code font, Consolas, in ems: 1126 of its 2048 units.</summary>
	private const double CodeAdvanceEms = 1126.0 / 2048;

	/// <summary>Gets how wide code on one line is drawn: its characters on that line, trailing spaces of a wrapped line left out, times the font's
	/// fixed advance.</summary>
	/// <remarks>Goo 0.32.9 lays out and measures the registered code font narrower than it draws it, so the width comes from the font's advance:
	/// every character of a monospace font is equally wide.</remarks>
	private static double ComputeCodeWidth( ElementHandle handle, CodeMark mark, ElementRect rect, int rects )
	{
		var text = mark.Text;
		if ( rects > 1 )
		{
			var onLine = new System.Text.StringBuilder();
			for ( var index = 0; index < text.Length; index++ )
			{
				if ( IsOnLine( handle, mark.Range.Start + index, rect.Y ) )
					onLine.Append( text[index] );
			}
			text = onLine.ToString().TrimEnd();
		}
		return new System.Globalization.StringInfo( text ).LengthInTextElements * CodeAdvanceEms * MarkdownView.BodySize;
	}
}
