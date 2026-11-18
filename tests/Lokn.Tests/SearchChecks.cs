using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Projects;
using Lokn.Modules.Workspaces;

namespace Lokn.Tests;

/// <summary>Checks the Search palette's registry: ranking, types and their tabs, <c>/type</c> filters, recents, provider ownership, and the built-in providers.</summary>
internal static class SearchChecks
{
	/// <summary>Runs the registry checks with fixture providers.</summary>
	internal static void Run()
	{
		CheckRanking();
		CheckTypes();
		CheckFilters();
		CheckRecents();
		CheckOwnership();
		Console.WriteLine( "PASS search: ranking by title start, word start, contains, keywords and fuzzy letters, shared type tabs, /type filters, recents per profile, failing providers, and providers leaving with their module." );
	}

	/// <summary>Runs the built-in providers over the shell, Workspaces, Projects and Conversations modules.</summary>
	internal static void RunFeatures()
	{
		var directory = Path.Combine( Path.GetTempPath(), "lokn-search-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( directory );
		CheckBuiltIn( directory );
		Directory.Delete( directory, recursive: true );
		Console.WriteLine( "PASS search providers: actions that can run with their shortcuts, open panels, settings opening Settings, tasks and projects without Conversations, then chats and projects with it, and no tasks or projects listed twice." );
	}

	/// <summary>Checks that matches rank by title start, then word start, then keywords, then containment, then fuzzy letters, and that every word must match.</summary>
	private static void CheckRanking()
	{
		var app = new LoknApplication();
		CoreChecks.Check( app.Load( new FixtureModule( "alpha", builder => builder.SearchProvider( "things", "Things", 10, _ =>
		[
			Item( "renew", "Renew license" ),
			Item( "start", "Start new task" ),
			Item( "new", "New chat" ),
			Item( "letter", "Gamma", keywords: ["newsletter"] ),
			Item( "detail", "Delta", detail: "news" ),
			Item( "fuzzy", "Nerd wide view" ),
			Item( "none", "Unrelated" ),
		] ) ) ), "A module registers a search provider." );
		CoreChecks.Check( Titles( app.Search.Search( "new", "things" ) ) is ["New chat", "Start new task", "Gamma", "Renew license", "Delta", "Nerd wide view"],
			"A title starting with the word ranks first, then a later word starting with it, then a keyword, then the title containing it, then the detail, then letters in order." );
		CoreChecks.Check( Titles( app.Search.Search( "nwv", "things" ) ) is ["Nerd wide view"], "Letters in order from a word start match fuzzily." );
		CoreChecks.Check( Titles( app.Search.Search( "new license", "things" ) ) is ["Renew license"], "Every word of the query has to match." );
		CoreChecks.Check( Titles( app.Search.Search( "NEW CHAT", "things" ) ) is ["New chat", ..], "Matching ignores case and prefers the whole query starting the title." );
		CoreChecks.Check( app.Search.Search( "zzz", "things" ).Count == 0, "A query nothing matches finds nothing." );
		app.Shutdown();
	}

	/// <summary>Checks that providers sharing a type share its tab, tabs follow their order, empty types are left out, and All groups matches by type.</summary>
	private static void CheckTypes()
	{
		var app = new LoknApplication();
		app.Load( new FixtureModule( "alpha", builder => builder
			.SearchProvider( "things", "Things", 20, _ => [Item( "a1", "Apple" ), Item( "a2", "Apricot" )] )
			.SearchProvider( "empty", "Empty", 5, _ => [] ) ) );
		app.Load( new FixtureModule( "beta", builder => builder
			.SearchProvider( "things", "Stuff", 30, _ => [Item( "b1", "Banana apple" )] )
			.SearchProvider( "others", "Others", 10, _ => [Item( "o1", "Apple pie" ), Item( "o2", "Orange" )] ) ) );

		CoreChecks.Check( app.Search.Types.Select( type => type.Id ).ToArray() is ["empty", "others", "things"] && app.Search.Types.Single( type => type.Id == "things" ).Title == "Things",
			"Types follow their order; a type several modules list takes the name of its lowest-ordered provider." );
		CoreChecks.Check( app.Search.ComputeTypes().Select( type => type.Id ).ToArray() is ["others", "things"], "A type whose providers list nothing has no tab." );
		CoreChecks.Check( Titles( app.Search.Search( "", "things" ) ) is ["Apple", "Apricot", "Banana apple"], "A type's tab lists every provider of the type, in provider order." );

		var all = app.Search.Search( "apple" );
		CoreChecks.Check( all.Select( group => group.Title ).ToArray() is ["Others", "Things"] && Titles( [all[1]] ) is ["Apple", "Banana apple"] && Titles( [all[0]] ) is ["Apple pie"],
			"All groups matches by type, the group with the best match first; ties keep the tab order." );
		CoreChecks.Check( Titles( app.Search.Search( "apple", "things" ) ) is ["Apple", "Banana apple"] && app.Search.Search( "apple", "things" ).Count == 1,
			"A type's tab lists its matches in one group." );

		var overview = app.Search.Search( "" );
		CoreChecks.Check( overview.Select( group => group.Title ).ToArray() is ["Others", "Things"] && overview.All( group => group.Hits.Count <= SearchRegistry.OverviewLimit ),
			"Without a query or recents, All shows each type's first items." );

		CoreChecks.Check( !app.Load( new FixtureModule( "bad", builder => builder.SearchProvider( "two words", "Bad", 0, _ => [] ) ) ), "A type with spaces is refused." );
		CoreChecks.Check( !app.Load( new FixtureModule( "twice", builder => builder.SearchProvider( "x", "X", 0, _ => [] ).SearchProvider( "x", "X", 0, _ => [] ) ) )
			&& app.Modules.All( module => module.Id != "twice" ), "A module listing one type twice is refused." );

		var calls = 0;
		app.Load( new FixtureModule( "broken", builder => builder.SearchProvider( "broken", "Broken", 0, _ =>
		{
			calls++;
			throw new InvalidOperationException( "provider failure" );
		} ) ) );
		CoreChecks.Check( Titles( app.Search.Search( "apple", null ) ).Count == 3 && calls > 0 && app.Search.ComputeTypes().All( type => type.Id != "broken" ),
			"A provider that throws lists nothing and the others still search." );
		app.Shutdown();
	}

	/// <summary>Checks <c>/type</c> parsing: choosing a type, a finished prefix by key, title or unique start, and text that names no type.</summary>
	private static void CheckFilters()
	{
		SearchType[] types = [new( "tasks", "Tasks", 10 ), new( "projects", "Projects", 20 ), new( "panels", "Panels", 40 )];
		CoreChecks.Check( SearchRegistry.ReadFilter( "new chat", types ) is { Type: null, Query: "new chat", Choices: null }, "Plain text is a plain query." );
		CoreChecks.Check( SearchRegistry.ReadFilter( "/", types ) is { Type: null, Choices.Count: 3 }, "A lone slash offers every type." );
		CoreChecks.Check( SearchRegistry.ReadFilter( "/p", types ).Choices?.Select( type => type.Id ).ToArray() is ["projects", "panels"], "A slash and letters offer the types starting with them." );
		CoreChecks.Check( SearchRegistry.ReadFilter( "/tasks fix bug", types ) is { Type.Id: "tasks", Query: "fix bug", Choices: null }, "A finished /type prefix picks the type and keeps what follows." );
		CoreChecks.Check( SearchRegistry.ReadFilter( "/Projects ", types ) is { Type.Id: "projects", Query: "" }, "A type's title picks it too, ignoring case." );
		CoreChecks.Check( SearchRegistry.ReadFilter( "/t x", types ) is { Type.Id: "tasks", Query: "x" }, "The start of one type's name picks it." );
		CoreChecks.Check( SearchRegistry.ReadFilter( "/p x", types ) is { Type: null, Query: "/p x" }, "A start several types share picks none and stays plain text." );
		CoreChecks.Check( SearchRegistry.ReadFilter( "/zzz x", types ) is { Type: null, Query: "/zzz x", Choices: null }, "A prefix naming no type stays plain text." );
	}

	/// <summary>Checks that the overview's Recents lists items run last, then those used most recently, per profile, and leaves them out of their type's group.</summary>
	private static void CheckRecents()
	{
		var app = new LoknApplication();
		var ran = new List<string>();
		app.Load( new FixtureModule( "alpha", builder => builder.SearchProvider( "things", "Things", 10, _ =>
		[
			Item( "old", "Old", lastUsed: DateTimeOffset.UnixEpoch.AddDays( 1 ) ),
			Item( "new", "Newer", lastUsed: DateTimeOffset.UnixEpoch.AddDays( 2 ) ),
			new SearchItem( "plain", "Plain", () => ran.Add( "plain" ) )
			{
				Actions = [new MenuAction( "Other", () => ran.Add( "other" ) ), new MenuAction( "Off", () => ran.Add( "off" ) ) { IsEnabled = false }],
			},
			Item( "fail", "Fail", run: () => throw new InvalidOperationException( "run failure" ) ),
		] ) ) );

		var overview = app.Search.Search( "" );
		CoreChecks.Check( overview[0] is { Title: "Recents", Type: null } && Titles( [overview[0]] ) is ["Newer", "Old"] && Titles( [overview[1]] ) is ["Plain", "Fail"],
			"Before anything runs, Recents lists the items used most recently, and their type's group leaves them out." );

		var plain = app.Search.Search( "plain" )[0].Hits[0];
		app.Search.Run( plain );
		CoreChecks.Check( ran is ["plain"] && app.Search.Recents is [{ ProviderId: "alpha.things", ItemId: "plain" }] && Titles( [app.Search.Search( "" )[0]] ) is ["Plain", "Newer", "Old"],
			"Running an item runs it and lists it first among recents." );
		app.Search.Run( plain, plain.Item.Actions[0] );
		app.Search.Run( plain, plain.Item.Actions[1] );
		CoreChecks.Check( ran is ["plain", "other"], "An item's other action runs; a disabled one does not." );

		var fail = app.Search.Search( "fail" )[0].Hits[0];
		app.Search.Run( fail );
		CoreChecks.Check( app.LastError?.Contains( "run failure" ) == true && Titles( [app.Search.Search( "" )[0]] ) is ["Fail", "Plain", "Newer", "Old"],
			"A failing item is reported in the shell notice and still counts as used." );

		app.Settings.ToggleProfile();
		CoreChecks.Check( app.Search.Recents.Count == 0 && Titles( [app.Search.Search( "" )[0]] ) is ["Newer", "Old"], "Each profile has its own recents." );
		app.Settings.ToggleProfile();
		CoreChecks.Check( app.Search.Recents.Count == 2, "Switching back finds the profile's recents again." );
		app.Shutdown();
	}

	/// <summary>Checks that unloading a module removes its providers and their tabs, and that enabling it again brings them back with their recents.</summary>
	private static void CheckOwnership()
	{
		var app = new LoknApplication();
		var beta = new FixtureModule( "beta", builder => builder.SearchProvider( "others", "Others", 10, _ => [Item( "o1", "Orange" )] ) );
		app.Load( new FixtureModule( "alpha", builder => builder.SearchProvider( "things", "Things", 20, _ => [Item( "a1", "Apple" )] ) ) );
		app.Load( beta );
		app.Search.Run( app.Search.Search( "orange" )[0].Hits[0] );
		CoreChecks.Check( app.Search.Providers.Select( provider => provider.Id ).ToArray() is ["beta.others", "alpha.things"], "Provider identifiers carry their module's identifier." );

		CoreChecks.Check( app.Unload( "beta" ) && app.Search.Providers.All( provider => provider.ModuleId != "beta" )
			&& app.Search.Types.Select( type => type.Id ).ToArray() is ["things"] && app.Search.Search( "orange" ).Count == 0
			&& app.Search.Search( "" ).All( group => group.Title != "Recents" ),
			"Unloading a module removes its providers, their tab and their items, recents included." );
		CoreChecks.Check( app.Enable( "beta" ) && Titles( [app.Search.Search( "" )[0]] ) is ["Orange"], "Enabling it again brings its items back with their place among recents." );
		app.Shutdown();
	}

	/// <summary>Checks the shell's, Workspaces', Projects' and Conversations' providers.</summary>
	private static void CheckBuiltIn( string directory )
	{
		var app = new LoknApplication();
		var profile = app.Settings.ActiveProfile;
		var shell = new ShellModule();
		var workspaces = new WorkspacesModule( Path.Combine( directory, "workspaces.json" ) );
		var store = new ConversationStore( new ProjectStore( Path.Combine( directory, "projects.json" ) ), Path.Combine( directory, "conversations.json" ) );
		var projects = new ProjectsModule( store.ProjectStore );
		CoreChecks.Check( app.Load( shell ) && app.Load( workspaces ) && app.Load( projects ), "The shell, Workspaces and Projects load." );

		var split = app.Search.Search( "split right", "actions" );
		CoreChecks.Check( split is [{ Hits: [{ Item: { Title: "Shell: split right", Detail: "Ctrl+\\" } }, ..] }], "Actions list commands with their shortcut on the right." );
		var current = profile == SettingsProfile.Work ? "Profile: switch to Work" : "Profile: switch to Personal";
		CoreChecks.Check( Titles( app.Search.Search( "", "actions" ) ).All( title => title != current ), "A command that cannot run now is left out of Actions." );

		var setting = app.Search.Search( ShellSettings.ShowStatusBar.Title, "settings" )[0].Hits[0];
		CoreChecks.Check( setting.Item.Detail == ShellSettings.ShowStatusBar.Category, "Settings list each setting with its category." );
		app.Search.Run( setting );
		var settings = app.Panels.Panels.SingleOrDefault( panel => panel.TypeId == "shell.settings" );
		CoreChecks.Check( settings is not null && app.Panels.ActivePanelId == settings.Id, "Running a setting opens Settings." );
		CoreChecks.Check( app.Search.Search( "settings", "panels" ) is [{ Hits: [{ Item.Title: "Settings" }, ..] }], "Panels list the open panels." );

		var task = workspaces.NewTask( app, WorkspacesModule.HomeSession, WorkspacesModule.HomeTitle, app.Panels.HideAll );
		workspaces.RenameTask( WorkspacesModule.HomeSession, task, "Fix the build" );
		var project = store.ProjectStore.Create( profile, "Ferrite", Path.Combine( directory, "ferrite" ) );
		projects.OpenProject( app, project.Id );
		var projectTask = workspaces.CurrentTask;
		workspaces.RenameTask( ProjectsModule.SessionKey( project.Id ), projectTask, "Bindless meshlets" );
		CoreChecks.Check( app.Search.ComputeTypes().Select( type => type.Id ).ToArray() is ["tasks", "projects", "panels", "actions", "settings"],
			"Without Conversations the tabs are Tasks, Projects, Panels, Actions and Settings." );
		var tasks = app.Search.Search( "", "tasks" )[0].Hits;
		CoreChecks.Check( tasks.Any( hit => hit.Item is { Title: "Fix the build", Detail: null } ) && tasks.Any( hit => hit.Item is { Title: "Bindless meshlets", Detail: "Ferrite" } ),
			"Tasks lists the tasks outside projects and each project's tasks with the project's name." );
		CoreChecks.Check( app.Search.Search( "ferrite", "projects" ) is [{ Hits: [{ Item: { Title: "Ferrite", LastUsed: not null, Actions: [{ Label: "New task" }] } }] }],
			"Projects lists the profile's projects with when they were last opened, and starts a task in one." );

		app.Search.Run( tasks.First( hit => hit.Item.Title == "Fix the build" ) );
		CoreChecks.Check( workspaces.CurrentSession == WorkspacesModule.HomeSession && workspaces.CurrentTask == task, "Running a task opens its session and shows it." );

		var conversations = new ConversationsModule( store, ( _, _, _ ) => new FixtureContent() ) { Dependencies = ["workspaces", "projects"] };
		CoreChecks.Check( app.Load( conversations ), "Conversations loads." );
		var chat = store.CreateConversation( profile, project.Id, "Meshlet renderer" );
		var types = app.Search.ComputeTypes().Select( type => type.Id ).ToArray();
		CoreChecks.Check( types is ["projects", "chats", "panels", "actions", "settings"], "With Conversations, its chats replace the tasks." );
		CoreChecks.Check( app.Search.Search( "ferrite", "projects" ) is [{ Hits: [{ Item.Actions: [{ Label: "New chat" }, { Label: "New task" }] }] }],
			"Projects lists each project once, from Conversations, which starts chats and tasks in it." );
		var found = app.Search.Search( "meshlet", "chats" )[0].Hits[0];
		CoreChecks.Check( found.Item is { Title: "Meshlet renderer", Detail: "Ferrite" }, "Chats list each chat with its project's name." );
		app.Search.Run( found, found.Item.Actions[0] );
		CoreChecks.Check( store.Get( chat.Id ).IsPinned, "A chat's Pin action pins it." );

		CoreChecks.Check( app.Unload( "conversations" ) && app.Search.ComputeTypes().Select( type => type.Id ).ToArray() is ["tasks", "projects", "panels", "actions", "settings"],
			"Unloading Conversations removes Chats and brings back Projects' own tasks and projects." );
		app.Shutdown();
	}

	/// <summary>Builds a fixture item.</summary>
	private static SearchItem Item( string id, string title, string? detail = null, IReadOnlyList<string>? keywords = null, DateTimeOffset? lastUsed = null, Action? run = null ) =>
		new( id, title, run ?? (() => { }) )
		{
			Detail = detail,
			Keywords = keywords ?? [],
			LastUsed = lastUsed,
		};

	/// <summary>Lists the titles of the hits of some groups in order.</summary>
	private static List<string> Titles( IReadOnlyList<SearchGroup> groups ) => [.. groups.SelectMany( group => group.Hits ).Select( hit => hit.Item.Title )];
}
