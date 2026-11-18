using System.Text.Json;
using Lokn.Core;
using Microsoft.Web.WebView2.Core;

namespace Lokn.Modules.Browser;

/// <summary>Contributes browser panels that show web pages with the system's WebView2 (Edge) runtime.</summary>
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

	/// <summary>Stores the shared WebView2 environment once its creation starts.</summary>
	private Task<CoreWebView2Environment>? _environment;

	/// <summary>Stores every open browser panel, in the order they opened.</summary>
	private readonly List<BrowserContent> _panels = [];

	/// <summary>Stores the browser panel that took the latest followed link or report, while it stays open.</summary>
	private BrowserContent? _linkPanel;

	/// <summary>Stores, per panel whose agent showed reports, such as a chat, the tab its latest report used, which its next report replaces.</summary>
	private readonly Dictionary<Guid, BrowserTab> _reportTabs = [];

	/// <summary>Stores, per panel whose agent opened pages, the browser panel beside it that its pages and tools use first.</summary>
	private readonly Dictionary<Guid, BrowserContent> _ownBrowsers = [];

	/// <summary>Gets the per-user folder WebView2 keeps cookies, history and caches in, one profile per settings profile.</summary>
	internal static string DataPath => Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "Lokn", "browser" );

	/// <summary>Gets the module identifier.</summary>
	public string Id => "browser";

	/// <summary>Gets the display name.</summary>
	public string Title => "Browser";

	/// <summary>Registers the settings, the browser panel, the command that opens an address, the browser a new task can start with, the handler for followed links, the tool agents show pages with, and the tools they use pages with.</summary>
	public void Register( ModuleBuilder builder )
	{
		builder
			.Setting( Home )
			.Setting( Search )
			.Panel( "page", "Browser", Create, "\uE774" )
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
			.Tool( "show",
				"Shows a web page in a browser panel beside your conversation, for the user to look at: an HTML report you write (benchmark results, a plan, charts, an interactive demo), a local .html file, or a web address. Prefer this over describing a visual result in text. Pass exactly one of html, path or url. Pages can run JavaScript and load scripts from CDNs. Later calls replace the page in the same browser tab.",
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
				Show );
		RegisterPageTools( builder );
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

	/// <summary>Opens a browser panel with its address bar focused for the user to type where to go.</summary>
	private static Guid OpenAddress( LoknApplication app ) => app.Panels.Open( "browser.page", input: new BrowserRequest( null, FocusAddress: true ) );

	/// <summary>Gets the per-user folder html reports agents show are saved in.</summary>
	internal static string ReportsPath => Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "Lokn", "reports" );

	/// <summary>Creates a browser panel and tracks it as a place later links can open tabs in.</summary>
	private BrowserContent Create( PanelContext context )
	{
		var content = new BrowserContent( context, this );
		_panels.Add( content );
		return content;
	}

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

	/// <summary>Shows a followed web link in a new tab of the browser panel on screen, or a new browser panel beside its source.</summary>
	private bool OpenLink( LinkRequest request )
	{
		if ( request.Uri.Scheme is not ("http" or "https") )
			return false;

		ShowBeside( request.App, request.Uri.AbsoluteUri, request.SourcePanelId, replace: null );
		return true;
	}

	/// <summary>Shows a page an agent asked for beside its conversation, in the tab its previous report used while that is on screen, saving html it wrote as a report file first.</summary>
	private AgentToolResult Show( AgentToolContext context, JsonElement arguments )
	{
		string? Read( string name ) => arguments.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

		var html = Read( "html" );
		var path = Read( "path" );
		var url = Read( "url" );
		if ( new[] { html, path, url }.Count( value => value is not null ) != 1 )
			throw new ArgumentException( "Pass exactly one of html, path or url." );

		string address;
		if ( html is not null )
		{
			path = SaveReport( html, Read( "title" ) );
			address = new Uri( path ).AbsoluteUri;
		}
		else if ( path is not null )
		{
			if ( !Path.IsPathFullyQualified( path ) || !File.Exists( path ) )
				throw new ArgumentException( $"No file exists at {path}; pass an absolute path." );

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
		var tab = ShowBeside( context.App, address, context.Caller, _reportTabs.GetValueOrDefault( caller ) );
		_reportTabs[caller] = tab;
		var shown = path is null ? address : path;
		return new( IsShown( context.App, tab.Owner ) ? $"Showing {shown} in browser panel {tab.Owner.Id}."
			: $"Opened {shown} in browser panel {tab.Owner.Id}, beside your conversation. The user is not looking at your conversation now, so the page waits there and shows when they open it." );
	}

	/// <summary>Loads an address beside the panel that asked, never in another session's or profile's browser.</summary>
	/// <remarks>
	/// The order: the tab to replace, while it is open; the asking panel's own browser; the browser on screen, when the asking
	/// panel is on screen with it and uses the same profile; otherwise a new browser beside the asking panel, in its profile.
	/// A source that is not on screen, such as a chat working in the background, gets its browser hidden beside it, where it
	/// appears when the user opens the chat; nothing on screen changes. Without a source, as for a user's link, it opens in view.
	/// </remarks>
	private BrowserTab ShowBeside( LoknApplication app, string address, Guid? sourcePanelId, BrowserTab? replace )
	{
		var source = sourcePanelId is { } id ? app.Panels.ResolvePanelId( id ) : (Guid?)null;
		var isSourceShown = source is not { } visible || app.Panels.IsShown( visible );
		var profile = source is { } asking ? app.Panels.GetProfile( asking ) : null;
		BrowserTab tab;
		if ( replace is { IsClosed: false } && replace.Owner.IsOpen && (source is { } from ? IsBeside( app, from, replace.Owner ) : IsShown( app, replace.Owner )) )
		{
			tab = replace;
			tab.Owner.Navigate( tab, address );
		}
		else if ( FindOwn( app, source ) is { } own )
		{
			tab = own.OpenTab( address );
		}
		else if ( isSourceShown && FindShown( app, profile ) is { } panel )
		{
			tab = panel.OpenTab( address );
		}
		else
		{
			var request = new BrowserRequest( address, FocusAddress: false, profile );
			var opened = source is { } beside ? app.Panels.OpenBeside( "browser.page", beside, request ) : app.Panels.Open( "browser.page", null, request );
			tab = _panels.Single( candidate => candidate.Id == opened ).Selected;
		}

		if ( source is { } owner )
			_ownBrowsers[owner] = tab.Owner;
		if ( isSourceShown )
		{
			_linkPanel = tab.Owner;
			app.Panels.Activate( tab.Owner.Id );
		}
		return tab;
	}

	/// <summary>Returns the open browser a panel's agent opened its pages in, while it stays in that panel's workspace.</summary>
	private BrowserContent? FindOwn( LoknApplication app, Guid? source )
	{
		if ( source is not { } id || !_ownBrowsers.TryGetValue( id, out var own ) || !own.IsOpen )
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

	/// <summary>Returns the browser panel on screen that later links open tabs in: the one the latest link used, otherwise the first in layout order; with a profile, only one of that profile.</summary>
	private BrowserContent? FindShown( LoknApplication app, SettingsProfile? profile = null )
	{
		if ( _linkPanel is { } panel && IsShown( app, panel ) && (profile is null || panel.Profile == profile) )
			return panel;

		foreach ( var pane in app.Panels.Panes )
		{
			if ( _panels.FirstOrDefault( candidate => candidate.Id == pane.PanelId && candidate.IsOpen && (profile is null || candidate.Profile == profile) ) is { } shown )
				return shown;
		}

		return null;
	}

	/// <summary>Reports whether a browser panel is open and the current layout shows it.</summary>
	private static bool IsShown( LoknApplication app, BrowserContent panel ) => panel.IsOpen && app.Panels.IsShown( panel.Id );

	/// <summary>Saves an html report under a unique, readable file name and returns its path.</summary>
	internal static string SaveReport( string html, string? title )
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

		File.WriteAllText( path, html );
		return path;
	}

	/// <summary>Creates the WebView2 environment once, on the UI thread, and shares it between panels.</summary>
	/// <exception cref="WebView2RuntimeNotFoundException">The Edge WebView2 runtime is not installed.</exception>
	internal Task<CoreWebView2Environment> GetEnvironmentAsync() => _environment ??= CoreWebView2Environment.CreateAsync( null, DataPath );
}

/// <summary>Asks a new browser panel to open an address, or to focus its address bar.</summary>
/// <param name="Address">The address or search text to open, or null for the home page.</param>
/// <param name="FocusAddress">Whether the address bar takes focus so the user can type.</param>
/// <param name="Profile">The settings profile whose cookies the pages use, such as the asking chat's; null uses the active profile.</param>
internal sealed record BrowserRequest( string? Address, bool FocusAddress, SettingsProfile? Profile = null );
