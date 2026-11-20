using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lokn.Relay;

/// <summary>Challenges a host socket and verifies the <c>relay.host</c> frame that answers it.</summary>
/// <remarks>
/// The relay first sends <c>{ "t": "relay.challenge", "nonce" }</c> with a fresh random nonce. The host answers <c>{ "t": "relay.host", "hostKey", "sig" }</c>:
/// <c>hostKey</c> is the host's static P-256 key as base64url SPKI DER, and <c>sig</c> is the base64url IEEE P1363 ECDSA P-256 SHA-256 signature over
/// <c>utf8( "loom-relay-proof v2\n" + hostId + "\n" + purpose + "\n" + nonce )</c>, where <c>purpose</c> is <c>control</c> for <c>/host/{hostId}</c> and
/// <c>data/{sid}</c> for <c>/host/{hostId}/{sid}</c>. A proof therefore works for one socket only: a captured one cannot be replayed.
/// </remarks>
internal static class HostProof
{
	/// <summary>Defines the purpose of a host's control socket.</summary>
	internal const string ControlPurpose = "control";

	/// <summary>Defines the length of a host id in base64url characters.</summary>
	private const int HostIdLength = 22;

	/// <summary>Defines the length of an IEEE P1363 P-256 signature in bytes.</summary>
	private const int SignatureLength = 64;

	/// <summary>Defines the object identifier of the P-256 curve.</summary>
	private const string P256Oid = "1.2.840.10045.3.1.7";

	/// <summary>Defines the domain prefix of the signed text.</summary>
	private const string Domain = "loom-relay-proof v2";

	/// <summary>Builds the purpose of a host's data socket for session <paramref name="sid"/>.</summary>
	internal static string BuildDataPurpose( string sid ) => "data/" + sid;

	/// <summary>Builds a fresh challenge nonce: 32 random bytes, base64url.</summary>
	internal static string CreateNonce() => Base64Url.EncodeToString( RandomNumberGenerator.GetBytes( 32 ) );

	/// <summary>Computes a host id: the first 22 base64url characters of the SHA-256 of the host's SPKI DER key.</summary>
	internal static string ComputeHostId( ReadOnlySpan<byte> hostKey ) => Base64Url.EncodeToString( SHA256.HashData( hostKey ) )[..HostIdLength];

	/// <summary>Verifies that <paramref name="frame"/> proves ownership of <paramref name="hostId"/> for <paramref name="purpose"/> against the challenge
	/// <paramref name="nonce"/>; otherwise <paramref name="reason"/> says why not.</summary>
	internal static bool Verify( string hostId, ReadOnlyMemory<byte> frame, string nonce, string purpose, out string reason )
	{
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse( frame );
		}
		catch ( JsonException )
		{
			return Reject( "The proof is not JSON.", out reason );
		}
		using ( document )
		{
			var root = document.RootElement;
			if ( root.ValueKind != JsonValueKind.Object || ReadString( root, "t" ) != "relay.host" )
				return Reject( "The first frame is not relay.host.", out reason );
			if ( ReadString( root, "hostKey" ) is not { } hostKeyText || ReadString( root, "sig" ) is not { } signatureText )
				return Reject( "The proof lacks hostKey or sig.", out reason );
			if ( !TryDecode( hostKeyText, out var hostKey ) || !TryDecode( signatureText, out var signature ) )
				return Reject( "The proof is not base64url.", out reason );
			if ( !string.Equals( ComputeHostId( hostKey ), hostId, StringComparison.Ordinal ) )
				return Reject( "The hostId does not match hostKey.", out reason );
			if ( signature.Length != SignatureLength )
				return Reject( "The signature is not P1363 P-256.", out reason );
			using var key = ECDsa.Create();
			try
			{
				key.ImportSubjectPublicKeyInfo( hostKey, out var read );
				if ( read != hostKey.Length || !IsP256( key ) )
					return Reject( "The hostKey is not a P-256 key.", out reason );
			}
			catch ( CryptographicException )
			{
				return Reject( "The hostKey is not a P-256 key.", out reason );
			}
			var signed = Encoding.UTF8.GetBytes( $"{Domain}\n{hostId}\n{purpose}\n{nonce}" );
			if ( !key.VerifyData( signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation ) )
				return Reject( "The signature does not verify; the host may run an older version.", out reason );
			reason = "";
			return true;
		}
	}

	/// <summary>Reads a string property of <paramref name="root"/>, or null when it is missing or not a string.</summary>
	private static string? ReadString( JsonElement root, string name ) =>
		root.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	/// <summary>Decodes base64url text without padding, failing on any other text.</summary>
	private static bool TryDecode( string text, out byte[] bytes )
	{
		try
		{
			bytes = Base64Url.DecodeFromChars( text );
			return true;
		}
		catch ( FormatException )
		{
			bytes = [];
			return false;
		}
	}

	/// <summary>Determines whether <paramref name="key"/> lies on the P-256 curve.</summary>
	private static bool IsP256( ECDsa key )
	{
		var curve = key.ExportParameters( false ).Curve;
		return curve.IsNamed && curve.Oid is { } oid && ( oid.Value == P256Oid || oid.FriendlyName is "nistP256" or "ECDSA_P256" );
	}

	/// <summary>Fails a verification with <paramref name="why"/>.</summary>
	private static bool Reject( string why, out string reason )
	{
		reason = why;
		return false;
	}
}
