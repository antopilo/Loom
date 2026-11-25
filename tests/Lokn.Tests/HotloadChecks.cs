using System.Diagnostics;
using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules;
using Lokn.Modules.Hotload;

namespace Lokn.Tests;

/// <summary>Exercises in-place module replacement and source modules compiled at runtime.</summary>
internal static class HotloadChecks
{
	/// <summary>Defines how long a check waits for the file watcher, in seconds.</summary>
	private const int WatchTimeoutSeconds = 20;

	/// <summary>Defines the source module template; {0} is the panel title.</summary>
	private const string NotesSource = """
		using Goo;
		using Lokn.Core;

		public sealed class NotesModule : ILoknModule
		{{
			public string Id => "notes";
			public string Title => "Notes";

			public void Register( ModuleBuilder builder )
			{{
				builder.Panel( "note", "{0}", context => new NoteContent() );
			}}
		}}

		internal sealed class NoteContent : PanelContent
		{{
			public override Blob Build() => new Text
			{{
				Content = "{0}",
			}};
		}}
		""";

	/// <summary>Runs the reload checks without opening a native window.</summary>
	internal static void Run()
	{
		CheckReload();
		CheckGroupReload();
		CheckModuleHost();
		CheckDaemonModuleHost();
		CheckSourceModules();
		CheckCompileCache();
		CheckModuleFolders();
		CheckProjectModules();
		Console.WriteLine( "PASS module reload in place, reload fallback, group reload, module DLL loading and replacement, the daemon's reloads held back while a chat works and its modules following the module states file, source module compile, edit, failure, and removal, cached builds across starts, user module folders, and trusted project modules." );
	}

	/// <summary>Verifies a reload keeps panes, restores captured panel state, and survives a broken replacement.</summary>
	private static void CheckReload()
	{
		var app = new LoknApplication();
		var restored = new List<int>();
		FixtureModule BuildModule( string title, Action<ModuleContext>? start = null ) => new( "live", builder => builder.Panel( "panel", title, context =>
		{
			if ( context.RestoredState is { } state )
				restored.Add( state.GetInt32() );

			return new StatefulContent( restored.Count );
		} ), start );

		CoreChecks.Check( app.Reload( BuildModule( "First" ) ) && app.Modules.Single().Id == "live", "Reloading an unknown module loads it." );
		app.Load( new FixtureModule( "other", builder => builder.Panel( "panel", "Other", _ => new FixtureContent() ) ) );
		app.Panels.Open( "live.panel" );
		var other = app.Panels.Open( "other.panel" );
		app.Panels.Open( "live.panel" );
		var panes = app.Panels.Panes.Select( pane => pane.Id ).ToArray();
		var active = app.Panels.ActivePaneId;
		var previous = app.Panels.Sessions.Select( session => session.Content ).OfType<StatefulContent>().ToArray();

		CoreChecks.Check( app.Reload( BuildModule( "Second" ) ), "A loaded module reloads." );
		CoreChecks.Check( app.Panels.Panes.Select( pane => pane.Id ).SequenceEqual( panes ) && app.Panels.ActivePaneId == active, "Reload keeps the pane tree and active pane." );
		CoreChecks.Check( app.Panels.Panes[1].PanelId == other, "Reload leaves other modules' panels untouched." );
		CoreChecks.Check( previous.All( content => content.CloseCount == 1 ) && restored.Count == 2, "Reload closes old panels once and restores their captured state." );
		CoreChecks.Check( app.Panels.Panels.Count( panel => panel.Title == "Second" ) == 2, "Reloaded panels come from the replacement factory." );
		CoreChecks.CheckInvariants( app.Panels );

		CoreChecks.Check( !app.Reload( BuildModule( "Broken", _ => throw new InvalidOperationException( "startup failure" ) ) ), "A replacement that fails startup is rejected." );
		CoreChecks.Check( app.Panels.Panels.Count( panel => panel.Title == "Second" ) == 2 && app.LastError!.Contains( "startup failure" ), "A failed reload falls back to the running implementation and its panels." );

		CoreChecks.Check( app.Reload( new FixtureModule( "live", _ => { } ) ) && app.Panels.Panels.Single().Id == other, "Panels of a removed type collapse their panes." );
		CoreChecks.CheckInvariants( app.Panels );

		app.Unload( "live" );
		CoreChecks.Check( app.Reload( BuildModule( "Third" ) ) && app.Modules.All( module => module.Id != "live" ), "Reloading a disabled module keeps it disabled." );
		CoreChecks.Check( app.Enable( "live" ) && app.Panels.Types.Any( type => type.Title == "Third" ), "Enabling starts the replacement." );
		CoreChecks.Check( app.Remove( "live" ) && !app.Enable( "live" ), "A removed module can no longer be enabled." );
		app.Shutdown();
	}

