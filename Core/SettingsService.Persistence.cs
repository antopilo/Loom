using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lokn.Core;

/// <summary>Loads and atomically saves local settings while preserving malformed source files.</summary>
public sealed partial class SettingsService
{
	/// <summary>Defines the supported settings file format version.</summary>
	private const int FileVersion = 1;

	/// <summary>Stores the optional settings file path; null keeps tests and headless apps in memory.</summary>
	private readonly string? _path;

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

	/// <summary>Loads saved values once without requiring their feature modules to be enabled.</summary>
	private void EnsureLoaded()
	{
		if ( _path is null || !File.Exists( _path ) )
			return;

		try
		{
			var document = JsonSerializer.Deserialize<SettingsDocument>( File.ReadAllText( _path ), JsonOptions )
				?? throw new JsonException( "The settings document is empty." );
			if ( document.Version != FileVersion || document.Global is null || document.Work is null || document.Personal is null )
				throw new JsonException( "The settings document has an unsupported version or missing scope." );

			_activeProfile = document.ActiveProfile;
			foreach ( var item in document.Global )
				_global.Add( item.Key, item.Value );

			foreach ( var item in document.Work )
				_work.Add( item.Key, item.Value );

			foreach ( var item in document.Personal )
				_personal.Add( item.Key, item.Value );
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

	/// <summary>Writes through a sibling temporary file so a failed write preserves the previous settings.</summary>
	private bool EnsureSaved()
	{
		if ( _path is null )
			return true;

		if ( !_canSave )
		{
			MarkFailed( $"Changes apply for this session. Correct {_path} and restart Lokn to save settings." );
			return false;
		}

		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( _path )! );
			var document = new SettingsDocument
			{
				ActiveProfile = _activeProfile,
				Global = _global,
				Work = _work,
				Personal = _personal,
			};
			var temporary = _path + ".tmp";
			File.WriteAllText( temporary, JsonSerializer.Serialize( document, JsonOptions ) );
			File.Move( temporary, _path, overwrite: true );
			LastError = null;
			return true;
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			MarkFailed( $"Changes apply for this session but could not be saved to {_path}: {error.Message}" );
			return false;
		}
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
