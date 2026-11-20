using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lokn.Modules.Remote;

namespace Lokn.Tests;

/// <summary>Checks the web client side of docs/remote.md: RFC 8291 push encryption, RFC 8292 VAPID tokens, the push service's subscriptions and delivery,
/// the QR code encoder, and the web client's files served by a real WebSocket host.</summary>
internal static class RemoteWebChecks
{
	/// <summary>Bounds every wait, so a broken check fails rather than hangs.</summary>
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds( 15 );

	/// <summary>Holds the "HELLO WORLD" symbol a reference encoder (Nayuki's qrcodegen: byte mode, level M, mask chosen by penalty) produces, rows top to bottom.</summary>
	private static readonly string[] HelloWorld =
	[
		"111111101100101111111",
		"100000100001001000001",
		"101110100101001011101",
		"101110101001001011101",
		"101110101110101011101",
		"100000101001001000001",
		"111111101010101111111",
		"000000001001100000000",
		"100010111111011111001",
		"000100001011100001111",
		"001111110011011010010",
		"111110001100010000000",
		"111110101010101100110",
		"000000001010111101011",
		"111111101110101011010",
		"100000100101110110011",
		"101110101101011000110",
		"101110100100100011011",
		"101110100111000111000",
		"100000100001010000000",
		"111111101111111110101",
	];

