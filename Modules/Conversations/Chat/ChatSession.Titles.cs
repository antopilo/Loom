using System.Text.Json;
using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Omp;
using static Lokn.Modules.Chat.ChatContent;

namespace Lokn.Modules.Chat;

/// <summary>Names the chat from its prompts with isolated small-model calls, and asks whether a prompt starts a new task.</summary>
public sealed partial class ChatSession
{
	/// <summary>After a turn, names the chat unless the user named it: once from its first prompts, then again every <see cref="RetitleEvery"/> prompts
	/// while <see cref="AutoRename"/> is on, so the title follows the task the chat works on now.</summary>
	/// <remarks>The small-model call receives at most a few bounded user prompts and the current title, never the session context, tools or project rules.</remarks>
	private void UpdateTitleSummary()
	{
		var conversation = store.Get( conversationId );
		var prompts = store.GetMessages( conversationId ).Count( message => message.Role == "user" && message.Text.Length > 0 );
		if ( conversation.IsTitleChosen || prompts == 0 )
			return;

		if ( !conversation.IsTitleGenerated )
		{
			if ( _isTitleRequested )
				return;

			_isTitleRequested = true;
			_titledAtPrompts = prompts;
			_ = UpdateTitleRequestAsync( TitleQuestion, BuildTitleMaterial( store.GetMessages( conversationId ) ) );
			return;
		}

		// A chat titled before this session started counts its prompts from now: the turn that just ended brought a new one.
		_titledAtPrompts ??= prompts - 1;
		if ( _isRetitling || prompts - _titledAtPrompts < RetitleEvery || !app.Settings.Get( AutoRename, ComputeScope( conversation.Profile ) ) )
			return;

		_isRetitling = true;
		_titledAtPrompts = prompts;
		_ = UpdateTitleRequestAsync( RetitleQuestion, BuildRetitleMaterial( conversation.Title, store.GetMessages( conversationId ) ) );
	}

	/// <summary>Takes a session name Omp reports after the user's own <c>/rename</c>: the title typed, or one Omp generated, cleaned.</summary>
	/// <remarks>
	/// Omp also reports the current name after commands such as <c>/model</c>, which may be an older one; those are ignored, since
	/// the chat's title already is what Lokn last chose. A title the user typed is kept for good: no compaction renames it.
	/// </remarks>
	private void UpdateSessionTitle( string title )
	{
		var rename = _sentCommand is { } sent && (sent == "/rename" || sent.StartsWith( "/rename ", StringComparison.Ordinal )) ? sent["/rename".Length..].Trim() : null;
		if ( rename is { Length: > 0 } )
			store.RenameConversation( conversationId, title );
		else if ( rename is not null )
			store.SetAutomaticTitle( conversationId, title, isGenerated: true );
	}

	/// <summary>Names the chat afresh after a compaction that committed, unless the user named it; otherwise keeps the once-per-chat title.</summary>
	/// <remarks>A compaction marks a new stretch of the conversation, so the title follows what it is about now.</remarks>
	private void UpdateCompactionTitle( JsonElement frame )
	{
		var isCommitted = frame.TryGetProperty( "result", out var result ) && result.ValueKind == JsonValueKind.Object
			&& ReadText( frame, "aborted" ) is not ("True" or "true");
		if ( !isCommitted )
		{
			UpdateTitleSummary();
			return;
		}

		_compactionCount++;
		if ( store.Get( conversationId ).IsTitleChosen )
			return;

		_isTitleRequested = true;
		_titledAtPrompts = store.GetMessages( conversationId ).Count( message => message.Role == "user" && message.Text.Length > 0 );
		_ = UpdateTitleRequestAsync( TitleQuestion, BuildTitleMaterial( store.GetMessages( conversationId ) ) );
	}

	/// <summary>Asks Omp's small-model role for a title from bounded material; a failure keeps the current title.</summary>
	/// <param name="question">The isolated question, such as <see cref="ChatContent.TitleQuestion"/>.</param>
	/// <param name="material">The bounded prompts, and the current title when renaming.</param>
	private async Task UpdateTitleRequestAsync( string question, string material )
	{
		try
		{
			var conversation = store.Get( conversationId );
			_auxiliaryCallCount++;
			MarkChanged();
			var answer = await backend.AskSmallAsync( conversation.Profile, question, material, Lifetime ).ConfigureAwait( false );
			Post( () => UpdateTitleFromAnswer( answer ) );
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			// The title is a convenience: the chat keeps its current name.
		}
		finally
		{
			Post( () => _isRetitling = false );
		}
	}

