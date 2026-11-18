using Goo;

namespace Lokn.Modules.Git;

/// <summary>Holds the Git views' line icons: Lucide's outlines on a 24 by 24 grid, drawn with round strokes in any color and size.</summary>
/// <remarks>
/// Each icon's circles, rectangles and lines are written as one path, so it draws as a single <see cref="Shape"/>.
/// The outlines come from Lucide (https://lucide.dev), under the ISC License: Copyright (c) for portions of Lucide are held by Cole Bemis 2013-2022
/// as part of Feather (MIT); all other copyright (c) for Lucide are held by Lucide Contributors 2022. Permission to use, copy, modify, and/or
/// distribute this software for any purpose with or without fee is hereby granted, provided that the above copyright notice and this permission
/// notice appear in all copies. THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH REGARD TO THIS SOFTWARE.
/// </remarks>
internal static class GitIcons
{
	/// <summary>Defines the icons' grid size.</summary>
	private const double Grid = 24;

	/// <summary>Defines the stroke width on the grid; it scales with the icon.</summary>
	private const double Stroke = 2;

	/// <summary>Holds a branch: two nodes joined by a curve to a third.</summary>
	internal static readonly VectorPath Branch = Parse( "M6 3v12" + Circle( 18, 6, 3 ) + Circle( 6, 18, 3 ) + "M18 9a9 9 0 0 1-9 9" );

	/// <summary>Holds two turning arrows, for fetching.</summary>
	internal static readonly VectorPath Fetch = Parse( "M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8M21 3v5h-5M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16M8 16H3v5" );

	/// <summary>Holds an arrow down into a tray, for pulling.</summary>
	internal static readonly VectorPath Pull = Parse( "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M7 10l5 5 5-5M12 15V3" );

	/// <summary>Holds an arrow up out of a tray, for pushing.</summary>
	internal static readonly VectorPath Push = Parse( "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M17 8l-5-5-5 5M12 3v12" );

	/// <summary>Holds a box, for stashing.</summary>
	internal static readonly VectorPath Stash = Parse( Rectangle( 2, 3, 20, 5, 1 ) + "M4 8v11a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8M10 12h4" );

	/// <summary>Holds a box with an arrow out of it, for popping a stash.</summary>
	internal static readonly VectorPath Pop = Parse( Rectangle( 2, 3, 20, 5, 1 ) + "M4 8v11a2 2 0 0 0 2 2h2M20 8v11a2 2 0 0 1-2 2h-2M9 15l3-3 3 3M12 12v9" );

	/// <summary>Holds a speech bubble, for review in chat.</summary>
	internal static readonly VectorPath Chat = Parse( "M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z" );

	/// <summary>Holds one turning arrow, for reloading.</summary>
	internal static readonly VectorPath Reload = Parse( "M21 12a9 9 0 1 1-9-9c2.52 0 4.93 1 6.74 2.74L21 8M21 3v5h-5" );

	/// <summary>Holds a chevron pointing down.</summary>
	internal static readonly VectorPath ChevronDown = Parse( "M6 9l6 6 6-6" );

	/// <summary>Holds a chevron pointing up.</summary>
	internal static readonly VectorPath ChevronUp = Parse( "M18 15l-6-6-6 6" );

	/// <summary>Holds a globe, for a branch on a remote.</summary>
	internal static readonly VectorPath Globe = Parse( Circle( 12, 12, 10 ) + "M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20M2 12h20" );

	/// <summary>Holds a tag.</summary>
	internal static readonly VectorPath Tag = Parse( "M12.586 2.586A2 2 0 0 0 11.172 2H4a2 2 0 0 0-2 2v7.172a2 2 0 0 0 .586 1.414l8.704 8.704a2.426 2.426 0 0 0 3.42 0l6.58-6.58a2.426 2.426 0 0 0 0-3.42z" + Circle( 7.5, 7.5, 0.5 ) );

	/// <summary>Holds a pencil, for the working tree and for editing.</summary>
	internal static readonly VectorPath Pencil = Parse( "M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497zM15 5l4 4" );

	/// <summary>Holds a commit: a node on a line.</summary>
	internal static readonly VectorPath Commit = Parse( Circle( 12, 12, 3 ) + "M3 12h6M15 12h6" );

	/// <summary>Holds a cross, for closing.</summary>
	internal static readonly VectorPath Close = Parse( "M18 6 6 18M6 6l12 12" );

	/// <summary>Holds a plus, for staging.</summary>
	internal static readonly VectorPath Plus = Parse( "M5 12h14M12 5v14" );

	/// <summary>Holds a minus, for unstaging.</summary>
	internal static readonly VectorPath Minus = Parse( "M5 12h14" );

	/// <summary>Holds an arrow turning back, for discarding.</summary>
	internal static readonly VectorPath Discard = Parse( "M9 14 4 9l5-5M4 9h10.5a5.5 5.5 0 0 1 5.5 5.5 5.5 5.5 0 0 1-5.5 5.5H11" );

	/// <summary>Holds a page with a folded corner, for a file.</summary>
	internal static readonly VectorPath File = Parse( "M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7ZM14 2v4a2 2 0 0 0 2 2h4" );

	/// <summary>Holds two overlapping pages, for copying.</summary>
	internal static readonly VectorPath Copy = Parse( Rectangle( 8, 8, 14, 14, 2 ) + "M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2" );

	/// <summary>Holds a folder.</summary>
	internal static readonly VectorPath Folder = Parse( "M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z" );

	/// <summary>Holds three bulleted lines, for a flat list.</summary>
	internal static readonly VectorPath List = Parse( "M3 6h.01M3 12h.01M3 18h.01M8 6h13M8 12h13M8 18h13" );

	/// <summary>Builds an icon of a size in a color.</summary>
	/// <param name="icon">The icon.</param>
	/// <param name="size">The icon's width and height in logical pixels.</param>
	/// <param name="color">The stroke color.</param>
	internal static Shape Build( VectorPath icon, double size, Color color ) => new()
	{
		Path = icon,
		Width = size,
		Height = size,
		FlexShrink = 0,
		Fit = ShapeFit.Contain,
		Stroke = new VectorStroke( Stroke, color ) { Cap = StrokeCap.Round, Join = StrokeJoin.Round },
	};

	/// <summary>Parses path data on the icons' grid.</summary>
	private static VectorPath Parse( string data ) => VectorPath.Parse( data, new ElementRect { Width = Grid, Height = Grid } );

	/// <summary>Writes a circle as two arcs.</summary>
	private static string Circle( double x, double y, double radius ) =>
		FormattableString.Invariant( $"M{x - radius} {y}a{radius} {radius} 0 1 0 {radius * 2} 0a{radius} {radius} 0 1 0 {-radius * 2} 0Z" );

	/// <summary>Writes a rectangle with rounded corners.</summary>
	private static string Rectangle( double x, double y, double width, double height, double radius ) => FormattableString.Invariant(
		$"M{x + radius} {y}H{x + width - radius}a{radius} {radius} 0 0 1 {radius} {radius}V{y + height - radius}a{radius} {radius} 0 0 1 {-radius} {radius}H{x + radius}a{radius} {radius} 0 0 1 {-radius} {-radius}V{y + radius}a{radius} {radius} 0 0 1 {radius} {-radius}Z" );
}
