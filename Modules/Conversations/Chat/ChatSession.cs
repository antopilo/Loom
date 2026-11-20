using System.Text.Json;
using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Omp;
using static Lokn.Modules.Chat.ChatContent;

namespace Lokn.Modules.Chat;

/// <summary>Runs one conversation's agent: its profile-pinned Omp process, turns, queue, questions and titles, for as long as the Conversations module runs.</summary>
/// <remarks>
/// A chat panel shows a session and hands it what the user types; the session keeps running when no panel shows it, so a
/// turn finishes and its messages reach the store with every panel closed. Everything here runs on the UI thread, and
/// background work comes back through the post delegate. A view follows it through <see cref="Changed"/> and the composer events.
/// </remarks>
/// <param name="app">The application whose settings and tools the session uses.</param>
/// <param name="store">The history the session records the conversation in.</param>
/// <param name="conversationId">The conversation the session runs.</param>
/// <param name="backend">The Omp service that starts the session's process.</param>
/// <param name="post">Posts an action to the UI thread and reports whether it was accepted.</param>
public sealed partial class ChatSession( LoknApplication app, ConversationStore store, Guid conversationId, OmpService backend, Func<Action, bool> post ) : IChatSession
{
	/// <summary>Defines the generated conversation title limit in UTF-16 code units.</summary>
	private const int TitleLimit = 64;

	/// <summary>Defines the diagnostic Omp prints when its account profile has no model to run.</summary>
	private const string MissingModelDiagnostic = "No default model selected";

	/// <summary>Ends the session's background work when it is disposed.</summary>
	private readonly CancellationTokenSource _lifetime = new();

	/// <summary>Guards the attached panels, which the tool bridge reads on the session's reader thread.</summary>
	private readonly Lock _gate = new();

	/// <summary>Lists the panels showing this session, in the order they attached.</summary>
	private readonly List<Guid> _panels = [];

	/// <summary>Reports whether the last panel detached while a turn ran, so the process stops once the turn settles.</summary>
	private bool _isStopPending;

	/// <summary>Reports whether the session was disposed and detached from its process.</summary>
	private bool _isDisposed;

	/// <summary>Retains the single startup task until the process stops.</summary>
	private Task<OmpSession>? _connection;

	/// <summary>Counts connections started and ended, so a startup that finishes after its connection ended changes nothing.</summary>
	private int _connectionEpoch;

	/// <summary>Cancels the startup of the current connection when it ends, or null without one.</summary>
	private CancellationTokenSource? _connectionCancel;

	/// <summary>Stores the connected process after startup completes.</summary>
	private OmpSession? _session;

	/// <summary>Serves Lokn's agent tools to the connected session.</summary>
	private OmpToolBridge? _toolBridge;

	/// <summary>Reports whether a prompt is connecting, running, or waiting on tools.</summary>
	private bool _isBusy;

	/// <summary>Reports whether this session already asked Omp to summarize the chat into a title.</summary>
	private bool _isTitleRequested;

	/// <summary>Stores the visible backend status or error.</summary>
	private string _status = "Ready";

	/// <summary>Retains the model reported by the connected backend rather than guessing its default.</summary>
	private string _connectedModel = "";

	/// <summary>Retains catalog limits and prices for the connected model when Omp reports them.</summary>
	private OmpModelInfo? _modelInfo;

	/// <summary>Counts compactions observed while this session runs.</summary>
	private int _compactionCount;

	/// <summary>Counts isolated model calls Lokn starts for this chat: automatic titles and new-task checks.</summary>
	private int _auxiliaryCallCount;

	/// <summary>Maps this backend process's streaming message identifiers to local history identifiers.</summary>
	private readonly Dictionary<string, Guid> _messageIds = new( StringComparer.Ordinal );

	/// <summary>Retains live tool execution state by the backend tool-call identifier.</summary>
	private readonly Dictionary<string, JsonElement> _tools = new( StringComparer.Ordinal );

	/// <summary>Stores the intent Omp gave each tool call when it started, by tool-call identifier.</summary>
	private readonly Dictionary<string, string> _intents = new( StringComparer.Ordinal );

