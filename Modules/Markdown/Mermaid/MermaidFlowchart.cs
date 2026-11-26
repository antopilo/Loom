using System.Text.RegularExpressions;

namespace Lokn.Modules.Markdown;

/// <summary>Names the shape a flowchart node draws.</summary>
internal enum FlowShape
{
	/// <summary>A rectangle, <c>id[text]</c>.</summary>
	Box,

	/// <summary>A rounded rectangle, <c>id(text)</c>.</summary>
	Round,

	/// <summary>A pill, <c>id([text])</c>.</summary>
	Stadium,

	/// <summary>A circle, <c>id((text))</c>.</summary>
	Circle,

	/// <summary>A diamond, <c>id{text}</c>.</summary>
	Diamond,

	/// <summary>A hexagon, <c>id{{text}}</c>.</summary>
	Hexagon,

	/// <summary>A cylinder, <c>id[(text)]</c>.</summary>
	Database,

	/// <summary>A slanted box, <c>id[/text/]</c> and its variants.</summary>
	Slant,

	/// <summary>A box with a double side, <c>id[[text]]</c>.</summary>
	Subroutine,

	/// <summary>A flag, <c>id&gt;text]</c>.</summary>
	Flag,

	/// <summary>A point that only joins lines: a node a long edge passes through.</summary>
	Point,
}

/// <summary>Names how a flowchart edge is drawn.</summary>
internal enum FlowLine
{
	/// <summary>A solid line, <c>--</c>.</summary>
	Solid,

	/// <summary>A dotted line, <c>-.-</c>.</summary>
	Dotted,

	/// <summary>A thick line, <c>==</c>.</summary>
	Thick,
}

/// <summary>Holds one flowchart node: its text, its shape and, after layout, its box.</summary>
internal sealed class FlowNode( string id )
{
	/// <summary>Gets the node's id.</summary>
	public string Id { get; } = id;

	/// <summary>Gets or sets the text the node shows.</summary>
	public string Label { get; set; } = id;

	/// <summary>Gets or sets the node's shape.</summary>
	public FlowShape Shape { get; set; } = FlowShape.Box;

	/// <summary>Gets or sets the node's width and height in logical pixels.</summary>
	public double Width { get; set; }

	/// <summary>Gets or sets the node's height in logical pixels.</summary>
	public double Height { get; set; }

	/// <summary>Gets or sets the node's center, after layout.</summary>
	public double X { get; set; }

	/// <summary>Gets or sets the node's center, after layout.</summary>
	public double Y { get; set; }

	/// <summary>Gets or sets the node's layer along the flow, after layout.</summary>
	public int Layer { get; set; }

	/// <summary>Gets or sets the node's place in its layer, after layout.</summary>
	public double Order { get; set; }

	/// <summary>Gets or sets the subgraph the node belongs to, or null.</summary>
	public FlowSubgraph? Subgraph { get; set; }
}

/// <summary>Holds one flowchart edge: its ends, its label and how it is drawn; after layout, the points it passes.</summary>
internal sealed class FlowEdge( FlowNode from, FlowNode to )
{
	/// <summary>Gets the node the edge leaves.</summary>
	public FlowNode From { get; } = from;

	/// <summary>Gets the node the edge reaches.</summary>
	public FlowNode To { get; } = to;

	/// <summary>Gets or sets the edge's label, or empty.</summary>
	public string Label { get; set; } = "";

	/// <summary>Gets or sets how the line is drawn.</summary>
	public FlowLine Line { get; set; }

	/// <summary>Gets or sets the mark at the end the edge reaches: <c>&gt;</c> an arrow, <c>o</c> a circle, <c>x</c> a cross, or a space for none.</summary>
	public char Head { get; set; } = '>';

	/// <summary>Gets or sets the mark at the end the edge leaves, as <see cref="Head"/>.</summary>
	public char Tail { get; set; } = ' ';

	/// <summary>Gets the points the edge passes, from its start to its end, after layout.</summary>
	public List<(double X, double Y)> Points { get; } = [];

	/// <summary>Gets or sets the index in <see cref="Points"/> of the point its label sits on, a place the layout keeps free for it; -1 without a label.</summary>
	public int LabelPoint { get; set; } = -1;
}

