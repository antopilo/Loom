using Goo;
using Lokn.Core;

namespace Lokn.Modules.Files;

/// <summary>Shows the side view at the left of the workspace, opened from a Files item in the sidebar: the current project's folders and files,
/// or another mode a module adds, such as Git's Changes and History.</summary>
/// <remarks>
/// It uses only the public API: the folder comes from the <see cref="IWorkingFolder"/> service (published by Projects), a clicked file
/// opens through <c>lokn://editor/open</c>, which the Editor module routes, and modes arrive as <see cref="FileTreeMode"/> contributions.
/// Without those modules the tree still shows, files do not open, and only the Files mode is offered.
/// </remarks>
public sealed class FilesModule : ILoknModule, IFileSideView
{
	/// <summary>Defines the tree's panel type.</summary>
	internal const string PanelType = "files.tree";

	/// <summary>Defines the tree's share of the workspace width when it opens.</summary>
	private const double TreeRatio = 0.24;

	/// <summary>Stores every tree panel built, by panel identifier, so a mode can be selected on the one that shows.</summary>
	private readonly Dictionary<Guid, FileTreeContent> _trees = [];

	/// <summary>Stores where the side view last sat in a shown layout, for the next time it opens; null before it has shown.</summary>
	private SidePlacement? _last;

	/// <summary>Stores the application while the module runs, or null.</summary>
	private LoknApplication? _app;

	/// <summary>Gets the module identifier.</summary>
	public string Id => "files";

	/// <summary>Gets the display name.</summary>
	public string Title => "Files";

	/// <summary>Registers the side view's extension points, its panel, the sidebar action that opens it, the tree a new task can start with, and its command.</summary>
	public void Register( ModuleBuilder builder )
	{
		builder
			// Other modules color files by state, such as Git with its changes, add buttons to the tree's header, such as a changed-files filter,
			// and add modes beside the tree, such as Git's Changes and History.
			.ExtensionPoint<FileStatusSource>( "status" )
			.ExtensionPoint<FileTreeButton>( "buttons" )
			.ExtensionPoint<FileTreeMode>( "modes" )
			// No task icon: the file tree is a side view beside any task, not what the task is about.
			.Panel( "tree", "Files", context => _trees[context.Id] = new FileTreeContent( context ), onHost: true )
			// Drawn like the sidebar's own actions, right under New project; highlighted while the tree shows.
			.Contribute( SidebarAction.Point, new SidebarAction( "Files", FileTreeContent.FolderGlyph, Toggle )
			{
				IsActive = app => Find( app ) is not null,
			} )
			.Contribute( TaskStarter.Point, new TaskStarter( "Files", FileTreeContent.FolderGlyph, 40, app => app.Panels.Open( PanelType ) ) )
			.Command( "toggle", "Files: show or hide", context => Toggle( context.App ) )
			.Service<IFileSideView>( this );
	}

	/// <summary>Starts remembering where the side view sits.</summary>
	public void Start( ModuleContext context )
	{
		_app = context.App;
		_app.Panels.TreeChanged += RememberPlacement;
	}

	/// <summary>Stops remembering.</summary>
	public void Stop()
	{
		if ( _app is not null )
			_app.Panels.TreeChanged -= RememberPlacement;
		_app = null;
	}

	/// <summary>Shows the side view in a mode, opening it at the left edge when hidden, or beside the panel that asks, and gives the focus back to the panel
	/// that had it.</summary>
	public void Show( LoknApplication app, string modeKey, Guid? besidePanelId = null )
	{
		foreach ( var closed in _trees.Keys.Where( id => app.Panels.Panels.All( panel => panel.Id != id ) ).ToArray() )
			_trees.Remove( closed );

		var active = app.Panels.ActivePanelId;
		if ( Find( app ) is null )
		{
			if ( besidePanelId is { } asking )
				OpenBeside( app, asking );
			else
				Toggle( app );
		}
		if ( Find( app ) is { } shown && _trees.TryGetValue( shown.Id, out var tree ) )
			tree.SelectMode( modeKey );
		if ( active is { } panel )
			app.Panels.Activate( panel );
	}

	/// <summary>Reports whether the side view is on screen in the shown layout.</summary>
	public bool IsShown( LoknApplication app ) => Find( app ) is not null;

	/// <summary>Closes the side view when it is on screen.</summary>
	public void Hide( LoknApplication app )
	{
		if ( Find( app ) is { } open )
			app.Panels.Close( open.Id );
	}

	/// <summary>Gets the tree panel in the shown layout, or null when none shows.</summary>
	/// <remarks>Other tasks and projects keep their own trees hidden in their saved layouts; those show their own folder and are never pulled in here.</remarks>
	internal static PanelInfo? Find( LoknApplication app ) =>
		app.Panels.Panels.FirstOrDefault( panel => panel.TypeId == PanelType && app.Panels.IsShown( panel.Id ) );

