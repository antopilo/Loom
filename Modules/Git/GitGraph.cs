namespace Lokn.Modules.Git;

/// <summary>Lays a commit history out as lanes and line segments for a graph column.</summary>
public static class GitGraph
{
	/// <summary>Lays commits into lanes, one row per commit.</summary>
	/// <remarks>
	/// The commits run newest first with every parent after its children, as <c>git log --topo-order</c> gives them.
	/// Each active lane expects one commit. A commit takes the first lane expecting it, and other lanes expecting it merge
	/// into that one; a commit nobody expects starts in the first free lane with a new color. The first parent continues the
	/// commit's lane and color; each further parent joins a lane already expecting it or starts a new lane with a new color.
	/// Parents missing from the list, where history was cut off, end their line at the bottom of the last row that needs them.
	/// The work is linear in the commits times the lanes.
	/// </remarks>
	public static IReadOnlyList<GitGraphRow> Layout( IReadOnlyList<GitCommit> commits )
	{
		var present = new HashSet<string>( commits.Count, StringComparer.Ordinal );
		foreach ( var commit in commits )
			present.Add( commit.Id );

		// The last row naming each missing parent; past it no row can reach that parent, so its lanes end.
		var lastNeed = new Dictionary<string, int>( StringComparer.Ordinal );
		for ( var row = 0; row < commits.Count; row++ )
		{
			foreach ( var parent in commits[row].Parents )
			{
				if ( !present.Contains( parent ) )
					lastNeed[parent] = row;
			}
		}

		var lanes = new List<string?>();
		var colors = new List<int>();
		var passing = new List<int>();
		var nextColor = 0;
		var rows = new GitGraphRow[commits.Count];
		for ( var row = 0; row < commits.Count; row++ )
		{
			var commit = commits[row];
			var up = new List<GitGraphLine>();
			var down = new List<GitGraphLine>();
			passing.Clear();

			var lane = lanes.IndexOf( commit.Id );
			for ( var index = 0; index < lanes.Count; index++ )
			{
				var expected = lanes[index];
				if ( expected is null )
					continue;

				if ( expected == commit.Id )
				{
					up.Add( new( index, lane, colors[index] ) );
					if ( index != lane )
						lanes[index] = null;
				}
				else
				{
					up.Add( new( index, index, colors[index] ) );
					passing.Add( index );
				}
			}

			if ( lane < 0 )
			{
				lane = Allocate( lanes, colors );
				colors[lane] = nextColor++;
			}
			var color = colors[lane];

			var parents = commit.Parents;
			if ( parents.Count == 0 )
				lanes[lane] = null;
			else
			{
				lanes[lane] = parents[0];
				down.Add( new( lane, lane, color ) );
				for ( var index = 1; index < parents.Count; index++ )
				{
					var target = lanes.IndexOf( parents[index] );
					if ( target < 0 )
					{
						target = Allocate( lanes, colors );
						lanes[target] = parents[index];
						colors[target] = nextColor++;
					}
					down.Add( new( lane, target, colors[target] ) );
				}
			}

			foreach ( var index in passing )
				down.Add( new( index, index, colors[index] ) );

			var width = lane + 1;
			foreach ( var line in up )
				width = Math.Max( width, Math.Max( line.From, line.To ) + 1 );
			foreach ( var line in down )
				width = Math.Max( width, Math.Max( line.From, line.To ) + 1 );
			rows[row] = new( lane, color, width, [.. up], [.. down] );

			if ( lastNeed.Count > 0 )
			{
				for ( var index = 0; index < lanes.Count; index++ )
				{
					if ( lanes[index] is { } expected && lastNeed.TryGetValue( expected, out var last ) && last <= row )
						lanes[index] = null;
				}
			}

			while ( lanes.Count > 0 && lanes[^1] is null )
			{
				lanes.RemoveAt( lanes.Count - 1 );
				colors.RemoveAt( colors.Count - 1 );
			}
		}

		return rows;
	}

	/// <summary>Returns the first free lane, adding one at the end when every lane is in use.</summary>
	private static int Allocate( List<string?> lanes, List<int> colors )
	{
		var free = lanes.IndexOf( null );
		if ( free >= 0 )
			return free;

		lanes.Add( null );
		colors.Add( 0 );
		return lanes.Count - 1;
	}
}
