using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Markdown;

namespace Lokn.Modules.Chat;

/// <summary>Draws file reads as one group of colored file chips and shell commands as a box with the command, its output and its outcome, as Tern does.</summary>
public sealed partial class ChatContent
{
	/// <summary>Defines the corner radius of a file chip, in logical pixels: fully round at its height.</summary>
	private const int ChipRadius = 11;

	/// <summary>Reports whether a block is a call of the read tool, which joins its neighbours in one group.</summary>
	private static bool IsReadCall( JsonElement block ) => ReadText( block, "type" ) == "toolCall" && ReadText( block, "name" ) == "read";

	/// <summary>Builds the blocks of an activity run, with each run of consecutive reads drawn as one group.</summary>
	/// <param name="parts">The run's parts, in order.</param>
	/// <param name="body">The list the built blocks are added to.</param>
	private void AddActivityBlocks( IReadOnlyList<TranscriptPart> parts, List<Blob> body )
	{
		var reads = new List<(string Key, JsonElement Block)>();
		void Flush()
		{
			if ( reads.Count > 0 )
				body.Add( BuildReadGroup( reads ) );
			reads = [];
		}

		foreach ( var part in parts )
		{
			var index = part.Start;
			foreach ( var block in ReadBlocks( part ) )
			{
				var key = $"{part.Message.Id:N}:{index++}";
				if ( IsReadCall( block ) )
				{
					reads.Add( (key, block) );
					continue;
				}

				// Blank text between two reads shows nothing, so it does not split their group.
				if ( reads.Count > 0 && ReadText( block, "type" ) == "text" && string.IsNullOrWhiteSpace( ReadText( block, "text" ) ) )
					continue;
				Flush();
				body.AddRange( BuildRichBlock( key, block, "assistant" ) );
			}
		}
		Flush();
	}

	/// <summary>Builds consecutive reads as a "Read N files" head over one chip per file; a chip opens its file, and its output once clicked again.</summary>
	private Container BuildReadGroup( List<(string Key, JsonElement Block)> reads )
	{
		var groupKey = reads[0].Key + ":reads";
		var chips = new List<Blob>();
		var outputs = new List<Blob>();
		var running = false;
		foreach ( var (key, block) in reads )
		{
			var id = ReadText( block, "id" );
			_shownTools.Add( id );
			var status = ComputeToolStatus( id, out var result );
			running |= status == "Running";
			var arguments = block.TryGetProperty( "arguments", out var args ) ? args : default;
			chips.Add( BuildFileChip( key, ComputeToolPath( arguments ), status ) );
			if ( _expanded.Contains( key ) && result is { } body && TerminalText.BuildPlain( ComputeResultText( body ) ) is { Length: > 0 } text )
				outputs.Add( BuildCodePreview( key + ":output", text, "text" ) );
		}

		var head = new Row
		{
			Key = "reads-head",
			AlignItems = AlignItems.Center,
			Gap = Theme.Current.Spacing,
			Children =
			[
				running ? ChatIcons.BuildSpinner( "reads-spinner" ) : ChatIcons.BuildIcon( ChatIcons.ComputeToolGlyph( "read" ) ),
				BuildToolTitle( "Read", Count( reads.Count, "file", "files" ) ),
			],
		};
		var children = new List<Blob>
		{
			head,
			new Row { Key = "reads-chips", FlexWrap = FlexWrap.Wrap, Gap = Theme.Current.Spacing / 2, Children = chips },
		};
		children.AddRange( outputs );
		return new Container
		{
			Key = groupKey,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = $"Read {Count( reads.Count, "file", "files" )}",
			Gap = Theme.Current.Spacing / 2,
			Children = children,
		};
	}

	/// <summary>Builds a tool's title: its name in bold, then what it did in the body color, such as "Read 3 files" or "Bash Checking versions".</summary>
	private static Text BuildToolTitle( string name, string detail ) => new( [
		new( name, new Style { FontWeight = 600, Color = Theme.Current.Text } ),
		new( detail.Length > 0 ? "  " + detail : "", new Style { Color = Theme.Current.Text } ),
	] )
	{
		FlexShrink = 1,
		MinWidth = 0,
		TextWrap = TextWrap.NoWrap,
		TextTrimming = TextTrimming.Ellipsis,
	};

