using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Builds the pane actions menu opened from a pane's grip.</summary>
internal sealed partial class ShellView
{
	/// <summary>Anchors the pane menu to the grip that opened it.</summary>
	private readonly ElementHandle _menuAnchor = new();

	/// <summary>Stores the pane whose actions menu is open.</summary>
	private Guid? _menuPaneId;

	/// <summary>Focuses a pane and opens its actions menu below its grip.</summary>
	private void UpdatePaneMenu( Guid paneId )
	{
		_app.Panels.ActivatePane( paneId );
		_menuPaneId = paneId;
		Rebuild();
	}

	/// <summary>Closes the pane actions menu.</summary>
	private void ReleasePaneMenu()
	{
		_menuPaneId = null;
		Rebuild();
	}

	/// <summary>Builds the open pane menu, or nothing when no menu is open or its pane has closed.</summary>
	private Blob? BuildPaneMenu()
	{
		if ( _menuPaneId is not { } paneId || _app.Panels.Panes.FirstOrDefault( pane => pane.Id == paneId ) is not { } pane )
			return null;

		var panel = pane.PanelId is { } panelId ? _app.Panels.Panels.FirstOrDefault( item => item.Id == panelId ) : null;
		return Mount<ContextMenuInput, ContextMenu>( "pane-menu", new( _menuAnchor, $"{(panel is null ? "Empty pane" : ComputePanelLabel( panel ))} options",
		[
			new MenuAction( "Open a panel…", () => RunPaneAction( pane, null, () => UpdatePalette( "Open ", ActionsType ) ) ) { AccessKey = Key.O },
			BuildPaneMenuAction( pane, "Split right", "shell.split-right" ),
			BuildPaneMenuAction( pane, "Split down", "shell.split-down" ),
			new MenuSeparator(),
			BuildPaneMenuAction( pane, panel is null ? "Close pane" : "Close panel", "shell.close" ),
			new MenuSeparator(),
			new MenuNote( panel is null ? "Open a panel to move it" : "Drag the grip to dock or swap" ),
		], ReleasePaneMenu )
		{
			Placement = PortalPlacement.Bottom,
			Width = 240,
		} );
	}

	/// <summary>Builds one pane action that runs a workspace command on the pane, with the command's shortcut on the right.</summary>
	private MenuAction BuildPaneMenuAction( DockPane pane, string label, string commandId )
	{
		// Commands act on the active pane, which opening the menu made this one.
		var shortcut = _app.Commands.Items.FirstOrDefault( command => command.Id == commandId )?.Shortcut;
		return new MenuAction( label, () => RunPaneAction( pane, commandId, null ) )
		{
			Shortcut = ComputeShortcutLabel( shortcut ),
			IsEnabled = _app.Commands.CanExecute( commandId ),
		};
	}

	/// <summary>Runs a workspace command or a custom action on the pane the menu was opened for.</summary>
	private void RunPaneAction( DockPane pane, string? commandId, Action? action )
	{
		_app.Panels.ActivatePane( pane.Id );
		if ( commandId is not null )
			_ = _app.Commands.ExecuteAsync( commandId );
		action?.Invoke();
	}
}