	/// <summary>Sets the chat's title from an answer, cleaned by the store, and gives Omp's session the same name.</summary>
	private void UpdateTitleFromAnswer( string answer )
	{
		if ( store.SetAutomaticTitle( conversationId, answer, isGenerated: true ) && _session is { } session )
			_ = session.SendAsync( new
			{
				type = "set_session_name",
				name = store.Get( conversationId ).Title,
			} );
	}

	/// <summary>Gets whether a prompt about to be sent is checked first: the setting is on, the chat is long enough, and the prompt is text, not a command.</summary>
	public bool ComputeTaskCheckDue( string text )
	{
		var conversation = store.Get( conversationId );
		var messages = store.GetMessages( conversationId );
		return ComputeTaskCheckDue( app, conversation, messages, text, () => ComputeContextShare( conversation, messages ) );
	}

	/// <summary>Gets whether a prompt about to be sent to a chat is checked first; shared by a session and its proxy in another process.</summary>
	/// <param name="app">The application whose settings and active profile decide whether the check is on.</param>
	/// <param name="conversation">The chat.</param>
	/// <param name="messages">The chat's messages.</param>
	/// <param name="text">The prompt.</param>
	/// <param name="share">Computes the share of the context window the chat uses, read only when the chat is long enough.</param>
	internal static bool ComputeTaskCheckDue( LoknApplication app, ConversationInfo conversation, IReadOnlyList<ConversationMessage> messages, string text, Func<double> share )
	{
		if ( conversation.IsTaskCheckOff || conversation.Profile != app.Settings.ActiveProfile
			|| !app.Settings.Get( NewTaskCheck, ComputeScope( conversation.Profile ) )
			|| string.IsNullOrWhiteSpace( text ) || text.TrimStart().StartsWith( '/' ) )
			return false;

		return messages.Count( message => message.Role == "user" && message.Text.Length > 0 ) >= TaskCheckMinPrompts
			&& share() >= TaskCheckShare;
	}

	/// <summary>Gets the share of the context window the chat uses, estimated from its saved text before Omp reports it, or zero when the window is unknown.</summary>
	private double ComputeContextShare( ConversationInfo conversation, IReadOnlyList<ConversationMessage> messages )
	{
		var window = _contextWindow;
		if ( window <= 0 )
		{
			var model = _connectedModel.Length > 0 ? _connectedModel : app.Settings.Get( OmpModule.Model, ComputeScope( conversation.Profile ) );
			window = backend.GetCachedModels( conversation.Profile )?.FirstOrDefault( item => item.Selector == model )?.ContextWindow ?? 0;
		}
		if ( window <= 0 )
			return 0;

		// About four characters make a token; the estimate stands in until Omp measures the context after the next reply.
		var tokens = _contextTokens ?? messages.Sum( message => (long)message.Text.Length ) / 4;
		return (double)tokens / window;
	}

	/// <summary>Asks the small model whether a prompt starts a new task; a failure, a timeout or an unclear answer counts as the same task.</summary>
	/// <param name="text">The prompt about to be sent.</param>
	/// <param name="cancellationToken">Stops the check, such as when the user stops it or it takes too long.</param>
	/// <returns>Whether the prompt looks like a new task; completes off the UI thread.</returns>
	public async Task<bool> CheckNewTaskAsync( string text, CancellationToken cancellationToken )
	{
		var profile = store.Get( conversationId ).Profile;
		var material = BuildTaskCheckMaterial( store.GetMessages( conversationId ), text );
		_auxiliaryCallCount++;
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken, Lifetime );
		try
		{
			var answer = await backend.AskSmallAsync( profile, TaskCheckQuestion, material, cancel.Token ).ConfigureAwait( false );
			return ReadNewTaskAnswer( answer );
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			// The check is a convenience: without an answer the prompt goes to this chat.
			return false;
		}
	}
}
