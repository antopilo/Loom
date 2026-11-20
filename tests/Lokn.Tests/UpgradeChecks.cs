using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokn.Core;
using Lokn.Modules.Browser;
using Lokn.Modules.Conversations;
using Lokn.Modules.Files;
using Lokn.Modules.Git;
using Lokn.Modules.Markdown;
using Lokn.Modules.Notifications;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;
using Lokn.Modules.Remote;
using Lokn.Modules.Terminal;
using Lokn.Modules.Workspaces;
using Lokn.Modules.Worktrees;

namespace Lokn.Tests;

/// <summary>Checks the upgrade from 0.2.0: <c>version.json</c>, backups and their restore, opt-in defaults, the What's new card, a real 0.2.0 data folder
/// opening in this version with everything kept, the app update's hold while agents work, and the daemon's restart after an update.</summary>
internal static class UpgradeChecks
{
	/// <summary>Defines the version these checks upgrade to.</summary>
	private const string Version = "0.3.0";

	/// <summary>Defines the login chat of the fixture, in the Demo app project, with four messages.</summary>
	private static readonly Guid LoginChat = Guid.Parse( "83688683-2893-453a-a313-7e719c953b8d" );

	/// <summary>Defines the release notes chat of the fixture, in the Notes project, with four messages.</summary>
	private static readonly Guid OutlineChat = Guid.Parse( "6f37e847-72cf-435f-8f5d-b0449d23c009" );

	/// <summary>Defines the chat outside projects of the fixture, with two messages.</summary>
	private static readonly Guid MeetingChat = Guid.Parse( "d898b829-3fec-4459-84c7-43ecdd492344" );

	/// <summary>Defines the task of the fixture, without a chat.</summary>
	private static readonly Guid ReviewTask = Guid.Parse( "8165feed-9a37-4567-bf09-8af8c6958da7" );

	/// <summary>Runs every upgrade check.</summary>
	internal static async Task RunAsync()
	{
		CheckBackups();
		if ( !OperatingSystem.IsWindows() )
		{
			Console.WriteLine( "SKIP upgrade: the daemon and the fixture's modules run on Windows only." );
			return;
		}
		CheckDefaults();
		CheckUpdateGate();
		var upgraded = CheckFixtureUpgrade();
		try
		{
			CheckDowngrade( upgraded );
		}
		finally
		{
			RemoteGatewayChecks.TryDelete( upgraded );
		}
		await CheckDaemonUpdateAsync();
	}