	/// <summary>Runs every web client check.</summary>
	internal static async Task RunAsync()
	{
		var folder = Path.Combine( Path.GetTempPath(), "lokn-remote-web-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( folder );
		try
		{
			CheckPushEncryption();
			CheckVapidToken();
			CheckQrCode();
			await CheckPushServiceAsync( folder );
			await CheckPushDeliveryAsync( folder );
			await CheckAssetsAsync();
		}
		finally
		{
			Directory.Delete( folder, true );
		}
	}

	/// <summary>Checks the example of RFC 8291 section 5 byte for byte, and that a message with a fresh key and salt decrypts for its receiver.</summary>
	private static void CheckPushEncryption()
	{
		var receiverPoint = Base64Url.DecodeFromChars( "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4" );
		var senderPoint = Base64Url.DecodeFromChars( "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8" );
		var auth = Base64Url.DecodeFromChars( "BTBZMqHH6r4Tts7J_aSIgg" );
		var salt = Base64Url.DecodeFromChars( "DGv6ra1nlYgDCS1FRnbzlw" );
		var plaintext = "When I grow up, I want to be a watermelon"u8.ToArray();
		var expected = Base64Url.DecodeFromChars(
			"DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPT"
			+ "pK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN" );

		using var sender = ImportPrivate( "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw", senderPoint );
		var body = WebPushCrypto.Encrypt( plaintext, receiverPoint, auth, sender, salt );
		CoreChecks.Check( body.AsSpan().SequenceEqual( expected ), "Web push encryption reproduces the RFC 8291 example message exactly." );

		using var receiver = ImportPrivate( "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94", receiverPoint );
		CoreChecks.Check( DecryptPush( expected, receiver, receiverPoint, auth ).AsSpan().SequenceEqual( plaintext ), "The test's receiver decrypts the RFC 8291 example." );
		var fresh = WebPushCrypto.Encrypt( plaintext, receiverPoint, auth );
		CoreChecks.Check( !fresh.AsSpan( 0, 16 ).SequenceEqual( salt ) && DecryptPush( fresh, receiver, receiverPoint, auth ).AsSpan().SequenceEqual( plaintext ),
			"A push message with a fresh key and salt decrypts for its receiver." );
		CoreChecks.Check( BinaryPrimitives.ReadUInt32BigEndian( fresh.AsSpan( 16 ) ) == 4096 && fresh[20] == 65 && fresh.Length == 86 + plaintext.Length + 1 + 16,
			"A push message is one aes128gcm record of size 4096 with the sender's 65-byte key in the header." );

		var refused = false;
		try
		{
			WebPushCrypto.Encrypt( new byte[WebPushCrypto.MaxPlaintext + 1], receiverPoint, auth );
		}
		catch ( ArgumentException )
		{
			refused = true;
		}
		CoreChecks.Check( refused, "A push message longer than one record is refused." );
	}

	/// <summary>Checks that a VAPID token is an ES256 JWT for the endpoint's origin, with expiry and contact, that verifies with the public key the browser gets.</summary>
	private static void CheckVapidToken()
	{
		using var key = ECDsa.Create( ECCurve.NamedCurves.nistP256 );
		var expires = DateTimeOffset.UtcNow + PushService.TokenLifetime;
		var audience = WebPushCrypto.ComputeAudience( new Uri( "https://push.example.net:8443/push/JzLQ3raZJfFBR0aqvOMsLrt54w4rJUsV" ) );
		CoreChecks.Check( audience == "https://push.example.net:8443" && WebPushCrypto.ComputeAudience( new Uri( "https://fcm.googleapis.com/fcm/send/abc" ) ) == "https://fcm.googleapis.com",
			"The VAPID audience is the endpoint's origin." );

		var token = WebPushCrypto.BuildVapidToken( key, audience, expires, PushService.Subject );
		var parts = token.Split( '.' );
		CoreChecks.Check( parts.Length == 3, "A VAPID token has a header, claims and a signature." );
		var header = JsonNode.Parse( Base64Url.DecodeFromChars( parts[0] ) )!.AsObject();
		var claims = JsonNode.Parse( Base64Url.DecodeFromChars( parts[1] ) )!.AsObject();
		CoreChecks.Check( (string?)header["alg"] == "ES256" && (string?)header["typ"] == "JWT", "A VAPID token is an ES256 JWT." );
		CoreChecks.Check( (string?)claims["aud"] == audience && (long?)claims["exp"] == expires.ToUnixTimeSeconds() && (string?)claims["sub"] == "mailto:loom@localhost",
			"A VAPID token names the audience, the expiry and the contact." );

		using var verifier = ImportVerifier( Base64Url.EncodeToString( WebPushCrypto.ExportPoint( key.ExportParameters( false ) ) ) );
		var signed = Encoding.ASCII.GetBytes( parts[0] + "." + parts[1] );
		var signature = Base64Url.DecodeFromChars( parts[2] );
		CoreChecks.Check( signature.Length == 64 && verifier.VerifyData( signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation ),
			"A VAPID token's signature verifies with the public key as the browser gets it." );
		signed[^1] ^= 1;
		CoreChecks.Check( !verifier.VerifyData( signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation ), "A changed VAPID token fails to verify." );
	}

	/// <summary>Checks the QR encoder against a reference encoder, reads its format and data back, and checks sizes and limits.</summary>
	private static void CheckQrCode()
	{
		var modules = QrCode.Encode( "HELLO WORLD" );
		CoreChecks.Check( modules.GetLength( 0 ) == 21 && modules.GetLength( 1 ) == 21, "Short text makes a version 1 symbol of 21 modules." );
		CoreChecks.Check( ReadRows( modules ).SequenceEqual( HelloWorld ), "The symbol for HELLO WORLD equals the reference encoder's, module for module." );

		foreach ( var (x, y) in new[] { (0, 0), (14, 0), (0, 14) } )
		{
			var isFinder = true;
			for ( var dy = 0; dy < 7; dy++ )
			{
				for ( var dx = 0; dx < 7; dx++ )
				{
					var ring = Math.Max( Math.Abs( dx - 3 ), Math.Abs( dy - 3 ) );
					isFinder &= modules[y + dy, x + dx] == (ring != 2);
				}
			}
			CoreChecks.Check( isFinder, $"A finder pattern sits at ({x}, {y})." );
		}

		var (level, mask) = ReadFormat( modules );
		CoreChecks.Check( level == 0 && mask is >= 0 and < 8, "The format information decodes to level M and a mask." );
		CoreChecks.Check( modules[21 - 8, 8], "The dark module is set." );

		// The data codewords read back from the unmasked data region: mode 0100, count 11, the bytes, terminator, then pad bytes.
		var codewords = ReadCodewords( modules, mask );
		var expected = new List<bool>();
		AppendBits( expected, 0b0100, 4 );
		AppendBits( expected, 11, 8 );
		foreach ( var value in "HELLO WORLD"u8 )
			AppendBits( expected, value, 8 );
		AppendBits( expected, 0, 4 );
		for ( var pad = 0xEC; expected.Count < 16 * 8; pad ^= 0xEC ^ 0x11 )
			AppendBits( expected, pad, 8 );
		var data = Enumerable.Range( 0, 16 ).Select( index => (byte)expected.Skip( index * 8 ).Take( 8 ).Aggregate( 0, ( value, bit ) => (value << 1) | (bit ? 1 : 0) ) ).ToArray();
		CoreChecks.Check( codewords.Length == 26 && codewords.AsSpan( 0, 16 ).SequenceEqual( data ), "The data codewords read back from the symbol." );

		var mixed = string.Concat( Enumerable.Range( 0, 400 ).Select( index => (char)(33 + index * 7 % 90) ) ) + "\u00FC\u20AC";
		var large = QrCode.Encode( mixed );
		CoreChecks.Check( large.GetLength( 0 ) == QrCode.ComputeSize( 15 ) && HashRows( large ) == "dbfd007b824ff0594785f8e6f799445a9e66edb4309aecfee9fe1b1918cd9fda",
			"A 406-byte UTF-8 text makes the reference encoder's version 15 symbol, with version information and several blocks." );
		var largest = QrCode.Encode( new string( 'x', 997 ) );
		CoreChecks.Check( largest.GetLength( 0 ) == QrCode.ComputeSize( 25 ) && HashRows( largest ) == "c0e86ef35116023b2141935e0589ee183c26a259dc2c2bdb285a060115bda812",
			"997 bytes fill version 25 as the reference encoder does." );

		var refused = false;
		try
		{
			QrCode.Encode( new string( 'x', 998 ) );
		}
		catch ( ArgumentException )
		{
			refused = true;
		}
		CoreChecks.Check( refused, "Text beyond version 25 is refused." );
	}

	/// <summary>Checks the VAPID key file, <c>push.key</c>, <c>push.subscribe</c> and its validation, saved subscriptions, and revocation.</summary>
	private static async Task CheckPushServiceAsync( string folder )
	{
		var keyPath = Path.Combine( folder, "service", "vapid-key.pem" );
		var devices = new DeviceStore( Path.Combine( folder, "service", "devices.json" ) );
		var deviceId = SaveDevice( devices );
		using var receiver = ECDiffieHellman.Create( ECCurve.NamedCurves.nistP256 );
		var p256dh = Base64Url.EncodeToString( WebPushCrypto.ExportPoint( receiver.ExportParameters( false ) ) );
		var auth = Base64Url.EncodeToString( RandomNumberGenerator.GetBytes( 16 ) );

		string publicKey;
		using ( var push = new PushService( devices, keyPath ) )
		{
			publicKey = push.PublicKey;
			var point = Base64Url.DecodeFromChars( publicKey );
			CoreChecks.Check( File.Exists( keyPath ) && point.Length == 65 && point[0] == 4, "The push service creates its VAPID key and offers it as an uncompressed point." );

			var (first, second) = MemoryConnection.CreatePair();
			await using var hostPeer = new RpcPeer( first );
			await using var devicePeer = new RpcPeer( second );
			push.Register( new RemoteConnection( deviceId, "Phone", DeviceScopes.Paired, hostPeer ) );
			hostPeer.Start();
			devicePeer.Start();

			var key = await devicePeer.RequestAsync( Frame.Create( "push.key" ), CancellationToken.None ).WaitAsync( Patience );
			CoreChecks.Check( Frame.ReadString( key, "key" ) == publicKey, "push.key answers the VAPID public key." );

			await devicePeer.RequestAsync( BuildSubscribe( "https://push.example.net/push/one", p256dh, auth ), CancellationToken.None ).WaitAsync( Patience );
			CoreChecks.Check( push.FindSubscriptions( deviceId ) is [{ Endpoint: "https://push.example.net/push/one" } saved] && saved.P256dh == p256dh && saved.Auth == auth,
				"push.subscribe saves the subscription for the device, even with the view scope." );

			string[] privateEndpoints = ["https://127.0.0.1/push", "https://localhost/push", "https://192.168.1.1/push", "https://10.0.0.5/push", "https://169.254.169.254/latest", "https://100.100.100.100/push",
				"https://[::1]/push", "https://[fd00::1]/push", "https://[::ffff:127.0.0.1]/push", "https://router.local/push", "https://intranet/push", "https://push.example.net:8443/push"];
			var malformed = new[] { ("http://push.example.net/push/two", p256dh, auth), ("https://push.example.net/push/two", "AAAA", auth), ("https://push.example.net/push/two", p256dh, "AAAA") };
			foreach ( var (endpoint, keyText, secret) in malformed.Concat( privateEndpoints.Select( endpoint => (endpoint, p256dh, auth) ) ) )
			{
				var code = "";
				try
				{
					await devicePeer.RequestAsync( BuildSubscribe( endpoint, keyText, secret ), CancellationToken.None ).WaitAsync( Patience );
				}
				catch ( RemoteException exception )
				{
					code = exception.Code;
				}
				CoreChecks.Check( code == RemoteErrors.BadRequest, $"push.subscribe refuses {endpoint}: not https on the default port of a public host, or a malformed key or secret." );
			}

			CoreChecks.Check( NetworkAddresses.IsPublic( IPAddress.Parse( "8.8.8.8" ) ) && NetworkAddresses.IsPublic( IPAddress.Parse( "2606:4700::1111" ) ), "Public addresses are public." );
			CoreChecks.Check( new[] { "127.0.0.1", "10.1.2.3", "172.16.0.1", "192.168.0.1", "169.254.1.1", "100.64.0.1", "0.0.0.0", "224.0.0.1", "::1", "fe80::1", "fc00::1", "::ffff:10.0.0.1", "64:ff9b::a00:1", "2002:c0a8:101::1", "2001:db8::1" }
				.All( text => !NetworkAddresses.IsPublic( IPAddress.Parse( text ) ) ), "Loopback, private, link-local, shared, multicast, reserved and embedded private addresses are not public." );
		}

		using ( var reloaded = new PushService( devices, keyPath ) )
		{
			CoreChecks.Check( reloaded.PublicKey == publicKey && reloaded.FindSubscriptions( deviceId ).Count == 1, "The VAPID key and the subscriptions load back from their files." );
			devices.Revoke( deviceId );
			CoreChecks.Check( reloaded.FindSubscriptions( deviceId ).Count == 0, "Revoking a device drops its subscriptions." );
		}
	}

	/// <summary>Checks delivery against a push service on 127.0.0.1: the headers, a VAPID token for the service's origin, a body the browser decrypts, and a 410 dropping the subscription.</summary>
	private static async Task CheckPushDeliveryAsync( string folder )
	{
		var keyPath = Path.Combine( folder, "delivery", "vapid-key.pem" );
		var devices = new DeviceStore( Path.Combine( folder, "delivery", "devices.json" ) );
		var deviceId = SaveDevice( devices );
		using var receiver = ECDiffieHellman.Create( ECCurve.NamedCurves.nistP256 );
		var receiverPoint = WebPushCrypto.ExportPoint( receiver.ExportParameters( false ) );
		var auth = RandomNumberGenerator.GetBytes( 16 );
		var port = FindFreePort();

		// push.subscribe takes only https endpoints, so the subscription to the local service is written to the file directly.
		var subscriptions = new JsonObject
		{
			[deviceId] = new JsonArray( new JsonObject
			{
				["endpoint"] = $"http://127.0.0.1:{port}/push/one",
				["p256dh"] = Base64Url.EncodeToString( receiverPoint ),
				["auth"] = Base64Url.EncodeToString( auth ),
			} ),
		};
		Directory.CreateDirectory( Path.GetDirectoryName( keyPath )! );
		await File.WriteAllTextAsync( Path.Combine( Path.GetDirectoryName( keyPath )!, PushService.SubscriptionsFile ), subscriptions.ToJsonString() );

		using var listener = new HttpListener();
		listener.Prefixes.Add( $"http://127.0.0.1:{port}/" );
		listener.Start();
		// The service's own client refuses loopback push services; this one reaches the listener.
		using var http = new HttpClient();
		using var push = new PushService( devices, keyPath ) { Client = http };
		var failures = new List<string>();
		push.Failed += failures.Add;
		var chatId = Guid.NewGuid();
		var notify = push.NotifyAsync( chatId, "Chat finished", "The agent is done." );

		var context = await listener.GetContextAsync().WaitAsync( Patience );
		var request = context.Request;
		using var body = new MemoryStream();
		await request.InputStream.CopyToAsync( body );
		var authorization = request.Headers["Authorization"] ?? "";
		CoreChecks.Check( request.HttpMethod == "POST" && request.Url?.AbsolutePath == "/push/one" && request.Headers["Content-Encoding"] == "aes128gcm" && request.Headers["TTL"] is { Length: > 0 },
			"A push is a POST to the endpoint with aes128gcm content and a TTL." );
		var token = authorization.StartsWith( "vapid t=", StringComparison.Ordinal ) && authorization.IndexOf( ", k=", StringComparison.Ordinal ) is var split and > 0
			? (authorization[8..split], authorization[(split + 4)..]) : ("", "");
		CoreChecks.Check( token.Item2 == push.PublicKey && VerifyToken( token.Item1, token.Item2, $"http://127.0.0.1:{port}" ), "A push carries a VAPID token for the service's origin, signed by the offered key." );
		var message = JsonNode.Parse( DecryptPush( body.ToArray(), receiver, receiverPoint, auth ) )!.AsObject();
		CoreChecks.Check( (string?)message["title"] == "Chat finished" && (string?)message["body"] == "The agent is done." && (string?)message["ch"] == chatId.ToString( "N" ),
			"The browser decrypts the notification's title, text and chat." );

		context.Response.StatusCode = 410;
		context.Response.Close();
		await notify.WaitAsync( Patience );
		CoreChecks.Check( push.FindSubscriptions( deviceId ).Count == 0 && failures.Count == 0, "A subscription the push service answers 410 for is dropped quietly." );
	}

	/// <summary>Checks that a real WebSocket host serves every web client file with its content type, caching and validator, and what the served code may do.</summary>
	private static async Task CheckAssetsAsync()
	{
		await using var host = new WebSocketHost( [new IPEndPoint( IPAddress.Loopback, 0 )], _ => Task.CompletedTask );
		WebClientAssets.Map( host );
		host.Start();
		using var http = new HttpClient { BaseAddress = new Uri( $"http://127.0.0.1:{host.Endpoints[0].Port}/" ) };
		var texts = new Dictionary<string, string>( StringComparer.Ordinal );

		foreach ( var (path, type) in new[]
		{
			("/", "text/html"), ("/app.js", "text/javascript"), ("/app.css", "text/css"), ("/sw.js", "text/javascript"),
			("/manifest.webmanifest", "application/manifest+json"), ("/icon.png", "image/png"),
		} )
		{
			using var response = await http.GetAsync( path ).WaitAsync( Patience );
			var bytes = await response.Content.ReadAsByteArrayAsync();
			CoreChecks.Check( response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == type && bytes.Length > 0,
				$"The web client serves {path} as {type}." );
			if ( type.StartsWith( "text/", StringComparison.Ordinal ) )
				texts[path] = Encoding.UTF8.GetString( bytes );
			if ( path == "/" )
			{
				var html = Encoding.UTF8.GetString( bytes );
				CoreChecks.Check( response.Headers.CacheControl?.NoCache == true && response.Headers.Contains( "Content-Security-Policy" ) && html.Contains( "app.js", StringComparison.Ordinal ),
					"The page is never cached without revalidation, has a content policy and loads app.js." );
			}
			if ( path == "/icon.png" )
				CoreChecks.Check( bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G', "The icon is a PNG." );
			if ( path == "/manifest.webmanifest" )
				CoreChecks.Check( JsonNode.Parse( bytes )?["start_url"] is not null, "The manifest is JSON with a start URL." );

			using var again = new HttpRequestMessage( HttpMethod.Get, path );
			again.Headers.TryAddWithoutValidation( "If-None-Match", response.Headers.ETag?.Tag ?? "" );
			using var cached = await http.SendAsync( again ).WaitAsync( Patience );
			CoreChecks.Check( cached.StatusCode == HttpStatusCode.NotModified, $"{path} answers 304 for the version the browser has." );
		}
		CheckClientSource( texts["/"], texts["/app.js"], texts["/sw.js"] );
	}

	/// <summary>Checks the served page and scripts as text, since the checks run no JavaScript: nothing inline the content policy would block, HTML only
	/// from the Markdown renderer (which escapes all text) and the fixed icons, links only to http and https pages and never with the opener, storage
	/// kept per host, and notification clicks reaching only this host's pages.</summary>
	/// <param name="html">The page.</param>
	/// <param name="app">The page's script.</param>
	/// <param name="worker">The service worker.</param>
	private static void CheckClientSource( string html, string app, string worker )
	{
		// A checkout may carry either line ending.
		app = app.Replace( "\r\n", "\n", StringComparison.Ordinal );
		var scripts = Regex.Matches( html, @"<script\b[^>]*>", RegexOptions.IgnoreCase );
		CoreChecks.Check( scripts.Count > 0 && scripts.All( tag => tag.Value.Contains( " src=", StringComparison.Ordinal ) )
			&& !Regex.IsMatch( html, @"\son[a-z]+\s*=|\sstyle\s*=|javascript:", RegexOptions.IgnoreCase ),
			"The page has no inline script, event handler attribute or style attribute, which its content policy refuses." );
		foreach ( var (name, code) in new[] { ("app.js", app), ("sw.js", worker) } )
		{
			CoreChecks.Check( !Regex.IsMatch( code, @"\beval\s*\(|new\s+Function\s*\(|setAttribute\(\s*'style'|insertAdjacentHTML|outerHTML|document\.write|setTimeout\(\s*'" ),
				$"{name} evaluates no strings as code and sets no style attributes." );
		}
		var markup = Regex.Matches( app, @"\bhtml:\s*([^,}\s]+)" ).Select( match => match.Groups[1].Value ).ToList();
		CoreChecks.Check( markup.Count > 0 && markup.All( value => value.StartsWith( "renderMarkdown(", StringComparison.Ordinal ) || value.StartsWith( "icons.", StringComparison.Ordinal ) )
			&& Regex.Matches( app, @"innerHTML\s*=\s*([^;]+);" ).All( match => match.Groups[1].Value.Trim() is "value" || match.Groups[1].Value.Trim().StartsWith( "icons.", StringComparison.Ordinal ) ),
			"The web client sets markup only from the Markdown renderer and its own icons." );
		var links = app.Split( '\n' ).Where( line => line.Contains( "href", StringComparison.Ordinal ) ).ToList();
		CoreChecks.Check( app.Contains( @"const isSafeUrl = url => /^https?:\/\//i.test(url);", StringComparison.Ordinal )
			&& app.Contains( "if (isSafeUrl(url)) {\n\t\t\tcard.append(element('a', { href: url,", StringComparison.Ordinal )
			&& links.Count == 2 && links.All( line => line.Contains( "rel", StringComparison.Ordinal ) && line.Contains( "noopener", StringComparison.Ordinal ) )
			&& links[0].Contains( "isSafeUrl(url) ?", StringComparison.Ordinal ),
			"Links open only http and https pages, in a new tab without the opener: the Markdown renderer's and the sign-in dialog's." );
		CoreChecks.Check( app.Split( '\n' ).Where( line => line.Contains( "localStorage.", StringComparison.Ordinal ) ).All( line => line.Contains( "profileKey(", StringComparison.Ordinal ) ),
			"The web client keeps what it remembers per host, since a relay origin serves several hosts' pages." );
		CoreChecks.Check( worker.Contains( "client.url.startsWith(scope)", StringComparison.Ordinal ) && worker.Contains( "[0-9a-f]{32}", StringComparison.Ordinal ),
			"A notification click opens its chat only in a page of its own host, and only a chat id the page's address takes." );
	}

	/// <summary>Saves a paired device and returns its id.</summary>
	private static string SaveDevice( DeviceStore devices )
	{
		using var key = RemoteKeys.Create();
		var deviceId = Guid.NewGuid().ToString( "N" );
		devices.Save( new PairedDevice { DeviceId = deviceId, Name = "Phone", DeviceKey = key.PublicKeyText, Scopes = DeviceScopes.Paired, Paired = DateTimeOffset.UtcNow } );
		return deviceId;
	}

	/// <summary>Builds a <c>push.subscribe</c> request.</summary>
	private static JsonObject BuildSubscribe( string endpoint, string p256dh, string auth )
	{
		var frame = Frame.Create( "push.subscribe" );
		frame["endpoint"] = endpoint;
		frame["p256dh"] = p256dh;
		frame["auth"] = auth;
		return frame;
	}

	/// <summary>Imports a P-256 private key from its base64url scalar and uncompressed public point.</summary>
	private static ECDiffieHellman ImportPrivate( string scalar, byte[] point )
	{
		var key = ECDiffieHellman.Create();
		key.ImportParameters( new ECParameters
		{
			Curve = ECCurve.NamedCurves.nistP256,
			D = Base64Url.DecodeFromChars( scalar ),
			Q = new ECPoint { X = point[1..33], Y = point[33..] },
		} );
		return key;
	}

	/// <summary>Imports a VAPID public key, base64url of the uncompressed point, for verifying signatures.</summary>
	private static ECDsa ImportVerifier( string publicKey )
	{
		var point = Base64Url.DecodeFromChars( publicKey );
		return ECDsa.Create( new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = point[1..33], Y = point[33..] } } );
	}

