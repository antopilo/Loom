using System.Runtime.CompilerServices;
using System.Text.Json;
using Lokn.Modules.Conversations;

namespace Lokn.Modules.Chat;

/// <summary>Keeps what the transcript reads from each stored message, so a rebuild during streaming walks only the message that changed.</summary>
public sealed partial class ChatContent
{
	/// <summary>Stores each message's facts by message instance; the store replaces a message's instance whenever it changes.</summary>
	private readonly ConditionalWeakTable<ConversationMessage, MessageFacts> _facts = [];

	/// <summary>Gets a message's facts, reading its rich content only the first time, or again after a module changes which tools draw cards.</summary>
	private MessageFacts ReadFacts( ConversationMessage message )
	{
		var cardVersion = context.App.Tools.CardVersion;
		if ( _facts.TryGetValue( message, out var facts ) && facts.CardVersion == cardVersion )
			return facts;

		var blocks = message.Role == "assistant" ? ReadBlocks( TranscriptPart.Whole( message ) ).ToArray() : [];
		var isToolCall = blocks.Select( block => ReadText( block, "type" ) == "toolCall" ).ToArray();
		facts = new MessageFacts(
			cardVersion,
			message.Role == "toolResult" && message.RichContent is { } result ? ReadText( result, "toolCallId" ) : null,
			[.. ComputeToolCallIds( message )],
			HasVisibleContent( message ),
			blocks,
			[.. blocks.Select( IsActivityBlock )],
			isToolCall,
			blocks.Where( ( _, index ) => isToolCall[index] ).Any( block => ReadText( block, "name" ) is "edit" or "write" or "ast_edit" ),
			blocks.Where( ( _, index ) => isToolCall[index] ).Any( block => ReadText( block, "name" ) is "todo" or "todo_write" ) );
		_facts.AddOrUpdate( message, facts );
		return facts;
	}

	/// <summary>Reports whether any block a part selects is a tool call.</summary>
	private bool HasToolCall( TranscriptPart part )
	{
		var isToolCall = ReadFacts( part.Message ).IsToolCall;
		for ( var index = part.Start; index < Math.Min( part.End, isToolCall.Length ); index++ )
		{
			if ( isToolCall[index] )
				return true;
		}
		return false;
	}

	/// <summary>Counts the tool calls a part selects as shown, so they never enter with motion later.</summary>
	private void UpdateToolsShown( TranscriptPart part )
	{
		var facts = ReadFacts( part.Message );
		for ( var index = part.Start; index < Math.Min( part.End, facts.Blocks.Length ); index++ )
		{
			if ( facts.IsToolCall[index] )
				_shownTools.Add( ReadText( facts.Blocks[index], "id" ) );
		}
	}

	/// <summary>Describes what the transcript reads from one stored message.</summary>
	/// <param name="CardVersion">The tool card version <see cref="IsActivity"/> was computed under.</param>
	/// <param name="ResultCallId">The tool call a tool result answers, empty when it names none, or null for any other message.</param>
	/// <param name="ToolCallIds">The tool calls the message makes, in order.</param>
	/// <param name="IsVisible">Whether the message shows anything.</param>
	/// <param name="Blocks">An assistant message's content blocks; empty for other roles.</param>
	/// <param name="IsActivity">Whether each block folds into an activity run.</param>
	/// <param name="IsToolCall">Whether each block is a tool call.</param>
	/// <param name="HasEdits">Whether the message calls a tool that changes files.</param>
	/// <param name="HasTodo">Whether the message calls the todo tool.</param>
	private sealed record MessageFacts(
		int CardVersion,
		string? ResultCallId,
		string[] ToolCallIds,
		bool IsVisible,
		JsonElement[] Blocks,
		bool[] IsActivity,
		bool[] IsToolCall,
		bool HasEdits,
		bool HasTodo );
}
