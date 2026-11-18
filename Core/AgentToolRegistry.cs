using System.Text.Json;
using System.Text.RegularExpressions;
using Goo;

namespace Lokn.Core;

/// <summary>Holds the tools modules offer to agents, such as an Omp chat, and runs them on the UI thread.</summary>
/// <remarks>
/// A tool name is its module identifier and local identifier joined by an underscore, reduced to the characters
/// model providers accept. Agents call tools through a host bridge; Lokn itself never calls them.
/// </remarks>
public sealed partial class AgentToolRegistry
{
	/// <summary>Defines the longest tool name model providers accept.</summary>
	private const int MaximumNameLength = 64;

	/// <summary>Stores the owning application.</summary>
	private readonly LoknApplication _app;

	/// <summary>Stores registrations by tool name.</summary>
	private readonly Dictionary<string, RegisteredTool> _tools = new( StringComparer.Ordinal );

	/// <summary>Stores the cards modules draw for calls to their tools, by tool name.</summary>
	private readonly Dictionary<string, RegisteredCard> _cards = new( StringComparer.Ordinal );

	/// <summary>Stores cards that threw, so a broken card is reported once rather than on every redraw.</summary>
	private readonly HashSet<string> _failedCards = new( StringComparer.Ordinal );

	/// <summary>Stores the native window's asynchronous UI dispatcher while attached.</summary>
	private Func<Func<Task<AgentToolResult>>, Task<AgentToolResult>>? _dispatch;

	/// <summary>Gets the registered tool metadata, ordered by name.</summary>
	public IReadOnlyList<AgentToolInfo> Items => [.. _tools.Values.Select( tool => tool.Definition.Info ).OrderBy( info => info.Name, StringComparer.Ordinal )];

	/// <summary>Reports that tools were added or removed, so bridges can tell their agents.</summary>
	public event Action? Changed;

	/// <summary>Creates the application tool registry.</summary>
	internal AgentToolRegistry( LoknApplication app ) => _app = app;

	/// <summary>Runs a tool on the UI thread and contains its failures as error results; callable from any thread.</summary>
	/// <param name="name">The registered tool name.</param>
	/// <param name="arguments">The arguments the agent sent, normally a JSON object.</param>
	/// <param name="caller">The panel whose agent made the call, when there is one.</param>
	/// <param name="cancellation">Cancelled when the agent abandons the call.</param>
	/// <param name="callId">The agent's identifier for the call, which its tool card sees too; empty when the agent has none.</param>
	/// <param name="report">Receives the tool's progress while it runs, from any thread; null when the caller shows none.</param>
	public async Task<AgentToolResult> InvokeAsync( string name, JsonElement arguments, Guid? caller, CancellationToken cancellation, string callId = "", Action<AgentToolResult>? report = null )
	{
		var context = new AgentToolContext( _app, caller, cancellation ) { CallId = callId, Reporter = report };
		if ( _dispatch is null )
			return await InvokeCoreAsync( name, arguments, context );

		try
		{
			return await _dispatch( () => InvokeCoreAsync( name, arguments, context ) );
		}
		catch ( OperationCanceledException )
		{
			return AgentToolResult.Error( "The call was cancelled." );
		}
	}

	/// <summary>Reports whether a module draws its own card for calls to a tool.</summary>
	/// <param name="name">The tool name the agent called.</param>
	public bool HasCard( string name ) => _cards.TryGetValue( name, out var card ) && !card.Lifetime.IsCancellationRequested;

	/// <summary>Counts changes to the registered cards, so a chat redraws calls whose card a module added, replaced or removed.</summary>
	public int CardVersion { get; private set; }

