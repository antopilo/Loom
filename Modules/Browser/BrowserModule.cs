using System.Text.Json;
using Lokn.Core;
using Microsoft.Web.WebView2.Core;

namespace Lokn.Modules.Browser;

/// <summary>Contributes browser panels that show a host's browser tabs (<see cref="LoknHost.BrowserTabs"/>, this PC's or a paired host's) with this window's own WebView2 (Edge) pages.</summary>
/// <remarks>
/// In the daemon, which has no window, the module creates no web view: its tools run in an attached window through this PC's <see cref="BrowserTabs.Windows"/>,
/// and fail with <see cref="BrowserTabs.NoWindowMessage"/> when none can (docs/remote.md, Host tools).
/// </remarks>
public sealed partial class BrowserModule : ILoknModule
{
	/// <summary>Defines the page a new browser panel opens.</summary>
	internal static readonly Setting<string> Home = new(
		"browser.home",
		"Home page",
		"The page a new browser panel opens.",
		defaultValue: "https://duckduckgo.com",
		category: "Browser",
		isValid: value => BrowserAddress.Compute( value, SearchDefault ) is not null );

	/// <summary>Defines the search address typed text that is not an address goes to; {0} is the escaped query.</summary>
	internal static readonly Setting<string> Search = new(
		"browser.search",
		"Search address",
		"Used for text that is not an address. {0} is the search text.",
		defaultValue: SearchDefault,
		category: "Browser",
		isValid: value => value.Contains( "{0}", StringComparison.Ordinal ) && Uri.TryCreate( value.Replace( "{0}", "q", StringComparison.Ordinal ), UriKind.Absolute, out _ ) );

	/// <summary>Defines the default search address.</summary>
	private const string SearchDefault = "https://duckduckgo.com/?q={0}";

	/// <summary>Stores the folder WebView2 data, reports and screenshots live under.</summary>
	private readonly string _root;

	/// <summary>Stores each host's WebView2 environment once its creation starts, with the proxy its pages connect through (null for none), by host id.</summary>
	/// <remarks>A WebView2 user data folder keeps the browser arguments it started with, so each host has its own folder (<see cref="ComputeDataPath"/>).</remarks>
	private readonly Dictionary<string, (string? Proxy, Task<CoreWebView2Environment> Environment)> _environments = new( StringComparer.Ordinal );

	/// <summary>Stores the running application, or null while the module is stopped.</summary>
	private LoknApplication? _app;

	/// <summary>Stores the module's lifetime, cancelled when it unloads or the application shuts down.</summary>
	private CancellationToken _lifetime;

	/// <summary>Stores every open browser panel, in the order they opened.</summary>
	private readonly List<BrowserContent> _panels = [];

	/// <summary>Stores the browser panel that took the latest followed link or report, while it stays open.</summary>
	private BrowserContent? _linkPanel;

	/// <summary>Stores, per panel whose agent showed reports, such as a chat, the tab its latest report used, which its next report replaces.</summary>
	private readonly Dictionary<Guid, BrowserTab> _reportTabs = [];

	/// <summary>Stores, per panel whose agent opened pages, the browser panel beside it that its pages and tools use first.</summary>
	private readonly Dictionary<Guid, BrowserContent> _ownBrowsers = [];

	/// <summary>Creates the module with its files in the data folder.</summary>
	public BrowserModule() : this( LoknPaths.DataRoot )
	{
	}

	/// <summary>Creates the module with its WebView2 data, reports and screenshots under another folder, for checks.</summary>
	internal BrowserModule( string root ) => _root = root;

	/// <summary>Computes the folder WebView2 keeps a host's cookies, history and caches in, one profile per settings profile: <c>browser</c> for this PC,
	/// <c>browser-hosts\&lt;hostId&gt;</c> for a paired host.</summary>
	/// <param name="root">The folder the module's files live under.</param>
	/// <param name="hostId">The host's id, <see cref="LoknHost.LocalId"/> for this PC.</param>
	internal static string ComputeDataPath( string root, string hostId ) =>
		hostId == LoknHost.LocalId ? Path.Combine( root, "browser" ) : Path.Combine( root, "browser-hosts", hostId );

	/// <summary>Gets the folder html reports agents show are saved in.</summary>
	internal string ReportsPath => Path.Combine( _root, "reports" );

	/// <summary>Gets the module identifier.</summary>
	public string Id => "browser";

