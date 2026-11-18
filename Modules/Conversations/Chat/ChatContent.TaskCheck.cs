using Goo;
using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Omp;

namespace Lokn.Modules.Chat;

/// <summary>Before a prompt goes to a long chat, asks a small model whether it starts a new task, and if so offers to send it in a new chat.</summary>
/// <remarks>
/// The check runs only when the chat already has <see cref="TaskCheckMinPrompts"/> prompts and its context is at least
/// <see cref="TaskCheckShare"/> full, so short chats send at once. A failed or slow check sends the prompt here.
/// </remarks>
public sealed partial class ChatContent
{
	/// <summary>Defines whether a prompt that looks like a new task is offered a new chat before it is sent.</summary>
	internal static readonly Setting<bool> NewTaskCheck = new(
		"conversations.new-task-check", "Suggest a new chat for a new task",
		"When a prompt starts a different task, offer to send it to a new chat.",
		true, "Conversations" );

	/// <summary>Defines the share of the context window a chat must use before its prompts are checked.</summary>
	internal const double TaskCheckShare = 0.2;

	/// <summary>Defines how many user prompts a chat must hold before its next prompt is checked.</summary>
	internal const int TaskCheckMinPrompts = 2;

	/// <summary>Defines how long the check may take before the prompt is sent here without it.</summary>
	private const int TaskCheckSeconds = 10;

	/// <summary>Defines the isolated question that tells a new task from more of the same one.</summary>
	internal const string TaskCheckQuestion = "The attached file shows the first and latest prompts a user gave a coding agent in one conversation, then the prompt they are about to send. "
		+ "Reply NEW if the new prompt starts a separate task that does not need anything from the conversation so far. "
		+ "Reply SAME if it continues, refines, fixes or asks about the conversation's work. When unsure, reply SAME. Reply with one word only.";

	/// <summary>Stops the running check, or null when no check runs.</summary>
	private CancellationTokenSource? _taskCheck;

	/// <summary>Reports whether the check found the prompt in the composer to be a new task, which shows the suggestion card.</summary>
	private bool _isNewTaskSuggested;

	/// <summary>Gets whether the prompt about to be sent is checked first: the setting is on, the chat is long enough, and the prompt is text, not a command.</summary>
	private bool ComputeTaskCheckDue( string text )
	{
		var conversation = store.Get( conversationId );
		if ( conversation.IsTaskCheckOff || conversation.Profile != context.App.Settings.ActiveProfile
			|| !context.App.Settings.Get( NewTaskCheck, ComputeScope( conversation.Profile ) )
			|| string.IsNullOrWhiteSpace( text ) || text.TrimStart().StartsWith( '/' ) )
			return false;

		var messages = store.GetMessages( conversationId );
		return messages.Count( message => message.Role == "user" && message.Text.Length > 0 ) >= TaskCheckMinPrompts
			&& ComputeContextShare( conversation, messages ) >= TaskCheckShare;
	}

	/// <summary>Gets the share of the context window the chat uses, estimated from its saved text before Omp reports it, or zero when the window is unknown.</summary>
	private double ComputeContextShare( ConversationInfo conversation, IReadOnlyList<ConversationMessage> messages )
	{
		var window = _contextWindow;
		if ( window <= 0 )
		{
			var model = _connectedModel.Length > 0 ? _connectedModel : context.App.Settings.Get( OmpModule.Model, ComputeScope( conversation.Profile ) );
			window = backend.GetCachedModels( conversation.Profile )?.FirstOrDefault( item => item.Selector == model )?.ContextWindow ?? 0;
		}
		if ( window <= 0 )
			return 0;

		// About four characters make a token; the estimate stands in until Omp measures the context after the next reply.
		var tokens = _contextTokens ?? messages.Sum( message => (long)message.Text.Length ) / 4;
		return (double)tokens / window;
	}

	/// <summary>Builds what the small model reads: the chat's first and latest prompts, then the new one, each bounded.</summary>
	internal static string BuildTaskCheckMaterial( IReadOnlyList<ConversationMessage> messages, string prompt ) =>
		BuildTitleMaterial( messages ) + "\n\nNew prompt:\n" + TrimTitlePrompt( prompt );

	/// <summary>Reads the small model's answer: true only for an answer that starts with NEW, so anything unclear sends the prompt here.</summary>
	internal static bool ReadNewTaskAnswer( string answer ) =>
		answer.Trim().TrimStart( '*', '"', '\'', '`' ).StartsWith( "NEW", StringComparison.OrdinalIgnoreCase );

	/// <summary>Starts the check for the prompt in the composer; the prompt is sent, or the suggestion shown, when it ends.</summary>
	private void UpdateTaskCheck( string text )
	{
		var conversation = store.Get( conversationId );
		var material = BuildTaskCheckMaterial( store.GetMessages( conversationId ), text );
		var cancel = CancellationTokenSource.CreateLinkedTokenSource( context.Lifetime );
		cancel.CancelAfter( TimeSpan.FromSeconds( TaskCheckSeconds ) );
		_taskCheck = cancel;
		_auxiliaryCallCount++;
		_status = "Checking whether this is a new task…";
		MarkChanged();
		_ = UpdateTaskCheckAsync( cancel, conversation.Profile, material );
	}