/// <summary>Holds one subgraph: its title and, after layout, its box.</summary>
internal sealed class FlowSubgraph( string id, string title )
{
	/// <summary>Gets the subgraph's id.</summary>
	public string Id { get; } = id;

	/// <summary>Gets the subgraph's title.</summary>
	public string Title { get; } = title;

	/// <summary>Gets or sets the subgraph's left edge, after layout.</summary>
	public double Left { get; set; }

	/// <summary>Gets or sets the subgraph's top edge, after layout.</summary>
	public double Top { get; set; }

	/// <summary>Gets or sets the subgraph's right edge, after layout.</summary>
	public double Right { get; set; }

	/// <summary>Gets or sets the subgraph's bottom edge, after layout.</summary>
	public double Bottom { get; set; }
}

/// <summary>Parses a Mermaid flowchart and lays it out in layers along its direction, Sugiyama style: cycles broken, layers by longest path,
/// long edges split by points, orders improved by barycenters, then positions centered on their neighbors.</summary>
internal sealed partial class MermaidFlowchart
{
	/// <summary>Defines the space between nodes of one layer, in logical pixels.</summary>
	private const double NodeGap = 28;

	/// <summary>Defines the space between layers, in logical pixels.</summary>
	private const double LayerGap = 48;

	/// <summary>Defines the space inside a node around its text, in logical pixels.</summary>
	private const double NodePadding = 14;

	/// <summary>Defines the space around the drawing and inside subgraphs, in logical pixels.</summary>
	private const double Margin = 16;

	/// <summary>Defines how many barycenter sweeps order the layers.</summary>
	private const int OrderSweeps = 8;

	/// <summary>Gets the nodes, in the order they first appear.</summary>
	public List<FlowNode> Nodes { get; } = [];

	/// <summary>Gets the edges, in the order they appear.</summary>
	public List<FlowEdge> Edges { get; } = [];

	/// <summary>Gets the subgraphs.</summary>
	public List<FlowSubgraph> Subgraphs { get; } = [];

	/// <summary>Gets the direction: TB, BT, LR or RL.</summary>
	public string Direction { get; private set; } = "TB";

	/// <summary>Gets the drawing's width, after layout.</summary>
	public double Width { get; private set; }

	/// <summary>Gets the drawing's height, after layout.</summary>
	public double Height { get; private set; }

	/// <summary>Holds the nodes by id.</summary>
	private readonly Dictionary<string, FlowNode> _byId = new( StringComparer.Ordinal );

	/// <summary>Parses a flowchart and lays it out with text measured by <paramref name="measure"/>; null when the source is not a flowchart it reads.</summary>
	/// <param name="source">The Mermaid source.</param>
	/// <param name="measure">Measures a label's width and height in logical pixels.</param>
	public static MermaidFlowchart? Build( string source, Func<string, (double Width, double Height)> measure )
	{
		var chart = new MermaidFlowchart();
		if ( !chart.Parse( source ) || chart.Nodes.Count == 0 )
			return null;
		chart.Layout( measure );
		return chart;
	}

	/// <summary>Reads the statements: nodes, chains of edges, subgraphs; styling statements are skipped.</summary>
	private bool Parse( string source )
	{
		var lines = source.Replace( "\r\n", "\n" ).Split( '\n' ).Select( StripComment ).Where( line => line.Trim().Length > 0 ).ToList();
		if ( lines.Count == 0 || HeaderPattern().Match( lines[0].Trim() ) is not { Success: true } header )
			return false;
		Direction = header.Groups[2].Value.ToUpperInvariant() switch { "" or "TD" => "TB", var other => other };
		var subgraphs = new Stack<FlowSubgraph>();
		foreach ( var raw in lines.Skip( 1 ) )
		{
			foreach ( var part in SplitStatements( raw ) )
			{
				var statement = part.Trim();
				if ( statement.Length == 0 )
					continue;
				if ( SubgraphPattern().Match( statement ) is { Success: true } subgraph )
				{
					var id = subgraph.Groups[1].Value;
					var title = subgraph.Groups[2].Success ? Unquote( subgraph.Groups[2].Value ) : subgraph.Groups[3].Success ? Unquote( subgraph.Groups[3].Value ) : id;
					var group = new FlowSubgraph( id, title );
					Subgraphs.Add( group );
					subgraphs.Push( group );
					continue;
				}
				if ( statement == "end" )
				{
					if ( subgraphs.Count > 0 )
						subgraphs.Pop();
					continue;
				}
				if ( SkippedPattern().IsMatch( statement ) )
					continue;
				ParseChain( statement, subgraphs.Count > 0 ? subgraphs.Peek() : null );
			}
		}
		return true;
	}

