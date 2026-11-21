using System.Reflection;
using System.Runtime.Loader;
using Lokn.Core;

namespace Lokn;

/// <summary>Loads feature modules from DLLs beside the application and reloads them when they are rebuilt or updated.</summary>
/// <remarks>
/// A module DLL can come from two folders: the bundled one beside the executable, and, in an installed Loom, the folder downloaded
/// module updates go to. The newest version of each module loads, but only when <see cref="ModuleCompatibility"/> finds that every type
/// and member it uses exists in the running application; otherwise the next older version loads, down to the bundled one.
/// The headless daemon's host, given <see cref="DaemonRules"/>, creates only the allowed modules and follows the window's choices: it reads
/// the same module states file again whenever it changes, and holds every reload back until no chat's agent works.
/// </remarks>
internal sealed class ModuleHost
{
	/// <summary>Defines the file name pattern of module assemblies.</summary>
	private const string AssemblyPattern = "Lokn.Modules.*.dll";

	/// <summary>Defines the folder inside the updates folder where downloaded updates wait until they are applied.</summary>
	private const string StagedFolder = "staged";

	/// <summary>Defines the quiet period in milliseconds after a file event before rebuilt assemblies are read.</summary>
	private const int DebounceMilliseconds = 300;

	/// <summary>Stores the application whose modules are hosted.</summary>
	private readonly LoknApplication _app;

	/// <summary>Stores the directory holding the bundled module assemblies.</summary>
	private readonly string _directory;

	/// <summary>Stores the directory holding applied module updates, or null when this application takes no module updates.</summary>
	private readonly string? _updatesDirectory;

	/// <summary>Stores the file that records each reload's outcome for scripts and agents.</summary>
	private readonly string _logPath;

	/// <summary>Stores the loaded assemblies by simple name; used on the UI thread.</summary>
	private readonly Dictionary<string, HostedAssembly> _assemblies = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Stores, per assembly name, the files last seen for it with their write times and sizes, guarded by the scan lock.</summary>
	private readonly Dictionary<string, string> _stamps = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Serializes the startup scan and overlapping debounce ticks.</summary>
	private readonly Lock _scanLock = new();

	/// <summary>Coalesces the file events one build raises into a single reload.</summary>
	private readonly Timer _debounce;

	/// <summary>Stores the watchers of the module folders while the host runs.</summary>
	private readonly List<FileSystemWatcher> _watchers = [];

	/// <summary>Stores which installed modules start on, or null when every module starts on and the user's choices are not remembered.</summary>
	private readonly ModuleStates? _states;

	/// <summary>Stores the daemon's rules, or null in a window.</summary>
	private readonly DaemonRules? _daemon;

	/// <summary>Stores, in the daemon, the changed assemblies waiting for every chat turn to end, by simple name; used on the UI thread.</summary>
	private readonly Dictionary<string, AssemblyImage> _deferred = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Coalesces the file events one save of the module states file raises into a single read; the daemon's only.</summary>
	private readonly Timer _statesDebounce;

	/// <summary>Stores the startup work <see cref="BeginStart"/> runs on a background thread until <see cref="Start"/> takes its result; null otherwise.</summary>
	private Task<Generation?>? _starting;

	/// <summary>Gets the directory beside the executable where module projects deploy their DLLs.</summary>
	internal static string DefaultDirectory => Path.Combine( AppContext.BaseDirectory, "modules" );

	/// <summary>Gets the file recording reload outcomes: <c>modules.log</c> in <see cref="LoknPaths.TempRoot"/>, a bare instance's own folder for one.</summary>
	internal static string DefaultLogPath => Path.Combine( LoknPaths.TempRoot, "modules.log" );

	/// <summary>Gets the folder downloaded updates wait in until they are applied, or null when this application takes no module updates.</summary>
	internal string? StagedDirectory => _updatesDirectory is null ? null : Path.Combine( _updatesDirectory, StagedFolder );

	/// <summary>Gets the version of every loaded module assembly by simple name; read on the UI thread.</summary>
	internal IReadOnlyDictionary<string, Version> Versions => _assemblies.ToDictionary( item => item.Key, item => item.Value.Image.Version, StringComparer.OrdinalIgnoreCase );

	/// <summary>Raised on the UI thread when an update does not fit the running application and is removed; gives its assembly name, version and the first problem.</summary>
	internal event Action<string, Version, string>? Refused;

