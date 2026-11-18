using System.Diagnostics;
using Lokn.Core;

namespace Lokn.Modules.Hotload;

/// <summary>Compiles modules from source directories and reloads them when their files change.</summary>
/// <remarks>
/// Modules come from the per-user directory, one module per subdirectory, and from any folders listed in the
/// <see cref="Folders"/> setting: a listed folder holding .cs files is one module, any other holds one per subdirectory.
/// </remarks>
public sealed partial class HotloadModule : ILoknModule
{
	/// <summary>Defines the quiet period in milliseconds after a file event before sources are rescanned.</summary>
	private const int DebounceMilliseconds = 300;

	/// <summary>Defines extra folders of source modules, kept wherever the user likes, such as in their own repository.</summary>
	internal static readonly Setting<string> Folders = new(
		"hotload.folders",
		"Module folders",
		"More folders with source modules. Separate them with semicolons.",
		defaultValue: "",
		category: "Modules" );

	/// <summary>Stores the per-user directory whose subdirectories each hold one module's sources.</summary>
	private readonly string _path;

	/// <summary>Stores where compiled source modules are kept between runs, or null to compile every module at every start.</summary>
	private readonly string? _cachePath;

	/// <summary>Stores the last sources read per module directory, keyed by full path and guarded by the scan lock.</summary>
	private readonly Dictionary<string, SourceFile[]> _sources = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Stores the module identifier loaded from each directory, keyed by full path and used on the UI thread.</summary>
	private readonly Dictionary<string, string> _moduleIds = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Serializes the startup scan and overlapping debounce ticks.</summary>
	private readonly Lock _scanLock = new();

	/// <summary>Coalesces the several file events one editor save raises into a single rescan.</summary>
	private readonly Timer _debounce;

	/// <summary>Stores one watcher per watched folder while the module runs.</summary>
	private readonly List<FileSystemWatcher> _watchers = [];

	/// <summary>Stores the configured extra folders as full paths; replaced whole on the UI thread and read by scans.</summary>
	private volatile string[] _folders = [];

	/// <summary>Stores the running module context, or null before the first start.</summary>
	private ModuleContext? _context;

