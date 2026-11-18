namespace Lokn.Core;

/// <summary>Names how far a setup item is from ready.</summary>
public enum SetupState
{
	/// <summary>The item works.</summary>
	Ready,

	/// <summary>The item is missing or broken; a fix can make it ready.</summary>
	Missing,

	/// <summary>The item cannot be ready until another item is; its fix waits.</summary>
	Blocked,
}

/// <summary>Reports what a setup check found.</summary>
/// <param name="State">How far the item is from ready.</param>
/// <param name="Detail">One short line for the user, such as "2.47.1 · C:\Program Files\Git\cmd\git.exe" or "Not installed".</param>
public sealed record SetupStatus( SetupState State, string Detail );

/// <summary>One thing Loom needs from the machine, such as Git or a signed-in Omp account; contribute it to <see cref="Point"/>.</summary>
/// <remarks>
/// The Get started panel lists every item in <see cref="Order"/>, runs each <see cref="CheckAsync"/> off the UI thread, and offers
/// <see cref="FixLabel"/> for an item that is not ready. A fix installs or signs in without asking for paths; it reports progress lines
/// and completes when the item should be ready, after which every item is checked again, because one fix can satisfy another.
/// </remarks>
public sealed class SetupItem
{
	/// <summary>The extension point the Setup module reads items from.</summary>
	public const string Point = "setup.items";

	/// <summary>Gets the stable key, such as <c>git</c>.</summary>
	public required string Key
	{
		get; init;
	}

	/// <summary>Gets the name the user sees, such as "Git".</summary>
	public required string Title
	{
		get; init;
	}

	/// <summary>Gets one line on what the item is for, such as "Shows changes and creates worktrees".</summary>
	public required string Purpose
	{
		get; init;
	}

	/// <summary>Gets whether chats cannot work without it; Loom opens Get started at startup while a required item is not ready.</summary>
	public bool IsRequired
	{
		get; init;
	}

	/// <summary>Gets the position in the list; lower comes first, so an item comes after what its fix needs.</summary>
	public int Order
	{
		get; init;
	}

	/// <summary>Gets the check, run off the UI thread.</summary>
	public required Func<CancellationToken, Task<SetupStatus>> CheckAsync
	{
		get; init;
	}

	/// <summary>Gets the fix button's label, such as "Install" or "Sign in", or null when there is no fix.</summary>
	public string? FixLabel
	{
		get; init;
	}

	/// <summary>Gets the fix, run off the UI thread; it reports progress lines and throws with a readable message when it fails.</summary>
	public Func<IProgress<string>, CancellationToken, Task>? FixAsync
	{
		get; init;
	}
}
