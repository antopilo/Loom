using Lokn.Modules.Git;

namespace Lokn.Modules.Worktrees;

/// <summary>Names the native action a working tree's row offers.</summary>
internal enum NativeAction
{
	/// <summary>Nothing to do.</summary>
	None,

	/// <summary>Record the checkout's own native build so others can share it.</summary>
	Record,

	/// <summary>Link a recorded build with the same native sources.</summary>
	Share,

	/// <summary>Delete the linked native files, before building native here.</summary>
	StopSharing,
}

/// <summary>Describes a working tree's native build.</summary>
/// <param name="Text">What the row says about it.</param>
/// <param name="IsWarning">Whether the build does not match the working tree's native sources.</param>
/// <param name="Action">The action the row offers.</param>
internal sealed record NativeStatus( string Text, bool IsWarning, NativeAction Action );

/// <summary>Holds what the panel shows for one working tree besides git's own record.</summary>
/// <param name="Folders">The directories a sparse checkout has, or empty for a full checkout.</param>
/// <param name="Native">The native build state, or null when the repository does not share native builds.</param>
/// <param name="SharedNative">The linked native output folder, left out of the size, or null.</param>
internal sealed record WorktreeDetails( IReadOnlyList<string> Folders, NativeStatus? Native, string? SharedNative );

/// <summary>Holds one listing of a repository for the panel.</summary>
/// <param name="MainPath">The main working tree's directory, which keys the repository's setup.</param>
/// <param name="Setup">The repository's setup.</param>
/// <param name="Worktrees">The working trees, main first.</param>
/// <param name="Branches">The branches, newest first.</param>
/// <param name="Details">The details by working tree directory.</param>
/// <param name="BranchNative">For branches whose native sources match a recorded build, which checkout holds it.</param>
internal sealed record RepositoryView( string MainPath, RepositorySetup Setup, IReadOnlyList<WorktreeInfo> Worktrees, IReadOnlyList<BranchInfo> Branches,
	IReadOnlyDictionary<string, WorktreeDetails> Details, IReadOnlyDictionary<string, string> BranchNative );

/// <summary>Makes working trees share caches and native builds, reports how they share them, and removes them without reaching through their links.</summary>
internal static class WorktreeSharing
{
	/// <summary>Lists a repository's working trees and branches with their sparse folders and native build state.</summary>
	internal static async Task<RepositoryView> ReadViewAsync( string repository, WorktreeSetupStore store, CancellationToken cancellationToken )
	{
		var worktrees = await GitWorktrees.ListAsync( repository, cancellationToken ).ConfigureAwait( false );
		var main = worktrees[0].Path;
		var snapshot = store.Capture( main );
		var setup = snapshot.Setup;
		var branches = await GitWorktrees.ListBranchesAsync( main, cancellationToken ).ConfigureAwait( false );
		var live = worktrees.Where( worktree => !worktree.IsPrunable && worktree.Head.Length > 0 && Directory.Exists( worktree.Path ) ).ToArray();

		var valid = new Dictionary<string, NativeBuildRecord>( StringComparer.OrdinalIgnoreCase );
		var fingerprints = new Dictionary<string, string>( StringComparer.Ordinal );
		if ( setup.SharesNative )
		{
			foreach ( var worktree in live )
			{
				var key = WorktreeSetupStore.Normalize( worktree.Path );
				if ( snapshot.Builds.TryGetValue( key, out var record ) && NativeBuilds.Matches( record, worktree.Path, setup ) )
					valid[key] = record;
			}

			fingerprints = await NativeBuilds.ComputeFingerprintsAsync( main, [.. live.Select( worktree => worktree.Head ), .. branches.Select( branch => branch.Reference )], setup.NativeInputs, cancellationToken ).ConfigureAwait( false );
		}

		var details = new Dictionary<string, WorktreeDetails>( StringComparer.OrdinalIgnoreCase );
		foreach ( var worktree in live )
		{
			var folders = await GitWorktrees.ReadSparseDirectoriesAsync( worktree.Path, cancellationToken ).ConfigureAwait( false );
			NativeStatus? native = null;
			string? shared = null;
			if ( setup.SharesNative )
			{
				native = await ComputeStatusAsync( worktree, fingerprints[worktree.Head], snapshot, valid, cancellationToken ).ConfigureAwait( false );
				if ( snapshot.Links.ContainsKey( WorktreeSetupStore.Normalize( worktree.Path ) ) )
					shared = Path.Combine( worktree.Path, setup.NativeOutput );
			}

			details[worktree.Path] = new( folders, native, shared );
		}

		var branchNative = new Dictionary<string, string>( StringComparer.Ordinal );
		foreach ( var branch in branches )
		{
			if ( fingerprints.TryGetValue( branch.Reference, out var fingerprint ) && valid.FirstOrDefault( pair => pair.Value.Fingerprint == fingerprint ).Key is { } holder )
				branchNative[branch.Reference] = Path.GetFileName( holder );
		}

		return new( main, setup, worktrees, branches, details, branchNative );
	}