	/// <summary>Removes a <c>%%</c> comment.</summary>
	private static string StripComment( string line )
	{
		var comment = line.IndexOf( "%%", StringComparison.Ordinal );
		return comment < 0 ? line : line[..comment];
	}

	/// <summary>Splits a line at semicolons outside quotes and brackets.</summary>
	private static IEnumerable<string> SplitStatements( string line )
	{
		var depth = 0;
		var isQuoted = false;
		var start = 0;
		for ( var index = 0; index < line.Length; index++ )
		{
			var character = line[index];
			if ( character == '"' )
				isQuoted = !isQuoted;
			else if ( !isQuoted && character is '[' or '(' or '{' )
				depth++;
			else if ( !isQuoted && character is ']' or ')' or '}' )
				depth = Math.Max( 0, depth - 1 );
			else if ( !isQuoted && depth == 0 && character == ';' )
			{
				yield return line[start..index];
				start = index + 1;
			}
		}
		yield return line[start..];
	}

	/// <summary>Reads a chain such as <c>A[Start] --&gt; B{Ok?} -- yes --&gt; C &amp; D</c>: groups of nodes joined by edges.</summary>
	private void ParseChain( string statement, FlowSubgraph? subgraph )
	{
		var position = 0;
		var previous = ReadGroup( statement, ref position, subgraph );
		while ( position < statement.Length )
		{
			SkipSpaces( statement, ref position );
			if ( position >= statement.Length || EdgePattern().Match( statement, position ) is not { Success: true } edge || edge.Index != position )
				return;
			position += edge.Length;
			SkipSpaces( statement, ref position );
			var label = edge.Groups["text"].Success ? edge.Groups["text"].Value.Trim() : "";
			if ( position < statement.Length && statement[position] == '|' )
			{
				var close = statement.IndexOf( '|', position + 1 );
				if ( close < 0 )
					return;
				label = statement[(position + 1)..close].Trim();
				position = close + 1;
			}
			var next = ReadGroup( statement, ref position, subgraph );
			if ( next.Count == 0 )
				return;
			var arrow = edge.Value.Trim();
			var line = arrow.Contains( '.' ) ? FlowLine.Dotted : arrow.Contains( '=' ) ? FlowLine.Thick : FlowLine.Solid;
			var head = arrow[^1] is '>' or 'o' or 'x' ? arrow[^1] : ' ';
			var tail = arrow[0] is '<' ? '>' : arrow[0] is 'o' or 'x' && arrow.Length > 1 && arrow[1] is '-' or '=' or '.' ? arrow[0] : ' ';
			foreach ( var from in previous )
			{
				foreach ( var to in next )
					Edges.Add( new FlowEdge( from, to ) { Label = Unquote( label ), Line = line, Head = head, Tail = tail } );
			}
			previous = next;
		}
	}

	/// <summary>Reads nodes joined by <c>&amp;</c>.</summary>
	private List<FlowNode> ReadGroup( string text, ref int position, FlowSubgraph? subgraph )
	{
		var nodes = new List<FlowNode>();
		while ( true )
		{
			SkipSpaces( text, ref position );
			if ( ReadNode( text, ref position, subgraph ) is not { } node )
				return nodes;
			nodes.Add( node );
			SkipSpaces( text, ref position );
			if ( position < text.Length && text[position] == '&' )
			{
				position++;
				continue;
			}
			return nodes;
		}
	}

