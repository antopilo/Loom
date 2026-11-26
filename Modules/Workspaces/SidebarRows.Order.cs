using Goo;
using Lokn.Core;

namespace Lokn.Modules.Workspaces;

/// <summary>Describes a row's place among rows the user reorders by dragging, such as a project's tasks.</summary>
/// <param name="Group">The rows it moves among, such as a project's unpinned tasks; rows of other groups do not drop on it.</param>
/// <param name="Index">Its place among the group's rows as shown, top first.</param>
/// <param name="MoveHere">Moves a dropped row of the group to this row's place, the rows between shifting toward where it was.</param>
public sealed record RowOrder( string Group, int Index, Action<Guid> MoveHere );

/// <summary>Lets the user drag rows to another place among their group's rows: the dragged row follows the pointer up and down, and the rows it
/// passes slide aside to open the place it lands in.</summary>
public sealed partial class SidebarRows
{
	/// <summary>Defines how long a row takes to slide aside for the dragged row, in milliseconds.</summary>
	private const double MakeRoomMs = 160;

	/// <summary>Defines how much the dragged row grows while it is held.</summary>
	private const double LiftScale = 1.02;

	/// <summary>Holds the drag in progress, or null while no row is dragged.</summary>
	private readonly State<RowDragState?> _drag = new( null );

	/// <summary>Holds the place of each reorderable row from the latest build, by row.</summary>
	private readonly Dictionary<Guid, RowOrder> _orders = [];

	/// <summary>Builds the drag that starts on a row's main button, carrying the row to another place in its group.</summary>
	/// <param name="id">The row.</param>
	/// <param name="order">The row's place among its group's rows.</param>
	public DragSource BuildRowDrag( Guid id, RowOrder order ) => new( start =>
	{
		context.Run( () => _drag.Value = StartDrag( id, order, start.WindowPosition.Y ) );
		return new DragData( new RowDrag( this, order.Group, id, order.Index ), DragEffect.Move );
	}, _ => context.Run( () => _drag.Value = null ) );

	/// <summary>Builds what a row does with a row of its group dragged over it, the dragged row itself included, since it stays under the pointer:
	/// it follows the pointer, and a drop moves the dragged row to the place it shows.</summary>
	private DropTarget BuildRowDrop( RowOrder order ) => new(
		drop => drop.Data.Value is RowDrag dragged && dragged.Owner == this && dragged.Group == order.Group ? DragEffect.Move : DragEffect.None,
		drop => context.Run( () => UpdateDrop( drop ) ) );

	/// <summary>Records where every row of the dragged row's group sits, so the place it lands can be found from the pointer alone.</summary>
	private RowDragState? StartDrag( Guid id, RowOrder order, double pointerY )
	{
		var rows = new List<(Guid Id, int Index, double Center)>();
		var height = 0.0;
		foreach ( var (rowId, rowOrder) in _orders )
		{
			if ( rowOrder.Group != order.Group || !_rows.TryGetValue( rowId, out var handle ) || !handle.IsMounted )
				continue;
			var box = handle.BorderBox;
			rows.Add( (rowId, rowOrder.Index, box.Y + box.Height / 2) );
			if ( rowId == id )
				height = box.Height;
		}
		if ( height <= 0 )
			return null;
		rows.Sort( ( a, b ) => a.Index.CompareTo( b.Index ) );
		return new( id, order.Group, order.Index, pointerY, pointerY, height + RowGap, order.Index, rows );
	}

	/// <summary>Follows the pointer: moves the dragged row and finds the place it lands, the last row whose middle it passed; a drop moves it there.</summary>
	private void UpdateDrop( DragEvent drop )
	{
		if ( drop.Data.Value is not RowDrag dragged || _drag.Value is not { } drag || drag.Id != dragged.Id )
			return;

		switch ( drop.Kind )
		{
			case DragEventKind.Enter or DragEventKind.Move:
			{
				var pointerY = drop.WindowPosition.Y;
				var target = ComputeTarget( drag, pointerY );
				if ( pointerY != drag.PointerY || target != drag.Target )
					_drag.Value = drag with { PointerY = pointerY, Target = target };
				break;
			}
			case DragEventKind.Drop:
			{
				_drag.Value = null;
				if ( drag.Target != drag.Index && drag.Rows.FirstOrDefault( row => row.Index == drag.Target ).Id is var targetId && _orders.TryGetValue( targetId, out var targetOrder ) )
					targetOrder.MoveHere( drag.Id );
				break;
			}
		}
	}

