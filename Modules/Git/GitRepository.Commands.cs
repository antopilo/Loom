using System.Globalization;
using System.Text;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Holds the commands that change a repository: the working tree, branches and history, remotes and stashes.</summary>
public static partial class GitRepository
{
	/// <summary>Stages files, deletions included.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="paths">The paths to stage; an empty list stages every change.</param>
	/// <param name="ct">Cancels the command.</param>
	public static Task StageAsync( IWorkspaceHost workspace, string root, IReadOnlyList<string> paths, CancellationToken ct ) =>
		paths.Count == 0
			? RunAsync( workspace, root, null, ct, "add", "--all" )
			: RunAsync( workspace, root, JoinPaths( paths ), ct, "add", "--all", "--pathspec-from-file=-", "--pathspec-file-nul" );

	/// <summary>Unstages files, keeping their working-tree changes; before the first commit it removes them from the index.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="paths">The paths to unstage; an empty list unstages everything.</param>
	/// <param name="ct">Cancels the command.</param>
	public static async Task UnstageAsync( IWorkspaceHost workspace, string root, IReadOnlyList<string> paths, CancellationToken ct )
	{
		if ( await HasHeadAsync( workspace, root, ct ).ConfigureAwait( false ) )
		{
			if ( paths.Count == 0 )
				await RunAsync( workspace, root, null, ct, "reset", "--quiet" ).ConfigureAwait( false );
			else
				await RunAsync( workspace, root, JoinPaths( paths ), ct, "reset", "--quiet", "--pathspec-from-file=-", "--pathspec-file-nul", "HEAD" ).ConfigureAwait( false );
		}
		else if ( paths.Count == 0 )
			await RunAsync( workspace, root, null, ct, "rm", "--cached", "-r", "--quiet", "--ignore-unmatch", "--", "." ).ConfigureAwait( false );
		else
			await RunAsync( workspace, root, JoinPaths( paths ), ct, "rm", "--cached", "-r", "--quiet", "--ignore-unmatch", "--pathspec-from-file=-", "--pathspec-file-nul" ).ConfigureAwait( false );
	}

	/// <summary>Discards changes: tracked files get their HEAD content back in the index and working tree, untracked files are deleted.</summary>
	/// <remarks>A renamed or copied file restores its old path too. Before the first commit, tracked files leave the index and the disk.</remarks>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="files">The changes to discard, from <see cref="ReadChangesAsync"/>.</param>
	/// <param name="ct">Cancels the command.</param>
	public static async Task DiscardAsync( IWorkspaceHost workspace, string root, IReadOnlyList<GitFileChange> files, CancellationToken ct )
	{
		var tracked = new List<string>();
		foreach ( var file in files )
		{
			if ( file.Status == '?' )
			{
				await DeleteUntrackedAsync( workspace, root, file.Path, ct ).ConfigureAwait( false );
				continue;
			}

			tracked.Add( file.Path );
			if ( file.OldPath is not null )
				tracked.Add( file.OldPath );
		}
		if ( tracked.Count == 0 )
			return;

		var input = JoinPaths( tracked );
		if ( await HasHeadAsync( workspace, root, ct ).ConfigureAwait( false ) )
			await RunAsync( workspace, root, input, ct, "restore", "--source=HEAD", "--staged", "--worktree", "--pathspec-from-file=-", "--pathspec-file-nul" ).ConfigureAwait( false );
		else
			await RunAsync( workspace, root, input, ct, "rm", "-r", "--force", "--quiet", "--ignore-unmatch", "--pathspec-from-file=-", "--pathspec-file-nul" ).ConfigureAwait( false );
	}

	/// <summary>Stashes the changes of some files only, untracked ones included, and removes them from the working tree and index.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="paths">The paths to stash; must not be empty, since an empty list would stash everything.</param>
	/// <param name="ct">Cancels the command.</param>
	public static Task StashPathsAsync( IWorkspaceHost workspace, string root, IReadOnlyList<string> paths, CancellationToken ct )
	{
		ArgumentOutOfRangeException.ThrowIfZero( paths.Count );
		return RunAsync( workspace, root, JoinPaths( paths ), ct, "stash", "push", "--quiet", "--include-untracked", "--pathspec-from-file=-", "--pathspec-file-nul" );
	}

