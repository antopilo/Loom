using Goo;
using Lokn.Core;

namespace Lokn.Modules.Chat;

/// <summary>Describes the effort ring under the prompt and what its tooltip says.</summary>
/// <param name="Ring">The rail button that draws the ring and steps to the next effort.</param>
/// <param name="Effort">The session's effort, such as <c>high</c>, or <c>default</c> when unknown.</param>
internal sealed record EffortInput( Button Ring, string Effort );

/// <summary>Holds the effort ring and names its effort in a tooltip while the pointer rests on it, since the ring's fill alone does not say which effort
/// it is.</summary>
/// <remarks>The hover state lives in this cell, so pointing at the ring redraws only it, not the whole chat.</remarks>
internal sealed class EffortTip : GuardedCell<EffortInput>
{
	/// <summary>Targets the ring, for the tooltip's anchor.</summary>
	private readonly ElementHandle _handle = new();

	/// <summary>Reports whether the pointer is over the ring.</summary>
	private readonly State<bool> _isHovered = new( false );

	/// <summary>Builds the ring, with its effort in a tooltip while hovered.</summary>
	protected override Blob BuildView( EffortInput input )
	{
		var children = new List<Blob> { input.Ring };
		if ( _isHovered.Value )
		{
			var theme = Theme.Current;
			// A native popup draws over native child surfaces, such as browser pages, which a portal cannot; it takes no input, so the ring keeps the pointer.
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
				Children = [new Text { Content = $"Effort: {input.Effort}", TextWrap = TextWrap.NoWrap }],
			};
			children.Add( Cell.Mount<NativePopupInput, NativePopup>( "tooltip", new( _handle, tooltip, null )
			{
				Placement = PortalPlacement.TopEnd,
				Kind = PopupKind.Tooltip,
				Role = AccessibilityRole.Tooltip,
				Name = $"Effort: {input.Effort}",
			} ) );
		}

		return new Container
		{
			Handle = _handle,
			FlexShrink = 0,
			OnPointerEnter = _ => _isHovered.Value = true,
			OnPointerLeave = _ => _isHovered.Value = false,
			Children = children,
		};
	}
}
