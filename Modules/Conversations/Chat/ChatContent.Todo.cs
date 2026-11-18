using System.Text.Json;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Chat;

/// <summary>Describes one phase of an agent's todo list.</summary>
/// <param name="Name">The phase name, or empty for a list without phases.</param>
/// <param name="Tasks">The phase's tasks in order.</param>
internal sealed record TodoPhase( string Name, IReadOnlyList<TodoTask> Tasks );

/// <summary>Describes one task of an agent's todo list.</summary>
/// <param name="Content">What the task is.</param>
/// <param name="State">How far the task has got.</param>
internal readonly record struct TodoTask( string Content, TaskState State );

/// <summary>Shows an agent's todo list as a card: a progress ring per phase and a status mark per task.</summary>
public sealed partial class ChatContent
{
	/// <summary>Defines the emphasis weight of the card title, phase names and the active task.</summary>
	private const int TodoEmphasisWeight = 600;

	/// <summary>Defines the regular weight of other tasks.</summary>
	private const int TodoRegularWeight = 400;

	/// <summary>Builds a todo tool call as one line, such as "Done: Review  3/5"; its full list opens on demand.</summary>
	/// <remarks>The current list stays pinned beside the conversation or above the prompt, so each update repeating it would only bloat the transcript.</remarks>
	private Blob BuildTodo( string key, string status, JsonElement arguments, JsonElement? result )
	{
		var phases = ComputeTodo( arguments, result );
		var tasks = phases.SelectMany( phase => phase.Tasks ).ToArray();
		var expanded = _expanded.Contains( key );
		var toggle = BuildDisclosure( ComputeTodoChange( status, arguments ), expanded, () => context.Run( () => UpdateExpanded( key ) ),
			tasks.Length > 0 ? ComputeTodoCount( tasks ) : "" );
		toggle.FlexShrink = 1;
		var mark = status switch
		{
			"Running" => ChatIcons.BuildSpinner( "todo-spinner" ),
			"Failed" => ChatIcons.BuildIcon( ChatIcons.TodoGlyph, Theme.Current.Error ),
			_ => ChatIcons.BuildIcon( ChatIcons.TodoGlyph ),
		};
		var children = new List<Blob>
		{
			new Row { Key = "todo-line", AlignItems = AlignItems.Center, Gap = Theme.Current.Spacing, Children = [mark, toggle] },
		};
		if ( status == "Failed" )
			children.Add( BuildText( result is { } failure ? TerminalText.BuildPlain( ComputeResultText( failure ) ) : "The todo update failed.", muted: true ) );
		if ( expanded && BuildTodo( phases ) is { Count: > 0 } card )
			children.Add( BuildIndent( card ) );
		return new Container
		{
			Key = key,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Todo",
			Gap = Theme.Current.Spacing,
			Children = children,
		};
	}

	/// <summary>Describes what one todo call changed from its operation and tasks, such as "Planned 5 tasks" or "Done: Review".</summary>
	private static string ComputeTodoChange( string status, JsonElement arguments )
	{
		var task = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty( "task", out var named )
			? named.ValueKind == JsonValueKind.Array ? string.Join( ", ", named.EnumerateArray().Select( item => item.GetString() ) ) : ReadText( arguments, "task" )
			: "";
		var count = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty( "items", out var items ) && items.ValueKind == JsonValueKind.Array
			? items.GetArrayLength()
			: ComputeTodo( arguments ).Sum( phase => phase.Tasks.Count );
		return ReadText( arguments, "op" ) switch
		{
			"init" when count > 0 => $"Planned {Count( count, "task", "tasks" )}",
			"append" when count > 0 => $"Added {Count( count, "task", "tasks" )}",
			"done" when task.Length > 0 => "Done: " + task,
			"start" when task.Length > 0 => "Started: " + task,
			"drop" or "rm" when task.Length > 0 => "Dropped: " + task,
			_ => status == "Running" ? "Updating tasks…" : "Updated tasks",
		};
	}

