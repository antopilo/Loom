using Goo;
using Lokn.Core;

namespace Lokn.Modules.Overview;

/// <summary>Holds the look of a project's brief: the painting of each weekday, its serif faces, and the paper and ink it is printed in.</summary>
/// <remarks>The paintings are public domain works from the National Gallery of Denmark (SMK, open.smk.dk), shipped with the module as
/// <c>Paintings\&lt;weekday&gt;.jpg</c>. The faces are Fraunces, roman and italic, under the SIL Open Font License (<c>Fonts\OFL-Fraunces.txt</c>),
/// set soft and without its wonky letters as in the sidebar and in Loom's report kit.</remarks>
internal static class BriefArt
{
	/// <summary>Defines the registered family of the hero title's face: Fraunces at its largest optical size.</summary>
	private const string DisplayFamily = "Lokn Brief Display";

	/// <summary>Defines the registered family of the page's other serif lines: Fraunces at a text optical size.</summary>
	private const string SerifFamily = "Lokn Brief Serif";

	/// <summary>Lists the paintings by <see cref="DayOfWeek"/>, Sunday first.</summary>
	private static readonly Painting[] Paintings =
	[
		new( "sunday", "Mountain Landscape in Venosta with Coira Castle and the Ortles", "Jørgen V. Sonne", "1829–31", "oil on paper mounted on cardboard" ),
		new( "monday", "The Cliffs of Møn", "Louis Gurlitt", "1842", "oil on canvas" ),
		new( "tuesday", "The Bay of Naples with Vesuvius", "Christen Købke", "1838–39", "oil on paper mounted on canvas" ),
		new( "wednesday", "The Lyseklippen Rock at Rø, Bornholm", "Vilhelm Kyhn", "1843", "oil on canvas" ),
		new( "thursday", "The Adige Valley near Rovereto", "J.C. Dahl", "1824", "oil on canvas" ),
		new( "friday", "The Bay by the Lime Kiln. Quiet Summer Afternoon", "Christen Købke", "1837", "oil on canvas" ),
		new( "saturday", "View over Lake Skarre", "P.C. Skovgaard", "1845", "oil on canvas" ),
	];

	/// <summary>Retains the registered faces for as long as the module is loaded: the display face upright and italic, the text face upright and italic.</summary>
	private static readonly FontSource[] Fonts =
	[
		Register( DisplayFamily, 500, false, "Fraunces.ttf", 144 ),
		Register( DisplayFamily, 500, true, "FrauncesItalic.ttf", 144 ),
		Register( SerifFamily, 400, false, "Fraunces.ttf", 24 ),
		Register( SerifFamily, 400, true, "FrauncesItalic.ttf", 24 ),
	];

	/// <summary>Gets the hero title's face, weight 500, with the app's face and its symbol and emoji fallbacks after it.</summary>
	public static string DisplayFontFamily => $"{Fonts[0].Family}, {ViewStyle.FontFamily}";

	/// <summary>Gets the face of section titles, the summary and the margins, weight 400, with the app's face after it.</summary>
	public static string SerifFontFamily => $"{Fonts[2].Family}, {ViewStyle.FontFamily}";

	/// <summary>Gets the painting shown on a day.</summary>
	public static Painting ForDay( DayOfWeek day ) => Paintings[(int)day];

	/// <summary>Decodes a painting from the module's resources; the caller owns the source and disposes it.</summary>
	public static ImageSource Decode( Painting painting )
	{
		using var stream = typeof( BriefArt ).Assembly.GetManifestResourceStream( $"Paintings.{painting.Resource}.jpg" )
			?? throw new InvalidOperationException( $"The Overview module lacks its painting {painting.Resource}." );
		return ImageSource.Decode( stream );
	}

	/// <summary>Registers one fixed instance of a Fraunces face from the module's resources.</summary>
	private static FontSource Register( string family, int weight, bool italic, string file, float opticalSize )
	{
		using var stream = typeof( BriefArt ).Assembly.GetManifestResourceStream( $"Fonts.{file}" )
			?? throw new InvalidOperationException( $"The Overview module lacks its font {file}." );
		var bytes = new byte[stream.Length];
		stream.ReadExactly( bytes );
		var font = new FontSource( family, weight, italic, bytes, 0, [new( "SOFT", 100 ), new( "WONK", 0 ), new( "opsz", opticalSize ), new( "wght", weight )] );
		font.Register();
		return font;
	}
}

/// <summary>Holds one painting of the brief and its credit.</summary>
/// <param name="Resource">The weekday its file is named after.</param>
/// <param name="Title">The work's title in English.</param>
/// <param name="Artist">Who painted it.</param>
/// <param name="Year">When it was painted.</param>
/// <param name="Medium">What it is painted with and on.</param>
internal sealed record Painting( string Resource, string Title, string Artist, string Year, string Medium )
{
	/// <summary>Gets the credit printed under the painting, as a museum label reads.</summary>
	public string Credit => $"{Title}, {Artist}, {Year}, {Medium}. SMK, public domain";
}

/// <summary>Holds the paper and ink of the brief, the tokens of Loom's report kit (<c>Modules\Browser\Reports\loom-report.css</c>): its dark "ink" paper
/// under a dark theme, its white "paper" under a light one.</summary>
/// <param name="Paper">The page.</param>
/// <param name="Ink">Titles and main text.</param>
/// <param name="Ink2">Secondary text.</param>
/// <param name="Ink3">Faint text: dates, credits.</param>
/// <param name="Rule">Hairlines and outlines.</param>
/// <param name="Card">The tint of a section's card on the page.</param>
/// <param name="Hover">The fill of a row under the pointer.</param>
internal sealed record BriefPaper( Color Paper, Color Ink, Color Ink2, Color Ink3, Color Rule, Color Card, Color Hover )
{
	/// <summary>Defines the report kit's dark paper.</summary>
	private static readonly BriefPaper Dark = Create( Color.Parse( "#1f1d1a" ), Color.Parse( "#efe8dc" ), Color.Parse( "#b3a99b" ), Color.Parse( "#7d7468" ) );

	/// <summary>Defines the report kit's white paper.</summary>
	private static readonly BriefPaper Light = Create( Color.Parse( "#f8f7f4" ), Color.Parse( "#222222" ), Color.Parse( "#666665" ), Color.Parse( "#9e9e9c" ) );

	/// <summary>Gets the paper that fits the applied theme, and subscribes the building view to theme changes.</summary>
	public static BriefPaper Current
	{
		get
		{
			var panel = Theme.Current.Panel;
			return 0.2126 * panel.R + 0.7152 * panel.G + 0.0722 * panel.B > 0.5 ? Light : Dark;
		}
	}

	/// <summary>Derives the hairline, card and hover tints from the ink.</summary>
	private static BriefPaper Create( Color paper, Color ink, Color ink2, Color ink3 ) =>
		new( paper, ink, ink2, ink3, ink.WithAlpha( 0.22 ), ink.WithAlpha( 0.045 ), ink.WithAlpha( 0.08 ) );
}
