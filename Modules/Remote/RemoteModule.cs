using System.Runtime.Versioning;
using Goo;
using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Omp;

namespace Lokn.Modules.Remote;

/// <summary>Runs This PC's chats in a daemon when asked, links this Loom with every paired host at once, and owns the protocol, the secure channel, the
/// transports, devices, pairing and its confirmation (docs/remote.md).</summary>
public sealed class RemoteModule : ILoknModule
{
	/// <summary>Names the role that runs this Loom's chats locally, as before.</summary>
	public const string LocalRole = "local";

	/// <summary>Names the role that shows the chats of this PC's daemon over the named pipe.</summary>
	public const string DaemonRole = "daemon";

	/// <summary>Names the role of <c>Loom.exe --host</c>, which runs the chats for its clients.</summary>
	public const string HostRole = "host";

	/// <summary>Names the former role that showed one paired host's chats instead of this PC's; read once and rewritten to <see cref="LocalRole"/>,
	/// since paired hosts now show beside This PC.</summary>
	private const string LegacyRemoteRole = "remote";

	/// <summary>Names the process data slot <see cref="LatchRole"/> keeps the running role in, so a reloaded module keeps the role this process runs in.</summary>
	private const string RoleSlot = "Lokn.Modules.Remote.Role";

	/// <summary>Lists the roles <see cref="Role"/> accepts, the legacy one included so an old settings file still loads.</summary>
	private static readonly string[] Roles = [LocalRole, DaemonRole, HostRole, LegacyRemoteRole];

	/// <summary>Lists the network modes <see cref="Network"/> accepts.</summary>
	private static readonly string[] Networks = ["off", "lan", "tailscale", "relay", "funnel"];

	/// <summary>Defines who runs This PC's chats; it is read from the Global scope only, and a change applies while Loom runs (<see cref="SwitchRole"/>).</summary>
	public static readonly Setting<string> Role = new(
		"remote.role", "Chats on this PC run in", "This Loom or the daemon on this PC. Chats move once no agent works on this PC. Paired hosts are added in Remote access.",
		LocalRole, "Remote", value => Roles.Contains( value ) )
	{
		Choices = [new( LocalRole, "This Loom" ), new( DaemonRole, "Daemon" )],
		IsHidden = true,
	};

	/// <summary>Defines the host's WebSocket port.</summary>
	public static readonly Setting<int> Port = new(
		"remote.port", "Port", "The port devices connect to.",
		47390, "Remote", value => value is > 0 and < 65536 )
	{
		Minimum = 1,
		Maximum = 65535,
		IsHidden = true,
	};

	/// <summary>Defines how devices reach the host, or off for the named pipe only; every mode but off is a preview.</summary>
	public static readonly Setting<string> Network = new(
		"remote.network", "Network access", "How other devices reach this PC's chats.",
		"off", "Remote", value => Networks.Contains( value ) )
	{
		Choices = [new( "off", "Off" ), new( "lan", "This network" ), new( "tailscale", "Tailscale: my devices" ), new( "relay", "Through a relay" ), new( "funnel", "Anywhere: a link" )],
		IsHidden = true,
	};

	/// <summary>Defines the relay's address for the relay mode.</summary>
	public static readonly Setting<string> RelayUrl = new(
		"remote.relay-url", "Relay", "The relay's https address, for the Relay mode.",
		"", "Remote" )
	{
		IsHidden = true,
	};

	/// <summary>Defines whether the daemon starts when the user signs in.</summary>
	public static readonly Setting<bool> LoginItem = new(
		"remote.login-item", "Start the daemon at sign-in", "Keeps chats reachable after sign-in without opening Loom, while chats run in the daemon.",
		false, "Remote" )
	{
		IsHidden = true,
	};

	/// <summary>Defines whether the daemon shows its icon in the notification area while it runs.</summary>
	public static readonly Setting<bool> Tray = new(
		"remote.tray", "Show the daemon in the notification area", "An icon near the clock while the daemon runs: open Loom, see its agents, or stop it.",
		true, "Remote" )
	{
		IsHidden = true,
	};

	/// <summary>Defines whether this PC lets the agents of the hosts it paired with use its browser tabs through their browser tools.</summary>
	public static readonly Setting<bool> LendBrowser = new(
		"remote.lend-browser", "Let agents on paired hosts use this PC's browser", "While Loom here is linked with a paired host, the host's agents may open, read and use pages in this PC's Browser panels.",
		true, "Remote" )
	{
		IsHidden = true,
	};

	/// <summary>Defines the Settings entry that shows Remote access, the Remote tab's only row: where devices connect from, pairing, devices and the
	/// PCs this one connects to; its value is never set, only its editor is used. The plain remote settings are hidden, since it shows them.</summary>
	internal static readonly Setting<string> Access = new(
		"remote.access", "Remote access", "", "", "Remote" )
	{
		Editor = ( app, scope ) => Cell.Mount<RemoteModule, AccessContent>( "remote-access", app.GetModule<RemoteModule>() ),
	};

	/// <summary>Defines the route of the pairing notification's answers, <c>lokn://remote/pairing/allow/&lt;deviceId&gt;</c> and <c>…/view/…</c>.</summary>
	internal const string PairingRoute = "pairing";

	/// <summary>Defines the route that dials a host again at once, <c>lokn://remote/reconnect/&lt;hostId&gt;</c>, which the sidebar's offline banner follows.</summary>
	public const string ReconnectRoute = "reconnect";

