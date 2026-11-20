using System.Text.Json;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Chat;

/// <summary>Folds a run of reasoning and tool calls into one summary line, such as "Read 2 files, ran 3 commands".</summary>
public sealed partial class ChatContent
{
	/// <summary>Stores the activity folds the reader just opened, until their content is first built and rises into place.</summary>
	private readonly HashSet<string> _openingActivities = new( StringComparer.Ordinal );

	/// <summary>Stores the activity folds the reader just closed, whose content fades out before the fold closes.</summary>
	private readonly HashSet<string> _closingActivities = new( StringComparer.Ordinal );

	/// <summary>Reads the content blocks a part selects, or none when its message has no block array.</summary>
	private static IEnumerable<JsonElement> ReadBlocks( TranscriptPart part )
	{
		if ( part.Message.RichContent is not { ValueKind: JsonValueKind.Object } rich || !rich.TryGetProperty( "content", out var content ) || content.ValueKind != JsonValueKind.Array )
			return [];

		return content.EnumerateArray().Take( part.End ).Skip( part.Start );
	}

	/// <summary>Reports whether a block belongs in an activity run: reasoning, a tool call other than a todo list or a module's card, or blank text that shows nothing.</summary>
	/// <remarks>A todo list is the plan the reader follows, and a module's card is drawn to be seen, so both stay visible rather than folding into the summary.</remarks>
	private bool IsActivityBlock( JsonElement block ) => ReadText( block, "type" ) switch
	{
		"thinking" => true,
		"toolCall" => ReadText( block, "name" ) is not ("todo" or "todo_write")
			&& !context.App.Tools.HasCard( ComputeCardTool( ReadText( block, "name" ), block.TryGetProperty( "arguments", out var arguments ) ? arguments : default ) ),
		"text" => string.IsNullOrWhiteSpace( ReadText( block, "text" ) ),
		_ => false,
	};

	/// <summary>Builds an activity row: a summary disclosure that opens onto every reasoning fold and tool line of the run.</summary>
	private Container BuildActivity( TranscriptRow row )
	{
		var first = row.Parts[0];
		var key = $"{first.Message.Id:N}:activity:{first.Start}";
		var expanded = _expanded.Contains( key );
		// A closing fold still holds its content while it fades out, but its chevron already turns closed.
		var isClosing = _closingActivities.Contains( key );
		var tools = row.Parts.SelectMany( ReadBlocks ).Where( block => ReadText( block, "type" ) == "toolCall" ).ToArray();
		var statuses = tools.Select( tool => ComputeToolStatus( ReadText( tool, "id" ), out _ ) ).ToArray();
		var summary = ComputeActivitySummary( tools );
		var toggle = BuildActivityToggle( summary, expanded && !isClosing, () => context.Run( () => UpdateActivityExpanded( first.Message.Id, key ) ) );
		var failed = statuses.Count( status => status == "Failed" );
		// A running run spins, a failed one is crossed out, and a finished one is quietly checked.
		var head = new List<Blob>
		{
			statuses.Contains( "Running" ) ? ChatIcons.BuildSpinner( "activity-spinner" ) : failed > 0 ? ChatIcons.BuildFailure() : ChatIcons.BuildFinished(),
			toggle,
		};
		if ( failed > 0 )
			head.Add( Cell.Mount<RollingTextInput, RollingText>( "activity-failed", new( $"{failed} failed", ViewStyle.SmallSize, Theme.Current.Error ) { FontFamily = ViewStyle.FontFamily, Shrink = false } ) );

		var children = new List<Blob>
		{
			new Row { Key = "activity-head", AlignItems = AlignItems.Center, Gap = Theme.Current.Spacing, Children = head, },
		};
		if ( expanded )
		{
			var body = new List<Blob>();
			foreach ( var part in row.Parts )
			{
				var index = part.Start;
				foreach ( var block in ReadBlocks( part ) )
					body.AddRange( BuildRichBlock( $"{part.Message.Id:N}:{index++}", block, "assistant" ) );
			}
			// Only a fold the reader just opened rises into place; one already open, restored or scrolled back shows at rest.
			var reveal = Cell.Mount<RevealInput, Reveal>( "activity-reveal", new( BuildIndent( body ), _openingActivities.Remove( key ) ) );
			children.Add( Reveal.BuildExit( "activity-body", reveal, isClosing ) );
		}
		else
		{
			// A closed run still shows the images its tools returned, such as screenshots; opening it shows them under their tools instead.
			foreach ( var tool in tools )
			{
				var id = ReadText( tool, "id" );
				ComputeToolStatus( id, out var result );
				children.AddRange( BuildToolImages( id, result ) );
			}
		}

		return new Container
		{
			Key = key,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = summary,
			Gap = Theme.Current.Spacing,
			Children = children,
		};
	}

