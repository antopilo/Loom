using System.Text.Json;
using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;

namespace Lokn.Modules.Chat;

/// <summary>Shows a chat whose agent runs in a host, such as Loom's daemon: reads the host's last pushed state, and sends what is asked of it through an <see cref="IChatChannel"/>.</summary>
/// <remarks>
/// <para>
/// The host's pushes for the chat arrive on the UI thread through <see cref="ApplyAttach"/>, <see cref="ApplyState"/>, <see cref="ApplyTool"/>,
/// <see cref="ApplyEvent"/>, <see cref="ApplyMessage"/> and <see cref="ApplyMessages"/>; each carries the chat's sequence number, and one
/// not above the last applied is stale and ignored. Messages go to the mirror <see cref="ConversationStore"/> the proxy shows.
/// </para>
/// <para>
/// Calls return before the host answers. Those that answer at once are decided from the last state: <see cref="Interject"/> hands over
/// what a connected session would take, and a refusal from the host comes back through <see cref="InterjectRefused"/>;
/// <see cref="UpdateDirectory"/> refuses as the host would and otherwise sends the folder; <see cref="ComputeFolderRefusal"/>,
/// <see cref="ComputeTaskCheckDue"/> and <see cref="ReadSelection"/> read the state and the mirror. A call the channel fails shows in
/// <see cref="Status"/> until the next state. <see cref="Attach"/> and <see cref="Detach"/> are sent as calls too, which the channel may
/// carry as <c>attach</c> and <c>detach</c>. The Omp process stays in the host: <see cref="Connected"/> is null and
/// <see cref="EnsureConnection"/> is not supported.
/// </para>
/// </remarks>
/// <param name="app">The application whose settings decide the new-task check, as for a local session.</param>
/// <param name="store">The mirror history the chat's messages are applied to.</param>
/// <param name="conversationId">The conversation the host runs.</param>
/// <param name="backend">This process's Omp service, for model names and provider usage.</param>
/// <param name="channel">Carries calls to the host.</param>
/// <param name="post">Posts an action to the UI thread and reports whether it was accepted.</param>
public sealed class RemoteChatSession( LoknApplication app, ConversationStore store, Guid conversationId, OmpService backend, IChatChannel channel, Func<Action, bool> post ) : IChatSession
{
	/// <summary>Ends waiting for the host's answers when the proxy is disposed.</summary>
	private readonly CancellationTokenSource _lifetime = new();

	/// <summary>Retains live tool execution frames by tool-call identifier, as the host pushed them.</summary>
	private readonly Dictionary<string, JsonElement> _tools = new( StringComparer.Ordinal );

	/// <summary>Stores the intent each started tool call gave, by tool-call identifier.</summary>
	private readonly Dictionary<string, string> _intents = new( StringComparer.Ordinal );

	/// <summary>Stores the host's last state.</summary>
	private ChatSessionState _state = new();

	/// <summary>Stores why the last call failed, shown as the status until the next state, or null.</summary>
	private string? _failure;

	/// <summary>Stores the sequence number of the last push applied.</summary>
	private long _seq;

	/// <summary>Reports whether the proxy was disposed.</summary>
	private bool _isDisposed;

	/// <summary>Reports whether a prompt was sent and the host has not pushed a state since, so the view shows it working at once, as a local session does.</summary>
	private bool _isSending;

	/// <summary>Raised on the UI thread after a state, tool or event push was applied, or a call failed, so a view rebuilds.</summary>
	public event Action? Changed;

	/// <summary>Raised on the UI thread when the host's session needs the user, with the conversation's title and what it needs.</summary>
	public event Action<string, string>? AttentionRequested;

	/// <summary>Raised on the UI thread when the host's session works on alone and withdraws its request for the user.</summary>
	public event Action? AttentionCleared;

	/// <summary>Raised on the UI thread when Omp admitted a prompt, with its text and attachments, so a view can empty its composer of them.</summary>
	public event Action<string, string[]>? PromptAdmitted;

	/// <summary>Raised on the UI thread when a sent prompt never reached Omp, which stays in the view's composer.</summary>
	public event Action? PromptRefused;

