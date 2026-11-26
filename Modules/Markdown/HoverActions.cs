using Goo;
using Lokn.Core;

namespace Lokn.Modules.Markdown;

/// <summary>Supplies a card and the actions that float over its top-right corner while the pointer is over it.</summary>
/// <param name="Card">The card, such as a code block.</param>
/// <param name="Actions">The icon buttons, such as Copy.</param>
internal sealed record HoverActionsInput( Container Card, Blob[] Actions );

/// <summary>Shows a card's actions as an overlay in its top-right corner only while the pointer is over the card, so they take no row of their own.</summary>
internal sealed class HoverActions : GuardedCell<HoverActionsInput>
{
	/// <summary>Defines how far the actions sit from the card's corner, in logical pixels.</summary>
	private const int Inset = 6;

	/// <summary>Tracks whether the pointer is over the card.</summary>
	private readonly State<bool> _isHovered = new( false );

	/// <summary>Builds the card with its actions over it while hovered.</summary>
	protected override Blob BuildView( HoverActionsInput input )
	{
		List<Blob> children = [input.Card];
		if ( _isHovered.Value )
			children.Add( new Row
			{
				Key = "hover-actions",
				Position = PositionType.Absolute,
				Top = Inset,
				Right = Inset,
				Gap = 2,
				Padding = 2,
				AlignItems = AlignItems.Center,
				BorderRadius = Theme.Current.Radius,
				BackgroundColor = Theme.Current.Raised,
				Children = [.. input.Actions],
			} );
		return new Container
		{
			MinWidth = 0,
			OnPointerEnter = _ => _isHovered.Value = true,
			OnPointerLeave = _ => _isHovered.Value = false,
			Children = children,
		};
	}
}
