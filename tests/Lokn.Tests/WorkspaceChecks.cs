using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Projects;
using Lokn.Modules.Workspaces;

namespace Lokn.Tests;

/// <summary>Checks saved workspaces: layout capture and apply, hidden panels, reload renames, and the Workspaces store and tools.</summary>
internal static class WorkspaceChecks
{
	/// <summary>Runs the layout and store checks headlessly.</summary>
	internal static async Task RunAsync()
	{
		CheckLayouts();
		CheckPlacement();
		await CheckStoreAsync();
		CheckSessions();
		CheckTasks();
		CheckRecency();
		CheckTabSelection();
		CheckTabStrip();
		Console.WriteLine( "PASS saved workspaces: layout capture and apply, hidden panels kept alive, hide all and show in place, matching by id and state, reload renames, store persistence, sessions with their own workspaces, version 1 migration, delete and switch tools, task rename, pin and delete in a session that is not open, task last use on switch, new and open with files that predate it, and the tab strip's task groups." );
	}

	/// <summary>Checks hiding every panel and showing a panel in a chosen pane, both without closing anything.</summary>
	private static void CheckPlacement()
	{
		var app = new LoknApplication();
		var closes = new Dictionary<string, int>();
		app.Load( new FixtureModule( "fixture", builder => builder.Panel( "note", "Note", context => new NoteContent( context.Input as string ?? "", closes ) ) ) );
		var left = app.Panels.Open( "fixture.note", input: "left" );
		var right = app.Panels.Open( "fixture.note", input: "right" );

		app.Panels.HideAll();
		CoreChecks.Check( app.Panels.Root is DockPane { PanelId: null } && app.Panels.Panels.Count == 2 && closes.Count == 0 && app.Panels.ActivePaneId == app.Panels.Root.Id,
			"Hiding all leaves one empty active pane and closes nothing." );

		var pane = app.Panels.Root.Id;
		CoreChecks.Check( app.Panels.Show( left, pane ) && app.Panels.Root is DockPane { PanelId: var shown } && shown == left, "Showing a hidden panel fills the chosen pane." );
		CoreChecks.Check( app.Panels.Show( right, pane ) && app.Panels.IsShown( right ) && !app.Panels.IsShown( left ) && app.Panels.Panes.Count == 1 && closes.Count == 0,
			"Showing a panel in an occupied pane hides the one it held instead of splitting." );

		var other = app.Panels.Open( "fixture.note", input: "other" );
		CoreChecks.Check( app.Panels.Panes.Count == 2 && app.Panels.Show( other, pane ) && app.Panels.Panes.Count == 1 && app.Panels.ActivePanelId == other && !app.Panels.IsShown( right ),
			"Showing a panel that is on screen elsewhere moves it, collapsing the pane it left." );
		CoreChecks.Check( !app.Panels.Show( Guid.NewGuid(), pane ) && !app.Panels.Show( left, Guid.NewGuid() ), "Unknown panels and panes are refused." );
		app.Shutdown();
	}