	/// <summary>Checks <c>version.json</c>, the backup before a newer version first runs, keeping three, the mutex between two starts, a failed backup,
	/// and a restore.</summary>
	private static void CheckBackups()
	{
		var root = CreateTemporary( "backups" );
		try
		{
			var fresh = Path.Combine( root, "fresh" );
			var first = DataUpgrade.Prepare( fresh, Version, canRestore: true );
			CoreChecks.Check( first is { PreviousVersion: null, BackupFolder: null, Error: null } && DataUpgrade.ReadLastRunVersion( fresh ) == Version
				&& DataUpgrade.ReadUpgradedFrom( fresh ) is null && DataUpgrade.ListBackups( fresh ).Count == 0,
				"A fresh data folder gets version.json with no backup and no What's new." );

			var data = Path.Combine( root, "data" );
			WriteOldData( data );
			var stamp = new DateTime( 2026, 11, 19, 10, 0, 0 );
			var upgrade = DataUpgrade.Prepare( data, Version, canRestore: true, stamp );
			var backup = upgrade.BackupFolder;
			CoreChecks.Check( upgrade.PreviousVersion == DataUpgrade.EarlierVersion && backup is not null && Path.GetFileName( backup ) == "earlier-20261119-100000"
				&& File.ReadAllText( Path.Combine( backup, "conversations.json" ) ) == "{\"old\":1}" && File.Exists( Path.Combine( backup, "conversations", "chat.json" ) )
				&& File.Exists( Path.Combine( backup, "settings.json" ) ) && File.Exists( Path.Combine( backup, "workspace.json" ) ) && !File.Exists( Path.Combine( backup, "notes.txt" ) )
				&& DataUpgrade.ReadLastRunVersion( data ) == Version && DataUpgrade.ReadUpgradedFrom( data ) == DataUpgrade.EarlierVersion,
				"Data without version.json is backed up with its chats' message files before 0.3.0 first runs, and version.json records the upgrade." );
			CoreChecks.Check( DataUpgrade.Prepare( data, Version, canRestore: true ) is { BackupFolder: null, Error: null } && DataUpgrade.ListBackups( data ).Count == 1,
				"A second start of the same version backs nothing up." );

			for ( var minor = 1; minor <= 4; minor++ )
				DataUpgrade.Prepare( data, $"0.3.{minor}", canRestore: true, stamp.AddDays( minor ) );
			var kept = DataUpgrade.ListBackups( data );
			CoreChecks.Check( kept.Count == DataUpgrade.KeptBackups && kept[0].Version == "0.3.3" && kept[^1].Version == "0.3.1"
				&& DataUpgrade.ReadUpgradedFrom( data ) == "0.3.3",
				"Each newer version backs up once and only the three newest backups are kept." );
			CoreChecks.Check( DataUpgrade.Prepare( data, "0.3.2", canRestore: true ) is { BackupFolder: null } && DataUpgrade.ReadLastRunVersion( data ) == "0.3.2"
				&& DataUpgrade.ListBackups( data ).Count == DataUpgrade.KeptBackups,
				"Running an older version records it without a backup." );

			var raced = Path.Combine( root, "raced" );
			WriteOldData( raced );
			var results = new DataUpgradeResult[2];
			Parallel.For( 0, 2, index => results[index] = DataUpgrade.Prepare( raced, Version, canRestore: index == 0 ) );
			CoreChecks.Check( results.Count( result => result.BackupFolder is not null ) == 1 && DataUpgrade.ListBackups( raced ).Count == 1,
				"Loom and its daemon starting together back up once, under the folder's mutex." );

			var broken = Path.Combine( root, "broken" );
			WriteOldData( broken );
			File.WriteAllText( Path.Combine( broken, DataUpgrade.BackupsFolderName ), "a file where the backups folder goes" );
			var failed = DataUpgrade.Prepare( broken, Version, canRestore: true );
			CoreChecks.Check( failed is { BackupFolder: null, Error: IOException } && DataUpgrade.ReadLastRunVersion( broken ) == Version,
				"A backup that fails is reported and Loom starts normally." );

			var restoring = kept[^1];
			File.WriteAllText( Path.Combine( data, "conversations.json" ), "{\"new\":1}" );
			File.WriteAllText( Path.Combine( data, "browser-tabs.json" ), "{}" );
			File.WriteAllText( Path.Combine( data, "conversations", "later.json" ), "{}" );
			DataUpgrade.RequestRestore( data, restoring.Folder );
			CoreChecks.Check( DataUpgrade.Prepare( data, "0.3.2", canRestore: false ) is { RestoredFrom: null } && DataUpgrade.ReadPendingRestore( data ) is not null,
				"The daemon leaves a restore to the desktop." );
			var restored = DataUpgrade.Prepare( data, "0.3.4", canRestore: true );
			CoreChecks.Check( restored.RestoredFrom == restoring.Folder && restored.BackupFolder is null && DataUpgrade.ReadPendingRestore( data ) is null
				&& File.ReadAllText( Path.Combine( data, "conversations.json" ) ) == "{\"old\":1}" && !File.Exists( Path.Combine( data, "browser-tabs.json" ) )
				&& !File.Exists( Path.Combine( data, "conversations", "later.json" ) ) && File.Exists( Path.Combine( data, "conversations", "chat.json" ) )
				&& DataUpgrade.ReadLastRunVersion( data ) == "0.3.4" && DataUpgrade.ReadUpgradedFrom( data ) is null,
				"Restoring copies the backup's files back before anything reads them, removes files it did not hold, and backs nothing up again." );
		}
		finally
		{
			RemoteGatewayChecks.TryDelete( root );
		}
		Console.WriteLine( "PASS upgrade backups: version.json, one backup per newer version under a mutex, the three newest kept, failures reported, restore at the next start." );
	}

