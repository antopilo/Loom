using Goo;
using Lokn.Core;

namespace Lokn.Modules.Workspaces;

/// <summary>Contributes saved workspaces: named panel layouts switched from a grid of tiles, by shortcut, or by an agent.</summary>
public sealed partial class WorkspacesModule : ILoknModule, IPanelPlaces
{
	/// <summary>Defines the registered grid panel identifier.</summary>
	public const string GridPanelType = "workspaces.grid";

	/// <summary>Stores the saved workspaces while the module runs.</summary>
	private WorkspaceStore? _store;

	/// <summary>Stores the running module context.</summary>
	private ModuleContext? _context;

	/// <summary>Creates the module over the per-user workspaces file; used when the application loads the module by itself.</summary>
	public WorkspacesModule() : this( WorkspaceStore.DefaultPath )
	{
	}

	/// <summary>Creates the module over a chosen workspaces file, for checks and other hosts.</summary>
	internal WorkspacesModule( string path ) => Path = path;

	/// <summary>Gets the module identifier.</summary>
	public string Id => "workspaces";

	/// <summary>Gets the display name.</summary>
	public string Title => "Workspaces";

	/// <summary>Keeps saved workspaces on for the application lifetime: chats, tasks and projects keep their layouts in them.</summary>
	public bool CanUnload => false;

	/// <summary>Gets the workspaces file.</summary>
	internal string Path
	{
		get;
	}

	/// <summary>Gets the saved workspaces once the module has started.</summary>
	internal WorkspaceStore Store => _store ?? throw new InvalidOperationException( "The Workspaces module has not started." );

	/// <summary>Registers the grid, the sidebar's action and task-starter points, switching commands, agent tools, and the tasks outside projects in the Search palette.</summary>
	public void Register( ModuleBuilder builder )
	{
		builder
			.Panel( "grid", "Workspaces", context => new WorkspacesContent( context, this ) )
			.ExtensionPoint<SidebarAction>( "sidebar-actions" )
			.ExtensionPoint<TaskStarter>( "task-starters" )
			.Slot( ShellSlots.Sidebar, 100, context => new WorkspaceNavigation( context, this, isTabs: false ) )
			.Slot( ShellSlots.Tabs, 100, context => new WorkspaceNavigation( context, this, isTabs: true ) )
			.Command( "new", "Workspaces: save layout as new workspace", context => Create( context.App ) )
			.SearchProvider( "tasks", "Tasks", TasksSearchOrder, context => ListTasks( context.App ) )
			.Service<IPanelPlaces>( this )
			.Command( "next", "Workspaces: next workspace", context => SwitchBy( context.App, 1 ),
				new( Key.Right, new KeyModifiers { Ctrl = true, Alt = true } ) )
			.Command( "previous", "Workspaces: previous workspace", context => SwitchBy( context.App, -1 ),
				new( Key.Left, new KeyModifiers { Ctrl = true, Alt = true } ) )
			.Tool( "list",
				"Lists the user's saved workspaces (named panel layouts such as Dev or Review) and which one is current.",
				"""{ "type": "object", "properties": {}, "additionalProperties": false }""",
				( context, _ ) => AgentToolResult.Json( new
				{
					Current = Store.Current,
					Workspaces = Store.Items.Select( item => new
					{
						item.Id,
						item.Name,
						IsCurrent = item.Id == Store.Current ? true : (bool?)null,
					} ),
				} ) )
			.Tool( "switch",
				"Switches to a saved workspace, sliding panels into its layout. Panels it leaves out stay open but hidden, so nothing stops. The panel you run in may be hidden by the switch.",
				"""
				{
					"type": "object",
					"properties": { "workspace": { "type": "string", "description": "A workspace name or id from workspaces_list." } },
					"required": ["workspace"],
					"additionalProperties": false
				}
				""",
				( context, arguments ) =>
				{
					var key = arguments.TryGetProperty( "workspace", out var value ) ? value.GetString() ?? "" : "";
					var workspace = Store.Find( key ) ?? throw new ArgumentException( $"No workspace is named {key}; call workspaces_list." );
					return Store.Switch( context.App.Panels, workspace.Id )
						? new( $"Switched to {workspace.Name}." )
						: AgentToolResult.Error( $"{workspace.Name}'s layout could not be applied." );
				} )
			.Tool( "save",
				"Saves the current panel layout as a new workspace with a name, and makes it current. Nothing on screen changes.",
				"""
				{
					"type": "object",
					"properties": { "name": { "type": "string", "description": "The workspace's name, such as Review." } },
					"required": ["name"],
					"additionalProperties": false
				}
				""",
				( context, arguments ) =>
				{
					var name = arguments.TryGetProperty( "name", out var value ) ? value.GetString() : null;
					var id = Store.Create( context.App.Panels, name );
					return new( $"Saved the layout as {Store.Items.First( item => item.Id == id ).Name}." );
				} );
	}