	/// <summary>Verifies a module and its dependent reload as one step and fall back together.</summary>
	private static void CheckGroupReload()
	{
		var app = new LoknApplication();
		var starts = new List<string>();
		FixtureModule BuildModule( string id, string title, params string[] dependencies ) => new( id, builder => builder.Panel( "panel", title, _ => new FixtureContent() ), _ => starts.Add( id ) )
		{
			Dependencies = dependencies,
		};

		app.Load( BuildModule( "base", "Base one" ) );
		app.Load( BuildModule( "user", "User one", "base" ) );
		app.Panels.Open( "base.panel" );
		app.Panels.Open( "user.panel" );
		var panes = app.Panels.Panes.Select( pane => pane.Id ).ToArray();
		string[] ReadTitles() => app.Panels.Panels.Select( panel => panel.Title ).Order().ToArray();

		CoreChecks.Check( !app.Reload( BuildModule( "base", "Base two" ) ) && ReadTitles().SequenceEqual( ["Base one", "User one"] ), "A module that others depend on cannot reload without them." );

		starts.Clear();
		CoreChecks.Check( app.Reload( [BuildModule( "base", "Base two" ), BuildModule( "user", "User two", "base" )] ) && starts.SequenceEqual( ["base", "user"] ), "A module and its dependent reload together in dependency order." );
		CoreChecks.Check( ReadTitles().SequenceEqual( ["Base two", "User two"] ) && app.Panels.Panes.Select( pane => pane.Id ).SequenceEqual( panes ), "A group reload reopens every panel in its pane." );

		var broken = new FixtureModule( "user", _ => throw new InvalidOperationException( "broken dependent" ) )
		{
			Dependencies = ["base"],
		};
		CoreChecks.Check( !app.Reload( [BuildModule( "base", "Base three" ), broken] ) && app.LastError!.Contains( "broken dependent" ), "A group with a failing module is rejected." );
		CoreChecks.Check( ReadTitles().SequenceEqual( ["Base two", "User two"] ) && app.Modules.Count == 2, "A failed group reload falls back to every running implementation." );
		CoreChecks.CheckInvariants( app.Panels );
		app.Shutdown();
	}