	/// <summary>Reads one node: its id, then an optional shape with its text, and a <c>:::class</c> that is ignored.</summary>
	private FlowNode? ReadNode( string text, ref int position, FlowSubgraph? subgraph )
	{
		if ( NodeIdPattern().Match( text, position ) is not { Success: true } id || id.Index != position )
			return null;
		position += id.Length;
		if ( !_byId.TryGetValue( id.Value, out var node ) )
		{
			node = new FlowNode( id.Value );
			_byId[id.Value] = node;
			Nodes.Add( node );
		}
		node.Subgraph ??= subgraph;
		foreach ( var (open, close, shape) in Shapes )
		{
			if ( string.CompareOrdinal( text, position, open, 0, open.Length ) != 0 )
				continue;
			var end = FindShapeEnd( text, position + open.Length, close );
			if ( end < 0 )
				continue;
			node.Label = Unquote( text[(position + open.Length)..end].Trim() );
			node.Shape = shape;
			position = end + close.Length;
			break;
		}
		if ( ClassPattern().Match( text, position ) is { Success: true } style && style.Index == position )
			position += style.Length;
		return node;
	}

	/// <summary>Lists the shape brackets, longest first so <c>((</c> wins over <c>(</c>.</summary>
	private static readonly (string Open, string Close, FlowShape Shape)[] Shapes =
	[
		("([", "])", FlowShape.Stadium), ("((", "))", FlowShape.Circle), ("[(", ")]", FlowShape.Database), ("[[", "]]", FlowShape.Subroutine),
		("{{", "}}", FlowShape.Hexagon), ("[/", "/]", FlowShape.Slant), ("[\\", "\\]", FlowShape.Slant), ("[/", "\\]", FlowShape.Slant),
		("[\\", "/]", FlowShape.Slant), ("[", "]", FlowShape.Box), ("(", ")", FlowShape.Round), ("{", "}", FlowShape.Diamond), (">", "]", FlowShape.Flag),
	];

	/// <summary>Finds a shape's closing bracket outside quotes, or -1.</summary>
	private static int FindShapeEnd( string text, int start, string close )
	{
		var isQuoted = false;
		for ( var index = start; index <= text.Length - close.Length; index++ )
		{
			if ( text[index] == '"' )
				isQuoted = !isQuoted;
			else if ( !isQuoted && string.CompareOrdinal( text, index, close, 0, close.Length ) == 0 )
				return index;
		}
		return -1;
	}

	/// <summary>Removes surrounding quotes and turns <c>&lt;br&gt;</c> into line breaks and Markdown backquote strings into their text.</summary>
	private static string Unquote( string text )
	{
		var value = text.Trim();
		if ( value.Length >= 2 && value[0] == '"' && value[^1] == '"' )
			value = value[1..^1];
		if ( value.Length >= 2 && value[0] == '`' && value[^1] == '`' )
			value = value[1..^1];
		return BreakPattern().Replace( value, "\n" ).Replace( "#quot;", "\"", StringComparison.Ordinal );
	}

	/// <summary>Skips spaces.</summary>
	private static void SkipSpaces( string text, ref int position )
	{
		while ( position < text.Length && char.IsWhiteSpace( text[position] ) )
			position++;
	}

	/// <summary>Sizes the nodes, then places them in layers and routes the edges.</summary>
	private void Layout( Func<string, (double Width, double Height)> measure )
	{
		var isVertical = Direction is "TB" or "BT";
		foreach ( var node in Nodes )
		{
			var (width, height) = measure( node.Label );
			(node.Width, node.Height) = node.Shape switch
			{
				FlowShape.Circle => (Math.Max( width, height ) + NodePadding * 2, Math.Max( width, height ) + NodePadding * 2),
				FlowShape.Diamond => (width + height + NodePadding * 2, width / 2 + height + NodePadding * 2),
				FlowShape.Hexagon or FlowShape.Slant or FlowShape.Flag => (width + NodePadding * 3, height + NodePadding),
				FlowShape.Database => (width + NodePadding * 2, height + NodePadding + 12),
				_ => (width + NodePadding * 2, height + NodePadding),
			};
		}

		// The layout works top to bottom; a horizontal chart swaps each node's sides first and the coordinates last.
		if ( !isVertical )
		{
			foreach ( var node in Nodes )
				(node.Width, node.Height) = (node.Height, node.Width);
		}

		var forward = BreakCycles();
		AssignLayers( forward );
		var layers = InsertPoints( forward, measure, isVertical, out var chains );
		OrderLayers( layers );
		Place( layers );
		Route( chains );

		if ( !isVertical )
		{
			foreach ( var node in Nodes.Concat( chains.SelectMany( chain => chain.Points ) ) )
			{
				(node.X, node.Y) = (node.Y, node.X);
				(node.Width, node.Height) = (node.Height, node.Width);
			}
			foreach ( var edge in Edges )
			{
				for ( var index = 0; index < edge.Points.Count; index++ )
					edge.Points[index] = (edge.Points[index].Y, edge.Points[index].X);
			}
		}

		Flip();
		PlaceSubgraphs( measure );
		Fit();
	}