	/// <summary>Opens an activity fold so its content rises in, or closes it once its content has faded out; opening a closing fold brings it back.</summary>
	private void UpdateActivityExpanded( Guid messageId, string key )
	{
		if ( !_expanded.Contains( key ) )
		{
			_openingActivities.Add( key );
			UpdateExpanded( key );
			return;
		}
		if ( _closingActivities.Remove( key ) )
		{
			MarkRowChanged( messageId );
			return;
		}

		_closingActivities.Add( key );
		MarkRowChanged( messageId );
		UpdateAfterExit( () =>
		{
			if ( _closingActivities.Remove( key ) )
				UpdateExpanded( key );
		} );
	}

	/// <summary>Builds an activity run's summary disclosure: a muted label that rolls when its counts change, and a chevron that turns as it opens or closes.</summary>
	private static Button BuildActivityToggle( string summary, bool expanded, Action action )
	{
		var chevron = Theme.Current.BuildChevron( false );
		// The closed chevron turned a quarter clockwise is the open one, so the turn can animate between the two.
		chevron.Transform = new PanelTransform { Rotate = expanded ? 90 : 0 };
		chevron.TransitionMs = ChatMotion.BaseMs;
		chevron.TransitionEasing = ChatMotion.Ease;
		chevron.TransitionProperties = [TransitionProperty.Transform];
		chevron.Key = "activity-chevron";
		var button = Theme.Current.BuildButton( summary, action );
		button.Key = "activity-toggle";
		button.AlignSelf = AlignSelf.FlexStart;
		button.MaxWidth = Length.Percent( 100 );
		button.MinWidth = 0;
		button.FlexShrink = 1;
		button.Padding = new Edges( 0, Theme.Current.Spacing / 2 );
		button.Margin = new Edges( 0, -Theme.Current.Spacing / 2 );
		button.FlexDirection = FlexDirection.Row;
		button.JustifyContent = JustifyContent.FlexStart;
		button.Children =
		[
			new Row
			{
				AlignItems = AlignItems.Center,
				Gap = Theme.Current.Spacing,
				MinWidth = 0,
				FlexShrink = 1,
				Children =
				[
					Cell.Mount<RollingTextInput, RollingText>( "activity-summary", new( summary, Theme.Current.FontSize, Theme.Current.Muted ) { FontFamily = ViewStyle.FontFamily } ),
					chevron,
				],
			},
		];
		return button;
	}

	/// <summary>Gets a tool's status, Requested, Running, Done or Failed, and its result or live output when there is one.</summary>
	private string ComputeToolStatus( string id, out JsonElement? result )
	{
		result = _toolResults.GetValueOrDefault( id )?.RichContent;
		var status = result is { } completed ? ComputeResultStatus( completed ) : "Requested";
		// A stored result is final: a live frame without its end, such as one whose end was missed, cannot make the tool run again.
		if ( result is not null || !session.Tools.TryGetValue( id, out var execution ) )
			return status;

		if ( execution.TryGetProperty( "result", out var output ) || execution.TryGetProperty( "partialResult", out output ) || execution.TryGetProperty( "update", out output ) )
			result = output;
		return ReadText( execution, "type" ) == "tool_execution_end" ? ComputeResultStatus( execution ) : "Running";
	}

