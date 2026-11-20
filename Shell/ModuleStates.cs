using System.Text.Json;

namespace Lokn;

/// <summary>Remembers which installed modules the user turned on or off, so the choice holds across restarts.</summary>
/// <remarks>
/// Normally every installed module is on unless the user turned it off. A bare instance starts with every module off unless the user turned
/// it on, so it shows only the shell until modules are picked in Settings › Modules. The file is written on each change. The daemon reads the
/// same file as Loom on this PC and reads it again whenever it changes (docs/remote.md, Roles); it never writes it.
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

	/// <summary>Gets the file the choices are kept in.</summary>
	internal string FilePath => _path;

	/// <summary>Gets whether the file existed when last read, that is whether the user ever chose; the daemon runs every allowed module until then.</summary>
	internal bool IsChosen
	{
		get; private set;
	}

	/// <summary>Reads the file; a missing or unreadable one starts from the default.</summary>
	/// <param name="path">The file.</param>
	/// <param name="isOffByDefault">Whether a module the user never touched stays off, as in a bare instance.</param>
	internal ModuleStates( string path, bool isOffByDefault )
	{
		_path = path;
		_isOffByDefault = isOffByDefault;
		// An unreadable file falls back to the default rather than stopping startup.
		Reload();
	}

	/// <summary>Creates the choices of an instance: the per-user file, or <c>modules.json</c> in a bare folder, where modules are off unless turned on.</summary>
	/// <param name="bare">The bare folder, or null for the user's instance.</param>
	internal static ModuleStates Create( string? bare ) =>
		bare is null ? new( DefaultPath, isOffByDefault: false ) : new( Path.Combine( bare, "modules.json" ), isOffByDefault: true );

	/// <summary>Reads the file again, replacing the choices held; a missing file clears them.</summary>
	/// <returns>False when the file could not be read, such as while another process writes it; the choices held stay.</returns>
	internal bool Reload()
	{
		try
		{
			var document = File.Exists( _path ) ? JsonSerializer.Deserialize<Document>( File.ReadAllText( _path ) ) : null;
			_on.Clear();
			_off.Clear();
			_on.UnionWith( document?.On ?? [] );
			_off.UnionWith( document?.Off ?? [] );
			IsChosen = document is not null;
			return true;
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or JsonException )
		{
			return false;
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

		IsChosen = true;

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
