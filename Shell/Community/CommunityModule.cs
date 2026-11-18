using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Installs community modules from the hub repository, keeps them up to date, and backs the Community list in Settings › Modules.</summary>
/// <remarks>
/// Installed modules are source folders in <c>community</c> under the data folder, which this module contributes to
/// <see cref="ModuleFolderRegistry"/>; Hotload compiles and loads them like any source module. The list reads the hub on demand; a background
/// check 20 seconds after startup and then every 6 hours updates installed modules while <see cref="Automatic"/> is on.
/// </remarks>
internal sealed class CommunityModule : ILoknModule
{
	/// <summary>Defines the hub releases are read from.</summary>
	internal const string DefaultSource = "https://github.com/antopilo/loom-modules";

	/// <summary>Defines the wait after startup before the first background check.</summary>
	private static readonly TimeSpan _firstCheck = TimeSpan.FromSeconds( 20 );

	/// <summary>Defines the time between background checks.</summary>
	private static readonly TimeSpan _interval = TimeSpan.FromHours( 6 );

	/// <summary>Defines whether installed community modules update in the background.</summary>
	internal static readonly Setting<bool> Automatic = new(
		"community.automatic", "Update community modules automatically",
		"Install new versions of the community modules you use as soon as the hub has them.",
		true, "Community modules" );

	/// <summary>Defines where the community modules come from.</summary>
	internal static readonly Setting<string> Source = new(
		"community.source", "Community module source",
		"The hub repository on GitHub, or a folder with a hub's index.json and modules folder.",
		DefaultSource, "Community modules", value => value.Trim().Length > 0 );

	/// <summary>Counts changes to the index and the error; reading it in a build rebuilds on each one, since Goo state holds no null.</summary>
	private readonly State<int> _revision = new( 0 );

	/// <summary>Stores the index last read, or null before the first read; changed only on the UI thread.</summary>
	private CommunityIndex? _index;

	/// <summary>Stores why the last read failed, or null; changed only on the UI thread.</summary>
	private string? _error;

	/// <summary>Stores whether a read runs.</summary>
	private readonly State<bool> _isLoading = new( false );

	/// <summary>Stores the installed modules, read again after each change.</summary>
	private readonly State<IReadOnlyList<InstalledModule>> _installed = new( [] );

	/// <summary>Stores the ids of the modules being installed, updated or removed.</summary>
	private readonly State<IReadOnlySet<string>> _busy = new( new HashSet<string>() );

	/// <summary>Stores the running module context, or null before the first start.</summary>
	private ModuleContext? _context;

	/// <summary>Stores the source the list was last read from, to notice when the setting changes.</summary>
	private string _lastSource = DefaultSource;

	/// <summary>Stores the background check timer while the module runs.</summary>
	private Timer? _timer;

	/// <summary>Gets the folder installed community modules go to.</summary>
	internal static string DefaultRoot => LoknPaths.Combine( "community" );

	/// <summary>Gets the module identifier.</summary>
	public string Id => "community";

	/// <summary>Gets the display name.</summary>
	public string Title => "Community modules";

	/// <summary>Keeps installed community modules loaded for the application lifetime.</summary>
	public bool CanUnload => false;

	/// <summary>Gets the index last read, or null before the first read; reading it in a build rebuilds on change.</summary>
	internal CommunityIndex? Index => _revision.Value >= 0 ? _index : null;

	/// <summary>Gets why the last read failed, or null; reading it in a build rebuilds on change.</summary>
	internal string? Error => _revision.Value >= 0 ? _error : null;

	/// <summary>Gets whether a read runs.</summary>
	internal bool IsLoading => _isLoading.Value;

	/// <summary>Gets the installed modules.</summary>
	internal IReadOnlyList<InstalledModule> Installed => _installed.Value;

	/// <summary>Gets the hub the source setting names.</summary>
	internal string SourceLabel => _context?.App.Settings.Get( Source ).Trim() ?? DefaultSource;

	/// <summary>Tells whether a module is being installed, updated or removed.</summary>
	internal bool IsBusy( string id ) => _busy.Value.Contains( id );

	/// <summary>Registers the settings and the update command.</summary>
	public void Register( ModuleBuilder builder )
	{
		builder
			.Setting( Automatic )
			.Setting( Source )
			.Command( "check", "Community modules: check for updates", _ => Refresh( isUpdating: true, isManual: true ) );
	}

	/// <summary>Contributes the installed modules' folder, so Hotload loads them, and starts the background checks.</summary>
	public void Start( ModuleContext context )
	{
		_context = context;
		Directory.CreateDirectory( DefaultRoot );
		_installed.Value = CreateHub( context.App ).ReadInstalled();
		context.App.ModuleFolders.Set( Id, [DefaultRoot] );
		context.App.Settings.Changed += UpdateSource;
		_timer = new( _ => context.Post( () =>
		{
			if ( context.App.Settings.Get( Automatic ) && _installed.Value.Count > 0 )
				Refresh( isUpdating: true, isManual: false );
		} ), null, _firstCheck, _interval );
	}