	/// <summary>Creates a host for the module assemblies in a directory.</summary>
	/// <param name="app">The application whose modules are hosted.</param>
	/// <param name="directory">The directory of the bundled module assemblies.</param>
	/// <param name="updatesDirectory">The directory applied module updates go to, or null to take no module updates.</param>
	/// <param name="logPath">The file each load's outcome is appended to.</param>
	/// <param name="states">Which modules start on, or null when every module starts on and the user's choices are not remembered.</param>
	/// <param name="daemon">The daemon's rules, or null in a window, which applies staged updates and saves the user's choices.</param>
	internal ModuleHost( LoknApplication app, string directory, string? updatesDirectory, string logPath, ModuleStates? states, DaemonRules? daemon = null )
	{
		_app = app;
		_directory = directory;
		_updatesDirectory = updatesDirectory;
		_logPath = logPath;
		_states = states;
		_daemon = daemon;
		_debounce = new( _ => PostChanges(), null, Timeout.Infinite, Timeout.Infinite );
		_statesDebounce = new( _ => _app.Post( UpdateStates ), null, Timeout.Infinite, Timeout.Infinite );
		// The daemon only follows the window's choices; the window writes the file.
		if ( states is not null && daemon is null )
			app.ModuleToggled += states.Set;
	}

	/// <summary>Begins <see cref="Start"/>'s work that touches no application state on a background thread, so it runs while the window opens:
	/// applying staged updates, then reading, loading and checking every module assembly. <see cref="Start"/> waits for it, then starts the modules.</summary>
	internal void BeginStart() => _starting ??= Task.Run( Prepare );

	/// <summary>Applies staged updates, loads every module assembly in dependency order, then watches the folders for new ones. The daemon leaves
	/// staged and outdated updates to Loom on this PC, which owns the updates folder, and also watches the module states file.</summary>
	internal void Start()
	{
		Generation? generation;
		using ( PerfTrace.Step( "modules.prepared" ) )
			generation = _starting is { } starting ? starting.GetAwaiter().GetResult() : Prepare();
		_starting = null;
		if ( generation is not null )
			Install( generation, isInitial: true );
		Watch( _directory, AssemblyPattern, _debounce );
		if ( _updatesDirectory is not null )
			Watch( _updatesDirectory, AssemblyPattern, _debounce );
		if ( _daemon is null )
			return;

		// A held-back reload applies once the Remote module publishes that no chat works.
		_app.HostStatusChanged += PostDeferred;
		if ( _states is not null )
		{
			var folder = Path.GetDirectoryName( _states.FilePath )!;
			Directory.CreateDirectory( folder );
			Watch( folder, Path.GetFileName( _states.FilePath ), _statesDebounce );
		}
	}

	/// <summary>Applies staged updates and reads, loads and checks every module assembly, starting nothing; null when there is none.</summary>
	private Generation? Prepare()
	{
		using var step = PerfTrace.Step( "modules.prepare" );
		Directory.CreateDirectory( _directory );
		if ( _updatesDirectory is not null )
		{
			Directory.CreateDirectory( _updatesDirectory );
			if ( _daemon is null )
			{
				ApplyStaged();
				RemoveOutdatedUpdates();
			}
		}

		var changes = ReadChanges();
		return changes.Count == 0 ? null : LoadChanges( changes, isCollectible: false );
	}

	/// <summary>Stops watching while leaving the loaded modules running.</summary>
	internal void Stop()
	{
		// Each watcher owns a native directory handle that would otherwise keep the directory in use.
		foreach ( var watcher in _watchers )
			watcher.Dispose();
		_watchers.Clear();
		_app.HostStatusChanged -= PostDeferred;
	}

	/// <summary>Moves downloaded updates from the staged folder into use; the folder watcher then reloads their modules.</summary>
	/// <returns>How many updates moved.</returns>
	internal int ApplyStaged()
	{
		if ( _updatesDirectory is null || StagedDirectory is not { } staged || !Directory.Exists( staged ) )
			return 0;

		var count = 0;
		foreach ( var file in Directory.GetFiles( staged, AssemblyPattern ) )
		{
			File.Move( file, Path.Combine( _updatesDirectory, Path.GetFileName( file ) ), overwrite: true );
			count++;
		}

		return count;
	}

