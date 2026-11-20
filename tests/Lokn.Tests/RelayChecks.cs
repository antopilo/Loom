using System.Buffers.Text;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokn.Relay;
using Microsoft.Extensions.DependencyInjection;

namespace Lokn.Tests;

/// <summary>Checks the relay's challenge proof, its limits, and its pairing of a host with two devices over a local HTTP listener.</summary>
internal static class RelayChecks
{
	/// <summary>Defines how long any single socket step may take before the check fails.</summary>
	private static readonly TimeSpan StepWait = TimeSpan.FromSeconds( 15 );

	/// <summary>Defines the shortened device wait the socket checks run with.</summary>
	private static readonly TimeSpan DeviceWait = TimeSpan.FromSeconds( 3 );

	/// <summary>Runs the proof checks, then the socket checks against a relay on a random loopback port.</summary>
	public static void Run()
	{
		CheckProof();
		CheckSocketsAsync().GetAwaiter().GetResult();
		CheckLimitsAsync().GetAwaiter().GetResult();
	}

	/// <summary>Checks that only a proof signed by the P-256 key behind the host id, over this nonce and purpose, passes.</summary>
	private static void CheckProof()
	{
		using var key = ECDsa.Create( ECCurve.NamedCurves.nistP256 );
		using var other = ECDsa.Create( ECCurve.NamedCurves.nistP256 );
		using var large = ECDsa.Create( ECCurve.NamedCurves.nistP384 );
		var spki = key.ExportSubjectPublicKeyInfo();
		var hostId = HostProof.ComputeHostId( spki );
		var otherId = HostProof.ComputeHostId( other.ExportSubjectPublicKeyInfo() );
		var largeId = HostProof.ComputeHostId( large.ExportSubjectPublicKeyInfo() );
		var nonce = HostProof.CreateNonce();
		const string Control = HostProof.ControlPurpose;
		CoreChecks.Check( hostId.Length == 22 && hostId == Base64Url.EncodeToString( SHA256.HashData( spki ) )[..22] && hostId != otherId, "A host id is the first 22 base64url characters of the SHA-256 of the host's SPKI key." );
		CoreChecks.Check( nonce != HostProof.CreateNonce() && Base64Url.DecodeFromChars( nonce ).Length == 32, "Each challenge is 32 fresh random bytes." );
		CoreChecks.Check( Verify( hostId, BuildProof( key, hostId, nonce, Control ), nonce, Control ), "A proof signed by the host's key over the challenge passes." );
		CoreChecks.Check( !Verify( hostId, BuildProof( key, hostId, nonce, Control ), HostProof.CreateNonce(), Control ), "A proof for another challenge fails, so it cannot be replayed." );
		CoreChecks.Check( !Verify( hostId, BuildProof( key, hostId, nonce, HostProof.BuildDataPurpose( "sid" ) ), nonce, Control ), "A data socket's proof does not open a control socket." );
		CoreChecks.Check( !Verify( hostId, BuildProof( key, hostId, nonce, Control, signed: "tampered" ), nonce, Control ), "A signature over other text fails." );
		CoreChecks.Check( !Verify( hostId, BuildProof( other, hostId, nonce, Control, hostKey: spki ), nonce, Control ), "A signature by another key fails." );
		CoreChecks.Check( !Verify( otherId, BuildProof( key, otherId, nonce, Control ), nonce, Control ), "A key cannot prove another host's id." );
		CoreChecks.Check( !Verify( largeId, BuildProof( large, largeId, nonce, Control ), nonce, Control ), "A key that is not P-256 fails." );
		var valid = JsonNode.Parse( BuildProof( key, hostId, nonce, Control ) )!.AsObject();
		valid["t"] = "relay.device";
		CoreChecks.Check( !Verify( hostId, valid.ToJsonString(), nonce, Control ), "A frame other than relay.host fails." );
		CoreChecks.Check( !Verify( hostId, "{}", nonce, Control ) && !Verify( hostId, "relay.host", nonce, Control ), "A malformed proof fails." );
	}

