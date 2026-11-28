using Goo;

namespace Lokn.Modules.Conversations;

/// <summary>Holds the painting of each weekday that Home and a project's overview show.</summary>
/// <remarks>The paintings are public domain works from the National Gallery of Denmark (SMK, open.smk.dk), shipped with the module as
/// <c>Home\Paintings\&lt;weekday&gt;.jpg</c>.</remarks>
public static class DailyPainting
{
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

	/// <summary>Gets the painting shown on a day.</summary>
	public static Painting ForDay( DayOfWeek day ) => Paintings[(int)day];

	/// <summary>Decodes a painting from the module's resources; the caller owns the source and disposes it.</summary>
	public static ImageSource Decode( Painting painting )
	{
		using var stream = typeof( DailyPainting ).Assembly.GetManifestResourceStream( $"Paintings.{painting.Resource}.jpg" )
			?? throw new InvalidOperationException( $"The Conversations module lacks its painting {painting.Resource}." );
		return ImageSource.Decode( stream );
	}
}

/// <summary>Holds one painting of the day and its credit.</summary>
/// <param name="Resource">The weekday its file is named after.</param>
/// <param name="Title">The work's title in English.</param>
/// <param name="Artist">Who painted it.</param>
/// <param name="Year">When it was painted.</param>
/// <param name="Medium">What it is painted with and on.</param>
public sealed record Painting( string Resource, string Title, string Artist, string Year, string Medium )
{
	/// <summary>Gets the credit printed under the painting, as a museum label reads.</summary>
	public string Credit => $"{Title}, {Artist}, {Year}, {Medium}. SMK, public domain";
}
