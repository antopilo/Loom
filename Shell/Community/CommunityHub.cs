using System.Security.Cryptography;
using System.Text.Json;
using Lokn.Core;

namespace Lokn;

/// <summary>Reads the community module hub and installs, updates and removes its modules as source folders that Hotload compiles.</summary>
/// <remarks>
/// The hub is a repository with one folder per module, <c>modules/&lt;id&gt;/</c>, holding <c>module.json</c> and C# files. Its CI publishes
/// <c>index.json</c>: every module with its version and the SHA-256 of each file, at one commit. Loom downloads a module's files at that
/// commit, checks each hash, and writes them into <c>&lt;root&gt;/&lt;id&gt;/</c>, a folder the Community module contributes to
/// <see cref="ModuleFolderRegistry"/>; Hotload then compiles the module against the running Loom, so a module that uses API this Loom
/// lacks does not compile and does not load. On GitHub the index is an asset of the release tagged <c>index</c> and the files come from
/// raw.githubusercontent.com; a folder hub holds <c>index.json</c> at its root, for tests and local development.
/// </remarks>
/// <param name="source">The hub: a GitHub repository address, or a folder.</param>
/// <param name="root">The folder installed modules go to, one subfolder each.</param>
internal sealed class CommunityHub( string source, string root )
{
	/// <summary>Defines the module description file every hub module has.</summary>
	internal const string DescriptionFile = "module.json";

	/// <summary>Defines the most bytes one module file may have, so a broken index cannot fill the disk.</summary>
	private const int MaximumFileBytes = 1024 * 1024;