	/// <summary>Raised on the UI thread when a queued or steering message was refused, with its text and attachments, so a view can return them to an empty composer.</summary>
	public event Action<string, string[]>? InterjectRefused;

	/// <summary>Raised on the UI thread when a message taken back from Omp's queue should return to an empty composer.</summary>
	public event Action<string>? UnqueueRestored;

	/// <summary>Raised on the UI thread when an Omp extension replaces the composer's text.</summary>
	public event Action<string>? EditorTextRequested;

	/// <summary>Raised on the UI thread when a tool call's live state changed, by its tool-call identifier, so a view rebuilds the row that holds it.</summary>
	public event Action<string>? ToolChanged;

	/// <summary>Raised on the UI thread when a question stopped being pending, with the question itself.</summary>
	public event Action<JsonElement>? DialogLeft;

	/// <summary>Raised on the UI thread when an answer to a question reached Omp, so a view marks its panel read.</summary>
	public event Action? DialogAnswered;

	/// <summary>Raised on the UI thread when the pending questions or their selections changed.</summary>
	public event Action? DialogsChanged;

	/// <summary>Raised on the UI thread when the host's session starts a new process, whose questions and messages start afresh.</summary>
	public event Action? Connecting;

	/// <summary>Raised on the UI thread after Omp started a fresh session and the transcript was emptied.</summary>
	public event Action? Cleared;

	/// <summary>Raised on the UI thread after a local command's output joined the transcript.</summary>
	public event Action? OutputAppended;

	/// <summary>Gets the sequence number of the last push applied, which a reattach passes as <c>since</c>.</summary>
	public long Seq => _seq;

	/// <summary>Gets the conversation the host runs.</summary>
	public Guid ConversationId => conversationId;

	/// <summary>Gets this process's Omp service, for model names, the model catalog and provider usage.</summary>
	public OmpService Backend => backend;

	/// <summary>Gets true: the agent runs in the host.</summary>
	public bool IsRemote => true;

	/// <summary>Gets whether the host's agent is connecting, working or waiting on tools, or a prompt was just sent to it.</summary>
	public bool IsBusy => _isSending || _state.IsBusy;

	/// <summary>Gets whether the host's agent ended its run and the session stays busy only until background work, such as jobs or subagents, settles.</summary>
	public bool IsSettling => _state.IsSettling;

	/// <summary>Gets the host's status, or why the last call did not reach it until the next state arrives, or that a prompt is on its way.</summary>
	public string Status => _failure ?? (_isSending ? "Sending…" : _state.Status);

	/// <summary>Gets null: the Omp process runs in the host.</summary>
	public OmpSession? Connected => null;

	/// <summary>Gets whether the host's Omp process is connected.</summary>
	public bool IsConnected => _state.IsConnected;

	/// <summary>Gets whether the host started a connection, finished or not.</summary>
	public bool IsConnectionStarted => _state.IsConnectionStarted;

	/// <summary>Gets the model the host's backend reported, as <c>provider/id</c>, or empty.</summary>
	public string ConnectedModel => _state.ConnectedModel;

	/// <summary>Gets the connected model's catalog limits and prices, or null.</summary>
	public OmpModelInfo? ModelInfo => _state.ModelInfo;

	/// <summary>Gets how many compactions ran while the host's session ran.</summary>
	public int CompactionCount => _state.CompactionCount;

	/// <summary>Gets how many isolated model calls the host started for this chat.</summary>
	public int AuxiliaryCallCount => _state.AuxiliaryCallCount;

	/// <summary>Gets live tool execution frames by tool-call identifier.</summary>
	public IReadOnlyDictionary<string, JsonElement> Tools => _tools;

	/// <summary>Gets the intent each started tool call gave, by tool-call identifier.</summary>
	public IReadOnlyDictionary<string, string> Intents => _intents;

	/// <summary>Gets the commands the host's Omp offers.</summary>
	public SlashCommand[] Commands => _state.Commands;

	/// <summary>Gets whether a <c>/compact</c> the user ran is still summarizing the session.</summary>
	public bool IsCompacting => _state.IsCompacting;

	/// <summary>Gets the context's tokens, or null when Omp has not measured them yet.</summary>
	public long? ContextTokens => _state.ContextTokens;

