using Goo;

namespace Lokn.Core;

/// <summary>Owns module registration, global commands, and the panel workspace on the UI thread.</summary>
public sealed class LoknApplication
{
	/// <summary>Stores loaded modules in registration order.</summary>
	private readonly Dictionary<string, LoadedModule> _modules = new( StringComparer.Ordinal );

	/// <summary>Retains explicitly composed modules so disabled features can be enabled again.</summary>
	private readonly Dictionary<string, ILoknModule> _knownModules = new( StringComparer.Ordinal );

	/// <summary>Tracks module changes read by retained module manager views.</summary>
	private readonly State<int> _moduleVersion = new( 0 );

	/// <summary>Stores the shell's UI-thread dispatcher while it is mounted.</summary>
	private Func<Action, bool>? _post;

	/// <summary>Stores the optional workspace file used at clean shutdown.</summary>
	private readonly string? _workspacePath;

	/// <summary>Tracks whether shutdown already captured the live workspace.</summary>
	private bool _isShutdown;

	/// <summary>Gets the global command registry.</summary>
	public CommandRegistry Commands
	{
		get;
	}

	/// <summary>Gets the panel workspace.</summary>
	public PanelWorkspace Panels
	{
		get;
	}

	/// <summary>Gets the panels open as popup windows over the shell, such as Settings.</summary>
	public PopupWindows Popups
	{
		get;
	}

	/// <summary>Gets the tools modules offer to agents.</summary>
	public AgentToolRegistry Tools
	{
		get;
	}

	/// <summary>Gets the folders of source modules that loaded modules contribute, such as trusted projects' modules.</summary>
	public ModuleFolderRegistry ModuleFolders { get; } = new();

	/// <summary>Gets the live global defaults and profile overrides.</summary>
	public SettingsService Settings
	{
		get;
	}

	/// <summary>Gets the notification feed shown under the titlebar bell, shared by every profile.</summary>
	public NotificationFeed Notifications
	{
		get;
	}

	/// <summary>Gets This PC and every paired host, and the host the user is on now.</summary>
	public LoknHosts Hosts
	{
		get;
	}

	/// <summary>Gets the browser tabs of the current host, <see cref="LoknHosts.Current"/>: page state without web views, which the authority saves and
	/// clients mirror (docs/remote.md, Browser frames).</summary>
	/// <remarks>A panel bound to a host uses <c>context.Host.BrowserTabs</c> instead, so it keeps its host's tabs when the user switches hosts.</remarks>
	public BrowserTabs BrowserTabs => Hosts.Current.BrowserTabs;

	/// <summary>Gets the latest contained module or command error.</summary>
	public string? LastError
	{
		get; private set;
	}

	/// <summary>Gets the live slot contributions, for the shell's areas and slots modules render themselves.</summary>
	public SlotRegistry Slots { get; } = new();

	/// <summary>Gets the extension points modules declare and the values contributed to them.</summary>
	public ExtensionRegistry Extensions { get; } = new();

	/// <summary>Gets the services loaded modules publish.</summary>
	public ServiceRegistry Services { get; } = new();

	/// <summary>Gets the search providers behind the Search palette and the items the user ran from it.</summary>
	public SearchRegistry Search
	{
		get;
	}

	/// <summary>Gets where the current host's disk, processes and terminals are reached: the workspace of <see cref="LoknHosts.Current"/>, This PC's by
	/// default.</summary>
	/// <remarks>For work that follows the host the user is on, such as a source module's command. A panel bound to a host uses
	/// <see cref="PanelContext.Workspace"/> instead, so it keeps working on its own host when the user switches hosts.</remarks>
	public IWorkspaceHost Workspace => Hosts.Current.Workspace;

	/// <summary>Raised on the UI thread after <see cref="Workspace"/> changed: the current host's workspace was replaced, or another host became current.</summary>
	public event Action? WorkspaceChanged;

	/// <summary>Raises <see cref="WorkspaceChanged"/>.</summary>
	internal void RaiseWorkspaceChanged() => WorkspaceChanged?.Invoke();