	/// <summary>Checks that a fresh install and the 0.2.0 fixture both start with This PC's chats in Loom, no daemon, no network and no sign-in entry.</summary>
	private static void CheckDefaults()
	{
		var root = CreateTemporary( "defaults" );
		try
		{
			File.Copy( Path.Combine( FixtureFolder, "settings.json" ), Path.Combine( root, "settings.json" ) );
			foreach ( var settings in new[] { new SettingsService( Path.Combine( root, "fresh.json" ) ), new SettingsService( Path.Combine( root, "settings.json" ) ) } )
			{
				settings.Register( "remote", [RemoteModule.Role, RemoteModule.Network, RemoteModule.LoginItem, RemoteModule.Tray] );
				CoreChecks.Check( settings.Get( RemoteModule.Role, SettingsScope.Global ) == RemoteModule.LocalRole && settings.Get( RemoteModule.Network, SettingsScope.Global ) == "off"
					&& !settings.Get( RemoteModule.LoginItem, SettingsScope.Global ),
					"A fresh install and an upgraded one run This PC's chats in Loom, with network access off and no daemon at sign-in." );
			}
		}
		finally
		{
			RemoteGatewayChecks.TryDelete( root );
		}
		Console.WriteLine( "PASS upgrade defaults: fresh and upgraded settings read remote.role local, remote.network off, remote.login-item off." );
	}

	/// <summary>Checks when an app update restarts Loom: at once with no agent working, else after asking, and once agents finish when the user chose to wait.</summary>
	[SupportedOSPlatform( "windows" )]
	private static void CheckUpdateGate()
	{
		CoreChecks.Check( UpdateGate.Decide( 0, false, false ) == UpdateDecision.RestartNow && UpdateGate.Decide( 2, false, false ) == UpdateDecision.Ask
			&& UpdateGate.Decide( 2, true, false ) == UpdateDecision.Wait && UpdateGate.Decide( 2, false, true ) == UpdateDecision.RestartNow
			&& UpdateGate.DescribeForce( 1 ) == "Update now (stops 1 agent)" && UpdateGate.DescribeForce( 3 ) == "Update now (stops 3 agents)",
			"An update restarts at once only while no agent works on This PC, or when the user stops them." );

		var working = 2;
		var restarts = 0;
		var gate = new UpdateGate( () => working, () => restarts++ );
		CoreChecks.Check( gate.Request( isForced: false ) == UpdateDecision.Ask && restarts == 0, "Restart now with agents working asks instead of restarting." );
		gate.WaitForAgents();
		CoreChecks.Check( gate.IsWaiting && restarts == 0 && gate.Request( isForced: false ) == UpdateDecision.Wait && !gate.Update(),
			"Update when agents finish waits while they work." );
		working = 1;
		CoreChecks.Check( !gate.Update() && restarts == 0, "One agent still working keeps the update waiting." );
		working = 0;
		CoreChecks.Check( gate.Update() && restarts == 1 && !gate.IsWaiting && !gate.Update(), "The update applies once, as soon as no agent works." );
		working = 3;
		CoreChecks.Check( gate.Request( isForced: true ) == UpdateDecision.RestartNow && restarts == 2, "Update now applies at once, stopping the agents." );
		CoreChecks.Check( HostRunner.ShouldStartAfterUpdate( wasRunning: true, isLoginItem: false ) && HostRunner.ShouldStartAfterUpdate( false, true )
			&& !HostRunner.ShouldStartAfterUpdate( false, false ), "After an update the daemon starts again when it ran before or starts at sign-in." );
		Console.WriteLine( "PASS update gate: Restart now waits for This PC's agents, offers Update when agents finish and Update now (stops N agents), and applies once they finish." );
	}

