using System.Text.Json;
using System.Text.RegularExpressions;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Chat;

/// <summary>Draws a grep as Tern does: its pattern and folder on the tool line, then each file's matches in a code card with their context.</summary>
public sealed partial class ChatContent
{
	/// <summary>Defines how many rows of a grep show before the rest folds away.</summary>
	private const int GrepLines = 12;

	/// <summary>Defines the width of the bar that marks a matching row, in logical pixels.</summary>
	private const int MatchBarWidth = 3;

	/// <summary>Describes one row of a grep's output: its line number, whether it matches, and its text; a gap row has no number.</summary>
	/// <param name="Number">The line number, or 0 for a gap between hunks.</param>
	/// <param name="IsMatch">Whether the row matched the pattern.</param>
	/// <param name="Text">The line's text.</param>
	private readonly record struct GrepLine( int Number, bool IsMatch, string Text );

	/// <summary>Describes one file in a grep's output and its rows in order.</summary>
	/// <param name="Path">The file's path as the output names it.</param>
	/// <param name="Lines">The file's rows.</param>
	/// <param name="Matches">How many rows matched.</param>
	private sealed record GrepFile( string Path, List<GrepLine> Lines, int Matches );

	/// <summary>Builds a grep as its pattern, folder and the agent's intent, then one card per file with its matching rows marked; the output folds past <see cref="GrepLines"/> rows.</summary>
	private Container BuildGrep( string key, JsonElement arguments, string status, JsonElement? result, string intent )
	{
		var theme = Theme.Current;
		var pattern = ReadText( arguments, "pattern" );
		var folder = ReadText( arguments, "path" );
		var mark = status switch
		{
			"Running" => ChatIcons.BuildSpinner( "grep-spinner" ),
			"Failed" => ChatIcons.BuildIcon( ChatIcons.ComputeToolGlyph( "grep" ), theme.Error ),
			_ => ChatIcons.BuildIcon( ChatIcons.ComputeToolGlyph( "grep" ) ),
		};
		var title = new Text( [
			new( "Grep", new Style { FontWeight = 600, Color = theme.Text } ),
			new( pattern.Length > 0 ? "  " + pattern : "", new Style { FontFamily = ViewStyle.CodeFontFamily, FontSize = ViewStyle.SmallSize, Color = theme.AnsiYellow } ),
			new( folder.Length > 0 ? "   in " + ShortenHome( folder ) : "", new Style { FontFamily = ViewStyle.CodeFontFamily, FontSize = ViewStyle.SmallSize, Color = theme.Muted } ),
			new( intent.Length > 0 ? "  " + intent : "", new Style { Color = theme.Muted } ),
		] )
		{
			FlexShrink = 1,
			MinWidth = 0,
			TextWrap = TextWrap.NoWrap,
			TextTrimming = TextTrimming.Ellipsis,
		};
		var children = new List<Blob>
		{
			new Row { Key = "tool-head", AlignItems = AlignItems.Center, Gap = theme.Spacing, Children = [mark, title] },
		};

		var text = result is { } body ? TerminalText.BuildPlain( ComputeResultText( body ) ) : "";
		var files = ParseGrep( text );
		var highlight = BuildGrepHighlight( pattern, arguments );
		if ( files.Count == 0 )
		{
			// Output in a form this does not read, such as "No matches", shows as it came.
			if ( text.Trim().Length > 0 )
				children.Add( BuildCodePreview( key + ":output", text.Trim(), "text" ) );
		}
		else
		{
			var expanded = _expanded.Contains( key + ":grep" );
			// Folding hides at least a few rows; hiding one or two would save nothing.
			var total = files.Sum( file => file.Lines.Count );
			var folds = total > GrepLines + PreviewSlack;
			var budget = expanded || !folds ? int.MaxValue : GrepLines;
			foreach ( var (index, file) in files.Index() )
			{
				if ( budget <= 0 )
					break;
				var shown = file.Lines.Take( budget ).ToList();
				budget -= shown.Count;
				// The rows the fold cuts off fade out, so the card reads as continuing.
				var fades = folds && !expanded && budget <= 0;
				children.Add( new Container
				{
					Key = key + ":file:" + index,
					Gap = theme.Spacing / 2,
					Margin = new Edges( theme.Spacing / 2, 0, 0, 0 ),
					Children = [BuildGrepFileTitle( key + ":title:" + index, file ), BuildGrepCard( key + ":card:" + index, file.Path, shown, highlight, fades )],
				} );
			}
			if ( folds )
			{
				var toggle = BuildMoreToggle( expanded ? "Show less" : $"{total - GrepLines:N0} more lines · Show all", expanded,
					() => context.Run( () => UpdateExpanded( key + ":grep" ) ) );
				toggle.Key = "grep-fold";
				children.Add( toggle );
			}
		}

		return new Container
		{
			Key = key,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = $"grep tool {status}",
			Gap = theme.Spacing / 2,
			Children = children,
		};
	}

