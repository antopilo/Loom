using System.Diagnostics;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Conversations;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;
using Lokn.Modules.Remote;

namespace Lokn.Tests;

/// <summary>Checks moving This PC's chats between this Loom and a real bare daemon while Loom runs, the wait for working agents and its Cancel, the
/// <c>lokn://remote/role/…</c> route, and the fallback to this Loom when the daemon keeps failing.</summary>
/// <remarks>Every daemon runs on a temporary bare folder (<c>Loom.exe --host --bare</c>), never the user's daemon or data; the checks point
/// <see cref="LoknPaths.DataRoot"/> at that folder while they run and restore it afterwards.</remarks>
internal static class HandoverChecks
{
	/// <summary>Defines how long one step may take, such as the daemon starting or stopping.</summary>
	private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds( 60 );

	/// <summary>Defines the process data slot the Remote module latches its running role in.</summary>
	private const string RoleSlot = "Lokn.Modules.Remote.Role";

	/// <summary>Defines the environment variable that makes a bare daemon exit at once with its value.</summary>
	private const string TestExitVariable = "LOKN_TEST_HOST_EXIT";

	/// <summary>Runs the failure count check, then the live handover and the fallback with a real daemon on Windows.</summary>
	public static async Task RunAsync()
	{
		CheckFailureWindow();
		Console.WriteLine( $"PASS daemon failure count: {DaemonFailures.Limit} failures within {DaemonFailures.Window.TotalMinutes:0} minutes give up on the daemon, older ones no longer count, and the count starts over." );
		if ( !OperatingSystem.IsWindows() )
		{
			Console.WriteLine( "SKIP live handover: the daemon runs on Windows only." );
			return;
		}

		var loom = RemoteGatewayChecks.FindLoom() ?? throw new InvalidOperationException( "No built Loom.exe under the repository's bin folder or next to the checks; run scripts/build.ps1 first." );
		await CheckHandoverAsync( loom );
		Console.WriteLine( "PASS live handover: lokn://remote/role/daemon chooses the daemon, the switch waits while an agent works and shows how many, Cancel restores the role; "
			+ "once no agent works the chats move to a real bare daemon, which starts and holds chats.lock, and the open chat panel shows the same chats and messages over a remote session; "
			+ "moving back stops the daemon, takes the lock again, makes the stores and tabs authoritative over the daemon's files, and the panel shows a local session." );
		await CheckFallbackAsync( loom );
		Console.WriteLine( $"PASS daemon fallback: a daemon that exits before opening its pipe ({TestExitVariable}) fails {DaemonFailures.Limit} times, after which This PC's chats run in this Loom again with "
			+ "the role saved as local, a notification and Remote access's note naming the reason and the daemon's log, and the chats intact." );
	}

	/// <summary>Checks the failure window: three failures within ten minutes count, older ones drop out, and the count starts over after giving up.</summary>
	private static void CheckFailureWindow()
	{
		var failures = new DaemonFailures();
		var start = DateTimeOffset.UnixEpoch;
		CoreChecks.Check( !failures.Add( start ) && !failures.Add( start.AddMinutes( 1 ) ), "Two failures do not give up on the daemon." );
		CoreChecks.Check( !failures.Add( start.AddMinutes( 11 ) ) && !failures.Add( start.AddMinutes( 12 ) ), "Failures more than ten minutes old no longer count." );
		CoreChecks.Check( failures.Add( start.AddMinutes( 13 ) ), "The third failure within ten minutes gives up on the daemon." );
		CoreChecks.Check( !failures.Add( start.AddMinutes( 14 ) ) && !failures.Add( start.AddMinutes( 15 ) ), "Giving up starts the count over." );
	}

