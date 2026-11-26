using Goo;
using Lokn.Core;

namespace Lokn.Modules.Markdown;

/// <summary>Gives the cards of a reply's media, such as images, SVG drawings and Mermaid diagrams, one look: a soft fill over the chat, a faint
/// outline, and a header row with the card's name and actions over a faint rule.</summary>
internal static class CardStyle
{
	/// <summary>Defines the header row's height in logical pixels.</summary>
	internal const int HeaderHeight = 34;

	/// <summary>Styles a card's outer box.</summary>
	internal static T Frame<T>( T card ) where T : Blob
	{
		var theme = Theme.Current;
		card.MinWidth = 0;
		card.Overflow = Overflow.Hidden;
		card.BackgroundColor = theme.Text.WithAlpha( 0.035 );
		card.BorderColor = theme.Border.WithAlpha( 0.45 );
		card.BorderWidth = theme.BorderWidth;
		card.BorderRadius = theme.Radius * 2;
		return card;
	}

	/// <summary>Builds a card's header row from its contents, such as a name, a spacer and buttons.</summary>
	internal static Row Header( List<Blob> children )
	{
		var theme = Theme.Current;
		return new Row
		{
			Key = "card-header",
			Height = HeaderHeight,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Padding = new Edges( 0, theme.Spacing / 2, 0, theme.Spacing * 1.5 ),
			BorderColor = theme.Border.WithAlpha( 0.45 ),
			BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
			Children = children,
		};
	}

	/// <summary>Builds a card's name in its header: muted small text that ends with an ellipsis when it does not fit.</summary>
	internal static Text Name( string name ) => new()
	{
		Content = name,
		FontSize = ViewStyle.SmallSize,
		Color = Theme.Current.Muted,
		FlexShrink = 1,
		MinWidth = 0,
		TextWrap = TextWrap.NoWrap,
		TextTrimming = TextTrimming.Ellipsis,
	};
}
