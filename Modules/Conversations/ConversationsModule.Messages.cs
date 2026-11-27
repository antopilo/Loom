using Lokn.Core;

namespace Lokn.Modules.Conversations;

/// <summary>Names the point other modules send a chat a message through, without referencing this module: a
/// <c>Func&lt;string, Guid, string, string?&gt;</c> taking the chat's host id, the chat and the message, and returning why it was not sent, or null.</summary>
/// <remarks>The browser sends the comments a user writes on a report to the chat whose agent made it.</remarks>
public static class ChatMessage
{
	/// <summary>Defines the extension point's name.</summary>
	public const string Point = "conversations.message";
}

/// <summary>Sends chats messages other modules ask for.</summary>
public sealed partial class ConversationsModule
{
	/// <summary>Sends a chat a message as if the user typed it: at once while its agent is idle, else queued for after the running turn.</summary>
	/// <returns>Why the message was not sent, or null when it was.</returns>
	private string? SendMessage( string hostId, Guid conversationId, string text )
	{
		if ( text.Trim().Length == 0 )
			return "The message is empty.";

		if ( _hosts?.Find( new ChatRef( hostId, conversationId ) ) is not { } chat )
			return "The chat that made this is gone.";

		var session = chat.Host.Sessions.GetOrCreate( conversationId );
		if ( !session.IsBusy )
		{
			session.Send( text, [] );
			return null;
		}

		return session.Interject( text, [], steer: false ) ? null : "The chat could not take the message now. Try again when its agent is done.";
	}
}