	/// <summary>Defines the route that chooses where This PC's chats run, <c>lokn://remote/role/daemon</c> or <c>lokn://remote/role/local</c>, so other modules
	/// such as Home turn the daemon on without referencing this one; <c>lokn://remote/role</c> opens Remote access.</summary>
	public const string RoleRoute = "role";

	/// <summary>Defines how long a move of This PC's chats to this Loom waits for a running daemon to stop.</summary>
	private static readonly TimeSpan DaemonStopTimeout = TimeSpan.FromSeconds( 10 );

	/// <summary>Stores the checks' replacements for the data folder, pipe, role, dialer and pairing, or null for the real ones.</summary>
	private readonly RemoteHooks? _hooks;

	/// <summary>Holds the link of every paired host, in the order they were added.</summary>
	private readonly List<RemoteClient> _links = [];

	/// <summary>Holds what Remote access does once it shows, or empty (<see cref="OpenAccess"/>).</summary>
	private readonly State<string> _accessRequest = new( "" );

	/// <summary>Counts changes of <see cref="_links"/>, so views reading <see cref="Links"/> rebuild.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Stores the running module context, or null while stopped.</summary>
	private ModuleContext? _context;

	/// <summary>Stores the sign-in entry last written, or null before the first.</summary>
	private bool? _loginItem;

	/// <summary>Stores the data folder's single-writer lock while this process writes the chats, in the <c>local</c> and host roles.</summary>
	private DataLock? _dataLock;

	/// <summary>Stores what publishes the daemon's status for its notification-area icon, in the host role.</summary>
	private HostStatusFeed? _statusFeed;

	/// <summary>Counts changes of the running role, the daemon link, the switch under way and the notice, so views reading them rebuild.</summary>
	private readonly State<int> _roleVersion = new( 0 );

	/// <summary>Stores the move of This PC's chats under way, or null.</summary>
	private RoleSwitch? _switch;

	/// <summary>Stores the store whose working agents a waiting switch follows, or null while none waits.</summary>
	private ConversationStore? _waitingStore;

	/// <summary>Counts the daemon's failures in the <c>daemon</c> role.</summary>
	private readonly DaemonFailures _failures = new();

	/// <summary>Stores whether this module itself writes the <see cref="Role"/> setting, so following the setting leaves that change to its writer.</summary>
	private bool _isChoosing;

	/// <summary>Creates the module as Loom loads it.</summary>
	public RemoteModule()
	{
	}

	/// <summary>Creates the module with checks' replacements for its data folder, pipe, role, dialer and pairing.</summary>
	internal RemoteModule( RemoteHooks hooks ) => _hooks = hooks;

	/// <summary>Gets the module identifier.</summary>
	public string Id => "remote";

	/// <summary>Gets the display name.</summary>
	public string Title => "Remote";

	/// <summary>Gets the modules this one serves or mirrors.</summary>
	public IReadOnlyList<string> Dependencies => ["conversations"];

	/// <summary>Gets the running module context; it throws before startup.</summary>
	internal ModuleContext Context => _context ?? throw new InvalidOperationException( "The Remote module has not started." );

	/// <summary>Gets the settings this module runs with, read from the Global scope.</summary>
	internal RemoteSettings Settings => RemoteSettings.Read( Context.App.Settings );

	/// <summary>Gets the data folder keys, devices, uploads and the paired hosts live in.</summary>
	internal string DataFolder => _hooks?.DataFolder ?? LoknPaths.DataRoot;

	/// <summary>Gets the role this process runs in: <see cref="HostRole"/> in the daemon, else <see cref="LocalRole"/> or <see cref="DaemonRole"/>, which
	/// follows the <see cref="Role"/> setting once a switch moved the chats (<see cref="Switching"/>); <see cref="DaemonRole"/> when a daemon that could
	/// not be stopped holds them. Reading it rebuilds the reader when it changes.</summary>
	/// <remarks>A reload of this module keeps the role the process runs in, and then applies a choice made meanwhile.</remarks>
	public string RunningRole
	{
		get
		{
			_ = _roleVersion.Value;
			return field;
		}
		private set;
	} = LocalRole;

	/// <summary>Gets the host gateway while this process is the daemon, or null.</summary>
	public HostGateway? Gateway
	{
		get; private set;
	}

	/// <summary>Gets the link with this PC's daemon, which shows This PC's chats in the <c>daemon</c> role, or null; reading it rebuilds the reader when it changes.</summary>
	public RemoteClient? Daemon
	{
		get
		{
			_ = _roleVersion.Value;
			return field;
		}
		private set;
	}

	/// <summary>Gets the link of every paired host, in the order they were added; reading it rebuilds the reader when one is added or removed.</summary>
	public IReadOnlyList<RemoteClient> Links
	{
		get
		{
			_ = _version.Value;
			return [.. _links];
		}
	}

	/// <summary>Gets the pairings of other devices waiting for the user's answer, while This PC's chats run in the daemon, or null.</summary>
	internal PairingPrompts? Prompts
	{
		get; private set;
	}

	/// <summary>Gets the move of This PC's chats under way, or null; reading it rebuilds the reader when it starts, stops waiting or ends.</summary>
	internal RoleSwitch? Switching
	{
		get
		{
			_ = _roleVersion.Value;
			return _switch;
		}
	}

