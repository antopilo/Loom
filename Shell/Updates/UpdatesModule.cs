using Lokn.Core;

namespace Lokn;

/// <summary>Keeps an installed Loom and its modules up to date: checks the update feed in the background, downloads, and asks before applying.</summary>
/// <remarks>
/// A new Loom is announced with <b>Restart now</b>; module updates with <b>Apply now</b>, which reloads those modules. Both otherwise apply
/// the next time Loom starts. While a new Loom waits, module updates are not fetched, because it brings its own modules.
/// </remarks>
/// <param name="host">The module host, which loads module updates and checks that they fit.</param>
internal sealed class UpdatesModule( ModuleHost host ) : ILoknModule
{
	/// <summary>Defines the feed releases are published to.</summary>
	internal const string DefaultSource = "https://github.com/antopilo/Loom";

	/// <summary>Defines the wait after startup before the first background check, so startup stays quick.</summary>
	private static readonly TimeSpan _firstCheck = TimeSpan.FromSeconds( 30 );

	/// <summary>Defines the time between background checks.</summary>
	private static readonly TimeSpan _interval = TimeSpan.FromHours( 6 );

	/// <summary>Defines whether Loom checks for updates in the background.</summary>
	internal static readonly Setting<bool> Automatic = new(
		"updates.automatic", "Check for updates automatically",
		"Download new versions of Loom and its modules in the background. Loom asks before it applies them.",
		true, "Updates" );

	/// <summary>Defines where Loom looks for new versions.</summary>
	internal static readonly Setting<string> Source = new(
		"updates.source", "Update source",
		"Where Loom looks for new versions: a GitHub repository, a web address or a folder. Leave it blank to turn updates off.",
		DefaultSource, "Updates" );

	/// <summary>Stores the application updater.</summary>
	private readonly AppUpdater _appUpdater = new();

	/// <summary>Stores the module updater.</summary>
	private readonly ModuleUpdater _moduleUpdater = new( host );

	/// <summary>Stores the running module context, or null before the first start.</summary>
	private ModuleContext? _context;

	/// <summary>Stores the background check timer while the module runs in an installed Loom.</summary>
	private Timer? _timer;

	/// <summary>Stores 1 while a check runs, so checks never overlap.</summary>
	private int _isChecking;

	/// <summary>Stores the Loom version last announced, so a background check announces it once.</summary>
	private string? _announcedApp;

	/// <summary>Stores the module updates last announced, so a background check announces them once.</summary>
	private string _announcedModules = "";

	/// <summary>Gets the module identifier.</summary>
	public string Id => "updates";

	/// <summary>Gets the display name.</summary>
	public string Title => "Updates";

	/// <summary>Gets the running Loom version, such as 0.2.0.</summary>
	private static string CurrentVersion => typeof( UpdatesModule ).Assembly.GetName().Version?.ToString( 3 ) ?? "?";

	/// <summary>Registers the settings, the check command, and the routes the notifications' buttons follow.</summary>
	public void Register( ModuleBuilder builder )
	{
		builder
			.Setting( Automatic )
			.Setting( Source )
			.Command( "check", "Updates: check for updates", _ => StartCheck( isManual: true ) )
			.Route( "restart", request => Restart( request.App ) )
			.Route( "apply-modules", request => ApplyModules( request.App ) );
	}

	/// <summary>Starts the background checks in an installed Loom, and reports module updates that do not fit.</summary>
	public void Start( ModuleContext context )
	{
		_context = context;
		host.Refused += ReportRefused;
		if ( Installation.IsInstalled )
			_timer = new( _ => context.Post( () => StartCheck( isManual: false ) ), null, _firstCheck, _interval );
	}

	/// <summary>Stops the background checks; a running check ends with the module's lifetime.</summary>
	public void Stop()
	{
		_timer?.Dispose();
		_timer = null;
		host.Refused -= ReportRefused;
	}

	/// <summary>Reads the settings on the UI thread and starts a check in the background; a manual check always reports its outcome.</summary>
	private void StartCheck( bool isManual )
	{
		if ( _context is not { Lifetime.IsCancellationRequested: false } context )
			return;

		var app = context.App;
		if ( !isManual && !app.Settings.Get( Automatic ) )
			return;

		if ( !Installation.IsInstalled )
		{
			if ( isManual )
				Post( app, "Updates work only in an installed Loom. This one runs from a build." );
			return;
		}

		if ( UpdateFeed.Parse( app.Settings.Get( Source ) ) is not { } feed )
		{
			if ( isManual )
				Post( app, "No update source is set. Set one under Settings › Updates." );
			return;
		}

		if ( Interlocked.Exchange( ref _isChecking, 1 ) == 1 )
			return;

		_ = Task.Run( () => CheckAsync( feed, context, isManual ) );
	}

