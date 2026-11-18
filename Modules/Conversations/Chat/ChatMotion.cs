using Goo;

namespace Lokn.Modules.Chat;

/// <summary>Defines the chat's one motion language: short ease-out timings and distances every animated change shares.</summary>
internal static class ChatMotion
{
	/// <summary>Defines the duration of hover-scale changes and small fades, in milliseconds.</summary>
	internal const double FastMs = 140;

	/// <summary>Defines the duration of a digit roll, a label change or a row entering, in milliseconds.</summary>
	internal const double BaseMs = 220;

	/// <summary>Defines the duration of height and position glides and large reveals, in milliseconds.</summary>
	internal const double SlowMs = 360;

	/// <summary>Defines how far a new element rises while it fades in, in logical pixels.</summary>
	internal const double Rise = 6;

	/// <summary>Holds the curve every chat motion follows: fast at first, settling gently.</summary>
	internal static readonly Easing Ease = Easing.EaseOut;
}