	/// <summary>Works out what a working tree's native build is and what to do about it.</summary>
	private static async Task<NativeStatus> ComputeStatusAsync( WorktreeInfo worktree, string fingerprint, WorktreeSetupSnapshot snapshot, Dictionary<string, NativeBuildRecord> valid, CancellationToken cancellationToken )
	{
		var setup = snapshot.Setup;
		var key = WorktreeSetupStore.Normalize( worktree.Path );
		var isDirty = await NativeBuilds.HasChangesAsync( worktree.Path, setup.NativeInputs, cancellationToken ).ConfigureAwait( false );
		var candidate = valid.FirstOrDefault( pair => pair.Value.Fingerprint == fingerprint && !string.Equals( pair.Key, key, StringComparison.OrdinalIgnoreCase ) ).Key;

		if ( snapshot.Links.TryGetValue( key, out var link ) )
		{
			var source = Path.GetFileName( link.Source );
			if ( !valid.TryGetValue( link.Source, out var linked ) || linked.Fingerprint != link.Fingerprint )
			{
				return candidate is not null && !isDirty
					? new( $"The native build linked from {source} was rebuilt or removed. {Path.GetFileName( candidate )} has a build of these native sources to link instead.", true, NativeAction.Share )
					: new( $"The native build linked from {source} was rebuilt or removed. Stop sharing and build native here.", true, NativeAction.StopSharing );
			}

			if ( isDirty )
				return new( $"Native sources have uncommitted changes that the build linked from {source} does not include. Stop sharing before building native here.", true, NativeAction.StopSharing );
			if ( fingerprint != link.Fingerprint )
				return new( $"This branch's native sources differ from the build linked from {source}. Stop sharing and build native here.", true, NativeAction.StopSharing );

			return new( $"Shares the native build of {source}. Build managed only here.", false, NativeAction.StopSharing );
		}

		if ( snapshot.Builds.TryGetValue( key, out var own ) )
		{
			if ( !valid.ContainsKey( key ) )
				return new( "The native build changed since it was recorded. Record it again once it is built from committed native sources.", true, isDirty ? NativeAction.None : NativeAction.Record );
			if ( isDirty )
				return new( "Native sources have uncommitted changes that the recorded native build does not include.", true, NativeAction.None );
			if ( own.Fingerprint != fingerprint )
				return new( "The recorded native build is of other native sources. Build native for this branch, then record it.", true, NativeAction.None );

			return new( $"Native build recorded ({fingerprint[..NativeBuilds.ShortLength]}). Working trees with the same native sources share it.", false, NativeAction.None );
		}

		if ( await HasOwnBuildAsync( worktree.Path, setup, cancellationToken ).ConfigureAwait( false ) )
		{
			return isDirty
				? new( "Has a native build, and its native sources have uncommitted changes.", false, NativeAction.None )
				: new( "Has a native build that is not recorded. Record it so working trees with the same native sources can share it.", false, NativeAction.Record );
		}

		if ( candidate is not null && !isDirty )
			return new( $"Has the same native sources as the build in {Path.GetFileName( candidate )}.", false, NativeAction.Share );

		return new( "No native build, and no recorded build has these native sources. Build native here.", false, NativeAction.None );
	}

	/// <summary>Reports whether a checkout's native output folder holds files git does not track: a native build.</summary>
	private static async Task<bool> HasOwnBuildAsync( string checkout, RepositorySetup setup, CancellationToken cancellationToken )
	{
		var output = Path.Combine( checkout, setup.NativeOutput );
		if ( !Directory.Exists( output ) )
			return false;

		var tracked = await GitWorktrees.ListTrackedAsync( checkout, setup.NativeOutput, cancellationToken ).ConfigureAwait( false );
		return NativeBuilds.ReadFiles( output ).Any( file => !tracked.Contains( ToRepositoryPath( setup.NativeOutput, file.Path ) ) );
	}