	/// <summary>Holds the commands the connected Omp offers, in its order.</summary>
	private SlashCommand[] _commands = [];

	/// <summary>Stores the last slash command sent that did not start a turn, so output Omp prints for it, even long after admitting it as <c>/compact</c> does, shows under its name.</summary>
	private string? _sentCommand;

	/// <summary>Gets whether a <c>/compact</c> the user ran is still summarizing the session.</summary>
	private bool _isCompacting;

	/// <summary>Raised on the UI thread after the session's state changed, so a view rebuilds.</summary>
	public event Action? Changed;

	/// <summary>Raised on the UI thread when the session needs the user, with the conversation's title and what it needs.</summary>
	public event Action<string, string>? AttentionRequested;

	/// <summary>Raised on the UI thread when the session works on alone and withdraws its request for the user.</summary>
	public event Action? AttentionCleared;

	/// <summary>Raised on the UI thread when Omp admitted a prompt, with its text and attachments, so a view can empty its composer of them.</summary>
	public event Action<string, string[]>? PromptAdmitted;

	/// <summary>Raised on the UI thread when Omp never accepted a sent prompt, which stays in the view's composer.</summary>
	public event Action? PromptRefused;

	/// <summary>Raised on the UI thread when Omp refused a queued or steering message, with its text and attachments, so a view can return them to an empty composer.</summary>
	public event Action<string, string[]>? InterjectRefused;

	/// <summary>Raised on the UI thread when a message taken back from Omp's queue should return to an empty composer.</summary>
	public event Action<string>? UnqueueRestored;

	/// <summary>Raised on the UI thread when an Omp extension replaces the composer's text.</summary>
	public event Action<string>? EditorTextRequested;

	/// <summary>Raised on the UI thread when a tool call's live state changed, by its tool-call identifier, so a view rebuilds the row that holds it.</summary>
	public event Action<string>? ToolChanged;

	/// <summary>Raised on the UI thread when a question was answered, cancelled or dismissed and stopped being pending, with the question itself.</summary>
	public event Action<JsonElement>? DialogLeft;

	/// <summary>Raised on the UI thread when an answer to a question reached Omp, so a view marks its panel read.</summary>
	public event Action? DialogAnswered;

	/// <summary>Raised on the UI thread when the pending questions or their selections changed.</summary>
	public event Action? DialogsChanged;

	/// <summary>Raised on the UI thread when the session starts a new process, whose questions and messages start afresh.</summary>
	public event Action? Connecting;

	/// <summary>Raised on the UI thread after Omp started a fresh session and the transcript was emptied.</summary>
	public event Action? Cleared;

	/// <summary>Gets the Omp service the session runs on.</summary>
	public OmpService Backend => backend;

	/// <summary>Raised on the UI thread after a local command's output joined the transcript.</summary>
	public event Action? OutputAppended;

	/// <summary>Gets the conversation the session runs.</summary>
	public Guid ConversationId => conversationId;

	/// <summary>Gets the panel showing the session, the one attached last, or null while no panel shows it; safe to read on any thread.</summary>
	internal Guid? PanelId
	{
		get
		{
			lock ( _gate )
				return _panels.Count > 0 ? _panels[^1] : null;
		}
	}

	/// <summary>Gets whether the agent is connecting, working or waiting on tools.</summary>
	public bool IsBusy => _isBusy;

	/// <summary>Gets the visible backend status or error.</summary>
	public string Status => _status;

	/// <summary>Gets the connected process, or null before it connects.</summary>
	public OmpSession? Connected => _session;

	/// <summary>Gets whether the session's process is connected.</summary>
	public bool IsConnected => _session is not null;

	/// <summary>Gets false: this session runs its agent in this process.</summary>
	public bool IsRemote => false;

	/// <summary>Gets whether a connection was started, finished or not.</summary>
	public bool IsConnectionStarted => _connection is not null;

	/// <summary>Gets the model the connected backend reported, as <c>provider/id</c>, or empty.</summary>
	public string ConnectedModel => _connectedModel;

