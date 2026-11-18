using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Lokn.Core;

namespace Lokn.Modules.Omp;

/// <summary>Describes an available chat model without exposing account credentials.</summary>
/// <param name="Provider">The provider that serves the model.</param>
/// <param name="Id">The model identifier within its provider.</param>
/// <param name="Name">The display name.</param>
/// <param name="ContextWindow">The context window in tokens, or zero when unknown.</param>
/// <param name="MaxOutput">The largest response in tokens, or zero when unknown.</param>
/// <param name="InputCost">The price of a million input tokens in dollars, or null when unknown.</param>
/// <param name="OutputCost">The price of a million output tokens in dollars, or null when unknown.</param>
/// <param name="IsReasoning">Whether the model can reason before answering.</param>
/// <param name="HasImages">Whether the model accepts images.</param>
public sealed record OmpModelInfo(
	string Provider,
	string Id,
	string Name,
	long ContextWindow = 0,
	long MaxOutput = 0,
	double? InputCost = null,
	double? OutputCost = null,
	bool IsReasoning = false,
	bool HasImages = false )
{
	/// <summary>Gets the unambiguous provider/model selector accepted by Omp.</summary>
	public string Selector => $"{Provider}/{Id}";
}

/// <summary>Discovers models and checks setup before a conversation can start.</summary>
public sealed partial class OmpService
{
	/// <summary>Defines the model discovery deadline in seconds.</summary>
	private const int DiscoverySeconds = 45;

	/// <summary>Defines how many recently chosen models each profile remembers.</summary>
	private const int RecentLimit = 5;

	/// <summary>Retains each configuration's last discovered catalog, keyed by its executable and Omp profile arguments.</summary>
	private readonly Dictionary<string, IReadOnlyList<OmpModelInfo>> _catalogs = [];

	/// <summary>Retains the models chosen this run per profile, newest first.</summary>
	private readonly Dictionary<SettingsProfile, List<string>> _recent = [];

	/// <summary>Gets the models recently chosen for a profile in this run, newest first.</summary>
	public IReadOnlyList<string> GetRecentModels( SettingsProfile profile )
	{
		lock ( _gate )
			return _recent.TryGetValue( profile, out var recent ) ? [.. recent] : [];
	}

	/// <summary>Remembers a model chosen for a profile, moving it to the front.</summary>
	public void RecordRecentModel( SettingsProfile profile, string selector )
	{
		lock ( _gate )
		{
			var recent = _recent.TryGetValue( profile, out var known ) ? known : _recent[profile] = [];
			recent.Remove( selector );
			recent.Insert( 0, selector );
			if ( recent.Count > RecentLimit )
				recent.RemoveRange( RecentLimit, recent.Count - RecentLimit );
		}
	}

	/// <summary>Gets the last catalog listed for a profile's current configuration, or null when none was listed yet.</summary>
	public IReadOnlyList<OmpModelInfo>? GetCachedModels( SettingsProfile profile )
	{
		var key = ComputeCatalogKey( ComputeExecutable( profile ), GetProfileName( profile ) );
		lock ( _gate )
			return _catalogs.GetValueOrDefault( key );
	}

	/// <summary>Gets a model's display name from the profile's last catalog, or one derived from its identifier when the catalog does not list it.</summary>
	public string GetModelName( SettingsProfile profile, string selector )
	{
		if ( GetCachedModels( profile )?.FirstOrDefault( model => model.Selector == selector ) is { } known )
			return known.Name;

		var slash = selector.IndexOf( '/' );
		return FormatModelName( slash < 0 ? selector : selector[(slash + 1)..] );
	}

	/// <summary>Derives a display name from a model identifier, so "claude-opus-4-5-20251101" reads "Claude Opus 4.5".</summary>
	internal static string FormatModelName( string id )
	{
		var name = new StringBuilder();
		var wasNumber = false;
		foreach ( var part in id.Split( ['-', '_'], StringSplitOptions.RemoveEmptyEntries ) )
		{
			var isNumber = part.All( char.IsAsciiDigit );
			// An eight-digit part is a release date, which the catalog's names leave out.
			if ( isNumber && part.Length == 8 )
				continue;

			if ( name.Length > 0 )
				name.Append( isNumber && wasNumber ? '.' : ' ' );
			name.Append( char.ToUpperInvariant( part[0] ) ).Append( part, 1, part.Length - 1 );
			wasNumber = isNumber;
		}

		return name.Length > 0 ? name.ToString() : id;
	}

	/// <summary>Identifies a catalog by the executable and Omp profile that listed it.</summary>
	private static string ComputeCatalogKey( string executable, string profileName ) => $"{executable}\n{profileName}";

	/// <summary>Lists available models using the bootstrap-safe CLI without starting an agent turn, and remembers the catalog.</summary>
	public async Task<IReadOnlyList<OmpModelInfo>> GetModelsAsync( SettingsProfile profile, CancellationToken cancellationToken = default )
	{
		var executable = ComputeExecutable( profile );
		var name = GetProfileName( profile );
		var models = BuildModels( await RunCliAsync( executable, ["--profile", name, "models", "--json"], cancellationToken ).ConfigureAwait( false ) );
		lock ( _gate )
			_catalogs[ComputeCatalogKey( executable, name )] = models;
		return models;
	}

	/// <summary>Gets the executable a Lokn profile runs, resolved on PATH when it is a bare command.</summary>
	internal string ComputeExecutable( SettingsProfile profile )
	{
		var configured = settings.Get( OmpModule.Executable, ComputeScope( profile ) );
		return ResolveExecutable( configured ) ?? configured;
	}

	/// <summary>Reads the supported model-list document and retains unique provider/model identities with their limits and prices.</summary>
	internal static IReadOnlyList<OmpModelInfo> BuildModels( string json )
	{
		using var document = JsonDocument.Parse( json );
		return document.RootElement.GetProperty( "models" ).EnumerateArray()
			.Select( model =>
			{
				var id = model.GetProperty( "id" ).GetString()!;
				var cost = model.TryGetProperty( "cost", out var prices ) && prices.ValueKind == JsonValueKind.Object ? prices : default;
				return new OmpModelInfo(
					model.GetProperty( "provider" ).GetString()!,
					id,
					model.TryGetProperty( "name", out var name ) ? name.GetString()! : id,
					ReadNumber( model, "contextWindow" ) is { } window ? (long)window : 0,
					ReadNumber( model, "maxTokens" ) is { } output ? (long)output : 0,
					ReadNumber( cost, "input" ),
					ReadNumber( cost, "output" ),
					model.TryGetProperty( "reasoning", out var reasoning ) && reasoning.ValueKind == JsonValueKind.True,
					model.TryGetProperty( "input", out var input ) && input.ValueKind == JsonValueKind.Array && input.EnumerateArray().Any( kind => kind.GetString() == "image" ) );
			} )
			.DistinctBy( model => model.Selector )
			.OrderBy( model => model.Provider )
			.ThenBy( model => model.Name )
			.ToArray();
	}

	/// <summary>Reads an optional number, or null when it is missing or not a number.</summary>
	private static double? ReadNumber( JsonElement element, string property ) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty( property, out var value ) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}
