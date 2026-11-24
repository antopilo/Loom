using System.Text;
using System.Text.RegularExpressions;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Markdown;

/// <summary>Maps Markdown blocks and inline emphasis to selectable native Goo text; the Markdown module publishes it as <see cref="IMarkdownRenderer"/>.</summary>
public static partial class MarkdownView
{
	/// <summary>Defines body text size in logical pixels.</summary>
	public const int BodySize = 14;

	/// <summary>Defines body line height as a multiple of its font size.</summary>
	public const double BodyLineHeight = 1.5;

	/// <summary>Defines code block text size in logical pixels.</summary>
	private const int CodeSize = 13;

	/// <summary>Defines the code block scroll height in logical pixels.</summary>
	private const int CodeHeight = 300;

	/// <summary>Defines the most code lines rendered in one block; all shown lines share one selectable text element.</summary>
	public const int MaxCodeLines = 500;

	/// <summary>Defines the emphasis weight used for headings and table headers.</summary>
	private const int EmphasisWeight = 600;

	/// <summary>Defines regular font weight on the OpenType scale.</summary>
	private const int RegularWeight = 400;

	/// <summary>Defines the code and quote inset in logical pixels.</summary>
	private const int BlockInset = 12;

	/// <summary>Defines the code line height as a multiple of the code font size, matching the former per-line gap.</summary>
	private const double CodeLineHeight = 1.45;

	/// <summary>Defines the gap between a code block's badge, code text and cut note in logical pixels.</summary>
	private const int CodeGap = 2;

	/// <summary>Defines the vertical space between ordinary blocks in logical pixels.</summary>
	private const int BlockGap = 12;

	/// <summary>Defines the space above first- and second-level headings in logical pixels.</summary>
	private const int MajorHeadingGap = 22;

	/// <summary>Defines the space above deeper headings in logical pixels.</summary>
	private const int MinorHeadingGap = 16;

	/// <summary>Defines the space between a heading and the block it introduces in logical pixels.</summary>
	private const int HeadingBodyGap = 6;

	/// <summary>Defines heading line height as a multiple of its font size.</summary>
	private const double HeadingLineHeight = 1.3;

	/// <summary>Defines the vertical space between list items in logical pixels.</summary>
	private const int ListItemGap = 4;

	/// <summary>Defines the minimum list marker column width in logical pixels.</summary>
	private const int MarkerWidth = 18;

	/// <summary>Defines the space between a list marker and its text in logical pixels.</summary>
	private const int MarkerGap = 6;

	/// <summary>Defines the drawn bullet diameter in logical pixels.</summary>
	private const int BulletSize = 5;

	/// <summary>Defines the indentation width of one tab in source columns.</summary>
	private const int TabWidth = 4;

	/// <summary>Defines the estimated advance of one body character in ems, used to size markers and table columns.</summary>
	private const double CharacterEms = 0.55;

	/// <summary>Defines the longest content, in characters, that sets a table column's preferred width.</summary>
	private const int MaxColumnCharacters = 80;

	/// <summary>Defines the longest word, in characters, a table column keeps unbroken before the table scrolls.</summary>
	private const int MaxUnbrokenCharacters = 16;

	/// <summary>Defines the vertical table cell inset in logical pixels.</summary>
	private const int CellInsetY = 6;

	/// <summary>Defines the horizontal table cell inset in logical pixels.</summary>
	private const int CellInsetX = 10;

	/// <summary>Defines table cell line height as a multiple of its font size.</summary>
	private const double CellLineHeight = 1.4;

	/// <summary>Defines how many opacity levels a fading tail uses, so characters revealed together share one span.</summary>
	private const int FadeSteps = 20;

	/// <summary>Defines heading sizes for levels one through three in logical pixels; deeper levels use body size.</summary>
	private static readonly int[] HeadingSizes = [19, 16, 15];

	/// <summary>Defines unordered list bullets by nesting depth.</summary>
	private static readonly string[] Bullets = ["•", "◦", "▪"];

	/// <summary>Builds one native symbol without relying on an unregistered fallback font.</summary>
	public static Text BuildMarker( string marker ) => new()
	{
		Content = marker,
		FontFamily = ViewStyle.SymbolFontFamily,
		Color = Theme.Current.Text,
		FontSize = Theme.Current.FontSize,
	};

	/// <summary>Builds headings, paragraphs, lists, checklists, quotes, tables, and fenced code with block-specific spacing.</summary>
	/// <param name="markdown">The Markdown source.</param>
	/// <param name="openLink">Opens a web link the reader follows.</param>
	/// <param name="fade">The opacity of a visible character by how many visible characters follow it, so freshly streamed words fade in; it must not fall as the count grows. Null renders everything at rest.</param>
	public static Blob Build( string markdown, Action<Uri> openLink, Func<int, double>? fade = null ) => BuildBlocks( markdown, openLink, fade, 0 );

	/// <summary>Builds the blocks of <paramref name="markdown"/>, fading its tail when <paramref name="after"/> more visible characters follow it.</summary>
	private static Container BuildBlocks( string markdown, Action<Uri> openLink, Func<int, double>? fade, int after )
	{
		var blocks = Parse( markdown );
		var following = ComputeFollowing( blocks, fade, after );
		var children = new List<Blob>();
		MarkdownBlock? previous = null;
		for ( var index = 0; index < blocks.Count; index++ )
		{
			var block = blocks[index];
			Blob child;
			if ( block.Kind == "list" )
			{
				// Consecutive items share one marker column so nested and numbered items stay aligned.
				var count = 1;
				while ( index + count < blocks.Count && blocks[index + count].Kind == "list" )
					count++;

				child = BuildList( blocks.Skip( index ).Take( count ).ToArray(), openLink, fade, following, index );
				index += count - 1;
			}
			else
				child = BuildBlock( block, openLink, fade, following?[index] ?? 0 );

			child.Margin = new Edges( ComputeSpacing( previous, block ), 0, 0, 0 );
			children.Add( child );
			previous = block;
		}

		return new Container
		{
			MinWidth = 0,
			Children = children,
		};
	}