	/// <summary>Gets the display name.</summary>
	public string Title => "Browser";

	/// <summary>Registers the settings, the browser panel, the command that opens an address, the browser a new task can start with, the handler for followed links, the tool agents show pages with, and the tools they use pages with.</summary>
	public void Register( ModuleBuilder builder )
	{
		if ( !OperatingSystem.IsWindows() )
			return;

		builder
			.Setting( Home )
			.Setting( Search )
			.Panel( "page", "Browser", Create, "\uE774", onHost: true )
			.Command( "open-url", "Browser: open URL", context => OpenAddress( context.App ) )
			.Contribute( TaskStarter.Point, new TaskStarter( "Browser", "\uE774", 50, app => OpenAddress( app ) ) )
			.Contribute( SetupItem.Point, new SetupItem
			{
				Key = "webview2",
				Title = "WebView2",
				Purpose = "Shows web pages in the Browser panel",
				Order = 40,
				CheckAsync = _ => Task.FromResult( CheckRuntime() ),
				FixLabel = "Install",
				FixAsync = ( progress, cancellationToken ) => ToolLocator.InstallWingetAsync( "Microsoft.EdgeWebView2Runtime", progress, cancellationToken ),
			} )
			.LinkHandler( OpenLink )
			.ToolAsync( "show",
				"""
				Shows a web page in a browser panel beside your conversation, for the user to look at: an HTML report you write (benchmark results, a plan, charts, an interactive demo), a local .html file, or a web address. Prefer this over describing a visual result in text. Pass exactly one of html, path or url. Pages can run JavaScript and load scripts from CDNs. Later calls replace the page in the same browser tab.
				An html report gets Loom's report kit, an editorial "paper and ink" look, so write plain semantic HTML with its classes and little or no CSS of your own. The user can switch its paper, chart style and font, export it, and select text or click a figure to comment; the comments come back to you as a message asking you to revise it, so show the revised report again with this tool.
				Kit: <main class="report"> around everything. <header class="report-head"><div class="top"><span class="kicker">Report</span><span class="dateline">Date</span></div><h1>Title</h1><p class="lead"><b>Key sentence.</b> Rest of the summary.</p></header>. Key numbers: <div class="stats"><div class="stat"><div class="value">986</div><div class="label">Confirmed kills</div><div class="sub">Detail</div></div>…</div>. Sections: <section><h2><span class="n">I.</span> Title</h2>…</section>. Note beside content: <div class="aside-grid"><aside><div class="note-title">1. Point</div><span class="tag">12 Oct 1932</span></aside><div>body, ul, table, figure</div></div>. Figure caption: <p class="fig">Fig. 1 — …</p>. Quotes: <blockquote><p>…</p><cite>Who</cite></blockquote>, <div class="pullquote"><p>"…"</p><cite>Who</cite></div>. Tables: th small caps, td.num right-aligned. Citation: <sup class="cite"><a href="url">1</a><span>Source shown on hover</span></sup>. Bars: <div class="bars"><div class="bar"><span>Label</span><span class="track"><i style="--value:.4"></i></span><span class="amount">986</span></div></div>. Timeline: <div class="timeline"><i class="span" style="--at:.3;--to:.5"></i><span class="event" style="--at:0"><b>12 Oct</b>Label</span></div> (class "event up" lifts a crowded label). SVG charts: paths with class fill-1 (solid ink) … fill-5 (dots), fill-6 (paper), .line, .area, .gridline, .axis; legend <div class="legend"><span><i data-fill="2"></i>Name</span></div>; drawings .shape and .stroke. Never pick colors yourself: charts use ink and patterns. Set a paper with <body data-paper="cream|paper|pink|sky|mint|lavender|peach|gray|sand|ink">.
				""",
				"""
				{
					"type": "object",
					"properties": {
						"html": { "type": "string", "description": "A complete HTML document to show. It is saved as a file first, so relative links resolve beside it." },
						"title": { "type": "string", "description": "A short name for the html report, used for its file name." },
						"path": { "type": "string", "description": "An absolute path to a local .html file, such as one you wrote." },
						"url": { "type": "string", "description": "An http or https address." }
					},
					"additionalProperties": false
				}
				""",
				Lend( "show", ( context, arguments ) => Task.FromResult( Show( context, arguments ) ) ) );
		RegisterPageTools( builder );
	}