	/// <summary>Gets the note about the last move of This PC's chats Loom made by itself, or one that failed, until it is dismissed or the next switch
	/// starts; null when there is none. Reading it rebuilds the reader when it changes.</summary>
	internal string? Notice
	{
		get
		{
			_ = _roleVersion.Value;
			return field;
		}
		private set;
	}

	/// <summary>Gets whether this process holds the data folder's single-writer lock, and so writes This PC's chats.</summary>
	internal bool IsWriter => _dataLock is not null;

	/// <summary>Gets whether this module starts and stops this PC's real daemon: on Windows, unless a check supplies no <see cref="RemoteHooks.DaemonExecutable"/>.</summary>
	[SupportedOSPlatformGuard( "windows" )]
	private bool UsesDaemonControl => OperatingSystem.IsWindows() && (_hooks is null || _hooks.DaemonExecutable is not null);

	/// <summary>Registers the remote settings, the Remote access panel, the window stream panel, the status bar's connection dot, the pairing answers',
	/// reconnect and role routes, the handler of <c>loom://pair#…</c> links, and the daemon's sign-in switch for its notification-area menu.</summary>
	public void Register( ModuleBuilder builder )
	{
		builder
			.Setting( Role )
			.Setting( Port )
			.Setting( Network )
			.Setting( RelayUrl )
			.Setting( LoginItem )
			.Setting( Tray )
			.Setting( LendBrowser )
			.Service<IHostLoginItem>( new HostLoginItem( this ) )
			.Setting( Access )
			.Slot( ShellSlots.StatusRight, 0, context => new RemoteStatusContent( context, this ) )
			.Route( PairingRoute, request => Prompts?.AnswerLink( request.Path ) )
			.Route( ReconnectRoute, request => ReconnectHost( Uri.UnescapeDataString( request.Path ) ) )
			.Route( RoleRoute, OpenRoleLink )
			.LinkHandler( PairingProtocol.Open );
		StreamPanel.Register( builder, BuildStreamSource );
	}

	/// <summary>Starts the gateway and the status of its notification-area icon in the daemon. In Loom it rewrites the former <c>remote</c> role,
	/// takes This PC's chats or links with the daemon, starts a link to every paired host, keeps the sign-in entry in step with its setting and
	/// the role, follows the <see cref="Role"/> setting, and lets browsers open <c>loom://</c> links in it. In the <c>local</c> role and the daemon it
	/// first takes the data folder's single-writer lock, so the daemon and this Loom never both write the chats.</summary>
	/// <exception cref="InvalidOperationException">In the daemon: Loom on this PC runs its chats itself, so the daemon must not.</exception>
	public void Start( ModuleContext context )
	{
		_context = context;
		var app = context.App;
		var dataFolder = DataFolder;
		var conversations = app.GetModule<ConversationsModule>();
		if ( app.IsHost )
		{
			RunningRole = HostRole;
			_dataLock = DataLock.TryTake( dataFolder )
				?? throw new InvalidOperationException( "Loom on this PC runs its chats itself (\u201cThis Loom\u201d in Remote access), so the daemon does not start. Choose the daemon there to use it." );
			var pipeName = _hooks?.PipeName;
			if ( _hooks is null && OperatingSystem.IsWindows() )
				pipeName = DaemonControl.PipeName;
			try
			{
				Gateway = new HostGateway( app, conversations.Hosts.Local, context.Post, new GatewayPaths( dataFolder, pipeName ) );
				Gateway.Start( Settings );
			}
			catch
			{
				_dataLock.Dispose();
				_dataLock = null;
				throw;
			}

			_statusFeed = new HostStatusFeed( app, conversations.Hosts.Local, Gateway, isUserDaemon: _hooks is null && !LoknPaths.IsCustom );
			FollowLoginItem();
			return;
		}

		if ( _hooks?.Role is null )
			MigrateRole( app.Settings );
		if ( (_hooks?.Role ?? LatchRole( Settings.Role )) == DaemonRole )
			StartDaemonLink();
		else
			TakeChats( dataFolder, conversations );

		StartLinks( dataFolder );
		FollowLoginItem();
		FollowRole();
		if ( _hooks is null && !LoknPaths.IsCustom && OperatingSystem.IsWindows() )
			PairingProtocol.Install( app );
	}

	/// <summary>Ends a switch under way, and stops the gateway, the daemon's status or every link and releases the single-writer lock. Paired hosts stay
	/// registered, stopped, so their panels and layouts survive a reload of this module, which links them again by id.</summary>
	public void Stop()
	{
		if ( _switch is { } pending )
			EndSwitch( pending, isDone: false );
		_statusFeed?.Dispose();
		_statusFeed = null;
		Gateway?.Stop();
		Gateway = null;
		foreach ( var link in _links )
			link.Stop();
		_links.Clear();
		StopDaemonLink();
		_dataLock?.Dispose();
		_dataLock = null;
		_context?.App.Settings.Changed -= UpdateLoginItem;
		_context?.App.Settings.Changed -= UpdateRole;
		_context = null;
	}

