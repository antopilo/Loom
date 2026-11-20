namespace Lokn.Core;

/// <summary>Knows the folder that holds all of Lokn's per-user data: settings, layout, chats, projects, modules and caches.</summary>
/// <remarks>
/// Every build on one Windows account shares <c>%LOCALAPPDATA%\Lokn</c> by default, so a fresh install opens with the data of a
/// development build. A <c>--bare &lt;folder&gt;</c> instance moves all of it, logs and temporary files included, to that folder, so it
/// changes nothing of the user's own Loom.
/// </remarks>
public static class LoknPaths
{
	/// <summary>Gets the default data folder, <c>%LOCALAPPDATA%\Lokn</c>.</summary>
	public static string DefaultDataRoot { get; } = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "Lokn" );

	/// <summary>Gets the folder that holds Lokn's data for this process.</summary>
	public static string DataRoot { get; private set; } = DefaultDataRoot;

	/// <summary>Gets whether this process uses a data folder other than the default.</summary>
	public static bool IsCustom => !string.Equals( DataRoot, DefaultDataRoot, StringComparison.OrdinalIgnoreCase );

	/// <summary>Gets the folder for this process's logs and temporary files: <c>%TEMP%\Lokn</c>, or the data folder itself when it is custom,
	/// so a bare instance writes nothing outside its folder.</summary>
	public static string TempRoot => IsCustom ? DataRoot : Path.Combine( Path.GetTempPath(), "Lokn" );

	/// <summary>Combines parts under the data folder, such as <c>Combine( "settings.json" )</c>.</summary>
	public static string Combine( params string[] parts ) => Path.Combine( [DataRoot, .. parts] );

	/// <summary>Moves Lokn's data to another folder for this process; call once at startup, before anything reads a path.</summary>
	/// <param name="folder">The folder; it is created when missing.</param>
	public static void UseDataRoot( string folder )
	{
		var full = Path.GetFullPath( folder );
		Directory.CreateDirectory( full );
		DataRoot = full;
	}
}
