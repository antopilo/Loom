using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lokn.Modules.Remote;

/// <summary>Builds and reads protocol frames: one camelCase UTF-8 JSON object with the type <c>t</c> and the optional <c>id</c>, <c>re</c>, <c>ch</c> and <c>seq</c> (docs/remote.md, Frames).</summary>
public static class Frame
{
	/// <summary>The protocol version the <c>hello</c> frames carry as <c>v</c>; 2 binds both plaintext hellos into the key derivation.</summary>
	public const int Version = 2;

	/// <summary>The largest frame any carrier accepts, so a peer cannot make the other side buffer without limit.</summary>
	public const int MaxBytes = 16 * 1024 * 1024;

	/// <summary>The type of a successful answer.</summary>
	public const string Ok = "ok";

	/// <summary>The type of a failed answer, or of a refusal outside any request.</summary>
	public const string Err = "err";

	/// <summary>Creates a frame of the given type.</summary>
	public static JsonObject Create( string type ) => new() { ["t"] = type };

	/// <summary>Builds the successful answer to request <paramref name="re"/>, moving the fields of <paramref name="fields"/> into it; <c>t</c>, <c>id</c> and <c>re</c> in them are ignored.</summary>
	public static JsonObject BuildOk( long re, JsonObject? fields = null )
	{
		var answer = new JsonObject { ["t"] = Ok, ["re"] = re };
		if ( fields is null )
			return answer;

		foreach ( var name in fields.Select( pair => pair.Key ).ToArray() )
		{
			var value = fields[name];
			fields.Remove( name );
			if ( name is not ("t" or "id" or "re") )
				answer[name] = value;
		}
		return answer;
	}

	/// <summary>Builds the failed answer to request <paramref name="re"/>.</summary>
	public static JsonObject BuildErr( long re, string code, string message ) => new() { ["t"] = Err, ["re"] = re, ["code"] = code, ["message"] = message };

	/// <summary>Builds a refusal that answers no request, such as the <c>revoked</c> answer to a <c>hello</c>.</summary>
	public static JsonObject BuildErr( string code, string message ) => new() { ["t"] = Err, ["code"] = code, ["message"] = message };

	/// <summary>Reads the frame type, or an empty string when it is missing.</summary>
	public static string ReadType( JsonObject frame ) => ReadString( frame, "t" ) ?? "";

	/// <summary>Reads the request number, or null for a push or an answer.</summary>
	public static long? ReadId( JsonObject frame ) => ReadLong( frame, "id" );

	/// <summary>Reads the request number an answer answers, or null.</summary>
	public static long? ReadRe( JsonObject frame ) => ReadLong( frame, "re" );

	/// <summary>Reads the conversation id of a chat frame, or null when it is missing or malformed.</summary>
	public static Guid? ReadChannel( JsonObject frame ) => ReadString( frame, "ch" ) is { } text && Guid.TryParseExact( text, "N", out var id ) ? id : null;

	/// <summary>Reads the per-chat sequence number of a host push, or null.</summary>
	public static long? ReadSeq( JsonObject frame ) => ReadLong( frame, "seq" );

	/// <summary>Sets the conversation id of a chat frame in its <c>N</c> form.</summary>
	public static JsonObject WithChannel( this JsonObject frame, Guid conversation )
	{
		frame["ch"] = conversation.ToString( "N" );
		return frame;
	}

	/// <summary>Sets the per-chat sequence number of a host push.</summary>
	public static JsonObject WithSeq( this JsonObject frame, long seq )
	{
		frame["seq"] = seq;
		return frame;
	}

	/// <summary>Reads a string field, or null when it is missing or not a string.</summary>
	public static string? ReadString( JsonObject frame, string name ) =>
		frame[name] is JsonValue value && value.TryGetValue<string>( out var text ) ? text : null;

	/// <summary>Reads a whole-number field, or null when it is missing or not a whole number.</summary>
	public static long? ReadLong( JsonObject frame, string name ) =>
		frame[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<long>( out var number ) ? number : null;

	/// <summary>Reads a Boolean field, or null when it is missing or not a Boolean.</summary>
	public static bool? ReadBool( JsonObject frame, string name ) =>
		frame[name] is JsonValue value && value.TryGetValue<bool>( out var flag ) ? flag : null;

	/// <summary>Reads an array of strings, or null when the field is missing or holds anything else.</summary>
	public static IReadOnlyList<string>? ReadStrings( JsonObject frame, string name )
	{
		if ( frame[name] is not JsonArray array )
			return null;

		var items = new List<string>( array.Count );
		foreach ( var item in array )
		{
			if ( item is not JsonValue value || !value.TryGetValue<string>( out var text ) )
				return null;
			items.Add( text );
		}
		return items;
	}

	/// <summary>Builds a JSON array of strings.</summary>
	public static JsonArray BuildStrings( IEnumerable<string> items ) => new( items.Select( item => (JsonNode?)JsonValue.Create( item ) ).ToArray() );

	/// <summary>Encodes a frame as UTF-8 JSON.</summary>
	public static byte[] Serialize( JsonObject frame ) => Encoding.UTF8.GetBytes( frame.ToJsonString() );

	/// <summary>Decodes UTF-8 JSON bytes into a frame.</summary>
	/// <exception cref="InvalidDataException">The bytes are not one JSON object with a string <c>t</c>.</exception>
	public static JsonObject Parse( ReadOnlySpan<byte> utf8 )
	{
		JsonNode? node;
		try
		{
			node = JsonNode.Parse( utf8 );
		}
		catch ( JsonException exception )
		{
			throw new InvalidDataException( "A frame is not valid JSON.", exception );
		}
		if ( node is not JsonObject frame || ReadString( frame, "t" ) is not { Length: > 0 } )
			throw new InvalidDataException( "A frame is not a JSON object with a type." );
		return frame;
	}
}