	/// <summary>Gets the connected model's catalog limits and prices, or null.</summary>
	public OmpModelInfo? ModelInfo => _modelInfo;

	/// <summary>Gets how many compactions ran while this session ran.</summary>
	public int CompactionCount => _compactionCount;

	/// <summary>Gets how many isolated model calls Lokn started for this chat.</summary>
	public int AuxiliaryCallCount => _auxiliaryCallCount;

	/// <summary>Gets live tool execution frames by tool-call identifier.</summary>
	public IReadOnlyDictionary<string, JsonElement> Tools => _tools;

	/// <summary>Gets the intent each started tool call gave, by tool-call identifier.</summary>
	public IReadOnlyDictionary<string, string> Intents => _intents;

	/// <summary>Gets the commands the connected Omp offers.</summary>
	public SlashCommand[] Commands => _commands;

	/// <summary>Gets whether a <c>/compact</c> the user ran is still summarizing the session.</summary>
	public bool IsCompacting => _isCompacting;

	/// <summary>Captures every value a view reads, for a view in another process.</summary>
	public ChatSessionState CaptureState() => new()
	{
		IsBusy = _isBusy,
		Status = _status,
		IsConnected = _session is not null,
		IsConnectionStarted = _connection is not null,
		IsConnecting = _connection is { IsCompleted: false },
		ConnectedModel = _connectedModel,
		ModelInfo = _modelInfo,
		ContextTokens = _contextTokens,
		ContextWindow = _contextWindow,
		ContextShare = store.Find( conversationId ) is { } conversation ? ComputeContextShare( conversation, store.GetMessages( conversationId ) ) : 0,
		IsAutoCompaction = _isAutoCompaction,
		Compaction = _compaction,
		IsCompacting = _isCompacting,
		IsAutoCompacting = _isAutoCompacting,
		Efforts = _efforts,
		Effort = _effort,
		CompactionCount = _compactionCount,
		AuxiliaryCallCount = _auxiliaryCallCount,
		Commands = _commands,
		Dialogs = [.. _dialogs],
		Selections = _answers.Where( answer => answer.Value.Count > 0 ).ToDictionary( answer => answer.Key, answer => answer.Value.ToArray() ),
		QueuedSteering = _queuedSteering,
		QueuedFollowUps = _queuedFollowUps,
		LiveSteered = _liveSteered,
	};

	/// <summary>Attaches a panel that shows this session; host tools the agent calls see the panel attached last as their caller.</summary>
	public void Attach( Guid panelId )
	{
		lock ( _gate )
		{
			_panels.Remove( panelId );
			_panels.Add( panelId );
		}
		_isStopPending = false;
		// A question shown in the feed is answered in place once a panel shows it.
		if ( _dialogs.Count > 0 )
			DialogsChanged?.Invoke();
	}

	/// <summary>Detaches a panel; once none shows the session, an idle process stops at once and a working one when its turn settles.</summary>
	public void Detach( Guid panelId )
	{
		int count;
		lock ( _gate )
		{
			_panels.Remove( panelId );
			count = _panels.Count;
		}
		if ( _isDisposed )
			return;

		store.Save();
		// The feed answers a pending question in place only while a panel shows the chat, and otherwise links to it.
		if ( _dialogs.Count > 0 )
			DialogsChanged?.Invoke();
		if ( count > 0 )
			return;

		if ( _isBusy )
		{
			_isStopPending = true;
			return;
		}

		UpdateDisconnect();
		MarkChanged();
	}

	/// <summary>Stops the process and its agent tools, saves streaming history, and ends the session's background work.</summary>
	public void Dispose()
	{
		if ( _isDisposed )
			return;

		_isDisposed = true;
		_lifetime.Cancel();
		store.SetWorking( conversationId, false );
		store.Save();
		UpdateStopSession();
		UpdateConnectionEnd();
		_lifetime.Dispose();
	}

	/// <summary>Gets the token cancelled when the session is disposed.</summary>
	private CancellationToken Lifetime => _isDisposed ? new CancellationToken( true ) : _lifetime.Token;