	/// <summary>Records a checkout's native build, refusing when its native sources are uncommitted or changed since it was built.</summary>
	internal static async Task<NativeBuildRecord> RecordAsync( string checkout, RepositorySetup setup, CancellationToken cancellationToken )
	{
		if ( await NativeBuilds.HasChangesAsync( checkout, setup.NativeInputs, cancellationToken ).ConfigureAwait( false ) )
			throw new InvalidOperationException( "Native sources have uncommitted changes. Commit or stash them, build native, then record the build." );
		if ( !await HasOwnBuildAsync( checkout, setup, cancellationToken ).ConfigureAwait( false ) )
			throw new InvalidOperationException( $"{setup.NativeOutput} has no native build to record." );

		var head = (await GitProcess.RunAsync( checkout, cancellationToken, "rev-parse", "HEAD" ).ConfigureAwait( false )).Trim();
		var fingerprint = (await NativeBuilds.ComputeFingerprintsAsync( checkout, [head], setup.NativeInputs, cancellationToken ).ConfigureAwait( false ))[head];
		var files = NativeBuilds.ReadFiles( Path.Combine( checkout, setup.NativeOutput ) );
		if ( await NativeBuilds.VerifyBuiltFromAsync( checkout, setup.NativeInputs, fingerprint, files, cancellationToken ).ConfigureAwait( false ) is { } problem )
			throw new InvalidOperationException( problem );

		return new( setup.NativeOutput, fingerprint, files );
	}

	/// <summary>Hard-links a recorded build with the working tree's native sources into its native output folder.</summary>
	/// <returns>The link to record, and how many files were linked.</returns>
	internal static async Task<(NativeLink Link, int Count)> ShareNativeAsync( string worktree, WorktreeSetupSnapshot snapshot, CancellationToken cancellationToken )
	{
		var setup = snapshot.Setup;
		if ( await NativeBuilds.HasChangesAsync( worktree, setup.NativeInputs, cancellationToken ).ConfigureAwait( false ) )
			throw new InvalidOperationException( "Native sources have uncommitted changes, so no recorded build matches them." );

		var head = (await GitProcess.RunAsync( worktree, cancellationToken, "rev-parse", "HEAD" ).ConfigureAwait( false )).Trim();
		var fingerprint = (await NativeBuilds.ComputeFingerprintsAsync( worktree, [head], setup.NativeInputs, cancellationToken ).ConfigureAwait( false ))[head];
		var key = WorktreeSetupStore.Normalize( worktree );
		var source = snapshot.Builds
			.Where( pair => pair.Value.Fingerprint == fingerprint && !string.Equals( pair.Key, key, StringComparison.OrdinalIgnoreCase ) )
			.Select( pair => pair.Key )
			.FirstOrDefault( checkout => NativeBuilds.Matches( snapshot.Builds[checkout], checkout, setup ) )
			?? throw new InvalidOperationException( $"No recorded native build has this branch's native sources ({fingerprint[..NativeBuilds.ShortLength]}). Build native here." );

		var tracked = await GitWorktrees.ListTrackedAsync( worktree, setup.NativeOutput, cancellationToken ).ConfigureAwait( false );
		var output = Path.Combine( worktree, setup.NativeOutput );
		bool IsTracked( string relative ) => tracked.Contains( ToRepositoryPath( setup.NativeOutput, relative ) );

		// Files of an earlier link go first, so nothing from another build stays beside the new one.
		FolderLinks.DeleteFiles( output, IsTracked );
		var count = FolderLinks.LinkFiles( Path.Combine( source, setup.NativeOutput ), output, IsTracked );
		return (new( source, fingerprint ), count);
	}

	/// <summary>Deletes a working tree's linked native files; the build they link to is untouched.</summary>
	/// <remarks>A native build in a linked working tree would rewrite the shared files in place, so this comes first.</remarks>
	internal static async Task<int> StopSharingNativeAsync( string worktree, RepositorySetup setup, CancellationToken cancellationToken )
	{
		var tracked = await GitWorktrees.ListTrackedAsync( worktree, setup.NativeOutput, cancellationToken ).ConfigureAwait( false );
		return FolderLinks.DeleteFiles( Path.Combine( worktree, setup.NativeOutput ), relative => tracked.Contains( ToRepositoryPath( setup.NativeOutput, relative ) ) );
	}

	/// <summary>Creates the working tree of a branch beside the main checkout with the repository's saved setup, then shares what the setup allows and records its native link.</summary>
	/// <remarks>The panel, the <c>worktrees_enter</c> tool and the chat sidebar all create working trees here.</remarks>
	/// <param name="setup">The repository setups and native records.</param>
	/// <param name="mainPath">The repository's main checkout.</param>
	/// <param name="branch">The branch: an existing local or remote one is checked out, any other name is created.</param>
	/// <param name="startPoint">Where a new branch starts, or empty for the main checkout's commit.</param>
	/// <param name="directories">The folders a sparse checkout writes, or null for the setup's default folders.</param>
	/// <param name="cancellationToken">Stops the work.</param>
	/// <returns>The new working tree's directory, and what was shared or skipped.</returns>
	internal static async Task<(string Path, List<string> Messages)> CreateAsync( WorktreeSetupStore setup, string mainPath, string branch, string startPoint, IReadOnlyList<string>? directories, CancellationToken cancellationToken )
	{
		var snapshot = setup.Capture( mainPath );
		var path = await GitWorktrees.AddAsync( mainPath, branch, startPoint, directories ?? snapshot.Setup.SparseDirectories, cancellationToken ).ConfigureAwait( false );
		var (messages, link) = await ShareNewAsync( mainPath, path, snapshot, cancellationToken ).ConfigureAwait( false );
		if ( link is not null )
			setup.SetLink( path, link );
		return (path, messages);
	}

