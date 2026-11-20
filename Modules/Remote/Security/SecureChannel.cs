using System.Buffers.Text;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Lokn.Modules.Remote;

/// <summary>A device that completed the host's handshake, and its encrypted connection.</summary>
/// <param name="Device">The paired device, as stored when the handshake ran.</param>
/// <param name="Connection">The encrypted connection, after the <c>ready</c> exchange.</param>
public sealed record HostSession( PairedDevice Device, SecureConnection Connection );

/// <summary>A host this device completed the handshake with, and its encrypted connection.</summary>
/// <param name="HostId">The host's id.</param>
/// <param name="Scopes">The scopes the host grants this device.</param>
/// <param name="Connection">The encrypted connection, after the <c>ready</c> exchange.</param>
public sealed record DeviceSession( string HostId, IReadOnlyList<string> Scopes, SecureConnection Connection );

/// <summary>Runs pairing and the connect handshake of docs/remote.md, Secure channel: plaintext text frames, then 3DH, HKDF-SHA256 and AES-256-GCM.</summary>
public static class SecureChannel
{
	/// <summary>How long a handshake may take before the host closes the connection.</summary>
	public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds( 15 );

	/// <summary>The largest plaintext handshake message either side accepts; the limit lifts to <see cref="Frame.MaxBytes"/> once the handshake succeeds.</summary>
	public const int HandshakeMaxBytes = 4 * 1024;

	/// <summary>Bounds the host's handshakes in progress: 64 at once, 8 from one address.</summary>
	public static ConnectionLimiter Handshakes { get; } = new( 64, 8 );

	/// <summary>The HKDF info of the device-to-host key.</summary>
	private static readonly byte[] ClientToHostInfo = Encoding.UTF8.GetBytes( "loom c2h v1" );

	/// <summary>The HKDF info of the host-to-device key.</summary>
	private static readonly byte[] HostToClientInfo = Encoding.UTF8.GetBytes( "loom h2c v1" );

	/// <summary>The longest device name the host keeps.</summary>
	private const int MaxNameLength = 100;

	/// <summary>The refusal of a pairing whose device id is already paired.</summary>
	private const string AlreadyPaired = "This device is already paired. Remove it on the host first, then pair again.";

	/// <summary>Runs the host side of a new connection: a <c>pair</c> frame pairs the device and closes; a <c>hello</c> frame from a paired device completes the handshake.</summary>
	/// <param name="carrier">The new connection; it is closed unless a session is returned.</param>
	/// <param name="host">The host's static keys.</param>
	/// <param name="devices">The paired devices; pairing adds to them.</param>
	/// <param name="offers">The live pairing offers.</param>
	/// <param name="cancellationToken">Cancels the handshake.</param>
	/// <returns>The session, or null when the connection paired a device, was refused or broke.</returns>
	public static async Task<HostSession?> AcceptAsync( MessageConnection carrier, RemoteKeys host, DeviceStore devices, PairingOffers offers, CancellationToken cancellationToken )
	{
		var address = carrier.RemoteAddress;
		if ( !Handshakes.TryEnter( address ) )
		{
			await carrier.DisposeAsync().ConfigureAwait( false );
			return null;
		}

		carrier.MaxMessageBytes = HandshakeMaxBytes;
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
		timeout.CancelAfter( HandshakeTimeout );
		HostSession? session = null;
		try
		{
			var first = await carrier.ReceiveMessageAsync( timeout.Token ).ConfigureAwait( false );
			var frame = first is null ? null : ParseText( first );
			switch ( frame is null ? "" : Frame.ReadType( frame ) )
			{
				case "pair":
					await PairDeviceAsync( carrier, frame!, host, devices, offers ).ConfigureAwait( false );
					break;
				case "hello":
					session = await AcceptHelloAsync( carrier, frame!, first!.Data, host, devices, timeout.Token ).ConfigureAwait( false );
					break;
				case "":
					break;
				default:
					await carrier.SendAsync( Frame.BuildErr( RemoteErrors.BadRequest, "Expected pair or hello." ) ).ConfigureAwait( false );
					break;
			}
		}
		catch ( Exception exception ) when ( exception is InvalidDataException or OperationCanceledException or IOException or WebSocketException or ObjectDisposedException )
		{
		}
		finally
		{
			Handshakes.Exit( address );
		}

		if ( session is null )
			await carrier.DisposeAsync().ConfigureAwait( false );
		else
			carrier.MarkSecured();
		return session;
	}

