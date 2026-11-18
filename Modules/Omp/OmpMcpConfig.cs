using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lokn.Modules.Omp;

/// <summary>Describes one MCP server as an Omp-owned config file defines it.</summary>
/// <param name="Name">The server name, unique within the file.</param>
/// <param name="Transport">The transport: stdio, http or sse.</param>
/// <param name="Target">The command line or URL the server uses.</param>
/// <param name="IsEnabled">Whether the file lets Omp connect to the server.</param>
/// <param name="Json">The server definition as indented JSON, for editing.</param>
internal sealed record OmpMcpServer( string Name, string Transport, string Target, bool IsEnabled, string Json );

/// <summary>Reads and changes an Omp-native <c>mcp.json</c>, keeping fields Lokn does not know.</summary>
/// <remarks>Omp owns the file and its format; see Omp's <c>mcp-config.md</c>. Lokn keeps no copy.</remarks>
internal static partial class OmpMcpConfig
{
	/// <summary>Defines the schema reference Omp writes at the top of the files it manages.</summary>
	internal const string SchemaUrl = "https://raw.githubusercontent.com/can1357/oh-my-pi/main/packages/coding-agent/src/config/mcp-schema.json";

	/// <summary>Defines the longest server name Omp's config writer accepts.</summary>
	private const int NameLimit = 100;

