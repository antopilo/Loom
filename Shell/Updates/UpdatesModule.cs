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

	/// <summary>Stores what holds the app update back while agents work on This PC, while the module runs.</summary>
	private UpdateGate? _gate;

	/// <summary>Stores the timer that checks the agents' work while the update waits for them to finish, or null.</summary>
	private Timer? _waitTimer;

	/// <summary>Defines how often a waiting update checks whether the agents finished.</summary>
	private static readonly TimeSpan _waitInterval = TimeSpan.FromSeconds( 2 );

	/// <summary>Says where an update check started, which decides what its outcome shows.</summary>
	internal enum CheckOrigin
	{
		/// <summary>The background timer, which announces a new version once.</summary>
		Background,

		/// <summary>The Updates: check for updates command, which always announces its outcome and reports a failure.</summary>
		Command,

		/// <summary>The Check for updates button on the Settings page, which shows its outcome there and posts a ready update's notice with its chime.</summary>
		Settings,
	}

	/// <summary>Holds what the last update check found, for the Settings page.</summary>
	/// <param name="CheckedAt">When the last check finished, or null before one finished.</param>
	/// <param name="IsChecking">Whether a check runs now.</param>
	/// <param name="AppVersion">The Loom version downloaded and waiting for a restart, or null.</param>
	/// <param name="Modules">The module updates staged and waiting to apply.</param>
	/// <param name="Error">Why the last check could not start or failed, or null.</param>
	internal sealed record UpdateStatus( DateTimeOffset? CheckedAt, bool IsChecking, string? AppVersion, IReadOnlyList<ModuleRelease> Modules, string? Error );

	/// <summary>Stores what the last check found; the Settings page reads it while it builds, so the page rebuilds when a check ends.</summary>
	private readonly Goo.State<UpdateStatus> _status = new( new UpdateStatus( null, false, null, [], null ) );

	/// <summary>Gets the module identifier.</summary>
	public string Id => "updates";

	/// <summary>Gets the display name.</summary>
	public string Title => "Updates";

	/// <summary>Gets the running Loom version, such as 0.2.0.</summary>
	internal static string CurrentVersion => typeof( UpdatesModule ).Assembly.GetName().Version?.ToString( 3 ) ?? "?";

	/// <summary>Registers the settings, the check command, routes for notification buttons, and the What's new panel type.</summary>
	public void Register( ModuleBuilder builder )
	{
		builder
			.Setting( Automatic )
			.Setting( Source )
			.PanelOnly( "whats-new", "What's new", WhatsNewPanel.Create )
			.Command( "check", "Updates: check for updates", _ => StartCheck( CheckOrigin.Command ) )
			.Route( "restart", request => RequestRestart( request.App, isForced: false ) )
			.Route( "restart-when-idle", request => WaitForAgents( request.App ) )
			.Route( "restart-now", request => RequestRestart( request.App, isForced: true ) )
			.Route( "apply-modules", request => ApplyModules( request.App ) );
	}

	/// <summary>Starts the background checks in an installed Loom, reports module updates that do not fit, and shows the What's new panel if this is an upgrade.</summary>
	public void Start( ModuleContext context )
	{
		_context = context;
		_gate = new UpdateGate( () => CountWorking( context.App ), () => Restart( context.App ) );
		host.Refused += ReportRefused;
		if ( Installation.IsInstalled )
			_timer = new( _ => context.Post( () => StartCheck( CheckOrigin.Background ) ), null, _firstCheck, _interval );
		// Show the What's new panel on startup if this is an upgrade.
		context.Post( () => WhatsNewPanel.TryOpenOnStartup( context.App ) );
	}

	/// <summary>Stops the background checks; a running check ends with the module's lifetime.</summary>
	public void Stop()
	{
		_timer?.Dispose();
		_timer = null;
		_waitTimer?.Dispose();
		_waitTimer = null;
		_gate?.Cancel();
		_gate = null;
		host.Refused -= ReportRefused;
	}

	/// <summary>Gets what the last update check found, for the Settings page.</summary>
	internal UpdateStatus Status => _status.Value;

	/// <summary>Runs a check for the Settings page's Check for updates button; its outcome shows there, and a ready update also posts its notice with the chime.</summary>
	internal void Check() => StartCheck( CheckOrigin.Settings );

	/// <summary>Restarts into the downloaded Loom for the Settings page, asking first when agents work on This PC.</summary>
	internal void RestartLoom( LoknApplication app ) => RequestRestart( app, isForced: false );

	/// <summary>Moves the staged module updates into use for the Settings page.</summary>
	internal void ApplyModuleUpdates( LoknApplication app ) => ApplyModules( app );

	/// <summary>Reads the settings on the UI thread and starts a check in the background; the origin decides what its outcome shows.</summary>
	private void StartCheck( CheckOrigin origin )
	{
		if ( _context is not { Lifetime.IsCancellationRequested: false } context )
			return;

		var app = context.App;
		if ( origin == CheckOrigin.Background && !app.Settings.Get( Automatic ) )
			return;

		if ( !Installation.IsInstalled )
		{
			Refuse( app, origin, "Updates work only in an installed Loom. This one runs from a build." );
			return;
		}

		if ( UpdateFeed.Parse( app.Settings.Get( Source ) ) is not { } feed )
		{
			Refuse( app, origin, "No update source is set. Set one under Settings › Updates." );
			return;
		}

		if ( Interlocked.Exchange( ref _isChecking, 1 ) == 1 )
			return;

		_status.Value = _status.Value with { IsChecking = true, Error = null };
		_ = Task.Run( () => CheckAsync( feed, context, origin ) );
	}

	/// <summary>Shows why a check did not start: a notice for the command, the Settings status for its button, and nothing for a background check.</summary>
	private void Refuse( LoknApplication app, CheckOrigin origin, string message )
	{
		if ( origin == CheckOrigin.Command )
			Post( app, message );
		else if ( origin == CheckOrigin.Settings )
			_status.Value = _status.Value with { Error = message };
	}

	/// <summary>Downloads a new Loom, or else the module updates that fit, and reports what waits or why the check failed.</summary>
	private async Task CheckAsync( UpdateFeed feed, ModuleContext context, CheckOrigin origin )
	{
		try
		{
			var version = await _appUpdater.DownloadAsync( feed, context.Lifetime ).ConfigureAwait( false );
			var modules = version is null ? await _moduleUpdater.StageAsync( feed, context, context.Lifetime ).ConfigureAwait( false ) : [];
			context.Post( () => Announce( context.App, version, modules, origin ) );
		}
		catch ( OperationCanceledException ) when ( context.Lifetime.IsCancellationRequested )
		{
		}
		catch ( Exception error )
		{
			// A background check fails quietly, such as when offline; a command or the Settings page shows why.
			UpdateLog( $"FAILED check: {error.Message}" );
			context.Post( () => Fail( context.App, error, origin ) );
		}
		finally
		{
			Volatile.Write( ref _isChecking, 0 );
		}
	}

	/// <summary>Says which Loom version waits for a restart; the notice and the Settings page both show it.</summary>
	internal static string AppReadyMessage( string version ) => $"Loom {version} is ready. Restart Loom to use it.";

	/// <summary>Says which module updates wait to apply; the notice and the Settings page both show it.</summary>
	internal static string ModulesReadyMessage( IReadOnlyList<ModuleRelease> modules )
	{
		var names = string.Join( ", ", modules.Select( module => $"{ReadTitle( module.Name )} {module.Version}" ) );
		return $"Module updates are ready: {names}. They apply the next time Loom starts.";
	}

	/// <summary>Says that Loom and its modules are current; the notice and the Settings page both show it.</summary>
	internal static string UpToDateMessage => $"Loom {CurrentVersion} and its modules are up to date.";

	/// <summary>Records what the check found for the Settings page, then posts what waits as ShouldNotify allows.</summary>
	private void Announce( LoknApplication app, string? version, IReadOnlyList<ModuleRelease> modules, CheckOrigin origin )
	{
		_status.Value = new UpdateStatus( DateTimeOffset.Now, false, version, modules, null );
		if ( version is not null )
		{
			if ( ShouldNotify( origin, version != _announcedApp ) )
				Post( app, AppReadyMessage( version ), new NotificationAction( "Restart now", LoknLinks.Create( Id, "restart" ) ) );
			_announcedApp = version;
			return;
		}

		if ( modules.Count > 0 )
		{
			var message = ModulesReadyMessage( modules );
			if ( ShouldNotify( origin, message != _announcedModules ) )
				Post( app, message, new NotificationAction( "Apply now", LoknLinks.Create( Id, "apply-modules" ) ) );
			_announcedModules = message;
			return;
		}

		if ( origin == CheckOrigin.Command )
			Post( app, UpToDateMessage );
	}

	/// <summary>Tells whether a check's result is posted: a command and the Settings button always, a background check when it changed.</summary>
	private static bool ShouldNotify( CheckOrigin origin, bool isChanged ) => origin != CheckOrigin.Background || isChanged;

	/// <summary>Records a failed check for the Settings page; a command also reports it as an error.</summary>
	private void Fail( LoknApplication app, Exception error, CheckOrigin origin )
	{
		_status.Value = _status.Value with { IsChecking = false, Error = $"Could not check for updates: {error.Message}" };
		if ( origin == CheckOrigin.Command )
			app.ReportError( "Updates", error );
	}

	/// <summary>Restarts into the downloaded version when no agent works on This PC, or when <paramref name="isForced"/>; otherwise asks whether to update
	/// when the agents finish or now, which stops them.</summary>
	private void RequestRestart( LoknApplication app, bool isForced )
	{
		if ( _gate is not { } gate || !_appUpdater.IsPending )
		{
			Post( app, "No new version of Loom is waiting. Run Updates: check for updates." );
			return;
		}

		var working = CountWorking( app );
		switch ( gate.Request( isForced ) )
		{
			case UpdateDecision.Ask:
				Post( app, $"{DescribeWorking( working )} on this PC. Loom can update once they finish, or now, which stops them.",
					new NotificationAction( "Update when agents finish", LoknLinks.Create( Id, "restart-when-idle" ) ),
					new NotificationAction( UpdateGate.DescribeForce( working ), LoknLinks.Create( Id, "restart-now" ) ) );
				break;
			case UpdateDecision.Wait:
				Post( app, $"Loom updates once the agents on this PC finish; {DescribeWorking( working ).ToLowerInvariant()}.",
					new NotificationAction( UpdateGate.DescribeForce( working ), LoknLinks.Create( Id, "restart-now" ) ) );
				break;
		}
	}

	/// <summary>Restarts into the downloaded version as soon as no agent works on This PC, checking every few seconds meanwhile.</summary>
	private void WaitForAgents( LoknApplication app )
	{
		if ( _gate is not { } gate || _context is not { } context || !_appUpdater.IsPending )
		{
			Post( app, "No new version of Loom is waiting. Run Updates: check for updates." );
			return;
		}

		gate.WaitForAgents();
		if ( !gate.IsWaiting )
			return;

		Post( app, "Loom updates once the agents on this PC finish.", new NotificationAction( UpdateGate.DescribeForce( CountWorking( app ) ), LoknLinks.Create( Id, "restart-now" ) ) );
		_waitTimer?.Dispose();
		_waitTimer = new( _ => context.Post( () =>
		{
			if ( _gate is { } current && !current.Update() && current.IsWaiting )
				return;

			_waitTimer?.Dispose();
			_waitTimer = null;
		} ), null, _waitInterval, _waitInterval );
	}

	/// <summary>Closes Loom and restarts it on the downloaded version; the update's hook stops the daemon gracefully and starts it again afterwards.</summary>
	private void Restart( LoknApplication app )
	{
		if ( app.Window is not { } window || !_appUpdater.Restart( window ) )
			Post( app, "No new version of Loom is waiting. Run Updates: check for updates." );
	}

	/// <summary>Counts the agents working on This PC, in this Loom or its daemon; none when no module reports them.</summary>
	private static int CountWorking( LoknApplication app ) => app.Services.Get<IAgentActivity>()?.Working ?? 0;

	/// <summary>Describes how many agents work: <c>1 agent is working</c>, <c>2 agents are working</c>.</summary>
	private static string DescribeWorking( int working ) => working == 1 ? "1 agent is working" : $"{working} agents are working";

	/// <summary>Moves the staged module updates into use, which reloads their modules.</summary>
	private void ApplyModules( LoknApplication app )
	{
		_announcedModules = "";
		_status.Value = _status.Value with { Modules = [] };
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
	internal static string ReadTitle( string assemblyName ) => assemblyName.StartsWith( "Lokn.Modules.", StringComparison.Ordinal ) ? assemblyName["Lokn.Modules.".Length..] : assemblyName;

	/// <summary>Appends one timestamped line to the updates log; a logging failure is ignored.</summary>
	private static void UpdateLog( string line )
	{
		try
		{
			var path = Path.Combine( LoknPaths.TempRoot, "updates.log" );
			Directory.CreateDirectory( Path.GetDirectoryName( path )! );
			File.AppendAllText( path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}" );
		}
		catch ( IOException )
		{
		}
	}
}