	/// <summary>Computes, for each block, how many visible characters follow it; null when nothing fades.</summary>
	private static int[]? ComputeFollowing( IReadOnlyList<MarkdownBlock> blocks, Func<int, double>? fade, int after )
	{
		if ( fade is null )
			return null;

		var following = new int[blocks.Count];
		var running = after;
		for ( var index = blocks.Count - 1; index >= 0; index-- )
		{
			following[index] = running;
			// Opacity never falls with distance, so once a block ends fully opaque every earlier block is at rest too.
			if ( running != int.MaxValue )
				running = fade( running ) >= 1 ? int.MaxValue : running + ComputeVisibleLength( blocks[index] );
		}
		return following;
	}

	/// <summary>Computes how many characters a block shows: its inline text without markup, or its source for code, tables and quotes.</summary>
	private static int ComputeVisibleLength( MarkdownBlock block ) => block.Kind is "paragraph" or "heading" or "list"
		? ParseInlineRuns( block.Text ).Sum( run => run.Text.Length )
		: block.Text.Length;

	/// <summary>Computes a fading character's opacity, rounded to <see cref="FadeSteps"/> levels.</summary>
	private static double ComputeFade( Func<int, double> fade, int distance ) => Math.Round( Math.Clamp( fade( distance ), 0, 1 ) * FadeSteps ) / FadeSteps;

	/// <summary>Computes the space above a block from its relationship to the preceding block.</summary>
	private static int ComputeSpacing( MarkdownBlock? previous, MarkdownBlock current )
	{
		if ( previous is null )
			return 0;

		if ( previous.Value.Kind == "heading" )
			return HeadingBodyGap;

		if ( current.Kind == "heading" )
			return current.Marker.Length <= 2 ? MajorHeadingGap : MinorHeadingGap;

		return BlockGap;
	}

	/// <summary>Builds one non-list block, fading its tail when <paramref name="after"/> visible characters follow it.</summary>
	private static Blob BuildBlock( MarkdownBlock block, Action<Uri> openLink, Func<int, double>? fade = null, int after = 0 )
	{
		if ( block.Kind == "code" )
			return BuildCode( block.Text, block.Marker, isRunnable: true );

		if ( block.Kind == "table" )
			return BuildTable( block.Text, openLink );

		if ( block.Kind == "rule" )
		{
			return new Container
			{
				Height = 1,
				BackgroundColor = Theme.Current.Border,
			};
		}

		if ( block.Kind == "quote" )
		{
			return new Container
			{
				Padding = new Edges( Theme.Current.Spacing, BlockInset ),
				BorderWidth = new Edges( 0, 0, 0, 2 ),
				BorderColor = Theme.Current.Accent,
				MinWidth = 0,
				Children = [BuildBlocks( block.Text, openLink, fade, after )],
			};
		}

		var text = BuildInlineText( block.Text, openLink, fade: fade, after: after );
		if ( block.Kind == "heading" )
		{
			var level = block.Marker.Length;
			text.FontWeight = EmphasisWeight;
			text.FontSize = level <= HeadingSizes.Length ? HeadingSizes[level - 1] : BodySize;
			text.LineHeight = HeadingLineHeight;
		}

		return text;
	}

	/// <summary>Builds consecutive list items with a shared marker column and hanging, nested indentation.</summary>
	/// <param name="items">The consecutive list items.</param>
	/// <param name="fade">The tail opacity by following visible characters, or null at rest.</param>
	/// <param name="following">Visible characters after each block of the enclosing document, or null at rest.</param>
	/// <param name="first">The index of the first item in <paramref name="following"/>.</param>
	private static Container BuildList( IReadOnlyList<MarkdownBlock> items, Action<Uri> openLink, Func<int, double>? fade = null, int[]? following = null, int first = 0 )
	{
		var markerWidth = Math.Max( MarkerWidth, (int)Math.Ceiling( items.Max( item => item.Marker.Length ) * CharacterEms * BodySize ) );
		var rows = new List<Blob>();
		for ( var index = 0; index < items.Count; index++ )
		{
			var item = items[index];
			rows.Add( new Row
			{
				Gap = MarkerGap,
				MinWidth = 0,
				MarginStart = item.Depth * (markerWidth + MarkerGap),
				AlignItems = AlignItems.FlexStart,
				Children =
				[
					BuildListMarker( item.Marker, markerWidth ),
					BuildInlineText( item.Text, openLink, grow: true, fade: fade, after: following?[first + index] ?? 0 ),
				],
			} );
		}

		return new Container
		{
			Gap = ListItemGap,
			MinWidth = 0,
			Children = rows,
		};
	}

