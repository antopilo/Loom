using System.Text.Json.Nodes;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>Reads and builds the fields the workspace frames of docs/remote.md share between the host and the client.</summary>
internal static class WorkspaceFrames
{
	/// <summary>Reads a required text field, or fails the request as <c>bad-request</c>.</summary>
	/// <param name="frame">The request.</param>
	/// <param name="name">The field.</param>
	public static string ReadRequiredString( JsonObject frame, string name ) =>
		Frame.ReadString( frame, name ) ?? throw new RemoteException( RemoteErrors.BadRequest, $"The field \"{name}\" is missing." );

	/// <summary>Reads a required whole number, or fails the request as <c>bad-request</c>.</summary>
	/// <param name="frame">The request.</param>
	/// <param name="name">The field.</param>
	public static long ReadRequiredLong( JsonObject frame, string name ) =>
		Frame.ReadLong( frame, name ) ?? throw new RemoteException( RemoteErrors.BadRequest, $"The field \"{name}\" is missing." );

	/// <summary>Reads base64 bytes, or fails the request as <c>bad-request</c>.</summary>
	/// <param name="frame">The request or push.</param>
	/// <param name="name">The field.</param>
	public static byte[] ReadData( JsonObject frame, string name )
	{
		try
		{
			return Convert.FromBase64String( ReadRequiredString( frame, name ) );
		}
		catch ( FormatException )
		{
			throw new RemoteException( RemoteErrors.BadRequest, $"The field \"{name}\" is not base64." );
		}
	}

	/// <summary>Reads the optional <c>env</c> object of string values.</summary>
	/// <param name="frame">The request.</param>
	public static IReadOnlyDictionary<string, string>? ReadEnvironment( JsonObject frame )
	{
		if ( frame["env"] is not JsonObject variables )
			return null;

		var environment = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var (name, value) in variables )
		{
			if ( value is not JsonValue text || !text.TryGetValue<string>( out var content ) )
				throw new RemoteException( RemoteErrors.BadRequest, $"The variable \"{name}\" is not text." );
			environment[name] = content;
		}
		return environment;
	}

	/// <summary>Builds the <c>env</c> object, or null for none.</summary>
	/// <param name="environment">The variables.</param>
	public static JsonObject? BuildEnvironment( IReadOnlyDictionary<string, string>? environment )
	{
		if ( environment is null )
			return null;

		var variables = new JsonObject();
		foreach ( var (name, value) in environment )
			variables[name] = value;
		return variables;
	}

	/// <summary>Converts a UTC time to the <c>mtime</c> field: Unix milliseconds, zero for <see cref="DateTime.MinValue"/>.</summary>
	/// <param name="time">The time.</param>
	public static long ComputeMtime( DateTime time ) =>
		time == DateTime.MinValue ? 0 : new DateTimeOffset( DateTime.SpecifyKind( time, DateTimeKind.Utc ) ).ToUnixTimeMilliseconds();

	/// <summary>Converts the <c>mtime</c> field back to a UTC time.</summary>
	/// <param name="mtime">Unix milliseconds.</param>
	public static DateTime ReadMtime( long mtime ) =>
		mtime == 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeMilliseconds( mtime ).UtcDateTime;

	/// <summary>Names a change as the <c>kind</c> of <c>fs.changed</c>.</summary>
	/// <param name="change">The change.</param>
	public static string BuildKind( WorkspaceChange change ) => change switch
	{
		WorkspaceChange.Created => "created",
		WorkspaceChange.Changed => "changed",
		WorkspaceChange.Deleted => "deleted",
		WorkspaceChange.Renamed => "renamed",
		_ => "lost",
	};

	/// <summary>Reads the <c>kind</c> of <c>fs.changed</c>; anything unknown counts as lost changes.</summary>
	/// <param name="kind">The kind.</param>
	public static WorkspaceChange ReadKind( string? kind ) => kind switch
	{
		"created" => WorkspaceChange.Created,
		"changed" => WorkspaceChange.Changed,
		"deleted" => WorkspaceChange.Deleted,
		"renamed" => WorkspaceChange.Renamed,
		_ => WorkspaceChange.Lost,
	};
}
