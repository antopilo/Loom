using Goo;
using Lokn.Core;

namespace Lokn.Modules.Workspaces;

/// <summary>Builds task rows, the saved workspaces of a session, in the sidebar.</summary>
public sealed partial class SidebarRows
{
	/// <summary>Defines how many panel icons a task shows at most.</summary>
	private const int PanelIconLimit = 3;

	/// <summary>Defines the size of a task's panel icons in logical pixels.</summary>
	private const int PanelIconSize = 11;

	/// <summary>Builds tiny icons for the kinds of panel a task shows, such as a terminal or a chat, after its name; types without an icon are skipped.</summary>
	public IEnumerable<Blob> BuildPanelIcons( WorkspaceTask task )
	{
		var types = context.App.Panels.Types;
		return task.PanelTypes
			.Select( id => types.FirstOrDefault( type => type.Id == id ) )
			.Select( type => type?.Glyph )
			.OfType<string>()
			.Distinct()
			.Take( PanelIconLimit )
			.Select( glyph => (Blob)new Text
			{
				Content = glyph,
				FontFamily = IconFont.Family,
				FontSize = PanelIconSize,
				Color = Theme.Current.Muted,
				FlexShrink = 0,
			} );
	}

	/// <summary>Builds the action that starts a new, empty task outside projects.</summary>
	public Button BuildNewTask( WorkspacesModule workspaces ) =>
		BuildIconAction( "New task", AddGlyph, () => workspaces.NewTask( context.App, WorkspacesModule.HomeSession, WorkspacesModule.HomeTitle, context.App.Panels.HideAll ) );

	/// <summary>Orders tasks to show: pinned ones first, each part in its saved order.</summary>
	public static WorkspaceTask[] ComputeShown( IEnumerable<WorkspaceTask> tasks ) => [.. tasks.OrderByDescending( task => task.IsPinned )];

	/// <summary>Adds the Tasks section, the tasks outside projects, when there are any.</summary>
	/// <returns>How many tasks it added.</returns>
	public int AddHomeTasks( WorkspacesModule workspaces, List<SidebarRow> rows )
	{
		var tasks = ComputeShown( workspaces.Tasks( WorkspacesModule.HomeSession ) );
		if ( tasks.Length == 0 )
			return 0;

		rows.Add( new( "tasks-heading", 0, () => BuildSectionHeading( "Tasks" ) ) );
		for ( var index = 0; index < tasks.Length; index++ )
		{
			var task = tasks[index];
			var place = index;
			rows.Add( new( task.Id.ToString(), RowGap, () => BuildTask( workspaces, WorkspacesModule.HomeSession, task, place,
				() => workspaces.OpenTask( context.App, WorkspacesModule.HomeSession, WorkspacesModule.HomeTitle, task.Id, context.App.Panels.HideAll ) ) ) );
		}
		return tasks.Length;
	}

	/// <summary>Builds one task row: its name, highlighted while it is the open task, its pin and … actions, its menu while open, and its name editor while renaming.</summary>
	/// <remarks>Double-click the name, press F2 on it, or choose Rename in its menu to rename it in place; drag it onto another task of its session, pinned or not as it is, to move it there.</remarks>
	/// <param name="workspaces">The module that keeps the task.</param>
	/// <param name="session">The task's session key.</param>
	/// <param name="task">The task.</param>
	/// <param name="index">The task's place among its session's tasks as shown, top first.</param>
	/// <param name="open">Opens the task's session and shows the task.</param>
	public Container BuildTask( WorkspacesModule workspaces, string session, WorkspaceTask task, int index, Action open )
	{
		var isSelected = workspaces.CurrentSession == session && workspaces.CurrentTask == task.Id;
		var isMenuOpen = IsMenuOpen( task.Id );
		var isHovered = IsHovered( task.Id );
		// Pinned tasks lead, so a task moves only among those pinned as it is.
		var order = new RowOrder( $"{session}:{task.IsPinned}", index, dragged => workspaces.MoveTask( session, dragged, task.Id ) );
		Blob main;
		if ( IsRenaming( task.Id ) )
		{
			main = BuildRenameEntry( task.Id, task.Name, "Rename task", name => workspaces.RenameTask( session, task.Id, name ) );
		}
		else
		{
			var button = BuildRowButton( task.Name, open, () => BeginRename( task.Id, task.Name ), isSelected ? $"{task.Name}, current" : null );
			button.DragSource = BuildRowDrag( task.Id, order );
			// A long name stays on one line and ends with an ellipsis rather than growing the row.
			button.Children =
			[
				.. BuildPanelIcons( task ),
				new Text
				{
					Content = task.Name,
					FontSize = Theme.Current.FontSize,
					Color = Theme.Current.Text,
					MinWidth = 0,
					FlexShrink = 1,
					TextWrap = TextWrap.NoWrap,
					TextMaxLines = 1,
					TextTrimming = TextTrimming.Ellipsis,
				},
			];
			main = button;
		}

		// A pinned task always shows its pin, which turns into unpin under the pointer; others offer pinning on hover.
		var pin = BuildIconButton( task.IsPinned && isHovered ? UnpinGlyph : PinGlyph,
			task.IsPinned ? $"Unpin {task.Name}" : $"Pin {task.Name}",
			() => workspaces.SetPinned( session, task.Id, !task.IsPinned ),
			task.IsPinned || isHovered || isMenuOpen );
		var children = new List<Blob>
		{
			BuildHoverRow( task.Id, isSelected,
			[
				main,
				pin,
				BuildMoreButton( task.Id, $"{task.Name} options", isHovered || isMenuOpen ),
			], order ),
		};
		if ( isMenuOpen )
		{
			children.Add( BuildMenu( task.Id, $"{task.Name} options",
			[
				new MenuAction( task.IsPinned ? "Unpin" : "Pin", () => context.Run( () => workspaces.SetPinned( session, task.Id, !task.IsPinned ) ) ) { AccessKey = Key.P },
				new MenuAction( "Rename", () => context.Run( () => BeginRename( task.Id, task.Name ) ) ) { AccessKey = Key.R },
				new MenuSeparator(),
				// A session keeps its last task, so that one cannot be deleted.
				new MenuAction( "Delete task", () => context.Run( () => workspaces.DeleteTask( context.App, session, task.Id ) ) )
				{
					AccessKey = Key.D,
					IsDestructive = true,
					IsEnabled = workspaces.Tasks( session ).Count > 1,
				},
			] ) );
		}

		return new Container
		{
			Key = task.Id.ToString(),
			Gap = 2,
			Children = children,
		};
	}
}