	/// <summary>Adds a pattern to the repository's top-level <c>.gitignore</c>, on its own line, unless the file already lists it.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="pattern">The ignore pattern, such as <c>/build/</c> or <c>*.log</c>.</param>
	/// <param name="ct">Cancels the write.</param>
	public static async Task IgnoreAsync( IWorkspaceHost workspace, string root, string pattern, CancellationToken ct )
	{
		var path = Path.Combine( root, ".gitignore" );
		var bytes = await workspace.FileExistsAsync( path, ct ).ConfigureAwait( false ) ? await workspace.ReadAsync( path, ct ).ConfigureAwait( false ) : [];
		using var reader = new StreamReader( new MemoryStream( bytes ), Encoding.UTF8, detectEncodingFromByteOrderMarks: true );
		var text = await reader.ReadToEndAsync( ct ).ConfigureAwait( false );
		if ( text.Split( '\n' ).Any( line => line.TrimEnd( '\r' ) == pattern ) )
			return;

		var newline = text.Contains( "\r\n", StringComparison.Ordinal ) ? "\r\n" : "\n";
		var separator = text.Length > 0 && !text.EndsWith( '\n' ) ? newline : "";
		// The line is appended as UTF-8 after the file's bytes, as appending to the file would.
		byte[] appended = [.. bytes, .. new UTF8Encoding( false ).GetBytes( separator + pattern + newline )];
		await workspace.WriteAsync( path, appended, ct ).ConfigureAwait( false );
	}

	/// <summary>Stages one hunk of the working tree's changes by applying its patch to the index.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="patch">The hunk's patch from the unstaged diff, built by <see cref="GitPatch.BuildHunkPatch"/>.</param>
	/// <param name="ct">Cancels the command.</param>
	public static Task StageHunkAsync( IWorkspaceHost workspace, string root, string patch, CancellationToken ct ) =>
		RunAsync( workspace, root, patch, ct, "apply", "--cached", "--whitespace=nowarn", "-" );

	/// <summary>Unstages one hunk by applying its patch to the index in reverse, keeping the working tree as it is.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="patch">The hunk's patch from the staged diff, built by <see cref="GitPatch.BuildHunkPatch"/>.</param>
	/// <param name="ct">Cancels the command.</param>
	public static Task UnstageHunkAsync( IWorkspaceHost workspace, string root, string patch, CancellationToken ct ) =>
		RunAsync( workspace, root, patch, ct, "apply", "--cached", "--reverse", "--whitespace=nowarn", "-" );

	/// <summary>Discards one hunk of the working tree's changes by applying its patch to the working tree in reverse.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="patch">The hunk's patch from the unstaged diff, built by <see cref="GitPatch.BuildHunkPatch"/>.</param>
	/// <param name="ct">Cancels the command.</param>
	public static Task DiscardHunkAsync( IWorkspaceHost workspace, string root, string patch, CancellationToken ct ) =>
		RunAsync( workspace, root, patch, ct, "apply", "--reverse", "--whitespace=nowarn", "-" );

	/// <summary>Commits the staged changes, or replaces the HEAD commit with them and the new message.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="message">The whole commit message.</param>
	/// <param name="amend">Whether to amend the HEAD commit instead of adding one.</param>
	/// <param name="ct">Cancels the command.</param>
	public static Task CommitAsync( IWorkspaceHost workspace, string root, string message, bool amend, CancellationToken ct ) =>
		amend
			? RunAsync( workspace, root, message, ct, "commit", "--quiet", "--amend", "--file=-" )
			: RunAsync( workspace, root, message, ct, "commit", "--quiet", "--file=-" );

