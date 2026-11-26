using System.Text.Json;
using System.Text.RegularExpressions;
using Goo;
using Lokn.Core;
using Lokn.Modules.Markdown;

namespace Lokn.Modules.Chat;

/// <summary>Draws a web search as Tern does: its query on the tool line with the provider and source count at the end, then its answer as Markdown.</summary>
public sealed partial class ChatContent
{
	/// <summary>Defines how many lines of a web search's answer show before the rest folds away.</summary>
	private const int WebSearchLines = 8;

	/// <summary>Builds a web search: a bold "Web search" and the query, the provider and source count muted at the far end, and the answer as Markdown,
	/// cut to its first <see cref="WebSearchLines"/> lines until opened.</summary>
	private Container BuildWebSearch( string key, JsonElement arguments, string status, JsonElement? result )
	{
		var theme = Theme.Current;
		var mark = status switch
		{
			"Running" => ChatIcons.BuildSpinner( "web-search-spinner" ),
			"Failed" => ChatIcons.BuildIcon( ChatIcons.ComputeToolGlyph( "web_search" ), theme.Error ),
			_ => ChatIcons.BuildIcon( ChatIcons.ComputeToolGlyph( "web_search" ) ),
		};
		var text = result is { } body ? TerminalText.BuildPlain( ComputeResultText( body ) ).Trim() : "";
		var head = new List<Blob> { mark, BuildToolTitle( "Web search", ReadText( arguments, "query" ) ), new Spacer() };
		var summary = new List<string>();
		if ( result is { ValueKind: JsonValueKind.Object } saved && saved.TryGetProperty( "details", out var details )
			&& ReadText( details, "provider" ) is { Length: > 0 } provider )
			summary.Add( char.ToUpperInvariant( provider[0] ) + provider[1..] );
		var sources = SourcePattern().Matches( text ).Count;
		if ( sources > 0 )
			summary.Add( $"{sources:N0} {(sources == 1 ? "source" : "sources")}" );
		if ( summary.Count > 0 )
			head.Add( new Text
			{
				Content = string.Join( " · ", summary ),
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

		if ( text.Length > 0 )
		{
			var lines = text.Replace( "\r\n", "\n" ).Split( '\n' );
			var folds = lines.Length > WebSearchLines + PreviewSlack;
			var expanded = _expanded.Contains( key + ":answer" );
			var shown = folds && !expanded ? string.Join( '\n', lines.Take( WebSearchLines ) ) : text;
			children.Add( new Container
			{
				Key = "answer",
				MinWidth = 0,
				Color = theme.Muted,
				Children = [MarkdownView.Build( shown, OpenLink )],
			} );
			if ( folds )
			{
				var toggle = BuildMoreToggle( expanded ? "Show less" : $"{lines.Length - WebSearchLines:N0} more lines · Show all", expanded,
					() => context.Run( () => UpdateExpanded( key + ":answer" ) ) );
				toggle.Key = "answer-fold";
				children.Add( toggle );
			}
		}

		return new Container
		{
			Key = key,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = $"web search {status}",
			Gap = theme.Spacing,
			Children = children,
		};
	}

	/// <summary>Matches a numbered source at the start of a line of a web search's answer, such as <c>[1]</c>.</summary>
	[GeneratedRegex( @"^\s*\[\d+\]", RegexOptions.Multiline )]
	private static partial Regex SourcePattern();
}