	/// <summary>Lists each edge's ends in the layering's direction: an edge that closes a cycle, found by depth-first search, is reversed.</summary>
	private List<(FlowNode From, FlowNode To, FlowEdge Edge)> BreakCycles()
	{
		var state = new Dictionary<FlowNode, int>();
		var outgoing = Nodes.ToDictionary( node => node, _ => new List<FlowEdge>() );
		foreach ( var edge in Edges )
			outgoing[edge.From].Add( edge );
		var reversed = new HashSet<FlowEdge>();
		void Visit( FlowNode node )
		{
			state[node] = 1;
			foreach ( var edge in outgoing[node] )
			{
				var next = edge.To;
				var seen = state.GetValueOrDefault( next );
				if ( seen == 1 )
					reversed.Add( edge );
				else if ( seen == 0 )
					Visit( next );
			}
			state[node] = 2;
		}
		foreach ( var node in Nodes )
		{
			if ( state.GetValueOrDefault( node ) == 0 )
				Visit( node );
		}
		return [.. Edges.Where( edge => edge.From != edge.To ).Select( edge => reversed.Contains( edge ) ? (edge.To, edge.From, edge) : (edge.From, edge.To, edge) )];
	}

	/// <summary>Puts each node one layer below the deepest node that leads to it.</summary>
	private void AssignLayers( List<(FlowNode From, FlowNode To, FlowEdge Edge)> forward )
	{
		var incoming = Nodes.ToDictionary( node => node, _ => 0 );
		foreach ( var (_, to, _) in forward )
			incoming[to]++;
		var ready = new Queue<FlowNode>( Nodes.Where( node => incoming[node] == 0 ) );
		foreach ( var node in Nodes )
			node.Layer = 0;
		while ( ready.Count > 0 )
		{
			var node = ready.Dequeue();
			foreach ( var (from, to, _) in forward )
			{
				if ( from != node )
					continue;
				to.Layer = Math.Max( to.Layer, node.Layer + 1 );
				if ( --incoming[to] == 0 )
					ready.Enqueue( to );
			}
		}
	}

	/// <summary>Holds an edge split into layer-to-layer steps: the points it passes between its ends, and whether it was reversed.</summary>
	private sealed record Chain( FlowEdge Edge, List<FlowNode> Points, bool IsReversed );