	/// <summary>Runs a tool in this process's windows, or, in the daemon, which has none, in an attached window that can show pages.</summary>
	/// <param name="id">The tool's id within the module, such as <c>navigate</c>.</param>
	/// <param name="run">Runs the tool here.</param>
	/// <remarks>A call the daemon forwards names the chat panel of whichever window attached the chat last, which may be another window's; a caller
	/// that is no panel here counts as none, so the page opens in view rather than beside a panel that does not exist.</remarks>
	private Func<AgentToolContext, JsonElement, Task<AgentToolResult>> Lend( string id, Func<AgentToolContext, JsonElement, Task<AgentToolResult>> run ) => ( context, arguments ) =>
		!context.App.IsHost ? run( context with { Caller = FindCaller( context ) }, arguments )
			: context.App.Hosts.Local.BrowserTabs.Windows?.TryRun( context, $"{Id}_{id}", arguments ) ?? Task.FromResult( AgentToolResult.Error( BrowserTabs.NoWindowMessage ) );

	/// <summary>Returns a call's caller when it is a panel of this window, else null.</summary>
	private static Guid? FindCaller( AgentToolContext context )
	{
		var panels = context.App.Panels;
		return context.Caller is { } caller && panels.Panels.Any( panel => panel.Id == panels.ResolvePanelId( caller ) ) ? caller : null;
	}

	/// <summary>Checks that the Edge WebView2 runtime is installed, reporting its version.</summary>
	private static SetupStatus CheckRuntime()
	{
		try
		{
			return new( SetupState.Ready, CoreWebView2Environment.GetAvailableBrowserVersionString() );
		}
		catch ( WebView2RuntimeNotFoundException )
		{
			return new( SetupState.Missing, "Not installed" );
		}
	}

	/// <summary>Opens a browser panel on the current host with its address bar focused for the user to type where to go.</summary>
	private static Guid OpenAddress( LoknApplication app ) => app.Panels.Open( "browser.page", input: new BrowserRequest( null, FocusAddress: true ) );

	/// <summary>Creates a browser panel and tracks it as a place later links can open tabs in.</summary>
	private BrowserContent Create( PanelContext context )
	{
		var content = new BrowserContent( context, this );
		_panels.Add( content );
		return content;
	}

	/// <summary>Keeps the module's lifetime, which tells a panel closing for an unload or shutdown from one the user closed, and follows removed hosts.</summary>
	public void Start( ModuleContext context )
	{
		_lifetime = context.Lifetime;
		_app = context.App;
		context.App.Hosts.Removed += ForgetHost;
	}

	/// <summary>Stops following removed hosts.</summary>
	public void Stop()
	{
		if ( _app is { } app )
			app.Hosts.Removed -= ForgetHost;
		_app = null;
	}

	/// <summary>Drops a removed host's WebView2 environment and deletes its data folder when nothing holds it any more.</summary>
	/// <remarks>The host's panels closed before this, but their pages may still hold the folder; then it stays until it is free, which a later
	/// removal of a host with the same id, or the user, clears.</remarks>
	private void ForgetHost( LoknHost host )
	{
		if ( host.IsLocal )
			return;

		_environments.Remove( host.Id );
		var folder = ComputeDataPath( _root, host.Id );
		try
		{
			if ( Directory.Exists( folder ) )
				Directory.Delete( folder, recursive: true );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			// Its pages still close; the folder stays.
		}
	}

	/// <summary>Gets whether the module is unloading or the application is shutting down.</summary>
	internal bool IsStopping => _lifetime.IsCancellationRequested;

	/// <summary>Reports whether an open browser panel of this window on a host shows one of that host's session tabs.</summary>
	internal bool IsShown( string hostId, Guid tabId ) => _panels.Any( panel => panel.IsOpen && panel.Host.Id == hostId && panel.Tabs.Any( tab => tab.Id == tabId ) );

	/// <summary>Stops tracking a closed browser panel.</summary>
	internal void Forget( BrowserContent content )
	{
		_panels.Remove( content );
		if ( _linkPanel == content )
			_linkPanel = null;
		foreach ( var source in _ownBrowsers.Where( pair => pair.Value == content ).Select( pair => pair.Key ).ToArray() )
			_ownBrowsers.Remove( source );
		foreach ( var source in _reportTabs.Where( pair => pair.Value.Owner == content ).Select( pair => pair.Key ).ToArray() )
			_reportTabs.Remove( source );
	}

