using Goo;
using Velopack;

namespace Lokn;

/// <summary>Downloads new versions of Loom itself with Velopack and applies them on a restart.</summary>
/// <remarks>
/// A downloaded release waits in the install's packages folder. <see cref="Restart"/> applies it now; otherwise Velopack applies it the
/// next time Loom starts, in <c>VelopackApp.Run</c>, before any window opens.
/// </remarks>
internal sealed class AppUpdater
{
	/// <summary>Stores the manager of the last check, which knows the downloaded release.</summary>
	private UpdateManager? _manager;

	/// <summary>Gets whether a downloaded release waits for a restart.</summary>
	internal bool IsPending => _manager?.UpdatePendingRestart is not null;

	/// <summary>Downloads the newest release when there is one; runs off the UI thread.</summary>
	/// <returns>The version waiting for a restart, or null when Loom is up to date or does not run from an install.</returns>
	internal async Task<string?> DownloadAsync( UpdateFeed feed, CancellationToken cancellationToken )
	{
		var manager = new UpdateManager( feed.App );
		if ( !manager.IsInstalled )
			return null;

		_manager = manager;
		var info = await manager.CheckForUpdatesAsync().ConfigureAwait( false );
		if ( info is null )
			return manager.UpdatePendingRestart?.Version.ToString();

		await manager.DownloadUpdatesAsync( info, null, cancellationToken ).ConfigureAwait( false );
		return info.TargetFullRelease.Version.ToString();
	}

	/// <summary>Closes Loom normally, so its state is saved, and has Velopack apply the downloaded release and start Loom again.</summary>
	/// <returns>False when no release waits for a restart.</returns>
	internal bool Restart( Window window )
	{
		if ( _manager?.UpdatePendingRestart is not { } pending )
			return false;

		// The updater waits for this process to exit before it replaces the files, then starts Loom with the same arguments, such as --data.
		_manager.WaitExitThenApplyUpdates( pending, silent: false, restart: true, Environment.GetCommandLineArgs()[1..] );
		window.RequestClose();
		return true;
	}
}
