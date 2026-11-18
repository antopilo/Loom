using System.Text.Json;
using System.Text.Json.Serialization;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Projects;

/// <summary>Describes a project and its working directory within one account profile.</summary>
public sealed record ProjectInfo( Guid Id, SettingsProfile Profile, string Name, string Directory );

/// <summary>Owns the projects of every profile on the UI thread and keeps them in one small file.</summary>
/// <remarks>
/// Readers of the project list rebuild when it changes. Every change is written at once, through a sibling temporary file.
/// Projects used to live in Conversations' <c>conversations.json</c>; while the projects file does not exist yet, they are read
/// from there once and written to it, so nothing is lost. A file that fails to read is never overwritten.
/// </remarks>
public sealed class ProjectStore
{
	/// <summary>Defines the projects file format version.</summary>
	private const int FileVersion = 1;

	/// <summary>Defines the conversation history versions whose <c>Projects</c> array is imported: one file with every message, and the index.</summary>
	private static readonly int[] _legacyVersions = [1, 2];

	/// <summary>Defines indented JSON with named account profiles.</summary>
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		Converters =
		{
			new JsonStringEnumConverter<SettingsProfile>( allowIntegerValues: false ),
		},
	};

	/// <summary>Stores the projects file, or null to keep projects in memory.</summary>
	private readonly string? _path;

	/// <summary>Stores the conversation history file projects are imported from while the projects file does not exist, or null.</summary>
	private readonly string? _legacyPath;

	/// <summary>Stores projects by stable identifier.</summary>
	private readonly Dictionary<Guid, ProjectInfo> _projects = [];

	/// <summary>Tracks changes to the projects, read by every view that lists them.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Reports whether the file was read, which happens on first use.</summary>
	private bool _isLoaded;

	/// <summary>Reports whether the projects were read completely, so the file can safely be replaced.</summary>
	private bool _canSave = true;

	/// <summary>Reports whether the projects file holds the projects: it was read or written.</summary>
	private bool _isSaved;

	/// <summary>Creates a store over an optional projects file, read on first use, importing from an older conversation history file while it does not exist.</summary>
	/// <param name="path">The projects file, or null to keep projects in memory.</param>
	/// <param name="legacyPath">The conversation history file whose <c>Projects</c> array is imported once, or null.</param>
	public ProjectStore( string? path = null, string? legacyPath = null )
	{
		_path = path is null ? null : Path.GetFullPath( path );
		_legacyPath = legacyPath is null ? null : Path.GetFullPath( legacyPath );
	}

	/// <summary>Gets the per-user projects file.</summary>
	public static string DefaultPath => Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "Lokn", "projects.json" );

	/// <summary>Gets the per-user conversation history file that held projects before this file existed.</summary>
	public static string LegacyPath => Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "Lokn", "conversations.json" );

	/// <summary>Raised on the UI thread after a project is deleted, so features can let go of what belonged to it.</summary>
	public event Action<ProjectInfo>? Deleted;

	/// <summary>Reports a failed read or write of the projects file.</summary>
	public event Action<string>? Failed;

	/// <summary>Gets the most recent read or write error, or null.</summary>
	public string? LastError
	{
		get; private set;
	}

	/// <summary>Gets whether every project was read: false when the projects file, or the history they are imported from, could not be read, so projects may be missing.</summary>
	public bool IsComplete
	{
		get
		{
			EnsureLoaded();
			return _canSave;
		}
	}

	/// <summary>Gets whether the projects file holds the projects, so the copy in an older conversation history file is no longer needed; always true in memory.</summary>
	public bool IsSaved
	{
		get
		{
			EnsureLoaded();
			return _path is null || _isSaved;
		}
	}

	/// <summary>Gets the projects of one profile ordered by display name.</summary>
	public IReadOnlyList<ProjectInfo> Projects( SettingsProfile profile )
	{
		EnsureLoaded();
		_ = _version.Value;
		return _projects.Values
			.Where( project => project.Profile == profile )
			.OrderBy( project => project.Name, StringComparer.OrdinalIgnoreCase )
			.ToArray();
	}

	/// <summary>Gets a project by identifier, or null when it no longer exists.</summary>
	public ProjectInfo? Find( Guid id )
	{
		EnsureLoaded();
		_ = _version.Value;
		return _projects.GetValueOrDefault( id );
	}

	/// <summary>Gets a project by identifier or throws when it no longer exists.</summary>
	/// <exception cref="KeyNotFoundException">No project has the identifier.</exception>
	public ProjectInfo Get( Guid id ) =>
		Find( id ) ?? throw new KeyNotFoundException( "The project no longer exists." );

	/// <summary>Creates a named project with a working directory in one profile.</summary>
	public ProjectInfo Create( SettingsProfile profile, string name, string directory )
	{
		EnsureLoaded();
		ArgumentException.ThrowIfNullOrWhiteSpace( name );
		ArgumentException.ThrowIfNullOrWhiteSpace( directory );
		var project = new ProjectInfo( Guid.NewGuid(), profile, name.Trim(), Path.GetFullPath( directory ) );
		_projects.Add( project.Id, project );
		MarkChanged();
		return project;
	}

	/// <summary>Updates a project's name and working directory while retaining its identity.</summary>
	public void Update( Guid id, string name, string directory )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( name );
		ArgumentException.ThrowIfNullOrWhiteSpace( directory );
		_projects[id] = Get( id ) with
		{
			Name = name.Trim(),
			Directory = Path.GetFullPath( directory ),
		};
		MarkChanged();
	}

	/// <summary>Deletes a project and raises <see cref="Deleted"/>; what belonged to it, such as its chats, is kept by its owners.</summary>
	/// <returns>Whether the project existed.</returns>
	public bool Delete( Guid id )
	{
		EnsureLoaded();
		if ( !_projects.Remove( id, out var project ) )
			return false;

		MarkChanged();
		Deleted?.Invoke( project );
		return true;
	}

	/// <summary>Reads the projects file once, before the first query or change; while it does not exist, imports the projects of the older conversation history file and writes them to it.</summary>
	internal void EnsureLoaded()
	{
		if ( _isLoaded )
			return;

		_isLoaded = true;
		if ( _path is null )
			return;

		var source = _path;
		try
		{
			if ( File.Exists( _path ) )
			{
				ProjectsDocument document;
				using ( var stream = File.OpenRead( _path ) )
					document = JsonSerializer.Deserialize<ProjectsDocument>( stream, JsonOptions ) ?? throw new JsonException( "The projects document is empty." );
				if ( document.Version != FileVersion || document.Projects is null )
					throw new JsonException( "The projects document version or collection is unsupported." );

				Add( document.Projects );
				_isSaved = true;
				return;
			}

			if ( _legacyPath is null || !File.Exists( _legacyPath ) )
				return;

			source = _legacyPath;
			LegacyDocument legacy;
			using ( var stream = File.OpenRead( _legacyPath ) )
				legacy = JsonSerializer.Deserialize<LegacyDocument>( stream, JsonOptions ) ?? throw new JsonException( "The history document is empty." );
			if ( !_legacyVersions.Contains( legacy.Version ) )
				throw new JsonException( "The history document version is unsupported." );

			Add( legacy.Projects ?? [] );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or JsonException or ArgumentException )
		{
			_projects.Clear();
			_canSave = false;
			MarkFailed( $"Could not load projects from {source}: {error.Message} The original file is preserved; changes stay in memory." );
			return;
		}

		// Imported from the conversation history: the projects file takes them over, so the next start reads it instead.
		Save();
	}

	/// <summary>Validates stored projects and adds them, all or none.</summary>
	/// <exception cref="JsonException">A project is empty or invalid.</exception>
	/// <exception cref="ArgumentException">Two projects share an identifier.</exception>
	private void Add( List<ProjectInfo?> projects )
	{
		var valid = new Dictionary<Guid, ProjectInfo>();
		foreach ( var project in projects )
		{
			if ( project is null || project.Id == Guid.Empty || !Enum.IsDefined( project.Profile )
				|| string.IsNullOrWhiteSpace( project.Name ) || string.IsNullOrWhiteSpace( project.Directory ) )
				throw new JsonException( "The document contains an invalid project." );

			valid.Add( project.Id, project );
		}

		foreach ( var (id, project) in valid )
			_projects.Add( id, project );
	}

	/// <summary>Notifies readers of the project list and writes it.</summary>
	private void MarkChanged()
	{
		_version.Value++;
		Save();
	}

	/// <summary>Writes every project through a sibling temporary file, so the previous complete file survives a failed write.</summary>
	private void Save()
	{
		if ( _path is null )
			return;

		if ( !_canSave )
		{
			MarkFailed( "Project changes stay in memory. Correct the projects file and restart Lokn to save them." );
			return;
		}

		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( _path )! );
			var temporary = _path + ".tmp";
			using ( var stream = File.Create( temporary ) )
				JsonSerializer.Serialize( stream, new ProjectsDocument { Projects = [.. _projects.Values] }, JsonOptions );
			File.Move( temporary, _path, overwrite: true );
			_isSaved = true;
			LastError = null;
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			MarkFailed( $"Project changes stay in memory but could not be saved: {error.Message}" );
		}
	}

	/// <summary>Records and reports a read or write error while keeping the live projects available.</summary>
	private void MarkFailed( string error )
	{
		LastError = error;
		Failed?.Invoke( error );
	}

	/// <summary>Stores every project of every profile.</summary>
	private sealed record ProjectsDocument
	{
		/// <summary>Gets the file format version.</summary>
		public int Version { get; init; } = FileVersion;

		/// <summary>Gets all profile-pinned projects.</summary>
		public List<ProjectInfo?> Projects { get; init; } = [];
	}

	/// <summary>Reads only the version and the projects of a conversation history file from before projects had their own file.</summary>
	private sealed record LegacyDocument
	{
		/// <summary>Gets the history file format version.</summary>
		public int Version { get; init; }

		/// <summary>Gets the projects it held, or null when it holds none.</summary>
		public List<ProjectInfo?>? Projects { get; init; }
	}
}
