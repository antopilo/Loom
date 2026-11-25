using Goo;

namespace Lokn.Modules.Chat;

/// <summary>Shows the session's thinking effort as a ring beside the model, which names the effort in a tooltip and steps to the next effort the model
/// supports when pressed.</summary>
/// <remarks>
/// The session reads the live model's efforts and the current one from Omp; pressing the ring asks it for the next effort,
/// wrapping from the most back to off.
/// </remarks>
public sealed partial class ChatContent
{
	/// <summary>Builds the effort ring with its tooltip, or null until the model offers more than one effort.</summary>
	private Blob? BuildEffort()
	{
		var efforts = session.Efforts;
		if ( !session.IsConnected || efforts.Length < 2 )
			return null;

		var effort = session.Effort;
		var index = Array.IndexOf( efforts, effort );
		var name = effort.Length > 0 ? effort : "default";
		var next = efforts[(index + 1) % efforts.Length];
		var button = BuildRailButton( "", () => context.Run( session.UpdateEffort ) );
		button.Key = "conversation-effort";
		button.AccessibilityName = $"Effort: {name}. Press for {next}";
		button.FlexShrink = 0;
		button.Children = [ChatIcons.BuildLevel( index <= 0 ? 0 : (double)index / (efforts.Length - 1) )];
		return Cell.Mount<EffortInput, EffortTip>( "conversation-effort-tip", new( button, name, next ) );
	}
}
