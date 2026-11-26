using System.Text.Json;
using Goo;

namespace Lokn.Modules.Chat;

/// <summary>Shows what the agent is doing while a turn runs: thinking, writing, or a running tool's intent.</summary>
public sealed partial class ChatContent
{
	/// <summary>Stores when the running turn started, or null while idle.</summary>
	private DateTime? _workingSince;

	/// <summary>Builds the working indicator while a turn runs and no question waits for the user.</summary>
	private Blob? BuildWorking()
	{
		if ( !session.IsBusy )
		{
			_workingSince = null;
			return null;
		}

		_workingSince ??= DateTime.UtcNow;
		if ( session.Dialogs.Count > 0 || _leavingDialogs.Count > 0 )
			return null;

		return Cell.Mount<WorkingInput, WorkingIndicator>( "conversation-working", new( ComputeWorkingLabel(), _workingSince.Value ) );
	}

	/// <summary>Names the current phase from the newest assistant message: a running tool's intent, thinking, writing, or working.</summary>
	internal string ComputeWorkingLabel()
	{
		var messages = store.PeekMessages( conversationId );
		var last = messages.Count > 0 ? messages[^1] : null;
		if ( last?.Role != "assistant" || last.RichContent is not { ValueKind: JsonValueKind.Object } rich
			|| !rich.TryGetProperty( "content", out var content ) || content.ValueKind != JsonValueKind.Array )
			return "Working";

		var blocks = content.EnumerateArray().ToArray();
		foreach ( var block in blocks.Reverse() )
		{
			if ( ReadText( block, "type" ) != "toolCall" )
				continue;

			var id = ReadText( block, "id" );
			if ( ComputeToolStatus( id, out _ ) != "Running" )
				continue;

			if ( session.Intents.TryGetValue( id, out var intent ) && intent.Length > 0 )
				return intent;

			var arguments = block.TryGetProperty( "arguments", out var args ) ? args : default;
			return ReadText( arguments, "i" ) is { Length: > 0 } stated ? stated : $"Running {ReadText( block, "name", "a tool" )}";
		}

		return blocks.Length == 0 ? "Working" : ReadText( blocks[^1], "type" ) switch
		{
			"thinking" => "Thinking",
			"text" when !string.IsNullOrWhiteSpace( ReadText( blocks[^1], "text" ) ) => "Writing",
			_ => "Working",
		};
	}
}
