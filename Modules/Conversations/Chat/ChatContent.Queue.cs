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
	/// <summary>Stores the steering messages Omp holds, as their queue text, oldest first.</summary>
	private string[] _queuedSteering = [];

	/// <summary>Stores the follow-up messages Omp holds, as their queue text, oldest first.</summary>
	private string[] _queuedFollowUps = [];

	/// <summary>Stores how many leading steering messages the model already received.</summary>
	private int _liveSteered;

	/// <summary>Stores whether Enter steers the running turn rather than queuing for after it; Ctrl+Enter does the other.</summary>
	private bool _isSteerMode;

	/// <summary>Reads Omp's queue snapshot.</summary>
	private void UpdateQueue( JsonElement frame )
	{
		_queuedSteering = ReadQueue( frame, "steering" );
		_queuedFollowUps = ReadQueue( frame, "followUp" );
		_liveSteered = frame.TryGetProperty( "liveSteered", out var live ) && live.TryGetInt32( out var count ) ? count : 0;
	}

	/// <summary>Forgets the queue, when the turn settles or the connection ends.</summary>
	private void ClearQueue()
	{
		_queuedSteering = [];
		_queuedFollowUps = [];
		_liveSteered = 0;
	}

	/// <summary>Shows a message the moment it is sent, before Omp reports its queue.</summary>
	private void UpdateQueueSent( string text, bool steer )
	{
		if ( steer )
			_queuedSteering = [.. _queuedSteering, text];
		else
			_queuedFollowUps = [.. _queuedFollowUps, text];
	}

	/// <summary>Builds the waiting messages, steering first, at the top of the prompt box; null when none wait.</summary>
	private Blob? BuildQueue()
	{
		if ( _queuedSteering.Length == 0 && _queuedFollowUps.Length == 0 )
			return null;

		var rows = new List<Blob>();
		for ( var i = 0; i < _queuedSteering.Length; i++ )
			rows.Add( BuildQueuedRow( $"queued-steer-{i}", _queuedSteering[i], i < _liveSteered ? QueuedKind.Delivered : QueuedKind.Steering ) );
		for ( var i = 0; i < _queuedFollowUps.Length; i++ )
			rows.Add( BuildQueuedRow( $"queued-follow-{i}", _queuedFollowUps[i], QueuedKind.FollowUp ) );
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
			var promote = BuildRailButton( "Steer now", () => context.Run( () => UpdatePromote( text ) ) );
			promote.AccessibilityName = $"Steer now: {line}";
			children.Add( promote );
		}
		if ( kind != QueuedKind.Delivered )
		{
			var remove = BuildRailButton( "×", () => context.Run( () => UpdateUnqueue( text, kind == QueuedKind.Steering ) ) );
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

	/// <summary>Moves a waiting follow-up into the steering queue, so the agent reads it at its next step.</summary>
	private void UpdatePromote( string text )
	{
		if ( _session is { } session )
			_ = UpdateQueueRequestAsync( session, "promote_queued_message", new { message = text }, restore: null );
	}

	/// <summary>Takes a waiting message back from Omp, and puts it in the prompt when the prompt is empty.</summary>
	private void UpdateUnqueue( string text, bool steering )
	{
		if ( _session is { } session )
			_ = UpdateQueueRequestAsync( session, "remove_queued_message", new { message = text, queue = steering ? "steering" : "followUp" }, restore: text );
	}

	/// <summary>Sends a queue request; Omp answers with a new <c>queue_update</c>, and a refusal shows in the status line.</summary>
	private async Task UpdateQueueRequestAsync( OmpSession session, string type, object fields, string? restore )
	{
		try
		{
			await session.RequestAsync( type, fields, context.Lifetime );
			if ( restore is null )
				return;

			context.Post( () =>
			{
				if ( _composer.Document.Length > 0 )
					return;

				_composer.Document.Apply( new TextChange( new TextRange( 0, 0 ), restore ) );
				store.SaveDraft( conversationId, restore );
				MarkChanged();
			} );
		}
		catch ( Exception error )
		{
			context.Post( () =>
			{
				_status = error is OperationCanceledException ? "Stopped" : DescribeFailure( error );
				MarkChanged();
			} );
		}
	}

	/// <summary>Reads one queue's messages.</summary>
	private static string[] ReadQueue( JsonElement frame, string name ) =>
		frame.TryGetProperty( name, out var queue ) && queue.ValueKind == JsonValueKind.Array
			? [.. queue.EnumerateArray().Where( item => item.ValueKind == JsonValueKind.String ).Select( item => item.GetString()! )]
			: [];

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
