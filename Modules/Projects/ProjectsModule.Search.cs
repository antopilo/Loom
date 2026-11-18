using Goo;
using Lokn.Core;
using Lokn.Modules.Workspaces;

namespace Lokn.Modules.Projects;

/// <summary>Lists the active profile's projects and their tasks in the Search palette while the Conversations module, which lists them itself, is not loaded.</summary>
public sealed partial class ProjectsModule
{
	/// <summary>Defines the place of Projects among the Search palette's types: after Tasks.</summary>
	public const int ProjectsSearchOrder = 20;

	/// <summary>Defines the Segoe Fluent Folder glyph of a project.</summary>
	private const string ProjectGlyph = "\uE8B7";

	/// <summary>Defines the module that lists projects with its chats, and whose chats are the tasks, while it is loaded.</summary>
	private const string ConversationsModuleId = "conversations";

	/// <summary>Adds the Projects and Tasks search providers.</summary>
	private void RegisterSearch( ModuleBuilder builder ) => builder
		.SearchProvider( "projects", "Projects", ProjectsSearchOrder, context => ListProjects( context.App ) )
		.SearchProvider( "tasks", "Tasks", WorkspacesModule.TasksSearchOrder, context => ListTasks( context.App ) );

	/// <summary>Builds a project's Search palette item, with its directory on the right.</summary>
	/// <param name="project">The project.</param>
	/// <param name="open">Opens the project.</param>
	/// <param name="lastUsed">When the user last worked in it, or null.</param>
	/// <param name="actions">Its other actions, such as starting a task in it.</param>
	public static SearchItem BuildProjectItem( ProjectInfo project, Action open, DateTimeOffset? lastUsed, IReadOnlyList<MenuAction> actions ) =>
		new( project.Id.ToString(), project.Name, open )
		{
			Detail = project.Directory,
			Glyph = ProjectGlyph,
			LastUsed = lastUsed,
			Actions = actions,
		};

	/// <summary>Lists the active profile's projects; running one opens its session at the task it shows, and its action starts a new task there.</summary>
	private IEnumerable<SearchItem> ListProjects( LoknApplication app )
	{
		if ( app.Modules.Any( module => module.Id == ConversationsModuleId ) )
			return [];

		var opened = (FindWorkspaces( app )?.Sessions ?? []).ToDictionary( session => session.Key, session => session.LastOpened, StringComparer.Ordinal );
		return _store.Projects( app.Settings.ActiveProfile ).Select( project => BuildProjectItem( project, () => OpenProject( app, project.Id ),
			opened.TryGetValue( SessionKey( project.Id ), out var last ) ? last : null,
			[new MenuAction( "New task", () => NewTask( app, project.Id ) ) { AccessKey = Key.T }] ) );
	}

	/// <summary>Lists the tasks of the active profile's projects, each with its project's name; nothing while Conversations is loaded, whose chats are the tasks then.</summary>
	private IEnumerable<SearchItem> ListTasks( LoknApplication app )
	{
		if ( app.Modules.Any( module => module.Id == ConversationsModuleId ) || FindWorkspaces( app ) is not { } workspaces )
			return [];

		return _store.Projects( app.Settings.ActiveProfile ).SelectMany( project => GetTasks( app, project.Id ).Select( task =>
			workspaces.BuildTaskItem( app, SessionKey( project.Id ), task, project.Name, () => OpenTask( app, project.Id, task.Id ) ) ) );
	}
}