	/// <summary>Gets the model's context window in tokens, or zero when unknown.</summary>
	public long ContextWindow => _state.ContextWindow;

	/// <summary>Gets whether the session compacts automatically.</summary>
	public bool IsAutoCompaction => _state.IsAutoCompaction;

	/// <summary>Gets the profile's compaction settings once the host read them, or null.</summary>
	public OmpCompaction? Compaction => _state.Compaction;

	/// <summary>Gets whether an automatic compaction runs now.</summary>
	public bool IsAutoCompacting => _state.IsAutoCompacting;

	/// <summary>Gets the efforts the live model supports, <c>off</c> first.</summary>
	public string[] Efforts => _state.Efforts;

	/// <summary>Gets the session's effort, or empty when unknown.</summary>
	public string Effort => _state.Effort;

	/// <summary>Gets the questions waiting for an answer, in wire order.</summary>
	public IReadOnlyList<JsonElement> Dialogs => _state.Dialogs;

	/// <summary>Gets the first question still waiting for an answer, or null when none is.</summary>
	public JsonElement? PendingDialog => _state.Dialogs.Length > 0 ? _state.Dialogs[0] : null;

	/// <summary>Gets the steering messages Omp holds, oldest first.</summary>
	public string[] QueuedSteering => _state.QueuedSteering;

	/// <summary>Gets the follow-up messages Omp holds, oldest first.</summary>
	public string[] QueuedFollowUps => _state.QueuedFollowUps;

	/// <summary>Gets how many leading steering messages the model already received.</summary>
	public int LiveSteered => _state.LiveSteered;

	/// <summary>Gets the chat's goal as the host's Omp reports it, or null without one.</summary>
	public JsonElement? Goal => _state.Goal;

	/// <summary>Gets the host's last state.</summary>
	public ChatSessionState CaptureState() => _state;

	/// <summary>Applies the answer to <c>attach</c>: the sequence number to follow from, the state, live tools and intents, and the messages when the host sent them.</summary>
	/// <remarks>
	/// The answer replaces whatever the proxy showed before, such as before a reconnect or a host restart, whose sequence numbers start over: tool rows
	/// whose frame changed or went away hear <see cref="ToolChanged"/>, and changed questions or selections raise <see cref="DialogsChanged"/>, as the
	/// pushes the proxy missed would have.
	/// </remarks>
	public void ApplyAttach( long seq, ChatSessionState state, IReadOnlyDictionary<string, JsonElement> tools, IReadOnlyDictionary<string, string> intents, IReadOnlyList<ConversationMessage>? messages )
	{
		if ( _isDisposed )
			return;

		_seq = seq;
		var changedTools = _tools.Keys.Where( id => !tools.ContainsKey( id ) ).ToList();
		foreach ( var (id, frame) in tools )
		{
			if ( !_tools.TryGetValue( id, out var known ) || known.GetRawText() != frame.GetRawText() )
				changedTools.Add( id );
		}
		var isDialogsChanged = !IsSameJson( _state.Dialogs, state.Dialogs ) || !IsSameJson( _state.Selections, state.Selections );
		_tools.Clear();
		foreach ( var (id, frame) in tools )
			_tools[id] = frame.Clone();
		_intents.Clear();
		foreach ( var (id, intent) in intents )
			_intents[id] = intent;
		if ( messages is not null )
			store.ApplyMessages( conversationId, messages );
		UpdateState( state );
		foreach ( var id in changedTools )
			ToolChanged?.Invoke( id );
		if ( isDialogsChanged )
			DialogsChanged?.Invoke();
	}

	/// <summary>Applies a <c>state</c> push.</summary>
	public void ApplyState( long seq, ChatSessionState state )
	{
		if ( IsNext( seq ) )
			UpdateState( state );
	}

	/// <summary>Applies a <c>tool</c> push: a tool call's latest frame, or JSON null once the host forgot it.</summary>
	public void ApplyTool( long seq, string id, JsonElement frame )
	{
		if ( !IsNext( seq ) )
			return;

		if ( frame.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined )
			_tools.Remove( id );
		else
		{
			_tools[id] = frame.Clone();
			// Only the starting frame carries the intent, as the host's session remembers it.
			if ( ReadText( frame, "type" ) == "tool_execution_start" && ReadText( frame, "intent" ) is { Length: > 0 } intent )
				_intents[id] = intent;
		}
		ToolChanged?.Invoke( id );
		Changed?.Invoke();
	}

