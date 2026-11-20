using System.Globalization;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Reads a repository's history, references and changes, and runs the commands of the Git panel.</summary>
/// <remarks>
/// Every method runs git through <see cref="GitProcess"/> on the workspace host it is given first (this PC's or a paired host's) in <c>root</c>, the repository's top-level folder
/// (<see cref="GitState.Root"/>), throws <see cref="InvalidOperationException"/> with git's message on failure, and honours
/// its cancellation token. Commands never wait on an editor or a prompt: messages go in as arguments or standard input,
/// and git runs with a no-op editor. Paths are repository-relative with forward slashes and taken literally, never as globs.
/// </remarks>
public static partial class GitRepository
{
	/// <summary>Defines the fields of one log record, separated by NUL; <c>-z</c> ends each record with NUL too.</summary>
	private const string LogFormat = "--format=%H%x00%h%x00%P%x00%an%x00%ae%x00%aI%x00%cn%x00%ce%x00%cI%x00%s%x00%b";

	/// <summary>Defines how many NUL-separated fields one log record has.</summary>
	private const int LogFields = 11;

	/// <summary>Defines the fields of one reference line, separated by NUL.</summary>
	private const string RefFormat = "--format=%(refname)%00%(objectname)%00%(*objectname)%00%(HEAD)%00%(upstream:short)%00%(upstream:track,nobracket)";

	/// <summary>Defines how many leading bytes decide whether an untracked file is binary, as git decides.</summary>
	private const int BinaryProbe = 8000;

	/// <summary>Defines the largest untracked file on a remote host read whole to count its lines; git counts larger ones there instead.</summary>
	private const long RemoteReadLimit = 1 << 20;

	/// <summary>Defines the buffer an untracked file on this PC is counted through.</summary>
	private const int CountBuffer = 1 << 16;

	/// <summary>Defines the patch options every unified diff uses, so the output parses the same whatever the user's configuration.</summary>
	private static readonly string[] PatchOptions = ["--no-color", "--no-ext-diff", "--src-prefix=a/", "--dst-prefix=b/", "-M"];

	/// <summary>
	/// Defines the environment of every command: a no-op editor, so a continued merge or rebase never waits on one.
	/// Paths are marked literal one by one with <see cref="Literal"/>; <c>GIT_LITERAL_PATHSPECS</c> would also apply to
	/// pathspecs git builds itself, and makes <c>stash push --include-untracked</c> leave untracked files behind.
	/// </summary>
	private static readonly IReadOnlyDictionary<string, string> CommandEnvironment = new Dictionary<string, string>
	{
		["GIT_EDITOR"] = ":",
	};

	/// <summary>Marks a repository-relative path as a literal pathspec, so a file name with glob characters names only itself.</summary>
	internal static string Literal( string path ) => ":(literal)" + path;

	/// <summary>Reads up to <paramref name="limit"/> commits of every branch, remote branch, tag and HEAD, in topological order.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="limit">The most commits to read; zero or less reads all of them.</param>
	/// <param name="ct">Cancels the read.</param>
	/// <returns>The commits newest first, each parent after its children; empty for a repository without commits.</returns>
	public static async Task<IReadOnlyList<GitCommit>> ReadLogAsync( IWorkspaceHost workspace, string root, int limit, CancellationToken ct )
	{
		var hasHead = await HasHeadAsync( workspace, root, ct ).ConfigureAwait( false );
		List<string> arguments = ["log", "-z", "--topo-order", "--no-show-signature", "--encoding=UTF-8", LogFormat, "--branches", "--remotes", "--tags"];
		if ( limit > 0 )
			arguments.Add( $"--max-count={limit.ToString( CultureInfo.InvariantCulture )}" );
		if ( hasHead )
			arguments.Add( "HEAD" );
		arguments.Add( "--" );

		return ParseLog( await RunAsync( workspace, root, null, ct, [.. arguments] ).ConfigureAwait( false ) );
	}