	/// <summary>Opens a copy of the 0.2.0 fixture as this version starts: backs it up, then loads every module and restores the layout, and checks
	/// that every chat, message, project, workspace and setting is kept, the browser panel's pages became session tabs, and the What's new card shows.</summary>
	/// <returns>The upgraded folder, after this version saved it, for the downgrade check.</returns>
	[SupportedOSPlatform( "windows" )]
	private static string CheckFixtureUpgrade()
	{
		var data = CreateTemporary( "upgrade" );
		CopyFolder( FixtureFolder, data );
		var previousRoot = LoknPaths.DataRoot;
		var upgrade = DataUpgrade.Prepare( data, Version, canRestore: true );
		CoreChecks.Check( upgrade is { PreviousVersion: DataUpgrade.EarlierVersion, Error: null } && upgrade.BackupFolder is { } backup
			&& File.ReadAllText( Path.Combine( backup, "conversations.json" ) ) == File.ReadAllText( Path.Combine( FixtureFolder, "conversations.json" ) )
			&& Directory.GetFiles( Path.Combine( backup, "conversations" ) ).Length == 3 && File.Exists( Path.Combine( backup, "workspaces.json" ) )
			&& File.Exists( Path.Combine( backup, "projects.json" ) ) && File.Exists( Path.Combine( backup, "modules.json" ) ),
			"The 0.2.0 data is backed up, chats' messages included, before 0.3.0 changes it." );

		LoknPaths.UseDataRoot( data );
		try
		{
			var host = Start( data );
			var app = host.App;
			var store = host.Conversations.Hosts.Local.Store;
			var restored = app.RestoreWorkspace();
			var types = app.Panels.Panels.Where( panel => app.Panels.IsShown( panel.Id ) ).Select( panel => panel.TypeId ).ToHashSet();
			CoreChecks.Check( restored && types.SetEquals( ["files.tree", ConversationsModule.ChatPanelType, "browser.page", "terminal.shell", "git.panel"] ),
				$"0.3.0 restores the 0.2.0 layout with its chat, Git, browser, terminal and file panels: {string.Join( ", ", types )}." );
			var tabs = app.Hosts.Local.BrowserTabs.Tabs.Select( tab => tab.Url ).ToArray();
			CoreChecks.Check( tabs.Contains( "https://example.com/" ) && tabs.Contains( "https://example.com/docs" ),
				$"The 0.2.0 browser panel's pages open as session tabs: {string.Join( ", ", tabs )}." );
			app.MarkStarted();
			Pump( host );

			var profile = SettingsProfile.Personal;
			var projects = store.ProjectStore.Projects( profile );
			var chats = store.Conversations( profile ).Concat( projects.SelectMany( project => store.Conversations( profile, project.Id ) ) ).Select( chat => chat.Id ).ToHashSet();
			CoreChecks.Check( projects.Select( project => project.Name ).Order().SequenceEqual( ["Demo app", "Notes"] ) && chats.SetEquals( [LoginChat, OutlineChat, MeetingChat, ReviewTask] )
				&& store.Get( LoginChat ).ProjectId == projects.Single( project => project.Name == "Demo app" ).Id,
				"Every 0.2.0 project, chat and task is kept, in its project." );
			CoreChecks.Check( store.GetMessages( LoginChat ).Count == 4 && store.GetMessages( OutlineChat ).Count == 4 && store.GetMessages( MeetingChat ).Count == 2
				&& store.GetMessages( LoginChat ).All( message => message.RichContent is not null ),
				"Every 0.2.0 message is kept with its rich content." );
			var loginSession = host.Workspaces.Sessions.FirstOrDefault( session => session.Key.Contains( LoginChat.ToString( "N" ), StringComparison.OrdinalIgnoreCase )
				|| session.Key.Contains( LoginChat.ToString(), StringComparison.OrdinalIgnoreCase ) )?.Key;
			CoreChecks.Check( host.Workspaces.Sessions.Count == 6 && loginSession is not null
				&& host.Workspaces.Tasks( loginSession ).Select( task => task.Name ).Order().SequenceEqual( ["Code", "Review"] ),
				$"Every 0.2.0 workspace session is kept, with its saved workspaces: {host.Workspaces.Sessions.Count} sessions." );
			CoreChecks.Check( app.Settings.ActiveProfile == SettingsProfile.Personal && app.Settings.Get( OmpModule.Model, SettingsScope.Personal ) == "anthropic/claude-sonnet-4-5"
				&& app.Settings.Get( TerminalModule.Shell, SettingsScope.Global ) == "pwsh -NoLogo",
				"The 0.2.0 settings are kept." );
			CoreChecks.Check( host.Remote.RunningRole == RemoteModule.LocalRole && host.Remote.Daemon is null && !DaemonControl.IsRunning && app.HostStatus is null
				&& app.Settings.Get( RemoteModule.Network, SettingsScope.Global ) == "off",
				"The upgraded install runs This PC's chats in Loom, with network access off, and starts no daemon, so no icon shows near the clock." );
			CoreChecks.Check( host.Conversations.IsWhatsNewShown && DataUpgrade.ReadUpgradedFrom( data ) == DataUpgrade.EarlierVersion,
				"Home shows the What's new card after the update to 0.3.0." );
			host.Conversations.DismissWhatsNew();
			CoreChecks.Check( !host.Conversations.IsWhatsNewShown && DataUpgrade.ReadUpgradedFrom( data ) is null && DataUpgrade.ReadLastRunVersion( data ) == Version,
				"Not now hides the What's new card for good." );

			Stop( host );
			using var layout = JsonDocument.Parse( File.ReadAllText( Path.Combine( data, "workspace.json" ) ) );
			var browser = layout.RootElement.GetProperty( "Panels" ).EnumerateArray().Concat( FindSessionPanels( data ) )
				.Select( panel => panel.TryGetProperty( "State", out var state ) ? state : default )
				.FirstOrDefault( state => state.ValueKind == JsonValueKind.Object && state.TryGetProperty( "Ids", out _ ) );
			CoreChecks.Check( browser.ValueKind == JsonValueKind.Object && browser.TryGetProperty( "Address", out _ ) && browser.TryGetProperty( "Tabs", out _ ),
				"A saved browser panel keeps the 0.2.0 Address and Tabs beside its session tab ids, so 0.2.0 still opens its pages." );

			var again = Start( data );
			CoreChecks.Check( again.Conversations.Hosts.Local.Store.GetMessages( LoginChat ).Count == 4 && again.Conversations.Hosts.Local.Store.ProjectStore.Projects( profile ).Count == 2
				&& !again.Conversations.IsWhatsNewShown, "0.3.0 reads what it saved back, and the card stays dismissed." );
			Stop( again );
		}
		finally
		{
			LoknPaths.UseDataRoot( previousRoot );
		}
		Console.WriteLine( "PASS upgrade 0.2.0 fixture: backed up, every chat, message, project, workspace and setting kept, layout restored with browser pages as session tabs, version.json written, What's new shown once." );
		return data;
	}

