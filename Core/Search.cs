namespace Lokn.Core;

/// <summary>Describes one thing the Search palette finds, such as a task, a chat, a panel or a command.</summary>
/// <param name="Id">Identifies the item within its provider from one search to the next, so the palette can list it among recents.</param>
/// <param name="Title">The name shown and matched first.</param>
/// <param name="Run">Opens or runs the item on the UI thread, after the palette closes.</param>
public sealed record SearchItem( string Id, string Title, Action Run )
{
	/// <summary>Gets the quiet text on the right of the row, such as a project name or a shortcut; matched after the title and keywords.</summary>
	public string? Detail { get; init; }

	/// <summary>Gets a Segoe Fluent Icons character drawn as the item's icon, or null for a plain dot.</summary>
	public string? Glyph { get; init; }

	/// <summary>Gets further words the item is found by, such as an identifier or a description.</summary>
	public IReadOnlyList<string> Keywords { get; init; } = [];

	/// <summary>Gets the item's other actions, shown in a menu from its ••• button or Alt+Enter.</summary>
	public IReadOnlyList<MenuAction> Actions { get; init; } = [];

	/// <summary>Gets when the user last opened or changed it, which ranks it among recents; null when unknown.</summary>
	public DateTimeOffset? LastUsed { get; init; }
}

/// <summary>Carries one search to a provider registered with <see cref="ModuleBuilder.SearchProvider"/>.</summary>
/// <param name="App">The application.</param>
/// <param name="Query">The text typed after any type filter; empty when the palette shows its overview.</param>
public sealed record SearchContext( LoknApplication App, string Query );

/// <summary>Describes a kind of result, one tab of the Search palette, which one or more modules provide.</summary>
/// <param name="Id">The type's key, such as <c>tasks</c>, which <c>/tasks</c> filters by.</param>
/// <param name="Title">The tab's name, from the provider with the lowest order.</param>
/// <param name="Order">The tab's place; lower comes first.</param>
public sealed record SearchType( string Id, string Title, int Order );

/// <summary>Describes a registered search provider.</summary>
/// <param name="Id">The qualified identifier, <c>module.type</c>.</param>
/// <param name="Type">The type its items belong to.</param>
/// <param name="Title">The type's name.</param>
/// <param name="Order">The type's place among the tabs; lower comes first.</param>
/// <param name="ModuleId">The module that registered it.</param>
public sealed record SearchProviderInfo( string Id, string Type, string Title, int Order, string ModuleId );

/// <summary>Describes one found item with where it came from.</summary>
/// <param name="Item">The item.</param>
/// <param name="ProviderId">The provider that listed it.</param>
/// <param name="Type">The type it belongs to.</param>
/// <param name="Score">How well it matches the query; zero without one.</param>
public sealed record SearchHit( SearchItem Item, string ProviderId, SearchType Type, int Score );

/// <summary>Describes one heading of results and its items.</summary>
/// <param name="Title">The heading, a type's name or Recents.</param>
/// <param name="Type">The type its items belong to, or null for Recents, which mixes them.</param>
/// <param name="Hits">The items, best first.</param>
public sealed record SearchGroup( string Title, SearchType? Type, IReadOnlyList<SearchHit> Hits );

/// <summary>Describes what a query starting with <c>/</c> asks for.</summary>
/// <param name="Type">The type a finished <c>/type </c> prefix picked, or null.</param>
/// <param name="Query">The text to search for: what follows a picked type, else the whole text.</param>
/// <param name="Choices">The types matching a <c>/type</c> prefix still being typed, or null when no type is being chosen.</param>
public sealed record SearchFilter( SearchType? Type, string Query, IReadOnlyList<SearchType>? Choices );

/// <summary>Remembers one item the user ran from the Search palette.</summary>
/// <param name="ProviderId">The provider that listed it.</param>
/// <param name="ItemId">The item's identifier within that provider.</param>
public sealed record SearchRecent( string ProviderId, string ItemId );

/// <summary>Holds the search providers of loaded modules, ranks what they find, and remembers what the user ran, per profile.</summary>
/// <remarks>Providers run on the UI thread each time the palette builds; reading state there rebuilds the palette when it changes.</remarks>
public sealed class SearchRegistry
{
	/// <summary>Defines how many items the overview's Recents heading shows.</summary>
	internal const int RecentLimit = 5;

	/// <summary>Defines how many items each type shows in the overview.</summary>
	internal const int OverviewLimit = 4;

	/// <summary>Defines how many matches each type shows when every type is searched.</summary>
	internal const int MatchLimit = 6;

	/// <summary>Defines how many used items each profile remembers.</summary>
	private const int HistoryLimit = 50;

