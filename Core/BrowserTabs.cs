using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Goo;

namespace Lokn.Core;

/// <summary>One browser tab of the session: the page state every window that shows the tab renders and reports back (docs/remote.md, Browser frames).</summary>
/// <remarks>The tab holds no web view: each window that shows it loads <see cref="Url"/> in its own and reports title, address, progress and history.</remarks>
public sealed record BrowserTabInfo
{
	/// <summary>Gets the tab's identifier, the same in every window and on the host.</summary>
	public Guid Id { get; init; }

	/// <summary>Gets the page's address, as the user sees it.</summary>
	public string Url { get; init; } = "";

	/// <summary>Gets the page title, or empty before the page names itself.</summary>
	public string Title { get; init; } = "";

	/// <summary>Gets the settings profile whose cookies and sign-ins the page uses.</summary>
	public SettingsProfile Profile { get; init; }

	/// <summary>Gets whether a window reported the page loading.</summary>
	public bool Loading { get; init; }

	/// <summary>Gets the loading progress a window reported, from 0 to 100.</summary>
	public int Progress { get; init; }

	/// <summary>Gets why the page failed to load or start, or null.</summary>
	public string? Failed { get; init; }

	/// <summary>Gets whether the reporting window's history can go back.</summary>
	public bool CanGoBack { get; init; }

	/// <summary>Gets whether the reporting window's history can go forward.</summary>
	public bool CanGoForward { get; init; }

	/// <summary>Gets when the tab was opened.</summary>
	public DateTimeOffset Created { get; init; }

	/// <summary>Gets when the tab last went to another address.</summary>
	public DateTimeOffset LastUsed { get; init; }

	/// <summary>Gets the tab's place in the session's order, lowest first.</summary>
	public int Order { get; init; }

	/// <summary>Gets the tab's revision on the authority, raised by every change it applies; a mirror applies a pushed tab only when it is newer.</summary>
	public long Rev { get; init; }

	/// <summary>Gets the view that set <see cref="Url"/> last, so that view does not load it again and every other view follows it; null when none did.</summary>
	public string? By { get; init; }
}

/// <summary>One change to the session's browser tabs, as a client sends it to the authority (<c>browser.open</c>, <c>browser.navigate</c>,
/// <c>browser.report</c>, <c>browser.close</c>).</summary>
public sealed record BrowserOp
{
	/// <summary>Names the operation that opens a tab.</summary>
	public const string OpenKind = "open";

	/// <summary>Names the operation that sends a tab to another address.</summary>
	public const string NavigateKind = "navigate";

	/// <summary>Names the operation a view sends with its page's state.</summary>
	public const string ReportKind = "report";

	/// <summary>Names the operation that closes a tab.</summary>
	public const string CloseKind = "close";

	/// <summary>Gets the operation: <see cref="OpenKind"/>, <see cref="NavigateKind"/>, <see cref="ReportKind"/> or <see cref="CloseKind"/>.</summary>
	public string Op { get; init; } = "";

	/// <summary>Gets the tab.</summary>
	public Guid Tab { get; init; }

	/// <summary>Gets the address to open or go to, or the reported one.</summary>
	public string? Url { get; init; }

	/// <summary>Gets the reported title.</summary>
	public string? Title { get; init; }

	/// <summary>Gets the profile a new tab uses.</summary>
	public SettingsProfile? Profile { get; init; }

	/// <summary>Gets whether the reported page is loading.</summary>
	public bool? Loading { get; init; }

	/// <summary>Gets the reported loading progress, from 0 to 100.</summary>
	public int? Progress { get; init; }

	/// <summary>Gets the reported failure, or null for none.</summary>
	public string? Failed { get; init; }

	/// <summary>Gets whether the reporting view can go back.</summary>
	public bool? CanGoBack { get; init; }

	/// <summary>Gets whether the reporting view can go forward.</summary>
	public bool? CanGoForward { get; init; }