	/// <summary>Reads the local branches, remote branches without their <c>HEAD</c> aliases, and tags, in that order.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="ct">Cancels the read.</param>
	public static async Task<IReadOnlyList<GitRef>> ReadRefsAsync( IWorkspaceHost workspace, string root, CancellationToken ct )
	{
		var refs = RunAsync( workspace, root, null, ct, "for-each-ref", RefFormat, "refs/heads", "refs/remotes", "refs/tags" );
		var remotes = RunAsync( workspace, root, null, ct, "remote" );
		return ParseRefs( await refs.ConfigureAwait( false ), ParseLines( await remotes.ConfigureAwait( false ) ) );
	}

	/// <summary>Reads the stash list, newest first.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="ct">Cancels the read.</param>
	public static async Task<IReadOnlyList<GitStash>> ReadStashesAsync( IWorkspaceHost workspace, string root, CancellationToken ct ) =>
		ParseStashes( await RunAsync( workspace, root, null, ct, "stash", "list", "-z", "--format=%gd%x00%H%x00%gs" ).ConfigureAwait( false ) );

	/// <summary>Reads the files a commit changed against its first parent, or against the empty tree for a root commit.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="commit">The commit, by identifier or any revision git accepts.</param>
	/// <param name="ct">Cancels the read.</param>
	public static async Task<IReadOnlyList<GitFileChange>> ReadCommitFilesAsync( IWorkspaceHost workspace, string root, string commit, CancellationToken ct ) =>
		ParseDiff( await RunAsync( workspace, root, null, ct, "diff-tree", "-r", "--root", "--no-commit-id", "-M", "--diff-merges=first-parent", "--raw", "--numstat", "-z", commit, "--" )
			.ConfigureAwait( false ) );

	/// <summary>Reads a commit's unified diff against its first parent, of every file or of one.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="commit">The commit, by identifier or any revision git accepts.</param>
	/// <param name="path">The one file to show, by its new path; null shows every file. A renamed file shows as a rename.</param>
	/// <param name="ct">Cancels the read.</param>
	public static async Task<string> ReadCommitPatchAsync( IWorkspaceHost workspace, string root, string commit, string? path, CancellationToken ct )
	{
		List<string> arguments = ["-c", "core.quotepath=false", "diff-tree", "-r", "--root", "--no-commit-id", "--diff-merges=first-parent", "-p", .. PatchOptions, commit, "--"];
		if ( path is not null )
		{
			arguments.Add( Literal( path ) );
			// Rename detection only pairs paths inside the pathspec, so a renamed file needs its old path too.
			var files = ParseDiff( await RunAsync( workspace, root, null, ct, "diff-tree", "-r", "--root", "--no-commit-id", "-M", "--diff-merges=first-parent", "--raw", "-z", commit, "--" )
				.ConfigureAwait( false ) );
			if ( files.FirstOrDefault( file => file.Path == path ) is { OldPath: { } oldPath } )
				arguments.Add( Literal( oldPath ) );
		}

		return await RunAsync( workspace, root, null, ct, [.. arguments] ).ConfigureAwait( false );
	}

	/// <summary>Reads the staged and unstaged changes with their line counts.</summary>
	/// <remarks>Conflicted files appear among the unstaged changes as <c>'U'</c>, untracked files as <c>'?'</c>, each file of a new folder separately.</remarks>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="ct">Cancels the read.</param>
	public static async Task<GitWorkingChanges> ReadChangesAsync( IWorkspaceHost workspace, string root, CancellationToken ct )
	{
		var status = RunAsync( workspace, root, null, ct, "status", "--porcelain=v2", "-z", "--untracked-files=all" );
		var staged = RunAsync( workspace, root, null, ct, "diff", "--cached", "--numstat", "-z", "-M" );
		var unstaged = RunAsync( workspace, root, null, ct, "diff", "--numstat", "-z", "-M" );
		var changes = ParseStatus( await status.ConfigureAwait( false ), ParseNumstat( await staged.ConfigureAwait( false ) ), ParseNumstat( await unstaged.ConfigureAwait( false ) ) );

		var unstagedFiles = new List<GitFileChange>( changes.Unstaged.Count );
		foreach ( var file in changes.Unstaged )
		{
			if ( file.Status != '?' )
			{
				unstagedFiles.Add( file );
				continue;
			}

			var (lines, binary) = await CountLinesAsync( workspace, root, file.Path, ct ).ConfigureAwait( false );
			unstagedFiles.Add( file with
			{
				Added = lines,
				IsBinary = binary,
			} );
		}

		return changes with
		{
			Unstaged = unstagedFiles,
		};
	}

