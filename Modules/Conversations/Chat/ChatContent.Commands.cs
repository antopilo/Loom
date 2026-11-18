using System.Text.Json;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Chat;

/// <summary>Describes one command Omp runs when a prompt starts with a slash.</summary>
/// <param name="Name">The command name without its slash.</param>
/// <param name="Description">The description Omp gives, or empty.</param>
/// <param name="Hint">The argument hint, such as <c>[on|off]</c>, or empty when the command takes none.</param>
/// <param name="Aliases">Other names that run the same command.</param>
internal sealed record SlashCommand( string Name, string Description, string Hint, string[] Aliases )
{
	/// <summary>Gets whether the command needs an argument, which Omp marks with an angle-bracketed hint.</summary>
	internal bool NeedsArgument => Hint.StartsWith( '<' );
}

/// <summary>Lists Omp's slash commands above the prompt while a command name is typed.</summary>
public sealed partial class ChatContent
{
	/// <summary>Defines how many command rows show before the list scrolls.</summary>
	private const int VisibleCommands = 8;

	/// <summary>Defines the longest prompt read as a command name, so a long draft is never copied per keystroke.</summary>
	private const int MaxCommandQueryLength = 64;

	/// <summary>Defines the height of one command row in logical pixels.</summary>
	private static int CommandRowHeight => Theme.Current.ControlHeight;

	/// <summary>Targets the command list to keep the keyboard selection in view.</summary>
	private readonly ElementHandle _commandList = new();

	/// <summary>Holds the commands the connected Omp offers, in its order.</summary>
	private SlashCommand[] _commands = [];

	/// <summary>Defines Lokn's own command that empties the conversation; Lokn runs it, not Omp.</summary>
	internal static readonly SlashCommand ClearCommand = new( "clear", "Clear this conversation and start a fresh session", "", [] );

	/// <summary>Gets Lokn's commands followed by Omp's; an Omp command of the same name gives way to Lokn's.</summary>
	private SlashCommand[] AllCommands => [ClearCommand, .. _commands.Where( command => command.Name != ClearCommand.Name )];

	/// <summary>Stores the command name typed so far, or null when the list is closed.</summary>
	private string? _commandQuery;

	/// <summary>Stores the prompt text whose list Escape or a run closed; the list stays closed until the text changes.</summary>
	private string? _dismissedCommandText;

	/// <summary>Stores the keyboard-selected index among the matching commands.</summary>
	private int _selectedCommand;

	/// <summary>Defines the transcript role of a local command's output, which Lokn shows but Omp keeps out of its session.</summary>
	internal const string CommandRole = "command";

	/// <summary>Stores the last slash command sent that did not start a turn, so output Omp prints for it, even long after admitting it as <c>/compact</c> does, shows under its name.</summary>
	private string? _sentCommand;

	/// <summary>Gets whether a <c>/compact</c> the user ran is still summarizing the session.</summary>
	private bool _isCompacting;

	/// <summary>Reads Omp's command catalog from an <c>available_commands_update</c> frame or a <c>get_available_commands</c> result.</summary>
	internal static SlashCommand[] ReadCommands( JsonElement data )
	{
		if ( data.ValueKind != JsonValueKind.Object || !data.TryGetProperty( "commands", out var list ) || list.ValueKind != JsonValueKind.Array )
			return [];

		var commands = new List<SlashCommand>( list.GetArrayLength() );
		foreach ( var command in list.EnumerateArray() )
		{
			var name = ReadText( command, "name" );
			if ( name.Length == 0 )
				continue;

			var hint = command.TryGetProperty( "input", out var input ) ? ReadText( input, "hint" ) : "";
			string[] aliases = command.TryGetProperty( "aliases", out var names ) && names.ValueKind == JsonValueKind.Array
				? [.. names.EnumerateArray().Where( alias => alias.ValueKind == JsonValueKind.String ).Select( alias => alias.GetString()! )]
				: [];
			commands.Add( new( name, ReadText( command, "description" ), hint, aliases ) );
		}
		return [.. commands];
	}

	/// <summary>Returns the command name being typed: the text after a leading slash, before any whitespace; otherwise null.</summary>
	internal static string? ComputeCommandQuery( string text ) =>
		text.StartsWith( '/' ) && !text.AsSpan( 1 ).ContainsAny( " \t\r\n" ) ? text[1..] : null;

