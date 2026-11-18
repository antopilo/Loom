using System.Text.Json;
using System.Text.Json.Serialization;
using Lokn.Core;

namespace Lokn.Modules.Omp;

/// <summary>Describes one named Omp model-routing preset.</summary>
/// <param name="Roles">The model selector assigned to each role.</param>
/// <param name="DefaultThinkingLevel">The preset's default reasoning level.</param>
internal sealed record OmpModelPreset(
	[property: JsonPropertyName( "modelRoles" )] IReadOnlyDictionary<string, string> Roles,
	[property: JsonPropertyName( "defaultThinkingLevel" )] string DefaultThinkingLevel );

/// <summary>Describes the effective model routing of one Omp profile.</summary>
/// <param name="Roles">The primary model selector assigned to each role.</param>
/// <param name="Fallbacks">The ordered fallback selectors assigned to each role.</param>
/// <param name="Presets">The named role and thinking-level presets.</param>
/// <param name="DefaultThinkingLevel">The default reasoning level.</param>
internal sealed record OmpModelRouting(
	IReadOnlyDictionary<string, string> Roles,
	IReadOnlyDictionary<string, IReadOnlyList<string>> Fallbacks,
	IReadOnlyDictionary<string, OmpModelPreset> Presets,
	string DefaultThinkingLevel );

/// <summary>Reads and writes Omp's profile-owned model roles, fallback chains and presets.</summary>
public sealed partial class OmpService
{
	/// <summary>Reads the effective model routing for a profile without starting an agent session.</summary>
	internal async Task<OmpModelRouting> GetModelRoutingAsync( SettingsProfile profile, CancellationToken cancellationToken = default )
	{
		var executable = ComputeExecutable( profile );
		var arguments = new[] { "--profile", GetProfileName( profile ), "config", "get" };
		var roles = RunCliAsync( executable, [.. arguments, "modelRoles", "--json"], cancellationToken );
		var fallbacks = RunCliAsync( executable, [.. arguments, "retry.fallbackChains", "--json"], cancellationToken );
		var presets = RunCliAsync( executable, [.. arguments, "modelPresets", "--json"], cancellationToken );
		var thinking = RunCliAsync( executable, [.. arguments, "defaultThinkingLevel", "--json"], cancellationToken );
		await Task.WhenAll( roles, fallbacks, presets, thinking ).ConfigureAwait( false );
		return BuildModelRouting( await roles, await fallbacks, await presets, await thinking );
	}

	/// <summary>Saves role assignments and fallback chains to the profile's global Omp configuration.</summary>
	internal async Task SaveModelRoutingAsync(
		SettingsProfile profile,
		IReadOnlyDictionary<string, string> roles,
		IReadOnlyDictionary<string, IReadOnlyList<string>> fallbacks,
		CancellationToken cancellationToken = default )
	{
		await SetConfigAsync( profile, "modelRoles", roles, cancellationToken ).ConfigureAwait( false );
		await SetConfigAsync( profile, "retry.fallbackChains", fallbacks, cancellationToken ).ConfigureAwait( false );
	}

	/// <summary>Saves or replaces one named preset with the supplied roles and reasoning level.</summary>
	internal Task SaveModelPresetAsync(
		SettingsProfile profile,
		IReadOnlyDictionary<string, OmpModelPreset> presets,
		CancellationToken cancellationToken = default ) =>
		SetConfigAsync( profile, "modelPresets", presets, cancellationToken );

	/// <summary>Applies a preset to new sessions and subsequent routed work in the profile.</summary>
	internal async Task ApplyModelPresetAsync( SettingsProfile profile, OmpModelPreset preset, CancellationToken cancellationToken = default )
	{
		await SetConfigAsync( profile, "modelRoles", preset.Roles, cancellationToken ).ConfigureAwait( false );
		await SetConfigAsync( profile, "defaultThinkingLevel", preset.DefaultThinkingLevel, cancellationToken ).ConfigureAwait( false );
	}

	/// <summary>Writes one typed Omp setting with the supported configuration CLI.</summary>
	private async Task SetConfigAsync( SettingsProfile profile, string key, object value, CancellationToken cancellationToken )
	{
		var executable = ComputeExecutable( profile );
		var json = value is string text ? text : JsonSerializer.Serialize( value );
		await RunCliAsync( executable, ["--profile", GetProfileName( profile ), "config", "set", key, json], cancellationToken ).ConfigureAwait( false );
	}

	/// <summary>Builds model-routing state from four <c>omp config get --json</c> documents.</summary>
	internal static OmpModelRouting BuildModelRouting( string rolesJson, string fallbacksJson, string presetsJson, string thinkingJson )
	{
		var roles = ReadStringRecord( ReadConfigValue( rolesJson ) );
		var fallbacks = ReadListRecord( ReadConfigValue( fallbacksJson ) );
		var presets = new Dictionary<string, OmpModelPreset>( StringComparer.Ordinal );
		var presetValue = ReadConfigValue( presetsJson );
		if ( presetValue.ValueKind == JsonValueKind.Object )
		{
			foreach ( var item in presetValue.EnumerateObject() )
			{
				if ( item.Value.ValueKind != JsonValueKind.Object )
					continue;
				var presetRoles = item.Value.TryGetProperty( "modelRoles", out var roleValue ) ? ReadStringRecord( roleValue ) : new Dictionary<string, string>();
				var level = item.Value.TryGetProperty( "defaultThinkingLevel", out var levelValue ) && levelValue.ValueKind == JsonValueKind.String
					? levelValue.GetString()! : "high";
				presets[item.Name] = new( presetRoles, level );
			}
		}

		var thinkingValue = ReadConfigValue( thinkingJson );
		var thinking = thinkingValue.ValueKind == JsonValueKind.String ? thinkingValue.GetString()! : "high";
		return new( roles, fallbacks, presets, thinking );
	}

	/// <summary>Reads the <c>value</c> member of an Omp configuration response.</summary>
	private static JsonElement ReadConfigValue( string json )
	{
		using var document = JsonDocument.Parse( json );
		return document.RootElement.TryGetProperty( "value", out var value ) ? value.Clone() : default;
	}

	/// <summary>Reads a string-valued JSON object, dropping null and unsupported values.</summary>
	private static Dictionary<string, string> ReadStringRecord( JsonElement value )
	{
		var result = new Dictionary<string, string>( StringComparer.Ordinal );
		if ( value.ValueKind != JsonValueKind.Object )
			return result;
		foreach ( var item in value.EnumerateObject() )
		{
			if ( item.Value.ValueKind == JsonValueKind.String && item.Value.GetString() is { Length: > 0 } selector )
				result[item.Name] = selector;
		}
		return result;
	}

	/// <summary>Reads an array-of-strings-valued JSON object, retaining explicit empty fallback chains.</summary>
	private static Dictionary<string, IReadOnlyList<string>> ReadListRecord( JsonElement value )
	{
		var result = new Dictionary<string, IReadOnlyList<string>>( StringComparer.Ordinal );
		if ( value.ValueKind != JsonValueKind.Object )
			return result;
		foreach ( var item in value.EnumerateObject() )
		{
			if ( item.Value.ValueKind == JsonValueKind.Array )
				result[item.Name] = [.. item.Value.EnumerateArray().Where( entry => entry.ValueKind == JsonValueKind.String ).Select( entry => entry.GetString()! )];
		}
		return result;
	}
}