	/// <summary>Reads the unified diff of one working-tree change.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="file">The change, from <see cref="ReadChangesAsync"/>.</param>
	/// <param name="staged">Whether the change is staged, compared index to HEAD, rather than working tree to index.</param>
	/// <param name="ct">Cancels the read.</param>
	/// <returns>
	/// The patch. An untracked file diffs against nothing, so every line is added; a conflicted file diffs against HEAD, so
	/// its conflict markers show as added lines.
	/// </returns>
	public static async Task<string> ReadWorkingPatchAsync( IWorkspaceHost workspace, string root, GitFileChange file, bool staged, CancellationToken ct )
	{
		if ( file.Status == '?' )
		{
			// A difference makes --no-index exit with 1, which is its success.
			var result = await GitProcess.RunProcessAsync( workspace, root, null, CommandEnvironment, ct, ["-c", "core.quotepath=false", "diff", "--no-index", .. PatchOptions, "--", "/dev/null", file.Path] )
				.ConfigureAwait( false );
			return result.ExitCode is 0 or 1 ? result.Output : throw BuildError( result );
		}

		List<string> arguments = ["-c", "core.quotepath=false", "diff", .. PatchOptions];
		if ( staged )
			arguments.Add( "--cached" );
		else if ( file.Status == 'U' && await HasHeadAsync( workspace, root, ct ).ConfigureAwait( false ) )
			arguments.Add( "HEAD" );

		arguments.Add( "--" );
		arguments.Add( Literal( file.Path ) );
		if ( file.OldPath is not null )
			arguments.Add( Literal( file.OldPath ) );

		return await RunAsync( workspace, root, null, ct, [.. arguments] ).ConfigureAwait( false );
	}

	/// <summary>Reads which merge, rebase, cherry-pick or revert the repository is in the middle of.</summary>
	/// <remarks>A <c>git am</c> session, which shares the rebase folder, reads as no operation.</remarks>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="ct">Cancels the read.</param>
	public static async Task<GitOperation> ReadOperationAsync( IWorkspaceHost workspace, string root, CancellationToken ct )
	{
		var output = await RunAsync( workspace, root, null, ct, "rev-parse", "--path-format=absolute",
			"--git-path", "rebase-merge", "--git-path", "rebase-apply", "--git-path", "MERGE_HEAD", "--git-path", "CHERRY_PICK_HEAD", "--git-path", "REVERT_HEAD" ).ConfigureAwait( false );
		if ( ParseLines( output ) is not [var rebaseMerge, var rebaseApply, var merge, var cherryPick, var revert] )
			throw new InvalidOperationException( "git rev-parse --git-path printed an unexpected answer." );

		if ( await workspace.DirectoryExistsAsync( rebaseMerge, ct ).ConfigureAwait( false ) )
			return GitOperation.Rebase;
		if ( await workspace.DirectoryExistsAsync( rebaseApply, ct ).ConfigureAwait( false ) )
			return await workspace.FileExistsAsync( Path.Combine( rebaseApply, "applying" ), ct ).ConfigureAwait( false ) ? GitOperation.None : GitOperation.Rebase;
		if ( await workspace.FileExistsAsync( merge, ct ).ConfigureAwait( false ) )
			return GitOperation.Merge;
		if ( await workspace.FileExistsAsync( cherryPick, ct ).ConfigureAwait( false ) )
			return GitOperation.CherryPick;
		if ( await workspace.FileExistsAsync( revert, ct ).ConfigureAwait( false ) )
			return GitOperation.Revert;

		return GitOperation.None;
	}

	/// <summary>Reads the HEAD commit's whole message, to start an amend from.</summary>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="ct">Cancels the read.</param>
	/// <returns>The message without trailing whitespace; empty before the first commit.</returns>
	public static async Task<string> ReadHeadMessageAsync( IWorkspaceHost workspace, string root, CancellationToken ct )
	{
		if ( !await HasHeadAsync( workspace, root, ct ).ConfigureAwait( false ) )
			return "";

		var message = await RunAsync( workspace, root, null, ct, "log", "-1", "--no-show-signature", "--encoding=UTF-8", "--format=%B", "HEAD", "--" ).ConfigureAwait( false );
		return message.TrimEnd();
	}

