using System.Text.Json;
using Lokn.Core;

namespace Lokn.Modules.Omp;

/// <summary>Describes one rate-limit window of a provider account, such as Claude's five-hour limit.</summary>
/// <param name="Label">The provider's name for the limit.</param>
/// <param name="UsedFraction">How much of the limit is spent, from 0 to 1.</param>
/// <param name="ResetsAt">When the window starts over, or null when the provider does not say.</param>
/// <param name="IsExhausted">Whether the provider refuses requests until the window resets.</param>
public sealed record OmpUsageLimit( string Label, double UsedFraction, DateTimeOffset? ResetsAt, bool IsExhausted );

/// <summary>Describes the usage of one signed-in provider account.</summary>
/// <param name="Provider">The provider identifier, such as anthropic.</param>
/// <param name="Account">The account's email or identifier, or empty when the provider does not report one.</param>
/// <param name="Limits">The account's rate-limit windows, shortest first as the provider lists them.</param>
/// <param name="SavedResets">How many limit resets the account holds.</param>
/// <param name="UsableResets">How many of the saved resets can be spent now.</param>
public sealed record OmpUsageAccount( string Provider, string Account, IReadOnlyList<OmpUsageLimit> Limits, int SavedResets, int UsableResets );

/// <summary>Holds the usage of every account an Omp profile is signed in to.</summary>
/// <param name="FetchedAt">When Omp read the usage from the providers.</param>
/// <param name="Accounts">The signed-in accounts that report usage.</param>
public sealed record OmpUsageReport( DateTimeOffset FetchedAt, IReadOnlyList<OmpUsageAccount> Accounts );

/// <summary>Reads provider usage limits and their resets through Omp, without starting an agent turn.</summary>
public sealed partial class OmpService
{
	/// <summary>Retains the latest successful usage report per profile for instant prompt-bar display.</summary>
	private readonly Dictionary<SettingsProfile, OmpUsageReport> _usageReports = [];

	/// <summary>Reads the usage limits of every account a profile is signed in to; Omp asks the providers and caches their answers briefly.</summary>
	public async Task<OmpUsageReport> GetUsageAsync( SettingsProfile profile, CancellationToken cancellationToken = default )
	{
		var json = await RunCliAsync( ComputeExecutable( profile ), ["--profile", GetProfileName( profile ), "usage", "--json"], cancellationToken ).ConfigureAwait( false );
		var report = BuildUsage( json );
		lock ( _gate )
			_usageReports[profile] = report;
		return report;
	}

	/// <summary>Gets the latest successful usage report for a profile, or null before one has been read.</summary>
	public OmpUsageReport? GetCachedUsage( SettingsProfile profile )
	{
		lock ( _gate )
			return _usageReports.GetValueOrDefault( profile );
	}

	/// <summary>Reads the document `omp usage --json` prints into accounts and their limits.</summary>
	internal static OmpUsageReport BuildUsage( string json )
	{
		var root = JsonSerializer.Deserialize<JsonElement>( json );
		var accounts = new List<OmpUsageAccount>();
		foreach ( var report in ReadItems( root, "reports" ) )
		{
			var limits = ReadItems( report, "limits" )
				.Select( BuildUsageLimit )
				.ToArray();

			var metadata = report.TryGetProperty( "metadata", out var details ) ? details : default;
			var resets = report.TryGetProperty( "resetCredits", out var credits ) ? credits : default;
			accounts.Add( new(
				ReadString( report, "provider" ),
				ReadString( metadata, "email" ) is { Length: > 0 } email ? email : ReadString( metadata, "accountId" ),
				limits,
				(int)( ReadNumber( resets, "availableCount" ) ?? 0 ),
				(int)( ReadNumber( resets, "redeemableCount" ) ?? 0 ) ) );
		}

		return new( ReadTime( root, "generatedAt" ) ?? DateTimeOffset.UtcNow, accounts );
	}

	/// <summary>Reads one limit: its label, the spent share of its window, and when the window resets.</summary>
	private static OmpUsageLimit BuildUsageLimit( JsonElement limit )
	{
		var amount = limit.TryGetProperty( "amount", out var spent ) ? spent : default;
		var window = limit.TryGetProperty( "window", out var period ) ? period : default;
		return new(
			ReadString( limit, "label" ),
			Math.Clamp( ReadNumber( amount, "usedFraction" ) ?? 0, 0, 1 ),
			ReadTime( window, "resetsAt" ),
			ReadString( limit, "status" ) == "exhausted" );
	}

	/// <summary>Enumerates an optional array property, or nothing when it is missing.</summary>
	private static IEnumerable<JsonElement> ReadItems( JsonElement element, string property ) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty( property, out var items ) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];

	/// <summary>Reads an optional string, or empty when it is missing.</summary>
	private static string ReadString( JsonElement element, string property ) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty( property, out var value ) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

	/// <summary>Reads an optional time given in milliseconds since the Unix epoch, or null when it is missing.</summary>
	private static DateTimeOffset? ReadTime( JsonElement element, string property ) =>
		ReadNumber( element, property ) is { } milliseconds ? DateTimeOffset.FromUnixTimeMilliseconds( (long)milliseconds ) : null;
}
