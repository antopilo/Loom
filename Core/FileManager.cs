using System.Diagnostics;

namespace Lokn.Core;

/// <summary>Opens paths in the local file manager.</summary>
public static class FileManager
{
	/// <summary>Opens a folder or reveals a file. On Unix, revealing a file opens its containing folder.</summary>
	public static void Open( string path, bool selectFile = false, bool useShellExecute = false )
	{
		if ( !OperatingSystem.IsWindows() )
		{
			var full = Path.GetFullPath( path );
			var folder = selectFile ? Path.GetDirectoryName( full )! : full;
			Process.Start( new ProcessStartInfo( "xdg-open" ) { ArgumentList = { folder } } )?.Dispose();
			return;
		}

		var start = new ProcessStartInfo( useShellExecute ? path : "explorer.exe" ) { UseShellExecute = useShellExecute };
		if ( !useShellExecute )
		{
			if ( selectFile )
				start.ArgumentList.Add( "/select," );
			start.ArgumentList.Add( path );
		}
		Process.Start( start )?.Dispose();
	}
}
