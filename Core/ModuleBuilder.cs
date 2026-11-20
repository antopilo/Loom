using System.Text.Json;
using Goo;

namespace Lokn.Core;

/// <summary>Stages namespaced contributions atomically for one module.</summary>
public sealed class ModuleBuilder
{
	/// <summary>Stores the owning module identifier.</summary>
	private readonly string _moduleId;

	/// <summary>Stores staged panel definitions.</summary>
	internal List<PanelDefinition> Panels { get; } = [];

	/// <summary>Stores staged command definitions.</summary>
	internal List<CommandDefinition> Commands { get; } = [];

	/// <summary>Stores staged typed setting definitions.</summary>
	internal List<SettingDefinition> Settings { get; } = [];

	/// <summary>Stores staged agent tool definitions.</summary>
	internal List<AgentToolDefinition> Tools { get; } = [];

	/// <summary>Stores staged tool cards.</summary>
	internal List<ToolCardDefinition> ToolCards { get; } = [];

	/// <summary>Tracks whether registration has finished.</summary>
	private bool _isSealed;

	/// <summary>Creates a staging area for one module.</summary>
	internal ModuleBuilder( string moduleId ) => _moduleId = moduleId;

	/// <summary>Adds a typed setting whose identifier belongs to this module's namespace.</summary>
	public ModuleBuilder Setting( SettingDefinition setting )
	{
		ArgumentNullException.ThrowIfNull( setting );
		EnsureWritable( setting.Id, setting.Title );
		if ( !setting.Id.StartsWith( _moduleId + ".", StringComparison.Ordinal ) )
			throw new ArgumentException( "Setting identifiers must start with their owning module ID.", nameof( setting ) );

		Settings.Add( setting );
		return this;
	}

	/// <summary>Adds a panel type and its automatic Open command, which opens it on the current host.</summary>
	/// <param name="id">The local identifier.</param>
	/// <param name="title">The default pane title.</param>
	/// <param name="create">Creates the content of each opened panel.</param>
	/// <param name="glyph">The type's tiny icon, a Segoe Fluent Icons code point, shown on the tasks that hold it; null has none.</param>
	/// <param name="onHost">Whether each panel works on one host, This PC or a paired host (<see cref="PanelTypeInfo.IsHostBound"/>), and reaches it
	/// through <see cref="PanelContext.Host"/> and <see cref="PanelContext.Workspace"/>.</param>
	public ModuleBuilder Panel( string id, string title, Func<PanelContext, PanelContent> create, string? glyph = null, bool onHost = false )
	{
		EnsureWritable( id, title );
		ArgumentNullException.ThrowIfNull( create );
		var qualifiedId = $"{_moduleId}.{id}";
		Panels.Add( new( new( qualifiedId, title, _moduleId ) { Glyph = glyph, IsHostBound = onHost }, create ) );
		return Command( $"open.{id}", $"Open {title}", context => context.App.Panels.Open( qualifiedId ) );
	}

	/// <summary>Restores saved panels of a former panel type, such as one another module registered before this one took the panel over, as this module's panel type <paramref name="id"/>.</summary>
	/// <param name="formerTypeId">The fully qualified identifier saved layouts may still hold, such as <c>conversations.git</c>.</param>
	/// <param name="id">The local identifier of a panel type this module registered earlier in <see cref="ILoknModule.Register"/>.</param>
	/// <remarks>The panel's factory receives the old panel's captured state, so keep reading the state the former type saved.</remarks>
	public ModuleBuilder PanelAlias( string formerTypeId, string id )
	{
		EnsureWritable( formerTypeId, id );
		var qualifiedId = $"{_moduleId}.{id}";
		var index = Panels.FindIndex( panel => panel.Info.Id == qualifiedId );
		if ( index < 0 )
			throw new InvalidOperationException( $"Register panel type {qualifiedId} before its former identifier." );
		if ( formerTypeId == qualifiedId )
			throw new ArgumentException( "A panel type's former identifier must differ from its identifier.", nameof( formerTypeId ) );

		Panels[index] = Panels[index] with { FormerIds = [.. Panels[index].FormerIds, formerTypeId] };
		return this;
	}

