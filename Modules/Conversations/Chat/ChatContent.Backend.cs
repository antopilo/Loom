using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Omp;

namespace Lokn.Modules.Chat;

/// <summary>Connects native conversation state to Omp's structured RPC stream.</summary>
public sealed partial class ChatContent
{
	/// <summary>Defines the generated conversation title limit in UTF-16 code units.</summary>
	private const int TitleLimit = 64;

	/// <summary>Defines how much of each prompt an automatic title may send to its isolated small-model call.</summary>
	private const int TitlePromptLimit = 2000;

	/// <summary>Defines the diagnostic Omp prints when its account profile has no model to run.</summary>
	private const string MissingModelDiagnostic = "No default model selected";

	/// <summary>Defines the prompt that asks the agent to finish a turn a module reload stopped.</summary>
	internal const string ContinuePrompt = "Lokn reloaded while you were working, which stopped your turn. Continue where you left off. If you were waiting for an answer to a question, ask it again.";

	/// <summary>Starts the backend without sending a model prompt.</summary>
	private void UpdateConnect()
	{
		if ( _connection is not null )
			return;

		_ = UpdateConnectAsync();
	}

	/// <summary>Observes explicit connection failures after the connection routine reports them in the panel.</summary>
	private async Task UpdateConnectAsync()
	{
		try
		{
			await EnsureConnection();
		}
		catch ( Exception )
		{
			// Connection startup owns the visible error and resets the connection for retry.
		}
	}