	/// <summary>Checks that each session keeps its own workspaces and layout, that leaving one keeps its panels alive, and that version 1 files move into Chats.</summary>
	private static void CheckSessions()
	{
		var path = Path.Combine( Path.GetTempPath(), "lokn-sessions-" + Guid.NewGuid().ToString( "N" ) + ".json" );
		var app = new LoknApplication();
		var closes = new Dictionary<string, int>();
		app.Load( new FixtureModule( "fixture", builder => builder.Panel( "note", "Note", context => new NoteContent( context.RestoredState?.GetString() ?? context.Input as string ?? "", closes ) ) ) );
		var module = new WorkspacesModule( path );
		app.Load( module );
		CoreChecks.Check( module.CurrentSession == "home", "A first start is in the home session." );
		var changes = 0;
		module.SessionChanged += () => changes++;

		var created = 0;
		void Fill( string name )
		{
			created++;
			app.Panels.HideAll();
			app.Panels.Open( "fixture.note", input: name );
		}

		module.OpenSession( app, "project:a", "Alpha", () => Fill( "alpha" ) );
		var alpha = app.Panels.ActivePanelId!.Value;
		var alphaSide = app.Panels.Open( "fixture.note", input: "alpha-side" );
		module.Create( app );
		CoreChecks.Check( module.CurrentSession == "project:a" && created == 1 && module.Store.Items.Count == 2, "A new session builds its first layout and gets workspaces of its own." );

		module.OpenSession( app, "project:b", "Beta", () => Fill( "beta" ) );
		CoreChecks.Check( app.Panels.Panes.Count == 1 && !app.Panels.IsShown( alpha ) && !app.Panels.IsShown( alphaSide ) && closes.Count == 0 && module.Store.Items.Count == 1,
			"Opening another session shows its layout and workspaces, keeping the first session's panels alive and hidden." );

		module.OpenSession( app, "project:a", "Alpha renamed", () => Fill( "never" ) );
		CoreChecks.Check( created == 2 && app.Panels.IsShown( alpha ) && app.Panels.IsShown( alphaSide ) && app.Panels.Panes.Count == 2 && module.Store.Items.Count == 2,
			"Returning to a session shows the same panels in its last layout without rebuilding it." );
		CoreChecks.Check( module.Sessions[0] is { Key: "project:a", Title: "Alpha renamed" } && module.Sessions.Count == 3, "Sessions list the most recently opened first under their latest title." );
		module.OpenSession( app, "project:a", "Alpha renamed", () => Fill( "never" ) );
		CoreChecks.Check( changes == 3 && created == 2, "SessionChanged is raised each time another session opens, and not when the open one is opened again." );

		var reloaded = new WorkspaceStore( path );
		reloaded.Load();
		CoreChecks.Check( reloaded.SessionKey == "project:a" && reloaded.Items.Count == 2 && reloaded.Sessions.Any( session => session.Key == "project:b" ), "Sessions and the current one are saved." );
		app.Shutdown();

		var legacy = Guid.NewGuid();
		File.WriteAllText( path, $$"""{ "Version": 1, "Current": "{{legacy}}", "Items": [ { "Id": "{{legacy}}", "Name": "Review", "Layout": null } ] }""" );
		var migrated = new WorkspaceStore( path );
		migrated.Load();
		CoreChecks.Check( migrated.LastError is null && migrated.SessionKey == "chats" && migrated.Items is [{ Name: "Review" }] && migrated.Current == legacy,
			"A version 1 file's workspaces move into the Chats session, which stays current." );
		File.Delete( path );
	}

