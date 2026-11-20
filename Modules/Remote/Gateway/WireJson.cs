using System.Text.Json;
using System.Text.Json.Nodes;
using Lokn.Modules.Projects;

namespace Lokn.Modules.Remote;

/// <summary>Converts the values chat and store frames carry to and from JSON with the protocol's options (<see cref="StoreOp.JsonOptions"/>).</summary>
internal static class WireJson
{
	/// <summary>Serializes a value into a frame field.</summary>
	public static JsonNode? BuildNode<T>( T value ) => JsonSerializer.SerializeToNode( value, StoreOp.JsonOptions );

	/// <summary>Reads a field as a value, or the type's default when the field is missing or null.</summary>
	/// <exception cref="RemoteException">The field holds something else (<c>bad-request</c>).</exception>
	public static T? Read<T>( JsonObject frame, string name )
	{
		try
		{
			return frame[name] is { } node ? node.Deserialize<T>( StoreOp.JsonOptions ) : default;
		}
		catch ( JsonException exception )
		{
			throw new RemoteException( RemoteErrors.BadRequest, $"The field {name} is malformed: {exception.Message}" );
		}
	}

	/// <summary>Reads a field that must be present.</summary>
	/// <exception cref="RemoteException">The field is missing or malformed (<c>bad-request</c>).</exception>
	public static T Require<T>( JsonObject frame, string name ) =>
		Read<T>( frame, name ) ?? throw new RemoteException( RemoteErrors.BadRequest, $"The frame {Frame.ReadType( frame )} needs the field {name}." );

	/// <summary>Reads the conversation id <c>ch</c> a chat frame must carry.</summary>
	/// <exception cref="RemoteException">It is missing or malformed (<c>bad-request</c>).</exception>
	public static Guid RequireChannel( JsonObject frame ) =>
		Frame.ReadChannel( frame ) ?? throw new RemoteException( RemoteErrors.BadRequest, $"The frame {Frame.ReadType( frame )} needs the chat id ch." );

	/// <summary>Converts a JSON node into an element, JSON null for a missing node.</summary>
	public static JsonElement BuildElement( JsonNode? node ) => JsonSerializer.SerializeToElement( node );
}