	/// <summary>Posts work from a background continuation to the UI thread while the session lives.</summary>
	private bool Post( Action action ) => !_isDisposed && post( () =>
	{
		if ( !_isDisposed )
			action();
	} );

	/// <summary>Records a state change: stops a process no panel shows once it is idle, tells the sidebar whether the agent works and the feed whether it waits on a question, then tells the views.</summary>
	private void MarkChanged()
	{
		if ( _isStopPending && !_isBusy && PanelId is null )
		{
			_isStopPending = false;
			UpdateDisconnect();
		}
		store.SetWorking( conversationId, _isBusy && !_isDisposed );
		UpdateDialogSignature();
		Changed?.Invoke();
	}

	/// <summary>Shows a status line message, such as a refusal the view decided on.</summary>
	public void UpdateStatus( string status )
	{
		_status = status;
		MarkChanged();
	}

	/// <summary>Starts the backend without sending a model prompt.</summary>
	public void UpdateConnect()
	{
		if ( _connection is not null )
			return;

		_ = UpdateConnectAsync();
	}

	/// <summary>Observes explicit connection failures after the connection routine reports them in the status.</summary>
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
	public Task<OmpSession> EnsureConnection()
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
		_answers.Clear();
		Connecting?.Invoke();
		var conversation = store.Get( conversationId );
		var directory = store.GetDirectory( conversationId );
		_connectionDirectory = directory;
		// Under the data folder, so a bare daemon or a --data instance keeps its Omp sessions out of the user's.
		var sessionDirectory = conversation.BackendSessionPath
			?? LoknPaths.Combine( "sessions", conversation.Profile.ToString(), conversationId.ToString( "N" ) );
		store.SaveBackendSession( conversationId, sessionDirectory );
		_status = "Connecting…";
		MarkChanged();
		var epoch = ++_connectionEpoch;
		_connectionCancel = CancellationTokenSource.CreateLinkedTokenSource( Lifetime );
		_connection = BuildConnectionAsync( conversation.Profile, directory, sessionDirectory, epoch, _connectionCancel.Token );
		return _connection;
	}

	/// <summary>Loads the authoritative backend transcript while keeping local history readable offline.</summary>
	/// <remarks>A connection that ended before its startup finished, by <see cref="UpdateDisconnect"/>, a transport failure or disposal, changes nothing when it
	/// finishes: its process stops, and the session's state is left to the connection that replaced it.</remarks>
	/// <param name="profile">The account profile the process runs under.</param>
	/// <param name="directory">The folder the process works in.</param>
	/// <param name="sessionDirectory">The Omp session directory that keeps the conversation.</param>
	/// <param name="epoch">The connection's number, which <see cref="_connectionEpoch"/> leaves once the connection ended.</param>
	/// <param name="cancellation">Cancelled once the connection ended.</param>
	private async Task<OmpSession> BuildConnectionAsync( SettingsProfile profile, string directory, string sessionDirectory, int epoch, CancellationToken cancellation )
	{
		OmpSession? session = null;
		try
		{
			session = await backend.OpenSessionAsync( profile, directory, sessionDirectory, cancellation, onStarting: starting =>
			{
				session = starting;
				// A connection that ended meanwhile is cancelled, so its process stops below without ever reporting to the session.
				if ( epoch != Volatile.Read( ref _connectionEpoch ) )
					return;

				_session = starting;
				starting.FrameReceived += MarkFrameReceived;
			} );
			if ( _isDisposed || cancellation.IsCancellationRequested )
				throw new OperationCanceledException( cancellation );

			var data = await session.RequestAsync( "get_messages", cancellationToken: cancellation );
			var state = await session.RequestAsync( "get_state", cancellationToken: cancellation );
			var model = state.TryGetProperty( "model", out var selected ) && selected.ValueKind == JsonValueKind.Object
				? ReadText( selected, "provider" ) + "/" + ReadText( selected, "id" ) : "";
			await session.RequestAsync( "set_ask_dialog", new
			{
				enabled = true
			}, cancellation );
			var messages = new List<ConversationMessage>();
			if ( data.ValueKind == JsonValueKind.Object && data.TryGetProperty( "messages", out var history ) )
			{
				foreach ( var message in history.EnumerateArray() )
					messages.Add( BuildStoredMessage( Guid.NewGuid(), message ) );
			}

			string[] economyEfforts = [];
			string? economyEffort = null;
			if ( messages.Count == 0 && app.Settings.Get( OmpModule.Economy, ComputeScope( profile ) ) )
			{
				try
				{
					var available = await session.RequestAsync( "get_available_thinking_levels", cancellationToken: cancellation );
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
						}, cancellation );
					}
				}
				catch ( Exception error ) when ( error is not OperationCanceledException )
				{
					// An older Omp or a model without selectable effort keeps its own default.
				}
			}

			var connected = session;
			var isPosted = Post( () =>
			{
				if ( epoch != _connectionEpoch )
				{
					// The connection ended while it started, so its process is not the session's.
					UpdateStaleStop( connected );
					return;
				}

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
			if ( !isPosted )
				throw new OperationCanceledException( cancellation );
			return session;
		}
		catch ( Exception error )
		{
			if ( session is not null )
			{
				session.FrameReceived -= MarkFrameReceived;
				await session.StopAsync();
			}
			var failed = session;
			Post( () =>
			{
				if ( epoch != _connectionEpoch )
				{
					// A connection that ended changes nothing of the one that replaced it.
					if ( failed is not null && _session == failed )
						_session = null;
					return;
				}

				_connection = null;
				_session = null;
				UpdateToolBridge( null );
				_isBusy = false;
				_status = error is OperationCanceledException ? "Disconnected" : DescribeFailure( error );
				UpdateConnectionEnd();
				MarkChanged();
			} );
			throw;
		}
	}

	/// <summary>Stops the process of a connection that ended while it started, and forgets it if the session still holds it.</summary>
	private void UpdateStaleStop( OmpSession stale )
	{
		stale.FrameReceived -= MarkFrameReceived;
		_ = stale.StopAsync();
		if ( _session == stale )
			_session = null;
	}

	/// <summary>Ends the current connection: its startup is cancelled, and a startup that finishes afterwards changes nothing.</summary>
	private void UpdateConnectionEnd()
	{
		_connectionEpoch++;
		if ( _connectionCancel is not { } cancel )
			return;

		_connectionCancel = null;
		cancel.Cancel();
		cancel.Dispose();
	}

	/// <summary>Offers Lokn's agent tools to a connected session, replacing the previous session's bridge; the panel showing the chat is each call's caller,
	/// and the conversation identifies the chat whether or not a panel shows it.</summary>
	private void UpdateToolBridge( OmpSession? session )
	{
		_toolBridge?.Dispose();
		_toolBridge = null;
		if ( session is null )
			return;

		_toolBridge = new( session, app, () => PanelId, store.Get( conversationId ).Profile, Post, conversationId );
		_toolBridge.Failed += message => Post( () =>
		{
			_status = message;
			MarkChanged();
		} );
		_toolBridge.Publish();
	}

	/// <summary>Stops the connected Omp process and detaches its events and agent tools.</summary>
	private void UpdateStopSession()
	{
		UpdateToolBridge( null );
		if ( _session is { } session )
		{
			session.FrameReceived -= MarkFrameReceived;
			_ = session.StopAsync();
		}
	}

	/// <summary>Stops the process and forgets it with everything it reported, so the next prompt starts Omp afresh and continues the same session.</summary>
	/// <remarks>Its pending questions, queue, live tools and status go with it: no process is left to answer them, and a reopened chat starts clean.</remarks>
	private void UpdateDisconnect()
	{
		UpdateStopSession();
		UpdateConnectionEnd();
		_session = null;
		_connection = null;
		_connectionDirectory = null;
		_status = "Ready";
		_sentCommand = null;
		_isCompacting = false;
		_isAutoCompacting = false;
		_messageIds.Clear();
		_tools.Clear();
		_intents.Clear();
		_dialogs.Clear();
		_answers.Clear();
		ClearQueue();
	}

	/// <summary>Sends a prompt and its attachments to the conversation's captured account, connecting first when needed.</summary>
	/// <remarks>Once Omp admits it, <see cref="PromptAdmitted"/> lets the view empty its composer; when Omp never accepts it, <see cref="PromptRefused"/> keeps it there.</remarks>
	public void Send( string text, string[] attachments )
	{
		UpdateFollowFolder();
		_isBusy = true;
		_sentCommand = text.TrimStart().StartsWith( '/' ) ? text.Trim() : null;
		_status = "Sending…";
		MarkChanged();
		_ = UpdateSendAsync( EnsureConnection(), text, attachments );
	}

	/// <summary>Asks the agent to finish the turn a module reload stopped.</summary>
	public void Continue()
	{
		if ( _isBusy )
			return;

		UpdateFollowFolder();
		_isBusy = true;
		_status = "Continuing after reload…";
		MarkChanged();
		_ = UpdateSendAsync( EnsureConnection(), ContinuePrompt, [] );
	}

	/// <summary>Empties the conversation in place: Omp starts a fresh session and the transcript is cleared, keeping the title and project.</summary>
	public void Clear()
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

	/// <summary>Asks Omp for a new session, then clears what is recorded of the old one.</summary>
	private async Task UpdateClearAsync( Task<OmpSession> connection )
	{
		try
		{
			var session = await connection;
			var result = await session.RequestAsync( "new_session", cancellationToken: Lifetime );
			Post( () =>
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
				_answers.Clear();
				_status = "Cleared";
				Cleared?.Invoke();
				MarkChanged();
			} );
		}
		catch ( Exception error )
		{
			Post( () =>
			{
				_isBusy = false;
				_status = error is OperationCanceledException ? "Stopped" : DescribeFailure( error );
				MarkChanged();
			} );
		}
	}

	/// <summary>Hands a message to the running turn: queued for after it, or steering it now; it shows in the queue at once, and <see cref="InterjectRefused"/> returns it if Omp refuses it.</summary>
	/// <returns>Whether the message was handed over; false without a connected process or anything to send.</returns>
	public bool Interject( string text, string[] attachments, bool steer )
	{
		if ( _session is null || (string.IsNullOrWhiteSpace( text ) && attachments.Length == 0) )
			return false;

		var message = ComposePrompt( text, attachments );
		UpdateQueueSent( message, steer );
		MarkChanged();
		_ = UpdateInterjectAsync( EnsureConnection(), text, attachments, message, steer );
		return true;
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
			}, Lifetime );
		}
		catch ( Exception error )
		{
			Post( () =>
			{
				// Omp did not take it, so it leaves the queue and the text and attachments go back to the view.
				if ( steer )
					_queuedSteering = [.. _queuedSteering.Where( queued => queued != message )];
				else
					_queuedFollowUps = [.. _queuedFollowUps.Where( queued => queued != message )];
				InterjectRefused?.Invoke( text, attachments );
				_status = error is OperationCanceledException ? "Stopped" : DescribeFailure( error );
				MarkChanged();
			} );
		}
	}

	/// <summary>Sends a tool card's message to the agent: as a new turn when idle, otherwise queued after the running turn.</summary>
	public void UpdateCardPrompt( string message )
	{
		if ( string.IsNullOrWhiteSpace( message ) )
			return;

		if ( _isBusy )
		{
			_ = UpdateFollowUpAsync( EnsureConnection(), message );
			return;
		}

		_isBusy = true;
		_status = "Sending…";
		MarkChanged();
		_ = UpdateSendAsync( EnsureConnection(), message, [] );
	}

	/// <summary>Queues a message for after the running turn without accessing UI state from a background continuation.</summary>
	private async Task UpdateFollowUpAsync( Task<OmpSession> connection, string message )
	{
		try
		{
			var session = await connection;
			await session.RequestAsync( "follow_up", new
			{
				message
			}, Lifetime );
			Post( () =>
			{
				_status = "Queued for after this turn";
				MarkChanged();
			} );
		}
		catch ( Exception error )
		{
			Post( () =>
			{
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
			}, Lifetime );
			Post( () =>
			{
				if ( ReadText( admission, "agentInvoked" ) is not ("False" or "false") )
					_sentCommand = null;
				else if ( !_isCompacting )
				{
					_isBusy = false;
					_status = "Ready";
				}
				// A view closed before Omp admitted the prompt saved it as the draft; it was sent, so it does not come back when the chat reopens.
				if ( text.Length > 0 && store.Find( conversationId )?.Draft == text )
					store.SaveDraft( conversationId, "" );
				PromptAdmitted?.Invoke( text, attachments );
				MarkChanged();
			} );
		}
		catch ( Exception error )
		{
			Post( () =>
			{
				// The backend never accepted this prompt, so the view keeps it and its attachments with the conversation.
				_sentCommand = null;
				PromptRefused?.Invoke();
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
	public void Stop()
	{
		if ( _session is not { } session )
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
			await session.RequestAsync( type, cancellationToken: Lifetime );
		}
		catch ( Exception error )
		{
			Post( () =>
			{
				_status = error.Message;
				MarkChanged();
			} );
		}
	}

	/// <summary>Marshals every background RPC notification onto the UI thread while the session lives.</summary>
	private void MarkFrameReceived( JsonElement frame ) => Post( () => UpdateFrame( frame ) );

	/// <summary>Applies full streaming snapshots and live tool state using stable backend identifiers.</summary>
	internal void UpdateFrame( JsonElement frame )
	{
		var wasBusy = _isBusy;
		var dialogCount = _dialogs.Count;
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
				ToolChanged?.Invoke( id );
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
				UpdateConnectionEnd();
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

	/// <summary>Remembers a starting tool's intent, which later progress frames no longer carry.</summary>
	private void UpdateIntent( JsonElement frame )
	{
		if ( ReadText( frame, "type" ) == "tool_execution_start" && ReadText( frame, "intent" ) is { Length: > 0 } intent )
			_intents[ReadText( frame, "toolCallId" )] = intent;
	}

	/// <summary>Forgets live frames of tools that never reported their end once the agent has settled, so no tool spins after the turn.</summary>
	private void SettleTools()
	{
		foreach ( var (id, frame) in _tools.ToArray() )
		{
			if ( ReadText( frame, "type" ) == "tool_execution_end" )
				continue;

			_tools.Remove( id );
			ToolChanged?.Invoke( id );
		}
	}

	/// <summary>Records what a local command printed, such as <c>/session</c> or <c>/usage</c>, under the command that asked for it.</summary>
	/// <remarks>
	/// Omp sends it as a <c>command_output</c> frame, before admitting the prompt or, for background work such as <c>/compact</c>,
	/// after. The frame names no command, so output with no command sent stays hidden.
	/// </remarks>
	private void UpdateCommandOutput( JsonElement frame )
	{
		var text = TerminalText.BuildPlain( ReadText( frame, "text" ) ).Trim();
		if ( _sentCommand is not { } command || text.Length == 0 )
			return;

		var rich = JsonSerializer.SerializeToElement( new
		{
			command
		} );
		store.AppendMessage( conversationId, new( Guid.NewGuid(), CommandRole, text, DateTimeOffset.UtcNow, rich ), persist: false );
		OutputAppended?.Invoke();
	}

	/// <summary>Asks for the user when a question arrives or a turn ends, and withdraws once the agent works on alone.</summary>
	private void UpdateAttention( bool wasBusy, int dialogCount )
	{
		var title = store.Get( conversationId ).Title;
		if ( _dialogs.Count > dialogCount )
			AttentionRequested?.Invoke( title, ReadText( _dialogs[^1], "title", "Your input is needed" ) );
		else if ( wasBusy && !_isBusy )
			AttentionRequested?.Invoke( title, _status == "Ready" ? "Finished and waiting for you" : _status );
		else if ( _isBusy && _dialogs.Count == 0 )
			AttentionCleared?.Invoke();
	}
}