	/// <summary>Gets the per-user directory holding hot-reloaded module sources.</summary>
	public static string DefaultPath => Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "Lokn", "modules" );

	/// <summary>Gets the per-user directory holding source module builds kept between runs, so an unchanged module starts without compiling.</summary>
	public static string DefaultCachePath => Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "Lokn", "cache", "modules" );

	/// <summary>Gets the stable hotload module identifier.</summary>
	public string Id => "hotload";

	/// <summary>Gets the feature's display name.</summary>
	public string Title => "Hotload";

	/// <summary>Creates a hotloader for the module directories below a path, optionally keeping their builds between runs.</summary>
	/// <param name="path">The directory whose subdirectories each hold one module's sources.</param>
	/// <param name="cachePath">Where builds are kept between runs, or null to compile every module at every start.</param>
	public HotloadModule( string path, string? cachePath = null )
	{
		_path = Path.GetFullPath( path );
		_cachePath = cachePath is null ? null : Path.GetFullPath( cachePath );
		_debounce = new( _ => PostChanges(), null, Timeout.Infinite, Timeout.Infinite );
	}

	/// <summary>Registers the module folders setting and the command that reveals the module source directory.</summary>
	public void Register( ModuleBuilder builder )
	{
		builder
			.Setting( Folders )
			.Command( "open-folder", "Hotload: open modules folder", _ => Process.Start( new ProcessStartInfo( _path )
			{
				UseShellExecute = true,
			} ) );
	}

	/// <summary>Loads every source module, then watches the directories for edits and the setting for other folders.</summary>
	public void Start( ModuleContext context )
	{
		_context = context;
		Directory.CreateDirectory( _path );
		EnsureEditorSettings();
		EnsureGuide();
		_folders = ReadFolders( context.App );

		// Compile before returning so a restored workspace finds these modules' panel types.
		foreach ( var change in ScanChanges() )
			Apply( change );

		UpdateWatchers();
		context.App.Settings.Changed += UpdateFolders;
		context.App.ModuleFolders.Changed += UpdateContributedFolders;
	}

	/// <summary>Stops watching while leaving the already loaded modules running.</summary>
	public void Stop()
	{
		if ( _context is { } context )
		{
			context.App.Settings.Changed -= UpdateFolders;
			context.App.ModuleFolders.Changed -= UpdateContributedFolders;
		}

		// Each watcher owns a native directory handle and would keep raising events for a disabled feature.
		foreach ( var watcher in _watchers )
			watcher.Dispose();
		_watchers.Clear();
	}

	/// <summary>Follows an edit of the folders setting: watches the new folders and rescans, loading and removing their modules.</summary>
	private void UpdateFolders()
	{
		if ( _context is not { Lifetime.IsCancellationRequested: false } context )
			return;

		var folders = ReadFolders( context.App );
		if ( folders.SequenceEqual( _folders, StringComparer.OrdinalIgnoreCase ) )
			return;

		_folders = folders;
		UpdateWatchers();
		_debounce.Change( 0, Timeout.Infinite );
	}

	/// <summary>Loads and removes the modules of folders other modules contribute at once, so a panel opened right after finds its type.</summary>
	private void UpdateContributedFolders()
	{
		if ( _context is not { Lifetime.IsCancellationRequested: false } context )
			return;

		var folders = ReadFolders( context.App );
		if ( folders.SequenceEqual( _folders, StringComparer.OrdinalIgnoreCase ) )
			return;

		_folders = folders;
		UpdateWatchers();
		foreach ( var change in ScanChanges() )
			Apply( change );
	}

	/// <summary>Watches the per-user directory and every configured folder that exists, reporting the ones that do not.</summary>
	private void UpdateWatchers()
	{
		foreach ( var watcher in _watchers )
			watcher.Dispose();
		_watchers.Clear();

		foreach ( var folder in _folders.Prepend( _path ) )
		{
			if ( !Directory.Exists( folder ) )
			{
				_context!.App.ReportError( "Hotload", new DirectoryNotFoundException( $"Module folder not found: {folder}" ) );
				continue;
			}

			var watcher = new FileSystemWatcher( folder )
			{
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
			};
			watcher.Changed += MarkDirty;
			watcher.Created += MarkDirty;
			watcher.Deleted += MarkDirty;
			watcher.Renamed += MarkDirty;
			watcher.EnableRaisingEvents = true;
			_watchers.Add( watcher );
		}
	}

	/// <summary>Restarts the quiet period after any file or directory event.</summary>
	private void MarkDirty( object sender, FileSystemEventArgs change ) => _debounce.Change( DebounceMilliseconds, Timeout.Infinite );

	/// <summary>Recompiles changed modules on the timer thread and applies them on the UI thread.</summary>
	private void PostChanges()
	{
		if ( _context is not { Lifetime.IsCancellationRequested: false } context )
			return;

		var changes = ScanChanges();
		if ( changes.Count == 0 )
			return;

		context.Post( () =>
		{
			foreach ( var change in changes )
				Apply( change );
		} );
	}

	/// <summary>Compiles each module directory whose sources differ from the last scan.</summary>
	private List<ModuleChange> ScanChanges()
	{
		var changes = new List<ModuleChange>();
		lock ( _scanLock )
		{
			var present = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
			foreach ( var directory in FindModuleDirectories() )
			{
				var name = Path.GetFileName( directory );
				try
				{
					var sources = ReadSources( directory );
					if ( sources.Length == 0 )
						continue;

					present.Add( directory );
					EnsureProject( directory );
					if ( _sources.TryGetValue( directory, out var previous ) && previous.SequenceEqual( sources ) )
						continue;

					// Remember sources before compiling so a broken edit is not recompiled until it changes.
					_sources[directory] = sources;
					changes.Add( ModuleChange.Compiled( directory, ModuleCache.Load( name, directory, sources, _cachePath ) ) );
				}
				catch ( Exception error )
				{
					// An unreadable or broken module keeps its running implementation.
					present.Add( directory );
					changes.Add( ModuleChange.Failed( directory, error ) );
				}
			}

			foreach ( var directory in _sources.Keys.Except( present, StringComparer.OrdinalIgnoreCase ).ToArray() )
			{
				_sources.Remove( directory );
				changes.Add( ModuleChange.Removed( directory ) );
			}
		}

		return changes;
	}

	/// <summary>Lists every module directory: each subdirectory of the per-user directory, then each configured folder or its subdirectories.</summary>
	private IEnumerable<string> FindModuleDirectories()
	{
		static IEnumerable<string> Subdirectories( string folder ) => Directory.Exists( folder ) ? Directory.GetDirectories( folder ) : [];

		var directories = new List<string>( Subdirectories( _path ) );
		foreach ( var folder in _folders )
		{
			if ( !Directory.Exists( folder ) )
				continue;

			if ( Directory.EnumerateFiles( folder, "*.cs" ).Any() )
				directories.Add( folder );
			else
				directories.AddRange( Subdirectories( folder ) );
		}

		return directories.Select( Path.GetFullPath ).Distinct( StringComparer.OrdinalIgnoreCase );
	}

	/// <summary>Reads the configured folders, then the ones loaded modules contribute, as distinct full paths, expanding environment variables and leaving out the per-user directory.</summary>
	private string[] ReadFolders( LoknApplication app )
	{
		var folders = new List<string>();
		foreach ( var entry in app.Settings.Get( Folders ).Split( [';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
		{
			try
			{
				var folder = Path.TrimEndingDirectorySeparator( Path.GetFullPath( Environment.ExpandEnvironmentVariables( entry.Trim( '"' ) ) ) );
				if ( !string.Equals( folder, Path.TrimEndingDirectorySeparator( _path ), StringComparison.OrdinalIgnoreCase ) )
					folders.Add( folder );
			}
			catch ( Exception error ) when ( error is ArgumentException or NotSupportedException or PathTooLongException )
			{
				app.ReportError( "Hotload", new ArgumentException( $"Not a folder path: {entry}", error ) );
			}
		}

		// Contributed folders, such as trusted projects' .lokn\modules, hold one module per subfolder like the per-user directory.
		folders.AddRange( app.ModuleFolders.Folders.Where( folder => !string.Equals( folder, Path.TrimEndingDirectorySeparator( _path ), StringComparison.OrdinalIgnoreCase ) ) );
		return [.. folders.Distinct( StringComparer.OrdinalIgnoreCase )];
	}

	/// <summary>Reads a module directory's C# files in a stable order.</summary>
	private static SourceFile[] ReadSources( string directory )
	{
		return Directory
			.EnumerateFiles( directory, "*.cs", SearchOption.AllDirectories )
			.Order( StringComparer.OrdinalIgnoreCase )
			.Select( file => new SourceFile( Path.GetRelativePath( directory, file ), File.ReadAllText( file ) ) )
			.ToArray();
	}

	/// <summary>Loads, reloads, or removes one module on the UI thread, containing feature failures.</summary>
	private void Apply( ModuleChange change )
	{
		var app = _context!.App;
		try
		{
			if ( change.Error is not null )
				throw change.Error;

			_moduleIds.TryGetValue( change.Directory, out var previousId );
			if ( change.Module is not { } module )
			{
				if ( previousId is not null )
					app.Remove( previousId );

				_moduleIds.Remove( change.Directory );
				return;
			}

			// Reload replaces by identifier, so a source module must not take over another module's.
			if ( module.Id != previousId && app.AvailableModules.Any( known => known.Id == module.Id ) )
				throw new InvalidOperationException( $"Module ID is already in use: {module.Id}" );

			if ( previousId is not null && previousId != module.Id )
				app.Remove( previousId );

			_moduleIds[change.Directory] = module.Id;
			app.Reload( module );
		}
		catch ( Exception error )
		{
			app.ReportError( $"Hotload {Path.GetFileName( change.Directory )}", error );
		}
	}

	/// <summary>Describes the outcome of scanning one module directory.</summary>
	private readonly struct ModuleChange
	{
		/// <summary>Gets the module directory's full path.</summary>
		internal string Directory
		{
			get; private init;
		}

		/// <summary>Gets the freshly compiled module, or null when it failed or was removed.</summary>
		internal ILoknModule? Module
		{
			get; private init;
		}

		/// <summary>Gets the read or compile failure, or null on success.</summary>
		internal Exception? Error
		{
			get; private init;
		}

		/// <summary>Creates the outcome for sources that compiled into a module.</summary>
		internal static ModuleChange Compiled( string directory, ILoknModule module ) => new()
		{
			Directory = directory,
			Module = module,
		};

		/// <summary>Creates the outcome for sources that could not be read or compiled.</summary>
		internal static ModuleChange Failed( string directory, Exception error ) => new()
		{
			Directory = directory,
			Error = error,
		};

		/// <summary>Creates the outcome for a directory that no longer holds sources.</summary>
		internal static ModuleChange Removed( string directory ) => new()
		{
			Directory = directory,
		};
	}
}