	/// <summary>Adds a panel type without an automatic Open command, for use with custom command logic.</summary>
	internal ModuleBuilder PanelOnly( string id, string title, Func<PanelContext, PanelContent> create )
	{
		EnsureWritable( id, title );
		ArgumentNullException.ThrowIfNull( create );
		var qualifiedId = $"{_moduleId}.{id}";
		Panels.Add( new( new( qualifiedId, title, _moduleId ), create ) );
		return this;
	}


	/// <summary>Adds a synchronous command with an optional shortcut and availability predicate.</summary>
	public ModuleBuilder Command(
		string id,
		string title,
		Action<CommandContext> execute,
		CommandShortcut? shortcut = null,
		Func<CommandContext, bool>? canExecute = null )
	{
		ArgumentNullException.ThrowIfNull( execute );
		return CommandAsync( id, title, context =>
		{
			execute( context );
			return Task.CompletedTask;
		}, shortcut, canExecute );
	}

	/// <summary>Adds an asynchronous command that receives module cancellation.</summary>
	public ModuleBuilder CommandAsync(
		string id,
		string title,
		Func<CommandContext, Task> execute,
		CommandShortcut? shortcut = null,
		Func<CommandContext, bool>? canExecute = null )
	{
		EnsureWritable( id, title );
		ArgumentNullException.ThrowIfNull( execute );
		Commands.Add( new( new( $"{_moduleId}.{id}", title, _moduleId, shortcut ), execute, canExecute ) );
		return this;
	}

	/// <summary>Adds a synchronous tool agents can call, named <c>{module}_{id}</c>; the handler runs on the UI thread.</summary>
	/// <param name="id">The local identifier, such as <c>open_panel</c>.</param>
	/// <param name="description">What the tool does and when to use it, written for the model.</param>
	/// <param name="parameters">The JSON Schema of the arguments object.</param>
	/// <param name="execute">Runs the call with its arguments; throw <see cref="ArgumentException"/> or <see cref="InvalidOperationException"/> to refuse it.</param>
	public ModuleBuilder Tool( string id, string description, string parameters, Func<AgentToolContext, JsonElement, AgentToolResult> execute )
	{
		ArgumentNullException.ThrowIfNull( execute );
		return ToolAsync( id, description, parameters, ( context, arguments ) => Task.FromResult( execute( context, arguments ) ) );
	}

	/// <summary>Adds an asynchronous tool agents can call; the handler starts on the UI thread and receives the call's cancellation.</summary>
	/// <param name="id">The local identifier, such as <c>open_panel</c>.</param>
	/// <param name="description">What the tool does and when to use it, written for the model.</param>
	/// <param name="parameters">The JSON Schema of the arguments object.</param>
	/// <param name="execute">Runs the call with its arguments; throw <see cref="ArgumentException"/> or <see cref="InvalidOperationException"/> to refuse it.</param>
	public ModuleBuilder ToolAsync( string id, string description, string parameters, Func<AgentToolContext, JsonElement, Task<AgentToolResult>> execute )
	{
		EnsureWritable( id, description );
		ArgumentNullException.ThrowIfNull( execute );
		using var schema = JsonDocument.Parse( parameters );
		if ( schema.RootElement.ValueKind != JsonValueKind.Object )
			throw new ArgumentException( "Tool parameters must be a JSON Schema object.", nameof( parameters ) );

		Tools.Add( new( new( AgentToolRegistry.ComputeName( _moduleId, id ), description, schema.RootElement.Clone(), _moduleId ), execute ) );
		return this;
	}