	/// <summary>Shows a followed web link in a new tab of the browser panel on screen, or a new browser panel beside its source, on the source's host;
	/// the daemon, which has no window, leaves links alone.</summary>
	private bool OpenLink( LinkRequest request )
	{
		if ( request.App.IsHost || request.Uri.Scheme is not ("http" or "https") )
			return false;

		var app = request.App;
		var source = request.SourcePanelId is { } id ? app.Panels.ResolvePanelId( id ) : (Guid?)null;
		var host = app.Hosts.Get( (source is { } panel ? app.Panels.GetHostId( panel ) : null) ?? app.Hosts.Current.Id );
		ShowBeside( app, host, request.Uri.AbsoluteUri, request.SourcePanelId, replace: null );
		return true;
	}

	/// <summary>Shows a page an agent asked for beside its conversation, in the tab its previous report used while that is on screen, saving html it wrote as a report file first.</summary>
	/// <remarks>The page opens in the browser tabs of the host whose agent asked (<see cref="FindHost"/>).</remarks>
	private AgentToolResult Show( AgentToolContext context, JsonElement arguments )
	{
		string? Read( string name ) => arguments.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

		var html = Read( "html" );
		var path = Read( "path" );
		var url = Read( "url" );
		if ( new[] { html, path, url }.Count( value => value is not null ) != 1 )
			throw new ArgumentException( "Pass exactly one of html, path or url." );

		// A paired PC's files are not the host's, where the agent wrote them; the host's own localhost reaches the paired PC through Loom.
		var host = FindHost( context );
		if ( host.BrowserTabs.IsRemote && (html is not null || path is not null) )
			throw new ArgumentException( "This browser runs on a paired PC, which cannot open files from the host. Serve the page over http on the host instead, such as with a local server on localhost, and pass its url: the paired PC reaches the host's localhost through Loom." );

		string address;
		if ( html is not null )
		{
			path = SaveReport( html, Read( "title" ) );
			RecordReport( path, Read( "title" ), context );
			address = new Uri( path ).AbsoluteUri;
		}
		else if ( path is not null )
		{
			if ( !Path.IsPathFullyQualified( path ) || !File.Exists( path ) )
				throw new ArgumentException( $"No file exists at {path}; pass an absolute path." );

			RecordReport( path, Read( "title" ), context );
			address = new Uri( path ).AbsoluteUri;
		}
		else if ( Uri.TryCreate( url, UriKind.Absolute, out var web ) && web.Scheme is "http" or "https" )
		{
			address = web.AbsoluteUri;
		}
		else
		{
			throw new ArgumentException( $"{url} is not an http or https address." );
		}

		var caller = context.Caller is { } id ? context.App.Panels.ResolvePanelId( id ) : Guid.Empty;
		var tab = ShowBeside( context.App, host, address, context.Caller, _reportTabs.GetValueOrDefault( caller ) );
		_reportTabs[caller] = tab;
		var shown = path is null ? address : path;
		return new( IsShown( context.App, tab.Owner ) ? $"Showing {shown} in browser panel {tab.Owner.Id}."
			: $"Opened {shown} in browser panel {tab.Owner.Id}, beside your conversation. The user is not looking at your conversation now, so the page waits there and shows when they open it." );
	}

	/// <summary>Loads an address beside the panel that asked, never in another session's, profile's or host's browser.</summary>
	/// <remarks>
	/// The order: the tab to replace, while it is open; the asking panel's own browser; the browser on screen, when the asking
	/// panel is on screen with it and uses the same profile; otherwise a new browser beside the asking panel, in its profile.
	/// Every browser reused shows <paramref name="host"/>'s tabs, and a new one opens on it.
	/// A source that is not on screen, such as a chat working in the background, gets its browser hidden beside it, where it
	/// appears when the user opens the chat; nothing on screen changes. Without a source, as for a user's link, it opens in view.
	/// </remarks>
	private BrowserTab ShowBeside( LoknApplication app, LoknHost host, string address, Guid? sourcePanelId, BrowserTab? replace )
	{
		var source = sourcePanelId is { } id ? app.Panels.ResolvePanelId( id ) : (Guid?)null;
		var isSourceShown = source is not { } visible || app.Panels.IsShown( visible );
		var profile = source is { } asking ? app.Panels.GetProfile( asking ) : null;
		BrowserTab tab;
		if ( replace is { IsClosed: false } && replace.Owner.IsOpen && replace.Owner.Host == host
			&& (source is { } from ? IsBeside( app, from, replace.Owner ) : IsShown( app, replace.Owner )) )
		{
			tab = replace;
			tab.Owner.Navigate( tab, address );
		}
		else if ( FindOwn( app, host, source ) is { } own )
		{
			tab = own.OpenTab( address );
		}
		else if ( isSourceShown && FindShown( app, host, profile ) is { } panel )
		{
			tab = panel.OpenTab( address );
		}
		else
		{
			tab = OpenPanel( app, host, source, new BrowserRequest( address, FocusAddress: false, profile ) );
		}

		Remember( app, tab, source, isSourceShown );
		return tab;
	}

