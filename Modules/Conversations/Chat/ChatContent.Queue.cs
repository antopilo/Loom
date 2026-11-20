using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Omp;

namespace Lokn.Modules.Chat;

/// <summary>Shows and edits the messages waiting for the running turn, and chooses whether a new one steers it or waits for it.</summary>
/// <remarks>
/// Omp owns the queue and reports it with <c>queue_update</c>: steering messages, which the agent reads at its next step, then
/// follow-ups, which start a turn once this one ends. The first <c>liveSteered</c> steering messages are already in the model's
/// request and can no longer be taken back. A message shows here as soon as it is sent, until Omp's next report replaces it.
/// </remarks>
public sealed partial class ChatContent
{
	/// <summary>Stores whether Enter steers the running turn rather than queuing for after it; Ctrl+Enter does the other.</summary>
	private bool _isSteerMode;

	/// <summary>Builds the waiting messages, steering first, at the top of the prompt box; null when none wait.</summary>
	private Blob? BuildQueue()
	{
		var steering = session.QueuedSteering;
		var followUps = session.QueuedFollowUps;
		if ( steering.Length == 0 && followUps.Length == 0 )
			return null;

		var rows = new List<Blob>();
		for ( var i = 0; i < steering.Length; i++ )
			rows.Add( BuildQueuedRow( $"queued-steer-{i}", steering[i], i < session.LiveSteered ? QueuedKind.Delivered : QueuedKind.Steering ) );
		for ( var i = 0; i < followUps.Length; i++ )
			rows.Add( BuildQueuedRow( $"queued-follow-{i}", followUps[i], QueuedKind.FollowUp ) );
		return new Column
		{
			Key = "conversation-queue",
			Gap = Theme.Current.Spacing / 4,
			AccessibilityRole = AccessibilityRole.List,
			AccessibilityName = "Waiting messages",
			Children = rows,
		};
	}

	/// <summary>Builds one waiting message: what it will do, its first line, and its actions.</summary>
	private Row BuildQueuedRow( string key, string text, QueuedKind kind )
	{
		var theme = Theme.Current;
		var (label, color, meaning) = kind switch
		{
			QueuedKind.Delivered => ("Steered", theme.Muted, "the agent already received it"),
			QueuedKind.Steering => ("Steer", theme.Accent, "the agent reads it at its next step"),
			_ => ("Queued", theme.Muted, "it is sent after this turn"),
		};
		var line = text.Split( '\n' )[0].Trim();
		var children = new List<Blob>
		{
			new Container
			{
				FlexShrink = 0,
				Padding = new Edges( 1, theme.Spacing / 2 ),
				BorderRadius = theme.Radius,
				BorderColor = color,
				BorderWidth = theme.BorderWidth,
				Children = [new Text { Content = label, FontSize = ViewStyle.SmallSize - 1, Color = color, TextWrap = TextWrap.NoWrap }],
			},
			new Text
			{
				Content = line.Length > 0 ? line : text.Trim(),
				FontSize = ViewStyle.SmallSize,
				Color = kind == QueuedKind.Delivered ? theme.Muted : theme.Text,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
				MinWidth = 0,
				FlexGrow = 1,
				FlexShrink = 1,
			},
		};
		if ( kind == QueuedKind.FollowUp )
		{
			var promote = BuildRailButton( "Steer now", () => context.Run( () => session.UpdatePromote( text ) ) );
			promote.AccessibilityName = $"Steer now: {line}";
			children.Add( promote );
		}
		if ( kind != QueuedKind.Delivered )
		{
			var remove = BuildRailButton( "×", () => context.Run( () => session.UpdateUnqueue( text, kind == QueuedKind.Steering ) ) );
			remove.AccessibilityName = $"Take back: {line}";
			children.Add( remove );
		}
		return new Row
		{
			Key = key,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			AccessibilityName = $"{label}: {line}; {meaning}",
			Children = children,
		};
	}

	/// <summary>Builds the switch between queuing and steering, shown beside the send button while the agent works.</summary>
	private Button BuildSendMode()
	{
		var button = BuildRailButton( _isSteerMode ? "Steer" : "Queue", () => context.Run( () =>
		{
			_isSteerMode = !_isSteerMode;
			MarkChanged();
		} ) );
		button.Key = "conversation-send-mode";
		button.FlexShrink = 0;
		button.Height = Theme.Current.ControlHeight;
		button.Color = _isSteerMode ? Theme.Current.Accent : Theme.Current.Muted;
		button.AccessibilityName = _isSteerMode
			? "Enter steers the running turn. Switch to queue for after it"
			: "Enter queues for after this turn. Switch to steer the running turn";
		return button;
	}

	/// <summary>Names what a waiting message will do.</summary>
	private enum QueuedKind
	{
		/// <summary>A steering message the model already received.</summary>
		Delivered,

		/// <summary>A steering message the agent reads at its next step.</summary>
		Steering,

		/// <summary>A follow-up sent once the turn ends.</summary>
		FollowUp,
	}
}