	/// <summary>Closes the tree when it shows; otherwise opens one for the current folder at the left edge of the workspace, right of the sidebar.</summary>
	internal static void Toggle( LoknApplication app )
	{
		if ( Find( app ) is { } open )
		{
			app.Panels.Close( open.Id );
			return;
		}

		var leftmost = FindLeftmost( app.Panels.Root );
		var id = app.Panels.Open( PanelType, leftmost.Id );
		// An occupied pane splits to its right; the tree then moves to its left, narrow, as the first column of the workspace.
		if ( leftmost.PanelId is not null && app.Panels.Move( id, leftmost.Id, PanelPlacement.Left ) && FindParent( app.Panels.Root, id ) is { } split )
			app.Panels.UpdateSplitRatio( split.Id, TreeRatio );
		app.Panels.Activate( id );
	}

	/// <summary>Opens a tree for the current folder for a panel that asks, such as a Git panel: where it last sat beside that panel and as wide as it was,
	/// else at the left of that panel, as wide as it opens at the workspace's edge.</summary>
	/// <param name="app">The application.</param>
	/// <param name="askingPanelId">The panel asking.</param>
	private void OpenBeside( LoknApplication app, Guid askingPanelId )
	{
		var panels = app.Panels;
		// Panes, not what shows: a panel hidden behind a maximized one is still where the side view sat.
		if ( panels.Panes.FirstOrDefault( pane => pane.PanelId == askingPanelId ) is not { } asking )
		{
			Toggle( app );
			return;
		}

		var (placement, ratio) = _last is { } last && last.AnchorPanelId == askingPanelId ? (last.Placement, last.Ratio) : (PanelPlacement.Left, TreeRatio);
		var id = panels.Open( PanelType, asking.Id );
		// The occupied pane splits to its right; the tree then moves to the side it sat on.
		if ( panels.Move( id, asking.Id, placement ) && FindParent( panels.Root, id ) is { } split )
			panels.UpdateSplitRatio( split.Id, split.First is DockPane { PanelId: var first } && first == id ? ratio : 1 - ratio );
		panels.Activate( id );
	}

	/// <summary>Records where the shown tree sits, whenever the layout changes; a layout without it keeps the last record.</summary>
	private void RememberPlacement()
	{
		if ( _app is not { } app || Find( app ) is not { } tree || ComputePlacement( app.Panels.Root, tree.Id ) is not { } placement )
			return;

		_last = placement;
	}

	/// <summary>Reads where a panel sits: on which side of the panel it touches, and what share of their split it takes.</summary>
	/// <returns>The placement, or null when the panel is alone or what it touches holds no panel.</returns>
	private static SidePlacement? ComputePlacement( DockNode node, Guid panelId )
	{
		if ( node is not DockSplit split )
			return null;

		var isFirst = split.First is DockPane { PanelId: var first } && first == panelId;
		if ( !isFirst && !(split.Second is DockPane { PanelId: var second } && second == panelId) )
			return ComputePlacement( split.First, panelId ) ?? ComputePlacement( split.Second, panelId );

		// The pane next to it, across the split: the nearest edge of what lies on the other side.
		var neighbor = FindEdge( isFirst ? split.Second : split.First, split.Axis, isFirst );
		if ( neighbor.PanelId is not { } anchor )
			return null;

		var placement = split.Axis == SplitAxis.Horizontal ? isFirst ? PanelPlacement.Left : PanelPlacement.Right : isFirst ? PanelPlacement.Top : PanelPlacement.Bottom;
		return new( anchor, placement, isFirst ? split.Ratio : 1 - split.Ratio );
	}

	/// <summary>Finds the pane of a layout that touches an edge: following each split along the axis toward the tree, and its first child across it.</summary>
	private static DockPane FindEdge( DockNode node, SplitAxis axis, bool treeIsFirst ) => node switch
	{
		DockPane pane => pane,
		DockSplit split => FindEdge( split.Axis == axis && !treeIsFirst ? split.Second : split.First, axis, treeIsFirst ),
		_ => throw new InvalidOperationException( "The layout has an unknown node." ),
	};

	/// <summary>Where the side view sat: the panel it touched, on which side of it, and what share of the two it took.</summary>
	/// <param name="AnchorPanelId">The panel next to it.</param>
	/// <param name="Placement">The side of that panel it was on.</param>
	/// <param name="Ratio">Its share of the split with that panel's side of the layout.</param>
	private readonly record struct SidePlacement( Guid AnchorPanelId, PanelPlacement Placement, double Ratio );

	/// <summary>Finds the pane at the left edge of the layout.</summary>
	private static DockPane FindLeftmost( DockNode node ) => node switch
	{
		DockPane pane => pane,
		DockSplit split => FindLeftmost( split.First ),
		_ => throw new InvalidOperationException( "The layout has an unknown node." ),
	};

	/// <summary>Finds the split with a panel's pane as one of its two children.</summary>
	private static DockSplit? FindParent( DockNode node, Guid panelId ) => node switch
	{
		DockSplit { First: DockPane { PanelId: var first } } split when first == panelId => split,
		DockSplit { Second: DockPane { PanelId: var second } } split when second == panelId => split,
		DockSplit split => FindParent( split.First, panelId ) ?? FindParent( split.Second, panelId ),
		_ => null,
	};
}
