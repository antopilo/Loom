namespace Lokn.Modules.Chat;

/// <summary>Defines chat text sizing alongside the shared theme; small text and the scrollbar are <see cref="Lokn.Core.ViewStyle"/>'s.</summary>
internal static class ChatStyle
{
	/// <summary>Defines assistant prose text size in logical pixels.</summary>
	internal const int BodySize = 14;

	/// <summary>Defines assistant prose line height as a multiple of its font size.</summary>
	internal const double BodyLineHeight = 1.5;

	/// <summary>Defines the widest reading column for the transcript and composer in logical pixels.</summary>
	internal const int ColumnWidth = 760;

	/// <summary>Defines the corner radius of message bubbles and the prompt box in logical pixels.</summary>
	internal const int BubbleRadius = 16;
}