	/// <summary>Moves a Loom's chats with a chat panel open into a real bare daemon and back, after checking the wait, Cancel and the role route.</summary>
	private static async Task CheckHandoverAsync( string loom )
	{
		var bare = CreateBare( "lokn-handover-", RemoteModule.LocalRole );
		var (first, second, texts) = SeedChats( bare );
		var previousRoot = LoknPaths.DataRoot;
		var queue = new List<Action>();
		LoknApplication? app = null;
		try
		{
			LoknPaths.UseDataRoot( bare );
			AppDomain.CurrentDomain.SetData( RoleSlot, null );
			(app, var store, var module) = Start( bare, loom, queue );
			CoreChecks.Check( module.RunningRole == RemoteModule.LocalRole && module.Daemon is null && module.IsWriter && !store.IsMirror,
				"The Loom starts in the local role and holds the chats. " + app.LastError );
			var profile = store.Get( first ).Profile;
			var panelId = app.Panels.Open( ConversationsModule.ChatPanelType, input: new ConversationPanelInput( first, profile ) );
			Pump( queue );
			var panel = FindPanel( app, panelId );
			CoreChecks.Check( panel.Chat == ChatRef.Local( first ) && panel.View?.Session is { IsRemote: false }, "The chat panel shows the first chat over a local session." );

			// An agent works: choosing the daemon, here through the link Home's What's new card follows, waits for it and can be cancelled.
			store.SetWorking( first, true );
			app.OpenLink( LoknLinks.Create( "remote", RemoteModule.RoleRoute, RemoteModule.DaemonRole ) );
			Pump( queue );
			CoreChecks.Check( app.Settings.Get( RemoteModule.Role, SettingsScope.Global ) == RemoteModule.DaemonRole && module.Switching is { Role: RemoteModule.DaemonRole, IsWaiting: true }
				&& module.CountWaiting() == 1 && module.RunningRole == RemoteModule.LocalRole && module.Daemon is null && module.IsWriter,
				"lokn://remote/role/daemon chooses the daemon, whose switch waits for the one working agent while the chats stay here." );
			var cancelled = module.Switching!.Completion;
			module.CancelSwitch();
			Pump( queue );
			CoreChecks.Check( cancelled is { IsCompleted: true, Result: false } && module.Switching is null && module.RunningRole == RemoteModule.LocalRole
				&& app.Settings.Get( RemoteModule.Role, SettingsScope.Global ) == RemoteModule.LocalRole, "Cancel ends the wait and sets the role back to This Loom." );
			store.SetWorking( first, false );
			Pump( queue );
			CoreChecks.Check( module.Switching is null && module.Daemon is null && module.IsWriter, "An agent finishing after Cancel moves nothing." );

			// The switch moves the chats once the agent finished.
			store.SetWorking( first, true );
			var toDaemon = module.SwitchRole( RemoteModule.DaemonRole );
			Pump( queue );
			CoreChecks.Check( !toDaemon.IsCompleted && module.CountWaiting() == 1 && module.IsWriter, "Switching to the daemon waits while an agent works." );
			var attached = new List<IReadOnlyList<ConversationMessage>>();
			store.MessagesReplaced += ( id, messages ) =>
			{
				if ( id == first )
					attached.Add( messages );
			};
			store.SetWorking( first, false );
			CoreChecks.Check( await PumpUntilAsync( queue, () => toDaemon.IsCompleted ) && toDaemon.Result && module.RunningRole == RemoteModule.DaemonRole && module.Daemon is not null
				&& !module.IsWriter && store.IsMirror && store.ProjectStore.IsMirror && app.Hosts.Local.BrowserTabs.IsMirror,
				"Once no agent works the chats move: the lock is let go, the stores and tabs mirror the daemon, and the daemon link starts." );
			CoreChecks.Check( await PumpUntilAsync( queue, () => module.Daemon is { Status: ClientState.Connected } && attached.Count > 0 ),
				$"The daemon starts from the built Loom.exe and the panel's chat attaches. {module.Daemon?.StatusText} {RemoteGatewayChecks.ReadLog( bare )}" );
			using ( var held = TakeLock( bare ) )
				CoreChecks.Check( held is null && DaemonIsRunning(), "The daemon runs and holds chats.lock." );
			panel = FindPanel( app, panelId );
			CoreChecks.Check( app.Panels.Panels.Any( info => info.Id == panelId ) && panel.Chat == ChatRef.Local( first ) && panel.View?.Session is RemoteChatSession
				&& attached[^1].Select( message => message.Text ).SequenceEqual( texts ) && store.GetMessages( first ).Select( message => message.Text ).SequenceEqual( texts ),
				"The chat panel stays open on the same chat, now over a remote session whose messages came from the daemon." );

			// A change made through the mirror is the daemon's to write; this Loom reads it back once the chats return.
			store.SetPinned( second, true );
			CoreChecks.Check( await PumpUntilAsync( queue, () => IsPinnedOnDisk( bare, second ) ), "The daemon saves a change made through the mirror. " + RemoteGatewayChecks.ReadLog( bare ) );
			var toLocal = module.SwitchRole( RemoteModule.LocalRole );
			CoreChecks.Check( await PumpUntilAsync( queue, () => toLocal.IsCompleted ) && toLocal.Result,
				$"Switching back completes. {module.Notice} {app.LastError} {RemoteGatewayChecks.ReadLog( bare )}" );
			CoreChecks.Check( module.RunningRole == RemoteModule.LocalRole && module.Daemon is null && module.IsWriter && !DaemonIsRunning()
				&& app.Settings.Get( RemoteModule.Role, SettingsScope.Global ) == RemoteModule.LocalRole,
				"Moving back stops the daemon and this Loom takes the lock again." );
			CoreChecks.Check( !store.IsMirror && !store.ProjectStore.IsMirror && !app.Hosts.Local.BrowserTabs.IsMirror && store.Find( second ) is { IsPinned: true }
				&& store.Find( first ) is not null && store.GetMessages( first ).Select( message => message.Text ).SequenceEqual( texts ),
				"The stores are authoritative again and read the daemon's files: the same chats and messages, with the change made in the daemon." );
			panel = FindPanel( app, panelId );
			CoreChecks.Check( app.Panels.Panels.Any( info => info.Id == panelId ) && panel.Chat == ChatRef.Local( first ) && panel.View?.Session is ChatSession,
				"The chat panel stays open on the same chat over a local session." );
			CoreChecks.Check( AppDomain.CurrentDomain.GetData( RoleSlot ) as string == RemoteModule.LocalRole, "The running role is latched for a reload of the Remote module." );
		}
		finally
		{
			Finish( app, loom, bare, previousRoot );
		}
	}