	/// <summary>Lists the commands whose name or alias starts with the query, then those whose name contains it, each in Omp's order.</summary>
	internal static List<SlashCommand> ComputeCommandMatches( IReadOnlyList<SlashCommand> commands, string query )
	{
		bool Starts( SlashCommand command ) => command.Name.StartsWith( query, StringComparison.OrdinalIgnoreCase )
			|| command.Aliases.Any( alias => alias.StartsWith( query, StringComparison.OrdinalIgnoreCase ) );
		return [.. commands.Where( Starts ), .. commands.Where( command => !Starts( command ) && command.Name.Contains( query, StringComparison.OrdinalIgnoreCase ) )];
	}

	/// <summary>Shows what a local command printed, such as <c>/session</c> or <c>/usage</c>, under the command that asked for it.</summary>
	/// <remarks>
	/// Omp sends it as a <c>command_output</c> frame, before admitting the prompt or, for background work such as <c>/compact</c>,
	/// after. The frame names no command, so output with no command sent stays hidden.
	/// </remarks>
	private void UpdateCommandOutput( JsonElement frame )
	{
		var text = TerminalText.BuildPlain( ReadText( frame, "text" ) ).Trim();
		if ( _sentCommand is not { } command || text.Length == 0 )
			return;

		var rich = JsonSerializer.SerializeToElement( new
		{
			command
		} );
		store.AppendMessage( conversationId, new( Guid.NewGuid(), CommandRole, text, DateTimeOffset.UtcNow, rich ), persist: false );
		_transcript.ScrollToEnd();
	}

	/// <summary>Reads the open command query from the prompt, resetting the selection when it changes; returns whether it changed.</summary>
	private bool UpdateCommandQuery()
	{
		var document = _composer.Document;
		var text = document.Length <= MaxCommandQueryLength ? document.GetText() : null;
		if ( text != _dismissedCommandText )
			_dismissedCommandText = null;
		var query = text is null || _dismissedCommandText is not null ? null : ComputeCommandQuery( text );
		if ( query == _commandQuery )
			return false;

		_commandQuery = query;
		_selectedCommand = 0;
		_commandList.ScrollTo( null, 0 );
		return true;
	}

	/// <summary>Opens, filters, or closes the command list as the prompt changes, connecting so Omp can list its commands.</summary>
	private void UpdatePromptChange()
	{
		if ( UpdateCommandQuery() )
		{
			if ( _commandQuery is not null && _commands.Length == 0 )
				UpdateConnect();
			MarkChanged();
		}
		UpdatePromptLines();
	}

	/// <summary>Builds the card of matching commands, scrolling past <see cref="VisibleCommands"/> rows, with a key legend or loading state.</summary>
	/// <remarks>The card mounts only while the list is open, so it rises in each time the list opens and stays put while it filters.</remarks>
	private Blob BuildCommands( string query )
	{
		var matches = ComputeCommandMatches( AllCommands, query );
		_selectedCommand = Math.Clamp( _selectedCommand, 0, Math.Max( 0, matches.Count - 1 ) );
		var children = new List<Blob>();
		if ( matches.Count > 0 )
		{
			var rows = new List<Blob>( matches.Count );
			for ( var index = 0; index < matches.Count; index++ )
				rows.Add( BuildCommandRow( matches[index], index == _selectedCommand ) );
			children.Add( new Container
			{
				Key = "conversation-commands-list",
				Handle = _commandList,
				Height = Math.Min( matches.Count, VisibleCommands ) * CommandRowHeight,
				FlexShrink = 0,
				OverflowY = Overflow.Scroll,
				ScrollbarY = ViewStyle.BuildScrollbar(),
				Children = rows,
			} );
		}

		var status = _commands.Length == 0
			? _connection is null ? "Connect to list Omp's commands." : "Loading Omp's commands…"
			: matches.Count == 0 ? $"No command matches /{query}."
			: $"{matches.Count} of {_commands.Length} commands   ·   ↑ ↓ select   ·   Tab complete   ·   Enter run   ·   Esc close";
		children.Add( new Text
		{
			Key = "conversation-commands-status",
			Content = status,
			Padding = new Edges( Theme.Current.Spacing / 2, Theme.Current.Spacing ),
			FontSize = ViewStyle.SmallSize,
			Color = Theme.Current.Muted,
			TextWrap = TextWrap.NoWrap,
			TextTrimming = TextTrimming.Ellipsis,
			MinWidth = 0,
		} );
		var card = new Container
		{
			Key = "conversation-commands",
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Slash commands",
			Padding = Theme.Current.Spacing / 2,
			BorderColor = Theme.Current.Border,
			BorderWidth = Theme.Current.BorderWidth,
			BorderRadius = ChatStyle.BubbleRadius,
			BackgroundColor = Theme.Current.Raised,
			Children = children,
		};
		return Cell.Mount<RevealInput, Reveal>( "conversation-commands-reveal", new( card, true ) );
	}