	/// <summary>Moves This PC's chats to another role while Loom runs and saves it in the <see cref="Role"/> setting. The user's switch waits until no
	/// agent works on This PC, here or in the daemon, and can be cancelled meanwhile (<see cref="CancelSwitch"/>); one Loom makes by itself, with a
	/// <paramref name="reason"/>, moves the chats at once and is posted as a notification and shown in Remote access once done. To the daemon: this
	/// Loom writes what it has not written yet, lets go of the chats and links with the daemon, starting it. To this Loom: the daemon stops, this Loom
	/// takes the chats back and its stores read the daemon's files. Chat panels stay open and show the same chats over the new sessions.</summary>
	/// <param name="role"><see cref="LocalRole"/> or <see cref="DaemonRole"/>.</param>
	/// <param name="reason">Why Loom switches by itself, such as after the daemon kept failing; null when the user chose.</param>
	/// <returns>A task that ends once the switch did or did not happen, with whether this Loom then runs in <paramref name="role"/>; false when it was
	/// cancelled, failed, or another switch was already moving the chats.</returns>
	/// <exception cref="ArgumentException">The role is not one of This PC's.</exception>
	internal Task<bool> SwitchRole( string role, string? reason = null )
	{
		if ( role is not (LocalRole or DaemonRole) )
			throw new ArgumentException( $"This PC's chats cannot run in the {role} role.", nameof( role ) );
		if ( _context is not { App.IsHost: false } )
			return Task.FromResult( false );

		ChooseRole( role );
		return StartSwitch( role, reason );
	}

	/// <summary>Ends a switch that still waits for agents, as Remote access's Cancel does: the <see cref="Role"/> setting goes back to the role this Loom
	/// runs in.</summary>
	internal void CancelSwitch()
	{
		if ( _switch is not { IsWaiting: true } pending )
			return;

		ChooseRole( RunningRole );
		EndSwitch( pending, isDone: false );
	}

	/// <summary>Counts the agents a waiting switch waits for: This PC's working chats, here or in the daemon while it runs; none once no switch waits.</summary>
	/// <remarks>Reading it rebuilds the reader when the count changes.</remarks>
	internal int CountWaiting()
	{
		if ( _switch is not { IsWaiting: true } || _context is not { } context )
			return 0;
		// A daemon that no longer runs has no agent left; what its mirror last said does not hold the switch back.
		if ( RunningRole == DaemonRole && !IsDaemonRunning() )
			return 0;

		return context.App.GetModule<ConversationsModule>().Hosts.Local.Store.WorkingCount;
	}

	/// <summary>Dismisses <see cref="Notice"/>.</summary>
	internal void DismissNotice()
	{
		Notice = null;
		_roleVersion.Value++;
	}

	/// <summary>Gets what Remote access should do once it shows: <see cref="AccessContent.AddHostRequest"/>, a pairing link to pair with, or empty;
	/// reading it rebuilds the reader when it changes.</summary>
	internal string AccessRequest => _accessRequest.Value;

	/// <summary>Opens Settings at Remote access, which then focuses the pairing link field for <see cref="AccessContent.AddHostRequest"/> or pairs with a
	/// <c>loom://pair#…</c> link.</summary>
	/// <param name="app">The application.</param>
	/// <param name="request">What Remote access does once it shows, or empty.</param>
	internal void OpenAccess( LoknApplication app, string request = "" )
	{
		_accessRequest.Value = request;
		app.OpenLink( LoknLinks.Create( "shell", "settings", Access.Id ) );
	}

	/// <summary>Takes the request <see cref="OpenAccess"/> left, so it runs once.</summary>
	internal string TakeAccessRequest()
	{
		var request = _accessRequest.Value;
		_accessRequest.Value = "";
		return request;
	}

	/// <summary>Finds the link of a paired host, or null.</summary>
	public RemoteClient? FindLink( string hostId ) => _links.FirstOrDefault( link => link.Host.Id == hostId );

	/// <summary>Pairs this PC with a host from its pairing link and shows its chats at once, without a restart: the host is saved in
	/// <c>remote-hosts.json</c>, linked, and becomes the current host unless a session of one host is open, such as a project of This PC, whose panels and
	/// folder stay on that host until the user switches. Pairing again with a host already paired replaces its entry, keeping its name, and its open window
	/// streams carry on over the new link.</summary>
	/// <param name="linkText">The pairing link, web client URL or payload.</param>
	/// <param name="cancellationToken">Cancels pairing.</param>
	/// <returns>The saved entry.</returns>
	/// <exception cref="ArgumentException">The text holds no pairing link, or the link is this PC's own.</exception>
	/// <exception cref="RemoteException">The host refused, such as for a used or expired link.</exception>
	/// <exception cref="IOException">No address of the host answered, or the list could not be written.</exception>
	/// <exception cref="InvalidDataException">The paired hosts list is damaged, so it is not overwritten; this is found before the link is used, which
	/// would otherwise leave the host with a device this PC cannot save.</exception>
	public async Task<PairedHost> AddHostAsync( string linkText, CancellationToken cancellationToken )
	{
		var context = Context;
		var dataFolder = DataFolder;
		_ = PairedHosts.Read( dataFolder );
		var (entry, _) = await (_hooks?.Pair ?? (( text, token ) => HostPairing.PairAsync( dataFolder, text, token )))( linkText, cancellationToken ).ConfigureAwait( false );
		return await UiInvoke.RunAsync( context.Post, () =>
		{
			PairedHosts.Upsert( dataFolder, entry );
			var saved = PairedHosts.Read( dataFolder ).FirstOrDefault( host => host.HostId == entry.HostId ) ?? entry;
			ClientStreamSource? stream = null;
			if ( FindLink( saved.HostId ) is { } running )
			{
				stream = running.Stream;
				running.Stop();
				_links.Remove( running );
			}
			StartLink( saved, stream );
			// The sidebar and tab strip follow the current host; the layout stays, so Remote access, where the link was pasted, shows the result.
			if ( !context.App.GetModule<ConversationsModule>().IsHostSessionOpen( context.App ) )
				context.App.Hosts.SetCurrent( saved.HostId );
			return saved;
		} ).ConfigureAwait( false );
	}

