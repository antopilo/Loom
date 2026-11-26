using Goo;
using Lokn.Core;

namespace Lokn.Modules.Markdown;

/// <summary>Draws Mermaid flowcharts and sequence diagrams natively: shapes as vector paths, labels as text, in the theme's colors.</summary>
/// <remarks>Other diagram types, and sources it cannot read, return null so the caller shows the code instead.</remarks>
internal static class MermaidView
{
	/// <summary>Defines the label text size in logical pixels.</summary>
	private const double FontSize = 13;

	/// <summary>Defines the label line height as a multiple of its size.</summary>
	private const double LineHeight = 1.35;

	/// <summary>Defines the estimated advance of one character in ems.</summary>
	private const double CharacterEms = 0.58;

	/// <summary>Defines the line width of shapes and edges, in logical pixels.</summary>
	private const double StrokeWidth = 1.5;

	/// <summary>Defines the size of an arrowhead, in logical pixels.</summary>
	private const double ArrowSize = 8;

	/// <summary>Defines how many diagrams stay cached by source before the cache starts over.</summary>
	private const int MaxCached = 32;

	/// <summary>Holds laid-out diagrams by source, a flowchart, a sequence diagram or null for one it does not read, so a message that rebuilds
	/// while it streams does not lay its diagram out again; on the UI thread. Drawing is cheap and follows the current theme, so it is not cached.</summary>
	private static readonly Dictionary<string, object?> _cache = new( StringComparer.Ordinal );

	/// <summary>Builds a diagram from Mermaid source, or null for a type or source it does not read.</summary>
	/// <param name="source">The Mermaid source, without its fence.</param>
	public static Blob? Build( string source )
	{
		if ( !_cache.TryGetValue( source, out var layout ) )
		{
			if ( _cache.Count >= MaxCached )
				_cache.Clear();
			_cache[source] = layout = Lay( source );
		}
		return layout switch
		{
			MermaidSequence sequence => DrawSequence( sequence ),
			MermaidFlowchart flowchart => DrawFlowchart( flowchart ),
			_ => null,
		};
	}

	/// <summary>Parses and lays out a diagram by its type, or returns null for a type or source it does not read.</summary>
	private static object? Lay( string source )
	{
		try
		{
			var first = source.Replace( "\r\n", "\n" ).Split( '\n' ).Select( line => line.Trim() ).FirstOrDefault( line => line.Length > 0 && !line.StartsWith( "%%", StringComparison.Ordinal ) ) ?? "";
			if ( first.StartsWith( "sequenceDiagram", StringComparison.OrdinalIgnoreCase ) )
				return MermaidSequence.Build( source, Measure );
			if ( first.StartsWith( "flowchart", StringComparison.OrdinalIgnoreCase ) || first.StartsWith( "graph", StringComparison.OrdinalIgnoreCase ) )
				return MermaidFlowchart.Build( source, Measure );
			return null;
		}
		catch ( Exception exception ) when ( exception is ArgumentException or InvalidOperationException or IndexOutOfRangeException )
		{
			// A source still streaming, or one this reader gets wrong, shows as code.
			return null;
		}
	}

	/// <summary>Estimates a label's width and height from its longest line and its line count.</summary>
	private static (double Width, double Height) Measure( string text )
	{
		var lines = text.Split( '\n' );
		return (Math.Max( 1, lines.Max( line => line.Length ) ) * CharacterEms * FontSize, lines.Length * FontSize * LineHeight);
	}