	/// <summary>Gets whether a report's address is where the view's own navigation took its page, which moves the tab there; a report without it
	/// describes the page at the tab's address and is dropped once the tab is elsewhere.</summary>
	public bool? Navigated { get; init; }

	/// <summary>Gets the view that made the change, or null.</summary>
	public string? By { get; init; }
}

/// <summary>Runs browser tool calls in a window that can show the tab, for a process without windows such as the daemon.</summary>
public interface IBrowserWindows
{
	/// <summary>Runs a browser tool call in the preferred window that can show pages, or returns null when no window can.</summary>
	/// <param name="context">The call's context.</param>
	/// <param name="tool">The tool name, such as <c>browser_navigate</c>.</param>
	/// <param name="arguments">The tool's arguments.</param>
	Task<AgentToolResult>? TryRun( AgentToolContext context, string tool, JsonElement arguments );
}

/// <summary>Reaches the loopback services of the PC whose session this PC mirrors, such as a paired PC's pages loading the host's dev server on
/// <c>localhost</c> (docs/remote.md, Localhost forwarding).</summary>
public interface IBrowserTunnel
{
	/// <summary>Connects a web view's connection to a loopback port of the session's PC and copies bytes both ways until both directions end.</summary>
	/// <param name="connection">The web view's connection, which the tunnel owns and closes.</param>
	/// <param name="host">The loopback name or address to reach there: <c>localhost</c>, or a loopback IP address.</param>
	/// <param name="port">The port.</param>
	/// <param name="answer">Tells the web view whether the port was reached, before any byte is copied; called exactly once.</param>
	Task RunAsync( Socket connection, string host, int port, Func<bool, Task> answer );
}

/// <summary>Keeps the session's browser tabs: page state without web views, saved in the data folder by the authority and mirrored by clients (docs/remote.md, Browser frames).</summary>
/// <remarks>
/// The authority is this process in the <c>local</c> role and the daemon in the <c>daemon</c> and <c>remote</c> roles. A mirror applies each change at
/// once and sends it through <see cref="Forward"/>; the authority applies it, raises the tab's revision, answers that revision and pushes the whole tab.
/// While a change the mirror sent waits for its answer, the tab's pushes are held: they may describe the tab before that change. The answer raises the
/// mirror's revision to the authority's, and the newest held push then applies only when it is at least that new, so neither an echo of its own change
/// nor an older state ever replaces what the mirror did. Changes a mirror could not send wait, <see cref="MaxUnsent"/> at most, and are applied and sent
/// again after the next <see cref="Load"/>: every open, navigate and close, and each tab's latest report unless the authority moved that tab meanwhile.
/// Mutate on the UI thread.
/// </remarks>
public sealed class BrowserTabs
{
	/// <summary>Defines the error a browser tool returns when no window can run it.</summary>
	public const string NoWindowMessage = "No Loom window is open to show the browser; open Loom on this PC or on a paired device.";

	/// <summary>Defines the most tabs the session keeps; opening more closes the least recently used.</summary>
	public const int MaxTabs = 256;

	/// <summary>Defines the most changes a mirror keeps while it cannot send them.</summary>
	public const int MaxUnsent = 256;

	/// <summary>Defines the longest address a tab keeps, in characters.</summary>
	public const int MaxUrlLength = 8192;

	/// <summary>Defines the longest title a tab keeps, in characters.</summary>
	public const int MaxTitleLength = 1024;

	/// <summary>Defines the supported file format version.</summary>
	private const int FileVersion = 1;