	/// <summary>Gets what the daemon shows in the notification area, published by the Remote module while this process is the daemon; else null.</summary>
	public HostStatus? HostStatus
	{
		get; private set;
	}

	/// <summary>Raised on the UI thread after <see cref="HostStatus"/> changed.</summary>
	public event Action? HostStatusChanged;

	/// <summary>Publishes what the daemon shows in the notification area, or null when nothing publishes it any more; on the UI thread.
	/// Publishing a status equal to the current one raises nothing.</summary>
	/// <param name="status">The new status, or null.</param>
	public void PublishHostStatus( HostStatus? status )
	{
		if ( status == HostStatus )
			return;

		HostStatus = status;
		HostStatusChanged?.Invoke();
	}

	/// <summary>Opens a notification the user clicked, such as in the feed or the Windows notification center: marks it read, then asks the
	/// <see cref="NotificationOpener"/> contributions in load order, then activates its panel, else follows its link.</summary>
	public void OpenNotification( LoknNotification item, Guid? sourcePanelId = null )
	{
		ArgumentNullException.ThrowIfNull( item );
		Notifications.MarkRead( item.Id );
		foreach ( var opener in Extensions.Get<NotificationOpener>( NotificationOpener.Point ) )
		{
			try
			{
				if ( opener.Open( this, item ) )
					return;
			}
			catch ( Exception error ) { ReportError( "Open notification", error ); }
		}

		if ( item.PanelId is { } panelId && Panels.Activate( Panels.ResolvePanelId( panelId ) ) )
			return;
		if ( item.Url is { } url && Uri.TryCreate( url, UriKind.Absolute, out var uri ) )
			OpenLink( uri, sourcePanelId );
	}

	/// <summary>Gets the currently loaded module metadata.</summary>
	public IReadOnlyList<ModuleInfo> Modules
	{
		get
		{
			_ = _moduleVersion.Value;
			return _modules.Values.Select( module => module.Info ).ToArray();
		}
	}

	/// <summary>Gets the known module instances available for enabling or disabling.</summary>
	public IReadOnlyList<ILoknModule> AvailableModules
	{
		get
		{
			_ = _moduleVersion.Value;
			return _knownModules.Values.ToArray();
		}
	}

	/// <summary>Gets the mounted shell window, for native file dialogs, the clipboard and its focus; null when no shell is mounted.</summary>
	public Window? Window
	{
		get; private set;
	}

	/// <summary>Gets the Windows app ID the application runs under, which its Windows notifications use; empty in tests and headless apps.</summary>
	public string ApplicationId { get; internal set; } = "";

	/// <summary>Gets whether this process is the headless daemon, <c>Loom.exe --host</c>, which runs chats for its clients and shows no window.</summary>
	public bool IsHost { get; init; }

	/// <summary>Gets whether an overlay requires native child surfaces, such as browser pages, to stay hidden.</summary>
	public bool NativeSurfacesOccluded => _nativeCovers.Count > 0;

	/// <summary>Reports changes to <see cref="NativeSurfacesOccluded"/>.</summary>
	public event Action? NativeSurfaceOcclusionChanged;

	/// <summary>Stores the owners of overlays that cover native surfaces.</summary>
	private readonly HashSet<object> _nativeCovers = [];

	/// <summary>Hides native child surfaces while an overlay a module draws, such as a popover, must show over them.</summary>
	/// <param name="owner">Identifies the overlay; each owner covers or uncovers independently.</param>
	/// <param name="isCovered">True while the overlay shows.</param>
	public void CoverNativeSurfaces( object owner, bool isCovered )
	{
		ArgumentNullException.ThrowIfNull( owner );
		var was = NativeSurfacesOccluded;
		if ( isCovered )
			_nativeCovers.Add( owner );
		else
			_nativeCovers.Remove( owner );
		if ( was != NativeSurfacesOccluded )
			NativeSurfaceOcclusionChanged?.Invoke();
	}