	/// <summary>Raised when a module asks to open the Search palette, such as from the sidebar's Search button; the shell opens it.</summary>
	public event Action? OpenRequested;

	/// <summary>Asks the shell to open the Search palette with an empty query on the All tab.</summary>
	public void Open() => OpenRequested?.Invoke();

	/// <summary>Defines the score of a title starting with a query word.</summary>
	private const int StartScore = 100;

	/// <summary>Defines the score of a later word of the title starting with a query word.</summary>
	private const int WordScore = 70;

	/// <summary>Defines the score of a title containing a query word anywhere.</summary>
	private const int ContainsScore = 40;

	/// <summary>Defines the score of a title holding a query word's letters in order, the first at a word start.</summary>
	private const int FuzzyScore = 15;

	/// <summary>Defines the share, in percent, of a title match a keyword match scores.</summary>
	private const int KeywordPercent = 60;

	/// <summary>Defines the share, in percent, of a title match a detail match scores.</summary>
	private const int DetailPercent = 30;

	/// <summary>Defines the bonus of a title starting with the whole query, words and spaces together.</summary>
	private const int PhraseBonus = 50;

	/// <summary>Stores the owning application.</summary>
	private readonly LoknApplication _app;

	/// <summary>Stores registered providers in load order.</summary>
	private readonly List<SearchProviderDefinition> _providers = [];

	/// <summary>Stores each profile's used items, newest first.</summary>
	private readonly Dictionary<SettingsProfile, List<SearchRecent>> _recents = [];

	/// <summary>Stores the providers whose last search failed, so a failure is reported once rather than on every build.</summary>
	private readonly HashSet<string> _failed = new( StringComparer.Ordinal );

	/// <summary>Creates the application's search registry.</summary>
	internal SearchRegistry( LoknApplication app ) => _app = app;

	/// <summary>Gets the registered providers by order, then load order.</summary>
	public IReadOnlyList<SearchProviderInfo> Providers => _providers.Select( provider => provider.Info ).OrderBy( info => info.Order ).ToArray();

	/// <summary>Gets every type a loaded provider lists, by order.</summary>
	public IReadOnlyList<SearchType> Types => Providers
		.GroupBy( info => info.Type, StringComparer.Ordinal )
		.Select( group => new SearchType( group.Key, group.First().Title, group.First().Order ) )
		.ToArray();

	/// <summary>Gets the active profile's used items, newest first.</summary>
	public IReadOnlyList<SearchRecent> Recents => _recents.TryGetValue( _app.Settings.ActiveProfile, out var recents ) ? recents.ToArray() : [];

	/// <summary>Gets the types that list something now, the palette's tabs; a type whose providers find nothing is left out.</summary>
	public IReadOnlyList<SearchType> ComputeTypes()
	{
		var listed = Collect( "", null ).Select( hit => hit.Type.Id ).ToHashSet( StringComparer.Ordinal );
		return Types.Where( type => listed.Contains( type.Id ) ).ToArray();
	}

