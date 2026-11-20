using System.Text.Json;
using Lokn.Core;

namespace Lokn.Modules.Worktrees;

/// <summary>Describes how one repository's new working trees are made and what they share.</summary>
/// <param name="SparseDirectories">The directories a new working tree checks out by default; empty checks out everything.</param>
/// <param name="SharedFolders">Ignored folders, such as download or shader caches, that every working tree links to the main checkout's copy.</param>
/// <param name="NativeInputs">The paths whose content decides the native build, such as src; empty turns native sharing off.</param>
/// <param name="NativeOutput">The folder the native build writes, such as game/bin/win64; empty turns native sharing off.</param>
internal sealed record RepositorySetup( string[] SparseDirectories, string[] SharedFolders, string[] NativeInputs, string NativeOutput )
{
	/// <summary>Gets the setup of a repository nobody has configured: full checkouts that share nothing.</summary>
	internal static RepositorySetup Empty { get; } = new( [], [], [], "" );

	/// <summary>Gets whether working trees with the same native sources share one native build.</summary>
	internal bool SharesNative => NativeInputs.Length > 0 && NativeOutput.Length > 0;
}

/// <summary>Describes one file of a recorded native build, so a later rebuild is noticed.</summary>
/// <param name="Path">The path relative to the native output folder, with forward slashes.</param>
/// <param name="Length">The size in bytes.</param>
/// <param name="WriteTicks">The last write time in UTC ticks.</param>
internal sealed record NativeFile( string Path, long Length, long WriteTicks );

/// <summary>Records that a checkout's native output folder holds a build of particular native sources.</summary>
/// <param name="Output">The output folder, relative to the checkout, when it was recorded.</param>
/// <param name="Fingerprint">The native sources' fingerprint; see <see cref="NativeBuilds"/>.</param>
/// <param name="Files">The output folder's files when it was recorded; any change means it was rebuilt.</param>
internal sealed record NativeBuildRecord( string Output, string Fingerprint, NativeFile[] Files );

/// <summary>Records that a working tree's native output links to another checkout's recorded build.</summary>
/// <param name="Source">The checkout whose build it links to.</param>
/// <param name="Fingerprint">The fingerprint of the build it linked.</param>
internal sealed record NativeLink( string Source, string Fingerprint );

/// <summary>Holds a repository's setup and the build records, for work off the UI thread.</summary>
/// <param name="Setup">The repository's setup.</param>
/// <param name="Builds">The recorded native builds by checkout directory.</param>
/// <param name="Links">The native links by working tree directory.</param>
internal sealed record WorktreeSetupSnapshot( RepositorySetup Setup, IReadOnlyDictionary<string, NativeBuildRecord> Builds, IReadOnlyDictionary<string, NativeLink> Links );

/// <summary>Keeps repository setups, recorded native builds and native links in a per-user file; used on the UI thread.</summary>
internal sealed class WorktreeSetupStore( string path )
{
	/// <summary>Defines the supported document version.</summary>
	private const int DocumentVersion = 1;

	/// <summary>Gets the worktree setup file, in the data folder.</summary>
	internal static string DefaultPath => LoknPaths.Combine( "worktrees.json" );

	/// <summary>Stores setups by main working tree directory.</summary>
	private readonly Dictionary<string, RepositorySetup> _repositories = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Stores recorded native builds by checkout directory.</summary>
	private readonly Dictionary<string, NativeBuildRecord> _builds = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Stores native links by working tree directory.</summary>
	private readonly Dictionary<string, NativeLink> _links = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Reports a file that failed to read, which this session must not overwrite.</summary>
	private bool _isInvalid;

	/// <summary>Guards the maps: the panel writes on the UI thread while its refresh captures them in the background.</summary>
	private readonly Lock _lock = new();

	/// <summary>Gets the latest read or write failure, or null.</summary>
	internal string? LastError
	{
		get; private set;
	}

