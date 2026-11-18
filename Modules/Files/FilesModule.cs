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
			.Panel( "tree", "Files", context => _trees[context.Id] = new FileTreeContent( context ) )
			// Drawn like the sidebar's own actions, right under New project; highlighted while the tree shows.
			.Contribute( SidebarAction.Point, new SidebarAction( "Files", FileTreeContent.FolderGlyph, Toggle )
			{
				IsActive = app => Find( app ) is not null,
			} )
			.Contribute( TaskStarter.Point, new TaskStarter( "Files", FileTreeContent.FolderGlyph, 40, app => app.Panels.Open( PanelType ) ) )
			.Command( "toggle", "Files: show or hide", context => Toggle( context.App ) )
			.Service<IFileSideView>( this );
	}

	/// <summary>Shows the side view in a mode, opening it at the left edge when hidden, and gives the focus back to the panel that had it.</summary>
	public void Show( LoknApplication app, string modeKey )
	{
		foreach ( var closed in _trees.Keys.Where( id => app.Panels.Panels.All( panel => panel.Id != id ) ).ToArray() )
			_trees.Remove( closed );

		var active = app.Panels.ActivePanelId;
		if ( Find( app ) is null )
			Toggle( app );
		if ( Find( app ) is { } shown && _trees.TryGetValue( shown.Id, out var tree ) )
			tree.SelectMode( modeKey );
		if ( active is { } panel )
			app.Panels.Activate( panel );
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
		if ( leftmost.PanelId is not null && app.Panels.Move( id, leftmost.Id, PanelPlacement.Left ) && FindSplitOf( app.Panels.Root, id ) is { } split )
			app.Panels.UpdateSplitRatio( split.Id, TreeRatio );
		app.Panels.Activate( id );
	}

	/// <summary>Finds the pane at the left edge of the layout.</summary>
	private static DockPane FindLeftmost( DockNode node ) => node switch
	{
		DockPane pane => pane,
		DockSplit split => FindLeftmost( split.First ),
		_ => throw new InvalidOperationException( "The layout has an unknown node." ),
	};

	/// <summary>Finds the split whose first child is the pane holding a panel.</summary>
	private static DockSplit? FindSplitOf( DockNode node, Guid panelId ) => node switch
	{
		DockSplit { First: DockPane { PanelId: var first } } split when first == panelId => split,
		DockSplit split => FindSplitOf( split.First, panelId ) ?? FindSplitOf( split.Second, panelId ),
		_ => null,
	};
}
