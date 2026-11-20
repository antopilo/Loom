using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Lokn.Core;
using Microsoft.Win32.SafeHandles;

namespace Lokn.Modules.Remote;

/// <summary>Keeps remote workspace frames away from what must not leave the host: Loom's keys and pairing state, and network or device paths.</summary>
/// <remarks>
/// Paths must be fully qualified local paths: UNC (<c>\\server\share</c>), device (<c>\\.\</c>, <c>\\?\</c>) and alternate data stream paths are refused before
/// anything touches them, so a remote device cannot make the host authenticate to another machine. The rest are resolved through links, junctions and
/// short names to their final path, which must not be a secret file (<c>*-key.pem</c>, <c>devices.json</c>, <c>push-subscriptions.json</c>,
/// <c>remote-hosts.json</c>, <c>remote-host.json</c>) inside a Loom data folder; moves and deletes may not take a data folder or a folder holding one.
/// Processes and terminals run as the user, so their checks only refuse the obvious: a data folder as the working folder and arguments naming a secret file.
/// </remarks>
internal static class WorkspaceGuard
{
	/// <summary>The names of Loom's secret files, matched without case; <c>remote-host.json</c> stays for data folders not migrated yet.</summary>
	private static readonly string[] SecretNames = ["devices.json", "push-subscriptions.json", "remote-hosts.json", "remote-host.json"];

	/// <summary>The ending of Loom's key files, such as <c>host-key.pem</c>, matched without case.</summary>
	private const string KeySuffix = "-key.pem";

	/// <summary>The answer to a refused path.</summary>
	private const string Refusal = "Loom's own keys and paired devices cannot be reached remotely.";

	/// <summary>Checks a path a workspace frame names and returns it fully qualified.</summary>
	/// <param name="path">The path from the frame.</param>
	/// <param name="isFolderChange">Whether the frame moves or deletes it, which a data folder or any folder holding one must not undergo.</param>
	/// <exception cref="RemoteException">The path is not a local fully qualified path, or reaches Loom's secrets.</exception>
	public static string CheckPath( string path, bool isFolderChange = false )
	{
		var full = CheckLocal( path );
		var canonical = Canonicalize( full );
		foreach ( var root in ReadDataRoots() )
		{
			if ( IsWithin( canonical, root ) && IsSecretName( Path.GetFileName( canonical ) ) )
				throw new RemoteException( RemoteErrors.Scope, Refusal );
			if ( isFolderChange && (IsWithin( root, canonical ) || string.Equals( root, canonical, StringComparison.OrdinalIgnoreCase )) )
				throw new RemoteException( RemoteErrors.Scope, Refusal );
		}
		return full;
	}

	/// <summary>Checks a process or terminal start: the program and working folder are local paths outside the data folders' secrets, the working folder
	/// is no data folder, and no argument names a secret file.</summary>
	/// <exception cref="RemoteException">The start reaches a network path or Loom's secrets.</exception>
	public static void CheckCommand( string file, IReadOnlyList<string> arguments, string? directory )
	{
		if ( IsNetworkPath( file ) )
			throw new RemoteException( RemoteErrors.BadRequest, "Programs on network or device paths cannot be started remotely." );
		if ( Path.IsPathFullyQualified( file ) )
			CheckPath( file );
		if ( directory is not null )
		{
			var canonical = Canonicalize( CheckPath( directory ) );
			if ( ReadDataRoots().Any( root => IsWithin( canonical, root ) || string.Equals( canonical, root, StringComparison.OrdinalIgnoreCase ) ) )
				throw new RemoteException( RemoteErrors.Scope, Refusal );
		}
		foreach ( var argument in arguments.Prepend( file ) )
		{
			if ( SecretNames.Any( name => argument.Contains( name, StringComparison.OrdinalIgnoreCase ) ) || argument.Contains( KeySuffix, StringComparison.OrdinalIgnoreCase ) )
				throw new RemoteException( RemoteErrors.Scope, Refusal );
		}
	}

	/// <summary>Gets whether a file name is one of Loom's secret files.</summary>
	internal static bool IsSecretName( string name ) =>
		name.EndsWith( KeySuffix, StringComparison.OrdinalIgnoreCase ) || SecretNames.Contains( name, StringComparer.OrdinalIgnoreCase );

