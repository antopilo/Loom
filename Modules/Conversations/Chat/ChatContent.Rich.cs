using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Markdown;

namespace Lokn.Modules.Chat;

/// <summary>Renders structured assistant, tool, thinking, task, image, and fallback message blocks.</summary>
public sealed partial class ChatContent
{
	/// <summary>Defines the author label's emphasis weight.</summary>
	private const int AuthorWeight = 600;

	/// <summary>Reports whether <see cref="BuildMessage"/> shows anything, without building it.</summary>
	private static bool HasVisibleContent( ConversationMessage message )
	{
		// Omp's own notices to the model (`<system-notice>` reminders) are custom messages it does not display.
		if ( message.Role == "custom" )
			return message.RichContent is { } custom && ReadText( custom, "display" ) is "True" or "true";
		if ( message.Role is not ("user" or "assistant") )
			return true;
		if ( ReadFailure( message ) is not null )
			return true;
		if ( message.RichContent is not { ValueKind: JsonValueKind.Object } rich || !rich.TryGetProperty( "content", out var content ) )
			return message.Text.Length > 0 || message.RichContent is not null;
		if ( content.ValueKind != JsonValueKind.Array || ReadText( rich, "isError" ) is "True" or "true" || rich.TryGetProperty( "details", out _ ) )
			return true;

		foreach ( var block in content.EnumerateArray() )
		{
			if ( ReadText( block, "type" ) != "text" || !string.IsNullOrWhiteSpace( ReadText( block, "text" ) ) )
				return true;
		}
		return false;
	}

	/// <summary>Builds one message's selected blocks with their rich content, or nothing when they show nothing.</summary>
	private Blob? BuildMessage( TranscriptPart part )
	{
		var message = part.Message;
		var body = new List<Blob>();
		var title = message.Role switch
		{
			"user" => "You",
			"assistant" => "Omp",
			"toolResult" => message.RichContent is { } result ? ReadText( result, "toolName", "Tool result" ) : "Tool result",
			CommandRole => message.RichContent is { } sent ? ReadText( sent, "command", "Command" ) : "Command",
			"custom" => "Notice",
			_ => message.Role,
		};
		if ( message.Role is not ("user" or "assistant" or "custom") )
			body.Add( new Text { Content = title, FontWeight = AuthorWeight, Color = Theme.Current.Muted, } );
		if ( message.Role == "custom" && message.RichContent is { ValueKind: JsonValueKind.Object } custom )
			body.AddRange( BuildNotice( message.Id.ToString( "N" ), custom ) );
		else if ( message.RichContent is { ValueKind: JsonValueKind.Object } rich && rich.TryGetProperty( "content", out var content ) )
		{
			if ( content.ValueKind == JsonValueKind.Array )
			{
				var index = 0;
				foreach ( var block in content.EnumerateArray() )
				{
					var key = message.Id.ToString( "N" ) + ":" + index;
					if ( index++ < part.Start || index > part.End )
						continue;
					if ( message.Role == "user" && ReadText( block, "type" ) == "text" )
						body.AddRange( BuildSentText( message.Id, ReadText( block, "text" ) ) );
					else
						body.AddRange( BuildRichBlock( key, block, message.Role ) );
				}
			}
			else if ( message.Role == "user" )
				body.AddRange( BuildSentText( message.Id, content.ToString() ) );
			else
				body.Add( BuildStreamText( message.Id.ToString( "N" ), content.ToString() ) );
			// Extras belong to the whole message, so only its last run shows them.
			if ( part.End == int.MaxValue && ReadText( rich, "isError" ) is "True" or "true" )
				body.Add( BuildText( "Tool failed", muted: true ) );
			if ( part.End == int.MaxValue && rich.TryGetProperty( "details", out var details ) )
				body.AddRange( BuildDetails( message.Id.ToString( "N" ), details ) );
		}
		else if ( message.Role == CommandRole )
			body.Add( BuildEntering( message.Id.ToString( "N" ), message.Text.Contains( "```", StringComparison.Ordinal ) ? MarkdownView.Build( message.Text, OpenLink ) : BuildText( message.Text ) ) );
		else if ( message.Text.Length > 0 && message.Role == "user" )
			body.AddRange( BuildSentText( message.Id, message.Text ) );
		else if ( message.Text.Length > 0 )
			body.Add( BuildStreamText( message.Id.ToString( "N" ), message.Text ) );
		else if ( message.RichContent is { } unknown )
			body.Add( BuildFold( message.Id.ToString( "N" ), "Message details", [MarkdownView.BuildCode( BuildJson( unknown ), "json" )] ) );

		// A reply the provider failed often has no content, so the reason is all the user sees of it.
		if ( part.End == int.MaxValue && ReadFailure( message ) is { } failure )
		{
			var notice = BuildText( failure );
			notice.Color = Theme.Current.Error;
			body.Add( notice );
		}

		if ( body.Count == 0 )
			return null;
		var isUser = message.Role == "user";
		var menu = BuildMessageMenu( part, title );
		if ( menu is not null )
			body.Add( menu );
		var bubble = new Container
		{
			Key = message.Id.ToString( "N" ),
			FlexShrink = 0,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = title + " message",
			AlignSelf = AlignSelf.Stretch,
			MaxWidth = Length.Percent( 100 ),
			Gap = Theme.Current.Spacing,
			BorderRadius = ChatStyle.BubbleRadius,
			OnPointerDown = BuildMessageMenuOpener( part ),
			Children = body,
		};
		// Only the bubble whose menu is open carries the menu's anchor.
		if ( menu is not null )
			bubble.Handle = _messageMenuHandle;
		if ( !isUser )
			return bubble;

		bubble.FlexShrink = 1;
		bubble.MinWidth = 0;
		bubble.MaxWidth = Length.Percent( 85 );
		bubble.Padding = new Edges( Theme.Current.Spacing * 1.25, Theme.Current.Spacing * 1.75 );
		bubble.BackgroundColor = Theme.Current.Raised;

		// The user's turns sit in a right-aligned bubble. A row sizes it: a content-sized column child
		// measures its height as if long unbroken words did not wrap, which lets a long path spill out of the bubble.
		return new Row
		{
			Key = "user-bubble",
			JustifyContent = JustifyContent.FlexEnd,
			Children = [bubble],
		};
	}

