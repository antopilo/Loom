using Lokn.Core;
using Lokn.Modules.Browser;

namespace Lokn.Tests;

/// <summary>Checks how windows keep a session tab in step, without WebView2 and without a clock: mirrors of an authority over a transport the check
/// delivers by hand in the order it chooses, and stand-in pages over <see cref="BrowserTabSync"/> whose cookies redirect differently.</summary>
internal static class BrowserSyncChecks
{
	/// <summary>Defines the first address of the checks' pages.</summary>
	private const string Home = "https://site.example/";

	/// <summary>Defines a page a signed-in window shows and a signed-out one is redirected away from.</summary>
	private const string Settings = "https://site.example/settings";

	/// <summary>Defines the sign-in page a signed-out window is redirected to, which sends a signed-in one back.</summary>
	private const string Login = "https://site.example/login?return_to=%2Fsettings";

	/// <summary>Runs the checks.</summary>
	internal static void Run()
	{
		CheckEcho();
		CheckStaleEcho( isAnswerFirst: false );
		CheckStaleEcho( isAnswerFirst: true );
		CheckRedirects();
		CheckUncommitted();
		CheckStateReports();
		CheckUnsent();
		Console.WriteLine( "PASS browser sync: a mirror's own changes are answered with the host's revision and pushes held meanwhile, so an older echo never navigates a page back in either delivery order; loads that follow another window, with their redirects, never move the tab, so windows whose cookies redirect differently settle; a navigation that never commits returns the tab to the page shown; state reports of a left address change nothing; and changes kept while disconnected are collapsed, bounded and dropped when stale." );
	}

	/// <summary>Verifies the plain round trip: a mirror's new tab and navigation reach the host and the other mirror, and the mirror ends with the host's
	/// tab and revision; an older push changes nothing.</summary>
	private static void CheckEcho()
	{
		var rig = new Rig();
		var mirror = rig.Add();
		var other = rig.Add();
		var tab = mirror.Tabs.Open( Home, SettingsProfile.Personal, "mirror-view" );
		mirror.Tabs.Navigate( tab.Id, Settings, "mirror-view" );
		rig.Settle();
		var hosted = rig.Host.Find( tab.Id )!;
		CoreChecks.Check( hosted is { Rev: 2, Url: Settings } && mirror.Tabs.Find( tab.Id ) == hosted && other.Tabs.Find( tab.Id ) == hosted,
			"A mirror's tab and navigation reach the host, and both mirrors end with the host's tab and revision." );
		mirror.Tabs.ApplyTab( hosted with { Rev = 1, Url = "https://stale.example/" } );
		CoreChecks.Check( mirror.Tabs.Find( tab.Id )!.Url == Settings, "A push older than the mirror's tab is ignored." );
	}

	/// <summary>Verifies the race of a link clicked in a window that follows another: the page's loading report still names the old address, its commit
	/// the new one, and the host's pushes of both arrive after the commit, before or after the answers; the page is never sent back.</summary>
	private static void CheckStaleEcho( bool isAnswerFirst )
	{
		var rig = new Rig { IsAnswerFirst = isAnswerFirst };
		var mirror = rig.Add();
		var tab = rig.Host.Open( Home, SettingsProfile.Personal, "other-window" );
		rig.Settle();
		var page = new Page( mirror, tab.Id );
		page.Start();
		rig.Settle();
		CoreChecks.Check( page.Address == Home && rig.Host.Find( tab.Id )!.By == "other-window", "A window shows the tab another window set without taking it over." );

		// The link's loading report goes out before the commit, and the host answers only after both.
		page.Begin( Settings );
		page.Finish();
		CoreChecks.Check( rig.Pending >= 2 && mirror.Tabs.Find( tab.Id ) is { Url: Settings } && page.Address == Settings, "The click applies at once in the window." );
		while ( rig.Pending > 0 )
			rig.DeliverToHost();
		mirror.DeliverAll();
		rig.Settle();
		var order = isAnswerFirst ? "answers first" : "pushes first";
		CoreChecks.Check( page.Follows == 0 && page.Address == Settings, $"The host's push of the page's earlier report never sends it back to the old address ({order})." );
		CoreChecks.Check( mirror.Tabs.Find( tab.Id ) is { Url: Settings } mirrored && mirrored.By == page.ViewId && mirrored == rig.Host.Find( tab.Id ),
			$"The mirror ends with the host's tab, at the clicked address, set by the window ({order})." );
	}

