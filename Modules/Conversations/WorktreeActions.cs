using Lokn.Core;

namespace Lokn.Modules.Conversations;

/// <summary>Describes one working tree of a project's repository, as the chat sidebar lists it.</summary>
/// <param name="Path">The working tree's absolute directory.</param>
/// <param name="Branch">The checked-out branch's short name, or null when detached.</param>
/// <param name="IsMain">Whether this is the repository's main checkout, which cannot be removed.</param>
public sealed record WorktreeEntry( string Path, string? Branch, bool IsMain );

/// <summary>The git worktree operations the chat sidebar offers on a project; the Worktrees module contributes them to <see cref="Point"/>.</summary>
/// <remarks>
/// While a contribution is loaded, a project whose repository has a linked worktree lists every worktree under its heading, and the
/// project and worktree menus offer creating, removing and managing worktrees. Without one, the sidebar groups chats only by the folders
/// they work in and offers none of these. Every operation but <see cref="OpenManager"/> may run off the UI thread.
/// </remarks>
/// <param name="ListAsync">Lists the working trees of the repository holding a folder, main checkout first; throws when the folder is not in a repository.</param>
/// <param name="CreateAsync">Creates the worktree of a branch (the repository folder, the branch, and where a new branch starts or empty) beside the main checkout with the repository's saved setup, and returns its directory; throws with git's message when git refuses.</param>
/// <param name="RemoveAsync">Removes a linked worktree (the repository folder, then the worktree), keeping its branch; throws with a message, and changes nothing, when it has uncommitted changes, is the main checkout, or git refuses.</param>
/// <param name="OpenManager">Opens the worktree manager on the repository holding a folder; call on the UI thread.</param>
/// <param name="ReadVersion">Reads a number that changes whenever the contributing module creates or removes a worktree; reading it in a view build rebuilds that view when it changes.</param>
public sealed record WorktreeActions(
	Func<string, CancellationToken, Task<IReadOnlyList<WorktreeEntry>>> ListAsync,
	Func<string, string, string, CancellationToken, Task<string>> CreateAsync,
	Func<string, string, CancellationToken, Task> RemoveAsync,
	Action<LoknApplication, string> OpenManager,
	Func<int> ReadVersion )
{
	/// <summary>The extension point the chat sidebar reads worktree operations from; the Conversations module declares it.</summary>
	public const string Point = "conversations.worktrees";
}