	/// <summary>Reads the file; a missing file is an empty store.</summary>
	internal void Load()
	{
		_repositories.Clear();
		_builds.Clear();
		_links.Clear();
		try
		{
			if ( !File.Exists( path ) )
				return;

			var document = JsonSerializer.Deserialize<SetupDocument>( File.ReadAllText( path ) )
				?? throw new InvalidDataException( "The worktrees file is empty." );
			if ( document.Version != DocumentVersion || document.Repositories is null || document.Builds is null || document.Links is null )
				throw new InvalidDataException( "The worktrees file version or structure is unsupported." );

			foreach ( var (key, value) in document.Repositories )
				_repositories[Normalize( key )] = value;
			foreach ( var (key, value) in document.Builds )
				_builds[Normalize( key )] = value;
			foreach ( var (key, value) in document.Links )
				_links[Normalize( key )] = value with { Source = Normalize( value.Source ) };
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException )
		{
			// The file stays as it is so nothing the user set up is lost; this session keeps its changes in memory.
			_isInvalid = true;
			LastError = $"Could not read {path}: {error.Message}";
		}
	}

	/// <summary>Gets a repository's setup and every build record and link, as a snapshot any thread may read.</summary>
	internal WorktreeSetupSnapshot Capture( string mainPath )
	{
		lock ( _lock )
		{
			var setup = _repositories.GetValueOrDefault( Normalize( mainPath ) ) ?? RepositorySetup.Empty;
			return new( setup, new Dictionary<string, NativeBuildRecord>( _builds, StringComparer.OrdinalIgnoreCase ), new Dictionary<string, NativeLink>( _links, StringComparer.OrdinalIgnoreCase ) );
		}
	}

	/// <summary>Replaces a repository's setup.</summary>
	internal void SetSetup( string mainPath, RepositorySetup setup )
	{
		lock ( _lock )
			_repositories[Normalize( mainPath )] = setup;
		Save();
	}

	/// <summary>Records a checkout's native build.</summary>
	internal void SetBuild( string checkout, NativeBuildRecord record )
	{
		lock ( _lock )
			_builds[Normalize( checkout )] = record;
		Save();
	}

	/// <summary>Records that a working tree links to another checkout's native build, or forgets the link when null.</summary>
	internal void SetLink( string worktree, NativeLink? link )
	{
		lock ( _lock )
		{
			if ( link is null )
				_links.Remove( Normalize( worktree ) );
			else
				_links[Normalize( worktree )] = link with { Source = Normalize( link.Source ) };
		}
		Save();
	}

	/// <summary>Forgets a removed working tree's recorded build and native link.</summary>
	/// <remarks>Links other working trees made to its build stay: their files outlive it, and their status reports the missing source.</remarks>
	internal void Forget( string checkout )
	{
		lock ( _lock )
		{
			_builds.Remove( Normalize( checkout ) );
			_links.Remove( Normalize( checkout ) );
		}
		Save();
	}

	/// <summary>Normalizes a directory to a full path without a trailing separator.</summary>
	internal static string Normalize( string directory ) => Path.TrimEndingDirectorySeparator( Path.GetFullPath( directory ) );

	/// <summary>Writes the store beside the file and then replaces it, unless the file failed to read.</summary>
	private void Save()
	{
		if ( _isInvalid )
			return;

		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( path )! );
			var temporary = path + ".tmp";
			SetupDocument document;
			lock ( _lock )
				document = new( DocumentVersion, new( _repositories ), new( _builds ), new( _links ) );
			File.WriteAllText( temporary, JsonSerializer.Serialize( document, new JsonSerializerOptions { WriteIndented = true } ) );
			File.Move( temporary, path, overwrite: true );
			LastError = null;
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			LastError = $"Could not save worktree setup: {error.Message}";
		}
	}

	/// <summary>Stores the versioned worktrees document.</summary>
	private sealed record SetupDocument( int Version, Dictionary<string, RepositorySetup> Repositories, Dictionary<string, NativeBuildRecord> Builds, Dictionary<string, NativeLink> Links );
}