	/// <summary>Opens the folder 0.3.0 saved with the 0.2.0 build's loader, when the 0.2.0 build from <c>git archive main</c> is in <c>%TEMP%\loom-020</c>.</summary>
	private static void CheckDowngrade( string upgraded )
	{
		var loader = Environment.GetEnvironmentVariable( "LOKN_LOADER_020" )
			?? Path.Combine( Path.GetTempPath(), "loom-020", "tests", "Lokn.Tests", "bin", "Debug", "net10.0", "Lokn.Tests.dll" );
		if ( !File.Exists( loader ) )
		{
			Console.WriteLine( $"SKIP downgrade to 0.2.0: no 0.2.0 loader at {loader} (docs/architecture.md, Data and upgrades, says how to build it)." );
			return;
		}

		var copy = CreateTemporary( "downgrade" );
		try
		{
			CopyFolder( upgraded, copy );
			var info = new ProcessStartInfo( "dotnet" ) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
			info.ArgumentList.Add( loader );
			info.ArgumentList.Add( "--load-data" );
			info.ArgumentList.Add( copy );
			using var process = Process.Start( info ) ?? throw new InvalidOperationException( "dotnet did not start." );
			var error = process.StandardError.ReadToEndAsync();
			var output = process.StandardOutput.ReadToEnd();
			process.WaitForExit();
			var lines = output.Split( '\n', StringSplitOptions.TrimEntries );
			bool HasChat( Guid id, int messages ) => lines.Any( line => line.StartsWith( $"chat {id} ", StringComparison.Ordinal ) && line.Contains( $" messages={messages} ", StringComparison.Ordinal ) );
			CoreChecks.Check( process.ExitCode == 0 && lines.Contains( "LOAD OK" ) && lines.Contains( "layout restored: True" ) && lines.Contains( "Personal: 2 projects, 4 chats and tasks" )
				&& HasChat( LoginChat, 4 ) && HasChat( OutlineChat, 4 ) && HasChat( MeetingChat, 2 ) && lines.Contains( "workspaces: 6 sessions" )
				&& lines.Any( line => line.Contains( "\"Review\" [", StringComparison.Ordinal ) && line.Contains( "browser.page", StringComparison.Ordinal ) && line.Contains( "terminal.shell", StringComparison.Ordinal ) )
				&& lines.Any( line => line.StartsWith( "settings active=Personal theme.name=dark-catppuccin terminal.shell=pwsh -NoLogo omp.model(Personal)=anthropic/claude-sonnet-4-5", StringComparison.Ordinal ) ),
				$"0.2.0 opens the data 0.3.0 saved, with nothing thrown: {output}{error.Result}" );
			foreach ( var line in lines.Where( line => line.StartsWith( "layout", StringComparison.Ordinal ) || line.StartsWith( "panel ", StringComparison.Ordinal )
				|| line.StartsWith( "Personal:", StringComparison.Ordinal ) || line.StartsWith( "workspaces:", StringComparison.Ordinal ) || line.StartsWith( "workspace ", StringComparison.Ordinal )
				|| line.StartsWith( "LOAD", StringComparison.Ordinal ) ) )
				Console.WriteLine( "  0.2.0: " + line );
		}
		finally
		{
			RemoteGatewayChecks.TryDelete( copy );
		}
		Console.WriteLine( "PASS downgrade to 0.2.0: the 0.2.0 build opens the data 0.3.0 saved, with every chat, project and setting." );
	}

