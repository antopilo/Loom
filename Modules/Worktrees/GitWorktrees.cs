using static Lokn.Modules.Git.GitProcess;

namespace Lokn.Modules.Worktrees;

/// <summary>Describes one working tree attached to a git repository.</summary>
/// <param name="Path">The working tree's absolute directory.</param>
/// <param name="Branch">The checked-out branch's short name, or null when detached or bare.</param>
/// <param name="Head">The checked-out commit hash, or empty for a bare repository.</param>
/// <param name="IsMain">Whether this is the repository's original working tree, which cannot be removed.</param>
/// <param name="IsLocked">Whether git refuses to move or remove it.</param>
/// <param name="IsPrunable">Whether its directory is gone and only git's record remains.</param>
public sealed record WorktreeInfo( string Path, string? Branch, string Head, bool IsMain, bool IsLocked, bool IsPrunable );

/// <summary>Describes one local branch, or a remote branch that has no local branch of the same name.</summary>
/// <param name="Reference">The full reference name, such as refs/heads/main.</param>
/// <param name="Name">The short name: feature/login, or origin/feature/login for a remote branch.</param>
/// <param name="LocalName">The local branch name a working tree checks out: the name without its remote.</param>
/// <param name="IsRemote">Whether only a remote has this branch.</param>
/// <param name="Upstream">The tracked remote branch's short name, or empty.</param>
/// <param name="Track">How far it is from its upstream, as git words it ("ahead 2, behind 1", "gone"), or empty.</param>
/// <param name="Date">The tip commit's committer date.</param>
/// <param name="Subject">The tip commit's subject line.</param>
/// <param name="WorktreePath">The working tree that has it checked out, or null.</param>
internal sealed record BranchInfo( string Reference, string Name, string LocalName, bool IsRemote, string Upstream, string Track, DateTimeOffset Date, string Subject, string? WorktreePath );

/// <summary>Lists, creates, removes, and measures a repository's working trees and branches through the git command line.</summary>
internal static class GitWorktrees
{
	/// <summary>Defines the suffix of the sibling directory that holds a repository's created working trees.</summary>
	private const string ContainerSuffix = ".worktrees";

	/// <summary>Defines the prefix git prints before a local branch's full reference name.</summary>
	private const string BranchPrefix = "refs/heads/";

	/// <summary>Defines the prefix of remote-tracking branch references.</summary>
	private const string RemotePrefix = "refs/remotes/";

	/// <summary>Defines the exit code git config uses for a key that is not set.</summary>
	private const int MissingKeyExitCode = 1;

	/// <summary>Defines the number of fields in one branch record of <see cref="ListBranchesAsync"/>.</summary>
	private const int BranchFieldCount = 6;

	/// <summary>Lists the working trees of the repository containing a directory, main working tree first.</summary>
	internal static async Task<IReadOnlyList<WorktreeInfo>> ListAsync( string repository, CancellationToken cancellationToken )
	{
		var output = await RunAsync( repository, cancellationToken, "worktree", "list", "--porcelain" ).ConfigureAwait( false );
		return Parse( output );
	}