	/// <summary>Checks message passing, rejection, replay and timeouts through a relay listening on 127.0.0.1.</summary>
	private static async Task CheckSocketsAsync()
	{
		await using var app = RelayApp.Build( ["http://127.0.0.1:0"], ["--Logging:LogLevel:Default=None"] );
		var hub = app.Services.GetRequiredService<RelayHub>();
		CoreChecks.Check( hub.DeviceWait == TimeSpan.FromSeconds( 10 ), "A device waits up to 10 s for its host." );
		hub.DeviceWait = DeviceWait;
		await app.StartAsync();
		try
		{
			var root = app.Urls.Single().Replace( "http://", "ws://", StringComparison.Ordinal );
			using var key = ECDsa.Create( ECCurve.NamedCurves.nistP256 );
			using var stranger = ECDsa.Create( ECCurve.NamedCurves.nistP256 );
			var hostId = HostProof.ComputeHostId( key.ExportSubjectPublicKeyInfo() );
			var strangerId = HostProof.ComputeHostId( stranger.ExportSubjectPublicKeyInfo() );
			string Prove( string nonce, string purpose ) => BuildProof( key, hostId, nonce, purpose );

			CoreChecks.Check( await ReadRejectionAsync( $"{root}/host/{hostId}", nonce => BuildProof( key, hostId, nonce, HostProof.ControlPurpose, signed: "tampered" ) ) == WebSocketCloseStatus.PolicyViolation, "The relay rejects a host control socket with a bad signature." );
			CoreChecks.Check( await ReadRejectionAsync( $"{root}/host/{strangerId}", nonce => BuildProof( key, strangerId, nonce, HostProof.ControlPurpose ) ) == WebSocketCloseStatus.PolicyViolation, "The relay rejects a host control socket for another key's host id." );
			CoreChecks.Check( await ReadRejectionAsync( $"{root}/host/{hostId}", nonce => Prove( nonce, HostProof.BuildDataPurpose( "x" ) ) ) == WebSocketCloseStatus.PolicyViolation, "The relay rejects a data socket's proof on a control socket." );
			CoreChecks.Check( !hub.HasHost( hostId ) && !hub.HasHost( strangerId ), "A rejected host is not registered." );

			using var control = await ConnectAsync( $"{root}/host/{hostId}" );
			string? controlProof = null;
			await AnswerChallengeAsync( control, nonce => controlProof = Prove( nonce, HostProof.ControlPurpose ) );
			var watch = Stopwatch.StartNew();
			while ( !hub.HasHost( hostId ) )
			{
				CoreChecks.Check( watch.Elapsed < StepWait, "The relay registers a host with a valid proof." );
				await Task.Delay( 20 );
			}
			CoreChecks.Check( await ReadRejectionAsync( $"{root}/host/{hostId}", _ => controlProof! ) == WebSocketCloseStatus.PolicyViolation && hub.HasHost( hostId ), "A replayed control proof is rejected and cannot replace the host's control socket." );

			using var device1 = await ConnectAsync( $"{root}/device/{hostId}" );
			var sid1 = await ReadOpenAsync( control );
			using var data1 = await ConnectAsync( $"{root}/host/{hostId}/{sid1}" );
			await AnswerChallengeAsync( data1, nonce => Prove( nonce, HostProof.BuildDataPurpose( sid1 ) ) );
			using var device2 = await ConnectAsync( $"{root}/device/{hostId}" );
			var sid2 = await ReadOpenAsync( control );
			using var data2 = await ConnectAsync( $"{root}/host/{hostId}/{sid2}" );
			await AnswerChallengeAsync( data2, nonce => Prove( nonce, HostProof.BuildDataPurpose( sid2 ) ) );
			CoreChecks.Check( sid1 != sid2, "Each device gets its own session id." );

			var large = RandomNumberGenerator.GetBytes( 100_000 );
			await SendAsync( device1, WebSocketMessageType.Text, "{\"t\":\"hello\",\"v\":1}" );
			await SendAsync( device1, WebSocketMessageType.Binary, large );
			await device1.SendAsync( "frag"u8.ToArray().AsMemory(), WebSocketMessageType.Text, false, CancellationToken.None );
			await device1.SendAsync( "ment"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None );
			await SendAsync( device2, WebSocketMessageType.Binary, [2, 0, 255] );
			CoreChecks.Check( await ReadMessageAsync( data1 ) is (WebSocketMessageType.Text, var hello) && Encoding.UTF8.GetString( hello ) == "{\"t\":\"hello\",\"v\":1}", "A device's text message reaches its host unchanged." );
			CoreChecks.Check( await ReadMessageAsync( data1 ) is (WebSocketMessageType.Binary, var copy) && copy.SequenceEqual( large ), "A large binary message reaches the host as one message, byte for byte." );
			CoreChecks.Check( await ReadMessageAsync( data1 ) is (WebSocketMessageType.Text, var joined) && Encoding.UTF8.GetString( joined ) == "fragment", "A fragmented message reaches the host as one message." );
			CoreChecks.Check( await ReadMessageAsync( data2 ) is (WebSocketMessageType.Binary, var second) && second.SequenceEqual( new byte[] { 2, 0, 255 } ), "A second device's message reaches its own host socket." );

			await SendAsync( data1, WebSocketMessageType.Binary, [0, 1, 254, 255] );
			await SendAsync( data1, WebSocketMessageType.Text, "{\"t\":\"hello\",\"v\":1,\"scopes\":[\"view\"]}" );
			await SendAsync( data2, WebSocketMessageType.Text, "two" );
			CoreChecks.Check( await ReadMessageAsync( device1 ) is (WebSocketMessageType.Binary, var back) && back.SequenceEqual( new byte[] { 0, 1, 254, 255 } ), "A host's binary message reaches its device unchanged." );
			CoreChecks.Check( await ReadMessageAsync( device1 ) is (WebSocketMessageType.Text, var answer) && Encoding.UTF8.GetString( answer ) == "{\"t\":\"hello\",\"v\":1,\"scopes\":[\"view\"]}", "A host's text message reaches its device unchanged and in order." );
			CoreChecks.Check( await ReadMessageAsync( device2 ) is (WebSocketMessageType.Text, var two) && Encoding.UTF8.GetString( two ) == "two", "Each host data socket answers only its own device." );

			var deviceClosing = device1.CloseAsync( WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None );
			CoreChecks.Check( await ReadCloseAsync( data1 ) == WebSocketCloseStatus.NormalClosure && data1.CloseStatusDescription == "bye", "A device's close reaches its host data socket." );
			await deviceClosing.WaitAsync( StepWait );
			CoreChecks.Check( device1.State == WebSocketState.Closed, "The relay completes the device's close handshake with the host's answer." );
			var hostClosing = data2.CloseAsync( (WebSocketCloseStatus)4001, "host gone", CancellationToken.None );
			CoreChecks.Check( await ReadCloseAsync( device2 ) == (WebSocketCloseStatus)4001 && device2.CloseStatusDescription == "host gone", "A host data socket's close reaches its device." );
			await hostClosing.WaitAsync( StepWait );

			CoreChecks.Check( await ReadRejectionAsync( $"{root}/host/{hostId}/unknown", nonce => Prove( nonce, HostProof.BuildDataPurpose( "unknown" ) ) ) == WebSocketCloseStatus.PolicyViolation, "A host data socket for an unknown session is closed." );
			using var device3 = await ConnectAsync( $"{root}/device/{hostId}" );
			var sid3 = await ReadOpenAsync( control );
			CoreChecks.Check( await ReadRejectionAsync( $"{root}/host/{hostId}/{sid3}", nonce => Prove( nonce, HostProof.BuildDataPurpose( sid1 ) ) ) == WebSocketCloseStatus.PolicyViolation, "A host data socket proven for another session is closed." );
			CoreChecks.Check( await ReadCloseAsync( device3 ) == WebSocketCloseStatus.EndpointUnavailable, "A device whose host never proves its data socket times out." );

			watch.Restart();
			using var lonely = await ConnectAsync( $"{root}/device/{strangerId}" );
			CoreChecks.Check( await ReadCloseAsync( lonely ) == WebSocketCloseStatus.EndpointUnavailable && watch.Elapsed >= DeviceWait - TimeSpan.FromMilliseconds( 200 ), "A device with no host connected waits, then times out." );

			await control.CloseAsync( WebSocketCloseStatus.NormalClosure, null, CancellationToken.None ).WaitAsync( StepWait );
			watch.Restart();
			while ( hub.HasHost( hostId ) )
			{
				CoreChecks.Check( watch.Elapsed < StepWait, "A host that closes its control socket is unregistered." );
				await Task.Delay( 20 );
			}
		}
		finally
		{
			await app.StopAsync();
		}
	}