	/// <summary>Asks the small model off the UI thread; a failure or timeout counts as the same task.</summary>
	private async Task UpdateTaskCheckAsync( CancellationTokenSource cancel, SettingsProfile profile, string material )
	{
		var isNew = false;
		try
		{
			var answer = await backend.AskSmallAsync( profile, TaskCheckQuestion, material, cancel.Token ).ConfigureAwait( false );
			isNew = ReadNewTaskAnswer( answer );
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			// The check is a convenience: without an answer the prompt goes to this chat.
		}

		context.Post( () => UpdateTaskChecked( cancel, isNew ) );
	}

	/// <summary>Shows the suggestion for a new task, otherwise sends the prompt; ignored when the user stopped the check.</summary>
	private void UpdateTaskChecked( CancellationTokenSource cancel, bool isNew )
	{
		cancel.Dispose();
		if ( _taskCheck != cancel )
			return;

		_taskCheck = null;
		_status = "Ready";
		if ( !isNew )
		{
			UpdateSendHere();
			return;
		}

		_isNewTaskSuggested = true;
		MarkChanged();
	}

	/// <summary>Stops a running check and keeps the prompt in the composer.</summary>
	/// <returns>Whether a check was running.</returns>
	private bool UpdateTaskCheckStop()
	{
		if ( _taskCheck is not { } cancel )
			return false;

		cancel.Cancel();
		_taskCheck = null;
		_status = "Ready";
		MarkChanged();
		return true;
	}

	/// <summary>Sends the prompt in this chat without checking it again.</summary>
	private void UpdateSendHere()
	{
		_isNewTaskSuggested = false;
		UpdateSend( isChecked: true );
	}

	/// <summary>Sends the prompt and its attachments in a new chat of the same project, or of the same folder outside projects, and empties this chat's composer.</summary>
	private void UpdateSendInNewChat()
	{
		var conversation = store.Get( conversationId );
		if ( conversation.Profile != context.App.Settings.ActiveProfile )
		{
			_status = $"Switch to the {conversation.Profile} profile to start a new chat from this one.";
			MarkChanged();
			return;
		}

		var prompt = ComposePrompt( _composer.Document.GetText(), _attachments );
		// Empty the composer first: the new chat takes this panel's place, and closing saves the composer as this chat's draft.
		_isNewTaskSuggested = false;
		_composer.Document.Apply( new TextChange( new TextRange( 0, _composer.Document.Length ), "" ) );
		_attachments.Clear();
		store.SaveDraft( conversationId, "" );
		var id = context.App.GetModule<ConversationsModule>().StartConversation( context.App, prompt, conversation.ProjectId );
		if ( conversation.ProjectId is null && conversation.Directory is { } folder )
			store.SetDirectory( id, folder );
	}

	/// <summary>Sends the prompt here and stops this chat from checking its prompts again.</summary>
	private void UpdateTaskCheckOff()
	{
		store.SetTaskCheckOff( conversationId );
		UpdateSendHere();
	}

	/// <summary>Builds the card above the prompt that offers a new chat for a prompt that looks like a new task, or null when there is none.</summary>
	private Blob? BuildNewTaskCard()
	{
		if ( !_isNewTaskSuggested )
			return null;

		var theme = Theme.Current;
		var newChat = theme.BuildButton( "New chat", () => context.Run( UpdateSendInNewChat ) );
		newChat.Key = "new-task-new-chat";
		newChat.Color = theme.Accent;
		var here = theme.BuildButton( "Send here", () => context.Run( UpdateSendHere ) );
		here.Key = "new-task-send-here";
		var off = theme.BuildButton( "Don't ask in this chat", () => context.Run( UpdateTaskCheckOff ) );
		off.Key = "new-task-off";
		off.Color = theme.Muted;
		return new Container
		{
			Key = "new-task-card",
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "New task suggestion",
			FlexShrink = 0,
			Gap = theme.Spacing,
			Padding = theme.Spacing * 3 / 2,
			BackgroundColor = theme.Raised,
			BorderColor = theme.Attention,
			BorderWidth = theme.BorderWidth,
			BorderRadius = ChatStyle.BubbleRadius,
			Children =
			[
				new Text
				{
					Content = "This looks like a new task. A new chat starts with a clean context, which is faster and costs less. Enter sends it here.",
					FontSize = ViewStyle.SmallSize,
					Color = theme.Text,
				},
				new Row
				{
					Gap = theme.Spacing,
					FlexWrap = FlexWrap.Wrap,
					Children = [newChat, here, off],
				},
			],
		};
	}
}