	/// <summary>Finds the place the dragged row lands: below every row whose middle its own middle passed going down, or above every one it passed going up.</summary>
	private static int ComputeTarget( RowDragState drag, double pointerY )
	{
		var self = drag.Rows.FirstOrDefault( row => row.Id == drag.Id );
		var center = self.Center + ClampOffset( drag, pointerY - drag.StartY );
		var target = drag.Index;
		foreach ( var row in drag.Rows )
		{
			if ( row.Index > drag.Index && center > row.Center )
				target = Math.Max( target, row.Index );
			else if ( row.Index < drag.Index && center < row.Center )
				target = Math.Min( target, row.Index );
		}
		return target;
	}

	/// <summary>Keeps the dragged row's offset within its group, from its first row's place to its last.</summary>
	private static double ClampOffset( RowDragState drag, double offset )
	{
		var self = drag.Rows.FirstOrDefault( row => row.Id == drag.Id ).Center;
		return Math.Clamp( offset, drag.Rows[0].Center - self, drag.Rows[^1].Center - self );
	}

	/// <summary>Moves a reorderable row while a row of its group is dragged: the dragged row follows the pointer, held above the others, and each row
	/// between its old place and the new one slides aside by one row; rows snap back without motion once the drag ends, as the list then shows the new order.</summary>
	private void ApplyDrag( Row row, Guid id, RowOrder order )
	{
		_orders[id] = order;
		if ( _drag.Value is not { } drag || drag.Group != order.Group )
			return;

		if ( id == drag.Id )
		{
			row.Transform = new PanelTransform { TranslateY = ClampOffset( drag, drag.PointerY - drag.StartY ), Scale = LiftScale };
			row.ZIndex = 1;
			row.BackgroundColor = Theme.Current.Hover;
			row.BoxShadow = new BoxShadow { OffsetY = 4, Blur = 12, Color = Color.Black.WithAlpha( 0.35 ) };
			return;
		}

		var shift = order.Index > drag.Index && order.Index <= drag.Target ? -drag.Slot
			: order.Index < drag.Index && order.Index >= drag.Target ? drag.Slot
			: 0;
		row.Transform = new PanelTransform { TranslateY = shift };
		row.TransitionMs = MakeRoomMs;
		row.TransitionEasing = Easing.EaseOut;
		row.TransitionProperties = [TransitionProperty.Transform];
	}

	/// <summary>Holds a drag in progress.</summary>
	/// <param name="Id">The dragged row.</param>
	/// <param name="Group">Its group.</param>
	/// <param name="Index">Its place when the drag started.</param>
	/// <param name="StartY">The pointer's height in the window when the drag started.</param>
	/// <param name="PointerY">The pointer's latest height in the window.</param>
	/// <param name="Slot">The height of one row's place: the dragged row's height and the gap after it.</param>
	/// <param name="Target">The place it lands if dropped now.</param>
	/// <param name="Rows">Each row of the group with its place and the middle of its box when the drag started, top first.</param>
	private sealed record RowDragState( Guid Id, string Group, int Index, double StartY, double PointerY, double Slot, int Target, IReadOnlyList<(Guid Id, int Index, double Center)> Rows );

	/// <summary>Identifies a row being dragged among its group's rows.</summary>
	/// <param name="Owner">The sidebar view that started the drag; another view's rows do not take it.</param>
	/// <param name="Group">The rows it moves among.</param>
	/// <param name="Id">The row.</param>
	/// <param name="Index">Its place among the group's rows when the drag started.</param>
	private sealed record RowDrag( SidebarRows Owner, string Group, Guid Id, int Index );
}