	/// <summary>Verifies a VAPID token's signature and audience as a push service would.</summary>
	private static bool VerifyToken( string token, string publicKey, string audience )
	{
		var parts = token.Split( '.' );
		if ( parts.Length != 3 )
			return false;
		using var verifier = ImportVerifier( publicKey );
		var claims = JsonNode.Parse( Base64Url.DecodeFromChars( parts[1] ) )!.AsObject();
		return verifier.VerifyData( Encoding.ASCII.GetBytes( parts[0] + "." + parts[1] ), Base64Url.DecodeFromChars( parts[2] ), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation )
			&& (string?)claims["aud"] == audience
			&& (long?)claims["exp"] > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
	}

	/// <summary>Decrypts a one-record aes128gcm push message as the browser does (RFC 8291 section 3.4), returning the plaintext without its delimiter.</summary>
	private static byte[] DecryptPush( byte[] body, ECDiffieHellman receiver, byte[] receiverPoint, byte[] auth )
	{
		var salt = body[..16];
		var keyLength = body[20];
		var senderPoint = body[21..(21 + keyLength)];
		using var sender = WebPushCrypto.ImportPoint( senderPoint ) ?? throw new InvalidDataException( "The sender key is not a P-256 point." );
		var secret = receiver.DeriveRawSecretAgreement( sender.PublicKey );
		byte[] keyInfo = [.. "WebPush: info\0"u8, .. receiverPoint, .. senderPoint];
		var ikm = HKDF.Expand( HashAlgorithmName.SHA256, HKDF.Extract( HashAlgorithmName.SHA256, secret, auth ), 32, keyInfo );
		var prk = HKDF.Extract( HashAlgorithmName.SHA256, ikm, salt );
		var contentKey = HKDF.Expand( HashAlgorithmName.SHA256, prk, 16, "Content-Encoding: aes128gcm\0"u8.ToArray() );
		var nonce = HKDF.Expand( HashAlgorithmName.SHA256, prk, 12, "Content-Encoding: nonce\0"u8.ToArray() );
		var record = body[(21 + keyLength)..];
		var plaintext = new byte[record.Length - 16];
		using var aes = new AesGcm( contentKey, 16 );
		aes.Decrypt( nonce, record[..^16], record[^16..], plaintext );
		return plaintext[..Array.LastIndexOf( plaintext, (byte)2 )];
	}

