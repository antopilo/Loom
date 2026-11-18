using System.Globalization;

namespace Lokn.Modules.Git;

/// <summary>Describes a folder's git repository, branch and working-tree changes at one moment.</summary>
/// <param name="Directory">The folder that was read.</param>
/// <param name="Root">The repository's top-level folder; null when the folder is not in a repository.</param>
/// <param name="Branch">The current branch; null when HEAD is detached or there is no repository.</param>
/// <param name="Commit">The short HEAD commit identifier; null before the first commit.</param>
/// <param name="Upstream">The branch's upstream, such as <c>origin/main</c>, or null.</param>
/// <param name="Ahead">Commits on the branch that its upstream lacks.</param>
/// <param name="Behind">Commits on the upstream that the branch lacks.</param>
/// <param name="Staged">Paths with staged changes.</param>
/// <param name="Unstaged">Tracked paths with changes not staged.</param>
/// <param name="Untracked">Untracked paths, a new folder counting once.</param>
/// <param name="Conflicted">Paths with unresolved merge conflicts.</param>
/// <param name="Files">Distinct changed paths, untracked ones included.</param>
/// <param name="Added">Lines added in tracked files against HEAD.</param>
/// <param name="Removed">Lines removed in tracked files against HEAD.</param>
/// <param name="Error">Why the read failed, such as git not being installed; null when it succeeded.</param>
public sealed record GitState(
	string Directory,
	string? Root,
	string? Branch,
	string? Commit,
	string? Upstream,
	int Ahead,
	int Behind,
	int Staged,
	int Unstaged,
	int Untracked,
	int Conflicted,
	int Files,
	int Added,
	int Removed,
	string? Error )
{
	/// <summary>Gets whether the folder is inside a repository's working tree.</summary>
	public bool IsRepository => Root is not null;

	/// <summary>Defines git's empty tree, the base a repository without commits is compared with.</summary>
	private const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

	/// <summary>Defines how many characters of a commit identifier are shown.</summary>
	private const int ShortCommit = 7;

	/// <summary>Builds the state of a folder outside any repository, or one that could not be read.</summary>
	internal static GitState BuildEmpty( string directory, string? error = null ) => new( directory, null, null, null, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, error );

	/// <summary>Reads a folder's repository, branch and changes with three git commands at most.</summary>
	/// <returns>The state, with <see cref="Error"/> set when git could not be run or failed unexpectedly.</returns>
	internal static async Task<GitState> ReadAsync( string directory, CancellationToken cancellationToken )
	{
		if ( !System.IO.Directory.Exists( directory ) )
			return BuildEmpty( directory, "The folder does not exist." );

		var paths = await GitProcess.RunProcessAsync( directory, null, cancellationToken, "rev-parse", "--path-format=absolute", "--show-toplevel" ).ConfigureAwait( false );
		if ( paths.ExitCode != 0 )
			return BuildEmpty( directory, paths.Diagnostic.Contains( "not a git repository", StringComparison.OrdinalIgnoreCase ) ? null : GitProcess.BuildError( paths ).Message );

		var root = Path.GetFullPath( paths.Output.Trim() );
		var status = await GitProcess.RunAsync( root, cancellationToken, "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=normal" ).ConfigureAwait( false );
		var state = ParseStatus( directory, root, status );
		if ( state.Staged + state.Unstaged + state.Conflicted == 0 )
			return state;

		var numstat = await GitProcess.RunAsync( root, cancellationToken, "diff", "--numstat", "-z", state.Commit is null ? EmptyTree : "HEAD" ).ConfigureAwait( false );
		var (added, removed) = ParseNumstat( numstat );
		return state with
		{
			Added = added,
			Removed = removed,
		};
	}

	/// <summary>Parses <c>git status --porcelain=v2 --branch -z</c> output into the branch and change counts.</summary>
	internal static GitState ParseStatus( string directory, string root, string output )
	{
		string? commit = null, branch = null, upstream = null;
		int ahead = 0, behind = 0, staged = 0, unstaged = 0, untracked = 0, conflicted = 0, files = 0;
		var entries = output.Split( '\0' );
		for ( var index = 0; index < entries.Length; index++ )
		{
			var entry = entries[index];
			if ( entry.Length < 2 )
				continue;

			switch ( entry[0] )
			{
				case '#':
					var header = entry[2..];
					if ( header.StartsWith( "branch.oid ", StringComparison.Ordinal ) && header["branch.oid ".Length..] is var oid && oid != "(initial)" )
						commit = oid;
					else if ( header.StartsWith( "branch.head ", StringComparison.Ordinal ) && header["branch.head ".Length..] is var head && head != "(detached)" )
						branch = head;
					else if ( header.StartsWith( "branch.upstream ", StringComparison.Ordinal ) )
						upstream = header["branch.upstream ".Length..];
					else if ( header.StartsWith( "branch.ab ", StringComparison.Ordinal ) && header["branch.ab ".Length..].Split( ' ' ) is [var plus, var minus] )
					{
						ahead = ParseCount( plus );
						behind = ParseCount( minus );
					}
					break;

				// Ordinary and renamed or copied entries: "1 XY …" and "2 XY …", the latter followed by its original path.
				case '1' or '2' when entry.Length > 4:
					files++;
					if ( entry[2] != '.' )
						staged++;
					if ( entry[3] != '.' )
						unstaged++;
					if ( entry[0] == '2' )
						index++;
					break;

				case 'u':
					files++;
					conflicted++;
					break;

				case '?':
					files++;
					untracked++;
					break;
			}
		}

		return new( directory, root, branch, commit?[..Math.Min( commit.Length, ShortCommit )], upstream, ahead, behind, staged, unstaged, untracked, conflicted, files, 0, 0, null );

		static int ParseCount( string text ) => int.TryParse( text.AsSpan( 1 ), NumberStyles.None, CultureInfo.InvariantCulture, out var count ) ? count : 0;
	}

	/// <summary>Sums <c>git diff --numstat -z</c> output into lines added and removed; binary files count no lines.</summary>
	internal static (int Added, int Removed) ParseNumstat( string output )
	{
		int added = 0, removed = 0;
		var entries = output.Split( '\0' );
		for ( var index = 0; index < entries.Length; index++ )
		{
			if ( entries[index].Split( '\t' ) is not [var plus, var minus, var path] )
				continue;

			added += int.TryParse( plus, NumberStyles.None, CultureInfo.InvariantCulture, out var count ) ? count : 0;
			removed += int.TryParse( minus, NumberStyles.None, CultureInfo.InvariantCulture, out count ) ? count : 0;

			// A rename leaves the path empty and gives the old and new paths as the next two entries.
			if ( path.Length == 0 )
				index += 2;
		}

		return (added, removed);
	}
}
