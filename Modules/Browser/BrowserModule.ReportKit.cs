using System.Text.RegularExpressions;

namespace Lokn.Modules.Browser;

/// <summary>Gives the html reports agents show Loom's report kit: a stylesheet, a script and fonts that make a plain report look like a printed page,
/// with charts, a style menu, exports and comments that go back to the chat (<c>Reports\loom-report.css</c> and <c>loom-report.js</c>).</summary>
/// <remarks>The kit ships inside the module and is written to <c>reports\kit</c> beside the reports; each saved report links it before its own styles,
/// so a report's own CSS still wins. A report that links the kit itself is left as it is.</remarks>
public sealed partial class BrowserModule
{
	/// <summary>Defines the prefix of the kit's files among the module's resources.</summary>
	private const string KitResources = "Kit/";

	/// <summary>Defines the tags a report gets to load the kit, relative to the reports folder.</summary>
	private const string KitTags = """<link rel="stylesheet" href="kit/loom-report.css"><script src="kit/loom-report.js" defer></script>""";

	/// <summary>Finds the start of a document's head.</summary>
	[GeneratedRegex( @"<head\b[^>]*>", RegexOptions.IgnoreCase )]
	private static partial Regex HeadStart();

	/// <summary>Finds the start of a document's html element, for a document without a head.</summary>
	[GeneratedRegex( @"<html\b[^>]*>", RegexOptions.IgnoreCase )]
	private static partial Regex HtmlStart();

	/// <summary>Gets the folder the kit is written to, beside the reports.</summary>
	internal string KitPath => Path.Combine( ReportsPath, "kit" );

	/// <summary>Writes the kit's files beside the reports, rewriting only those that differ, such as after the module updated.</summary>
	private void EnsureKit()
	{
		var assembly = typeof( BrowserModule ).Assembly;
		foreach ( var name in assembly.GetManifestResourceNames().Where( name => name.StartsWith( KitResources, StringComparison.Ordinal ) ) )
		{
			using var stream = assembly.GetManifestResourceStream( name ) ?? throw new InvalidOperationException( $"The Browser module lacks {name}." );
			var bytes = new byte[stream.Length];
			stream.ReadExactly( bytes );
			var path = Path.Combine( KitPath, name[KitResources.Length..].Replace( '/', Path.DirectorySeparatorChar ) );
			if ( File.Exists( path ) && new FileInfo( path ).Length == bytes.Length && File.ReadAllBytes( path ).AsSpan().SequenceEqual( bytes ) )
				continue;

			Directory.CreateDirectory( Path.GetDirectoryName( path )! );
			File.WriteAllBytes( path, bytes );
		}
	}

	/// <summary>Links the kit at the start of a report's head, before its own styles, unless it links the kit already.</summary>
	private static string AddKit( string html )
	{
		if ( html.Contains( "loom-report.css", StringComparison.OrdinalIgnoreCase ) )
			return html;

		var start = HeadStart().Match( html ) is { Success: true } head ? head : HtmlStart().Match( html );
		return start.Success ? html.Insert( start.Index + start.Length, KitTags ) : KitTags + html;
	}
}