	/// <summary>Downloads a new Loom, or else the module updates that fit, and announces what waits.</summary>
	private async Task CheckAsync( UpdateFeed feed, ModuleContext context, bool isManual )
	{
		try
		{
			var version = await _appUpdater.DownloadAsync( feed, context.Lifetime ).ConfigureAwait( false );
			var modules = version is null ? await _moduleUpdater.StageAsync( feed, context, context.Lifetime ).ConfigureAwait( false ) : [];
			context.Post( () => Announce( context.App, version, modules, isManual ) );
		}
		catch ( OperationCanceledException ) when ( context.Lifetime.IsCancellationRequested )
		{
		}
		catch ( Exception error )
		{
			// A background check fails quietly, such as when offline; a manual one shows why.
			UpdateLog( $"FAILED check: {error.Message}" );
			if ( isManual )
				context.Post( () => context.App.ReportError( "Updates", error ) );
		}
		finally
		{
			Volatile.Write( ref _isChecking, 0 );
		}
	}

	/// <summary>Posts what waits to the notification feed, once per version unless the user asked.</summary>
	private void Announce( LoknApplication app, string? version, IReadOnlyList<ModuleRelease> modules, bool isManual )
	{
		if ( version is not null )
		{
			if ( isManual || version != _announcedApp )
				Post( app, $"Loom {version} is ready. Restart Loom to use it.", new NotificationAction( "Restart now", LoknLinks.Create( Id, "restart" ) ) );
			_announcedApp = version;
			return;
		}

		if ( modules.Count > 0 )
		{
			var names = string.Join( ", ", modules.Select( module => $"{ReadTitle( module.Name )} {module.Version}" ) );
			if ( isManual || names != _announcedModules )
				Post( app, $"Module updates are ready: {names}. They apply the next time Loom starts.", new NotificationAction( "Apply now", LoknLinks.Create( Id, "apply-modules" ) ) );
			_announcedModules = names;
			return;
		}

		if ( isManual )
			Post( app, $"Loom {CurrentVersion} and its modules are up to date." );
	}

	/// <summary>Closes Loom and restarts it on the downloaded version.</summary>
	private void Restart( LoknApplication app )
	{
		if ( app.Window is not { } window || !_appUpdater.Restart( window ) )
			Post( app, "No new version of Loom is waiting. Run Updates: check for updates." );
	}

	/// <summary>Moves the staged module updates into use, which reloads their modules.</summary>
	private void ApplyModules( LoknApplication app )
	{
		_announcedModules = "";
		if ( host.ApplyStaged() == 0 )
			Post( app, "No module updates are waiting." );
	}

	/// <summary>Tells the user that a module update did not load; the details are in the module log.</summary>
	private void ReportRefused( string name, Version version, string problem )
	{
		if ( _context is { } context )
			Post( context.App, $"Loom did not load {ReadTitle( name )} {version.ToString( 3 )} because it does not match this version of Loom. The previous version runs." );
	}

	/// <summary>Posts one notification under Updates.</summary>
	private static void Post( LoknApplication app, string message, params NotificationAction[] actions )
	{
		app.Notifications.Post( new( "Updates", message )
		{
			Source = "updates",
			Actions = actions,
		} );
	}

	/// <summary>Reads a module's name from its assembly name, such as Git from <c>Lokn.Modules.Git</c>.</summary>
	private static string ReadTitle( string assemblyName ) => assemblyName.StartsWith( "Lokn.Modules.", StringComparison.Ordinal ) ? assemblyName["Lokn.Modules.".Length..] : assemblyName;

	/// <summary>Appends one timestamped line to the updates log; a logging failure is ignored.</summary>
	private static void UpdateLog( string line )
	{
		try
		{
			var path = Path.Combine( Path.GetTempPath(), "Lokn", "updates.log" );
			Directory.CreateDirectory( Path.GetDirectoryName( path )! );
			File.AppendAllText( path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}" );
		}
		catch ( IOException )
		{
		}
	}
}