	/// <summary>Verifies two windows whose cookies redirect differently: the signed-out one follows to the sign-in page, which never moves the tab, so the
	/// signed-in one stays and neither loops; the follower can still lead with its own click.</summary>
	private static void CheckRedirects()
	{
		var rig = new Rig();
		var host = rig.Add();
		var paired = rig.Add();
		var id = Guid.NewGuid();
		var signedIn = new Page( host, id, url => url == Login ? Settings : null );
		host.Tabs.Open( Home, SettingsProfile.Personal, signedIn.ViewId, id );
		signedIn.Start();
		rig.Settle();
		var signedOut = new Page( paired, id, url => url == Settings ? Login : null );
		signedOut.Start();
		rig.Settle();

		signedIn.Type( Settings );
		rig.Settle();
		CoreChecks.Check( signedOut.Address == Login && signedIn.Address == Settings && signedIn.Follows == 0,
			"The signed-out window follows and is redirected to sign in, and the signed-in window is not pulled there." );
		CoreChecks.Check( rig.Host.Find( id ) is { Url: Settings } hosted && hosted.By == signedIn.ViewId, "A followed load's redirect leaves the tab at the address the leading window set." );
		var follows = signedOut.Follows;
		for ( var tick = 0; tick < 5; tick++ )
			host.Tabs.Report( id, signedIn.ViewId, Settings, $"Settings {tick}", isLoading: false, 100, failed: null, canGoBack: false, canGoForward: false );
		rig.Settle();
		CoreChecks.Check( follows == 1 && signedOut.Follows == follows && signedOut.Address == Login,
			$"The redirected window loads the leading window's address once, not again at each of its title changes ({follows}, then {signedOut.Follows})." );

		var late = new Page( paired, id, url => url == Settings ? Login : null );
		late.Start();
		rig.Settle();
		CoreChecks.Check( late.Address == Login && rig.Host.Find( id )!.Url == Settings && signedIn.Follows == 0, "Opening the tab in another signed-out window pulls no window to the sign-in page." );

		signedOut.Click( Home );
		rig.Settle();
		CoreChecks.Check( rig.Host.Find( id ) is { Url: Home } led && led.By == signedOut.ViewId && signedIn.Address == Home, "A click in the following window still leads every window." );
	}

	/// <summary>Verifies a navigation of the window's own that never replaces the page, such as a download: the tab returns to the page still shown and
	/// stops loading, rather than staying on the download for every window and restart.</summary>
	private static void CheckUncommitted()
	{
		var rig = new Rig();
		var mirror = rig.Add();
		var id = Guid.NewGuid();
		const string download = "https://site.example/report.zip";
		var page = new Page( mirror, id, downloads: download );
		mirror.Tabs.Open( Home, SettingsProfile.Personal, page.ViewId, id );
		page.Start();
		rig.Settle();
		page.Type( download );
		rig.Settle();
		CoreChecks.Check( page.Address == Home && rig.Host.Find( id ) is { Url: Home, Loading: false } && mirror.Tabs.Find( id ) is { Url: Home, Loading: false },
			"A navigation that never commits leaves the tab on the page still shown, not loading." );
	}

