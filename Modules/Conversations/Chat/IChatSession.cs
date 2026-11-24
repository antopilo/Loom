using System.Text.Json;
using Lokn.Modules.Conversations;
using Lokn.Modules.Omp;

namespace Lokn.Modules.Chat;

/// <summary>Runs or shows one conversation's agent: what a chat view reads and asks of it, wherever the agent runs.</summary>
/// <remarks>
/// <see cref="ChatSession"/> runs the agent in this process; <see cref="RemoteChatSession"/> shows one running in a host and sends
/// what is asked of it there. Members are used on the UI thread, and events are raised on it. Calls that answer at once and that a
/// proxy cannot ask its host synchronously are computed from the session's last state on a proxy: <see cref="Interject"/>,
/// <see cref="UpdateDirectory"/>, <see cref="ComputeFolderRefusal"/>, <see cref="ComputeTaskCheckDue"/> and <see cref="ReadSelection"/>.
/// </remarks>
public interface IChatSession : IDisposable
{
	/// <summary>Raised on the UI thread after the session's state changed, so a view rebuilds.</summary>
	event Action? Changed;

	/// <summary>Raised on the UI thread when the session needs the user, with the conversation's title and what it needs.</summary>
	event Action<string, string>? AttentionRequested;

	/// <summary>Raised on the UI thread when the session works on alone and withdraws its request for the user.</summary>
	event Action? AttentionCleared;

	/// <summary>Raised on the UI thread when Omp admitted a prompt, with its text and attachments, so a view can empty its composer of them.</summary>
	event Action<string, string[]>? PromptAdmitted;

	/// <summary>Raised on the UI thread when Omp never accepted a sent prompt, which stays in the view's composer.</summary>
	event Action? PromptRefused;

	/// <summary>Raised on the UI thread when Omp refused a queued or steering message, with its text and attachments, so a view can return them to an empty composer.</summary>
	event Action<string, string[]>? InterjectRefused;

	/// <summary>Raised on the UI thread when a message taken back from Omp's queue should return to an empty composer.</summary>
	event Action<string>? UnqueueRestored;

	/// <summary>Raised on the UI thread when an Omp extension replaces the composer's text.</summary>
	event Action<string>? EditorTextRequested;

	/// <summary>Raised on the UI thread when a tool call's live state changed, by its tool-call identifier, so a view rebuilds the row that holds it.</summary>
	event Action<string>? ToolChanged;

	/// <summary>Raised on the UI thread when a question was answered, cancelled or dismissed and stopped being pending, with the question itself.</summary>
	event Action<JsonElement>? DialogLeft;

	/// <summary>Raised on the UI thread when an answer to a question reached Omp, so a view marks its panel read.</summary>
	event Action? DialogAnswered;

	/// <summary>Raised on the UI thread when the pending questions or their selections changed.</summary>
	event Action? DialogsChanged;

	/// <summary>Raised on the UI thread when the session starts a new process, whose questions and messages start afresh.</summary>
	event Action? Connecting;

	/// <summary>Raised on the UI thread after Omp started a fresh session and the transcript was emptied.</summary>
	event Action? Cleared;

	/// <summary>Raised on the UI thread after a local command's output joined the transcript.</summary>
	event Action? OutputAppended;

	/// <summary>Gets the conversation the session runs.</summary>
	Guid ConversationId { get; }

	/// <summary>Gets this process's Omp service, for model names, the model catalog and provider usage.</summary>
	OmpService Backend { get; }

	/// <summary>Gets whether the agent runs in another process, whose Omp process this one cannot reach.</summary>
	bool IsRemote { get; }

	/// <summary>Gets whether the agent is connecting, working or waiting on tools.</summary>
	bool IsBusy { get; }

	/// <summary>Gets whether the agent's run ended and the session stays busy only until background work, such as jobs or subagents, settles.</summary>
	bool IsSettling { get; }

	/// <summary>Gets the visible backend status or error.</summary>
	string Status { get; }

	/// <summary>Gets the connected Omp process in this process, or null before it connects and always for a remote session.</summary>
	OmpSession? Connected { get; }

	/// <summary>Gets whether the session's Omp process is connected, wherever it runs.</summary>
	bool IsConnected { get; }

	/// <summary>Gets whether a connection was started, finished or not.</summary>
	bool IsConnectionStarted { get; }

	/// <summary>Gets the model the connected backend reported, as <c>provider/id</c>, or empty.</summary>
	string ConnectedModel { get; }

	/// <summary>Gets the connected model's catalog limits and prices, or null.</summary>
	OmpModelInfo? ModelInfo { get; }

	/// <summary>Gets how many compactions ran while this session ran.</summary>
	int CompactionCount { get; }

	/// <summary>Gets how many isolated model calls Lokn started for this chat.</summary>
	int AuxiliaryCallCount { get; }

	/// <summary>Gets live tool execution frames by tool-call identifier.</summary>
	IReadOnlyDictionary<string, JsonElement> Tools { get; }

	/// <summary>Gets the intent each started tool call gave, by tool-call identifier.</summary>
	IReadOnlyDictionary<string, string> Intents { get; }

	/// <summary>Gets the commands the connected Omp offers.</summary>
	SlashCommand[] Commands { get; }

	/// <summary>Gets whether a <c>/compact</c> the user ran is still summarizing the session.</summary>
	bool IsCompacting { get; }

	/// <summary>Gets the context's tokens, or null when Omp has not measured them yet.</summary>
	long? ContextTokens { get; }