	/// <summary>Pairs this device with a host through its pairing link, then closes the connection.</summary>
	/// <param name="carrier">A new connection to the host; it is closed afterwards.</param>
	/// <param name="device">This device's static keys.</param>
	/// <param name="deviceId">This device's id, a GUID in <c>N</c> form.</param>
	/// <param name="name">This device's name, as the host lists it.</param>
	/// <param name="link">The host's pairing link.</param>
	/// <param name="cancellationToken">Cancels pairing.</param>
	/// <returns>The scopes the host granted.</returns>
	/// <exception cref="RemoteException">The host refused, such as for a used or expired link.</exception>
	/// <exception cref="IOException">The connection closed before the answer.</exception>
	public static async Task<IReadOnlyList<string>> PairAsync( MessageConnection carrier, RemoteKeys device, string deviceId, string name, PairingLink link, CancellationToken cancellationToken )
	{
		try
		{
			var pair = Frame.Create( "pair" );
			pair["deviceId"] = deviceId;
			pair["name"] = name;
			pair["deviceKey"] = device.PublicKeyText;
			pair["mac"] = PairingLink.ComputeMac( link.Secret, link.HostId, device.PublicKeyText );
			await carrier.SendAsync( pair ).ConfigureAwait( false );

			var answer = await ReceiveAnswerAsync( carrier, "paired", cancellationToken ).ConfigureAwait( false );
			if ( Frame.ReadString( answer, "hostId" ) != link.HostId )
				throw new RemoteException( RemoteErrors.Failed, "The host is not the one the pairing link names." );
			return Frame.ReadStrings( answer, "scopes" ) ?? DeviceScopes.Paired;
		}
		finally
		{
			await carrier.DisposeAsync().ConfigureAwait( false );
		}
	}

