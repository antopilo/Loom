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
	public const double BodyLineHeight = 1.65;

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
	private const int BlockGap = 22;

	/// <summary>Defines the vertical space before and after a framed block, such as code or a table, in logical pixels.</summary>
	private const int FramedGap = 24;

	/// <summary>Defines the space above first- and second-level headings in logical pixels.</summary>
	private const int MajorHeadingGap = 28;

	/// <summary>Defines the space above deeper headings in logical pixels.</summary>
	private const int MinorHeadingGap = 24;

	/// <summary>Defines the space between a heading and the block it introduces in logical pixels.</summary>
	private const int HeadingBodyGap = 16;

	/// <summary>Defines heading line height as a multiple of its font size.</summary>
	private const double HeadingLineHeight = 1.3;

	/// <summary>Defines the vertical space between list items in logical pixels.</summary>
	private const int ListItemGap = 6;

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

	/// <summary>Builds headings, paragraphs, lists, checklists, quotes, tables, math, images, footnotes and fenced code with block-specific spacing.</summary>
	/// <param name="markdown">The Markdown source.</param>
	/// <param name="openLink">Opens a link the reader follows: a web address, or a <c>lokn://editor/open</c> link to a file.</param>
	/// <param name="fade">The opacity of a visible character by how many visible characters follow it, so freshly streamed words fade in; it must not fall as the count grows. Null renders everything at rest.</param>
	public static Blob Build( string markdown, Action<Uri> openLink, Func<int, double>? fade = null )
	{
		var blocks = Parse( markdown );
		// Reference links and footnote marks anywhere in the document, quotes included, read the definitions of the whole document.
		var (references, footnotes) = (_references, _footnotes);
		_references = blocks.Where( block => block.Kind == "reference" ).GroupBy( block => block.Marker.Trim().ToLowerInvariant() )
			.ToDictionary( group => group.Key, group => group.First().Text );
		_footnotes = blocks.Where( block => block.Kind == "footnote" ).Select( block => block.Marker.Trim().ToLowerInvariant() ).Distinct()
			.Select( ( label, index ) => (label, index) ).ToDictionary( pair => pair.label, pair => pair.index + 1 );
		try
		{
			return BuildBlocks( blocks, openLink, fade, 0 );
		}
		finally
		{
			(_references, _footnotes) = (references, footnotes);
		}
	}

	/// <summary>Builds the blocks of <paramref name="markdown"/>, fading its tail when <paramref name="after"/> more visible characters follow it.</summary>
	private static Container BuildBlocks( string markdown, Action<Uri> openLink, Func<int, double>? fade, int after ) => BuildBlocks( Parse( markdown ), openLink, fade, after );

	/// <summary>Builds parsed blocks, fading their tail when <paramref name="after"/> more visible characters follow them; footnotes gather at the end
	/// under a rule, and reference definitions show nothing.</summary>
	private static Container BuildBlocks( IReadOnlyList<MarkdownBlock> blocks, Action<Uri> openLink, Func<int, double>? fade, int after )
	{
		var following = ComputeFollowing( blocks, fade, after );
		var children = new List<Blob>();
		var notes = new List<MarkdownBlock>();
		MarkdownBlock? previous = null;
		for ( var index = 0; index < blocks.Count; index++ )
		{
			var block = blocks[index];
			if ( block.Kind == "reference" )
				continue;
			if ( block.Kind == "footnote" )
			{
				notes.Add( block );
				continue;
			}

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
				child = BuildBlock( block, openLink, index, fade, following?[index] ?? 0 );

			// A code block, quote or formula inside a list item lines up with the item's text.
			child.Margin = new Edges( ComputeSpacing( previous, block ), 0, 0, block.Kind == "list" ? 0 : block.Depth * (MarkerWidth + MarkerGap) );
			children.Add( child );
			previous = block;
		}
		if ( notes.Count > 0 )
			children.Add( BuildFootnotes( notes, openLink, children.Count > 0 ) );

		return new Container
		{
			MinWidth = 0,
			Children = children,
		};
	}

	/// <summary>Builds the footnotes under a short rule, each led by its number, in smaller muted text.</summary>
	private static Container BuildFootnotes( IReadOnlyList<MarkdownBlock> notes, Action<Uri> openLink, bool isAfterContent )
	{
		var rows = new List<Blob>
		{
			new Container { Key = "footnotes-rule", Width = 48, Height = 1, BackgroundColor = Theme.Current.Border },
		};
		foreach ( var note in notes )
		{
			var text = BuildInlineText( note.Text, openLink, grow: true );
			text.FontSize = BodySize - 2;
			text.Color = Theme.Current.Muted;
			rows.Add( new Row
			{
				Key = "note:" + note.Marker,
				Gap = MarkerGap,
				MinWidth = 0,
				AlignItems = AlignItems.FlexStart,
				Children =
				[
					new Text { Content = FormatFootnote( note.Marker ), FontSize = BodySize - 2, Color = Theme.Current.Accent, FlexShrink = 0 },
					CodeBoxes.Wrap( text, "note", grow: true ),
				],
			} );
		}
		return new Container
		{
			Key = "footnotes",
			Gap = ListItemGap,
			MinWidth = 0,
			Margin = new Edges( isAfterContent ? BlockGap * 2 : 0, 0, 0, 0 ),
			Children = rows,
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

	/// <summary>Computes how many characters a block shows: its inline text without markup, or its source for code, tables, quotes and math.</summary>
	private static int ComputeVisibleLength( MarkdownBlock block ) => block.Kind switch
	{
		"paragraph" or "heading" or "list" => ParseInlineRuns( block.Text ).Sum( run => run.Text.Length ),
		"reference" or "footnote" => 0,
		_ => block.Text.Length,
	};

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

		// A framed block, such as code, a table or a quote, sets itself apart from the prose around it.
		if ( IsFramed( previous.Value ) || IsFramed( current ) )
			return FramedGap;

		return BlockGap;
	}

	/// <summary>Reports whether a block draws its own frame or rules: code, a table, a quote or a formula.</summary>
	private static bool IsFramed( MarkdownBlock block ) => block.Kind is "code" or "table" or "quote" or "math";

	/// <summary>Builds one non-list block, fading its tail when <paramref name="after"/> visible characters follow it.</summary>
	private static Blob BuildBlock( MarkdownBlock block, Action<Uri> openLink, int index, Func<int, double>? fade = null, int after = 0 )
	{
		if ( block.Kind == "code" && block.Marker.Equals( "mermaid", StringComparison.OrdinalIgnoreCase ) )
			return BuildDiagram( block.Text );

		if ( block.Kind == "code" && SvgView.IsSvg( block.Marker, block.Text ) )
			return BuildDrawing( block.Text, block.Marker );

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
				BackgroundColor = Theme.Current.Raised,
				BorderTopRightRadius = Theme.Current.Radius,
				BorderBottomRightRadius = Theme.Current.Radius,
				MinWidth = 0,
				Children = [BuildBlocks( block.Text, openLink, fade, after )],
			};
		}

		if ( block.Kind == "math" )
			return BuildMath( block.Text );

		if ( block.Kind == "paragraph" && BuildParagraphWithImages( block.Text, openLink ) is { } withImages )
			return withImages;

		var text = BuildInlineText( block.Text, openLink, fade: fade, after: after );
		if ( block.Kind == "heading" )
		{
			var level = block.Marker.Length;
			text.FontWeight = EmphasisWeight;
			// Headings stand out in the theme's yellow, as Tern draws them.
			text.Color = Theme.Current.AnsiYellow;
			text.FontSize = level <= HeadingSizes.Length ? HeadingSizes[level - 1] : BodySize;
			text.LineHeight = HeadingLineHeight;
		}

		return CodeBoxes.Wrap( text, "text:" + index );
	}

	/// <summary>Builds an SVG block as its drawing, with its kind and a Copy button above it; SVG Goo cannot read shows its source with a note.</summary>
	private static Blob BuildDrawing( string source, string language )
	{
		var header = new List<Blob> { CardStyle.Name( "svg" ), new Spacer() };
		if ( CopyText is { } copy )
			header.Add( BuildCodeButton( "Copy", "Copy drawing source", () => copy( source ) ) );
		var row = CardStyle.Header( header );
		if ( SvgView.Build( source, row, BlockInset * 2 ) is { } drawing )
			return drawing;

		return new Container
		{
			MinWidth = 0,
			Gap = CodeGap * 2,
			Children =
			[
				new Text { Content = "This drawing could not be read; its source:", FontSize = BodySize - 2, Color = Theme.Current.Muted },
				BuildCode( source, language.Length > 0 ? language : "xml" ),
			],
		};
	}

	/// <summary>Builds a Mermaid diagram in a framed card with its type and a Copy button: drawn natively for flowcharts and sequence diagrams;
	/// other types, and sources it cannot read, show their source with a note.</summary>
	private static Blob BuildDiagram( string source )
	{
		if ( MermaidView.Build( source ) is { } diagram )
		{
			var kind = source.TrimStart().Split( [' ', '\n', '\r'], 2 )[0];
			var header = new List<Blob> { CardStyle.Name( $"mermaid · {kind}" ), new Spacer() };
			if ( CopyText is { } copy )
				header.Add( BuildCodeButton( "Copy", "Copy diagram source", () => copy( source ) ) );
			return CardStyle.Frame( new Container
			{
				AccessibilityRole = AccessibilityRole.Group,
				AccessibilityName = "Diagram",
				Children =
				[
					CardStyle.Header( header ),
					new Container { Padding = BlockInset, MinWidth = 0, Children = [diagram] },
				],
			} );
		}
		return new Container
		{
			MinWidth = 0,
			Gap = CodeGap * 2,
			Children =
			[
				new Text { Content = "This Mermaid diagram type is not drawn yet; its source:", FontSize = BodySize - 2, Color = Theme.Current.Muted },
				BuildCode( source, "mermaid" ),
			],
		};
	}

	/// <summary>Builds display math, centered and a little larger than prose, as LaTeX converted to Unicode with its colors.</summary>
	private static Text BuildMath( string latex ) => new( [.. LatexText.Convert( latex ).Select( segment => new TextSpan( segment.Text, BuildInlineStyle( new( segment.Text, InlineKind.Math, null, segment.Color ), 1 )! ) )] )
	{
		Selectable = true,
		FontFamily = ViewStyle.FontFamily,
		FontSize = BodySize + 2,
		LineHeight = BodyLineHeight,
		Color = Theme.Current.Text,
		TextAlign = TextAlign.Center,
		MinWidth = 0,
	};

	/// <summary>Defines the largest side, in pixels, an image in Markdown is decoded at.</summary>
	private const int ImagePixels = 1600;

	/// <summary>Defines the most bytes a web image in Markdown may have, 20 MiB.</summary>
	private const int MaxWebImageBytes = 20 * 1024 * 1024;

	/// <summary>Defines how many image sources stay cached before the cache starts over.</summary>
	private const int MaxCachedImages = 64;

	/// <summary>Holds each image's shared source by full path or web address, so rebuilt messages reuse the decoded image; on the UI thread.</summary>
	private static readonly Dictionary<string, AsyncImageSource> _images = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Downloads web images; shared, as one client per request would exhaust sockets.</summary>
	private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds( 30 ) };

	/// <summary>Builds a paragraph that holds images, <c>![alt](source)</c> or <c>&lt;img src alt&gt;</c>, as its text and its images in order, each image
	/// on its own line; a source is an existing local file, by full path or <c>file:</c> URI, or an http or https address. Null for a paragraph
	/// without an image that shows, which builds as text, where an image shows its alt text as a link.</summary>
	private static Container? BuildParagraphWithImages( string text, Action<Uri> openLink )
	{
		if ( !text.Contains( "![", StringComparison.Ordinal ) && !text.Contains( "<img", StringComparison.OrdinalIgnoreCase ) )
			return null;

		var children = new List<Blob>();
		var position = 0;
		foreach ( Match match in ImagePattern().Matches( text ) )
		{
			var destination = match.Groups["path"].Success ? match.Groups["path"].Value : match.Groups["src"].Value;
			var alt = match.Groups["alt"].Success ? match.Groups["alt"].Value : match.Groups["imgalt"].Value;
			if ( FindImageSource( System.Net.WebUtility.HtmlDecode( destination ) ) is not var (key, source, name) )
				continue;

			if ( text[position..match.Index].Trim() is { Length: > 0 } before )
				children.Add( CodeBoxes.Wrap( BuildInlineText( before, openLink ), "before:" + match.Index ) );
			// A local image names its own size from its header; a web image loads later, so its card shows no scale.
			var size = Path.IsPathFullyQualified( key ) ? MediaCard.ReadSize( key ) : null;
			var image = new Image
			{
				Source = source,
				Fit = ImageFit.Contain,
				MaxWidth = Length.Percent( 100 ),
				Height = size is { Height: > 0 } known ? Math.Min( MediaCard.MaxImageHeight, known.Height ) : MediaCard.MaxImageHeight,
				BorderRadius = Theme.Current.Radius / 2,
				AccessibilityName = alt.Length > 0 ? alt : name,
				Fallback = new Text { Content = $"Image could not load: {name}", Color = Theme.Current.Muted, FontSize = Theme.Current.FontSize - 1 },
			};
			if ( size is { Width: > 0, Height: > 0 } shape )
				image.AspectRatio = (double)shape.Width / shape.Height;
			var target = Path.IsPathFullyQualified( key ) ? ResolveLink( key ) : ParseWebLink( key );
			children.Add( MediaCard.Build( $"image:{key}", name, image, size, () =>
			{
				if ( target is not null )
					openLink( target );
			} ) );
			position = match.Index + match.Length;
		}
		if ( children.Count == 0 )
			return null;
		if ( text[position..].Trim() is { Length: > 0 } after )
			children.Add( CodeBoxes.Wrap( BuildInlineText( after, openLink ), "after" ) );

		return new Container
		{
			MinWidth = 0,
			Gap = Theme.Current.Spacing,
			Children = children,
		};
	}

	/// <summary>Finds the shared source of an image: a local file that exists, by full path or <c>file:</c> URI, or a web address; null for anything else.</summary>
	private static (string Key, AsyncImageSource Source, string Name)? FindImageSource( string destination )
	{
		var value = destination.Trim();
		if ( ParseWebLink( value ) is { } web )
		{
			var address = web.AbsoluteUri;
			return (address, GetImageSource( address, cancellation => LoadWebImageAsync( web, cancellation ) ), Path.GetFileName( web.AbsolutePath ) is { Length: > 0 } file ? file : web.Host);
		}

		var path = Uri.TryCreate( value, UriKind.Absolute, out var uri ) && uri.IsFile ? uri.LocalPath : value;
		if ( !Path.IsPathFullyQualified( path ) || !File.Exists( path ) )
			return null;
		return (path, GetImageSource( path, cancellation => Task.Run( () => ImageSource.LoadThumbnail( path, ImagePixels, ImagePixels, cancellation ), cancellation ) ), Path.GetFileName( path ));
	}

	/// <summary>Gets the cached source of an image by its key, creating it with a loader the first time.</summary>
	private static AsyncImageSource GetImageSource( string key, Func<CancellationToken, Task<ImageSource>> load )
	{
		if ( _images.TryGetValue( key, out var source ) )
			return source;
		if ( _images.Count >= MaxCachedImages )
			_images.Clear();
		return _images[key] = new AsyncImageSource( load );
	}

	/// <summary>Downloads a web image of at most <see cref="MaxWebImageBytes"/> and decodes it.</summary>
	/// <exception cref="InvalidDataException">The image is larger than the limit.</exception>
	private static async Task<ImageSource> LoadWebImageAsync( Uri address, CancellationToken cancellation )
	{
		using var response = await _http.GetAsync( address, HttpCompletionOption.ResponseHeadersRead, cancellation ).ConfigureAwait( false );
		response.EnsureSuccessStatusCode();
		if ( response.Content.Headers.ContentLength > MaxWebImageBytes )
			throw new InvalidDataException( "The image is too large." );
		await using var stream = await response.Content.ReadAsStreamAsync( cancellation ).ConfigureAwait( false );
		using var buffer = new MemoryStream();
		var chunk = new byte[81920];
		int read;
		while ( (read = await stream.ReadAsync( chunk, cancellation ).ConfigureAwait( false )) > 0 )
		{
			if ( buffer.Length + read > MaxWebImageBytes )
				throw new InvalidDataException( "The image is too large." );
			buffer.Write( chunk, 0, read );
		}
		return ImageSource.Decode( buffer.GetBuffer().AsMemory( 0, (int)buffer.Length ), cancellation );
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
				// Keyed per item, so each item's mounted cells, such as its code boxes, stay its own.
				Key = "item:" + index,
				Gap = MarkerGap,
				MinWidth = 0,
				MarginStart = item.Depth * (markerWidth + MarkerGap),
				AlignItems = AlignItems.FlexStart,
				Children =
				[
					BuildListMarker( item.Marker, markerWidth ),
					CodeBoxes.Wrap( BuildInlineText( item.Text, openLink, grow: true, fade: fade, after: following?[first + index] ?? 0 ), "item-text", grow: true ),
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
					BorderColor = Theme.Current.Accent,
					BackgroundColor = bullet == 1 ? Color.Transparent : Theme.Current.Accent,
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

	/// <summary>Defines the most lines a code block shows without scrolling or clipping; at the code line height they stay under <see cref="CodeHeight"/>.</summary>
	private const int ShortCodeLines = 10;

	/// <summary>Defines the most characters a short code block holds, so its wrapped lines also stay under <see cref="CodeHeight"/>.</summary>
	private const int ShortCodeCharacters = 900;

	/// <summary>Builds a selectable code or diff block with a language badge, syntax colors and internal scrolling.</summary>
	/// <param name="code">The code to show.</param>
	/// <param name="language">The fenced-code or tool language; it picks the colors and labels the block.</param>
	/// <param name="framed">Whether the block draws its own raised background and inset.</param>
	/// <param name="showLanguage">Whether the language badge is shown; a tool head that already names it leaves it out.</param>
	/// <param name="fadeLines">How many trailing lines fade out, marking a cut preview.</param>
	/// <param name="fadeHeadLines">How many leading lines fade in, marking a preview cut at its start, such as a command's last output lines.</param>
	/// <param name="isRunnable">Whether a shell snippet gets a Run button; written code blocks do, tool output does not.</param>
	/// <param name="lineNumbers">Whether a muted line number column runs beside the code, as for a written file.</param>
	public static Blob BuildCode( string code, string language = "", bool framed = true, bool showLanguage = true, int fadeLines = 0, bool isRunnable = false, bool lineNumbers = false, int fadeHeadLines = 0 )
	{
		// Tabs show as four spaces. A tab has no glyph, and Goo looks for one through every fallback face each time it shapes the line: one tab-indented
		// tool output took 180 ms to lay out, a visible stall while scrolling. Copy still copies the code as written.
		var lines = code.Replace( "\r\n", "\n" ).Replace( "\t", "    " ).Split( '\n' );
		var children = new List<Blob>();
		// The language, a Run button for shell snippets and a Copy button, which copies every line, also those not shown, float over the block's
		// corner while the pointer is over it, so they take no row of their own.
		var header = new List<Blob>();
		if ( showLanguage && language.Length > 0 && language != "text" )
			header.Add( BuildBadge( language ) );
		if ( isRunnable && RunCode is { } run && CanRunCode?.Invoke( language ) == true && code.Trim().Length > 0 )
			header.Add( BuildCodeIcon( "\uE768", "Run in terminal", () => run( code, language ) ) );
		if ( CopyText is { } copy && code.Length > 0 )
			header.Add( BuildCodeIcon( "\uE8C8", "Copy code", () => copy( code ) ) );
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
			if ( index < fadeHeadLines )
				alpha = Math.Min( alpha, (double)(index + 1) / (fadeHeadLines + 1) );
			var lineSpans = grammar is null || line.Length == 0 ? new[] { new TextSpan( ViewStyle.CutCodeLine( line ), null! ) } : BuildHighlightedLine( line, grammar, ref isInComment );
			foreach ( var span in lineSpans )
				spans.Add( span.Style is null || alpha < 1.0 ? new TextSpan( span.Text, new Style { Color = color.WithAlpha( alpha ) } ) : span );
			if ( index < shown - 1 )
				spans.Add( new TextSpan( "\n", null! ) );
		}
		var text = new Text( [.. spans] ) { Selectable = true, FontFamily = ViewStyle.CodeFontFamily, FontSize = CodeSize, LineHeight = CodeLineHeight, Color = Theme.Current.Text, FlexShrink = 1, MinWidth = 0 };
		children.Add( !lineNumbers ? text : new Row
		{
			Gap = Theme.Current.Spacing * 1.5,
			MinWidth = 0,
			Children =
			[
				new Text
				{
					Content = string.Join( '\n', Enumerable.Range( 1, shown ) ),
					FontFamily = ViewStyle.CodeFontFamily,
					FontSize = CodeSize,
					LineHeight = CodeLineHeight,
					TextAlign = TextAlign.Right,
					Color = Theme.Current.Muted,
					FlexShrink = 0,
				},
				text,
			],
		} );
		if ( lines.Length > MaxCodeLines )
			children.Add( new Text { Content = $"… {lines.Length - MaxCodeLines:N0} more lines not shown", Color = Theme.Current.Muted, FontFamily = ViewStyle.FontFamily, FontSize = CodeSize, Selectable = true, MinWidth = 0 } );

		// Goo draws a clipping viewport with rounded corners into a layer of its own, and every layer adds to every later frame's work, so a block
		// short enough never to reach the scroll height neither scrolls nor clips: tool previews and most snippets. Lines may wrap, so the test
		// counts characters as well as lines.
		var isShort = shown <= ShortCodeLines && lines.Take( shown ).Sum( line => line.Length + 1 ) <= ShortCodeCharacters;
		var block = new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = language == "diff" ? "Diff" : "Code block",
			Padding = framed ? BlockInset : 0,
			Gap = CodeGap,
			OverflowY = isShort ? Overflow.Visible : Overflow.Scroll,
			ScrollbarY = ViewStyle.GetScrollbar(),
			BackgroundColor = framed ? Theme.Current.Raised : Color.Transparent,
			BorderRadius = framed ? Theme.Current.Radius : 0,
			Children = children,
		};
		if ( !isShort )
			block.MaxHeight = CodeHeight;
		// A container holds the cell, since a mounted cell drops the margin blocks are spaced with.
		return header.Count == 0 ? block
			: new Container { MinWidth = 0, Children = [Cell.Mount<HoverActionsInput, HoverActions>( "code-actions", new( block, [.. header] ) )] };
	}

	/// <summary>Builds one icon button of a code block's hover actions.</summary>
	private static Button BuildCodeIcon( string glyph, string name, Action action )
	{
		var button = Theme.Current.BuildButton( glyph, action );
		button.AccessibilityName = name;
		button.FontFamily = ViewStyle.IconFontFamily;
		button.FontSize = CodeSize;
		button.Color = Theme.Current.Muted;
		button.Width = 24;
		button.Height = 22;
		button.Padding = 0;
		button.FlexShrink = 0;
		return button;
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
		// A fixed height with the text centered, so a line height inherited from code above it cannot push the text off center.
		Height = 18,
		JustifyContent = JustifyContent.Center,
		Padding = new Edges( 0, 6 ),
		BorderRadius = Theme.Current.Radius,
		BackgroundColor = Theme.Current.Hover,
		Children = [new Text { Content = text, FontFamily = ViewStyle.CodeFontFamily, FontSize = ViewStyle.SmallSize - 1, LineHeight = 1, Color = Theme.Current.Muted, TextWrap = TextWrap.NoWrap }],
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
				if ( isHeader )
				{
					text.Color = Theme.Current.Muted;
					text.FontSize = CodeSize;
				}
				text.LineHeight = CellLineHeight;
				text.TextAlign = column < alignments.Length ? alignments[column] : TextAlign.Left;
				text.Padding = new Edges( CellInsetY, CellInsetX );
				var (basis, least) = (ComputeColumnWidth( preferred[column] ), ComputeMinimumWidth( minimum[column] ));
				cells.Add( CodeBoxes.Wrap( text, "cell:" + column, layout: cell =>
				{
					cell.FlexBasis = basis;
					cell.FlexShrink = 1;
					cell.MinWidth = least;
				} ) );
			}

			tableRows.Add( new Row
			{
				Key = "row:" + index,
				// Open rows with a thin line under each, as Tern draws them; the header sits on the same canvas, muted.
				BorderWidth = new Edges( 0, 0, index < rows.Length - 1 ? Theme.Current.BorderWidth : 0, 0 ),
				BorderColor = Theme.Current.Divider,
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
					MinWidth = Length.Em( minimum.Sum( characters => Math.Max( characters, 1 ) ) * CharacterEms * BoldWidthFactor ) + 2 * (CellInsetX * minimum.Length + Theme.Current.BorderWidth),
					BorderWidth = 0,
					Overflow = Overflow.Hidden,
					Children = tableRows,
				},
			],
		};
	}

	/// <summary>Computes a table column width from its estimated character count, including cell insets.</summary>
	private static Length ComputeColumnWidth( int characters ) => Length.Em( Math.Max( characters, 1 ) * CharacterEms ) + 2 * CellInsetX;

	/// <summary>Defines how much wider bold text, as a header or a bold first column, runs than the character estimate, so its longest word never breaks.</summary>
	private const double BoldWidthFactor = 1.2;

	/// <summary>Computes a table column's narrowest width from its longest word, with room for bold, so a word such as "Header" never breaks inside.</summary>
	private static Length ComputeMinimumWidth( int characters ) => Length.Em( Math.Max( characters, 1 ) * CharacterEms * BoldWidthFactor ) + 2 * CellInsetX;

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

	/// <summary>Parses block structure, list nesting, and soft line breaks while keeping code bodies and incomplete streaming fences intact.</summary>
	/// <remarks>
	/// Besides the usual blocks it reads underlined (setext) headings, code indented by four spaces, fences of any length, display math in
	/// <c>$$…$$</c> or <c>\[…\]</c>, link reference and footnote definitions, and lines that are only HTML block tags such as <c>&lt;details&gt;</c>.
	/// A fence or quote indented under a list item stays inside the item, at its depth.
	/// </remarks>
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
			var indent = ComputeIndent( line );
			// A block indented under a list item belongs to it, one level deeper.
			var isInItem = listIndents.Count > 0 && indent > 0;
			var itemDepth = isInItem ? listIndents.Count : 0;
			if ( string.IsNullOrWhiteSpace( line ) )
			{
				FlushParagraph( paragraph, blocks );
				isAfterBlank = true;
				continue;
			}

			if ( FencePattern().Match( line ) is { Success: true } fence )
			{
				FlushParagraph( paragraph, blocks );
				if ( !isInItem )
					listIndents.Clear();
				// A fence closes at a run of the same character at least as long, so a longer fence can hold a shorter one.
				var mark = fence.Groups["mark"].Value;
				var language = fence.Groups["info"].Value.Trim();
				var code = new List<string>();
				while ( ++index < lines.Length && !IsFenceClose( lines[index], mark ) )
					code.Add( RemoveIndent( lines[index], indent ) );
				blocks.Add( new( "code", string.Join( "\n", code ), language, itemDepth ) );
			}
			else if ( listIndents.Count == 0 && paragraph.Length == 0 && indent >= TabWidth )
			{
				// Code indented by four columns keeps its lines; blank lines inside it stay, trailing ones do not.
				var code = new List<string> { RemoveIndent( line, TabWidth ) };
				while ( index + 1 < lines.Length && (string.IsNullOrWhiteSpace( lines[index + 1] ) || ComputeIndent( lines[index + 1] ) >= TabWidth) )
					code.Add( RemoveIndent( lines[++index], TabWidth ) );
				while ( code.Count > 0 && string.IsNullOrWhiteSpace( code[^1] ) )
					code.RemoveAt( code.Count - 1 );
				blocks.Add( new( "code", string.Join( "\n", code ), "" ) );
			}
			else if ( trimmed.StartsWith( "$$", StringComparison.Ordinal ) || trimmed.StartsWith( "\\[", StringComparison.Ordinal ) )
			{
				FlushParagraph( paragraph, blocks );
				if ( !isInItem )
					listIndents.Clear();
				var close = trimmed[0] == '$' ? "$$" : "\\]";
				var math = new StringBuilder( trimmed[2..] );
				var end = math.ToString().IndexOf( close, StringComparison.Ordinal );
				while ( end < 0 && index + 1 < lines.Length )
				{
					math.Append( '\n' ).Append( lines[++index].Trim() );
					end = math.ToString().IndexOf( close, StringComparison.Ordinal );
				}
				var formula = end < 0 ? math.ToString() : math.ToString()[..end];
				blocks.Add( new( "math", formula.Trim(), "", itemDepth ) );
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
			else if ( paragraph.Length > 0 && SetextPattern().Match( line ) is { Success: true } underline )
			{
				// A line of = or - under a paragraph makes it a heading of level one or two.
				var title = paragraph.ToString();
				paragraph.Clear();
				listIndents.Clear();
				blocks.Add( new( "heading", title, underline.Groups[1].Value[0] == '=' ? "#" : "##" ) );
			}
			else if ( RulePattern().IsMatch( trimmed ) )
			{
				FlushParagraph( paragraph, blocks );
				listIndents.Clear();
				blocks.Add( new( "rule", "", "" ) );
			}
			else if ( FootnotePattern().Match( trimmed ) is { Success: true } footnote && indent < TabWidth )
			{
				FlushParagraph( paragraph, blocks );
				listIndents.Clear();
				// Indented lines after a footnote continue it.
				var note = new StringBuilder( footnote.Groups[2].Value.Trim() );
				while ( index + 1 < lines.Length && lines[index + 1].Trim().Length > 0 && ComputeIndent( lines[index + 1] ) > 0 )
					note.Append( ' ' ).Append( lines[++index].Trim() );
				blocks.Add( new( "footnote", note.ToString(), footnote.Groups[1].Value ) );
			}
			else if ( paragraph.Length == 0 && indent < TabWidth && ReferencePattern().Match( trimmed ) is { Success: true } reference )
			{
				listIndents.Clear();
				blocks.Add( new( "reference", reference.Groups[2].Value, reference.Groups[1].Value ) );
			}
			else if ( ListPattern().Match( trimmed ) is { Success: true } item )
			{
				FlushParagraph( paragraph, blocks );
				var depth = ComputeDepth( listIndents, indent );
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
				if ( !isInItem )
					listIndents.Clear();
				var quote = new List<string> { ComputeQuoteLine( trimmed ) };
				while ( index + 1 < lines.Length && lines[index + 1].TrimStart().StartsWith( '>' ) )
					quote.Add( ComputeQuoteLine( lines[++index].TrimStart() ) );
				blocks.Add( new( "quote", string.Join( "\n", quote ), "", itemDepth ) );
			}
			else if ( HtmlBlockPattern().IsMatch( trimmed ) )
			{
				// A line of only block tags, such as <details> or </div>, holds no text; a summary shows as a bold line and <hr> as a rule.
				FlushParagraph( paragraph, blocks );
				listIndents.Clear();
				if ( SummaryPattern().Match( trimmed ) is { Success: true } summary )
					blocks.Add( new( "paragraph", $"**{summary.Groups[1].Value.Trim()}**", "" ) );
				else if ( HrPattern().IsMatch( trimmed ) )
					blocks.Add( new( "rule", "", "" ) );
			}
			else if ( listIndents.Count > 0 && (!isAfterBlank || indent > 0) )
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

	/// <summary>Reports whether a line closes a fence: only a run of its character at least as long as its opening, and spaces.</summary>
	private static bool IsFenceClose( string line, string mark )
	{
		var trimmed = line.Trim();
		return trimmed.Length >= mark.Length && trimmed.All( character => character == mark[0] );
	}

	/// <summary>Removes up to a number of columns of leading whitespace from a line.</summary>
	private static string RemoveIndent( string line, int columns )
	{
		var width = 0;
		var index = 0;
		while ( index < line.Length && width < columns && line[index] is ' ' or '\t' )
		{
			width += line[index] == '\t' ? TabWidth : 1;
			index++;
		}
		return line[index..];
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

	/// <summary>Matches a fence's opening: three or more backticks or tildes, then its info string such as the language.</summary>
	[GeneratedRegex( @"^\s*(?<mark>`{3,}|~{3,})(?<info>[^`]*)$" )]
	private static partial Regex FencePattern();

	/// <summary>Matches the underline of a setext heading: a run of = or - alone on its line.</summary>
	[GeneratedRegex( @"^ {0,3}(=+|-+)\s*$" )]
	private static partial Regex SetextPattern();

	/// <summary>Matches a footnote definition: its label, then its text.</summary>
	[GeneratedRegex( @"^\[\^([^\]\s]+)\]:\s*(.*)$" )]
	private static partial Regex FootnotePattern();

	/// <summary>Matches a link reference definition: its label, then its destination and an optional title.</summary>
	[GeneratedRegex( @"^\[([^\]^][^\]]*)\]:\s*(<[^>]+>|\S+)(?:\s+(?:""[^""]*""|'[^']*'|\([^)]*\)))?\s*$" )]
	private static partial Regex ReferencePattern();

	/// <summary>Matches a line of only HTML block tags, summaries and comments, such as <c>&lt;details&gt;&lt;summary&gt;More&lt;/summary&gt;</c>.</summary>
	[GeneratedRegex( @"^(?:(?:<summary[^>]*>.*?</summary\s*>|<!--.*?-->|</?(?:details|summary|div|p|section|article|header|footer|nav|aside|figure|figcaption|center|table|thead|tbody|tfoot|tr|td|th|ul|ol|li|dl|dt|dd|br|hr|picture|source|span)(?:\s[^<>]*)?/?>)\s*)+$", RegexOptions.IgnoreCase )]
	private static partial Regex HtmlBlockPattern();

	/// <summary>Matches a summary element's text.</summary>
	[GeneratedRegex( @"<summary[^>]*>(.*?)</summary\s*>", RegexOptions.IgnoreCase )]
	private static partial Regex SummaryPattern();

	/// <summary>Matches a horizontal rule tag.</summary>
	[GeneratedRegex( @"<hr\b", RegexOptions.IgnoreCase )]
	private static partial Regex HrPattern();
}

/// <summary>Stores one parsed Markdown block, its semantic prefix or language, and its list nesting depth.</summary>
public readonly record struct MarkdownBlock( string Kind, string Text, string Marker, int Depth = 0 );
