using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Contains panel build failures without replacing sibling feature views.</summary>
internal sealed class PanelSurface( PanelSession session ) : Cell
{
	/// <summary>Defines panel error content padding in logical pixels.</summary>
	private const int ErrorPadding = 24;

	/// <summary>Builds the feature content or a local failure surface.</summary>
	public override Blob Build()
	{
		try
		{
			if ( session.Content is { } content )
				return content.Build() ?? throw new InvalidOperationException( "The panel returned no content." );

			return BuildFailure( session.Failure ?? "The panel is unavailable." );
		}
		catch ( Exception error )
		{
			return BuildFailure( error.Message );
		}
	}

	/// <summary>Builds a contained, readable panel error.</summary>
	private Blob BuildFailure( string error )
	{
		return new Container
		{
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			OverflowY = Overflow.Scroll,
			ScrollbarY = ViewStyle.BuildScrollbar(),
			Padding = ErrorPadding,
			Gap = Theme.Current.Spacing,
			Children =
			[
				new Text
				{
					Content = $"{session.Info.Title} could not render",
					Color = Theme.Current.Accent,
				},
				new Text
				{
					Content = error,
					Color = Theme.Current.Muted,
				},
			],
		};
	}
}
