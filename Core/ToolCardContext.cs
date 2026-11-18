using System.Text.Json;
using Goo;

namespace Lokn.Core;

/// <summary>Describes how far a call to an agent tool has come.</summary>
public enum AgentToolStatus
{
	/// <summary>The agent asked for the call, and it has not started.</summary>
	Requested,

	/// <summary>The call is running.</summary>
	Running,

	/// <summary>The call finished.</summary>
	Done,

	/// <summary>The call failed or was cancelled.</summary>
	Failed,
}

/// <summary>Shows one call to a module's agent tool to its card, and lets the card act on it.</summary>
/// <remarks>
/// A chat builds the card on the UI thread on every redraw of the call's row. Draw only from what this context holds,
/// never from live module objects, so a card looks the same while it runs, after Lokn restarts, and in history.
/// </remarks>
public abstract class ToolCardContext
{
	/// <summary>Gets the owning application on its UI thread.</summary>
	public abstract LoknApplication App { get; }

	/// <summary>Gets the agent's identifier for the call, which the running tool sees as <see cref="AgentToolContext.CallId"/>.</summary>
	public abstract string CallId { get; }

	/// <summary>Gets the arguments the agent sent, normally a JSON object.</summary>
	public abstract JsonElement Arguments { get; }

	/// <summary>Gets how far the call has come.</summary>
	public abstract AgentToolStatus Status { get; }

	/// <summary>Gets the latest progress text while the call runs, then its result text.</summary>
	public abstract string Text { get; }

	/// <summary>Gets the latest reported details while the call runs, then its result's; undefined when there are none.</summary>
	public abstract JsonElement Details { get; }

	/// <summary>Gets the card's view state, such as which sections are open, created on first use and kept per chat and call.</summary>
	/// <typeparam name="T">The card's own state type; one instance per type.</typeparam>
	public abstract T State<T>() where T : class, new();

	/// <summary>Applies a view change, such as to <see cref="State{T}"/>, and redraws the card.</summary>
	/// <param name="change">The change, run on the UI thread.</param>
	public abstract void Run( Action change );

	/// <summary>Sends the agent a message as if the user typed it: a new turn when the agent is idle, or queued after the current turn.</summary>
	/// <param name="message">The message text.</param>
	public abstract void Prompt( string message );
}

/// <summary>Stores a module's card for calls to one of its tools.</summary>
/// <param name="Name">The qualified tool name.</param>
/// <param name="Build">Builds the card for one call.</param>
internal sealed record ToolCardDefinition( string Name, Func<ToolCardContext, Blob> Build );