	/// <summary>Draws calls to one of this module's tools as its own card in a chat, in place of the chat's tool line.</summary>
	/// <param name="id">The local identifier of a tool this module registers.</param>
	/// <param name="build">Builds the card on the UI thread on every redraw of the call; keep it cheap, and draw only from the context.</param>
	/// <remarks>Report progress from the tool with <see cref="AgentToolContext.Report"/> and finish with <see cref="AgentToolResult.Details"/>; the card reads both as <see cref="ToolCardContext.Details"/>.</remarks>
	public ModuleBuilder ToolCard( string id, Func<ToolCardContext, Blob> build )
	{
		EnsureWritable( id, id );
		ArgumentNullException.ThrowIfNull( build );
		ToolCards.Add( new( AgentToolRegistry.ComputeName( _moduleId, id ), build ) );
		return this;
	}

	/// <summary>Opens links the user follows, such as web addresses in a chat; return false to leave a link to the next handler.</summary>
	/// <remarks>Handlers run on the UI thread in module load order. A link no handler opens goes to the system's default browser.</remarks>
	public ModuleBuilder LinkHandler( Func<LinkRequest, bool> open )
	{
		ArgumentNullException.ThrowIfNull( open );
		if ( _isSealed )
			throw new InvalidOperationException( "Module registration has finished." );
		if ( Links is not null )
			throw new InvalidOperationException( "A module registers at most one link handler." );

		Links = open;
		return this;
	}

	/// <summary>Gets the staged link handler, or null.</summary>
	internal Func<LinkRequest, bool>? Links
	{
		get; private set;
	}

	/// <summary>Adds content to a window area such as <see cref="ShellSlots.Sidebar"/>, or to a slot another module renders, such as <c>module.name</c>.</summary>
	/// <param name="slot">The slot; any number of modules can fill one.</param>
	/// <param name="order">The content's place in the slot; lower comes first.</param>
	/// <param name="create">Creates the content after the module starts; it lives until the module unloads.</param>
	public ModuleBuilder Slot( string slot, int order, Func<PanelContext, PanelContent> create )
	{
		EnsureWritable( slot, slot );
		ArgumentNullException.ThrowIfNull( create );
		Slots.Add( (slot, order, create) );
		return this;
	}

	/// <summary>Gets the staged slot contributions.</summary>
	internal List<(string Slot, int Order, Func<PanelContext, PanelContent> Create)> Slots { get; } = [];

	/// <summary>Declares a point where other modules contribute values of a type, read with <see cref="ExtensionRegistry.Get{T}"/> as <c>module.id</c>.</summary>
	/// <typeparam name="T">The type each contribution must have; share it through a project both modules reference.</typeparam>
	/// <param name="id">The point's local identifier.</param>
	public ModuleBuilder ExtensionPoint<T>( string id ) where T : class
	{
		EnsureWritable( id, id );
		Points.Add( new( $"{_moduleId}.{id}", typeof( T ), _moduleId ) );
		return this;
	}

	/// <summary>Gets the staged extension points.</summary>
	internal List<ExtensionPointInfo> Points { get; } = [];

	/// <summary>Contributes a value to another module's extension point; it is withdrawn when this module unloads.</summary>
	/// <param name="point">The qualified point identifier, <c>module.id</c>.</param>
	/// <param name="value">The contribution.</param>
	public ModuleBuilder Contribute<T>( string point, T value ) where T : class
	{
		EnsureWritable( point, point );
		ArgumentNullException.ThrowIfNull( value );
		Contributions.Add( (point, value) );
		return this;
	}

	/// <summary>Gets the staged contributions.</summary>
	internal List<(string Point, object Value)> Contributions { get; } = [];