	/// <summary>Verifies a module DLL loads apart from the application's assemblies and is replaced when its file is rebuilt.</summary>
	private static void CheckModuleHost()
	{
		var root = Path.Combine( Path.GetTempPath(), "Lokn.Tests", Guid.NewGuid().ToString( "N" ) );
		var directory = Path.Combine( root, "modules" );
		var log = Path.Combine( root, "modules.log" );
		var source = Path.Combine( AppContext.BaseDirectory, "Lokn.Modules.Welcome.dll" );
		var target = Path.Combine( directory, "Lokn.Modules.Welcome.dll" );
		Directory.CreateDirectory( directory );
		File.Copy( source, target );

		var app = new LoknApplication();
		var queued = new List<Action>();
		app.BindDispatcher( action =>
		{
			lock ( queued )
				queued.Add( action );

			return true;
		} );

		var host = new ModuleHost( app, directory, null, log, new ModuleStates( Path.Combine( directory, "modules.json" ), isOffByDefault: false ) );
		host.Start();
		var first = app.AvailableModules.Single( module => module.Id == "welcome" );
		CoreChecks.Check( first.GetType().FullName == typeof( WelcomeModule ).FullName && first.GetType() != typeof( WelcomeModule ), "A module DLL loads into its own context, apart from the application's assemblies." );
		var pane = app.Panels.Panes.Single( candidate => candidate.PanelId is not null ).Id;

		// A build replaces the file; a later write time stands in for new contents.
		File.Copy( source, target, overwrite: true );
		File.SetLastWriteTimeUtc( target, DateTime.UtcNow.AddMinutes( 1 ) );
		ApplyNext( queued );
		var second = app.AvailableModules.Single( module => module.Id == "welcome" );
		CoreChecks.Check( second.GetType() != first.GetType() && app.Modules.Any( module => module.Id == "welcome" ), "A rebuilt DLL replaces the running module with a freshly loaded one." );
		CoreChecks.Check( app.Panels.Panes.Single( candidate => candidate.Id == pane ).PanelId is not null, "A module reloaded from its DLL keeps its panel in place." );

		File.WriteAllText( target, "not an assembly" );
		ApplyNext( queued );
		CoreChecks.Check( ReferenceEquals( app.AvailableModules.Single( module => module.Id == "welcome" ), second ) && app.LastError!.Contains( "Lokn.Modules.Welcome" ), "An unloadable DLL is reported while the running module stays." );

		var outcomes = File.ReadAllLines( log );
		CoreChecks.Check( outcomes.Length == 3 && outcomes[0].Contains( "LOADED Lokn.Modules.Welcome (modules: welcome)" ) && outcomes[1].Contains( "RELOADED Lokn.Modules.Welcome" ) && outcomes[2].Contains( "FAILED Lokn.Modules.Welcome" ), "Every load, reload, and failure is recorded for scripts." );

		host.Stop();
		app.Shutdown();
		Directory.Delete( root, recursive: true );
	}