	/// <summary>Builds the layers, adding a point node in each layer a long edge crosses, so every step joins neighboring layers. Node layers are
	/// doubled first, so every edge crosses at least one layer between its ends: a labeled edge's middle point there is its label's size, so the
	/// layout keeps room for the label rather than drawing it over nodes.</summary>
	private List<List<FlowNode>> InsertPoints( List<(FlowNode From, FlowNode To, FlowEdge Edge)> forward, Func<string, (double Width, double Height)> measure,
		bool isVertical, out List<Chain> chains )
	{
		foreach ( var node in Nodes )
			node.Layer *= 2;
		var count = Nodes.Count == 0 ? 0 : Nodes.Max( node => node.Layer ) + 1;
		var layers = Enumerable.Range( 0, count ).Select( _ => new List<FlowNode>() ).ToList();
		foreach ( var node in Nodes )
			layers[node.Layer].Add( node );
		chains = [];
		foreach ( var (from, to, edge) in forward )
		{
			var points = new List<FlowNode>();
			var labelLayer = edge.Label.Length > 0 ? (from.Layer + to.Layer) / 2 : -1;
			for ( var layer = from.Layer + 1; layer < to.Layer; layer++ )
			{
				var point = new FlowNode( "" ) { Shape = FlowShape.Point, Layer = layer, Width = 8, Height = 8 };
				if ( layer == labelLayer )
				{
					// The layout runs top to bottom, so a horizontal chart's label lies on its side here.
					var (width, height) = measure( edge.Label );
					(point.Width, point.Height) = isVertical ? (width + 12, height + 4) : (height + 4, width + 12);
				}
				layers[layer].Add( point );
				points.Add( point );
			}
			chains.Add( new( edge, points, edge.From != from ) );
			edge.LabelPoint = labelLayer < 0 ? -1 : 1 + (edge.From != from ? to.Layer - 1 - labelLayer : labelLayer - from.Layer - 1);
		}
		_links = [];
		foreach ( var chain in chains )
		{
			var (from, to) = chain.IsReversed ? (chain.Edge.To, chain.Edge.From) : (chain.Edge.From, chain.Edge.To);
			var path = new List<FlowNode> { from };
			path.AddRange( chain.Points );
			path.Add( to );
			for ( var index = 0; index + 1 < path.Count; index++ )
				_links.Add( (path[index], path[index + 1]) );
		}
		return layers;
	}

	/// <summary>Holds every step between neighboring layers, upper end first.</summary>
	private List<(FlowNode Upper, FlowNode Lower)> _links = [];

	/// <summary>Orders each layer by the average place of its neighbors, sweeping down and up, keeping the order with the fewest crossings.</summary>
	private void OrderLayers( List<List<FlowNode>> layers )
	{
		foreach ( var layer in layers )
		{
			for ( var index = 0; index < layer.Count; index++ )
				layer[index].Order = index;
		}
		var upper = _links.GroupBy( link => link.Lower ).ToDictionary( group => group.Key, group => group.Select( link => link.Upper ).ToList() );
		var lower = _links.GroupBy( link => link.Upper ).ToDictionary( group => group.Key, group => group.Select( link => link.Lower ).ToList() );
		var best = layers.Select( layer => layer.ToList() ).ToList();
		var bestCrossings = CountCrossings( layers );
		for ( var sweep = 0; sweep < OrderSweeps && bestCrossings > 0; sweep++ )
		{
			var isDown = sweep % 2 == 0;
			for ( var step = 1; step < layers.Count; step++ )
			{
				var index = isDown ? step : layers.Count - 1 - step;
				var neighbors = isDown ? upper : lower;
				var layer = layers[index];
				// A node without neighbors on that side keeps its place.
				var keys = layer.ToDictionary( node => node, node => neighbors.TryGetValue( node, out var list ) && list.Count > 0 ? list.Average( other => other.Order ) : node.Order );
				layer.Sort( ( a, b ) => keys[a].CompareTo( keys[b] ) is var compare and not 0 ? compare : a.Order.CompareTo( b.Order ) );
				for ( var place = 0; place < layer.Count; place++ )
					layer[place].Order = place;
			}
			var crossings = CountCrossings( layers );
			if ( crossings < bestCrossings )
			{
				bestCrossings = crossings;
				best = layers.Select( layer => layer.ToList() ).ToList();
			}
		}
		for ( var index = 0; index < layers.Count; index++ )
		{
			layers[index].Clear();
			layers[index].AddRange( best[index] );
			for ( var place = 0; place < layers[index].Count; place++ )
				layers[index][place].Order = place;
		}
	}

	/// <summary>Counts the crossings between the steps of neighboring layers.</summary>
	private int CountCrossings( List<List<FlowNode>> layers )
	{
		var crossings = 0;
		foreach ( var group in _links.GroupBy( link => link.Upper.Layer ) )
		{
			var steps = group.ToList();
			for ( var a = 0; a < steps.Count; a++ )
			{
				for ( var b = a + 1; b < steps.Count; b++ )
				{
					var top = steps[a].Upper.Order - steps[b].Upper.Order;
					var bottom = steps[a].Lower.Order - steps[b].Lower.Order;
					if ( top * bottom < 0 )
						crossings++;
				}
			}
		}
		return crossings;
	}