	/// <summary>Defines how Lokn writes the file: indented with tabs, like the rest of the user's config.</summary>
	private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, IndentCharacter = '\t', IndentSize = 1 };

	/// <summary>Defines the definition a new server starts from.</summary>
	internal const string Template = """
		{
			"type": "stdio",
			"command": "npx",
			"args": ["-y", "package-name"]
		}
		""";

	/// <summary>Lists the servers a file defines, in file order, or none when the file does not exist.</summary>
	/// <exception cref="InvalidDataException">The file is not a JSON object with an object <c>mcpServers</c> map.</exception>
	internal static IReadOnlyList<OmpMcpServer> Read( string path )
	{
		var root = Load( path );
		var disabled = ReadNames( root, "disabledServers" );
		var servers = new List<OmpMcpServer>();
		foreach ( var (name, node) in GetServers( root, create: false ) ?? [] )
		{
			if ( node is not JsonObject server )
				continue;
			var url = ReadString( server, "url" );
			var command = ReadString( server, "command" );
			var transport = ReadString( server, "type" ) is { Length: > 0 } type ? type : url.Length > 0 ? "http" : "stdio";
			var args = server["args"] is JsonArray list ? list.Select( item => item?.GetValueKind() == JsonValueKind.String ? item.GetValue<string>() : item?.ToJsonString() ?? "" ) : [];
			var target = url.Length > 0 ? url : string.Join( ' ', args.Prepend( command ) );
			var enabled = !IsFalse( server["enabled"] ) && !disabled.Contains( name );
			servers.Add( new( name, transport, target, enabled, server.ToJsonString( WriteOptions ) ) );
		}

		return servers;
	}

	/// <summary>Adds a server, or replaces <paramref name="previousName"/> with it, keeping its place in the file.</summary>
	/// <exception cref="FormatException">The name or the definition is not valid.</exception>
	/// <exception cref="InvalidOperationException">Another server already has the name.</exception>
	internal static void Save( string path, string name, string json, string? previousName )
	{
		name = name.Trim();
		ValidateName( name );
		var server = ParseServer( json );
		var root = Load( path );
		var servers = GetServers( root, create: true )!;
		if ( !string.Equals( name, previousName, StringComparison.Ordinal ) && servers.ContainsKey( name ) )
			throw new InvalidOperationException( $"A server named \"{name}\" already exists." );

		if ( previousName is not null && servers.ContainsKey( previousName ) )
		{
			// Rebuild the map so a renamed server keeps its position.
			var entries = servers.ToList();
			servers.Clear();
			foreach ( var (key, value) in entries )
				servers[key == previousName ? name : key] = key == previousName ? server : value?.DeepClone();
		}
		else
		{
			servers[name] = server;
		}

		Write( path, root );
	}

	/// <summary>Removes a server and any enable or disable override that names it.</summary>
	internal static void Remove( string path, string name )
	{
		var root = Load( path );
		GetServers( root, create: false )?.Remove( name );
		RemoveName( root, "disabledServers", name );
		RemoveName( root, "enabledServers", name );
		Write( path, root );
	}

	/// <summary>Turns a server on or off the way Omp's <c>/mcp enable</c> and <c>/mcp disable</c> do for its own files.</summary>
	internal static void SetEnabled( string path, string name, bool enabled )
	{
		var root = Load( path );
		if ( GetServers( root, create: false )?[name] is not JsonObject server )
			throw new KeyNotFoundException( $"No server named \"{name}\"." );

		if ( enabled )
			server.Remove( "enabled" );
		else
			server["enabled"] = false;
		RemoveName( root, "disabledServers", name );
		Write( path, root );
	}

	/// <summary>Parses and checks one server definition with the rules Omp's validator applies.</summary>
	/// <exception cref="FormatException">The text is not a valid server definition.</exception>
	internal static JsonObject ParseServer( string json )
	{
		JsonNode? node;
		try
		{
			node = JsonNode.Parse( json, documentOptions: new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip } );
		}
		catch ( JsonException exception )
		{
			throw new FormatException( $"The definition is not valid JSON: {exception.Message}", exception );
		}

		if ( node is not JsonObject server )
			throw new FormatException( "The definition must be a JSON object." );

		var type = ReadString( server, "type" );
		var hasCommand = ReadString( server, "command" ).Length > 0;
		var hasUrl = ReadString( server, "url" ).Length > 0;
		if ( type.Length > 0 && type is not ("stdio" or "http" or "sse") )
			throw new FormatException( $"Unknown type \"{type}\". Use stdio, http or sse." );
		if ( hasCommand && hasUrl )
			throw new FormatException( "Set \"command\" or \"url\", not both." );
		if ( type is "http" or "sse" && !hasUrl )
			throw new FormatException( $"A {type} server needs \"url\"." );
		if ( type is "stdio" or "" && !hasUrl && !hasCommand )
			throw new FormatException( "A stdio server needs \"command\"." );
		if ( type == "stdio" && hasUrl )
			throw new FormatException( "A stdio server needs \"command\", not \"url\"." );

		return server;
	}

	/// <summary>Checks a name against Omp's config writer: letters, digits, <c>_ - . :</c> and single inner spaces.</summary>
	/// <exception cref="FormatException">The name is not accepted.</exception>
	internal static void ValidateName( string name )
	{
		if ( name.Length is 0 or > NameLimit || !NamePattern().IsMatch( name ) )
			throw new FormatException( $"Use 1 to {NameLimit} letters, digits, _ - . : and single spaces for the server name." );
	}

	/// <summary>Matches a name Omp's config writer accepts.</summary>
	[GeneratedRegex( @"^[A-Za-z0-9_.:-]+( [A-Za-z0-9_.:-]+)*$" )]
	private static partial Regex NamePattern();

	/// <summary>Reads the file as a JSON object, or an empty one when it does not exist.</summary>
	private static JsonObject Load( string path )
	{
		if ( !File.Exists( path ) )
			return [];

		var text = File.ReadAllText( path );
		if ( text.Trim().Length == 0 )
			return [];

		try
		{
			return JsonNode.Parse( text, documentOptions: new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip } ) as JsonObject
				?? throw new InvalidDataException( $"{path} is not a JSON object." );
		}
		catch ( JsonException exception )
		{
			throw new InvalidDataException( $"{path} is not valid JSON: {exception.Message}", exception );
		}
	}

	/// <summary>Gets the <c>mcpServers</c> map, creating it when asked.</summary>
	private static JsonObject? GetServers( JsonObject root, bool create )
	{
		switch ( root["mcpServers"] )
		{
			case JsonObject servers:
				return servers;
			case null when create:
				var created = new JsonObject();
				root["mcpServers"] = created;
				return created;
			case null:
				return null;
			default:
				throw new InvalidDataException( "\"mcpServers\" is not a JSON object." );
		}
	}

	/// <summary>Writes the file with the schema reference first, replacing it in one step so Omp never reads half a file.</summary>
	private static void Write( string path, JsonObject root )
	{
		var ordered = new JsonObject { ["$schema"] = SchemaUrl };
		foreach ( var (key, value) in root )
		{
			if ( key != "$schema" )
				ordered[key] = value?.DeepClone();
		}

		Directory.CreateDirectory( Path.GetDirectoryName( path )! );
		var temporary = path + ".lokn.tmp";
		File.WriteAllText( temporary, ordered.ToJsonString( WriteOptions ) + Environment.NewLine, new UTF8Encoding( false ) );
		File.Move( temporary, path, overwrite: true );
	}

	/// <summary>Reads the names in a top-level string list.</summary>
	private static HashSet<string> ReadNames( JsonObject root, string property ) =>
		root[property] is JsonArray names
			? names.Where( item => item?.GetValueKind() == JsonValueKind.String ).Select( item => item!.GetValue<string>() ).ToHashSet( StringComparer.Ordinal )
			: [];

	/// <summary>Removes a name from a top-level string list, and the list when it becomes empty.</summary>
	private static void RemoveName( JsonObject root, string property, string name )
	{
		if ( root[property] is not JsonArray names )
			return;
		foreach ( var item in names.Where( item => item?.GetValueKind() == JsonValueKind.String && item.GetValue<string>() == name ).ToArray() )
			names.Remove( item );
		if ( names.Count == 0 )
			root.Remove( property );
	}

	/// <summary>Reads a string property, or an empty string when it is missing or not a string.</summary>
	private static string ReadString( JsonObject value, string property ) =>
		value[property] is JsonValue text && text.GetValueKind() == JsonValueKind.String ? text.GetValue<string>().Trim() : "";

	/// <summary>Reads Omp's accepted "off" forms of <c>enabled</c>: false, "false" and "0".</summary>
	private static bool IsFalse( JsonNode? value ) => value?.GetValueKind() switch
	{
		JsonValueKind.False => true,
		JsonValueKind.String => value.GetValue<string>().Trim() is "false" or "0",
		JsonValueKind.Number => value.GetValue<double>() == 0,
		_ => false,
	};
}