	/// <summary>Starts a Loom in the daemon role whose daemon exits at once, and checks it falls back to running the chats itself after three failures.</summary>
	private static async Task CheckFallbackAsync( string loom )
	{
		var bare = CreateBare( "lokn-fallback-", RemoteModule.DaemonRole );
		var (first, _, texts) = SeedChats( bare );
		var previousRoot = LoknPaths.DataRoot;
		var queue = new List<Action>();
		LoknApplication? app = null;
		try
		{
			// The daemons this Loom starts inherit it and exit with code 3 before they take their mutex.
			Environment.SetEnvironmentVariable( TestExitVariable, "3" );
			LoknPaths.UseDataRoot( bare );
			AppDomain.CurrentDomain.SetData( RoleSlot, null );
			(app, var store, var module) = Start( bare, loom, queue );
			CoreChecks.Check( module.RunningRole == RemoteModule.DaemonRole && module.Daemon is not null && !module.IsWriter, "The Loom starts in the daemon role." );
			CoreChecks.Check( await PumpUntilAsync( queue, () => module.RunningRole == RemoteModule.LocalRole && module.Switching is null ),
				$"After {DaemonFailures.Limit} failed starts This PC's chats run in this Loom again. {module.Daemon?.StatusText} {RemoteGatewayChecks.ReadLog( bare )}" );
			var notification = app.Notifications.Items.FirstOrDefault( item => item.Source == "remote" );
			CoreChecks.Check( notification is not null && notification.Message.Contains( "exit code 3", StringComparison.Ordinal )
				&& notification.Message.Contains( RemoteClient.DaemonLogPath, StringComparison.Ordinal ) && module.Notice == notification.Message,
				$"A notification and Remote access's note name the reason and the daemon's log. {notification?.Message}" );
			CoreChecks.Check( module.Daemon is null && module.IsWriter && !store.IsMirror && store.GetMessages( first ).Select( message => message.Text ).SequenceEqual( texts ),
				"This Loom holds the chats again, and they are intact." );
			CoreChecks.Check( RemoteGatewayChecks.ReadLog( bare ).Contains( "EXIT test hook 3", StringComparison.Ordinal ), "The daemons exited through the test hook." );
			CoreChecks.Check( ReadSavedRole( bare ) == RemoteModule.LocalRole, "The local role is saved." );
		}
		finally
		{
			Environment.SetEnvironmentVariable( TestExitVariable, null );
			Finish( app, loom, bare, previousRoot );
		}
	}

