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
		// The installer starts Loom with a hook argument and expects it to exit at once, before any window or single-instance election.
		if ( OperatingSystem.IsWindows() )
			VelopackApp.Build().SetArgs( args ).OnBeforeUninstallFastCallback( _ => Installation.RemoveRegistration() ).Run();
		// --bare <folder> runs a second, isolated instance with only the shell: its own app ID, data files and an empty module folder.
		var bare = Array.IndexOf( args, "--bare" ) is var index and >= 0 && index + 1 < args.Length ? Path.GetFullPath( args[index + 1] ) : null;
		// Each bare folder is its own single instance, so several demos run side by side.
		var applicationId = bare is null ? ApplicationId
			: $"{ApplicationId}.bare.{Convert.ToHexString( System.Security.Cryptography.SHA256.HashData( System.Text.Encoding.UTF8.GetBytes( bare.ToUpperInvariant() ) ) )[..8].ToLowerInvariant()}";
		Application.Configure( "Loom", typeof( Program ).Assembly.GetName().Version?.ToString( 3 ) ?? "1.0.0", applicationId );
		var app = bare is null
			? new LoknApplication( SettingsService.DefaultPath, PanelWorkspace.DefaultLayoutPath, NotificationFeed.DefaultPath )
			: new LoknApplication( Path.Combine( bare, "settings.json" ), Path.Combine( bare, "workspace.json" ), Path.Combine( bare, "notifications.json" ) );
		app.ApplicationId = applicationId;
		// The shell module was called workspace; its saved settings move to the new names once.
		app.Settings.MoveSaved( "workspace.", "shell." );
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

		// Notification clicks relaunch Lokn, so a second launch hands its request to the running window and exits.
		// The notifier subscribes to activations first so none is missed; a failed election runs standalone.
		using var attention = new AttentionNotifier( app, window );
		var instance = Application.AcquireSingleInstance( args );
		if ( instance.Status == SingleInstanceStatus.Forwarded )
			return;

		using var lease = instance.Lease;

		// Bind before startup so feature work can queue safely while the window opens.
		app.BindDispatcher( window.Post );
		app.Commands.BindDispatcher( operation => window.InvokeAsync( operation ) );
		// The theme is part of the shell; it loads first so every module's views draw with it.
		app.Load( new ThemeModule( bare is null ? ThemeModule.DefaultPath : Path.Combine( bare, "theme.json" ) ) );
		// Feature modules are separate DLLs, loaded here and reloaded whenever one is rebuilt or updated; an installed Loom also loads downloaded module updates.
		// A bare instance sees every installed module but starts with all of them off; Settings › Modules turns them on, and its choice is kept in its folder.
		var modules = new ModuleHost( app, ModuleHost.DefaultDirectory, bare is null ? Installation.ModuleUpdatesDirectory : null, ModuleHost.DefaultLogPath,
			bare is null ? new ModuleStates( ModuleStates.DefaultPath, isOffByDefault: false ) : new ModuleStates( Path.Combine( bare, "modules.json" ), isOffByDefault: true ) );
		modules.Start();
		app.Load( new ShellModule() );
		ShellBackdrop.Apply( window, app.Settings.Get( ShellSettings.Backdrop ) );
		if ( bare is null )
		{
			app.Load( new HotloadModule( HotloadModule.DefaultPath, HotloadModule.DefaultCachePath ) );
			app.Load( new UpdatesModule( modules ) );
		}

		app.RestoreWorkspace();
		// Modules pick their own startup view, such as Home, through ModuleContext.WhenStarted; without one the shell shows its empty pane.
		app.MarkStarted();

		Application.Run( window );
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