	/// <summary>Gets the model's context window in tokens, or zero when unknown.</summary>
	long ContextWindow { get; }

	/// <summary>Gets whether the session compacts automatically.</summary>
	bool IsAutoCompaction { get; }

	/// <summary>Gets the profile's compaction settings once read, or null.</summary>
	OmpCompaction? Compaction { get; }

	/// <summary>Gets whether an automatic compaction runs now.</summary>
	bool IsAutoCompacting { get; }

	/// <summary>Gets the efforts the live model supports, <c>off</c> first.</summary>
	string[] Efforts { get; }

	/// <summary>Gets the session's effort, or empty when unknown.</summary>
	string Effort { get; }

	/// <summary>Gets the questions waiting for an answer, in wire order.</summary>
	IReadOnlyList<JsonElement> Dialogs { get; }

	/// <summary>Gets the first question still waiting for an answer, or null when none is.</summary>
	JsonElement? PendingDialog { get; }

	/// <summary>Gets the steering messages Omp holds, oldest first.</summary>
	string[] QueuedSteering { get; }

	/// <summary>Gets the follow-up messages Omp holds, oldest first.</summary>
	string[] QueuedFollowUps { get; }

	/// <summary>Gets how many leading steering messages the model already received.</summary>
	int LiveSteered { get; }

	/// <summary>Gets the chat's goal as Omp reports it: <c>objective</c>, <c>status</c>, <c>tokensUsed</c> and <c>tokenBudget</c>; null without one.</summary>
	JsonElement? Goal { get; }

	/// <summary>Captures every value a view reads, for a view in another process.</summary>
	ChatSessionState CaptureState();

	/// <summary>Attaches a panel that shows this session; host tools the agent calls see the panel attached last as their caller.</summary>
	void Attach( Guid panelId );

	/// <summary>Detaches a panel; once none shows the session, an idle process stops at once and a working one when its turn settles.</summary>
	void Detach( Guid panelId );

	/// <summary>Shows a status line message, such as a refusal the view decided on.</summary>
	void UpdateStatus( string status );

	/// <summary>Starts the backend without sending a model prompt.</summary>
	void UpdateConnect();

	/// <summary>Connects the session's Omp process in this process, such as for its Agent Hub.</summary>
	/// <exception cref="NotSupportedException">The session is remote (<see cref="IsRemote"/>): its process runs in the host.</exception>
	Task<OmpSession> EnsureConnection();

	/// <summary>Sends a prompt and its attachments, connecting first when needed; <see cref="PromptAdmitted"/> or <see cref="PromptRefused"/> follows.</summary>
	void Send( string text, string[] attachments );

	/// <summary>Asks the agent to finish the turn a module reload stopped.</summary>
	void Continue();

	/// <summary>Empties the conversation in place: Omp starts a fresh session and the transcript is cleared, keeping the title and project.</summary>
	void Clear();

	/// <summary>Hands a message to the running turn: queued for after it, or steering it now; <see cref="InterjectRefused"/> returns it if it is refused.</summary>
	/// <returns>Whether the message was handed over; false without a connected process or anything to send.</returns>
	bool Interject( string text, string[] attachments, bool steer );

	/// <summary>Sends a tool card's message to the agent: as a new turn when idle, otherwise queued after the running turn.</summary>
	void UpdateCardPrompt( string message );

	/// <summary>Requests cancellation of the running turn without discarding its recorded messages.</summary>
	void Stop();

	/// <summary>Steps the session to the next effort the model supports.</summary>
	void UpdateEffort();

	/// <summary>Switches this chat's session to a model, connecting first when needed.</summary>
	void UpdateModel( OmpModelInfo model );

	/// <summary>Dismisses a question that takes no answer, such as an authorization page to open.</summary>
	void DismissDialog( string id );

	/// <summary>Sends one answer to a pending question; the answer is serialized as JSON, as Omp reads it.</summary>
	void AnswerDialog( string id, object response );

	/// <summary>Gets the labels selected for one questionnaire question, by dialog and question key; none before the first pick.</summary>
	IReadOnlySet<string> ReadSelection( string key );

	/// <summary>Picks or unpicks one option of a questionnaire question; a single-choice question keeps only the last pick.</summary>
	void UpdateSelection( string key, string label, bool isMulti );

	/// <summary>Releases a finished question's selections, once its exit played.</summary>
	void ReleaseSelections( string id );

	/// <summary>Moves a waiting follow-up into the steering queue.</summary>
	void UpdatePromote( string text );

	/// <summary>Takes a waiting message back from Omp, offering it to the views' empty composer through <see cref="UnqueueRestored"/>.</summary>
	void UpdateUnqueue( string text, bool steering );

	/// <summary>Moves a chat outside projects to another folder.</summary>
	/// <returns>Whether the folder was set; a project chat, or one whose agent is working or connecting, keeps its folder and says why.</returns>
	bool UpdateDirectory( string directory );

	/// <summary>Gets why the chat cannot change its folder now, or null when it can.</summary>
	string? ComputeFolderRefusal( ConversationInfo conversation );

	/// <summary>Gets whether a prompt about to be sent is checked first for starting a new task.</summary>
	bool ComputeTaskCheckDue( string text );

	/// <summary>Asks the small model whether a prompt starts a new task; a failure, a timeout or an unclear answer counts as the same task.</summary>
	/// <returns>Whether the prompt looks like a new task; may complete off the UI thread.</returns>
	Task<bool> CheckNewTaskAsync( string text, CancellationToken cancellationToken );
}