	/// <summary>Verifies the authority keeps state reports to the address they describe: one of an address the tab left changes nothing, and only a
	/// navigated report moves the tab and names its view.</summary>
	private static void CheckStateReports()
	{
		var host = new BrowserTabs();
		var tab = host.Open( Settings, SettingsProfile.Personal, "leader" );
		var rev = host.Apply( new BrowserOp { Op = BrowserOp.ReportKind, Tab = tab.Id, By = "lagging", Url = Home, Title = "Old page", Loading = false, Progress = 100 } );
		CoreChecks.Check( rev == tab.Rev && host.Find( tab.Id ) is { Url: Settings, By: "leader", Title: "" }, "A state report of an address the tab left changes nothing." );
		host.Apply( new BrowserOp { Op = BrowserOp.ReportKind, Tab = tab.Id, By = "clicker", Url = Login, Title = "Sign in", Loading = false, Progress = 100, Navigated = true } );
		CoreChecks.Check( host.Find( tab.Id ) is { Url: Login, By: "clicker", Title: "Sign in" } moved && moved.Rev == tab.Rev + 1, "A navigated report moves the tab and names its view." );
	}

	/// <summary>Verifies changes kept while disconnected: one report per tab, a stale one dropped after the host moved the tab, an open and close made
	/// offline sent as nothing, and past the limit the oldest openings dropped with their tabs' other changes, so every change sent applies.</summary>
	private static void CheckUnsent()
	{
		var rig = new Rig();
		var mirror = rig.Add();
		var other = rig.Add();
		var tab = mirror.Tabs.Open( Home, SettingsProfile.Personal, "mirror-view" );
		var moved = mirror.Tabs.Open( Home, SettingsProfile.Personal, "mirror-view" );
		rig.Settle();

		mirror.IsConnected = false;
		for ( var progress = 10; progress <= 100; progress += 30 )
			mirror.Tabs.Report( tab.Id, "mirror-view", Home, $"At {progress}", isLoading: progress < 100, progress, failed: null, canGoBack: false, canGoForward: false );
		mirror.Tabs.Report( moved.Id, "mirror-view", Home, "Stale", isLoading: false, 100, failed: null, canGoBack: false, canGoForward: false );
		var offline = mirror.Tabs.Open( "https://offline.example/", SettingsProfile.Personal, "mirror-view" );
		mirror.Tabs.Close( offline.Id );
		other.Tabs.Navigate( moved.Id, Settings, "other-view" );
		rig.Settle();
		mirror.IsConnected = true;
		mirror.Tabs.Load( rig.Host.Tabs );
		CoreChecks.Check( rig.Pending == 1 && mirror.Tabs.Find( moved.Id ) is { Url: Settings, Title: "" } && mirror.Tabs.Find( offline.Id ) is null,
			$"After reconnecting, a tab's reports go as one, a report of a tab the host moved is dropped, and a tab opened and closed offline sends nothing ({rig.Pending} sent)." );
		rig.Settle();
		CoreChecks.Check( rig.Host.Find( tab.Id ) is { Title: "At 100", Loading: false } && rig.Host.Find( moved.Id ) is { Url: Settings, Title: "" },
			"The latest report applies and the host's newer address stands." );

		mirror.IsConnected = false;
		var opened = new List<Guid>();
		for ( var index = 0; index < BrowserTabs.MaxUnsent + 10; index++ )
		{
			var added = mirror.Tabs.Open( $"https://many.example/{index}", SettingsProfile.Personal, "mirror-view" );
			mirror.Tabs.Report( added.Id, "mirror-view", added.Url, $"Tab {index}", isLoading: false, 100, failed: null, canGoBack: false, canGoForward: false );
			opened.Add( added.Id );
		}
		mirror.IsConnected = true;
		var before = rig.Host.Tabs.Count;
		mirror.Tabs.Load( rig.Host.Tabs );
		rig.Settle();
		var reached = opened.Count( id => rig.Host.Find( id ) is not null );
		CoreChecks.Check( reached > 0 && reached < opened.Count && rig.Host.Tabs.Count == before + reached && opened.Where( id => rig.Host.Find( id ) is not null ).All( id => rig.Host.Find( id )!.Title.Length > 0 )
			&& rig.Host.Find( opened[^1] ) is not null && rig.Host.Find( opened[0] ) is null,
			$"Past the limit the oldest offline tabs are dropped whole, and every tab that reaches the host comes with its report ({reached} of {opened.Count})." );
	}