	/// <summary>Runs the device side of the connect handshake with a paired host.</summary>
	/// <param name="carrier">A new connection to the host; it is closed when the handshake fails.</param>
	/// <param name="device">This device's static keys.</param>
	/// <param name="deviceId">This device's id, as paired.</param>
	/// <param name="hostKey">The host's static public key from the pairing link, base64url SPKI.</param>
	/// <param name="cancellationToken">Cancels the handshake.</param>
	/// <exception cref="RemoteException">The host refused, such as <c>revoked</c>, or is not the paired host.</exception>
	/// <exception cref="IOException">The connection closed during the handshake.</exception>
	public static async Task<DeviceSession> ConnectAsync( MessageConnection carrier, RemoteKeys device, string deviceId, string hostKey, CancellationToken cancellationToken )
	{
		using var hostStatic = RemoteKeys.ImportAgreementKey( hostKey ) ?? throw new ArgumentException( "The host key is not a P-256 public key.", nameof( hostKey ) );
		using var ephemeral = ECDiffieHellman.Create( ECCurve.NamedCurves.nistP256 );
		var ephemeralKey = ephemeral.ExportSubjectPublicKeyInfo();
		SecureConnection? connection = null;
		carrier.MaxMessageBytes = HandshakeMaxBytes;
		try
		{
			var hello = Frame.Create( "hello" );
			hello["v"] = Frame.Version;
			hello["deviceId"] = deviceId;
			hello["eph"] = Base64Url.EncodeToString( ephemeralKey );
			var helloText = Frame.Serialize( hello );
			await carrier.SendMessageAsync( new( true, helloText ) ).ConfigureAwait( false );

			var answerMessage = await carrier.ReceiveMessageAsync( cancellationToken ).ConfigureAwait( false )
				?? throw new IOException( "The host closed the connection during the handshake." );
			var answer = ReadAnswer( ParseText( answerMessage ), "hello" );
			if ( Frame.ReadLong( answer, "v" ) != Frame.Version )
				throw new RemoteException( RemoteErrors.Version, "The host speaks another protocol version." );
			if ( Frame.ReadString( answer, "hostId" ) is not { } hostId || hostId != RemoteKeys.ComputeHostId( Base64Url.DecodeFromChars( hostKey ) ) )
				throw new RemoteException( RemoteErrors.Failed, "The host is not the paired host." );
			if ( Frame.ReadString( answer, "eph" ) is not { } hostEphemeralText || RemoteKeys.ImportAgreementKey( hostEphemeralText ) is not { } hostEphemeral )
				throw new RemoteException( RemoteErrors.BadRequest, "The host sent no valid ephemeral key." );

			byte[] clientToHost, hostToClient;
			using ( hostEphemeral )
			{
				var ikm = Concat(
					ephemeral.DeriveRawSecretAgreement( hostEphemeral.PublicKey ),
					device.Agreement.DeriveRawSecretAgreement( hostEphemeral.PublicKey ),
					ephemeral.DeriveRawSecretAgreement( hostStatic.PublicKey ) );
				(clientToHost, hostToClient) = DeriveKeys( ikm, ephemeralKey, hostEphemeral.ExportSubjectPublicKeyInfo(), helloText, answerMessage.Data );
			}

			connection = new( carrier, clientToHost, hostToClient );
			await connection.SendAsync( Frame.Create( "ready" ) ).ConfigureAwait( false );
			if ( await connection.ReceiveAsync( cancellationToken ).ConfigureAwait( false ) is not { } ready || Frame.ReadType( ready ) != "ready" )
				throw new IOException( connection.CloseReason ?? "The host closed the connection during the handshake." );
			carrier.MarkSecured();
			return new( hostId, Frame.ReadStrings( answer, "scopes" ) ?? [], connection );
		}
		catch
		{
			if ( connection is not null )
				await connection.DisposeAsync().ConfigureAwait( false );
			else
				await carrier.DisposeAsync().ConfigureAwait( false );
			throw;
		}
	}

	/// <summary>Checks a <c>pair</c> frame's MAC against the live offers, stores a new device with the <c>view</c> scope, or <c>view</c> and
	/// <c>control</c> for an offer that grants control at once, and answers <c>paired</c>; a device id already paired is refused.</summary>
	private static async Task PairDeviceAsync( MessageConnection carrier, JsonObject pair, RemoteKeys host, DeviceStore devices, PairingOffers offers )
	{
		if ( Frame.ReadString( pair, "deviceId" ) is not { } deviceId || !Guid.TryParseExact( deviceId, "N", out _ )
			|| Frame.ReadString( pair, "deviceKey" ) is not { } deviceKey
			|| Frame.ReadString( pair, "mac" ) is not { } mac )
		{
			await carrier.SendAsync( Frame.BuildErr( RemoteErrors.BadRequest, "The pair frame is incomplete." ) ).ConfigureAwait( false );
			return;
		}

		using ( var key = RemoteKeys.ImportAgreementKey( deviceKey ) )
		{
			if ( key is null )
			{
				await carrier.SendAsync( Frame.BuildErr( RemoteErrors.BadRequest, "The device key is not a P-256 public key." ) ).ConfigureAwait( false );
				return;
			}
		}

		if ( devices.Find( deviceId ) is not null )
		{
			// A new key must not take over an existing device's scopes, uploads and push subscriptions.
			await carrier.SendAsync( Frame.BuildErr( RemoteErrors.BadRequest, AlreadyPaired ) ).ConfigureAwait( false );
			return;
		}

		if ( offers.TryConsume( host.HostId, deviceKey, mac ) is not { } offer )
		{
			await carrier.SendAsync( Frame.BuildErr( RemoteErrors.BadRequest, "The pairing link is wrong, used or expired. Show a new one on the host." ) ).ConfigureAwait( false );
			return;
		}

		var name = (Frame.ReadString( pair, "name" ) ?? "").Trim();
		var device = new PairedDevice
		{
			DeviceId = deviceId,
			Name = name.Length == 0 ? "Device" : name[..Math.Min( name.Length, MaxNameLength )],
			DeviceKey = deviceKey,
			Scopes = offer.Control ? DeviceScopes.Controlling : DeviceScopes.Paired,
			Paired = DateTimeOffset.UtcNow,
		};
		if ( !devices.TryAdd( device ) )
		{
			await carrier.SendAsync( Frame.BuildErr( RemoteErrors.BadRequest, AlreadyPaired ) ).ConfigureAwait( false );
			return;
		}
		var paired = Frame.Create( "paired" );
		paired["hostId"] = host.HostId;
		paired["scopes"] = Frame.BuildStrings( device.Scopes );
		await carrier.SendAsync( paired ).ConfigureAwait( false );
	}

