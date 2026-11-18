using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Lokn.Modules.Worktrees;

/// <summary>Creates and removes the junctions and hard links that let working trees share folders instead of copying them.</summary>
/// <remarks>
/// Git deletes through a junction whose folder holds tracked files (git clean -fdx and git worktree remove --force both do),
/// so a junction is only made for a folder git has no files in, and removal takes every junction out first. Folders
/// that also hold tracked files get hard links per file instead: deleting a hard link removes only that name.
/// </remarks>
internal static partial class FolderLinks
{
	/// <summary>Defines the reparse tag of a junction (a mount point).</summary>
	private const uint MountPointTag = 0xA0000003;

	/// <summary>Defines the control code that writes a reparse point.</summary>
	private const uint SetReparsePoint = 0x000900A4;

	/// <summary>Defines write access to a file.</summary>
	private const uint GenericWrite = 0x40000000;

	/// <summary>Allows other handles to read, write and delete while ours is open.</summary>
	private const uint ShareAll = 0x00000007;

	/// <summary>Opens only an existing file.</summary>
	private const uint OpenExisting = 3;

	/// <summary>Allows a directory handle and opens the reparse point itself rather than its target.</summary>
	private const uint DirectoryReparseFlags = 0x02000000 | 0x00200000;

	/// <summary>Defines the size of a reparse buffer's tag, length and reserved fields.</summary>
	private const int ReparseHeaderSize = 8;

	/// <summary>Defines the size of a mount point's four name offset and length fields.</summary>
	private const int MountPointHeaderSize = 8;