	/// <summary>Builds a module's card for one call to its tool on the UI thread, or null when it has none or the card failed.</summary>
	/// <param name="name">The tool name the agent called.</param>
	/// <param name="context">The call as the chat shows it, and the card's view state and actions.</param>
	/// <remarks>A card that throws is reported once as a shell error and left to the chat's ordinary tool line.</remarks>
	public Blob? BuildCard( string name, ToolCardContext context )
	{
		if ( !_cards.TryGetValue( name, out var card ) || card.Lifetime.IsCancellationRequested || _failedCards.Contains( name ) )
			return null;

		try
		{
			return card.Definition.Build( context );
		}
		catch ( Exception error )
		{
			_failedCards.Add( name );
			_app.ReportError( $"{name} card", error );
			return null;
		}
	}

	/// <summary>Builds the qualified name of a module's tool, or throws when it cannot be a provider tool name.</summary>
	internal static string ComputeName( string moduleId, string id )
	{
		var name = InvalidCharacters().Replace( $"{moduleId}_{id}", "_" );
		if ( name.Length > MaximumNameLength )
			throw new ArgumentException( $"Tool names are limited to {MaximumNameLength} characters: {name}", nameof( id ) );

		return name;
	}

	/// <summary>Attaches or detaches the native execution dispatcher.</summary>
	internal void BindDispatcher( Func<Func<Task<AgentToolResult>>, Task<AgentToolResult>>? dispatch ) => _dispatch = dispatch;

	/// <summary>Rejects duplicate tool names, and cards for tools their module does not offer, before registration.</summary>
	internal void EnsureAvailable( IReadOnlyList<AgentToolDefinition> definitions, IReadOnlyList<ToolCardDefinition> cards )
	{
		var names = _tools.Keys.ToHashSet( StringComparer.Ordinal );
		foreach ( var definition in definitions )
		{
			if ( !names.Add( definition.Info.Name ) )
				throw new InvalidOperationException( $"Duplicate agent tool: {definition.Info.Name}" );
		}

		var drawn = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var card in cards )
		{
			if ( !definitions.Any( definition => definition.Info.Name == card.Name ) )
				throw new InvalidOperationException( $"A tool card needs its module's tool: {card.Name}" );
			if ( !drawn.Add( card.Name ) )
				throw new InvalidOperationException( $"Duplicate tool card: {card.Name}" );
		}
	}

	/// <summary>Commits contributions after all module registrations pass validation.</summary>
	internal void Register( IReadOnlyList<AgentToolDefinition> definitions, IReadOnlyList<ToolCardDefinition> cards, CancellationToken lifetime )
	{
		foreach ( var definition in definitions )
			_tools.Add( definition.Info.Name, new( definition, lifetime ) );
		foreach ( var card in cards )
		{
			_cards.Add( card.Name, new( card, lifetime ) );
			_failedCards.Remove( card.Name );
		}
		if ( cards.Count > 0 )
			CardVersion++;
		if ( definitions.Count > 0 )
			Changed?.Invoke();
	}

	/// <summary>Removes every tool and tool card owned by a module.</summary>
	internal void RemoveModule( string moduleId )
	{
		var names = _tools.Values.Where( tool => tool.Definition.Info.ModuleId == moduleId ).Select( tool => tool.Definition.Info.Name ).ToArray();
		foreach ( var name in names )
		{
			_tools.Remove( name );
			if ( _cards.Remove( name ) )
				CardVersion++;
		}
		if ( names.Length > 0 )
			Changed?.Invoke();
	}

	/// <summary>Runs a tool where its caller placed it, turning exceptions into error results.</summary>
	private async Task<AgentToolResult> InvokeCoreAsync( string name, JsonElement arguments, AgentToolContext context )
	{
		if ( !_tools.TryGetValue( name, out var tool ) || tool.Lifetime.IsCancellationRequested )
			return AgentToolResult.Error( $"Lokn has no tool named {name}." );

		using var linked = CancellationTokenSource.CreateLinkedTokenSource( tool.Lifetime, context.Cancellation );
		try
		{
			return await tool.Definition.Execute( context with { Cancellation = linked.Token }, arguments );
		}
		catch ( OperationCanceledException ) when ( linked.IsCancellationRequested )
		{
			return AgentToolResult.Error( "The call was cancelled." );
		}
		catch ( Exception error ) when ( error is ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or JsonException )
		{
			// Bad arguments and refused actions are the agent's to correct, not shell errors.
			return AgentToolResult.Error( error.Message );
		}
		catch ( Exception error )
		{
			_app.ReportError( name, error );
			return AgentToolResult.Error( error.Message );
		}
	}

	/// <summary>Matches characters a provider tool name cannot contain.</summary>
	[GeneratedRegex( "[^A-Za-z0-9_-]" )]
	private static partial Regex InvalidCharacters();

	/// <summary>Stores one registration with its owning module lifetime.</summary>
	private sealed record RegisteredTool( AgentToolDefinition Definition, CancellationToken Lifetime );

	/// <summary>Stores one tool card with its owning module lifetime.</summary>
	private sealed record RegisteredCard( ToolCardDefinition Definition, CancellationToken Lifetime );
}

