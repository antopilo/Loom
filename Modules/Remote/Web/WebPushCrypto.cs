using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Lokn.Modules.Remote;

/// <summary>Encrypts web push messages (RFC 8291, <c>aes128gcm</c> of RFC 8188) and signs VAPID tokens (RFC 8292).</summary>
internal static class WebPushCrypto
{
	/// <summary>The record size written in the header; a message is always one record.</summary>
	public const int RecordSize = 4096;

	/// <summary>The size of an uncompressed P-256 point: 0x04, then X and Y.</summary>
	public const int PointSize = 65;

	/// <summary>The size of the subscription's authentication secret.</summary>
	public const int AuthSecretSize = 16;

	/// <summary>The size of the GCM tag.</summary>
	private const int TagSize = 16;

	/// <summary>The size of the salt.</summary>
	private const int SaltSize = 16;

	/// <summary>The size of the header: salt, record size, key length, sender key.</summary>
	private const int HeaderSize = SaltSize + 4 + 1 + PointSize;

	/// <summary>Gets the most plaintext one record holds: the record size less the tag and the padding delimiter.</summary>
	public static int MaxPlaintext => RecordSize - TagSize - 1;

	/// <summary>Encrypts a message for a subscription with a new sender key and a random salt.</summary>
	/// <param name="plaintext">The message, at most <see cref="MaxPlaintext"/> bytes.</param>
	/// <param name="receiverKey">The subscription's <c>p256dh</c> key, an uncompressed P-256 point.</param>
	/// <param name="authSecret">The subscription's 16-byte <c>auth</c> secret.</param>
	/// <returns>The request body: the header, then the one encrypted record.</returns>
	/// <exception cref="ArgumentException">The message is too long, or a key or the secret is malformed.</exception>
	public static byte[] Encrypt( ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> receiverKey, ReadOnlySpan<byte> authSecret )
	{
		using var sender = ECDiffieHellman.Create( ECCurve.NamedCurves.nistP256 );
		Span<byte> salt = stackalloc byte[SaltSize];
		RandomNumberGenerator.Fill( salt );
		return Encrypt( plaintext, receiverKey, authSecret, sender, salt );
	}

	/// <summary>Encrypts a message for a subscription with a given sender key and salt, as RFC 8291 section 3.4 derives the keys.</summary>
	/// <param name="plaintext">The message, at most <see cref="MaxPlaintext"/> bytes.</param>
	/// <param name="receiverKey">The subscription's <c>p256dh</c> key, an uncompressed P-256 point.</param>
	/// <param name="authSecret">The subscription's 16-byte <c>auth</c> secret.</param>
	/// <param name="sender">The application server's P-256 key pair for this message.</param>
	/// <param name="salt">The 16-byte salt.</param>
	/// <returns>The request body: the header, then the one encrypted record.</returns>
	/// <exception cref="ArgumentException">The message is too long, or a key, the secret or the salt is malformed.</exception>
	public static byte[] Encrypt( ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> receiverKey, ReadOnlySpan<byte> authSecret, ECDiffieHellman sender, ReadOnlySpan<byte> salt )
	{
		if ( plaintext.Length > MaxPlaintext )
			throw new ArgumentException( "The push message is too long.", nameof( plaintext ) );
		if ( authSecret.Length != AuthSecretSize )
			throw new ArgumentException( "The authentication secret is not 16 bytes.", nameof( authSecret ) );
		if ( salt.Length != SaltSize )
			throw new ArgumentException( "The salt is not 16 bytes.", nameof( salt ) );

		using var receiver = ImportPoint( receiverKey ) ?? throw new ArgumentException( "The receiver key is not an uncompressed P-256 point.", nameof( receiverKey ) );
		var senderKey = ExportPoint( sender.ExportParameters( false ) );
		var secret = sender.DeriveRawSecretAgreement( receiver.PublicKey );

		// key_info = "WebPush: info" ‖ 0x00 ‖ ua_public ‖ as_public
		var keyInfo = new byte[14 + PointSize * 2];
		"WebPush: info\0"u8.CopyTo( keyInfo );
		receiverKey.CopyTo( keyInfo.AsSpan( 14 ) );
		senderKey.CopyTo( keyInfo.AsSpan( 14 + PointSize ) );
		Span<byte> keyPrk = stackalloc byte[32];
		HKDF.Extract( HashAlgorithmName.SHA256, secret, authSecret, keyPrk );
		Span<byte> ikm = stackalloc byte[32];
		HKDF.Expand( HashAlgorithmName.SHA256, keyPrk, ikm, keyInfo );
		CryptographicOperations.ZeroMemory( secret );

		Span<byte> prk = stackalloc byte[32];
		HKDF.Extract( HashAlgorithmName.SHA256, ikm, salt, prk );
		Span<byte> contentKey = stackalloc byte[16];
		HKDF.Expand( HashAlgorithmName.SHA256, prk, contentKey, "Content-Encoding: aes128gcm\0"u8 );
		Span<byte> nonce = stackalloc byte[12];
		HKDF.Expand( HashAlgorithmName.SHA256, prk, nonce, "Content-Encoding: nonce\0"u8 );

		var body = new byte[HeaderSize + plaintext.Length + 1 + TagSize];
		salt.CopyTo( body );
		BinaryPrimitives.WriteUInt32BigEndian( body.AsSpan( SaltSize ), RecordSize );
		body[SaltSize + 4] = PointSize;
		senderKey.CopyTo( body.AsSpan( SaltSize + 5 ) );

		// The last (and only) record ends its plaintext with the delimiter 0x02 and no padding.
		var record = new byte[plaintext.Length + 1];
		plaintext.CopyTo( record );
		record[^1] = 2;
		using var aes = new AesGcm( contentKey, TagSize );
		aes.Encrypt( nonce, record, body.AsSpan( HeaderSize, record.Length ), body.AsSpan( HeaderSize + record.Length ) );
		return body;
	}