	/// <summary>Reports changes that require the shell to refresh.</summary>
	internal event Action? Changed;

	/// <summary>Creates an empty application that can run without optional feature modules.</summary>
	/// <param name="settingsPath">The settings file, or null to keep settings in memory.</param>
	/// <param name="workspacePath">The workspace file saved at shutdown, or null.</param>
	/// <param name="notificationsPath">The notification feed file, or null to keep the feed in memory.</param>
	/// <param name="browserTabsPath">The browser tabs file, or null to keep the session's tabs in memory.</param>
	public LoknApplication( string? settingsPath = null, string? workspacePath = null, string? notificationsPath = null, string? browserTabsPath = null )
	{
		_workspacePath = workspacePath;
		Commands = new( this );
		Tools = new( this );
		Search = new( this );
		Panels = new( this );
		Popups = new( this );
		Settings = new( settingsPath );
		Settings.Changed += MarkChanged;
		Settings.Failed += MarkSettingsFailed;
		if ( Settings.LastError is { } error )
			MarkSettingsFailed( error );

		Notifications = new( notificationsPath );
		Notifications.Failed += MarkNotificationsFailed;
		if ( Notifications.LoadError is { } notificationsError )
			MarkNotificationsFailed( notificationsError );

		var browserTabs = new BrowserTabs( browserTabsPath );
		browserTabs.Failed += MarkBrowserTabsFailed;
		if ( browserTabs.LoadError is { } tabsError )
			MarkBrowserTabsFailed( tabsError );
		Hosts = new( this, browserTabs );
	}

	/// <summary>Atomically registers and starts a module after checking required modules.</summary>
	public bool Load( ILoknModule module )
	{
		ArgumentNullException.ThrowIfNull( module );
		LoadedModule? loaded = null;

		try
		{
			ArgumentException.ThrowIfNullOrWhiteSpace( module.Id );
			if ( _modules.ContainsKey( module.Id ) )
				throw new InvalidOperationException( $"Module already loaded: {module.Id}" );

			var dependencies = module.Dependencies.Distinct( StringComparer.Ordinal ).ToArray();
			foreach ( var dependency in dependencies )
			{
				if ( !_modules.ContainsKey( dependency ) )
					throw new InvalidOperationException( $"Required module is not loaded: {dependency}" );
			}

			var builder = new ModuleBuilder( module.Id );
			try
			{
				module.Register( builder );
			}
			finally { builder.Seal(); }

			Commands.EnsureAvailable( builder.Commands );
			Tools.EnsureAvailable( builder.Tools, builder.ToolCards );
			Panels.EnsureAvailable( builder.Panels );
			Settings.EnsureAvailable( builder.Settings );
			Extensions.EnsureAvailable( builder.Points, builder.Contributions );
			Services.EnsureAvailable( builder.Services );
			Search.EnsureAvailable( builder.SearchProviders );
			loaded = new( module, new( module.Id, module.Title, Array.AsReadOnly( dependencies ), module.CanUnload ), builder.Links, builder.Routes );
			_modules.Add( module.Id, loaded );
			_knownModules[module.Id] = module;
			Commands.Register( builder.Commands, loaded.Lifetime.Token );
			Tools.Register( builder.Tools, builder.ToolCards, loaded.Lifetime.Token );
			Panels.Register( builder.Panels, loaded.Lifetime.Token );
			Settings.Register( module.Id, builder.Settings );
			Extensions.Register( module.Id, builder.Points, builder.Contributions );
			Services.Register( module.Id, builder.Services );
			Search.Register( builder.SearchProviders );
			module.Start( new( this, loaded.Lifetime.Token ) );
			// Slot content is created after startup, so it can use services the module starts; a failure rolls the module back.
			foreach ( var (slot, order, create) in builder.Slots )
				Slots.Add( new( Guid.NewGuid(), slot, module.Id, order, create( new PanelContext( this, Guid.NewGuid(), loaded.Lifetime.Token, input: null ) ) ) );
			_moduleVersion.Value++;
			MarkChanged();
			return true;
		}
		catch ( Exception error )
		{
			if ( loaded is not null )
				ReleaseModule( loaded );

			ReportError( "Load module", error );
			return false;
		}
	}