	/// <summary>Finds which candidate updates would be refused beside the running modules, without starting them.</summary>
	/// <param name="candidates">Each candidate's assembly name and bytes.</param>
	/// <returns>The refused candidates' assembly names, each with the first problem found.</returns>
	/// <remarks>Use it on the UI thread. The candidates are checked together, so an update that needs another update passes with it.</remarks>
	internal IReadOnlyDictionary<string, string> Check( IReadOnlyList<(string Name, byte[] Image)> candidates )
	{
		var changes = candidates
			.Select( candidate => new AssemblyImage( candidate.Name, candidate.Image, null, ModuleCompatibility.ReadVersion( candidate.Image ), Path: null, IsUpdate: true,
				Fallback: _assemblies.GetValueOrDefault( candidate.Name )?.Image ) )
			.ToList();
		var refused = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		try
		{
			LoadCompatible( BuildAffected( changes ), isCollectible: true, ( image, problem ) =>
			{
				if ( changes.Contains( image ) )
					refused[image.Name] = problem;
			} );
		}
		catch ( Exception error )
		{
			// A running module that no longer loads beside them is a fault of this application, not of the updates; refuse them all.
			foreach ( var change in changes )
				refused.TryAdd( change.Name, error.Message );
		}

		return refused;
	}

	/// <summary>Starts watching one folder for written files that match a pattern, restarting a quiet period on each event.</summary>
	/// <param name="directory">The folder.</param>
	/// <param name="filter">The file name pattern.</param>
	/// <param name="debounce">The timer whose quiet period each event restarts.</param>
	private void Watch( string directory, string filter, Timer debounce )
	{
		var watcher = new FileSystemWatcher( directory, filter )
		{
			NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
		};
		void MarkDirty( object sender, FileSystemEventArgs change ) => debounce.Change( DebounceMilliseconds, Timeout.Infinite );
		watcher.Changed += MarkDirty;
		watcher.Created += MarkDirty;
		watcher.Renamed += MarkDirty;
		watcher.Deleted += MarkDirty;
		watcher.EnableRaisingEvents = true;
		_watchers.Add( watcher );
	}

	/// <summary>Reads rebuilt assemblies on the timer thread and reloads them on the UI thread.</summary>
	private void PostChanges()
	{
		var changes = ReadChanges();
		if ( changes.Count > 0 )
			_app.Post( () => Receive( changes ) );
	}

	/// <summary>Reloads changed assemblies on the UI thread; the daemon holds them back while a chat's agent works, since a reload would stop its turn.</summary>
	/// <param name="changes">The changed assemblies.</param>
	private void Receive( IReadOnlyList<AssemblyImage> changes )
	{
		if ( _daemon is null )
		{
			Apply( changes );
			return;
		}

		// A newer change to the same assembly replaces the one waiting.
		foreach ( var change in changes )
			_deferred[change.Name] = change;
		if ( _app.HostStatus is { Working: > 0 } status )
			UpdateLog( $"DEFERRED {string.Join( ", ", _deferred.Keys.Order( StringComparer.OrdinalIgnoreCase ) )}: {status.Working} {( status.Working == 1 ? "chat is" : "chats are" )} working; reloads once none is" );
		else
			ApplyDeferred();
	}

	/// <summary>Queues a look at the held-back reloads after the daemon's status changed, outside the change that raised it.</summary>
	private void PostDeferred() => _app.Post( ApplyDeferred );

	/// <summary>Applies the held-back reloads once no chat's agent works; on the UI thread.</summary>
	private void ApplyDeferred()
	{
		if ( _deferred.Count == 0 || _app.HostStatus is { Working: > 0 } )
			return;

		var changes = _deferred.Values.ToList();
		_deferred.Clear();
		Apply( changes );
	}

