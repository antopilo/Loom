namespace Lokn.Core;

/// <summary>Names the window areas the shell builds from module contributions; register them with <see cref="ModuleBuilder.Slot"/>.</summary>
public static class ShellSlots
{
	/// <summary>The titlebar's left side, after the shell's workspace actions.</summary>
	public const string TitleLeft = "shell.title.left";

	/// <summary>The titlebar's right side, before the notification bell and window controls.</summary>
	public const string TitleRight = "shell.title.right";

	/// <summary>The fixed sidebar column; sections stack top to bottom above the profile switcher.</summary>
	public const string Sidebar = "shell.sidebar";

	/// <summary>The tab strip under the titlebar, shown instead of the sidebar when the user picks tabs navigation; contributions fill it left to right before the profile switcher.</summary>
	public const string Tabs = "shell.tabs";

	/// <summary>The status bar's left side, after the shell's panel and module counts.</summary>
	public const string StatusLeft = "shell.status.left";

	/// <summary>The status bar's right side, before the shell's hint.</summary>
	public const string StatusRight = "shell.status.right";
}

/// <summary>Describes one live slot contribution.</summary>
/// <param name="Id">A unique identifier for this contribution's lifetime, for keying its mount.</param>
/// <param name="Slot">The slot the contribution fills.</param>
/// <param name="ModuleId">The module that contributed it.</param>
/// <param name="Order">Its place in the slot; lower comes first.</param>
/// <param name="Content">The live content, created after its module starts and closed when the module unloads.</param>
public sealed record SlotItem( Guid Id, string Slot, string ModuleId, int Order, PanelContent Content );

/// <summary>Holds the live slot contributions of loaded modules, for the shell's areas and for slots modules render themselves.</summary>
public sealed class SlotRegistry
{
	/// <summary>Stores contributions in load order.</summary>
	private readonly List<SlotItem> _items = [];

	/// <summary>Gets the contributions to one slot, by order and then load order.</summary>
	/// <param name="slot">The slot, such as <see cref="ShellSlots.Sidebar"/> or a module's own <c>module.name</c>.</param>
	public IReadOnlyList<SlotItem> Get( string slot ) => _items
		.Where( item => item.Slot == slot )
		.OrderBy( item => item.Order )
		.ToArray();

	/// <summary>Adds a created contribution.</summary>
	internal void Add( SlotItem item ) => _items.Add( item );

	/// <summary>Removes and returns a module's contributions.</summary>
	internal IReadOnlyList<SlotItem> RemoveModule( string moduleId )
	{
		var removed = _items.Where( item => item.ModuleId == moduleId ).ToArray();
		_items.RemoveAll( item => item.ModuleId == moduleId );
		return removed;
	}
}

/// <summary>Describes an extension point a module declares for other modules to contribute to.</summary>
/// <param name="Id">The qualified identifier, <c>module.name</c>.</param>
/// <param name="ContributionType">The type every contribution must have.</param>
/// <param name="ModuleId">The declaring module.</param>
public sealed record ExtensionPointInfo( string Id, Type ContributionType, string ModuleId );

/// <summary>Holds the extension points modules declare and the values modules contribute to them.</summary>
public sealed class ExtensionRegistry
{
	/// <summary>Stores declared points by identifier.</summary>
	private readonly Dictionary<string, ExtensionPointInfo> _points = new( StringComparer.Ordinal );

	/// <summary>Stores contributions in load order.</summary>
	private readonly List<(string Point, string ModuleId, object Value)> _contributions = [];

	/// <summary>Gets the declared extension points.</summary>
	public IReadOnlyList<ExtensionPointInfo> Points => _points.Values.ToArray();

	/// <summary>Gets the contributions to a point in load order; a contribution made before its point was declared is included when its type matches.</summary>
	/// <param name="point">The qualified point identifier, <c>module.name</c>.</param>
	public IReadOnlyList<T> Get<T>( string point ) where T : class => _contributions
		.Where( item => item.Point == point )
		.Select( item => item.Value )
		.OfType<T>()
		.ToArray();

	/// <summary>Rejects a declaration of a taken point or a contribution of the wrong type before anything registers.</summary>
	internal void EnsureAvailable( IReadOnlyList<ExtensionPointInfo> points, IReadOnlyList<(string Point, object Value)> contributions )
	{
		foreach ( var point in points )
		{
			if ( _points.TryGetValue( point.Id, out var existing ) )
				throw new InvalidOperationException( $"Extension point {point.Id} is already declared by {existing.ModuleId}." );
		}

		foreach ( var (id, value) in contributions )
		{
			var point = points.FirstOrDefault( candidate => candidate.Id == id ) ?? _points.GetValueOrDefault( id );
			if ( point is not null && !point.ContributionType.IsInstanceOfType( value ) )
				throw new InvalidOperationException( $"Extension point {id} takes {point.ContributionType.Name}, not {value.GetType().Name}." );
		}
	}

