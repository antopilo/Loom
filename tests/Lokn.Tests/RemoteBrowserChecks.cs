using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokn.Core;
using Lokn.Modules.Browser;
using Lokn.Modules.Conversations;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;
using Lokn.Modules.Remote;
using Lokn.Modules.Workspaces;

namespace Lokn.Tests;

/// <summary>Checks the session's browser tabs over the protocol (docs/remote.md, Browser frames): mirrors on a daemon-role and a paired client, browser
/// tool calls forwarded to a window with Loom on this PC preferred, the no-window error and the opt-out setting, and the localhost tunnel to a loopback
/// echo server with its refusals; in process over in-memory connections, with temporary folders only.</summary>
internal static class RemoteBrowserChecks
{
	/// <summary>Defines how long one step may take.</summary>
	private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds( 30 );

	/// <summary>Runs the checks.</summary>
	internal static void Run()
	{
		var folder = Path.Combine( Path.GetTempPath(), "lokn-remote-browser-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( folder );
		var hostQueue = new List<Action>();
		var daemonQueue = new List<Action>();
		var pairedQueue = new List<Action>();
		var host = BuildApp( hostQueue, isHost: true );
		var daemon = BuildApp( daemonQueue, isHost: false );
		var paired = BuildApp( pairedQueue, isHost: false );
		var echo = new TcpListener( IPAddress.Loopback, 0 );
		echo.Start();
		_ = RunEchoAsync( echo );
		var isDaemonDown = false;
		try
		{
			bool PumpUntil( Func<bool> condition )
			{
				var clock = Stopwatch.StartNew();
				while ( !condition() && clock.Elapsed < StepTimeout )
				{
					Pump( hostQueue );
					Pump( daemonQueue );
					Pump( pairedQueue );
					Thread.Sleep( 10 );
				}
				return condition();
			}

			T PumpFor<T>( Task<T> task )
			{
				PumpUntil( () => task.IsCompleted );
				return task.IsCompleted ? task.GetAwaiter().GetResult() : throw new TimeoutException( "A call did not finish." );
			}

			AgentToolResult Call( string tool ) => PumpFor( host.Tools.InvokeAsync( tool, JsonSerializer.SerializeToElement( new { url = "https://example.com/" } ), null, CancellationToken.None ) );

			// The host: a daemon with the stores, the browser module without web views, and the gateway without a pipe.
			LoadOmp( host );
			var hostStore = new ConversationStore( new ProjectStore() );
			CoreChecks.Check( ProjectChecks.LoadProjects( host, hostStore ) && host.Load( new WorkspacesModule( Path.Combine( folder, "workspaces.json" ) ) )
				&& host.Load( new ConversationsModule( hostStore, ConversationsModule.BuildChat ) ) && host.Load( new BrowserModule( Path.Combine( folder, "host-browser" ) ) ),
				"The browser host loads Projects, Workspaces, Conversations and Browser." );
			var hostRemote = new RemoteModule( new RemoteHooks { DataFolder = Path.Combine( folder, "host" ) } );
			CoreChecks.Check( host.Load( hostRemote ) && hostRemote.Gateway is not null && host.BrowserTabs.Windows is not null, "The browser host starts the gateway, which runs browser tools in windows." );
			var gateway = hostRemote.Gateway!;
			var refused = Call( "browser_navigate" );
			CoreChecks.Check( refused.IsError && refused.Text == BrowserTabs.NoWindowMessage, $"With no window attached, a browser tool says to open Loom ({refused.Text})." );

			// A paired PC with control lends its browser: its stand-in browser tools say where they ran.
			var pairedId = Guid.NewGuid().ToString( "N" );
			var viewerId = Guid.NewGuid().ToString( "N" );
			using ( var keys = RemoteKeys.Create() )
			{
				gateway.Devices.Save( new PairedDevice { DeviceId = pairedId, Name = "Laptop", DeviceKey = keys.PublicKeyText, Scopes = DeviceScopes.Controlling, Paired = DateTimeOffset.UtcNow } );
				gateway.Devices.Save( new PairedDevice { DeviceId = viewerId, Name = "Phone", DeviceKey = keys.PublicKeyText, Scopes = DeviceScopes.Paired, Paired = DateTimeOffset.UtcNow } );
			}
			LoadWindow( paired, "Laptop" );
			var pairedData = Path.Combine( folder, "paired" );
			PairedHosts.Save( pairedData, [new PairedHost( gateway.Keys.HostId, gateway.Keys.PublicKeyText, [], null, pairedId, "Browser host", DateTimeOffset.UtcNow )] );
			var pairedRemote = new RemoteModule( new RemoteHooks
			{
				DataFolder = pairedData,
				Role = RemoteModule.LocalRole,
				Dial = ( hostId, cancellationToken ) =>
				{
					var (hostEnd, clientEnd) = MemoryConnection.CreatePair();
					_ = gateway.RunConnectionAsync( hostEnd, pairedId, "Laptop", DeviceScopes.Controlling );
					return Task.FromResult( new ClientCarrier( clientEnd, DeviceScopes.Controlling, "relay" ) );
				},
			} );
			CoreChecks.Check( paired.Load( pairedRemote ), "The paired PC links the browser host." );
			var pairedTabs = paired.Hosts.Get( gateway.Keys.HostId ).BrowserTabs;
			CoreChecks.Check( pairedTabs.IsMirror && pairedTabs.IsRemote && pairedTabs.Tunnel is not null && pairedTabs.ProxyServer is not null
				&& !paired.Hosts.Local.BrowserTabs.IsMirror && !ReferenceEquals( paired.Hosts.Local.BrowserTabs, pairedTabs ),
				"The paired PC mirrors the host's browser tabs in that host's own session, whose pages connect through its proxy and tunnel; This PC's tabs stay its own." );
			CoreChecks.Check( PumpUntil( () => Call( "browser_navigate" ).Text == "ran on Laptop" ), "With only a paired PC lending its browser, the host's browser tool runs there." );

			var tab = pairedTabs.Open( "https://example.com/", SettingsProfile.Personal, "laptop-view" );
			CoreChecks.Check( PumpUntil( () => host.BrowserTabs.Find( tab.Id ) is { Rev: 1 } && pairedTabs.Find( tab.Id ) is { Rev: 1 } && paired.Hosts.Local.BrowserTabs.Find( tab.Id ) is null ),
				"A tab the paired PC opens reaches the host, whose push gives the mirror its revision, and never lands in This PC's tabs." );

			// Loom on this PC, attached to the daemon, mirrors the same tabs and is preferred for browser tools.
			LoadWindow( daemon, "This PC" );
			var daemonRemote = new RemoteModule( new RemoteHooks
			{
				DataFolder = Path.Combine( folder, "daemon" ),
				Role = RemoteModule.DaemonRole,
				Dial = ( hostId, cancellationToken ) =>
				{
					var (hostEnd, clientEnd) = MemoryConnection.CreatePair();
					_ = gateway.RunConnectionAsync( hostEnd, HostGateway.LocalDevice, HostGateway.LocalName, DeviceScopes.Controlling );
					return Task.FromResult( new ClientCarrier( clientEnd, DeviceScopes.Controlling, "pipe" ) );
				},
			} );
			CoreChecks.Check( daemon.Load( daemonRemote ) && PumpUntil( () => daemon.BrowserTabs.IsSynced && daemon.BrowserTabs.Find( tab.Id ) is { Url: "https://example.com/" } ),
				"The daemon-role client's snapshot holds the paired PC's tab." );
			CoreChecks.Check( PumpUntil( () => Call( "browser_navigate" ).Text == "ran on This PC" ), "Loom on this PC is preferred over a paired PC for browser tools." );

			daemon.BrowserTabs.Navigate( tab.Id, "https://example.com/two", "pc-view" );
			CoreChecks.Check( PumpUntil( () => pairedTabs.Find( tab.Id ) is { Url: "https://example.com/two", By: "pc-view" } ), "A navigation in one window reaches the other with the view that made it." );
			pairedTabs.Report( tab.Id, "laptop-view", "https://example.com/two", "Page two", isLoading: false, 100, failed: null, canGoBack: true, canGoForward: false );
			CoreChecks.Check( PumpUntil( () => daemon.BrowserTabs.Find( tab.Id ) is { Title: "Page two", CanGoBack: true, By: "pc-view" } && host.BrowserTabs.Find( tab.Id )!.Title == "Page two" ),
				"A window's report of its page's title and history reaches the host and the other window." );
			var closing = daemon.BrowserTabs.Open( "https://example.org/", SettingsProfile.Work, "pc-view" );
			CoreChecks.Check( PumpUntil( () => pairedTabs.Find( closing.Id ) is not null ), "A tab opened in the other window reaches the paired PC." );
			pairedTabs.Close( closing.Id );
			CoreChecks.Check( PumpUntil( () => daemon.BrowserTabs.Find( closing.Id ) is null && host.BrowserTabs.Find( closing.Id ) is null ), "Closing a tab in one window closes it everywhere." );

			daemon.Shutdown();
			isDaemonDown = true;
			CoreChecks.Check( PumpUntil( () => Call( "browser_navigate" ).Text == "ran on Laptop" ), "When Loom on this PC leaves, the paired PC runs browser tools again." );
			CoreChecks.Check( host.BrowserTabs.Find( tab.Id ) is not null, "The session keeps its tabs when a window leaves." );
			paired.Settings.Set( RemoteModule.LendBrowser, SettingsScope.Global, false );
			var optedOut = default( AgentToolResult );
			CoreChecks.Check( PumpUntil( () => (optedOut = Call( "browser_navigate" )).Text == BrowserTabs.NoWindowMessage ) && optedOut!.IsError,
				"A paired PC whose user turned lending off runs no browser tool, and the host says no window can." );

			CheckTunnel( pairedTabs, echo, PumpUntil );
			CheckRefusals( gateway, pairedId, viewerId, echo, PumpFor );
			CoreChecks.Check( host.LastError is null, $"The host reports no failure ({host.LastError})." );
		}
		finally
		{
			paired.Shutdown();
			if ( !isDaemonDown )
				daemon.Shutdown();
			host.Shutdown();
			echo.Stop();
			TryDelete( folder );
		}
		Console.WriteLine( "PASS remote browser: session tabs mirrored on a daemon-role and a paired client with revisions; browser tools run in a window, Loom on this PC first, with the no-window error and the opt-out; the paired PC's proxy tunnels localhost, names under it and loopback addresses to the host's loopback echo server and back, past the window and with the page's half-close, refuses this PC's own address, and uploads a lent tool's file to the host; non-loopback hosts, the gateway's port and view-only devices are refused." );
	}

	/// <summary>Checks the paired PC's proxy, which every page of the host's session connects through: localhost, a name under it and a loopback address
	/// reach the host's loopback echo server, whose answer comes back whole past the tunnel's window, also after the page ended what it sends; this PC's
	/// own address is refused; and a file a lent tool saves is uploaded to the host.</summary>
	private static void CheckTunnel( BrowserTabs pairedTabs, TcpListener echo, Func<Func<bool>, bool> pumpUntil )
	{
		var port = ((IPEndPoint)echo.LocalEndpoint).Port;
		var proxy = new Uri( pairedTabs.ProxyServer! );
		CoreChecks.Check( proxy.Scheme == "socks5" && proxy.Host == "127.0.0.1", $"The proxy listens on this PC's loopback ({proxy})." );

		async Task<(byte Reply, TcpClient Client)> OpenAsync( byte type, byte[] address )
		{
			var client = new TcpClient( AddressFamily.InterNetwork ) { NoDelay = true };
			await client.ConnectAsync( IPAddress.Loopback, proxy.Port );
			var stream = client.GetStream();
			await stream.WriteAsync( new byte[] { 5, 1, 0 } );
			var greeting = new byte[2];
			await stream.ReadExactlyAsync( greeting );
			await stream.WriteAsync( (byte[])[5, 1, 0, type, .. address, (byte)(port >> 8), (byte)port] );
			var reply = new byte[10];
			await stream.ReadExactlyAsync( reply );
			return (reply[1], client);
		}

		byte Reply( byte type, byte[] address )
		{
			var opening = OpenAsync( type, address );
			CoreChecks.Check( pumpUntil( () => opening.IsCompleted ) && opening.IsCompletedSuccessfully, "The proxy answers a connect request." );
			opening.Result.Client.Dispose();
			return opening.Result.Reply;
		}

		static byte[] Name( string host ) => [(byte)host.Length, .. Encoding.ASCII.GetBytes( host )];

		var open = OpenAsync( 3, Name( "localhost" ) );
		CoreChecks.Check( pumpUntil( () => open.IsCompleted ) && open.Result.Reply == 0, "A page's connection to localhost reaches the host's loopback service." );
		using ( var client = open.Result.Client )
		{
			var stream = client.GetStream();
			var payload = new byte[(TunnelService.WindowBytes * 2) + 12345];
			Random.Shared.NextBytes( payload );
			var received = new byte[payload.Length + 1];
			var write = Task.Run( async () =>
			{
				await stream.WriteAsync( payload );
				// The page sent all it will; the answer still on its way must arrive whole.
				client.Client.Shutdown( SocketShutdown.Send );
			} );
			var read = Task.Run( async () =>
			{
				var total = 0;
				while ( true )
				{
					var count = await stream.ReadAsync( received.AsMemory( total ) );
					if ( count == 0 )
						return total;
					total += count;
				}
			} );
			CoreChecks.Check( pumpUntil( () => read.IsCompleted ) && write.IsCompletedSuccessfully && read.Result == payload.Length && received.AsSpan( 0, payload.Length ).SequenceEqual( payload ),
				"Bytes a page sends to the host's localhost come back whole from its loopback service, past the tunnel's window and after the page ended what it sends." );
		}

		CoreChecks.Check( Reply( 3, Name( "app.localhost" ) ) == 0 && Reply( 1, [127, 0, 0, 1] ) == 0, "A name under localhost and a loopback address reach the host's loopback too." );
		var own = NetworkInterface.GetAllNetworkInterfaces().Where( adapter => adapter.OperationalStatus == OperationalStatus.Up )
			.SelectMany( adapter => adapter.GetIPProperties().UnicastAddresses ).Select( unicast => unicast.Address )
			.FirstOrDefault( address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback( address ) );
		CoreChecks.Check( own is null || Reply( 1, own.GetAddressBytes() ) == 2, $"This PC's own address ({own}) is refused, so the host's pages never reach its services." );

		var shot = Path.Combine( Path.GetTempPath(), $"lokn-shot-{Guid.NewGuid():N}.png" );
		File.WriteAllBytes( shot, [1, 2, 3, 4] );
		try
		{
			var sharing = pairedTabs.ShareFile!( shot, CancellationToken.None );
			CoreChecks.Check( pumpUntil( () => sharing.IsCompleted ) && sharing.IsCompletedSuccessfully && sharing.Result != shot && File.ReadAllBytes( sharing.Result ).SequenceEqual( (byte[])[1, 2, 3, 4] ),
				"A file a lent browser tool saves, such as a screenshot, is uploaded to the host and named by its path there." );
		}
		finally
		{
			File.Delete( shot );
		}
	}

	/// <summary>Checks the host refuses tunnels to other machines and to its own gateway port, and that a view-only device may read browser tabs but not
	/// change them, tunnel or lend its browser.</summary>
	private static void CheckRefusals( HostGateway gateway, string pairedId, string viewerId, TcpListener echo, Func<Task<JsonObject>, JsonObject> pumpFor )
	{
		string? Refusal( RpcPeer peer, JsonObject frame )
		{
			try
			{
				pumpFor( peer.RequestAsync( frame, CancellationToken.None ) );
				return null;
			}
			catch ( RemoteException exception )
			{
				return exception.Code;
			}
		}

		JsonObject Open( long id, int port, string? hostName = null )
		{
			var frame = Frame.Create( "tcp.open" );
			frame["tcp"] = id;
			frame["port"] = port;
			if ( hostName is not null )
				frame["host"] = hostName;
			return frame;
		}

		var port = ((IPEndPoint)echo.LocalEndpoint).Port;
		var (hostEnd, clientEnd) = MemoryConnection.CreatePair();
		_ = gateway.RunConnectionAsync( hostEnd, pairedId, "Laptop", DeviceScopes.Controlling );
		var control = new RpcPeer( clientEnd );
		control.Start();
		CoreChecks.Check( Refusal( control, Open( 1, port, "10.1.2.3" ) ) == RemoteErrors.Scope && Refusal( control, Open( 2, port, "example.com" ) ) == RemoteErrors.Scope,
			"The host refuses tunnels to any address but its own loopback." );
		CoreChecks.Check( Refusal( control, Open( 3, RemoteModule.Port.DefaultValue ) ) == RemoteErrors.Scope, "The host refuses tunnels to its own gateway port." );
		CoreChecks.Check( Refusal( control, Open( 4, port, "::1" ) ) is null or RemoteErrors.Failed && Refusal( control, Open( 5, port, "localhost" ) ) is null,
			"The host opens tunnels to its loopback by name and address." );
		var offer = Frame.Create( "tools.offer" );
		offer["tools"] = new JsonArray( new JsonObject { ["name"] = "probe_echo", ["description"] = "Echoes.", ["parameters"] = new JsonObject(), ["moduleId"] = "probe" } );
		CoreChecks.Check( Refusal( control, offer ) == RemoteErrors.Scope, "A paired device may offer no tool but its browser tools." );
		_ = control.DisposeAsync();

		var (viewerHost, viewerEnd) = MemoryConnection.CreatePair();
		_ = gateway.RunConnectionAsync( viewerHost, viewerId, "Phone", DeviceScopes.Paired );
		var viewer = new RpcPeer( viewerEnd );
		viewer.Start();
		CoreChecks.Check( pumpFor( viewer.RequestAsync( Frame.Create( "browser.sync" ), CancellationToken.None ) )["tabs"] is JsonArray { Count: > 0 },
			"A view-only device reads the session's browser tabs." );
		var change = Frame.Create( "browser.open" );
		change["tab"] = Guid.NewGuid().ToString();
		change["url"] = "https://example.com/";
		var lend = Frame.Create( "tools.offer" );
		lend["tools"] = new JsonArray( new JsonObject { ["name"] = "browser_navigate", ["description"] = "Navigates.", ["parameters"] = new JsonObject(), ["moduleId"] = "browser" } );
		CoreChecks.Check( Refusal( viewer, change ) == RemoteErrors.Scope && Refusal( viewer, Open( 6, port ) ) == RemoteErrors.Scope && Refusal( viewer, lend ) == RemoteErrors.Scope,
			"A view-only device may not change browser tabs, tunnel or lend its browser." );
		_ = viewer.DisposeAsync();
	}

	/// <summary>Loads a window's modules in an application: Omp with the fixture, the stores, and stand-in browser tools that say which window ran them.</summary>
	private static void LoadWindow( LoknApplication app, string name )
	{
		LoadOmp( app );
		var store = new ConversationStore( new ProjectStore() );
		CoreChecks.Check( ProjectChecks.LoadProjects( app, store ) && app.Load( new ConversationsModule( store, ConversationsModule.BuildChat ) )
			&& app.Load( new FixtureModule( "browser", builder => builder
				.Tool( "navigate", "Navigates.", """{ "type": "object" }""", ( _, _ ) => new( $"ran on {name}" ) )
				.Tool( "tabs", "Lists tabs.", """{ "type": "object" }""", ( _, _ ) => new( $"tabs of {name}" ) ) ) ),
			$"{name} loads its stores and stand-in browser tools." );
	}

	/// <summary>Echoes every connection's bytes back until the listener stops.</summary>
	private static async Task RunEchoAsync( TcpListener listener )
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
					var stream = client.GetStream();
					var buffer = new byte[16 * 1024];
					try
					{
						int count;
						while ( (count = await stream.ReadAsync( buffer )) > 0 )
							await stream.WriteAsync( buffer.AsMemory( 0, count ) );
					}
					catch ( Exception exception ) when ( exception is IOException or SocketException or ObjectDisposedException )
					{
					}
				}
			} );
		}
	}

	/// <summary>Creates an application whose UI thread is a queue the check pumps.</summary>
	private static LoknApplication BuildApp( List<Action> queue, bool isHost )
	{
		var app = new LoknApplication() { IsHost = isHost };
		app.BindDispatcher( action =>
		{
			lock ( queue )
				queue.Add( action );
			return true;
		} );
		return app;
	}

	/// <summary>Loads Omp with the fixture (this executable) as its executable and its first model.</summary>
	private static void LoadOmp( LoknApplication app )
	{
		CoreChecks.Check( app.Load( new OmpModule() ), "Omp loads with the fixture." );
		app.Settings.Set( OmpModule.Executable, SettingsScope.Global, Path.Combine( AppContext.BaseDirectory, "Lokn.Tests.exe" ) );
		app.Settings.Set( OmpModule.Model, SettingsScope.Global, "fixture/one" );
	}

	/// <summary>Runs every queued action.</summary>
	private static void Pump( List<Action> queue )
	{
		Action[] actions;
		lock ( queue )
		{
			actions = [.. queue];
			queue.Clear();
		}
		foreach ( var action in actions )
			action();
	}

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
}