	/// <summary>Draws a flowchart: subgraphs, then edges with their marks, then nodes, then labels.</summary>
	private static Blob DrawFlowchart( MermaidFlowchart chart )
	{
		var theme = Theme.Current;
		var children = new List<Blob>();
		var (width, height) = (chart.Width, chart.Height);

		var groups = new PathBuilder( 0, 0, width, height );
		foreach ( var subgraph in chart.Subgraphs.Where( subgraph => subgraph.Right > subgraph.Left ) )
			groups.RoundedRectangle( subgraph.Left, subgraph.Top, subgraph.Right - subgraph.Left, subgraph.Bottom - subgraph.Top, 6 );
		children.Add( Layer( groups, width, height, new Shape { Fill = new VectorPaint( theme.Text.WithAlpha( 0.03 ) ), Stroke = new VectorStroke( 1, theme.Border ) } ) );
		foreach ( var subgraph in chart.Subgraphs.Where( subgraph => subgraph.Right > subgraph.Left ) )
			children.Add( Label( $"group:{subgraph.Id}", subgraph.Title, subgraph.Left + 10, subgraph.Top + 6, subgraph.Right - subgraph.Left - 20, theme.Muted, TextAlign.Left ) );

		var lines = new Dictionary<FlowLine, PathBuilder>();
		var heads = new PathBuilder( 0, 0, width, height );
		var openHeads = new PathBuilder( 0, 0, width, height );
		foreach ( var edge in chart.Edges )
		{
			var points = Clip( edge );
			if ( points.Count < 2 )
				continue;
			if ( !lines.TryGetValue( edge.Line, out var path ) )
				lines[edge.Line] = path = new PathBuilder( 0, 0, width, height );
			AddCurve( path, points );
			AddMark( edge.Head, points[^1], points[^2], heads, openHeads );
			AddMark( edge.Tail, points[0], points[1], heads, openHeads );
		}
		foreach ( var (line, path) in lines )
		{
			var stroke = new VectorStroke( line == FlowLine.Thick ? StrokeWidth * 2 : StrokeWidth, theme.Muted ) { Cap = StrokeCap.Round, Join = StrokeJoin.Round };
			if ( line == FlowLine.Dotted )
				stroke.Dashes = new DashPattern( [3, 4], 0 );
			children.Add( Layer( path, width, height, new Shape { Stroke = stroke } ) );
		}
		children.Add( Layer( heads, width, height, new Shape { Fill = new VectorPaint( theme.Muted ) } ) );
		children.Add( Layer( openHeads, width, height, new Shape { Fill = new VectorPaint( theme.Canvas ), Stroke = new VectorStroke( StrokeWidth, theme.Muted ) } ) );

		var outlines = new PathBuilder( 0, 0, width, height );
		foreach ( var node in chart.Nodes )
			AddNodeShape( outlines, node );
		children.Add( Layer( outlines, width, height, new Shape { Fill = new VectorPaint( theme.Raised ), Stroke = new VectorStroke( StrokeWidth, theme.Accent.WithAlpha( 0.8 ) ) { Join = StrokeJoin.Round } } ) );
		foreach ( var node in chart.Nodes )
		{
			var (_, textHeight) = Measure( node.Label );
			// A cylinder's text sits below its top cap.
			var capShift = node.Shape == FlowShape.Database ? 4 : 0;
			children.Add( Label( $"node:{node.Id}", node.Label, node.X - node.Width / 2, node.Y - textHeight / 2 + capShift, node.Width, theme.Text, TextAlign.Center ) );
		}

		foreach ( var (edge, index) in chart.Edges.Select( ( edge, index ) => (edge, index) ) )
		{
			if ( edge.Label.Length == 0 || edge.Points.Count < 2 )
				continue;
			var (x, y) = Middle( edge.Points );
			var (textWidth, textHeight) = Measure( edge.Label );
			children.Add( Label( $"edge:{index}", edge.Label, x - textWidth / 2 - 4, y - textHeight / 2, textWidth + 8, theme.Text, TextAlign.Center, theme.Canvas ) );
		}

		return Frame( children, width, height, "Flowchart" );
	}