	/// <summary>Reports whether two panels are in the same workspace: both on screen, or both hidden in one saved layout.</summary>
	public bool AreTogether( Guid first, Guid second )
	{
		if ( _context is not { } context || _store is not { } store )
			return false;

		var panels = context.App.Panels;
		var isFirstShown = panels.IsShown( first );
		if ( isFirstShown != panels.IsShown( second ) )
			return false;

		return isFirstShown || store.HoldsTogether( panels, panels.ResolvePanelId( first ), panels.ResolvePanelId( second ) );
	}

	/// <summary>Reads the saved workspaces; on a first start the current session becomes the active profile's.</summary>
	public void Start( ModuleContext context )
	{
		_context = context;
		_store = new WorkspaceStore( Path );
		_store.Load();
		_store.EnsureProfile( context.App.Settings.ActiveProfile );
		ReportFailure();
	}

	/// <summary>Releases the store; the live layout is saved by the workspace itself.</summary>
	public void Stop()
	{
		_store = null;
		_context = null;
	}

	/// <summary>Gets the current session's key, such as home or project:&lt;id&gt;, subscribing the building view.</summary>
	public string CurrentSession => Store.SessionKey;

	/// <summary>Raised on the UI thread after the current session changed, whoever opened the other one, such as so the host it belongs to becomes current.</summary>
	public event Action? SessionChanged;

	/// <summary>Gets every session, most recently opened first, subscribing the building view.</summary>
	public IReadOnlyList<WorkspaceSessionInfo> Sessions => Store.Sessions;

	/// <summary>Gets a profile's current session key, or null when it has not opened one; subscribes the building view.</summary>
	public string? SessionFor( SettingsProfile profile ) => Store.SessionFor( profile );

	/// <summary>Forgets a session that is not current, such as a deleted conversation's.</summary>
	public void RemoveSession( string key )
	{
		Store.RemoveSession( key );
		ReportFailure();
	}

	/// <summary>Finds the session a hidden panel belongs to: the most recently opened one whose saved workspaces hold it, or null.</summary>
	public string? FindSession( LoknApplication app, Guid panelId ) => Store.FindSession( app.Panels, panelId );

	/// <summary>Switches to the workspace of the current session whose saved layout holds a hidden panel, so it shows where the user left it.</summary>
	/// <returns>Whether the panel is on screen now.</returns>
	public bool ShowWorkspaceOf( LoknApplication app, Guid panelId )
	{
		if ( app.Panels.IsShown( panelId ) )
			return true;

		var isSwitched = Store.FindWorkspace( app.Panels, panelId ) is { } id && Store.Switch( app.Panels, id );
		ReportFailure();
		return isSwitched && app.Panels.IsShown( panelId );
	}

	/// <summary>Opens a session for the active profile: the current one's layout is kept, and the session's current workspace is shown with the panels it holds.</summary>
	/// <remarks>Raises <see cref="SessionChanged"/> once the session opened when it is another one.</remarks>
	/// <param name="app">The application.</param>
	/// <param name="key">The session key; one per project or conversation, plus whatever other places the caller defines.</param>
	/// <param name="title">The session's display name.</param>
	/// <param name="createLayout">Builds the session's first layout when it has none yet, or when its saved one cannot be applied.</param>
	public void OpenSession( LoknApplication app, string key, string title, Action createLayout )
	{
		var previous = Store.SessionKey;
		Store.OpenSession( app.Panels, key, title, app.Settings.ActiveProfile, createLayout );
		ReportFailure();
		if ( Store.SessionKey != previous )
			SessionChanged?.Invoke();
	}

	/// <summary>Defines the session of the tasks outside projects.</summary>
	public const string HomeSession = WorkspaceStore.DefaultSession;

	/// <summary>Defines the display name of the session of the tasks outside projects.</summary>
	public const string HomeTitle = "Home";

	/// <summary>Defines the name of a task the user just started.</summary>
	public const string NewTaskName = "New task";

	/// <summary>Gets a session's tasks in order, or none when it was never opened; subscribes the building view.</summary>
	/// <remarks>A task saved before tasks kept their last use counts as used when its session was last opened if the session shows it, otherwise never.</remarks>
	public IReadOnlyList<WorkspaceTask> Tasks( string session )
	{
		var current = Store.CurrentOf( session );
		var opened = Store.LastOpenedOf( session );
		return [.. Store.ItemsOf( session ).Select( item => new WorkspaceTask( item.Id, item.Name, item.IsPinned,
			item.LastUsed ?? (item.Id == current ? opened : DateTimeOffset.MinValue) )
		{
			// The task on screen is the live layout; the others are as the user left them.
			PanelTypes = session == Store.SessionKey && item.Id == Store.Current && _context is { } context
				? [.. context.App.Panels.Panels.Where( panel => context.App.Panels.IsShown( panel.Id ) ).Select( panel => panel.TypeId ).Distinct()]
				: ReadPanelTypes( item.Layout ),
		} )];
	}