	/// <summary>Applies an <c>evt</c> push, raising the event the host's session raised; an event this version does not know is ignored.</summary>
	public void ApplyEvent( long seq, ChatSessionEvent sessionEvent )
	{
		if ( !IsNext( seq ) )
			return;

		switch ( sessionEvent.Name )
		{
			case ChatSessionEvent.AttentionRequested:
				AttentionRequested?.Invoke( sessionEvent.Read<string>( 0 ), sessionEvent.Read<string>( 1 ) );
				break;
			case ChatSessionEvent.AttentionCleared:
				AttentionCleared?.Invoke();
				break;
			case ChatSessionEvent.PromptAdmitted:
				PromptAdmitted?.Invoke( sessionEvent.Read<string>( 0 ), sessionEvent.Read<string[]>( 1 ) );
				break;
			case ChatSessionEvent.PromptRefused:
				PromptRefused?.Invoke();
				break;
			case ChatSessionEvent.InterjectRefused:
				InterjectRefused?.Invoke( sessionEvent.Read<string>( 0 ), sessionEvent.Read<string[]>( 1 ) );
				break;
			case ChatSessionEvent.UnqueueRestored:
				UnqueueRestored?.Invoke( sessionEvent.Read<string>( 0 ) );
				break;
			case ChatSessionEvent.EditorTextRequested:
				EditorTextRequested?.Invoke( sessionEvent.Read<string>( 0 ) );
				break;
			case ChatSessionEvent.DialogLeft:
				DialogLeft?.Invoke( sessionEvent.Read<JsonElement>( 0 ).Clone() );
				break;
			case ChatSessionEvent.DialogAnswered:
				DialogAnswered?.Invoke();
				break;
			case ChatSessionEvent.DialogsChanged:
				DialogsChanged?.Invoke();
				break;
			case ChatSessionEvent.Connecting:
				// The host's session forgets live tools when it starts a new process, without a tool push for each.
				_tools.Clear();
				Connecting?.Invoke();
				break;
			case ChatSessionEvent.Cleared:
				_tools.Clear();
				_intents.Clear();
				Cleared?.Invoke();
				break;
			case ChatSessionEvent.OutputAppended:
				OutputAppended?.Invoke();
				break;
			default:
				// A newer host may raise events this version has no view for.
				return;
		}
		Changed?.Invoke();
	}

	/// <summary>Applies a <c>msg</c> push to the mirror history.</summary>
	public void ApplyMessage( long seq, ConversationMessage message, bool persist )
	{
		if ( IsNext( seq ) )
			store.ApplyMessage( conversationId, message, persist );
	}

	/// <summary>Applies a <c>msgs</c> push, which replaces every message, to the mirror history.</summary>
	public void ApplyMessages( long seq, IReadOnlyList<ConversationMessage> messages )
	{
		if ( IsNext( seq ) )
			store.ApplyMessages( conversationId, messages );
	}

	/// <summary>Sends that a panel shows the chat, so the host's tools see it as their caller.</summary>
	public void Attach( Guid panelId ) => Call( ChatSessionCall.Attach, panelId );

	/// <summary>Sends that a panel no longer shows the chat.</summary>
	public void Detach( Guid panelId ) => Call( ChatSessionCall.Detach, panelId );

	/// <summary>Sends a status line message, such as a refusal the view decided on.</summary>
	public void UpdateStatus( string status ) => Call( ChatSessionCall.UpdateStatus, status );

	/// <summary>Asks the host to start the chat's Omp process without sending a prompt.</summary>
	public void UpdateConnect() => Call( ChatSessionCall.UpdateConnect );

	/// <summary>Not supported: the chat's Omp process runs in the host.</summary>
	/// <exception cref="NotSupportedException">Always.</exception>
	public Task<OmpSession> EnsureConnection() => throw new NotSupportedException( "The chat's Omp process runs in the host, not in this window." );