	/// <summary>Verifies the daemon's host holds a rebuilt module back while a chat works, and follows the window's module states file without writing it.</summary>
	private static void CheckDaemonModuleHost()
	{
		var root = Path.Combine( Path.GetTempPath(), "Lokn.Tests", Guid.NewGuid().ToString( "N" ) );
		var directory = Path.Combine( root, "modules" );
		var log = Path.Combine( root, "host.log" );
		var statesPath = Path.Combine( root, "modules.json" );
		var source = Path.Combine( AppContext.BaseDirectory, "Lokn.Modules.Welcome.dll" );
		var target = Path.Combine( directory, "Lokn.Modules.Welcome.dll" );
		Directory.CreateDirectory( directory );
		File.Copy( source, target );

		var app = new LoknApplication() { IsHost = true };
		var queued = new List<Action>();
		app.BindDispatcher( action =>
		{
			lock ( queued )
				queued.Add( action );

			return true;
		} );
		void Drain()
		{
			while ( true )
			{
				Action[] actions;
				lock ( queued )
				{
					actions = [.. queued];
					queued.Clear();
				}
				if ( actions.Length == 0 )
					return;
				foreach ( var action in actions )
					action();
			}
		}
		string[] ReadLoaded() => [.. app.Modules.Select( module => module.Id ).Order( StringComparer.Ordinal )];

		// Fixture modules stand in for the daemon's built-in ones: "keep" is required, as Remote is, and needs "needed"; "user" needs "base".
		FixtureModule Build( string id, params string[] dependencies ) => new( id, _ => { } ) { Dependencies = dependencies };
		foreach ( var module in (FixtureModule[])[Build( "needed" ), Build( "keep", "needed" ), Build( "base" ), Build( "user", "base" )] )
			app.Load( module );
		var rules = new ModuleHost.DaemonRules( new HashSet<string>( ["welcome", "needed", "keep", "base", "user"], StringComparer.Ordinal ), new HashSet<string>( ["keep"], StringComparer.Ordinal ) );
		var host = new ModuleHost( app, directory, null, log, new ModuleStates( statesPath, isOffByDefault: true ), rules );
		try
		{
			host.Start();
			CoreChecks.Check( app.Modules.Any( module => module.Id == "welcome" ), "Before the window ever saved its choices, a bare daemon runs every allowed module." );
			var first = app.AvailableModules.Single( module => module.Id == "welcome" ).GetType();

			app.PublishHostStatus( new HostStatus( 1, 0, 0, true, null ) );
			Drain();
			File.Copy( source, target, overwrite: true );
			File.SetLastWriteTimeUtc( target, DateTime.UtcNow.AddMinutes( 1 ) );
			ApplyNext( queued );
			CoreChecks.Check( app.AvailableModules.Single( module => module.Id == "welcome" ).GetType() == first
				&& File.ReadAllLines( log ).Last().Contains( "DEFERRED Lokn.Modules.Welcome: 1 chat is working" ), "The daemon holds a rebuilt module back while a chat works, and logs it." );

			app.PublishHostStatus( new HostStatus( 0, 0, 0, true, null ) );
			ApplyNext( queued );
			Drain();
			CoreChecks.Check( app.AvailableModules.Single( module => module.Id == "welcome" ).GetType() != first && app.Modules.Any( module => module.Id == "welcome" )
				&& File.ReadAllLines( log ).Last().Contains( "RELOADED Lokn.Modules.Welcome" ), "The held-back reload applies once no chat works." );

			// The window turned "base" off, which turns its dependent off too; the required module and the module it needs stay on whatever the file says.
			const string Off = """{ "On": [ "user", "welcome" ], "Off": [ "base", "keep", "needed" ] }""";
			File.WriteAllText( statesPath, Off );
			ApplyNext( queued );
			Drain();
			CoreChecks.Check( ReadLoaded() is ["keep", "needed", "welcome"], "A module the window turns off stops in the daemon with its dependents; the required module and what it needs stay. " + string.Join( ", ", ReadLoaded() ) );
			var lines = File.ReadAllLines( log );
			CoreChecks.Check( lines.Any( line => line.EndsWith( "DISABLED user", StringComparison.Ordinal ) ) && lines.Last().EndsWith( "DISABLED base", StringComparison.Ordinal ), "Each module the daemon turns off is logged, dependents first." );

			const string On = """{ "On": [ "base", "user" ], "Off": [ "welcome" ] }""";
			File.WriteAllText( statesPath, On );
			ApplyNext( queued );
			Drain();
			var tail = File.ReadAllLines( log ).TakeLast( 3 ).Select( line => line[(line.IndexOf( ' ', line.IndexOf( ' ' ) + 1 ) + 1)..] ).ToArray();
			CoreChecks.Check( ReadLoaded() is ["base", "keep", "needed", "user"] && tail is ["DISABLED welcome", "ENABLED base", "ENABLED user"],
				"Modules the window turns on start in the daemon after the modules they need, and one turned off stops. " + string.Join( ", ", ReadLoaded() ) + " | " + string.Join( " | ", tail ) );
			CoreChecks.Check( File.ReadAllText( statesPath ) == On, "The daemon never writes the module states file." );
		}
		finally
		{
			host.Stop();
			app.Shutdown();
			Directory.Delete( root, recursive: true );
		}
	}

