namespace Lokn.Modules.Git;

/// <summary>Describes one commit as <see cref="GitRepository.ReadLogAsync"/> reads it.</summary>
/// <param name="Id">The full commit identifier.</param>
/// <param name="ShortId">The abbreviated identifier git chose for the repository.</param>
/// <param name="Parents">The parent identifiers, the first parent first; empty for a root commit.</param>
/// <param name="AuthorName">The author's name.</param>
/// <param name="AuthorEmail">The author's email address.</param>
/// <param name="AuthorDate">When the change was authored, in the author's time zone.</param>
/// <param name="CommitterName">The committer's name.</param>
/// <param name="CommitterEmail">The committer's email address.</param>
/// <param name="CommitDate">When the commit was made, in the committer's time zone.</param>
/// <param name="Subject">The message's first paragraph on one line.</param>
/// <param name="Body">The rest of the message without trailing whitespace; empty when there is none.</param>
public sealed record GitCommit( string Id, string ShortId, IReadOnlyList<string> Parents,
	string AuthorName, string AuthorEmail, DateTimeOffset AuthorDate,
	string CommitterName, string CommitterEmail, DateTimeOffset CommitDate,
	string Subject, string Body );

/// <summary>Names the kinds of reference <see cref="GitRepository.ReadRefsAsync"/> lists.</summary>
public enum GitRefKind
{
	/// <summary>A branch under <c>refs/heads</c>.</summary>
	LocalBranch,

	/// <summary>A remote-tracking branch under <c>refs/remotes</c>.</summary>
	RemoteBranch,

	/// <summary>A tag under <c>refs/tags</c>, lightweight or annotated.</summary>
	Tag,
}

/// <summary>Describes a branch or tag.</summary>
/// <param name="Name">The full reference, such as <c>refs/heads/main</c>.</param>
/// <param name="ShortName">The display name, such as <c>main</c>, <c>origin/main</c> or <c>v1.0</c>.</param>
/// <param name="Kind">Whether the reference is a local branch, a remote branch or a tag.</param>
/// <param name="Target">The commit the reference points at; an annotated tag's commit, not the tag object.</param>
/// <param name="Remote">The remote of a remote branch, such as <c>origin</c>; null for other kinds.</param>
/// <param name="Upstream">A local branch's upstream short name, such as <c>origin/main</c>; null when it has none.</param>
/// <param name="Ahead">Commits on a local branch that its upstream lacks.</param>
/// <param name="Behind">Commits on the upstream that a local branch lacks.</param>
/// <param name="IsCurrent">Whether this is the checked-out branch.</param>
/// <param name="IsUpstreamGone">Whether a local branch's configured upstream no longer exists.</param>
public sealed record GitRef( string Name, string ShortName, GitRefKind Kind, string Target, string? Remote,
	string? Upstream, int Ahead, int Behind, bool IsCurrent, bool IsUpstreamGone );

/// <summary>Describes one stash entry.</summary>
/// <param name="Index">The position in the stash list, zero being the newest.</param>
/// <param name="Name">The stash's name, such as <c>stash@{0}</c>.</param>
/// <param name="Message">The stash's message, such as <c>On main: work in progress</c>.</param>
/// <param name="Commit">The stash commit's identifier.</param>
public sealed record GitStash( int Index, string Name, string Message, string Commit );

/// <summary>Describes one changed file in a commit or in the working tree.</summary>
/// <param name="Path">The repository-relative path with forward slashes; the new path of a rename or copy.</param>
/// <param name="OldPath">The original path of a rename or copy; null otherwise.</param>
/// <param name="Status">
/// The change: <c>'M'</c> modified, <c>'A'</c> added, <c>'D'</c> deleted, <c>'R'</c> renamed, <c>'C'</c> copied,
/// <c>'T'</c> type change, <c>'U'</c> conflicted, <c>'?'</c> untracked.
/// </param>
/// <param name="Added">Lines added; zero for a binary file.</param>
/// <param name="Removed">Lines removed; zero for a binary file.</param>
/// <param name="IsBinary">Whether git treats the file as binary, so it has no line counts.</param>
public sealed record GitFileChange( string Path, string? OldPath, char Status, int Added, int Removed, bool IsBinary );

/// <summary>Describes the uncommitted changes of a working tree.</summary>
/// <param name="Staged">Changes in the index against HEAD.</param>
/// <param name="Unstaged">Changes in the working tree against the index, conflicted (<c>'U'</c>) and untracked (<c>'?'</c>) files included.</param>
public sealed record GitWorkingChanges( IReadOnlyList<GitFileChange> Staged, IReadOnlyList<GitFileChange> Unstaged );

/// <summary>Names the multi-step operation a repository is in the middle of.</summary>
public enum GitOperation
{
	/// <summary>No operation is in progress.</summary>
	None,

	/// <summary>A merge stopped for conflicts or a commit.</summary>
	Merge,

	/// <summary>A rebase stopped part way.</summary>
	Rebase,

	/// <summary>A cherry-pick stopped for conflicts.</summary>
	CherryPick,

	/// <summary>A revert stopped for conflicts.</summary>
	Revert,
}

/// <summary>Names how <see cref="GitRepository.ResetAsync"/> moves the current branch.</summary>
public enum GitResetMode
{
	/// <summary>Moves the branch only; the index and working tree keep their changes.</summary>
	Soft,

	/// <summary>Moves the branch and resets the index; the working tree keeps its changes.</summary>
	Mixed,

	/// <summary>Moves the branch and resets the index and working tree, discarding changes.</summary>
	Hard,
}

/// <summary>Describes one line segment of a graph row, in lane units.</summary>
/// <remarks>
/// Up segments run from the row's top edge at lane <paramref name="From"/> to its middle at lane <paramref name="To"/>;
/// Down segments run from the middle at lane <paramref name="From"/> to the bottom edge at lane <paramref name="To"/>.
/// </remarks>
/// <param name="From">The lane the segment starts in.</param>
/// <param name="To">The lane the segment ends in.</param>
/// <param name="Color">The lane color index, stable along a branch line.</param>
public readonly record struct GitGraphLine( int From, int To, int Color );

/// <summary>Describes how one commit's row of the graph is drawn.</summary>
/// <param name="Lane">The lane of the commit's dot.</param>
/// <param name="Color">The color index of the commit's dot.</param>
/// <param name="Width">How many lanes the row spans, for the graph column's width.</param>
/// <param name="Up">The segments in the row's upper half.</param>
/// <param name="Down">The segments in the row's lower half.</param>
public sealed record GitGraphRow( int Lane, int Color, int Width, IReadOnlyList<GitGraphLine> Up, IReadOnlyList<GitGraphLine> Down );