	/// <summary>Checks the app update's daemon hooks with a bare daemon: stopping for the update records that it ran, and it starts again afterwards
	/// without a sign-in entry; with no record it stays stopped.</summary>
	[SupportedOSPlatform( "windows" )]
	private static async Task CheckDaemonUpdateAsync()
	{
		if ( RemoteGatewayChecks.FindLoom() is not { } loom )
		{
			Console.WriteLine( "SKIP daemon update restart: no built Loom.exe; run scripts/build.ps1 first." );
			return;
		}

		var bare = CreateTemporary( "update-daemon" );
		var marker = Path.Combine( bare, "daemon-update.flag" );
		try
		{
			using ( RemoteGatewayChecks.StartLoom( loom, "--host", "--bare", bare ) )
				CoreChecks.Check( await WaitUntilAsync( () => HostRunner.IsRunning( bare ) ), "The bare daemon starts. " + RemoteGatewayChecks.ReadLog( bare ) );
			HostRunner.StopForUpdate( bare, marker );
			CoreChecks.Check( File.Exists( marker ) && await WaitUntilAsync( () => !HostRunner.IsRunning( bare ) ), "Before an update the running daemon is recorded and stopped gracefully." );
			CoreChecks.Check( HostRunner.StartAfterUpdate( loom, bare, marker ) && !File.Exists( marker ) && await WaitUntilAsync( () => HostRunner.IsRunning( bare ) ),
				"After the update the daemon that ran starts again from the new build, with no sign-in entry. " + RemoteGatewayChecks.ReadLog( bare ) );
			HostRunner.StopForUpdate( bare, marker );
			File.Delete( marker );
			CoreChecks.Check( !HostRunner.StartAfterUpdate( loom, bare, marker ) && !HostRunner.IsRunning( bare ), "A daemon that did not run before the update stays stopped." );
		}
		finally
		{
			HostRunner.Stop( bare );
			RemoteGatewayChecks.TryDelete( bare );
		}
		Console.WriteLine( "PASS daemon update restart: the update's hooks stop a running bare daemon gracefully and start it again afterwards, without a sign-in entry." );
	}

	/// <summary>Gets the 0.2.0 data fixture copied beside the checks.</summary>
	private static string FixtureFolder => Path.Combine( AppContext.BaseDirectory, "Fixtures", "data-0.2.0" );

	/// <summary>Writes data the way 0.2.0 left it: the data files, a chat's message file, and a file a backup leaves out.</summary>
	private static void WriteOldData( string folder )
	{
		Directory.CreateDirectory( Path.Combine( folder, "conversations" ) );
		File.WriteAllText( Path.Combine( folder, "conversations.json" ), "{\"old\":1}" );
		File.WriteAllText( Path.Combine( folder, "conversations", "chat.json" ), "[]" );
		File.WriteAllText( Path.Combine( folder, "settings.json" ), "{}" );
		File.WriteAllText( Path.Combine( folder, "workspace.json" ), "{}" );
		File.WriteAllText( Path.Combine( folder, "notes.txt" ), "not data" );
	}