	/// <summary>Verifies source directories compile at startup and follow edits, compile errors, and deletion.</summary>
	private static void CheckSourceModules()
	{
		var root = Path.Combine( Path.GetTempPath(), "Lokn.Tests", Guid.NewGuid().ToString( "N" ) );
		var file = Path.Combine( root, "Notes", "NotesModule.cs" );
		Directory.CreateDirectory( Path.GetDirectoryName( file )! );
		File.WriteAllText( file, string.Format( NotesSource, "Note" ) );

		var app = new LoknApplication();
		var queued = new List<Action>();
		app.BindDispatcher( action =>
		{
			lock ( queued )
				queued.Add( action );

			return true;
		} );

		CoreChecks.Check( app.Load( new HotloadModule( root ) ), "The hotload module loads." );
		CoreChecks.Check( app.Modules.Any( module => module.Id == "notes" ) && app.LastError is null, "A source module compiles and loads at startup." );
		CoreChecks.Check( File.ReadAllText( Path.Combine( root, "Directory.Build.props" ) ).Contains( "Lokn.Core.dll" ) && File.Exists( Path.Combine( root, "Notes", "Notes.csproj" ) ), "Editor project files are generated beside the sources." );
		CoreChecks.Check( File.ReadAllText( Path.Combine( root, "AGENTS.md" ) ).Contains( "ILoknModule" ), "The agent authoring guide is generated in the modules directory." );
		var panel = app.Panels.Open( "notes.note" );
		var pane = app.Panels.ActivePaneId;

		File.WriteAllText( file, string.Format( NotesSource, "Edited note" ) );
		ApplyNext( queued );
		var reopened = app.Panels.Panels.Single();
		CoreChecks.Check( reopened.Title == "Edited note" && reopened.Id != panel, "Saving a source file reloads its module and panels." );
		CoreChecks.Check( app.Panels.Panes.Single( candidate => candidate.PanelId == reopened.Id ).Id == pane, "A reloaded panel stays in its pane." );

		File.WriteAllText( file, string.Format( NotesSource, "Broken" ) + "}" );
		ApplyNext( queued );
		CoreChecks.Check( app.LastError!.Contains( "NotesModule.cs" ) && app.Panels.Panels.Single().Id == reopened.Id, "A compile error is reported while the running module stays." );

		Directory.Delete( Path.GetDirectoryName( file )!, recursive: true );
		ApplyNext( queued );
		CoreChecks.Check( app.AvailableModules.All( module => module.Id != "notes" ) && app.Panels.Panels.Count == 0, "Deleting a source directory removes its module." );

		app.Shutdown();
		Directory.Delete( root, recursive: true );
	}

	/// <summary>Verifies a later start reuses an unchanged module's build, and never a stale or damaged one.</summary>
	private static void CheckCompileCache()
	{
		var root = Path.Combine( Path.GetTempPath(), "Lokn.Tests", Guid.NewGuid().ToString( "N" ) );
		var modules = Path.Combine( root, "modules" );
		var cache = Path.Combine( root, "cache" );
		var file = Path.Combine( modules, "Notes", "NotesModule.cs" );
		Directory.CreateDirectory( Path.GetDirectoryName( file )! );
		string? Start( string? damage = null )
		{
			if ( damage is not null )
			{
				foreach ( var build in Directory.EnumerateFiles( cache, "*.dll" ) )
					File.WriteAllText( build, damage );
			}

			var app = new LoknApplication();
			app.Load( new HotloadModule( modules, cache ) );
			string? title = null;
			if ( app.LastError is null && app.Modules.Any( module => module.Id == "notes" ) )
			{
				var panel = app.Panels.Open( "notes.note" );
				title = app.Panels.Panels.Single( item => item.Id == panel ).Title;
			}
			app.Shutdown();
			return title;
		}
		(string Name, DateTime Written) ReadBuild() => Directory.EnumerateFiles( cache, "*.dll" ).Select( build => (build, File.GetLastWriteTimeUtc( build )) ).Single();

		File.WriteAllText( file, string.Format( NotesSource, "Cached note" ) );
		CoreChecks.Check( Start() == "Cached note", "A compiled source module loads and is kept for the next start." );
		var kept = ReadBuild();
		CoreChecks.Check( Start() == "Cached note" && ReadBuild() == kept, "An unchanged source module starts from its kept build without compiling." );
		File.WriteAllText( file, string.Format( NotesSource, "Changed note" ) );
		CoreChecks.Check( Start() == "Changed note" && ReadBuild().Name != kept.Name, "An edited source module compiles again and replaces its kept build." );
		CoreChecks.Check( Start( damage: "not an assembly" ) == "Changed note", "A damaged kept build is compiled again." );
		Directory.Delete( root, recursive: true );
	}

