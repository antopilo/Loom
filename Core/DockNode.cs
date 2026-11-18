namespace Lokn.Core;

/// <summary>Selects side-by-side or vertically stacked workspace panes.</summary>
public enum SplitAxis
{
	/// <summary>Places the first pane to the left of the second.</summary>
	Horizontal,

	/// <summary>Places the first pane above the second.</summary>
	Vertical,
}

/// <summary>Selects a swap or a docking edge relative to a target pane.</summary>
public enum PanelPlacement
{
	/// <summary>Swaps occupied panes or fills an empty target pane.</summary>
	Swap,

	/// <summary>Creates a pane to the left of the target.</summary>
	Left,

	/// <summary>Creates a pane to the right of the target.</summary>
	Right,

	/// <summary>Creates a pane above the target.</summary>
	Top,

	/// <summary>Creates a pane below the target.</summary>
	Bottom,
}

/// <summary>Describes an immutable node in the workspace split tree.</summary>
/// <param name="Id">The stable layout node identifier.</param>
public abstract record DockNode( Guid Id );

/// <summary>Describes one visible pane containing at most one panel.</summary>
/// <param name="Id">The stable pane identifier.</param>
/// <param name="PanelId">The visible panel identifier, or null for an empty pane.</param>
public sealed record DockPane( Guid Id, Guid? PanelId ) : DockNode( Id );

/// <summary>Describes two panes or subtrees separated by a resizable divider.</summary>
/// <param name="Id">The stable split identifier.</param>
/// <param name="Axis">The split orientation.</param>
/// <param name="Ratio">The first child's fraction of the available size, from zero to one.</param>
/// <param name="First">The first subtree.</param>
/// <param name="Second">The second subtree.</param>
public sealed record DockSplit( Guid Id, SplitAxis Axis, double Ratio, DockNode First, DockNode Second ) : DockNode( Id );

/// <summary>Describes an open panel independently of its visual position.</summary>
/// <param name="Id">The stable panel instance identifier.</param>
/// <param name="TypeId">The registered panel type identifier.</param>
/// <param name="Title">The pane title.</param>
public sealed record PanelInfo( Guid Id, string TypeId, string Title );
