using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Supplies workspace actions owned by the shell to the window titlebar.</summary>
/// <param name="ToggleSidebar">Shows or hides the sidebar.</param>
/// <param name="OpenSettings">Opens the settings editor.</param>
/// <param name="Left">Module contributions shown after the workspace actions.</param>
/// <param name="Right">Module contributions shown before the window controls, such as the Notifications module's bell.</param>
/// <param name="Middle">Content that fills the middle of the titlebar, such as the tabs; null leaves it empty for dragging. With content, the sidebar button is left out: tabs cannot be hidden.</param>
internal sealed record TitleBarInput(
	Action ToggleSidebar,
	Action OpenSettings,
	IReadOnlyList<SlotItem> Left,
	IReadOnlyList<SlotItem> Right,
	Blob? Middle );

/// <summary>Builds compact workspace icon controls inside the native drag region.</summary>
internal sealed partial class TitleBar
{
	/// <summary>Defines the workspace icon button width in logical pixels.</summary>
	private const int ActionWidth = 32;

	/// <summary>Defines the workspace icon button height in logical pixels.</summary>
	private const int ActionHeight = 28;

	/// <summary>Defines the workspace icon size in logical pixels.</summary>
	private const int ActionFontSize = 14;

	/// <summary>Defines the workspace action gap in logical pixels.</summary>
	private const int ActionGap = 4;

	/// <summary>Defines the workspace action cluster's horizontal inset in logical pixels.</summary>
	private const int ActionInset = 8;

	/// <summary>Defines the native DockLeft icon used to show or hide the sidebar.</summary>
	private const string SidebarGlyph = "\uE90C";

	/// <summary>Defines the native Settings icon used to open preferences.</summary>
	private const string SettingsGlyph = "\uE713";

	/// <summary>Stores the workspace action whose tooltip is visible.</summary>
	private string? _hoveredAction;

	/// <summary>Targets each workspace action by label, so its tooltip keeps one anchor across rebuilds.</summary>
	private readonly Dictionary<string, ElementHandle> _actionHandles = [];

	/// <summary>Builds the workspace controls at the start of the titlebar.</summary>
	private Blob BuildWorkspaceActions( TitleBarInput input )
	{
		List<Blob> actions = [];
		if ( input.Middle is null )
			actions.Add( BuildWorkspaceAction( "Toggle sidebar", SidebarGlyph, "Sidebar  ·  Ctrl+B", input.ToggleSidebar ) );
		actions.Add( BuildWorkspaceAction( "Settings", SettingsGlyph, "Settings", input.OpenSettings ) );
		return new Row
		{
			Padding = new Edges( 0, ActionInset ),
			Gap = ActionGap,
			AlignItems = AlignItems.Center,
			Children = actions,
		};
	}

	/// <summary>Builds a named, keyboard-accessible icon button with hover help.</summary>
	private Blob BuildWorkspaceAction( string label, string glyph, string help, Action callback )
	{
		var handle = _actionHandles.TryGetValue( label, out var known ) ? known : _actionHandles[label] = new ElementHandle();
		Action activate = () =>
		{
			_hoveredAction = null;
			Rebuild();
			InvokeInputCallback( callback );
		};
		var children = new List<Blob>
		{
			new Text
			{
				Content = glyph,
				FontFamily = CaptionFont.Family,
				FontSize = ActionFontSize,
			},
		};
		if ( _hoveredAction == label )
		{
			// A native popup draws over native child surfaces, such as browser pages, which a portal cannot.
			var tooltip = new Container
			{
				Padding = Theme.Current.Spacing,
				BackgroundColor = Theme.Current.Raised,
				Color = Theme.Current.Text,
				FontFamily = Theme.Current.FontFamily,
				FontSize = Theme.Current.FontSize,
				BorderColor = Theme.Current.Border,
				BorderWidth = Theme.Current.BorderWidth,
				BorderRadius = Theme.Current.Radius,
				Children = [new Text { Content = help }],
			};
			children.Add( Cell.Mount<NativePopupInput, NativePopup>( "tooltip", new( handle, tooltip, null )
			{
				Placement = PortalPlacement.BottomStart,
				Kind = PopupKind.Tooltip,
				Role = AccessibilityRole.Tooltip,
				Name = help,
			} ) );
		}

		return new Button
		{
			Key = label,
			Handle = handle,
			Width = ActionWidth,
			Height = ActionHeight,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = Theme.Current.Radius,
			Focusable = true,
			AccessibilityName = label,
			Cursor = Cursor.Pointer,
			OnClick = activate,
			OnPointerEnter = _ =>
			{
				_hoveredAction = label;
				Rebuild();
			},
			OnPointerLeave = _ =>
			{
				_hoveredAction = null;
				Rebuild();
			},
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, activate ),
				new( Key.Space, KeyModifiers.None, activate ),
			],
			HoverStyle = new Style
			{
				BackgroundColor = Theme.Current.CaptionHover,
				Color = Theme.Current.Text,
			},
			FocusStyle = new Style
			{
				BackgroundColor = Theme.Current.CaptionHover,
			},
			Children = children,
		};
	}
}