	/// <summary>Reads the module states file again after Loom on this PC saved it and turns the daemon's modules on and off to match; on the UI thread.</summary>
	/// <remarks>A module turned off stops with every module that needs it; the required modules and the modules they need stay on whatever the
	/// file says. A module turned on starts after the modules it needs, and stays off while one of them is off, as at startup.</remarks>
	private void UpdateStates()
	{
		// A file read while it is being written is read again after the write's own event.
		if ( _daemon is null || _states is null || !_states.Reload() )
			return;

		var known = OrderModules( _app.AvailableModules.Where( IsAllowed ).ToArray() );
		var wanted = ComputeWanted( known );
		var loaded = _app.Modules.Select( module => module.Id ).ToHashSet( StringComparer.Ordinal );
		var off = known.Where( module => loaded.Contains( module.Id ) && !wanted.Contains( module.Id ) ).Select( module => module.Id ).ToHashSet( StringComparer.Ordinal );
		var isGrowing = off.Count > 0;
		while ( isGrowing )
		{
			var dependents = known.Where( module => loaded.Contains( module.Id ) && !off.Contains( module.Id ) && module.Dependencies.Any( off.Contains ) ).ToArray();
			foreach ( var dependent in dependents )
				off.Add( dependent.Id );
			isGrowing = dependents.Length > 0;
		}

		// Dependents stop before the modules they need.
		foreach ( var module in Enumerable.Reverse( known ).Where( module => off.Contains( module.Id ) ) )
		{
			if ( _app.Unload( module.Id ) )
				UpdateLog( $"DISABLED {module.Id}" );
		}

		foreach ( var module in known.Where( module => wanted.Contains( module.Id ) && !_app.Modules.Any( loaded => loaded.Id == module.Id ) ) )
		{
			if ( module.Dependencies.All( id => _app.Modules.Any( loaded => loaded.Id == id ) ) && _app.Enable( module.Id ) )
				UpdateLog( $"ENABLED {module.Id}" );
		}
	}

	/// <summary>Finds the modules the daemon runs: those that cannot unload, those the module states turn on, or every one before the user ever chose,
	/// and the required modules with every module they need, directly or through another.</summary>
	/// <param name="modules">The allowed modules known to the daemon.</param>
	private HashSet<string> ComputeWanted( IReadOnlyList<ILoknModule> modules )
	{
		var rules = _daemon!;
		var wanted = modules
			.Where( module => !module.CanUnload || rules.Required.Contains( module.Id ) || _states is null || !_states.IsChosen || _states.IsOn( module.Id ) )
			.Select( module => module.Id )
			.ToHashSet( StringComparer.Ordinal );
		var pending = new Stack<string>( rules.Required );
		while ( pending.TryPop( out var id ) )
		{
			foreach ( var dependency in modules.FirstOrDefault( module => module.Id == id )?.Dependencies ?? [] )
			{
				if ( wanted.Add( dependency ) )
					pending.Push( dependency );
			}
		}

		return wanted;
	}

	/// <summary>Deletes updates no newer than the bundled module, which an application update has caught up with.</summary>
	private void RemoveOutdatedUpdates()
	{
		foreach ( var file in Directory.GetFiles( _updatesDirectory!, AssemblyPattern ) )
		{
			var bundled = Path.Combine( _directory, Path.GetFileName( file ) );
			if ( File.Exists( bundled ) && ModuleCompatibility.ReadVersion( File.ReadAllBytes( file ) ) <= ModuleCompatibility.ReadVersion( File.ReadAllBytes( bundled ) ) )
				File.Delete( file );
		}
	}

	/// <summary>Lists each module assembly's files by simple name: the bundled one, then the applied update.</summary>
	private Dictionary<string, List<string>> ListFiles()
	{
		var files = new Dictionary<string, List<string>>( StringComparer.OrdinalIgnoreCase );
		string[] directories = _updatesDirectory is null || !Directory.Exists( _updatesDirectory ) ? [_directory] : [_directory, _updatesDirectory];
		foreach ( var directory in directories )
		{
			foreach ( var file in Directory.GetFiles( directory, AssemblyPattern ) )
			{
				var name = Path.GetFileNameWithoutExtension( file );
				if ( !files.TryGetValue( name, out var list ) )
					files[name] = list = [];
				list.Add( file );
			}
		}

		return files;
	}

	/// <summary>Describes a module's files by path, write time and size, so any change to one of them shows.</summary>
	private static string ComputeStamp( IEnumerable<string> files )
	{
		return string.Join( "|", files.Select( file => new FileInfo( file ) ).Select( info => $"{info.FullName}*{info.LastWriteTimeUtc.Ticks}*{info.Length}" ) );
	}

	/// <summary>Reads every module assembly whose files changed since the last scan.</summary>
	private List<AssemblyImage> ReadChanges()
	{
		var changes = new List<AssemblyImage>();
		lock ( _scanLock )
		{
			foreach ( var (name, files) in ListFiles() )
			{
				try
				{
					var stamp = ComputeStamp( files );
					if ( _stamps.TryGetValue( name, out var previous ) && previous == stamp )
						continue;

					changes.Add( ReadImage( name, files ) );
					_stamps[name] = stamp;
				}
				catch ( IOException )
				{
					// A build or a download is still writing this file; look again once it has finished.
					_debounce.Change( DebounceMilliseconds, Timeout.Infinite );
				}
			}
		}

		return changes;
	}

