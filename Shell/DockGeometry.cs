using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Computes logical workspace rectangles independently of retained UI ownership.</summary>
internal sealed class DockGeometry
{
	/// <summary>Defines the pane grip width in logical pixels.</summary>
	internal const int GripWidth = 36;

	/// <summary>Defines the pane grip height in logical pixels.</summary>
	internal const int GripHeight = 16;

	/// <summary>Defines the gap between a pane's top edge and its grip in logical pixels.</summary>
	internal const int GripInset = 4;

	/// <summary>Defines the default space around and between panes in logical pixels.</summary>
	internal const int DefaultGap = 8;

	/// <summary>Defines the default pane corner radius in logical pixels.</summary>
	internal const int DefaultRadius = 8;

	/// <summary>Defines the narrowest draggable divider in logical pixels; with a smaller gap it overlaps the panes beside it.</summary>
	internal const int DividerHitWidth = 8;

	/// <summary>Defines the preferred minimum pane dimension in logical pixels.</summary>
	internal const int MinimumSize = 140;

	/// <summary>Stores the space around and between panes in logical pixels.</summary>
	private readonly double _gap;

	/// <summary>Stores pane bounds in workspace coordinates.</summary>
	internal Dictionary<Guid, ElementRect> Panes { get; } = [];

	/// <summary>Stores divider bounds in workspace coordinates.</summary>
	internal Dictionary<Guid, ElementRect> Dividers { get; } = [];

	/// <summary>Stores split bounds in workspace coordinates.</summary>
	internal Dictionary<Guid, ElementRect> Splits { get; } = [];

	/// <summary>Gets the area inside the outer gap that the whole tree fills; hidden panels keep this size.</summary>
	internal ElementRect Content
	{
		get; private set;
	}

	/// <summary>Creates empty geometry for one gap.</summary>
	private DockGeometry( double gap ) => _gap = Math.Max( 0, gap );

	/// <summary>Builds rectangles for the complete split tree, with a gap around and between panes.</summary>
	internal static DockGeometry Build( DockNode root, LayoutSize size, double gap = DefaultGap )
	{
		var geometry = new DockGeometry( gap );
		var insetX = Math.Min( geometry._gap, size.Width / 2 );
		var insetY = Math.Min( geometry._gap, size.Height / 2 );
		geometry.Content = new()
		{
			X = insetX,
			Y = insetY,
			Width = Math.Max( 0, size.Width - insetX * 2 ),
			Height = Math.Max( 0, size.Height - insetY * 2 ),
		};
		geometry.BuildNode( root, geometry.Content );
		return geometry;
	}

	/// <summary>Computes descendants while shrinking gracefully when the viewport is small.</summary>
	private void BuildNode( DockNode node, ElementRect bounds )
	{
		if ( node is DockPane )
		{
			Panes.Add( node.Id, bounds );
			return;
		}

		var split = (DockSplit)node;
		Splits.Add( split.Id, bounds );
		var isHorizontal = split.Axis == SplitAxis.Horizontal;
		var extent = isHorizontal ? bounds.Width : bounds.Height;
		var divider = Math.Min( _gap, extent );
		// The draggable area stays at least DividerHitWidth wide, centered on the gap.
		var hit = Math.Min( Math.Max( divider, DividerHitWidth ), extent );
		var hitOffset = (hit - divider) / 2;
		var available = Math.Max( 0, extent - divider );
		var minimum = Math.Min( MinimumSize, available / 2 );
		var first = Math.Clamp( available * split.Ratio, minimum, available - minimum );
		var second = available - first;

		if ( isHorizontal )
		{
			BuildNode( split.First, new()
			{
				X = bounds.X,
				Y = bounds.Y,
				Width = first,
				Height = bounds.Height,
			} );
			Dividers.Add( split.Id, new()
			{
				X = bounds.X + first - hitOffset,
				Y = bounds.Y,
				Width = hit,
				Height = bounds.Height,
			} );
			BuildNode( split.Second, new()
			{
				X = bounds.X + first + divider,
				Y = bounds.Y,
				Width = second,
				Height = bounds.Height,
			} );
		}
		else
		{
			BuildNode( split.First, new()
			{
				X = bounds.X,
				Y = bounds.Y,
				Width = bounds.Width,
				Height = first,
			} );
			Dividers.Add( split.Id, new()
			{
				X = bounds.X,
				Y = bounds.Y + first - hitOffset,
				Width = bounds.Width,
				Height = hit,
			} );
			BuildNode( split.Second, new()
			{
				X = bounds.X,
				Y = bounds.Y + first + divider,
				Width = bounds.Width,
				Height = second,
			} );
		}
	}