	/// <summary>Verifies a project's .lokn\modules load at startup only once trusted, and that trusting one loads it at once.</summary>
	private static void CheckProjectModules()
	{
		var root = Path.Combine( Path.GetTempPath(), "Lokn.Tests", Guid.NewGuid().ToString( "N" ) );
		var repository = Path.Combine( root, "repo" );
		var module = Path.Combine( repository, ".lokn", "modules", "Lab" );
		Directory.CreateDirectory( module );
		Directory.CreateDirectory( Path.Combine( root, "user" ) );
		File.WriteAllText( Path.Combine( module, "LabModule.cs" ), string.Format( NotesSource.Replace( "\"notes\"", "\"lab\"" ).Replace( "NotesModule", "LabModule" ), "Lab" ) );

		var app = new LoknApplication();
		app.BindDispatcher( _ => true );
		var projects = new Lokn.Modules.Projects.ProjectsModule( Path.Combine( root, "projects.json" ) ) { Dependencies = [] };
		var project = projects.Store.Create( app.Settings.ActiveProfile, "Repo", repository );
		try
		{
			CoreChecks.Check( app.Load( projects ) && app.Load( new HotloadModule( Path.Combine( root, "user" ) ) ), "Projects and Hotload load." );
			CoreChecks.Check( app.Modules.All( loaded => loaded.Id != "lab" ) && projects.BuildModulePrompt( new PanelContext( app, Guid.NewGuid(), CancellationToken.None, null ) ) is not null,
				"An untrusted project's module does not load at startup, and Home asks about it." );

			projects.UpdateTrust( app, project );
			CoreChecks.Check( app.Modules.Any( loaded => loaded.Id == "lab" ) && app.Panels.Types.Any( type => type.Id == "lab.note" ), "Trusting the project loads its module at once." );
			CoreChecks.Check( projects.BuildModulePrompt( new PanelContext( app, Guid.NewGuid(), CancellationToken.None, null ) ) is null, "A trusted project is not asked about again." );
			CoreChecks.Check( Lokn.Modules.Projects.ProjectsModule.FindProjectModules( repository ).SequenceEqual( ["Lab"] ), "A project lists the module folders that hold C# files." );
			if ( !OperatingSystem.IsWindows() )
			{
				var other = Path.Combine( root, "Repo" );
				Directory.CreateDirectory( Path.Combine( other, ".lokn", "modules", "Lab" ) );
				File.WriteAllText( Path.Combine( other, ".lokn", "modules", "Lab", "LabModule.cs" ), "" );
				projects.Store.Create( app.Settings.ActiveProfile, "Other", other );
				projects.UpdateProjectModules( app );
				CoreChecks.Check( projects.BuildModulePrompt( new PanelContext( app, Guid.NewGuid(), CancellationToken.None, null ) ) is not null,
					"On a case-sensitive file system, trusting a project does not trust a folder whose path differs only in case." );
			}
		}
		finally
		{
			app.Shutdown();
			Directory.Delete( root, recursive: true );
		}
	}