	/// <summary>Reads a module's files into its newest image, which falls back to the bundled one when it is refused.</summary>
	private AssemblyImage ReadImage( string name, IReadOnlyList<string> files )
	{
		var bundled = files.FirstOrDefault( file => !IsUpdate( file ) ) is { } path ? ReadFile( name, path, fallback: null ) : null;
		var update = files.FirstOrDefault( IsUpdate ) is { } updatePath ? ReadFile( name, updatePath, bundled ) : null;
		// An update only counts while it is newer than the bundled module.
		return update is not null && (bundled is null || update.Version > bundled.Version) ? update : bundled ?? update!;
	}

	/// <summary>Reads one module file with its symbols; reading the bytes leaves the file unlocked, so a build or download can replace it.</summary>
	private AssemblyImage ReadFile( string name, string file, AssemblyImage? fallback )
	{
		var image = File.ReadAllBytes( file );
		var symbols = Path.ChangeExtension( file, ".pdb" );
		return new( name, image, File.Exists( symbols ) ? File.ReadAllBytes( symbols ) : null, ModuleCompatibility.ReadVersion( image ), file, IsUpdate( file ), fallback );
	}

	/// <summary>Tells whether a file is an applied update rather than a bundled module.</summary>
	private bool IsUpdate( string file ) => _updatesDirectory is not null && string.Equals( Path.GetDirectoryName( file ), _updatesDirectory, StringComparison.OrdinalIgnoreCase );

	/// <summary>Loads changed assemblies together with the assemblies that reference them, then swaps their modules in.</summary>
	private void Apply( IReadOnlyList<AssemblyImage> changes )
	{
		if ( changes.Count == 0 )
			return;

		Install( LoadChanges( changes, isCollectible: true ), isInitial: false );
	}

	/// <summary>Loads changed assemblies together with the assemblies that reference them and checks that they fit, without starting a module or
	/// reporting anything; before the modules start, this touches no application state and runs off the UI thread.</summary>
	/// <param name="changes">The changed assemblies.</param>
	/// <param name="isCollectible">Whether they load into collectible contexts (<see cref="LoadGeneration"/>).</param>
	private Generation LoadChanges( IReadOnlyList<AssemblyImage> changes, bool isCollectible )
	{
		var affected = BuildAffected( changes );
		var names = string.Join( ", ", affected.Select( image => image.Name ) );
		var refused = new List<(AssemblyImage Image, string Problem)>();
		try
		{
			return new( names, affected, LoadCompatible( affected, isCollectible, ( image, problem ) => refused.Add( (image, problem) ) ), refused, null );
		}
		catch ( Exception error )
		{
			return new( names, affected, [], refused, error );
		}
	}

	/// <summary>Reports a loaded generation's refused updates and swaps its modules in; on the UI thread.</summary>
	private void Install( Generation generation, bool isInitial )
	{
		var names = generation.Names;
		try
		{
			foreach ( var (image, problem) in generation.Refused )
				Refuse( image, problem );
			if ( generation.Error is { } loadError )
			{
				ReportFailure( names, loadError );
				return;
			}

			var hosted = generation.Hosted;
			// A refused update leaves its module on the version already running; reloading it would only stop its work.
			if ( !isInitial && generation.Refused.Count > 0 && hosted.All( item => _assemblies.TryGetValue( item.Image.Name, out var current ) && current.Image.IsSameFile( item.Image ) ) )
				return;

			foreach ( var item in hosted )
				item.Modules = [.. BuildModules( item.Assembly ).Where( IsAllowed )];

			var modules = OrderModules( hosted.SelectMany( item => item.Modules ).ToArray() );
			RemoveDroppedModules( generation.Affected, modules );

			// At startup each module stands alone, so one failing module does not hold back the others; a module the user turned off is only listed.
			var isLoaded = true;
			if ( isInitial )
			{
				var wanted = _daemon is null ? null : ComputeWanted( modules );
				foreach ( var module in modules )
				{
					// A module whose needed modules are off stays listed and off, rather than failing to load; one that cannot unload is always on.
					var isOn = !module.CanUnload || (wanted?.Contains( module.Id ) ?? _states?.IsOn( module.Id ) ?? true);
					if ( isOn && module.Dependencies.All( id => _app.Modules.Any( loaded => loaded.Id == id ) ) )
					{
						using var step = PerfTrace.Step( "module", module.Id );
						isLoaded &= _app.Load( module );
					}
					else
						_app.AddAvailable( module );
				}
			}
			else
				isLoaded = _app.Reload( modules );
			if ( !isLoaded && !isInitial )
				throw new InvalidOperationException( _app.LastError ?? "The replacement modules did not load; the previous ones keep running." );

			foreach ( var item in hosted )
				_assemblies[item.Image.Name] = item;

			UpdateLog( $"{( isInitial ? "LOADED" : "RELOADED" )} {names} (modules: {string.Join( ", ", modules.Select( module => module.Id ) )})" );
			foreach ( var item in hosted.Where( item => item.Image.IsUpdate ) )
				UpdateLog( $"USING {item.Image.Name} {item.Image.Version.ToString( 3 )} (update)" );
		}
		catch ( Exception error )
		{
			ReportFailure( names, error );
		}
	}

