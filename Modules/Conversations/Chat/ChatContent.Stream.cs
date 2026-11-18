using Goo;
using Lokn.Modules.Markdown;

namespace Lokn.Modules.Chat;

/// <summary>Decides which text the reader sees arrive live: the streaming reply reveals word by word, and output joining a row on screen fades in once.</summary>
/// <remarks>
/// A message is streaming when the agent is busy, it is the newest message in the conversation, it is the assistant's, and
/// it was created after this panel opened. History, a transcript restored after a reload and a reconnect's replayed messages
/// are older than the panel, so they render at rest. Each streamed block keeps its <see cref="StreamProgress"/> here, so a
/// finished reply keeps its reveal until it has caught up, then renders as static Markdown; a block scrolled back into view
/// is at rest because its reveal completes when it unmounts.
/// </remarks>
public sealed partial class ChatContent
{
	/// <summary>Defines the most characters a block may hold when first seen streaming and still reveal from its start. A longer block either streamed out of view or is a finished reply that is briefly the newest while the next turn starts, so it shows at rest.</summary>
	private const int FreshStreamChars = 160;

	/// <summary>Stores when this panel opened; messages created earlier are history and never stream in.</summary>
	private readonly DateTimeOffset _streamOpenedAt = DateTimeOffset.UtcNow;

	/// <summary>Stores the reveal progress of each text block seen streaming, by block key.</summary>
	private readonly Dictionary<string, StreamProgress> _streams = new( StringComparer.Ordinal );

	/// <summary>Stores the keys of output blocks already built, so their fade-in plays only when they first join a row on screen.</summary>
	private readonly HashSet<string> _enteredBlocks = new( StringComparer.Ordinal );

	/// <summary>Reports whether a message is the reply Omp is streaming now.</summary>
	private bool IsStreaming( Guid messageId )
	{
		if ( !_isBusy )
			return false;

		var messages = store.GetMessages( conversationId );
		return messages.Count > 0 && messages[^1] is { Role: "assistant" } newest && newest.Id == messageId && newest.CreatedAt >= _streamOpenedAt;
	}

	/// <summary>Reports whether a block key, which starts with its message identifier, belongs to the streaming reply.</summary>
	private bool IsStreaming( string key ) => key.Length >= 32 && Guid.TryParseExact( key.AsSpan( 0, 32 ), "N", out var id ) && IsStreaming( id );

	/// <summary>Builds a Markdown text block: revealed word by word while its message streams and until the reveal catches up, static otherwise.</summary>
	/// <param name="key">The block's stable key, starting with its message identifier.</param>
	/// <param name="text">The block's Markdown as received so far.</param>
	private Blob BuildStreamText( string key, string text )
	{
		var isLive = IsStreaming( key );
		if ( _streams.TryGetValue( key, out var progress ) )
		{
			if ( !isLive && progress.Revealed >= text.Length )
				return MarkdownView.Build( text, OpenLink );
		}
		else if ( isLive )
			_streams.Add( key, progress = new() { Revealed = text.Length > FreshStreamChars ? text.Length : 0 } );
		else
			return MarkdownView.Build( text, OpenLink );

		return Cell.Mount<StreamRevealInput, StreamReveal>( "stream:" + key, new( text, isLive, progress, OpenLink ) );
	}

	/// <summary>Wraps an output block so it fades and rises in when it joins a row already on screen; a new row's own entrance covers blocks that arrive with it.</summary>
	/// <param name="key">The block's stable key.</param>
	/// <param name="content">The block to show.</param>
	private Blob BuildEntering( string key, Blob content ) =>
		Cell.Mount<RevealInput, Reveal>( "enter:" + key, new( content, _enteredBlocks.Add( key ) && _isRowLive ) );
}