	/// <summary>Answers a paired device's <c>hello</c>, derives the keys and exchanges the encrypted <c>ready</c> frames; refuses unknown or revoked devices with <c>revoked</c>.</summary>
	/// <param name="carrier">The connection.</param>
	/// <param name="hello">The device's <c>hello</c> frame.</param>
	/// <param name="helloText">The <c>hello</c> message's bytes as received, which the keys are bound to.</param>
	/// <param name="host">The host's static keys.</param>
	/// <param name="devices">The paired devices.</param>
	/// <param name="cancellationToken">Cancels the handshake.</param>
	private static async Task<HostSession?> AcceptHelloAsync( MessageConnection carrier, JsonObject hello, byte[] helloText, RemoteKeys host, DeviceStore devices, CancellationToken cancellationToken )
	{
		if ( Frame.ReadLong( hello, "v" ) != Frame.Version )
		{
			await carrier.SendAsync( Frame.BuildErr( RemoteErrors.Version, $"The host speaks protocol version {Frame.Version}." ) ).ConfigureAwait( false );
			return null;
		}

		if ( Frame.ReadString( hello, "deviceId" ) is not { } deviceId || devices.Find( deviceId ) is not { } device )
		{
			await carrier.SendAsync( Frame.BuildErr( RemoteErrors.Revoked, "This device is not paired with the host." ) ).ConfigureAwait( false );
			return null;
		}

		using var deviceStatic = RemoteKeys.ImportAgreementKey( device.DeviceKey );
		using var deviceEphemeral = Frame.ReadString( hello, "eph" ) is { } eph ? RemoteKeys.ImportAgreementKey( eph ) : null;
		if ( deviceStatic is null || deviceEphemeral is null )
		{
			await carrier.SendAsync( Frame.BuildErr( RemoteErrors.BadRequest, "The hello frame has no valid ephemeral key." ) ).ConfigureAwait( false );
			return null;
		}

		using var ephemeral = ECDiffieHellman.Create( ECCurve.NamedCurves.nistP256 );
		var ephemeralKey = ephemeral.ExportSubjectPublicKeyInfo();
		var answer = Frame.Create( "hello" );
		answer["v"] = Frame.Version;
		answer["hostId"] = host.HostId;
		answer["eph"] = Base64Url.EncodeToString( ephemeralKey );
		answer["scopes"] = Frame.BuildStrings( device.Scopes );
		var answerText = Frame.Serialize( answer );
		await carrier.SendMessageAsync( new( true, answerText ) ).ConfigureAwait( false );

		var ikm = Concat(
			ephemeral.DeriveRawSecretAgreement( deviceEphemeral.PublicKey ),
			ephemeral.DeriveRawSecretAgreement( deviceStatic.PublicKey ),
			host.Agreement.DeriveRawSecretAgreement( deviceEphemeral.PublicKey ) );
		var (clientToHost, hostToClient) = DeriveKeys( ikm, deviceEphemeral.ExportSubjectPublicKeyInfo(), ephemeralKey, helloText, answerText );
		var connection = new SecureConnection( carrier, hostToClient, clientToHost );
		if ( await connection.ReceiveAsync( cancellationToken ).ConfigureAwait( false ) is not { } ready || Frame.ReadType( ready ) != "ready" )
		{
			await connection.DisposeAsync().ConfigureAwait( false );
			return null;
		}
		await connection.SendAsync( Frame.Create( "ready" ) ).ConfigureAwait( false );
		return new( device, connection );
	}