	/// <summary>Stops the background checks and withdraws the folder.</summary>
	public void Stop()
	{
		_timer?.Dispose();
		_timer = null;
		if ( _context is { } context )
		{
			context.App.Settings.Changed -= UpdateSource;
			context.App.ModuleFolders.Set( Id, [] );
		}
	}

	/// <summary>Reads the hub's index in the background; with <paramref name="isUpdating"/>, also installs newer versions of installed modules.</summary>
	/// <param name="isUpdating">Whether to update installed modules.</param>
	/// <param name="isManual">Whether the user asked, so the outcome is reported even when nothing changed.</param>
	internal void Refresh( bool isUpdating = false, bool isManual = false )
	{
		if ( _context is not { Lifetime.IsCancellationRequested: false } context || _isLoading.Value )
			return;

		_isLoading.Value = true;
		_error = null;
		_revision.Value++;
		var hub = CreateHub( context.App );
		_ = Task.Run( async () =>
		{
			try
			{
				var index = await hub.ReadIndexAsync( context.Lifetime ).ConfigureAwait( false );
				var updates = isUpdating ? CommunityHub.FindUpdates( index, hub.ReadInstalled() ) : [];
				foreach ( var module in updates )
					await hub.InstallAsync( index, module, context.Lifetime ).ConfigureAwait( false );

				context.Post( () =>
				{
					_index = index;
					_revision.Value++;
					_installed.Value = hub.ReadInstalled();
					if ( updates.Count > 0 )
						Post( context.App, $"Updated community modules: {string.Join( ", ", updates.Select( module => $"{module.Name} {module.Version}" ) )}." );
					else if ( isManual )
						Post( context.App, "Your community modules are up to date." );
				} );
			}
			catch ( OperationCanceledException ) when ( context.Lifetime.IsCancellationRequested )
			{
			}
			catch ( Exception error )
			{
				context.Post( () =>
				{
					_error = error.Message;
					_revision.Value++;
					if ( isManual )
						context.App.ReportError( "Community modules", error );
				} );
			}
			finally
			{
				context.Post( () => _isLoading.Value = false );
			}
		} );
	}

	/// <summary>Installs or updates a module from the last index read; Hotload loads it once its files are written.</summary>
	internal void Install( HubModule module )
	{
		if ( _index is not { } index )
			return;

		Change( module.Id, hub => hub.InstallAsync( index, module, _context!.Lifetime ) );
	}

	/// <summary>Removes an installed module, which unloads it and closes its panels.</summary>
	internal void Remove( string id ) => Change( id, hub => Task.Run( () => hub.Remove( id ) ) );

	/// <summary>Runs one install, update or removal in the background, marking the module busy until it ends.</summary>
	private void Change( string id, Func<CommunityHub, Task> change )
	{
		if ( _context is not { Lifetime.IsCancellationRequested: false } context || IsBusy( id ) )
			return;

		_busy.Value = new HashSet<string>( _busy.Value ) { id };
		var hub = CreateHub( context.App );
		_ = Task.Run( async () =>
		{
			Exception? failure = null;
			try
			{
				await change( hub ).ConfigureAwait( false );
			}
			catch ( Exception error ) when ( error is not OperationCanceledException )
			{
				failure = error;
			}

			context.Post( () =>
			{
				var busy = new HashSet<string>( _busy.Value );
				busy.Remove( id );
				_busy.Value = busy;
				_installed.Value = hub.ReadInstalled();
				if ( failure is not null )
					context.App.ReportError( "Community modules", failure );
			} );
		} );
	}

	/// <summary>Forgets the list read from a hub the source setting no longer names.</summary>
	private void UpdateSource()
	{
		if ( !string.Equals( SourceLabel, _lastSource, StringComparison.Ordinal ) )
		{
			_index = null;
			_error = null;
			_revision.Value++;
		}

		_lastSource = SourceLabel;
	}

	/// <summary>Creates a hub client for the source setting's value.</summary>
	private static CommunityHub CreateHub( LoknApplication app )
	{
		var source = app.Settings.Get( Source ).Trim();
		return new( source.StartsWith( "http", StringComparison.OrdinalIgnoreCase ) ? source : Path.GetFullPath( Environment.ExpandEnvironmentVariables( source ) ), DefaultRoot );
	}

	/// <summary>Posts one notification under Community modules.</summary>
	private static void Post( LoknApplication app, string message )
	{
		app.Notifications.Post( new( "Community modules", message ) { Source = "community" } );
	}
}