	/// <summary>Publishes an implementation of a contract that other modules resolve with <see cref="ServiceRegistry.Get{T}"/>.</summary>
	/// <typeparam name="T">The contract; one loaded module publishes each.</typeparam>
	/// <param name="service">The implementation, withdrawn when this module unloads.</param>
	public ModuleBuilder Service<T>( T service ) where T : class
	{
		if ( _isSealed )
			throw new InvalidOperationException( "Module registration has finished." );
		ArgumentNullException.ThrowIfNull( service );
		if ( Services.Any( entry => entry.Contract == typeof( T ) ) )
			throw new InvalidOperationException( $"A module publishes {typeof( T ).Name} at most once." );

		Services.Add( (typeof( T ), service) );
		return this;
	}

	/// <summary>Gets the staged services.</summary>
	internal List<(Type Contract, object Service)> Services { get; } = [];

	/// <summary>Opens <c>lokn://module/name/…</c> links, built with <see cref="LoknLinks.Create"/>, from notifications, chats and other modules.</summary>
	/// <param name="name">The route name, the link's first path segment.</param>
	/// <param name="open">Opens the link on the UI thread.</param>
	public ModuleBuilder Route( string name, Action<RouteRequest> open )
	{
		EnsureWritable( name, name );
		ArgumentNullException.ThrowIfNull( open );
		if ( !Routes.TryAdd( name, open ) )
			throw new InvalidOperationException( $"Route {name} is registered twice." );
		return this;
	}

	/// <summary>Gets the staged routes by name.</summary>
	internal Dictionary<string, Action<RouteRequest>> Routes { get; } = new( StringComparer.Ordinal );

	/// <summary>Lists this module's items in the Search palette under a type, one of its tabs, such as <c>tasks</c>; the provider's identifier is <c>module.type</c>.</summary>
	/// <param name="type">The type's key, which <c>/type</c> in the palette filters by: letters, digits, <c>-</c> and <c>_</c>. Modules listing the same type share its tab.</param>
	/// <param name="title">The type's name, such as Tasks.</param>
	/// <param name="order">The type's place among the tabs; lower comes first. The lowest order among a type's providers names and places it.</param>
	/// <param name="search">Lists the items on the UI thread each time the palette builds; keep it cheap. The palette ranks and filters what it returns,
	/// so most providers ignore <see cref="SearchContext.Query"/>.</param>
	public ModuleBuilder SearchProvider( string type, string title, int order, Func<SearchContext, IEnumerable<SearchItem>> search )
	{
		EnsureWritable( type, title );
		ArgumentNullException.ThrowIfNull( search );
		if ( !type.All( character => char.IsAsciiLetterOrDigit( character ) || character is '-' or '_' ) )
			throw new ArgumentException( "Search types use letters, digits, - and _ only.", nameof( type ) );

		SearchProviders.Add( new( new( $"{_moduleId}.{type}", type, title, order, _moduleId ), search ) );
		return this;
	}

	/// <summary>Gets the staged search providers.</summary>
	internal List<SearchProviderDefinition> SearchProviders { get; } = [];

	/// <summary>Prevents modules from adding untracked contributions after registration.</summary>
	internal void Seal() => _isSealed = true;

	/// <summary>Checks registration identifiers and the staging lifetime.</summary>
	private void EnsureWritable( string id, string title )
	{
		if ( _isSealed )
			throw new InvalidOperationException( "Module registration has finished." );

		ArgumentException.ThrowIfNullOrWhiteSpace( id );
		ArgumentException.ThrowIfNullOrWhiteSpace( title );
	}
}

/// <summary>Stores a registered panel factory.</summary>
internal sealed record PanelDefinition( PanelTypeInfo Info, Func<PanelContext, PanelContent> Create )
{
	/// <summary>Gets the identifiers saved layouts may hold for this panel type from before it was renamed.</summary>
	public IReadOnlyList<string> FormerIds { get; init; } = [];
}

/// <summary>Stores command execution and its availability predicate.</summary>
internal sealed record CommandDefinition(
	CommandInfo Info,
	Func<CommandContext, Task> Execute,
	Func<CommandContext, bool>? CanExecute );