	/// <summary>Draws a sequence diagram: frames, lifelines, participants, notes, then messages.</summary>
	private static Blob DrawSequence( MermaidSequence diagram )
	{
		var theme = Theme.Current;
		var children = new List<Blob>();
		var (width, height) = (diagram.Width, diagram.Height);

		// Frames first, so everything else draws over them.
		var frames = new PathBuilder( 0, 0, width, height );
		var dividers = new PathBuilder( 0, 0, width, height );
		var labelIndex = 0;
		foreach ( var row in diagram.Rows )
		{
			if ( row.Kind == MermaidSequence.RowKind.FrameStart )
			{
				var end = diagram.Rows.FirstOrDefault( other => other.Kind == MermaidSequence.RowKind.FrameEnd && other.Frame == row );
				var bottom = end is null ? diagram.FooterTop - 6 : end.Top + end.Height;
				frames.Rectangle( row.Span.Left, row.Top, row.Span.Right - row.Span.Left, bottom - row.Top );
				children.Add( Label( $"frame:{labelIndex++}", row.Text, row.Span.Left + 6, row.Top + 3, row.Span.Right - row.Span.Left - 12, theme.Accent, TextAlign.Left ) );
			}
			else if ( row.Kind == MermaidSequence.RowKind.FrameDivider )
			{
				dividers.MoveTo( row.Span.Left, row.Top ).LineTo( row.Span.Right, row.Top );
				children.Add( Label( $"frame:{labelIndex++}", $"[{row.Text}]", row.Span.Left + 6, row.Top + 3, row.Span.Right - row.Span.Left - 12, theme.Muted, TextAlign.Left ) );
			}
		}
		children.Insert( 0, Layer( frames, width, height, new Shape { Fill = new VectorPaint( theme.Text.WithAlpha( 0.03 ) ), Stroke = new VectorStroke( 1, theme.Border ) } ) );
		children.Add( Layer( dividers, width, height, new Shape { Stroke = new VectorStroke( 1, theme.Border ) { Dashes = new DashPattern( [4, 4], 0 ) } } ) );

		var lifelines = new PathBuilder( 0, 0, width, height );
		var top = 16 + diagram.HeaderHeight;
		foreach ( var participant in diagram.Participants )
			lifelines.MoveTo( participant.X, top ).LineTo( participant.X, diagram.FooterTop );
		children.Add( Layer( lifelines, width, height, new Shape { Stroke = new VectorStroke( 1, theme.Border ) { Dashes = new DashPattern( [4, 4], 0 ) } } ) );

		var boxes = new PathBuilder( 0, 0, width, height );
		var actors = new PathBuilder( 0, 0, width, height );
		var names = new List<Blob>();
		foreach ( var participant in diagram.Participants )
		{
			foreach ( var y in new[] { 16.0, diagram.FooterTop } )
			{
				var boxTop = y + diagram.HeaderHeight - participant.Height;
				if ( participant.IsActor )
				{
					// A stick figure above the name.
					var cx = participant.X;
					actors.Ellipse( cx - 5, boxTop, 10, 10 );
					actors.MoveTo( cx, boxTop + 10 ).LineTo( cx, boxTop + 18 ).MoveTo( cx - 7, boxTop + 13 ).LineTo( cx + 7, boxTop + 13 );
					actors.MoveTo( cx, boxTop + 18 ).LineTo( cx - 6, boxTop + 24 ).MoveTo( cx, boxTop + 18 ).LineTo( cx + 6, boxTop + 24 );
					names.Add( Label( $"actor:{participant.Id}:{y}", participant.Name, cx - participant.Width / 2, boxTop + 26, participant.Width, theme.Text, TextAlign.Center ) );
				}
				else
				{
					boxes.RoundedRectangle( participant.X - participant.Width / 2, boxTop, participant.Width, participant.Height, 4 );
					var (_, textHeight) = Measure( participant.Name );
					names.Add( Label( $"participant:{participant.Id}:{y}", participant.Name, participant.X - participant.Width / 2, boxTop + (participant.Height - textHeight) / 2, participant.Width, theme.Text, TextAlign.Center ) );
				}
			}
		}
		children.Add( Layer( boxes, width, height, new Shape { Fill = new VectorPaint( theme.Raised ), Stroke = new VectorStroke( StrokeWidth, theme.Accent.WithAlpha( 0.8 ) ) } ) );
		children.Add( Layer( actors, width, height, new Shape { Stroke = new VectorStroke( StrokeWidth, theme.Accent.WithAlpha( 0.8 ) ) { Cap = StrokeCap.Round } } ) );
		children.AddRange( names );

		var notes = new PathBuilder( 0, 0, width, height );
		var solid = new PathBuilder( 0, 0, width, height );
		var dashed = new PathBuilder( 0, 0, width, height );
		var heads = new PathBuilder( 0, 0, width, height );
		var openHeads = new PathBuilder( 0, 0, width, height );
		foreach ( var (row, index) in diagram.Rows.Select( ( row, index ) => (row, index) ) )
		{
			if ( row is { Kind: MermaidSequence.RowKind.Note, From: { } first, To: { } last } )
			{
				var (textWidth, textHeight) = Measure( row.Text );
				var noteWidth = textWidth + 20;
				var left = row.Placement switch
				{
					"left" => first.X - 10 - noteWidth,
					"right" => last.X + 10,
					_ => Math.Min( first.X, last.X ) - 20,
				};
				if ( row.Placement == "over" )
					noteWidth = Math.Max( noteWidth, Math.Abs( last.X - first.X ) + 40 );
				if ( row.Placement == "over" && first == last )
					left = first.X - noteWidth / 2;
				notes.Rectangle( left, row.Top, noteWidth, row.Height );
				children.Add( Label( $"note:{index}", row.Text, left, row.Top + (row.Height - textHeight) / 2, noteWidth, theme.Text, TextAlign.Center ) );
				continue;
			}
			if ( row is not { Kind: MermaidSequence.RowKind.Message, From: { } from, To: { } to } )
				continue;

			var path = row.IsDashed ? dashed : solid;
			var text = row.Number > 0 ? $"{row.Number}. {row.Text}" : row.Text;
			var (labelWidth, labelHeight) = Measure( text );
			var lineY = row.Top + row.Height - 6;
			if ( from == to )
			{
				// A message to itself leaves to the right and comes back lower.
				var loopTop = row.Top + labelHeight + 4;
				path.MoveTo( from.X, loopTop ).LineTo( from.X + 34, loopTop ).LineTo( from.X + 34, lineY ).LineTo( from.X + 2, lineY );
				AddArrow( row.Head, (from.X, lineY), (from.X + 34, lineY), heads, openHeads );
				children.Add( Label( $"message:{index}", text, from.X + 6, row.Top, labelWidth + 8, theme.Text, TextAlign.Left ) );
				continue;
			}
			var direction = Math.Sign( to.X - from.X );
			path.MoveTo( from.X, lineY ).LineTo( to.X - direction * 2, lineY );
			AddArrow( row.Head, (to.X, lineY), (from.X, lineY), heads, openHeads );
			var middle = (from.X + to.X) / 2;
			children.Add( Label( $"message:{index}", text, middle - labelWidth / 2 - 4, lineY - labelHeight - 3, labelWidth + 8, theme.Text, TextAlign.Center ) );
		}
		children.Add( Layer( notes, width, height, new Shape { Fill = new VectorPaint( theme.AnsiYellow.WithAlpha( 0.18 ) ), Stroke = new VectorStroke( 1, theme.AnsiYellow.WithAlpha( 0.6 ) ) } ) );
		// Labels drawn so far sit under the messages' lines; keep notes' text over their boxes by moving them after.
		var noteLabels = children.Where( child => child.Key is { } key && key.StartsWith( "note:", StringComparison.Ordinal ) ).ToList();
		foreach ( var label in noteLabels )
		{
			children.Remove( label );
			children.Add( label );
		}
		children.Add( Layer( solid, width, height, new Shape { Stroke = new VectorStroke( StrokeWidth, theme.Muted ) { Cap = StrokeCap.Round, Join = StrokeJoin.Round } } ) );
		children.Add( Layer( dashed, width, height, new Shape { Stroke = new VectorStroke( StrokeWidth, theme.Muted ) { Dashes = new DashPattern( [5, 4], 0 ) } } ) );
		children.Add( Layer( heads, width, height, new Shape { Fill = new VectorPaint( theme.Muted ) } ) );
		children.Add( Layer( openHeads, width, height, new Shape { Stroke = new VectorStroke( StrokeWidth, theme.Muted ) { Cap = StrokeCap.Round, Join = StrokeJoin.Round } } ) );

		return Frame( children, width, height, "Sequence diagram" );
	}

