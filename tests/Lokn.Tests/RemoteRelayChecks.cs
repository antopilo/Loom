using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Lokn.Modules.Remote;
using Lokn.Relay;
using Microsoft.Extensions.DependencyInjection;

namespace Lokn.Tests;

/// <summary>Checks the relay and Tailscale network modes: relay addresses, a host link and devices through an in-process relay, the web client the relay
/// serves, reconnecting after the relay restarts, and parsing of captured Tailscale CLI output.</summary>
internal static class RemoteRelayChecks
{
	/// <summary>Bounds every wait, so a broken check fails rather than hangs.</summary>
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds( 15 );

	/// <summary>Bounds the wait for the host link to come back, which covers a few backoff steps.</summary>
	private static readonly TimeSpan ReconnectPatience = TimeSpan.FromSeconds( 40 );

	/// <summary>The regular expression literal app.js matches its path with: a page under <c>&lt;prefix&gt;/h/&lt;hostId&gt;/</c> was served by a relay.</summary>
	private const string PagePathRule = @"/^(.*)\/h\/([A-Za-z0-9_-]+)\//";

	/// <summary>Captured <c>tailscale status --json</c> of a signed-in Windows PC, trimmed of peers.</summary>
	private const string RunningStatus = """
		{
		  "Version": "1.76.6-t1a2b3c4d5-g6e7f8a9b0",
		  "TUN": true,
		  "BackendState": "Running",
		  "HaveNodeKey": true,
		  "AuthURL": "",
		  "TailscaleIPs": [
		    "100.101.102.103",
		    "fd7a:115c:a1e0::1234:5678"
		  ],
		  "Self": {
		    "ID": "nABCDEF1CNTRL",
		    "PublicKey": "nodekey:0123456789abcdef",
		    "HostName": "DESK",
		    "DNSName": "desk.tail1234.ts.net.",
		    "OS": "windows",
		    "UserID": 123456,
		    "TailscaleIPs": [
		      "100.101.102.103",
		      "fd7a:115c:a1e0::1234:5678"
		    ],
		    "Online": true,
		    "CapMap": { "https": null, "https://tailscale.com/cap/ssh": null }
		  },
		  "Health": [],
		  "MagicDNSSuffix": "tail1234.ts.net",
		  "CurrentTailnet": {
		    "Name": "user@example.com",
		    "MagicDNSSuffix": "tail1234.ts.net",
		    "MagicDNSEnabled": true
		  },
		  "CertDomains": [
		    "desk.tail1234.ts.net"
		  ],
		  "Peer": {},
		  "User": {}
		}
		""";

	/// <summary>Captured <c>tailscale status --json</c> of a signed-out PC.</summary>
	private const string SignedOutStatus = """
		{
		  "Version": "1.76.6-t1a2b3c4d5-g6e7f8a9b0",
		  "TUN": true,
		  "BackendState": "NeedsLogin",
		  "HaveNodeKey": false,
		  "AuthURL": "https://login.tailscale.com/a/1a2b3c4d5e6f",
		  "TailscaleIPs": null,
		  "Self": {
		    "ID": "",
		    "PublicKey": "nodekey:0000000000000000",
		    "HostName": "DESK",
		    "DNSName": "",
		    "OS": "windows",
		    "TailscaleIPs": null,
		    "Online": false
		  },
		  "Health": [
		    "You are logged out."
		  ],
		  "MagicDNSSuffix": "",
		  "CurrentTailnet": null,
		  "Peer": null,
		  "User": null
		}
		""";