	/// <summary>Runs one repository command with the shared environment, returning its output or throwing git's message.</summary>
	/// <param name="workspace">The host git runs on.</param>
	/// <param name="root">The folder git runs in.</param>
	/// <param name="input">Text for git's standard input, or null for none.</param>
	/// <param name="ct">Cancels and kills the command.</param>
	/// <param name="arguments">The git arguments.</param>
	private static async Task<string> RunAsync( IWorkspaceHost workspace, string root, string? input, CancellationToken ct, params string[] arguments )
	{
		var result = await GitProcess.RunProcessAsync( workspace, root, input, CommandEnvironment, ct, arguments ).ConfigureAwait( false );
		return result.ExitCode == 0 ? result.Output : throw BuildError( result );
	}

	/// <summary>Builds the error for a failed command from its diagnostic, or from its output when a command such as merge reports there.</summary>
	private static InvalidOperationException BuildError( GitResult result ) =>
		GitProcess.BuildError( result.Diagnostic.Trim().Length == 0 ? result with { Diagnostic = result.Output } : result );

	/// <summary>Reports whether HEAD names a commit, which it does not before the first commit.</summary>
	private static async Task<bool> HasHeadAsync( IWorkspaceHost workspace, string root, CancellationToken ct )
	{
		var result = await GitProcess.RunProcessAsync( workspace, root, null, CommandEnvironment, ct, "rev-parse", "--verify", "--quiet", "HEAD" ).ConfigureAwait( false );
		if ( result.ExitCode == 0 )
			return true;

		// An unborn HEAD fails quietly; anything with a message, such as a missing repository, is a real failure.
		return result.Diagnostic.Trim().Length == 0 ? false : throw BuildError( result );
	}

	/// <summary>Counts an untracked file's lines, and reports it binary, with no lines, when its first bytes hold a NUL as git decides.</summary>
	/// <remarks>
	/// A file on this PC streams through a small buffer and stops at a NUL in its first bytes, whatever its size. A remote host's file is read whole
	/// only up to <see cref="RemoteReadLimit"/>; git on the host counts a larger one, so it never crosses the connection.
	/// </remarks>
	/// <param name="workspace">The host the file is on.</param>
	/// <param name="root">The repository's top-level folder.</param>
	/// <param name="relative">The file's repository-relative path.</param>
	/// <param name="ct">Cancels the count.</param>
	/// <returns>The lines, a last line without a newline counting; zero for a folder, a binary file or a file that vanished.</returns>
	private static async Task<(int Lines, bool Binary)> CountLinesAsync( IWorkspaceHost workspace, string root, string relative, CancellationToken ct )
	{
		var path = Path.Combine( root, relative );
		try
		{
			if ( workspace is LocalWorkspaceHost )
				return await CountLocalLinesAsync( path, ct ).ConfigureAwait( false );

			var stat = await workspace.StatAsync( path, ct ).ConfigureAwait( false );
			if ( !stat.IsFile )
				return (0, false);
			if ( stat.Size > RemoteReadLimit )
				return await CountWithGitAsync( workspace, root, relative, ct ).ConfigureAwait( false );

			var bytes = await workspace.ReadAsync( path, ct ).ConfigureAwait( false );
			if ( bytes.AsSpan( 0, Math.Min( bytes.Length, BinaryProbe ) ).Contains( (byte)0 ) )
				return (0, true);

			var lines = bytes.AsSpan().Count( (byte)'\n' );
			return (bytes.Length == 0 || bytes[^1] == '\n' ? lines : lines + 1, false);
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			// A file being written or locked has no count yet; the next read will see it.
			return (0, false);
		}
	}

	/// <summary>Counts a file on this PC through a small buffer, stopping at a NUL in its first <see cref="BinaryProbe"/> bytes.</summary>
	private static async Task<(int Lines, bool Binary)> CountLocalLinesAsync( string path, CancellationToken ct )
	{
		if ( !File.Exists( path ) )
			return (0, false);

		await using var stream = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, CountBuffer, useAsync: true );
		var buffer = new byte[CountBuffer];
		long total = 0;
		var lines = 0;
		var last = (byte)'\n';
		int read;
		while ( (read = await stream.ReadAsync( buffer, ct ).ConfigureAwait( false )) > 0 )
		{
			var span = buffer.AsSpan( 0, read );
			if ( total < BinaryProbe && span[..(int)Math.Min( read, BinaryProbe - total )].Contains( (byte)0 ) )
				return (0, true);

			lines += span.Count( (byte)'\n' );
			last = span[^1];
			total += read;
		}