	/// <summary>Reads the types of the panels a saved layout shows, in tree order without repeats, or none when it has no layout.</summary>
	internal static IReadOnlyList<string> ReadPanelTypes( System.Text.Json.JsonElement? layout )
	{
		if ( layout is not { ValueKind: System.Text.Json.JsonValueKind.Object } root
			|| !root.TryGetProperty( "Panels", out var panels ) || panels.ValueKind != System.Text.Json.JsonValueKind.Array
			|| !root.TryGetProperty( "Root", out var tree ) )
			return [];

		var types = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var panel in panels.EnumerateArray() )
		{
			if ( panel.TryGetProperty( "Id", out var id ) && panel.TryGetProperty( "TypeId", out var type ) && type.GetString() is { } name )
				types[id.ToString()] = name;
		}

		var shown = new List<string>();
		void Visit( System.Text.Json.JsonElement node )
		{
			if ( node.ValueKind != System.Text.Json.JsonValueKind.Object )
				return;
			if ( node.TryGetProperty( "PanelId", out var id ) && id.ValueKind == System.Text.Json.JsonValueKind.String && types.TryGetValue( id.GetString()!, out var type ) && !shown.Contains( type ) )
				shown.Add( type );
			if ( node.TryGetProperty( "First", out var first ) )
				Visit( first );
			if ( node.TryGetProperty( "Second", out var second ) )
				Visit( second );
		}

		Visit( tree );
		return shown;
	}

	/// <summary>Renames a task of any session; a blank name keeps the old one.</summary>
	public void RenameTask( string session, Guid id, string name )
	{
		Store.Rename( session, id, name );
		ReportFailure();
	}

	/// <summary>Pins a task of any session, which lists it first, or unpins it.</summary>
	public void SetPinned( string session, Guid id, bool isPinned )
	{
		Store.SetPinned( session, id, isPinned );
		ReportFailure();
	}

	/// <summary>Moves a task of any session to another's place in its order; the tasks between shift toward where it was.</summary>
	public void MoveTask( string session, Guid id, Guid target )
	{
		Store.Move( session, id, target );
		ReportFailure();
	}

	/// <summary>Deletes a task of any session, first switching away from it when it is on screen; a session keeps its last task.</summary>
	/// <returns>Whether the task was deleted.</returns>
	public bool DeleteTask( LoknApplication app, string session, Guid id )
	{
		var isDeleted = Store.Delete( app.Panels, session, id );
		ReportFailure();
		return isDeleted;
	}

	/// <summary>Gets the current session's current task, subscribing the building view.</summary>
	public Guid CurrentTask => Store.Current;

	/// <summary>Opens a session, then one of its tasks.</summary>
	/// <param name="app">The application.</param>
	/// <param name="session">The session key, such as <see cref="HomeSession"/> or a project's.</param>
	/// <param name="title">The session's display name.</param>
	/// <param name="task">The task to show.</param>
	/// <param name="createLayout">Builds the session's first layout when it has none.</param>
	public void OpenTask( LoknApplication app, string session, string title, Guid task, Action createLayout )
	{
		OpenSession( app, session, title, createLayout );
		Switch( app, task );
	}

	/// <summary>Opens a session and starts a new, empty task in it: the task it leaves keeps its layout, and every panel is hidden.</summary>
	/// <remarks>A session opened for the first time gets its first task from opening; that task becomes the new one rather than a second.</remarks>
	/// <param name="app">The application.</param>
	/// <param name="session">The session's key.</param>
	/// <param name="title">The session's display name.</param>
	/// <param name="createLayout">Builds the session's first layout when it opens for the first time.</param>
	/// <param name="name">The task's name, such as the + grid starter it begins with.</param>
	/// <returns>The new task.</returns>
	public Guid NewTask( LoknApplication app, string session, string title, Action createLayout, string name = NewTaskName )
	{
		var isFirst = Store.ItemsOf( session ).Count == 0;
		OpenSession( app, session, title, createLayout );
		Guid id;
		if ( isFirst )
		{
			id = Store.Current;
			Store.Rename( id, name );
		}
		else
		{
			id = Store.Create( app.Panels, name );
		}

		app.Panels.HideAll();
		ReportFailure();
		return id;
	}

	/// <summary>Switches to a workspace and reports a store failure, for the grid and commands.</summary>
	internal void Switch( LoknApplication app, Guid id )
	{
		Store.Switch( app.Panels, id );
		ReportFailure();
	}

	/// <summary>Saves the current layout as a new workspace.</summary>
	internal Guid Create( LoknApplication app )
	{
		var id = Store.Create( app.Panels );
		ReportFailure();
		return id;
	}

	/// <summary>Switches to the workspace a number of tiles away, wrapping around.</summary>
	private void SwitchBy( LoknApplication app, int step )
	{
		var items = Store.Items;
		var index = items.ToList().FindIndex( item => item.Id == Store.Current );
		Switch( app, items[((index + step) % items.Count + items.Count) % items.Count].Id );
	}

	/// <summary>Shows a workspaces file failure in the shell notice.</summary>
	private void ReportFailure()
	{
		if ( _store?.LastError is { } error )
			_context?.App.ReportError( "Workspaces", new IOException( error ) );
	}
}