	/// <summary>Searches every type, or one, and groups what matches.</summary>
	/// <param name="query">The text typed; empty shows the overview: Recents, then the first items of each type.</param>
	/// <param name="type">The type to search, or null for every type.</param>
	/// <returns>Without a type, one group per type with its best matches, the best group first; with one, a single group of every match.</returns>
	public IReadOnlyList<SearchGroup> Search( string query, string? type = null )
	{
		ArgumentNullException.ThrowIfNull( query );
		var text = query.Trim();
		var hits = Collect( text, type );
		if ( text.Length == 0 )
		{
			if ( type is not null )
				return hits.Count == 0 ? [] : [new( hits[0].Type.Title, hits[0].Type, hits )];
			return ComputeOverview( hits );
		}

		var words = text.Split( ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
		var ranked = hits
			.Select( hit => hit with { Score = Score( hit.Item, words ) } )
			.Where( hit => hit.Score > 0 )
			.OrderByDescending( hit => hit.Score )
			.ThenByDescending( hit => hit.Item.LastUsed ?? DateTimeOffset.MinValue )
			.ToArray();
		if ( type is not null )
			return ranked.Length == 0 ? [] : [new( ranked[0].Type.Title, ranked[0].Type, ranked )];

		return ranked
			.GroupBy( hit => hit.Type.Id, StringComparer.Ordinal )
			.Select( group => new SearchGroup( group.First().Type.Title, group.First().Type, group.Take( MatchLimit ).ToArray() ) )
			.OrderByDescending( group => group.Hits[0].Score )
			.ThenBy( group => group.Type!.Order )
			.ToArray();
	}

	/// <summary>Reads a query that starts with <c>/</c>: a type being chosen, or a finished <c>/type </c> prefix and the text after it.</summary>
	/// <param name="text">The text in the search field.</param>
	/// <param name="types">The types that can be picked, such as <see cref="ComputeTypes"/>.</param>
	/// <remarks>A type is picked by its key or title, or by the start of one when only one type matches. A finished prefix naming no type leaves the text a plain query.</remarks>
	public static SearchFilter ReadFilter( string text, IReadOnlyList<SearchType> types )
	{
		ArgumentNullException.ThrowIfNull( text );
		ArgumentNullException.ThrowIfNull( types );
		if ( !text.StartsWith( '/' ) )
			return new( null, text, null );

		var end = text.IndexOf( ' ', StringComparison.Ordinal );
		var name = end < 0 ? text[1..] : text[1..end];
		var matches = types
			.Where( type => type.Id.StartsWith( name, StringComparison.OrdinalIgnoreCase ) || type.Title.StartsWith( name, StringComparison.OrdinalIgnoreCase ) )
			.ToArray();
		if ( end < 0 )
			return new( null, "", matches );

		var picked = types.FirstOrDefault( type => string.Equals( type.Id, name, StringComparison.OrdinalIgnoreCase ) || string.Equals( type.Title, name, StringComparison.OrdinalIgnoreCase ) )
			?? (name.Length > 0 && matches.Length == 1 ? matches[0] : null);
		return picked is null ? new( null, text, null ) : new( picked, text[(end + 1)..], null );
	}

	/// <summary>Remembers an item as the active profile's most recently used, then runs it, reporting a failure in the shell notice.</summary>
	public void Run( SearchHit hit )
	{
		ArgumentNullException.ThrowIfNull( hit );
		Run( hit, hit.Item.Run );
	}

	/// <summary>Remembers an item as the active profile's most recently used, then runs one of its other actions, reporting a failure in the shell notice.</summary>
	public void Run( SearchHit hit, MenuAction action )
	{
		ArgumentNullException.ThrowIfNull( hit );
		ArgumentNullException.ThrowIfNull( action );
		if ( action.IsEnabled )
			Run( hit, action.Run );
	}

	/// <summary>Records an item as the active profile's most recently used.</summary>
	/// <param name="providerId">The provider that listed it.</param>
	/// <param name="itemId">The item's identifier within that provider.</param>
	public void MarkUsed( string providerId, string itemId )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( providerId );
		ArgumentException.ThrowIfNullOrWhiteSpace( itemId );
		var profile = _app.Settings.ActiveProfile;
		if ( !_recents.TryGetValue( profile, out var recents ) )
			_recents[profile] = recents = [];

		var used = new SearchRecent( providerId, itemId );
		recents.Remove( used );
		recents.Insert( 0, used );
		if ( recents.Count > HistoryLimit )
			recents.RemoveRange( HistoryLimit, recents.Count - HistoryLimit );
	}

	/// <summary>Scores how well an item matches every query word; zero when one word matches nothing.</summary>
	/// <remarks>Each word scores its best match: the title starting with it, then a later title word starting with it, then the title containing it,
	/// then the title holding its letters in order; keywords and the detail score a share of the same scale. The whole query starting the title adds a bonus.</remarks>
	internal static int Score( SearchItem item, IReadOnlyList<string> words )
	{
		var total = 0;
		foreach ( var word in words )
		{
			var best = Match( item.Title, word, isFuzzy: true );
			foreach ( var keyword in item.Keywords )
				best = Math.Max( best, Match( keyword, word, isFuzzy: false ) * KeywordPercent / 100 );
			if ( item.Detail is { } detail )
				best = Math.Max( best, Match( detail, word, isFuzzy: false ) * DetailPercent / 100 );
			if ( best == 0 )
				return 0;

			total += best;
		}

		if ( words.Count > 1 && item.Title.StartsWith( string.Join( ' ', words ), StringComparison.OrdinalIgnoreCase ) )
			total += PhraseBonus;
		return total;
	}

	/// <summary>Scores one word against one text.</summary>
	private static int Match( string text, string word, bool isFuzzy )
	{
		if ( text.StartsWith( word, StringComparison.OrdinalIgnoreCase ) )
			return StartScore;

		var index = text.IndexOf( word, StringComparison.OrdinalIgnoreCase );
		if ( index < 0 )
			return isFuzzy && IsFuzzy( text, word ) ? FuzzyScore : 0;

		// A later occurrence may start a word even when the first one does not.
		for ( ; index >= 0; index = text.IndexOf( word, index + 1, StringComparison.OrdinalIgnoreCase ) )
		{
			if ( !char.IsLetterOrDigit( text[index - 1] ) )
				return WordScore;
		}

		return ContainsScore;
	}