	/// <summary>Builds a VAPID token: a JWT signed with ES256 whose <c>aud</c> is the push service's origin, <c>exp</c> its expiry and <c>sub</c> the contact.</summary>
	/// <param name="key">The application server's P-256 signing key.</param>
	/// <param name="audience">The push service's origin, such as <c>https://fcm.googleapis.com</c>; see <see cref="ComputeAudience"/>.</param>
	/// <param name="expires">When the token expires, at most 24 hours ahead.</param>
	/// <param name="subject">The contact the push service may reach, a <c>mailto:</c> or <c>https:</c> address.</param>
	public static string BuildVapidToken( ECDsa key, string audience, DateTimeOffset expires, string subject )
	{
		var header = Base64Url.EncodeToString( """{"typ":"JWT","alg":"ES256"}"""u8 );
		var claims = new JsonObject
		{
			["aud"] = audience,
			["exp"] = expires.ToUnixTimeSeconds(),
			["sub"] = subject,
		};
		var signed = header + "." + Base64Url.EncodeToString( Encoding.UTF8.GetBytes( claims.ToJsonString() ) );
		var signature = key.SignData( Encoding.ASCII.GetBytes( signed ), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation );
		return signed + "." + Base64Url.EncodeToString( signature );
	}

	/// <summary>Computes the VAPID audience of a push endpoint: its scheme, host and any non-default port.</summary>
	public static string ComputeAudience( Uri endpoint ) => endpoint.GetLeftPart( UriPartial.Authority );

	/// <summary>Exports a public key as an uncompressed point, as <c>applicationServerKey</c>, <c>p256dh</c> and the header carry it.</summary>
	public static byte[] ExportPoint( ECParameters parameters )
	{
		var point = new byte[PointSize];
		point[0] = 4;
		if ( parameters.Q.X is not { Length: 32 } x || parameters.Q.Y is not { Length: 32 } y )
			throw new CryptographicException( "The key is not a P-256 public key." );
		x.CopyTo( point, 1 );
		y.CopyTo( point, 33 );
		return point;
	}

	/// <summary>Imports an uncompressed P-256 point for key agreement, or null when it is not one.</summary>
	public static ECDiffieHellman? ImportPoint( ReadOnlySpan<byte> point )
	{
		if ( point.Length != PointSize || point[0] != 4 )
			return null;
		var key = ECDiffieHellman.Create();
		try
		{
			key.ImportParameters( new ECParameters
			{
				Curve = ECCurve.NamedCurves.nistP256,
				Q = new ECPoint { X = point[1..33].ToArray(), Y = point[33..].ToArray() },
			} );
			return key;
		}
		catch ( CryptographicException )
		{
			key.Dispose();
			return null;
		}
	}
}
