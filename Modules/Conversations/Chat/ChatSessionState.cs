using System.Text.Json;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;

namespace Lokn.Modules.Chat;

/// <summary>Holds every value of a chat session a view reads, as a host sends it to the views of other processes in <c>state</c> pushes.</summary>
/// <remarks>
/// Serialized with <see cref="StoreOp.JsonOptions"/> (camelCase). Live tool frames and intents travel apart, in <c>tool</c>
/// pushes and the <c>attach</c> answer; messages travel in <c>msg</c> and <c>msgs</c> pushes. See <c>docs/remote.md</c>.
/// </remarks>
public sealed record ChatSessionState
{
	/// <summary>Gets whether the agent is connecting, working or waiting on tools.</summary>
	public bool IsBusy { get; init; }

	/// <summary>Gets the visible backend status or error.</summary>
	public string Status { get; init; } = "Ready";

	/// <summary>Gets whether the session's Omp process is connected.</summary>
	public bool IsConnected { get; init; }

	/// <summary>Gets whether a connection was started, finished or not.</summary>
	public bool IsConnectionStarted { get; init; }

	/// <summary>Gets whether the Omp process is still starting, during which the chat's folder cannot change.</summary>
	public bool IsConnecting { get; init; }

	/// <summary>Gets the model the connected backend reported, as <c>provider/id</c>, or empty.</summary>
	public string ConnectedModel { get; init; } = "";

	/// <summary>Gets the connected model's catalog limits and prices, or null.</summary>
	public OmpModelInfo? ModelInfo { get; init; }

	/// <summary>Gets the context's tokens, or null when Omp has not measured them yet.</summary>
	public long? ContextTokens { get; init; }

	/// <summary>Gets the model's context window in tokens, or zero when unknown.</summary>
	public long ContextWindow { get; init; }

	/// <summary>Gets the share of the context window the chat uses as the host estimates it, which decides whether a new prompt is checked for a new task.</summary>
	public double ContextShare { get; init; }

	/// <summary>Gets whether the session compacts automatically.</summary>
	public bool IsAutoCompaction { get; init; } = true;

	/// <summary>Gets the profile's compaction settings once read, or null.</summary>
	public OmpCompaction? Compaction { get; init; }

	/// <summary>Gets whether a <c>/compact</c> the user ran is still summarizing the session.</summary>
	public bool IsCompacting { get; init; }

	/// <summary>Gets whether an automatic compaction runs now.</summary>
	public bool IsAutoCompacting { get; init; }

	/// <summary>Gets the efforts the live model supports, <c>off</c> first.</summary>
	public string[] Efforts { get; init; } = [];

	/// <summary>Gets the session's effort, or empty when unknown.</summary>
	public string Effort { get; init; } = "";

	/// <summary>Gets how many compactions ran while the session ran.</summary>
	public int CompactionCount { get; init; }

	/// <summary>Gets how many isolated model calls Lokn started for the chat.</summary>
	public int AuxiliaryCallCount { get; init; }

	/// <summary>Gets the commands the connected Omp offers.</summary>
	public SlashCommand[] Commands { get; init; } = [];

	/// <summary>Gets the questions waiting for an answer, in wire order, as Omp sent them.</summary>
	public JsonElement[] Dialogs { get; init; } = [];

	/// <summary>Gets the labels selected in questionnaires, by dialog and question key; keys without a selection are left out.</summary>
	public Dictionary<string, string[]> Selections { get; init; } = [];

	/// <summary>Gets the steering messages Omp holds, oldest first.</summary>
	public string[] QueuedSteering { get; init; } = [];

	/// <summary>Gets the follow-up messages Omp holds, oldest first.</summary>
	public string[] QueuedFollowUps { get; init; } = [];

	/// <summary>Gets how many leading steering messages the model already received.</summary>
	public int LiveSteered { get; init; }

	/// <summary>Gets the chat's goal as Omp reports it, or null without one.</summary>
	public JsonElement? Goal { get; init; }
}

/// <summary>Carries one event a chat session raised, other than <see cref="IChatSession.Changed"/>, which a new state implies, and
/// <see cref="IChatSession.ToolChanged"/>, which travels as a <c>tool</c> push; a host sends it to other processes in <c>evt</c> pushes.</summary>
/// <param name="Name">The event's name, one of the constants here.</param>
/// <param name="Args">The event's arguments as a JSON array, in their order.</param>
public sealed record ChatSessionEvent( string Name, JsonElement Args )
{
	/// <summary>Names <see cref="IChatSession.AttentionRequested"/>: title, message.</summary>
	public const string AttentionRequested = nameof( IChatSession.AttentionRequested );

	/// <summary>Names <see cref="IChatSession.AttentionCleared"/>.</summary>
	public const string AttentionCleared = nameof( IChatSession.AttentionCleared );

	/// <summary>Names <see cref="IChatSession.PromptAdmitted"/>: text, attachments.</summary>
	public const string PromptAdmitted = nameof( IChatSession.PromptAdmitted );

	/// <summary>Names <see cref="IChatSession.PromptRefused"/>.</summary>
	public const string PromptRefused = nameof( IChatSession.PromptRefused );

	/// <summary>Names <see cref="IChatSession.InterjectRefused"/>: text, attachments.</summary>
	public const string InterjectRefused = nameof( IChatSession.InterjectRefused );

	/// <summary>Names <see cref="IChatSession.UnqueueRestored"/>: text.</summary>
	public const string UnqueueRestored = nameof( IChatSession.UnqueueRestored );

	/// <summary>Names <see cref="IChatSession.EditorTextRequested"/>: text.</summary>
	public const string EditorTextRequested = nameof( IChatSession.EditorTextRequested );

	/// <summary>Names <see cref="IChatSession.DialogLeft"/>: the question.</summary>
	public const string DialogLeft = nameof( IChatSession.DialogLeft );

	/// <summary>Names <see cref="IChatSession.DialogAnswered"/>.</summary>
	public const string DialogAnswered = nameof( IChatSession.DialogAnswered );

	/// <summary>Names <see cref="IChatSession.DialogsChanged"/>.</summary>
	public const string DialogsChanged = nameof( IChatSession.DialogsChanged );

	/// <summary>Names <see cref="IChatSession.Connecting"/>.</summary>
	public const string Connecting = nameof( IChatSession.Connecting );

	/// <summary>Names <see cref="IChatSession.Cleared"/>.</summary>
	public const string Cleared = nameof( IChatSession.Cleared );

	/// <summary>Names <see cref="IChatSession.OutputAppended"/>.</summary>
	public const string OutputAppended = nameof( IChatSession.OutputAppended );

	/// <summary>Builds an event from its arguments, serialized with the protocol's JSON options.</summary>
	public static ChatSessionEvent Build( string name, params object?[] args ) =>
		new( name, JsonSerializer.SerializeToElement( args, StoreOp.JsonOptions ) );

	/// <summary>Reads one argument by its position.</summary>
	/// <exception cref="ArgumentException">The argument is missing.</exception>
	public T Read<T>( int index ) =>
		Args.ValueKind == JsonValueKind.Array && index < Args.GetArrayLength() && Args[index].Deserialize<T>( StoreOp.JsonOptions ) is { } value ? value
			: throw new ArgumentException( $"The chat event {Name} has no argument {index}." );
}