	/// <summary>Defines readable JSON with profile names for the tabs file.</summary>
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true,
		Converters = { new JsonStringEnumConverter<SettingsProfile>( allowIntegerValues: false ) },
	};

	/// <summary>Stores the tabs by identifier.</summary>
	private readonly Dictionary<Guid, BrowserTabInfo> _tabs = [];

	/// <summary>Holds a mirror's changes that could not be sent yet, oldest first.</summary>
	private readonly List<Unsent> _unsent = [];

	/// <summary>Holds a mirror's sent changes the authority has not answered yet, by reference, since two equal reports are two changes.</summary>
	private readonly HashSet<BrowserOp> _sent = new( ReferenceEqualityComparer.Instance );

	/// <summary>Holds, per tab with a sent change unanswered, the newest tab the authority pushed meanwhile.</summary>
	private readonly Dictionary<Guid, BrowserTabInfo> _held = [];

	/// <summary>Holds, per tab, the address a mirror last received from the authority, which tells whether a report kept while disconnected is stale.</summary>
	private readonly Dictionary<Guid, string> _received = [];

	/// <summary>Holds the tabs whose revision a mirror took from an answer, so its own state stands for that revision until the authority's push of it.</summary>
	private readonly HashSet<Guid> _answered = [];

	/// <summary>Tracks changes read by retained Goo views.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Stores the tabs file, or null to keep the tabs in memory.</summary>
	private readonly string? _path;

	/// <summary>Stores whether the loaded file can be replaced, false after it failed to parse.</summary>
	private bool _canSave = true;

	/// <summary>Stores whether a title changed since the authority last saved, which waits for the next address change or <see cref="Stop"/>.</summary>
	private bool _isDirty;

	/// <summary>Stores the proxy a paired host's session loads pages through, once started.</summary>
	private BrowserProxy? _proxy;

	/// <summary>Stores what reaches the paired host's loopback; read by the proxy's connections on the thread pool.</summary>
	private volatile IBrowserTunnel? _tunnel;

	/// <summary>Creates the tabs in memory, or loaded from a file the authority saves.</summary>
	/// <param name="path">The tabs file, such as <see cref="DefaultPath"/>, or null.</param>
	public BrowserTabs( string? path = null )
	{
		_path = path is null ? null : Path.GetFullPath( path );
		EnsureLoaded();
	}

	/// <summary>Gets the tabs file in the data folder.</summary>
	public static string DefaultPath => LoknPaths.Combine( "browser-tabs.json" );

	/// <summary>Gets the tabs in session order, subscribing the building view to changes.</summary>
	public IReadOnlyList<BrowserTabInfo> Tabs
	{
		get
		{
			_ = _version.Value;
			return [.. _tabs.Values.OrderBy( tab => tab.Order ).ThenBy( tab => tab.Created )];
		}
	}

	/// <summary>Gets whether this process mirrors the tabs of a daemon or paired host.</summary>
	public bool IsMirror { get; private set; }

	/// <summary>Gets whether this process mirrors the tabs of another PC, a paired host, whose files and loopback services are not this PC's: its web views
	/// load only <see cref="CanShow"/> addresses, through <see cref="ProxyServer"/>.</summary>
	public bool IsRemote { get; private set; }

	/// <summary>Gets whether the tabs are the authority's: always for the authority, and for a mirror once a snapshot was loaded, so a missing tab is gone.</summary>
	public bool IsSynced { get; private set; } = true;

	/// <summary>Gets where a mirror sends each change it applied, returning whether it was sent; null for the authority. A sent change is answered through
	/// <see cref="Answer"/>.</summary>
	public Func<BrowserOp, bool>? Forward { get; private set; }

	/// <summary>Gets or sets what runs browser tool calls in a window, in a process without windows such as the daemon; null when none can.</summary>
	public IBrowserWindows? Windows { get; set; }

	/// <summary>Gets or sets what reaches the paired host's loopback services for <see cref="ProxyServer"/>; null refuses loopback destinations.</summary>
	public IBrowserTunnel? Tunnel
	{
		get => _tunnel;
		set => _tunnel = value;
	}

	/// <summary>Gets or sets what copies a file a browser tool saved, such as a screenshot, to the PC whose agents call the tools, returning its path there;
	/// null when they run on this PC.</summary>
	public Func<string, CancellationToken, Task<string>>? ShareFile { get; set; }

	/// <summary>Gets the proxy every web view of a <see cref="IsRemote"/> session must connect through, as a Chromium <c>--proxy-server</c> value, or null
	/// for a session of this PC, or when the proxy could not start.</summary>
	public string? ProxyServer => _proxy?.Server;

	/// <summary>Gets the load failure, or null.</summary>
	internal string? LoadError { get; private set; }

	/// <summary>Raised on the UI thread after a tab changed, opened or closed, with its identifier and its new state or null once closed.</summary>
	public event Action<Guid, BrowserTabInfo?>? TabChanged;

	/// <summary>Raised on the UI thread after a mirror loaded a snapshot, which may have dropped or replaced any tab.</summary>
	public event Action? Reset;

	/// <summary>Reports a save failure to the owning application.</summary>
	internal event Action<string>? Failed;

	/// <summary>Finds a tab, or null.</summary>
	public BrowserTabInfo? Find( Guid id )
	{
		_ = _version.Value;
		return _tabs.GetValueOrDefault( id );
	}

	/// <summary>Reports whether this process's web views may load an address: any for a session of this PC; for a paired host's only http, https and
	/// <c>about:blank</c>, since its files and other schemes name what is on the host, not here.</summary>
	public bool CanShow( string url ) => !IsRemote || url == "about:blank"
		|| (Uri.TryCreate( url, UriKind.Absolute, out var uri ) && uri.Scheme is "http" or "https");

	/// <summary>Reads the tabs file again, such as after the daemon that owned it stopped, so this process does not save over its last changes; a mirror or
	/// an in-memory session is left alone.</summary>
	public void Reload()
	{
		if ( _path is null || IsMirror )
			return;

		_tabs.Clear();
		_canSave = true;
		_isDirty = false;
		LoadError = null;
		EnsureLoaded();
		if ( LoadError is { } error )
			Failed?.Invoke( error );
		_version.Value++;
		Reset?.Invoke();
	}

	/// <summary>Turns the tabs into a mirror that sends its changes through <paramref name="forward"/> until the next <see cref="Load"/> replaces them.</summary>
	/// <param name="forward">Sends a change to the authority and returns whether it was sent.</param>
	/// <param name="isRemote">Whether the authority is another PC, a paired host: the tabs empty meanwhile and pages load through <see cref="ProxyServer"/>.
	/// False keeps the tabs shown meanwhile, as for the daemon on this PC, whose file they came from.</param>
	public void BeginMirror( Func<BrowserOp, bool> forward, bool isRemote )
	{
		ArgumentNullException.ThrowIfNull( forward );
		IsMirror = true;
		IsSynced = false;
		Forward = forward;
		_sent.Clear();
		_held.Clear();
		_received.Clear();
		_answered.Clear();
		if ( !isRemote )
		{
			foreach ( var tab in _tabs.Values )
				_received[tab.Id] = tab.Url;
			return;
		}

		IsRemote = true;
		StartProxy();
		if ( _tabs.Count == 0 )
			return;

		_tabs.Clear();
		_version.Value++;
		Reset?.Invoke();
	}

	/// <summary>Lets go of what a mirror's owner, such as a stopping Remote module, left behind, so none of its code stays reachable: changes wait as while
	/// disconnected and are sent after the next <see cref="BeginMirror"/> and <see cref="Load"/>, and <see cref="Tunnel"/> and <see cref="ShareFile"/> clear.
	/// The tabs, <see cref="IsMirror"/>, <see cref="IsRemote"/> and the proxy stay, so views keep showing the tabs.</summary>
	public void Detach()
	{
		if ( IsMirror )
			Forward = SendNothing;
		Tunnel = null;
		ShareFile = null;
	}

	/// <summary>Makes a mirror the authority again, such as after this PC's daemon stopped and this process took the session back: changes not sent yet are
	/// dropped, since the daemon saved its tabs as it stopped, nothing is forwarded any more, the proxy of a paired host's session stops, and the tabs
	/// file is read again (<see cref="Reload"/>).</summary>
	/// <remarks>Call it on the UI thread. Tabs that mirror nothing are left alone.</remarks>
	public void EndMirror()
	{
		if ( !IsMirror )
			return;

		IsMirror = false;
		IsRemote = false;
		IsSynced = true;
		Forward = null;
		Tunnel = null;
		ShareFile = null;
		_unsent.Clear();
		_sent.Clear();
		_held.Clear();
		_received.Clear();
		_answered.Clear();
		_proxy?.Dispose();
		_proxy = null;
		Reload();
	}

	/// <summary>Sends nothing, for a detached mirror.</summary>
	private static readonly Func<BrowserOp, bool> SendNothing = _ => false;

	/// <summary>Replaces every tab with an authority's snapshot, then applies and sends again the changes that could not be sent before; a report of a tab
	/// the authority moved meanwhile is dropped as stale.</summary>
	public void Load( IEnumerable<BrowserTabInfo> tabs )
	{
		_tabs.Clear();
		_sent.Clear();
		_held.Clear();
		_received.Clear();
		_answered.Clear();
		foreach ( var tab in tabs )
		{
			_tabs[tab.Id] = tab;
			_received[tab.Id] = tab.Url;
		}
		IsSynced = true;
		Unsent[] waiting = [.. _unsent];
		_unsent.Clear();
		foreach ( var (op, received) in waiting )
		{
			if ( op.Op == BrowserOp.ReportKind && received is not null && _tabs.TryGetValue( op.Tab, out var current ) && current.Url != received )
				continue;
			Submit( op );
		}
		_version.Value++;
		Reset?.Invoke();
	}

	/// <summary>Opens a tab at an address and returns it.</summary>
	/// <param name="url">The address.</param>
	/// <param name="profile">The profile whose cookies the page uses.</param>
	/// <param name="by">The view that loads it, or null.</param>
	/// <param name="id">The tab's identifier, or null for a new one.</param>
	public BrowserTabInfo Open( string url, SettingsProfile profile, string? by = null, Guid? id = null )
	{
		var op = new BrowserOp { Op = BrowserOp.OpenKind, Tab = id ?? Guid.NewGuid(), Url = url, Profile = profile, By = by };
		Submit( op );
		return _tabs[op.Tab];
	}

	/// <summary>Sends a tab to another address, which every view showing it but <paramref name="by"/> loads.</summary>
	public void Navigate( Guid id, string url, string? by = null ) => Submit( new() { Op = BrowserOp.NavigateKind, Tab = id, Url = url, By = by } );

	/// <summary>Records what a view's page shows: its address, title, loading progress, failure and history.</summary>
	/// <param name="isNavigated">Whether the view's own navigation took its page to <paramref name="url"/>, which moves the tab there; otherwise the report
	/// describes the page at the tab's address and changes nothing once the tab is elsewhere.</param>
	public void Report( Guid id, string by, string url, string title, bool isLoading, int progress, string? failed, bool canGoBack, bool canGoForward, bool isNavigated = false ) =>
		Submit( new()
		{
			Op = BrowserOp.ReportKind,
			Tab = id,
			By = by,
			Url = url,
			Title = title,
			Loading = isLoading,
			Progress = progress,
			Failed = failed,
			CanGoBack = canGoBack,
			CanGoForward = canGoForward,
			Navigated = isNavigated ? true : null,
		} );

	/// <summary>Closes a tab in every window.</summary>
	public void Close( Guid id ) => Submit( new() { Op = BrowserOp.CloseKind, Tab = id } );

	/// <summary>Applies a client's change as the authority, raising the tab's revision; a change that alters nothing is ignored.</summary>
	/// <returns>The tab's revision after the change, or null once it is closed or unknown.</returns>
	/// <exception cref="ArgumentException">The operation is unknown or misses what it needs.</exception>
	public long? Apply( BrowserOp op )
	{
		var valid = Validate( op );
		Submit( valid );
		return _tabs.GetValueOrDefault( valid.Tab )?.Rev;
	}

	/// <summary>Applies a tab the authority pushed, when it is newer than the mirror's own, or as new as an answered change of the mirror, whose state it
	/// then replaces with the authority's; held while a change the mirror sent for it is unanswered.</summary>
	public void ApplyTab( BrowserTabInfo tab )
	{
		var isNewer = !_tabs.TryGetValue( tab.Id, out var known ) || known.Rev < tab.Rev || (known.Rev == tab.Rev && _answered.Contains( tab.Id ));
		var isWaiting = _sent.Any( op => op.Tab == tab.Id );
		if ( _held.TryGetValue( tab.Id, out var held ) && held.Rev >= tab.Rev )
			return;
		if ( isNewer || isWaiting )
			_received[tab.Id] = tab.Url;
		if ( isWaiting )
		{
			_held[tab.Id] = tab;
			return;
		}
		if ( !isNewer )
			return;

		Take( tab );
	}

	/// <summary>Removes a tab the authority closed.</summary>
	public void ApplyClosed( Guid id )
	{
		_held.Remove( id );
		_received.Remove( id );
		_answered.Remove( id );
		if ( _tabs.Remove( id ) )
			MarkChanged( id, null, isSaved: false );
	}

	/// <summary>Takes the authority's answer to a change this mirror sent: the tab's revision after it, or null when the change was refused or lost. Once
	/// no change of the tab waits, the newest push held meanwhile applies when it is at least as new as the mirror's tab; until a push of the answered
	/// revision replaces it, the mirror's tab is its own state at that revision.</summary>
	public void Answer( BrowserOp op, long? rev )
	{
		if ( !_sent.Remove( op ) )
			return;

		if ( rev is { } answered && _tabs.TryGetValue( op.Tab, out var known ) && known.Rev < answered )
		{
			_tabs[op.Tab] = known with { Rev = answered };
			_answered.Add( op.Tab );
		}
		if ( _sent.Any( sent => sent.Tab == op.Tab ) || !_held.Remove( op.Tab, out var held ) )
			return;
		// A tab closed here meanwhile stays closed; the authority's closing push follows.
		if ( !_tabs.TryGetValue( op.Tab, out var current ) || held.Rev < current.Rev )
			return;

		Take( held );
	}

	/// <summary>Saves a title change the authority has not saved yet and stops the proxy; at application shutdown.</summary>
	public void Stop()
	{
		if ( _isDirty && !IsMirror )
			EnsureSaved();
		_proxy?.Dispose();
		_proxy = null;
	}

	/// <summary>Checks a client's operation before the authority applies it.</summary>
	private static BrowserOp Validate( BrowserOp op )
	{
		if ( op.Tab == Guid.Empty )
			throw new ArgumentException( "The browser change names no tab." );
		if ( op.Url is { Length: > MaxUrlLength } )
			throw new ArgumentException( "The address is too long." );
		if ( op.Op is BrowserOp.OpenKind or BrowserOp.NavigateKind && op.Url is not { Length: > 0 } )
			throw new ArgumentException( "The browser change needs an address." );
		if ( op.Op is not (BrowserOp.OpenKind or BrowserOp.NavigateKind or BrowserOp.ReportKind or BrowserOp.CloseKind) )
			throw new ArgumentException( $"Unknown browser change: {op.Op}" );

		return op with { Title = op.Title is { Length: > MaxTitleLength } title ? title[..MaxTitleLength] : op.Title, Progress = op.Progress is { } progress ? Math.Clamp( progress, 0, 100 ) : null };
	}

	/// <summary>Starts the proxy a paired host's session loads pages through, once per process, reporting a failure.</summary>
	private void StartProxy()
	{
		if ( _proxy is not null )
			return;

		try
		{
			_proxy = new BrowserProxy( () => _tunnel );
		}
		catch ( SocketException error )
		{
			Failed?.Invoke( $"The browser could not start its connection to the host's pages: {error.Message} Browser pages stay closed until Loom restarts." );
		}
	}

	/// <summary>Replaces a tab with the authority's and tells the views.</summary>
	private void Take( BrowserTabInfo tab )
	{
		_tabs[tab.Id] = tab;
		_answered.Remove( tab.Id );
		MarkChanged( tab.Id, tab, isSaved: false );
	}

	/// <summary>Applies a change here and, in a mirror, sends it or keeps it until the next snapshot.</summary>
	private void Submit( BrowserOp op )
	{
		if ( !ApplyCore( op ) || Forward is not { } forward )
			return;

		_sent.Add( op );
		if ( forward( op ) )
			return;

		_sent.Remove( op );
		Keep( op );
	}

	/// <summary>Keeps a change that could not be sent: a report replaces the tab's older one, a navigation its older navigation and reports, and a close
	/// every change of the tab, dropping the tab's opening too when the authority never had it. Past <see cref="MaxUnsent"/> the oldest change goes, with
	/// the changes that need it.</summary>
	private void Keep( BrowserOp op )
	{
		switch ( op.Op )
		{
			case BrowserOp.ReportKind:
				_unsent.RemoveAll( entry => entry.Op.Tab == op.Tab && entry.Op.Op == BrowserOp.ReportKind );
				break;
			case BrowserOp.NavigateKind:
				_unsent.RemoveAll( entry => entry.Op.Tab == op.Tab && entry.Op.Op is BrowserOp.NavigateKind or BrowserOp.ReportKind );
				break;
			case BrowserOp.CloseKind:
				var isOpenedHere = _unsent.Any( entry => entry.Op.Tab == op.Tab && entry.Op.Op == BrowserOp.OpenKind );
				_unsent.RemoveAll( entry => entry.Op.Tab == op.Tab );
				if ( isOpenedHere )
					return;
				break;
		}

		_unsent.Add( new( op, _received.GetValueOrDefault( op.Tab ) ) );
		if ( _unsent.Count <= MaxUnsent )
			return;

		var oldest = _unsent[0];
		_unsent.RemoveAt( 0 );
		if ( oldest.Op.Op == BrowserOp.OpenKind )
			_unsent.RemoveAll( entry => entry.Op.Tab == oldest.Op.Tab );
	}

	/// <summary>Applies one change and reports whether it altered a tab; the authority raises the tab's revision.</summary>
	private bool ApplyCore( BrowserOp op )
	{
		var now = DateTimeOffset.UtcNow;
		var known = _tabs.GetValueOrDefault( op.Tab );
		BrowserTabInfo? next;
		switch ( op.Op )
		{
			case BrowserOp.OpenKind:
				if ( known is not null )
					return false;

				next = new BrowserTabInfo
				{
					Id = op.Tab,
					Url = op.Url ?? "about:blank",
					Profile = op.Profile ?? SettingsProfile.Personal,
					Created = now,
					LastUsed = now,
					Order = _tabs.Count == 0 ? 0 : _tabs.Values.Max( tab => tab.Order ) + 1,
					Loading = true,
					By = op.By,
				};
				break;
			case BrowserOp.NavigateKind when known is not null:
				next = known with { Url = op.Url ?? known.Url, By = op.By, LastUsed = now, Loading = true, Progress = 0, Failed = null };
				break;
			case BrowserOp.ReportKind when known is not null:
				var url = op.Url ?? known.Url;
				var isMoved = url != known.Url;
				// A page's state at an address the tab has left, such as a window still catching up, is stale; only a view's own navigation moves the tab.
				if ( isMoved && op.Navigated != true )
					return false;

				next = known with
				{
					Url = url,
					By = isMoved ? op.By : known.By,
					LastUsed = isMoved ? now : known.LastUsed,
					Title = op.Title ?? known.Title,
					Loading = op.Loading ?? known.Loading,
					Progress = op.Progress ?? known.Progress,
					Failed = op.Failed,
					CanGoBack = op.CanGoBack ?? known.CanGoBack,
					CanGoForward = op.CanGoForward ?? known.CanGoForward,
				};
				break;
			case BrowserOp.CloseKind when known is not null:
				next = null;
				break;
			default:
				return false;
		}

		if ( next is not null && known is not null && next == known )
			return false;

		if ( next is null )
		{
			_tabs.Remove( op.Tab );
			_held.Remove( op.Tab );
			_answered.Remove( op.Tab );
			MarkChanged( op.Tab, null, isSaved: true );
			return true;
		}

		if ( !IsMirror )
			next = next with { Rev = (known?.Rev ?? 0) + 1 };
		_tabs[op.Tab] = next;
		if ( known is null )
			TrimOldest();
		_isDirty |= known is not null && known.Title != next.Title;
		MarkChanged( op.Tab, next, isSaved: known is null || known.Url != next.Url );
		return true;
	}

	/// <summary>Closes the least recently used tabs past <see cref="MaxTabs"/>; the authority alone does, so mirrors follow its pushes.</summary>
	private void TrimOldest()
	{
		if ( IsMirror )
			return;

		while ( _tabs.Count > MaxTabs )
		{
			var oldest = _tabs.Values.MinBy( tab => tab.LastUsed )!.Id;
			_tabs.Remove( oldest );
			MarkChanged( oldest, null, isSaved: true );
		}
	}

	/// <summary>Saves the authority's tabs when <paramref name="isSaved"/>, refreshes the views and raises <see cref="TabChanged"/>.</summary>
	/// <remarks>A title alone is saved with the next address change or at <see cref="Stop"/>, so a page that animates its title never writes the file
	/// for each change.</remarks>
	private void MarkChanged( Guid id, BrowserTabInfo? tab, bool isSaved )
	{
		if ( isSaved && !IsMirror )
			EnsureSaved();
		_version.Value++;
		TabChanged?.Invoke( id, tab );
	}

	/// <summary>Loads the saved tabs, as not loading and with no failure; a file that fails to parse is left untouched and never overwritten.</summary>
	private void EnsureLoaded()
	{
		if ( _path is null || !File.Exists( _path ) )
			return;

		try
		{
			var document = JsonSerializer.Deserialize<TabsDocument>( File.ReadAllText( _path ), JsonOptions )
				?? throw new JsonException( "The browser tabs file is empty." );
			if ( document.Version != FileVersion || document.Tabs is null )
				throw new JsonException( "The browser tabs file has an unsupported version or no tabs." );

			foreach ( var tab in document.Tabs.Where( tab => tab.Id != Guid.Empty && tab.Url.Length is > 0 and <= MaxUrlLength ).Take( MaxTabs ) )
				_tabs[tab.Id] = tab with { Loading = false, Progress = 0, Failed = null, Rev = 0, By = null };
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or JsonException )
		{
			_canSave = false;
			LoadError = $"Could not load {_path}: {error.Message} The original file is unchanged; browser tabs stay in memory until it is corrected and Loom restarts.";
		}
	}

	/// <summary>Writes the tabs through a sibling temporary file so a failed write keeps the previous ones.</summary>
	private void EnsureSaved()
	{
		_isDirty = false;
		if ( _path is null || !_canSave )
			return;

		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( _path )! );
			var temporary = _path + ".tmp";
			var tabs = _tabs.Values.OrderBy( tab => tab.Order ).Select( tab => tab with { Loading = false, Progress = 0, Rev = 0, By = null } ).ToList();
			File.WriteAllText( temporary, JsonSerializer.Serialize( new TabsDocument { Tabs = tabs }, JsonOptions ) );
			File.Move( temporary, _path, overwrite: true );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			Failed?.Invoke( $"Browser tabs could not be saved to {_path}: {error.Message}" );
		}
	}

	/// <summary>A change a mirror could not send, with the address it last received for the tab then.</summary>
	/// <param name="Op">The change.</param>
	/// <param name="Received">The tab's address the authority last sent, or null when it had not sent the tab.</param>
	private readonly record struct Unsent( BrowserOp Op, string? Received );

	/// <summary>Stores the tabs in the versioned file format.</summary>
	private sealed record TabsDocument
	{
		/// <summary>Gets the file format version.</summary>
		public int Version { get; init; } = FileVersion;

		/// <summary>Gets the tabs in session order.</summary>
		public List<BrowserTabInfo>? Tabs { get; init; } = [];
	}
}