	/// <summary>Creates a bare folder whose settings run chats with the fixture, hide the daemon's icon and choose a role, and whose daemon runs only the
	/// modules it needs.</summary>
	private static string CreateBare( string prefix, string role )
	{
		var bare = Path.Combine( Path.GetTempPath(), prefix + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( bare );
		var seed = new LoknApplication( Path.Combine( bare, "settings.json" ) );
		CoreChecks.Check( seed.Load( new OmpModule() ) && seed.Load( new FixtureModule( "remote", builder => builder.Setting( RemoteModule.Role ).Setting( RemoteModule.Tray ) ) ),
			"The seed settings register." );
		seed.Settings.Set( OmpModule.Executable, SettingsScope.Global, Path.Combine( AppContext.BaseDirectory, "Lokn.Tests.exe" ) );
		seed.Settings.Set( RemoteModule.Tray, SettingsScope.Global, false );
		seed.Settings.Set( RemoteModule.Role, SettingsScope.Global, role );
		seed.Shutdown();
		File.WriteAllText( Path.Combine( bare, "modules.json" ), """{ "On": [], "Off": [ "git", "browser" ] }""" );
		return bare;
	}

	/// <summary>Writes two chats with messages to a bare folder's history.</summary>
	/// <returns>The chats and the first one's message texts.</returns>
	private static (Guid First, Guid Second, string[] Texts) SeedChats( string bare )
	{
		var store = new ConversationStore( new ProjectStore( Path.Combine( bare, "projects.json" ) ), Path.Combine( bare, "conversations.json" ) );
		var first = store.CreateConversation( SettingsProfile.Personal, title: "Handover one" ).Id;
		var second = store.CreateConversation( SettingsProfile.Personal, title: "Handover two" ).Id;
		string[] texts = ["Move this chat", "It moved with its messages"];
		store.AppendMessage( first, new ConversationMessage( Guid.NewGuid(), "user", texts[0], DateTimeOffset.UtcNow ) );
		store.AppendMessage( first, new ConversationMessage( Guid.NewGuid(), "assistant", texts[1], DateTimeOffset.UtcNow ) );
		store.AppendMessage( second, new ConversationMessage( Guid.NewGuid(), "user", "Second chat", DateTimeOffset.UtcNow ) );
		store.Flush();
		return (first, second, texts);
	}

	/// <summary>Starts a Loom over a bare folder, with its UI thread a queue the check pumps: Omp, Projects, Conversations with chat panels, and Remote
	/// starting the built Loom.exe as its daemon.</summary>
	private static (LoknApplication App, ConversationStore Store, RemoteModule Module) Start( string bare, string loom, List<Action> queue )
	{
		var app = new LoknApplication( Path.Combine( bare, "settings.json" ), browserTabsPath: Path.Combine( bare, "browser-tabs.json" ) );
		app.BindDispatcher( action =>
		{
			lock ( queue )
				queue.Add( action );
			return true;
		} );
		var store = new ConversationStore( new ProjectStore( Path.Combine( bare, "projects.json" ) ), Path.Combine( bare, "conversations.json" ) );
		var module = new RemoteModule( new RemoteHooks { DataFolder = bare, DaemonExecutable = loom } );
		CoreChecks.Check( app.Load( new OmpModule() ) && ProjectChecks.LoadProjects( app, store ) && app.Load( new ConversationsModule( store, ConversationsModule.BuildChat ) )
			&& app.Load( module ), "Omp, Projects, Conversations and Remote load over the bare folder. " + app.LastError );
		Pump( queue );
		return (app, store, module);
	}

	/// <summary>Shuts the Loom down, stops a daemon left running, restores the data folder and the role latch, and deletes the bare folder.</summary>
	private static void Finish( LoknApplication? app, string loom, string bare, string previousRoot )
	{
		app?.Shutdown();
		if ( DaemonIsRunning() )
		{
			using var stop = RemoteGatewayChecks.StartLoom( loom, "--host", "--stop", "--bare", bare );
			stop.WaitForExit( (int)StepTimeout.TotalMilliseconds );
		}
		var clock = Stopwatch.StartNew();
		while ( DaemonIsRunning() && clock.Elapsed < StepTimeout )
			Thread.Sleep( 100 );
		LoknPaths.UseDataRoot( previousRoot );
		AppDomain.CurrentDomain.SetData( RoleSlot, null );
		RemoteGatewayChecks.TryDelete( bare );
	}

	/// <summary>Reports whether the daemon of the data folder <see cref="LoknPaths.DataRoot"/> names runs.</summary>
	private static bool DaemonIsRunning() => OperatingSystem.IsWindows() && DaemonControl.IsRunning;

	/// <summary>Takes a bare folder's chats lock, or answers null while another process holds it.</summary>
	private static DataLock? TakeLock( string bare ) => DataLock.TryTake( bare );

	/// <summary>Reads the role saved in a bare folder's settings file, as the next start of Loom would.</summary>
	private static string ReadSavedRole( string bare )
	{
		var reader = new LoknApplication( Path.Combine( bare, "settings.json" ) );
		try
		{
			CoreChecks.Check( reader.Load( new FixtureModule( "remote", builder => builder.Setting( RemoteModule.Role ) ) ), "The role setting registers." );
			return reader.Settings.Get( RemoteModule.Role, SettingsScope.Global );
		}
		finally
		{
			reader.Shutdown();
		}
	}

	/// <summary>Reports whether a bare folder's history file, as the daemon last wrote it, has a chat pinned; reading it changes nothing.</summary>
	private static bool IsPinnedOnDisk( string bare, Guid chatId ) =>
		new ConversationStore( Path.Combine( bare, "conversations.json" ) ).Find( chatId ) is { IsPinned: true };

	/// <summary>Gets an open conversation panel by identifier.</summary>
	private static ConversationPanel FindPanel( LoknApplication app, Guid panelId ) =>
		(ConversationPanel)app.Panels.Sessions.Single( session => session.Info.Id == panelId ).Content!;

	/// <summary>Runs every queued action, and the actions they queue, until none waits.</summary>
	private static void Pump( List<Action> queue )
	{
		while ( true )
		{
			Action[] actions;
			lock ( queue )
			{
				actions = [.. queue];
				queue.Clear();
			}
			if ( actions.Length == 0 )
				return;

			foreach ( var action in actions )
				action();
		}
	}

	/// <summary>Pumps the queue until a condition holds or a step's time is up.</summary>
	private static async Task<bool> PumpUntilAsync( List<Action> queue, Func<bool> condition )
	{
		var clock = Stopwatch.StartNew();
		while ( clock.Elapsed < StepTimeout )
		{
			Pump( queue );
			if ( condition() )
				return true;

			await Task.Delay( 20 );
		}
		Pump( queue );
		return condition();
	}
}