	/// <summary>Builds each supported Omp content kind and preserves unfamiliar payloads as readable JSON.</summary>
	/// <remarks>Text and reasoning of the streaming reply reveal word by word; tool output fades in when it joins a row on screen.</remarks>
	private IEnumerable<Blob> BuildRichBlock( string key, JsonElement block, string role )
	{
		var type = ReadText( block, "type" );
		if ( type == "text" )
		{
			var text = ReadText( block, "text" );
			if ( string.IsNullOrWhiteSpace( text ) )
				yield break;
			if ( role == "toolResult" )
				yield return BuildEntering( key, MarkdownView.BuildCode( TerminalText.BuildPlain( text ), text.StartsWith( "diff ", StringComparison.Ordinal ) || text.StartsWith( "@@", StringComparison.Ordinal ) ? "diff" : "text" ) );
			else
				yield return BuildStreamText( key, text );
		}
		else if ( type == "thinking" )
		{
			// A closed fold never shows its body, so only an open one takes part in the reveal.
			var thinking = ReadText( block, "thinking" );
			yield return BuildFold( key, "Reasoning", [_expanded.Contains( key ) ? BuildStreamText( key, thinking ) : MarkdownView.Build( thinking, OpenLink )], ChatIcons.ThinkingGlyph );
		}
		else if ( type == "toolCall" )
			yield return BuildTool( key, block );
		else if ( type == "image" )
		{
			var image = BuildImage( key, block );
			yield return image ?? BuildText( "Image attachment · " + ReadText( block, "mimeType", "unsupported image" ), muted: true );
		}
		else if ( type is "checklist" or "todo" )
		{
			foreach ( var item in BuildTasks( block ) )
				yield return item;
		}
		else
			yield return BuildFold( key, type.Length > 0 ? type : "Rich content", [MarkdownView.BuildCode( BuildJson( block ), "json" )] );
	}

	/// <summary>Builds optional tool details such as a diff or task summary; a diff already shown as cards can be left out.</summary>
	private IEnumerable<Blob> BuildDetails( string key, JsonElement details, bool includeDiff = true )
	{
		if ( details.ValueKind != JsonValueKind.Object )
			yield break;
		if ( includeDiff && details.TryGetProperty( "diff", out var diff ) )
			yield return MarkdownView.BuildCode( diff.ValueKind == JsonValueKind.String ? diff.GetString() ?? "" : BuildJson( diff ), "diff" );
		if ( details.TryGetProperty( "phases", out _ ) || details.TryGetProperty( "todos", out _ ) || details.TryGetProperty( "tasks", out _ ) )
		{
			foreach ( var task in BuildTasks( details ) )
				yield return task;
		}
		else if ( !details.TryGetProperty( "diff", out _ ) && details.EnumerateObject().Any() )
			yield return BuildFold( key + ":details", "Details", [MarkdownView.BuildCode( BuildJson( details ), "json" )] );
	}