	/// <summary>Gets whether a path is a UNC, device or <c>\\?\</c> path, which a remote frame may not name.</summary>
	internal static bool IsNetworkPath( string path ) =>
		path.StartsWith( @"\\", StringComparison.Ordinal ) || path.StartsWith( "//", StringComparison.Ordinal ) || path.StartsWith( @"\/", StringComparison.Ordinal )
		|| path.StartsWith( @"/\", StringComparison.Ordinal ) || path.StartsWith( @"\??\", StringComparison.Ordinal );

	/// <summary>Checks a path is a fully qualified local path without a stream name, and returns it normalized.</summary>
	private static string CheckLocal( string path )
	{
		if ( IsNetworkPath( path ) )
			throw new RemoteException( RemoteErrors.BadRequest, "Network and device paths cannot be reached remotely." );
		if ( !Path.IsPathFullyQualified( path ) )
			throw new RemoteException( RemoteErrors.BadRequest, "The path is not a full local path." );
		var full = Path.GetFullPath( path );
		// A colon past the drive names an alternate data stream.
		if ( IsNetworkPath( full ) || full.IndexOf( ':', 2 ) >= 0 )
			throw new RemoteException( RemoteErrors.BadRequest, "Network, device and stream paths cannot be reached remotely." );
		return full;
	}

	/// <summary>Gets the data folders whose secrets are guarded: the one in use and the user's default, resolved like the paths they are compared with.</summary>
	private static IEnumerable<string> ReadDataRoots() =>
		new[] { LoknPaths.DataRoot, LoknPaths.DefaultDataRoot }
			.Select( root => Canonicalize( Path.TrimEndingDirectorySeparator( Path.GetFullPath( root ) ) ) )
			.Distinct( StringComparer.OrdinalIgnoreCase );

	/// <summary>Gets whether <paramref name="path"/> lies inside <paramref name="folder"/>, compared without case.</summary>
	private static bool IsWithin( string path, string folder ) =>
		path.StartsWith( Path.TrimEndingDirectorySeparator( folder ) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase );

	/// <summary>Resolves the deepest existing part of a full path to its final path, through links, junctions and short names, and appends the rest.</summary>
	internal static string Canonicalize( string full )
	{
		if ( !OperatingSystem.IsWindows() )
			return full;

		var existing = full;
		var rest = "";
		while ( !File.Exists( existing ) && !Directory.Exists( existing ) )
		{
			var parent = Path.GetDirectoryName( existing );
			if ( parent is null )
				return full;
			rest = rest.Length == 0 ? Path.GetFileName( existing ) : Path.Combine( Path.GetFileName( existing ), rest );
			existing = parent;
		}
		var final = ReadFinalPath( existing ) ?? existing;
		return Path.TrimEndingDirectorySeparator( rest.Length == 0 ? final : Path.Combine( final, rest ) );
	}

	/// <summary>Reads the final path of an existing file or folder, without the <c>\\?\</c> prefix, or null when Windows cannot give it.</summary>
	[SupportedOSPlatform( "windows" )]
	private static string? ReadFinalPath( string path )
	{
		using var handle = CreateFile( path, 0, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, BackupSemantics, 0 );
		if ( handle.IsInvalid )
			return null;
		var buffer = new StringBuilder( 1024 );
		var length = GetFinalPathNameByHandle( handle, buffer, buffer.Capacity, 0 );
		if ( length == 0 || length >= buffer.Capacity )
			return null;
		var final = buffer.ToString();
		if ( final.StartsWith( @"\\?\UNC\", StringComparison.Ordinal ) )
			return @"\\" + final[8..];
		return final.StartsWith( @"\\?\", StringComparison.Ordinal ) ? final[4..] : final;
	}

	/// <summary>Lets <see cref="CreateFile"/> open folders as well as files.</summary>
	private const int BackupSemantics = 0x02000000;

	/// <summary>Opens a file or folder handle without access rights, enough to ask for its path.</summary>
	[SupportedOSPlatform( "windows" )]
	[DllImport( "kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true )]
	private static extern SafeFileHandle CreateFile( string name, int access, FileShare share, nint security, FileMode mode, int flags, nint template );

	/// <summary>Gets the normalized final path of an open handle.</summary>
	[SupportedOSPlatform( "windows" )]
	[DllImport( "kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true )]
	private static extern int GetFinalPathNameByHandle( SafeFileHandle file, StringBuilder path, int length, int flags );
}
