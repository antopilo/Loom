using System.Text.Json;

namespace Lokn.Core;

/// <summary>Selects the application profile whose overrides are active.</summary>
public enum SettingsProfile
{
	/// <summary>Applies work-specific overrides.</summary>
	Work,

	/// <summary>Applies personal overrides.</summary>
	Personal,
}

/// <summary>Selects saved global defaults or one profile's overrides.</summary>
public enum SettingsScope
{
	/// <summary>Supplies defaults inherited by both profiles.</summary>
	Global,

	/// <summary>Overrides global defaults for work.</summary>
	Work,

	/// <summary>Overrides global defaults for personal use.</summary>
	Personal,
}

/// <summary>Selects the editor used by the settings panel.</summary>
public enum SettingKind
{
	/// <summary>Stores a Boolean value.</summary>
	Boolean,

	/// <summary>Stores a whole number.</summary>
	Integer,

	/// <summary>Stores a text value.</summary>
	Text,
}

/// <summary>Describes a named configurable value contributed by a module.</summary>
public abstract class SettingDefinition
{
	/// <summary>Gets the stable module-qualified setting identifier.</summary>
	public string Id
	{
		get;
	}

	/// <summary>Gets the display name used by the settings editor.</summary>
	public string Title
	{
		get;
	}

	/// <summary>Gets the explanatory text, including any units or supported ranges.</summary>
	public string Description
	{
		get;
	}

	/// <summary>Gets the category used to group settings in the editor.</summary>
	public string Category
	{
		get;
	}

	/// <summary>Gets the value kind supported by the setting.</summary>
	public SettingKind Kind
	{
		get;
	}

	/// <summary>Gets an editor that replaces the settings panel's built-in field, given the application and the scope being edited.</summary>
	/// <remarks>It runs inside the settings panel's build, so it reads and writes through <see cref="SettingsService"/> like any other view.</remarks>
	public Func<LoknApplication, SettingsScope, Goo.Blob>? Editor
	{
		get; init;
	}

	/// <summary>Gets the values a user picks from, shown as a segmented control; null shows a switch, a number stepper or a text field by kind.</summary>
	/// <remarks>Each value must be of the setting's type and pass its validation.</remarks>
	public IReadOnlyList<SettingChoice>? Choices
	{
		get; init;
	}

	/// <summary>Gets the unit an integer is shown with, such as <c>ms</c> or <c>px</c>; null shows the number alone.</summary>
	public string? Unit
	{
		get; init;
	}

	/// <summary>Gets how far the stepper's buttons move an integer.</summary>
	public int Step { get; init; } = 1;

	/// <summary>Gets the smallest integer the stepper goes to; values below it are refused like any invalid value.</summary>
	public int Minimum { get; init; } = int.MinValue;

	/// <summary>Gets the largest integer the stepper goes to.</summary>
	public int Maximum { get; init; } = int.MaxValue;

	/// <summary>Gets the built-in default value before saved defaults or overrides apply.</summary>
	internal abstract object Default
	{
		get;
	}

	/// <summary>Creates immutable setting metadata.</summary>
	private protected SettingDefinition( string id, string title, string description, string category, SettingKind kind )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( id );
		ArgumentException.ThrowIfNullOrWhiteSpace( title );
		ArgumentException.ThrowIfNullOrWhiteSpace( category );
		Id = id;
		Title = title;
		Description = description;
		Category = category;
		Kind = kind;
	}

	/// <summary>Checks a candidate value against its declared type and validation rule.</summary>
	internal abstract bool IsValidValue( object value );

	/// <summary>Reads a valid persisted value without exposing malformed values to features.</summary>
	internal abstract bool TryRead( JsonElement element, out object value );
}

/// <summary>One value of a setting with <see cref="SettingDefinition.Choices"/>.</summary>
/// <param name="Value">The stored value, of the setting's type.</param>
/// <param name="Label">The text on its segment.</param>
public sealed record SettingChoice( object Value, string Label );

/// <summary>Describes a typed Boolean, integer, or text setting with a built-in default.</summary>
public sealed class Setting<T> : SettingDefinition where T : notnull
{
	/// <summary>Stores the optional validation rule for values of this setting.</summary>
	private readonly Func<T, bool>? _isValid;

	/// <summary>Gets the typed built-in default value.</summary>
	public T DefaultValue
	{
		get;
	}

	/// <summary>Gets the built-in value for untyped editor access.</summary>
	internal override object Default => DefaultValue;

	/// <summary>Creates a typed setting and validates its built-in default.</summary>
	public Setting(
		string id,
		string title,
		string description,
		T defaultValue,
		string category,
		Func<T, bool>? isValid = null )
		: base( id, title, description, category, ComputeKind() )
	{
		ArgumentNullException.ThrowIfNull( defaultValue );
		_isValid = isValid;
		DefaultValue = defaultValue;
		if ( !IsValidValue( defaultValue ) )
			throw new ArgumentException( "The built-in setting default is invalid.", nameof( defaultValue ) );
	}

	/// <summary>Checks the runtime type and contains validation failures from module rules.</summary>
	internal override bool IsValidValue( object value )
	{
		try
		{
			return value is T typed && (_isValid?.Invoke( typed ) ?? true);
		}
		catch ( Exception )
		{
			return false;
		}
	}

	/// <summary>Deserializes a persisted value only when its type and range are supported.</summary>
	internal override bool TryRead( JsonElement element, out object value )
	{
		try
		{
			if ( element.Deserialize<T>() is { } typed && IsValidValue( typed ) )
			{
				value = typed;
				return true;
			}
		}
		catch ( JsonException )
		{
		}

		value = DefaultValue;
		return false;
	}

	/// <summary>Maps supported CLR types to their settings editor.</summary>
	private static SettingKind ComputeKind()
	{
		if ( typeof( T ) == typeof( bool ) )
			return SettingKind.Boolean;

		if ( typeof( T ) == typeof( int ) )
			return SettingKind.Integer;

		if ( typeof( T ) == typeof( string ) )
			return SettingKind.Text;

		throw new NotSupportedException( $"Settings do not support {typeof( T ).Name}." );
	}
}