	/// <summary>Builds one file a read opened as a rounded chip: a file icon, the path in green code font and any line range muted after it.</summary>
	/// <param name="key">The read's key; clicking the chip opens the file and shows or hides the read's output under the group.</param>
	/// <param name="path">The path or address read, with any line selector.</param>
	/// <param name="status">The read's status; a failed read shows its path in the error color.</param>
	private Button BuildFileChip( string key, string path, string status )
	{
		var theme = Theme.Current;
		var (target, selector) = SplitSelector( path );
		var shown = target;
		var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
		if ( home.Length > 0 && shown.StartsWith( home, StringComparison.OrdinalIgnoreCase ) )
			shown = "~" + shown[home.Length..];
		var color = status == "Failed" ? theme.Error : theme.AnsiGreen;
		var button = theme.BuildButton( shown, () => context.Run( () =>
		{
			UpdateExpanded( key );
			if ( _expanded.Contains( key ) && ComputeFileLink( target ) is { } link )
				OpenLink( link );
		} ) );
		button.Key = "chip:" + key;
		button.AccessibilityName = $"{shown}{selector}, {status}";
		button.FlexDirection = FlexDirection.Row;
		button.AlignItems = AlignItems.Center;
		button.Gap = theme.Spacing / 2;
		button.Height = ChipRadius * 2;
		button.Padding = new Edges( 0, theme.Spacing );
		button.BorderRadius = ChipRadius;
		button.BorderWidth = 0;
		button.BackgroundColor = theme.Raised;
		button.MinWidth = 0;
		button.MaxWidth = Length.Percent( 100 );
		button.Children =
		[
			status == "Running" ? ChatIcons.BuildSpinner( "chip-spinner" ) : ChatIcons.BuildIcon( "\uE7C3", theme.Muted ),
			new Text( [
				new( shown, new Style { FontFamily = ViewStyle.CodeFontFamily, FontSize = ViewStyle.SmallSize, Color = color } ),
				new( selector, new Style { FontFamily = ViewStyle.CodeFontFamily, FontSize = ViewStyle.SmallSize, Color = theme.Muted } ),
			] )
			{
				FlexShrink = 1,
				MinWidth = 0,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
		];
		return button;
	}

	/// <summary>Splits a read's target from its line selector, such as <c>view.cs:10-40</c>; a web address keeps its own colon.</summary>
	private static (string Target, string Selector) SplitSelector( string path )
	{
		var colon = path.LastIndexOf( ':' );
		return colon > 0 && colon < path.Length - 1 && path[(colon + 1)..].All( character => char.IsDigit( character ) || character is '-' or '+' or ',' )
			? (path[..colon], path[colon..])
			: (path, "");
	}

	/// <summary>Gets the link that opens what a read read: a web address as it is, a file in the editor, or null when there is nothing to open.</summary>
	private static Uri? ComputeFileLink( string target )
	{
		if ( target.Length == 0 )
			return null;
		if ( Uri.TryCreate( target, UriKind.Absolute, out var web ) && web.Scheme is "http" or "https" )
			return web;
		return target.Contains( "://", StringComparison.Ordinal ) ? null : new Uri( $"{LoknLinks.Create( "editor", "open" )}?path={Uri.EscapeDataString( target )}" );
	}

	/// <summary>Builds a shell command as a bold "Bash" with its intent, over a box with the highlighted command, its output and how it ended.</summary>
	private Container BuildBash( string key, string id, JsonElement arguments, string status, JsonElement? result, string intent )
	{
		var theme = Theme.Current;
		var command = ReadText( arguments, "command" );
		var mark = status switch
		{
			"Running" => ChatIcons.BuildSpinner( "bash-spinner" ),
			"Failed" => ChatIcons.BuildIcon( ChatIcons.ComputeToolGlyph( "bash" ), theme.Error ),
			_ => ChatIcons.BuildIcon( ChatIcons.ComputeToolGlyph( "bash" ) ),
		};
		var box = new List<Blob> { new Container { Key = "command", Padding = new Edges( theme.Spacing, theme.Spacing * 1.5, 0, theme.Spacing * 1.5 ), Children = [MarkdownView.BuildCode( command, "bash", framed: false, showLanguage: false )] } };
		if ( result is { } body && TerminalText.BuildPlain( ComputeResultText( body ) ).TrimEnd() is { Length: > 0 } output )
		{
			box.Add( new Container { Key = "output-rule", Height = theme.BorderWidth, BackgroundColor = theme.Divider } );
			box.Add( new Container { Key = "output", Padding = new Edges( 0, theme.Spacing * 1.5 ), Children = [BuildCodePreview( key + ":output", output, "text" )] } );
		}
		var (word, color) = status switch
		{
			"Running" => ("Running", theme.Muted),
			"Failed" => ("✗ Failed", theme.Error),
			"Requested" => ("Waiting", theme.Muted),
			_ => ("✓ Done", theme.AnsiGreen),
		};
		box.Add( new Text
		{
			Key = "outcome",
			Content = word,
			Color = color,
			FontSize = ViewStyle.SmallSize,
			Padding = new Edges( theme.Spacing / 2, theme.Spacing * 1.5, theme.Spacing, theme.Spacing * 1.5 ),
		} );

		return new Container
		{
			Key = key,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = $"bash tool {status}",
			Gap = theme.Spacing / 2,
			Children =
			[
				new Row { Key = "tool-head", AlignItems = AlignItems.Center, Gap = theme.Spacing, Children = [mark, BuildToolTitle( "Bash", intent )] },
				new Container
				{
					Key = "bash-box:" + id,
					Gap = theme.Spacing,
					BackgroundColor = theme.Panel,
					BorderWidth = theme.BorderWidth,
					BorderColor = theme.Border,
					BorderRadius = theme.Radius * 2,
					Overflow = Overflow.Hidden,
					Children = box,
				},
			],
		};
	}
}
