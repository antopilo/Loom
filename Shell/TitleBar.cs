using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Builds the custom titlebar: across the window with the window controls, or, while the sidebar shows, as the sidebar's top row whose
/// window controls the top-right pane holds instead.</summary>
internal sealed partial class TitleBar : GuardedCell<TitleBarInput>
{
	/// <summary>Defines the titlebar height in logical pixels; the full-window notification feed starts below it.</summary>
	internal const int BarHeight = 36;

	/// <summary>Defines the caption icon font weight on the OpenType scale.</summary>
	private const int CaptionFontWeight = 400;

	/// <summary>Retains the registered system caption font for the application lifetime; the notification views use its icons too.</summary>
	internal static readonly FontSource CaptionFont = SystemFonts.Register( "Lokn Caption", CaptionFontWeight, italic: false, "SegoeIcons.ttf" );

	/// <summary>Defines the titlebar divider thickness in logical pixels.</summary>
	private const int DividerWidth = 1;

	/// <summary>Builds the draggable titlebar: in the sidebar, Loom's menu, the modules' contributions and the sidebar button; across the window, the
	/// same with content such as the tabs in its middle and the window controls at its end.</summary>
	protected override Blob BuildView( TitleBarInput input )
	{
		List<Blob> children;
		if ( input.IsInSidebar )
		{
			children =
			[
				BuildWorkspaceActions( input ),
				.. input.Left.Select( BuildSlot ),
				new Spacer(),
				.. input.Right.Select( BuildSlot ),
				BuildSidebarAction( input ),
			];
		}
		else
		{
			children =
			[
				BuildWorkspaceActions( input ),
				.. input.Left.Select( BuildSlot ),
				input.Middle ?? new Spacer(),
				.. input.Right.Select( BuildSlot ),
				WindowControls.Build( Host, BarHeight, 0 ),
			];
		}

		// Goo excludes interactive descendants from native dragging and double-clicks.
		return Window.DragRegion( new Container
		{
			FlexDirection = FlexDirection.Row,
			Height = BarHeight,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Padding = input.IsInSidebar ? new Edges( 0, ActionInset, 0, 0 ) : new Edges( 0 ),
			Color = Host.IsFocused ? Theme.Current.Text : Theme.Current.Inactive,
			BorderWidth = input.IsInSidebar ? new Edges( 0 ) : new Edges( 0, 0, DividerWidth, 0 ),
			BorderColor = Theme.Current.Divider,
			FontFamily = Theme.Current.FontFamily,
			Children = children,
		} );
	}

	/// <summary>Builds one module contribution, sized by its own content and centered in the bar's height.</summary>
	private Blob BuildSlot( SlotItem item ) => new Container
	{
		Key = $"slot:{item.Id}",
		FlexShrink = 0,
		Height = BarHeight,
		// The slot is a column, so its content is centered down the bar by justifying, and across by aligning.
		JustifyContent = JustifyContent.Center,
		AlignItems = AlignItems.Center,
		Children = [Mount<ContentSurface>( $"slot-surface:{item.Id}", () => new( item ) )],
	};
}
