using System.Text.Json;
using Velopack.Sources;

namespace Lokn;

/// <summary>Where Loom finds new versions of itself and of its modules, read from the <see cref="UpdatesModule.Source"/> setting.</summary>
/// <remarks>
/// The feed holds Velopack's application releases and a <c>modules.json</c> index with module DLLs beside it. On GitHub the application
/// releases are the repository's releases and the module files are assets of the pre-release tagged <c>modules</c>, which Velopack skips
/// because it reads stable releases only. A web address or a folder holds the application releases at its root and the module files in
/// its <c>modules</c> folder; <c>scripts/release.ps1</c> writes both layouts.
/// </remarks>
/// <param name="App">The source Velopack reads application releases from.</param>
/// <param name="ReadModuleFile">Reads a file of the module feed, such as <c>modules.json</c> or a module DLL, by name.</param>
internal sealed record UpdateFeed( IUpdateSource App, Func<string, CancellationToken, Task<byte[]>> ReadModuleFile )
{
	/// <summary>Defines the module index file name.</summary>
	internal const string IndexFile = "modules.json";

	/// <summary>Defines the tag of the GitHub release holding the module files.</summary>
	internal const string ModulesTag = "modules";

	/// <summary>Stores the client every download shares.</summary>
	private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes( 5 ) };

	/// <summary>Reads a feed from the setting's value: a GitHub repository address, another web address, or a folder.</summary>
	/// <returns>The feed, or null when the value is blank and updates are off.</returns>
	internal static UpdateFeed? Parse( string value )
	{
		value = value.Trim();
		if ( value.Length == 0 )
			return null;

		if ( Uri.TryCreate( value, UriKind.Absolute, out var uri ) && uri.Scheme is "http" or "https" )
		{
			if ( string.Equals( uri.Host, "github.com", StringComparison.OrdinalIgnoreCase ) )
			{
				var repository = uri.GetLeftPart( UriPartial.Path ).TrimEnd( '/' );
				if ( repository.EndsWith( ".git", StringComparison.OrdinalIgnoreCase ) )
					repository = repository[..^4];

				return new( new GithubSource( repository, null, false ), ( name, token ) => DownloadAsync( $"{repository}/releases/download/{ModulesTag}/{name}", token ) );
			}

			var root = value.TrimEnd( '/' );
			return new( new SimpleWebSource( root ), ( name, token ) => DownloadAsync( $"{root}/modules/{name}", token ) );
		}

		var folder = Path.GetFullPath( Environment.ExpandEnvironmentVariables( value ) );
		return new( new SimpleFileSource( new DirectoryInfo( folder ) ), ( name, token ) => File.ReadAllBytesAsync( Path.Combine( folder, "modules", name ), token ) );
	}

	/// <summary>Reads the module index; a feed without one offers no module updates.</summary>
	internal async Task<ModuleIndex> ReadIndexAsync( CancellationToken cancellationToken )
	{
		byte[] json;
		try
		{
			json = await ReadModuleFile( IndexFile, cancellationToken ).ConfigureAwait( false );
		}
		catch ( Exception error ) when ( error is FileNotFoundException or DirectoryNotFoundException or HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } )
		{
			return new( [] );
		}

		return JsonSerializer.Deserialize<ModuleIndex>( json, ModuleIndex.JsonOptions ) ?? new( [] );
	}

	/// <summary>Downloads one file.</summary>
	private static async Task<byte[]> DownloadAsync( string address, CancellationToken cancellationToken )
	{
		using var response = await _http.GetAsync( address, cancellationToken ).ConfigureAwait( false );
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadAsByteArrayAsync( cancellationToken ).ConfigureAwait( false );
	}
}

/// <summary>Lists the module updates a feed offers; <c>scripts/release.ps1</c> writes it as <c>modules.json</c>.</summary>
/// <param name="Modules">Every published module version, old ones included, so an older Loom can still find one that fits it.</param>
internal sealed record ModuleIndex( IReadOnlyList<ModuleRelease> Modules )
{
	/// <summary>Defines the JSON shape: camelCase names.</summary>
	internal static JsonSerializerOptions JsonOptions { get; } = new( JsonSerializerDefaults.Web );
}

/// <summary>One published version of one module.</summary>
/// <param name="Name">The module's assembly name, such as <c>Lokn.Modules.Git</c>.</param>
/// <param name="Version">The module's version, its project's <c>&lt;Version&gt;</c>.</param>
/// <param name="File">The DLL's file name in the feed.</param>
/// <param name="Sha256">The DLL's SHA-256 hash in hexadecimal, checked after the download.</param>
/// <param name="App">The Loom version it was built with, for people reading the index; compatibility is checked, not read from here.</param>
internal sealed record ModuleRelease( string Name, string Version, string File, string Sha256, string? App );