	/// <summary>Turns on a known module that is off, using a fresh registration lifetime, and reports it through <see cref="ModuleToggled"/>.</summary>
	public bool Enable( string id )
	{
		if ( !_knownModules.TryGetValue( id, out var module ) || _modules.ContainsKey( id ) || !Load( module ) )
			return false;

		ModuleToggled?.Invoke( id, true );
		return true;
	}

	/// <summary>Reports that the user turned a module on (true) or off (false), so the host can remember it across restarts.</summary>
	public event Action<string, bool>? ModuleToggled;

	/// <summary>Makes a module known without starting it, such as an installed module the user turned off; <see cref="Enable"/> starts it.</summary>
	public void AddAvailable( ILoknModule module )
	{
		ArgumentNullException.ThrowIfNull( module );
		if ( _modules.ContainsKey( module.Id ) )
			throw new InvalidOperationException( $"Module already loaded: {module.Id}" );

		UpdateKnown( module );
	}

	/// <summary>Turns off a module after refusing removal of a required dependency, and reports it through <see cref="ModuleToggled"/>.</summary>
	public bool Unload( string id )
	{
		if ( !_modules.TryGetValue( id, out var module ) )
			return false;

		if ( !module.Info.CanUnload )
			return false;

		var dependent = Modules.FirstOrDefault( candidate => candidate.Dependencies.Contains( id ) );
		if ( dependent is not null )
		{
			ReportError( "Unload module", new InvalidOperationException( $"{dependent.Title} requires {module.Info.Title}." ) );
			return false;
		}

		ReleaseModule( module );
		MarkChanged();
		ModuleToggled?.Invoke( id, false );
		return true;
	}

	/// <summary>Replaces a module's implementation, restarting it in place with its open panels when it is loaded.</summary>
	public bool Reload( ILoknModule module ) => Reload( [module] );

	/// <summary>Replaces several modules together, in load order, so a module and the modules that depend on it restart as one step.</summary>
	public bool Reload( IReadOnlyList<ILoknModule> modules )
	{
		ArgumentNullException.ThrowIfNull( modules );
		var ids = modules
			.Select( module => module.Id )
			.ToHashSet( StringComparer.Ordinal );
		var current = _modules.Values
			.Where( loaded => ids.Contains( loaded.Info.Id ) )
			.ToArray();

		// A loaded module outside the group would keep using the implementation being replaced.
		var dependent = _modules.Values.FirstOrDefault( loaded => !ids.Contains( loaded.Info.Id ) && loaded.Info.Dependencies.Any( ids.Contains ) );
		if ( dependent is not null )
		{
			ReportError( "Reload module", new InvalidOperationException( $"{dependent.Info.Title} depends on a module being reloaded and must be reloaded with it." ) );
			return false;
		}

		// A known but disabled module stays disabled; Enable starts its replacement.
		var disabled = ids
			.Where( id => _knownModules.ContainsKey( id ) && !_modules.ContainsKey( id ) )
			.ToHashSet( StringComparer.Ordinal );

		var panels = current
			.SelectMany( loaded => Panels.SuspendModule( loaded.Info.Id ) )
			.ToArray();
		foreach ( var loaded in current.Reverse() )
			ReleaseModule( loaded );

		var isReloaded = modules.All( module => disabled.Contains( module.Id ) ? UpdateKnown( module ) : Load( module ) );
		if ( !isReloaded )
		{
			// A replacement that fails registration or startup falls back to the implementations that were running.
			foreach ( var loaded in _modules.Values.Where( loaded => ids.Contains( loaded.Info.Id ) ).Reverse().ToArray() )
				ReleaseModule( loaded );

			foreach ( var loaded in current )
				Load( loaded.Module );
		}

		Panels.ResumePanels( panels );
		return isReloaded;
	}