	/// <summary>Builds the bar over one file's matches: a file icon, its path, and its match count at the end, in a rounded box.</summary>
	private static Row BuildGrepFileTitle( string key, GrepFile file )
	{
		var theme = Theme.Current;
		return new Row
		{
			Key = key,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Padding = new Edges( theme.Spacing / 2, theme.Spacing ),
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius * 2,
			Children =
			[
				ChatIcons.BuildIcon( "\uE7C3", theme.Muted ),
				new Text
				{
					Content = ShortenHome( file.Path ),
					Color = theme.Muted,
					FontFamily = ViewStyle.CodeFontFamily,
					FontSize = ViewStyle.SmallSize,
					FlexGrow = 1,
					FlexShrink = 1,
					MinWidth = 0,
					TextWrap = TextWrap.NoWrap,
					TextTrimming = TextTrimming.Ellipsis,
				},
				ChatIcons.BuildBadge( file.Matches.ToString( "N0" ) ),
			],
		};
	}

	/// <summary>Defines how many of a cut grep card's last rows fade out.</summary>
	private const int GrepFadeRows = 3;

	/// <summary>Builds a grep's pattern as a regex that finds what it matched in a row, case-insensitive when the call asked; null when it is not a valid regex.</summary>
	private static Regex? BuildGrepHighlight( string pattern, JsonElement arguments )
	{
		if ( pattern.Length == 0 )
			return null;
		var ignoreCase = arguments.ValueKind == JsonValueKind.Object && (arguments.TryGetProperty( "i", out var i ) && i.ValueKind == JsonValueKind.True
			|| arguments.TryGetProperty( "ignoreCase", out var c ) && c.ValueKind == JsonValueKind.True
			|| arguments.TryGetProperty( "case", out var cs ) && cs.ValueKind == JsonValueKind.False);
		try
		{
			return new Regex( pattern, (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds( 20 ) );
		}
		catch ( ArgumentException )
		{
			return new Regex( Regex.Escape( pattern ), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds( 20 ) );
		}
	}

	/// <summary>Builds the rows of a file's matches: a bar and a bright number on matching rows, muted numbers on context, code colored by the file's
	/// language, and what the pattern matched marked bold in yellow; a cut card fades out its last rows.</summary>
	private static Container BuildGrepCard( string key, string path, List<GrepLine> lines, Regex? highlight, bool fades )
	{
		var theme = Theme.Current;
		var grammar = CodeHighlighter.FindForPath( path );
		var digits = lines.Select( line => line.Number ).DefaultIfEmpty( 1 ).Max().ToString().Length;
		var rows = new List<Blob>();
		foreach ( var (index, line) in lines.Index() )
		{
			var fromEnd = lines.Count - index;
			var opacity = fades && fromEnd <= GrepFadeRows ? (double)fromEnd / (GrepFadeRows + 1) : 1;
			if ( line.Number == 0 )
			{
				rows.Add( new Text { Content = "⋯", Color = theme.Muted, Opacity = opacity, Padding = new Edges( 0, theme.Spacing * 2 ) } );
				continue;
			}

			var code = line.Text.Length == 0 ? " " : ViewStyle.CutCodeLine( line.Text.Replace( "\t", "    " ) );
			var isInComment = false;
			var spans = grammar is null || line.Text.Length == 0 ? [new TextSpan( code, null! )] : CodeHighlighter.BuildSpans( code, grammar, ref isInComment );
			if ( line.IsMatch && highlight is not null )
				spans = MarkMatches( spans, code, highlight );
			rows.Add( new Row
			{
				MinWidth = 0,
				Opacity = opacity,
				BackgroundColor = line.IsMatch ? theme.Accent.WithAlpha( 0.06 ) : Color.Transparent,
				Children =
				[
					new Container { Width = MatchBarWidth, FlexShrink = 0, BackgroundColor = line.IsMatch ? theme.Accent : Color.Transparent },
					new Text
					{
						Content = line.Number.ToString(),
						Width = Length.Em( digits * 0.6 ) + theme.Spacing * 2,
						Padding = new Edges( 0, theme.Spacing ),
						FlexShrink = 0,
						TextAlign = TextAlign.Right,
						Color = line.IsMatch ? theme.Text : theme.Muted,
						FontWeight = line.IsMatch ? 600 : 400,
					},
					new Text( spans )
					{
						FlexGrow = 1,
						FlexShrink = 1,
						MinWidth = 0,
						TextWrap = TextWrap.NoWrap,
						TextTrimming = TextTrimming.Ellipsis,
						Color = line.IsMatch ? theme.Text : theme.Muted,
					},
				],
			} );
		}

		return new Container
		{
			Key = key,
			MinWidth = 0,
			Overflow = Overflow.Hidden,
			FontFamily = ViewStyle.CodeFontFamily,
			FontSize = ViewStyle.SmallSize,
			Children = rows,
		};
	}

	/// <summary>Splits a row's colored spans where the pattern matched, marking those characters bold in yellow over their syntax color.</summary>
	private static TextSpan[] MarkMatches( TextSpan[] spans, string code, Regex highlight )
	{
		var marked = new bool[code.Length];
		try
		{
			foreach ( Match match in highlight.Matches( code ) )
			{
				for ( var at = match.Index; at < match.Index + match.Length && at < marked.Length; at++ )
					marked[at] = true;
			}
		}
		catch ( RegexMatchTimeoutException )
		{
			return spans;
		}
		if ( !marked.Contains( true ) )
			return spans;

		var theme = Theme.Current;
		var result = new List<TextSpan>();
		var offset = 0;
		foreach ( var span in spans )
		{
			var start = 0;
			while ( start < span.Text.Length )
			{
				var isMarked = offset + start < marked.Length && marked[offset + start];
				var end = start;
				while ( end < span.Text.Length && (offset + end < marked.Length && marked[offset + end]) == isMarked )
					end++;
				var piece = span.Text[start..end];
				result.Add( isMarked
					? new TextSpan( piece, new Style { Color = theme.AnsiYellow, FontWeight = 700, TextDecoration = TextDecoration.Underline } )
					: new TextSpan( piece, span.Style! ) );
				start = end;
			}
			offset += span.Text.Length;
		}
		return [.. result];
	}

	/// <summary>Reads a grep's output: Omp's headed form (<c>## folder/</c>, <c>### file#TAG</c>, then <c>*12:text</c> for a match and <c> 11:text</c> for
	/// context), or ripgrep's <c>path:12:text</c> and <c>path-11-text</c>. A jump in line numbers, or a <c>--</c> or <c>…</c> line, starts a new hunk.</summary>
	private static List<GrepFile> ParseGrep( string text )
	{
		var files = new List<GrepFile>();
		string root = "", folder = "";
		List<GrepLine>? lines = null;
		var matches = 0;
		string? path = null;
		var last = 0;

		void Flush()
		{
			if ( path is not null && lines is { Count: > 0 } )
				files.Add( new( path, lines, matches ) );
			(path, lines, matches, last) = (null, null, 0, 0);
		}

		void Add( string file, int number, bool isMatch, string line )
		{
			if ( file != path )
			{
				Flush();
				(path, lines) = (file, []);
			}
			if ( last > 0 && number != last + 1 && lines![^1].Number != 0 )
				lines.Add( new( 0, false, "" ) );
			lines!.Add( new( number, isMatch, line ) );
			matches += isMatch ? 1 : 0;
			last = number;
		}

		foreach ( var raw in text.Replace( "\r\n", "\n" ).Split( '\n' ) )
		{
			// Omp heads each folder with "#"s and a trailing slash, one more per level, and each file with one more "#" and its tag, such as
			// "# Modules/Omp/" then "## OmpService.cs#D3F9"; a folder deeper than the root heads with "## sub/".
			if ( HeadingPattern().Match( raw ) is { Success: true } heading )
			{
				Flush();
				var name = heading.Groups["name"].Value.Trim();
				if ( name.EndsWith( '/' ) || name.EndsWith( '\\' ) )
				{
					if ( heading.Groups["level"].Length == 1 )
						(root, folder) = (name, "");
					else
						folder = name;
				}
				else
				{
					var tag = name.LastIndexOf( '#' );
					path = Path.Combine( root, folder, tag > 0 ? name[..tag] : name ).Replace( '\\', '/' );
					lines = [];
				}
			}
			else if ( path is not null && lines is not null && OmpLinePattern().Match( raw ) is { Success: true } omp )
			{
				var number = int.Parse( omp.Groups["n"].Value );
				if ( last > 0 && number != last + 1 && lines.Count > 0 && lines[^1].Number != 0 )
					lines.Add( new( 0, false, "" ) );
				lines.Add( new( number, omp.Groups["m"].Value == "*", omp.Groups["t"].Value ) );
				matches += omp.Groups["m"].Value == "*" ? 1 : 0;
				last = number;
			}
			else if ( raw is "--" or "…" or "..." )
			{
				if ( lines is { Count: > 0 } && lines[^1].Number != 0 )
					lines.Add( new( 0, false, "" ) );
			}
			else if ( RipgrepLinePattern().Match( raw ) is { Success: true } rg )
				Add( rg.Groups["p"].Value, int.Parse( rg.Groups["n"].Value ), rg.Groups["s"].Value == ":", rg.Groups["t"].Value );
		}
		Flush();
		return files;
	}

	/// <summary>Shortens a path under the user's home folder to start with <c>~</c>.</summary>
	private static string ShortenHome( string path )
	{
		var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
		return home.Length > 0 && path.StartsWith( home, StringComparison.OrdinalIgnoreCase ) ? "~" + path[home.Length..] : path;
	}

	/// <summary>Matches a heading of Omp's grep output: one to three "#" and a folder or a file.</summary>
	[GeneratedRegex( @"^(?<level>#{1,3}) (?<name>.+)$" )]
	private static partial Regex HeadingPattern();

	/// <summary>Matches one row of Omp's grep output: <c>*12:text</c> for a match or <c> 11:text</c> for context.</summary>
	[GeneratedRegex( @"^(?<m>[* ])(?<n>\d+):(?<t>.*)$" )]
	private static partial Regex OmpLinePattern();

	/// <summary>Matches one row of ripgrep's output: <c>path:12:text</c> for a match or <c>path-11-text</c> for context; a Windows path keeps its drive.</summary>
	[GeneratedRegex( @"^(?<p>(?:[A-Za-z]:)?[^:\r\n]+?)(?<s>[:-])(?<n>\d+)[:-](?<t>.*)$" )]
	private static partial Regex RipgrepLinePattern();
}
