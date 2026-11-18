using System.Text.Json;

namespace Lokn.Core;

/// <summary>Names how a file differs from the version a status source compares against, such as git's HEAD.</summary>
public enum FileState
{
	/// <summary>The file is new and tracked.</summary>
	Added,

	/// <summary>The file changed.</summary>
	Modified,

	/// <summary>The file was removed; the tree does not list it, but its folder shows a change.</summary>
	Deleted,

	/// <summary>The file was renamed or moved here.</summary>
	Renamed,

	/// <summary>The file is new and not tracked yet.</summary>
	Untracked,

	/// <summary>The file has unresolved conflicts.</summary>
	Conflicted,

	/// <summary>The file is ignored; the tree dims it.</summary>
	Ignored,
}

/// <summary>Computes the state of the files under a folder, such as git status; contribute it to <see cref="Point"/>.</summary>
/// <remarks>
/// The file tree asks every source when its folder opens and after files change under it, and colors files by the first state any source
/// reports. A source does its work off the UI thread and returns full paths; return an empty map for a folder it does not cover.
/// </remarks>
/// <param name="Name">Names the source, such as <c>git</c>.</param>
/// <param name="Compute">Computes the states under the folder at the full path, by full file path.</param>
public sealed record FileStatusSource( string Name, Func<string, CancellationToken, Task<IReadOnlyDictionary<string, FileState>>> Compute )
{
	/// <summary>The extension point the Files module reads status sources from.</summary>
	public const string Point = "files.status";
}

/// <summary>What a header button sees of the file tree it acts on.</summary>
public interface IFileTreeView
{
	/// <summary>Gets the folder the tree shows.</summary>
	string Root
	{
		get;
	}

	/// <summary>Gets the state the status sources reported for a file, or null when it has none.</summary>
	FileState? GetState( string path );

	/// <summary>Gets the key of the filter the tree applies, or null when it shows every file.</summary>
	string? FilterKey
	{
		get;
	}

	/// <summary>Shows only the files the filter keeps, and the folders that hold them; pass nulls to show every file again.</summary>
	/// <param name="key">Identifies the filter, so the button that set it can show it is on and turn it off.</param>
	/// <param name="keep">Reports whether a file stays in the tree.</param>
	void SetFilter( string? key, Func<string, bool>? keep );
}

/// <summary>A button a module adds to the file tree's header, such as a filter that shows only changed files; contribute it to <see cref="Point"/>.</summary>
/// <param name="Key">A stable identifier, unique among the buttons.</param>
/// <param name="Glyph">A Segoe Fluent Icons character drawn on the button.</param>
/// <param name="Name">What the button does, read by screen readers and shown on hover.</param>
/// <param name="Run">Acts on the tree, on the UI thread.</param>
public sealed record FileTreeButton( string Key, string Glyph, string Name, Action<IFileTreeView> Run )
{
	/// <summary>The extension point the Files module reads header buttons from.</summary>
	public const string Point = "files.buttons";

	/// <summary>Gets whether the button shows as switched on, such as while its filter applies; null draws it as a plain button.</summary>
	public Func<IFileTreeView, bool>? IsOn { get; init; }
}

/// <summary>What a side-view mode receives when the Files panel creates its content.</summary>
/// <param name="Panel">The Files panel's context: its identifier, lifetime and dispatch. The mode's content lives as long as the panel.</param>
/// <param name="Tree">The panel's tree; its <see cref="IFileTreeView.Root"/> is the folder the side view shows, which changes with the open project.</param>
/// <param name="RestoredState">What the mode's content captured when the panel last closed, or before a module reload replaced it; null for a new one.</param>
public sealed record FileTreeModeContext( PanelContext Panel, IFileTreeView Tree, JsonElement? RestoredState );

/// <summary>A mode of the Files side view: a button in its header that swaps the file tree for other content, such as a repository's changes; contribute it to <see cref="Point"/>.</summary>
/// <remarks>
/// The panel creates a mode's content the first time the user picks it and keeps it while other modes show, so its state survives switching;
/// it builds the content inside the panel under its header, captures its state with the panel's, and closes it with the panel. The built-in
/// Files mode (<see cref="FilesKey"/>) always comes first; contributed modes follow by <see cref="Order"/>.
/// </remarks>
/// <param name="Key">A stable identifier, unique among the modes; the panel saves the selected one by it.</param>
/// <param name="Glyph">A Segoe Fluent Icons character drawn on the mode's button.</param>
/// <param name="Name">The mode's name, read by screen readers and shown on hover.</param>
/// <param name="Order">Where the button sits after the Files mode, lowest first.</param>
/// <param name="Create">Creates the mode's content on the UI thread.</param>
public sealed record FileTreeMode( string Key, string Glyph, string Name, int Order, Func<FileTreeModeContext, PanelContent> Create )
{
	/// <summary>The extension point the Files module reads side-view modes from.</summary>
	public const string Point = "files.modes";

	/// <summary>The key of the built-in mode that shows the file tree.</summary>
	public const string FilesKey = "files";

	/// <summary>Gets whether the tree's header buttons (<see cref="FileTreeButton"/>) show while this mode is selected; they always show in the Files mode.</summary>
	public bool ShowsButtons { get; init; }
}

/// <summary>Opens the Files side view in a mode from another module, such as Git showing its Changes beside a newly opened Git panel; the Files module publishes it as a service.</summary>
/// <remarks>Resolve it with <c>app.Services.Get&lt;IFileSideView&gt;()</c>; without the Files module it is null, and nothing opens.</remarks>
public interface IFileSideView
{
	/// <summary>Shows the side view at the workspace's left edge in a mode, opening it when it is hidden, and keeps the focus where it was.</summary>
	/// <param name="app">The application.</param>
	/// <param name="modeKey">The mode to select, such as <c>git.changes</c>; <see cref="FileTreeMode.FilesKey"/> shows the tree.</param>
	void Show( LoknApplication app, string modeKey );
}