	/// <summary>Captured <c>tailscale serve --help</c> of Tailscale 1.76, whose Serve takes <c>--bg</c>.</summary>
	private const string CurrentServeHelp = """
		USAGE
		  tailscale serve <target>
		  tailscale serve status [--json]
		  tailscale serve reset

		Tailscale Serve enables you to share a local server securely within your tailnet.
		To share a local server on the internet, use "tailscale funnel"

		EXAMPLES
		  - Expose an HTTP server running at 127.0.0.1:3000 in the foreground:
		    $ tailscale serve 3000

		  - Expose an HTTP server running at 127.0.0.1:3000 in the background:
		    $ tailscale serve --bg 3000

		SUBCOMMANDS
		  status  View current serve configuration
		  reset   Reset current serve config

		FLAGS
		  --bg, --bg=false
		    	Run the command as a background process (default false)
		  --http uint
		    	Expose an HTTP server at the specified port
		  --https uint
		    	Expose an HTTPS server at the specified port (default mode)
		""";

	/// <summary>Captured <c>tailscale serve --help</c> of Tailscale 1.48, before <c>--bg</c>.</summary>
	private const string LegacyServeHelp = """
		USAGE
		  serve https:<port> <mount-point> <source> [off]
		  serve tcp:<port> tcp://localhost:<local-port> [off]
		  serve tls-terminated-tcp:<port> tcp://localhost:<local-port> [off]
		  serve status [--json]
		  serve reset

		*** BETA; all of this is subject to change ***

		The 'tailscale serve' set of commands allows you to serve
		content and local servers from your Tailscale node to
		your tailnet.

		EXAMPLES
		  - To proxy requests to a web server at 127.0.0.1:3000:
		    $ tailscale serve https:443 / http://127.0.0.1:3000

		    Or, using the default port (443):
		    $ tailscale serve https / http://127.0.0.1:3000

		SUBCOMMANDS
		  status  show current serve/funnel status
		  reset   reset current serve/funnel config
		""";

