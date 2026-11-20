using Goo;
using Lokn.Core;
using Lokn.Modules.Hotload;
using Velopack;

namespace Lokn;

/// <summary>Starts the Lokn desktop application.</summary>
internal static class Program
{
	/// <summary>Defines the initial client width in logical pixels.</summary>
	private const int InitialWidth = 1200;

	/// <summary>Defines the initial client height in logical pixels.</summary>
	private const int InitialHeight = 800;

	/// <summary>Defines the app ID Windows groups Lokn's windows and notifications under.</summary>
	private const string ApplicationId = "dev.lokn.desktop";

	/// <summary>Opens the main window and runs the UI event loop.</summary>
	// Windows delivers Explorer file drops through OLE, which only initializes on a single-threaded apartment;
	// without this Goo reports that native file drops are unsupported and the window fails to open.
	[STAThread]
	private static void Main( string[] args )
	{
		PerfTrace.Mark( "main" );
		// --bare <folder> runs a second, isolated instance, a window or with --host a daemon, that changes nothing outside its folder (BareInstance): it
		// moves there before anything reads a path, and skips Velopack, which would apply the user's downloaded update, log into %LOCALAPPDATA% and
		// run installer hooks that stop the user's daemon and remove its sign-in entry.
		var bare = BareInstance.Parse( args );
		if ( bare is not null )
			BareInstance.Enter( bare );
		// --host runs the headless daemon (docs/remote.md). Velopack still locates the install, so the daemon takes module updates, but the daemon never
		// applies a downloaded app update itself; the installer never passes --host, so its hook arguments still reach the desktop path below.
		var isHost = OperatingSystem.IsWindows() && args.Contains( "--host" );
		// The installer starts Loom with a hook argument and expects it to exit at once, before any window or single-instance election.
		// An update or uninstall first stops the user's daemon gracefully, so it saves its chats and releases the installed files; after an update the
		// daemon starts again from the new build when it ran before the update or its sign-in entry is on, and uninstalling removes that entry, which
		// nothing else would. A downloaded update is not applied at start while the daemon runs, since its agents may be working: Updates applies it
		// once they finish, or when the user stops them.
		if ( OperatingSystem.IsWindows() && bare is null )
		{
			VelopackApp.Build().SetArgs( args ).SetAutoApplyOnStartup( !isHost && !HostRunner.IsRunning( null ) )
				.OnBeforeUpdateFastCallback( _ =>
				{
					if ( OperatingSystem.IsWindows() )
						HostRunner.StopForUpdate();
				} )
				.OnAfterUpdateFastCallback( _ =>
				{
					if ( OperatingSystem.IsWindows() && Environment.ProcessPath is { } executable )
						HostRunner.StartAfterUpdate( executable );
				} )
				.OnBeforeUninstallFastCallback( _ =>
				{
					if ( OperatingSystem.IsWindows() )
					{
						HostRunner.Stop( null );
						HostRunner.RemoveLoginItem();
					}
					Installation.RemoveRegistration();
				} ).Run();
		}
		PerfTrace.Mark( "installer-hooks" );

		if ( isHost && OperatingSystem.IsWindows() )
		{
			HostRunner.Run( bare, isStop: args.Contains( "--stop" ) );
			return;
		}

		if ( RunDesktop( args, bare ) )
			ShellRestart.Start( args );
	}