	/// <summary>Adds a sequence message's end mark: a filled arrow, an open arrow, a cross, or nothing.</summary>
	private static void AddArrow( char head, (double X, double Y) tip, (double X, double Y) from, PathBuilder filled, PathBuilder open )
	{
		switch ( head )
		{
			case '>':
				AddMark( '>', tip, from, filled, open );
				break;
			case ')':
			{
				var (ux, uy) = Unit( tip, from );
				var back = (X: tip.X + ux * ArrowSize, Y: tip.Y + uy * ArrowSize);
				open.MoveTo( back.X - uy * ArrowSize / 2, back.Y + ux * ArrowSize / 2 ).LineTo( tip.X, tip.Y ).LineTo( back.X + uy * ArrowSize / 2, back.Y - ux * ArrowSize / 2 );
				break;
			}
			case 'x':
				AddMark( 'x', tip, from, filled, open );
				break;
		}
	}

	/// <summary>Adds an edge end mark at a tip, pointing away from <paramref name="from"/>: an arrow, a circle, or a cross.</summary>
	private static void AddMark( char mark, (double X, double Y) tip, (double X, double Y) from, PathBuilder filled, PathBuilder open )
	{
		var (ux, uy) = Unit( tip, from );
		switch ( mark )
		{
			case '>':
			{
				var back = (X: tip.X + ux * ArrowSize, Y: tip.Y + uy * ArrowSize);
				filled.MoveTo( tip.X, tip.Y )
					.LineTo( back.X - uy * ArrowSize / 2, back.Y + ux * ArrowSize / 2 )
					.LineTo( back.X + uy * ArrowSize / 2, back.Y - ux * ArrowSize / 2 )
					.Close();
				break;
			}
			case 'o':
				open.Ellipse( tip.X + ux * 4 - 4, tip.Y + uy * 4 - 4, 8, 8 );
				break;
			case 'x':
			{
				var center = (X: tip.X + ux * 5, Y: tip.Y + uy * 5);
				open.MoveTo( center.X - 4, center.Y - 4 ).LineTo( center.X + 4, center.Y + 4 ).MoveTo( center.X + 4, center.Y - 4 ).LineTo( center.X - 4, center.Y + 4 );
				break;
			}
		}
	}