	/// <summary>Shows a host's session tab in this window for an agent, such as one another window opened: in a panel here that shows it, else as a new tab of the asking
	/// panel's own browser or of the browser on screen in its profile, else in a new browser beside the asking panel; each on that host.</summary>
	private BrowserTab ShowSession( LoknApplication app, LoknHost host, Guid id, Guid? sourcePanelId )
	{
		var source = sourcePanelId is { } asked ? app.Panels.ResolvePanelId( asked ) : (Guid?)null;
		var isSourceShown = source is not { } visible || app.Panels.IsShown( visible );
		var views = _panels.Where( panel => panel.IsOpen && panel.Host == host ).SelectMany( panel => panel.Tabs.Where( tab => tab.Id == id ) ).ToArray();
		if ( (views.FirstOrDefault( view => IsShown( app, view.Owner ) ) ?? views.FirstOrDefault()) is { } existing )
		{
			existing.Owner.UpdateSelect( existing );
			return existing;
		}

		var profile = source is { } asking ? app.Panels.GetProfile( asking ) : null;
		var tab = FindOwn( app, host, source ) is { } own ? own.ShowTab( id )
			: isSourceShown && FindShown( app, host, profile ) is { } panel ? panel.ShowTab( id )
			: OpenPanel( app, host, source, new BrowserRequest( null, FocusAddress: false, profile, id ) );
		Remember( app, tab, source, isSourceShown );
		return tab;
	}

	/// <summary>Opens a browser panel on a host beside the asking panel, or in view without one, and returns its tab.</summary>
	private BrowserTab OpenPanel( LoknApplication app, LoknHost host, Guid? source, BrowserRequest request )
	{
		var opened = source is { } beside ? app.Panels.OpenBeside( "browser.page", beside, request, host.Id ) : app.Panels.Open( "browser.page", null, request, host.Id );
		return _panels.Single( candidate => candidate.Id == opened ).Selected ?? throw new InvalidOperationException( "The browser panel opened without a tab." );
	}

	/// <summary>Remembers a tab's panel as the asking panel's own browser, and as the one later links use while the asking panel is on screen, which brings it forward.</summary>
	private void Remember( LoknApplication app, BrowserTab tab, Guid? source, bool isSourceShown )
	{
		if ( source is { } owner )
			_ownBrowsers[owner] = tab.Owner;
		if ( isSourceShown )
		{
			_linkPanel = tab.Owner;
			app.Panels.Activate( tab.Owner.Id );
		}
	}

	/// <summary>Returns the open browser on a host a panel's agent opened its pages in, while it stays in that panel's workspace.</summary>
	private BrowserContent? FindOwn( LoknApplication app, LoknHost host, Guid? source )
	{
		if ( source is not { } id || !_ownBrowsers.TryGetValue( id, out var own ) || !own.IsOpen || own.Host != host )
			return null;

		return IsBeside( app, id, own ) ? own : null;
	}

	/// <summary>Reports whether a browser is in the same workspace as the panel that asked: waiting to appear beside it, both on screen,
	/// or both hidden in one saved layout. Without the Workspaces module, two hidden panels never count as together.</summary>
	/// <remarks>Two hidden panels can belong to different workspaces, so being hidden together is not enough: reusing such a browser would load the page in another workspace.</remarks>
	private static bool IsBeside( LoknApplication app, Guid source, BrowserContent browser )
	{
		if ( !browser.IsOpen )
			return false;
		if ( app.Panels.GetWaitingBeside( browser.Id ) == source )
			return true;

		var isSourceShown = app.Panels.IsShown( source );
		if ( isSourceShown != app.Panels.IsShown( browser.Id ) )
			return false;

		return isSourceShown || app.Services.Get<IPanelPlaces>()?.AreTogether( source, browser.Id ) == true;
	}