	/// <summary>Places layers one under the other and the nodes of each layer side by side, then moves nodes toward their neighbors' centers while
	/// keeping their order and gaps.</summary>
	private void Place( List<List<FlowNode>> layers )
	{
		var y = 0.0;
		foreach ( var layer in layers )
		{
			var height = layer.Count == 0 ? 0 : layer.Max( node => node.Height );
			var x = 0.0;
			foreach ( var node in layer )
			{
				node.X = x + node.Width / 2;
				node.Y = y + height / 2;
				x += node.Width + NodeGap;
			}
			// Layers are doubled, so a gap between nodes is two half gaps around the layer of edge points between them.
			y += height + LayerGap / 2;
		}

		var neighbors = new Dictionary<FlowNode, List<FlowNode>>();
		foreach ( var (a, b) in _links )
		{
			(neighbors.TryGetValue( a, out var list ) ? list : neighbors[a] = []).Add( b );
			(neighbors.TryGetValue( b, out var other ) ? other : neighbors[b] = []).Add( a );
		}
		for ( var pass = 0; pass < 12; pass++ )
		{
			foreach ( var layer in pass % 2 == 0 ? layers : Enumerable.Reverse( layers ) )
			{
				foreach ( var node in layer )
				{
					if ( neighbors.TryGetValue( node, out var list ) )
						node.X = list.Average( other => other.X );
				}
				// Keep the order and the gaps: push right from the left, then pull back left from the right, so the layer centers on its wishes.
				for ( var index = 1; index < layer.Count; index++ )
					layer[index].X = Math.Max( layer[index].X, layer[index - 1].X + (layer[index - 1].Width + layer[index].Width) / 2 + NodeGap );
				for ( var index = layer.Count - 2; index >= 0; index-- )
					layer[index].X = Math.Min( layer[index].X, layer[index + 1].X - (layer[index].Width + layer[index + 1].Width) / 2 - NodeGap );
			}
		}
	}

	/// <summary>Sets each edge's points: from its start node's side, through its point nodes, to its end node's side.</summary>
	private static void Route( List<Chain> chains )
	{
		foreach ( var chain in chains )
		{
			var edge = chain.Edge;
			var middle = chain.IsReversed ? Enumerable.Reverse( chain.Points ).ToList() : chain.Points;
			edge.Points.Clear();
			edge.Points.Add( (edge.From.X, edge.From.Y) );
			edge.Points.AddRange( middle.Select( point => (point.X, point.Y) ) );
			edge.Points.Add( (edge.To.X, edge.To.Y) );
		}
	}

	/// <summary>Mirrors the drawing for BT and RL charts.</summary>
	private void Flip()
	{
		if ( Direction is not ("BT" or "RL") )
			return;
		var isVertical = Direction == "BT";
		var extent = Nodes.Count == 0 ? 0 : isVertical ? Nodes.Max( node => node.Y ) + Nodes.Min( node => node.Y ) : Nodes.Max( node => node.X ) + Nodes.Min( node => node.X );
		foreach ( var node in Nodes )
		{
			if ( isVertical )
				node.Y = extent - node.Y;
			else
				node.X = extent - node.X;
		}
		foreach ( var edge in Edges )
		{
			for ( var index = 0; index < edge.Points.Count; index++ )
			{
				var (x, y) = edge.Points[index];
				edge.Points[index] = isVertical ? (x, extent - y) : (extent - x, y);
			}
		}
	}

	/// <summary>Sets each subgraph's box around its nodes, with room for its title.</summary>
	private void PlaceSubgraphs( Func<string, (double Width, double Height)> measure )
	{
		foreach ( var subgraph in Subgraphs )
		{
			var members = Nodes.Where( node => node.Subgraph == subgraph ).ToList();
			if ( members.Count == 0 )
				continue;
			var (_, titleHeight) = measure( subgraph.Title );
			subgraph.Left = members.Min( node => node.X - node.Width / 2 ) - Margin;
			subgraph.Right = members.Max( node => node.X + node.Width / 2 ) + Margin;
			subgraph.Top = members.Min( node => node.Y - node.Height / 2 ) - Margin - titleHeight;
			subgraph.Bottom = members.Max( node => node.Y + node.Height / 2 ) + Margin;
			subgraph.Right = Math.Max( subgraph.Right, subgraph.Left + measure( subgraph.Title ).Width + Margin * 2 );
		}
	}