	/// <summary>Gets the unit vector from a tip toward another point.</summary>
	private static (double X, double Y) Unit( (double X, double Y) tip, (double X, double Y) toward )
	{
		var dx = toward.X - tip.X;
		var dy = toward.Y - tip.Y;
		var length = Math.Max( 0.001, Math.Sqrt( dx * dx + dy * dy ) );
		return (dx / length, dy / length);
	}

	/// <summary>Gets an edge's points with its ends moved from the nodes' centers to their outlines.</summary>
	private static List<(double X, double Y)> Clip( FlowEdge edge )
	{
		var points = edge.Points.ToList();
		if ( points.Count < 2 )
			return points;
		points[0] = Border( edge.From, points[1] );
		points[^1] = Border( edge.To, points[^2] );
		return points;
	}

	/// <summary>Finds where the line from a node's center toward a point leaves the node's outline.</summary>
	private static (double X, double Y) Border( FlowNode node, (double X, double Y) toward )
	{
		var dx = toward.X - node.X;
		var dy = toward.Y - node.Y;
		if ( Math.Abs( dx ) < 0.001 && Math.Abs( dy ) < 0.001 )
			return (node.X, node.Y);
		var halfWidth = node.Width / 2;
		var halfHeight = node.Height / 2;
		double scale;
		if ( node.Shape is FlowShape.Diamond )
			scale = 1 / (Math.Abs( dx ) / halfWidth + Math.Abs( dy ) / halfHeight);
		else if ( node.Shape is FlowShape.Circle )
			scale = 1 / Math.Sqrt( dx * dx / (halfWidth * halfWidth) + dy * dy / (halfHeight * halfHeight) );
		else
			scale = Math.Min( Math.Abs( dx ) < 0.001 ? double.MaxValue : halfWidth / Math.Abs( dx ), Math.Abs( dy ) < 0.001 ? double.MaxValue : halfHeight / Math.Abs( dy ) );
		// The arrowhead's tip touches the outline without overlapping its stroke.
		var length = Math.Sqrt( dx * dx + dy * dy );
		var gap = StrokeWidth / length;
		return (node.X + dx * (scale + gap), node.Y + dy * (scale + gap));
	}

