using System.Reflection;
using System.Runtime.Loader;
using Lokn.Core;

namespace Lokn;

/// <summary>Loads feature modules from DLLs beside the application and reloads them when they are rebuilt.</summary>
internal sealed class ModuleHost
{
	/// <summary>Defines the file name pattern of module assemblies.</summary>
	private const string AssemblyPattern = "Lokn.Modules.*.dll";

	/// <summary>Defines the quiet period in milliseconds after a file event before rebuilt assemblies are read.</summary>
	private const int DebounceMilliseconds = 300;

	/// <summary>Stores the application whose modules are hosted.</summary>
	private readonly LoknApplication _app;

	/// <summary>Stores the directory holding module assemblies.</summary>
	private readonly string _directory;

	/// <summary>Stores the file that records each reload's outcome for scripts and agents.</summary>
	private readonly string _logPath;

	/// <summary>Stores the loaded assemblies by simple name; used on the UI thread.</summary>
	private readonly Dictionary<string, HostedAssembly> _assemblies = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Stores each assembly file's last seen write time and size, guarded by the scan lock.</summary>
	private readonly Dictionary<string, (DateTime Written, long Length)> _stamps = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Serializes the startup scan and overlapping debounce ticks.</summary>
	private readonly Lock _scanLock = new();

	/// <summary>Coalesces the file events one build raises into a single reload.</summary>
	private readonly Timer _debounce;

	/// <summary>Stores the module directory watcher while the host runs.</summary>
	private FileSystemWatcher? _watcher;

	/// <summary>Gets the directory beside the executable where module projects deploy their DLLs.</summary>
	internal static string DefaultDirectory => Path.Combine( AppContext.BaseDirectory, "modules" );

	/// <summary>Gets the per-user file recording reload outcomes.</summary>
	internal static string DefaultLogPath => Path.Combine( Path.GetTempPath(), "Lokn", "modules.log" );

	/// <summary>Stores which installed modules start on.</summary>
	private readonly ModuleStates _states;

	/// <summary>Creates a host for the module assemblies in a directory.</summary>
	internal ModuleHost( LoknApplication app, string directory, string logPath, ModuleStates states )
	{
		_app = app;
		_directory = directory;
		_logPath = logPath;
		_states = states;
		_debounce = new( _ => PostChanges(), null, Timeout.Infinite, Timeout.Infinite );
		app.ModuleToggled += states.Set;
	}

	/// <summary>Loads every module assembly in dependency order, then watches the directory for rebuilt ones.</summary>
	internal void Start()
	{
		Directory.CreateDirectory( _directory );
		Apply( ReadChanges(), isInitial: true );

		_watcher = new( _directory, AssemblyPattern )
		{
			NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
		};
		_watcher.Changed += MarkDirty;
		_watcher.Created += MarkDirty;
		_watcher.Renamed += MarkDirty;
		_watcher.EnableRaisingEvents = true;
	}

	/// <summary>Stops watching while leaving the loaded modules running.</summary>
	internal void Stop()
	{
		// The watcher owns a native directory handle that would otherwise keep the directory in use.
		_watcher?.Dispose();
		_watcher = null;
	}

	/// <summary>Restarts the quiet period after a module assembly is written.</summary>
	private void MarkDirty( object sender, FileSystemEventArgs change ) => _debounce.Change( DebounceMilliseconds, Timeout.Infinite );

	/// <summary>Reads rebuilt assemblies on the timer thread and reloads them on the UI thread.</summary>
	private void PostChanges()
	{
		var changes = ReadChanges();
		if ( changes.Count > 0 )
			_app.Post( () => Apply( changes, isInitial: false ) );
	}

