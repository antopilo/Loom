using Lokn.Modules.Workspaces;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Projects;

/// <summary>Lists the active profile's projects with their tasks in the sidebar, the way the chat sidebar lists them with their chats, or their recent tasks in groups in the tab strip.</summary>
/// <remarks>
/// The sidebar starts with New task, Search and New project, then each project under a bold heading that folds its tasks, then
/// the tasks outside projects under Tasks. Rows show their actions on hover, and a right-click opens a row's menu. The tab strip
/// shows one group per project, its icon and name opening it, with the tasks <see cref="RecentTabs.ComputeShown"/> picks, and the
/// Tasks group outside projects, then a + that starts a task outside projects and a button that adds a project. Shows nothing
/// while the Conversations module is loaded: it lists the projects with their chats then.
/// </remarks>
/// <param name="context">The slot's dispatch and lifetime.</param>
/// <param name="module">The module that owns the projects.</param>
/// <param name="isTabs">Whether it fills the tab strip, in a row, instead of the sidebar, in a column.</param>
internal sealed class ProjectNavigation( PanelContext context, ProjectsModule module, bool isTabs ) : PanelContent
{
	/// <summary>Defines the module whose sidebar lists projects with their chats instead of this list.</summary>
	private const string ConversationsModule = "conversations";

	/// <summary>Builds the tab strip's task groups.</summary>
	private readonly RecentTabs _tabs = new( context );

	/// <summary>Defines project icons' width and height in the tab strip, in logical pixels.</summary>
	private const int TabIconSize = 16;

	/// <summary>Holds the sidebar's row state and builds its rows.</summary>
	private readonly SidebarRows _sidebar = new( context );

	/// <summary>Gets the sidebar's project forms, which build its project rows, created on first use beside <see cref="_sidebar"/>.</summary>
	private ProjectRows Projects => field ??= new( context, _sidebar, module.Store );

	/// <summary>Builds the projects: with their tasks and the tasks outside projects in the sidebar, as tabs with a + in the strip.</summary>
	public override Blob Build()
	{
		if ( context.App.Modules.Any( item => item.Id == ConversationsModule ) )
			return new Container();

		return isTabs ? BuildTabs() : BuildSidebar();
	}

	/// <summary>Builds the sidebar: the actions, the projects with their tasks, then the tasks outside projects.</summary>
	private Container BuildSidebar()
	{
		var workspaces = ProjectsModule.FindWorkspaces( context.App );
		var actions = new List<Blob>();
		if ( workspaces is not null )
			actions.Add( _sidebar.BuildNewTask( workspaces ) );
		actions.Add( _sidebar.BuildSearch() );
		actions.Add( Projects.BuildNewProjectAction( project => module.OpenProject( context.App, project ) ) );
		_sidebar.AddContributedActions( actions );
		if ( module.BuildModulePrompt( context, isNarrow: true ) is { } prompt )
			actions.Add( prompt );

		var rows = new List<SidebarRow>();
		var shown = 0;
		foreach ( var project in module.Store.Projects( context.App.Settings.ActiveProfile ) )
		{
			AddProject( workspaces, project, shown, rows );
			shown++;
		}

		if ( workspaces is not null )
			shown += _sidebar.AddHomeTasks( workspaces, rows );
		if ( shown == 0 )
			rows.Add( new( "empty", SidebarRows.RowGap, static () => SidebarRows.BuildEmpty( "Add a project to give its folder a workspace of its own." ) ) );

		return _sidebar.BuildSidebar( "Projects", actions, rows, workspaces?.CurrentTask );
	}

	/// <summary>Adds a project's rows: its heading, set visibly apart from what comes before, then its tasks at the same indent unless it is collapsed.</summary>
	private void AddProject( WorkspacesModule? workspaces, ProjectInfo project, int projectIndex, List<SidebarRow> rows )
	{
		rows.Add( new( "project:" + project.Id, Theme.Current.Spacing * 2, () => BuildProject( project, projectIndex ) ) );
		if ( _sidebar.IsCollapsed( project.Id ) || workspaces is null )
			return;

		var session = ProjectsModule.SessionKey( project.Id );
		var tasks = SidebarRows.ComputeShown( module.GetTasks( context.App, project.Id ) );
		for ( var index = 0; index < tasks.Length; index++ )
		{
			var task = tasks[index];
			var place = index;
			rows.Add( new( task.Id.ToString(), SidebarRows.RowGap, () => _sidebar.BuildTask( workspaces, session, task, place, () => module.OpenTask( context.App, project, task.Id ) ) ) );
		}
	}