	/// <summary>Checks the relay's limits: devices waiting for one host, and the allowed host list.</summary>
	private static async Task CheckLimitsAsync()
	{
		const string Allowed = "AAAAAAAAAAAAAAAAAAAAAA";
		await using var app = RelayApp.Build( ["http://127.0.0.1:0"], ["--Logging:LogLevel:Default=None", "--Relay:AllowedHosts:0=" + Allowed] );
		app.Services.GetRequiredService<RelayHub>().DeviceWait = DeviceWait;
		await app.StartAsync();
		try
		{
			var root = app.Urls.Single().Replace( "http://", "ws://", StringComparison.Ordinal );
			using var key = ECDsa.Create( ECCurve.NamedCurves.nistP256 );
			var hostId = HostProof.ComputeHostId( key.ExportSubjectPublicKeyInfo() );
			CoreChecks.Check( await FailsToConnectAsync( $"{root}/host/{hostId}" ) && await FailsToConnectAsync( $"{root}/device/{hostId}" ), "A relay with an allowed host list refuses other hosts and their devices." );

			var waiting = new List<ClientWebSocket>();
			try
			{
				for ( var i = 0; i < RelayHub.MaxWaitingPerHost; i++ )
					waiting.Add( await ConnectAsync( $"{root}/device/{Allowed}" ) );
				CoreChecks.Check( await FailsToConnectAsync( $"{root}/device/{Allowed}" ), "A device beyond the waiting limit of one host is refused." );
			}
			finally
			{
				foreach ( var socket in waiting )
					socket.Dispose();
			}
		}
		finally
		{
			await app.StopAsync();
		}
	}

