using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Goo;
using Lokn.Core;
using Lokn.Modules.Browser;
using Lokn.Modules.Conversations;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;
using Lokn.Modules.Remote;
using Lokn.Modules.Workspaces;

namespace Lokn.Tests;

/// <summary>Checks browser tabs in native Goo windows with real WebView2 pages: two Loom windows in the daemon role against an in-process gateway show the
/// same session tab, a navigation typed in one's address bar reaches the other's page, address and title, and closing both and opening one again restores the tab.</summary>
/// <remarks>Every application keeps its settings and stores in memory and its files, WebView2 data included, in a temporary folder; the pages are local
/// files. The windows reach the gateway over in-memory connections, never this PC's daemon.</remarks>
internal static class BrowserUiChecks
{
	/// <summary>Defines the frame count used to settle layout and accessibility updates.</summary>
	private const int SettleFrames = 4;

	/// <summary>Defines how long one step may take; starting WebView2 takes a few seconds.</summary>
	private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds( 45 );

	/// <summary>Runs the checks, failing when a view faulted; on a single-threaded apartment thread, which WebView2 needs.</summary>
	internal static void Run()
	{
		Application.VerifyState = true;
		var faults = new List<string>();
		void Record( Type view, Exception error ) => faults.Add( $"{view.Name}: {error.Message}" );
		CellFaults.Faulted += Record;
		var folder = Path.Combine( Path.GetTempPath(), "lokn-browser-ui-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( folder );
		var one = Path.Combine( folder, "one.html" );
		var two = Path.Combine( folder, "two.html" );
		File.WriteAllText( one, "<!doctype html><title>Page one</title><h1>One</h1>" );
		File.WriteAllText( two, "<!doctype html><title>Page two</title><h1>Two</h1>" );
		var twoUrl = new Uri( two ).AbsoluteUri;
		var hostQueue = new List<Action>();
		var host = new LoknApplication { IsHost = true };
		host.BindDispatcher( action =>
		{
			lock ( hostQueue )
				hostQueue.Add( action );
			return true;
		} );
		try
		{
			LoadOmp( host );
			var hostStore = new ConversationStore( new ProjectStore() );
			var hostRemote = new RemoteModule( new RemoteHooks { DataFolder = Path.Combine( folder, "host" ) } );
			CoreChecks.Check( ProjectChecks.LoadProjects( host, hostStore ) && host.Load( new WorkspacesModule( Path.Combine( folder, "workspaces.json" ) ) )
				&& host.Load( new ConversationsModule( hostStore, ConversationsModule.BuildChat ) ) && host.Load( new BrowserModule( Path.Combine( folder, "host-browser" ) ) )
				&& host.Load( hostRemote ) && hostRemote.Gateway is not null,
				"The host loads its stores and Browser and starts the gateway." );
			var gateway = hostRemote.Gateway!;

			var first = OpenWindow( folder, "first", hostQueue, gateway, isRestored: false );
			first.PumpUntil( () => first.Module.Daemon!.Status == ClientState.Connected && first.App.BrowserTabs.IsSynced, "The first window connects to the daemon." );
			var firstPanel = first.App.Panels.Open( "browser.page", input: new BrowserRequest( one, FocusAddress: false ) );
			var tabId = first.Browser( firstPanel ).Selected!.Id;
			var firstTab = first.Browser( firstPanel ).Selected!;
			first.PumpUntil( () => host.BrowserTabs.Find( tabId ) is { Title: "Page one" } && first.HasButton( "Page one (selected tab)" ),
				"The first window's page loads and its title reaches the daemon's session.",
				() => $"page started {firstTab.Started.IsCompleted}, running {firstTab.Controller is not null}, failure '{firstTab.Failure}', session {first.App.BrowserTabs.Find( tabId )}, host {host.BrowserTabs.Find( tabId )}" );

			var second = OpenWindow( folder, "second", hostQueue, gateway, isRestored: false );
			Screen.PumpAllUntil( [first, second], () => second.Module.Daemon!.Status == ClientState.Connected && second.App.BrowserTabs.Find( tabId ) is not null,
				"The second window receives the session tab." );
			var secondPanel = second.App.Panels.Open( "browser.page", input: new BrowserRequest( null, FocusAddress: false, TabId: tabId ) );
			Screen.PumpAllUntil( [first, second], () => second.HasButton( "Page one (selected tab)" ) && second.Browser( secondPanel ).Selected!.Controller is not null,
				"The second window shows the same tab with its own page." );

			// The second window took the system focus from the first; Screen.Type focuses the address bar first, as a click activating the window would.
			first.Type( "Address", twoUrl );
			first.Press( Key.Enter );
			Screen.PumpAllUntil( [first, second], () => second.Browser( secondPanel ).Selected!.PageSource == twoUrl && second.HasButton( "Page two (selected tab)" )
				&& second.Has( AccessibilityRole.TextInput, "Address", twoUrl ) && first.HasButton( "Page two (selected tab)" ) && host.BrowserTabs.Find( tabId ) is { Title: "Page two" },
				"A navigation typed in the first window's address bar loads in the second window's page, with its address and title, and both windows and the daemon's session show it.",
				() => $"first tab {first.App.BrowserTabs.Find( tabId )}, first page {first.Browser( firstPanel ).Selected!.PageSource}, host {host.BrowserTabs.Find( tabId )}" );
			CoreChecks.Check( first.Browser( firstPanel ).Selected!.PageSource == twoUrl && host.BrowserTabs.Find( tabId ) is { Url: var url } shown && url == twoUrl && shown.By == firstTab.ViewId,
				$"The daemon's session holds the typed address, set by the first window (first page {first.Browser( firstPanel ).Selected!.PageSource}, host {host.BrowserTabs.Find( tabId )}, first view {firstTab.ViewId}, second view {second.Browser( secondPanel ).Selected!.ViewId})." );

			first.Close();
			second.Close();
			CoreChecks.Check( host.BrowserTabs.Find( tabId ) is { Url: var kept } && kept == twoUrl, "The session keeps the tab while no window is open." );

			var again = OpenWindow( folder, "first", hostQueue, gateway, isRestored: true );
			again.PumpUntil( () => again.HasButton( "Page two (selected tab)" ) && again.App.Panels.Panels.Any( panel => panel.TypeId == "browser.page" )
				&& again.Browser( again.App.Panels.Panels.First( panel => panel.TypeId == "browser.page" ).Id ).Selected is { Id: var restored, PageSource: var source } && restored == tabId && source == twoUrl,
				"Opening the window again restores its browser panel with the session tab and loads its page." );
			CoreChecks.Check( again.App.LastError is null && host.LastError is null, $"No window and not the daemon reported a failure ({again.App.LastError}; {host.LastError})." );
			again.Close();
			CheckPairedPage( folder, hostQueue, gateway );
			CoreChecks.Check( faults.Count == 0, "No view faulted: " + string.Join( "; ", faults ) );
		}
		finally
		{
			CellFaults.Faulted -= Record;
			host.Shutdown();
		}

		TryDelete( folder );
		Console.WriteLine( "PASS native browser tabs: two windows on the daemon show the same session tab with their own pages; a navigation typed in one window's address bar and submitted with Enter reaches the other's page, address and title; the tab survives both windows closing and comes back when one opens again; a paired PC's WebView2 loads the host's localhost page through its proxy and tunnel, and fails rather than reach its own loopback once the tunnel is refused." );
	}

	/// <summary>Checks a paired PC's window with a real WebView2: a browser panel on the paired host shows a session tab at the host's localhost, loaded through
	/// that host's proxy, tunnel and own WebView2 data folder; once the host lets the PC only view, which refuses the tunnel, reloading it fails rather than
	/// reach this PC's own loopback past the proxy.</summary>
	private static void CheckPairedPage( string folder, List<Action> hostQueue, HostGateway gateway )
	{
		var web = new TcpListener( IPAddress.Loopback, 0 );
		web.Start();
		_ = ServeAsync( web );
		try
		{
			var hostPage = $"http://localhost:{((IPEndPoint)web.LocalEndpoint).Port}/";
			var deviceId = Guid.NewGuid().ToString( "N" );
			using ( var keys = RemoteKeys.Create() )
				gateway.Devices.Save( new PairedDevice { DeviceId = deviceId, Name = "Laptop", DeviceKey = keys.PublicKeyText, Scopes = DeviceScopes.Controlling, Paired = DateTimeOffset.UtcNow } );
			var paired = OpenWindow( folder, "paired", hostQueue, gateway, isRestored: false, deviceId );
			var hostId = gateway.Keys.HostId;
			var desk = paired.App.Hosts.Get( hostId );
			paired.PumpUntil( () => paired.Module.FindLink( hostId ) is { Status: ClientState.Connected } && desk.BrowserTabs.IsSynced && desk.BrowserTabs.ProxyServer is not null,
				"The paired PC's window connects to the host." );
			var panel = paired.App.Panels.Open( "browser.page", input: new BrowserRequest( hostPage, FocusAddress: false ), hostId: hostId );
			var tab = paired.Browser( panel ).Selected!;
			CoreChecks.Check( paired.Browser( panel ).Session == desk.BrowserTabs && !paired.App.Hosts.Local.BrowserTabs.Tabs.Any( info => info.Id == tab.Id ),
				"The browser panel on the paired host shows that host's tabs, not This PC's." );
			paired.PumpUntil( () => paired.HasButton( "Host page (selected tab)" ) && tab.LastLoad == "loaded", "The paired PC loads the host's localhost page through its proxy and the tunnel.",
				() => $"load '{tab.LastLoad}', failure '{tab.Failure}', page {tab.PageSource}, proxy {desk.BrowserTabs.ProxyServer}" );
			CoreChecks.Check( Directory.Exists( BrowserModule.ComputeDataPath( Path.Combine( folder, "paired-browser" ), hostId ) ),
				"The paired host's pages keep their WebView2 data in the host's own folder." );

			gateway.Devices.SetScopes( deviceId, DeviceScopes.Paired );
			gateway.CloseDevice( deviceId );
			paired.PumpUntil( () => paired.Module.FindLink( hostId ) is { Status: ClientState.Connected } link && !link.Scopes.Contains( DeviceScopes.Control ),
				"The paired PC reconnects with view only, which refuses the tunnel." );
			tab.Controller!.CoreWebView2.Reload();
			paired.PumpUntil( () => tab.LastLoad.StartsWith( "failed", StringComparison.Ordinal ), "Reloading the host's localhost page fails rather than load this PC's own loopback service.",
				() => $"load '{tab.LastLoad}', page {tab.PageSource}" );
			paired.Close();
		}
		finally
		{
			web.Stop();
		}
	}

	/// <summary>Answers every request with a page titled "Host page", standing in for a dev server on the host's localhost.</summary>
	private static async Task ServeAsync( TcpListener listener )
	{
		while ( true )
		{
			TcpClient client;
			try
			{
				client = await listener.AcceptTcpClientAsync();
			}
			catch ( Exception exception ) when ( exception is SocketException or ObjectDisposedException or InvalidOperationException )
			{
				return;
			}
			_ = Task.Run( async () =>
			{
				using ( client )
				{
					try
					{
						var stream = client.GetStream();
						var buffer = new byte[8192];
						var request = new StringBuilder();
						while ( !request.ToString().Contains( "\r\n\r\n", StringComparison.Ordinal ) )
						{
							var count = await stream.ReadAsync( buffer );
							if ( count == 0 )
								return;
							request.Append( Encoding.ASCII.GetString( buffer, 0, count ) );
						}
						const string body = "<!doctype html><title>Host page</title><h1>Host</h1>";
						await stream.WriteAsync( Encoding.ASCII.GetBytes( $"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nCache-Control: no-store\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}" ) );
					}
					catch ( Exception exception ) when ( exception is IOException or SocketException or ObjectDisposedException )
					{
					}
				}
			} );
		}
	}

	/// <summary>Opens a window whose dialer hands the gateway the other end of an in-memory pair: in the <c>daemon</c> role as Loom on this PC, or with
	/// <paramref name="deviceId"/> in the <c>local</c> role with the gateway listed as a paired host, as that paired device with its
	/// current scopes; its layout is in a file of the folder, restored when <paramref name="isRestored"/>.</summary>
	private static Screen OpenWindow( string folder, string name, List<Action> hostQueue, HostGateway gateway, bool isRestored, string? deviceId = null )
	{
		var app = new LoknApplication( null, Path.Combine( folder, name + "-workspace.json" ) );
		var dataFolder = Path.Combine( folder, name );
		if ( deviceId is not null )
		{
			Directory.CreateDirectory( dataFolder );
			PairedHosts.Save( dataFolder, [new PairedHost( gateway.Keys.HostId, gateway.Keys.PublicKeyText, [], null, deviceId, "Desk", DateTimeOffset.UtcNow )] );
		}

		var module = new RemoteModule( new RemoteHooks
		{
			DataFolder = dataFolder,
			Role = deviceId is null ? RemoteModule.DaemonRole : RemoteModule.LocalRole,
			Dial = ( hostId, cancellationToken ) =>
			{
				var (hostEnd, clientEnd) = MemoryConnection.CreatePair();
				var scopes = deviceId is null ? DeviceScopes.Controlling : gateway.Devices.Find( deviceId )!.Scopes;
				_ = gateway.RunConnectionAsync( hostEnd, deviceId ?? HostGateway.LocalDevice, deviceId is null ? HostGateway.LocalName : "Laptop", scopes );
				return Task.FromResult( new ClientCarrier( clientEnd, scopes, deviceId is null ? "pipe" : "relay" ) );
			},
		} );
		return new Screen( app, module, hostQueue, () =>
		{
			CoreChecks.Check( app.Load( new ShellModule() ), "The shell loads." );
			LoadOmp( app );
			var store = new ConversationStore( new ProjectStore() );
			CoreChecks.Check( ProjectChecks.LoadProjects( app, store ) && app.Load( new ConversationsModule( store, ConversationsModule.BuildChat ) )
				&& app.Load( new BrowserModule( Path.Combine( folder, name + "-browser" ) ) ) && app.Load( module )
				&& (deviceId is null ? module.Daemon is not null : module.FindLink( gateway.Keys.HostId ) is not null),
				"A window loads its stores, Browser and its link." );
			if ( isRestored )
				CoreChecks.Check( app.RestoreWorkspace(), "The window restores its saved layout." );
		} );
	}

	/// <summary>Loads Omp with the fixture (this executable) as its executable, so no real agent starts.</summary>
	private static void LoadOmp( LoknApplication app )
	{
		CoreChecks.Check( app.Load( new OmpModule() ), "Omp loads." );
		app.Settings.Set( OmpModule.Executable, SettingsScope.Global, Path.Combine( AppContext.BaseDirectory, "Lokn.Tests.exe" ) );
	}

	/// <summary>Enumerates a semantic subtree.</summary>
	private static IEnumerable<AccessibilityNode> Flatten( AccessibilityNode node )
	{
		yield return node;
		foreach ( var child in node.Children )
		{
			foreach ( var descendant in Flatten( child ) )
				yield return descendant;
		}
	}

	/// <summary>Deletes a temporary folder, leaving it when a file is still in use, such as by a WebView2 process that is still closing.</summary>
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

	/// <summary>One native window over an application, which also runs the host's queued work between frames.</summary>
	private sealed class Screen
	{
		/// <summary>Stores the host application's queued work, run on this thread as its UI thread.</summary>
		private readonly List<Action> _host;

		/// <summary>Stores the window's accessibility tree.</summary>
		private readonly SemanticAdapter _semantics = new();

		/// <summary>Creates the window over the application, binds the application's dispatcher to it, runs <paramref name="load"/>, then opens the window.</summary>
		public Screen( LoknApplication app, RemoteModule module, List<Action> host, Action load )
		{
			App = app;
			Module = module;
			_host = host;
			Window = new Window
			{
				Title = "Lokn browser checks",
				Size = new( 900, 700 ),
				Decorated = false,
				Transparent = true,
				Background = Theme.Current.Canvas,
				Foreground = Theme.Current.Text,
				Root = new ShellView( app ) { AppliesNativeWindowHooks = false },
				AccessibilityAdapter = _semantics,
			};
			app.BindDispatcher( Window.Post );
			app.Commands.BindDispatcher( operation => Window.InvokeAsync( operation ) );
			load();
			Window.Open();
			Pump();
		}

		/// <summary>Gets the application.</summary>
		public LoknApplication App { get; }

		/// <summary>Gets the Remote module.</summary>
		public RemoteModule Module { get; }

		/// <summary>Gets the native window.</summary>
		public Window Window { get; }

		/// <summary>Gets the semantic nodes in source order.</summary>
		public IEnumerable<AccessibilityNode> Nodes => _semantics.Tree?.Root is { } root ? Flatten( root ) : [];

		/// <summary>Gets a browser panel's content.</summary>
		public BrowserContent Browser( Guid panelId ) => (BrowserContent)App.Panels.Sessions.Single( session => session.Info.Id == panelId ).Content!;

		/// <summary>Determines whether a node has the role and exact name, and the value when given.</summary>
		public bool Has( AccessibilityRole role, string name, string? value = null ) => Nodes.Any( node => node.Role == role && node.Name == name && (value is null || node.Value == value) );

		/// <summary>Determines whether a button has the exact name.</summary>
		public bool HasButton( string name ) => Has( AccessibilityRole.Button, name );

		/// <summary>Pumps every window until a condition holds, failing with what was expected, what shows and <paramref name="detail"/> after <see cref="StepTimeout"/>.</summary>
		public static void PumpAllUntil( IReadOnlyList<Screen> screens, Func<bool> condition, string what, Func<string>? detail = null )
		{
			var clock = Stopwatch.StartNew();
			while ( !condition() )
			{
				if ( clock.Elapsed > StepTimeout )
				{
					var shown = string.Join( " | ", screens.SelectMany( screen => screen.Nodes ).Select( node => node.Name ).Where( name => name is { Length: > 0 } ).Distinct() );
					CoreChecks.Check( false, $"{what} (timed out; {detail?.Invoke()}; showing: {shown[..Math.Min( shown.Length, 3000 )]})" );
				}
				foreach ( var screen in screens )
					screen.Pump();
			}
			foreach ( var screen in screens )
				screen.Pump();
		}

		/// <summary>Pumps this window until a condition holds.</summary>
		public void PumpUntil( Func<bool> condition, string what, Func<string>? detail = null ) => PumpAllUntil( [this], condition, what, detail );

		/// <summary>Replaces the text of the one text entry with the exact name through native pointer and keyboard input.</summary>
		/// <remarks>Goo discards presses and keys once its window lost the system focus, as this window does when another opens, until focus returns or an
		/// element is focused; the entry is focused first, as a click on an inactive window would activate it.</remarks>
		public void Type( string name, string text )
		{
			var node = Nodes.Single( candidate => candidate.Role == AccessibilityRole.TextInput && candidate.Name == name );
			CoreChecks.Check( Window.PerformAccessibilityAction( node.Id, new AccessibilityActionRequest( AccessibilityAction.Focus ) ), $"The {name} entry takes the focus." );
			var bounds = node.VisibleBounds ?? node.Bounds;
			var x = (bounds.X + bounds.Width / 2) * Window.UiScale;
			var y = (bounds.Y + bounds.Height / 2) * Window.UiScale;
			Window.PlatformInput.PointerMove( 1, PointerDevice.Mouse, x, y, KeyModifiers.None, 0.0f );
			Window.PlatformInput.PointerPress( 1, PointerDevice.Mouse, x, y, PointerButton.Primary, KeyModifiers.None, 0.0f );
			Window.PlatformInput.PointerRelease( 1, PointerDevice.Mouse, x, y, PointerButton.Primary, KeyModifiers.None, 0.0f );
			Pump();
			Window.PlatformInput.KeyPress( Key.A, KeyModifiers.Primary );
			Window.PlatformInput.KeyRelease( Key.A );
			Window.PlatformInput.CommitText( text );
			Pump();
		}

		/// <summary>Presses and releases a key where the focus is.</summary>
		public void Press( Key key )
		{
			Window.PlatformInput.KeyPress( key, KeyModifiers.None );
			Window.PlatformInput.KeyRelease( key );
			Pump();
		}

		/// <summary>Settles layout and accessibility delivery, running the host's work between frames.</summary>
		public void Pump()
		{
			for ( var index = 0; index < SettleFrames; index++ )
			{
				Action[] actions;
				lock ( _host )
				{
					actions = [.. _host];
					_host.Clear();
				}
				foreach ( var action in actions )
					action();
				Window.Pump( 0.016 );
				Thread.Sleep( 10 );
			}
		}

		/// <summary>Closes the window, which saves its layout and shuts its application down.</summary>
		public void Close()
		{
			Window.RequestClose();
			Pump();
		}
	}
}