	/// <summary>Adds a smooth curve through points: a straight line for two, else a Catmull-Rom spline as cubic curves.</summary>
	private static void AddCurve( PathBuilder path, List<(double X, double Y)> points )
	{
		path.MoveTo( points[0].X, points[0].Y );
		if ( points.Count == 2 )
		{
			path.LineTo( points[1].X, points[1].Y );
			return;
		}
		for ( var index = 0; index + 1 < points.Count; index++ )
		{
			var p0 = points[Math.Max( 0, index - 1 )];
			var p1 = points[index];
			var p2 = points[index + 1];
			var p3 = points[Math.Min( points.Count - 1, index + 2 )];
			path.CubicTo( p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6, p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6, p2.X, p2.Y );
		}
	}

	/// <summary>Adds a node's outline in its shape.</summary>
	private static void AddNodeShape( PathBuilder path, FlowNode node )
	{
		var (left, top, width, height) = (node.X - node.Width / 2, node.Y - node.Height / 2, node.Width, node.Height);
		var (right, bottom, cx, cy) = (left + width, top + height, node.X, node.Y);
		switch ( node.Shape )
		{
			case FlowShape.Round:
				path.RoundedRectangle( left, top, width, height, 8 );
				break;
			case FlowShape.Stadium:
				path.RoundedRectangle( left, top, width, height, height / 2 );
				break;
			case FlowShape.Circle:
				path.Ellipse( left, top, width, height );
				break;
			case FlowShape.Diamond:
				path.MoveTo( cx, top ).LineTo( right, cy ).LineTo( cx, bottom ).LineTo( left, cy ).Close();
				break;
			case FlowShape.Hexagon:
			{
				var inset = Math.Min( height / 2, width / 4 );
				path.MoveTo( left + inset, top ).LineTo( right - inset, top ).LineTo( right, cy ).LineTo( right - inset, bottom ).LineTo( left + inset, bottom ).LineTo( left, cy ).Close();
				break;
			}
			case FlowShape.Database:
			{
				var cap = Math.Min( 8, height / 4 );
				path.MoveTo( left, top + cap ).LineTo( left, bottom - cap ).CubicTo( left, bottom + cap / 3, right, bottom + cap / 3, right, bottom - cap ).LineTo( right, top + cap );
				path.CubicTo( right, top - cap / 3, left, top - cap / 3, left, top + cap ).CubicTo( left, top + cap * 7 / 3, right, top + cap * 7 / 3, right, top + cap );
				break;
			}
			case FlowShape.Slant:
			{
				var inset = Math.Min( 12, width / 6 );
				path.MoveTo( left + inset, top ).LineTo( right, top ).LineTo( right - inset, bottom ).LineTo( left, bottom ).Close();
				break;
			}
			case FlowShape.Subroutine:
				path.Rectangle( left, top, width, height );
				path.MoveTo( left + 6, top ).LineTo( left + 6, bottom ).MoveTo( right - 6, top ).LineTo( right - 6, bottom );
				break;
			case FlowShape.Flag:
				path.MoveTo( left, top ).LineTo( right, top ).LineTo( right, bottom ).LineTo( left, bottom ).LineTo( left + Math.Min( 12, width / 6 ), cy ).Close();
				break;
			case FlowShape.Point:
				break;
			default:
				path.RoundedRectangle( left, top, width, height, 3 );
				break;
		}
	}