	/// <summary>Runs every relay and Tailscale check.</summary>
	internal static async Task RunAsync()
	{
		CheckRelayUris();
		CheckTailscaleParsing();
		await CheckDiscoveryAsync();
		var folder = Path.Combine( Path.GetTempPath(), "lokn-relay-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( folder );
		try
		{
			await CheckUnreachableRelayAsync();
			await CheckRelayLinkAsync( folder );
		}
		finally
		{
			Directory.Delete( folder, true );
		}
	}

	/// <summary>Checks that discovery finds a host on this PC by its id and lists it nearby with its name and addresses, ignores another id, and drops
	/// answers that are no answer or whose key does not match their host id.</summary>
	private static async Task CheckDiscoveryAsync()
	{
		using var keys = RemoteKeys.Create();
		var real = new NearbyHost( keys.HostId, keys.PublicKeyText, "DESK", ["192.168.50.7:47390"] );
		CoreChecks.Check( LanDiscovery.ReadAnswer( LanDiscovery.BuildAnswer( real ) ) is { Name: "DESK" } read && read.HostId == keys.HostId && read.Addrs.SequenceEqual( real.Addrs )
			&& LanDiscovery.ReadAnswer( LanDiscovery.BuildAnswer( real with { HostId = "forged" } ) ) is null && LanDiscovery.ReadAnswer( "not json"u8 ) is null,
			"An answer reads back, and one whose key does not hash to its host id, or that is no answer, is dropped." );
		using var host = new LanDiscovery( keys.HostId, keys.PublicKeyText, "DESK", () => real.Addrs );
		if ( !host.Start() )
		{
			Console.WriteLine( "SKIP LAN discovery round trip: another program holds the discovery port." );
			return;
		}
		var found = await LanDiscovery.FindAsync( keys.HostId, TimeSpan.FromSeconds( 1.5 ), CancellationToken.None );
		CoreChecks.Check( found.SequenceEqual( real.Addrs ), $"A broadcast query finds the host by its id with its addresses ({string.Join( ", ", found )})." );
		var nearby = await LanDiscovery.ListAsync( TimeSpan.FromSeconds( 1.5 ), CancellationToken.None );
		CoreChecks.Check( nearby.Any( item => item.HostId == keys.HostId && item.Name == "DESK" && item.Addrs.SequenceEqual( real.Addrs ) ), "Listing nearby hosts includes the host with its name." );
		var other = await LanDiscovery.FindAsync( Guid.NewGuid().ToString( "N" ), TimeSpan.FromSeconds( 0.5 ), CancellationToken.None );
		CoreChecks.Check( other.Count == 0, "A query for another host id gets no answer." );
	}

	/// <summary>Checks that relay addresses in every accepted form become WebSocket endpoints.</summary>
	private static void CheckRelayUris()
	{
		CoreChecks.Check( RelayDialer.BuildUri( "https://relay.example", "/device/abc" ).AbsoluteUri == "wss://relay.example/device/abc", "An https relay address becomes wss." );
		CoreChecks.Check( RelayDialer.BuildUri( "http://127.0.0.1:8080/", "/host/abc" ).AbsoluteUri == "ws://127.0.0.1:8080/host/abc", "An http relay address becomes ws and keeps its port." );
		CoreChecks.Check( RelayDialer.BuildUri( "relay.example:9000", "/device/abc" ).AbsoluteUri == "wss://relay.example:9000/device/abc", "A bare relay address means wss." );
		CoreChecks.Check( RelayDialer.BuildUri( "https://example.com/relay/?x=1#y", "/device/abc" ).AbsoluteUri == "wss://example.com/relay/device/abc", "A relay behind a path prefix keeps the prefix and drops the query." );
		CoreChecks.Check( RelayDialer.BuildUri( "wss://relay.example:443", "/device/abc" ).AbsoluteUri == "wss://relay.example/device/abc", "A wss relay address stays wss." );
		CoreChecks.Check( RelayDialer.BuildPageUrl( "wss://relay.example", "abc" ) == "https://relay.example/h/abc/" && RelayDialer.BuildPageUrl( "relay.example", "abc" ) == "https://relay.example/h/abc/",
			"A secure relay's web client is at https://<relay>/h/{hostId}/." );
		CoreChecks.Check( RelayDialer.BuildPageUrl( "http://127.0.0.1:8080", "abc" ) == "http://127.0.0.1:8080/h/abc/" && RelayDialer.BuildPageUrl( "https://example.com/relay/", "abc" ) == "https://example.com/relay/h/abc/",
			"A plain relay's web client is http, and a path prefix is kept." );
		var refused = false;
		try
		{
			RelayDialer.BuildUri( "ftp://relay.example", "/device/abc" );
		}
		catch ( ArgumentException )
		{
			refused = true;
		}

		CoreChecks.Check( refused, "A relay address that is not http, https, ws or wss is refused." );

		CoreChecks.Check( RelayDialer.BuildUri( "http://192.168.1.5:8080", "/host/abc" ).Scheme == "ws" && RelayDialer.BuildUri( "ws://localhost:8080", "/host/abc" ).Scheme == "ws"
			&& RelayDialer.BuildUri( "http://100.101.102.103", "/host/abc" ).Scheme == "ws", "A plain relay address on this PC, a private network or the tailnet is allowed." );
		foreach ( var plain in new[] { "http://relay.example", "ws://203.0.113.9:8080", "http://8.8.8.8" } )
		{
			var plainRefused = false;
			try
			{
				RelayDialer.BuildUri( plain, "/host/abc" );
			}
			catch ( ArgumentException )
			{
				plainRefused = true;
			}
			CoreChecks.Check( plainRefused, $"A plain relay address on a public host ({plain}) is refused." );
		}
	}

	/// <summary>Checks the parsing of captured Tailscale CLI output and the Serve and Funnel commands for both CLI generations.</summary>
	private static void CheckTailscaleParsing()
	{
		CoreChecks.Check( TailscaleCli.ParseAddresses( "100.101.102.103\r\n" ).SequenceEqual( ["100.101.102.103"] ), "tailscale ip -4 output parses to its address." );
		CoreChecks.Check( TailscaleCli.ParseAddresses( "100.64.0.1\n100.64.0.2\n\n" ).SequenceEqual( ["100.64.0.1", "100.64.0.2"] ), "Several addresses parse, one per line." );
		CoreChecks.Check( TailscaleCli.ParseAddresses( "Tailscale is stopped.\n" ).Count == 0, "Text that is not an address is skipped." );

		var running = TailscaleCli.ParseStatus( RunningStatus );
		CoreChecks.Check( running.IsRunning && running.DnsName == "desk.tail1234.ts.net" && running.AuthUrl is null, "A running status gives the MagicDNS name without its trailing dot." );
		CoreChecks.Check( running.Addresses.SequenceEqual( ["100.101.102.103", "fd7a:115c:a1e0::1234:5678"] ), "A running status lists the PC's tailnet addresses." );
		CoreChecks.Check( TailscaleCli.DescribeProblem( running ) is null, "A running Tailscale has no problem." );
		CoreChecks.Check( running.HasHttps && !running.HasFunnel && running.NodeId == "nABCDEF1CNTRL"
			&& TailscaleCli.BuildFunnelUrl( running.NodeId ) == "https://login.tailscale.com/f/funnel?node=nABCDEF1CNTRL",
			"A status with HTTPS certificates and no Funnel attribute says so, and the page that allows Funnel names this node." );

		var signedOut = TailscaleCli.ParseStatus( SignedOutStatus );
		CoreChecks.Check( !signedOut.IsRunning && signedOut.DnsName is null && signedOut.Addresses.Count == 0 && signedOut.AuthUrl == "https://login.tailscale.com/a/1a2b3c4d5e6f", "A signed-out status has no name or addresses and carries the sign-in address." );
		CoreChecks.Check( TailscaleCli.DescribeProblem( signedOut ) is { } problem && problem.Contains( "not signed in", StringComparison.Ordinal ) && problem.Contains( signedOut.AuthUrl!, StringComparison.Ordinal ), "A signed-out Tailscale is reported as not signed in, with the sign-in address." );
		CoreChecks.Check( TailscaleCli.DescribeProblem( running with { BackendState = "Stopped" } ) is { } stopped && stopped.Contains( "disconnected", StringComparison.Ordinal ), "A stopped Tailscale is reported as disconnected." );
		var malformed = false;
		try
		{
			TailscaleCli.ParseStatus( "[1, 2]" );
		}
		catch ( System.Text.Json.JsonException )
		{
			malformed = true;
		}

		CoreChecks.Check( malformed, "A status that is not an object is refused." );

		CoreChecks.Check( TailscaleCli.SupportsBackground( CurrentServeHelp ) && !TailscaleCli.SupportsBackground( LegacyServeHelp ), "The CLI generation is told by --bg in Serve's help." );
		static string Join( IReadOnlyList<IReadOnlyList<string>> commands ) => string.Join( " && ", commands.Select( command => string.Join( ' ', command ) ) );
		CoreChecks.Check( Join( TailscaleCli.BuildCommands( 47390, false, true ) ) == "serve --bg --https=443 http://127.0.0.1:47390", "Serve with a 1.52+ CLI proxies the port on HTTPS 443 in the background." );
		CoreChecks.Check( Join( TailscaleCli.BuildCommands( 47390, true, true ) ) == "funnel --bg --https=443 http://127.0.0.1:47390", "Funnel on with a 1.52+ CLI proxies the port to the internet." );
		CoreChecks.Check( Join( TailscaleCli.BuildCommands( 47390, false, false ) ) == "serve https:443 / http://127.0.0.1:47390 && funnel 443 off", "Serve with an earlier CLI uses its form and turns Funnel off." );
		CoreChecks.Check( Join( TailscaleCli.BuildCommands( 47390, true, false ) ) == "serve https:443 / http://127.0.0.1:47390 && funnel 443 on", "Funnel on with an earlier CLI serves, then turns Funnel on." );
		CoreChecks.Check( Join( TailscaleCli.BuildOffCommands( true ) ) == "serve --https=443 off", "Turning the proxy off with a 1.52+ CLI removes the HTTPS 443 handler." );
		CoreChecks.Check( Join( TailscaleCli.BuildOffCommands( false ) ) == "funnel 443 off && serve https:443 / off", "Turning the proxy off with an earlier CLI turns Funnel off, then removes the handler." );
	}

	/// <summary>Checks that a host link to a relay nobody listens on reports it and keeps retrying.</summary>
	private static async Task CheckUnreachableRelayAsync()
	{
		using var keys = RemoteKeys.Create();
		var statuses = new ConcurrentQueue<string>();
		await using var link = new RelayHostLink( $"http://127.0.0.1:{FindFreePort()}", keys, _ => Task.CompletedTask );
		link.StatusChanged += statuses.Enqueue;
		link.Start();
		CoreChecks.Check( await WaitUntilAsync( () => statuses.Any( status => status.StartsWith( "Cannot reach the relay", StringComparison.Ordinal ) && status.Contains( "retrying in", StringComparison.Ordinal ) ), Patience ), "A host link reports an unreachable relay and that it retries." );
		CoreChecks.Check( !link.IsConnected, "A host link to an unreachable relay is not connected." );
	}

	/// <summary>Checks pairing, the handshake, requests and pushes through an in-process relay, the host link's reconnect after the relay restarts, and its close.</summary>
	private static async Task CheckRelayLinkAsync( string folder )
	{
		using var hostKeys = RemoteKeys.Create();
		using var deviceKeys = RemoteKeys.Create();
		var devices = new DeviceStore( Path.Combine( folder, "devices.json" ) );
		var offers = new PairingOffers();
		var hostPeers = Channel.CreateUnbounded<RpcPeer>();
		var statuses = new ConcurrentQueue<string>();
		var failures = new ConcurrentQueue<Exception>();

		var relay = RelayApp.Build( ["http://127.0.0.1:0"], ["--Logging:LogLevel:Default=None"] );
		await relay.StartAsync();
		var root = relay.Urls.Single();
		var port = new Uri( root ).Port;
		var relayRunning = true;
		try
		{
			await using var link = new RelayHostLink( root, hostKeys, async connection =>
			{
				if ( await SecureChannel.AcceptAsync( connection, hostKeys, devices, offers, CancellationToken.None ) is not { } session )
					return;
				var peer = new RpcPeer( session.Connection );
				var closed = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
				peer.On( "echo", frame => Task.FromResult<JsonObject?>( new() { ["value"] = frame["value"]?.DeepClone(), ["device"] = session.Device.DeviceId } ) );
				peer.Closed += _ => closed.TrySetResult();
				peer.Start();
				hostPeers.Writer.TryWrite( peer );
				await closed.Task;
			} );
			link.StatusChanged += statuses.Enqueue;
			link.Failed += failures.Enqueue;
			CoreChecks.Check( link.Status == "Not started" && !link.IsConnected, "A new host link is idle until started." );
			link.Start();
			CoreChecks.Check( await WaitUntilAsync( () => ReadHub( relay ).HasHost( hostKeys.HostId ) && link.IsConnected, Patience ), "The host link proves its key and registers with the relay." );
			await CheckRelayWebClientAsync( root, hostKeys, offers, devices );
			CoreChecks.Check( link.Status.StartsWith( "Connected to the relay at 127.0.0.1:", StringComparison.Ordinal ), "The host link reports that it is connected." );

			var pairing = PairingLink.TryParse( offers.Create( hostKeys, [], root ).Link.BuildUri() )!;
			var deviceId = Guid.NewGuid().ToString( "N" );
			var scopes = await SecureChannel.PairAsync( await RelayDialer.ConnectAsync( pairing.Relay!, pairing.HostId, CancellationToken.None ), deviceKeys, deviceId, "Phone", pairing, CancellationToken.None ).WaitAsync( Patience );
			CoreChecks.Check( scopes.SequenceEqual( ["view"] ) && devices.Find( deviceId ) is { Name: "Phone" } stored && stored.DeviceKey == deviceKeys.PublicKeyText, "A device pairs with the host through the relay." );
			devices.SetScopes( deviceId, DeviceScopes.Controlling );

			var (client, host) = await ConnectAsync( root, hostKeys, deviceKeys, deviceId, pairing.HostKey, hostPeers, "before the restart" );
			var clientClosed = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
			client.Closed += _ => clientClosed.TrySetResult();

			relayRunning = false;
			await StopAsync( relay );
			await relay.DisposeAsync();
			await clientClosed.Task.WaitAsync( Patience );
			CoreChecks.Check( client.IsClosed && await WaitUntilAsync( () => host.IsClosed, Patience ), "A device's session through the relay ends on both sides when the relay stops." );
			CoreChecks.Check( await WaitUntilAsync( () => !link.IsConnected && link.Status.Contains( "retrying in", StringComparison.Ordinal ), Patience ), "The host link notices the relay is gone and retries." );

			relay = await RestartAsync( port );
			relayRunning = true;
			CoreChecks.Check( await WaitUntilAsync( () => ReadHub( relay ).HasHost( hostKeys.HostId ) && link.IsConnected, ReconnectPatience ), "The host link reconnects after the relay restarts." );
			(client, _) = await ConnectAsync( root, hostKeys, deviceKeys, deviceId, pairing.HostKey, hostPeers, "after the restart" );
			await client.DisposeAsync();

			await link.DisposeAsync();
			CoreChecks.Check( await WaitUntilAsync( () => !ReadHub( relay ).HasHost( hostKeys.HostId ), Patience ) && !link.IsConnected, "Disposing the host link closes its control socket." );
			CoreChecks.Check( failures.IsEmpty, "No device session through the relay failed." );
			CoreChecks.Check( statuses.Count( status => status.StartsWith( "Connected", StringComparison.Ordinal ) ) >= 2, "The host link reports each connect." );
		}
		finally
		{
			if ( relayRunning )
			{
				await StopAsync( relay );
				await relay.DisposeAsync();
			}
		}
	}

	/// <summary>Checks the web client the relay serves under /h/{hostId}/ with the host's headers and validators, and that a page following app.js's
	/// socket rule pairs with the host through /device/{hostId}.</summary>
	private static async Task CheckRelayWebClientAsync( string root, RemoteKeys hostKeys, PairingOffers offers, DeviceStore devices )
	{
		var page = RelayDialer.BuildPageUrl( root, hostKeys.HostId );
		CoreChecks.Check( page == root.TrimEnd( '/' ) + "/h/" + hostKeys.HostId + "/", "The relay's web client lives at /h/{hostId}/." );
		using var http = new HttpClient( new HttpClientHandler { AllowAutoRedirect = false } ) { BaseAddress = new Uri( page ) };
		var script = "";
		foreach ( var (path, type) in new[]
		{
			("", "text/html"), ("index.html", "text/html"), ("app.js", "text/javascript"), ("app.css", "text/css"), ("sw.js", "text/javascript"),
			("manifest.webmanifest", "application/manifest+json"), ("icon.png", "image/png"),
		} )
		{
			using var response = await http.GetAsync( path ).WaitAsync( Patience );
			var bytes = await response.Content.ReadAsByteArrayAsync();
			CoreChecks.Check( response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == type && bytes.Length > 0 && response.Headers.ETag is not null,
				$"The relay serves /h/{{hostId}}/{path} as {type}." );
			if ( path == "" )
				CoreChecks.Check( response.Headers.CacheControl?.NoCache == true && response.Headers.TryGetValues( "Content-Security-Policy", out var policy )
					&& policy.Single().Contains( "connect-src 'self';", StringComparison.Ordinal ) && Encoding.UTF8.GetString( bytes ).Contains( "src=\"app.js\"", StringComparison.Ordinal ),
				"The relay's page has the host's content policy, which lets it open sockets to its own origin only, and loads its files relatively." );
			if ( path == "app.js" )
				script = Encoding.UTF8.GetString( bytes );
			if ( path == "icon.png" )
				CoreChecks.Check( bytes[0] == 0x89 && bytes[1] == (byte)'P', "The relay's icon is a PNG." );

			using var again = new HttpRequestMessage( HttpMethod.Get, path );
			again.Headers.TryAddWithoutValidation( "If-None-Match", response.Headers.ETag?.Tag ?? "" );
			using var cached = await http.SendAsync( again ).WaitAsync( Patience );
			CoreChecks.Check( cached.StatusCode == HttpStatusCode.NotModified, $"The relay answers /h/{{hostId}}/{path} 304 for the version the browser has." );
		}

		using ( var bare = await http.GetAsync( $"/h/{hostKeys.HostId}" ).WaitAsync( Patience ) )
			CoreChecks.Check( (int)bare.StatusCode is >= 300 and < 400 && bare.Headers.Location?.OriginalString == hostKeys.HostId + "/", "/h/{hostId} without the slash is sent to its folder." );
		CoreChecks.Check( script.Contains( PagePathRule, StringComparison.Ordinal ) && script.Contains( "'/device/'", StringComparison.Ordinal ), "app.js holds the relay page's socket rule these checks follow." );

		var socket = ResolveSocketUri( new Uri( page ) );
		CoreChecks.Check( socket.AbsoluteUri == RelayDialer.BuildUri( root, "/device/" + hostKeys.HostId ).AbsoluteUri, "A page under /h/{hostId}/ connects to the relay's /device/{hostId}." );
		CoreChecks.Check( ResolveSocketUri( new Uri( "https://desk.tail1234.ts.net/" ) ).AbsoluteUri == "wss://desk.tail1234.ts.net/loom"
			&& ResolveSocketUri( new Uri( "https://example.com/relay/h/abc/index.html" ) ).AbsoluteUri == "wss://example.com/relay/device/abc",
			"A page at a host's root connects to /loom, and a relay's path prefix is kept." );

		var webUrl = offers.Create( hostKeys, [], root ).Link.BuildWebUrl( page );
		CoreChecks.Check( webUrl.StartsWith( page + PairingLink.WebMarker, StringComparison.Ordinal ), "The relay mode's pairing web URL is the relay's /h/{hostId}/ page with #pair=." );
		using var browser = RemoteKeys.Create();
		var browserId = Guid.NewGuid().ToString( "N" );
		var scopes = await SecureChannel.PairAsync( await WebSocketDialer.ConnectAsync( socket, CancellationToken.None ), browser, browserId, "Browser", PairingLink.TryParse( webUrl )!, CancellationToken.None ).WaitAsync( Patience );
		CoreChecks.Check( scopes.SequenceEqual( ["view"] ) && devices.Find( browserId ) is { Name: "Browser" }, "A page following the relay web URL pairs with the host through /device/{hostId}." );
	}

	/// <summary>Resolves a page's socket address by app.js's rule (<see cref="PagePathRule"/>): a page under <c>&lt;prefix&gt;/h/&lt;hostId&gt;/</c> connects
	/// to <c>&lt;prefix&gt;/device/&lt;hostId&gt;</c> on its origin, any other page to <c>/loom</c>.</summary>
	private static Uri ResolveSocketUri( Uri page )
	{
		var relayed = Regex.Match( page.AbsolutePath, "^(.*)/h/([A-Za-z0-9_-]+)/" );
		var origin = (page.Scheme == "https" ? "wss://" : "ws://") + page.Authority;
		return new( origin + (relayed.Success ? relayed.Groups[1].Value + "/device/" + relayed.Groups[2].Value : "/loom") );
	}

	/// <summary>Connects the device through the relay, checks a request with its answer and a push, and returns both peers.</summary>
	private static async Task<(RpcPeer Client, RpcPeer Host)> ConnectAsync( string root, RemoteKeys hostKeys, RemoteKeys deviceKeys, string deviceId, string hostKey, Channel<RpcPeer> hostPeers, string when )
	{
		var session = await SecureChannel.ConnectAsync( await RelayDialer.ConnectAsync( root, hostKeys.HostId, CancellationToken.None ), deviceKeys, deviceId, hostKey, CancellationToken.None ).WaitAsync( Patience );
		CoreChecks.Check( session.HostId == hostKeys.HostId && session.Scopes.SequenceEqual( ["view", "control"] ), $"A paired device completes the handshake through the relay {when}." );
		var client = new RpcPeer( session.Connection );
		var pushed = new TaskCompletionSource<JsonObject>( TaskCreationOptions.RunContinuationsAsynchronously );
		client.OnPush( "note", frame => pushed.TrySetResult( frame ) );
		client.Start();
		var host = await hostPeers.Reader.ReadAsync().AsTask().WaitAsync( Patience );

		var request = Frame.Create( "echo" );
		request["value"] = 42;
		var answer = await client.RequestAsync( request, CancellationToken.None ).WaitAsync( Patience );
		CoreChecks.Check( Frame.ReadLong( answer, "value" ) == 42 && Frame.ReadString( answer, "device" ) == deviceId, $"A request through the relay gets the host's answer {when}." );
		await host.PushAsync( Frame.Create( "note" ).WithSeq( 9 ) );
		CoreChecks.Check( Frame.ReadSeq( await pushed.Task.WaitAsync( Patience ) ) == 9, $"A host push reaches the device through the relay {when}." );
		return (client, host);
	}

	/// <summary>Starts a relay again on <paramref name="port"/>, retrying while the port is still being released.</summary>
	private static async Task<Microsoft.AspNetCore.Builder.WebApplication> RestartAsync( int port )
	{
		var watch = Stopwatch.StartNew();
		while ( true )
		{
			var relay = RelayApp.Build( [$"http://127.0.0.1:{port}"], ["--Logging:LogLevel:Default=None"] );
			try
			{
				await relay.StartAsync();
				return relay;
			}
			catch ( IOException ) when ( watch.Elapsed < Patience )
			{
				await relay.DisposeAsync();
				await Task.Delay( 200 );
			}
		}
	}

	/// <summary>Stops a relay at once, aborting its open sockets instead of waiting for them to end.</summary>
	private static async Task StopAsync( Microsoft.AspNetCore.Builder.WebApplication relay )
	{
		using var now = new CancellationTokenSource( TimeSpan.FromMilliseconds( 500 ) );
		try
		{
			await relay.StopAsync( now.Token );
		}
		catch ( OperationCanceledException )
		{
			// The sockets were aborted, which is the point.
		}
	}

	/// <summary>Gets a relay's hub, which knows the connected hosts.</summary>
	private static RelayHub ReadHub( Microsoft.AspNetCore.Builder.WebApplication relay ) => relay.Services.GetRequiredService<RelayHub>();

	/// <summary>Polls <paramref name="condition"/> until it holds or <paramref name="patience"/> passes.</summary>
	private static async Task<bool> WaitUntilAsync( Func<bool> condition, TimeSpan patience )
	{
		var watch = Stopwatch.StartNew();
		while ( !condition() )
		{
			if ( watch.Elapsed > patience )
				return false;
			await Task.Delay( 20 );
		}

		return true;
	}

	/// <summary>Finds a free loopback TCP port.</summary>
	private static int FindFreePort()
	{
		var listener = new TcpListener( IPAddress.Loopback, 0 );
		listener.Start();
		var port = ( (IPEndPoint)listener.LocalEndpoint ).Port;
		listener.Stop();
		return port;
	}
}