	/// <summary>An authority and its mirrors over a transport delivered by hand: changes wait for <see cref="DeliverToHost"/>, pushes and answers in each
	/// mirror's inbox.</summary>
	private sealed class Rig
	{
		/// <summary>Holds the changes sent to the host, in order.</summary>
		private readonly Queue<(Mirror From, BrowserOp Op)> _toHost = new();

		/// <summary>Creates the authority, which pushes every change to the inbox of every connected mirror.</summary>
		internal Rig()
		{
			Host.TabChanged += ( id, tab ) =>
			{
				foreach ( var mirror in Mirrors.Where( mirror => mirror.IsConnected ) )
					mirror.Inbox.Add( () =>
					{
						if ( tab is null )
							mirror.Tabs.ApplyClosed( id );
						else
							mirror.Tabs.ApplyTab( tab );
					} );
			};
		}

		/// <summary>Gets the authority.</summary>
		internal BrowserTabs Host { get; } = new();

		/// <summary>Gets the mirrors.</summary>
		internal List<Mirror> Mirrors { get; } = [];

		/// <summary>Gets or sets whether a change's answer reaches its sender before the pushes it caused.</summary>
		internal bool IsAnswerFirst { get; init; }

		/// <summary>Gets how many changes wait for the host.</summary>
		internal int Pending => _toHost.Count;

		/// <summary>Adds a mirror synced with the host.</summary>
		internal Mirror Add()
		{
			var mirror = new Mirror();
			mirror.Tabs.BeginMirror( op =>
			{
				if ( !mirror.IsConnected )
					return false;
				_toHost.Enqueue( (mirror, op) );
				return true;
			}, isRemote: false );
			mirror.Tabs.Load( Host.Tabs );
			Mirrors.Add( mirror );
			return mirror;
		}

		/// <summary>Delivers the oldest change to the host; its pushes reach every inbox, and its answer the sender's after them or, with
		/// <see cref="IsAnswerFirst"/>, before them.</summary>
		internal void DeliverToHost()
		{
			var (from, op) = _toHost.Dequeue();
			var position = from.Inbox.Count;
			long? rev;
			try
			{
				rev = Host.Apply( op );
			}
			catch ( ArgumentException )
			{
				rev = null;
			}
			void Answer() => from.Tabs.Answer( op, rev );
			if ( IsAnswerFirst )
				from.Inbox.Insert( position, Answer );
			else
				from.Inbox.Add( Answer );
		}

		/// <summary>Delivers everything, round trip by round trip, until nothing is in flight, failing past a bound that windows navigating each other back
		/// and forth would pass.</summary>
		internal void Settle()
		{
			for ( var trips = 0; _toHost.Count > 0 || Mirrors.Any( mirror => mirror.Inbox.Count > 0 ); trips++ )
			{
				CoreChecks.Check( trips < 50, "The windows settle instead of navigating each other back and forth." );
				while ( _toHost.Count > 0 )
					DeliverToHost();
				foreach ( var mirror in Mirrors )
					mirror.DeliverAll();
			}
		}
	}

	/// <summary>A mirror of the host's tabs with the inbox of what the host sent it and its pages' events.</summary>
	private sealed class Mirror
	{
		/// <summary>Gets the mirrored tabs.</summary>
		internal BrowserTabs Tabs { get; } = new();

		/// <summary>Gets what waits to run on the mirror's UI thread, in order: pushes, answers and page events.</summary>
		internal List<Action> Inbox { get; } = [];

		/// <summary>Gets or sets whether changes can be sent.</summary>
		internal bool IsConnected { get; set; } = true;

		/// <summary>Runs the inbox until it is empty.</summary>
		internal void DeliverAll()
		{
			while ( Inbox.Count > 0 )
			{
				var next = Inbox[0];
				Inbox.RemoveAt( 0 );
				next();
			}
		}
	}

