using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Lokn.Modules.Remote;

namespace Lokn.Tests;

/// <summary>Checks the remote protocol and secure channel of docs/remote.md: keys, pairing, the 3DH handshake, counters, revocation, and requests over memory, a named pipe and a WebSocket.</summary>
internal static class RemoteChecks
{
	/// <summary>Bounds every wait, so a broken check fails rather than hangs.</summary>
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds( 15 );

	/// <summary>Runs every remote check.</summary>
	internal static async Task RunAsync()
	{
		var folder = Path.Combine( Path.GetTempPath(), "lokn-remote-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( folder );
		try
		{
			CheckKeys( folder );
			CheckPairingLink();
			CheckPairingOffers();
			CheckRelayProof();
			CheckLimiter();
			await CheckPairingAsync( folder );
			await CheckPairedHostsAsync( folder );
			await CheckHandshakeAsync( folder );
			await CheckHandshakeDefensesAsync( folder );
			await CheckChannelRulesAsync( folder );
			await CheckRevocationAsync( folder );
			await CheckMemoryRpcAsync();
			await CheckBusyAsync();
			CheckSendQueue();
			await CheckPipeRpcAsync();
			await CheckWebSocketRpcAsync( folder );
			await CheckHttpHostAsync();
		}
		finally
		{
			Directory.Delete( folder, true );
		}
	}

	/// <summary>Checks that a key file is created once and loaded again, and the host id's form.</summary>
	private static void CheckKeys( string folder )
	{
		var path = Path.Combine( folder, "keys", "host-key.pem" );
		using var created = RemoteKeys.LoadOrCreate( path );
		using var loaded = RemoteKeys.LoadOrCreate( path );
		CoreChecks.Check( created.PublicKey.AsSpan().SequenceEqual( loaded.PublicKey ), "A saved remote key loads back as the same key." );
		CoreChecks.Check( created.HostId.Length == 22 && created.HostId == RemoteKeys.ComputeHostId( loaded.PublicKey ), "The host id is the first 22 characters of base64url( SHA-256( SPKI ) )." );
		CoreChecks.Check( !created.PublicKeyText.Contains( '=' ) && !created.PublicKeyText.Contains( '+' ) && !created.PublicKeyText.Contains( '/' ), "Public keys travel as base64url without padding." );
		if ( OperatingSystem.IsWindows() )
		{
			var devices = new DeviceStore( Path.Combine( folder, "keys", "devices.json" ) );
			Pair( devices, created );
			foreach ( var file in new[] { path, Path.Combine( folder, "keys", "devices.json" ) } )
			{
				var security = new FileInfo( file ).GetAccessControl();
				var rules = security.GetAccessRules( true, true, typeof( SecurityIdentifier ) ).Cast<FileSystemAccessRule>().ToArray();
				CoreChecks.Check( security.AreAccessRulesProtected && rules.Length == 1 && rules[0].IdentityReference == WindowsIdentity.GetCurrent().User
					&& rules[0].AccessControlType == AccessControlType.Allow, $"{Path.GetFileName( file )} grants only the current user, whatever its folder grants." );
			}
		}
	}

	/// <summary>Checks that pairing links round-trip in the native, web and bare forms, and reject altered data.</summary>
	private static void CheckPairingLink()
	{
		using var host = RemoteKeys.Create();
		var offers = new PairingOffers();
		var link = offers.Create( host, ["192.168.1.2:47390"], "https://relay.example" ).Link;
		var uri = link.BuildUri();
		CoreChecks.Check( uri.StartsWith( "loom://pair#", StringComparison.Ordinal ), "A pairing link uses loom://pair#." );
		var native = PairingLink.TryParse( uri );
		var web = PairingLink.TryParse( link.BuildWebUrl( "https://host.example/#old" ) );
		var bare = PairingLink.TryParse( link.EncodePayload() );
		CoreChecks.Check( native is not null && web is not null && bare is not null, "Native, web and bare pairing data parse." );
		CoreChecks.Check( native!.HostId == host.HostId && native.HostKey == host.PublicKeyText && native.Secret == link.Secret && native.Relay == "https://relay.example" && native.Addrs.SequenceEqual( ["192.168.1.2:47390"] ), "Pairing data keeps every field." );
		CoreChecks.Check( link.BuildWebUrl( "https://host.example/#old" ).StartsWith( "https://host.example/#pair=", StringComparison.Ordinal ), "The web form carries the data after #pair=." );
		using var other = RemoteKeys.Create();
		var forged = link with { HostId = other.HostId };
		CoreChecks.Check( PairingLink.TryParse( forged.BuildUri() ) is null, "A link whose host id does not match its key is refused." );
		CoreChecks.Check( PairingLink.TryParse( "loom://pair#not-base64!" ) is null, "Malformed pairing data is refused." );
		var relayless = PairingLink.TryParse( (link with { Relay = null }).BuildUri() );
		CoreChecks.Check( relayless is { Relay: null }, "A link without a relay omits it." );
	}

	/// <summary>Checks the pairing MAC: valid once, invalid, reused and expired; and an offer that grants control says so when used.</summary>
	private static void CheckPairingOffers()
	{
		using var host = RemoteKeys.Create();
		using var device = RemoteKeys.Create();
		var clock = new CheckClock();
		var offers = new PairingOffers( clock );
		var offer = offers.Create( host, [], null );
		var mac = PairingLink.ComputeMac( offer.Link.Secret, host.HostId, device.PublicKeyText );
		CoreChecks.Check( offer.Expires == clock.Now + TimeSpan.FromMinutes( 5 ) && !offer.Control, "A pairing secret is valid for 5 minutes and grants view only by default." );
		CoreChecks.Check( offers.TryConsume( host.HostId, device.PublicKeyText, PairingLink.ComputeMac( offer.Link.Secret, host.HostId, host.PublicKeyText ) ) is null, "A MAC over another device key is refused." );
		CoreChecks.Check( offers.TryConsume( host.HostId, device.PublicKeyText, "!!" ) is null, "A malformed MAC is refused." );
		CoreChecks.Check( offers.TryConsume( host.HostId, device.PublicKeyText, mac ) == offer, "A valid pairing MAC is accepted and answers its offer." );
		CoreChecks.Check( offers.TryConsume( host.HostId, device.PublicKeyText, mac ) is null, "A pairing secret works only once." );

		var late = offers.Create( host, [], null );
		clock.Now += TimeSpan.FromMinutes( 5 ) + TimeSpan.FromSeconds( 1 );
		CoreChecks.Check( offers.TryConsume( host.HostId, device.PublicKeyText, PairingLink.ComputeMac( late.Link.Secret, host.HostId, device.PublicKeyText ) ) is null, "An expired pairing secret is refused." );

		var cancelled = offers.Create( host, [], null );
		offers.Cancel( cancelled );
		CoreChecks.Check( offers.TryConsume( host.HostId, device.PublicKeyText, PairingLink.ComputeMac( cancelled.Link.Secret, host.HostId, device.PublicKeyText ) ) is null, "A withdrawn pairing secret is refused." );

		var older = offers.Create( host, [], null );
		var newer = offers.Create( host, [], null );
		PairingOffer? used = null;
		offers.Used += offer => used = offer;
		CoreChecks.Check( offers.TryConsume( host.HostId, device.PublicKeyText, PairingLink.ComputeMac( older.Link.Secret, host.HostId, device.PublicKeyText ) ) is null, "Showing a new pairing link withdraws the one before." );
		CoreChecks.Check( used is null && offers.TryConsume( host.HostId, device.PublicKeyText, PairingLink.ComputeMac( newer.Link.Secret, host.HostId, device.PublicKeyText ) ) is not null && used == newer, "A pairing raises Used with its offer." );
		var all = offers.Create( host, [], null );
		offers.CancelAll();
		CoreChecks.Check( offers.TryConsume( host.HostId, device.PublicKeyText, PairingLink.ComputeMac( all.Link.Secret, host.HostId, device.PublicKeyText ) ) is null, "CancelAll withdraws the live offer." );

		var control = offers.Create( host, [], null, control: true );
		CoreChecks.Check( offers.TryConsume( host.HostId, device.PublicKeyText, PairingLink.ComputeMac( control.Link.Secret, host.HostId, device.PublicKeyText ) ) is { Control: true },
			"An offer that grants control answers so when a device uses it." );
	}

	/// <summary>Checks the relay proof: bound to the host id, the relay's nonce and the socket's purpose, so it cannot be replayed elsewhere.</summary>
	private static void CheckRelayProof()
	{
		using var host = RemoteKeys.Create();
		using var other = RemoteKeys.Create();
		var hello = RelayProof.BuildHello( host, "nonce-1", RelayProof.ControlPurpose );
		var sig = Frame.ReadString( hello, "sig" )!;
		CoreChecks.Check( Frame.ReadType( hello ) == "relay.host" && Frame.ReadString( hello, "hostKey" ) == host.PublicKeyText && hello["ts"] is null, "The relay proof frame carries the key and signature, no time." );
		CoreChecks.Check( RelayProof.Verify( host.HostId, host.PublicKeyText, "nonce-1", RelayProof.ControlPurpose, sig ), "A relay proof verifies for its nonce and purpose." );
		CoreChecks.Check( !RelayProof.Verify( host.HostId, host.PublicKeyText, "nonce-2", RelayProof.ControlPurpose, sig ), "A relay proof does not answer another challenge." );
		CoreChecks.Check( !RelayProof.Verify( host.HostId, host.PublicKeyText, "nonce-1", RelayProof.BuildDataPurpose( "sid" ), sig ), "A control proof does not open a data socket." );
		CoreChecks.Check( !RelayProof.Verify( other.HostId, host.PublicKeyText, "nonce-1", RelayProof.ControlPurpose, sig ), "A relay proof for another host id is refused." );
		CoreChecks.Check( !RelayProof.Verify( other.HostId, other.PublicKeyText, "nonce-1", RelayProof.ControlPurpose, sig ), "A relay proof signed by another key is refused." );
	}

	/// <summary>Checks the connection limiter's total and per-address limits.</summary>
	private static void CheckLimiter()
	{
		var limiter = new ConnectionLimiter( 3, 2 );
		CoreChecks.Check( limiter.TryEnter( "a" ) && limiter.TryEnter( "a" ) && !limiter.TryEnter( "a" ), "One address gets at most its own limit." );
		CoreChecks.Check( limiter.TryEnter( null ) && !limiter.TryEnter( "b" ) && !limiter.TryEnter( null ), "The total limit holds across addresses." );
		limiter.Exit( "a" );
		CoreChecks.Check( limiter.TryEnter( "b" ) && limiter.Running == 3, "Leaving frees a place." );
	}

	/// <summary>Checks pairing over a connection: the device is stored with the view scope and raises Added, the link cannot pair twice, and a link that
	/// grants control pairs a device with control at once.</summary>
	private static async Task CheckPairingAsync( string folder )
	{
		using var host = RemoteKeys.Create();
		using var device = RemoteKeys.Create();
		var devices = new DeviceStore( Path.Combine( folder, "pairing", "devices.json" ) );
		var added = new List<PairedDevice>();
		devices.Added += added.Add;
		var offers = new PairingOffers();
		var link = PairingLink.TryParse( offers.Create( host, ["127.0.0.1:47390"], null ).Link.BuildUri() )!;
		var deviceId = Guid.NewGuid().ToString( "N" );

		var (hostEnd, deviceEnd) = MemoryConnection.CreatePair();
		var accepted = SecureChannel.AcceptAsync( hostEnd, host, devices, offers, CancellationToken.None );
		var scopes = await SecureChannel.PairAsync( deviceEnd, device, deviceId, "Phone", link, CancellationToken.None ).WaitAsync( Patience );
		CoreChecks.Check( await accepted.WaitAsync( Patience ) is null && hostEnd.IsClosed, "The host closes the connection after pairing." );
		CoreChecks.Check( scopes.SequenceEqual( ["view"] ), "A new pairing gets the view scope." );
		var stored = devices.Find( deviceId );
		CoreChecks.Check( stored is { Name: "Phone" } && stored.DeviceKey == device.PublicKeyText && stored.Scopes.SequenceEqual( ["view"] ) && added.Single().DeviceId == deviceId,
			"The host stores the paired device and raises Added with it." );
		var reloaded = new DeviceStore( Path.Combine( folder, "pairing", "devices.json" ) ).Find( deviceId );
		CoreChecks.Check( reloaded is not null && reloaded.DeviceKey == device.PublicKeyText && reloaded.Scopes.SequenceEqual( ["view"] ), "devices.json keeps the paired device." );

		(hostEnd, deviceEnd) = MemoryConnection.CreatePair();
		accepted = SecureChannel.AcceptAsync( hostEnd, host, devices, offers, CancellationToken.None );
		var refused = await CaptureAsync( SecureChannel.PairAsync( deviceEnd, device, Guid.NewGuid().ToString( "N" ), "Again", link, CancellationToken.None ) );
		await accepted.WaitAsync( Patience );
		CoreChecks.Check( refused is RemoteException { Code: "bad-request" } && devices.Devices.Count == 1 && added.Count == 1, "A used pairing link cannot pair another device." );

		using var trusted = RemoteKeys.Create();
		var controlLink = PairingLink.TryParse( offers.Create( host, ["127.0.0.1:47390"], null, control: true ).Link.BuildUri() )!;
		var trustedId = Guid.NewGuid().ToString( "N" );
		(hostEnd, deviceEnd) = MemoryConnection.CreatePair();
		accepted = SecureChannel.AcceptAsync( hostEnd, host, devices, offers, CancellationToken.None );
		var granted = await SecureChannel.PairAsync( deviceEnd, trusted, trustedId, "Laptop", controlLink, CancellationToken.None ).WaitAsync( Patience );
		await accepted.WaitAsync( Patience );
		CoreChecks.Check( granted.SequenceEqual( DeviceScopes.Controlling ) && devices.Find( trustedId )?.Scopes.SequenceEqual( DeviceScopes.Controlling ) == true,
			"A link that grants control pairs the device with view and control, and its paired frame says so." );

		// A device on the host's network asks without a link: the host's user sees the same code the device computes, and allows or denies it.
		using var nearby = RemoteKeys.Create();
		var knockId = Guid.NewGuid().ToString( "N" );
		KnockRequest? asked = null;
		(hostEnd, deviceEnd) = MemoryConnection.CreatePair();
		accepted = SecureChannel.AcceptAsync( hostEnd, host, devices, offers, CancellationToken.None, ( request, _ ) =>
		{
			asked = request;
			return Task.FromResult<IReadOnlyList<string>?>( DeviceScopes.Controlling );
		} );
		var allowed = await SecureChannel.KnockAsync( deviceEnd, nearby, knockId, "Laptop 2", host.HostId, CancellationToken.None ).WaitAsync( Patience );
		await accepted.WaitAsync( Patience );
		CoreChecks.Check( asked is { Name: "Laptop 2" } && asked.Code == SecureChannel.ComputeKnockCode( host.PublicKeyText, nearby.PublicKeyText ) && asked.Code.Length == 7
			&& allowed.SequenceEqual( DeviceScopes.Controlling ) && devices.Find( knockId )?.Scopes.SequenceEqual( DeviceScopes.Controlling ) == true,
			"A request without a link shows the host's user the code the device computes, and once allowed pairs the device with the scopes granted." );
		var deniedId = Guid.NewGuid().ToString( "N" );
		(hostEnd, deviceEnd) = MemoryConnection.CreatePair();
		accepted = SecureChannel.AcceptAsync( hostEnd, host, devices, offers, CancellationToken.None, ( _, _ ) => Task.FromResult<IReadOnlyList<string>?>( null ) );
		var denied = await CaptureAsync( SecureChannel.KnockAsync( deviceEnd, nearby, deniedId, "Stranger", host.HostId, CancellationToken.None ) );
		await accepted.WaitAsync( Patience );
		CoreChecks.Check( denied is RemoteException && devices.Find( deniedId ) is null, "A denied request pairs nothing." );
		(hostEnd, deviceEnd) = MemoryConnection.CreatePair();
		accepted = SecureChannel.AcceptAsync( hostEnd, host, devices, offers, CancellationToken.None );
		var unasked = await CaptureAsync( SecureChannel.KnockAsync( deviceEnd, nearby, Guid.NewGuid().ToString( "N" ), "Stranger", host.HostId, CancellationToken.None ) );
		await accepted.WaitAsync( Patience );
		CoreChecks.Check( unasked is RemoteException, "A host that takes no requests refuses them." );
	}

	/// <summary>Checks <c>remote-hosts.json</c>: the former <c>remote-host.json</c> is imported once and deleted, entries are added, replaced keeping
	/// their name, readdressed and removed, the file is owner-only, a damaged list is never overwritten, workspace frames may not reach it, and a
	/// pairing link of this PC itself is refused before anything is dialed.</summary>
	private static async Task CheckPairedHostsAsync( string folder )
	{
		var data = Path.Combine( folder, "paired-hosts" );
		Directory.CreateDirectory( data );
		CoreChecks.Check( PairedHosts.Read( data ).Count == 0 && !File.Exists( PairedHosts.ComputePath( data ) ), "A data folder without paired hosts reads none and writes nothing." );

		File.WriteAllText( PairedHosts.ComputeLegacyPath( data ), """{ "hostId": "hostAAAAAAAAAAAAAAAAAA", "hostKey": "key-a", "addrs": ["192.168.1.4:47390"], "relay": null, "deviceId": "device-a" }""" );
		var imported = PairedHosts.Read( data );
		CoreChecks.Check( imported is [{ HostId: "hostAAAAAAAAAAAAAAAAAA", HostKey: "key-a", DeviceId: "device-a", Name: "" }] && imported[0].Addrs.SequenceEqual( ["192.168.1.4:47390"] )
			&& imported[0].Added != default && !File.Exists( PairedHosts.ComputeLegacyPath( data ) ) && File.Exists( PairedHosts.ComputePath( data ) ),
			"The former remote-host.json is imported into remote-hosts.json without a name, then deleted." );
		CoreChecks.Check( PairedHosts.Read( data ).Count == 1, "The import happens once." );
		if ( OperatingSystem.IsWindows() )
		{
			var security = new FileInfo( PairedHosts.ComputePath( data ) ).GetAccessControl();
			var rules = security.GetAccessRules( true, true, typeof( SecurityIdentifier ) ).Cast<FileSystemAccessRule>().ToArray();
			CoreChecks.Check( security.AreAccessRulesProtected && rules.Length == 1 && rules[0].IdentityReference == WindowsIdentity.GetCurrent().User,
				"remote-hosts.json grants only the current user." );
		}

		PairedHosts.Save( data, [imported[0] with { Name = "Desk PC" }] );
		PairedHosts.Upsert( data, new PairedHost( "hostBBBBBBBBBBBBBBBBBB", "key-b", ["10.0.0.2:47390"], null, "device-b", "", DateTimeOffset.UtcNow ) );
		PairedHosts.Upsert( data, new PairedHost( "hostAAAAAAAAAAAAAAAAAA", "key-a", ["192.168.1.9:47390"], null, "device-a2", "", DateTimeOffset.UtcNow ) );
		var hosts = PairedHosts.Read( data );
		CoreChecks.Check( hosts is [{ HostId: "hostAAAAAAAAAAAAAAAAAA", DeviceId: "device-a2", Name: "Desk PC" }, { HostId: "hostBBBBBBBBBBBBBBBBBB" }],
			"Pairing again with a host replaces its entry in place and keeps the name the user gave it." );
		PairedHosts.UpdateReported( data, "hostBBBBBBBBBBBBBBBBBB", ["10.0.0.3:47390"], "Bravo PC" );
		PairedHosts.Upsert( data, new PairedHost( "hostBBBBBBBBBBBBBBBBBB", "key-b", ["10.0.0.3:47390"], null, "device-b2", "", DateTimeOffset.UtcNow ) );
		PairedHosts.Remove( data, "hostAAAAAAAAAAAAAAAAAA" );
		hosts = PairedHosts.Read( data );
		CoreChecks.Check( hosts is [{ HostId: "hostBBBBBBBBBBBBBBBBBB", HostName: "Bravo PC", DeviceId: "device-b2" }] && hosts[0].Addrs.SequenceEqual( ["10.0.0.3:47390"] )
			&& RemoteClient.ComputeName( hosts[0], "" ) == "Bravo PC",
			"A host's new addresses and reported name are saved, pairing again keeps the name until it reports another, an offline host shows it, and a removed host leaves the list." );

		const string damaged = "{ \"version\": 1, \"hosts\": [ broken";
		File.WriteAllText( PairedHosts.ComputePath( data ), damaged );
		File.WriteAllText( PairedHosts.ComputeLegacyPath( data ), """{ "hostId": "hostCCCCCCCCCCCCCCCCCC", "hostKey": "key-c", "addrs": [], "deviceId": "device-c" }""" );
		var unreadable = Capture( () => PairedHosts.Read( data ) );
		var unwritten = Capture( () => PairedHosts.Upsert( data, hosts[0] ) );
		CoreChecks.Check( unreadable is InvalidDataException && unwritten is InvalidDataException && File.ReadAllText( PairedHosts.ComputePath( data ) ) == damaged
			&& File.Exists( PairedHosts.ComputeLegacyPath( data ) ), "A damaged list is reported, never overwritten, and the former file waits until it can be imported." );
		File.WriteAllText( PairedHosts.ComputePath( data ), """{ "version": 2, "hosts": [] }""" );
		CoreChecks.Check( Capture( () => PairedHosts.Read( data ) ) is InvalidDataException, "A list of a newer format is not read." );
		CoreChecks.Check( WorkspaceGuard.IsSecretName( "remote-hosts.json" ) && WorkspaceGuard.IsSecretName( "REMOTE-HOSTS.JSON" ) && WorkspaceGuard.IsSecretName( "remote-host.json" ),
			"Workspace frames may not reach the paired hosts list or the former paired host file." );

		using var own = RemoteKeys.LoadOrCreate( Path.Combine( data, "host-key.pem" ) );
		var ownLink = new PairingOffers().Create( own, ["127.0.0.1:9"], null ).Link.BuildUri();
		var self = await CaptureAsync( HostPairing.PairAsync( data, ownLink, CancellationToken.None ) );
		CoreChecks.Check( self is ArgumentException { Message: "That pairing link is this PC's." }, "Pairing this PC with itself is refused." );
	}

	/// <summary>Runs an action and returns what it threw, or null.</summary>
	private static Exception? Capture( Action action )
	{
		try
		{
			action();
			return null;
		}
		catch ( Exception exception )
		{
			return exception;
		}
	}

	/// <summary>Checks the handshake's defenses: an oversized plaintext hello, a re-pairing of a paired device id, and a hello changed on the way.</summary>
	private static async Task CheckHandshakeDefensesAsync( string folder )
	{
		using var host = RemoteKeys.Create();
		using var device = RemoteKeys.Create();
		var devices = new DeviceStore( Path.Combine( folder, "defenses", "devices.json" ) );

		var (hostEnd, deviceEnd) = MemoryConnection.CreatePair();
		var accepted = SecureChannel.AcceptAsync( hostEnd, host, devices, new PairingOffers(), CancellationToken.None );
		await deviceEnd.SendMessageAsync( new( true, Encoding.UTF8.GetBytes( "{\"t\":\"hello\",\"pad\":\"" + new string( 'x', SecureChannel.HandshakeMaxBytes ) + "\"}" ) ) );
		CoreChecks.Check( await accepted.WaitAsync( Patience ) is null && hostEnd.IsClosed, "A plaintext handshake message over 4 KiB closes the connection." );

		var deviceId = Pair( devices, device );
		var offers = new PairingOffers();
		var link = PairingLink.TryParse( offers.Create( host, [], null ).Link.BuildUri() )!;
		using var thief = RemoteKeys.Create();
		(hostEnd, deviceEnd) = MemoryConnection.CreatePair();
		accepted = SecureChannel.AcceptAsync( hostEnd, host, devices, offers, CancellationToken.None );
		var repaired = await CaptureAsync( SecureChannel.PairAsync( deviceEnd, thief, deviceId, "Thief", link, CancellationToken.None ) );
		await accepted.WaitAsync( Patience );
		CoreChecks.Check( repaired is RemoteException { Code: "bad-request" } && devices.Find( deviceId )?.DeviceKey == device.PublicKeyText, "Pairing an already paired device id is refused and keeps its key." );

		devices.SetScopes( deviceId, DeviceScopes.Paired );
		(hostEnd, deviceEnd) = MemoryConnection.CreatePair();
		hostEnd.Intercept = message =>
		{
			if ( !message.IsText || JsonNode.Parse( message.Data ) is not JsonObject frame || Frame.ReadType( frame ) != "hello" )
				return message;
			frame["scopes"] = Frame.BuildStrings( DeviceScopes.Controlling );
			return message with { Data = Frame.Serialize( frame ) };
		};
		accepted = SecureChannel.AcceptAsync( hostEnd, host, devices, new PairingOffers(), CancellationToken.None );
		var tampered = await CaptureAsync( SecureChannel.ConnectAsync( deviceEnd, device, deviceId, host.PublicKeyText, CancellationToken.None ) );
		CoreChecks.Check( tampered is IOException && await accepted.WaitAsync( Patience ) is null, "A plaintext hello changed on the way breaks the keys, so the handshake fails." );
	}

	/// <summary>Checks the connect handshake both ways and frames in both directions after it.</summary>
	private static async Task CheckHandshakeAsync( string folder )
	{
		using var host = RemoteKeys.Create();
		using var device = RemoteKeys.Create();
		var devices = new DeviceStore( Path.Combine( folder, "handshake", "devices.json" ) );
		var deviceId = Pair( devices, device );
		devices.SetScopes( deviceId, DeviceScopes.Controlling );

		var (hostSession, deviceSession, _) = await ConnectAsync( host, device, devices, deviceId );
		CoreChecks.Check( hostSession.Device.DeviceId == deviceId && deviceSession.HostId == host.HostId, "Both sides learn who they talk to." );
		CoreChecks.Check( deviceSession.Scopes.SequenceEqual( ["view", "control"] ), "The host's hello carries the device's scopes." );

		await deviceSession.Connection.SendAsync( Frame.Create( "sync" ) );
		var received = await hostSession.Connection.ReceiveAsync( CancellationToken.None ).WaitAsync( Patience );
		CoreChecks.Check( received is not null && Frame.ReadType( received ) == "sync", "An encrypted frame reaches the host." );
		await hostSession.Connection.SendAsync( Frame.Create( "state" ).WithChannel( Guid.Empty ).WithSeq( 3 ) );
		var pushed = await deviceSession.Connection.ReceiveAsync( CancellationToken.None ).WaitAsync( Patience );
		CoreChecks.Check( pushed is not null && Frame.ReadSeq( pushed ) == 3 && Frame.ReadChannel( pushed ) == Guid.Empty, "An encrypted frame reaches the device." );

		using var stranger = RemoteKeys.Create();
		var (hostEnd, deviceEnd) = MemoryConnection.CreatePair();
		var accepted = SecureChannel.AcceptAsync( hostEnd, host, devices, new PairingOffers(), CancellationToken.None );
		var wrongHost = await CaptureAsync( SecureChannel.ConnectAsync( deviceEnd, device, deviceId, stranger.PublicKeyText, CancellationToken.None ) );
		CoreChecks.Check( wrongHost is RemoteException && await accepted.WaitAsync( Patience ) is null, "A device refuses a host whose id does not match the paired key." );

		var (impostorEnd, impostorDevice) = MemoryConnection.CreatePair();
		accepted = SecureChannel.AcceptAsync( impostorEnd, host, devices, new PairingOffers(), CancellationToken.None );
		var impostor = await CaptureAsync( SecureChannel.ConnectAsync( impostorDevice, stranger, deviceId, host.PublicKeyText, CancellationToken.None ) );
		CoreChecks.Check( impostor is IOException && await accepted.WaitAsync( Patience ) is null, "A device id used with another static key fails the handshake." );
	}

	/// <summary>Checks that a tampered byte, a replayed counter, a skipped counter and a text message each close the channel.</summary>
	private static async Task CheckChannelRulesAsync( string folder )
	{
		using var host = RemoteKeys.Create();
		using var device = RemoteKeys.Create();
		var devices = new DeviceStore( Path.Combine( folder, "rules", "devices.json" ) );
		var deviceId = Pair( devices, device );

		var (hostSession, deviceSession, deviceEnd) = await ConnectAsync( host, device, devices, deviceId );
		deviceEnd.Intercept = message =>
		{
			var changed = (byte[])message.Data.Clone();
			changed[^1] ^= 1;
			return message with { Data = changed };
		};
		await deviceSession.Connection.SendAsync( Frame.Create( "sync" ) );
		CoreChecks.Check( await hostSession.Connection.ReceiveAsync( CancellationToken.None ).WaitAsync( Patience ) is null, "A tampered byte closes the channel." );
		CoreChecks.Check( hostSession.Connection.CloseReason is not null && deviceEnd.IsClosed, "The close reaches the device and records why." );

		(hostSession, deviceSession, deviceEnd) = await ConnectAsync( host, device, devices, deviceId );
		CarrierMessage? recorded = null;
		deviceEnd.Intercept = message => recorded = message;
		await deviceSession.Connection.SendAsync( Frame.Create( "sync" ) );
		CoreChecks.Check( await hostSession.Connection.ReceiveAsync( CancellationToken.None ).WaitAsync( Patience ) is not null, "The first copy of a frame arrives." );
		await deviceEnd.SendRawAsync( recorded! );
		CoreChecks.Check( await hostSession.Connection.ReceiveAsync( CancellationToken.None ).WaitAsync( Patience ) is null && deviceEnd.IsClosed, "A replayed counter closes the channel." );

		(hostSession, deviceSession, deviceEnd) = await ConnectAsync( host, device, devices, deviceId );
		var dropped = false;
		deviceEnd.Intercept = message =>
		{
			if ( dropped )
				return message;
			dropped = true;
			return null;
		};
		await deviceSession.Connection.SendAsync( Frame.Create( "first" ) );
		await deviceSession.Connection.SendAsync( Frame.Create( "second" ) );
		CoreChecks.Check( await hostSession.Connection.ReceiveAsync( CancellationToken.None ).WaitAsync( Patience ) is null && deviceEnd.IsClosed, "An out-of-order counter closes the channel." );

		(hostSession, _, deviceEnd) = await ConnectAsync( host, device, devices, deviceId );
		await deviceEnd.SendAsync( Frame.Create( "sync" ) );
		CoreChecks.Check( await hostSession.Connection.ReceiveAsync( CancellationToken.None ).WaitAsync( Patience ) is null, "A plaintext frame after the handshake closes the channel." );
	}

	/// <summary>Checks that unknown and revoked devices get a plaintext revoked error, and that revoking raises the event.</summary>
	private static async Task CheckRevocationAsync( string folder )
	{
		using var host = RemoteKeys.Create();
		using var device = RemoteKeys.Create();
		var devices = new DeviceStore( Path.Combine( folder, "revoke", "devices.json" ) );

		var unknown = await CaptureAsync( ConnectAsync( host, device, devices, Guid.NewGuid().ToString( "N" ) ) );
		CoreChecks.Check( unknown is RemoteException { Code: "revoked" }, "An unknown device gets the revoked error." );

		var deviceId = Pair( devices, device );
		string? revokedId = null;
		devices.Revoked += id => revokedId = id;
		CoreChecks.Check( devices.Revoke( deviceId ) && revokedId == deviceId && devices.Find( deviceId ) is null, "Revoking deletes the device and raises the event its connections close on." );
		CoreChecks.Check( new DeviceStore( Path.Combine( folder, "revoke", "devices.json" ) ).Find( deviceId ) is null, "A revoked device stays deleted in devices.json." );
		var revoked = await CaptureAsync( ConnectAsync( host, device, devices, deviceId ) );
		CoreChecks.Check( revoked is RemoteException { Code: "revoked" }, "A revoked device gets the revoked error." );
	}

	/// <summary>Checks requests, answers, errors and pushes over an in-memory connection.</summary>
	private static async Task CheckMemoryRpcAsync()
	{
		var (first, second) = MemoryConnection.CreatePair();
		await ExerciseAsync( first, second, "memory" );
	}

	/// <summary>Checks that a connection runs at most <see cref="RpcPeer.MaxRunningRequests"/> requests at once and answers more <c>busy</c>, and that a peer's
	/// own requests wait their turn rather than draw <c>busy</c>.</summary>
	private static async Task CheckBusyAsync()
	{
		var (first, second) = MemoryConnection.CreatePair();
		var release = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
		await using ( var server = new RpcPeer( first ) )
		{
			server.On( "slow", async _ =>
			{
				await release.Task;
				return null;
			} );
			server.Start();
			for ( var id = 1; id <= RpcPeer.MaxRunningRequests + 1; id++ )
				await second.SendAsync( new JsonObject { ["t"] = "slow", ["id"] = id } );
			var refusal = await second.ReceiveAsync( CancellationToken.None ).WaitAsync( Patience );
			CoreChecks.Check( refusal is not null && Frame.ReadType( refusal ) == Frame.Err && Frame.ReadRe( refusal ) == RpcPeer.MaxRunningRequests + 1 && Frame.ReadString( refusal, "code" ) == RemoteErrors.Busy,
				"A request beyond the running limit is answered busy." );
			release.SetResult();
		}

		var (hostEnd, clientEnd) = MemoryConnection.CreatePair();
		var gate = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
		await using var host = new RpcPeer( hostEnd );
		await using var client = new RpcPeer( clientEnd );
		host.On( "slow", async _ =>
		{
			await gate.Task;
			return null;
		} );
		host.Start();
		client.Start();
		var requests = Enumerable.Range( 0, RpcPeer.MaxRunningRequests * 2 ).Select( _ => client.RequestAsync( Frame.Create( "slow" ), CancellationToken.None ) ).ToArray();
		gate.SetResult();
		CoreChecks.Check( await CaptureAsync( Task.WhenAll( requests ) ) is null, "A peer's own requests beyond the limit wait their turn and all succeed." );
	}

	/// <summary>Checks that a peer that stops reading overflows the send queue, which drops the connection.</summary>
	private static void CheckSendQueue()
	{
		var (first, _) = MemoryConnection.CreatePair();
		first.ReserveSend( MessageConnection.MaxQueuedBytes );
		var overflow = false;
		try
		{
			first.ReserveSend( 1 );
		}
		catch ( IOException )
		{
			overflow = true;
		}
		CoreChecks.Check( overflow && first.IsClosed, "A full send queue drops the connection." );
	}

	/// <summary>Checks requests, answers, errors and pushes over a real named pipe with a unique name.</summary>
	private static async Task CheckPipeRpcAsync()
	{
		// Named pipes with a user-only ACL exist on Windows only, as Loom does.
		if ( !OperatingSystem.IsWindows() )
			return;

		var name = PipeHost.ComputePipeName() + "-check-" + Guid.NewGuid().ToString( "N" )[..8];
		CoreChecks.Check( PipeHost.ComputePipeName().StartsWith( "loom-host-S-1-", StringComparison.Ordinal ), "The pipe name carries the user's SID." );
		CoreChecks.Check( PipeHost.ComputePipeName( @"C:\Bare" ) == PipeHost.ComputePipeName( @"c:\bare\" ) && PipeHost.ComputePipeName( @"C:\Bare" ) != PipeHost.ComputePipeName(), "A bare daemon's pipe name adds a stable hash of its folder." );
		CoreChecks.Check( await PipeDialer.ConnectAsync( name, TimeSpan.FromMilliseconds( 200 ), CancellationToken.None ) is null, "Dialing an absent pipe answers null." );

		var served = new TaskCompletionSource<IFrameConnection>( TaskCreationOptions.RunContinuationsAsynchronously );
		var finished = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
		await using var host = new PipeHost( name, async connection =>
		{
			served.TrySetResult( connection );
			await finished.Task;
		} );
		host.Start();
		var client = await PipeDialer.ConnectAsync( name, Patience, CancellationToken.None );
		CoreChecks.Check( client is not null, "The pipe host accepts a client." );
		var server = await served.Task.WaitAsync( Patience );
		await ExerciseAsync( server, client!, "named pipe" );
		finished.TrySetResult();
	}

	/// <summary>Checks a device pairing, connecting and making requests through a WebSocket host on a loopback port the system picks, and a mapped GET path.</summary>
	private static async Task CheckWebSocketRpcAsync( string folder )
	{
		using var hostKeys = RemoteKeys.Create();
		using var device = RemoteKeys.Create();
		var devices = new DeviceStore( Path.Combine( folder, "websocket", "devices.json" ) );
		var offers = new PairingOffers();
		var sessions = new TaskCompletionSource<HostSession>( TaskCreationOptions.RunContinuationsAsynchronously );
		var finished = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
		await using var host = new WebSocketHost( [new IPEndPoint( IPAddress.Loopback, 0 )], async connection =>
		{
			if ( await SecureChannel.AcceptAsync( connection, hostKeys, devices, offers, CancellationToken.None ) is not { } session )
				return;
			sessions.TrySetResult( session );
			await finished.Task;
		} );
		host.MapGet( "/hello", _ => new HostResponse( 200 ) { Headers = { ["Content-Type"] = "text/plain" }, Body = "hi"u8.ToArray() } );
		host.Start();
		var port = host.Endpoints[0].Port;
		CoreChecks.Check( port != 0, "A host started on port 0 reports the port it took." );

		using ( var http = new HttpClient() )
		{
			var text = await http.GetStringAsync( $"http://127.0.0.1:{port}/hello" ).WaitAsync( Patience );
			var missing = await http.GetAsync( $"http://127.0.0.1:{port}/nothing" ).WaitAsync( Patience );
			CoreChecks.Check( text == "hi" && missing.StatusCode == HttpStatusCode.NotFound, "The WebSocket host serves mapped GET paths and 404 for others." );
		}

		var uri = WebSocketDialer.BuildHostUri( $"127.0.0.1:{port}", 47390 );
		CoreChecks.Check( uri.AbsolutePath == "/loom" && uri.Port == port && WebSocketDialer.BuildHostUri( "host", 47390 ).Port == 47390, "Host addresses become ws://host:port/loom." );
		var link = offers.Create( hostKeys, [$"127.0.0.1:{port}"], null ).Link;
		var deviceId = Guid.NewGuid().ToString( "N" );
		var scopes = await SecureChannel.PairAsync( await WebSocketDialer.ConnectAsync( uri, CancellationToken.None ), device, deviceId, "Laptop", link, CancellationToken.None ).WaitAsync( Patience );
		CoreChecks.Check( scopes.SequenceEqual( ["view"] ), "A device pairs over the WebSocket." );

		var deviceSession = await SecureChannel.ConnectAsync( await WebSocketDialer.ConnectAsync( uri, CancellationToken.None ), device, deviceId, link.HostKey, CancellationToken.None ).WaitAsync( Patience );
		var hostSession = await sessions.Task.WaitAsync( Patience );
		await ExerciseAsync( hostSession.Connection, deviceSession.Connection, "WebSocket" );
		finished.TrySetResult();
	}

	/// <summary>Checks the WebSocket host's HTTP server on two loopback addresses at once without a URL reservation: mapped GET paths under any
	/// <c>Host</c>, the RFC 6455 upgrade, and 400 with a close for bad requests.</summary>
	private static async Task CheckHttpHostAsync()
	{
		var port = FindFreePort();
		var second = IPAddress.Parse( "127.0.0.2" );
		var echoed = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
		await using var host = new WebSocketHost( [new IPEndPoint( IPAddress.Loopback, port ), new IPEndPoint( second, port )], async connection =>
		{
			if ( await connection.ReceiveMessageAsync( CancellationToken.None ) is not { } message )
				return;
			await connection.SendMessageAsync( message );
			echoed.TrySetResult();
		} );
		host.MapGet( "/hello", request => new HostResponse( 200 ) { Headers = { ["Content-Type"] = "text/plain" }, Body = Encoding.UTF8.GetBytes( "hi " + request["Host"] ) } );
		host.Start();
		CoreChecks.Check( host.Endpoints.Select( endpoint => endpoint.ToString() ).SequenceEqual( [$"127.0.0.1:{port}", $"127.0.0.2:{port}"] ), "The host listens on each address it is given." );

		using ( var http = new HttpClient() )
		{
			foreach ( var address in new[] { "127.0.0.1", "127.0.0.2" } )
			{
				var text = await http.GetStringAsync( $"http://{address}:{port}/hello" ).WaitAsync( Patience );
				CoreChecks.Check( text == $"hi {address}:{port}", $"The host serves mapped GET paths on {address}." );
			}

			using var proxied = new HttpRequestMessage( HttpMethod.Get, $"http://127.0.0.2:{port}/hello" );
			proxied.Headers.Host = "desk.tail1234.ts.net";
			using var proxiedAnswer = await http.SendAsync( proxied ).WaitAsync( Patience );
			CoreChecks.Check( proxiedAnswer.StatusCode == HttpStatusCode.OK && await proxiedAnswer.Content.ReadAsStringAsync() == "hi desk.tail1234.ts.net",
				"The host answers whatever Host a proxy such as tailscale serve sends." );

			using var head = await http.SendAsync( new HttpRequestMessage( HttpMethod.Head, $"http://127.0.0.1:{port}/hello" ) ).WaitAsync( Patience );
			CoreChecks.Check( head.StatusCode == HttpStatusCode.OK && head.Content.Headers.ContentLength == $"hi 127.0.0.1:{port}".Length && (await head.Content.ReadAsByteArrayAsync()).Length == 0,
				"HEAD gets the length without the body." );
			using var posted = await http.PostAsync( $"http://127.0.0.1:{port}/hello", new StringContent( "body" ) ).WaitAsync( Patience );
			CoreChecks.Check( posted.StatusCode == HttpStatusCode.BadRequest, "A request with a body is answered 400." );
		}

		using ( var socket = new ClientWebSocket() )
		{
			await socket.ConnectAsync( new Uri( $"ws://127.0.0.2:{port}/loom" ), CancellationToken.None ).WaitAsync( Patience );
			await using var connection = new WebSocketConnection( socket, "host" );
			await connection.SendMessageAsync( new CarrierMessage( true, "ping"u8.ToArray() ) );
			var reply = await connection.ReceiveMessageAsync( CancellationToken.None ).WaitAsync( Patience );
			await echoed.Task.WaitAsync( Patience );
			CoreChecks.Check( reply is { IsText: true } && Encoding.UTF8.GetString( reply.Data ) == "ping", "A WebSocket on the second loopback address reaches the accept handler and back." );
		}

		// The key and answer are RFC 6455's example.
		var handshake = await SendRawAsync( second, port, "GET /loom HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: keep-alive, Upgrade\r\n"
			+ "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n", true );
		CoreChecks.Check( handshake.StartsWith( "HTTP/1.1 101 ", StringComparison.Ordinal ) && handshake.Contains( "\r\nSec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n", StringComparison.Ordinal ),
			"The upgrade answers 101 with the RFC 6455 Sec-WebSocket-Accept." );
		var version = await SendRawAsync( IPAddress.Loopback, port, "GET /loom HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
			+ "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 8\r\n\r\n", false );
		CoreChecks.Check( version.StartsWith( "HTTP/1.1 426 ", StringComparison.Ordinal ) && version.Contains( "\r\nSec-WebSocket-Version: 13\r\n", StringComparison.Ordinal ), "Another WebSocket version is answered 426 with version 13." );

		foreach ( var (request, what) in new[]
		{
			("GET /loom HTTP/1.1\r\nHost: x\r\n\r\n", "A request to /loom without upgrade headers"),
			("GET /hello HTTP/1.1\r\nHost: x\r\nX-Filler: " + new string( 'a', HostRequest.MaxHeadBytes ) + "\r\n\r\n", "A request with headers over 8 KiB"),
			("GET /hello HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\n\r\nhello", "A GET with a body"),
			("NOT HTTP\r\n\r\n", "A malformed request line"),
			("GET /hello HTTP/1.1\r\n folded: x\r\n\r\n", "A folded header line"),
		} )
		{
			var answer = await SendRawAsync( IPAddress.Loopback, port, request, false );
			CoreChecks.Check( answer.StartsWith( "HTTP/1.1 400 ", StringComparison.Ordinal ) && answer.Contains( "\r\nConnection: close\r\n", StringComparison.Ordinal ), $"{what} is answered 400 and closed." );
		}

		await host.DisposeAsync();
		var refused = false;
		try
		{
			using var late = new TcpClient();
			await late.ConnectAsync( second, port ).WaitAsync( Patience );
		}
		catch ( SocketException )
		{
			refused = true;
		}

		CoreChecks.Check( refused, "A disposed host stops listening." );
	}

	/// <summary>Sends raw request text to a host and reads its answer: the status line and headers only, or everything until the host closes.</summary>
	private static async Task<string> SendRawAsync( IPAddress address, int port, string request, bool headOnly )
	{
		using var client = new TcpClient();
		await client.ConnectAsync( address, port ).WaitAsync( Patience );
		var stream = client.GetStream();
		await stream.WriteAsync( Encoding.ASCII.GetBytes( request ) ).AsTask().WaitAsync( Patience );
		using var answer = new MemoryStream();
		var buffer = new byte[4096];
		while ( !(headOnly && answer.GetBuffer().AsSpan( 0, (int)answer.Length ).IndexOf( "\r\n\r\n"u8 ) >= 0) )
		{
			var read = await stream.ReadAsync( buffer ).AsTask().WaitAsync( Patience );
			if ( read == 0 )
				break;
			answer.Write( buffer, 0, read );
		}
		return Encoding.ASCII.GetString( answer.GetBuffer(), 0, (int)answer.Length );
	}

	/// <summary>Runs requests, answers, errors, nested requests and pushes between two peers over two connected ends, then checks the close.</summary>
	private static async Task ExerciseAsync( IFrameConnection serverEnd, IFrameConnection clientEnd, string carrier )
	{
		var server = new RpcPeer( serverEnd );
		var client = new RpcPeer( clientEnd );
		server
			.On( "echo", frame => Task.FromResult<JsonObject?>( new() { ["value"] = frame["value"]?.DeepClone() } ) )
			.On( "refuse", _ => throw new RemoteException( RemoteErrors.Scope, "Needs control." ) )
			.On( "crash", _ => throw new InvalidOperationException( "Boom" ) )
			.On( "ask", async _ =>
			{
				var answer = await server.RequestAsync( Frame.Create( "client.ping" ), CancellationToken.None );
				await server.PushAsync( Frame.Create( "note" ).WithSeq( 7 ) );
				return new() { ["pong"] = Frame.ReadString( answer, "pong" ) };
			} );
		var note = new TaskCompletionSource<JsonObject>( TaskCreationOptions.RunContinuationsAsynchronously );
		client
			.On( "client.ping", _ => Task.FromResult<JsonObject?>( new() { ["pong"] = "yes" } ) )
			.OnPush( "note", frame => note.TrySetResult( frame ) );
		var serverClosed = new TaskCompletionSource<Exception?>( TaskCreationOptions.RunContinuationsAsynchronously );
		server.Closed += failure => serverClosed.TrySetResult( failure );
		server.Start();
		client.Start();

		var echoes = await Task.WhenAll( Enumerable.Range( 0, 20 ).Select( index =>
		{
			var request = Frame.Create( "echo" );
			request["value"] = index;
			return client.RequestAsync( request, CancellationToken.None );
		} ) ).WaitAsync( Patience );
		CoreChecks.Check( echoes.Select( answer => Frame.ReadLong( answer, "value" ) ).SequenceEqual( Enumerable.Range( 0, 20 ).Select( index => (long?)index ) )
			&& echoes.All( answer => Frame.ReadType( answer ) == "ok" && Frame.ReadRe( answer ) is not null ), $"Concurrent requests get their own ok answers over the {carrier}." );

		var refused = await CaptureAsync( client.RequestAsync( Frame.Create( "refuse" ), CancellationToken.None ) );
		CoreChecks.Check( refused is RemoteException { Code: "scope", Message: "Needs control." }, $"A refusal arrives as err with its code over the {carrier}." );
		var crashed = await CaptureAsync( client.RequestAsync( Frame.Create( "crash" ), CancellationToken.None ) );
		CoreChecks.Check( crashed is RemoteException { Code: "failed", Message: "Boom" }, $"A failing handler answers err failed over the {carrier}." );
		var unknown = await CaptureAsync( client.RequestAsync( Frame.Create( "nope" ), CancellationToken.None ) );
		CoreChecks.Check( unknown is RemoteException { Code: "bad-request" }, $"An unknown request answers err bad-request over the {carrier}." );

		var asked = await client.RequestAsync( Frame.Create( "ask" ), CancellationToken.None ).WaitAsync( Patience );
		CoreChecks.Check( Frame.ReadString( asked, "pong" ) == "yes", $"A handler can make its own request back over the {carrier}." );
		CoreChecks.Check( Frame.ReadSeq( await note.Task.WaitAsync( Patience ) ) == 7, $"Pushes arrive over the {carrier}." );

		using var cancel = new CancellationTokenSource();
		server.On( "ask-never", _ => new TaskCompletionSource<JsonObject?>().Task );
		var never = client.RequestAsync( Frame.Create( "ask-never" ), cancel.Token );
		await cancel.CancelAsync();
		CoreChecks.Check( await CaptureAsync( never ) is OperationCanceledException, $"A request can be cancelled over the {carrier}." );

		var pending = new TaskCompletionSource<JsonObject?>();
		var slowStarted = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
		server.On( "slow", _ =>
		{
			slowStarted.TrySetResult();
			return pending.Task;
		} );
		var orphan = client.RequestAsync( Frame.Create( "slow" ), CancellationToken.None );
		await slowStarted.Task.WaitAsync( Patience );
		await client.DisposeAsync();
		CoreChecks.Check( await CaptureAsync( orphan ) is IOException, $"Closing fails pending requests over the {carrier}." );
		await serverClosed.Task.WaitAsync( Patience );
		CoreChecks.Check( server.IsClosed && client.IsClosed, $"Both peers see the close over the {carrier}." );
		pending.TrySetResult( null );
	}

	/// <summary>Stores a paired device with the view scope directly, as pairing would.</summary>
	private static string Pair( DeviceStore devices, RemoteKeys device )
	{
		var deviceId = Guid.NewGuid().ToString( "N" );
		devices.Save( new() { DeviceId = deviceId, Name = "Check", DeviceKey = device.PublicKeyText, Scopes = DeviceScopes.Paired, Paired = DateTimeOffset.UtcNow } );
		return deviceId;
	}

	/// <summary>Runs both sides of the connect handshake over a memory pair.</summary>
	/// <exception cref="InvalidOperationException">The host refused while the device succeeded.</exception>
	private static async Task<(HostSession Host, DeviceSession Device, MemoryConnection DeviceEnd)> ConnectAsync( RemoteKeys host, RemoteKeys device, DeviceStore devices, string deviceId )
	{
		var (hostEnd, deviceEnd) = MemoryConnection.CreatePair();
		var accepted = SecureChannel.AcceptAsync( hostEnd, host, devices, new PairingOffers(), CancellationToken.None );
		var deviceSession = await SecureChannel.ConnectAsync( deviceEnd, device, deviceId, host.PublicKeyText, CancellationToken.None ).WaitAsync( Patience );
		var hostSession = await accepted.WaitAsync( Patience ) ?? throw new InvalidOperationException( "The host refused a handshake the device completed." );
		return (hostSession, deviceSession, deviceEnd);
	}

	/// <summary>Awaits a task within the patience and returns the exception it failed with, or null.</summary>
	private static async Task<Exception?> CaptureAsync( Task task )
	{
		try
		{
			await task.WaitAsync( Patience );
			return null;
		}
		catch ( TimeoutException exception ) when ( !task.IsCompleted )
		{
			throw new InvalidOperationException( "A remote check timed out.", exception );
		}
		catch ( Exception exception )
		{
			return exception;
		}
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

	/// <summary>A clock the checks move by hand.</summary>
	private sealed class CheckClock : TimeProvider
	{
		/// <summary>Gets or sets the current time.</summary>
		public DateTimeOffset Now { get; set; } = new( 2026, 11, 18, 12, 0, 0, TimeSpan.Zero );

		/// <summary>Returns <see cref="Now"/>.</summary>
		public override DateTimeOffset GetUtcNow() => Now;
	}
}
