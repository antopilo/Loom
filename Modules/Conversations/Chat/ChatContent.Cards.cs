using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Omp;

namespace Lokn.Modules.Chat;

/// <summary>Draws calls to module tools that bring their own card, and carries the card's view state and actions.</summary>
public sealed partial class ChatContent
{
	/// <summary>Retains each card's view state by its call and state type, for as long as this panel lives.</summary>
	private readonly Dictionary<(string CallId, Type Type), object> _cardStates = [];

	/// <summary>Gets the application for this panel's tool cards, which cannot reach the constructor's context themselves.</summary>
	private LoknApplication CardApp => context.App;

	/// <summary>Defines the address prefix of the devices through which Omp lets the agent run Lokn's tools.</summary>
	private const string DevicePrefix = "xd://";

	/// <summary>Gets the tool whose card draws a call: the call's own tool, or the Lokn tool an agent ran by writing its arguments to <c>xd://tool</c>.</summary>
	/// <remarks>Omp offers Lokn's tools to the agent as xd:// devices, so most calls to them arrive as <c>write</c> calls.</remarks>
	private static string ComputeCardTool( string name, JsonElement arguments ) =>
		name == "write" && ReadText( arguments, "path" ) is { } path && path.StartsWith( DevicePrefix, StringComparison.Ordinal ) ? path[DevicePrefix.Length..].TrimEnd( '/' ) : name;

	/// <summary>Builds a module's card for one tool call, or null when the tool has none or its card failed.</summary>
	private Blob? BuildToolCard( string key, string id, string name, JsonElement arguments, string status, JsonElement? result )
	{
		var tool = ComputeCardTool( name, arguments );
		if ( !context.App.Tools.HasCard( tool ) )
			return null;

		var details = result is { ValueKind: JsonValueKind.Object } body && body.TryGetProperty( "details", out var value ) ? value : default;
		if ( tool != name )
		{
			// A device write's result keeps the tool's own arguments and details under "xdev"; until it arrives, the written text is the arguments.
			var device = details.ValueKind == JsonValueKind.Object && details.TryGetProperty( "xdev", out var xdev ) && xdev.ValueKind == JsonValueKind.Object ? xdev : default;
			arguments = device.ValueKind == JsonValueKind.Object && device.TryGetProperty( "args", out var args ) ? args : ParseDeviceArguments( ReadText( arguments, "content" ) );
			details = device.ValueKind == JsonValueKind.Object && device.TryGetProperty( "inner", out var inner ) ? inner : default;
		}

		var card = new ChatToolCard( this, id, arguments, status switch
		{
			"Running" => AgentToolStatus.Running,
			"Done" => AgentToolStatus.Done,
			"Failed" => AgentToolStatus.Failed,
			_ => AgentToolStatus.Requested,
		}, result is { } output ? ComputeResultText( output ) : "", details );
		if ( context.App.Tools.BuildCard( tool, card ) is not { } content )
			return null;

		return new Container
		{
			Key = "tool-card:" + key,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = $"{tool} · {status}",
			Children = [content],
		};
	}

	/// <summary>Parses the JSON an agent is writing to a device, or undefined while it is still incomplete.</summary>
	private static JsonElement ParseDeviceArguments( string content )
	{
		try
		{
			using var document = JsonDocument.Parse( content );
			return document.RootElement.Clone();
		}
		catch ( JsonException )
		{
			return default;
		}
	}

	/// <summary>Applies a card's view change and rebuilds the row that holds its call.</summary>
	private void UpdateCard( string callId, Action change ) => context.Run( () =>
	{
		change();
		if ( _toolOwners.TryGetValue( callId, out var owner ) )
			MarkRowChanged( owner );
		else
			MarkChanged();
	} );

	/// <summary>Sends a card's message to the agent: as a new turn when idle, otherwise queued after the running turn.</summary>
	private void UpdateCardPrompt( string message )
	{
		var isStarting = !session.IsBusy && !string.IsNullOrWhiteSpace( message );
		session.UpdateCardPrompt( message );
		if ( isStarting )
			_transcript.ScrollToEnd();
	}

	/// <summary>Shows one tool call to its module's card from what this chat recorded of it.</summary>
	/// <param name="chat">The chat drawing the card.</param>
	/// <param name="callId">The agent's tool-call identifier.</param>
	/// <param name="arguments">The arguments the agent sent.</param>
	/// <param name="status">How far the call has come.</param>
	/// <param name="text">The latest progress or result text.</param>
	/// <param name="details">The latest progress or result details, or undefined.</param>
	private sealed class ChatToolCard( ChatContent chat, string callId, JsonElement arguments, AgentToolStatus status, string text, JsonElement details ) : ToolCardContext
	{
		/// <summary>Gets the owning application.</summary>
		public override LoknApplication App => chat.CardApp;

		/// <summary>Gets the agent's tool-call identifier.</summary>
		public override string CallId => callId;

		/// <summary>Gets the arguments the agent sent.</summary>
		public override JsonElement Arguments => arguments;

		/// <summary>Gets how far the call has come.</summary>
		public override AgentToolStatus Status => status;

		/// <summary>Gets the latest progress or result text.</summary>
		public override string Text => text;

		/// <summary>Gets the latest progress or result details, or undefined.</summary>
		public override JsonElement Details => details;

		/// <summary>Gets this call's view state of the given type, creating it on first use.</summary>
		public override T State<T>()
		{
			if ( chat._cardStates.TryGetValue( (callId, typeof( T )), out var state ) )
				return (T)state;

			var created = new T();
			chat._cardStates[(callId, typeof( T ))] = created;
			return created;
		}

		/// <summary>Applies a view change and redraws the card.</summary>
		public override void Run( Action change ) => chat.UpdateCard( callId, change );

		/// <summary>Sends the agent a message from the card.</summary>
		public override void Prompt( string message ) => chat.UpdateCardPrompt( message );
	}
}
