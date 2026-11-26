using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Conversations;

namespace Lokn.Modules.Chat;

/// <summary>Pins the agent's current todo list: beside the conversation when there is room, otherwise just above the prompt.</summary>
public sealed partial class ChatContent
{
	/// <summary>Defines the pinned list's width beside the conversation in logical pixels.</summary>
	private const int TodoPinWidth = 260;

	/// <summary>Defines the tallest the pinned list grows above the prompt before it scrolls, in logical pixels.</summary>
	private const int TodoPinMaxHeight = 180;

	/// <summary>Retains the transcript area, whose width decides where the list is pinned.</summary>
	private readonly ElementHandle _transcriptArea = new();

	/// <summary>Tracks whether the margin beside the conversation column fits the list; views that read it rebuild when it flips.</summary>
	private readonly State<bool> _isTodoBeside = new( false );

	/// <summary>Tracks whether the transcript area's size is being followed.</summary>
	private bool _isAreaWatched;

	/// <summary>Builds the pinned list, or null when the agent has no unfinished list or it belongs in the other place.</summary>
	private Blob? BuildTodoPin( IReadOnlyList<TodoPhase> phases, bool isBeside )
	{
		if ( !_isAreaWatched )
		{
			_isAreaWatched = true;
			_transcriptArea.MetricsChanged += metrics => context.Run( () => UpdateTodoBeside( metrics.ContentBox.Width ) );
		}

		if ( isBeside != _isTodoBeside.Value || phases.Count == 0 )
			return null;

		var card = new Container
		{
			Key = isBeside ? "todo-pin-beside" : "todo-pin-above",
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Current todo",
			Gap = Theme.Current.Spacing,
			Padding = Theme.Current.Spacing * 3 / 2,
			BackgroundColor = Theme.Current.Raised,
			BorderColor = Theme.Current.Border,
			BorderWidth = Theme.Current.BorderWidth,
			BorderRadius = ChatStyle.BubbleRadius,
			OverflowY = Overflow.Scroll,
			ScrollbarY = ViewStyle.GetScrollbar(),
			MinHeight = 0,
			Children = BuildTodo( phases ),
		};
		if ( !isBeside )
		{
			card.MaxHeight = TodoPinMaxHeight;
			card.FlexShrink = 0;
			return card;
		}

		// Floats in the left margin like a glossary, never over the centered conversation column, and scrolls when taller than the transcript.
		card.MaxHeight = Length.Percent( 100 );
		return new Container
		{
			Key = "todo-pin-margin",
			Position = PositionType.Absolute,
			Left = ContentPadding,
			Top = Theme.Current.Spacing,
			// Ends above the floating prompt rather than behind it.
			Bottom = ReadFloatingHeight(),
			Width = TodoPinWidth,
			AlignItems = AlignItems.Stretch,
			JustifyContent = JustifyContent.FlexStart,
			Children = [card],
		};
	}

	/// <summary>Finds the agent's newest todo list that still has work left, or none.</summary>
	/// <remarks>A call still running, or one that only marks a task, may carry no list yet; the newest call that has one wins.</remarks>
	internal IReadOnlyList<TodoPhase> ComputeCurrentTodo( IReadOnlyList<ConversationMessage> messages )
	{
		for ( var index = messages.Count - 1; index >= 0; index-- )
		{
			var message = messages[index];
			if ( !ReadFacts( message ).HasTodo )
				continue;

			foreach ( var block in Enumerable.Reverse( ReadFacts( message ).Blocks ) )
			{
				if ( ReadText( block, "type" ) != "toolCall" || ReadText( block, "name" ) is not ("todo" or "todo_write") )
					continue;

				ComputeToolStatus( ReadText( block, "id" ), out var result );
				var phases = ComputeTodo( block.TryGetProperty( "arguments", out var arguments ) ? arguments : default, result );
				if ( phases.Count == 0 )
					continue;

				var isOpen = phases.Any( phase => phase.Tasks.Any( task => task.State is TaskState.Pending or TaskState.Active ) );
				return isOpen ? phases : [];
			}
		}

		return [];
	}

	/// <summary>Places the list beside the conversation once the margin left of the centered column fits it.</summary>
	private void UpdateTodoBeside( double width )
	{
		var column = Math.Min( ChatStyle.ColumnWidth, width - ContentPadding * 2 );
		var isBeside = (width - column) / 2 >= TodoPinWidth + ContentPadding * 2;
		if ( _isTodoBeside.Value != isBeside )
			_isTodoBeside.Value = isBeside;
	}
}