	/// <summary>Reads every module assembly whose file changed since the last scan.</summary>
	private List<AssemblyImage> ReadChanges()
	{
		var changes = new List<AssemblyImage>();
		lock ( _scanLock )
		{
			foreach ( var file in Directory.GetFiles( _directory, AssemblyPattern ) )
			{
				var name = Path.GetFileNameWithoutExtension( file );
				try
				{
					var info = new FileInfo( file );
					var stamp = (info.LastWriteTimeUtc, info.Length);
					if ( _stamps.TryGetValue( name, out var previous ) && previous == stamp )
						continue;

					// Reading the bytes leaves the file unlocked, so the next build can replace it while Lokn runs.
					var symbols = Path.ChangeExtension( file, ".pdb" );
					changes.Add( new( name, File.ReadAllBytes( file ), File.Exists( symbols ) ? File.ReadAllBytes( symbols ) : null ) );
					_stamps[name] = stamp;
				}
				catch ( IOException )
				{
					// The build is still writing this file; look again once it has finished.
					_debounce.Change( DebounceMilliseconds, Timeout.Infinite );
				}
			}
		}

		return changes;
	}

	/// <summary>Loads changed assemblies together with the assemblies that reference them, then swaps their modules in.</summary>
	private void Apply( IReadOnlyList<AssemblyImage> changes, bool isInitial )
	{
		if ( changes.Count == 0 )
			return;

		var affected = BuildAffected( changes );
		var names = string.Join( ", ", affected.Select( image => image.Name ) );
		try
		{
			// Assemblies in one generation resolve each other through this map; untouched assemblies keep their loaded copy.
			var generation = _assemblies
				.Where( item => affected.All( image => !string.Equals( image.Name, item.Key, StringComparison.OrdinalIgnoreCase ) ) )
				.ToDictionary( item => item.Key, item => item.Value.Assembly, StringComparer.OrdinalIgnoreCase );

			var hosted = new List<HostedAssembly>();
			foreach ( var image in affected )
			{
				// A collectible context lets the GC reclaim a replaced module once nothing references it.
				var context = new ModuleLoadContext( image.Name, generation );
				var assembly = context.LoadFromStream( new MemoryStream( image.Image ), image.Symbols is null ? null : new MemoryStream( image.Symbols ) );
				generation[image.Name] = assembly;
				hosted.Add( new( image, assembly ) );
			}

			foreach ( var item in hosted )
				item.Modules = BuildModules( item.Assembly );

			var modules = OrderModules( hosted.SelectMany( item => item.Modules ).ToArray() );
			RemoveDroppedModules( affected, modules );

			// At startup each module stands alone, so one failing module does not hold back the others; a module the user turned off is only listed.
			var isLoaded = true;
			if ( isInitial )
			{
				foreach ( var module in modules )
				{
					// A module whose needed modules are off stays listed and off, rather than failing to load; one that cannot unload is always on.
					if ( (!module.CanUnload || _states.IsOn( module.Id )) && module.Dependencies.All( id => _app.Modules.Any( loaded => loaded.Id == id ) ) )
						isLoaded &= _app.Load( module );
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
		}
		catch ( Exception error )
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
	private void UpdateLog( string line )
	{
		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( _logPath )! );
			File.AppendAllText( _logPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}" );
		}
		catch ( IOException )
		{
		}
	}

	/// <summary>Holds one module assembly's bytes as read from disk.</summary>
	/// <param name="Name">The assembly's simple name.</param>
	/// <param name="Image">The assembly file's bytes.</param>
	/// <param name="Symbols">The matching portable symbols, or null when none were deployed.</param>
	private sealed record AssemblyImage( string Name, byte[] Image, byte[]? Symbols );

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

	/// <summary>Resolves references between module assemblies of one generation; everything else comes from the application.</summary>
	private sealed class ModuleLoadContext( string name, IReadOnlyDictionary<string, Assembly> modules ) : AssemblyLoadContext( name, isCollectible: true )
	{
		/// <summary>Returns the loaded module assembly of that name, or null to use the application's own assemblies.</summary>
		protected override Assembly? Load( AssemblyName assemblyName )
		{
			return assemblyName.Name is { } simpleName && modules.TryGetValue( simpleName, out var assembly ) ? assembly : null;
		}
	}
}
