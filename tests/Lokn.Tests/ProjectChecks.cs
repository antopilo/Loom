using Goo;
using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Projects;
using Lokn.Modules.Workspaces;

namespace Lokn.Tests;

/// <summary>Checks the Projects module: projects moving out of an older conversation history, chats following deleted projects, the trusted modules setting moving to its new identifier, and the project list without Conversations.</summary>
internal static class ProjectChecks
{
	/// <summary>Loads a Projects module over a conversation store's projects, as the application loads it before Conversations.</summary>
	internal static bool LoadProjects( LoknApplication app, ConversationStore store ) => app.Load( new ProjectsModule( store.ProjectStore ) { Dependencies = [] } );

	/// <summary>Runs the project checks over isolated temporary files.</summary>
	internal static void Run()
	{
		var directory = Path.Combine( Path.GetTempPath(), "lokn-projects-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( directory );
		CheckMigration( directory );
		CheckSetting( directory );
		CheckNavigation( directory );
		Directory.Delete( directory, recursive: true );
		Console.WriteLine( "PASS projects: imported once from an older conversation history with chats kept in them, chats leaving deleted or missing projects, the trusted modules setting moved, and the project list and sessions without Conversations." );
	}

	/// <summary>Checks that an older history's projects move to the projects file once, chats keep them, and a chat whose project is gone leaves it.</summary>
	private static void CheckMigration( string directory )
	{
		var historyPath = Path.Combine( directory, "conversations.json" );
		var projectsPath = Path.Combine( directory, "projects.json" );
		var (app, work, gone) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
		var (inApp, inWork, loose, orphan) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
		var folder = directory.Replace( "\\", "\\\\" );
		File.WriteAllText( historyPath, $$$"""
			{"Version":2,"Projects":[
				{"Id":"{{{app}}}","Profile":"Personal","Name":"App","Directory":"{{{folder}}}\\app"},
				{"Id":"{{{work}}}","Profile":"Work","Name":"Office","Directory":"{{{folder}}}\\office"}],
			"Conversations":[
				{"Id":"{{{inApp}}}","Profile":"Personal","ProjectId":"{{{app}}}","Title":"App chat","UpdatedAt":"2026-01-01T00:00:00+00:00","Worktree":"{{{folder}}}\\app.worktrees\\x"},
				{"Id":"{{{inWork}}}","Profile":"Work","ProjectId":"{{{work}}}","Title":"Office chat","UpdatedAt":"2026-01-01T00:00:00+00:00"},
				{"Id":"{{{loose}}}","Profile":"Personal","ProjectId":null,"Title":"Loose chat","UpdatedAt":"2026-01-01T00:00:00+00:00"},
				{"Id":"{{{orphan}}}","Profile":"Personal","ProjectId":"{{{gone}}}","Title":"Orphan chat","UpdatedAt":"2026-01-01T00:00:00+00:00","Worktree":"{{{folder}}}\\gone"}]}
			""" );

		var projects = new ProjectStore( projectsPath, historyPath );
		var store = new ConversationStore( projects, historyPath );
		CoreChecks.Check( projects.LastError is null && projects.IsSaved && File.Exists( projectsPath )
			&& projects.Projects( SettingsProfile.Personal ) is [{ Name: "App" } personal] && personal.Id == app && personal.Directory == Path.Combine( directory, "app" )
			&& projects.Projects( SettingsProfile.Work ) is [{ Name: "Office" } office] && office.Id == work,
			"Without a projects file, the projects of the older conversation history are imported and written to it." );
		CoreChecks.Check( store.LastError is null && store.Get( inApp ) is { ProjectId: { } kept, Worktree: not null } && kept == app && store.Get( inWork ).ProjectId == work
			&& store.Get( loose ).ProjectId is null && store.Conversations( SettingsProfile.Personal, app ).Single().Id == inApp,
			"Chats keep their imported projects and worktrees." );
		CoreChecks.Check( store.Get( orphan ) is { ProjectId: null, Worktree: null, Title: "Orphan chat" }, "A chat whose project no longer exists leaves it instead of failing the history." );

		store.Flush();
		var history = File.ReadAllText( historyPath );
		CoreChecks.Check( !history.Contains( "\"Projects\"" ) && history.Contains( "Orphan chat" ), "Once the projects file holds them, the history no longer keeps projects." );

		// A history that still names projects, such as one written by an older Lokn, is not imported again once the projects file exists.
		var other = Guid.NewGuid();
		File.WriteAllText( historyPath, history.Replace( "{\"Version\":2,", $$$"""{"Version":2,"Projects":[{"Id":"{{{other}}}","Profile":"Personal","Name":"Other","Directory":"{{{folder}}}\\other"}],""" ) );
		var reopenedProjects = new ProjectStore( projectsPath, historyPath );
		var reopened = new ConversationStore( reopenedProjects, historyPath );
		CoreChecks.Check( reopenedProjects.Find( other ) is null && reopenedProjects.Projects( SettingsProfile.Personal ).Count == 1 && reopened.Get( inApp ).ProjectId == app,
			"A second start reads the projects file and does not import again; chats still belong to their projects." );

		reopenedProjects.Delete( work );
		reopened.Flush();
		var afterDelete = new ConversationStore( new ProjectStore( projectsPath ), historyPath );
		CoreChecks.Check( afterDelete.Get( inWork ) is { ProjectId: null, Title: "Office chat" } && afterDelete.ProjectStore.Find( work ) is null,
			"Deleting a project takes its chats out of it, keeping them." );

		// Projects that cannot be read are never taken for deleted: chats keep them, and the broken file is kept.
		File.WriteAllText( projectsPath, "{broken" );
		var broken = new ProjectStore( projectsPath, historyPath );
		var unsure = new ConversationStore( broken, historyPath );
		CoreChecks.Check( !broken.IsComplete && broken.LastError is not null && unsure.Get( inApp ).ProjectId == app && File.ReadAllText( projectsPath ) == "{broken",
			"An unreadable projects file is preserved, and chats keep their projects rather than leaving them." );

		// A failed import keeps the projects in the history, so the next start imports them again.
		var lockedHistory = Path.Combine( directory, "locked", "conversations.json" );
		var lockedProjects = Path.Combine( directory, "locked", "projects.json" );
		Directory.CreateDirectory( lockedProjects );
		File.WriteAllText( lockedHistory, $$$"""
			{"Version":2,"Projects":[{"Id":"{{{app}}}","Profile":"Personal","Name":"App","Directory":"{{{folder}}}\\app"}],
			"Conversations":[{"Id":"{{{inApp}}}","Profile":"Personal","ProjectId":"{{{app}}}","Title":"App chat","UpdatedAt":"2026-01-01T00:00:00+00:00"}]}
			""" );
		var unsaved = new ProjectStore( lockedProjects, lockedHistory );
		var carrying = new ConversationStore( unsaved, lockedHistory );
		carrying.SaveDraft( inApp, "Still here" );
		carrying.Flush();
		CoreChecks.Check( !unsaved.IsSaved && unsaved.LastError is not null && unsaved.Find( app ) is not null && File.ReadAllText( lockedHistory ).Contains( "\"Projects\"" )
			&& carrying.Get( inApp ).ProjectId == app,
			"When the projects file cannot be written, the history keeps its projects for the next start." );
	}

	/// <summary>Checks that the trusted project modules saved under Conversations' former setting move to the Projects module's.</summary>
	private static void CheckSetting( string directory )
	{
		var settingsPath = Path.Combine( directory, "settings.json" );
		File.WriteAllText( settingsPath, """
			{"Version":1,"ActiveProfile":"Personal",
			"Global":{"conversations.module-projects":"C:\\repo","shell.other":"kept"},
			"Work":{"conversations.module-projects":"C:\\work"},
			"Personal":{}}
			""" );
		var app = new LoknApplication( settingsPath );
		var module = new ProjectsModule( Path.Combine( directory, "setting-projects.json" ) ) { Dependencies = [] };
		CoreChecks.Check( app.Load( module ), "The Projects module loads." );
		var saved = File.ReadAllText( settingsPath );
		CoreChecks.Check( app.Settings.Get( ProjectsModule.TrustedModuleProjects, SettingsScope.Global ) == "C:\\repo"
			&& app.Settings.Get( ProjectsModule.TrustedModuleProjects, SettingsScope.Work ) == "C:\\work"
			&& !app.Settings.HasValue( ProjectsModule.TrustedModuleProjects, SettingsScope.Personal )
			&& !saved.Contains( "conversations.module-projects" ) && saved.Contains( "projects.module-projects" ) && saved.Contains( "shell.other" ),
			"Trusted project modules move from the Conversations setting to the Projects one in every scope, and the old identifier is forgotten." );
		app.Shutdown();

		var reopened = new LoknApplication( settingsPath );
		reopened.Load( new ProjectsModule( Path.Combine( directory, "setting-projects.json" ) ) { Dependencies = [] } );
		CoreChecks.Check( reopened.Settings.Get( ProjectsModule.TrustedModuleProjects, SettingsScope.Global ) == "C:\\repo", "The moved setting survives a restart." );
		reopened.Shutdown();
	}

	/// <summary>Checks that without Conversations the project list adds and opens projects in their own Workspaces session, and that it steps aside for Conversations.</summary>
	private static void CheckNavigation( string directory )
	{
		var app = new LoknApplication();
		var workspaces = new WorkspacesModule( Path.Combine( directory, "workspaces.json" ) );
		var module = new ProjectsModule( Path.Combine( directory, "navigation-projects.json" ) );
		CoreChecks.Check( app.Load( workspaces ) && app.Load( module ), "Projects loads after Workspaces without Conversations." );
		var sidebar = app.Slots.Get( ShellSlots.Sidebar ).Single( item => item.ModuleId == "projects" ).Content;
		var tabs = app.Slots.Get( ShellSlots.Tabs ).Single( item => item.ModuleId == "projects" ).Content;
		CoreChecks.Check( sidebar.Build() is Container { Children.Count: > 0 } && tabs.Build() is Row,
			"Without Conversations, Projects lists the projects in the sidebar and the tab strip." );

		var project = module.Store.Create( app.Settings.ActiveProfile, "Notes", Path.Combine( directory, "notes" ) );
		app.Panels.Open( "workspaces.grid" );
		module.OpenProject( app, project );
		CoreChecks.Check( workspaces.CurrentSession == ProjectsModule.SessionKey( project.Id ) && ProjectsModule.ReadSession( workspaces.CurrentSession ) == project.Id
			&& app.Panels.Panes is [{ PanelId: null }] && module.FindCurrentProject( app ) == project.Id,
			"Opening a project opens its own Workspaces session, first as one empty pane." );
		CoreChecks.Check( ProjectsModule.SessionKey( project.Id, LoknHost.LocalId ) == ProjectsModule.SessionKey( project.Id ) && ProjectsModule.SessionKey( project.Id ) == $"project:{project.Id:N}"
			&& ProjectsModule.SessionKey( project.Id, "h1" ) == $"project:{project.Id:N}@h1" && ProjectsModule.ReadSession( ProjectsModule.SessionKey( project.Id, "h1" ) ) is null,
			"A paired host's project key is qualified with the host, This PC's stays as it was, and only This PC's keys read as Projects' own." );

		// The project's group holds its heading row and its one task at the same level; the tasks outside projects follow under Tasks.
		var task = workspaces.Tasks( ProjectsModule.SessionKey( project.Id ) ).Single();
		var taskList = app.Slots.Get( ShellSlots.Sidebar ).Single( item => item.ModuleId == "workspaces" ).Content;
		CoreChecks.Check( sidebar.Build() is Container { Children: [Container { Key: "content", Children: var rows }] }
			&& rows.OfType<Container>().SingleOrDefault( row => row.Key == project.Id.ToString() ) is { Children: [Row, Container { Key: var taskKey }] } && taskKey == task.Id.ToString()
			&& rows.Any( row => row is Text { Content: "Tasks" } ) && taskList.Build() is Container { Children: [] },
			"The project sidebar lists each project with its tasks and then the tasks outside projects, and the Workspaces list steps aside." );

		// A project without an icon image shows the emoji the user chose, which survives a restart, reaches a mirror, and goes away when removed.
		CoreChecks.Check( !module.HasImageIcon( project, LoknHost.LocalId ) && module.BuildProjectIcon( project, LoknHost.LocalId, 16 ) is null,
			"A project whose folder has no icon image and no chosen emoji has no icon." );
		var mirror = new ProjectStore();
		mirror.Load( module.Store.Projects( app.Settings.ActiveProfile ) );
		module.Store.Mutated += mirror.Apply;
		module.Store.SetIcon( project.Id, " 🦊 " );
		var shown = module.Store.Get( project.Id );
		CoreChecks.Check( shown.Icon == "🦊" && mirror.Get( project.Id ).Icon == "🦊"
			&& new ProjectStore( Path.Combine( directory, "navigation-projects.json" ) ).Get( project.Id ).Icon == "🦊"
			&& module.BuildProjectIcon( shown, LoknHost.LocalId, 16 ) is Container { Children: [Text { Content: "🦊" }] }
			&& Descendants( sidebar.Build() ).OfType<Text>().Any( text => text.Content == "🦊" ),
			"A chosen emoji is saved, survives a restart, reaches a mirror, and shows as the project's icon in the sidebar." );
		CoreChecks.Check( ProjectRows.ComputeIcon( "👩🏽‍💻 and more" ) == "👩🏽‍💻" && ProjectRows.ComputeIcon( "  " ) == "",
			"The icon field keeps one whole emoji, modifiers and joiners included, from what is typed or pasted." );
		module.Store.SetIcon( project.Id, "" );
		CoreChecks.Check( module.Store.Get( project.Id ).Icon is null && mirror.Get( project.Id ).Icon is null && module.BuildProjectIcon( module.Store.Get( project.Id ), LoknHost.LocalId, 16 ) is null,
			"Removing the icon clears it here and in the mirror." );
		module.Store.Mutated -= mirror.Apply;

		app.Load( new FixtureModule( "conversations", _ => { } ) );
		CoreChecks.Check( sidebar.Build() is Container { Children: [] } && tabs.Build() is Container { Children: [] },
			"With Conversations loaded, the project list steps aside." );
		app.Shutdown();
	}

	/// <summary>Walks a view's blobs, including content inside a profile slide.</summary>
	private static IEnumerable<Blob> Descendants( Blob blob )
	{
		yield return blob;
		if ( blob.GetType().GetProperty( "Children" )?.GetValue( blob ) is IEnumerable<Blob> children )
		{
			foreach ( var child in children.SelectMany( Descendants ) )
				yield return child;
		}
		var fields = blob.GetType().GetFields( System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public );
		foreach ( var slide in fields.Select( field => field.GetValue( blob ) ).OfType<ProfileSlideInput>() )
		{
			foreach ( var child in Descendants( slide.Content ) )
				yield return child;
		}
	}
}