	/// <summary>Removes a paired host from this Loom: its link stops, its chats and every panel on it close, and its entry leaves
	/// <c>remote-hosts.json</c>. The host keeps this device until its user revokes it there.</summary>
	public void RemoveHost( string hostId )
	{
		var app = Context.App;
		if ( FindLink( hostId ) is { } link )
		{
			link.Stop();
			_links.Remove( link );
			_version.Value++;
		}
		app.GetModule<ConversationsModule>().Hosts.Remove( hostId );
		app.Hosts.Remove( hostId );
		try
		{
			PairedHosts.Remove( DataFolder, hostId );
		}
		catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or InvalidDataException )
		{
			app.ReportError( "Remote", exception );
		}
	}

	/// <summary>Renames a paired host in this Loom and saves the name; an empty name shows the name the host reports again.</summary>
	/// <exception cref="IOException">The list could not be written.</exception>
	/// <exception cref="InvalidDataException">The paired hosts list is damaged, so it is not overwritten.</exception>
	public void RenameHost( string hostId, string name )
	{
		if ( FindLink( hostId ) is not { Paired: { } entry } link )
			return;

		var renamed = entry with { Name = name.Trim() };
		var hosts = PairedHosts.Read( DataFolder ).Select( host => host.HostId == hostId ? host with { Name = renamed.Name } : host ).ToList();
		PairedHosts.Save( DataFolder, hosts );
		link.UpdatePaired( renamed );
		_version.Value++;
	}

	/// <summary>Dials a host again at once instead of after the current pause: a paired host, or This PC's daemon for <see cref="LoknHost.LocalId"/>.</summary>
	public void ReconnectHost( string hostId )
	{
		if ( hostId == LoknHost.LocalId )
			Daemon?.Reconnect();
		else
			FindLink( hostId )?.Reconnect();
	}

	/// <summary>Reads the paired hosts, importing the former <c>remote</c> role's host once, and links each; a damaged list is reported and links none.</summary>
	private void StartLinks( string dataFolder )
	{
		IReadOnlyList<PairedHost> entries;
		try
		{
			entries = PairedHosts.Read( dataFolder );
		}
		catch ( Exception exception ) when ( exception is InvalidDataException or IOException or UnauthorizedAccessException )
		{
			Context.App.ReportError( "Remote", exception );
			return;
		}

		foreach ( var entry in entries )
			StartLink( entry );
	}

	/// <summary>Registers a paired host with Core and Conversations, or finds it registered before a reload, and starts its link.</summary>
	/// <param name="entry">The host's entry.</param>
	/// <param name="stream">The window stream of the host's previous link, which the new link carries on; null for a new one.</param>
	private void StartLink( PairedHost entry, ClientStreamSource? stream = null )
	{
		var context = Context;
		var app = context.App;
		var host = app.Hosts.Add( entry.HostId, RemoteClient.ComputeName( entry, "" ) );
		if ( entry.Name.Length > 0 )
			host.Name = entry.Name;
		var chats = app.GetModule<ConversationsModule>().Hosts.Add( host );
		var link = new RemoteClient( app, chats, () => app.GetModule<OmpModule>().Service, context.Post, HostLinkKind.Paired, DataFolder, entry, BindDial( entry.HostId ),
			stream: stream );
		_links.Add( link );
		_version.Value++;
		link.Start();
	}

	/// <summary>Gets the checks' dialer for one host, or null for the link's own.</summary>
	private Func<CancellationToken, Task<ClientCarrier>>? BindDial( string hostId ) =>
		_hooks?.Dial is { } dial ? token => dial( hostId, token ) : null;

	/// <summary>Rewrites the former <c>remote</c> role to <c>local</c> in the Global scope, once, so This PC's own chats show again and the paired host
	/// is listed beside them (its <c>remote-host.json</c> is imported by <see cref="PairedHosts.Read"/>).</summary>
	private static void MigrateRole( SettingsService settings )
	{
		if ( settings.Get( Role, SettingsScope.Global ) == LegacyRemoteRole )
			settings.Set( Role, SettingsScope.Global, LocalRole );
	}

	/// <summary>Keeps the role this process runs in across reloads of this module, so a reload never moves the chats by itself: the first start takes the
	/// setting's, and every move of the chats latches its role (<see cref="SetRunningRole"/>). A role latched by an older build as <c>remote</c> reads as
	/// <c>local</c>.</summary>
	/// <remarks>The slot holds a string in the process's own data, which outlives this module's assembly.</remarks>
	private static string LatchRole( string setting )
	{
		if ( AppDomain.CurrentDomain.GetData( RoleSlot ) is string latched )
			return latched == LegacyRemoteRole ? LocalRole : latched;

		AppDomain.CurrentDomain.SetData( RoleSlot, setting );
		return setting;
	}

	/// <summary>Takes the chats for the <c>local</c> role at start: stops this PC's daemon when it runs, so it saves and lets go, then takes the
	/// single-writer lock. When the daemon cannot be stopped, the chats stay in it: this Loom links with it, chooses the daemon role and says so.</summary>
	private void TakeChats( string dataFolder, ConversationsModule conversations )
	{
		var app = Context.App;
		var isStopped = false;
		if ( UsesDaemonControl && DaemonControl.IsRunning )
			isStopped = DaemonControl.StopAndWait( DaemonStopTimeout );

		_dataLock = DataLock.TryTake( dataFolder );
		if ( _dataLock is null )
		{
			StartDaemonLink();
			if ( _hooks?.Role is null )
				ChooseRole( DaemonRole );
			app.ReportError( "Remote", new InvalidOperationException(
				$"The daemon on this PC did not stop and still holds this PC's chats, so they stay in the daemon and this Loom shows them through it. Choose This Loom in Remote access to try again. The daemon's log is {RemoteClient.DaemonLogPath}." ) );
			return;
		}

		// The daemon wrote its last changes as it stopped; read them, so this Loom does not save over them.
		if ( isStopped )
		{
			conversations.Hosts.Local.Projects.Reload();
			conversations.Hosts.Local.Store.Reload();
			app.Hosts.Local.BrowserTabs.Reload();
		}
		app.Hosts.Local.Update( LoknHostState.Local, "Chats run in this Loom.", canControl: true );
		SetRunningRole( LocalRole );
	}

	/// <summary>Links with this PC's daemon for the <c>daemon</c> role, which starts the daemon when it does not run, asks about the devices that pair with
	/// it, and counts its failures.</summary>
	private void StartDaemonLink()
	{
		var context = Context;
		var app = context.App;
		var daemon = new RemoteClient( app, app.GetModule<ConversationsModule>().Hosts.Local, () => app.GetModule<OmpModule>().Service, context.Post, HostLinkKind.Daemon,
			DataFolder, null, BindDial( LoknHost.LocalId ), _hooks?.DaemonExecutable );
		daemon.DaemonFailed += UpdateDaemonFailed;
		Daemon = daemon;
		Prompts = new PairingPrompts( app, daemon, context.Post );
		daemon.Start();
		SetRunningRole( DaemonRole );
	}

	/// <summary>Stops the link with this PC's daemon and its pairing questions; This PC's mirrors stay detached (<see cref="RemoteClient.Stop"/>).</summary>
	private void StopDaemonLink()
	{
		Prompts?.Dispose();
		Prompts = null;
		if ( Daemon is { } daemon )
		{
			daemon.DaemonFailed -= UpdateDaemonFailed;
			daemon.Stop();
		}
		Daemon = null;
		_failures.Clear();
		_roleVersion.Value++;
	}

	/// <summary>Records the role this Loom runs in, rebuilds the views that show it, and latches it for a reload of this module.</summary>
	private void SetRunningRole( string role )
	{
		RunningRole = role;
		_roleVersion.Value++;
		if ( _hooks?.Role is null )
			AppDomain.CurrentDomain.SetData( RoleSlot, role );
	}

	/// <summary>Saves where This PC's chats run in the <see cref="Role"/> setting without following the change, which the caller makes itself.</summary>
	private void ChooseRole( string role )
	{
		_isChoosing = true;
		try
		{
			Context.App.Settings.Set( Role, SettingsScope.Global, role );
		}
		finally
		{
			_isChoosing = false;
		}
	}

	/// <summary>Follows the <see cref="Role"/> setting while Loom runs, unless a check fixed the role, and applies a choice made while this module was off.</summary>
	private void FollowRole()
	{
		if ( _hooks?.Role is not null )
			return;

		Context.App.Settings.Changed += UpdateRole;
		UpdateRole();
	}

	/// <summary>Starts moving This PC's chats when the <see cref="Role"/> setting names another role than the one they run in or move to; choosing the
	/// running role again ends a switch that still waits, as Cancel does.</summary>
	private void UpdateRole()
	{
		if ( _isChoosing || _hooks?.Role is not null || _context is not { } context )
			return;

		var chosen = context.App.Settings.Get( Role, SettingsScope.Global );
		if ( chosen is LocalRole or DaemonRole && chosen != (_switch?.Role ?? RunningRole) )
			_ = StartSwitch( chosen, reason: null );
	}

	/// <summary>Starts moving This PC's chats to a role unless they run in it already or move there; a switch that still waits gives way. The user's switch
	/// waits for This PC's agents first, Loom's own moves the chats at once.</summary>
	/// <returns>The switch's completion.</returns>
	private Task<bool> StartSwitch( string role, string? reason )
	{
		if ( _switch is { } pending )
		{
			// A switch moving the chats finishes first; the setting it finds then decides what follows.
			if ( !pending.IsWaiting )
				return pending.Role == role ? pending.Completion : Task.FromResult( false );
			// Loom's own switch does not wait, so it replaces the user's waiting one even to the same role.
			if ( pending.Role == role && reason is null )
				return pending.Completion;

			EndSwitch( pending, isDone: false );
		}
		if ( role == RunningRole )
			return Task.FromResult( true );

		var next = new RoleSwitch( role, reason );
		_switch = next;
		Notice = null;
		_roleVersion.Value++;
		if ( next.IsWaiting )
			FollowWorking();
		else
			MoveChats( next );
		return next.Completion;
	}

	/// <summary>Waits for This PC's agents: follows its store's working chats, and moves the chats once none works.</summary>
	private void FollowWorking()
	{
		_waitingStore = Context.App.GetModule<ConversationsModule>().Hosts.Local.Store;
		_waitingStore.WorkingChanged += UpdateWorking;
		UpdateWaiting();
	}

	/// <summary>Stops following This PC's working chats.</summary>
	private void UnfollowWorking()
	{
		_waitingStore?.WorkingChanged -= UpdateWorking;
		_waitingStore = null;
	}

	/// <summary>Follows a chat whose agent started or stopped working while a switch waits.</summary>
	private void UpdateWorking( Guid conversationId, bool isWorking ) => UpdateWaiting();

	/// <summary>Moves the chats of a waiting switch once no agent it waits for works.</summary>
	private void UpdateWaiting()
	{
		if ( _switch is { IsWaiting: true } pending && CountWaiting() == 0 )
			MoveChats( pending );
	}

	/// <summary>Moves the chats of a switch, which can no longer be cancelled.</summary>
	private void MoveChats( RoleSwitch pending )
	{
		pending.IsWaiting = false;
		UnfollowWorking();
		_roleVersion.Value++;
		if ( pending.Role == DaemonRole )
			MoveToDaemon( pending );
		else
			MoveToLocal( pending );
	}

	/// <summary>Moves This PC's chats into the daemon: writes what this Loom has not written yet, lets go of the single-writer lock, and links with the
	/// daemon, which starts it; the link turns the stores and browser tabs into mirrors, and its sessions reopen the chat panels on the same chats.</summary>
	private void MoveToDaemon( RoleSwitch pending )
	{
		var local = Context.App.GetModule<ConversationsModule>().Hosts.Local;
		// Projects are written as they change; the history waits for a quiet moment and a tab's title for its next address, so both are written now.
		local.Store.Flush();
		local.Host.BrowserTabs.Stop();
		_dataLock?.Dispose();
		_dataLock = null;
		StartDaemonLink();
		FinishSwitch( pending, isDone: true );
	}

	/// <summary>Moves This PC's chats back into this Loom: pauses the daemon link so it does not start the daemon again, stops the daemon and waits off the
	/// UI thread until it saved and exited, then takes the chats back (<see cref="TakeChatsBack"/>).</summary>
	private void MoveToLocal( RoleSwitch pending )
	{
		var context = Context;
		var usesControl = UsesDaemonControl;
		Daemon?.StopDaemon();
		_ = Task.Run( () =>
		{
			var isStopped = !usesControl || !OperatingSystem.IsWindows() || DaemonControl.StopAndWait( DaemonStopTimeout );
			context.Post( () => TakeChatsBack( pending, isStopped ) );
		} );
	}

	/// <summary>Takes This PC's chats back once the daemon stopped: takes the single-writer lock, stops the daemon link, makes the stores and browser tabs
	/// the authority again over the files the daemon wrote, and goes back to local sessions, which reopen the chat panels on the same chats. When the
	/// daemon did not stop or the lock stays held, the chats stay in the daemon: its link dials again, the setting goes back to it, and Loom says why.</summary>
	private void TakeChatsBack( RoleSwitch pending, bool isStopped )
	{
		if ( _switch != pending || _context is not { } context )
			return;

		var app = context.App;
		var dataLock = isStopped ? DataLock.TryTake( DataFolder ) : null;
		if ( dataLock is null )
		{
			Daemon?.Reconnect();
			ChooseRole( DaemonRole );
			Notice = isStopped
				? $"Another process still holds this PC's chats ({Path.Combine( DataFolder, DataLock.FileName )}), so they stay in the daemon."
				: $"The daemon on this PC did not stop within {DaemonStopTimeout.TotalSeconds:0} s, so this PC's chats stay in it. Its log is {RemoteClient.DaemonLogPath}.";
			app.ReportError( "Remote", new InvalidOperationException( Notice ) );
			FinishSwitch( pending, isDone: false );
			return;
		}

		_dataLock = dataLock;
		StopDaemonLink();
		var local = app.GetModule<ConversationsModule>().Hosts.Local;
		// The daemon wrote its last changes as it stopped; the stores own the files again and read them, so this Loom does not write over them.
		local.Projects.EndMirror();
		local.Store.EndMirror();
		local.Host.BrowserTabs.EndMirror();
		local.Sessions.Source = null;
		app.Hosts.Local.Update( LoknHostState.Local, "Chats run in this Loom.", canControl: true );
		SetRunningRole( LocalRole );
		FinishSwitch( pending, isDone: true );
	}

	/// <summary>Ends a switch that moved the chats or could not, posts why Loom switched by itself once it did, and applies a choice made meanwhile.</summary>
	private void FinishSwitch( RoleSwitch pending, bool isDone )
	{
		EndSwitch( pending, isDone );
		_failures.Clear();
		if ( isDone && pending.Reason is { } reason && _context is { } context )
		{
			Notice = reason;
			_roleVersion.Value++;
			context.App.Notifications.Post( new( "Daemon on this PC", reason ) { Source = Id, Link = LoknLinks.Create( Id, RoleRoute ) } );
		}
		UpdateRole();
	}

	/// <summary>Forgets a switch, stops waiting for its agents and completes it.</summary>
	private void EndSwitch( RoleSwitch pending, bool isDone )
	{
		if ( _switch == pending )
			_switch = null;
		pending.IsWaiting = false;
		UnfollowWorking();
		_roleVersion.Value++;
		pending.Complete( isDone );
	}

	/// <summary>Counts a failure of the daemon in the <c>daemon</c> role: a switch to this Loom waiting for its agents stops waiting once it is gone, and
	/// <see cref="DaemonFailures.Limit"/> failures within <see cref="DaemonFailures.Window"/> move This PC's chats back into this Loom and keep them there.</summary>
	private void UpdateDaemonFailed( string reason )
	{
		if ( _context is null || RunningRole != DaemonRole || _switch is { IsWaiting: false } )
			return;

		UpdateWaiting();
		if ( _switch is { IsWaiting: false } || !_failures.Add( DateTimeOffset.UtcNow ) )
			return;

		_ = SwitchRole( LocalRole, $"The daemon failed {DaemonFailures.Limit} times within {DaemonFailures.Window.TotalMinutes:0} minutes, so this PC's chats run in this Loom again. "
			+ $"The last time: {reason} Its log is {RemoteClient.DaemonLogPath}." );
	}

	/// <summary>Reports whether this PC's daemon runs: whether it holds its mutex, or with a check's dialer whether its link is connected.</summary>
	private bool IsDaemonRunning() => UsesDaemonControl ? DaemonControl.IsRunning : Daemon?.Status == ClientState.Connected;

	/// <summary>Follows <c>lokn://remote/role/…</c>: <c>daemon</c> or <c>local</c> chooses where This PC's chats run, which moves them as the setting does;
	/// <c>add-host</c> opens Remote access at the pairing link field, and any other path opens Remote access.</summary>
	private void OpenRoleLink( RouteRequest request )
	{
		if ( request.Path is LocalRole or DaemonRole )
			request.App.Settings.Set( Role, SettingsScope.Global, request.Path );
		else
			OpenAccess( request.App, request.Path == AccessContent.AddHostRequest ? AccessContent.AddHostRequest : "" );
	}

	/// <summary>Writes the sign-in entry now and whenever the settings change, in Loom and in the daemon alike; checks never touch the user's Run key.</summary>
	private void FollowLoginItem()
	{
		if ( _hooks is not null )
			return;

		Context.App.Settings.Changed += UpdateLoginItem;
		UpdateLoginItem();
	}

	/// <summary>Writes or removes the daemon's sign-in entry when it differs from what was last written: it is on only while <see cref="LoginItem"/> is
	/// on and chats run in the daemon (the <see cref="Role"/> setting). The Remote access panel and the daemon's notification-area menu both change
	/// it through the setting.</summary>
	private void UpdateLoginItem()
	{
		if ( _context is not { } context || !OperatingSystem.IsWindows() )
			return;

		var settings = context.App.Settings;
		var isOn = settings.Get( LoginItem, SettingsScope.Global ) && settings.Get( Role, SettingsScope.Global ) == DaemonRole;
		if ( _loginItem == isOn )
			return;

		_loginItem = isOn;
		try
		{
			DaemonControl.UpdateLoginItem( isOn );
		}
		catch ( Exception exception ) when ( exception is UnauthorizedAccessException or IOException or System.Security.SecurityException )
		{
			context.App.ReportError( "Remote", exception );
		}
	}

	/// <summary>Supplies a new stream panel's source: the windows of the paired host the panel is on, across reconnects, else this PC's own.</summary>
	private IStreamSource? BuildStreamSource( PanelContext context ) =>
		context.HostId is { } hostId && !context.Host.IsLocal ? FindLink( hostId )?.Stream : new LocalStreamSource();
}

