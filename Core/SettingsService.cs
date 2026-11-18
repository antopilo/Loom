using System.Text.Json;
using Goo;

namespace Lokn.Core;

/// <summary>Resolves live profile overrides over global defaults and persists explicit changes.</summary>
public sealed partial class SettingsService
{
	/// <summary>Stores registered setting definitions and their owning module identifiers.</summary>
	private readonly Dictionary<string, RegisteredSetting> _definitions = new( StringComparer.Ordinal );

	/// <summary>Stores only explicit global defaults, preserving unknown module values across unloads.</summary>
	private readonly Dictionary<string, JsonElement> _global = new( StringComparer.Ordinal );

	/// <summary>Stores explicit work profile overrides.</summary>
	private readonly Dictionary<string, JsonElement> _work = new( StringComparer.Ordinal );

	/// <summary>Stores explicit personal profile overrides.</summary>
	private readonly Dictionary<string, JsonElement> _personal = new( StringComparer.Ordinal );

	/// <summary>Tracks changes read by retained Goo views and feature panels.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Stores the active profile without duplicating resolved settings.</summary>
	private SettingsProfile _activeProfile = SettingsProfile.Personal;

	/// <summary>Gets the selected profile and subscribes the current view to changes.</summary>
	public SettingsProfile ActiveProfile
	{
		get
		{
			_ = _version.Value;
			return _activeProfile;
		}
	}

	/// <summary>Gets the registered definitions offered by the settings editor.</summary>
	public IReadOnlyList<SettingDefinition> Definitions
	{
		get
		{
			_ = _version.Value;
			return _definitions.Values.Select( item => item.Definition ).ToArray();
		}
	}

	/// <summary>Gets the identifier of the module that registered a setting, which the settings editor uses to show it on that module's tab; null when it is not registered.</summary>
	public string? FindModule( SettingDefinition setting ) => _definitions.TryGetValue( setting.Id, out var entry ) && entry.Definition == setting ? entry.ModuleId : null;

	/// <summary>Reports any change to a value, the definitions or the active profile, so views and services that read settings refresh.</summary>
	public event Action? Changed;

	/// <summary>Reports persistence or validation failures to the owning application.</summary>
	internal event Action<string>? Failed;

	/// <summary>Creates in-memory settings or loads the explicitly named local file.</summary>
	public SettingsService( string? path = null )
	{
		_path = path is null ? null : Path.GetFullPath( path );
		EnsureLoaded();
	}

	/// <summary>Reads the effective typed value for the active profile or a specified scope.</summary>
	public T Get<T>( Setting<T> setting, SettingsScope? scope = null ) where T : notnull => (T)GetValue( setting, scope );

	/// <summary>Reads a value through profile override, global default, then built-in default precedence.</summary>
	public object GetValue( SettingDefinition setting, SettingsScope? scope = null )
	{
		_ = _version.Value;
		var definition = EnsureRegistered( setting );
		var selectedScope = scope ?? ComputeProfileScope( _activeProfile );
		var values = ComputeValues( selectedScope );
		if ( values.TryGetValue( setting.Id, out var value ) && definition.TryRead( value, out var result ) )
			return result;

		if ( selectedScope != SettingsScope.Global && _global.TryGetValue( setting.Id, out value ) && definition.TryRead( value, out result ) )
			return result;

		return definition.Default;
	}

	/// <summary>Stores a typed value in global defaults or one profile's overrides.</summary>
	public bool Set<T>( Setting<T> setting, SettingsScope scope, T value ) where T : notnull => SetValue( setting, scope, value );

	/// <summary>Validates and stores an explicit value, applying it live before reporting persistence failures.</summary>
	public bool SetValue( SettingDefinition setting, SettingsScope scope, object value )
	{
		var definition = EnsureRegistered( setting );
		if ( !definition.IsValidValue( value ) )
		{
			MarkFailed( $"The value for {definition.Title} is invalid. {definition.Description}" );
			return false;
		}

		var values = ComputeValues( scope );
		var serialized = JsonSerializer.SerializeToElement( value, value.GetType() );
		if ( values.TryGetValue( setting.Id, out var previous ) && previous.GetRawText() == serialized.GetRawText() )
			return EnsureSaved();

		values[setting.Id] = serialized;
		return UpdateSavedValues();
	}

	/// <summary>Checks whether a scope has a valid explicit value rather than an inherited value.</summary>
	public bool HasValue( SettingDefinition setting, SettingsScope scope )
	{
		_ = _version.Value;
		var definition = EnsureRegistered( setting );
		return ComputeValues( scope ).TryGetValue( setting.Id, out var value ) && definition.TryRead( value, out _ );
	}

	/// <summary>Removes a saved value so its scope resumes inheriting the next default.</summary>
	public bool Reset( SettingDefinition setting, SettingsScope scope )
	{
		EnsureRegistered( setting );
		if ( !ComputeValues( scope ).Remove( setting.Id ) )
			return false;

		return UpdateSavedValues();
	}

