namespace Lokn.Core;

/// <summary>Maintains immutable docking tree transformations.</summary>
public sealed partial class PanelWorkspace
{
	/// <summary>Enumerates panes in first-child then second-child order.</summary>
	private static IEnumerable<DockPane> EnumeratePanes( DockNode node )
	{
		if ( node is DockPane pane )
		{
			yield return pane;
			yield break;
		}

		var split = (DockSplit)node;
		foreach ( var child in EnumeratePanes( split.First ).Concat( EnumeratePanes( split.Second ) ) )
			yield return child;
	}

	/// <summary>Finds a node by its stable identifier.</summary>
	private static DockNode? FindNode( DockNode node, Guid id )
	{
		if ( node.Id == id )
			return node;

		return node is DockSplit split ? FindNode( split.First, id ) ?? FindNode( split.Second, id ) : null;
	}

	/// <summary>Finds a pane by its stable identifier.</summary>
	private static DockPane? FindPane( DockNode node, Guid id ) => FindNode( node, id ) as DockPane;

	/// <summary>Replaces one node while preserving every unrelated subtree.</summary>
	private static DockNode Replace( DockNode node, Guid id, DockNode replacement )
	{
		if ( node.Id == id )
			return replacement;

		if ( node is not DockSplit split )
			return node;

		var first = Replace( split.First, id, replacement );
		var second = Replace( split.Second, id, replacement );
		if ( ReferenceEquals( first, split.First ) && ReferenceEquals( second, split.Second ) )
			return split;

		return split with
		{
			First = first,
			Second = second,
		};
	}

	/// <summary>Removes a pane and promotes its sibling when its parent becomes redundant.</summary>
	private static DockNode? RemovePane( DockNode node, Guid id )
	{
		if ( node.Id == id )
			return null;

		if ( node is not DockSplit split )
			return node;

		var first = RemovePane( split.First, id );
		var second = RemovePane( split.Second, id );
		if ( first is null )
			return second;

		if ( second is null )
			return first;

		return split with
		{
			First = first,
			Second = second,
		};
	}
}
