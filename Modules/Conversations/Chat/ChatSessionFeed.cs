using System.Text.Json;
using Lokn.Modules.Projects;

namespace Lokn.Modules.Chat;

/// <summary>Follows a host's chat session and hands what other processes need to show it, in order: new states, tool frames and events.</summary>
/// <remarks>
/// The Remote module's gateway turns them into <c>state</c>, <c>tool</c> and <c>evt</c> pushes, which a <see cref="RemoteChatSession"/>
/// applies. A state is handed only when it differs from the last one, and before each tool frame or event whenever it changed, so a proxy
/// raising the event reads the state the session had when it raised it. Create and dispose it on the UI thread.
/// </remarks>
public sealed class ChatSessionFeed : IDisposable
{
	/// <summary>Stores the session followed.</summary>
	private readonly IChatSession _session;

	/// <summary>Receives each new state.</summary>
	private readonly Action<ChatSessionState> _state;

	/// <summary>Receives a tool call's identifier and latest frame, or <see cref="Removed"/> once the session forgot it.</summary>
	private readonly Action<string, JsonElement> _tool;

	/// <summary>Receives each event.</summary>
	private readonly Action<ChatSessionEvent> _event;

	/// <summary>Stores the last state handed, serialized, to tell a new one.</summary>
	private string _sent;

	/// <summary>Follows a session, taking its current state as the one already handed, such as in an <c>attach</c> answer.</summary>
	/// <param name="session">The host's session.</param>
	/// <param name="state">Receives each new state.</param>
	/// <param name="tool">Receives a tool call's identifier and latest frame, or <see cref="Removed"/> once the session forgot it.</param>
	/// <param name="sessionEvent">Receives each event.</param>
	public ChatSessionFeed( IChatSession session, Action<ChatSessionState> state, Action<string, JsonElement> tool, Action<ChatSessionEvent> sessionEvent )
	{
		_session = session;
		_state = state;
		_tool = tool;
		_event = sessionEvent;
		State = session.CaptureState();
		_sent = JsonSerializer.Serialize( State, StoreOp.JsonOptions );
		session.Changed += UpdateState;
		session.ToolChanged += UpdateTool;
		session.AttentionRequested += UpdateAttentionRequested;
		session.AttentionCleared += UpdateAttentionCleared;
		session.PromptAdmitted += UpdatePromptAdmitted;
		session.PromptRefused += UpdatePromptRefused;
		session.InterjectRefused += UpdateInterjectRefused;
		session.UnqueueRestored += UpdateUnqueueRestored;
		session.EditorTextRequested += UpdateEditorText;
		session.DialogLeft += UpdateDialogLeft;
		session.DialogAnswered += UpdateDialogAnswered;
		session.DialogsChanged += UpdateDialogsChanged;
		session.Connecting += UpdateConnecting;
		session.Cleared += UpdateCleared;
		session.OutputAppended += UpdateOutputAppended;
	}

	/// <summary>Gets the JSON null a tool frame is handed as once the session forgot the tool call.</summary>
	public static JsonElement Removed { get; } = JsonSerializer.SerializeToElement<object?>( null );

	/// <summary>Gets the last state handed, or the one the feed started from.</summary>
	public ChatSessionState State { get; private set; }

	/// <summary>Stops following the session.</summary>
	public void Dispose()
	{
		_session.Changed -= UpdateState;
		_session.ToolChanged -= UpdateTool;
		_session.AttentionRequested -= UpdateAttentionRequested;
		_session.AttentionCleared -= UpdateAttentionCleared;
		_session.PromptAdmitted -= UpdatePromptAdmitted;
		_session.PromptRefused -= UpdatePromptRefused;
		_session.InterjectRefused -= UpdateInterjectRefused;
		_session.UnqueueRestored -= UpdateUnqueueRestored;
		_session.EditorTextRequested -= UpdateEditorText;
		_session.DialogLeft -= UpdateDialogLeft;
		_session.DialogAnswered -= UpdateDialogAnswered;
		_session.DialogsChanged -= UpdateDialogsChanged;
		_session.Connecting -= UpdateConnecting;
		_session.Cleared -= UpdateCleared;
		_session.OutputAppended -= UpdateOutputAppended;
	}

