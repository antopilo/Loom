using System.Diagnostics;
using System.Runtime.Versioning;
using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Hotload;
using Lokn.Modules.Notifications;
using Lokn.Modules.Projects;
using Lokn.Modules.Remote;
using Lokn.Modules.Workspaces;
using Lokn.Modules.Worktrees;

namespace Lokn.Tests;

/// <summary>Checks that a bare instance, <c>Loom.exe [--host] --bare &lt;folder&gt;</c>, computes only its own daemon names, files and commands and
/// registers nothing of the user's Loom, while the user's instance keeps the user's.</summary>
/// <remarks>Nothing here starts, stops or opens a daemon, or writes the registry: the names and paths are computed, and the one process it starts exits
/// through the daemon's test hook before it takes any mutex.</remarks>
internal static class IsolationChecks
{
	/// <summary>Defines the user's app ID, as <c>Program</c> declares it.</summary>
	private const string ApplicationId = "dev.lokn.desktop";

	/// <summary>Defines the variable Goo reads its Vulkan pipeline cache folder from.</summary>
	private const string PipelineCacheVariable = "GOO_VK_PIPELINE_CACHE_DIR";

	/// <summary>Runs the checks on Windows, where the daemon, the Run key and the protocols exist.</summary>
	internal static void Run()
	{
		if ( !OperatingSystem.IsWindows() )
			return;

		CoreChecks.Check( !LoknPaths.IsCustom, "The isolation checks start on the user's data folder." );
		CheckUser();
		CheckBare();
		CheckBareProcess();
		Console.WriteLine( "PASS bare isolation: a bare instance's data, logs, temporary files, Goo cache, app ID, daemon mutex, stop event, pipe and daemon command are its folder's, it writes no Run key, protocol or shortcut, and Loom.exe --bare skips Velopack." );
	}

	/// <summary>Checks the names, files and commands of the user's own Loom.</summary>
	[SupportedOSPlatform( "windows" )]
	private static void CheckUser()
	{
		var temp = Path.Combine( Path.GetTempPath(), "Lokn" );
		CoreChecks.Check( LoknPaths.TempRoot == temp && ModuleHost.DefaultLogPath == Path.Combine( temp, "modules.log" ) && RemoteClient.DaemonLogPath == Path.Combine( temp, "host.log" ),
			"The user's Loom logs to %TEMP%\\Lokn." );
		CoreChecks.Check( HostRunner.ComputeMutexName( null ) == @"Local\LoomHost" && HostRunner.ComputeStopEventName( null ) == @"Local\LoomHost.stop"
			&& DaemonControl.MutexObjectName == @"Local\LoomHost" && DaemonControl.StopEventObjectName == @"Local\LoomHost.stop"
			&& DaemonControl.PipeName == PipeHost.ComputePipeName( null ) && DaemonControl.BuildArguments().SequenceEqual( ["--host"] ),
			"The user's Loom names and starts the user's daemon." );
		CoreChecks.Check( DaemonControl.CanChangeLoginItem && PairingProtocol.CanInstall && NotificationIdentity.CanInstall,
			"The user's Loom keeps its sign-in entry, loom protocol, notification protocol and Start menu shortcut." );
		CoreChecks.Check( BareInstance.ComputeApplicationId( ApplicationId, null ) == ApplicationId && BareInstance.Parse( ["--host"] ) is null,
			"The user's Loom keeps the user's app ID." );
	}

