using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Markdown;

/// <summary>Parses and builds inline Markdown: emphasis, code, links, math, footnote marks, inline HTML, escapes and entities.</summary>
public static partial class MarkdownView
{
	/// <summary>Defines how far, in logical pixels, the pointer may move between press and release and still count as a click on a link.</summary>
	private const double ClickSlop = 4;

	/// <summary>Defines the space added after the character before inline code and after the code's last character, in logical pixels:
	/// the room its box's padding fills, so the box keeps a margin from the words around it.</summary>
	internal const double CodePad = 4;

	/// <summary>Stores whether the link being opened on this thread was Ctrl-clicked.</summary>
	[ThreadStatic]
	private static bool _isOpeningExternally;

	/// <summary>Gets whether the link being opened was Ctrl-clicked, asking for the system's default app instead of Loom; true only while the
	/// <c>openLink</c> handler a view was built with runs.</summary>
	public static bool IsOpeningExternally
	{
		get => _isOpeningExternally;
		private set => _isOpeningExternally = value;
	}

	/// <summary>Defines the room set after the character before a link to a file or a web page, in logical pixels, which its icon fills.</summary>
	internal const double MentionGap = 18;

	/// <summary>Defines the first private-use character that masks a code span, math, an escaped character or a decoded entity while emphasis is matched.</summary>
	private const char Mask = '\uF000';

	/// <summary>Defines how many pieces one text can mask; further ones stay literal.</summary>
	private const int MaskCount = 0x800;

	/// <summary>Stores where the primary pointer pressed link text, in window coordinates, until it is released; shared by every link text, since one pointer presses at a time.</summary>
	private static Point? _linkPress;

	/// <summary>Holds the link reference definitions of the document being built, such as <c>[1]: https://…</c>, by lowercase label; on the UI thread.</summary>
	[ThreadStatic]
	private static Dictionary<string, string>? _references;

	/// <summary>Holds the footnote numbers of the document being built, by lowercase label, in order of definition; on the UI thread.</summary>
	[ThreadStatic]
	private static Dictionary<string, int>? _footnotes;

	/// <summary>Stores one run of visible inline text, its emphasis, the link it opens, and the color math gave it.</summary>
	/// <remarks>A math box's background is dropped: Goo draws no background behind part of a text, and fails on one.</remarks>
	private readonly record struct InlineRun( string Text, InlineKind Kind, Uri? Link, Color? Color = null );

	/// <summary>Stores one masked piece: its kind and its text.</summary>
	/// <param name="Kind">What the piece is: code, math, or plain text such as an escaped character.</param>
	/// <param name="Text">The piece's text.</param>
	/// <param name="IsText">Whether a LaTeX piece is text, such as a color command in prose, whose spaces show.</param>
	private readonly record struct MaskedPiece( InlineKind Kind, string Text, bool IsText = false );

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

		/// <summary>Math, or text a LaTeX color command colors.</summary>
		Math = 32,

		/// <summary>A footnote mark.</summary>
		Note = 64,

		/// <summary>A character followed by the room an inline code box needs: the one before the code, or the code's last one.</summary>
		Pad = 128,

