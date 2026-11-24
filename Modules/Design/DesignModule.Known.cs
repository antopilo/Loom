using System.Text.Json;
using Lokn.Core;
using Lokn.Modules.Chat;

namespace Lokn.Modules.Design;

/// <summary>Remembers the designs this Loom served and the chat working on each, so a studio tab restored after a restart shows its design
/// and sends comments to its chat at once, before any agent touches the design again.</summary>
public sealed partial class DesignModule
{
	/// <summary>Defines how many designs are remembered; serving more forgets the least recently served.</summary>
	private const int MaxKnown = 200;

	/// <summary>Serializes the file readably, with camel-case names.</summary>
	private static readonly JsonSerializerOptions KnownJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

	/// <summary>Guards <see cref="_known"/>, which the server reads on its own threads.</summary>
	private readonly Lock _knownLock = new();

	/// <summary>Stores the remembered designs by token.</summary>
	private Dictionary<string, KnownDesign> _known = new( StringComparer.Ordinal );

	/// <summary>Gets the file the remembered designs are kept in, in the data folder.</summary>
	private static string KnownPath => LoknPaths.Combine( "designs.json" );

	/// <summary>Reads the remembered designs, merged with those already in memory, keeping each design's latest use; the window and the daemon share the file.</summary>
	private void LoadKnown()
	{
		var read = ReadKnownFile();
		lock ( _knownLock )
			Merge( read );
	}

	/// <summary>Reads the file's remembered designs by token; none when the file is missing or does not parse.</summary>
	private static Dictionary<string, KnownDesign> ReadKnownFile()
	{
		List<KnownDesign> list = [];
		try
		{
			if ( File.Exists( KnownPath ) )
				list = JsonSerializer.Deserialize<List<KnownDesign>>( File.ReadAllText( KnownPath ), KnownJson ) ?? [];
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or JsonException )
		{
		}
		return list.Where( item => item.Name.Length > 0 && Path.IsPathFullyQualified( item.Folder ) )
			.GroupBy( item => new DesignStore( item.Name, item.Folder ).Token, StringComparer.Ordinal )
			.ToDictionary( group => group.Key, group => group.MaxBy( item => item.Used )!, StringComparer.Ordinal );
	}

	/// <summary>Adds read designs to those in memory, keeping each design's latest use; the caller holds <see cref="_knownLock"/>.</summary>
	private void Merge( Dictionary<string, KnownDesign> read )
	{
		foreach ( var (token, item) in read )
		{
			if ( !_known.TryGetValue( token, out var held ) || held.Used < item.Used )
				_known[token] = item;
		}
	}

	/// <summary>Remembers a served design and, when one is known, the chat working on it; on the UI thread.</summary>
	/// <remarks>The file is read first, so designs the other Loom process remembered since are kept.</remarks>
	private void Remember( DesignStore store, Guid? conversation )
	{
		var read = ReadKnownFile();
		List<KnownDesign> list;
		lock ( _knownLock )
		{
			Merge( read );
			var previous = _known.GetValueOrDefault( store.Token );
			var item = new KnownDesign( store.Name, store.Folder, conversation ?? previous?.Conversation, DateTimeOffset.UtcNow );
			if ( previous is not null && previous.Conversation == item.Conversation && DateTimeOffset.UtcNow - previous.Used < TimeSpan.FromMinutes( 10 ) )
				return;
			_known[store.Token] = item;
			foreach ( var stale in _known.OrderByDescending( pair => pair.Value.Used ).Skip( MaxKnown ).Select( pair => pair.Key ).ToArray() )
				_known.Remove( stale );
			list = [.. _known.Values.OrderBy( known => known.Used )];
		}
		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( KnownPath )! );
			var temporary = KnownPath + "." + Environment.ProcessId + ".tmp";
			File.WriteAllText( temporary, JsonSerializer.Serialize( list, KnownJson ) );
			File.Move( temporary, KnownPath, overwrite: true );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
		}
	}

	/// <summary>Finds a remembered design by token for the server, and links it to its chat on the UI thread; null when unknown or its folder is gone.</summary>
	/// <remarks>A token not in memory reads the file again: the other Loom process may have opened the design, and this one owns the port now.</remarks>
	private DesignStore? ResolveKnown( string token )
	{
		KnownDesign? known;
		lock ( _knownLock )
			known = _known.GetValueOrDefault( token );
		if ( known is null )
		{
			LoadKnown();
			lock ( _knownLock )
				known = _known.GetValueOrDefault( token );
		}
		if ( known is null || !Directory.Exists( known.Folder ) )
			return null;

		var store = new DesignStore( known.Name, known.Folder );
		if ( known.Conversation is { } conversation )
			_context?.Post( () => LinkKnown( store, conversation ) );
		return store;
	}

	/// <summary>Links a remembered design to its chat, whose studio already shows it, once the chat still exists here.</summary>
	private void LinkKnown( DesignStore store, Guid conversation )
	{
		if ( _host is not { } host )
			return;
		try
		{
			_ = host.Store.Get( conversation );
		}
		catch ( KeyNotFoundException )
		{
			return;
		}
		if ( host.Sessions.GetOrCreate( conversation ) is not ChatSession session )
			return;
		UpdateFollowed();
		LinkChat( session, store );
		MarkShown( session, store );
	}

	/// <summary>Holds one remembered design.</summary>
	/// <param name="Name">The design's name.</param>
	/// <param name="Folder">The design folder.</param>
	/// <param name="Conversation">The chat that worked on it last, or null.</param>
	/// <param name="Used">When it was last served or linked.</param>
	private sealed record KnownDesign( string Name, string Folder, Guid? Conversation, DateTimeOffset Used );
}