	/// <summary>Hands the session's state when it differs from the last one handed.</summary>
	private void UpdateState()
	{
		var state = _session.CaptureState();
		var serialized = JsonSerializer.Serialize( state, StoreOp.JsonOptions );
		if ( serialized == _sent )
			return;

		_sent = serialized;
		State = state;
		_state( state );
	}

	/// <summary>Hands a tool call's latest frame, after the state it changed with.</summary>
	private void UpdateTool( string id )
	{
		UpdateState();
		_tool( id, _session.Tools.TryGetValue( id, out var frame ) ? frame : Removed );
	}

	/// <summary>Hands an event, after the state the session had when it raised it.</summary>
	private void UpdateEvent( string name, params object?[] args )
	{
		UpdateState();
		_event( ChatSessionEvent.Build( name, args ) );
	}

	/// <summary>Hands <see cref="IChatSession.AttentionRequested"/>.</summary>
	private void UpdateAttentionRequested( string title, string message ) => UpdateEvent( ChatSessionEvent.AttentionRequested, title, message );

	/// <summary>Hands <see cref="IChatSession.AttentionCleared"/>.</summary>
	private void UpdateAttentionCleared() => UpdateEvent( ChatSessionEvent.AttentionCleared );

	/// <summary>Hands <see cref="IChatSession.PromptAdmitted"/>.</summary>
	private void UpdatePromptAdmitted( string text, string[] attachments ) => UpdateEvent( ChatSessionEvent.PromptAdmitted, text, attachments );

	/// <summary>Hands <see cref="IChatSession.PromptRefused"/>.</summary>
	private void UpdatePromptRefused() => UpdateEvent( ChatSessionEvent.PromptRefused );

	/// <summary>Hands <see cref="IChatSession.InterjectRefused"/>.</summary>
	private void UpdateInterjectRefused( string text, string[] attachments ) => UpdateEvent( ChatSessionEvent.InterjectRefused, text, attachments );

	/// <summary>Hands <see cref="IChatSession.UnqueueRestored"/>.</summary>
	private void UpdateUnqueueRestored( string text ) => UpdateEvent( ChatSessionEvent.UnqueueRestored, text );

	/// <summary>Hands <see cref="IChatSession.EditorTextRequested"/>.</summary>
	private void UpdateEditorText( string text ) => UpdateEvent( ChatSessionEvent.EditorTextRequested, text );

	/// <summary>Hands <see cref="IChatSession.DialogLeft"/>.</summary>
	private void UpdateDialogLeft( JsonElement dialog ) => UpdateEvent( ChatSessionEvent.DialogLeft, dialog );

	/// <summary>Hands <see cref="IChatSession.DialogAnswered"/>.</summary>
	private void UpdateDialogAnswered() => UpdateEvent( ChatSessionEvent.DialogAnswered );

	/// <summary>Hands <see cref="IChatSession.DialogsChanged"/>.</summary>
	private void UpdateDialogsChanged() => UpdateEvent( ChatSessionEvent.DialogsChanged );

	/// <summary>Hands <see cref="IChatSession.Connecting"/>.</summary>
	private void UpdateConnecting() => UpdateEvent( ChatSessionEvent.Connecting );

	/// <summary>Hands <see cref="IChatSession.Cleared"/>.</summary>
	private void UpdateCleared() => UpdateEvent( ChatSessionEvent.Cleared );

	/// <summary>Hands <see cref="IChatSession.OutputAppended"/>.</summary>
	private void UpdateOutputAppended() => UpdateEvent( ChatSessionEvent.OutputAppended );
}
