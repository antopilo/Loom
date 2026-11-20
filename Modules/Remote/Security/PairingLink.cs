using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lokn.Modules.Remote;

/// <summary>The data of a pairing link, <c>loom://pair#&lt;payload&gt;</c> or a web client URL with <c>#pair=&lt;payload&gt;</c>, where the payload is base64url JSON.</summary>
/// <param name="HostId">The host's id.</param>
/// <param name="HostKey">The host's static public key, base64url SPKI.</param>
/// <param name="Secret">The one-use pairing secret, base64url of 32 random bytes.</param>
/// <param name="Addrs">The addresses the host listens on, as host:port.</param>
/// <param name="Relay">The relay's address, or null.</param>
public sealed record PairingLink( string HostId, string HostKey, string Secret, IReadOnlyList<string> Addrs, string? Relay )
{
	/// <summary>The scheme and path of a native pairing link.</summary>
	public const string UriPrefix = "loom://pair#";

	/// <summary>The fragment marker of a web client pairing URL.</summary>
	public const string WebMarker = "#pair=";

	/// <summary>Encodes the payload: base64url of its JSON.</summary>
	public string EncodePayload()
	{
		var data = new JsonObject
		{
			["hostId"] = HostId,
			["hostKey"] = HostKey,
			["secret"] = Secret,
			["addrs"] = Frame.BuildStrings( Addrs ),
		};
		if ( Relay is not null )
			data["relay"] = Relay;
		return Base64Url.EncodeToString( Encoding.UTF8.GetBytes( data.ToJsonString() ) );
	}

	/// <summary>Builds the native link, <c>loom://pair#&lt;payload&gt;</c>.</summary>
	public string BuildUri() => UriPrefix + EncodePayload();

	/// <summary>Builds the web client URL for a page address, appending <c>#pair=&lt;payload&gt;</c>.</summary>
	public string BuildWebUrl( string pageUrl )
	{
		var hash = pageUrl.IndexOf( '#' );
		return (hash < 0 ? pageUrl : pageUrl[..hash]) + WebMarker + EncodePayload();
	}

	/// <summary>Reads a native link, a web client URL or a bare payload, or returns null when the text holds no valid pairing data.</summary>
	public static PairingLink? TryParse( string text )
	{
		text = text.Trim();
		var payload = text.StartsWith( UriPrefix, StringComparison.OrdinalIgnoreCase ) ? text[UriPrefix.Length..]
			: text.IndexOf( WebMarker, StringComparison.Ordinal ) is var marker and >= 0 ? text[(marker + WebMarker.Length)..]
			: text;
		try
		{
			if ( JsonNode.Parse( Base64Url.DecodeFromChars( payload ) ) is not JsonObject data
				|| Frame.ReadString( data, "hostId" ) is not { Length: > 0 } hostId
				|| Frame.ReadString( data, "hostKey" ) is not { Length: > 0 } hostKey
				|| Frame.ReadString( data, "secret" ) is not { Length: > 0 } secret
				|| Base64Url.DecodeFromChars( secret ).Length != 32
				|| RemoteKeys.ComputeHostId( Base64Url.DecodeFromChars( hostKey ) ) != hostId )
				return null;
			return new( hostId, hostKey, secret, Frame.ReadStrings( data, "addrs" ) ?? [], Frame.ReadString( data, "relay" ) );
		}
		catch ( Exception exception ) when ( exception is FormatException or JsonException )
		{
			return null;
		}
	}

	/// <summary>Computes the pairing MAC a device sends: base64url( HMAC-SHA256( secret, utf8( hostId + "." + deviceKey ) ) ).</summary>
	/// <param name="secret">The pairing secret, base64url.</param>
	/// <param name="hostId">The host's id.</param>
	/// <param name="deviceKey">The device's public key, base64url SPKI.</param>
	/// <exception cref="FormatException">The secret is not base64url.</exception>
	public static string ComputeMac( string secret, string hostId, string deviceKey ) =>
		Base64Url.EncodeToString( HMACSHA256.HashData( Base64Url.DecodeFromChars( secret ), Encoding.UTF8.GetBytes( hostId + "." + deviceKey ) ) );
}
