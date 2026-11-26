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

	/// <summary>Builds a grep as its pattern and folder, then one card per file with its matching rows marked; the output folds past <see cref="GrepLines"/> rows.</summary>
	private Container BuildGrep( string key, JsonElement arguments, string status, JsonElement? result )
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
				children.Add( BuildGrepFileTitle( key + ":file:" + index, file ) );
				var shown = file.Lines.Take( budget ).ToList();
				budget -= shown.Count;
				children.Add( BuildGrepCard( key + ":card:" + index, file.Path, shown ) );
			}
			if ( folds )
			{
				var toggle = BuildDisclosure( expanded ? "Show less" : $"{total - GrepLines:N0} more lines · Show all", expanded,
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

	/// <summary>Builds the line over one file's matches: a file icon, its path, and its match count in a badge.</summary>
	private static Row BuildGrepFileTitle( string key, GrepFile file )
	{
		var theme = Theme.Current;
		return new Row
		{
			Key = key,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Margin = new Edges( theme.Spacing / 2, 0, 0, 0 ),
			Children =
			[
				ChatIcons.BuildIcon( "\uE7C3", theme.Muted ),
				new Text
				{
					Content = ShortenHome( file.Path ),
					Color = theme.AnsiGreen,
					FontFamily = ViewStyle.CodeFontFamily,
					FontSize = ViewStyle.SmallSize,
					FlexShrink = 1,
					MinWidth = 0,
					TextWrap = TextWrap.NoWrap,
					TextTrimming = TextTrimming.Ellipsis,
				},
				ChatIcons.BuildBadge( file.Matches.ToString( "N0" ) ),
			],
		};
	}

	/// <summary>Builds a card of a file's rows: a bar and a bright number on matching rows, muted numbers on context, and code colored by the file's language.</summary>
	private static Container BuildGrepCard( string key, string path, List<GrepLine> lines )
	{
		var theme = Theme.Current;
		var grammar = CodeHighlighter.FindForPath( path );
		var digits = lines.Select( line => line.Number ).DefaultIfEmpty( 1 ).Max().ToString().Length;
		var rows = new List<Blob>();
		foreach ( var line in lines )
		{
			if ( line.Number == 0 )
			{
				rows.Add( new Text { Content = "⋯", Color = theme.Muted, Padding = new Edges( 0, theme.Spacing * 2 ) } );
				continue;
			}

			var code = line.Text.Length == 0 ? " " : ViewStyle.CutCodeLine( line.Text.Replace( "\t", "    " ) );
			var isInComment = false;
			rows.Add( new Row
			{
				MinWidth = 0,
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
					new Text( grammar is null || line.Text.Length == 0 ? [new TextSpan( code, null! )] : CodeHighlighter.BuildSpans( code, grammar, ref isInComment ) )
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
			Padding = new Edges( theme.Spacing / 2, 0 ),
			FontFamily = ViewStyle.CodeFontFamily,
			FontSize = ViewStyle.SmallSize,
			BackgroundColor = theme.Panel,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius * 2,
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
			if ( raw.StartsWith( "### ", StringComparison.Ordinal ) )
			{
				Flush();
				var name = raw[4..];
				var tag = name.LastIndexOf( '#' );
				path = Path.Combine( root, folder, tag > 0 ? name[..tag] : name ).Replace( '\\', '/' );
				lines = [];
			}
			else if ( raw.StartsWith( "## ", StringComparison.Ordinal ) )
			{
				Flush();
				folder = raw[3..].Trim();
			}
			else if ( raw.StartsWith( "# ", StringComparison.Ordinal ) )
			{
				Flush();
				(root, folder) = (raw[2..].Trim(), "");
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

	/// <summary>Matches one row of Omp's grep output: <c>*12:text</c> for a match or <c> 11:text</c> for context.</summary>
	[GeneratedRegex( @"^(?<m>[* ])(?<n>\d+):(?<t>.*)$" )]
	private static partial Regex OmpLinePattern();

	/// <summary>Matches one row of ripgrep's output: <c>path:12:text</c> for a match or <c>path-11-text</c> for context; a Windows path keeps its drive.</summary>
	[GeneratedRegex( @"^(?<p>(?:[A-Za-z]:)?[^:\r\n]+?)(?<s>[:-])(?<n>\d+)[:-](?<t>.*)$" )]
	private static partial Regex RipgrepLinePattern();
}
