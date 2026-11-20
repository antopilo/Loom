using Lokn.Core;
using Lokn.Modules.Workspaces;

namespace Lokn.Modules.Projects;

/// <summary>Contributes projects: a named working folder per profile, each opening a Workspaces session of its own, with its icon and the modules it carries.</summary>
/// <remarks>
/// The sidebar or tab strip lists the active profile's projects while the Conversations module, which groups its chats under them,
/// is not loaded. Other modules read and change projects through <see cref="Store"/> and open one with <see cref="OpenProject"/>.
/// </remarks>
public sealed partial class ProjectsModule : ILoknModule, IWorkingFolder
{
	/// <summary>Defines the prefix of a project's Workspaces session key.</summary>
	public const string SessionPrefix = "project:";

	/// <summary>Stores the projects.</summary>
	private readonly ProjectStore _store;

	/// <summary>Stores the running module context.</summary>
	private ModuleContext? _context;

	/// <summary>Creates the module over the per-user projects file, importing the projects of an older conversation history once; used when the application loads the module by itself.</summary>
	public ProjectsModule() : this( new ProjectStore( ProjectStore.DefaultPath, ProjectStore.LegacyPath ) )
	{
	}

	/// <summary>Creates the module over a chosen projects file, for checks and other hosts.</summary>
	/// <param name="path">The projects file.</param>
	/// <param name="legacyPath">The conversation history file whose projects are imported while the projects file does not exist, or null.</param>
	internal ProjectsModule( string path, string? legacyPath = null ) : this( new ProjectStore( path, legacyPath ) )
	{
	}

	/// <summary>Creates the module over a supplied store, for checks that also give it to a conversation store.</summary>
	internal ProjectsModule( ProjectStore store ) => _store = store;

	/// <summary>Gets the module identifier.</summary>
	public string Id => "projects";

	/// <summary>Gets the display name.</summary>
	public string Title => "Projects";

	/// <summary>Gets the modules required first: Workspaces, which keeps each project's session.</summary>
	public IReadOnlyList<string> Dependencies { get; init; } = ["workspaces"];

	/// <summary>Gets the projects of every profile.</summary>
	public ProjectStore Store => _store;

	/// <summary>Registers the trusted project modules setting, the project list in the sidebar and the tab strip, the point other sessions' folders come from,
	/// the open session's folder as the working folder, and the projects and their tasks in the Search palette.</summary>
	public void Register( ModuleBuilder builder )
	{
		builder
			.Setting( TrustedModuleProjects )
			.ExtensionPoint<SessionFolder>( "folders" )
			.Slot( ShellSlots.Sidebar, 50, context => new ProjectNavigation( context, this, isTabs: false ) )
			.Slot( ShellSlots.Tabs, 50, context => new ProjectNavigation( context, this, isTabs: true ) )
			.Service<IWorkingFolder>( this );
		RegisterSearch( builder );
	}

	/// <summary>Reads the projects, moves the trusted project modules saved under Conversations' former setting, and loads the trusted projects' modules.</summary>
	public void Start( ModuleContext context )
	{
		_context = context;
		_store.EnsureLoaded();
		if ( _store.LastError is { } error )
			context.App.ReportError( "Projects", new IOException( error ) );
		_store.Failed += MarkFailed;

		context.App.Settings.MoveValues( LegacyTrustedModuleProjects, TrustedModuleProjects );
		context.App.Settings.Changed += UpdateProjectModulesOnChange;
		UpdateProjectModules( context.App );
	}

	/// <summary>Detaches from the store and settings and releases the project icons; every change is already written.</summary>
	public void Stop()
	{
		_store.Failed -= MarkFailed;
		_context?.App.Settings.Changed -= UpdateProjectModulesOnChange;
		ReleaseIcons();

		// Project modules stay contributed: a reload of this module starts again and replaces them, rather than closing their panels.
		_context = null;
	}

	/// <summary>Gets a project's Workspaces session key: <c>project:&lt;id&gt;</c>, followed by <c>@hostId</c> for a paired host's project.</summary>
	/// <param name="projectId">The project.</param>
	/// <param name="hostId">The host the project is on, or null or <see cref="LoknHost.LocalId"/> for This PC.</param>
	public static string SessionKey( Guid projectId, string? hostId = null ) =>
		hostId is null || hostId == LoknHost.LocalId ? SessionPrefix + projectId.ToString( "N" ) : $"{SessionPrefix}{projectId:N}@{hostId}";

	/// <summary>Reads the This PC project a Workspaces session key stands for, or null for any other session, a paired host's project's included.</summary>
	public static Guid? ReadSession( string? key ) =>
		key is not null && key.StartsWith( SessionPrefix, StringComparison.Ordinal ) && Guid.TryParseExact( key[SessionPrefix.Length..], "N", out var id ) ? id : null;