		/// <summary>A character followed by the room a mention's icon needs: the one before a link to a file or a web page.</summary>
		Mention = 256,
	}

	/// <summary>Builds inline bold, italic, code, strike-through, math and link spans in prose typography, fading its tail when <paramref name="fade"/> is given.</summary>
	/// <remarks>A click on a link's text opens it; a drag that selects text does not.</remarks>
	private static Text BuildInlineText( string text, Action<Uri>? openLink, bool grow = false, Func<int, double>? fade = null, int after = 0 )
	{
		var runs = PadCode( ParseInlineRuns( text ) );
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
		// Each boxed code range, each mention and each link in the shown text, which is the runs' text in order.
		var marks = new List<CodeMark>();
		var links = new List<(int Start, int End, Uri Link)>();
		var offset = 0;
		InlineRun? previous = null;
		foreach ( var run in runs )
		{
			if ( IsBoxed( run.Kind ) && run.Text.Length > 0 )
			{
				// A code span split for its padded last character still gets one box.
				if ( marks.Count > 0 && marks[^1] is { Boxed: true } box && box.Range.Start + box.Range.Length == offset )
					marks[^1] = box with { Range = new TextRange( box.Range.Start, box.Range.Length + run.Text.Length ), Text = box.Text + run.Text };
				else
					marks.Add( new( new TextRange( offset, run.Text.Length ), run.Text, IsCode: true, Boxed: true, PadBefore: offset > 0, Glyph: null ) );
			}
			else if ( run.Link is { } target && run.Text.Length > 0 )
			{
				// A link's runs join one mark, which shows its hover effect; a mention's first run leads with its icon, in the room the
				// character before it leaves.
				if ( previous is { Link: var before } && before == target && marks.Count > 0 && marks[^1] is { IsLink: true } joined
					&& joined.Range.Start + joined.Range.Length == offset )
					marks[^1] = joined with { Range = new TextRange( joined.Range.Start, joined.Range.Length + run.Text.Length ), Text = joined.Text + run.Text };
				else
					marks.Add( new( new TextRange( offset, run.Text.Length ), run.Text, run.Kind.HasFlag( InlineKind.Code ), Boxed: false, PadBefore: false,
						previous is { } prior && prior.Kind.HasFlag( InlineKind.Mention ) ? ComputeMentionGlyph( target ) : null, IsLink: true ) );
			}
			if ( run.Link is { } link )
				links.Add( (offset, offset + run.Text.Length, link) );
			offset += run.Text.Length;
			previous = run;
		}
		if ( marks.Count == 0 && (openLink is null || links.Count == 0) )
			return view;

		// Code boxes, mention icons and links all read this text's layout through its handle.
		var handle = new ElementHandle();
		view.Handle = handle;
		if ( marks.Count > 0 )
			CodeBoxes.Remember( view, handle, [.. marks] );
		if ( openLink is null || links.Count == 0 )
			return view;

		// The press lives outside this declaration: a rebuild between press and release replaces these handlers, and a press
		// remembered only in this build's closure was lost, so the link did nothing.
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
					// Ctrl, or Cmd on macOS, asks for the system's own app; the handler reads that while it runs.
					IsOpeningExternally = e.Modifiers.HasPrimary;
					try
					{
						openLink( link );
					}
					finally
					{
						IsOpeningExternally = false;
					}
					return;
				}
			}
		};
		return view;
	}

	/// <summary>Splits off the character before each inline code span and the span's last character, marked to be followed by the box's room, and the
	/// character before each link, marked to be followed by its icon's room.</summary>
	/// <remarks>Spacing after characters, not added characters, so copied text stays as written.</remarks>
	private static List<InlineRun> PadCode( List<InlineRun> runs )
	{
		var padded = new List<InlineRun>( runs.Count + 4 );
		void MarkBefore( InlineKind flag )
		{
			if ( padded.Count == 0 || padded[^1] is not { Text.Length: > 0 } before || IsBoxed( before.Kind ) )
				return;
			var cut = before.Text.Length - LastLength( before.Text );
			padded[^1] = before with { Text = before.Text[..cut] };
			padded.Add( before with { Text = before.Text[cut..], Kind = before.Kind | flag } );
		}

		foreach ( var run in runs )
		{
			if ( run.Text.Length > 0 && run.Link is { } link && !IsBoxed( run.Kind ) && (padded.Count == 0 || padded[^1].Link != link) )
				MarkBefore( InlineKind.Mention );
			if ( !IsBoxed( run.Kind ) || run.Text.Length == 0 )
			{
				padded.Add( run );
				continue;
			}

			MarkBefore( InlineKind.Pad );
			var last = run.Text.Length - LastLength( run.Text );
			if ( last > 0 )
				padded.Add( run with { Text = run.Text[..last] } );
			padded.Add( run with { Text = run.Text[last..], Kind = run.Kind | InlineKind.Pad } );
		}
		padded.RemoveAll( run => run.Text.Length == 0 );
		return padded;
	}

	/// <summary>Gets the length of a text's last character: two for a surrogate pair, such as most emoji.</summary>
	private static int LastLength( string text ) => text.Length > 1 && char.IsLowSurrogate( text[^1] ) && char.IsHighSurrogate( text[^2] ) ? 2 : 1;

	/// <summary>Reports whether a run is inline code drawn in a box: code that is not a link, since a mention shows by its icon instead.</summary>
	private static bool IsBoxed( InlineKind kind ) => kind.HasFlag( InlineKind.Code ) && !kind.HasFlag( InlineKind.Link );

	/// <summary>Builds inline spans; with a fade, characters split into runs of equal opacity by how many visible characters follow them.</summary>
	private static TextSpan[] BuildInlineSpans( string text, List<InlineRun> runs, Func<int, double>? fade, int after )
	{
		if ( fade is null || fade( after ) >= 1 )
			return [.. runs.Select( run => new TextSpan( run.Text, BuildInlineStyle( run, 1 )! ) )];

		var spans = new List<TextSpan>();
		var remaining = runs.Sum( run => run.Text.Length );
		foreach ( var run in runs )
		{
			var value = run.Text;
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
				spans.Add( new( value[start..index], BuildInlineStyle( run, alpha )! ) );
				start = index;
				alpha = next;
			}
			remaining -= value.Length;
		}
		if ( spans.Count == 0 )
			spans.Add( new( text, null! ) );
		return [.. spans];
	}

	/// <summary>Parses inline Markdown into styled spans, with escapes, entities and inline HTML read as Markdown does.</summary>
	public static IReadOnlyList<TextSpan> ParseInline( string text ) => [.. ParseInlineRuns( text ).Select( run => new TextSpan( run.Text, BuildInlineStyle( run, 1 )! ) )];

	/// <summary>Splits inline Markdown into visible text runs and their emphasis.</summary>
	/// <remarks>
	/// One pass first masks what emphasis must not see, each piece by one private-use character: code spans, found as CommonMark does (a run of
	/// backticks closes at the next run of the same length, so <c>``a`b``</c> works); backslash escapes; HTML entities; inline math in <c>$…$</c>
	/// or <c>\(…\)</c> and LaTeX color commands; and <c>&lt;code&gt;</c> or <c>&lt;kbd&gt;</c> elements. The same pass turns other inline HTML into
	/// the Markdown it stands for, such as <c>&lt;b&gt;</c> into <c>**</c>, and autolinks such as <c>&lt;https://…&gt;</c> into links. Emphasis is
	/// matched on the masked text, so emphasis around code, such as <c>**`file.cs`**</c>, keeps its formatting.
	/// </remarks>
	private static List<InlineRun> ParseInlineRuns( string text )
	{
		var pieces = new List<MaskedPiece>();
		var masked = MaskInline( text, pieces );
		var runs = new List<InlineRun>();
		AddEmphasisRuns( masked, InlineKind.Plain, null, pieces, runs );
		if ( runs.Count == 0 )
			runs.Add( new( text, InlineKind.Plain, null ) );
		return runs;
	}

	/// <summary>Masks code spans, escapes, entities, math and code elements, and turns other inline HTML into Markdown; see <see cref="ParseInlineRuns"/>.</summary>
	private static string MaskInline( string text, List<MaskedPiece> pieces )
	{
		var masked = new StringBuilder( text.Length );
		void Add( InlineKind kind, string value )
		{
			masked.Append( (char)(Mask + pieces.Count) );
			pieces.Add( new( kind, value ) );
		}

		var index = 0;
		while ( index < text.Length )
		{
			var character = text[index];
			if ( pieces.Count >= MaskCount )
			{
				masked.Append( character );
				index++;
				continue;
			}

			if ( character == '\\' && index + 1 < text.Length )
			{
				var next = text[index + 1];
				// \( … \) is inline math.
				if ( next == '(' && text.IndexOf( "\\)", index + 2, StringComparison.Ordinal ) is var mathEnd and > 0 )
				{
					Add( InlineKind.Math, text[(index + 2)..mathEnd] );
					index = mathEnd + 2;
					continue;
				}
				// A LaTeX color command outside math colors its text.
				if ( ColorCommandPattern().Match( text, index ) is { Success: true } command && command.Index == index )
				{
					var end = FindGroupsEnd( text, command.Index + command.Length, command.Groups[1].Value == "fcolorbox" ? 3 : 2 );
					if ( end > 0 )
					{
						masked.Append( (char)(Mask + pieces.Count) );
						pieces.Add( new( InlineKind.Math, text[index..end], IsText: true ) );
						index = end;
						continue;
					}
				}
				if ( char.IsAsciiLetterOrDigit( next ) || char.IsWhiteSpace( next ) || next > 127 )
				{
					masked.Append( character );
					index++;
					continue;
				}
				Add( InlineKind.Plain, next.ToString() );
				index += 2;
				continue;
			}

			if ( character == '`' )
			{
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
				Add( InlineKind.Code, content );
				index = close + length;
				continue;
			}

			// An image that cannot show inline keeps its alt text, linked to its source.
			if ( character == '!' && index + 1 < text.Length && text[index + 1] == '[' )
			{
				index++;
				continue;
			}

			if ( character == '$' && FindInlineMath( text, index ) is var mathClose and > 0 )
			{
				Add( InlineKind.Math, text[(index + 1)..mathClose] );
				index = mathClose + 1;
				continue;
			}

			if ( character == '&' && EntityPattern().Match( text, index ) is { Success: true } entity && entity.Index == index )
			{
				var decoded = WebUtility.HtmlDecode( entity.Value );
				if ( decoded != entity.Value )
				{
					Add( InlineKind.Plain, decoded );
					index += entity.Length;
					continue;
				}
			}

			if ( character == '<' && ReadInlineHtml( text, index, Add, masked ) is var consumed and > 0 )
			{
				index += consumed;
				continue;
			}

			masked.Append( character );
			index++;
		}
		return masked.ToString();
	}

	/// <summary>Finds the <c>$</c> that closes inline math opened at a position, or -1: the content neither starts nor ends with a space, and a
	/// digit right after the closing one means prices such as <c>$5 and $6</c>, not math. A <c>$$</c> is display math, handled as a block.</summary>
	private static int FindInlineMath( string text, int open )
	{
		if ( open + 1 >= text.Length || text[open + 1] is '$' or ' ' || (open > 0 && text[open - 1] == '$') )
			return -1;
		for ( var index = open + 1; index < text.Length; index++ )
		{
			if ( text[index] == '\\' )
			{
				index++;
				continue;
			}
			if ( text[index] != '$' )
				continue;
			if ( text[index - 1] == ' ' || (index + 1 < text.Length && char.IsAsciiDigit( text[index + 1] )) )
				return -1;
			return index;
		}
		return -1;
	}

	/// <summary>Finds the end of a number of braced groups starting at a position, or -1 when they are not all there.</summary>
	private static int FindGroupsEnd( string text, int position, int count )
	{
		for ( var group = 0; group < count; group++ )
		{
			while ( position < text.Length && text[position] == ' ' )
				position++;
			// A model such as [HTML] may come before the color.
			if ( group == 0 && position < text.Length && text[position] == '[' )
			{
				var square = text.IndexOf( ']', position );
				if ( square < 0 )
					return -1;
				position = square + 1;
			}
			if ( position >= text.Length || text[position] != '{' )
				return -1;
			var depth = 0;
			for ( ; position < text.Length; position++ )
			{
				if ( text[position] == '{' )
					depth++;
				else if ( text[position] == '}' && --depth == 0 )
					break;
			}
			if ( position >= text.Length )
				return -1;
			position++;
		}
		return position;
	}

	/// <summary>Reads inline HTML at a position, adding what it stands for, and returns how many characters it took, or 0 when it is not HTML.</summary>
	/// <remarks>
	/// <c>&lt;code&gt;</c>, <c>&lt;kbd&gt;</c> and <c>&lt;samp&gt;</c> become code; <c>&lt;b&gt;</c>, <c>&lt;strong&gt;</c>, <c>&lt;i&gt;</c>,
	/// <c>&lt;em&gt;</c>, <c>&lt;s&gt;</c>, <c>&lt;del&gt;</c> and <c>&lt;strike&gt;</c> become their Markdown marks; <c>&lt;a href&gt;</c> becomes a
	/// link; <c>&lt;br&gt;</c> breaks the line; <c>&lt;sup&gt;</c> and <c>&lt;sub&gt;</c> use script letters; autolinks become links; comments and
	/// any other tag disappear, keeping their text.
	/// </remarks>
	private static int ReadInlineHtml( string text, int index, Action<InlineKind, string> add, StringBuilder masked )
	{
		if ( AutolinkPattern().Match( text, index ) is { Success: true } autolink && autolink.Index == index )
		{
			var address = autolink.Groups[1].Value;
			if ( address.Contains( '@' ) && !address.Contains( ':' ) )
				masked.Append( address );
			else
				masked.Append( '[' ).Append( EscapeLabel( address ) ).Append( "](" ).Append( address ).Append( ')' );
			return autolink.Length;
		}

		if ( text.AsSpan( index ).StartsWith( "<!--" ) )
		{
			var close = text.IndexOf( "-->", index + 4, StringComparison.Ordinal );
			return close < 0 ? 0 : close + 3 - index;
		}

		if ( HtmlElementPattern().Match( text, index ) is { Success: true } element && element.Index == index )
		{
			var inner = element.Groups["inner"].Value;
			switch ( element.Groups["tag"].Value.ToLowerInvariant() )
			{
				case "code" or "kbd" or "samp" or "tt":
					add( InlineKind.Code, WebUtility.HtmlDecode( inner ) );
					return element.Length;
				case "sup":
					add( InlineKind.Plain, LatexText.ConvertPlain( "^{" + WebUtility.HtmlDecode( inner ) + "}" ) );
					return element.Length;
				case "sub":
					add( InlineKind.Plain, LatexText.ConvertPlain( "_{" + WebUtility.HtmlDecode( inner ) + "}" ) );
					return element.Length;
				case "a" when HrefPattern().Match( element.Groups["attributes"].Value ) is { Success: true } href:
					masked.Append( '[' ).Append( inner ).Append( "](" ).Append( WebUtility.HtmlDecode( href.Groups[1].Value ) ).Append( ')' );
					return element.Length;
			}
		}

		if ( HtmlTagPattern().Match( text, index ) is not { Success: true } tag || tag.Index != index )
			return 0;

		var name = tag.Groups["tag"].Value.ToLowerInvariant();
		// An image inside prose shows its alt text; an image alone in its paragraph shows as an image.
		if ( name == "img" )
		{
			var alt = AltPattern().Match( tag.Value ) is { Success: true } found ? WebUtility.HtmlDecode( found.Groups[1].Value ) : "";
			masked.Append( alt );
			return tag.Length;
		}
		masked.Append( name switch
		{
			"b" or "strong" => "**",
			"i" or "em" or "cite" or "var" => "*",
			"s" or "del" or "strike" => "~~",
			"br" => "\n",
			_ => "",
		} );
		return tag.Length;
	}

	/// <summary>Escapes the characters that would end a link label early.</summary>
	private static string EscapeLabel( string label ) => label.Replace( "]", "\\]", StringComparison.Ordinal );

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

	/// <summary>Adds the runs of masked text, matching emphasis, links, footnote marks and reference links, and parsing their contents again with the
	/// outer emphasis and link added.</summary>
	private static void AddEmphasisRuns( string text, InlineKind outer, Uri? link, List<MaskedPiece> pieces, List<InlineRun> runs )
	{
		var position = 0;
		foreach ( Match match in InlinePattern().Matches( text ) )
		{
			InlineKind kind;
			string inner;
			var target = link;
			if ( match.Groups["bi"].Success )
				(kind, inner) = (InlineKind.Bold | InlineKind.Italic, match.Groups["bi"].Value);
			else if ( match.Groups["b"].Success )
				(kind, inner) = (InlineKind.Bold, match.Groups["b"].Value);
			else if ( match.Groups["s"].Success )
				(kind, inner) = (InlineKind.Strike, match.Groups["s"].Value);
			else if ( match.Groups["i"].Success )
				(kind, inner) = (InlineKind.Italic, match.Groups["i"].Value);
			else if ( match.Groups["fn"].Success )
			{
				if ( match.Index > position )
					AddPieceRuns( text[position..match.Index], outer, link, pieces, runs );
				runs.Add( new( FormatFootnote( match.Groups["fn"].Value ), outer | InlineKind.Note, link ) );
				position = match.Index + match.Length;
				continue;
			}
			else if ( match.Groups["l"].Success )
			{
				(kind, inner) = (InlineKind.Link, match.Groups["l"].Value);
				target = ResolveLink( match.Groups["href"].Value ) ?? link;
			}
			else
			{
				// A reference link, [text][label] or [label], links only when the document defines its label.
				var label = match.Groups["ref"].Success && match.Groups["ref"].Value.Length > 0 ? match.Groups["ref"].Value : match.Groups["rl"].Value;
				if ( _references?.GetValueOrDefault( label.Trim().ToLowerInvariant() ) is not { } destination )
					continue;
				(kind, inner) = (InlineKind.Link, match.Groups["rl"].Value);
				target = ResolveLink( destination ) ?? link;
			}

			if ( match.Index > position )
				AddPieceRuns( text[position..match.Index], outer, link, pieces, runs );
			AddEmphasisRuns( inner, outer | kind, target, pieces, runs );
			position = match.Index + match.Length;
		}
		if ( position < text.Length )
			AddPieceRuns( text[position..], outer, link, pieces, runs );
	}

	/// <summary>Formats a footnote mark: its number in superscript when the document defines it, else its label in brackets.</summary>
	private static string FormatFootnote( string label ) =>
		_footnotes?.GetValueOrDefault( label.Trim().ToLowerInvariant() ) is { } number and > 0 ? LatexText.ConvertPlain( $"^{{{number}}}" ) : $"[{label}]";

	/// <summary>Adds text with its masked pieces restored: code as code runs, math as colored runs, escapes and entities as plain text, and bare
	/// web addresses outside a link as link runs.</summary>
	private static void AddPieceRuns( string text, InlineKind kind, Uri? link, List<MaskedPiece> pieces, List<InlineRun> runs )
	{
		var plain = new StringBuilder();
		void Flush()
		{
			if ( plain.Length == 0 )
				return;
			AddUrlRuns( plain.ToString(), kind, link, runs );
			plain.Clear();
		}

		foreach ( var character in text )
		{
			var index = character - Mask;
			if ( index < 0 || index >= pieces.Count )
			{
				plain.Append( character );
				continue;
			}

			var piece = pieces[index];
			switch ( piece.Kind )
			{
				case InlineKind.Plain:
					// An escaped character or a decoded entity stays literal; it never starts a link.
					Flush();
					runs.Add( new( piece.Text, kind, link ) );
					break;
				case InlineKind.Code:
					Flush();
					// Code that names a file or a web address is a mention of it, which opens it.
					if ( link is null && (ParseBareUrl( piece.Text ) ?? ParseFileMention( piece.Text )) is { } mention )
						runs.Add( new( piece.Text, kind | InlineKind.Code | InlineKind.Link, mention ) );
					else
						runs.Add( new( piece.Text, kind | InlineKind.Code, link ) );
					break;
				default:
					Flush();
					foreach ( var segment in LatexText.Convert( piece.Text, piece.IsText ) )
						runs.Add( new( segment.Text, kind | InlineKind.Math, link, segment.Color ) );
					break;
			}
		}
		Flush();
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

	/// <summary>Gets a link that opens the file a piece of code names, such as <c>src/view.cs</c> or <c>Core/Rows.cs:42</c>, or null when it names none.</summary>
	/// <remarks>Only a known file extension counts, so member access such as <c>node.id</c> or <c>Theme.Current</c> stays code.</remarks>
	private static Uri? ParseFileMention( string code ) =>
		FileMentionPattern().Match( code.Trim() ) is { Success: true } match && FileExtensions.Contains( match.Groups["e"].Value )
			? ResolveLink( match.Groups["p"].Value )
			: null;

	/// <summary>Gets the icon a mention leads with: a globe for a web page, a page for a file.</summary>
	private static string ComputeMentionGlyph( Uri target ) => target.Scheme is "http" or "https" ? "\uE774" : "\uE7C3";

	/// <summary>Reads an absolute http or https address, or null.</summary>
	private static Uri? ParseWebLink( string destination ) =>
		Uri.TryCreate( destination.Trim(), UriKind.Absolute, out var uri ) && uri.Scheme is "https" or "http" ? uri : null;

	/// <summary>Reads a link's destination: a web address as it is; a file, by <c>file:</c> URI or by full or relative path, as a link that opens it in
	/// the editor (<c>lokn://editor/open?path=…</c>), whose opener resolves a relative path against its own folder; null for anything else.</summary>
	/// <param name="destination">The destination as written, possibly in angle brackets and followed by a quoted title.</param>
	internal static Uri? ResolveLink( string destination )
	{
		var value = destination.Trim();
		if ( value.StartsWith( '<' ) && value.IndexOf( '>' ) is var close and > 0 )
			value = value[1..close];
		else if ( LinkTitlePattern().Match( value ) is { Success: true } titled )
			value = titled.Groups[1].Value;
		value = value.Trim();
		if ( value.Length == 0 || value.StartsWith( '#' ) )
			return null;
		if ( ParseWebLink( value ) is { } web )
			return web;

		string path;
		if ( Uri.TryCreate( value, UriKind.Absolute, out var uri ) && !(value.Length > 1 && value[1] == ':') )
		{
			if ( !uri.IsFile )
				return null;
			path = uri.LocalPath;
		}
		else
		{
			// A line anchor such as #L12 is not part of the file's name.
			var anchor = value.IndexOf( '#' );
			path = Uri.UnescapeDataString( anchor > 0 ? value[..anchor] : value );
		}
		return new Uri( $"{LoknLinks.Create( "editor", "open" )}?path={Uri.EscapeDataString( path )}" );
	}

	/// <summary>Builds the style of one inline run at an opacity; plain text at full opacity needs none.</summary>
	private static Style? BuildInlineStyle( InlineRun run, double alpha )
	{
		var kind = run.Kind;
		if ( kind == InlineKind.Plain && alpha >= 1 && run.Color is null )
			return null;

		var style = new Style();
		if ( kind.HasFlag( InlineKind.Bold ) )
			style.FontWeight = EmphasisWeight;
		if ( kind.HasFlag( InlineKind.Italic ) )
			style.FontStyle = FontStyle.Italic;
		if ( kind.HasFlag( InlineKind.Code ) )
			style.FontFamily = ViewStyle.CodeFontFamily;
		if ( kind.HasFlag( InlineKind.Pad ) )
			style.LetterSpacing = CodePad;
		else if ( kind.HasFlag( InlineKind.Mention ) )
			style.LetterSpacing = MentionGap;
		// A mention, code that opens what it names, reads by its icon and color rather than an underline.
		if ( kind.HasFlag( InlineKind.Link ) && !kind.HasFlag( InlineKind.Code ) )
			style.TextDecoration = TextDecoration.Underline;
		else if ( kind.HasFlag( InlineKind.Strike ) )
			style.TextDecoration = TextDecoration.LineThrough;
		// Code shows in the theme's red and links in its green, as Tern draws them; a run's own color still wins.
		var color = run.Color ?? (kind.HasFlag( InlineKind.Link ) ? Theme.Current.AnsiGreen
			: kind.HasFlag( InlineKind.Code ) ? Theme.Current.AnsiRed
			: kind.HasFlag( InlineKind.Note ) ? Theme.Current.Accent : Theme.Current.Text);
		if ( alpha < 1 )
			style.Color = color.WithAlpha( color.A * alpha );
		else if ( color != Theme.Current.Text )
			style.Color = color;
		return style;
	}

	/// <summary>Matches inline emphasis, deletion, italics, footnote marks, links and reference links; masked pieces are hidden from it.</summary>
	/// <remarks>Underscores mark emphasis only at word edges, so names such as <c>snake_case_name</c> stay as they are.</remarks>
	[GeneratedRegex( @"\*\*\*(?<bi>[^*\s](?:.*?[^*\s])?)\*\*\*|(?<!\w)___(?<bi>[^_\s](?:.*?[^_\s])?)___(?!\w)|\*\*(?<b>[^*\s](?:.*?[^\s])?)\*\*|(?<!\w)__(?<b>[^_\s](?:.*?[^\s])?)__(?!\w)|~~(?<s>.+?)~~|\*(?<i>[^*\s](?:[^*]*?[^*\s])?)\*|(?<!\w)_(?<i>[^_\s](?:[^_]*?[^_\s])?)_(?!\w)|\[\^(?<fn>[^\]\s]+)\]|\[(?<l>[^\]]+)\]\((?<href><[^>]*>|[^()\s]*(?:\([^()\s]*\)[^()\s]*)*(?:\s+(?:""[^""]*""|'[^']*'))?)\)|\[(?<rl>[^\]]+)\](?:\[(?<ref>[^\]]*)\])?" )]
	private static partial Regex InlinePattern();

	/// <summary>Matches a bare web address in prose; trailing sentence punctuation is trimmed after the match.</summary>
	[GeneratedRegex( @"https?://[^\s<>()\[\]{}""'`]+" )]
	private static partial Regex BareUrlPattern();

	/// <summary>Matches a file path in code: a name with an extension, after any folders and a drive, then an optional line such as <c>:42</c>,
	/// <c>:10-20</c> or <c>#L42</c>, which the link leaves out.</summary>
	[GeneratedRegex( @"^(?<p>(?:[A-Za-z]:)?[\w.\-~/\\@+]*?[\w\-]+\.(?<e>[A-Za-z0-9]{1,10}))(?::\d+(?:-\d+)?|#L\d+(?:-L?\d+)?)?$" )]
	private static partial Regex FileMentionPattern();

	/// <summary>Holds the file extensions that make code a file mention.</summary>
	private static readonly HashSet<string> FileExtensions = new( StringComparer.OrdinalIgnoreCase )
	{
		"cs", "csproj", "sln", "slnx", "props", "targets", "razor", "xaml", "gs",
		"js", "mjs", "cjs", "jsx", "ts", "mts", "cts", "tsx", "json", "jsonc", "jsonl", "css", "scss", "html", "htm", "vue", "svelte",
		"py", "rs", "go", "java", "kt", "swift", "c", "cc", "cpp", "cxx", "h", "hh", "hpp", "hxx", "lua", "rb", "php", "sql",
		"md", "mdx", "txt", "log", "csv", "xml", "yml", "yaml", "toml", "ini", "cfg", "conf", "env", "lock",
		"sh", "bash", "zsh", "ps1", "psm1", "bat", "cmd",
		"png", "jpg", "jpeg", "gif", "webp", "svg", "ico", "pdf",
		"shader", "hlsl", "glsl", "vmat", "vmdl", "prefab", "scene", "sbproj",
	};

	/// <summary>Matches an HTML entity: named, decimal or hexadecimal.</summary>
	[GeneratedRegex( @"&(?:[A-Za-z][A-Za-z0-9]{1,31}|#[0-9]{1,7}|#[xX][0-9A-Fa-f]{1,6});" )]
	private static partial Regex EntityPattern();

	/// <summary>Matches an autolink: a web address or an email address in angle brackets.</summary>
	[GeneratedRegex( @"<((?:https?|ftp|mailto|file)://[^\s<>]+|mailto:[^\s<>]+|[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,})>" )]
	private static partial Regex AutolinkPattern();

	/// <summary>Matches a whole inline HTML element whose content becomes something else: code, scripts or a link.</summary>
	[GeneratedRegex( @"<(?<tag>code|kbd|samp|tt|sup|sub|a)(?<attributes>\s[^>]*)?>(?<inner>.*?)</\k<tag>\s*>", RegexOptions.IgnoreCase )]
	private static partial Regex HtmlElementPattern();

	/// <summary>Matches one opening, closing or self-closing tag of a known HTML element, so generics in prose such as <c>List&lt;T&gt;</c> stay text.</summary>
	[GeneratedRegex( @"</?(?<tag>a|b|strong|i|em|cite|var|s|del|strike|ins|u|mark|small|big|br|hr|span|div|p|font|center|abbr|q|img|details|summary|section|article|header|footer|nav|aside|figure|figcaption|ul|ol|li|dl|dt|dd|table|thead|tbody|tfoot|tr|td|th|blockquote|pre|h[1-6]|picture|source|video|audio|wbr)(?=[\s/>])(?:\s+[^<>]*?)?\s*/?>", RegexOptions.IgnoreCase )]
	private static partial Regex HtmlTagPattern();

	/// <summary>Matches an <c>alt</c> attribute's value.</summary>
	[GeneratedRegex( @"alt\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase )]
	private static partial Regex AltPattern();

	/// <summary>Matches an <c>href</c> attribute's value.</summary>
	[GeneratedRegex( @"href\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase )]
	private static partial Regex HrefPattern();

	/// <summary>Matches one image: Markdown's <c>![alt](source "title")</c>, or an HTML <c>&lt;img&gt;</c> with its <c>src</c> and optional <c>alt</c>.</summary>
	[GeneratedRegex( @"!\[(?<alt>[^\]]*)\]\((?:<(?<path>[^>]+)>|(?<path>[^)]+?))(?:\s+""[^""]*"")?\)|<img\b(?=[^>]*\bsrc\s*=\s*[""'](?<src>[^""']+)[""'])(?:(?=[^>]*\balt\s*=\s*[""'](?<imgalt>[^""']*)[""']))?[^>]*>", RegexOptions.IgnoreCase )]
	private static partial Regex ImagePattern();

	/// <summary>Matches a link destination followed by a quoted title.</summary>
	[GeneratedRegex( @"^(\S+)\s+(?:""[^""]*""|'[^']*')$" )]
	private static partial Regex LinkTitlePattern();

	/// <summary>Matches the start of a LaTeX color command outside math.</summary>
	[GeneratedRegex( @"\\(textcolor|colorbox|fcolorbox)(?=[\s\[{])" )]
	private static partial Regex ColorCommandPattern();
}