	/// <summary>Lists the panels saved in <c>workspaces.json</c>'s workspaces, where Home moved the restored layout.</summary>
	private static IEnumerable<JsonElement> FindSessionPanels( string data )
	{
		var node = JsonNode.Parse( File.ReadAllText( Path.Combine( data, "workspaces.json" ) ) );
		return Collect( node ).Select( text => JsonDocument.Parse( text ).RootElement );

		static IEnumerable<string> Collect( JsonNode? node )
		{
			switch ( node )
			{
				case JsonObject item:
					if ( item["TypeId"] is not null && item["State"] is not null )
						yield return item.ToJsonString();
					foreach ( var child in item.Select( pair => pair.Value ) )
					{
						foreach ( var found in Collect( child ) )
							yield return found;
					}
					break;
				case JsonArray list:
					foreach ( var child in list )
					{
						foreach ( var found in Collect( child ) )
							yield return found;
					}
					break;
			}
		}
	}

	/// <summary>Composes the application and its modules over one data folder as Loom does, with the modules' own constructors reading the data folder.</summary>
	private static Host Start( string data )
	{
		var app = new LoknApplication( Path.Combine( data, "settings.json" ), Path.Combine( data, "workspace.json" ), Path.Combine( data, "notifications.json" ), Path.Combine( data, "browser-tabs.json" ) );
		var queued = new List<Action>();
		app.BindDispatcher( action =>
		{
			lock ( queued )
				queued.Add( action );
			return true;
		} );
		app.Settings.MoveSaved( "workspace.", "shell." );
		var workspaces = new WorkspacesModule();
		var conversations = new ConversationsModule();
		var remote = new RemoteModule();
		ILoknModule[] modules =
		[
			new ThemeModule( Path.Combine( data, "theme.json" ) ),
			new MarkdownModule(),
			new OmpModule(),
			new NotificationsModule(),
			new GitModule(),
			new FilesModule(),
			workspaces,
			new ProjectsModule(),
			conversations,
			new WorktreesModule(),
			new TerminalModule(),
			new BrowserModule(),
			remote,
			new ShellModule(),
		];
		foreach ( var module in modules )
			CoreChecks.Check( app.Load( module ), $"{module.Title} loads on the upgraded data: {app.LastError}" );
		return new( app, queued, conversations, workspaces, remote );
	}

	/// <summary>Shuts the application down, which saves the layout and flushes history, and runs what it posted.</summary>
	private static void Stop( Host host )
	{
		Pump( host );
		host.App.Shutdown();
		Pump( host );
	}

	/// <summary>Runs posted UI work until none is left.</summary>
	private static void Pump( Host host )
	{
		for ( var round = 0; round < 50; round++ )
		{
			Action[] actions;
			lock ( host.Queued )
			{
				actions = [.. host.Queued];
				host.Queued.Clear();
			}
			if ( actions.Length == 0 )
				return;

			foreach ( var action in actions )
				action();
		}
	}

	/// <summary>Waits up to 15 seconds for a condition.</summary>
	private static async Task<bool> WaitUntilAsync( Func<bool> condition )
	{
		var clock = Stopwatch.StartNew();
		while ( !condition() && clock.Elapsed < TimeSpan.FromSeconds( 15 ) )
			await Task.Delay( 100 );
		return condition();
	}

	/// <summary>Creates an empty temporary folder for one check.</summary>
	private static string CreateTemporary( string name )
	{
		var folder = Path.Combine( Path.GetTempPath(), $"lokn-{name}-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( folder );
		return folder;
	}

	/// <summary>Copies a folder and everything under it.</summary>
	private static void CopyFolder( string source, string target )
	{
		Directory.CreateDirectory( target );
		foreach ( var file in Directory.EnumerateFiles( source ) )
			File.Copy( file, Path.Combine( target, Path.GetFileName( file ) ), overwrite: true );
		foreach ( var child in Directory.EnumerateDirectories( source ) )
			CopyFolder( child, Path.Combine( target, Path.GetFileName( child ) ) );
	}

	/// <summary>One composed application over a data folder.</summary>
	/// <param name="App">The application.</param>
	/// <param name="Queued">The UI work it posted, run by <see cref="Pump"/>.</param>
	/// <param name="Conversations">The Conversations module.</param>
	/// <param name="Workspaces">The Workspaces module.</param>
	/// <param name="Remote">The Remote module.</param>
	private sealed record Host( LoknApplication App, List<Action> Queued, ConversationsModule Conversations, WorkspacesModule Workspaces, RemoteModule Remote );
}
