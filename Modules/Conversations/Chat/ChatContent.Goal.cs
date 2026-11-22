using Goo;
using Lokn.Core;

namespace Lokn.Modules.Chat;

/// <summary>Shows the chat's goal above the prompt, with what it used and buttons that pause, resume or drop it.</summary>
public sealed partial class ChatContent
{
	/// <summary>Builds the goal line above the prompt, or null while the chat has no goal in progress.</summary>
	/// <remarks>Its buttons send <c>/goal</c> commands, so they act the same on a chat that runs in a host.</remarks>
	private Blob? BuildGoalBar()
	{
		if ( session.Goal is not { } goal || ReadText( goal, "status" ) is not ("active" or "paused" or "budget-limited") )
			return null;

		var theme = Theme.Current;
		var status = ReadText( goal, "status" );
		var children = new List<Blob>
		{
			new Text
			{
				Content = "Goal",
				FlexShrink = 0,
				FontSize = ViewStyle.SmallSize,
				FontWeight = AuthorWeight,
				Color = status == "active" ? theme.Accent : theme.Muted,
			},
			new Text
			{
				Content = ReadText( goal, "objective" ),
				FlexGrow = 1,
				FlexShrink = 1,
				MinWidth = 0,
				FontSize = ViewStyle.SmallSize,
				Color = theme.Text,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
			new Text
			{
				Content = $"{ChatModes.DescribeGoalStatus( goal )} · {ChatModes.DescribeGoalTokens( goal )}",
				FlexShrink = 0,
				FontSize = ViewStyle.SmallSize,
				Color = status == "budget-limited" ? theme.Attention : theme.Muted,
				TextWrap = TextWrap.NoWrap,
			},
		};
		if ( status == "active" )
			children.Add( BuildGoalButton( "Pause", "pause" ) );
		else if ( status == "paused" )
			children.Add( BuildGoalButton( "Resume", "resume" ) );
		children.Add( BuildGoalButton( "Drop", "drop" ) );
		return new Row
		{
			Key = "goal-bar",
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Goal",
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Padding = new Edges( theme.Spacing / 2, theme.Spacing ),
			BackgroundColor = theme.Raised,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = ChatStyle.BubbleRadius,
			Children = children,
		};
	}

	/// <summary>Builds a goal button that sends <c>/goal</c> with an action.</summary>
	private Button BuildGoalButton( string label, string action )
	{
		var button = BuildRailButton( label, () => context.Run( () => session.Send( $"/{ChatModes.GoalName} {action}", [] ) ) );
		button.Key = "goal-" + action;
		button.FlexShrink = 0;
		return button;
	}
}
