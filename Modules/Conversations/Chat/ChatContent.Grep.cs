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
		// Tern's head: the pattern quoted after the verb, and where it searched with the counts at the far end.
		var title = new Text( [
			new( "Grep", new Style { FontWeight = 600, Color = theme.Text } ),
			new( pattern.Length > 0 ? $"  “{pattern}”" : "", new Style { FontFamily = ViewStyle.CodeFontFamily, FontSize = ViewStyle.SmallSize, Color = theme.Text } ),
			new( intent.Length > 0 ? "  " + intent : "", new Style { Color = theme.Muted } ),
		] )
		{
			FlexShrink = 1,
			MinWidth = 0,
			TextWrap = TextWrap.NoWrap,
			TextTrimming = TextTrimming.Ellipsis,
		};
		var text = result is { } body ? TerminalText.BuildPlain( ComputeResultText( body ) ) : "";
		var files = ParseGrep( text );
		var matches = files.Sum( file => file.Matches );
		var summary = new List<string>();
		if ( folder.Length > 0 )
			summary.Add( "in " + ShortenHome( folder ) );
		if ( files.Count > 0 )
			summary.Add( $"{matches:N0} {(matches == 1 ? "match" : "matches")} · {files.Count:N0} {(files.Count == 1 ? "file" : "files")}" );
		var head = new List<Blob> { mark, title, new Spacer() };
		if ( summary.Count > 0 )
			head.Add( new Text
			{
				Content = string.Join( "  ", summary ),
				FontFamily = ViewStyle.CodeFontFamily,
				FontSize = ViewStyle.SmallSize,
				Color = theme.Muted,
				FlexShrink = 1,
				MinWidth = 0,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			} );
		var children = new List<Blob>
		{
			new Row { Key = "tool-head", AlignItems = AlignItems.Center, Gap = theme.Spacing, Children = head },
		};

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
					Children = [BuildGrepFileTitle( key + ":title:" + index, file ), BuildGrepCard( key + ":card:" + index, file.Path, shown, fades )],
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
			Gap = theme.Spacing * 1.25,
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
			BorderColor = theme.Border.WithAlpha( 0.45 ),
			BackgroundColor = theme.Text.WithAlpha( 0.035 ),
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

	/// <summary>Builds the rows of a file's matches in an inset card, as Tern does: matching rows bright with an accent bar and number, context rows dimmed,
	/// code colored by the file's language; a cut card fades out its last rows.</summary>
	private static Container BuildGrepCard( string key, string path, List<GrepLine> lines, bool fades )
	{
		var theme = Theme.Current;
		var grammar = CodeHighlighter.FindForPath( path );
		var digits = lines.Select( line => line.Number ).DefaultIfEmpty( 1 ).Max().ToString().Length;
		var rows = new List<Blob>();
		foreach ( var (index, line) in lines.Index() )
		{
			var fromEnd = lines.Count - index;
			var fade = fades && fromEnd <= GrepFadeRows ? (double)fromEnd / (GrepFadeRows + 1) : 1;
			if ( line.Number == 0 )
			{
				rows.Add( new Text { Content = "…", Color = theme.Muted, Opacity = fade * 0.6, Padding = new Edges( 0, theme.Spacing * 2 ) } );
				continue;
			}

			// A row is one line: a carriage return left by mixed line endings, or another line break, would draw it two lines tall.
			var text = LineBreakPattern().Replace( line.Text.TrimEnd( '\r' ), " " );
			var code = text.Length == 0 ? " " : ViewStyle.CutCodeLine( text.Replace( "\t", "    " ) );
			var isInComment = false;
			var spans = grammar is null || text.Length == 0 ? [new TextSpan( code, null! )] : CodeHighlighter.BuildSpans( code, grammar, ref isInComment );
			// Context reads as background to the match, so it dims rather than losing its colors. The dimming goes on each text, not the row: Goo
			// draws a dimmed element that has children into a layer of its own, which every later frame pays for.
			var opacity = line.IsMatch ? fade : fade * 0.55;
			rows.Add( new Row
			{
				MinWidth = 0,
				BackgroundColor = line.IsMatch ? theme.Accent.WithAlpha( 0.07 * fade ) : Color.Transparent,
				Children =
				[
					new Container { Width = MatchBarWidth, FlexShrink = 0, BackgroundColor = line.IsMatch ? theme.Accent.WithAlpha( fade ) : Color.Transparent },
					new Text
					{
						Content = line.Number.ToString(),
						Width = Length.Em( digits * 0.6 ) + theme.Spacing * 2,
						Padding = new Edges( 0, theme.Spacing ),
						FlexShrink = 0,
						// A face whose digits are exactly 0.6 em, as Berkeley Mono's, overflowed the column by rounding and wrapped, doubling the row.
						TextWrap = TextWrap.NoWrap,
						TextAlign = TextAlign.Right,
						Color = line.IsMatch ? theme.Accent : theme.Muted,
						FontWeight = line.IsMatch ? 600 : 400,
						Opacity = opacity,
					},
					new Text( spans )
					{
						FlexGrow = 1,
						FlexShrink = 1,
						MinWidth = 0,
						Padding = new Edges( 0, theme.Spacing, 0, 0 ),
						TextWrap = TextWrap.NoWrap,
						TextTrimming = TextTrimming.Ellipsis,
						Color = theme.Text,
						Opacity = opacity,
					},
				],
			} );
		}

		return new Container
		{
			Key = key,
			MinWidth = 0,
			Padding = new Edges( theme.Spacing / 2, 0 ),
			BackgroundColor = theme.Canvas,
			BorderColor = theme.Border.WithAlpha( 0.45 ),
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius,
			FontFamily = ViewStyle.CodeFontFamily,
			FontSize = ViewStyle.SmallSize,
			LineHeight = 1.5,
			Children = rows,
		};
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

	/// <summary>Matches a character that breaks a line: a carriage return, a vertical tab, a form feed, or a Unicode line or paragraph separator.</summary>
	[GeneratedRegex( "[\r\v\f\u0085\u2028\u2029]" )]
	private static partial Regex LineBreakPattern();
}