	/// <summary>Opens a project's session with its saved workspaces and layout, first loading the modules a This PC project carries once they are trusted.</summary>
	/// <param name="app">The application.</param>
	/// <param name="project">The project, This PC's or a paired host's.</param>
	/// <param name="hostId">The host the project is on, or null for This PC.</param>
	/// <param name="createLayout">Builds the session's first layout; by default one empty pane, with every open panel kept, hidden.</param>
	/// <remarks>Without the Workspaces module, the first layout is built every time.</remarks>
	public void OpenProject( LoknApplication app, ProjectInfo project, string? hostId = null, Action? createLayout = null )
	{
		// A project added since startup may bring modules; ask about them, and load trusted ones before its layout looks for their panels.
		UpdateHostModules( app, hostId );
		createLayout ??= app.Panels.HideAll;
		if ( FindWorkspaces( app ) is { } workspaces )
			workspaces.OpenSession( app, SessionKey( project.Id, hostId ), project.Name, createLayout );
		else
			createLayout();
	}

	/// <summary>Opens a project's session and starts a new, empty task in it, named <paramref name="name"/>, such as the + grid starter it begins with.</summary>
	/// <param name="app">The application.</param>
	/// <param name="project">The project, This PC's or a paired host's.</param>
	/// <param name="hostId">The host the project is on, or null for This PC.</param>
	/// <param name="name">The task's name.</param>
	public void NewTask( LoknApplication app, ProjectInfo project, string? hostId = null, string name = WorkspacesModule.NewTaskName )
	{
		UpdateHostModules( app, hostId );
		if ( FindWorkspaces( app ) is { } workspaces )
			workspaces.NewTask( app, SessionKey( project.Id, hostId ), project.Name, app.Panels.HideAll, name );
	}

	/// <summary>Opens a project's session and shows one of its tasks.</summary>
	/// <param name="app">The application.</param>
	/// <param name="project">The project, This PC's or a paired host's.</param>
	/// <param name="task">The task.</param>
	/// <param name="hostId">The host the project is on, or null for This PC.</param>
	public void OpenTask( LoknApplication app, ProjectInfo project, Guid task, string? hostId = null )
	{
		UpdateHostModules( app, hostId );
		FindWorkspaces( app )?.OpenTask( app, SessionKey( project.Id, hostId ), project.Name, task, app.Panels.HideAll );
	}

	/// <summary>Loads the modules This PC's projects carry; a paired host's projects bring none.</summary>
	private void UpdateHostModules( LoknApplication app, string? hostId )
	{
		if ( hostId is null || hostId == LoknHost.LocalId )
			UpdateProjectModules( app );
	}

	/// <summary>Gets a project's tasks, or none before its session is first opened or without Workspaces; subscribes the building view.</summary>
	internal IReadOnlyList<WorkspaceTask> GetTasks( LoknApplication app, Guid id, string? hostId = null ) => FindWorkspaces( app )?.Tasks( SessionKey( id, hostId ) ) ?? [];

	/// <summary>Gets the open task when a project's session is open, or empty; subscribes the building view.</summary>
	internal Guid GetCurrentTask( LoknApplication app, Guid id, string? hostId = null ) =>
		FindWorkspaces( app ) is { } workspaces && workspaces.CurrentSession == SessionKey( id, hostId ) ? workspaces.CurrentTask : Guid.Empty;

	/// <summary>Gets the active profile's project whose session is open, or null in any other session; reading it rebuilds the reader when the session changes.</summary>
	internal Guid? FindCurrentProject( LoknApplication app ) =>
		ReadSession( FindWorkspaces( app )?.CurrentSession ) is { } id && _store.Find( id )?.Profile == app.Settings.ActiveProfile ? id : null;

	/// <summary>Gets the open session's folder, so terminals and other tools start there: the open project's directory, else the folder a
	/// <see cref="SessionFolder"/> gives for another session, such as a task's; null when none covers it.</summary>
	public string? Current
	{
		get
		{
			if ( _context is not { } context )
				return null;
			if ( FindCurrentProject( context.App ) is { } id )
				return _store.Find( id )?.Directory;
			if ( FindWorkspaces( context.App )?.CurrentSession is not { } session )
				return null;

			foreach ( var folder in context.App.Extensions.Get<SessionFolder>( SessionFolder.Point ) )
			{
				if ( folder.Compute( context.App, session ) is { } directory )
					return directory;
			}
			return null;
		}
	}

	/// <summary>Gets the Workspaces module, which keeps sessions, or null when it is not loaded.</summary>
	internal static WorkspacesModule? FindWorkspaces( LoknApplication app ) =>
		app.Modules.Any( module => module.Id == "workspaces" ) ? app.GetModule<WorkspacesModule>() : null;

	/// <summary>Shows a projects file failure in the shell notice; the store reports on the UI thread.</summary>
	private void MarkFailed( string error ) => _context?.App.ReportError( "Projects", new IOException( error ) );
}