	/// <summary>Returns the browser panel on screen on a host that later links open tabs in: the one the latest link used, otherwise the first in layout order;
	/// with a profile, only one of that profile.</summary>
	private BrowserContent? FindShown( LoknApplication app, LoknHost host, SettingsProfile? profile = null )
	{
		if ( _linkPanel is { } panel && panel.Host == host && IsShown( app, panel ) && (profile is null || panel.Profile == profile) )
			return panel;

		foreach ( var pane in app.Panels.Panes )
		{
			if ( _panels.FirstOrDefault( candidate => candidate.Id == pane.PanelId && candidate.IsOpen && candidate.Host == host && (profile is null || candidate.Profile == profile) ) is { } shown )
				return shown;
		}

		return null;
	}

	/// <summary>Reports whether a browser panel is open and the current layout shows it.</summary>
	private static bool IsShown( LoknApplication app, BrowserContent panel ) => panel.IsOpen && app.Panels.IsShown( panel.Id );

	/// <summary>Saves an html report under a unique, readable file name, with the report kit linked (<see cref="AddKit"/>), and returns its path.</summary>
	internal string SaveReport( string html, string? title )
	{
		var name = new string( (title ?? "report").Select( character => char.IsAsciiLetterOrDigit( character ) ? char.ToLowerInvariant( character ) : '-' ).ToArray() ).Trim( '-' );
		while ( name.Contains( "--", StringComparison.Ordinal ) )
			name = name.Replace( "--", "-", StringComparison.Ordinal );
		if ( name.Length is 0 or > 60 )
			name = name.Length == 0 ? "report" : name[..60].TrimEnd( '-' );

		Directory.CreateDirectory( ReportsPath );
		var path = Path.Combine( ReportsPath, $"{DateTime.Now:yyyyMMdd-HHmmss}-{name}.html" );
		for ( var copy = 2; File.Exists( path ); copy++ )
			path = Path.Combine( ReportsPath, $"{DateTime.Now:yyyyMMdd-HHmmss}-{name}-{copy}.html" );

		try
		{
			EnsureKit();
			html = AddKit( html );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			// The report still shows, in its own styles only.
		}

		File.WriteAllText( path, html );
		return path;
	}

	/// <summary>Creates a host's WebView2 environment once, on the UI thread, and shares it between that host's panels; a paired host's pages connect through
	/// its session's proxy (<see cref="BrowserTabs.ProxyServer"/>), loopback included, so localhost means the host and never this PC.</summary>
	/// <remarks>Each host has its own user data folder (<see cref="ComputeDataPath"/>), since a folder keeps the browser arguments, the proxy among them, it started with.</remarks>
	/// <exception cref="WebView2RuntimeNotFoundException">The Edge WebView2 runtime is not installed.</exception>
	/// <exception cref="InvalidOperationException">A paired host's session has no proxy, or the proxy changed since the environment started.</exception>
	internal Task<CoreWebView2Environment> GetEnvironmentAsync( LoknHost host )
	{
		var tabs = host.BrowserTabs;
		var proxy = tabs.ProxyServer;
		if ( tabs.IsRemote && proxy is null )
			throw new InvalidOperationException( "It has no connection to the host's pages. Restart Loom." );
		if ( _environments.TryGetValue( host.Id, out var existing ) )
			return existing.Proxy == proxy ? existing.Environment : throw new InvalidOperationException( "Its connection changed since it started. Restart Loom." );

		// Chromium sends loopback addresses past a proxy unless told otherwise.
		var options = proxy is null ? null : new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = $"--proxy-server={proxy} --proxy-bypass-list=<-loopback>" };
		var environment = CoreWebView2Environment.CreateAsync( null, ComputeDataPath( _root, host.Id ), options );
		_environments[host.Id] = (proxy, environment);
		return environment;
	}
}

/// <summary>Asks a new browser panel to open an address or show a session tab, or to focus its address bar.</summary>
/// <param name="Address">The address or search text to open, or null for the home page.</param>
/// <param name="FocusAddress">Whether the address bar takes focus so the user can type.</param>
/// <param name="Profile">The settings profile whose cookies the pages use, such as the asking chat's; null uses the active profile.</param>
/// <param name="TabId">The session tab to show instead of opening one, or null.</param>
internal sealed record BrowserRequest( string? Address, bool FocusAddress, SettingsProfile? Profile = null, Guid? TabId = null );