	/// <summary>Builds one command row: its name and argument hint, then its description, highlighted when selected.</summary>
	/// <remarks>The highlight fades between rows as the selection moves, so it reads as one highlight sliding along the list.</remarks>
	private Button BuildCommandRow( SlashCommand command, bool isSelected ) => new()
	{
		Key = "command:" + command.Name,
		Height = CommandRowHeight,
		FlexShrink = 0,
		FlexDirection = FlexDirection.Row,
		AlignItems = AlignItems.Center,
		Gap = Theme.Current.Spacing,
		Padding = new Edges( 0, Theme.Current.Spacing ),
		BorderRadius = Theme.Current.Radius,
		BackgroundColor = isSelected ? Theme.Current.Hover : Theme.Current.Raised,
		TransitionMs = ChatMotion.FastMs,
		TransitionEasing = ChatMotion.Ease,
		TransitionProperties = [TransitionProperty.BackgroundColor],
		Color = Theme.Current.Text,
		AccessibilityName = "/" + command.Name,
		OnClick = () => context.Run( () => UpdateCommandAccept( command, run: true ) ),
		HoverStyle = new Style
		{
			BackgroundColor = Theme.Current.Hover,
		},
		Children =
		[
			new Text
			{
				Content = "/" + command.Name,
				FlexShrink = 0,
				TextWrap = TextWrap.NoWrap,
			},
			new Text
			{
				Content = command.Hint,
				FlexShrink = 1,
				MinWidth = 0,
				MaxWidth = Length.Percent( 30 ),
				FontSize = ViewStyle.SmallSize,
				Color = Theme.Current.Muted,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
			new Text
			{
				Content = command.Description.Split( '\n' )[0],
				FlexGrow = 1,
				FlexShrink = 1,
				MinWidth = 0,
				FontSize = ViewStyle.SmallSize,
				Color = Theme.Current.Muted,
				TextAlign = TextAlign.End,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
		],
	};

	/// <summary>Moves the keyboard selection through the matches, wrapping at either end, and scrolls it into view.</summary>
	private void UpdateCommandSelection( int step )
	{
		if ( _commandQuery is not { } query )
			return;

		var count = ComputeCommandMatches( AllCommands, query ).Count;
		if ( count == 0 )
			return;

		_selectedCommand = (Math.Clamp( _selectedCommand, 0, count - 1 ) + step + count) % count;
		var top = _selectedCommand * CommandRowHeight;
		var viewport = Math.Min( count, VisibleCommands ) * CommandRowHeight;
		var offset = _commandList.ScrollOffset.Y;
		if ( top < offset )
			_commandList.ScrollTo( null, top );
		else if ( top + CommandRowHeight > offset + viewport )
			_commandList.ScrollTo( null, top + CommandRowHeight - viewport );
		MarkChanged();
	}

	/// <summary>Accepts the selected match; returns false when nothing matches, so the prompt is sent as typed.</summary>
	private bool UpdateCommandSelected( bool run )
	{
		if ( _commandQuery is not { } query )
			return false;

		var matches = ComputeCommandMatches( AllCommands, query );
		if ( matches.Count == 0 )
			return false;

		UpdateCommandAccept( matches[Math.Clamp( _selectedCommand, 0, matches.Count - 1 )], run );
		return true;
	}

	/// <summary>Writes the command into the prompt and runs it, or leaves it followed by a space for its arguments when it needs some or only completion was asked.</summary>
	private void UpdateCommandAccept( SlashCommand command, bool run )
	{
		var text = "/" + command.Name;
		run &= !command.NeedsArgument;
		_composer.Selection = new( new( 0, TextAffinity.Downstream ), new( _composer.Document.Length, TextAffinity.Downstream ) );
		_composer.Execute( new TextCommand( TextCommandKind.Insert, run ? text : text + " ", false ) );
		_composer.Focus();
		if ( run )
		{
			// The list stays closed while the command is sent, instead of reopening on the unchanged prompt.
			_dismissedCommandText = text;
			UpdateSend();
		}
		UpdateCommandQuery();
		MarkChanged();
	}

	/// <summary>Closes the list until the prompt changes.</summary>
	private void UpdateCommandDismiss()
	{
		_dismissedCommandText = _composer.Document.GetText();
		UpdateCommandQuery();
		MarkChanged();
	}
}
