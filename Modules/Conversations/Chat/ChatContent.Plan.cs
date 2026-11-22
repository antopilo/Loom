using Goo;
using Lokn.Core;

namespace Lokn.Modules.Chat;

/// <summary>Shows the plans <c>/plan</c> asks for: each one as a card in the reply, and Implement and Revise above the prompt for the latest one.</summary>
public sealed partial class ChatContent
{
	/// <summary>Builds an assistant text block that holds a plan: the text before it, the plan in a card, then the text after it.</summary>
	/// <param name="key">The block's stable key, starting with its message identifier; the text before the plan keeps it, so its reveal goes on.</param>
	/// <param name="parts">The block split at its plan.</param>
	private IEnumerable<Blob> BuildPlanText( string key, PlanParts parts )
	{
		if ( !string.IsNullOrWhiteSpace( parts.Before ) )
			yield return BuildStreamText( key, parts.Before.TrimEnd() );

		var theme = Theme.Current;
		yield return new Container
		{
			Key = key + ":plan-card",
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Plan",
			Gap = theme.Spacing,
			Padding = theme.Spacing * 3 / 2,
			BackgroundColor = theme.Raised,
			BorderColor = parts.IsClosed ? theme.Accent : theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = ChatStyle.BubbleRadius,
			Children =
			[
				new Text
				{
					Content = "Plan",
					FontWeight = AuthorWeight,
					Color = theme.Accent,
				},
				BuildStreamText( key + ":plan", parts.Plan.Trim() ),
			],
		};

		if ( !string.IsNullOrWhiteSpace( parts.After ) )
			yield return BuildStreamText( key + ":after", parts.After.Trim() );
	}

	/// <summary>Gets whether the agent's last reply since the user's last prompt ends with a finished plan, which waits for Implement or Revise.</summary>
	private bool HasPendingPlan()
	{
		if ( session.IsBusy )
			return false;

		var messages = store.GetMessages( conversationId );
		for ( var i = messages.Count - 1; i >= 0; i-- )
		{
			var message = messages[i];
			if ( message.Role == "user" )
				return false;
			if ( message.Role == "assistant" && ChatModes.SplitPlan( message.Text ) is { IsClosed: true } )
				return true;
		}
		return false;
	}

	/// <summary>Builds the card above the prompt that implements or revises the latest plan, or null when no plan waits.</summary>
	private Blob? BuildPlanActions()
	{
		if ( !HasPendingPlan() )
			return null;

		var theme = Theme.Current;
		var implement = theme.BuildButton( "Implement", () => context.Run( () => UpdateCardPrompt( ChatModes.ImplementPrompt ) ) );
		implement.Key = "plan-implement";
		implement.Color = theme.Accent;
		var revise = theme.BuildButton( "Revise", () => context.Run( UpdatePlanRevise ) );
		revise.Key = "plan-revise";
		return new Container
		{
			Key = "plan-actions",
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Plan ready",
			FlexShrink = 0,
			Gap = theme.Spacing,
			Padding = theme.Spacing * 3 / 2,
			BackgroundColor = theme.Raised,
			BorderColor = theme.Accent,
			BorderWidth = theme.BorderWidth,
			BorderRadius = ChatStyle.BubbleRadius,
			Children =
			[
				new Text
				{
					Content = "The plan is ready. Nothing changed yet. Implement it, or revise it with your notes.",
					FontSize = ViewStyle.SmallSize,
					Color = theme.Text,
				},
				new Row
				{
					Gap = theme.Spacing,
					FlexWrap = FlexWrap.Wrap,
					Children = [implement, revise],
				},
			],
		};
	}

	/// <summary>Starts a <c>/plan</c> prompt in the composer for the user's notes on the plan.</summary>
	private void UpdatePlanRevise()
	{
		_composer.Selection = new( new( 0, TextAffinity.Downstream ), new( _composer.Document.Length, TextAffinity.Downstream ) );
		_composer.Execute( new TextCommand( TextCommandKind.Insert, "/" + ChatModes.PlanName + " ", false ) );
		_composer.Focus();
		UpdateCommandQuery();
		MarkChanged();
	}
}
