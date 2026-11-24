namespace Lokn.Core;

/// <summary>An action the sidebar shows beside its own, such as New project: an icon button in the chat sidebar's header, or a row with an icon and a title
/// in the task sidebar without Conversations.</summary>
/// <remarks>Contribute it with <c>builder.Contribute( SidebarAction.Point, new SidebarAction( … ) )</c>; the sidebar lists contributions in load order.</remarks>
/// <param name="Title">The row's text, or the button's accessible name.</param>
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