	/// <summary>Reads a todo call's list: the result's phases, or the list the call itself set up.</summary>
	private static IReadOnlyList<TodoPhase> ComputeTodo( JsonElement arguments, JsonElement? result )
	{
		var phases = ComputeTodo( result is { } done && done.ValueKind == JsonValueKind.Object && done.TryGetProperty( "details", out var details ) ? details : result ?? default );
		return phases.Count > 0 ? phases : ComputeTodo( arguments );
	}

	/// <summary>Builds the card title with its overall count, then each phase; finished phases fold to their header.</summary>
	private static List<Blob> BuildTodo( IReadOnlyList<TodoPhase> phases )
	{
		var children = new List<Blob>();
		var tasks = phases.SelectMany( phase => phase.Tasks ).ToArray();
		if ( tasks.Length == 0 )
			return children;

		children.Add( new Row
		{
			Key = "todo-head",
			AlignItems = AlignItems.Center,
			Gap = Theme.Current.Spacing,
			Children =
			[
				ChatIcons.BuildIcon( ChatIcons.TodoGlyph ),
				new Text { Content = "Todo", FontWeight = TodoEmphasisWeight, FontSize = Theme.Current.FontSize, Color = Theme.Current.Text },
				BuildTodoCount( "todo-count", tasks ),
			],
		} );
		foreach ( var (index, phase) in phases.Index() )
		{
			var isFinished = phase.Tasks.All( task => task.State is TaskState.Done or TaskState.Dropped );
			if ( phase.Name.Length > 0 )
				children.Add( BuildTodoPhase( index, phase, isFinished ) );
			if ( isFinished && phase.Name.Length > 0 )
				continue;

			children.Add( new Container
			{
				Key = "todo-tasks:" + index,
				Gap = Theme.Current.Spacing / 2,
				// Tasks hang under their phase name, past the phase's ring.
				Padding = new Edges( 0, 0, 0, phase.Name.Length > 0 ? ChatIcons.MarkSize + Theme.Current.Spacing : 0 ),
				Children = [.. phase.Tasks.Select( BuildTodoTask )],
			} );
		}

		return children;
	}

	/// <summary>Builds a phase header: its progress ring, its name and its count.</summary>
	private static Row BuildTodoPhase( int index, TodoPhase phase, bool isFinished ) => new()
	{
		Key = "todo-phase:" + index,
		AlignItems = AlignItems.Center,
		Gap = Theme.Current.Spacing,
		Children =
		[
			ChatIcons.BuildProgress( phase.Tasks.Count == 0 ? 1 : (double)phase.Tasks.Count( task => task.State == TaskState.Done ) / phase.Tasks.Count ),
			new Text
			{
				Content = phase.Name,
				FontWeight = TodoEmphasisWeight,
				Color = isFinished ? Theme.Current.Muted : Theme.Current.Text,
				FlexGrow = 1,
				FlexShrink = 1,
				MinWidth = 0,
			},
			BuildTodoCount( "todo-phase-count", phase.Tasks ),
		],
	};