	/// <summary>Checks renaming, pinning and deleting the tasks of a session that is not open, that pins are saved, that files without pins load, and the sidebar's task list.</summary>
	private static void CheckTasks()
	{
		var path = Path.Combine( Path.GetTempPath(), "lokn-tasks-" + Guid.NewGuid().ToString( "N" ) + ".json" );
		var app = new LoknApplication();
		app.Load( new FixtureModule( "fixture", builder => builder.Panel( "note", "Note", context => new NoteContent( context.Input as string ?? "", [] ) ) ) );
		var module = new WorkspacesModule( path );
		app.Load( module );
		var main = module.CurrentTask;
		var notes = module.NewTask( app, WorkspacesModule.HomeSession, WorkspacesModule.HomeTitle, app.Panels.HideAll );
		var sidebar = app.Slots.Get( ShellSlots.Sidebar ).Single( item => item.ModuleId == "workspaces" ).Content;
		CoreChecks.Check( sidebar.Build() is Container { Children: var rows } && rows.Any( row => row is Text { Content: "Tasks" } ) && rows.Count( row => row is Container { Key: { } key } && (key == main.ToString() || key == notes.ToString()) ) == 2,
			"Without Projects, the sidebar lists the tasks outside projects under Tasks." );

		module.OpenSession( app, "project:a", "Alpha", app.Panels.HideAll );
		var shown = app.Panels.Open( "fixture.note", input: "alpha" );
		module.RenameTask( WorkspacesModule.HomeSession, notes, "  Notes  " );
		module.SetPinned( WorkspacesModule.HomeSession, notes, true );
		CoreChecks.Check( module.Tasks( WorkspacesModule.HomeSession ) is [{ Name: "Main", IsPinned: false }, { Name: "Notes", IsPinned: true }] && module.CurrentSession == "project:a",
			"A task of a session that is not open is renamed and pinned without opening it." );

		var reloaded = new WorkspaceStore( path );
		reloaded.Load();
		CoreChecks.Check( reloaded.ItemsOf( WorkspacesModule.HomeSession ) is [{ IsPinned: false }, { Name: "Notes", IsPinned: true }], "Pins and names are saved." );

		CoreChecks.Check( module.DeleteTask( app, WorkspacesModule.HomeSession, notes ) && module.Tasks( WorkspacesModule.HomeSession ) is [{ Name: "Main" }]
			&& module.CurrentSession == "project:a" && app.Panels.IsShown( shown ),
			"Deleting the current task of a session that is not open leaves the open one on screen." );
		CoreChecks.Check( !module.DeleteTask( app, WorkspacesModule.HomeSession, main ), "A session keeps its last task." );
		module.OpenSession( app, WorkspacesModule.HomeSession, WorkspacesModule.HomeTitle, () => { } );
		CoreChecks.Check( module.CurrentTask == main, "A session whose current task was deleted opens its neighbor." );
		app.Shutdown();

		var task = Guid.NewGuid();
		File.WriteAllText( path, $$"""{ "Version": 2, "Session": "home", "Sessions": [ { "Key": "home", "Title": "Home", "Current": "{{task}}", "Items": [ { "Id": "{{task}}", "Name": "Old", "Layout": null } ], "LastOpened": "2026-01-01T00:00:00+00:00" } ] }""" );
		var old = new WorkspaceStore( path );
		old.Load();
		CoreChecks.Check( old.LastError is null && old.Items is [{ Name: "Old", IsPinned: false }], "A file written before pins loads with every task unpinned." );
		File.Delete( path );
	}

	/// <summary>Checks that a task's last use is kept when it starts, is switched to or left, or is opened with its session, and is saved; files without it fall back to the session's last opening.</summary>
	private static void CheckRecency()
	{
		var path = Path.Combine( Path.GetTempPath(), "lokn-recency-" + Guid.NewGuid().ToString( "N" ) + ".json" );
		var app = new LoknApplication();
		app.Load( new FixtureModule( "fixture", builder => builder.Panel( "note", "Note", context => new NoteContent( context.Input as string ?? "", [] ) ) ) );
		var module = new WorkspacesModule( path );
		app.Load( module );
		DateTimeOffset LastUsed( string session, Guid id ) => module.Tasks( session ).Single( task => task.Id == id ).LastUsed;
		var home = WorkspacesModule.HomeSession;
		var main = module.CurrentTask;

		var started = DateTimeOffset.UtcNow;
		var notes = module.NewTask( app, home, WorkspacesModule.HomeTitle, app.Panels.HideAll );
		CoreChecks.Check( LastUsed( home, notes ) >= started && LastUsed( home, main ) >= started, "A new task counts as used when it starts, and so does the task it leaves." );

		var switched = DateTimeOffset.UtcNow;
		module.Switch( app, main );
		CoreChecks.Check( LastUsed( home, main ) >= switched && LastUsed( home, notes ) >= switched, "Switching marks the task shown and the task left as used." );

		var opened = DateTimeOffset.UtcNow;
		module.OpenSession( app, "project:a", "Alpha", app.Panels.HideAll );
		var alpha = module.CurrentTask;
		CoreChecks.Check( LastUsed( "project:a", alpha ) >= opened && LastUsed( home, main ) >= opened, "Opening a session marks the task it shows as used." );

		var reopened = DateTimeOffset.UtcNow;
		module.OpenTask( app, home, WorkspacesModule.HomeTitle, notes, app.Panels.HideAll );
		CoreChecks.Check( module.CurrentTask == notes && LastUsed( home, notes ) >= reopened, "Opening a task in another session marks it as used." );

		var reloaded = new WorkspaceStore( path );
		reloaded.Load();
		CoreChecks.Check( reloaded.ItemsOf( home ).All( item => item.LastUsed >= started ), "Last use is saved with each task." );
		app.Shutdown();

		var current = Guid.NewGuid();
		var other = Guid.NewGuid();
		File.WriteAllText( path, $$"""{ "Version": 2, "Session": "home", "Sessions": [ { "Key": "home", "Title": "Home", "Current": "{{current}}", "Items": [ { "Id": "{{other}}", "Name": "Other", "Layout": null }, { "Id": "{{current}}", "Name": "Shown", "Layout": null } ], "LastOpened": "2026-01-01T00:00:00+00:00" } ] }""" );
		var oldApp = new LoknApplication();
		var old = new WorkspacesModule( path );
		oldApp.Load( old );
		CoreChecks.Check( old.Store.LastError is null && old.Tasks( home ) is [{ Name: "Other" } first, { Name: "Shown" } second]
			&& first.LastUsed == DateTimeOffset.MinValue && second.LastUsed == new DateTimeOffset( 2026, 1, 1, 0, 0, 0, TimeSpan.Zero ),
			"A file written before tasks kept their last use counts the task its session shows as used when the session was last opened, and the others never." );
		oldApp.Shutdown();
		File.Delete( path );
	}

