using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Lokn.Modules.Remote;

/// <summary>Writes files that hold keys or pairing state so that only the current user can open them, whatever the folder's own permissions are.</summary>
public static class SecretFile
{
	/// <summary>Writes text through a temporary file that grants only the current user access, then moves it over <paramref name="path"/>, creating the folder.</summary>
	/// <remarks>The temporary file's protected ACL moves with it, so a <c>--bare</c> folder on a shared or synced location does not expose the file.</remarks>
	public static void WriteAllText( string path, string text )
	{
		Directory.CreateDirectory( Path.GetDirectoryName( Path.GetFullPath( path ) )! );
		var temporary = path + ".tmp";
		var bytes = Encoding.UTF8.GetBytes( text );
#if LOKN_WINDOWS
		if ( OperatingSystem.IsWindows() )
			WriteOwnerOnly( temporary, bytes );
		else
			File.WriteAllBytes( temporary, bytes );
#else
		File.WriteAllBytes( temporary, bytes );
#endif
		File.Move( temporary, path, overwrite: true );
	}

#if LOKN_WINDOWS
	/// <summary>Builds the security of a file only the current user may use: no inherited entries, full control for the user alone.</summary>
	[SupportedOSPlatform( "windows" )]
	internal static FileSecurity BuildOwnerOnly()
	{
		var user = WindowsIdentity.GetCurrent().User!;
		var security = new FileSecurity();
		security.SetOwner( user );
		security.SetAccessRuleProtection( true, false );
		security.AddAccessRule( new FileSystemAccessRule( user, FileSystemRights.FullControl, AccessControlType.Allow ) );
		return security;
	}

	/// <summary>Creates or replaces a file with the owner-only security and writes its bytes.</summary>
	[SupportedOSPlatform( "windows" )]
	private static void WriteOwnerOnly( string path, byte[] bytes )
	{
		File.Delete( path );
		using var stream = new FileInfo( path ).Create( FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, BuildOwnerOnly() );
		stream.Write( bytes );
	}
#endif
}
