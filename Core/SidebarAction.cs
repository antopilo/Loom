namespace Lokn.Core;

/// <summary>An action row the sidebar shows after its own actions, such as New project, drawn like them: an icon and a title.</summary>
/// <remarks>Contribute it with <c>builder.Contribute( SidebarAction.Point, new SidebarAction( … ) )</c>; the sidebar lists contributions in load order.</remarks>
/// <param name="Title">The row's text and accessible name.</param>
/// <param name="Glyph">The icon, a Segoe Fluent Icons code point.</param>
/// <param name="Run">Runs when the row is clicked.</param>
public sealed record SidebarAction( string Title, string Glyph, Action<LoknApplication> Run )
{
	/// <summary>Defines the extension point the sidebar reads; the Workspaces module declares it.</summary>
	public const string Point = "workspaces.sidebar-actions";

	/// <summary>Gets whether the row shows as active, such as while the panel it opens is shown; null never does.</summary>
	public Func<LoknApplication, bool>? IsActive
	{
		get; init;
	}
}