	/// <summary>Builds a list marker sized to the first text line: drawn bullets, drawn task marks, or right-aligned numbers.</summary>
	private static Blob BuildListMarker( string marker, int width )
	{
		// Checklist items get drawn marks, so a plan reads as a checklist.
		if ( marker is "☑" or "☐" )
		{
			return new Container
			{
				Width = width,
				Height = BodySize * BodyLineHeight,
				FlexShrink = 0,
				AlignItems = AlignItems.Center,
				JustifyContent = JustifyContent.Center,
				Children = [BuildTaskMark( marker == "☑" )],
			};
		}

		var bullet = Array.IndexOf( Bullets, marker );
		if ( bullet < 0 )
		{
			return new Text
			{
				Content = marker,
				Width = width,
				FlexShrink = 0,
				TextAlign = char.IsDigit( marker[0] ) ? TextAlign.Right : TextAlign.Center,
				FontFamily = ViewStyle.FontFamily,
				FontSize = BodySize,
				LineHeight = BodyLineHeight,
				Color = Theme.Current.Text,
			};
		}

		// Font bullets render too small and faint at body size, so bullets are drawn: disc, ring, then square.
		return new Container
		{
			Width = width,
			Height = BodySize * BodyLineHeight,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			Children =
			[
				new Container
				{
					Width = BulletSize,
					Height = BulletSize,
					BorderRadius = bullet == 2 ? 0 : BulletSize / 2.0,
					BorderWidth = bullet == 1 ? Theme.Current.BorderWidth : 0,
					BorderColor = Theme.Current.Text,
					BackgroundColor = bullet == 1 ? Color.Transparent : Theme.Current.Text,
				},
			],
		};
	}

	/// <summary>Gets or sets the action that copies text to the clipboard; the Markdown module sets it while it runs, and code blocks show Copy only then.</summary>
	public static Action<string>? CopyText
	{
		get; set;
	}

	/// <summary>Gets or sets the test of whether a fenced-code language runs as a shell snippet; the Markdown module sets it while it runs.</summary>
	public static Func<string, bool>? CanRunCode
	{
		get; set;
	}

	/// <summary>Gets or sets the action that runs a shell snippet in a terminal beside the chat; the Markdown module sets it while it runs.</summary>
	public static Action<string, string>? RunCode
	{
		get; set;
	}