	/// <summary>Computes a pane's grip, centered against the pane's top edge.</summary>
	internal static ElementRect ComputeGrip( ElementRect pane )
	{
		var width = Math.Min( GripWidth, pane.Width );
		var height = Math.Min( GripHeight, pane.Height );
		return new()
		{
			X = pane.X + (pane.Width - width) / 2,
			Y = pane.Y + Math.Min( GripInset, pane.Height - height ),
			Width = width,
			Height = height,
		};
	}

	/// <summary>Computes the highlighted area for an edge or center swap.</summary>
	internal static ElementRect ComputePreview( ElementRect bounds, PanelPlacement placement )
	{
		return placement switch
		{
			PanelPlacement.Left => new()
			{
				X = bounds.X,
				Y = bounds.Y,
				Width = bounds.Width / 2,
				Height = bounds.Height,
			},
			PanelPlacement.Right => new()
			{
				X = bounds.X + bounds.Width / 2,
				Y = bounds.Y,
				Width = bounds.Width / 2,
				Height = bounds.Height,
			},
			PanelPlacement.Top => new()
			{
				X = bounds.X,
				Y = bounds.Y,
				Width = bounds.Width,
				Height = bounds.Height / 2,
			},
			PanelPlacement.Bottom => new()
			{
				X = bounds.X,
				Y = bounds.Y + bounds.Height / 2,
				Width = bounds.Width,
				Height = bounds.Height / 2,
			},
			_ => bounds,
		};
	}
}

/// <summary>Selects the workspace rectangle assigned to one flat retained child.</summary>
internal enum DockSlotKind
{
	Body, Grip, Divider, Preview, Hidden
}

/// <summary>Maps a stable retained child to a docking geometry rectangle.</summary>
internal readonly record struct DockSlot( DockSlotKind Kind, Guid NodeId, PanelPlacement Placement = PanelPlacement.Swap );

/// <summary>Arranges flat, keyed panel children so docking never reparents their UI state, with a gap around and between panes.</summary>
internal sealed class DockLayout( DockNode root, IReadOnlyList<DockSlot> slots, double gap ) : LayoutAlgorithm
{
	/// <summary>Uses the finite viewport assigned by the shell, in logical pixels.</summary>
	public LayoutSize Measure( LayoutContext context, LayoutSize available )
	{
		return new( double.IsFinite( available.Width ) ? available.Width : 0, double.IsFinite( available.Height ) ? available.Height : 0 );
	}

	/// <summary>Places every retained child, without changing panel ownership.</summary>
	public void Arrange( LayoutContext context, LayoutSize finalSize )
	{
		var geometry = DockGeometry.Build( root, finalSize, gap );
		for ( var index = 0; index < slots.Count; index++ )
		{
			var slot = slots[index];
			var bounds = slot.Kind switch
			{
				DockSlotKind.Body => geometry.Panes[slot.NodeId],
				DockSlotKind.Hidden => geometry.Content,
				DockSlotKind.Grip => DockGeometry.ComputeGrip( geometry.Panes[slot.NodeId] ),
				DockSlotKind.Divider => geometry.Dividers[slot.NodeId],
				_ => DockGeometry.ComputePreview( geometry.Panes[slot.NodeId], slot.Placement ),
			};

			context.ArrangeChild( index, bounds );
		}
	}
}
