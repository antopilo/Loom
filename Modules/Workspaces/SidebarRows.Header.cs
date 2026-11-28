using Goo;
using Lokn.Core;

namespace Lokn.Modules.Workspaces;

/// <summary>Builds the sidebar's header: small icon buttons beside the host, the wide New chat button and the square Search button.</summary>
public sealed partial class SidebarRows
{
	/// <summary>Builds an icon button of the header, always shown, such as New project; an active one is highlighted.</summary>
	/// <param name="glyph">The icon, a Segoe Fluent Icons code point.</param>
	/// <param name="accessibilityName">What the button does, such as "New project".</param>
	/// <param name="action">Runs on a click.</param>
	/// <param name="isActive">Whether the button shows as active, such as while the panel it opens is shown.</param>
	public Button BuildHeaderButton( string glyph, string accessibilityName, Action action, bool isActive = false )
	{
		var button = BuildIconButton( glyph, accessibilityName, action );
		button.Key = $"header:{accessibilityName}";
		if ( isActive )
			button.BackgroundColor = Theme.Current.Hover;
		return button;
	}

	/// <summary>Adds the actions other modules contribute to <see cref="SidebarAction.Point"/>, such as Notifications, as header icon buttons; one with
	/// <see cref="SidebarAction.OpenAt"/> drags onto a pane to open there.</summary>
	public void AddContributedHeaderButtons( List<Blob> children )
	{
		foreach ( var item in context.App.Extensions.Get<SidebarAction>( SidebarAction.Point ) )
		{
			var button = BuildHeaderButton( item.Glyph, item.Title, () => item.Run( context.App ), item.IsActive?.Invoke( context.App ) == true );
			if ( item.OpenAt is { } openAt )
				button.DragSource = BuildPanelDrag( () => true, ( pane, placement ) => openAt( context.App, pane, placement ) );
			children.Add( button );
		}
	}

	/// <summary>Builds the header's wide button, such as New chat: an icon and a title on a raised, bordered field that fills the row.</summary>
	public Button BuildPrimaryAction( string title, string glyph, Action action )
	{
		var theme = Theme.Current;
		var button = BuildIconAction( title, glyph, action );
		button.Key = $"primary:{title}";
		button.FlexGrow = 1;
		button.MinWidth = 0;
		button.BackgroundColor = theme.Raised;
		button.BorderColor = theme.Border;
		button.BorderWidth = theme.BorderWidth;
		button.HoverStyle = new Style { BackgroundColor = theme.Hover };
		return button;
	}

	/// <summary>Builds the header's square Search button, bordered like <see cref="BuildPrimaryAction"/>, that opens the Search palette.</summary>
	public Button BuildSearchButton()
	{
		var theme = Theme.Current;
		var button = BuildIconButton( SearchGlyph, "Search", context.App.Search.Open );
		button.Key = "search";
		button.BorderColor = theme.Border;
		button.BorderWidth = theme.BorderWidth;
		return button;
	}
}
