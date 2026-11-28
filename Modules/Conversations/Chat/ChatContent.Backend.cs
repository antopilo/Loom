using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Conversations;

namespace Lokn.Modules.Chat;

/// <summary>Hands what the user types to the chat's session, and follows the session's changes in the composer, transcript and panel.</summary>
public sealed partial class ChatContent
{
	/// <summary>Defines how much of each prompt an automatic title may send to its isolated small-model call.</summary>
	private const int TitlePromptLimit = 2000;

	/// <summary>Defines the prompt that asks the agent to finish a turn a module reload stopped.</summary>
	internal const string ContinuePrompt = "Lokn reloaded while you were working, which stopped your turn. Continue where you left off. If you were waiting for an answer to a question, ask it again.";

	/// <summary>Defines the isolated question that names a chat from bounded user prompts, with one emoji for its subject first.</summary>
	internal const string TitleQuestion = "Reply with only a title for this conversation as it stands now, starting with one emoji that fits its subject and a space: "
		+ "at most 4 plain words after the emoji, no category prefix, no quotes, no trailing punctuation.";

	/// <summary>Defines the isolated question that names a chat again for the task it works on now, with its emoji, keeping a title that still fits.</summary>
	internal const string RetitleQuestion = "Reply with only a title for what this conversation works on now, weighing the latest prompts most, starting with one emoji "
		+ "that fits its subject and a space: at most 4 plain words after the emoji, no category prefix, no quotes, no trailing punctuation. "
		+ "If the current title still fits, reply with it unchanged.";

	/// <summary>Defines how many new user prompts a chat takes before it is named again for its current task.</summary>
	internal const int RetitleEvery = 3;

	/// <summary>Defines how many of the latest user prompts name a chat again.</summary>
	private const int RetitlePrompts = 3;

	/// <summary>Defines whether chats the user did not name are named again as their task changes.</summary>
	internal static readonly Setting<bool> AutoRename = new(
		"conversations.auto-rename", "Rename chats as their task changes",
		"Every few prompts, give a chat you did not name a title for what it works on now.",
		true, "Conversations" );

	/// <summary>Subscribes this view to its session's events, or unsubscribes it when the panel closes.</summary>
	private void UpdateSubscription( bool isSubscribed )
	{
		if ( isSubscribed )
		{
			session.Changed += MarkChanged;
			session.AttentionRequested += UpdateAttentionRequested;
			session.AttentionCleared += UpdateAttentionCleared;
			session.PromptAdmitted += UpdatePromptAdmitted;
			session.PromptRefused += UpdatePromptRefused;
			session.InterjectRefused += UpdateInterjectRefused;
			session.UnqueueRestored += UpdateUnqueueRestored;
			session.EditorTextRequested += UpdateEditorText;
			session.ToolChanged += UpdateToolChanged;
			session.DialogLeft += UpdateDialogLeft;
			session.DialogAnswered += UpdateDialogAnswered;
			session.DialogsChanged += UpdateDialogsChanged;
			session.Connecting += UpdateConnecting;
			session.Cleared += UpdateCleared;
			session.OutputAppended += _transcript.ScrollToEnd;
			return;
		}

		session.Changed -= MarkChanged;
		session.AttentionRequested -= UpdateAttentionRequested;
		session.AttentionCleared -= UpdateAttentionCleared;
		session.PromptAdmitted -= UpdatePromptAdmitted;
		session.PromptRefused -= UpdatePromptRefused;
		session.InterjectRefused -= UpdateInterjectRefused;
		session.UnqueueRestored -= UpdateUnqueueRestored;
		session.EditorTextRequested -= UpdateEditorText;
		session.ToolChanged -= UpdateToolChanged;
		session.DialogLeft -= UpdateDialogLeft;
		session.DialogAnswered -= UpdateDialogAnswered;
		session.DialogsChanged -= UpdateDialogsChanged;
		session.Connecting -= UpdateConnecting;
		session.Cleared -= UpdateCleared;
		session.OutputAppended -= _transcript.ScrollToEnd;
	}

	/// <summary>Highlights this panel and tells the user the session waits for them, marking a question as waiting and a finished turn as done, with the chat's project, branch and changes.</summary>
	private void UpdateAttentionRequested( string title, string message )
	{
		var details = new List<string>();
		if ( _gitWatch?.State is { IsRepository: true } state )
		{
			if ( (state.Branch ?? state.Commit) is { } head )
				details.Add( head );
			if ( state.Files > 0 )
				details.Add( $"{Count( state.Files, "file", "files" )} · +{state.Added:N0} −{state.Removed:N0}" );
		}
		context.RequestAttention( new PanelAttention( title, message )
		{
			Tone = session.PendingDialog is not null ? NotificationTone.Attention
				: session.Status == "Ready" ? NotificationTone.Success
				: NotificationTone.None,
			Project = store.Find( conversationId )?.ProjectId is { } projectId ? store.ProjectStore.Find( projectId )?.Name : null,
			Details = details,
		} );
	}