	/// <summary>Logs and reports a generation that did not load, by its innermost meaningful error.</summary>
	/// <param name="names">The generation's assembly names.</param>
	/// <param name="error">Why it did not load.</param>
	private void ReportFailure( string names, Exception error )
	{
		var failure = error switch
		{
			ReflectionTypeLoadException { LoaderExceptions: [{ } first, ..] } => first,
			TargetInvocationException { InnerException: { } inner } => inner,
			_ => error,
		};
		UpdateLog( $"FAILED {names}: {failure.Message}" );
		_app.ReportError( $"Reload {names}", failure );
	}

	/// <summary>Loads a generation of assemblies, replacing each update that does not fit with its older version until all of them fit.</summary>
	/// <param name="affected">The images to load; refused updates are replaced in it.</param>
	/// <param name="isCollectible">Whether they load into collectible contexts (<see cref="LoadGeneration"/>).</param>
	/// <param name="refuse">Told about each refused update and its first problem.</param>
	/// <exception cref="InvalidOperationException">A module that is not an update does not fit, so no older version can replace it.</exception>
	private List<HostedAssembly> LoadCompatible( List<AssemblyImage> affected, bool isCollectible, Action<AssemblyImage, string> refuse )
	{
		while ( true )
		{
			List<HostedAssembly> hosted;
			using ( PerfTrace.Step( "modules.load" ) )
				hosted = LoadGeneration( affected, isCollectible );
			var refused = new List<(AssemblyImage Image, string Problem)>();
			using var check = PerfTrace.Step( "modules.check" );
			foreach ( var item in hosted )
			{
				if ( ModuleCompatibility.FindProblem( item.Assembly, item.Image.Image ) is not { } problem )
					continue;

				// An update is blamed for its own problem; a bundled module that no longer fits is blamed on the updates it uses.
				var blamed = item.Image.IsUpdate
					? [item.Image]
					: hosted.Where( other => other.Image.IsUpdate && item.References.Contains( other.Image.Name, StringComparer.OrdinalIgnoreCase ) ).Select( other => other.Image ).ToList();
				if ( blamed.Count == 0 )
					throw new InvalidOperationException( $"{item.Image.Name} {item.Image.Version} does not fit this version of Loom: {problem}" );

				foreach ( var image in blamed.Where( image => refused.All( entry => entry.Image != image ) ) )
					refused.Add( (image, problem) );
			}

			if ( refused.Count == 0 )
				return hosted;

			foreach ( var (image, problem) in refused )
			{
				refuse( image, problem );
				var index = affected.IndexOf( image );
				if ( image.Fallback is { } older )
					affected[index] = older;
				else
					affected.RemoveAt( index );
			}
		}
	}

