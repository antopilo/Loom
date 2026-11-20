using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lokn.Core;

/// <summary>What <see cref="DataUpgrade.Prepare"/> did with a data folder at start.</summary>
/// <param name="PreviousVersion">The version that ran on the folder before, <see cref="DataUpgrade.EarlierVersion"/> when its data has no <c>version.json</c>,
/// or null for a fresh folder.</param>
/// <param name="BackupFolder">The backup made before this version changes anything, or null when none was needed.</param>
/// <param name="RestoredFrom">The backup a pending restore copied back before anything read the folder, or null.</param>
/// <param name="Error">Why the backup, the restore or <c>version.json</c> failed, or null; the caller reports it and starts normally.</param>
public sealed record DataUpgradeResult( string? PreviousVersion, string? BackupFolder, string? RestoredFrom, Exception? Error );

/// <summary>One backup of a data folder, made before a newer version of Loom first ran on it.</summary>
/// <param name="Folder">The backup's folder, <c>&lt;data&gt;\backups\&lt;version&gt;-&lt;yyyyMMdd-HHmmss&gt;</c>.</param>
/// <param name="Version">The version whose data it holds, or <see cref="DataUpgrade.EarlierVersion"/>.</param>
/// <param name="Created">When it was made, in local time.</param>
public sealed record DataBackup( string Folder, string Version, DateTime Created );

/// <summary>Keeps a data folder safe across Loom versions: records the version that last ran on it in <c>version.json</c>, backs its files up before
/// a newer version first changes them, keeps the newest three backups, and copies one back when the user restores it (docs/architecture.md, Data
/// and upgrades).</summary>
/// <remarks>
/// Loom and its daemon both call <see cref="Prepare"/> at start, before they load a module or migrate anything, under one named mutex per folder,
/// so whichever starts first backs up and the other finds <c>version.json</c> current. A restore is only requested here; the next desktop start
/// copies it back before anything reads the folder.
/// </remarks>
public static class DataUpgrade
{
	/// <summary>Defines the file that records the version that last ran on a data folder.</summary>
	public const string VersionFileName = "version.json";

	/// <summary>Defines the folder in the data folder that holds the backups.</summary>
	public const string BackupsFolderName = "backups";

	/// <summary>Defines the version name of data that has no <c>version.json</c>, written by a Loom older than 0.3.0.</summary>
	public const string EarlierVersion = "earlier";

	/// <summary>Defines how many backups are kept; older ones are deleted after a new one is made.</summary>
	public const int KeptBackups = 3;

	/// <summary>Defines the folder of each chat's message file, which a backup copies whole.</summary>
	private const string MessagesFolderName = "conversations";

	/// <summary>Defines the file in the backups folder that names the backup the next start copies back.</summary>
	private const string PendingRestoreFileName = "restore.json";

	/// <summary>Defines the timestamp format at the end of a backup's folder name.</summary>
	private const string StampFormat = "yyyyMMdd-HHmmss";

	/// <summary>Defines how long a start waits for another process that backs the same folder up.</summary>
	private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds( 60 );

	/// <summary>Lists the data files a backup holds, when they exist.</summary>
	private static readonly string[] BackedUpFiles = ["conversations.json", "projects.json", "workspaces.json", "workspace.json", "settings.json", "modules.json", "browser-tabs.json"];