	/// <summary>Moves the values a setting was saved under before it changed identifier, such as when it moved to another module, to the setting.</summary>
	/// <remarks>
	/// Each scope keeps a value it already has under the current identifier; a former value the setting cannot read is dropped.
	/// The former identifier is forgotten in every scope, so a later call changes nothing.
	/// </remarks>
	/// <param name="previousId">The identifier the values were saved under.</param>
	/// <param name="setting">The registered setting that takes them.</param>
	/// <returns>Whether any saved value moved or was dropped.</returns>
	public bool MoveValues( string previousId, SettingDefinition setting )
	{
		var definition = EnsureRegistered( setting );
		var isChanged = false;
		foreach ( var values in new[] { _global, _work, _personal } )
		{
			if ( !values.Remove( previousId, out var value ) )
				continue;

			isChanged = true;
			if ( !values.ContainsKey( setting.Id ) && definition.TryRead( value, out _ ) )
				values[setting.Id] = value;
		}

		if ( isChanged )
			UpdateSavedValues();
		return isChanged;
	}

	/// <summary>Selects the profile whose values apply to live views and future feature reads.</summary>
	public bool SetActiveProfile( SettingsProfile profile )
	{
		if ( !Enum.IsDefined( profile ) )
			throw new ArgumentOutOfRangeException( nameof( profile ) );

		if ( _activeProfile == profile )
			return EnsureSaved();

		_activeProfile = profile;
		return UpdateSavedValues();
	}

	/// <summary>Switches between Work and Personal without closing panels or reloading modules.</summary>
	public bool ToggleProfile() => SetActiveProfile( _activeProfile == SettingsProfile.Work ? SettingsProfile.Personal : SettingsProfile.Work );

	/// <summary>Checks all setting identifiers before publishing a module's contributions.</summary>
	internal void EnsureAvailable( IEnumerable<SettingDefinition> definitions )
	{
		var ids = _definitions.Keys.ToHashSet( StringComparer.Ordinal );
		foreach ( var definition in definitions )
		{
			if ( !ids.Add( definition.Id ) )
				throw new InvalidOperationException( $"Duplicate setting: {definition.Id}" );
		}
	}

	/// <summary>Registers validated definitions while preserving previously saved profile values.</summary>
	internal void Register( string moduleId, IReadOnlyList<SettingDefinition> definitions )
	{
		foreach ( var definition in definitions )
			_definitions.Add( definition.Id, new( moduleId, definition ) );

		if ( definitions.Count > 0 )
			MarkChanged();
	}

	/// <summary>Removes a module's schema while retaining its saved values for re-enabling.</summary>
	internal void RemoveModule( string moduleId )
	{
		var ids = _definitions.Values
			.Where( item => item.ModuleId == moduleId )
			.Select( item => item.Definition.Id )
			.ToArray();
		foreach ( var id in ids )
			_definitions.Remove( id );

		if ( ids.Length > 0 )
			MarkChanged();
	}

	/// <summary>Maps a profile to the scope containing its explicit overrides.</summary>
	public static SettingsScope ComputeProfileScope( SettingsProfile profile ) => profile switch
	{
		SettingsProfile.Work => SettingsScope.Work,
		SettingsProfile.Personal => SettingsScope.Personal,
		_ => throw new ArgumentOutOfRangeException( nameof( profile ) ),
	};

	/// <summary>Finds the registered definition and rejects mismatched typed setting handles.</summary>
	private SettingDefinition EnsureRegistered( SettingDefinition setting )
	{
		if ( !_definitions.TryGetValue( setting.Id, out var registered ) || registered.Definition.Kind != setting.Kind )
			throw new InvalidOperationException( $"Setting is not registered: {setting.Id}" );

		return registered.Definition;
	}

	/// <summary>Returns the one owned dictionary for an editable scope.</summary>
	private Dictionary<string, JsonElement> ComputeValues( SettingsScope scope ) => scope switch
	{
		SettingsScope.Global => _global,
		SettingsScope.Work => _work,
		SettingsScope.Personal => _personal,
		_ => throw new ArgumentOutOfRangeException( nameof( scope ) ),
	};

	/// <summary>Persists an edit and invalidates views that read settings.</summary>
	private bool UpdateSavedValues()
	{
		var saved = EnsureSaved();
		MarkChanged();
		return saved;
	}

	/// <summary>Invalidates subscribed views after settings or definitions change.</summary>
	private void MarkChanged()
	{
		_version.Value++;
		Changed?.Invoke();
	}

	/// <summary>Stores and reports a settings failure without closing the application.</summary>
	private void MarkFailed( string message )
	{
		LastError = message;
		Failed?.Invoke( message );
	}

	/// <summary>Stores one module-owned definition without copying its settings values.</summary>
	private sealed record RegisteredSetting( string ModuleId, SettingDefinition Definition );
}
