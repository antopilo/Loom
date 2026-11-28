using System.Text.Json;
using Lokn.Core;
using Lokn.Modules.Browser;

namespace Lokn.Tests;

/// <summary>Exercises address bar parsing, the session's browser tabs (operations, revisions, persistence and mirrors), and browser panels over them without
/// opening a window; every file goes to a temporary folder.</summary>
internal static class BrowserChecks
{
	/// <summary>Defines the search template the checks use.</summary>
	private const string Search = "https://search.example/?q={0}";

	/// <summary>Runs the browser checks.</summary>
	internal static void Run()
	{
		CoreChecks.Check( BrowserAddress.Compute( "https://example.com/a?b=1", Search ) == "https://example.com/a?b=1", "A full address loads as typed." );
		CoreChecks.Check( BrowserAddress.Compute( "  example.com/docs ", Search ) == "https://example.com/docs" && BrowserAddress.Compute( "github.com", Search ) == "https://github.com/", "A host gets https." );
		CoreChecks.Check( BrowserAddress.Compute( "localhost:5173", Search ) == "http://localhost:5173/" && BrowserAddress.Compute( "127.0.0.1:8080/x", Search ) == "http://127.0.0.1:8080/x", "Local hosts use http." );
		CoreChecks.Check( BrowserAddress.Compute( @"C:\docs\index.html", Search ) == "file:///C:/docs/index.html", "A drive path opens the file." );
		CoreChecks.Check( BrowserAddress.Compute( "how to cook rice", Search ) == "https://search.example/?q=how%20to%20cook%20rice" && BrowserAddress.Compute( "dotnet", Search ) == "https://search.example/?q=dotnet", "Words are searched." );
		CoreChecks.Check( BrowserAddress.Compute( "javascript:alert(1)", Search )!.StartsWith( "https://search.example/", StringComparison.Ordinal ), "Script addresses are searched, not run." );
		CoreChecks.Check( BrowserAddress.Compute( "   ", Search ) is null, "Blank text loads nothing." );

		var folder = Path.Combine( Path.GetTempPath(), "lokn-browser-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( folder );
		try
		{
			CheckSession();
			CheckPersistence( folder );
			CheckMirror();
			BrowserSyncChecks.Run();
			CheckHostTools( folder );
			CheckRemotePolicy( folder );

			var app = new LoknApplication();
			var module = new BrowserModule( folder );
			CoreChecks.Check( app.Load( module ), "The browser module loads." );
			CoreChecks.Check( app.Panels.Types.Any( type => type.Id == "browser.page" ) && app.Commands.Items.Any( command => command.Id == "browser.open-url" ), "The browser panel and its open command are registered." );
			CoreChecks.Check( !app.Settings.SetValue( app.Settings.Definitions.Single( definition => definition.Id == "browser.search" ), SettingsScope.Global, "https://no-placeholder.example/" ), "A search address without {0} is rejected." );
			CheckLinks( app, module );
			CheckPanels( app );
			Guid[] open = [.. app.BrowserTabs.Tabs.Select( tab => tab.Id )];
			app.Shutdown();
			CoreChecks.Check( open.Length > 0 && open.All( id => app.BrowserTabs.Find( id ) is not null ), "Shutting down, which closes every panel, keeps their tabs in the session." );
		}
		finally
		{
			TryDelete( folder );
		}
		Console.WriteLine( "PASS browser address parsing, search fallback, local hosts and files, session tabs with revisions, persistence with titles saved at shutdown and mirrors, panels over session tabs closing their tabs in the local role, browser_tabs capped, the daemon's no-window error, a paired host's session limited to web pages through its proxy with lent tools refusing files, browser panels and tools routed to their host's tabs with one WebView2 data folder per host, registration, and followed links in new tabs of the browser panel on screen." );
	}

	/// <summary>Verifies a paired host's session in this window: its pages load through a proxy on loopback, only http, https and about:blank load here, a
	/// tab at a file of the host says so instead of loading this PC's file, and lent browser tools refuse html and paths to show, files, drive and UNC
	/// paths, and tabs at such addresses. A browser panel on the host shows the host's tabs, tools follow the calling agent's host or else the calling panel's,
	/// This PC's tabs are untouched, and each host's pages keep their own WebView2 data folder.</summary>
	private static void CheckRemotePolicy( string folder )
	{
		var app = new LoknApplication();
		try
		{
			CoreChecks.Check( app.Load( new BrowserModule( Path.Combine( folder, "remote" ) ) ), "The browser module loads for a paired host's session." );
			var desk = app.Hosts.Add( "policy-host-id-0000000", "Desk" );
			var tabs = desk.BrowserTabs;
			tabs.BeginMirror( _ => false, isRemote: true );
			CoreChecks.Check( BrowserModule.ComputeDataPath( folder, LoknHost.LocalId ) == Path.Combine( folder, "browser" )
				&& BrowserModule.ComputeDataPath( folder, desk.Id ) == Path.Combine( folder, "browser-hosts", desk.Id ),
				"This PC's pages keep the browser data folder, and each paired host's pages get their own." );
			CoreChecks.Check( !app.Hosts.Local.BrowserTabs.IsRemote && app.Hosts.Local.BrowserTabs.CanShow( "file:///C:/Users/me/page.html" ) && !tabs.CanShow( "file:///C:/Users/me/page.html" ),
				"Only the paired host's session is limited to web pages; This PC's still opens files." );
			CoreChecks.Check( tabs.IsRemote && tabs.ProxyServer?.StartsWith( "socks5://127.0.0.1:", StringComparison.Ordinal ) == true,
				"A paired host's session loads its pages through a proxy on this PC's loopback." );
			CoreChecks.Check( tabs.CanShow( "https://example.com/" ) && tabs.CanShow( "http://localhost:5173/" ) && tabs.CanShow( "about:blank" )
				&& !tabs.CanShow( "file:///C:/Users/me/secret.txt" ) && !tabs.CanShow( BrowserAddress.Compute( @"C:\Users\me\secret.txt", Search )! )
				&& !tabs.CanShow( BrowserAddress.Compute( @"\\server\share\a.html", Search )! ) && !tabs.CanShow( "data:text/html,hi" ) && !tabs.CanShow( "edge://settings" )
				&& !tabs.CanShow( "javascript:alert(1)" ),
				"Only http, https and about:blank load for a paired host's session; files, drive and UNC paths and other schemes do not." );

			var hostFile = new BrowserTabInfo { Id = Guid.NewGuid(), Url = "file:///C:/Users/me/secret.txt", Rev = 1, By = "host-view" };
			tabs.Load( [hostFile] );
			var panel = app.Panels.Open( "browser.page", input: new BrowserRequest( null, FocusAddress: false, TabId: hostFile.Id ), hostId: desk.Id );
			var content = (BrowserContent)app.Panels.Sessions.Single( session => session.Info.Id == panel ).Content!;
			var view = content.Selected!;
			CoreChecks.Check( content.Host == desk && content.Session == tabs && app.Hosts.Local.BrowserTabs.Find( hostFile.Id ) is null,
				"A browser panel on a paired host shows that host's tabs, not This PC's." );
			CoreChecks.Check( view.Blocked.Contains( hostFile.Url, StringComparison.Ordinal ) && new BrowserTabSync( tabs, hostFile.Id ).ComputeLoad( "", isStarting: true ) is null,
				"A tab at a file of the host says so instead of loading this PC's file." );

			string Refusal( string tool, string json ) =>
				app.Tools.InvokeAsync( tool, JsonDocument.Parse( json ).RootElement, null, CancellationToken.None, host: desk.Id ).GetAwaiter().GetResult() is { IsError: true } result ? result.Text : "";
			var html = Refusal( "browser_report", """{ "html": "<p>x</p>" }""" );
			var path = Refusal( "browser_report", """{ "path": "C:\\Windows\\win.ini" }""" );
			var file = Refusal( "browser_navigate", """{ "url": "file:///C:/Windows/win.ini" }""" );
			var drive = Refusal( "browser_navigate", """{ "url": "C:\\Windows\\win.ini" }""" );
			var unc = Refusal( "browser_navigate", """{ "url": "\\\\server\\share\\a.html" }""" );
			var read = Refusal( "browser_read", $$"""{ "tab": "{{hostFile.Id:N}}" }""" );
			CoreChecks.Check( html.Contains( "cannot open files from the host", StringComparison.Ordinal ) && path.Contains( "cannot open files from the host", StringComparison.Ordinal )
				&& file.Contains( "only http and https", StringComparison.Ordinal ) && drive.Contains( "only http and https", StringComparison.Ordinal )
				&& unc.Contains( "only http and https", StringComparison.Ordinal ) && read.Contains( "on the host's PC", StringComparison.Ordinal ),
				$"Lent browser tools refuse html and paths to show, file, drive and UNC addresses, and a tab at a file ({html} | {path} | {file} | {drive} | {unc} | {read})." );
			CoreChecks.Check( tabs.Tabs.Count == 1 && app.Panels.Panels.Count( candidate => candidate.TypeId == "browser.page" ) == 1, "Refused calls open no tab and no panel." );

			var byPanel = app.Tools.InvokeAsync( "browser_navigate", JsonDocument.Parse( """{ "url": "file:///C:/Windows/win.ini" }""" ).RootElement, panel, CancellationToken.None ).GetAwaiter().GetResult();
			CoreChecks.Check( byPanel is { IsError: true } && byPanel.Text.Contains( "only http and https", StringComparison.Ordinal ),
				$"A call from This PC's agent without a host uses the calling panel's host's tabs ({byPanel.Text})." );
			var listed = app.Tools.InvokeAsync( "browser_tabs", JsonDocument.Parse( "{}" ).RootElement, null, CancellationToken.None, host: desk.Id ).GetAwaiter().GetResult();
			var local = app.Tools.InvokeAsync( "browser_tabs", JsonDocument.Parse( "{}" ).RootElement, null, CancellationToken.None ).GetAwaiter().GetResult();
			CoreChecks.Check( listed.Text.Contains( hostFile.Id.ToString( "N" )[..8], StringComparison.Ordinal ) && !local.Text.Contains( hostFile.Id.ToString( "N" )[..8], StringComparison.Ordinal ),
				$"browser_tabs lists the calling host's tabs; This PC's agent never sees the paired host's ({listed.Text} | {local.Text})." );
		}
		finally
		{
			app.Shutdown();
		}
	}

	/// <summary>Verifies the session's operations: open, navigate, report and close, each raising the tab's revision once, and nothing for a change that alters nothing.</summary>
	private static void CheckSession()
	{
		var tabs = new BrowserTabs();
		var changes = new List<(Guid Id, BrowserTabInfo? Tab)>();
		tabs.TabChanged += ( id, tab ) => changes.Add( (id, tab) );
		var first = tabs.Open( "https://example.com/", SettingsProfile.Work, "view-a" );
		var second = tabs.Open( "https://example.org/", SettingsProfile.Personal );
		CoreChecks.Check( first.Rev == 1 && first.Profile == SettingsProfile.Work && first.Loading && first.By == "view-a" && tabs.Tabs.Select( tab => tab.Id ).SequenceEqual( [first.Id, second.Id] )
			&& second.Order > first.Order && changes.Count == 2, "Opening tabs gives each revision 1, its profile and the opening view, in session order." );

		tabs.Report( first.Id, "view-a", "https://example.com/", "Example", isLoading: false, 100, failed: null, canGoBack: false, canGoForward: false );
		var reported = tabs.Find( first.Id )!;
		CoreChecks.Check( reported.Title == "Example" && !reported.Loading && reported.Progress == 100 && reported.Rev == 2, "A report sets the title and loading state and raises the revision." );
		tabs.Report( first.Id, "view-b", "https://example.com/", "Example", isLoading: false, 100, failed: null, canGoBack: false, canGoForward: false );
		CoreChecks.Check( tabs.Find( first.Id )!.Rev == 2 && changes.Count == 3, "A report that changes nothing raises nothing." );

		tabs.Navigate( first.Id, "https://example.com/next", "view-b" );
		var navigated = tabs.Find( first.Id )!;
		CoreChecks.Check( navigated.Url == "https://example.com/next" && navigated.By == "view-b" && navigated.Loading && navigated.Rev == 3 && navigated.LastUsed >= reported.LastUsed,
			"Navigating sets the address and the view other windows follow, and raises the revision." );
		tabs.Report( first.Id, "view-a", "https://example.com/next", "Next", isLoading: false, 100, failed: "failed: ConnectionAborted", canGoBack: true, canGoForward: false );
		CoreChecks.Check( tabs.Find( first.Id ) is { By: "view-b", Failed: "failed: ConnectionAborted", CanGoBack: true, Title: "Next" },
			"A report of the same address keeps the view that set it, and carries failure and history." );

		var failed = false;
		try
		{
			tabs.Apply( new BrowserOp { Op = "move", Tab = first.Id } );
		}
		catch ( ArgumentException )
		{
			failed = true;
		}
		CoreChecks.Check( failed, "An unknown change from a client is refused." );

		tabs.Close( second.Id );
		CoreChecks.Check( tabs.Find( second.Id ) is null && tabs.Tabs.Count == 1 && changes[^1] == (second.Id, null), "Closing a tab removes it and says so." );
		tabs.Close( second.Id );
		CoreChecks.Check( changes[^1] == (second.Id, null) && changes.Count(change => change.Tab is null) == 1, "Closing it again does nothing." );
	}

	/// <summary>Verifies the authority saves its tabs in its file and loads them again, never loading a page as still loading, saves a title alone only with
	/// the next address change or at shutdown, and keeps an unreadable file untouched.</summary>
	private static void CheckPersistence( string folder )
	{
		var path = Path.Combine( folder, "browser-tabs.json" );
		var tabs = new BrowserTabs( path );
		var kept = tabs.Open( "https://example.com/", SettingsProfile.Personal );
		var closed = tabs.Open( "https://example.org/", SettingsProfile.Work );
		tabs.Report( kept.Id, "view", "https://example.com/", "Kept page", isLoading: true, 40, failed: null, canGoBack: false, canGoForward: false );
		tabs.Close( closed.Id );
		var again = new BrowserTabs( path );
		CoreChecks.Check( again.Tabs is [{ } restored] && restored.Id == kept.Id && restored.Title == "Kept page" && restored.Url == "https://example.com/" && !restored.Loading
			&& restored.Profile == SettingsProfile.Personal && restored.Created == tabs.Find( kept.Id )!.Created && again.LoadError is null,
			"A new start loads the saved tabs with their ids, titles, addresses, profiles and times, not loading." );

		var written = File.GetLastWriteTimeUtc( path );
		var saved = File.ReadAllText( path );
		for ( var frame = 0; frame < 20; frame++ )
			tabs.Report( kept.Id, "view", "https://example.com/", $"Ticker {frame}", isLoading: false, 100, failed: null, canGoBack: false, canGoForward: false );
		CoreChecks.Check( File.ReadAllText( path ) == saved && File.GetLastWriteTimeUtc( path ) == written, "A page that animates its title writes nothing per change." );
		tabs.Stop();
		CoreChecks.Check( new BrowserTabs( path ).Tabs is [{ Title: "Ticker 19" }], "Shutdown saves the latest title." );

		File.WriteAllText( path, "{ broken" );
		var broken = new BrowserTabs( path );
		broken.Open( "https://example.net/", SettingsProfile.Personal );
		CoreChecks.Check( broken.LoadError is not null && File.ReadAllText( path ) == "{ broken", "An unreadable tabs file is reported and never overwritten." );
	}

	/// <summary>Verifies a mirror against an authority: changes apply at once and reach the authority, echoes and stale pushes change nothing, changes made
	/// while disconnected wait for the next snapshot, and a snapshot drops tabs the authority closed.</summary>
	private static void CheckMirror()
	{
		var host = new BrowserTabs();
		var mirror = new BrowserTabs();
		var other = new BrowserTabs();
		var isConnected = true;
		var sent = new List<BrowserOp>();
		host.TabChanged += ( id, tab ) =>
		{
			// The host pushes every change to every client, the sender's too.
			foreach ( var client in (BrowserTabs[])[mirror, other] )
			{
				if ( tab is null )
					client.ApplyClosed( id );
				else
					client.ApplyTab( tab );
			}
		};
		bool Forward( BrowserOp op )
		{
			if ( !isConnected )
				return false;
			sent.Add( op );
			// The host's push arrives before its answer here, so the mirror holds it until the answer.
			mirror.Answer( op, host.Apply( op ) );
			return true;
		}
		mirror.BeginMirror( Forward, isRemote: false );
		other.BeginMirror( _ => true, isRemote: false );
		CoreChecks.Check( mirror.IsMirror && !mirror.IsSynced, "A mirror waits for the host's snapshot." );
		mirror.Load( host.Tabs );
		other.Load( host.Tabs );

		var tab = mirror.Open( "https://example.com/", SettingsProfile.Personal, "mirror-view" );
		CoreChecks.Check( host.Find( tab.Id ) is { Rev: 1 } && mirror.Find( tab.Id ) is { Rev: 1 } && other.Find( tab.Id ) is { Url: "https://example.com/", By: "mirror-view" },
			"A mirror's new tab reaches the host, whose push brings every mirror the host's revision." );

		mirror.Navigate( tab.Id, "https://example.com/two", "mirror-view" );
		var echo = host.Find( tab.Id )!;
		mirror.ApplyTab( echo );
		CoreChecks.Check( mirror.Find( tab.Id ) == echo && other.Find( tab.Id )!.Url == "https://example.com/two",
			"Navigating in one mirror reaches the other, and the echo of its own change changes nothing." );
		mirror.ApplyTab( echo with { Rev = 1, Url = "https://stale.example/" } );
		CoreChecks.Check( mirror.Find( tab.Id )!.Url == "https://example.com/two", "A push older than the mirror's tab is ignored." );

		isConnected = false;
		mirror.Report( tab.Id, "mirror-view", "https://example.com/two", "Two", isLoading: false, 100, failed: null, canGoBack: true, canGoForward: false );
		var waiting = mirror.Open( "https://example.org/", SettingsProfile.Work, "mirror-view" );
		CoreChecks.Check( mirror.Find( tab.Id )!.Title == "Two" && mirror.Find( waiting.Id ) is not null && host.Find( waiting.Id ) is null,
			"While disconnected a mirror applies its changes at once and keeps them." );
		isConnected = true;
		var count = sent.Count;
		mirror.Load( host.Tabs );
		CoreChecks.Check( sent.Count == count + 2 && host.Find( waiting.Id ) is not null && host.Find( tab.Id )!.Title == "Two" && mirror.Find( waiting.Id ) is not null,
			"The next snapshot applies and sends again the changes made while disconnected." );

		host.Close( tab.Id );
		CoreChecks.Check( mirror.Find( tab.Id ) is null && other.Find( tab.Id ) is null, "A tab the host closes leaves every mirror." );
		var resets = 0;
		other.Reset += () => resets++;
		other.Load( [] );
		CoreChecks.Check( other.Tabs.Count == 0 && resets == 1 && other.IsSynced, "A snapshot without a tab drops it and tells the views." );
	}

	/// <summary>Verifies the daemon, which has no window, loads the browser module without web views, and its browser tools fail with a clear error until a
	/// window lends its browser, and then run there.</summary>
	private static void CheckHostTools( string folder )
	{
		var host = new LoknApplication { IsHost = true };
		try
		{
			CoreChecks.Check( host.Load( new BrowserModule( folder ) ), "The browser module loads in the daemon." );
			var arguments = JsonSerializer.SerializeToElement( new { url = "https://example.com/" } );
			var refused = host.Tools.InvokeAsync( "browser_navigate", arguments, null, CancellationToken.None ).GetAwaiter().GetResult();
			CoreChecks.Check( refused.IsError && refused.Text == BrowserTabs.NoWindowMessage, $"Without a window, a browser tool says to open Loom ({refused.Text})." );
			var lender = new Lender();
			host.BrowserTabs.Windows = lender;
			var lent = host.Tools.InvokeAsync( "browser_tabs", JsonSerializer.SerializeToElement( new { } ), null, CancellationToken.None ).GetAwaiter().GetResult();
			CoreChecks.Check( !lent.IsError && lent.Text == "ran browser_tabs" && host.Panels.Panels.Count == 0, "With a window lending its browser, the tool runs there and the daemon opens no panel." );
		}
		finally
		{
			host.Shutdown();
		}
	}

	/// <summary>Verifies a followed link opens a browser panel beside its source, later links open new focused tabs in the browser panel on screen, and agent reports reuse their own tab.</summary>
	private static void CheckLinks( LoknApplication app, BrowserModule module )
	{
		app.ClearError();
		CoreChecks.Check( app.Load( new FixtureModule( "source", builder => builder.Panel( "page", "Source", _ => new SourceContent() ) ) ), "A source panel type registers." );
		var source = app.Panels.Open( "source.page" );
		var sourcePane = app.Panels.Panes.Single( pane => pane.PanelId == source ).Id;
		app.OpenLink( new Uri( "https://github.com/a/b/pull/1" ), source );
		var browsers = app.Panels.Panels.Where( panel => panel.TypeId == "browser.page" ).ToArray();
		CoreChecks.Check( browsers.Length == 1 && app.Panels.Panes.Any( pane => pane.Id == sourcePane && pane.PanelId == source ), "A followed link opens a browser panel beside its source, which keeps its pane." );
		CoreChecks.Check( app.Panels.Root is DockSplit { Axis: SplitAxis.Horizontal }, "The browser opens to the side of the source." );

		app.Panels.Activate( source );
		app.OpenLink( new Uri( "https://github.com/a/b/pull/2" ), source );
		CoreChecks.Check( app.Panels.Panels.Count( panel => panel.TypeId == "browser.page" ) == 1 && app.Panels.ActivePanelId == browsers[0].Id, "A later link reuses and focuses the side panel." );
		var saved = Capture( app, browsers[0].Id );
		CoreChecks.Check( saved.GetProperty( "Address" ).GetString() == "https://github.com/a/b/pull/2" && ReadTabs( saved ) is ["https://github.com/a/b/pull/1", "https://github.com/a/b/pull/2"] && saved.GetProperty( "Selected" ).GetInt32() == 1, "A later link opens in a new tab, which is selected." );
		var ids = ReadIds( saved );
		CoreChecks.Check( ids.Length == 2 && ids.All( id => app.BrowserTabs.Find( id ) is not null ) && app.BrowserTabs.Find( ids[1] )!.Url == "https://github.com/a/b/pull/2",
			"The panel's tabs are the session's, and its saved state holds their ids." );

		app.Panels.Close( browsers[0].Id );
		CoreChecks.Check( ids.All( id => app.BrowserTabs.Find( id ) is null ), "In the local role, closing a panel closes its tabs, which no other panel shows." );
		var own = app.Panels.Open( "browser.page" );
		app.Panels.Activate( source );
		app.OpenLink( new Uri( "https://example.com/" ), source );
		var mine = Capture( app, own );
		CoreChecks.Check( app.Panels.Panels.Count( panel => panel.TypeId == "browser.page" ) == 1 && app.Panels.ActivePanelId == own, "A link opens in the browser panel the user opened, and focuses it." );
		CoreChecks.Check( ReadTabs( mine ).Length == 2 && mine.GetProperty( "Address" ).GetString() == "https://example.com/" && mine.GetProperty( "Selected" ).GetInt32() == 1, "The link opens as a new selected tab beside the panel's home page." );

		app.OpenLink( new Uri( "mailto:someone@example.com" ), source );
		CoreChecks.Check( app.Panels.Panels.Count( panel => panel.TypeId == "browser.page" ) == 1 && app.LastError is null, $"Links that are not web pages are left to other handlers ({app.LastError})." );

		// An agent's html report is saved and shown in a new tab of the browser panel beside its conversation.
		var side = own;
		var arguments = JsonDocument.Parse( """{ "html": "<h1>Bench</h1>", "title": "Bench: Results v2!" }""" ).RootElement;
		var result = app.Tools.InvokeAsync( "browser_report", arguments, source, CancellationToken.None ).GetAwaiter().GetResult();
		var report = Directory.GetFiles( module.ReportsPath, "*-bench-results-v2.html" ).OrderBy( file => file ).LastOrDefault();
		var savedReport = report is null ? "" : File.ReadAllText( report );
		CoreChecks.Check( !result.IsError && report is not null && savedReport.EndsWith( "<h1>Bench</h1>", StringComparison.Ordinal ) && result.Text.Contains( report ), $"An html report is saved under a readable name ({result.Text})." );
		CoreChecks.Check( savedReport.StartsWith( """<link rel="stylesheet" href="kit/loom-report.css">""", StringComparison.Ordinal )
			&& File.Exists( Path.Combine( module.KitPath, "loom-report.js" ) ) && File.Exists( Path.Combine( module.KitPath, "fonts", "fraunces.woff2" ) ),
			"An html report links the report kit, which is written beside the reports with its fonts." );
		var state = Capture( app, side );
		CoreChecks.Check( state.GetProperty( "Address" ).GetString() == new Uri( report! ).AbsoluteUri && ReadTabs( state ).Length == 3 && app.Panels.Panels.Count( panel => panel.TypeId == "browser.page" ) == 1, "The report loads in a new tab of the existing browser panel." );
		var again = JsonDocument.Parse( """{ "url": "https://example.com/v3" }""" ).RootElement;
		CoreChecks.Check( !app.Tools.InvokeAsync( "browser_report", again, source, CancellationToken.None ).GetAwaiter().GetResult().IsError, "A second page shows." );
		var replaced = Capture( app, side );
		CoreChecks.Check( replaced.GetProperty( "Address" ).GetString() == "https://example.com/v3" && ReadTabs( replaced ).Length == 3, "The next report replaces the previous report's tab." );

		var both = JsonDocument.Parse( """{ "html": "<p>x</p>", "url": "https://example.com" }""" ).RootElement;
		var missing = JsonDocument.Parse( """{ "path": "C:\\no\\such\\report.html" }""" ).RootElement;
		CoreChecks.Check( app.Tools.InvokeAsync( "browser_report", both, source, CancellationToken.None ).GetAwaiter().GetResult().IsError
			&& app.Tools.InvokeAsync( "browser_report", missing, source, CancellationToken.None ).GetAwaiter().GetResult().IsError, "Ambiguous arguments and missing files are refused." );

		// Tabs no panel here shows, such as another window's: the most recent are listed, the rest counted.
		var loose = Enumerable.Range( 0, 25 ).Select( index => app.BrowserTabs.Open( $"https://loose.example/{index}", SettingsProfile.Personal ).Id ).ToArray();
		var listed = app.Tools.InvokeAsync( "browser_tabs", JsonDocument.Parse( "{}" ).RootElement, source, CancellationToken.None ).GetAwaiter().GetResult();
		CoreChecks.Check( !listed.IsError && listed.Text.Contains( "not open in this window", StringComparison.Ordinal ) && listed.Text.Contains( loose[^1].ToString( "N" )[..8], StringComparison.Ordinal )
			&& listed.Text.Contains( "5 older tabs not open in this window are not listed.", StringComparison.Ordinal ) && listed.Text.Contains( "https://example.com/v3", StringComparison.Ordinal ),
			$"browser_tabs lists the tabs open here and the 20 most recent others, and counts the rest ({listed.Text})." );
		foreach ( var id in loose )
			app.BrowserTabs.Close( id );
		CheckBackground( app );
	}

	/// <summary>Verifies a report from a chat the user is not looking at, in another profile, never lands in the layout on screen, and appears beside that chat when it comes back.</summary>
	private static void CheckBackground( LoknApplication app )
	{
		CoreChecks.Check( app.Load( new FixtureModule( "work", builder => builder.Panel( "chat", "Work chat", _ => new SourceContent( SettingsProfile.Work ) ) ) ), "A Work chat stand-in registers." );
		var chat = app.Panels.Open( "work.chat" );
		var screen = app.Panels.CaptureLayout();
		app.Panels.HideAll();
		var personal = app.Panels.Open( "browser.page" );
		var before = app.Panels.CaptureLayout().GetRawText();
		Guid[] earlier = [.. app.Panels.Panels.Where( panel => panel.TypeId == "browser.page" ).Select( panel => panel.Id )];
		var html = JsonDocument.Parse( """{ "html": "<p>work</p>", "title": "work report" }""" ).RootElement;
		var result = app.Tools.InvokeAsync( "browser_report", html, chat, CancellationToken.None ).GetAwaiter().GetResult();
		var opened = app.Panels.Panels.Where( panel => panel.TypeId == "browser.page" ).Select( panel => panel.Id ).Except( earlier ).ToArray();
		CoreChecks.Check( !result.IsError && opened is [var hidden] && !app.Panels.IsShown( hidden ) && app.Panels.GetWaitingBeside( hidden ) == chat
			&& app.Panels.CaptureLayout().GetRawText() == before && ReadTabs( Capture( app, personal ) ).Length == 1,
			$"A background chat's report opens in its own hidden browser, leaving the layout and browser on screen untouched ({result.Text})." );
		CoreChecks.Check( app.Panels.GetProfile( opened[0] ) == SettingsProfile.Work, "The chat's browser uses the chat's profile." );

		var navigate = JsonDocument.Parse( """{ "url": "https://example.com/work" }""" ).RootElement;
		app.Tools.InvokeAsync( "browser_navigate", navigate, chat, CancellationToken.None ).GetAwaiter().GetResult();
		CoreChecks.Check( ReadTabs( Capture( app, personal ) ).Length == 1, "A background chat's browser tools never drive the browser on screen." );

		static bool IsBeside( DockNode node, Guid left, Guid right ) => node is DockSplit split
			&& (split is { First: DockPane { PanelId: var first }, Second: DockPane { PanelId: var second } } && first == left && second == right
				|| IsBeside( split.First, left, right ) || IsBeside( split.Second, left, right ));
		CoreChecks.Check( app.Panels.ApplyLayout( screen ) && app.Panels.IsShown( chat ) && IsBeside( app.Panels.Root, chat, opened[0] ),
			"When the chat comes back on screen, its browser appears beside it." );
	}

	/// <summary>Verifies two panels showing one session tab follow it: a navigation through one reaches the other's address, and a tab closed in the session
	/// leaves both, which close with their last tab.</summary>
	private static void CheckPanels( LoknApplication app )
	{
		var queue = new List<Action>();
		app.BindDispatcher( action =>
		{
			queue.Add( action );
			return true;
		} );
		app.Panels.HideAll();
		var first = app.Panels.Open( "browser.page", input: new BrowserRequest( "https://example.com/shared", FocusAddress: false ) );
		var shared = ReadIds( Capture( app, first ) ).Single();
		var second = app.Panels.Open( "browser.page", input: new BrowserRequest( null, FocusAddress: false, TabId: shared ) );
		CoreChecks.Check( ReadIds( Capture( app, second ) ) is [var shown] && shown == shared && Capture( app, second ).GetProperty( "Address" ).GetString() == "https://example.com/shared",
			"A panel asked for a session tab shows it with the session's address." );

		// Hidden, the pages do not run, so the tool answers at once after sending the tab elsewhere.
		app.Panels.HideAll();
		var tool = JsonDocument.Parse( $$"""{ "tab": "{{shared.ToString( "N" )[..8]}}", "url": "https://example.com/moved" }""" ).RootElement;
		app.Tools.InvokeAsync( "browser_navigate", tool, null, CancellationToken.None ).GetAwaiter().GetResult();
		CoreChecks.Check( app.BrowserTabs.Find( shared )!.Url == "https://example.com/moved" && Capture( app, second ).GetProperty( "Address" ).GetString() == "https://example.com/moved"
			&& Capture( app, first ).GetProperty( "Address" ).GetString() == "https://example.com/moved",
			"A navigation of the session tab through a tool reaches every panel that shows it." );

		app.BrowserTabs.Close( shared );
		CoreChecks.Check( ReadIds( Capture( app, first ) ).Length == 0 && ReadIds( Capture( app, second ) ).Length == 0, "A tab closed in the session leaves every panel that showed it." );
		foreach ( var action in queue.ToArray() )
			action();
		queue.Clear();
		CoreChecks.Check( app.Panels.Panels.All( panel => panel.Id != first && panel.Id != second ), "Panels whose last tab closed close too." );
		app.BindDispatcher( null );
	}

	/// <summary>Captures a browser panel's saved state.</summary>
	private static JsonElement Capture( LoknApplication app, Guid panelId ) => app.Panels.Sessions.Single( session => session.Info.Id == panelId ).Content!.CaptureState()!.Value;

	/// <summary>Reads the tab addresses from a browser panel's saved state.</summary>
	private static string[] ReadTabs( JsonElement state ) => [.. state.GetProperty( "Tabs" ).EnumerateArray().Select( tab => tab.GetString()! )];

	/// <summary>Reads the session tab ids from a browser panel's saved state.</summary>
	private static Guid[] ReadIds( JsonElement state ) => [.. state.GetProperty( "Ids" ).EnumerateArray().Select( tab => tab.GetGuid() )];

	/// <summary>Deletes a temporary folder, leaving it when a file is still in use.</summary>
	private static void TryDelete( string folder )
	{
		try
		{
			Directory.Delete( folder, recursive: true );
		}
		catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
		{
		}
	}

	/// <summary>Stands in for a window that lends its browser to the daemon.</summary>
	private sealed class Lender : IBrowserWindows
	{
		/// <summary>Answers with the tool it ran.</summary>
		public Task<AgentToolResult>? TryRun( AgentToolContext context, string tool, JsonElement arguments ) => Task.FromResult( new AgentToolResult( $"ran {tool}" ) );
	}

	/// <summary>Stands in for a panel links are followed in, such as a chat, optionally pinned to a profile.</summary>
	private sealed class SourceContent( SettingsProfile? profile = null ) : PanelContent
	{
		/// <summary>Gets the profile the stand-in works in.</summary>
		public override SettingsProfile? Profile => profile;

		/// <summary>Builds an empty view.</summary>
		public override Goo.Blob Build() => new Goo.Container();
	}
}