	/// <summary>Builds a <c>relay.host</c> proof for <paramref name="hostId"/> over <paramref name="nonce"/> and <paramref name="purpose"/>, signed by
	/// <paramref name="signer"/>, optionally over other text or with another key.</summary>
	private static string BuildProof( ECDsa signer, string hostId, string nonce, string purpose, string? signed = null, byte[]? hostKey = null )
	{
		var signature = signer.SignData( Encoding.UTF8.GetBytes( signed ?? $"loom-relay-proof v2\n{hostId}\n{purpose}\n{nonce}" ), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation );
		return new JsonObject
		{
			["t"] = "relay.host",
			["hostKey"] = Base64Url.EncodeToString( hostKey ?? signer.ExportSubjectPublicKeyInfo() ),
			["sig"] = Base64Url.EncodeToString( signature ),
		}.ToJsonString();
	}

	/// <summary>Verifies <paramref name="proof"/> for <paramref name="hostId"/> against <paramref name="nonce"/> and <paramref name="purpose"/>.</summary>
	private static bool Verify( string hostId, string proof, string nonce, string purpose ) => HostProof.Verify( hostId, Encoding.UTF8.GetBytes( proof ), nonce, purpose, out _ );

	/// <summary>Reads the relay's challenge on a host socket and sends the proof <paramref name="prove"/> builds for its nonce.</summary>
	private static async Task AnswerChallengeAsync( WebSocket socket, Func<string, string> prove )
	{
		var (type, data) = await ReadMessageAsync( socket );
		CoreChecks.Check( type == WebSocketMessageType.Text, "The relay challenges a host socket with a text frame." );
		using var document = JsonDocument.Parse( data );
		var root = document.RootElement;
		CoreChecks.Check( root.GetProperty( "t" ).GetString() == "relay.challenge" && root.GetProperty( "nonce" ).GetString() is { Length: > 0 }, "The relay's challenge carries a nonce." );
		await SendAsync( socket, WebSocketMessageType.Text, prove( root.GetProperty( "nonce" ).GetString()! ) );
	}