	/// <summary>Withdraws this panel's request for the user once the agent works on alone.</summary>
	private void UpdateAttentionCleared() => context.ClearAttention();

	/// <summary>Empties the composer of a prompt Omp admitted, if it still holds it, and drops the attachments it carried.</summary>
	private void UpdatePromptAdmitted( string text, string[] attachments )
	{
		if ( _composer.Document.GetText() == text )
			_composer.Document.Apply( new TextChange( new TextRange( 0, text.Length ), "" ) );
		_attachments.RemoveAll( attachments.Contains );
		store.SaveDraft( conversationId, _composer.Document.GetText() );
	}

	/// <summary>Keeps a prompt Omp never accepted with the conversation: the composer still holds it.</summary>
	private void UpdatePromptRefused() => store.SaveDraft( conversationId, _composer.Document.GetText() );

	/// <summary>Returns a queued or steering message Omp refused, and its attachments, to an empty composer.</summary>
	private void UpdateInterjectRefused( string text, string[] attachments )
	{
		if ( _composer.Document.Length == 0 )
			_composer.Document.Apply( new TextChange( new TextRange( 0, 0 ), text ) );
		_attachments.AddRange( attachments.Where( path => !_attachments.Contains( path ) ) );
		store.SaveDraft( conversationId, _composer.Document.GetText() );
	}

	/// <summary>Puts a message taken back from Omp's queue in the composer when the composer is empty.</summary>
	private void UpdateUnqueueRestored( string text )
	{
		if ( _composer.Document.Length > 0 )
			return;

		_composer.Document.Apply( new TextChange( new TextRange( 0, 0 ), text ) );
		store.SaveDraft( conversationId, text );
		MarkChanged();
	}

	/// <summary>Replaces the composer's text, as an Omp extension asked.</summary>
	private void UpdateEditorText( string text ) => _composer.Document.Apply( new TextChange( new TextRange( 0, _composer.Document.Length ), text ) );

	/// <summary>Rebuilds the row holding a tool call whose live state changed.</summary>
	private void UpdateToolChanged( string callId )
	{
		if ( _toolOwners.TryGetValue( callId, out var owner ) )
			_revisions[owner] = _revisions.GetValueOrDefault( owner ) + 1;
	}

	/// <summary>Forgets the dialog editors of the previous process, whose questions start afresh.</summary>
	private void UpdateConnecting()
	{
		_leavingDialogs.Clear();
		_dialogEditors.Clear();
	}

	/// <summary>Forgets what this view showed of the cleared session and returns to the transcript's end.</summary>
	private void UpdateCleared()
	{
		_leavingDialogs.Clear();
		_dialogEditors.Clear();
		_expanded.Clear();
		_revisions.Clear();
		_cardStates.Clear();
		_transcript.ScrollToEnd();
	}

	/// <summary>Submits the current prompt and its attachments once to the conversation's captured account.</summary>
	internal void UpdateSend() => UpdateSend( isChecked: false );

	/// <summary>Sends the prompt a chat started with, such as Home's: it replaces the composer's text, then goes as if the user pressed Enter.</summary>
	/// <remarks>The starter hands the prompt over itself, so it does not depend on the stored draft having reached this chat yet.</remarks>
	internal void UpdateStart( string prompt )
	{
		if ( _composer.Document.GetText() != prompt )
			_composer.Document.Apply( new TextChange( new TextRange( 0, _composer.Document.Length ), prompt ) );
		UpdateSend();
	}

	/// <summary>Submits the current prompt, first checking whether it starts a new task unless <paramref name="isChecked"/> or the user already saw the suggestion.</summary>
	private void UpdateSend( bool isChecked )
	{
		if ( _taskCheck is not null )
			return;

		var text = _composer.Document.GetText();
		// While a question waits for typed text, the message box is its answer box.
		if ( text.Trim().Length > 0 && UpdateComposerAnswer( text.Trim() ) )
		{
			_composer.Document.Apply( new TextChange( new TextRange( 0, text.Length ), "" ) );
			store.SaveDraft( conversationId, "" );
			return;
		}

		if ( text.Trim() == "/" + ClearCommand.Name )
		{
			_composer.Document.Apply( new TextChange( new TextRange( 0, text.Length ), "" ) );
			store.SaveDraft( conversationId, "" );
			UpdateClear();
			return;
		}

		// A goal command acts on the session at any time, even while the agent works.
		if ( ChatModes.ReadGoal( text ) is not null )
		{
			session.Send( text, [] );
			return;
		}

		if ( session.IsBusy )
		{
			if ( ChatModes.ReadPlanTask( text ) is not null )
			{
				session.UpdateStatus( "Wait for this turn to end, then send /plan." );
				return;
			}

			// While the agent works, Enter queues the message for after the turn or steers it, as the switch beside Send says.
			UpdateInterject( steer: _isSteerMode );
			return;
		}

		if ( string.IsNullOrWhiteSpace( text ) && _attachments.Count == 0 )
			return;

		// Enter while the suggestion shows sends here, as its card says.
		if ( !isChecked && !_isNewTaskSuggested && session.ComputeTaskCheckDue( text ) )
		{
			UpdateTaskCheck( text );
			return;
		}

		_isNewTaskSuggested = false;
		session.Send( text, _attachments.ToArray() );
		_transcript.ScrollToEnd();
	}