	/// <summary>Captures account and project configuration before asynchronously starting the child.</summary>
	private Task<OmpSession> EnsureConnection()
	{
		if ( _connection is not null )
			return _connection;

		// RPC message identifiers are scoped to one child process and restart on reconnect.
		_messageIds.Clear();
		_connectedModel = "";
		_modelInfo = null;
		_efforts = [];
		_effort = "";
		_tools.Clear();
		_dialogs.Clear();
		ClearQueue();
		_dialogEditors.Clear();
		_answers.Clear();
		var conversation = store.Get( conversationId );
		var directory = store.GetDirectory( conversationId );
		_connectionDirectory = directory;
		var sessionDirectory = conversation.BackendSessionPath ?? Path.Combine(
			Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ),
			"Lokn", "sessions", conversation.Profile.ToString(), conversationId.ToString( "N" ) );
		store.SaveBackendSession( conversationId, sessionDirectory );
		_status = "Connecting…";
		MarkChanged();
		_connection = BuildConnectionAsync( conversation.Profile, directory, sessionDirectory );
		return _connection;
	}

	/// <summary>Loads the authoritative backend transcript while keeping local history readable offline.</summary>
	private async Task<OmpSession> BuildConnectionAsync( SettingsProfile profile, string directory, string sessionDirectory )
	{
		OmpSession? session = null;
		try
		{
			session = await backend.OpenSessionAsync( profile, directory, sessionDirectory, context.Lifetime, onStarting: starting =>
			{
				session = starting;
				_session = starting;
				starting.FrameReceived += MarkFrameReceived;
			} );
			if ( _isClosed || context.Lifetime.IsCancellationRequested )
			{
				await session.StopAsync();
				throw new OperationCanceledException( context.Lifetime );
			}

			var data = await session.RequestAsync( "get_messages", cancellationToken: context.Lifetime );
			var state = await session.RequestAsync( "get_state", cancellationToken: context.Lifetime );
			var model = state.TryGetProperty( "model", out var selected ) && selected.ValueKind == JsonValueKind.Object
				? ReadText( selected, "provider" ) + "/" + ReadText( selected, "id" ) : "";
			await session.RequestAsync( "set_ask_dialog", new
			{
				enabled = true
			}, context.Lifetime );
			var messages = new List<ConversationMessage>();
			if ( data.ValueKind == JsonValueKind.Object && data.TryGetProperty( "messages", out var history ) )
			{
				foreach ( var message in history.EnumerateArray() )
					messages.Add( BuildStoredMessage( Guid.NewGuid(), message ) );
			}

			string[] economyEfforts = [];
			string? economyEffort = null;
			if ( messages.Count == 0 && context.App.Settings.Get( OmpModule.Economy, ComputeScope( profile ) ) )
			{
				try
				{
					var available = await session.RequestAsync( "get_available_thinking_levels", cancellationToken: context.Lifetime );
					economyEfforts = available.ValueKind == JsonValueKind.Object && available.TryGetProperty( "levels", out var levels ) && levels.ValueKind == JsonValueKind.Array
						? [.. levels.EnumerateArray().Where( level => level.ValueKind == JsonValueKind.String ).Select( level => level.GetString()! )]
						: [];
					economyEffort = economyEfforts.Contains( "low", StringComparer.Ordinal ) ? "low"
						: economyEfforts.Contains( "minimal", StringComparer.Ordinal ) ? "minimal"
						: economyEfforts.Contains( "off", StringComparer.Ordinal ) ? "off" : null;
					if ( economyEffort is not null )
					{
						await session.RequestAsync( "set_thinking_level", new
						{
							level = economyEffort,
						}, context.Lifetime );
					}
				}
				catch ( Exception error ) when ( error is not OperationCanceledException )
				{
					// An older Omp or a model without selectable effort keeps its own default.
				}
			}

			var connected = session;
			context.Post( () =>
			{
				_session = connected;
				UpdateToolBridge( connected );
				_connectedModel = model;
				UpdateContextUsage( state );
				UpdateEffortState( state );
				if ( economyEfforts.Length > 0 )
					_efforts = economyEfforts;
				if ( economyEffort is not null )
					_effort = economyEffort;
				else
					_ = UpdateEffortsAsync( connected );
				_ = UpdateCompactionSettingsAsync();
				_ = UpdateModelInfoAsync( model );
				if ( messages.Count > 0 )
					store.ReplaceMessages( conversationId, messages );
				_messageIds.Clear();
				_status = "Connected";
				MarkChanged();
			} );
			return session;
		}
		catch ( Exception error )
		{
			if ( session is not null )
			{
				session.FrameReceived -= MarkFrameReceived;
				await session.StopAsync();
			}
			context.Post( () =>
			{
				_connection = null;
				_session = null;
				UpdateToolBridge( null );
				_isBusy = false;
				_status = error is OperationCanceledException ? "Disconnected" : DescribeFailure( error );
				MarkChanged();
			} );
			throw;
		}
	}

	/// <summary>Offers Lokn's agent tools to a connected session, replacing the previous session's bridge.</summary>
	private void UpdateToolBridge( OmpSession? session )
	{
		_toolBridge?.Dispose();
		_toolBridge = null;
		if ( session is null )
			return;

		_toolBridge = new( session, context.App, context.Id, store.Get( conversationId ).Profile, context.Post );
		_toolBridge.Failed += message => context.Post( () =>
		{
			_status = message;
			MarkChanged();
		} );
		_toolBridge.Publish();
	}

	/// <summary>Submits the current prompt and its attachments once to the conversation's captured account; also sends the prompt a chat started with.</summary>
	internal void UpdateSend() => UpdateSend( isChecked: false );

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

		if ( _isBusy )
		{
			// While the agent works, Enter queues the message for after the turn or steers it, as the switch beside Send says.
			UpdateInterject( steer: _isSteerMode );
			return;
		}

		if ( string.IsNullOrWhiteSpace( text ) && _attachments.Count == 0 )
			return;

		// Enter while the suggestion shows sends here, as its card says.
		if ( !isChecked && !_isNewTaskSuggested && ComputeTaskCheckDue( text ) )
		{
			UpdateTaskCheck( text );
			return;
		}

		_isNewTaskSuggested = false;
		UpdateFollowFolder();
		_isBusy = true;
		_sentCommand = text.TrimStart().StartsWith( '/' ) ? text.Trim() : null;
		_status = "Sending…";
		MarkChanged();
		_transcript.ScrollToEnd();
		_ = UpdateSendAsync( EnsureConnection(), text, _attachments.ToArray() );
	}

	/// <summary>Asks the agent to finish the turn a module reload stopped, leaving the composer and its draft alone.</summary>
	internal void UpdateContinue()
	{
		if ( _isBusy )
			return;

		UpdateFollowFolder();
		_isBusy = true;
		_status = "Continuing after reload…";
		MarkChanged();
		_transcript.ScrollToEnd();
		_ = UpdateSendAsync( EnsureConnection(), ContinuePrompt, [] );
	}

	/// <summary>Gets whether the conversation can be cleared now: not while a turn runs.</summary>
	internal bool CanClear => !_isBusy;

	/// <summary>Gets whether the agent is connecting, working or waiting on tools, so switching the panel away would stop it.</summary>
	internal bool IsBusy => _isBusy;

	/// <summary>Empties the conversation in place: Omp starts a fresh session and the transcript is cleared, keeping the panel, title and project.</summary>
	internal void UpdateClear()
	{
		if ( _isBusy )
		{
			_status = "Stop the running turn before clearing the conversation.";
			MarkChanged();
			return;
		}

		_isBusy = true;
		_status = "Clearing…";
		MarkChanged();
		_ = UpdateClearAsync( EnsureConnection() );
	}

	/// <summary>Asks Omp for a new session, then clears what this panel shows of the old one.</summary>
	private async Task UpdateClearAsync( Task<OmpSession> connection )
	{
		try
		{
			var session = await connection;
			var result = await session.RequestAsync( "new_session", cancellationToken: context.Lifetime );
			context.Post( () =>
			{
				_isBusy = false;
				if ( ReadText( result, "cancelled" ) is "True" or "true" )
				{
					_status = "An Omp extension kept the current session.";
					MarkChanged();
					return;
				}

				store.ReplaceMessages( conversationId, [] );
				_messageIds.Clear();
				_tools.Clear();
				_intents.Clear();
				_dialogs.Clear();
				_dialogEditors.Clear();
				_expanded.Clear();
				_revisions.Clear();
				_cardStates.Clear();
				_status = "Cleared";
				MarkChanged();
				_transcript.ScrollToEnd();
			} );
		}
		catch ( Exception error )
		{
			context.Post( () =>
			{
				_isBusy = false;
				_status = error is OperationCanceledException ? "Stopped" : DescribeFailure( error );
				MarkChanged();
			} );
		}
	}

	/// <summary>Hands the prompt to the running turn the other way than Enter does: steering when Enter queues, queued when it steers; sends normally while idle.</summary>
	internal void UpdateSteer()
	{
		if ( _isBusy )
			UpdateInterject( steer: !_isSteerMode );
		else
			UpdateSend();
	}

	/// <summary>Hands the prompt to the running turn: queued for after it, or steering it now; the prompt empties at once, the message shows in the queue, and both come back if Omp refuses it.</summary>
	private void UpdateInterject( bool steer )
	{
		var text = _composer.Document.GetText();
		if ( _session is null || (string.IsNullOrWhiteSpace( text ) && _attachments.Count == 0) )
			return;

		var attachments = _attachments.ToArray();
		_composer.Document.Apply( new TextChange( new TextRange( 0, text.Length ), "" ) );
		_attachments.Clear();
		store.SaveDraft( conversationId, "" );
		var message = ComposePrompt( text, attachments );
		UpdateQueueSent( message, steer );
		MarkChanged();
		_ = UpdateInterjectAsync( EnsureConnection(), text, attachments, message, steer );
	}

	/// <summary>Sends a queued or steering message without accessing UI state from a background continuation.</summary>
	private async Task UpdateInterjectAsync( Task<OmpSession> connection, string text, string[] attachments, string message, bool steer )
	{
		try
		{
			var session = await connection;
			await session.RequestAsync( steer ? "steer" : "follow_up", new
			{
				message
			}, context.Lifetime );
		}
		catch ( Exception error )
		{
			context.Post( () =>
			{
				// Omp did not take it, so it leaves the queue and the text and attachments return to an empty prompt.
				if ( steer )
					_queuedSteering = [.. _queuedSteering.Where( queued => queued != message )];
				else
					_queuedFollowUps = [.. _queuedFollowUps.Where( queued => queued != message )];
				if ( _composer.Document.Length == 0 )
					_composer.Document.Apply( new TextChange( new TextRange( 0, 0 ), text ) );
				_attachments.AddRange( attachments.Where( path => !_attachments.Contains( path ) ) );
				store.SaveDraft( conversationId, _composer.Document.GetText() );
				_status = error is OperationCanceledException ? "Stopped" : DescribeFailure( error );
				MarkChanged();
			} );
		}
	}

	/// <summary>Sends a prompt without accessing UI state from a background continuation.</summary>
	private async Task UpdateSendAsync( Task<OmpSession> connection, string text, string[] attachments )
	{
		try
		{
			var session = await connection;
			var admission = await session.RequestAsync( "prompt", new
			{
				message = ComposePrompt( text, attachments )
			}, context.Lifetime );
			context.Post( () =>
			{
				if ( ReadText( admission, "agentInvoked" ) is not ("False" or "false") )
					_sentCommand = null;
				else if ( !_isCompacting )
				{
					_isBusy = false;
					_status = "Ready";
				}
				if ( _composer.Document.GetText() == text )
					_composer.Document.Apply( new TextChange( new TextRange( 0, text.Length ), "" ) );
				_attachments.RemoveAll( attachments.Contains );
				store.SaveDraft( conversationId, _composer.Document.GetText() );
				MarkChanged();
			} );
		}
		catch ( Exception error )
		{
			context.Post( () =>
			{
				// The backend never accepted this prompt, so keep it and its attachments with the conversation.
				_sentCommand = null;
				store.SaveDraft( conversationId, _composer.Document.GetText() );
				_isBusy = false;
				_status = error is OperationCanceledException ? "Stopped" : DescribeFailure( error );
				MarkChanged();
			} );
		}
	}

	/// <summary>Explains a backend failure, pointing at setup when the account profile has no usable model.</summary>
	private string DescribeFailure( Exception error )
	{
		if ( !error.Message.Contains( MissingModelDiagnostic, StringComparison.OrdinalIgnoreCase ) )
			return error.Message;

		var profile = store.Get( conversationId ).Profile;
		return $"{profile} has no signed-in Omp account or model yet ({MissingModelDiagnostic}). Your message was not sent and stays in the box. Choose the model button above to open Omp setup, then send again.";
	}

	/// <summary>Requests cancellation of the running turn without discarding its recorded messages.</summary>
	private void UpdateStop()
	{
		if ( UpdateTaskCheckStop() || _session is not { } session )
			return;

		_status = "Stopping…";
		MarkChanged();
		_ = UpdateRequestAsync( session, "abort" );
	}

	/// <summary>Contains failures from explicit backend control actions.</summary>
	private async Task UpdateRequestAsync( OmpSession session, string type )
	{
		try
		{
			await session.RequestAsync( type, cancellationToken: context.Lifetime );
		}
		catch ( Exception error )
		{
			context.Post( () =>
			{
				_status = error.Message;
				MarkChanged();
			} );
		}
	}

	/// <summary>Marshals every background RPC notification onto the panel's guarded UI lifetime.</summary>
	private void MarkFrameReceived( JsonElement frame ) => context.Post( () => UpdateFrame( frame ) );

	/// <summary>Applies full streaming snapshots and live tool state using stable backend identifiers.</summary>
	internal void UpdateFrame( JsonElement frame )
	{
		var wasBusy = _isBusy;
		var dialogCount = PendingDialogCount;
		var type = ReadText( frame, "type" );
		if ( type is "message_start" or "message_update" or "message_end" )
		{
			if ( !frame.TryGetProperty( "message", out var message ) )
				return;

			var key = ReadText( frame, "messageId" );
			if ( key.Length == 0 )
				return;

			var exists = _messageIds.TryGetValue( key, out var id );
			if ( !exists )
				_messageIds.Add( key, id = Guid.NewGuid() );
			var stored = BuildStoredMessage( id, message );
			if ( exists )
				store.UpdateMessage( conversationId, stored, persist: type == "message_end" );
			else
				store.AppendMessage( conversationId, stored, persist: type == "message_end" );
			if ( type == "message_end" )
				UpdateShellFolder( message );

			var conversation = store.Get( conversationId );
			if ( stored.Role == "user" && conversation.Title == "New chat" && stored.Text.Length > 0 )
			{
				// The first line stands in at once; a model's summary replaces it after the turn.
				var title = stored.Text.Split( '\n' )[0];
				store.SetAutomaticTitle( conversationId, title[..Math.Min( title.Length, TitleLimit )], isGenerated: false );
			}
		}
		else if ( type.StartsWith( "tool_execution_", StringComparison.Ordinal ) || type == "tool_stream_update" )
		{
			var id = ReadText( frame, "toolCallId" );
			// A late stream or update frame, such as buffered output flushed after the end, must not reopen a finished tool.
			if ( id.Length > 0 && !(type != "tool_execution_end" && _tools.TryGetValue( id, out var known ) && ReadText( known, "type" ) == "tool_execution_end") )
			{
				_tools[id] = frame.Clone();
				UpdateIntent( frame );
				if ( _toolOwners.TryGetValue( id, out var owner ) )
					_revisions[owner] = _revisions.GetValueOrDefault( owner ) + 1;
			}
		}
		else if ( type == "extension_ui_request" )
			UpdateDialog( frame );
		else if ( type == "queue_update" )
			UpdateQueue( frame );
		else if ( type == "available_commands_update" )
			_commands = ReadCommands( frame );
		else if ( type == "command_output" )
			UpdateCommandOutput( frame );
		else if ( type == "agent_start" )
		{
			_sentCommand = null;
			_shellFolders.Clear();
			_isBusy = true;
			_status = "Working…";
		}
		else if ( type == "session_settled" || type == "agent_end" && ReadText( frame, "isTerminal", "true" ) is not ("False" or "false") )
		{
			_isBusy = false;
			_status = "Ready";
			ClearQueue();
			SettleTools();
			store.Save();
			UpdateTitleSummary();
			UpdateFollowShell();
			UpdateContextRequest();
		}
		else if ( type is "turn_end" or "thinking_level_changed" )
			UpdateContextRequest();
		else if ( type == "model_changed" && _session is { } changed )
		{
			UpdateContextRequest();
			_ = UpdateEffortsAsync( changed );
		}
		else if ( type == "manual_compaction_start" )
		{
			_isCompacting = true;
			_isBusy = true;
			_status = "Compacting…";
		}
		else if ( type == "manual_compaction_end" )
		{
			_isCompacting = false;
			_isBusy = false;
			_status = ReadText( frame, "aborted" ) is "True" or "true" ? "Stopped" : ReadText( frame, "errorMessage", "Compacted" );
			UpdateCompactionTitle( frame );
			UpdateContextRequest();
		}
		else if ( type.EndsWith( "compaction_start", StringComparison.Ordinal ) )
			_isAutoCompacting = true;
		else if ( type.EndsWith( "compaction_end", StringComparison.Ordinal ) )
		{
			_isAutoCompacting = false;
			UpdateCompactionTitle( frame );
			UpdateContextRequest();
		}
		else if ( type == "session_info_update" && ReadText( frame, "title" ) is { Length: > 0 } summary )
			UpdateSessionTitle( summary[..Math.Min( summary.Length, TitleLimit )] );
		else if ( type is "extension_error" or "notice" )
		{
			_status = ReadText( frame, "message", ReadText( frame, "error", type.Replace( '_', ' ' ) ) );
			if ( ReadText( frame, "source" ) == "omp-transport" && ReadText( frame, "level" ) == "error" )
			{
				if ( _session is { } failed )
				{
					failed.FrameReceived -= MarkFrameReceived;
					_ = failed.StopAsync();
				}
				_session = null;
				UpdateToolBridge( null );
				_connection = null;
				_isBusy = false;
				_dialogs.Clear();
				ClearQueue();
			}
		}
		else if ( type == "prompt_result" && (ReadText( frame, "status" ) is "error" or "aborted" || ReadText( frame, "sessionSettled" ) is "True" or "true") )
		{
			_isBusy = false;
			_status = ReadText( frame, "status" ) == "completed" ? "Ready" : ReadText( frame, "error", "Stopped" );
		}

		UpdateAttention( wasBusy, dialogCount );
		MarkChanged();
	}

	/// <summary>Defines the isolated question that names a chat from bounded user prompts.</summary>
	internal const string TitleQuestion = "Reply with only a title for this conversation as it stands now: at most 4 plain words, no emoji, no category prefix, no quotes, no trailing punctuation.";

	/// <summary>Asks once per chat, after a turn, for a short title unless the user named it or one was already generated.</summary>
	/// <remarks>The small-model call receives at most the first and latest user prompts, never the session context, tools or project rules.</remarks>
	private void UpdateTitleSummary()
	{
		var conversation = store.Get( conversationId );
		if ( _isTitleRequested || conversation.IsTitleChosen || conversation.IsTitleGenerated
			|| !store.GetMessages( conversationId ).Any( message => message.Role == "user" ) )
			return;

		_isTitleRequested = true;
		_ = UpdateTitleRequestAsync();
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
		_ = UpdateTitleRequestAsync();
	}

	/// <summary>Asks Omp's small-model role for a title from bounded user prompts; a failure keeps the current title.</summary>
	private async Task UpdateTitleRequestAsync()
	{
		try
		{
			var conversation = store.Get( conversationId );
			var material = BuildTitleMaterial( store.GetMessages( conversationId ) );
			_auxiliaryCallCount++;
			MarkChanged();
			var answer = await backend.AskSmallAsync( conversation.Profile, TitleQuestion, material, context.Lifetime ).ConfigureAwait( false );
			context.Post( () => UpdateTitleFromAnswer( answer ) );
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			// The title is a convenience: the chat keeps its current name.
		}
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

	/// <summary>Asks for the user when a question arrives or a turn ends, and withdraws once the agent works on alone.</summary>
	/// <remarks>Counts only questions still waiting: one answered or cancelled is leaving while its exit plays, and asks nothing.</remarks>
	private void UpdateAttention( bool wasBusy, int dialogCount )
	{
		var title = store.Get( conversationId ).Title;
		if ( PendingDialogCount > dialogCount )
			context.RequestAttention( title, ReadText( _dialogs[^1], "title", "Your input is needed" ) );
		else if ( wasBusy && !_isBusy )
			context.RequestAttention( title, _status == "Ready" ? "Finished and waiting for you" : _status );
		else if ( _isBusy && PendingDialogCount == 0 )
			context.ClearAttention();
	}

	/// <summary>Converts an authoritative Omp message into durable local rich history.</summary>
	internal static ConversationMessage BuildStoredMessage( Guid id, JsonElement message )
	{
		var createdAt = message.TryGetProperty( "timestamp", out var timestamp ) && timestamp.TryGetInt64( out var milliseconds )
			? DateTimeOffset.FromUnixTimeMilliseconds( milliseconds ) : DateTimeOffset.UtcNow;
		return new( id, ReadText( message, "role", "assistant" ), ComputeMessageText( message ), createdAt, message.Clone() );
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