	/// <summary>Checks the tab strip's choice of tabs: the current tab always, pinned and recent tabs within the caps, groups by recency, and empty groups left out; and its stable order.</summary>
	private static void CheckTabSelection()
	{
		var now = new DateTimeOffset( 2026, 11, 17, 12, 0, 0, TimeSpan.Zero );
		RecentTab Make( string name, double daysAgo, bool isPinned = false ) => new( Guid.NewGuid(), name, isPinned, now - TimeSpan.FromDays( daysAgo ) );
		static string[] Names( RecentTabGroup group ) => [.. group.Tabs.Select( tab => tab.Title )];

		var stale = Make( "stale current", 40 );
		var groups = new[]
		{
			new RecentTabGroup( "project:b", [Make( "b old", 10 ), Make( "b pinned", 20, isPinned: true )] ),
			new RecentTabGroup( "project:c", [Make( "c old", 30 )] ),
			new RecentTabGroup( "project:a", [Make( "a1", 1 ), Make( "a2", 2 ), Make( "a3", 3 ), Make( "a0", 0.5 )] ),
			new RecentTabGroup( "project:d", [Make( "d1", 1.5 )] ),
			new RecentTabGroup( "home", [Make( "h recent", 0.1 ), stale] ),
		};
		var shown = RecentTabs.ComputeShown( groups, "home", stale.Id, now );
		CoreChecks.Check( shown.Select( group => group.Key ).SequenceEqual( ["home", "project:a", "project:d", "project:b"] ),
			"Groups follow their most recently used tab, the current one counting as used now, and a group with nothing recent, pinned or current is left out." );
		CoreChecks.Check( Names( shown[0] ).SequenceEqual( ["stale current", "h recent"] ) && Names( shown[1] ).SequenceEqual( ["a0", "a1", "a2"] ) && Names( shown[3] ).SequenceEqual( ["b pinned"] ),
			"A group shows the current tab however old, pinned tabs, and tabs of the last 7 days, at most 3, most recently used first." );

		var many = Enumerable.Range( 0, 4 ).Select( index => new RecentTabGroup( $"project:{index}", [Make( $"{index}a", 1 + index ), Make( $"{index}b", 1.1 + index ), Make( $"{index}c", 1.2 + index )] ) ).ToList();
		var current = Make( "current", 30 );
		many.Add( new RecentTabGroup( "home", [Make( "recent 1", 0.1 ), Make( "recent 2", 0.2 ), Make( "recent 3", 0.3 ), current] ) );
		var capped = RecentTabs.ComputeShown( many, "home", current.Id, now );
		CoreChecks.Check( capped.Sum( group => group.Tabs.Count ) == RecentTabs.TotalLimit && Names( capped[0] ).SequenceEqual( ["current", "recent 1", "recent 2"] )
			&& capped.Select( group => group.Key ).SequenceEqual( ["home", "project:0", "project:1"] ) && capped[2].Tabs.Count == 2,
			"The strip shows at most 8 tabs, the current one first in its group and counted toward both caps; groups left without a tab are left out." );

		var empty = RecentTabs.ComputeShown( [new RecentTabGroup( "project:z", [Make( "z old", 30 )] ), new RecentTabGroup( "home", [Make( "h old", 30 )] )], "project:z", Guid.NewGuid(), now );
		CoreChecks.Check( empty is [{ Key: "project:z", Tabs: [] }], "The current group stays with nothing to show; other empty groups are left out." );

		var order = new RecentTabOrder();
		RecentTab a = Make( "a", 1 ), b = Make( "b", 2 ), c = Make( "c", 3 ), d = Make( "d", 4 );
		static string Describe( IReadOnlyList<RecentTabGroup> arranged ) => string.Join( " | ", arranged.Select( group => group.Key + ":" + string.Join( ",", group.Tabs.Select( tab => tab.Title ) ) ) );
		order.Arrange( [new( "g1", [a, b] ), new( "g2", [c] )], a.Id );
		order.MarkOwnAction();
		var kept = order.Arrange( [new( "g2", [c] ), new( "g1", [b, a] ), new( "g3", [d] )], b.Id );
		CoreChecks.Check( Describe( kept ) == "g1:a,b | g2:c | g3:d", "After the strip's own click, groups and tabs keep their places and newly shown ones go last." );
		var background = order.Arrange( [new( "g2", [c] ), new( "g1", [b, a] )], b.Id );
		CoreChecks.Check( Describe( background ) == "g1:a,b | g2:c", "A rebuild that does not change the current tab keeps the order, dropping what left." );
		var resorted = order.Arrange( [new( "g2", [c] ), new( "g1", [b, a] )], c.Id );
		CoreChecks.Check( Describe( resorted ) == "g2:c | g1:b,a", "When the current tab changes elsewhere, the strip takes the recency order again." );
	}