	/// <summary>Determines whether the relay refuses the WebSocket upgrade of <paramref name="url"/>.</summary>
	private static async Task<bool> FailsToConnectAsync( string url )
	{
		try
		{
			using var socket = await ConnectAsync( url );
			return false;
		}
		catch ( WebSocketException )
		{
			return true;
		}
	}

	/// <summary>Opens a client WebSocket to <paramref name="url"/>.</summary>
	private static async Task<ClientWebSocket> ConnectAsync( string url )
	{
		var socket = new ClientWebSocket();
		using var timeout = new CancellationTokenSource( StepWait );
		await socket.ConnectAsync( new Uri( url ), timeout.Token );
		return socket;
	}

	/// <summary>Sends <paramref name="text"/> as one UTF-8 message.</summary>
	private static Task SendAsync( WebSocket socket, WebSocketMessageType type, string text ) => SendAsync( socket, type, Encoding.UTF8.GetBytes( text ) );

	/// <summary>Sends <paramref name="data"/> as one message of <paramref name="type"/>.</summary>
	private static async Task SendAsync( WebSocket socket, WebSocketMessageType type, byte[] data )
	{
		using var timeout = new CancellationTokenSource( StepWait );
		await socket.SendAsync( data.AsMemory(), type, true, timeout.Token );
	}

	/// <summary>Reads one whole message; a close reads as a close with no data.</summary>
	private static async Task<(WebSocketMessageType Type, byte[] Data)> ReadMessageAsync( WebSocket socket )
	{
		using var timeout = new CancellationTokenSource( StepWait );
		using var data = new MemoryStream();
		var buffer = new byte[4096];
		while ( true )
		{
			var result = await socket.ReceiveAsync( buffer.AsMemory(), timeout.Token );
			if ( result.MessageType == WebSocketMessageType.Close )
				return (WebSocketMessageType.Close, []);
			data.Write( buffer, 0, result.Count );
			if ( result.EndOfMessage )
				return (result.MessageType, data.ToArray());
		}
	}

	/// <summary>Reads the <c>relay.open</c> frame on a host's control socket and returns its session id.</summary>
	private static async Task<string> ReadOpenAsync( WebSocket control )
	{
		var (type, data) = await ReadMessageAsync( control );
		CoreChecks.Check( type == WebSocketMessageType.Text, "The relay announces a device to its host with a text frame." );
		using var document = JsonDocument.Parse( data );
		var root = document.RootElement;
		CoreChecks.Check( root.GetProperty( "t" ).GetString() == "relay.open" && root.GetProperty( "sid" ).GetString() is { Length: > 0 }, "The relay announces a device with relay.open and a session id." );
		return root.GetProperty( "sid" ).GetString()!;
	}

	/// <summary>Reads until the peer closes, answers the close, and returns its status, or null when a message arrives first.</summary>
	private static async Task<WebSocketCloseStatus?> ReadCloseAsync( WebSocket socket )
	{
		var (type, _) = await ReadMessageAsync( socket );
		if ( type != WebSocketMessageType.Close )
			return null;
		if ( socket.State == WebSocketState.CloseReceived )
		{
			using var timeout = new CancellationTokenSource( StepWait );
			await socket.CloseOutputAsync( WebSocketCloseStatus.NormalClosure, null, timeout.Token );
		}
		return socket.CloseStatus;
	}

	/// <summary>Opens a host socket to <paramref name="url"/>, answers its challenge with <paramref name="prove"/>, and returns the status the relay closes it with.</summary>
	private static async Task<WebSocketCloseStatus?> ReadRejectionAsync( string url, Func<string, string> prove )
	{
		using var socket = await ConnectAsync( url );
		await AnswerChallengeAsync( socket, prove );
		return await ReadCloseAsync( socket );
	}
}
