using Goo;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Describes one icon button of the Git toolbar.</summary>
/// <param name="Icon">The line icon, on <see cref="GitIcons"/>' 24 by 24 grid.</param>
/// <param name="Name">What the button does, read by assistive technology and shown in its tooltip.</param>
/// <param name="IsEnabled">Whether the button can be pressed.</param>
/// <param name="Run">Runs the button's action.</param>
/// <param name="Handle">Targets the button, for a menu that opens beside it; null when none does.</param>
/// <param name="Badge">A small count drawn on the button, such as the review notes a review takes along; zero draws none.</param>
/// <param name="IsNarrow">Whether the button is half width, such as the chevron of a split button.</param>
/// <param name="IsOn">Whether the button shows a chosen option, raised.</param>
internal sealed record GitToolInput( VectorPath Icon, string Name, bool IsEnabled, Action Run, ElementHandle? Handle = null, int Badge = 0, bool IsNarrow = false, bool IsOn = false );

/// <summary>Draws a square, quiet icon button that shows its name in a tooltip under the pointer.</summary>
/// <remarks>The hover state lives in this cell, so moving over the toolbar redraws only the button, not the whole panel.</remarks>
internal sealed class GitToolButton : GuardedCell<GitToolInput>
{
	/// <summary>Defines the icon size in logical pixels.</summary>
	private const int IconSize = 16;

	/// <summary>Defines the opacity of a disabled button's icon.</summary>
	private const double DisabledOpacity = 0.35;

	/// <summary>Defines how long the button's fill takes to change, in milliseconds.</summary>
	private const int FadeMs = 120;

	/// <summary>Targets the button when the input names no handle, for the tooltip's anchor.</summary>
	private readonly ElementHandle _own = new();

	/// <summary>Reports whether the pointer is over the button.</summary>
	private readonly State<bool> _isHovered = new( false );

	/// <summary>Builds the button with its icon, its badge, and its tooltip while hovered.</summary>
	protected override Blob BuildView( GitToolInput input )
	{
		var theme = Theme.Current;
		var handle = input.Handle ?? _own;
		var size = theme.ControlHeight - theme.Spacing / 2;
		var icon = GitIcons.Build( input.Icon, input.IsNarrow ? IconSize - 4 : IconSize, input.IsOn ? theme.Text : theme.Text.WithAlpha( 0.82 ) );
		icon.Opacity = input.IsEnabled ? 1 : DisabledOpacity;
		var children = new List<Blob> { icon };
		if ( input.Badge > 0 )
			children.Add( new Container
			{
				Position = PositionType.Absolute,
				Right = 1,
				Top = 1,
				MinWidth = 13,
				Height = 13,
				Padding = new Edges( 0, 3 ),
				AlignItems = AlignItems.Center,
				JustifyContent = JustifyContent.Center,
				BorderRadius = 7,
				BackgroundColor = theme.Accent,
				Children = [new Text { Content = input.Badge.ToString( "N0" ), FontSize = 9, Color = theme.Panel, LineHeight = 1 }],
			} );
		if ( _isHovered.Value )
		{
			// A native popup draws over native child surfaces, such as browser pages, which a portal cannot.
			var tooltip = new Container
			{
				Padding = new Edges( theme.Spacing / 2, theme.Spacing ),
				BackgroundColor = theme.Raised,
				Color = theme.Text,
				FontFamily = theme.FontFamily,
				FontSize = ViewStyle.SmallSize,
				BorderColor = theme.Border,
				BorderWidth = theme.BorderWidth,
				BorderRadius = theme.Radius,
				Children = [new Text { Content = input.Name, TextWrap = TextWrap.NoWrap }],
			};
			children.Add( Cell.Mount<NativePopupInput, NativePopup>( "tooltip", new( handle, tooltip, null )
			{
				Placement = PortalPlacement.BottomStart,
				Kind = PopupKind.Tooltip,
				Role = AccessibilityRole.Tooltip,
				Name = input.Name,
			} ) );
		}

		void Run()
		{
			if ( input.IsEnabled )
				InvokeInputCallback( input.Run );
		}
		return new Button
		{
			Handle = handle,
			Width = input.IsNarrow ? size / 2 : size,
			Height = size,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = theme.Radius,
			BackgroundColor = input.IsOn ? theme.Hover : Color.Transparent,
			HoverStyle = input.IsEnabled ? new Style { BackgroundColor = theme.Hover } : null,
			TransitionMs = FadeMs,
			TransitionProperties = [TransitionProperty.BackgroundColor],
			FocusStyle = new Style { BackgroundColor = theme.Hover },
			Focusable = input.IsEnabled,
			Cursor = input.IsEnabled ? Cursor.Pointer : Cursor.Default,
			Disabled = !input.IsEnabled,
			AccessibilityName = input.Name,
			OnClick = Run,
			OnPointerEnter = _ => _isHovered.Value = true,
			OnPointerLeave = _ => _isHovered.Value = false,
			Children = children,
		};
	}
}