	/// <summary>Asks the agent to finish the turn a module reload stopped, leaving the composer and its draft alone.</summary>
	internal void UpdateContinue()
	{
		if ( session.IsBusy )
			return;

		session.Continue();
		_transcript.ScrollToEnd();
	}

	/// <summary>Gets whether the conversation can be cleared now: not while a turn runs.</summary>
	internal bool CanClear => !session.IsBusy;

	/// <summary>Gets whether the agent is connecting, working or waiting on tools.</summary>
	internal bool IsBusy => session.IsBusy;

	/// <summary>Empties the conversation in place: Omp starts a fresh session and the transcript is cleared, keeping the panel, title and project.</summary>
	internal void UpdateClear() => session.Clear();

	/// <summary>Hands the prompt to the running turn the other way than Enter does: steering when Enter queues, queued when it steers; sends normally while idle.</summary>
	internal void UpdateSteer()
	{
		if ( session.IsBusy )
			UpdateInterject( steer: !_isSteerMode );
		else
			UpdateSend();
	}

	/// <summary>Hands the prompt to the running turn: queued for after it, or steering it now; the prompt empties at once, the message shows in the queue, and both come back if Omp refuses it.</summary>
	private void UpdateInterject( bool steer )
	{
		var text = _composer.Document.GetText();
		if ( !session.Interject( text, _attachments.ToArray(), steer ) )
			return;

		_composer.Document.Apply( new TextChange( new TextRange( 0, text.Length ), "" ) );
		_attachments.Clear();
		store.SaveDraft( conversationId, "" );
		MarkChanged();
	}

	/// <summary>Requests cancellation of the running turn, or of the new-task check, without discarding recorded messages.</summary>
	private void UpdateStop()
	{
		if ( !UpdateTaskCheckStop() )
			session.Stop();
	}

	/// <summary>Builds renaming material: the current title, then the latest <see cref="RetitlePrompts"/> user prompts, oldest first, each bounded.</summary>
	internal static string BuildRetitleMaterial( string title, IReadOnlyList<ConversationMessage> messages )
	{
		var latest = messages.Where( message => message.Role == "user" && message.Text.Length > 0 ).TakeLast( RetitlePrompts ).Select( message => TrimTitlePrompt( message.Text ) );
		return "Current title:\n" + title + "\n\nLatest prompts:\n" + string.Join( "\n\n", latest );
	}

	/// <summary>Builds title material from the first and latest user prompts, bounded independently so old chats stay cheap.</summary>
	internal static string BuildTitleMaterial( IReadOnlyList<ConversationMessage> messages )
	{
		var first = default( ConversationMessage );
		var latest = default( ConversationMessage );
		foreach ( var message in messages )
		{
			if ( message.Role != "user" || message.Text.Length == 0 )
				continue;
			first ??= message;
			latest = message;
		}
		if ( first is null )
			return "";
		var firstText = TrimTitlePrompt( first.Text );
		if ( ReferenceEquals( first, latest ) )
			return "First prompt:\n" + firstText;
		return "First prompt:\n" + firstText + "\n\nLatest prompt:\n" + TrimTitlePrompt( latest!.Text );
	}

	/// <summary>Bounds one title prompt without cutting a UTF-16 surrogate pair.</summary>
	private static string TrimTitlePrompt( string text )
	{
		if ( text.Length <= TitlePromptLimit )
			return text;
		var length = TitlePromptLimit;
		if ( char.IsHighSurrogate( text[length - 1] ) )
			length--;
		return text[..length];
	}

	/// <summary>Converts an authoritative Omp message into durable local rich history; a prompt's text leaves out the plan instructions <c>/plan</c> added.</summary>
	internal static ConversationMessage BuildStoredMessage( Guid id, JsonElement message )
	{
		var createdAt = message.TryGetProperty( "timestamp", out var timestamp ) && timestamp.TryGetInt64( out var milliseconds )
			? DateTimeOffset.FromUnixTimeMilliseconds( milliseconds ) : DateTimeOffset.UtcNow;
		var role = ReadText( message, "role", "assistant" );
		var text = ComputeMessageText( message );
		return new( id, role, role == "user" ? ChatModes.StripPlanRequest( text ) : text, createdAt, message.Clone() );
	}

	/// <summary>Extracts visible message text while preserving full rich blocks separately.</summary>
	internal static string ComputeMessageText( JsonElement message )
	{
		if ( !message.TryGetProperty( "content", out var content ) )
			return ReadText( message, "text" );
		if ( content.ValueKind == JsonValueKind.String )
			return content.GetString() ?? "";
		if ( content.ValueKind != JsonValueKind.Array )
			return content.ToString();

		return string.Join( "\n", content.EnumerateArray()
			.Where( block => ReadText( block, "type" ) == "text" )
			.Select( block => ReadText( block, "text" ) ) );
	}
}