	/// <summary>Loads images into fresh contexts that resolve each other, and the untouched assemblies, by name.</summary>
	/// <param name="images">The images.</param>
	/// <param name="isCollectible">Whether the GC may reclaim the contexts once nothing references them, as for a replaced or refused module. The runtime
	/// compiles a collectible assembly's methods fully optimized on their first call, about three times as long as its quick first tier, so the
	/// modules Loom starts with load into contexts that stay; only reloads, which replace them, and update checks load into collectible ones.</param>
	private List<HostedAssembly> LoadGeneration( IReadOnlyList<AssemblyImage> images, bool isCollectible )
	{
		// Assemblies in one generation resolve each other through this map; untouched assemblies keep their loaded copy.
		var generation = _assemblies
			.Where( item => images.All( image => !string.Equals( image.Name, item.Key, StringComparison.OrdinalIgnoreCase ) ) )
			.ToDictionary( item => item.Key, item => item.Value.Assembly, StringComparer.OrdinalIgnoreCase );

		var hosted = new List<HostedAssembly>();
		foreach ( var image in images )
		{
			var context = new ModuleLoadContext( image.Name, generation, isCollectible );
			var assembly = context.LoadFromStream( new MemoryStream( image.Image ), image.Symbols is null ? null : new MemoryStream( image.Symbols ) );
			generation[image.Name] = assembly;
			hosted.Add( new( image, assembly ) );
		}

		return hosted;
	}

	/// <summary>Records a refused update, deletes its file so it is not tried again, and reports it. The daemon leaves the file to Loom on this PC,
	/// which owns the updates folder; it tries the file again only once the file changes.</summary>
	private void Refuse( AssemblyImage image, string problem )
	{
		UpdateLog( $"SKIPPED {image.Name} {image.Version}: {problem}" );
		if ( image.Path is { } path && image.IsUpdate && _daemon is null )
		{
			lock ( _scanLock )
			{
				// The stamp moves first, so the watcher sees the deletion as already handled rather than as a change to reload.
				_stamps[image.Name] = ComputeStamp( ListFiles().GetValueOrDefault( image.Name, [] ).Where( file => !string.Equals( file, path, StringComparison.OrdinalIgnoreCase ) ) );
				File.Delete( path );
			}
		}

		Refused?.Invoke( image.Name, image.Version, problem );
	}

	/// <summary>Expands changed assemblies with every loaded assembly that references one of them, directly or through another.</summary>
	private List<AssemblyImage> BuildAffected( IReadOnlyList<AssemblyImage> changes )
	{
		var affected = changes.ToDictionary( image => image.Name, StringComparer.OrdinalIgnoreCase );
		var isGrowing = true;
		while ( isGrowing )
		{
			// A dependent compiled against a replaced assembly must rebind to the new copy.
			var dependents = _assemblies.Values
				.Where( item => !affected.ContainsKey( item.Image.Name ) && item.References.Any( affected.ContainsKey ) )
				.ToArray();
			foreach ( var dependent in dependents )
				affected.Add( dependent.Image.Name, dependent.Image );

			isGrowing = dependents.Length > 0;
		}

		return affected.Values
			.OrderBy( image => image.Name, StringComparer.OrdinalIgnoreCase )
			.ToList();
	}

	/// <summary>Creates every module an assembly defines through its parameterless constructor.</summary>
	private static ILoknModule[] BuildModules( Assembly assembly )
	{
		return assembly
			.GetTypes()
			.Where( type => !type.IsAbstract && typeof( ILoknModule ).IsAssignableFrom( type ) && type.GetConstructor( Type.EmptyTypes ) is not null )
			.Select( type => (ILoknModule)Activator.CreateInstance( type )! )
			.ToArray();
	}

	/// <summary>Tells whether this host creates a module: every module in a window, only the allowed ones in the daemon.</summary>
	private bool IsAllowed( ILoknModule module ) => _daemon is null || _daemon.Allowed.Contains( module.Id );

	/// <summary>Orders modules so each one follows the modules it declares as dependencies.</summary>
	private static List<ILoknModule> OrderModules( IReadOnlyList<ILoknModule> modules )
	{
		var ordered = new List<ILoknModule>();
		var pending = modules
			.OrderBy( module => module.Id, StringComparer.Ordinal )
			.ToList();
		while ( pending.Count > 0 )
		{
			// A dependency outside this set is already loaded, or its absence is reported when the module loads.
			var next = pending.FirstOrDefault( module => !module.Dependencies.Any( id => pending.Any( other => other.Id == id ) ) ) ?? pending[0];
			pending.Remove( next );
			ordered.Add( next );
		}

		return ordered;
	}

	/// <summary>Removes modules that a rebuilt assembly no longer defines.</summary>
	private void RemoveDroppedModules( IReadOnlyList<AssemblyImage> affected, IReadOnlyList<ILoknModule> modules )
	{
		var dropped = affected
			.Where( image => _assemblies.ContainsKey( image.Name ) )
			.SelectMany( image => _assemblies[image.Name].Modules )
			.Select( module => module.Id )
			.Where( id => modules.All( module => module.Id != id ) );
		foreach ( var id in dropped.ToArray() )
			_app.Remove( id );
	}