	/// <summary>Checks that Projects draws the tab strip's groups, its own and the Tasks outside projects, while the Workspaces strip steps aside, and that Workspaces draws the Tasks group without Projects.</summary>
	private static void CheckTabStrip()
	{
		var directory = Path.Combine( Path.GetTempPath(), "lokn-tabs-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( directory );
		var app = new LoknApplication();
		var workspaces = new WorkspacesModule( Path.Combine( directory, "workspaces.json" ) );
		var projects = new ProjectsModule( Path.Combine( directory, "projects.json" ) );
		CoreChecks.Check( app.Load( workspaces ) && app.Load( projects ), "Projects loads after Workspaces." );
		var projectTabs = app.Slots.Get( ShellSlots.Tabs ).Single( item => item.ModuleId == "projects" ).Content;
		var taskTabs = app.Slots.Get( ShellSlots.Tabs ).Single( item => item.ModuleId == "workspaces" ).Content;
		var home = workspaces.CurrentTask;

		var project = projects.Store.Create( app.Settings.ActiveProfile, "Notes", Path.Combine( directory, "notes" ) );
		projects.OpenProject( app, project );
		var session = ProjectsModule.SessionKey( project.Id );
		var task = workspaces.CurrentTask;
		CoreChecks.Check( projectTabs.Build() is Row { Children: [Row { Key: var first, Children: [Button { Key: "label" }, Button { Key: var tab }, Button { Key: "new" }] }, Row { Key: "home", Children: [_, Button { Key: var homeTab }, _] }, Button { Key: "new-home-task" }, Button { Key: "add" }] }
			&& first == session && tab == task.ToString() && homeTab == home.ToString() && taskTabs.Build() is Container { Children: [] },
			"With Projects, its strip shows the open project's group first, then the Tasks group with the task just left, then the + and new-project buttons; the Workspaces strip steps aside." );

		app.Unload( "projects" );
		CoreChecks.Check( taskTabs.Build() is Row { Children: [Row { Key: "home" }, Button { Key: "new-home-task" }] }, "Without Projects, the Workspaces strip shows the Tasks group and its +." );
		app.Load( new FixtureModule( "conversations", _ => { } ) );
		CoreChecks.Check( taskTabs.Build() is Container { Children: [] }, "With Conversations loaded, the Workspaces strip steps aside." );
		app.Shutdown();
		Directory.Delete( directory, recursive: true );
	}

	/// <summary>Checks that applying a layout reuses open panels, hides the rest without closing them, and reopens what is missing.</summary>
	private static void CheckLayouts()
	{
		var app = new LoknApplication();
		var closes = new Dictionary<string, int>();
		app.Load( new FixtureModule( "fixture", builder => builder.Panel( "note", "Note", context => new NoteContent( context.RestoredState?.GetString() ?? context.Input as string ?? "", closes ) ) ) );
		var sidebar = app.Panels.Open( "fixture.note", input: "sidebar" );
		var dev = app.Panels.Open( "fixture.note", input: "dev" );
		var devLayout = app.Panels.CaptureLayout();

		app.Panels.Close( dev );
		closes.Clear();
		var review = app.Panels.Open( "fixture.note", input: "review" );
		app.Panels.Move( review, app.Panels.Panes.First( pane => pane.PanelId == sidebar ).Id, PanelPlacement.Bottom );
		var reviewLayout = app.Panels.CaptureLayout();

		CoreChecks.Check( app.Panels.ApplyLayout( devLayout ), "A captured layout applies." );
		var devAgain = app.Panels.Panels.Single( panel => panel.Id != sidebar && panel.Id != review ).Id;
		CoreChecks.Check( app.Panels.IsShown( sidebar ) && app.Panels.IsShown( devAgain ) && !app.Panels.IsShown( review ) && closes.Count == 0,
			"Applying a layout keeps the shared panel, reopens a closed one from its state, and hides the panel it leaves out without closing it." );
		CoreChecks.Check( app.Panels.Root is DockSplit { Axis: SplitAxis.Horizontal, First: DockPane { PanelId: var left } } && left == sidebar,
			"The applied tree has the saved shape." );

		CoreChecks.Check( app.Panels.ApplyLayout( reviewLayout ) && app.Panels.IsShown( review ) && !app.Panels.IsShown( devAgain ) && app.Panels.Panels.Count == 3,
			"Switching back shows the hidden panel itself rather than a copy." );
		CoreChecks.Check( app.Panels.Root is DockSplit { Axis: SplitAxis.Vertical }, "Each layout keeps its own split shape." );

		app.Panels.Activate( devAgain );
		CoreChecks.Check( app.Panels.IsShown( devAgain ) && app.Panels.ActivePanelId == devAgain, "Activating a hidden panel brings it back into the current layout." );
		app.Panels.ApplyLayout( reviewLayout );
		CoreChecks.Check( !app.Panels.IsShown( devAgain ) && app.Panels.Close( devAgain ) && closes.GetValueOrDefault( "dev" ) == 1 && app.Panels.Panels.Count == 2,
			"A hidden panel can be closed, running its close hook once." );

		var saved = Path.Combine( Path.GetTempPath(), "lokn-workspace-" + Guid.NewGuid().ToString( "N" ) + ".json" );
		app.Panels.ApplyLayout( devLayout );
		app.Panels.SaveLayout( saved );
		CoreChecks.Check( !File.ReadAllText( saved ).Contains( "\"review\"" ), "The saved workspace file holds the shown panels only." );
		File.Delete( saved );

		CoreChecks.Check( !app.Panels.ApplyLayout( JsonDocument.Parse( "{\"Version\":1}" ).RootElement ) && app.Panels.IsShown( sidebar ), "A malformed layout changes nothing." );
		app.ClearError();

		var hidden = app.Panels.Panels.Single( panel => !app.Panels.IsShown( panel.Id ) ).Id;
		app.Reload( new FixtureModule( "fixture", builder => builder.Panel( "note", "Note", context => new NoteContent( context.RestoredState?.GetString() ?? "", closes ) ) ) );
		CoreChecks.Check( app.Panels.Panels.Count == 3 && app.Panels.Panels.Count( panel => !app.Panels.IsShown( panel.Id ) ) == 1 && app.Panels.Panels.All( panel => panel.Id != hidden ),
			"A module reload reopens its hidden panels hidden." );
		var count = app.Panels.Panels.Count;
		app.Panels.ApplyLayout( reviewLayout );
		CoreChecks.Check( app.Panels.Panels.Count == count && app.Panels.Panels.Count( panel => app.Panels.IsShown( panel.Id ) ) == 2,
			"Layouts saved before a reload find the reopened panels instead of opening copies." );
		app.Shutdown();
	}

	/// <summary>Checks creating, switching, renaming, deleting and persisting workspaces, and the agent tools.</summary>
	private static async Task CheckStoreAsync()
	{
		var path = Path.Combine( Path.GetTempPath(), "lokn-workspaces-" + Guid.NewGuid().ToString( "N" ) + ".json" );
		var app = new LoknApplication();
		app.Load( new FixtureModule( "fixture", builder => builder.Panel( "note", "Note", context => new NoteContent( context.RestoredState?.GetString() ?? context.Input as string ?? "", [] ) ) ) );
		var module = new WorkspacesModule( path );
		CoreChecks.Check( app.Load( module ), "The Workspaces module loads." );
		var store = module.Store;
		CoreChecks.Check( store.Items is [{ Name: "Main" }] && store.Current == store.Items[0].Id, "With no saved workspaces, the current layout is Main." );

		var sidebar = app.Panels.Open( "fixture.note", input: "sidebar" );
		var main = store.Current;
		var dev = module.Create( app );
		store.Rename( dev, "  Dev  " );
		var devPanel = app.Panels.Open( "fixture.note", input: "dev" );
		CoreChecks.Check( store.Items.Select( item => item.Name ).SequenceEqual( ["Main", "Dev"] ) && store.Current == dev, "A new workspace takes the current layout, and names are trimmed." );

		module.Switch( app, main );
		CoreChecks.Check( app.Panels.IsShown( sidebar ) && !app.Panels.IsShown( devPanel ), "Switching to Main hides Dev's extra panel." );
		var tools = await app.Tools.InvokeAsync( "workspaces_switch", JsonDocument.Parse( """{ "workspace": "dev" }""" ).RootElement, null, CancellationToken.None );
		CoreChecks.Check( !tools.IsError && store.Current == dev && app.Panels.IsShown( devPanel ), "The switch tool finds a workspace by name and brings its panels back." );

		var reloaded = new WorkspaceStore( path );
		reloaded.Load();
		CoreChecks.Check( reloaded.Items.Count == 2 && reloaded.Current == dev && reloaded.Items.First( item => item.Id == main ).Layout is not null,
			"Workspaces, the current one, and the layout last left in each are saved." );

		CoreChecks.Check( store.Delete( app.Panels, dev ) && store.Current == main && !app.Panels.IsShown( devPanel ), "Deleting the current workspace switches to its neighbor first." );
		CoreChecks.Check( !store.Delete( app.Panels, main ) && store.Items.Count == 1, "The last workspace cannot be deleted." );

		File.WriteAllText( path, "{broken" );
		var broken = new WorkspaceStore( path );
		broken.Load();
		broken.Create( app.Panels, "Scratch" );
		CoreChecks.Check( broken.LastError is not null && File.ReadAllText( path ) == "{broken", "An unreadable workspaces file is reported and left untouched." );
		File.Delete( path );
		app.Shutdown();
	}

	/// <summary>Holds a named note and counts closes per name.</summary>
	private sealed class NoteContent( string name, Dictionary<string, int> closes ) : PanelContent
	{
		/// <summary>Builds the note's name.</summary>
		public override Blob Build() => new Text( name );

		/// <summary>Captures the name, which identifies the note.</summary>
		public override JsonElement? CaptureState() => JsonSerializer.SerializeToElement( name );

		/// <summary>Counts the close.</summary>
		public override void Closed() => closes[name] = closes.GetValueOrDefault( name ) + 1;
	}
}