	/// <summary>Backs the data up when this version first runs on it, opens the main window and runs the UI event loop until it closes.</summary>
	/// <param name="args">The command line.</param>
	/// <param name="bare">The bare folder, which <see cref="LoknPaths.DataRoot"/> names already, or null for the user's data.</param>
	/// <returns>Whether the user asked Loom to start again once this window closed, such as to restore a backup.</returns>
	private static bool RunDesktop( string[] args, string? bare )
	{
		var version = typeof( Program ).Assembly.GetName().Version?.ToString( 3 ) ?? "1.0.0";
		var dataFolder = LoknPaths.DataRoot;
		// A restore the user asked for replaces the chats, so the daemon that holds them stops first and saves.
		if ( OperatingSystem.IsWindows() && DataUpgrade.ReadPendingRestore( dataFolder ) is not null )
			HostRunner.Stop( bare );
		// Before anything reads or writes the data: copy a restore the user asked for back, and back the data up when a newer Loom first runs on it.
		// The daemon does the same under the same mutex, so whichever starts first backs up once.
		var upgrade = DataUpgrade.Prepare( dataFolder, version, canRestore: true );
		DataBackups.DataFolder = dataFolder;
		PerfTrace.Mark( "data-upgrade" );

		// Each bare folder is its own single instance, so several demos run side by side.
		var applicationId = BareInstance.ComputeApplicationId( ApplicationId, bare );
		Application.Configure( "Loom", version, applicationId );
		var app = new LoknApplication( SettingsService.DefaultPath, PanelWorkspace.DefaultLayoutPath, NotificationFeed.DefaultPath, BrowserTabs.DefaultPath );
		app.ApplicationId = applicationId;
		// The shell module was called workspace; its saved settings move to the new names once.
		app.Settings.MoveSaved( "workspace.", "shell." );
		if ( upgrade.Error is { } upgradeError )
			app.ReportError( "Backup", upgradeError );
		if ( upgrade.RestoredFrom is { } restored )
			app.Notifications.Post( new( "Backup", $"Loom restored the backup {Path.GetFileName( restored )}." ) { Source = "shell" } );
		// A view whose build fails draws a warning mark instead of closing Loom; the failure shows in the shell's error notice once.
		CellFaults.Faulted += ( view, error ) => app.ReportError( view.Name, error );
		var window = new Window
		{
			Title = bare is null ? "Loom" : "Loom (bare)",
			IconPng = ReadIcon(),
			Decorated = false,
			// Transparency is fixed at native-window creation; the shell setting can then switch its material live.
			Transparent = true,
			Background = Theme.Current.Canvas,
			Foreground = Theme.Current.Text,
			Size = new LayoutSize( InitialWidth, InitialHeight ),
			// Chat composers accept files dragged in from Explorer.
			NativeFileDropEnabled = true,
			Root = new ShellView( app ),
		};

		PerfTrace.Mark( "window" );
		// Notification clicks relaunch Lokn, so a second launch hands its request to the running window and exits.
		// The notifier subscribes to activations first so none is missed; a failed election runs standalone.
		using var attention = new AttentionNotifier( app, window );
		var instance = Application.AcquireSingleInstance( args );
		if ( instance.Status == SingleInstanceStatus.Forwarded )
			return false;

		using var lease = instance.Lease;
		PerfTrace.Mark( "single-instance" );

		// Bind before startup so feature work can queue safely while the window opens.
		app.BindDispatcher( window.Post );
		app.Commands.BindDispatcher( operation => window.InvokeAsync( operation ) );
		// The daemon shares settings.json; values it saves, such as its network mode, show here without a restart.
		using var settingsWatch = app.Settings.Watch( window.Post );
		// The theme is part of the shell; it loads first so every module's views draw with it.
		app.Load( new ThemeModule() );
		// Feature modules are separate DLLs, loaded here and reloaded whenever one is rebuilt or updated; an installed Loom also loads downloaded module updates.
		// A bare instance sees every installed module but starts with all of them off; Settings › Modules turns them on, and its choice is kept in its folder.
		var modules = new ModuleHost( app, ModuleHost.DefaultDirectory, bare is null ? Installation.ModuleUpdatesDirectory : null, ModuleHost.DefaultLogPath, ModuleStates.Create( bare is not null ) );
		modules.Start();
		PerfTrace.Mark( "modules" );
		app.Load( new ShellModule() );
		ShellBackdrop.Apply( window, app.Settings.Get( ShellSettings.Backdrop ) );
		if ( bare is null )
		{
			app.Load( new HotloadModule( HotloadModule.DefaultPath, HotloadModule.DefaultCachePath ) );
			// Community modules are source folders from the hub, so they load through Hotload and need it first.
			app.Load( new CommunityModule() );
			app.Load( new UpdatesModule( modules ) );
		}

		PerfTrace.Mark( "shell" );
		app.RestoreWorkspace();
		PerfTrace.Mark( "workspace" );
		// Modules pick their own startup view, such as Home, through ModuleContext.WhenStarted; without one the shell shows its empty pane.
		app.MarkStarted();

		PerfTrace.Mark( "started" );
		PerfTrace.Attach( window );
		Application.Run( window );
		return ShellRestart.IsRequested;
	}

	/// <summary>Reads the window icon embedded in the executable.</summary>
	private static byte[] ReadIcon()
	{
		using var stream = typeof( Program ).Assembly.GetManifestResourceStream( "Loom.png" ) ?? throw new InvalidOperationException( "The embedded window icon is missing." );
		using var buffer = new MemoryStream();
		stream.CopyTo( buffer );
		return buffer.ToArray();
	}
}