	/// <summary>Verifies modules load from configured folders anywhere: a folder of .cs files is one module, any other holds one per subfolder.</summary>
	private static void CheckModuleFolders()
	{
		var root = Path.Combine( Path.GetTempPath(), "Lokn.Tests", Guid.NewGuid().ToString( "N" ) );
		var single = Path.Combine( root, "repo", "Tools" );
		var pack = Path.Combine( root, "pack" );
		Directory.CreateDirectory( single );
		Directory.CreateDirectory( Path.Combine( pack, "Clock" ) );
		Directory.CreateDirectory( Path.Combine( root, "user" ) );
		File.WriteAllText( Path.Combine( single, "ToolsModule.cs" ), string.Format( NotesSource.Replace( "\"notes\"", "\"tools\"" ).Replace( "NotesModule", "ToolsModule" ), "Tools" ) );
		File.WriteAllText( Path.Combine( pack, "Clock", "ClockModule.cs" ), string.Format( NotesSource.Replace( "\"notes\"", "\"clock\"" ).Replace( "NotesModule", "ClockModule" ), "Clock" ) );

		// The setting is saved before Hotload registers it, as when Lokn starts with module folders configured.
		var settings = Path.Combine( root, "settings.json" );
		File.WriteAllText( settings, JsonSerializer.Serialize( new
		{
			Version = 1,
			ActiveProfile = "Personal",
			Global = new Dictionary<string, string> { ["hotload.folders"] = $"\"%LOKN_TEST_MODULES%\\repo\\Tools\"; {pack}" },
			Work = new Dictionary<string, string>(),
			Personal = new Dictionary<string, string>(),
		} ) );
		var app = new LoknApplication( settings );
		var queued = new List<Action>();
		app.BindDispatcher( action =>
		{
			lock ( queued )
				queued.Add( action );

			return true;
		} );

		try
		{
			Environment.SetEnvironmentVariable( "LOKN_TEST_MODULES", root );
			CoreChecks.Check( app.Load( new HotloadModule( Path.Combine( root, "user" ) ) ), "The hotload module loads with module folders configured." );
			CoreChecks.Check( app.Modules.Any( module => module.Id == "tools" ) && app.Modules.Any( module => module.Id == "clock" ) && app.LastError is null, "A folder of .cs files and a folder of module folders both load at startup." );
			CoreChecks.Check( File.ReadAllText( Path.Combine( single, "Tools.csproj" ) ).Contains( Path.Combine( root, "user", "Directory.Build.props" ) ), "A module outside the per-user directory gets a project importing the shared editor settings." );

			app.Settings.Set( HotloadModule.Folders, SettingsScope.Global, pack );
			ApplyNext( queued );
			CoreChecks.Check( app.AvailableModules.All( module => module.Id != "tools" ) && app.Modules.Any( module => module.Id == "clock" ), "Removing a folder from the setting removes its modules and keeps the others." );

			File.WriteAllText( Path.Combine( pack, "Clock", "ClockModule.cs" ), string.Format( NotesSource.Replace( "\"notes\"", "\"clock\"" ).Replace( "NotesModule", "ClockModule" ), "Edited clock" ) );
			ApplyNext( queued );
			CoreChecks.Check( app.Panels.Types.Single( type => type.ModuleId == "clock" ).Title == "Edited clock", "Saving a file in a configured folder reloads its module." );

			app.Settings.Set( HotloadModule.Folders, SettingsScope.Global, Path.Combine( root, "missing" ) );
			ApplyNext( queued );
			CoreChecks.Check( app.LastError?.Contains( "missing" ) == true && app.AvailableModules.All( module => module.Id != "clock" ), "A missing folder is reported and the modules of the folder it replaced are removed." );
		}
		finally
		{
			Environment.SetEnvironmentVariable( "LOKN_TEST_MODULES", null );
			app.Shutdown();
			Directory.Delete( root, recursive: true );
		}
	}

	/// <summary>Waits for the watcher to post one batch of changes and applies it like the UI thread.</summary>
	private static void ApplyNext( List<Action> queued )
	{
		var clock = Stopwatch.StartNew();
		while ( clock.Elapsed.TotalSeconds < WatchTimeoutSeconds )
		{
			Action? action = null;
			lock ( queued )
			{
				if ( queued.Count > 0 )
				{
					action = queued[0];
					queued.RemoveAt( 0 );
				}
			}

			if ( action is not null )
			{
				action();
				return;
			}

			Thread.Sleep( 20 );
		}

		throw new InvalidOperationException( "The file watcher posted no module changes." );
	}

	/// <summary>Captures a number so a reload can prove state reaches the replacement panel.</summary>
	private sealed class StatefulContent( int value ) : PanelContent
	{
		/// <summary>Gets the number of close hook calls.</summary>
		internal int CloseCount
		{
			get; private set;
		}

		/// <summary>Builds a minimal fixture surface.</summary>
		public override Blob Build() => new Text
		{
			Content = "Stateful",
		};

		/// <summary>Captures the fixture value as restorable state.</summary>
		public override JsonElement? CaptureState() => JsonSerializer.SerializeToElement( value );

		/// <summary>Records panel closure.</summary>
		public override void Closed() => CloseCount++;
	}
}