	/// <summary>Checks out a branch: a local branch directly, a remote branch through its local tracking branch, a tag detached.</summary>
	/// <remarks>A remote branch uses the local branch of the same name when there is one, and creates it tracking the remote branch otherwise.</remarks>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="branch">The reference, from <see cref="ReadRefsAsync"/>.</param>
	/// <param name="ct">Cancels the command.</param>
	public static async Task CheckoutAsync( IWorkspaceHost workspace, string root, GitRef branch, CancellationToken ct )
	{
		switch ( branch.Kind )
		{
			case GitRefKind.LocalBranch:
				await RunAsync( workspace, root, null, ct, "switch", "--quiet", branch.ShortName ).ConfigureAwait( false );
				break;

			case GitRefKind.RemoteBranch:
				var local = RemoteBranchName( branch );
				var exists = await GitProcess.RunProcessAsync( workspace, root, null, CommandEnvironment, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{local}" ).ConfigureAwait( false );
				if ( exists.ExitCode == 0 )
					await RunAsync( workspace, root, null, ct, "switch", "--quiet", local ).ConfigureAwait( false );
				else
					await RunAsync( workspace, root, null, ct, "switch", "--quiet", "--track", "--create", local, branch.Name ).ConfigureAwait( false );
				break;

			default:
				await RunAsync( workspace, root, null, ct, "switch", "--quiet", "--detach", branch.Name ).ConfigureAwait( false );
				break;
		}
	}

	/// <summary>Checks out a commit with a detached HEAD.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="commit">The commit, by identifier or any revision git accepts.</param>
	/// <param name="ct">Cancels the command.</param>
	public static Task CheckoutCommitAsync( IWorkspaceHost workspace, string root, string commit, CancellationToken ct ) =>
		RunAsync( workspace, root, null, ct, "switch", "--quiet", "--detach", commit );

	/// <summary>Creates a local branch, and optionally checks it out.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="name">The new branch's name.</param>
	/// <param name="startPoint">The commit or reference the branch starts at; a remote branch becomes its upstream, as git configures by default.</param>
	/// <param name="checkout">Whether to check the new branch out.</param>
	/// <param name="ct">Cancels the command.</param>
	public static Task CreateBranchAsync( IWorkspaceHost workspace, string root, string name, string startPoint, bool checkout, CancellationToken ct ) =>
		checkout
			? RunAsync( workspace, root, null, ct, "switch", "--quiet", "--create", name, startPoint )
			: RunAsync( workspace, root, null, ct, "branch", name, startPoint );

	/// <summary>Renames a local branch.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="name">The branch's current name.</param>
	/// <param name="newName">The branch's new name.</param>
	/// <param name="ct">Cancels the command.</param>
	public static Task RenameBranchAsync( IWorkspaceHost workspace, string root, string name, string newName, CancellationToken ct ) =>
		RunAsync( workspace, root, null, ct, "branch", "--move", name, newName );

	/// <summary>Deletes a branch: a local branch locally, a remote branch on its remote with <c>push --delete</c>, a tag locally.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="branch">The reference, from <see cref="ReadRefsAsync"/>.</param>
	/// <param name="force">Whether to delete a local branch that is not merged.</param>
	/// <param name="ct">Cancels the command.</param>
	public static Task DeleteBranchAsync( IWorkspaceHost workspace, string root, GitRef branch, bool force, CancellationToken ct ) => branch.Kind switch
	{
		GitRefKind.LocalBranch => RunAsync( workspace, root, null, ct, "branch", force ? "-D" : "-d", branch.ShortName ),
		GitRefKind.RemoteBranch => RunAsync( workspace, root, null, ct, "push", branch.Remote ?? "", "--delete", $"refs/heads/{RemoteBranchName( branch )}" ),
		_ => DeleteTagAsync( workspace, root, branch.ShortName, ct ),
	};

	/// <summary>Merges a reference into the current branch with git's default message.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="reference">The branch, tag or commit to merge.</param>
	/// <param name="ct">Cancels the command.</param>
	/// <exception cref="InvalidOperationException">The merge failed or stopped for conflicts; <see cref="ReadOperationAsync"/> then reports it.</exception>
	public static Task MergeAsync( IWorkspaceHost workspace, string root, string reference, CancellationToken ct ) =>
		RunAsync( workspace, root, null, ct, "merge", "--no-edit", reference );

	/// <summary>Rebases the current branch onto a reference.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="onto">The branch, tag or commit to rebase onto.</param>
	/// <param name="ct">Cancels the command.</param>
	/// <exception cref="InvalidOperationException">The rebase failed or stopped for conflicts; <see cref="ReadOperationAsync"/> then reports it.</exception>
	public static Task RebaseAsync( IWorkspaceHost workspace, string root, string onto, CancellationToken ct ) =>
		RunAsync( workspace, root, null, ct, "rebase", onto );

	/// <summary>Applies a commit's change on top of the current branch as a new commit.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="commit">The commit to pick.</param>
	/// <param name="ct">Cancels the command.</param>
	public static Task CherryPickAsync( IWorkspaceHost workspace, string root, string commit, CancellationToken ct ) =>
		RunAsync( workspace, root, null, ct, "cherry-pick", commit );

	/// <summary>Adds a commit that undoes a commit's change, with git's default message.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="commit">The commit to revert.</param>
	/// <param name="ct">Cancels the command.</param>
	public static Task RevertAsync( IWorkspaceHost workspace, string root, string commit, CancellationToken ct ) =>
		RunAsync( workspace, root, null, ct, "revert", "--no-edit", commit );

	/// <summary>Moves the current branch to a commit.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="commit">The commit to move to.</param>
	/// <param name="mode">What happens to the index and working tree.</param>
	/// <param name="ct">Cancels the command.</param>
	public static Task ResetAsync( IWorkspaceHost workspace, string root, string commit, GitResetMode mode, CancellationToken ct ) =>
		RunAsync( workspace, root, null, ct, "reset", "--quiet", mode switch
		{
			GitResetMode.Soft => "--soft",
			GitResetMode.Hard => "--hard",
			_ => "--mixed",
		}, commit, "--" );

	/// <summary>Creates a tag: annotated when it has a message, lightweight otherwise.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="name">The tag's name.</param>
	/// <param name="commit">The commit to tag.</param>
	/// <param name="message">The annotation; null or empty makes a lightweight tag.</param>
	/// <param name="ct">Cancels the command.</param>
	public static Task CreateTagAsync( IWorkspaceHost workspace, string root, string name, string commit, string? message, CancellationToken ct ) =>
		string.IsNullOrEmpty( message )
			? RunAsync( workspace, root, null, ct, "tag", name, commit )
			: RunAsync( workspace, root, message, ct, "tag", "--annotate", "--file=-", name, commit );

	/// <summary>Deletes a local tag.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="name">The tag's name.</param>
	/// <param name="ct">Cancels the command.</param>
	public static Task DeleteTagAsync( IWorkspaceHost workspace, string root, string name, CancellationToken ct ) =>
		RunAsync( workspace, root, null, ct, "tag", "--delete", name );

	/// <summary>Abandons a stopped merge, rebase, cherry-pick or revert, returning to where it started.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="operation">The operation, from <see cref="ReadOperationAsync"/>.</param>
	/// <param name="ct">Cancels the command.</param>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="operation"/> is <see cref="GitOperation.None"/>.</exception>
	public static Task AbortAsync( IWorkspaceHost workspace, string root, GitOperation operation, CancellationToken ct ) =>
		RunAsync( workspace, root, null, ct, OperationCommand( operation ), "--abort" );

	/// <summary>Continues a stopped merge, rebase, cherry-pick or revert once its conflicts are resolved and staged, keeping git's messages.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="operation">The operation, from <see cref="ReadOperationAsync"/>.</param>
	/// <param name="ct">Cancels the command.</param>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="operation"/> is <see cref="GitOperation.None"/>.</exception>
	public static Task ContinueAsync( IWorkspaceHost workspace, string root, GitOperation operation, CancellationToken ct ) =>
		RunAsync( workspace, root, null, ct, OperationCommand( operation ), "--continue" );

	/// <summary>Fetches every remote, pruning deleted branches and fetching all tags.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="ct">Cancels the command.</param>
	public static Task FetchAsync( IWorkspaceHost workspace, string root, CancellationToken ct ) =>
		RunAsync( workspace, root, null, ct, "fetch", "--all", "--prune", "--tags" );

	/// <summary>Pulls the current branch's upstream, rebasing onto it or merging it with git's default message.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="rebase">Whether to rebase rather than merge.</param>
	/// <param name="ct">Cancels the command.</param>
	public static Task PullAsync( IWorkspaceHost workspace, string root, bool rebase, CancellationToken ct ) =>
		rebase
			? RunAsync( workspace, root, null, ct, "pull", "--rebase" )
			: RunAsync( workspace, root, null, ct, "pull", "--no-rebase", "--no-edit" );

	/// <summary>Pushes a local branch to its upstream, or, when it has none, to the only remote or <c>origin</c>, setting that as its upstream.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="branch">The local branch, from <see cref="ReadRefsAsync"/>; null pushes the current branch.</param>
	/// <param name="force">Whether to overwrite the remote branch, with <c>--force-with-lease</c> so nobody else's commits are lost.</param>
	/// <param name="ct">Cancels the command.</param>
	/// <exception cref="ArgumentException"><paramref name="branch"/> is not a local branch.</exception>
	public static async Task PushAsync( IWorkspaceHost workspace, string root, GitRef? branch, bool force, CancellationToken ct )
	{
		if ( branch is { Kind: not GitRefKind.LocalBranch } )
			throw new ArgumentException( "Only a local branch can be pushed.", nameof( branch ) );

		var name = branch?.ShortName
			?? (await GitProcess.TryRunAsync( workspace, root, ct, "symbolic-ref", "--short", "--quiet", "HEAD" ).ConfigureAwait( false ))?.Trim()
			?? throw new InvalidOperationException( "HEAD is detached; check out a branch to push it." );

		List<string> arguments = ["push"];
		if ( force )
			arguments.Add( "--force-with-lease" );

		var remote = (await GitProcess.TryRunAsync( workspace, root, ct, "config", "--get", $"branch.{name}.remote" ).ConfigureAwait( false ))?.Trim();
		var merge = (await GitProcess.TryRunAsync( workspace, root, ct, "config", "--get", $"branch.{name}.merge" ).ConfigureAwait( false ))?.Trim();
		if ( !string.IsNullOrEmpty( remote ) && !string.IsNullOrEmpty( merge ) )
			arguments.AddRange( [remote, $"refs/heads/{name}:{merge}"] );
		else
			arguments.AddRange( ["--set-upstream", await ReadDefaultRemoteAsync( workspace, root, ct ).ConfigureAwait( false ), $"refs/heads/{name}:refs/heads/{name}"] );

		await RunAsync( workspace, root, null, ct, [.. arguments] ).ConfigureAwait( false );
	}

	/// <summary>Pushes one tag to the only remote or <c>origin</c>.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="name">The tag's name.</param>
	/// <param name="ct">Cancels the command.</param>
	public static async Task PushTagAsync( IWorkspaceHost workspace, string root, string name, CancellationToken ct ) =>
		await RunAsync( workspace, root, null, ct, "push", await ReadDefaultRemoteAsync( workspace, root, ct ).ConfigureAwait( false ), $"refs/tags/{name}" ).ConfigureAwait( false );

	/// <summary>Stashes the working tree and index changes.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="message">The stash's message; null or empty uses git's default.</param>
	/// <param name="includeUntracked">Whether untracked files are stashed and removed too.</param>
	/// <param name="ct">Cancels the command.</param>
	public static Task StashAsync( IWorkspaceHost workspace, string root, string? message, bool includeUntracked, CancellationToken ct )
	{
		List<string> arguments = ["stash", "push", "--quiet"];
		if ( includeUntracked )
			arguments.Add( "--include-untracked" );
		if ( !string.IsNullOrEmpty( message ) )
			arguments.AddRange( ["--message", message] );

		return RunAsync( workspace, root, null, ct, [.. arguments] );
	}

	/// <summary>Applies a stash to the working tree, and with <paramref name="pop"/> drops it once it applied cleanly.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="index">The stash's position, from <see cref="ReadStashesAsync"/>.</param>
	/// <param name="pop">Whether to drop the stash after applying it.</param>
	/// <param name="ct">Cancels the command.</param>
	public static Task StashApplyAsync( IWorkspaceHost workspace, string root, int index, bool pop, CancellationToken ct ) =>
		RunAsync( workspace, root, null, ct, "stash", pop ? "pop" : "apply", "--quiet", StashName( index ) );

	/// <summary>Deletes a stash.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="index">The stash's position, from <see cref="ReadStashesAsync"/>.</param>
	/// <param name="ct">Cancels the command.</param>
	public static Task StashDropAsync( IWorkspaceHost workspace, string root, int index, CancellationToken ct ) =>
		RunAsync( workspace, root, null, ct, "stash", "drop", "--quiet", StashName( index ) );

	/// <summary>Joins literal paths for <c>--pathspec-from-file=- --pathspec-file-nul</c>, which keeps any number of paths off the command line.</summary>
	private static string JoinPaths( IReadOnlyList<string> paths ) => string.Join( '\0', paths.Select( Literal ) );

	/// <summary>Names a stash by its position.</summary>
	private static string StashName( int index ) => $"stash@{{{index.ToString( CultureInfo.InvariantCulture )}}}";

	/// <summary>Gives a remote branch's name on its remote: <c>origin/feature/login</c> gives <c>feature/login</c>.</summary>
	private static string RemoteBranchName( GitRef branch ) =>
		branch.Remote is { Length: > 0 } remote && branch.ShortName.StartsWith( remote + "/", StringComparison.Ordinal ) ? branch.ShortName[(remote.Length + 1)..] : branch.ShortName;

	/// <summary>Gives the git command that owns an operation's <c>--abort</c> and <c>--continue</c>.</summary>
	private static string OperationCommand( GitOperation operation ) => operation switch
	{
		GitOperation.Merge => "merge",
		GitOperation.Rebase => "rebase",
		GitOperation.CherryPick => "cherry-pick",
		GitOperation.Revert => "revert",
		_ => throw new ArgumentOutOfRangeException( nameof( operation ), operation, "No operation is in progress." ),
	};

	/// <summary>Reads the remote a branch without an upstream pushes to: the only remote, or <c>origin</c> among several.</summary>
	private static async Task<string> ReadDefaultRemoteAsync( IWorkspaceHost workspace, string root, CancellationToken ct )
	{
		var remotes = ParseLines( await RunAsync( workspace, root, null, ct, "remote" ).ConfigureAwait( false ) );
		return remotes switch
		{
			[] => throw new InvalidOperationException( "The repository has no remote to push to." ),
			[var only] => only,
			_ when remotes.Contains( "origin" ) => "origin",
			_ => throw new InvalidOperationException( "The repository has several remotes and none is named origin; set the branch's upstream first." ),
		};
	}

	/// <summary>Deletes an untracked file, or an untracked folder such as a nested repository, inside the working tree.</summary>
	private static async Task DeleteUntrackedAsync( IWorkspaceHost workspace, string root, string path, CancellationToken ct )
	{
		var top = Path.GetFullPath( root );
		var full = Path.GetFullPath( Path.Combine( top, path ) );
		if ( !full.StartsWith( Path.TrimEndingDirectorySeparator( top ) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase ) )
			throw new InvalidOperationException( $"'{path}' is outside the repository." );

		try
		{
			await workspace.DeleteAsync( full, ct ).ConfigureAwait( false );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			throw new InvalidOperationException( $"Could not delete '{path}': {error.Message}", error );
		}
	}
}
