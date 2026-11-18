using Goo;
using Lokn.Core;

namespace Lokn.Modules.Workspaces;

/// <summary>Lists tasks, the saved workspaces, so a window without chats can still switch between them.</summary>
/// <remarks>
/// The sidebar lists the tasks outside projects the way the chat sidebar lists chats, under New task and Search; while Projects
/// is loaded it lists them itself, after its projects, and this list shows nothing. The tab strip shows the tasks outside projects as
/// the Tasks group picked by <see cref="RecentTabs.ComputeShown"/>, then a + that starts one; while Projects is loaded it shows that
/// group itself among its projects, and this strip shows nothing. Shows nothing while the Conversations module is loaded: its
/// projects and chats lead the navigation then.
/// </remarks>
/// <param name="context">The slot's dispatch and lifetime.</param>
/// <param name="module">The module that owns the workspaces.</param>
/// <param name="isTabs">Whether it fills the tab strip, in a row, instead of the sidebar, in a column.</param>
internal sealed class WorkspaceNavigation( PanelContext context, WorkspacesModule module, bool isTabs ) : PanelContent
{
	/// <summary>Defines the module whose projects and chats replace this list.</summary>
	private const string ConversationsModule = "conversations";

	/// <summary>Defines the module that lists the tasks outside projects after its projects, in place of the sidebar list.</summary>
	private const string ProjectsModule = "projects";

	/// <summary>Holds the sidebar's row state and builds its rows.</summary>
	private readonly SidebarRows _sidebar = new( context );

	/// <summary>Builds the tab strip's task groups.</summary>
	private readonly RecentTabs _tabs = new( context );

	/// <summary>Builds the sidebar's tasks outside projects, or the tab strip's Tasks group with the + that starts one.</summary>
	public override Blob Build()
	{
		if ( context.App.Modules.Any( item => item.Id == ConversationsModule ) )
			return new Container();

		return isTabs ? BuildTabs() : BuildSidebar();
	}

	/// <summary>Builds the sidebar: New task and Search, then the tasks outside projects; nothing while Projects lists them.</summary>
	private Container BuildSidebar()
	{
		if ( context.App.Modules.Any( item => item.Id == ProjectsModule ) )
			return new Container();

		var children = new List<Blob> { _sidebar.BuildNewTask( module ), _sidebar.BuildSearch() };
		_sidebar.AddContributedActions( children );
		if ( _sidebar.AddHomeTasks( module, children ) == 0 )
			children.Add( SidebarRows.BuildEmpty( "Start a task to organize your work." ) );

		return new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Tasks",
			Padding = SidebarRows.ContentPadding,
			Gap = 2,
			OverflowY = Overflow.Scroll,
			Children = children,
		};
	}

	/// <summary>Builds the tab strip: the Tasks group, then the + that starts a task outside projects; nothing while Projects shows them.</summary>
	private Blob BuildTabs()
	{
		if ( context.App.Modules.Any( item => item.Id == ProjectsModule ) )
			return new Container();

		var children = new List<Blob>();
		foreach ( var group in _tabs.Arrange( [RecentTabs.BuildHomeGroup( module )], module.CurrentSession, module.CurrentTask ) )
			children.Add( _tabs.BuildHome( module, group ) );
		children.Add( _tabs.BuildNewTask( module ) );
		return RecentTabs.BuildStrip( "Tasks", children );
	}
}