	/// <summary>Finds the middle of a polyline by length, where an edge's label goes.</summary>
	private static (double X, double Y) Middle( List<(double X, double Y)> points )
	{
		var lengths = new List<double>();
		for ( var index = 0; index + 1 < points.Count; index++ )
			lengths.Add( Math.Sqrt( Math.Pow( points[index + 1].X - points[index].X, 2 ) + Math.Pow( points[index + 1].Y - points[index].Y, 2 ) ) );
		var half = lengths.Sum() / 2;
		for ( var index = 0; index < lengths.Count; index++ )
		{
			if ( half <= lengths[index] && lengths[index] > 0 )
			{
				var share = half / lengths[index];
				return (points[index].X + (points[index + 1].X - points[index].X) * share, points[index].Y + (points[index + 1].Y - points[index].Y) * share);
			}
			half -= lengths[index];
		}
		return points[^1];
	}

	/// <summary>Places one shape over the whole drawing at its size, so its view box maps one to one.</summary>
	private static Shape Layer( PathBuilder path, double width, double height, Shape shape )
	{
		shape.Path = path.Build();
		shape.Fit = ShapeFit.Fill;
		shape.Position = PositionType.Absolute;
		shape.Left = 0;
		shape.Top = 0;
		shape.Width = width;
		shape.Height = height;
		shape.Accessibility = new Accessibility { Hidden = true };
		return shape;
	}

	/// <summary>Places a label at a top-left corner, in a box of a width, aligned within it.</summary>
	private static Text Label( string key, string text, double left, double top, double width, Color color, TextAlign align, Color? background = null ) => new()
	{
		Key = key,
		Content = text,
		Position = PositionType.Absolute,
		Left = left,
		Top = top,
		Width = Math.Max( 1, width ),
		FontFamily = ViewStyle.FontFamily,
		FontSize = FontSize,
		LineHeight = LineHeight,
		Color = color,
		TextAlign = align,
		TextWrap = TextWrap.NoWrap,
		BackgroundColor = background ?? Color.Transparent,
		BorderRadius = background is null ? 0 : 3,
		Selectable = true,
	};

	/// <summary>Puts the drawing in a framed box at its size, inside a viewport that scrolls sideways when it is wider than the column.</summary>
	private static Blob Frame( List<Blob> children, double width, double height, string name ) => new Row
	{
		AccessibilityRole = AccessibilityRole.Group,
		AccessibilityName = name,
		MinWidth = 0,
		Overflow = Overflow.Scroll,
		AlignItems = AlignItems.FlexStart,
		Children =
		[
			new Container
			{
				Width = Math.Ceiling( width ),
				Height = Math.Ceiling( height ),
				FlexShrink = 0,
				BorderWidth = Theme.Current.BorderWidth,
				BorderColor = Theme.Current.Border,
				BorderRadius = Theme.Current.Radius,
				BackgroundColor = Theme.Current.Canvas,
				Children = children,
			},
		],
	};
}