	/// <summary>Receives the host's plaintext answer of the expected type, turning an <c>err</c> into a <see cref="RemoteException"/>.</summary>
	private static async Task<JsonObject> ReceiveAnswerAsync( MessageConnection carrier, string expected, CancellationToken cancellationToken ) =>
		ReadAnswer( await carrier.ReceiveAsync( cancellationToken ).ConfigureAwait( false ) ?? throw new IOException( "The host closed the connection during the handshake." ), expected );

	/// <summary>Checks the host's plaintext answer is of the expected type, turning an <c>err</c> into a <see cref="RemoteException"/>.</summary>
	private static JsonObject ReadAnswer( JsonObject answer, string expected )
	{
		var type = Frame.ReadType( answer );
		if ( type == Frame.Err )
			throw new RemoteException( Frame.ReadString( answer, "code" ) ?? RemoteErrors.Failed, Frame.ReadString( answer, "message" ) ?? "The host refused the connection." );
		if ( type != expected )
			throw new RemoteException( RemoteErrors.BadRequest, $"The host answered {type} where {expected} was due." );
		return answer;
	}

	/// <summary>Parses a plaintext handshake message.</summary>
	/// <exception cref="InvalidDataException">The message is binary or not a frame.</exception>
	private static JsonObject ParseText( CarrierMessage message ) =>
		message.IsText ? Frame.Parse( message.Data ) : throw new InvalidDataException( "A plaintext frame arrived as a binary message." );

	/// <summary>Derives both directions' keys: HKDF-SHA256 over the 3DH secrets with salt
	/// SHA-256( device ephemeral SPKI ‖ host ephemeral SPKI ‖ SHA-256( device hello ) ‖ SHA-256( host hello ) ), so a changed plaintext hello breaks the keys.</summary>
	/// <param name="ikm">The 3DH secrets; zeroed afterwards.</param>
	/// <param name="clientEphemeral">The device's ephemeral SPKI.</param>
	/// <param name="hostEphemeral">The host's ephemeral SPKI.</param>
	/// <param name="clientHello">The device's <c>hello</c> message bytes as sent.</param>
	/// <param name="hostHello">The host's <c>hello</c> message bytes as sent.</param>
	/// <returns>The device-to-host and the host-to-device key, 32 bytes each.</returns>
	internal static (byte[] ClientToHost, byte[] HostToClient) DeriveKeys( byte[] ikm, byte[] clientEphemeral, byte[] hostEphemeral, byte[] clientHello, byte[] hostHello )
	{
		var salt = SHA256.HashData( Concat( clientEphemeral, hostEphemeral, SHA256.HashData( clientHello ), SHA256.HashData( hostHello ) ) );
		var keys = (HKDF.DeriveKey( HashAlgorithmName.SHA256, ikm, 32, salt, ClientToHostInfo ), HKDF.DeriveKey( HashAlgorithmName.SHA256, ikm, 32, salt, HostToClientInfo ));
		CryptographicOperations.ZeroMemory( ikm );
		return keys;
	}

	/// <summary>Joins byte arrays.</summary>
	private static byte[] Concat( params byte[][] parts )
	{
		var joined = new byte[parts.Sum( part => part.Length )];
		var offset = 0;
		foreach ( var part in parts )
		{
			part.CopyTo( joined, offset );
			offset += part.Length;
		}
		return joined;
	}
}
