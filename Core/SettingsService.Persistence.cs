using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lokn.Core;

/// <summary>Loads and atomically saves local settings while preserving malformed source files.</summary>
/// <remarks>
/// Several processes may share one file, such as the desktop and the daemon (docs/remote.md, Roles). A save merges: under a named mutex it
/// reads the file, applies only the values this process changed since it last read or wrote it, and writes the result when it differs, so
/// another process's changes are kept. <see cref="Watch"/> reloads the file when another process wrote it.
/// </remarks>
public sealed partial class SettingsService
{
	/// <summary>Defines the supported settings file format version.</summary>
	private const int FileVersion = 1;

	/// <summary>Defines how long a save or reload waits for another process's, in milliseconds.</summary>
	private const int FileGateMilliseconds = 5000;

	/// <summary>Defines the quiet period after a file event before the file is read again, in milliseconds.</summary>
	private const int ReloadDelayMilliseconds = 250;

	/// <summary>Stores the optional settings file path; null keeps tests and headless apps in memory.</summary>
	private readonly string? _path;

	/// <summary>Stores the global values as the file last held them when this process read or wrote it.</summary>
	private readonly Dictionary<string, JsonElement> _savedGlobal = new( StringComparer.Ordinal );

	/// <summary>Stores the work values as the file last held them.</summary>
	private readonly Dictionary<string, JsonElement> _savedWork = new( StringComparer.Ordinal );

	/// <summary>Stores the personal values as the file last held them.</summary>
	private readonly Dictionary<string, JsonElement> _savedPersonal = new( StringComparer.Ordinal );

	/// <summary>Stores the active profile as the file last held it.</summary>
	private SettingsProfile _savedProfile = SettingsProfile.Personal;

	/// <summary>Tracks whether the loaded file can safely be replaced by this version.</summary>
	private bool _canSave = true;

