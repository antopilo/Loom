using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Shows the What's new panel after an app update, displaying the changelog for the current version.</summary>
internal sealed class WhatsNewPanel : PanelContent
{
	/// <summary>Holds the version and changelog to display.</summary>
	private record WhatsNewInput( string Version, string Changelog );

	private readonly string _version;
	private readonly string _changelog;


	private WhatsNewPanel( string version, string changelog )
	{
		_version = version;
		_changelog = changelog;
	}

	/// <summary>Factory for the panel, called by the docking system.</summary>
	internal static PanelContent Create( PanelContext context )
	{
		if ( context.Input is WhatsNewInput { Version: var version, Changelog: var changelog } )
		{
			return new WhatsNewPanel( version, changelog );
		}
		// Fallback if input is missing (should not happen).
		return new WhatsNewPanel( "?", "" );
	}

	public override Blob Build()
	{
		if ( string.IsNullOrEmpty( _changelog ) )
		{
			return new Text { Content = "What's new", Color = Theme.Current.Text };
		}

		var theme = Theme.Current;
		var blobs = ParseChangelog( _changelog, _version, theme );
		return new Container
		{
			Key = "whats-new",
			Padding = new Edges( theme.Spacing * 2 ),
			Gap = theme.Spacing,
			Children = blobs,
		};
	}

	/// <summary>Attempts to load and open the What's new panel if this is an upgrade; silently does nothing if conditions are not met.</summary>
	internal static void TryOpenOnStartup( LoknApplication app )
	{
		if ( DataUpgrade.ReadUpgradedFrom( LoknPaths.DataRoot ) is null )
			return; // Not an upgrade.

		var version = typeof( WhatsNewPanel ).Assembly.GetName().Version?.ToString( 3 );
		if ( version is null )
			return;

		var changelogPath = Path.Combine( AppContext.BaseDirectory, "CHANGELOG.md" );
		if ( !File.Exists( changelogPath ) )
			return;

		try
		{
			var content = File.ReadAllText( changelogPath );
			if ( ExtractVersionSection( content, version ) is { Length: > 0 } section )
			{
				// Mark as shown immediately.
				DataUpgrade.DismissUpgradedFrom( LoknPaths.DataRoot );
				// Open the panel with the extracted changelog as input.
				var input = new WhatsNewInput( version, section );
				app.Panels.Open( "updates.whats-new", input: input );
			}
		}
		catch ( Exception error )
		{
			// Log but don't crash.
			UpdateLog( $"FAILED to show what's new: {error.Message}" );
		}
	}

	/// <summary>Extracts the markdown section for a version, from its heading to the next version heading or end of file.</summary>
	private static string ExtractVersionSection( string content, string version )
	{
		var versionHeading = $"## {version}";
		var startIndex = content.IndexOf( versionHeading, StringComparison.Ordinal );
		if ( startIndex == -1 )
			return "";

		var sectionStart = startIndex + versionHeading.Length;
		if ( sectionStart < content.Length && content[sectionStart] == '\n' )
			sectionStart++;

		// Find the next version heading.
		var nextHeadingIndex = content.IndexOf( "\n## ", sectionStart, StringComparison.Ordinal );
		var sectionEnd = nextHeadingIndex == -1 ? content.Length : nextHeadingIndex;

		return content[sectionStart..sectionEnd].Trim();
	}

	/// <summary>Parses the changelog markdown section into displayable Goo blobs.</summary>
	private static List<Blob> ParseChangelog( string section, string version, Theme theme )
	{
		var blobs = new List<Blob>();

		// Title
		blobs.Add( new Text
		{
			Key = "title",
			Content = $"What's new in Loom {version}",
			FontSize = 20,
			FontWeight = 600,
			Margin = new Edges( 0, 0, theme.Spacing * 2, 0 ),
		} );

		// Parse bullet points.
		var lines = section.Split( new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None );
		foreach ( var line in lines )
		{
			var trimmed = line.TrimEnd();
			if ( trimmed.StartsWith( "- ", StringComparison.Ordinal ) )
			{
				var text = trimmed[2..].Trim();
				blobs.Add( BuildBulletPoint( text, theme ) );
			}
		}

		return blobs;
	}

	/// <summary>Builds a bullet point, parsing **bold text** into styled segments.</summary>
	private static Blob BuildBulletPoint( string text, Theme theme )
	{
		var container = new Container
		{
			Key = $"bullet-{Guid.NewGuid():N}",
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.FlexStart,
			Gap = theme.Spacing,
			Margin = new Edges( 0, 0, theme.Spacing, 0 ),
		};

		container.Add( new Text
		{
			Content = "•",
			FontSize = theme.FontSize,
			FlexShrink = 0,
		} );

		var textContainer = new Container
		{
			Key = "text",
			FlexDirection = FlexDirection.Row,
			FlexWrap = FlexWrap.Wrap,
			FlexShrink = 1,
			AlignItems = AlignItems.Center,
			Gap = 2,
		};

		var segments = ParseInlineBold( text );
		foreach ( var (segment, isBold) in segments )
		{
			textContainer.Add( new Text
			{
				Content = segment,
				FontSize = theme.FontSize,
				FontWeight = isBold ? 600 : 400,
				Color = isBold ? theme.Accent : theme.Text,
			} );
		}

		container.Add( textContainer );
		return container;
	}

	/// <summary>Parses inline **bold text** markers into (text, isBold) tuples.</summary>
	private static List<(string, bool)> ParseInlineBold( string text )
	{
		var result = new List<(string, bool)>();
		var index = 0;

		while ( index < text.Length )
		{
			var boldStart = text.IndexOf( "**", index, StringComparison.Ordinal );
			if ( boldStart == -1 )
			{
				if ( index < text.Length )
					result.Add( (text[index..], false) );
				break;
			}

			if ( boldStart > index )
				result.Add( (text[index..boldStart], false) );

			var boldEnd = text.IndexOf( "**", boldStart + 2, StringComparison.Ordinal );
			if ( boldEnd == -1 )
			{
				result.Add( (text[(boldStart + 2)..], false) );
				break;
			}

			result.Add( (text[(boldStart + 2)..boldEnd], true) );
			index = boldEnd + 2;
		}

		return result;
	}

	/// <summary>Appends one timestamped line to the updates log; a logging failure is ignored.</summary>
	private static void UpdateLog( string line )
	{
		try
		{
			var path = Path.Combine( LoknPaths.TempRoot, "updates.log" );
			Directory.CreateDirectory( Path.GetDirectoryName( path )! );
			File.AppendAllText( path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}" );
		}
		catch ( IOException )
		{
		}
	}
}
