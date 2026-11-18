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

	/// <summary>Gets a project's Workspaces session key.</summary>
	public static string SessionKey( Guid projectId ) => SessionPrefix + projectId.ToString( "N" );

	/// <summary>Reads the project a Workspaces session key stands for, or null for any other session.</summary>
	public static Guid? ReadSession( string? key ) =>
		key is not null && key.StartsWith( SessionPrefix, StringComparison.Ordinal ) && Guid.TryParseExact( key[SessionPrefix.Length..], "N", out var id ) ? id : null;

	/// <summary>Opens a project's session with its saved workspaces and layout, first loading the modules it carries once they are trusted.</summary>
	/// <param name="app">The application.</param>
	/// <param name="id">The project.</param>
	/// <param name="createLayout">Builds the session's first layout; by default one empty pane, with every open panel kept, hidden.</param>
	/// <remarks>Without the Workspaces module, the first layout is built every time.</remarks>
	public void OpenProject( LoknApplication app, Guid id, Action? createLayout = null )
	{
		var project = _store.Get( id );

		// A project added since startup may bring modules; ask about them, and load trusted ones before its layout looks for their panels.
		UpdateProjectModules( app );
		createLayout ??= app.Panels.HideAll;
		if ( FindWorkspaces( app ) is { } workspaces )
			workspaces.OpenSession( app, SessionKey( project.Id ), project.Name, createLayout );
		else
			createLayout();
	}

	/// <summary>Opens a project's session and starts a new, empty task in it, named <paramref name="name"/>, such as the + grid starter it begins with.</summary>
	public void NewTask( LoknApplication app, Guid id, string name = WorkspacesModule.NewTaskName )
	{
		var project = _store.Get( id );
		UpdateProjectModules( app );
		if ( FindWorkspaces( app ) is { } workspaces )
			workspaces.NewTask( app, SessionKey( project.Id ), project.Name, app.Panels.HideAll, name );
	}

	/// <summary>Opens a project's session and shows one of its tasks.</summary>
	public void OpenTask( LoknApplication app, Guid id, Guid task )
	{
		var project = _store.Get( id );
		UpdateProjectModules( app );
		FindWorkspaces( app )?.OpenTask( app, SessionKey( project.Id ), project.Name, task, app.Panels.HideAll );
	}

	/// <summary>Gets a project's tasks, or none before its session is first opened or without Workspaces; subscribes the building view.</summary>
	internal IReadOnlyList<WorkspaceTask> GetTasks( LoknApplication app, Guid id ) => FindWorkspaces( app )?.Tasks( SessionKey( id ) ) ?? [];

	/// <summary>Gets the open task when a project's session is open, or empty; subscribes the building view.</summary>
	internal Guid GetCurrentTask( LoknApplication app, Guid id ) =>
		FindWorkspaces( app ) is { } workspaces && workspaces.CurrentSession == SessionKey( id ) ? workspaces.CurrentTask : Guid.Empty;

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