	/// <summary>Defines readable JSON with named profiles instead of numeric enum values.</summary>
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true,
		Converters =
		{
			new JsonStringEnumConverter<SettingsProfile>( allowIntegerValues: false ),
		},
	};

	/// <summary>Gets the last settings validation or file error.</summary>
	public string? LastError
	{
		get; private set;
	}

	/// <summary>Gets the per-user application settings file path.</summary>
	public static string DefaultPath => Path.Combine(
		Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ),
		"Lokn",
		"settings.json" );

	/// <summary>Reloads the file whenever another process writes it, applying its values on the UI thread through <paramref name="post"/>.</summary>
	/// <param name="post">Posts an action to the UI thread and reports whether it was accepted.</param>
	/// <returns>A handle that stops watching; null for in-memory settings or a folder that cannot be watched.</returns>
	internal IDisposable? Watch( Func<Action, bool> post )
	{
		if ( _path is null || Path.GetDirectoryName( _path ) is not { } directory )
			return null;

		try
		{
			Directory.CreateDirectory( directory );
			return new FileFollower( directory, Path.GetFileName( _path ), () => post( Reload ) );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or ArgumentException )
		{
			return null;
		}
	}

	/// <summary>Reads the file again and takes the values another process saved, keeping this process's unsaved changes; on the UI thread.</summary>
	internal void Reload()
	{
		if ( _path is null )
			return;

		SettingsDocument? disk;
		try
		{
			using ( AcquireFileGate() )
				disk = ReadDocument();
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or JsonException )
		{
			// A file being corrected or written by hand is read again at its next change.
			return;
		}

		if ( disk is null )
			return;

		var merged = Merge( disk );
		var isChanged = !IsSame( merged, Capture( _global, _work, _personal, _activeProfile ) );
		_canSave = true;
		Adopt( disk, merged );
		if ( isChanged )
			MarkChanged();
	}

	/// <summary>Loads saved values once without requiring their feature modules to be enabled.</summary>
	private void EnsureLoaded()
	{
		if ( _path is null || !File.Exists( _path ) )
			return;

		try
		{
			SettingsDocument? document;
			using ( AcquireFileGate() )
				document = ReadDocument();
			if ( document is not null )
				Adopt( document, document );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or JsonException )
		{
			_canSave = false;
			MarkFailed( $"Could not load {_path}: {error.Message} The original file is unchanged; settings stay in memory until the file is corrected and Lokn restarts." );
		}
	}

	/// <summary>Moves saved values from settings renamed with their module, such as <c>workspace.panel-gap</c> to <c>shell.panel-gap</c>, in every scope, then saves.</summary>
	/// <param name="oldPrefix">The old identifier prefix, with its dot.</param>
	/// <param name="newPrefix">The new identifier prefix, with its dot.</param>
	/// <remarks>A value already saved under the new identifier wins, so running it again changes nothing.</remarks>
	internal void MoveSaved( string oldPrefix, string newPrefix )
	{
		var isMoved = false;
		foreach ( var scope in new[] { _global, _work, _personal } )
		{
			foreach ( var key in scope.Keys.Where( key => key.StartsWith( oldPrefix, StringComparison.Ordinal ) ).ToArray() )
			{
				scope.TryAdd( newPrefix + key[oldPrefix.Length..], scope[key] );
				scope.Remove( key );
				isMoved = true;
			}
		}
		if ( isMoved && _canSave )
			EnsureSaved();
	}

	/// <summary>Merges this process's changes into the file and writes it through a sibling temporary file, so a failed write preserves the previous
	/// settings; nothing is written when the file already holds every value.</summary>
	private bool EnsureSaved()
	{
		if ( _path is null )
			return true;

		if ( !_canSave )
		{
			MarkFailed( $"Changes apply for this session. Correct {_path} and restart Lokn to save settings." );
			return false;
		}

		var current = Capture( _global, _work, _personal, _activeProfile );
		if ( IsSame( current, Capture( _savedGlobal, _savedWork, _savedPersonal, _savedProfile ) ) )
			return true;

		try
		{
			SettingsDocument merged;
			using ( AcquireFileGate() )
			{
				SettingsDocument? disk;
				try
				{
					disk = ReadDocument();
				}
				catch ( JsonException error )
				{
					_canSave = false;
					MarkFailed( $"Could not save to {_path}: the file changed and is no longer valid ({error.Message}). It is left unchanged; changes apply for this session." );
					return false;
				}

				merged = Merge( disk ?? new SettingsDocument() );
				if ( disk is null || !IsSame( merged, disk ) )
				{
					Directory.CreateDirectory( Path.GetDirectoryName( _path )! );
					var temporary = _path + ".tmp";
					File.WriteAllText( temporary, JsonSerializer.Serialize( merged, JsonOptions ) );
					File.Move( temporary, _path, overwrite: true );
				}
			}

			var isOthers = !IsSame( merged, current );
			Adopt( merged, merged );
			LastError = null;
			if ( isOthers )
				MarkChanged();
			return true;
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			MarkFailed( $"Changes apply for this session but could not be saved to {_path}: {error.Message}" );
			return false;
		}
	}

	/// <summary>Reads the file, or null when there is none.</summary>
	/// <exception cref="JsonException">The file is not a settings document of this version.</exception>
	private SettingsDocument? ReadDocument()
	{
		if ( !File.Exists( _path ) )
			return null;

		var document = JsonSerializer.Deserialize<SettingsDocument>( File.ReadAllText( _path! ), JsonOptions )
			?? throw new JsonException( "The settings document is empty." );
		if ( document.Version != FileVersion || document.Global is null || document.Work is null || document.Personal is null )
			throw new JsonException( "The settings document has an unsupported version or missing scope." );
		return document;
	}

	/// <summary>Builds the file's next content: <paramref name="disk"/> with every value this process changed since it last read or wrote the file.</summary>
	private SettingsDocument Merge( SettingsDocument disk ) => new()
	{
		ActiveProfile = _activeProfile != _savedProfile ? _activeProfile : disk.ActiveProfile,
		Global = MergeScope( disk.Global, _global, _savedGlobal ),
		Work = MergeScope( disk.Work, _work, _savedWork ),
		Personal = MergeScope( disk.Personal, _personal, _savedPersonal ),
	};

	/// <summary>Applies one scope's changes, values set or removed since <paramref name="saved"/>, onto the file's values.</summary>
	private static Dictionary<string, JsonElement> MergeScope( Dictionary<string, JsonElement> disk, Dictionary<string, JsonElement> current, Dictionary<string, JsonElement> saved )
	{
		var merged = new Dictionary<string, JsonElement>( disk, StringComparer.Ordinal );
		foreach ( var (key, value) in current )
		{
			if ( !saved.TryGetValue( key, out var previous ) || !JsonElement.DeepEquals( previous, value ) )
				merged[key] = value;
		}
		foreach ( var key in saved.Keys )
		{
			if ( !current.ContainsKey( key ) )
				merged.Remove( key );
		}
		return merged;
	}

	/// <summary>Makes <paramref name="values"/> this process's values and <paramref name="saved"/> what the file holds.</summary>
	private void Adopt( SettingsDocument saved, SettingsDocument values )
	{
		Replace( _global, values.Global );
		Replace( _work, values.Work );
		Replace( _personal, values.Personal );
		_activeProfile = values.ActiveProfile;
		Replace( _savedGlobal, saved.Global );
		Replace( _savedWork, saved.Work );
		Replace( _savedPersonal, saved.Personal );
		_savedProfile = saved.ActiveProfile;
	}

	/// <summary>Replaces a dictionary's entries with another's.</summary>
	private static void Replace( Dictionary<string, JsonElement> target, Dictionary<string, JsonElement> source )
	{
		target.Clear();
		foreach ( var (key, value) in source )
			target[key] = value;
	}

	/// <summary>Copies values into a document, so later changes to the dictionaries do not reach it.</summary>
	private static SettingsDocument Capture( Dictionary<string, JsonElement> global, Dictionary<string, JsonElement> work, Dictionary<string, JsonElement> personal, SettingsProfile profile ) => new()
	{
		ActiveProfile = profile,
		Global = new( global, StringComparer.Ordinal ),
		Work = new( work, StringComparer.Ordinal ),
		Personal = new( personal, StringComparer.Ordinal ),
	};

	/// <summary>Checks whether two documents hold the same profile and values, comparing values by their JSON meaning.</summary>
	private static bool IsSame( SettingsDocument left, SettingsDocument right ) =>
		left.ActiveProfile == right.ActiveProfile && IsSame( left.Global, right.Global ) && IsSame( left.Work, right.Work ) && IsSame( left.Personal, right.Personal );

	/// <summary>Checks whether two scopes hold the same keys with the same JSON values.</summary>
	private static bool IsSame( Dictionary<string, JsonElement> left, Dictionary<string, JsonElement> right ) =>
		left.Count == right.Count && left.All( item => right.TryGetValue( item.Key, out var other ) && JsonElement.DeepEquals( item.Value, other ) );

	/// <summary>Takes the mutex every process holds while it reads or writes this file; when another process holds it too long, goes on without it.</summary>
	private FileGate AcquireFileGate()
	{
		var hash = Convert.ToHexStringLower( SHA256.HashData( Encoding.UTF8.GetBytes( _path!.ToUpperInvariant() ) ) )[..16];
		var mutex = new Mutex( false, @"Local\Lokn.settings." + hash );
		bool isOwned;
		try
		{
			isOwned = mutex.WaitOne( FileGateMilliseconds );
		}
		catch ( AbandonedMutexException )
		{
			// A process that died while saving left the mutex behind; the file itself was replaced atomically or not at all.
			isOwned = true;
		}
		return new FileGate( mutex, isOwned );
	}

	/// <summary>Holds the settings file's mutex until disposed.</summary>
	/// <param name="mutex">The mutex.</param>
	/// <param name="isOwned">Whether this thread owns it.</param>
	private sealed class FileGate( Mutex mutex, bool isOwned ) : IDisposable
	{
		/// <summary>Releases the mutex when owned and closes it.</summary>
		public void Dispose()
		{
			if ( isOwned )
				mutex.ReleaseMutex();
			mutex.Dispose();
		}
	}

	/// <summary>Watches the settings file and reports, after a quiet period, that it changed.</summary>
	private sealed class FileFollower : IDisposable
	{
		/// <summary>Stores the folder watcher.</summary>
		private readonly FileSystemWatcher _watcher;

		/// <summary>Coalesces the events of one write.</summary>
		private readonly Timer _quiet;

		/// <summary>Starts watching a file.</summary>
		/// <param name="directory">The file's folder.</param>
		/// <param name="name">The file's name.</param>
		/// <param name="changed">Runs on a timer thread after the file changed.</param>
		public FileFollower( string directory, string name, Action changed )
		{
			_quiet = new Timer( _ => changed(), null, Timeout.Infinite, Timeout.Infinite );
			_watcher = new FileSystemWatcher( directory, name )
			{
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
			};
			_watcher.Changed += MarkDirty;
			_watcher.Created += MarkDirty;
			_watcher.Renamed += MarkDirty;
			_watcher.EnableRaisingEvents = true;
		}

		/// <summary>Stops watching.</summary>
		public void Dispose()
		{
			_watcher.Dispose();
			_quiet.Dispose();
		}

		/// <summary>Restarts the quiet period.</summary>
		private void MarkDirty( object sender, FileSystemEventArgs change ) => _quiet.Change( ReloadDelayMilliseconds, Timeout.Infinite );
	}

	/// <summary>Stores explicit scope values and the selected profile in the versioned file format.</summary>
	private sealed record SettingsDocument
	{
		/// <summary>Gets the settings file format version.</summary>
		public int Version { get; init; } = FileVersion;

		/// <summary>Gets the last selected application profile.</summary>
		public SettingsProfile ActiveProfile { get; init; } = SettingsProfile.Personal;

		/// <summary>Gets explicit global defaults without copying them into profile overrides.</summary>
		public Dictionary<string, JsonElement> Global { get; init; } = [];

		/// <summary>Gets explicit work overrides.</summary>
		public Dictionary<string, JsonElement> Work { get; init; } = [];

		/// <summary>Gets explicit personal overrides.</summary>
		public Dictionary<string, JsonElement> Personal { get; init; } = [];
	}
}