	/// <summary>Moves this process into a temporary bare folder, checks every name, file and command, and moves it back.</summary>
	[SupportedOSPlatform( "windows" )]
	private static void CheckBare()
	{
		var userRoot = LoknPaths.DataRoot;
		var userPipe = DaemonControl.PipeName;
		var userCache = Environment.GetEnvironmentVariable( PipelineCacheVariable );
		var bare = Path.Combine( Path.GetTempPath(), "lokn-isolation-" + Guid.NewGuid().ToString( "N" ) );
		try
		{
			CoreChecks.Check( BareInstance.Parse( ["--bare", bare + Path.DirectorySeparatorChar] ) == bare + Path.DirectorySeparatorChar
				&& BareInstance.Parse( ["--host", "--bare", bare] ) == bare, "The bare folder is read from the command line as a full path." );
			Environment.SetEnvironmentVariable( PipelineCacheVariable, null );
			BareInstance.Enter( bare );
			CoreChecks.Check( LoknPaths.IsCustom && LoknPaths.DataRoot == bare && LoknPaths.TempRoot == bare && Directory.Exists( bare ),
				"A bare instance keeps its data, logs and temporary files in its folder." );
			CoreChecks.Check( Environment.GetEnvironmentVariable( PipelineCacheVariable ) == Path.Combine( bare, "cache", "vulkan" ),
				"A bare instance keeps Goo's pipeline cache in its folder." );

			string[] files =
			[
				SettingsService.DefaultPath, PanelWorkspace.DefaultLayoutPath, NotificationFeed.DefaultPath, BrowserTabs.DefaultPath, ThemeModule.DefaultPath,
				ModuleStates.DefaultPath, ModuleStates.Create( isBare: true ).FilePath, ModuleHost.DefaultLogPath, HostRunner.UpdateMarkerPath,
				CommunityModule.DefaultRoot, HotloadModule.DefaultPath, HotloadModule.DefaultCachePath, ConversationStore.DefaultPath, ProjectStore.DefaultPath,
				WorkspaceStore.DefaultPath, WorktreeSetupStore.DefaultPath, DeviceStore.DefaultPath, RemoteKeys.DefaultHostPath, RemoteKeys.DefaultDevicePath,
				PushService.DefaultKeyPath, RemoteClient.DaemonLogPath,
			];
			var outside = files.Where( file => !file.StartsWith( bare + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase ) ).ToList();
			CoreChecks.Check( outside.Count == 0, "Every file a bare instance reads or writes is in its folder: " + string.Join( ", ", outside ) );
			CoreChecks.Check( RemoteClient.DaemonLogPath == Path.Combine( bare, "host.log" ) && ModuleHost.DefaultLogPath == Path.Combine( bare, "modules.log" ),
				"A bare instance and its daemon log beside its data." );

			var hash = HostRunner.ComputeBareHash( bare );
			CoreChecks.Check( hash.Length == 16 && hash == PipeHost.ComputeBareHash( bare ) && hash == PipeHost.ComputeBareHash( bare + Path.DirectorySeparatorChar ),
				"The shell and the Remote module hash a bare folder alike, with or without a trailing separator." );
			CoreChecks.Check( HostRunner.ComputeMutexName( bare ) == $@"Local\LoomHost.{hash}" && DaemonControl.MutexObjectName == HostRunner.ComputeMutexName( bare )
				&& PipeHost.ComputeMutexName( bare ) == DaemonControl.MutexObjectName,
				"A bare window, its daemon and the Remote module name the bare daemon's mutex alike, never the user's." );
			CoreChecks.Check( HostRunner.ComputeStopEventName( bare ) == $@"Local\LoomHost.stop.{hash}" && DaemonControl.StopEventObjectName == HostRunner.ComputeStopEventName( bare ),
				"A bare window stops only its own daemon, never the user's." );
			CoreChecks.Check( DaemonControl.PipeName == PipeHost.ComputePipeName( bare ) && DaemonControl.PipeName == userPipe + "-" + hash,
				"A bare window links only with its own daemon's pipe." );
			CoreChecks.Check( DaemonControl.BuildArguments().SequenceEqual( ["--host", "--bare", bare] ),
				"A bare window starts its daemon with --host --bare <folder>, never a daemon on the user's data." );
			CoreChecks.Check( !DaemonControl.CanChangeLoginItem && !PairingProtocol.CanInstall && !NotificationIdentity.CanInstall,
				"A bare instance writes no Run key value, loom or lokn-notification protocol, or Start menu shortcut." );

			var applicationId = BareInstance.ComputeApplicationId( ApplicationId, bare );
			CoreChecks.Check( applicationId.StartsWith( ApplicationId + ".bare.", StringComparison.Ordinal ) && applicationId.Length == ApplicationId.Length + ".bare.".Length + 8
				&& applicationId != BareInstance.ComputeApplicationId( ApplicationId, bare + "-other" ),
				"Each bare folder has its own app ID, so its single-instance election and notifications are its own." );
		}
		finally
		{
			LoknPaths.UseDataRoot( userRoot );
			Environment.SetEnvironmentVariable( PipelineCacheVariable, userCache );
			RemoteGatewayChecks.TryDelete( bare );
		}
		CoreChecks.Check( !LoknPaths.IsCustom && DaemonControl.PipeName == userPipe, "The checks return to the user's data folder." );
	}

	/// <summary>Starts the built <c>Loom.exe --host --bare</c> with the daemon's test hook, which exits before any mutex: it logs into its own folder, which
	/// shows <c>Main</c> moved it there first, and Velopack, which logs every run to <c>%LOCALAPPDATA%\velopack\velopack.log</c> under its process ID,
	/// does not run. Other Velopack apps and checks may write the shared log meanwhile, so only this process's lines count.</summary>
	[SupportedOSPlatform( "windows" )]
	private static void CheckBareProcess()
	{
		var loom = RemoteGatewayChecks.FindLoom() ?? throw new InvalidOperationException( "No built Loom.exe under the repository's bin folder or next to the checks; run scripts/build.ps1 first." );
		var velopackLog = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "velopack", "velopack.log" );
		var velopackStart = File.Exists( velopackLog ) ? new FileInfo( velopackLog ).Length : 0;
		var bare = Path.Combine( Path.GetTempPath(), "lokn-isolation-" + Guid.NewGuid().ToString( "N" ) );
		try
		{
			var info = new ProcessStartInfo( loom ) { UseShellExecute = false, CreateNoWindow = true };
			foreach ( var argument in new[] { "--host", "--bare", bare } )
				info.ArgumentList.Add( argument );
			info.Environment["LOKN_TEST_HOST_EXIT"] = "7";
			using var process = Process.Start( info ) ?? throw new InvalidOperationException( "Loom.exe did not start." );
			if ( !process.WaitForExit( 30000 ) )
			{
				process.Kill( entireProcessTree: true );
				throw new InvalidOperationException( "Loom.exe --host --bare with the test hook did not exit." );
			}

			CoreChecks.Check( process.ExitCode == 7 && RemoteGatewayChecks.ReadLog( bare ).Contains( "EXIT test hook 7", StringComparison.Ordinal ),
				"Loom.exe --bare moves into its folder before anything else. " + RemoteGatewayChecks.ReadLog( bare ) );
			CoreChecks.Check( !ReadSince( velopackLog, velopackStart ).Contains( $"[lib-csharp:{process.Id}]", StringComparison.Ordinal ),
				"Loom.exe --bare never runs Velopack, which would apply the user's update and run installer hooks." );
		}
		finally
		{
			RemoteGatewayChecks.TryDelete( bare );
		}
	}

	/// <summary>Reads what a shared log gained after a length, or empty when it is missing.</summary>
	private static string ReadSince( string path, long length )
	{
		if ( !File.Exists( path ) )
			return "";

		using var stream = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete );
		stream.Position = Math.Min( length, stream.Length );
		using var reader = new StreamReader( stream );
		return reader.ReadToEnd();
	}
}