	/// <summary>Appends one timestamped outcome line; a logging failure never blocks a reload.</summary>
	private void UpdateLog( string line ) => LogFile.Append( _logPath, line );

	/// <summary>Holds the daemon's module rules (docs/remote.md, Roles).</summary>
	/// <param name="Allowed">The only module IDs the daemon creates; every other module an assembly defines is ignored.</param>
	/// <param name="Required">The module IDs that stay on whatever the module states say, with every module they need, such as the module the daemon exists for.</param>
	internal sealed record DaemonRules( IReadOnlySet<string> Allowed, IReadOnlySet<string> Required );

	/// <summary>Holds one module assembly's bytes as read from disk or downloaded.</summary>
	/// <param name="Name">The assembly's simple name.</param>
	/// <param name="Image">The assembly file's bytes.</param>
	/// <param name="Symbols">The matching portable symbols, or null when none were deployed.</param>
	/// <param name="Version">The assembly version, the module's own release version.</param>
	/// <param name="Path">The file it was read from, or null for a download being checked.</param>
	/// <param name="IsUpdate">Whether it is a module update rather than the bundled module.</param>
	/// <param name="Fallback">The older image that loads instead when this one is refused, or null when there is none.</param>
	private sealed record AssemblyImage( string Name, byte[] Image, byte[]? Symbols, Version Version, string? Path, bool IsUpdate, AssemblyImage? Fallback )
	{
		/// <summary>Tells whether another image came from the same file with the same contents.</summary>
		internal bool IsSameFile( AssemblyImage other ) => Path is not null && string.Equals( Path, other.Path, StringComparison.OrdinalIgnoreCase ) && Image.AsSpan().SequenceEqual( other.Image );
	}

	/// <summary>Stores a loaded module assembly with the bytes it came from, kept to load it again when a dependency is rebuilt.</summary>
	private sealed class HostedAssembly( AssemblyImage image, Assembly assembly )
	{
		/// <summary>Gets the bytes the assembly was loaded from.</summary>
		internal AssemblyImage Image => image;

		/// <summary>Gets the loaded assembly.</summary>
		internal Assembly Assembly => assembly;

		/// <summary>Gets the simple names of the assemblies this one references.</summary>
		internal string[] References { get; } = assembly
			.GetReferencedAssemblies()
			.Select( reference => reference.Name ?? "" )
			.ToArray();

		/// <summary>Gets or sets the module instances created from this assembly.</summary>
		internal ILoknModule[] Modules { get; set; } = [];
	}

	/// <summary>Holds a generation of module assemblies loaded and checked, ready for the UI thread to report and start.</summary>
	/// <param name="Names">The affected assemblies' names, for the log.</param>
	/// <param name="Affected">The images loaded, with refused updates replaced by their older version or left out.</param>
	/// <param name="Hosted">The loaded assemblies, empty when <paramref name="Error"/> is set.</param>
	/// <param name="Refused">The updates refused, each with its first problem, in the order they were found.</param>
	/// <param name="Error">Why the generation did not load, or null.</param>
	private sealed record Generation( string Names, List<AssemblyImage> Affected, List<HostedAssembly> Hosted, List<(AssemblyImage Image, string Problem)> Refused, Exception? Error );

	/// <summary>Resolves references between module assemblies of one generation; everything else comes from the application.</summary>
	private sealed class ModuleLoadContext( string name, IReadOnlyDictionary<string, Assembly> modules, bool isCollectible ) : AssemblyLoadContext( name, isCollectible )
	{
		/// <summary>Returns the module assembly of that name, else the application's loaded assembly of that name, else null for the default rules.</summary>
		/// <remarks>
		/// The application's assemblies bind by name, not version: a module built against a newer Core or Goo still loads, and
		/// <see cref="ModuleCompatibility"/> then checks that everything it uses is there.
		/// </remarks>
		protected override Assembly? Load( AssemblyName assemblyName )
		{
			if ( assemblyName.Name is not { } simpleName )
				return null;

			if ( modules.TryGetValue( simpleName, out var assembly ) )
				return assembly;

			foreach ( var loaded in Default.Assemblies )
			{
				if ( string.Equals( loaded.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase ) )
					return loaded;
			}

			return null;
		}
	}
}