	/// <summary>Copies a pending restore back, then backs the folder up when <paramref name="version"/> is newer than the version that last ran on
	/// it, keeps the newest backups, and records <paramref name="version"/>; all before anything else reads or writes the folder.</summary>
	/// <param name="folder">The data folder.</param>
	/// <param name="version">The version starting, such as 0.3.0.</param>
	/// <param name="canRestore">Whether this start copies a pending restore back: the desktop does, the daemon leaves it to the desktop.</param>
	/// <param name="now">The time the backup is stamped with, or null for now.</param>
	/// <returns>What was done; a failure is in <see cref="DataUpgradeResult.Error"/> and nothing throws.</returns>
	public static DataUpgradeResult Prepare( string folder, string version, bool canRestore, DateTime? now = null )
	{
		folder = Path.GetFullPath( folder );
		using var mutex = new Mutex( false, ComputeMutexName( folder ) );
		var isOwned = false;
		try
		{
			try
			{
				isOwned = mutex.WaitOne( MutexTimeout );
			}
			catch ( AbandonedMutexException )
			{
				// A start that crashed while it backed up left the mutex behind; this one owns it now.
				isOwned = true;
			}
			return PrepareOwned( folder, version, canRestore, now ?? DateTime.Now );
		}
		finally
		{
			if ( isOwned )
				mutex.ReleaseMutex();
		}
	}

	/// <summary>Reads the version that last ran on a data folder, or null when it has no readable <c>version.json</c>.</summary>
	public static string? ReadLastRunVersion( string folder ) => ReadString( ReadVersionFile( folder ), "lastRunVersion" );

	/// <summary>Reads the version a data folder was upgraded from while the user has not dismissed what is new, or null.</summary>
	/// <remarks>Home shows its What's new card while this is older than the version the card describes.</remarks>
	public static string? ReadUpgradedFrom( string folder ) => ReadString( ReadVersionFile( folder ), "upgradedFrom" );

	/// <summary>Reads a text field of a JSON object, or null when it is missing or not text.</summary>
	private static string? ReadString( JsonObject? document, string name ) =>
		document?[name] is JsonValue value && value.TryGetValue<string>( out var text ) ? text : null;

	/// <summary>Forgets the version a data folder was upgraded from, once the user dismissed what is new; a write failure is ignored, so the card shows again.</summary>
	public static void DismissUpgradedFrom( string folder )
	{
		if ( ReadVersionFile( folder ) is not { } document || document["upgradedFrom"] is null )
			return;

		document.Remove( "upgradedFrom" );
		try
		{
			WriteVersionFile( folder, document );
		}
		catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
		{
		}
	}

	/// <summary>Gets whether a version, as <c>version.json</c> or a backup names it, is older than another; <see cref="EarlierVersion"/> is older than any.</summary>
	public static bool IsOlder( string? version, string than ) => version == EarlierVersion
		|| (Version.TryParse( version, out var parsed ) && Version.TryParse( than, out var other ) && parsed < other);

	/// <summary>Lists a data folder's backups, newest first.</summary>
	public static IReadOnlyList<DataBackup> ListBackups( string folder )
	{
		var root = Path.Combine( folder, BackupsFolderName );
		if ( !Directory.Exists( root ) )
			return [];

		return [.. Directory.EnumerateDirectories( root ).Select( ReadBackup ).OfType<DataBackup>().OrderByDescending( backup => backup.Created )];
	}

	/// <summary>Asks the next desktop start to copy a backup back before anything reads the folder; the caller then restarts Loom.</summary>
	/// <param name="folder">The data folder.</param>
	/// <param name="backup">The backup's folder, one of <see cref="ListBackups"/>.</param>
	/// <exception cref="IOException">The request could not be written.</exception>
	public static void RequestRestore( string folder, string backup )
	{
		var root = Path.Combine( folder, BackupsFolderName );
		Directory.CreateDirectory( root );
		WriteAtomically( Path.Combine( root, PendingRestoreFileName ), new JsonObject { ["backup"] = Path.GetFileName( backup ) }.ToJsonString() );
	}