	/// <summary>Stores the client every download shares.</summary>
	private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes( 2 ) };

	/// <summary>Gets the folder installed modules go to.</summary>
	internal string Root => root;

	/// <summary>Reads the hub's index.</summary>
	internal async Task<CommunityIndex> ReadIndexAsync( CancellationToken cancellationToken )
	{
		var json = ReadRepository() is { } repository
			? await DownloadAsync( $"{repository}/releases/download/index/index.json", cancellationToken ).ConfigureAwait( false )
			: await File.ReadAllBytesAsync( Path.Combine( source, "index.json" ), cancellationToken ).ConfigureAwait( false );
		return JsonSerializer.Deserialize<CommunityIndex>( json, CommunityIndex.JsonOptions ) ?? throw new InvalidDataException( "The community module index is empty." );
	}

	/// <summary>Lists the installed modules with the version each one's description gives.</summary>
	internal IReadOnlyList<InstalledModule> ReadInstalled()
	{
		if ( !Directory.Exists( root ) )
			return [];

		var installed = new List<InstalledModule>();
		foreach ( var folder in Directory.GetDirectories( root ) )
		{
			var id = Path.GetFileName( folder );
			try
			{
				var description = JsonSerializer.Deserialize<ModuleDescription>( File.ReadAllBytes( Path.Combine( folder, DescriptionFile ) ), CommunityIndex.JsonOptions );
				installed.Add( new( id, description?.Name ?? id, description?.Version ?? "" ) );
			}
			catch ( Exception error ) when ( error is IOException or JsonException or UnauthorizedAccessException )
			{
				installed.Add( new( id, id, "" ) );
			}
		}

		return installed;
	}

	/// <summary>Downloads a module's files at the index's commit, checks them, and writes them over its folder; runs off the UI thread.</summary>
	/// <exception cref="InvalidDataException">A file name is not allowed, or a download does not match the index.</exception>
	/// <remarks>Every file is downloaded and checked before the first one is written, so a failed download leaves the installed version.</remarks>
	internal async Task InstallAsync( CommunityIndex index, HubModule module, CancellationToken cancellationToken )
	{
		if ( !IsValidId( module.Id ) )
			throw new InvalidDataException( $"'{module.Id}' is not a valid module id." );

		var repository = ReadRepository();
		var files = new List<(string Name, byte[] Bytes)>();
		foreach ( var file in module.Files )
		{
			if ( !IsAllowedFile( file.Path ) )
				throw new InvalidDataException( $"{module.Id} has a file Loom does not install: {file.Path}" );

			var bytes = repository is not null
				? await DownloadAsync( $"https://raw.githubusercontent.com/{repository["https://github.com/".Length..]}/{index.Commit}/modules/{module.Id}/{file.Path}", cancellationToken ).ConfigureAwait( false )
				: await File.ReadAllBytesAsync( Path.Combine( source, "modules", module.Id, file.Path ), cancellationToken ).ConfigureAwait( false );
			if ( bytes.Length > MaximumFileBytes || !string.Equals( Convert.ToHexString( SHA256.HashData( bytes ) ), file.Sha256, StringComparison.OrdinalIgnoreCase ) )
				throw new InvalidDataException( $"{module.Id}/{file.Path} does not match the community module index." );

			files.Add( (file.Path, bytes) );
		}

		var folder = Path.Combine( root, module.Id );
		Directory.CreateDirectory( folder );
		foreach ( var (name, bytes) in files )
			await File.WriteAllBytesAsync( Path.Combine( folder, name ), bytes, cancellationToken ).ConfigureAwait( false );

		// Files the new version no longer has go; the project Hotload generates for editors stays.
		foreach ( var stale in Directory.GetFiles( folder ).Where( path => IsAllowedFile( Path.GetFileName( path ) ) && files.All( file => !string.Equals( file.Name, Path.GetFileName( path ), StringComparison.OrdinalIgnoreCase ) ) ) )
			File.Delete( stale );
	}

	/// <summary>Removes an installed module's folder, which unloads it.</summary>
	internal void Remove( string id )
	{
		var folder = Path.Combine( root, id );
		if ( IsValidId( id ) && Directory.Exists( folder ) )
			Directory.Delete( folder, recursive: true );
	}

	/// <summary>Lists the hub modules newer than the installed version of the same module.</summary>
	internal static IReadOnlyList<HubModule> FindUpdates( CommunityIndex index, IReadOnlyList<InstalledModule> installed )
	{
		return index.Modules
			.Where( module => installed.FirstOrDefault( item => item.Id == module.Id ) is { } current && ParseVersion( module.Version ) > ParseVersion( current.Version ) )
			.ToList();
	}

	/// <summary>Reads a version such as 1.2.0; a malformed one reads as 0.0.</summary>
	internal static Version ParseVersion( string value ) => Version.TryParse( value, out var version ) ? version : new( 0, 0 );

	/// <summary>Tells whether an id is a lowercase name a folder can safely take: letters, digits and dashes.</summary>
	internal static bool IsValidId( string id ) => id.Length is > 0 and <= 64 && id.All( letter => letter is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' );

	/// <summary>Tells whether a file name is one a hub module may have: its description, a readme, or C# source, all at the module's top level.</summary>
	internal static bool IsAllowedFile( string name )
	{
		return name.Length is > 0 and <= 128
			&& name.IndexOfAny( ['/', '\\', ':'] ) < 0
			&& !name.StartsWith( '.' )
			&& (name == DescriptionFile || name.Equals( "README.md", StringComparison.OrdinalIgnoreCase ) || name.EndsWith( ".cs", StringComparison.OrdinalIgnoreCase ));
	}

	/// <summary>Reads the source as a GitHub repository address, https://github.com/owner/name, or returns null for a folder.</summary>
	/// <exception cref="InvalidDataException">The source is a web address outside GitHub.</exception>
	private string? ReadRepository()
	{
		if ( !Uri.TryCreate( source.Trim(), UriKind.Absolute, out var uri ) || uri.Scheme is not ("http" or "https") )
			return null;

		if ( !string.Equals( uri.Host, "github.com", StringComparison.OrdinalIgnoreCase ) )
			throw new InvalidDataException( $"The community module source must be a GitHub repository or a folder: {source}" );

		var path = uri.AbsolutePath.Trim( '/' );
		if ( path.EndsWith( ".git", StringComparison.OrdinalIgnoreCase ) )
			path = path[..^4];

		return $"https://github.com/{path}";
	}

	/// <summary>Downloads one file.</summary>
	private static async Task<byte[]> DownloadAsync( string address, CancellationToken cancellationToken )
	{
		using var response = await _http.GetAsync( address, cancellationToken ).ConfigureAwait( false );
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadAsByteArrayAsync( cancellationToken ).ConfigureAwait( false );
	}
}

/// <summary>Lists every module the hub offers, as its CI publishes it in <c>index.json</c>.</summary>
/// <param name="Commit">The hub commit every file is read at.</param>
/// <param name="Modules">The modules.</param>
internal sealed record CommunityIndex( string Commit, IReadOnlyList<HubModule> Modules )
{
	/// <summary>Defines the JSON shape: camelCase names.</summary>
	internal static JsonSerializerOptions JsonOptions { get; } = new( JsonSerializerDefaults.Web );
}

/// <summary>One module of the hub.</summary>
/// <param name="Id">The module's id: its folder name and its <see cref="ILoknModule.Id"/>.</param>
/// <param name="Name">The name the user sees.</param>
/// <param name="Author">Who wrote it, usually a GitHub user name.</param>
/// <param name="Description">One sentence on what it does.</param>
/// <param name="Version">Its version, raised with every change.</param>
/// <param name="Files">Its files with their hashes.</param>
internal sealed record HubModule( string Id, string Name, string Author, string Description, string Version, IReadOnlyList<HubFile> Files );

/// <summary>One file of a hub module.</summary>
/// <param name="Path">The file name inside the module's folder.</param>
/// <param name="Sha256">Its SHA-256 hash in hexadecimal.</param>
internal sealed record HubFile( string Path, string Sha256 );

/// <summary>A module installed from the hub.</summary>
/// <param name="Id">Its id, the folder name.</param>
/// <param name="Name">Its name from its description.</param>
/// <param name="Version">Its version from its description, or empty when the description is missing.</param>
internal sealed record InstalledModule( string Id, string Name, string Version );

/// <summary>The fields of <c>module.json</c> that Loom reads from an installed module.</summary>
/// <param name="Name">The name the user sees.</param>
/// <param name="Version">The version.</param>
internal sealed record ModuleDescription( string? Name, string? Version );