	/// <summary>Builds one task: its status mark beside its text, emphasized while active and struck through once settled.</summary>
	private static Row BuildTodoTask( TodoTask task )
	{
		var mark = ChatIcons.BuildTaskMark( task.State );
		// The mark centers on the first line of text, which may wrap.
		mark.Margin = new Edges( (ChatStyle.BodySize * ChatStyle.BodyLineHeight - ChatIcons.MarkSize) / 2, 0, 0, 0 );
		var isSettled = task.State is TaskState.Done or TaskState.Dropped;
		return new Row
		{
			AlignItems = AlignItems.FlexStart,
			Gap = Theme.Current.Spacing,
			AccessibilityName = $"{task.Content}, {task.State switch
			{
				TaskState.Active => "in progress",
				TaskState.Done => "done",
				TaskState.Dropped => "dropped",
				_ => "pending",
			}}",
			Children =
			[
				mark,
				new Text
				{
					Content = task.Content,
					Selectable = true,
					FontSize = ChatStyle.BodySize,
					LineHeight = ChatStyle.BodyLineHeight,
					FontWeight = task.State == TaskState.Active ? TodoEmphasisWeight : TodoRegularWeight,
					Color = isSettled ? Theme.Current.Muted : Theme.Current.Text,
					TextDecoration = isSettled ? TextDecoration.LineThrough : TextDecoration.None,
					FlexShrink = 1,
					MinWidth = 0,
				},
			],
		};
	}

	/// <summary>Builds a finished-over-total count that rolls up as tasks finish.</summary>
	private static Blob BuildTodoCount( string key, IEnumerable<TodoTask> tasks ) =>
		Cell.Mount<RollingTextInput, RollingText>( key, new( ComputeTodoCount( tasks ), ViewStyle.SmallSize, Theme.Current.Muted ) { FontFamily = ViewStyle.FontFamily, Shrink = false } );

	/// <summary>Formats finished over total tasks, such as "2/5".</summary>
	private static string ComputeTodoCount( IEnumerable<TodoTask> tasks )
	{
		var all = tasks.ToArray();
		return $"{all.Count( task => task.State == TaskState.Done )}/{all.Length}";
	}

	/// <summary>Reads a todo list from Omp's result details (<c>phases</c> of <c>tasks</c>), an <c>init</c> call's <c>list</c>, or a flat task array.</summary>
	internal static IReadOnlyList<TodoPhase> ComputeTodo( JsonElement value )
	{
		if ( value.ValueKind == JsonValueKind.Object && value.TryGetProperty( "phases", out var phases ) && phases.ValueKind == JsonValueKind.Array )
			return [.. phases.EnumerateArray().Select( phase => new TodoPhase( ReadText( phase, "name" ), ComputeTodoTasks( phase ) ) )];

		// An init call lists each phase's items as plain text before any result reports their state.
		if ( value.ValueKind == JsonValueKind.Object && value.TryGetProperty( "list", out var list ) && list.ValueKind == JsonValueKind.Array )
		{
			return [.. list.EnumerateArray().Select( phase => new TodoPhase( ReadText( phase, "phase" ),
				phase.TryGetProperty( "items", out var items ) && items.ValueKind == JsonValueKind.Array
					? [.. items.EnumerateArray().Select( item => new TodoTask( ReadText( item, "content", item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "" ), TaskState.Pending ) )]
					: [] ) )];
		}

		var tasks = ComputeTodoTasks( value );
		return tasks.Count == 0 ? [] : [new TodoPhase( "", tasks )];
	}

	/// <summary>Reads the tasks of one phase, or of a flat list under <c>tasks</c>, <c>todos</c> or <c>items</c>.</summary>
	private static IReadOnlyList<TodoTask> ComputeTodoTasks( JsonElement value )
	{
		var tasks = value;
		if ( value.ValueKind == JsonValueKind.Object && !value.TryGetProperty( "tasks", out tasks ) && !value.TryGetProperty( "todos", out tasks ) && !value.TryGetProperty( "items", out tasks ) )
			return [];
		if ( tasks.ValueKind != JsonValueKind.Array )
			return [];

		return [.. tasks.EnumerateArray()
			.Where( task => task.ValueKind == JsonValueKind.Object )
			.Select( task => new TodoTask( ReadText( task, "content", ReadText( task, "text", ReadText( task, "title" ) ) ), ReadText( task, "status" ) switch
			{
				"completed" or "done" => TaskState.Done,
				"in_progress" or "running" or "active" => TaskState.Active,
				"abandoned" or "cancelled" or "canceled" or "dropped" or "skipped" => TaskState.Dropped,
				_ => TaskState.Pending,
			} ) )];
	}
}