	/// <summary>Sends a prompt to the host and shows the chat working until the host's next state, as a local session does; when it does not reach the
	/// host, <see cref="PromptRefused"/> keeps it in the composer.</summary>
	public void Send( string text, string[] attachments )
	{
		if ( _isDisposed )
			return;

		_isSending = true;
		_failure = null;
		Changed?.Invoke();
		_ = UpdateSendAsync( text, attachments );
	}

	/// <summary>Sends a prompt and reports a failed call as a refused prompt, no longer working.</summary>
	private async Task UpdateSendAsync( string text, string[] attachments )
	{
		if ( await CallAsync( ChatSessionCall.Send, text, attachments ).ConfigureAwait( false ) is not null )
			return;

		Post( () =>
		{
			_isSending = false;
			PromptRefused?.Invoke();
			Changed?.Invoke();
		} );
	}

	/// <summary>Asks the host's agent to finish the turn a module reload stopped.</summary>
	public void Continue() => Call( ChatSessionCall.Continue );

	/// <summary>Asks the host to empty the conversation in place.</summary>
	public void Clear() => Call( ChatSessionCall.Clear );

	/// <summary>Hands a message to the host's running turn when its session is connected; when the host refuses it, <see cref="InterjectRefused"/> returns it.</summary>
	/// <returns>Whether the message was handed over; false while the host's session is not connected or with nothing to send.</returns>
	public bool Interject( string text, string[] attachments, bool steer )
	{
		if ( !_state.IsConnected || (string.IsNullOrWhiteSpace( text ) && attachments.Length == 0) )
			return false;

		_ = UpdateInterjectAsync( text, attachments, steer );
		return true;
	}

	/// <summary>Sends a queued or steering message, returning it to the composer when the host did not take it.</summary>
	private async Task UpdateInterjectAsync( string text, string[] attachments, bool steer )
	{
		if ( await CallAsync( ChatSessionCall.Interject, text, attachments, steer ).ConfigureAwait( false ) is not { ValueKind: JsonValueKind.True } )
			Post( () => InterjectRefused?.Invoke( text, attachments ) );
	}

	/// <summary>Sends a tool card's message to the host's agent.</summary>
	public void UpdateCardPrompt( string message ) => Call( ChatSessionCall.UpdateCardPrompt, message );

	/// <summary>Asks the host to cancel the running turn.</summary>
	public void Stop() => Call( ChatSessionCall.Stop );

	/// <summary>Asks the host to step the session to the next effort the model supports.</summary>
	public void UpdateEffort() => Call( ChatSessionCall.UpdateEffort );

	/// <summary>Asks the host to switch the chat's session to a model.</summary>
	public void UpdateModel( OmpModelInfo model ) => Call( ChatSessionCall.UpdateModel, model );

	/// <summary>Asks the host to dismiss a question that takes no answer.</summary>
	public void DismissDialog( string id ) => Call( ChatSessionCall.DismissDialog, id );

	/// <summary>Sends one answer to a pending question as the JSON Omp reads, with its own property names.</summary>
	public void AnswerDialog( string id, object response ) =>
		Call( ChatSessionCall.AnswerDialog, id, response as JsonElement? ?? JsonSerializer.SerializeToElement( response, response.GetType() ) );

	/// <summary>Gets the labels selected for one questionnaire question in the host's last state; none before the first pick.</summary>
	public IReadOnlySet<string> ReadSelection( string key ) =>
		_state.Selections.TryGetValue( key, out var labels ) ? new HashSet<string>( labels, StringComparer.Ordinal ) : new HashSet<string>();

	/// <summary>Picks or unpicks one option of a questionnaire question in the host's session.</summary>
	public void UpdateSelection( string key, string label, bool isMulti ) => Call( ChatSessionCall.UpdateSelection, key, label, isMulti );

	/// <summary>Releases a finished question's selections in the host's session.</summary>
	public void ReleaseSelections( string id ) => Call( ChatSessionCall.ReleaseSelections, id );

	/// <summary>Asks the host to move a waiting follow-up into the steering queue.</summary>
	public void UpdatePromote( string text ) => Call( ChatSessionCall.UpdatePromote, text );

	/// <summary>Asks the host to take a waiting message back, which returns through <see cref="UnqueueRestored"/>.</summary>
	public void UpdateUnqueue( string text, bool steering ) => Call( ChatSessionCall.UpdateUnqueue, text, steering );

