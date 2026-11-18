using System.Text.Json;

namespace Lokn;

/// <summary>Remembers which installed modules the user turned on or off, so the choice holds across restarts.</summary>
/// <remarks>
/// Normally every installed module is on unless the user turned it off. A bare instance starts with every module off unless the user turned
/// it on, so it shows only the shell until modules are picked in Settings › Modules. The file is written on each change.
/// </remarks>
internal sealed class ModuleStates
{
	/// <summary>Defines readable JSON for the file.</summary>
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	/// <summary>Stores the file path.</summary>
	private readonly string _path;

	/// <summary>Stores whether modules are off unless turned on.</summary>
	private readonly bool _isOffByDefault;

	/// <summary>Stores the modules the user turned on.</summary>
	private readonly HashSet<string> _on = new( StringComparer.Ordinal );

	/// <summary>Stores the modules the user turned off.</summary>
	private readonly HashSet<string> _off = new( StringComparer.Ordinal );

	/// <summary>Gets the per-user file of a normal instance.</summary>
	internal static string DefaultPath => Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "Lokn", "modules.json" );

	/// <summary>Reads the file; a missing or unreadable one starts from the default.</summary>
	/// <param name="path">The file.</param>
	/// <param name="isOffByDefault">Whether a module the user never touched stays off, as in a bare instance.</param>
	internal ModuleStates( string path, bool isOffByDefault )
	{
		_path = path;
		_isOffByDefault = isOffByDefault;
		try
		{
			if ( File.Exists( path ) && JsonSerializer.Deserialize<Document>( File.ReadAllText( path ) ) is { } document )
			{
				_on.UnionWith( document.On ?? [] );
				_off.UnionWith( document.Off ?? [] );
			}
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or JsonException )
		{
			// An unreadable file falls back to the default rather than stopping startup.
		}
	}

	/// <summary>Reports whether a module starts on.</summary>
	internal bool IsOn( string id ) => _isOffByDefault ? _on.Contains( id ) : !_off.Contains( id );

	/// <summary>Records that the user turned a module on or off, and saves it.</summary>
	internal void Set( string id, bool isOn )
	{
		if ( isOn )
		{
			_on.Add( id );
			_off.Remove( id );
		}
		else
		{
			_off.Add( id );
			_on.Remove( id );
		}

		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( _path )! );
			File.WriteAllText( _path, JsonSerializer.Serialize( new Document( [.. _on.Order()], [.. _off.Order()] ), JsonOptions ) );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			// The choice still applies for this session.
		}
	}

	/// <summary>The file's contents.</summary>
	/// <param name="On">The modules turned on.</param>
	/// <param name="Off">The modules turned off.</param>
	private sealed record Document( string[]? On, string[]? Off );
}