	/// <summary>Creates a working tree for a branch and returns its directory.</summary>
	/// <remarks>
	/// A local branch is checked out as it is, and a branch that only exists on a remote becomes a local branch tracking it.
	/// Any other name creates a new branch from <paramref name="startPoint"/>, or from the main working tree's commit when it is empty.
	/// With <paramref name="directories"/>, the working tree is a cone-mode sparse checkout of those directories plus the
	/// repository's top-level files, so large folders outside them are never written to disk.
	/// </remarks>
	internal static async Task<string> AddAsync( string repository, string branch, string startPoint, IReadOnlyList<string> directories, CancellationToken cancellationToken )
	{
		var worktrees = await ListAsync( repository, cancellationToken ).ConfigureAwait( false );
		var path = BuildPath( worktrees[0].Path, branch );
		var (isLocal, isRemote) = await FindBranchAsync( repository, branch, cancellationToken ).ConfigureAwait( false );
		var exists = isLocal || isRemote;
		if ( exists && startPoint.Length > 0 )
			throw new InvalidOperationException( $"Branch '{branch}' already exists, so it is checked out at its own commit. Clear the start point to use it." );

		var isSparse = directories.Count > 0;
		List<string> arguments = ["worktree", "add"];
		if ( isSparse )
			arguments.Add( "--no-checkout" );
		if ( !exists )
			arguments.AddRange( ["-b", branch] );
		arguments.AddRange( ["--", path] );
		if ( exists )
			arguments.Add( branch );
		else if ( startPoint.Length > 0 )
			arguments.Add( startPoint );

		// A remote-only branch name makes git create the local branch tracking it.
		await RunAsync( repository, cancellationToken, [.. arguments] ).ConfigureAwait( false );
		if ( !isSparse )
			return path;

		try
		{
			// The sparse patterns must be in place before the first checkout writes any file.
			await RunAsync( path, cancellationToken, ["sparse-checkout", "set", "--cone", "--", .. directories] ).ConfigureAwait( false );
			await RunAsync( path, cancellationToken, "checkout" ).ConfigureAwait( false );
		}
		catch ( Exception error )
		{
			// Nothing in the new directory is the user's yet, so a failed or cancelled checkout leaves no half-made working tree behind.
			try
			{
				await RunAsync( repository, CancellationToken.None, "worktree", "remove", "--force", "--", path ).ConfigureAwait( false );
				if ( !isLocal )
					await RunAsync( repository, CancellationToken.None, "branch", "-D", "--", branch ).ConfigureAwait( false );
			}
			catch ( InvalidOperationException cleanup )
			{
				throw new InvalidOperationException( $"{error.Message} Removing the incomplete working tree also failed: {cleanup.Message}", error );
			}

			throw;
		}

		return path;
	}