	/// <summary>Builds a project's bold heading with its actions, its + grid and its menu; dragging the heading moves the project among the others.</summary>
	private Container BuildProject( ProjectInfo project, int index )
	{
		var isCollapsed = _sidebar.IsCollapsed( project.Id );
		var isOpen = module.FindCurrentProject( context.App ) == project.Id;
		var order = new RowOrder( "projects", index, dragged => module.Store.Move( dragged, project.Id ), IsHeading: true );
		var heading = Projects.BuildHeading( project, module.BuildProjectIcon( project, LoknHost.LocalId, SidebarRows.GroupIconSize ), isCollapsed,
			$"{project.Name}, {(isCollapsed ? "collapsed" : "expanded")}{(isOpen ? ", open" : "")}", drag: _sidebar.BuildRowDrag( project.Id, order ) );
		// Row actions stay out of sight until the pointer is on the row, or its menu or + grid is open.
		var isRevealed = _sidebar.IsRevealed( project.Id );
		void NewTask( string name ) => module.NewTask( context.App, project, name: name );
		var children = new List<Blob>
		{
			_sidebar.BuildHoverRow( project.Id,
			[
				heading,
				_sidebar.BuildAddButton( project.Id, $"New task in {project.Name}", () => NewTask( WorkspacesModule.NewTaskName ), isRevealed ),
				_sidebar.BuildMoreButton( project.Id, $"{project.Name} options", isRevealed ),
			], order ),
		};
		if ( _sidebar.BuildStarters( project.Id, project.Name, NewTask ) is { } starters )
			children.Add( starters );
		if ( _sidebar.IsMenuOpen( project.Id ) )
		{
			var entries = new List<MenuEntry>
			{
				new MenuAction( "New task", () => context.Run( () => module.NewTask( context.App, project ) ) ) { AccessKey = Key.T },
				new MenuAction( "Rename", () => context.Run( () => Projects.BeginRename( project ) ) ) { AccessKey = Key.R },
				new MenuAction( "Change folder…", () => context.Run( () => Projects.BeginChangeFolder( project ) ) ) { AccessKey = Key.F },
			};
			// A folder's own icon image wins, so an icon is chosen only for a project without one.
			if ( !module.HasImageIcon( project, LoknHost.LocalId ) )
				entries.Add( new MenuAction( project.Icon is null ? "Set icon…" : "Change icon…", () => context.Run( () => Projects.BeginChooseIcon( project ) ) ) { AccessKey = Key.I } );
			entries.Add( new MenuSeparator() );
			entries.Add( new MenuAction( "Remove project", () => context.Run( () => module.Store.Delete( project.Id ) ) ) { AccessKey = Key.D, IsDestructive = true } );
			children.Add( _sidebar.BuildMenu( project.Id, $"{project.Name} options", entries ) );
		}
		if ( Projects.BuildIconPicker( project ) is { } picker )
			children.Add( picker );

		return SidebarRows.BuildGroup( project.Id, children );
	}

	/// <summary>Builds the tab strip: the groups <see cref="RecentTabs.ComputeShown"/> picks, a project's or the Tasks outside projects, then the + that starts a task outside projects and the new-project button.</summary>
	private Row BuildTabs()
	{
		var children = new List<Blob>();
		if ( ProjectsModule.FindWorkspaces( context.App ) is { } workspaces )
		{
			var projects = module.Store.Projects( context.App.Settings.ActiveProfile ).ToDictionary( project => ProjectsModule.SessionKey( project.Id ), StringComparer.Ordinal );
			var groups = projects.Select( pair => RecentTabs.BuildTaskGroup( pair.Key, workspaces.Tasks( pair.Key ) ) ).Append( RecentTabs.BuildHomeGroup( workspaces ) );
			foreach ( var group in _tabs.Arrange( groups, workspaces.CurrentSession, workspaces.CurrentTask ) )
				children.Add( projects.TryGetValue( group.Key, out var project ) ? BuildGroup( workspaces, group, project ) : _tabs.BuildHome( workspaces, group ) );
			children.Add( _tabs.BuildNewTask( workspaces ) );
		}
		children.Add( BuildAdd() );
		return RecentTabs.BuildStrip( "Projects", children );
	}

	/// <summary>Builds a project's group: its icon and name, which open the project, its shown tasks, and the + that starts a task in it.</summary>
	private Row BuildGroup( WorkspacesModule workspaces, RecentTabGroup group, ProjectInfo project )
	{
		var icon = module.BuildProjectIcon( project, LoknHost.LocalId, TabIconSize );
		return _tabs.BuildTasks( workspaces, group, project.Name, icon,
			() => module.OpenProject( context.App, project ),
			task => module.OpenTask( context.App, project, task ),
			() => module.NewTask( context.App, project ) );
	}

	/// <summary>Builds the tab strip's small new-project button, which opens the folder chooser, then the chosen folder's project.</summary>
	private Button BuildAdd()
	{
		var theme = Theme.Current;
		var button = theme.BuildButton( "New project", () => context.Run( () => Projects.BeginCreate( project => module.OpenProject( context.App, project ) ) ) );
		button.Key = "add";
		button.Width = theme.ControlHeight;
		button.Padding = 0;
		button.FlexShrink = 0;
		button.AlignItems = AlignItems.Center;
		button.JustifyContent = JustifyContent.Center;
		button.AccessibilityName = "New project";
		button.Children = [SidebarRows.BuildIcon( SidebarRows.NewFolderGlyph )];
		return button;
	}
}
