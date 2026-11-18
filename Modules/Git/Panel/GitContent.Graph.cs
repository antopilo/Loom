using System.Globalization;
using System.Text;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Holds one row's drawn graph: its line paths per lane color and its commit dot.</summary>
/// <param name="Color">The dot's lane color index.</param>
/// <param name="Lane">The dot's lane, which places the row's avatar, color band and branch connector.</param>
/// <param name="Width">The row's drawn width in logical pixels.</param>
/// <param name="Dot">The commit dot, a filled circle.</param>
/// <param name="Lines">The row's line segments, one path per lane color.</param>
internal sealed record GitGraphPaths( int Color, int Lane, double Width, VectorPath Dot, IReadOnlyList<(int Color, VectorPath Path)> Lines );

/// <summary>Holds a loaded log: its commits newest first, each row's drawn graph, and where each commit is.</summary>
/// <param name="Commits">The commits in log order.</param>
/// <param name="Graph">The drawn graph of each commit's row, in the same order.</param>
/// <param name="Lanes">The widest row's lane count.</param>
/// <param name="Index">Each commit's position by its full identifier.</param>
internal sealed record GitHistory( IReadOnlyList<GitCommit> Commits, IReadOnlyList<GitGraphPaths> Graph, int Lanes, IReadOnlyDictionary<string, int> Index )
{
	/// <summary>Defines a commit row's height in logical pixels.</summary>
	internal const double RowHeight = 26;

	/// <summary>Defines the distance between lane centers in logical pixels.</summary>
	internal const double LaneWidth = 16;

	/// <summary>Defines the commit dot radius in logical pixels.</summary>
	internal const double DotRadius = 4;

	/// <summary>Lays out the commits' graph and draws each row's paths; call it off the UI thread for long logs.</summary>
	internal static GitHistory Build( IReadOnlyList<GitCommit> commits )
	{
		var rows = GitGraph.Layout( commits );
		var graph = new GitGraphPaths[rows.Count];
		var lanes = 1;
		for ( var index = 0; index < rows.Count; index++ )
		{
			graph[index] = Draw( rows[index] );
			lanes = Math.Max( lanes, rows[index].Width );
		}

		var positions = new Dictionary<string, int>( commits.Count, StringComparer.OrdinalIgnoreCase );
		for ( var index = 0; index < commits.Count; index++ )
			positions.TryAdd( commits[index].Id, index );
		return new( commits, graph, lanes, positions );
	}

	/// <summary>Draws one row: its up segments from the top edge to the middle, its down segments from the middle to the bottom edge, curved where they change lane.</summary>
	private static GitGraphPaths Draw( GitGraphRow row )
	{
		var width = Math.Max( 1, row.Width ) * LaneWidth;
		var middle = RowHeight / 2;
		var paths = new SortedDictionary<int, StringBuilder>();
		foreach ( var line in row.Up )
			AddSegment( paths, line, 0, middle );
		foreach ( var line in row.Down )
			AddSegment( paths, line, middle, RowHeight );

		var box = new ElementRect { Width = width, Height = RowHeight };
		var center = ComputeX( row.Lane );
		var dot = string.Create( CultureInfo.InvariantCulture,
			$"M{center - DotRadius} {middle} A{DotRadius} {DotRadius} 0 1 0 {center + DotRadius} {middle} A{DotRadius} {DotRadius} 0 1 0 {center - DotRadius} {middle} Z" );
		return new( row.Color, row.Lane, width, VectorPath.Parse( dot, box ),
			[.. paths.Select( pair => (pair.Key, VectorPath.Parse( pair.Value.ToString(), box )) )] );
	}

	/// <summary>Adds one segment to its color's path, straight within a lane and an S-curve between lanes.</summary>
	private static void AddSegment( SortedDictionary<int, StringBuilder> paths, GitGraphLine line, double top, double bottom )
	{
		if ( !paths.TryGetValue( line.Color, out var path ) )
			paths.Add( line.Color, path = new StringBuilder() );

		var from = ComputeX( line.From );
		var to = ComputeX( line.To );
		var middle = (top + bottom) / 2;
		if ( from == to )
			path.Append( string.Create( CultureInfo.InvariantCulture, $"M{from} {top} L{to} {bottom} " ) );
		else
			path.Append( string.Create( CultureInfo.InvariantCulture, $"M{from} {top} C{from} {middle} {to} {middle} {to} {bottom} " ) );
	}

	/// <summary>Gets a lane's center in logical pixels from the row's left edge.</summary>
	internal static double ComputeX( int lane ) => lane * LaneWidth + LaneWidth / 2;
}

/// <summary>Draws the commit graph column and picks lane colors.</summary>
internal sealed partial class GitContent
{
	/// <summary>Defines the graph line thickness in logical pixels.</summary>
	private const double LineWidth = 1.6;

	/// <summary>Defines the most lanes the graph column shows; wider rows are cut off at its right edge.</summary>
	private const int MaxLanes = 16;

	/// <summary>Gets a lane color index's theme color, cycling through the accent and the ANSI colors.</summary>
	private static Color ComputeLaneColor( int index )
	{
		var theme = Theme.Current;
		Color[] colors = [theme.Accent, theme.AnsiBlue, theme.AnsiGreen, theme.AnsiMagenta, theme.AnsiYellow, theme.AnsiCyan, theme.AnsiRed];
		return colors[(index % colors.Length + colors.Length) % colors.Length];
	}

	/// <summary>Gets the graph column width for the loaded log.</summary>
	private double ComputeGraphWidth() => Math.Min( _history?.Lanes ?? 1, MaxLanes ) * GitHistory.LaneWidth + Theme.Current.Spacing / 2.0;

	/// <summary>Builds one row's graph cell: a shape per line color, then the commit dot, hollow for the checked-out commit.</summary>
	private static Container BuildGraph( GitGraphPaths graph, double width, bool isHead )
	{
		var layers = new List<Blob>();
		foreach ( var (color, path) in graph.Lines )
			layers.Add( BuildLayer( path, graph.Width, new Shape { Stroke = new VectorStroke( LineWidth, ComputeLaneColor( color ) ) { Cap = StrokeCap.Round } } ) );

		var dotColor = ComputeLaneColor( graph.Color );
		layers.Add( BuildLayer( graph.Dot, graph.Width, isHead
			? new Shape { Fill = new VectorPaint( Theme.Current.Panel ), Stroke = new VectorStroke( 2, dotColor ) }
			: new Shape { Fill = new VectorPaint( dotColor ) } ) );
		return new Container
		{
			Width = width,
			Height = GitHistory.RowHeight,
			FlexShrink = 0,
			Overflow = Overflow.Hidden,
			Children = layers,
		};
	}

	/// <summary>Places one shape over the whole row at its drawn size, so its view box maps one to one.</summary>
	private static Shape BuildLayer( VectorPath path, double width, Shape shape )
	{
		shape.Path = path;
		shape.Fit = ShapeFit.Fill;
		shape.Position = PositionType.Absolute;
		shape.Left = 0;
		shape.Top = 0;
		shape.Width = width;
		shape.Height = GitHistory.RowHeight;
		return shape;
	}
}
