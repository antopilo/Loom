using Goo;

namespace Lokn.Modules.Chat;

/// <summary>Shows the session's thinking effort as a ring beside the model, which steps to the next effort the model supports when pressed.</summary>
/// <remarks>
/// The session reads the live model's efforts and the current one from Omp; pressing the ring asks it for the next effort,
/// wrapping from the most back to off.
/// </remarks>
public sealed partial class ChatContent
{
	/// <summary>Builds the effort ring, or null until the model offers more than one effort.</summary>
	private Button? BuildEffort()
	{
		var efforts = session.Efforts;
		if ( !session.IsConnected || efforts.Length < 2 )
			return null;

		var effort = session.Effort;
		var index = Array.IndexOf( efforts, effort );
		var button = BuildRailButton( "", () => context.Run( session.UpdateEffort ) );
		button.Key = "conversation-effort";
		button.AccessibilityName = $"Effort: {(effort.Length > 0 ? effort : "default")}. Press for {efforts[(index + 1) % efforts.Length]}";
		button.FlexShrink = 0;
		button.Children = [ChatIcons.BuildLevel( index <= 0 ? 0 : (double)index / (efforts.Length - 1) )];
		return button;
	}
}