	/// <summary>Writes a symbol's rows as text, 1 for dark.</summary>
	private static string[] ReadRows( bool[,] modules ) =>
		[.. Enumerable.Range( 0, modules.GetLength( 0 ) ).Select( y => string.Concat( Enumerable.Range( 0, modules.GetLength( 1 ) ).Select( x => modules[y, x] ? '1' : '0' ) ) )];

	/// <summary>Hashes a symbol's rows, joined with line feeds, as the reference values were computed.</summary>
	private static string HashRows( bool[,] modules ) => Convert.ToHexStringLower( SHA256.HashData( Encoding.ASCII.GetBytes( string.Join( '\n', ReadRows( modules ) ) ) ) );

	/// <summary>Reads both copies of the format information, checks they agree and that their BCH code holds, and returns the level bits and the mask.</summary>
	private static (int Level, int Mask) ReadFormat( bool[,] modules )
	{
		var size = modules.GetLength( 0 );
		int first = 0, second = 0;
		for ( var index = 0; index < 15; index++ )
		{
			var (x, y) = index switch
			{
				< 6 => (8, index),
				6 => (8, 7),
				7 => (8, 8),
				8 => (7, 8),
				_ => (14 - index, 8),
			};
			first |= (modules[y, x] ? 1 : 0) << index;
			var (x2, y2) = index < 8 ? (size - 1 - index, 8) : (8, size - 15 + index);
			second |= (modules[y2, x2] ? 1 : 0) << index;
		}
		var bits = first ^ 0x5412;
		var data = bits >> 10;
		var remainder = data;
		for ( var step = 0; step < 10; step++ )
			remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
		CoreChecks.Check( first == second && (bits & 0x3FF) == remainder, "Both format copies agree and carry a valid BCH code." );
		return (data >> 3, data & 7);
	}

