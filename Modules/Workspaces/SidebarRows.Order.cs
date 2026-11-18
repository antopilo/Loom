using Goo;
using Lokn.Core;

namespace Lokn.Modules.Workspaces;

/// <summary>Describes a row's place among rows the user reorders by dragging, such as a project's tasks.</summary>
/// <param name="Group">The rows it moves among, such as a project's unpinned tasks; rows of other groups do not drop on it.</param>
/// <param name="Index">Its place among the group's rows as shown, top first.</param>
/// <param name="MoveHere">Moves a dropped row of the group to this row's place, the rows between shifting toward where it was.</param>
public sealed record RowOrder( string Group, int Index, Action<Guid> MoveHere );

/// <summary>Lets the user drag rows to another place among their group's rows, with a line showing where a dragged row lands.</summary>
public sealed partial class SidebarRows
{
	/// <summary>Defines the drop line's thickness in logical pixels.</summary>
	private const int DropLineThickness = 2;

	/// <summary>Tracks the row a dragged row is over and whether it lands below it, or an empty row when none is.</summary>
	private readonly State<(Guid Row, bool IsBelow)> _dropAt = new( (Guid.Empty, false) );

	/// <summary>Builds the drag that starts on a row's main button, carrying the row to another place in its group.</summary>
	/// <param name="id">The row.</param>
	/// <param name="order">The row's place among its group's rows.</param>
	public DragSource BuildRowDrag( Guid id, RowOrder order ) =>
		new( _ => new DragData( new RowDrag( this, order.Group, id, order.Index ), DragEffect.Move ), _ => context.Run( ClearDrop ) );

	/// <summary>Builds what a row does with a row dragged over it: a row of its group shows the drop line and takes this row's place when dropped.</summary>
	private DropTarget BuildRowDrop( Guid id, RowOrder order ) => new(
		drop => drop.Data.Value is RowDrag dragged && dragged.Owner == this && dragged.Group == order.Group && dragged.Id != id ? DragEffect.Move : DragEffect.None,
		drop => context.Run( () => UpdateDrop( drop, id, order ) ) );

	/// <summary>Builds the accent line in the gap above or below a row, where a row dropped on it lands.</summary>
	/// <remarks>Only the edge the line hangs from is set: Goo rejects <see cref="Length.Auto"/> as a position offset, and an unset one stays free.</remarks>
	private static Container BuildDropLine( bool isBelow )
	{
		var line = new Container
		{
			Key = "drop-line",
			Position = PositionType.Absolute,
			Left = 0,
			Right = 0,
			Height = DropLineThickness,
			BorderRadius = DropLineThickness / 2.0,
			BackgroundColor = Theme.Current.Accent,
		};
		if ( isBelow )
			line.Bottom = -DropLineThickness;
		else
			line.Top = -DropLineThickness;
		return line;
	}

	/// <summary>Follows a row of the group over this one: the line shows on the side the dragged row lands, which is below when it comes from above; a drop moves it here.</summary>
	private void UpdateDrop( DragEvent drop, Guid id, RowOrder order )
	{
		if ( drop.Data.Value is not RowDrag dragged )
			return;

		switch ( drop.Kind )
		{
			case DragEventKind.Enter or DragEventKind.Move:
				var at = (id, dragged.Index < order.Index);
				if ( _dropAt.Value != at )
					_dropAt.Value = at;
				break;
			case DragEventKind.Leave:
				if ( _dropAt.Value.Row == id )
					ClearDrop();
				break;
			case DragEventKind.Drop:
				ClearDrop();
				order.MoveHere( dragged.Id );
				break;
		}
	}

	/// <summary>Hides the drop line.</summary>
	private void ClearDrop()
	{
		if ( _dropAt.Value.Row != Guid.Empty )
			_dropAt.Value = (Guid.Empty, false);
	}

	/// <summary>Identifies a row being dragged among its group's rows.</summary>
	/// <param name="Owner">The sidebar view that started the drag; another view's rows do not take it.</param>
	/// <param name="Group">The rows it moves among.</param>
	/// <param name="Id">The row.</param>
	/// <param name="Index">Its place among the group's rows when the drag started.</param>
	private sealed record RowDrag( SidebarRows Owner, string Group, Guid Id, int Index );
}
