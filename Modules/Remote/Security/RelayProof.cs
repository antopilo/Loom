using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Lokn.Modules.Remote;

/// <summary>Builds and checks the proof a host gives the relay for one socket: ECDSA P-256 SHA-256 (IEEE P1363, base64url) over
/// <c>utf8( "loom-relay-proof v2\n" + hostId + "\n" + purpose + "\n" + nonce )</c>, where <c>nonce</c> is the relay's fresh challenge for that socket and
/// <c>purpose</c> is <see cref="ControlPurpose"/> or <see cref="BuildDataPurpose"/>, so a proof works for one socket only and cannot be replayed.</summary>
public static class RelayProof
{
	/// <summary>The purpose of a host's control socket, <c>/host/{hostId}</c>.</summary>
	public const string ControlPurpose = "control";

	/// <summary>The type of the relay's first frame on a host socket, <c>{ "t": "relay.challenge", "nonce" }</c>.</summary>
	public const string Challenge = "relay.challenge";

	/// <summary>The domain prefix of the signed text.</summary>
	private const string Domain = "loom-relay-proof v2";

	/// <summary>Builds the purpose of a host's data socket for session <paramref name="sid"/>, <c>/host/{hostId}/{sid}</c>.</summary>
	public static string BuildDataPurpose( string sid ) => "data/" + sid;

	/// <summary>Builds the host's answer to a challenge: <c>{ "t": "relay.host", "hostKey", "sig" }</c>.</summary>
	/// <param name="host">The host's static keys.</param>
	/// <param name="nonce">The nonce of the relay's <c>relay.challenge</c>.</param>
	/// <param name="purpose">What the socket is for.</param>
	public static JsonObject BuildHello( RemoteKeys host, string nonce, string purpose )
	{
		var hello = Frame.Create( "relay.host" );
		hello["hostKey"] = host.PublicKeyText;
		hello["sig"] = Base64Url.EncodeToString( host.Sign( BuildSigned( host.HostId, purpose, nonce ) ) );
		return hello;
	}

	/// <summary>Checks a proof: the host id derives from the key and the signature covers this nonce and purpose.</summary>
	public static bool Verify( string hostId, string hostKey, string nonce, string purpose, string sig )
	{
		try
		{
			var spki = Base64Url.DecodeFromChars( hostKey );
			if ( RemoteKeys.ComputeHostId( spki ) != hostId )
				return false;
			using var key = ECDsa.Create();
			key.ImportSubjectPublicKeyInfo( spki, out _ );
			return key.KeySize == 256 && key.VerifyData( BuildSigned( hostId, purpose, nonce ), Base64Url.DecodeFromChars( sig ), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation );
		}
		catch ( Exception exception ) when ( exception is FormatException or CryptographicException )
		{
			return false;
		}
	}

	/// <summary>Builds the signed bytes.</summary>
	private static byte[] BuildSigned( string hostId, string purpose, string nonce ) => Encoding.UTF8.GetBytes( $"{Domain}\n{hostId}\n{purpose}\n{nonce}" );
}