	/// <summary>Gets the loaded module of a type, for a feature that declared it as a dependency.</summary>
	public T GetModule<T>() where T : class, ILoknModule
	{
		return _modules.Values
			.Select( loaded => loaded.Module )
			.OfType<T>()
			.FirstOrDefault() ?? throw new InvalidOperationException( $"Module is not loaded: {typeof( T ).Name}" );
	}

	/// <summary>Records a disabled module's replacement so that Enable starts the new implementation.</summary>
	private bool UpdateKnown( ILoknModule module )
	{
		_knownModules[module.Id] = module;
		_moduleVersion.Value++;
		MarkChanged();
		return true;
	}

	/// <summary>Unloads a module and forgets it so it can no longer be enabled.</summary>
	public bool Remove( string id )
	{
		if ( _modules.ContainsKey( id ) && !Unload( id ) )
			return false;

		if ( !_knownModules.Remove( id ) )
			return false;

		_moduleVersion.Value++;
		MarkChanged();
		return true;
	}

	/// <summary>Reports, once, that the application finished starting: its modules loaded and its workspace restored.</summary>
	/// <remarks>A module chooses its own startup view here, such as Home; modules loaded later never see it. Use <see cref="ModuleContext.WhenStarted"/>.</remarks>
	public event Action? Started;

	/// <summary>Gets whether <see cref="Started"/> was raised.</summary>
	public bool IsStarted { get; private set; }

	/// <summary>Raises <see cref="Started"/> once the host restored the workspace.</summary>
	internal void MarkStarted()
	{
		if ( IsStarted )
			return;

		IsStarted = true;
		Started?.Invoke();
		Started = null;
	}

	/// <summary>Restores the configured workspace after its feature modules are loaded.</summary>
	public bool RestoreWorkspace() => _workspacePath is not null && Panels.RestoreLayout( _workspacePath );

	/// <summary>Stops modules in reverse registration order and detaches UI dispatch.</summary>
	public void Shutdown()
	{
		if ( _isShutdown )
			return;

		_isShutdown = true;
		if ( _workspacePath is not null )
			Panels.SaveLayout( _workspacePath );

		Popups.CloseAll();
		Commands.BindDispatcher( null );
		Tools.BindDispatcher( null );
		foreach ( var module in _modules.Values.Reverse().ToArray() )
			ReleaseModule( module );
		foreach ( var host in Hosts.All )
			host.BrowserTabs.Stop();

		_post = null;
		BindWindow( null );
	}

	/// <summary>Clears a previously reported error.</summary>
	public void ClearError()
	{
		LastError = null;
		MarkChanged();
	}

	/// <summary>Binds or detaches the shell-owned UI dispatcher.</summary>
	internal void BindDispatcher( Func<Action, bool>? post ) => _post = post;

	/// <summary>Binds or detaches the window that hosts the shell.</summary>
	internal void BindWindow( Window? window )
	{
		lock ( WindowApps )
		{
			if ( Window is { } previous )
				WindowApps.Remove( previous );
			if ( window is not null )
				WindowApps[window] = this;
		}

		Window = window;
	}

	/// <summary>Maps each shell window to the application it hosts.</summary>
	private static readonly Dictionary<Window, LoknApplication> WindowApps = [];

	/// <summary>Finds the application a window hosts, or null for a window no shell is bound to.</summary>
	internal static LoknApplication? Find( Window window )
	{
		lock ( WindowApps )
			return WindowApps.GetValueOrDefault( window );
	}

	/// <summary>Posts work only while the shell remains mounted.</summary>
	internal bool Post( Action action ) => _post?.Invoke( action ) ?? false;

	/// <summary>Notifies the shell of a completed model change.</summary>
	internal void MarkChanged() => Changed?.Invoke();

	/// <summary>Contains a feature error and shows its message in the shell's notice.</summary>
	public void ReportError( string source, Exception error )
	{
		LastError = $"{source}: {error.Message}";
		MarkChanged();
	}

	/// <summary>Exposes a settings failure through the existing shell notice.</summary>
	private void MarkSettingsFailed( string error )
	{
		LastError = $"Settings: {error}";
		MarkChanged();
	}