	/// <summary>Links a new working tree's shared folders to the main checkout and its native output to a matching recorded build.</summary>
	/// <returns>What was shared or skipped, and the native link to record, if any.</returns>
	internal static async Task<(List<string> Messages, NativeLink? Link)> ShareNewAsync( string mainPath, string worktree, WorktreeSetupSnapshot snapshot, CancellationToken cancellationToken )
	{
		var messages = new List<string>();
		var shared = new List<string>();
		foreach ( var folder in snapshot.Setup.SharedFolders )
		{
			if ( await LinkFolderAsync( mainPath, worktree, folder, cancellationToken ).ConfigureAwait( false ) is { } skipped )
				messages.Add( skipped );
			else
				shared.Add( folder );
		}

		if ( shared.Count > 0 )
			messages.Insert( 0, $"Shares {string.Join( ", ", shared )} with the main checkout." );

		NativeLink? link = null;
		if ( snapshot.Setup.SharesNative )
		{
			try
			{
				(link, var count) = await ShareNativeAsync( worktree, snapshot, cancellationToken ).ConfigureAwait( false );
				messages.Add( $"Shares the native build of {Path.GetFileName( link.Source )} ({count} files): build managed only here." );
			}
			catch ( InvalidOperationException error )
			{
				messages.Add( error.Message );
			}
		}

		return (messages, link);
	}

	/// <summary>Makes one folder of a working tree a junction to the main checkout's folder.</summary>
	/// <returns>Null when linked, otherwise why the folder was left alone.</returns>
	private static async Task<string?> LinkFolderAsync( string mainPath, string worktree, string folder, CancellationToken cancellationToken )
	{
		var tracked = await GitWorktrees.ListTrackedAsync( worktree, folder, cancellationToken ).ConfigureAwait( false );
		if ( tracked.Count > 0 )
			return $"Did not share {folder}: git tracks files in it, and git deletes through a link to such a folder.";
		if ( !await GitWorktrees.IsIgnoredAsync( worktree, folder + "/", cancellationToken ).ConfigureAwait( false ) )
			return $"Did not share {folder}: git does not ignore it, so the link would show as a change.";

		var link = Path.Combine( worktree, folder );
		if ( FolderLinks.IsLink( link ) )
			return null;
		if ( Directory.Exists( link ) )
		{
			if ( Directory.EnumerateFileSystemEntries( link ).Any() )
				return $"Did not share {folder}: the working tree already has its own.";

			Directory.Delete( link );
		}

		var target = Path.Combine( mainPath, folder );
		Directory.CreateDirectory( target );
		Directory.CreateDirectory( Path.GetDirectoryName( link )! );
		FolderLinks.CreateJunction( link, target );
		return null;
	}

	/// <summary>Removes a linked working tree: its folder links go first, so git cannot delete through them into the folders they share.</summary>
	/// <remarks>Git would refuse a working tree with changes, so that is checked before any link is touched, and the links come back if git still refuses.</remarks>
	internal static async Task RemoveAsync( string repository, string worktree, CancellationToken cancellationToken )
	{
		var changes = await GitProcess.RunAsync( worktree, cancellationToken, "status", "--porcelain" ).ConfigureAwait( false );
		if ( changes.Trim().Length > 0 )
		{
			var first = changes.ReplaceLineEndings( "\n" ).Split( '\n', StringSplitOptions.RemoveEmptyEntries ).Take( 3 );
			throw new InvalidOperationException( $"{worktree} has uncommitted or untracked changes, so it was not removed: {string.Join( "; ", first )}" );
		}

		var links = FolderLinks.RemoveLinks( worktree );
		try
		{
			await GitWorktrees.RemoveAsync( repository, worktree, cancellationToken ).ConfigureAwait( false );
		}
		catch
		{
			foreach ( var (link, target) in links )
			{
				if ( Directory.Exists( target ) && !Directory.Exists( link ) )
					FolderLinks.CreateJunction( link, target );
			}

			throw;
		}
	}

	/// <summary>Joins the native output folder and a path relative to it into a repository path as git lists it.</summary>
	private static string ToRepositoryPath( string output, string relative ) => output.TrimEnd( '/' ) + "/" + relative;
}