	/// <summary>Builds a todo card from any structured task payload: phases, a flat task list, or an init list.</summary>
	private static IEnumerable<Blob> BuildTasks( JsonElement value ) => BuildTodo( ComputeTodo( value ) );

	/// <summary>Builds a muted, collapsible line, optionally led by an icon, whose content stays available on demand.</summary>
	private Blob BuildFold( string key, string title, IReadOnlyList<Blob> body, string glyph = "" )
	{
		var expanded = _expanded.Contains( key );
		var toggle = BuildDisclosure( title, expanded, () => context.Run( () => UpdateExpanded( key ) ) );
		var children = new List<Blob>
		{
			glyph.Length == 0 ? toggle : new Row { AlignItems = AlignItems.Center, Gap = Theme.Current.Spacing, Children = [ChatIcons.BuildIcon( glyph ), toggle] },
		};
		if ( expanded )
			children.Add( BuildIndent( [.. body] ) );
		return new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = title,
			Gap = Theme.Current.Spacing,
			Children = children,
		};
	}

	/// <summary>Builds a muted text button with an optional monospace detail, followed by a chevron that shows whether its content is open.</summary>
	private static Button BuildDisclosure( string label, bool expanded, Action action, string detail = "" )
	{
		// Goo aligns separate text boxes by their edges, not their baselines, so the label and its code-font detail share one text run.
		var spans = new List<TextSpan> { new( label, null! ) };
		if ( detail.Length > 0 )
			spans.Add( new( "  " + detail, new Style
			{
				FontFamily = ViewStyle.CodeFontFamily,
				FontSize = ViewStyle.SmallSize,
			} ) );

		var button = Theme.Current.BuildButton( label, action );
		button.AccessibilityName = label;
		button.AlignSelf = AlignSelf.FlexStart;
		button.MaxWidth = Length.Percent( 100 );
		button.MinWidth = 0;
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
					new Text( [.. spans] )
					{
						Color = Theme.Current.Muted,
						FlexShrink = 1,
						MinWidth = 0,
						TextWrap = TextWrap.NoWrap,
						TextTrimming = TextTrimming.Ellipsis,
					},
					Theme.Current.BuildChevron( expanded ),
				],
			},
		];
		return button;
	}

	/// <summary>Indents opened disclosure content under its label.</summary>
	private static Container BuildIndent( List<Blob> children ) => new()
	{
		Padding = new Edges( 0, 0, 0, Theme.Current.Spacing * 2 ),
		Gap = Theme.Current.Spacing,
		Children = children,
	};

	/// <summary>Opens a followed link through Lokn's link handlers, beside this conversation when the Browser module takes it.</summary>
	private void OpenLink( Uri uri ) => context.Run( () => context.App.OpenLink( uri, context.Id ) );

	/// <summary>Opens or closes one disclosure by its stable key, which starts with its message identifier.</summary>
	private void UpdateExpanded( string key )
	{
		if ( !_expanded.Add( key ) )
			_expanded.Remove( key );
		if ( key.Length >= 32 && Guid.TryParseExact( key.AsSpan( 0, 32 ), "N", out var messageId ) )
			MarkRowChanged( messageId );
		else
			MarkChanged();
	}

	/// <summary>Computes the final success or error label for a recorded tool result.</summary>
	private static string ComputeResultStatus( JsonElement result ) => ReadText( result, "isError" ) is "True" or "true" ? "Failed" : "Done";

	/// <summary>Extracts structured tool-result text while retaining unfamiliar values in JSON form.</summary>
	private static string ComputeResultText( JsonElement result ) => result.ValueKind == JsonValueKind.Object && result.TryGetProperty( "content", out _ )
		? ComputeMessageText( result ) : result.ValueKind == JsonValueKind.String ? result.GetString() ?? "" : BuildJson( result );

	/// <summary>Formats structured payloads with readable indentation.</summary>
	private static string BuildJson( JsonElement value ) => JsonSerializer.Serialize( value, new JsonSerializerOptions
	{
		WriteIndented = true,
	} );
}
