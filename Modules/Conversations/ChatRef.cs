using Lokn.Core;

namespace Lokn.Modules.Conversations;

/// <summary>A chat anywhere: its host and its id.</summary>
/// <remarks>
/// Its text form is the chat's <c>N</c> GUID for This PC and <c>&lt;id&gt;@&lt;hostId&gt;</c> for a paired host, the same suffix Workspaces session keys and
/// <c>lokn://conversations/open/</c> links carry.
/// </remarks>
/// <param name="HostId">The host the chat lives on: <see cref="LoknHost.LocalId"/> for This PC, else the paired host's identifier.</param>
/// <param name="ConversationId">The conversation.</param>
public readonly record struct ChatRef( string HostId, Guid ConversationId )
{
	/// <summary>Gets whether the chat lives on This PC.</summary>
	public bool IsLocal => HostId == LoknHost.LocalId;

	/// <summary>Gets a chat on This PC.</summary>
	/// <param name="conversationId">The conversation.</param>
	public static ChatRef Local( Guid conversationId ) => new( LoknHost.LocalId, conversationId );

	/// <summary>Writes the chat as <c>N</c> for This PC, or <c>N@hostId</c> for a paired host.</summary>
	public override string ToString() => IsLocal ? ConversationId.ToString( "N" ) : $"{ConversationId:N}@{HostId}";

	/// <summary>Reads a chat written by <see cref="ToString"/>: an <c>N</c> or <c>D</c> GUID, optionally followed by <c>@hostId</c>.</summary>
	/// <param name="text">The text.</param>
	/// <param name="chat">The chat read, or the default when the text is not one.</param>
	/// <returns>Whether the text names a chat.</returns>
	public static bool TryParse( string? text, out ChatRef chat )
	{
		chat = default;
		if ( string.IsNullOrEmpty( text ) )
			return false;

		var at = text.IndexOf( '@' );
		var id = at < 0 ? text : text[..at];
		var host = at < 0 ? LoknHost.LocalId : text[(at + 1)..];
		if ( host.Length == 0 || host.Contains( '@' ) || host.Contains( '/' ) )
			return false;
		if ( !Guid.TryParseExact( id, "N", out var conversationId ) && !Guid.TryParseExact( id, "D", out conversationId ) )
			return false;

		chat = new( host, conversationId );
		return true;
	}
}
