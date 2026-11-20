namespace Lokn.Modules.Remote;

/// <summary>A request the other side refused with an <c>err</c> frame, or a refusal to send as one.</summary>
/// <param name="code">The error code, one of <see cref="RemoteErrors"/>.</param>
/// <param name="message">The readable reason.</param>
public sealed class RemoteException( string code, string message ) : Exception( message )
{
	/// <summary>Gets the error code, one of <see cref="RemoteErrors"/>.</summary>
	public string Code => code;
}

/// <summary>The error codes of <c>err</c> frames (docs/remote.md).</summary>
public static class RemoteErrors
{
	/// <summary>The device lacks the scope the request needs.</summary>
	public const string Scope = "scope";

	/// <summary>The request names a chat the host does not have.</summary>
	public const string UnknownChat = "unknown-chat";

	/// <summary>The other side speaks another protocol version.</summary>
	public const string Version = "version";

	/// <summary>The frame is malformed or of an unknown type.</summary>
	public const string BadRequest = "bad-request";

	/// <summary>The request was understood but failed.</summary>
	public const string Failed = "failed";

	/// <summary>The device is unknown or was revoked.</summary>
	public const string Revoked = "revoked";

	/// <summary>The other side already runs as many requests of this connection as it allows; retry after one ends.</summary>
	public const string Busy = "busy";
}