	/// <summary>Reports whether a text holds a word's letters in order, the first starting one of its words.</summary>
	private static bool IsFuzzy( string text, string word )
	{
		if ( word.Length < 2 )
			return false;

		for ( var start = 0; start < text.Length; start++ )
		{
			if ( (start > 0 && char.IsLetterOrDigit( text[start - 1] )) || char.ToUpperInvariant( text[start] ) != char.ToUpperInvariant( word[0] ) )
				continue;

			var next = 1;
			for ( var index = start + 1; index < text.Length && next < word.Length; index++ )
			{
				if ( char.ToUpperInvariant( text[index] ) == char.ToUpperInvariant( word[next] ) )
					next++;
			}

			if ( next == word.Length )
				return true;
		}

		return false;
	}

	/// <summary>Builds the overview: Recents, the items run last and then those used most recently, then each type's first items.</summary>
	private List<SearchGroup> ComputeOverview( IReadOnlyList<SearchHit> hits )
	{
		var byKey = new Dictionary<SearchRecent, SearchHit>();
		foreach ( var hit in hits )
			byKey.TryAdd( new( hit.ProviderId, hit.Item.Id ), hit );

		var recents = Recents
			.Select( recent => byKey.GetValueOrDefault( recent ) )
			.OfType<SearchHit>()
			.Concat( hits.Where( hit => hit.Item.LastUsed is not null ).OrderByDescending( hit => hit.Item.LastUsed ) )
			.Distinct()
			.Take( RecentLimit )
			.ToArray();
		var shown = recents.ToHashSet();
		var groups = new List<SearchGroup>();
		if ( recents.Length > 0 )
			groups.Add( new( "Recents", null, recents ) );

		foreach ( var group in hits.Where( hit => !shown.Contains( hit ) ).GroupBy( hit => hit.Type.Id, StringComparer.Ordinal ) )
		{
			var first = group
				.OrderByDescending( hit => hit.Item.LastUsed ?? DateTimeOffset.MinValue )
				.Take( OverviewLimit )
				.ToArray();
			groups.Add( new( first[0].Type.Title, first[0].Type, first ) );
		}

		return groups;
	}

	/// <summary>Asks the providers of every type, or one, for their items, in tab order then each provider's own order.</summary>
	/// <remarks>A provider that throws lists nothing; its failure reaches the shell notice once, posted so a build never changes it.</remarks>
	private List<SearchHit> Collect( string query, string? type )
	{
		var types = Types.ToDictionary( item => item.Id, StringComparer.Ordinal );
		var hits = new List<SearchHit>();
		var context = new SearchContext( _app, query );
		foreach ( var provider in _providers.OrderBy( provider => types[provider.Info.Type].Order ).ThenBy( provider => provider.Info.Order ) )
		{
			if ( type is not null && provider.Info.Type != type )
				continue;

			try
			{
				hits.AddRange( provider.Search( context ).Select( item => new SearchHit( item, provider.Info.Id, types[provider.Info.Type], 0 ) ) );
				_failed.Remove( provider.Info.Id );
			}
			catch ( Exception error )
			{
				if ( _failed.Add( provider.Info.Id ) )
					_app.Post( () => _app.ReportError( $"Search {provider.Info.Title}", error ) );
			}
		}

		return hits;
	}

	/// <summary>Remembers and runs an item's action.</summary>
	private void Run( SearchHit hit, Action action )
	{
		MarkUsed( hit.ProviderId, hit.Item.Id );
		try
		{
			action();
		}
		catch ( Exception error ) { _app.ReportError( hit.Item.Title, error ); }
	}

	/// <summary>Rejects a provider identifier that is already registered.</summary>
	internal void EnsureAvailable( IReadOnlyList<SearchProviderDefinition> definitions )
	{
		var identifiers = _providers.Select( provider => provider.Info.Id ).ToHashSet( StringComparer.Ordinal );
		foreach ( var definition in definitions )
		{
			if ( !identifiers.Add( definition.Info.Id ) )
				throw new InvalidOperationException( $"Duplicate search provider: {definition.Info.Id}" );
		}
	}

	/// <summary>Adds a module's providers.</summary>
	internal void Register( IReadOnlyList<SearchProviderDefinition> definitions ) => _providers.AddRange( definitions );

	/// <summary>Removes a module's providers; what the user ran stays remembered, so a reloaded provider's items keep their place.</summary>
	internal void RemoveModule( string moduleId )
	{
		_providers.RemoveAll( provider => provider.Info.ModuleId == moduleId );
		_failed.RemoveWhere( id => id.StartsWith( moduleId + ".", StringComparison.Ordinal ) );
	}
}

/// <summary>Stores a registered search provider and its search.</summary>
internal sealed record SearchProviderDefinition( SearchProviderInfo Info, Func<SearchContext, IEnumerable<SearchItem>> Search );