	/// <summary>Gets the backup a restore waits to copy back at the next start, or null.</summary>
	public static string? ReadPendingRestore( string folder )
	{
		var path = Path.Combine( folder, BackupsFolderName, PendingRestoreFileName );
		try
		{
			return File.Exists( path ) && ReadString( JsonNode.Parse( File.ReadAllText( path ) ) as JsonObject, "backup" ) is { Length: > 0 } name
				? Path.Combine( folder, BackupsFolderName, Path.GetFileName( name ) )
				: null;
		}
		catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException )
		{
			return null;
		}
	}

	/// <summary>Does <see cref="Prepare"/>'s work while holding the folder's mutex.</summary>
	private static DataUpgradeResult PrepareOwned( string folder, string version, bool canRestore, DateTime now )
	{
		string? restored = null;
		Exception? error = null;
		if ( canRestore && ReadPendingRestore( folder ) is { } pending )
		{
			try
			{
				Restore( folder, pending );
				restored = pending;
			}
			catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
			{
				error = new IOException( $"Loom could not restore the backup {pending}: {exception.Message}", exception );
			}
			TryDelete( Path.Combine( folder, BackupsFolderName, PendingRestoreFileName ) );
		}

		var document = ReadVersionFile( folder );
		var previous = ReadString( document, "lastRunVersion" ) ?? (HasData( folder ) ? EarlierVersion : null);
		string? backup = null;
		// A restored backup holds the data of its own version, which this version reads as it did when the backup was made; it is not backed up again.
		if ( restored is null && previous is not null && IsOlder( previous, version ) )
		{
			try
			{
				backup = Backup( folder, previous, now );
				Prune( folder );
			}
			catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
			{
				error ??= new IOException( $"Loom could not back up its data before upgrading from {previous}: {exception.Message}. It starts anyway.", exception );
			}
		}

		if ( previous == version && restored is null )
			return new( previous, null, null, error );

		document ??= [];
		document["lastRunVersion"] = version;
		if ( restored is not null )
			document.Remove( "upgradedFrom" );
		else if ( previous is not null && IsOlder( previous, version ) )
			document["upgradedFrom"] = previous;
		try
		{
			WriteVersionFile( folder, document );
		}
		catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
		{
			error ??= new IOException( $"Loom could not write {Path.Combine( folder, VersionFileName )}: {exception.Message}", exception );
		}
		return new( previous, backup, restored, error );
	}

	/// <summary>Gets whether a folder holds any data a backup would copy, which tells an upgraded folder from a fresh one.</summary>
	private static bool HasData( string folder ) => BackedUpFiles.Any( name => File.Exists( Path.Combine( folder, name ) ) );

	/// <summary>Copies the data files and the chats' message files into a new backup folder named after the version and the time.</summary>
	/// <returns>The backup's folder.</returns>
	private static string Backup( string folder, string previous, DateTime now )
	{
		var root = Path.Combine( folder, BackupsFolderName );
		var target = Path.Combine( root, $"{previous}-{now.ToString( StampFormat, CultureInfo.InvariantCulture )}" );
		for ( var index = 2; Directory.Exists( target ); index++ )
			target = Path.Combine( root, $"{previous}-{now.ToString( StampFormat, CultureInfo.InvariantCulture )}-{index}" );

		// Written into a temporary folder first, so a backup that failed halfway never lists as a complete one.
		var temporary = target + ".partial";
		if ( Directory.Exists( temporary ) )
			Directory.Delete( temporary, recursive: true );
		Directory.CreateDirectory( temporary );
		foreach ( var name in BackedUpFiles )
		{
			var source = Path.Combine( folder, name );
			if ( File.Exists( source ) )
				File.Copy( source, Path.Combine( temporary, name ) );
		}
		var messages = Path.Combine( folder, MessagesFolderName );
		if ( Directory.Exists( messages ) )
			CopyFolder( messages, Path.Combine( temporary, MessagesFolderName ) );
		File.WriteAllText( Path.Combine( temporary, VersionFileName ), new JsonObject { ["lastRunVersion"] = previous }.ToJsonString() );
		Directory.Move( temporary, target );
		return target;
	}

	/// <summary>Deletes all but the <see cref="KeptBackups"/> newest backups, and any left half-written.</summary>
	private static void Prune( string folder )
	{
		foreach ( var old in ListBackups( folder ).Skip( KeptBackups ) )
			Directory.Delete( old.Folder, recursive: true );
		foreach ( var partial in Directory.EnumerateDirectories( Path.Combine( folder, BackupsFolderName ), "*.partial" ) )
			Directory.Delete( partial, recursive: true );
	}

	/// <summary>Copies a backup's files back over the data folder: each data file the backup holds replaces the current one, each it lacks is deleted, and
	/// the message files are replaced whole.</summary>
	private static void Restore( string folder, string backup )
	{
		if ( !Directory.Exists( backup ) )
			throw new DirectoryNotFoundException( $"The backup {backup} no longer exists." );

		foreach ( var name in BackedUpFiles )
		{
			var source = Path.Combine( backup, name );
			var target = Path.Combine( folder, name );
			if ( File.Exists( source ) )
				File.Copy( source, target, overwrite: true );
			else
				TryDelete( target );
		}
		var messages = Path.Combine( folder, MessagesFolderName );
		if ( Directory.Exists( messages ) )
			Directory.Delete( messages, recursive: true );
		var saved = Path.Combine( backup, MessagesFolderName );
		if ( Directory.Exists( saved ) )
			CopyFolder( saved, messages );
	}

	/// <summary>Reads a backup's version and time from its folder name, or null for a folder that is not one.</summary>
	private static DataBackup? ReadBackup( string path )
	{
		var name = Path.GetFileName( path );
		var match = System.Text.RegularExpressions.Regex.Match( name, @"^(?<version>.+)-(?<stamp>\d{8}-\d{6})(-\d+)?$" );
		if ( !match.Success || !DateTime.TryParseExact( match.Groups["stamp"].Value, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var created ) )
			return null;

		return new( path, match.Groups["version"].Value, created );
	}

	/// <summary>Copies a folder and everything under it.</summary>
	private static void CopyFolder( string source, string target )
	{
		Directory.CreateDirectory( target );
		foreach ( var file in Directory.EnumerateFiles( source ) )
			File.Copy( file, Path.Combine( target, Path.GetFileName( file ) ), overwrite: true );
		foreach ( var child in Directory.EnumerateDirectories( source ) )
			CopyFolder( child, Path.Combine( target, Path.GetFileName( child ) ) );
	}

	/// <summary>Reads <c>version.json</c>, or null when it is missing or does not parse.</summary>
	private static JsonObject? ReadVersionFile( string folder )
	{
		var path = Path.Combine( folder, VersionFileName );
		try
		{
			return File.Exists( path ) ? JsonNode.Parse( File.ReadAllText( path ) ) as JsonObject : null;
		}
		catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or JsonException )
		{
			return null;
		}
	}

	/// <summary>Writes <c>version.json</c> through a temporary file.</summary>
	private static void WriteVersionFile( string folder, JsonObject document )
	{
		Directory.CreateDirectory( folder );
		WriteAtomically( Path.Combine( folder, VersionFileName ), document.ToJsonString( new JsonSerializerOptions { WriteIndented = true } ) );
	}

	/// <summary>Writes a file through a sibling temporary file that then replaces it.</summary>
	private static void WriteAtomically( string path, string text )
	{
		var temporary = path + ".tmp";
		File.WriteAllText( temporary, text );
		File.Move( temporary, path, overwrite: true );
	}

	/// <summary>Deletes a file when it exists; a failure is ignored.</summary>
	private static void TryDelete( string path )
	{
		try
		{
			if ( File.Exists( path ) )
				File.Delete( path );
		}
		catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
		{
		}
	}

	/// <summary>Builds the folder's mutex name: <c>Local\Lokn.upgrade.</c> and the first 16 hex digits of SHA-256 over its upper-case full path.</summary>
	private static string ComputeMutexName( string folder ) =>
		@"Local\Lokn.upgrade." + Convert.ToHexStringLower( SHA256.HashData( Encoding.UTF8.GetBytes( Path.TrimEndingDirectorySeparator( folder ).ToUpperInvariant() ) ) )[..16];
}
