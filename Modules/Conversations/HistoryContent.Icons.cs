using Goo;
using Lokn.Core;
using Lokn.Modules.Workspaces;

namespace Lokn.Modules.Conversations;

/// <summary>Builds the chat sidebar's own icons and marks beside the shared <see cref="SidebarRows"/> ones.</summary>
internal sealed partial class HistoryContent
{
	/// <summary>Defines the broom glyph from Segoe Fluent Icons, on the action that tidies a project's chats.</summary>
	private const string TidyGlyph = "\uEA99";

	/// <summary>Builds a steady dot in the attention color beside a chat whose agent finished or asked something and waits for the user.</summary>
	/// <remarks>It does not pulse: nothing runs, the chat only waits to be looked at.</remarks>
	internal static Container BuildWaitingDot() => new()
	{
		Key = "waiting",
		Width = ActivityOrb.Size,
		Height = ActivityOrb.Size,
		FlexShrink = 0,
		BorderRadius = ActivityOrb.Size / 2.0,
		BackgroundColor = Theme.Current.Attention,
	};

	/// <summary>Pulses a small dot beside a chat whose agent is working; the fade animates on its own and stops when unmounted.</summary>
	internal sealed class ActivityOrb : GuardedCell<Color>
	{
		/// <summary>Defines the dot's diameter in logical pixels.</summary>
		internal const int Size = 7;

		/// <summary>Defines the time for each fade, out or back in, in milliseconds.</summary>
		private const double FadeMs = 700;

		/// <summary>Defines the opacity the dot fades down to.</summary>
		private const double DimOpacity = 0.25;

		/// <summary>Counts fades; an even count shows the dot bright, an odd one dim.</summary>
		private readonly State<int> _fades = new( 0 );

		/// <summary>Starts pulsing, and stops when the orb unmounts.</summary>
		protected override void Mounted() => Every( FadeMs, () => _fades.Value++ );

		/// <summary>Builds the dot at its next brightness, fading there over one period.</summary>
		protected override Blob BuildView( Color color ) => new Container
		{
			Width = Size,
			Height = Size,
			FlexShrink = 0,
			BorderRadius = Size / 2.0,
			BackgroundColor = color,
			Opacity = _fades.Value % 2 == 0 ? 1 : DimOpacity,
			TransitionMs = FadeMs,
			TransitionEasing = Easing.EaseInOut,
			TransitionProperties = [TransitionProperty.Opacity],
		};
	}
}