/// <summary>Describes a tool offered to agents.</summary>
/// <param name="Name">The qualified tool name: module identifier, underscore, local identifier.</param>
/// <param name="Description">What the tool does and when to use it, written for the model.</param>
/// <param name="Parameters">The JSON Schema of the tool's arguments object.</param>
/// <param name="ModuleId">The owning module identifier.</param>
public sealed record AgentToolInfo( string Name, string Description, JsonElement Parameters, string ModuleId );

/// <summary>Provides a running tool with the application, the calling panel, cancellation, and a way to show progress.</summary>
/// <param name="App">The owning application on its UI thread.</param>
/// <param name="Caller">The panel whose agent made the call, such as a chat, when there is one.</param>
/// <param name="Cancellation">Cancelled when the agent abandons the call or the module unloads.</param>
public sealed record AgentToolContext( LoknApplication App, Guid? Caller, CancellationToken Cancellation )
{
	/// <summary>Gets the agent's identifier for this call, which the call's card sees as <see cref="ToolCardContext.CallId"/>; empty when the agent has none.</summary>
	public string CallId { get; init; } = "";

	/// <summary>Stores where progress goes, such as the agent bridge; null when the caller shows none.</summary>
	internal Action<AgentToolResult>? Reporter { get; init; }

	/// <summary>Shows progress in the call's card while the tool runs; callable from any thread, and the latest report wins.</summary>
	/// <param name="progress">The text and details so far; the agent only sees the final result.</param>
	public void Report( AgentToolResult progress ) => Reporter?.Invoke( progress );
}

/// <summary>Holds the text an agent receives from a tool, whether the call failed, and details for the call's card.</summary>
/// <param name="Text">The result text: prose, or JSON for structured data.</param>
/// <param name="IsError">Whether the call failed; the agent sees the text as the error.</param>
/// <param name="Details">Structured data for the call's card, kept with the conversation; serialized with camel-case names and omitted nulls. Omp drops it from a result with <paramref name="IsError"/> set, so a card that shows failures needs a successful result.</param>
public sealed record AgentToolResult( string Text, bool IsError = false, object? Details = null )
{
	/// <summary>Builds a failed result whose text explains what went wrong.</summary>
	public static AgentToolResult Error( string message ) => new( message, IsError: true );

	/// <summary>Builds a successful result holding a value serialized as indented JSON.</summary>
	public static AgentToolResult Json( object value ) => new( JsonSerializer.Serialize( value, _json ) );

	/// <summary>Serializes <see cref="Details"/> the way <see cref="Json"/> serializes values, or null when there are none.</summary>
	public JsonElement? SerializeDetails() => Details is null ? null : JsonSerializer.SerializeToElement( Details, _json );

	/// <summary>Serializes structured results readably, with camel-case names.</summary>
	private static readonly JsonSerializerOptions _json = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
		Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter( JsonNamingPolicy.CamelCase ) },
	};
}

/// <summary>Stores a registered tool's metadata and handler.</summary>
internal sealed record AgentToolDefinition( AgentToolInfo Info, Func<AgentToolContext, JsonElement, Task<AgentToolResult>> Execute );