/// <summary>Checks' replacements for what the Remote module would otherwise take from the user's data folder, settings, pipe and network.</summary>
internal sealed record RemoteHooks
{
	/// <summary>Gets the data folder keys, devices, uploads and the paired hosts live in, or null for <see cref="LoknPaths.DataRoot"/>.</summary>
	public string? DataFolder
	{
		get; init;
	}

	/// <summary>Gets the daemon's pipe name, or null for no pipe; checks hand connections to <see cref="HostGateway.RunConnectionAsync"/> instead.</summary>
	public string? PipeName
	{
		get; init;
	}

	/// <summary>Gets the role to run in instead of the setting's, or null; a hooked role skips the migration of the former <c>remote</c> role and does not
	/// follow the setting, so only <see cref="RemoteModule.SwitchRole"/> moves the chats.</summary>
	public string? Role
	{
		get; init;
	}

	/// <summary>Gets the built <c>Loom.exe</c> that starts the daemon, or null. With it the module starts, stops and watches the real daemon of the data
	/// folder <see cref="LoknPaths.DataRoot"/> names, which the check points at a temporary bare folder; without it, hooks never touch a daemon.</summary>
	public string? DaemonExecutable
	{
		get; init;
	}

	/// <summary>Gets the links' dialer instead of their own, by host id (<see cref="LoknHost.LocalId"/> for the daemon link), or null.</summary>
	public Func<string, CancellationToken, Task<ClientCarrier>>? Dial
	{
		get; init;
	}

	/// <summary>Gets what pairs with a host from a pairing link instead of dialing it, or null for <see cref="HostPairing.PairAsync"/>.</summary>
	public Func<string, CancellationToken, Task<(PairedHost Host, IReadOnlyList<string> Scopes)>>? Pair
	{
		get; init;
	}
}