	/// <summary>Moves the drawing so it starts at the margin and records its size, edge labels included.</summary>
	private void Fit()
	{
		var left = double.MaxValue;
		var top = double.MaxValue;
		var right = double.MinValue;
		var bottom = double.MinValue;
		void Include( double x0, double y0, double x1, double y1 )
		{
			left = Math.Min( left, x0 );
			top = Math.Min( top, y0 );
			right = Math.Max( right, x1 );
			bottom = Math.Max( bottom, y1 );
		}
		foreach ( var node in Nodes )
			Include( node.X - node.Width / 2, node.Y - node.Height / 2, node.X + node.Width / 2, node.Y + node.Height / 2 );
		foreach ( var subgraph in Subgraphs.Where( subgraph => subgraph.Right > subgraph.Left ) )
			Include( subgraph.Left, subgraph.Top, subgraph.Right, subgraph.Bottom );
		foreach ( var point in Edges.SelectMany( edge => edge.Points ) )
			Include( point.X, point.Y, point.X, point.Y );

		var dx = Margin - left;
		var dy = Margin - top;
		foreach ( var node in Nodes )
		{
			node.X += dx;
			node.Y += dy;
		}
		foreach ( var edge in Edges )
		{
			for ( var index = 0; index < edge.Points.Count; index++ )
				edge.Points[index] = (edge.Points[index].X + dx, edge.Points[index].Y + dy);
		}
		foreach ( var subgraph in Subgraphs )
		{
			subgraph.Left += dx;
			subgraph.Right += dx;
			subgraph.Top += dy;
			subgraph.Bottom += dy;
		}
		Width = right - left + Margin * 2;
		Height = bottom - top + Margin * 2;
	}

	/// <summary>Matches the header: <c>flowchart</c> or <c>graph</c> and an optional direction.</summary>
	[GeneratedRegex( @"^(flowchart|graph)(?:\s+(TB|TD|BT|LR|RL))?\s*;?$", RegexOptions.IgnoreCase )]
	private static partial Regex HeaderPattern();

	/// <summary>Matches a subgraph's start: its id and optional title in brackets or quotes.</summary>
	[GeneratedRegex( @"^subgraph\s+([^\s\[""]+)?\s*(?:\[(.+)\]|(""[^""]*""))?\s*$" )]
	private static partial Regex SubgraphPattern();

	/// <summary>Matches statements that only style or link the chart, which do not change its shape.</summary>
	[GeneratedRegex( @"^(classDef|class|style|linkStyle|click|direction|accTitle|accDescr)\b" )]
	private static partial Regex SkippedPattern();

	/// <summary>Matches a node id.</summary>
	[GeneratedRegex( @"[\p{L}\p{N}_][\p{L}\p{N}_\-.]*" )]
	private static partial Regex NodeIdPattern();

	/// <summary>Matches a <c>:::class</c> after a node.</summary>
	[GeneratedRegex( @":::[\w-]+" )]
	private static partial Regex ClassPattern();

	/// <summary>Matches an edge: its line with optional ends, and text written inside it such as <c>-- yes --&gt;</c>.</summary>
	[GeneratedRegex( @"(?:<|o|x)?(?:--\s*(?<text>[^->|][^>|]*?)\s*(?:-->|---)|==\s*(?<text>[^=>|][^>|]*?)\s*(?:==>|===)|-\.\s*(?<text>[^.>|][^>|]*?)\s*\.->|-{2,}>|-{3,}|={2,}>|={3,}|-\.+->|-\.+-|--[ox]|==[ox])" )]
	private static partial Regex EdgePattern();

	/// <summary>Matches a line break tag in a label.</summary>
	[GeneratedRegex( @"<br\s*/?>", RegexOptions.IgnoreCase )]
	private static partial Regex BreakPattern();
}
