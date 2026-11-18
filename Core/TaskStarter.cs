namespace Lokn.Core;

/// <summary>Something a new task can start with, such as a chat or a terminal: an icon in the grid that opens from a project's + in the sidebar.</summary>
/// <remarks>
/// Contribute it with <c>builder.Contribute( TaskStarter.Point, new TaskStarter( … ) )</c>. The grid shows the loaded modules' starters by
/// <see cref="Order"/>, then in load order, so it changes as modules load and unload. Choosing one creates the task, named after <see cref="Title"/>,
/// opens its empty workspace, then runs <see cref="Open"/>.
/// </remarks>
/// <param name="Title">The icon's name, read by screen readers and shown under the grid while the pointer is on it.</param>
/// <param name="Glyph">The icon, a Segoe Fluent Icons code point.</param>
/// <param name="Order">The icon's place in the grid, lowest first.</param>
/// <param name="Open">Fills the new task's empty workspace, such as by opening a panel in its one pane.</param>
public sealed record TaskStarter( string Title, string Glyph, int Order, Action<LoknApplication> Open )
{
	/// <summary>Defines the extension point the sidebar reads; the Workspaces module declares it.</summary>
	public const string Point = "workspaces.task-starters";
}