	/// <summary>Builds a selectable code or diff block with a language badge, syntax colors and internal scrolling.</summary>
	/// <param name="code">The code to show.</param>
	/// <param name="language">The fenced-code or tool language; it picks the colors and labels the block.</param>
	/// <param name="framed">Whether the block draws its own raised background and inset.</param>
	/// <param name="showLanguage">Whether the language badge is shown; a tool head that already names it leaves it out.</param>
	/// <param name="fadeLines">How many trailing lines fade out, marking a cut preview.</param>
	/// <param name="isRunnable">Whether a shell snippet gets a Run button; written code blocks do, tool output does not.</param>
	public static Blob BuildCode( string code, string language = "", bool framed = true, bool showLanguage = true, int fadeLines = 0, bool isRunnable = false )
	{
		var lines = code.Replace( "\r\n", "\n" ).Split( '\n' );
		var children = new List<Blob>();
		// The header holds the language, a Run button for shell snippets, and a Copy button, which copies every line, also those not shown.
		var header = new List<Blob>();
		if ( showLanguage && language.Length > 0 && language != "text" )
			header.Add( BuildBadge( language ) );
		if ( isRunnable && RunCode is { } run && CanRunCode?.Invoke( language ) == true && code.Trim().Length > 0 )
			header.Add( BuildCodeButton( "Run", "Run in terminal", () => run( code, language ) ) );
		if ( CopyText is { } copy && code.Length > 0 )
			header.Add( BuildCodeButton( "Copy", "Copy code", () => copy( code ) ) );
		if ( header.Count > 0 )
			children.Add( new Row { Key = "code-language", JustifyContent = JustifyContent.FlexEnd, AlignItems = AlignItems.Center, Gap = Theme.Current.Spacing, Children = header } );
		var grammar = CodeHighlighter.Find( language );
		var isInComment = false;
		var shown = Math.Min( lines.Length, MaxCodeLines );
		var spans = new List<TextSpan>();
		for ( var index = 0; index < shown; index++ )
		{
			var line = lines[index];
			var color = language == "diff" && line.StartsWith( '+' ) ? Theme.Current.Success
				: language == "diff" && line.StartsWith( '-' ) ? Theme.Current.Error : Theme.Current.Text;
			// A cut preview fades out over its last lines, so it reads as continuing rather than ending.
			// Opacity is per element and all lines share one text, so the fade goes into the span colors; Goo styles are
			// write-only, so a faded line cannot tint its token colors and shows in the plain line color instead.
			var alpha = shown - index <= fadeLines ? (double)(shown - index) / (fadeLines + 1) : 1.0;
			var lineSpans = grammar is null || line.Length == 0 ? new[] { new TextSpan( ViewStyle.CutCodeLine( line ), null! ) } : BuildHighlightedLine( line, grammar, ref isInComment );
			foreach ( var span in lineSpans )
				spans.Add( span.Style is null || alpha < 1.0 ? new TextSpan( span.Text, new Style { Color = color.WithAlpha( alpha ) } ) : span );
			if ( index < shown - 1 )
				spans.Add( new TextSpan( "\n", null! ) );
		}
		children.Add( new Text( [.. spans] ) { Selectable = true, FontFamily = ViewStyle.CodeFontFamily, FontSize = CodeSize, LineHeight = CodeLineHeight, Color = Theme.Current.Text } );
		if ( lines.Length > MaxCodeLines )
			children.Add( new Text { Content = $"… {lines.Length - MaxCodeLines:N0} more lines not shown", Color = Theme.Current.Muted, FontFamily = ViewStyle.FontFamily, FontSize = CodeSize, Selectable = true, MinWidth = 0 } );

		return new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = language == "diff" ? "Diff" : "Code block",
			Padding = framed ? BlockInset : 0,
			Gap = CodeGap,
			MaxHeight = CodeHeight,
			OverflowY = Overflow.Scroll,
			ScrollbarY = ViewStyle.BuildScrollbar(),
			BackgroundColor = framed ? Theme.Current.Raised : Color.Transparent,
			BorderRadius = Theme.Current.Radius,
			Children = children,
		};
	}

	/// <summary>Builds one quiet button of a code block's header.</summary>
	private static Blob BuildCodeButton( string label, string name, Action action )
	{
		var button = Theme.Current.BuildButton( label, action );
		button.AccessibilityName = name;
		button.Color = Theme.Current.Muted;
		button.FontSize = CodeSize;
		button.FlexShrink = 0;
		return button;
	}

	/// <summary>Builds a checklist mark: an empty ring, or a filled disc with a check once done.</summary>
	private static Container BuildTaskMark( bool isDone )
	{
		var theme = Theme.Current;
		const int size = 14;
		return new Container
		{
			Width = size,
			Height = size,
			FlexShrink = 0,
			BorderRadius = size / 2.0,
			BorderWidth = isDone ? 0 : 1.5,
			BorderColor = theme.Muted,
			BackgroundColor = isDone ? theme.Success : Color.Transparent,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			Children = isDone ? [new Text { Content = "✓", FontFamily = ViewStyle.SymbolFontFamily, FontSize = 10, Color = theme.Panel }] : [],
		};
	}

	/// <summary>Builds a small rounded label, such as a code block's language.</summary>
	private static Container BuildBadge( string text ) => new()
	{
		FlexShrink = 0,
		Padding = new Edges( 1, 6 ),
		BorderRadius = Theme.Current.Radius,
		BackgroundColor = Theme.Current.Hover,
		Children = [new Text { Content = text, FontFamily = ViewStyle.CodeFontFamily, FontSize = ViewStyle.SmallSize - 1, Color = Theme.Current.Muted, TextWrap = TextWrap.NoWrap }],
	};

	/// <summary>Builds the colored spans of one code line, cut like <see cref="ViewStyle.CutCodeLine"/> when it is too long to lay out.</summary>
	private static TextSpan[] BuildHighlightedLine( string line, CodeHighlighter.Grammar grammar, ref bool isInComment )
	{
		var length = ViewStyle.ComputeCodeLineLength( line );
		var spans = CodeHighlighter.BuildSpans( line[..length], grammar, ref isInComment );
		if ( length < line.Length )
			spans = [.. spans, new( $" … {line.Length - length:N0} more characters not shown", new Style { Color = Theme.Current.Muted } )];
		return spans;
	}

	/// <summary>Builds a table whose columns share widths across rows and scroll when they cannot fit.</summary>
	private static Blob BuildTable( string markdown, Action<Uri> openLink )
	{
		var lines = markdown.Split( '\n' );
		var alignments = SplitCells( lines[1] ).Select( ComputeAlignment ).ToArray();
		var rows = lines
			.Where( ( _, index ) => index != 1 )
			.Select( SplitCells )
			.ToArray();

		// Goo has no table layout, so every row receives the same estimated column bases and minimums.
		// Flex shrinking is then identical across rows, which keeps the columns aligned while cells wrap.
		var columnCount = rows[0].Count;
		var preferred = new int[columnCount];
		var minimum = new int[columnCount];
		foreach ( var row in rows )
		{
			for ( var column = 0; column < columnCount && column < row.Count; column++ )
			{
				var visible = string.Concat( ParseInline( row[column] ).Select( span => span.Text ) );
				var longestWord = visible.Split( ' ' ).Max( word => word.Length );
				preferred[column] = Math.Max( preferred[column], Math.Min( visible.Length, MaxColumnCharacters ) );
				minimum[column] = Math.Max( minimum[column], Math.Min( longestWord, MaxUnbrokenCharacters ) );
			}
		}

		var tableRows = new List<Blob>();
		for ( var index = 0; index < rows.Length; index++ )
		{
			var isHeader = index == 0;
			var cells = new List<Blob>();
			for ( var column = 0; column < columnCount; column++ )
			{
				var text = BuildInlineText( column < rows[index].Count ? rows[index][column] : "", openLink );
				text.FontWeight = isHeader ? EmphasisWeight : RegularWeight;
				text.LineHeight = CellLineHeight;
				text.TextAlign = column < alignments.Length ? alignments[column] : TextAlign.Left;
				text.Padding = new Edges( CellInsetY, CellInsetX );
				text.FlexBasis = ComputeColumnWidth( preferred[column] );
				text.FlexShrink = 1;
				text.MinWidth = ComputeColumnWidth( minimum[column] );
				cells.Add( text );
			}

			tableRows.Add( new Row
			{
				BackgroundColor = isHeader ? Theme.Current.Raised : Color.Transparent,
				BorderWidth = new Edges( isHeader ? 0 : Theme.Current.BorderWidth, 0, 0, 0 ),
				BorderColor = Theme.Current.Border,
				Children = cells,
			} );
		}

		// A scrolling viewport lets the content-sized table shrink to fit, wrapping cells, and scroll sideways only once
		// every column reaches its minimum; its height fits the table, so the wheel still scrolls the chat. It scrolls on
		// both axes because Goo 0.31 never draws a horizontal-only viewport that scrolls into view inside a vertical one.
		return new Row
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Table",
			MinWidth = 0,
			Overflow = Overflow.Scroll,
			AlignItems = AlignItems.FlexStart,
			Children =
			[
				new Container
				{
					// Em widths resolve against this font size, matching the cells' estimates. An explicit basis keeps
					// the table width independent of any one row's content, so all rows shrink identically.
					FontSize = BodySize,
					FlexBasis = ComputeTableWidth( preferred ),
					FlexShrink = 1,
					MinWidth = ComputeTableWidth( minimum ),
					BorderWidth = Theme.Current.BorderWidth,
					BorderColor = Theme.Current.Border,
					BorderRadius = Theme.Current.Radius,
					Overflow = Overflow.Hidden,
					Children = tableRows,
				},
			],
		};
	}

	/// <summary>Computes a table column width from its estimated character count, including cell insets.</summary>
	private static Length ComputeColumnWidth( int characters ) => Length.Em( Math.Max( characters, 1 ) * CharacterEms ) + 2 * CellInsetX;

	/// <summary>Computes a table width from its estimated column character counts, including cell insets and borders.</summary>
	private static Length ComputeTableWidth( int[] columns ) =>
		Length.Em( columns.Sum( characters => Math.Max( characters, 1 ) ) * CharacterEms ) + 2 * (CellInsetX * columns.Length + Theme.Current.BorderWidth);

	/// <summary>Computes horizontal alignment from one Markdown table separator cell.</summary>
	private static TextAlign ComputeAlignment( string separator )
	{
		if ( separator.StartsWith( ':' ) && separator.EndsWith( ':' ) )
			return TextAlign.Center;

		return separator.EndsWith( ':' ) ? TextAlign.Right : TextAlign.Left;
	}

	/// <summary>Splits a table row on unescaped pipes outside inline code spans.</summary>
	private static List<string> SplitCells( string line )
	{
		var row = line.Trim();
		if ( row.StartsWith( '|' ) )
			row = row[1..];

		if ( row.EndsWith( '|' ) && !row.EndsWith( "\\|", StringComparison.Ordinal ) )
			row = row[..^1];

		var cells = new List<string>();
		var cell = new StringBuilder();
		var isCode = false;
		for ( var index = 0; index < row.Length; index++ )
		{
			var character = row[index];
			if ( character == '\\' && index + 1 < row.Length && row[index + 1] == '|' )
			{
				cell.Append( '|' );
				index++;
				continue;
			}

			if ( character == '`' )
				isCode = !isCode;

			if ( character == '|' && !isCode )
			{
				cells.Add( cell.ToString().Trim() );
				cell.Clear();
				continue;
			}

			cell.Append( character );
		}

		cells.Add( cell.ToString().Trim() );
		return cells;
	}

	/// <summary>Builds inline bold, italic, code, strike-through and link spans in prose typography, fading its tail when <paramref name="fade"/> is given.</summary>
	/// <remarks>A click on a link's text opens it; a drag that selects text does not.</remarks>
	private static Text BuildInlineText( string text, Action<Uri>? openLink, bool grow = false, Func<int, double>? fade = null, int after = 0 )
	{
		var runs = ParseInlineRuns( text );
		var view = new Text( BuildInlineSpans( text, runs, fade, after ) )
		{
			Selectable = true,
			FontFamily = ViewStyle.FontFamily,
			FontSize = BodySize,
			LineHeight = BodyLineHeight,
			Color = Theme.Current.Text,
			MinWidth = 0,
			FlexGrow = grow ? 1 : 0,
		};
		if ( openLink is null || !runs.Any( run => run.Link is not null ) )
			return view;

		// Each link's range in the shown text, which is the runs' text in order.
		var links = new List<(int Start, int End, Uri Link)>();
		var offset = 0;
		foreach ( var run in runs )
		{
			if ( run.Link is { } link )
				links.Add( (offset, offset + run.Text.Length, link) );
			offset += run.Text.Length;
		}

		// The press lives outside this declaration: a rebuild between press and release replaces these handlers, and a press
		// remembered only in this build's closure was lost, so the link did nothing.
		var handle = new ElementHandle();
		view.Handle = handle;
		view.OnPointerDown = e => _linkPress = e.Button == PointerButton.Primary ? e.WindowPosition : null;
		view.OnPointerUp = e =>
		{
			var pressed = _linkPress;
			_linkPress = null;
			if ( pressed is not { } start || e.Button != PointerButton.Primary )
				return;

			var end = e.WindowPosition;
			if ( Math.Abs( end.X - start.X ) > ClickSlop || Math.Abs( end.Y - start.Y ) > ClickSlop
				|| !handle.TryGetTextPositionAt( end, TextCoordinateSpace.Window, out var position ) )
				return;

			foreach ( var (from, to, link) in links )
			{
				if ( position.Offset >= from && position.Offset < to )
				{
					openLink( link );
					return;
				}
			}
		};
		return view;
	}

	/// <summary>Builds inline spans; with a fade, characters split into runs of equal opacity by how many visible characters follow them.</summary>
	private static TextSpan[] BuildInlineSpans( string text, List<InlineRun> runs, Func<int, double>? fade, int after )
	{
		if ( fade is null || fade( after ) >= 1 )
			return [.. runs.Select( run => new TextSpan( run.Text, BuildInlineStyle( run.Kind, 1 )! ) )];

		var spans = new List<TextSpan>();
		var remaining = runs.Sum( run => run.Text.Length );
		foreach ( var (value, kind, _) in runs )
		{
			if ( value.Length == 0 )
				continue;
			var start = 0;
			var alpha = ComputeFade( fade, after + remaining - 1 );
			for ( var index = 1; index <= value.Length; index++ )
			{
				// The halves of a surrogate pair stay in one span.
				if ( index < value.Length && char.IsLowSurrogate( value[index] ) )
					continue;
				var next = index < value.Length ? ComputeFade( fade, after + remaining - 1 - index ) : -1;
				if ( next == alpha )
					continue;
				spans.Add( new( value[start..index], BuildInlineStyle( kind, alpha )! ) );
				start = index;
				alpha = next;
			}
			remaining -= value.Length;
		}
		if ( spans.Count == 0 )
			spans.Add( new( text, null! ) );
		return [.. spans];
	}

	/// <summary>Parses inline Markdown without interpreting embedded HTML or escape sequences.</summary>
	public static IReadOnlyList<TextSpan> ParseInline( string text ) => [.. ParseInlineRuns( text ).Select( run => new TextSpan( run.Text, BuildInlineStyle( run.Kind, 1 )! ) )];

	/// <summary>Splits inline Markdown into visible text runs and their emphasis.</summary>
	/// <remarks>
	/// Code spans are found first, as CommonMark does: a run of backticks closes at the next run of the same length, so <c>``a`b``</c> works.
	/// Each span is masked by one private-use character before emphasis is matched, so emphasis around code, such as <c>**`file.cs`**</c>,
	/// and link labels with code keep their formatting instead of showing the backticks.
	/// </remarks>
	private static List<InlineRun> ParseInlineRuns( string text )
	{
		var codes = new List<string>();
		var masked = new StringBuilder( text.Length );
		var index = 0;
		while ( index < text.Length )
		{
			if ( text[index] != '`' || codes.Count >= CodeMaskCount )
			{
				masked.Append( text[index++] );
				continue;
			}

			var length = 1;
			while ( index + length < text.Length && text[index + length] == '`' )
				length++;
			var close = FindBacktickRun( text, index + length, length );
			if ( close < 0 )
			{
				// An unclosed run is literal text, such as a lone backtick or a span still streaming.
				masked.Append( text, index, length );
				index += length;
				continue;
			}

			var content = text[(index + length)..close];
			// One space on both sides is padding that lets a span start or end with a backtick.
			if ( content.Length > 2 && content[0] == ' ' && content[^1] == ' ' && content.Trim().Length > 0 )
				content = content[1..^1];
			masked.Append( (char)(CodeMask + codes.Count) );
			codes.Add( content );
			index = close + length;
		}

		var runs = new List<InlineRun>();
		AddEmphasisRuns( masked.ToString(), InlineKind.Plain, null, codes, runs );
		if ( runs.Count == 0 )
			runs.Add( new( text, InlineKind.Plain, null ) );
		return runs;
	}

	/// <summary>Finds the next run of exactly a number of backticks at or after a position, or -1.</summary>
	private static int FindBacktickRun( string text, int start, int length )
	{
		for ( var index = start; index < text.Length; index++ )
		{
			if ( text[index] != '`' )
				continue;
			var end = index;
			while ( end < text.Length && text[end] == '`' )
				end++;
			if ( end - index == length )
				return index;
			index = end - 1;
		}
		return -1;
	}

	/// <summary>Adds the runs of masked text, matching emphasis and links and parsing their contents again with the outer emphasis and link added.</summary>
	private static void AddEmphasisRuns( string text, InlineKind outer, Uri? link, List<string> codes, List<InlineRun> runs )
	{
		var position = 0;
		foreach ( Match match in InlinePattern().Matches( text ) )
		{
			if ( match.Index > position )
				AddCodeRuns( text[position..match.Index], outer, link, codes, runs );
			// Groups one through five capture the visible text of each alternative; a link's destination follows in six.
			var group = 1;
			while ( !match.Groups[group].Success )
				group++;
			var kind = group switch
			{
				1 or 2 => InlineKind.Bold,
				3 => InlineKind.Strike,
				4 => InlineKind.Italic,
				_ => InlineKind.Link,
			};
			var inner = kind == InlineKind.Link ? ParseWebLink( match.Groups[6].Value ) ?? link : link;
			AddEmphasisRuns( match.Groups[group].Value, outer | kind, inner, codes, runs );
			position = match.Index + match.Length;
		}
		if ( position < text.Length )
			AddCodeRuns( text[position..], outer, link, codes, runs );
	}

	/// <summary>Adds text with its masked code spans restored as code runs, and bare web addresses outside a link as link runs.</summary>
	private static void AddCodeRuns( string text, InlineKind kind, Uri? link, List<string> codes, List<InlineRun> runs )
	{
		var start = 0;
		for ( var index = 0; index < text.Length; index++ )
		{
			var code = text[index] - CodeMask;
			if ( code < 0 || code >= codes.Count )
				continue;
			if ( index > start )
				AddUrlRuns( text[start..index], kind, link, runs );
			runs.Add( new( codes[code], kind | InlineKind.Code, link ?? ParseBareUrl( codes[code] ) ) );
			start = index + 1;
		}
		if ( start < text.Length )
			AddUrlRuns( text[start..], kind, link, runs );
	}

	/// <summary>Adds plain text, splitting out each bare web address as a link run unless the text already belongs to a link.</summary>
	private static void AddUrlRuns( string text, InlineKind kind, Uri? link, List<InlineRun> runs )
	{
		if ( link is not null )
		{
			runs.Add( new( text, kind, link ) );
			return;
		}

		var position = 0;
		foreach ( Match match in BareUrlPattern().Matches( text ) )
		{
			// Closing punctuation after an address ends the sentence, not the address.
			var value = match.Value.TrimEnd( '.', ',', ';', ':', '!', '?', '\'', '"' );
			if ( ParseWebLink( value ) is not { } uri )
				continue;
			if ( match.Index > position )
				runs.Add( new( text[position..match.Index], kind, null ) );
			runs.Add( new( value, kind | InlineKind.Link, uri ) );
			position = match.Index + value.Length;
		}
		if ( position < text.Length )
			runs.Add( new( text[position..], kind, null ) );
	}

	/// <summary>Reads a code span that is exactly one web address, such as <c>`https://example.com`</c>, or null.</summary>
	private static Uri? ParseBareUrl( string code ) => BareUrlPattern().Match( code ) is { Success: true } match && match.Length == code.Length ? ParseWebLink( code ) : null;

	/// <summary>Reads an absolute http or https address, the only links a click opens, or null.</summary>
	private static Uri? ParseWebLink( string destination ) =>
		Uri.TryCreate( destination.Trim(), UriKind.Absolute, out var uri ) && uri.Scheme is "https" or "http" ? uri : null;

	/// <summary>Defines how far, in logical pixels, the pointer may move between press and release and still count as a click on a link.</summary>
	private const double ClickSlop = 4;

	/// <summary>Stores where the primary pointer pressed link text, in window coordinates, until it is released; shared by every link text, since one pointer presses at a time.</summary>
	private static Point? _linkPress;

	/// <summary>Stores one run of visible inline text, its emphasis, and the web address it links to, if any.</summary>
	private readonly record struct InlineRun( string Text, InlineKind Kind, Uri? Link );

	/// <summary>Builds the style of one inline run at an opacity; plain text at full opacity needs none.</summary>
	private static Style? BuildInlineStyle( InlineKind kind, double alpha )
	{
		if ( kind == InlineKind.Plain && alpha >= 1 )
			return null;

		var style = new Style();
		if ( kind.HasFlag( InlineKind.Bold ) )
			style.FontWeight = EmphasisWeight;
		if ( kind.HasFlag( InlineKind.Italic ) )
			style.FontStyle = FontStyle.Italic;
		if ( kind.HasFlag( InlineKind.Code ) )
			style.FontFamily = ViewStyle.CodeFontFamily;
		if ( kind.HasFlag( InlineKind.Link ) )
			style.TextDecoration = TextDecoration.Underline;
		else if ( kind.HasFlag( InlineKind.Strike ) )
			style.TextDecoration = TextDecoration.LineThrough;
		var color = kind.HasFlag( InlineKind.Code ) || kind.HasFlag( InlineKind.Link ) ? Theme.Current.Accent : Theme.Current.Text;
		if ( alpha < 1 )
			style.Color = color.WithAlpha( color.A * alpha );
		else if ( color != Theme.Current.Text )
			style.Color = color;
		return style;
	}

	/// <summary>Defines the first private-use character that masks a code span while emphasis is matched.</summary>
	private const char CodeMask = '\uF000';

	/// <summary>Defines how many code spans one text can mask; further backticks stay literal.</summary>
	private const int CodeMaskCount = 0x800;

	/// <summary>Names the emphasis of one inline run; nested emphasis combines.</summary>
	[Flags]
	private enum InlineKind
	{
		/// <summary>Unformatted text.</summary>
		Plain = 0,

		/// <summary>Strong emphasis.</summary>
		Bold = 1,

		/// <summary>An inline code span.</summary>
		Code = 2,

		/// <summary>Struck-through text.</summary>
		Strike = 4,

		/// <summary>Italic emphasis.</summary>
		Italic = 8,

		/// <summary>A link label.</summary>
		Link = 16,
	}

	/// <summary>Parses block structure, list nesting, and soft line breaks while keeping code bodies and incomplete streaming fences intact.</summary>
	public static IReadOnlyList<MarkdownBlock> Parse( string markdown )
	{
		var lines = markdown.Replace( "\r\n", "\n" ).Split( '\n' );
		var blocks = new List<MarkdownBlock>();
		var paragraph = new StringBuilder();
		var listIndents = new Stack<int>();
		var isAfterBlank = false;
		var isHardBreak = false;
		for ( var index = 0; index < lines.Length; index++ )
		{
			var line = lines[index];
			var trimmed = line.TrimStart();
			if ( string.IsNullOrWhiteSpace( line ) )
			{
				FlushParagraph( paragraph, blocks );
				isAfterBlank = true;
				continue;
			}

			if ( trimmed.StartsWith( "```", StringComparison.Ordinal ) || trimmed.StartsWith( "~~~", StringComparison.Ordinal ) )
			{
				FlushParagraph( paragraph, blocks );
				listIndents.Clear();
				var fence = trimmed[..3];
				var language = trimmed[3..].Trim();
				var code = new List<string>();
				while ( ++index < lines.Length && !lines[index].TrimStart().StartsWith( fence, StringComparison.Ordinal ) )
					code.Add( lines[index] );
				blocks.Add( new( "code", string.Join( "\n", code ), language ) );
			}
			else if ( index + 1 < lines.Length && line.Contains( '|' ) && TableRulePattern().IsMatch( lines[index + 1] ) )
			{
				FlushParagraph( paragraph, blocks );
				listIndents.Clear();
				var table = new List<string> { line, lines[++index] };
				while ( index + 1 < lines.Length && lines[index + 1].Contains( '|' ) && !string.IsNullOrWhiteSpace( lines[index + 1] ) )
					table.Add( lines[++index] );
				blocks.Add( new( "table", string.Join( "\n", table ), "" ) );
			}
			else if ( HeadingPattern().Match( trimmed ) is { Success: true } heading )
			{
				FlushParagraph( paragraph, blocks );
				listIndents.Clear();
				blocks.Add( new( "heading", heading.Groups[2].Value, heading.Groups[1].Value ) );
			}
			else if ( RulePattern().IsMatch( trimmed ) )
			{
				FlushParagraph( paragraph, blocks );
				listIndents.Clear();
				blocks.Add( new( "rule", "", "" ) );
			}
			else if ( ListPattern().Match( trimmed ) is { Success: true } item )
			{
				FlushParagraph( paragraph, blocks );
				var depth = ComputeDepth( listIndents, ComputeIndent( line ) );
				var text = item.Groups[2].Value;
				var marker = char.IsDigit( item.Groups[1].Value[0] ) ? item.Groups[1].Value : Bullets[depth % Bullets.Length];
				if ( text.StartsWith( "[ ] ", StringComparison.Ordinal ) || text.StartsWith( "[x] ", StringComparison.OrdinalIgnoreCase ) )
				{
					marker = text[1] == ' ' ? "☐" : "☑";
					text = text[4..];
				}
				blocks.Add( new( "list", text, marker, depth ) );
			}
			else if ( trimmed.StartsWith( '>' ) )
			{
				FlushParagraph( paragraph, blocks );
				listIndents.Clear();
				var quote = new List<string> { ComputeQuoteLine( trimmed ) };
				while ( index + 1 < lines.Length && lines[index + 1].TrimStart().StartsWith( '>' ) )
					quote.Add( ComputeQuoteLine( lines[++index].TrimStart() ) );
				blocks.Add( new( "quote", string.Join( "\n", quote ), "" ) );
			}
			else if ( listIndents.Count > 0 && (!isAfterBlank || ComputeIndent( line ) > 0) )
			{
				// Lazy and indented continuation lines belong to the preceding list item.
				var last = blocks[^1];
				blocks[^1] = last with
				{
					Text = last.Text + (isAfterBlank ? "\n" : " ") + trimmed.TrimEnd(),
				};
			}
			else if ( IsDiagramLine( line ) )
			{
				// An unfenced diagram of box-drawing lines and arrows keeps its lines and spacing, as a fenced one would; joined into a paragraph it was unreadable.
				FlushParagraph( paragraph, blocks );
				listIndents.Clear();
				var diagram = new StringBuilder( line.TrimEnd() );
				while ( index + 1 < lines.Length && lines[index + 1].Trim().Length > 0 )
					diagram.Append( '\n' ).Append( lines[++index].TrimEnd() );
				blocks.Add( new( "code", diagram.ToString(), "" ) );
			}
			else
			{
				// Soft line breaks join into one wrapping paragraph; trailing double spaces or a backslash force a break.
				listIndents.Clear();
				if ( paragraph.Length > 0 )
					paragraph.Append( isHardBreak ? '\n' : ' ' );

				var content = trimmed.TrimEnd();
				isHardBreak = line.EndsWith( "  ", StringComparison.Ordinal ) || content.EndsWith( '\\' );
				paragraph.Append( content.EndsWith( '\\' ) ? content[..^1] : content );
			}

			isAfterBlank = false;
		}
		FlushParagraph( paragraph, blocks );
		return blocks;
	}

	/// <summary>Reports whether a line draws a diagram: it holds box-drawing characters or triangle arrowheads.</summary>
	/// <remarks>Plain arrows such as → are left out; prose uses them, and a sentence with one must still wrap.</remarks>
	private static bool IsDiagramLine( string line ) =>
		line.Any( character => character is >= '\u2500' and <= '\u257F' or '▲' or '▼' or '►' or '◄' or '▶' or '◀' );

	/// <summary>Computes a list item's nesting depth from its indentation and the enclosing items' indentation.</summary>
	private static int ComputeDepth( Stack<int> indents, int indent )
	{
		while ( indents.Count > 0 && indent < indents.Peek() )
			indents.Pop();

		if ( indents.Count == 0 || indent > indents.Peek() )
			indents.Push( indent );

		return indents.Count - 1;
	}

	/// <summary>Computes a line's leading whitespace width in source columns.</summary>
	private static int ComputeIndent( string line )
	{
		var width = 0;
		foreach ( var character in line )
		{
			if ( character == ' ' )
				width++;
			else if ( character == '\t' )
				width += TabWidth;
			else
				break;
		}
		return width;
	}

	/// <summary>Removes one quote marker and its optional following space.</summary>
	private static string ComputeQuoteLine( string trimmed ) => trimmed.Length > 1 && trimmed[1] == ' ' ? trimmed[2..] : trimmed[1..];

	/// <summary>Completes the current paragraph when another block begins.</summary>
	private static void FlushParagraph( StringBuilder paragraph, List<MarkdownBlock> blocks )
	{
		if ( paragraph.Length == 0 )
			return;
		blocks.Add( new( "paragraph", paragraph.ToString(), "" ) );
		paragraph.Clear();
	}

	/// <summary>Matches block headings with one through six level markers and optional closing markers.</summary>
	[GeneratedRegex( @"^(#{1,6})\s+(.+?)(?:\s+#+)?\s*$" )]
	private static partial Regex HeadingPattern();

	/// <summary>Matches unordered and ordered list item prefixes.</summary>
	[GeneratedRegex( @"^([-+*]|\d+[.)])\s+(.+)$" )]
	private static partial Regex ListPattern();

	/// <summary>Matches thematic breaks of three or more dashes, asterisks, or underscores.</summary>
	[GeneratedRegex( @"^([-*_])(\s*\1){2,}\s*$" )]
	private static partial Regex RulePattern();

	/// <summary>Matches Markdown table separator rows.</summary>
	[GeneratedRegex( @"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$" )]
	private static partial Regex TableRulePattern();

	/// <summary>Matches inline emphasis, deletion, italics, and link labels; code spans are masked before it runs.</summary>
	[GeneratedRegex( @"\*\*(.+?)\*\*|__(.+?)__|~~(.+?)~~|\*([^*]+)\*|\[([^\]]+)\]\(([^)]+)\)" )]
	private static partial Regex InlinePattern();

	/// <summary>Matches a bare web address in prose; trailing sentence punctuation is trimmed after the match.</summary>
	[GeneratedRegex( @"https?://[^\s<>()\[\]{}""'`]+" )]
	private static partial Regex BareUrlPattern();
}

/// <summary>Stores one parsed Markdown block, its semantic prefix or language, and its list nesting depth.</summary>
public readonly record struct MarkdownBlock( string Kind, string Text, string Marker, int Depth = 0 );