	/// <summary>Exposes a notification feed failure through the existing shell notice.</summary>
	private void MarkNotificationsFailed( string error )
	{
		LastError = $"Notifications: {error}";
		MarkChanged();
	}

	/// <summary>Exposes a browser tabs load or save failure through the existing shell notice.</summary>
	private void MarkBrowserTabsFailed( string error )
	{
		LastError = $"Browser tabs: {error}";
		MarkChanged();
	}

	/// <summary>Cancels module work and removes all of its owned contributions.</summary>
	private void ReleaseModule( LoadedModule module )
	{
		_modules.Remove( module.Info.Id );
		Commands.RemoveModule( module.Info.Id );
		Tools.RemoveModule( module.Info.Id );
		try
		{
			module.Lifetime.Cancel();
		}
		catch ( Exception error ) { ReportError( module.Info.Title, error ); }

		Panels.RemoveModule( module.Info.Id );
		foreach ( var item in Slots.RemoveModule( module.Info.Id ) )
		{
			try
			{
				item.Content.Closed();
			}
			catch ( Exception error ) { ReportError( module.Info.Title, error ); }
		}
		Extensions.RemoveModule( module.Info.Id );
		Services.RemoveModule( module.Info.Id );
		Search.RemoveModule( module.Info.Id );
		try
		{
			module.Module.Stop();
		}
		catch ( Exception error ) { ReportError( module.Info.Title, error ); }

		Settings.RemoveModule( module.Info.Id );

		// The source owns cancellation registrations, so release them at module unload.
		module.Lifetime.Dispose();
		_moduleVersion.Value++;
	}

	/// <summary>Stores one module and its explicitly owned cancellation lifetime.</summary>
	private sealed record LoadedModule( ILoknModule Module, ModuleInfo Info, Func<LinkRequest, bool>? Links = null, IReadOnlyDictionary<string, Action<RouteRequest>>? Routes = null )
	{
		/// <summary>Gets the cancellation source owned until module unload.</summary>
		internal CancellationTokenSource Lifetime { get; } = new();
	}

	/// <summary>Opens a link the user followed: a <c>lokn://</c> link by its module's route, any other by the first module handler that takes it, otherwise the system's default browser.</summary>
	/// <param name="uri">The link; only http and https links reach the default browser.</param>
	/// <param name="sourcePanelId">The panel the link was followed in, so a handler can open beside it, or null.</param>
	public void OpenLink( Uri uri, Guid? sourcePanelId = null )
	{
		ArgumentNullException.ThrowIfNull( uri );
		var request = new LinkRequest( this, uri, sourcePanelId );
		if ( LoknLinks.Parse( uri ) is ( var moduleId, var route, var path ) )
		{
			if ( _modules.TryGetValue( moduleId, out var owner ) && owner.Routes?.GetValueOrDefault( route ) is { } open && !owner.Lifetime.IsCancellationRequested )
			{
				try
				{
					open( new RouteRequest( this, uri, path, sourcePanelId ) );
				}
				catch ( Exception error ) { ReportError( owner.Info.Title, error ); }
			}
			else
				ReportError( "Open link", new InvalidOperationException( $"No loaded module opens {uri}." ) );
			return;
		}

		foreach ( var module in _modules.Values.Where( module => module.Links is not null && !module.Lifetime.IsCancellationRequested ).ToArray() )
		{
			try
			{
				if ( module.Links!( request ) )
					return;
			}
			catch ( Exception error ) { ReportError( module.Info.Title, error ); }
		}

		if ( uri.IsAbsoluteUri && uri.Scheme is "http" or "https" )
		{
			try
			{
				System.Diagnostics.Process.Start( new System.Diagnostics.ProcessStartInfo( uri.AbsoluteUri ) { UseShellExecute = true } );
			}
			catch ( Exception error ) when ( error is System.ComponentModel.Win32Exception or InvalidOperationException ) { ReportError( "Open link", error ); }
		}
	}
}