	/// <summary>Summarizes tool calls by kind in order of first use, such as "Read 2 files, edited ShellView.cs, ran 3 commands".</summary>
	private string ComputeActivitySummary( IReadOnlyList<JsonElement> tools )
	{
		var kinds = new List<string>();
		var calls = new Dictionary<string, int>( StringComparer.Ordinal );
		var files = new Dictionary<string, HashSet<string>>( StringComparer.Ordinal );
		var root = store.GetDirectory( conversationId );
		foreach ( var tool in tools )
		{
			var name = ReadText( tool, "name", "tool" );
			var kind = name switch
			{
				"edit" or "ast_edit" => "edit",
				"grep" or "glob" or "find" or "ast_grep" => "search",
				"fetch" or "web_fetch" => "fetch",
				// Older chats keep the tools' names from before the shell module was renamed from workspace.
				"shell_state" or "shell_list_commands" or "workspace_state" or "workspace_list_commands" => "lokn_look",
				"shell_run_command" or "workspace_run_command" => "lokn_command",
				_ when name.StartsWith( "shell_", StringComparison.Ordinal ) || name.StartsWith( "workspace_", StringComparison.Ordinal ) => "lokn_arrange",
				_ => name,
			};
			if ( !calls.ContainsKey( kind ) )
				kinds.Add( kind );
			calls[kind] = calls.GetValueOrDefault( kind ) + 1;

			var arguments = tool.TryGetProperty( "arguments", out var args ) ? args : default;
			var paths = files.TryGetValue( kind, out var known ) ? known : files[kind] = new( StringComparer.OrdinalIgnoreCase );
			if ( kind is "edit" or "write" )
			{
				ComputeToolStatus( ReadText( tool, "id" ), out var result );
				foreach ( var file in DiffParser.ParseTool( name, arguments, result, root ) )
					paths.Add( file.Path );
			}
			else if ( ReadText( arguments, "path" ) is { Length: > 0 } path )
				paths.Add( path );
		}

		var phrases = kinds.Select( kind =>
		{
			var count = calls[kind];
			var paths = files[kind];
			return kind switch
			{
				"read" => $"read {Count( paths.Count > 0 ? paths.Count : count, "file", "files" )}",
				"edit" or "write" when paths.Count == 1 => $"{(kind == "edit" ? "edited" : "wrote")} {Path.GetFileName( paths.First() )}",
				"edit" or "write" when paths.Count > 1 => $"{(kind == "edit" ? "edited" : "wrote")} {paths.Count} files",
				"edit" => $"made {Count( count, "edit", "edits" )}",
				"write" => $"wrote {Count( count, "file", "files" )}",
				"bash" => $"ran {Count( count, "command", "commands" )}",
				"search" => $"ran {Count( count, "search", "searches" )}",
				"web_search" => count == 1 ? "searched the web" : $"searched the web {count} times",
				"fetch" => $"fetched {Count( count, "page", "pages" )}",
				"task" => $"ran {Count( count, "agent", "agents" )}",
				"eval" => $"ran {Count( count, "script", "scripts" )}",
				"ask" => $"asked {Count( count, "question", "questions" )}",
				"lokn_look" => "looked around Lokn",
				"lokn_arrange" => "arranged panels",
				"lokn_command" => $"ran {Count( count, "Lokn command", "Lokn commands" )}",
				_ => count == 1 ? $"used {kind}" : $"used {kind} {count} times",
			};
		} );
		var summary = string.Join( ", ", phrases );
		return summary.Length == 0 ? "Worked" : char.ToUpperInvariant( summary[0] ) + summary[1..];
	}

	/// <summary>Formats a count with the singular or plural noun.</summary>
	private static string Count( int count, string one, string many ) => $"{count} {(count == 1 ? one : many)}";
}