	/// <summary>Adds a module's points and contributions.</summary>
	internal void Register( string moduleId, IReadOnlyList<ExtensionPointInfo> points, IReadOnlyList<(string Point, object Value)> contributions )
	{
		foreach ( var point in points )
			_points.Add( point.Id, point );
		foreach ( var (point, value) in contributions )
			_contributions.Add( (point, moduleId, value) );
	}

	/// <summary>Removes a module's points and contributions; contributions other modules made to its points stay for its next load.</summary>
	internal void RemoveModule( string moduleId )
	{
		foreach ( var point in _points.Values.Where( point => point.ModuleId == moduleId ).ToArray() )
			_points.Remove( point.Id );
		_contributions.RemoveAll( item => item.ModuleId == moduleId );
	}
}

/// <summary>Holds the services loaded modules publish, one implementation per contract type.</summary>
/// <remarks>Resolve a service on each use rather than keeping it: a reload replaces the implementation.</remarks>
public sealed class ServiceRegistry
{
	/// <summary>Stores implementations by contract type.</summary>
	private readonly Dictionary<Type, (string ModuleId, object Service)> _services = [];

	/// <summary>Gets the implementation of a contract, or null when no loaded module publishes it.</summary>
	public T? Get<T>() where T : class => _services.TryGetValue( typeof( T ), out var entry ) ? (T)entry.Service : null;

	/// <summary>Rejects a contract another module already publishes.</summary>
	internal void EnsureAvailable( IReadOnlyList<(Type Contract, object Service)> services )
	{
		foreach ( var (contract, _) in services )
		{
			if ( _services.TryGetValue( contract, out var existing ) )
				throw new InvalidOperationException( $"Service {contract.Name} is already published by {existing.ModuleId}." );
		}
	}

	/// <summary>Adds a module's services.</summary>
	internal void Register( string moduleId, IReadOnlyList<(Type Contract, object Service)> services )
	{
		foreach ( var (contract, service) in services )
			_services.Add( contract, (moduleId, service) );
	}

	/// <summary>Removes a module's services.</summary>
	internal void RemoveModule( string moduleId )
	{
		foreach ( var contract in _services.Where( entry => entry.Value.ModuleId == moduleId ).Select( entry => entry.Key ).ToArray() )
			_services.Remove( contract );
	}
}

/// <summary>Describes a followed <c>lokn://module/route/rest</c> link for the route that opens it.</summary>
/// <param name="App">The application.</param>
/// <param name="Uri">The full link.</param>
/// <param name="Path">The path after the route name, without a leading slash, unescaped; empty when there is none.</param>
/// <param name="SourcePanelId">The panel the link was followed in, or null.</param>
public sealed record RouteRequest( LoknApplication App, Uri Uri, string Path, Guid? SourcePanelId )
{
	/// <summary>Gets a query parameter, unescaped, or null.</summary>
	public string? Query( string name )
	{
		foreach ( var pair in Uri.Query.TrimStart( '?' ).Split( '&', StringSplitOptions.RemoveEmptyEntries ) )
		{
			var parts = pair.Split( '=', 2 );
			if ( Uri.UnescapeDataString( parts[0] ) == name )
				return parts.Length > 1 ? Uri.UnescapeDataString( parts[1] ) : "";
		}
		return null;
	}
}

/// <summary>Builds and parses the <c>lokn://</c> links modules open with <see cref="ModuleBuilder.Route"/>.</summary>
public static class LoknLinks
{
	/// <summary>The scheme of links routed to modules.</summary>
	public const string Scheme = "lokn";

	/// <summary>Builds a link to a module's route.</summary>
	/// <param name="moduleId">The module that registered the route.</param>
	/// <param name="route">The route name.</param>
	/// <param name="path">Segments after the route name, each escaped.</param>
	public static Uri Create( string moduleId, string route, params string[] path )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( moduleId );
		ArgumentException.ThrowIfNullOrWhiteSpace( route );
		var rest = string.Concat( path.Select( segment => "/" + Uri.EscapeDataString( segment ) ) );
		return new( $"{Scheme}://{moduleId}/{Uri.EscapeDataString( route )}{rest}" );
	}

	/// <summary>Splits a link into its module, route and remaining path, or returns null when it is not a <c>lokn://</c> link.</summary>
	internal static (string ModuleId, string Route, string Path)? Parse( Uri uri )
	{
		if ( !uri.IsAbsoluteUri || uri.Scheme != Scheme || uri.Host.Length == 0 )
			return null;

		var segments = uri.AbsolutePath.Trim( '/' ).Split( '/', 2 );
		if ( segments[0].Length == 0 )
			return null;

		return (uri.Host, Uri.UnescapeDataString( segments[0] ), segments.Length > 1 ? Uri.UnescapeDataString( segments[1] ) : "");
	}
}