	/// <summary>Sends a chat outside projects to another folder unless the host's last state refuses it, which shows why.</summary>
	/// <returns>Whether the folder was sent to the host.</returns>
	public bool UpdateDirectory( string directory )
	{
		if ( _isDisposed )
			return false;

		if ( ComputeFolderRefusal( store.Get( conversationId ) ) is { } refusal )
		{
			UpdateStatus( refusal );
			return false;
		}

		Call( ChatSessionCall.UpdateDirectory, directory );
		return true;
	}

	/// <summary>Gets why the chat cannot change its folder now, from the host's last state and a prompt on its way, or null when it can.</summary>
	public string? ComputeFolderRefusal( ConversationInfo conversation ) => ChatSession.ComputeFolderRefusal( conversation, IsBusy, _state.IsConnecting );

	/// <summary>Gets whether a prompt is checked first for starting a new task, from this window's settings, the mirror's messages and the host's context share.</summary>
	public bool ComputeTaskCheckDue( string text ) =>
		ChatSession.ComputeTaskCheckDue( app, store.Get( conversationId ), store.GetMessages( conversationId ), text, () => _state.ContextShare );

	/// <summary>Asks the host's session whether a prompt starts a new task; a failed call, or a disposed proxy, counts as the same task.</summary>
	public async Task<bool> CheckNewTaskAsync( string text, CancellationToken cancellationToken )
	{
		if ( _isDisposed )
			return false;

		using var cancel = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken, _lifetime.Token );
		try
		{
			var answer = await channel.CallAsync( ChatSessionCall.CheckNewTask, ChatSessionCall.BuildArgs( text ), cancel.Token ).ConfigureAwait( false );
			return answer.ValueKind == JsonValueKind.True;
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			// The check is a convenience: without an answer the prompt goes to this chat.
			return false;
		}
	}

	/// <summary>Stops waiting for the host's answers; the chat runs on in the host.</summary>
	public void Dispose()
	{
		if ( _isDisposed )
			return;

		_isDisposed = true;
		_lifetime.Cancel();
		_lifetime.Dispose();
	}

	/// <summary>Takes a push's sequence number when it follows the last applied one.</summary>
	private bool IsNext( long seq )
	{
		if ( _isDisposed || seq <= _seq )
			return false;

		_seq = seq;
		return true;
	}

	/// <summary>Takes the host's state, which replaces a failed call's status and a sent prompt's, follows it in the sidebar's working set, and tells the views.</summary>
	private void UpdateState( ChatSessionState state )
	{
		_state = state;
		_failure = null;
		_isSending = false;
		store.SetWorking( conversationId, state.IsBusy );
		Changed?.Invoke();
	}

	/// <summary>Reports whether two values serialize to the same protocol JSON, such as two states' questions.</summary>
	private static bool IsSameJson<T>( T left, T right ) =>
		JsonSerializer.Serialize( left, StoreOp.JsonOptions ) == JsonSerializer.Serialize( right, StoreOp.JsonOptions );

	/// <summary>Sends a call without waiting for its answer.</summary>
	private void Call( string method, params object?[] args ) => _ = CallAsync( method, args );

	/// <summary>Sends a call; a failure shows in the status until the next state.</summary>
	/// <returns>The answer, or null when the call failed.</returns>
	private async Task<JsonElement?> CallAsync( string method, params object?[] args )
	{
		if ( _isDisposed )
			return null;

		try
		{
			return await channel.CallAsync( method, ChatSessionCall.BuildArgs( args ), _lifetime.Token ).ConfigureAwait( false );
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			Post( () =>
			{
				_failure = error is OperationCanceledException ? "Disconnected from the host" : "The host did not take it: " + error.Message;
				Changed?.Invoke();
			} );
			return null;
		}
	}

	/// <summary>Posts work from a background continuation to the UI thread while the proxy lives.</summary>
	private bool Post( Action action ) => !_isDisposed && post( () =>
	{
		if ( !_isDisposed )
			action();
	} );

	/// <summary>Reads a string property of a JSON object, or empty.</summary>
	private static string ReadText( JsonElement element, string name ) => ChatContent.ReadText( element, name );
}
