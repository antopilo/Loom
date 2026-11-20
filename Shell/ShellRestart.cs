using System.Diagnostics;
using Lokn.Core;

namespace Lokn;

/// <summary>Starts Loom again once its window closed, such as to restore a backup before anything reads the data.</summary>
internal static class ShellRestart
{
	/// <summary>Gets whether Loom starts again after its window closes.</summary>
	internal static bool IsRequested
	{
		get; private set;
	}

	/// <summary>Closes the window normally, so state is saved, and has <see cref="Start"/> run once <c>Program.Main</c> let go of everything.</summary>
	internal static void Request( LoknApplication app )
	{
		IsRequested = true;
		app.Window?.RequestClose();
	}

	/// <summary>Starts this executable again with the same arguments, such as <c>--bare</c>; on the main thread after the window and its single-instance lease are gone.</summary>
	internal static void Start( string[] args )
	{
		if ( Environment.ProcessPath is not { } executable )
			return;

		var info = new ProcessStartInfo( executable ) { UseShellExecute = false };
		foreach ( var argument in args )
			info.ArgumentList.Add( argument );
		Process.Start( info )?.Dispose();
	}
}
