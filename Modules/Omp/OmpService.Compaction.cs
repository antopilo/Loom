using System.Globalization;
using System.Text.Json;
using Lokn.Core;

namespace Lokn.Modules.Omp;

/// <summary>Holds an Omp profile's automatic compaction settings, enough to tell when a session compacts.</summary>
/// <param name="IsEnabled">Whether automatic compaction is on (<c>compaction.enabled</c>, and a <c>compaction.strategy</c> other than off).</param>
/// <param name="ThresholdPercent">The share of the context window that triggers it, or zero or less for Omp's default (<c>compaction.thresholdPercent</c>).</param>
/// <param name="ThresholdTokens">The token count that triggers it, or zero or less for none (<c>compaction.thresholdTokens</c>).</param>
/// <param name="ReserveTokens">The tokens kept free for the reply, or null for Omp's default (<c>compaction.reserveTokens</c>).</param>
/// <param name="ModelThresholds">Per-model overrides by <c>provider/model</c> or <c>provider/prefix*</c>: a token count, a fixed trigger such as <c>f400000</c>, or a percentage such as <c>80%</c> (<c>compaction.modelThresholds</c>).</param>
public sealed record OmpCompaction( bool IsEnabled, double ThresholdPercent, long ThresholdTokens, long? ReserveTokens, IReadOnlyDictionary<string, string> ModelThresholds )
{
	/// <summary>Defines the reply reserve Omp keeps when none is configured.</summary>
	private const long DefaultReserve = 16384;

	/// <summary>Computes the context size at which Omp compacts a session, mirroring Omp's own rule; null when it never compacts on its own.</summary>
	/// <param name="window">The model's context window in tokens.</param>
	/// <param name="model">The session's model as <c>provider/id</c>, which may have an override.</param>
	public long? ComputeThreshold( long window, string model )
	{
		if ( !IsEnabled || window <= 0 )
			return null;

		var percent = ThresholdPercent;
		var tokens = ThresholdTokens;
		var baseWindow = 0L;
		if ( FindModelThreshold( model ) is { } overriding )
		{
			// A fixed trigger is the token count itself; a plain count caps the window the default rule applies to.
			if ( overriding.EndsWith( '%' ) && double.TryParse( overriding[..^1], CultureInfo.InvariantCulture, out var share ) && share is > 0 and <= 100 )
				(percent, tokens) = (share, -1);
			else if ( overriding.StartsWith( 'f' ) && long.TryParse( overriding[1..], CultureInfo.InvariantCulture, out var fixedTokens ) && fixedTokens > 0 )
				tokens = fixedTokens;
			else if ( long.TryParse( overriding, CultureInfo.InvariantCulture, out var cap ) && cap > 0 )
				(tokens, baseWindow) = (-1, cap);
		}

		long threshold;
		if ( tokens > 0 )
			threshold = tokens >= window ? ComputeDefault( window ) : Math.Min( window - 1, Math.Max( 1, tokens ) );
		else
		{
			var effective = baseWindow > 0 ? Math.Min( window, baseWindow ) : window;
			threshold = percent <= 0 ? ComputeDefault( effective ) : (long)Math.Floor( effective * (Math.Clamp( percent, 1, 99 ) / 100) );
		}

		return threshold > 0 && threshold <= window ? threshold : null;
	}

	/// <summary>Computes Omp's default trigger: the window less the larger of 15% and the reply reserve.</summary>
	private long ComputeDefault( long window )
	{
		var fifteen = Math.Max( 1, (long)Math.Floor( window * 0.15 ) );
		var reserve = Math.Max( (long)Math.Floor( window * 0.15 ), ReserveTokens ?? DefaultReserve );
		if ( ReserveTokens is null && reserve >= window - fifteen || reserve >= window )
			reserve = fifteen;
		return Math.Max( 0, Math.Min( window - 1, window - reserve ) );
	}

	/// <summary>Finds a model's override: its exact selector, else the longest matching prefix ending in <c>*</c>.</summary>
	private string? FindModelThreshold( string model )
	{
		if ( ModelThresholds.TryGetValue( model, out var exact ) )
			return exact;

		return ModelThresholds
			.Where( item => item.Key.EndsWith( '*' ) && model.StartsWith( item.Key[..^1], StringComparison.Ordinal ) )
			.OrderByDescending( item => item.Key.Length )
			.Select( item => item.Value )
			.FirstOrDefault();
	}

	/// <summary>Reads the settings from <c>omp config list --json</c>, which maps each key to an object holding its value.</summary>
	internal static OmpCompaction Parse( string json )
	{
		using var document = JsonDocument.Parse( json );
		var root = document.RootElement;
		JsonElement? Value( string key ) => root.TryGetProperty( key, out var entry ) && entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty( "value", out var value ) ? value : null;
		double Number( string key, double fallback ) => Value( key ) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : fallback;

		var thresholds = new Dictionary<string, string>( StringComparer.Ordinal );
		if ( Value( "compaction.modelThresholds" ) is { ValueKind: JsonValueKind.Object } models )
		{
			foreach ( var model in models.EnumerateObject() )
			{
				var text = model.Value.ValueKind == JsonValueKind.Number ? model.Value.GetRawText() : model.Value.GetString();
				if ( !string.IsNullOrWhiteSpace( text ) )
					thresholds[model.Name] = text.Trim().ToLowerInvariant();
			}
		}

		var strategy = Value( "compaction.strategy" ) is { ValueKind: JsonValueKind.String } named ? named.GetString() : null;
		return new(
			Value( "compaction.enabled" ) is not { ValueKind: JsonValueKind.False } && strategy != "off",
			Number( "compaction.thresholdPercent", -1 ),
			(long)Number( "compaction.thresholdTokens", -1 ),
			Value( "compaction.reserveTokens" ) is { ValueKind: JsonValueKind.Number } reserve ? reserve.GetInt64() : null,
			thresholds );
	}
}

/// <summary>Reads Omp's automatic compaction settings for the context meter.</summary>
public sealed partial class OmpService
{
	/// <summary>Reads a Lokn profile's Omp compaction settings with the bootstrap-safe CLI, without starting an agent.</summary>
	public async Task<OmpCompaction> GetCompactionAsync( SettingsProfile profile, CancellationToken cancellationToken = default )
	{
		var output = await RunCliAsync( ComputeExecutable( profile ), ["--profile", GetProfileName( profile ), "config", "list", "--json"], cancellationToken ).ConfigureAwait( false );
		return OmpCompaction.Parse( output );
	}
}
