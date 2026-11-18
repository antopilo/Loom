namespace Lokn.Modules.Git;

/// <summary>Names a folder the way the Git panel, chats and the sidebar show it.</summary>
internal static class FolderName
{
	/// <summary>Gets a folder's last path segment, or the whole path for a drive root.</summary>
	internal static string Compute( string folder )
	{
		var name = Path.GetFileName( Path.TrimEndingDirectorySeparator( folder ) );
		return name.Length > 0 ? name : folder;
	}
}
