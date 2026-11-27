namespace Lokn.Core;

/// <summary>Describes something dragged from a module's view, such as a chat in the sidebar, that opens as a panel where it is dropped on a pane.</summary>
/// <remarks>The shell takes any drag whose data implements this, so a module can offer a panel without the shell knowing its type.</remarks>
public interface IPanelDrop
{
	/// <summary>Gets whether this drag can open a panel now; a drag that cannot is refused over panes.</summary>
	bool CanDrop { get; }

	/// <summary>Shows the dragged item's panel against a pane: in a new split on one of its sides, or in its place.</summary>
	/// <param name="paneId">The pane it was dropped on.</param>
	/// <param name="placement">Where against the pane it goes.</param>
	void Drop( Guid paneId, PanelPlacement placement );
}