	/// <summary>Reads a version 1 symbol's 26 codewords from its data region, undoing the mask, in the standard's zigzag order.</summary>
	private static byte[] ReadCodewords( bool[,] modules, int mask )
	{
		const int size = 21;
		bool IsFunction( int x, int y ) => x == 6 || y == 6 || (x < 9 && y < 9) || (x >= size - 8 && y < 9) || (x < 9 && y >= size - 8);
		var bits = new List<bool>();
		for ( var right = size - 1; right >= 1; right -= 2 )
		{
			if ( right == 6 )
				right = 5;
			var isUpward = ((right + 1) & 2) == 0;
			for ( var step = 0; step < size; step++ )
			{
				var y = isUpward ? size - 1 - step : step;
				for ( var offset = 0; offset < 2; offset++ )
				{
					var x = right - offset;
					if ( IsFunction( x, y ) )
						continue;
					var invert = mask switch
					{
						0 => (x + y) % 2 == 0,
						1 => y % 2 == 0,
						2 => x % 3 == 0,
						3 => (x + y) % 3 == 0,
						4 => (x / 3 + y / 2) % 2 == 0,
						5 => x * y % 2 + x * y % 3 == 0,
						6 => (x * y % 2 + x * y % 3) % 2 == 0,
						_ => ((x + y) % 2 + x * y % 3) % 2 == 0,
					};
					bits.Add( modules[y, x] ^ invert );
				}
			}
		}
		return [.. Enumerable.Range( 0, bits.Count / 8 ).Select( index => (byte)bits.Skip( index * 8 ).Take( 8 ).Aggregate( 0, ( value, bit ) => (value << 1) | (bit ? 1 : 0) ) )];
	}

	/// <summary>Appends a value's low bits, highest first.</summary>
	private static void AppendBits( List<bool> bits, int value, int count )
	{
		for ( var bit = count - 1; bit >= 0; bit-- )
			bits.Add( ((value >> bit) & 1) != 0 );
	}

	/// <summary>Finds a free TCP port on the loopback address.</summary>
	private static int FindFreePort()
	{
		using var listener = new TcpListener( IPAddress.Loopback, 0 );
		listener.Start();
		return ((IPEndPoint)listener.LocalEndpoint).Port;
	}
}