	/// <summary>Makes <paramref name="link"/> a junction to the existing directory <paramref name="target"/>.</summary>
	/// <remarks>Junctions need no administrator rights or developer mode, unlike directory symbolic links.</remarks>
	internal static void CreateJunction( string link, string target )
	{
		target = Path.TrimEndingDirectorySeparator( Path.GetFullPath( target ) );
		if ( !Directory.Exists( target ) )
			throw new DirectoryNotFoundException( $"Cannot link to {target}: it does not exist." );

		var substitute = Encoding.Unicode.GetBytes( @"\??\" + target );
		var print = Encoding.Unicode.GetBytes( target );
		var dataLength = MountPointHeaderSize + substitute.Length + 2 + print.Length + 2;
		var buffer = new byte[ReparseHeaderSize + dataLength];
		BitConverter.TryWriteBytes( buffer.AsSpan( 0 ), MountPointTag );
		BitConverter.TryWriteBytes( buffer.AsSpan( 4 ), (ushort)dataLength );
		BitConverter.TryWriteBytes( buffer.AsSpan( 8 ), (ushort)0 );
		BitConverter.TryWriteBytes( buffer.AsSpan( 10 ), (ushort)substitute.Length );
		BitConverter.TryWriteBytes( buffer.AsSpan( 12 ), (ushort)(substitute.Length + 2) );
		BitConverter.TryWriteBytes( buffer.AsSpan( 14 ), (ushort)print.Length );
		substitute.CopyTo( buffer, ReparseHeaderSize + MountPointHeaderSize );
		print.CopyTo( buffer, ReparseHeaderSize + MountPointHeaderSize + substitute.Length + 2 );

		Directory.CreateDirectory( link );
		try
		{
			using var handle = CreateFile( link, GenericWrite, ShareAll, 0, OpenExisting, DirectoryReparseFlags, 0 );
			if ( handle.IsInvalid )
				throw new Win32Exception( Marshal.GetLastPInvokeError() );
			if ( !DeviceIoControl( handle, SetReparsePoint, buffer, buffer.Length, 0, 0, out _, 0 ) )
				throw new Win32Exception( Marshal.GetLastPInvokeError() );
		}
		catch
		{
			Directory.Delete( link );
			throw;
		}
	}

	/// <summary>Reports whether a path is a junction or symbolic link rather than a real directory.</summary>
	internal static bool IsLink( string path )
	{
		var info = new DirectoryInfo( path );
		return info.Exists && info.Attributes.HasFlag( FileAttributes.ReparsePoint );
	}

	/// <summary>Removes every junction and directory symbolic link below a directory, without touching what they point at.</summary>
	/// <returns>Each removed link and the directory it pointed at, so a failed removal can put them back.</returns>
	internal static IReadOnlyList<(string Link, string Target)> RemoveLinks( string root )
	{
		var removed = new List<(string Link, string Target)>();
		if ( !Directory.Exists( root ) )
			return removed;

		var pending = new Stack<string>();
		pending.Push( root );
		while ( pending.TryPop( out var directory ) )
		{
			foreach ( var child in new DirectoryInfo( directory ).EnumerateDirectories( "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 } ) )
			{
				if ( child.Attributes.HasFlag( FileAttributes.ReparsePoint ) )
				{
					var target = child.LinkTarget;

					// A non-recursive delete of a link removes the link alone.
					child.Delete();
					if ( target is not null )
						removed.Add( (child.FullName, Path.GetFullPath( target, directory )) );
				}
				else
					pending.Push( child.FullName );
			}
		}

		return removed;
	}

	/// <summary>Hard-links every file below <paramref name="source"/> into the same place below <paramref name="destination"/>.</summary>
	/// <param name="source">The directory whose files are shared.</param>
	/// <param name="destination">The directory that receives the links; created as needed.</param>
	/// <param name="skip">Returns true for a path, relative to <paramref name="source"/> with forward slashes, that must not be linked.</param>
	/// <returns>The number of files linked.</returns>
	/// <remarks>A file already at a destination path is replaced, so linking again refreshes the links. Both directories must be on one volume.</remarks>
	internal static int LinkFiles( string source, string destination, Func<string, bool> skip )
	{
		var count = 0;
		var options = new EnumerationOptions
		{
			RecurseSubdirectories = true,
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.ReparsePoint,
		};
		foreach ( var file in new DirectoryInfo( source ).EnumerateFiles( "*", options ) )
		{
			var relative = Path.GetRelativePath( source, file.FullName ).Replace( '\\', '/' );
			if ( skip( relative ) )
				continue;

			var target = Path.Combine( destination, relative );
			Directory.CreateDirectory( Path.GetDirectoryName( target )! );
			if ( File.Exists( target ) )
				File.Delete( target );
			if ( !CreateHardLink( target, file.FullName, 0 ) )
				throw new IOException( $"Could not link {target} to {file.FullName}: {new Win32Exception( Marshal.GetLastPInvokeError() ).Message}" );

			count++;
		}

		return count;
	}

	/// <summary>Deletes every file below a directory that <paramref name="keep"/> does not claim; removing a hard link leaves its other names intact.</summary>
	/// <returns>The number of files deleted.</returns>
	internal static int DeleteFiles( string directory, Func<string, bool> keep )
	{
		if ( !Directory.Exists( directory ) )
			return 0;

		var count = 0;
		var options = new EnumerationOptions
		{
			RecurseSubdirectories = true,
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.ReparsePoint,
		};
		foreach ( var file in new DirectoryInfo( directory ).EnumerateFiles( "*", options ).ToArray() )
		{
			if ( keep( Path.GetRelativePath( directory, file.FullName ).Replace( '\\', '/' ) ) )
				continue;

			file.Delete();
			count++;
		}

		return count;
	}

	/// <summary>Opens a file or directory handle.</summary>
	[LibraryImport( "kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16 )]
	private static partial SafeFileHandle CreateFile( string name, uint access, uint share, nint security, uint disposition, uint flags, nint template );

	/// <summary>Sends a control code to a file system object.</summary>
	[LibraryImport( "kernel32.dll", SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static partial bool DeviceIoControl( SafeFileHandle device, uint code, byte[] input, int inputSize, nint output, int outputSize, out int returned, nint overlapped );

	/// <summary>Gives an existing file another name on the same volume.</summary>
	[LibraryImport( "kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16 )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static partial bool CreateHardLink( string link, string existing, nint security );
}