	/// <summary>Stands in for a window's WebView2 page: navigations raise NavigationStarting, redirects, the commit and completion in WebView2's order,
	/// later on the mirror's thread when the page follows the session, as a real page's events come.</summary>
	private sealed class Page
	{
		/// <summary>Stores the view's decisions.</summary>
		private readonly BrowserTabSync _sync;

		/// <summary>Stores the mirror whose inbox runs the page's later events.</summary>
		private readonly Mirror _mirror;

		/// <summary>Stores where this page's cookies redirect an address, or null for none.</summary>
		private readonly Func<string, string?> _redirect;

		/// <summary>Stores the addresses that download rather than replace the page.</summary>
		private readonly HashSet<string> _downloads;

		/// <summary>Stores the address the current navigation goes to, until it finishes.</summary>
		private string? _target;

		/// <summary>Creates the page of a view of a session tab in a mirror, following the tab's changes.</summary>
		internal Page( Mirror mirror, Guid id, Func<string, string?>? redirect = null, params string[] downloads )
		{
			_mirror = mirror;
			_sync = new( mirror.Tabs, id );
			_redirect = redirect ?? (_ => null);
			_downloads = [.. downloads];
			mirror.Tabs.TabChanged += ( changed, info ) =>
			{
				if ( changed == id && info is not null )
					Follow();
			};
			mirror.Tabs.Reset += Follow;
		}

		/// <summary>Gets the view's identity.</summary>
		internal string ViewId => _sync.ViewId;

		/// <summary>Gets the address the page shows.</summary>
		internal string Address { get; private set; } = "";

		/// <summary>Gets how many loads the page started to follow another view.</summary>
		internal int Follows { get; private set; }

		/// <summary>Starts the page on the tab's address.</summary>
		internal void Start() => Navigate( _sync.ComputeLoad( "", isStarting: true ) ?? "about:blank" );

		/// <summary>Sends the tab to a typed address and loads it.</summary>
		internal void Type( string url )
		{
			_sync.Navigate( url );
			Navigate( url );
		}

		/// <summary>Follows a link the user clicked in the page.</summary>
		internal void Click( string url ) => Navigate( url );

		/// <summary>Starts a navigation the user or this window started, as NavigationStarting: the page still shows its address and reports loading.</summary>
		internal void Begin( string url )
		{
			_sync.BeginNavigation( url, isRedirected: false, isUserInitiated: true );
			_target = url;
			Report( BrowserPageEvent.State, isLoading: true );
		}

		/// <summary>Ends the current navigation: its redirects, then the commit and completion, or only the completion of a download.</summary>
		internal void Finish()
		{
			if ( _target is not { } url )
				return;

			_target = null;
			if ( !_downloads.Contains( url ) )
			{
				for ( var hop = 0; hop < 5 && _redirect( url ) is { } next; hop++ )
				{
					_sync.BeginNavigation( next, isRedirected: true, isUserInitiated: false );
					url = next;
				}
				Address = url;
				Report( BrowserPageEvent.Commit, isLoading: true );
			}
			Report( BrowserPageEvent.Complete, isLoading: false );
		}

		/// <summary>Starts and ends a navigation.</summary>
		private void Navigate( string url )
		{
			Begin( url );
			Finish();
		}

		/// <summary>Loads the address another view set, later on the mirror's thread, as WebView2 raises a load's events after the call.</summary>
		private void Follow()
		{
			if ( Address.Length == 0 || _sync.ComputeLoad( Address, isStarting: false ) is not { } url )
				return;

			Follows++;
			_mirror.Inbox.Add( () => Navigate( url ) );
		}

		/// <summary>Reports what the page shows.</summary>
		private void Report( BrowserPageEvent happened, bool isLoading ) =>
			_sync.Report( new( Address, Address.Length == 0 ? "" : $"Title of {Address}", isLoading, isLoading ? 10 : 100, null, false, false ), happened );
	}
}