		return (last == '\n' ? lines : lines + 1, false);
	}

	/// <summary>Has git count an untracked file where it lives, as an added file's <c>--numstat</c>: its lines, or <c>-</c> for a binary file.</summary>
	private static async Task<(int Lines, bool Binary)> CountWithGitAsync( IWorkspaceHost workspace, string root, string relative, CancellationToken ct )
	{
		// A difference makes --no-index exit with 1, which is its success.
		var result = await GitProcess.RunProcessAsync( workspace, root, null, CommandEnvironment, ct, "diff", "--no-index", "--numstat", "--", "/dev/null", relative ).ConfigureAwait( false );
		if ( result.ExitCode is not (0 or 1) )
			return (0, false);

		var added = result.Output.Split( '\t', 2 )[0];
		return added == "-" ? (0, true) : (int.TryParse( added, NumberStyles.None, CultureInfo.InvariantCulture, out var lines ) ? lines : 0, false);
	}

	/// <summary>Parses <c>git log -z</c> output in <see cref="LogFormat"/> into commits.</summary>
	internal static IReadOnlyList<GitCommit> ParseLog( string output )
	{
		var fields = output.Split( '\0' );
		var commits = new List<GitCommit>( fields.Length / LogFields );
		for ( var start = 0; start + LogFields <= fields.Length; start += LogFields )
		{
			var record = fields.AsSpan( start, LogFields );
			commits.Add( new(
				record[0],
				record[1],
				record[2].Split( ' ', StringSplitOptions.RemoveEmptyEntries ),
				record[3],
				record[4],
				ParseDate( record[5] ),
				record[6],
				record[7],
				ParseDate( record[8] ),
				record[9],
				record[10].TrimEnd() ) );
		}

		return commits;

		static DateTimeOffset ParseDate( string text ) => DateTimeOffset.Parse( text, CultureInfo.InvariantCulture, DateTimeStyles.None );
	}

	/// <summary>Parses <c>git for-each-ref</c> output in <see cref="RefFormat"/> into references, skipping remote <c>HEAD</c> aliases.</summary>
	/// <param name="output">The command's output, one reference per line.</param>
	/// <param name="remotes">The repository's remote names, to split a remote branch's name where the remote ends.</param>
	internal static IReadOnlyList<GitRef> ParseRefs( string output, IReadOnlyList<string> remotes )
	{
		var refs = new List<GitRef>();
		foreach ( var line in output.Split( '\n', StringSplitOptions.RemoveEmptyEntries ) )
		{
			if ( line.Split( '\0' ) is not [var name, var target, var peeled, var head, var upstream, var track] )
				continue;

			GitRefKind kind;
			string shortName;
			string? remote = null;
			if ( name.StartsWith( "refs/heads/", StringComparison.Ordinal ) )
			{
				kind = GitRefKind.LocalBranch;
				shortName = name["refs/heads/".Length..];
			}
			else if ( name.StartsWith( "refs/remotes/", StringComparison.Ordinal ) )
			{
				if ( name.EndsWith( "/HEAD", StringComparison.Ordinal ) )
					continue;

				kind = GitRefKind.RemoteBranch;
				shortName = name["refs/remotes/".Length..];
				remote = FindRemote( shortName, remotes );
			}
			else if ( name.StartsWith( "refs/tags/", StringComparison.Ordinal ) )
			{
				kind = GitRefKind.Tag;
				shortName = name["refs/tags/".Length..];
			}
			else
				continue;

			int ahead = 0, behind = 0;
			var gone = track == "gone";
			if ( !gone )
			{
				foreach ( var part in track.Split( ", ", StringSplitOptions.RemoveEmptyEntries ) )
				{
					if ( part.StartsWith( "ahead ", StringComparison.Ordinal ) )
						ahead = ParseCount( part["ahead ".Length..] );
					else if ( part.StartsWith( "behind ", StringComparison.Ordinal ) )
						behind = ParseCount( part["behind ".Length..] );
				}
			}

			refs.Add( new( name, shortName, kind, peeled.Length > 0 ? peeled : target, remote, upstream.Length > 0 ? upstream : null, ahead, behind, head == "*", gone ) );
		}

		return refs;

		static int ParseCount( string text ) => int.TryParse( text, NumberStyles.None, CultureInfo.InvariantCulture, out var count ) ? count : 0;
	}

	/// <summary>Finds the remote a remote branch's short name starts with, the longest when remote names nest.</summary>
	private static string FindRemote( string shortName, IReadOnlyList<string> remotes )
	{
		string? found = null;
		foreach ( var remote in remotes )
		{
			if ( shortName.Length > remote.Length && shortName[remote.Length] == '/' && shortName.StartsWith( remote, StringComparison.Ordinal )
				&& remote.Length > (found?.Length ?? 0) )
				found = remote;
		}

		// A remote-tracking branch left behind by a removed remote still names its remote first.
		return found ?? shortName[..Math.Max( shortName.IndexOf( '/' ), 0 )];
	}

	/// <summary>Parses <c>git stash list -z</c> output in the <c>%gd %H %gs</c> format into stashes.</summary>
	internal static IReadOnlyList<GitStash> ParseStashes( string output )
	{
		var fields = output.Split( '\0' );
		var stashes = new List<GitStash>();
		for ( var start = 0; start + 3 <= fields.Length; start += 3 )
		{
			var name = fields[start];
			var open = name.IndexOf( '{' );
			var close = name.IndexOf( '}' );
			var index = open >= 0 && close > open && int.TryParse( name.AsSpan( open + 1, close - open - 1 ), NumberStyles.None, CultureInfo.InvariantCulture, out var value )
				? value
				: stashes.Count;
			stashes.Add( new( index, name, fields[start + 2], fields[start + 1] ) );
		}

		return stashes;
	}

	/// <summary>Parses <c>git diff --raw --numstat -z</c> output into files, joining each file's status and line counts by path.</summary>
	internal static IReadOnlyList<GitFileChange> ParseDiff( string output )
	{
		var entries = output.Split( '\0' );
		var files = new List<GitFileChange>();
		var byPath = new Dictionary<string, int>( StringComparer.Ordinal );
		var index = 0;
		while ( index < entries.Length )
		{
			var entry = entries[index];
			if ( entry.Length == 0 )
			{
				index++;
				continue;
			}

			// Raw: ":mode mode sha sha STATUS" then the path, or the old and new paths of a rename or copy.
			if ( entry[0] == ':' )
			{
				var status = entry[(entry.LastIndexOf( ' ' ) + 1)..];
				var letter = status.Length > 0 ? status[0] : 'M';
				string path;
				string? oldPath = null;
				if ( letter is 'R' or 'C' && index + 2 < entries.Length )
				{
					oldPath = entries[index + 1];
					path = entries[index + 2];
					index += 3;
				}
				else if ( index + 1 < entries.Length )
				{
					path = entries[index + 1];
					index += 2;
				}
				else
					break;

				byPath[path] = files.Count;
				files.Add( new( path, oldPath, letter, 0, 0, false ) );
				continue;
			}

			// Numstat: "added\tremoved\tpath", or an empty path followed by the old and new paths of a rename or copy.
			var (counts, next) = ReadNumstat( entries, index );
			index = next;
			if ( counts is { } numstat && byPath.TryGetValue( numstat.Path, out var position ) )
			{
				files[position] = files[position] with
				{
					Added = numstat.Added,
					Removed = numstat.Removed,
					IsBinary = numstat.Binary,
				};
			}
		}

		return files;
	}

	/// <summary>Parses <c>git diff --numstat -z</c> output into line counts by new path.</summary>
	internal static Dictionary<string, (int Added, int Removed, bool Binary)> ParseNumstat( string output )
	{
		var entries = output.Split( '\0' );
		var counts = new Dictionary<string, (int Added, int Removed, bool Binary)>( StringComparer.Ordinal );
		var index = 0;
		while ( index < entries.Length )
		{
			if ( entries[index].Length == 0 )
			{
				index++;
				continue;
			}

			var (entry, next) = ReadNumstat( entries, index );
			index = next;
			if ( entry is { } numstat )
				counts[numstat.Path] = (numstat.Added, numstat.Removed, numstat.Binary);
		}

		return counts;
	}

	/// <summary>Reads the numstat entry at <paramref name="index"/>, with the two path entries that follow a rename's empty path.</summary>
	/// <returns>The entry, or null when it is malformed, and the index after it.</returns>
	private static ((string Path, int Added, int Removed, bool Binary)? Entry, int Next) ReadNumstat( string[] entries, int index )
	{
		if ( entries[index].Split( '\t', 3 ) is not [var plus, var minus, var path] )
			return (null, index + 1);

		var next = index + 1;
		if ( path.Length == 0 )
		{
			if ( index + 2 >= entries.Length )
				return (null, entries.Length);

			path = entries[index + 2];
			next = index + 3;
		}

		var binary = plus == "-";
		var added = int.TryParse( plus, NumberStyles.None, CultureInfo.InvariantCulture, out var count ) ? count : 0;
		var removed = int.TryParse( minus, NumberStyles.None, CultureInfo.InvariantCulture, out count ) ? count : 0;
		return ((path, added, removed, binary), next);
	}

	/// <summary>Parses <c>git status --porcelain=v2 -z</c> output into staged and unstaged changes, with line counts from numstat.</summary>
	/// <param name="output">The status output, without branch headers.</param>
	/// <param name="staged">Line counts of the index against HEAD, by path.</param>
	/// <param name="unstaged">Line counts of the working tree against the index, by path.</param>
	/// <returns>The changes; untracked files have no counts yet.</returns>
	internal static GitWorkingChanges ParseStatus( string output, IReadOnlyDictionary<string, (int Added, int Removed, bool Binary)> staged,
		IReadOnlyDictionary<string, (int Added, int Removed, bool Binary)> unstaged )
	{
		var stagedFiles = new List<GitFileChange>();
		var unstagedFiles = new List<GitFileChange>();
		var untracked = new List<GitFileChange>();
		var entries = output.Split( '\0' );
		for ( var index = 0; index < entries.Length; index++ )
		{
			var entry = entries[index];
			if ( entry.Length < 2 )
				continue;

			switch ( entry[0] )
			{
				// "1 XY sub mH mI mW hH hI path"
				case '1' when entry.Split( ' ', 9 ) is [_, var xy, _, _, _, _, _, _, var path] && xy.Length == 2:
					Add( xy, path, null );
					break;

				// "2 XY sub mH mI mW hH hI Xscore path", then the original path as the next entry.
				case '2' when entry.Split( ' ', 10 ) is [_, var xy, _, _, _, _, _, _, _, var path] && xy.Length == 2:
					var original = index + 1 < entries.Length ? entries[++index] : null;
					Add( xy, path, original );
					break;

				// "u XY sub m1 m2 m3 mW h1 h2 h3 path"
				case 'u' when entry.Split( ' ', 11 ) is [.., var path]:
					var (added, removed, binary) = unstaged.GetValueOrDefault( path );
					unstagedFiles.Add( new( path, null, 'U', added, removed, binary ) );
					break;

				case '?':
					untracked.Add( new( entry[2..], null, '?', 0, 0, false ) );
					break;
			}
		}

		unstagedFiles.AddRange( untracked );
		return new( stagedFiles, unstagedFiles );

		void Add( string xy, string path, string? original )
		{
			if ( xy[0] != '.' )
			{
				var (added, removed, binary) = staged.GetValueOrDefault( path );
				stagedFiles.Add( new( path, xy[0] is 'R' or 'C' ? original : null, xy[0], added, removed, binary ) );
			}
			if ( xy[1] != '.' )
			{
				var (added, removed, binary) = unstaged.GetValueOrDefault( path );
				unstagedFiles.Add( new( path, xy[1] is 'R' or 'C' ? original : null, xy[1], added, removed, binary ) );
			}
		}
	}

	/// <summary>Splits command output into its non-empty lines.</summary>
	private static string[] ParseLines( string output ) => output.Split( ['\n', '\r'], StringSplitOptions.RemoveEmptyEntries );
}