	/// <summary>Reports whether a branch exists locally and whether it exists on a remote, which decides between checking it out and creating it.</summary>
	private static async Task<(bool IsLocal, bool IsRemote)> FindBranchAsync( string repository, string branch, CancellationToken cancellationToken )
	{
		// for-each-ref also matches branches below a same-named folder, so compare the exact references.
		var references = (await RunAsync( repository, cancellationToken, "for-each-ref", "--format=%(refname)", BranchPrefix + branch, RemotePrefix + "*/" + branch ).ConfigureAwait( false ))
			.Split( '\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries );
		return (references.Contains( BranchPrefix + branch ), references.Any( reference => IsRemoteBranch( reference, branch ) ));
	}

	/// <summary>Reports whether a reference is the branch on one remote, as in refs/remotes/origin/feature/login.</summary>
	private static bool IsRemoteBranch( string reference, string branch )
	{
		if ( !reference.StartsWith( RemotePrefix, StringComparison.Ordinal ) || !reference.EndsWith( "/" + branch, StringComparison.Ordinal ) )
			return false;

		var remote = reference[RemotePrefix.Length..^(branch.Length + 1)];
		return remote.Length > 0 && !remote.Contains( '/' );
	}

	/// <summary>Lists local branches and the remote branches no local branch shares a name with, newest commit first.</summary>
	internal static async Task<IReadOnlyList<BranchInfo>> ListBranchesAsync( string repository, CancellationToken cancellationToken )
	{
		var output = await RunAsync( repository, cancellationToken, "for-each-ref", "--sort=-committerdate",
			"--format=%(refname)%00%(upstream:short)%00%(upstream:track,nobracket)%00%(committerdate:unix)%00%(subject)%00%(worktreepath)",
			"refs/heads", "refs/remotes" ).ConfigureAwait( false );
		return ParseBranches( output );
	}

	/// <summary>Parses <see cref="ListBranchesAsync"/> output, dropping remote HEAD aliases and remote branches a local branch already names.</summary>
	internal static IReadOnlyList<BranchInfo> ParseBranches( string output )
	{
		var branches = new List<BranchInfo>();
		foreach ( var line in output.ReplaceLineEndings( "\n" ).Split( '\n', StringSplitOptions.RemoveEmptyEntries ) )
		{
			var fields = line.Split( '\0' );
			if ( fields.Length < BranchFieldCount )
				continue;

			var reference = fields[0];
			var isRemote = reference.StartsWith( RemotePrefix, StringComparison.Ordinal );
			if ( !isRemote && !reference.StartsWith( BranchPrefix, StringComparison.Ordinal ) )
				continue;

			var name = reference[(isRemote ? RemotePrefix : BranchPrefix).Length..];
			var separator = name.IndexOf( '/' );
			if ( isRemote && separator < 0 )
				continue;

			var localName = isRemote ? name[(separator + 1)..] : name;
			if ( isRemote && localName == "HEAD" )
				continue;

			var date = long.TryParse( fields[3], out var seconds ) ? DateTimeOffset.FromUnixTimeSeconds( seconds ) : DateTimeOffset.MinValue;
			var worktree = fields[5].Length > 0 ? Path.GetFullPath( fields[5] ) : null;
			branches.Add( new( reference, name, localName, isRemote, fields[1], fields[2], date, fields[4], worktree ) );
		}

		var local = branches.Where( branch => !branch.IsRemote ).Select( branch => branch.Name ).ToHashSet( StringComparer.Ordinal );
		return branches.Where( branch => !branch.IsRemote || !local.Contains( branch.LocalName ) ).ToArray();
	}

	/// <summary>Deletes a local branch; git refuses when its commits are not merged into its upstream or HEAD.</summary>
	internal static Task DeleteBranchAsync( string repository, string branch, CancellationToken cancellationToken )
	{
		return RunAsync( repository, cancellationToken, "branch", "-d", "--", branch );
	}

	/// <summary>Fetches every remote and prunes remote branches that no longer exist.</summary>
	internal static Task FetchAsync( string repository, CancellationToken cancellationToken )
	{
		return RunAsync( repository, cancellationToken, "fetch", "--all", "--prune" );
	}

	/// <summary>Lists the index entries below a repository-relative directory as forward-slash paths relative to the repository root.</summary>
	/// <remarks>Entries a sparse checkout leaves out of the working tree are included, since they are still git's files.</remarks>
	internal static async Task<HashSet<string>> ListTrackedAsync( string checkout, string directory, CancellationToken cancellationToken )
	{
		var output = await RunAsync( checkout, cancellationToken, "ls-files", "-z", "--", directory ).ConfigureAwait( false );
		return output.Split( '\0', StringSplitOptions.RemoveEmptyEntries ).ToHashSet( StringComparer.OrdinalIgnoreCase );
	}

	/// <summary>Reports whether git ignores a path; it need not exist yet.</summary>
	internal static async Task<bool> IsIgnoredAsync( string checkout, string path, CancellationToken cancellationToken )
	{
		var result = await RunProcessAsync( checkout, null, cancellationToken, "check-ignore", "-q", "--", path ).ConfigureAwait( false );
		return result.ExitCode switch
		{
			0 => true,
			1 => false,
			_ => throw BuildError( result ),
		};
	}

	/// <summary>Reads the directories a sparse working tree checks out, or an empty list for a full checkout.</summary>
	internal static async Task<IReadOnlyList<string>> ReadSparseDirectoriesAsync( string path, CancellationToken cancellationToken )
	{
		var setting = await RunProcessAsync( path, null, cancellationToken, "config", "--type=bool", "--get", "core.sparseCheckout" ).ConfigureAwait( false );
		if ( setting.ExitCode == MissingKeyExitCode )
			return [];
		if ( setting.ExitCode != 0 )
			throw BuildError( setting );
		if ( setting.Output.Trim() != "true" )
			return [];

		var output = await RunAsync( path, cancellationToken, "sparse-checkout", "list" ).ConfigureAwait( false );
		return output.Split( '\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries );
	}

	/// <summary>Splits typed directories on commas, semicolons, and line breaks into repository-relative forward-slash paths.</summary>
	internal static IReadOnlyList<string> ParseDirectories( string text )
	{
		return text
			.Split( [',', ';', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries )
			.Select( directory => directory.Replace( '\\', '/' ).Trim( '/' ) )
			.Where( directory => directory.Length > 0 )
			.Distinct( StringComparer.Ordinal )
			.ToArray();
	}

	/// <summary>Removes a linked working tree; git refuses when it has uncommitted changes or is locked.</summary>
	/// <remarks>Git deletes through folder links, so callers remove a working tree's links first; see <see cref="WorktreeSharing.RemoveAsync"/>.</remarks>
	internal static Task RemoveAsync( string repository, string path, CancellationToken cancellationToken )
	{
		return RunAsync( repository, cancellationToken, "worktree", "remove", "--", path );
	}

	/// <summary>Computes the bytes of every file below a directory, or zero when the directory is gone.</summary>
	/// <param name="path">The directory to measure.</param>
	/// <param name="excluded">A directory below it whose files are not counted, such as a shared native build, or null.</param>
	/// <param name="cancellationToken">Stops the measurement.</param>
	internal static long ComputeSize( string path, string? excluded, CancellationToken cancellationToken )
	{
		if ( !Directory.Exists( path ) )
			return 0;

		// Reparse points are skipped so junctions and symlinks are neither followed nor counted twice.
		var options = new EnumerationOptions
		{
			RecurseSubdirectories = true,
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.ReparsePoint,
		};

		var prefix = excluded is null ? null : Path.TrimEndingDirectorySeparator( Path.GetFullPath( excluded ) ) + Path.DirectorySeparatorChar;
		long total = 0;
		foreach ( var file in new DirectoryInfo( path ).EnumerateFiles( "*", options ) )
		{
			cancellationToken.ThrowIfCancellationRequested();
			if ( prefix is null || !file.FullName.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) )
				total += file.Length;
		}

		return total;
	}

	/// <summary>Builds the directory for a branch's working tree beside the main working tree.</summary>
	internal static string BuildPath( string mainPath, string branch )
	{
		var main = Path.TrimEndingDirectorySeparator( mainPath );
		var folder = string.Join( "-", branch.Split( Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries ) );
		return Path.Combine( Path.GetDirectoryName( main )!, Path.GetFileName( main ) + ContainerSuffix, folder );
	}

	/// <summary>Parses the blank-line separated records of `git worktree list --porcelain`.</summary>
	internal static IReadOnlyList<WorktreeInfo> Parse( string output )
	{
		var worktrees = new List<WorktreeInfo>();
		var records = output
			.ReplaceLineEndings( "\n" )
			.Split( "\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );

		foreach ( var record in records )
		{
			var lines = record.Split( '\n' );
			var path = ReadValue( lines, "worktree" );
			if ( path is null )
				continue;

			var branch = ReadValue( lines, "branch" );
			worktrees.Add( new(
				Path.GetFullPath( path ),
				branch?.StartsWith( BranchPrefix, StringComparison.Ordinal ) == true ? branch[BranchPrefix.Length..] : branch,
				ReadValue( lines, "HEAD" ) ?? "",
				IsMain: worktrees.Count == 0,
				IsLocked: lines.Any( line => line == "locked" || line.StartsWith( "locked ", StringComparison.Ordinal ) ),
				IsPrunable: lines.Any( line => line == "prunable" || line.StartsWith( "prunable ", StringComparison.Ordinal ) ) ) );
		}

		return worktrees;
	}

	/// <summary>Reads the text after a record's `name value` line, or null when the line is absent.</summary>
	private static string? ReadValue( string[] lines, string name )
	{
		var prefix = name + " ";
		return lines.FirstOrDefault( line => line.StartsWith( prefix, StringComparison.Ordinal ) )?[prefix.Length..];
	}
}
