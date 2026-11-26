using System.Diagnostics;
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
		if ( !isUser )
			return bubble;

		bubble.FlexShrink = 1;
		bubble.MinWidth = 0;
		bubble.MaxWidth = Length.Percent( 85 );
		bubble.Padding = new Edges( Theme.Current.Spacing * 1.25, Theme.Current.Spacing * 1.75 );
		bubble.BackgroundColor = Theme.Current.Raised;
		// A sharp corner toward the sender makes the bubble read as speech.
		bubble.BorderBottomRightRadius = ChatStyle.BubbleTailRadius;

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
			else if ( role == "assistant" && ChatModes.SplitPlan( text ) is { } plan )
			{
				foreach ( var part in BuildPlanText( key, plan ) )
					yield return part;
			}
			else
				yield return BuildStreamText( key, text );
		}
		else if ( type == "thinking" )
			yield return BuildThinking( key, ReadText( block, "thinking" ) );
		else if ( type == "toolCall" )
			yield return BuildTool( key, block );
		else if ( type == "image" )
		{
			var image = BuildImage( key, key.Length >= 32 && Guid.TryParseExact( key.AsSpan( 0, 32 ), "N", out var messageId ) ? messageId : Guid.Empty, block );
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
	/// <param name="followsRun">Whether the fold shows open while its row is the run the agent works on now, such as reasoning.</param>
	private Blob BuildFold( string key, string title, IReadOnlyList<Blob> body, string glyph = "", bool followsRun = false )
	{
		var expanded = IsOpen( key, followsRun );
		var isRunning = followsRun && _isRowRunning;
		var toggle = BuildDisclosure( title, expanded, () => context.Run( () => UpdateOpen( key, isRunning ) ) );
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
	/// <param name="label">The button's text.</param>
	/// <param name="expanded">Whether the content it opens is shown.</param>
	/// <param name="action">Opens or closes the content.</param>
	/// <param name="detail">Monospace text after the label, such as a folder.</param>
	/// <param name="emphasis">Monospace text after the detail in the body color and bold, such as a file name.</param>
	/// <param name="tail">Monospace text after the emphasis, such as a line range.</param>
	/// <param name="note">Body-font text at the end, such as the agent's stated intent.</param>
	private static Button BuildDisclosure( string label, bool expanded, Action action, string detail = "", string emphasis = "", string tail = "", string note = "" )
	{
		// A tool on a file reads as a bold verb, then its path in code type: "Read ~/src/view.cs:10-20".
		var isPath = emphasis.Length > 0;
		// Goo aligns separate text boxes by their edges, not their baselines, so the label and its code-font detail share one text run.
		var spans = new List<TextSpan> { new( label, isPath ? new Style { FontWeight = 600, Color = Theme.Current.Text } : null! ) };
		if ( detail.Length + emphasis.Length + tail.Length > 0 )
			spans.Add( new( "  " + detail, new Style
			{
				FontFamily = ViewStyle.CodeFontFamily,
				FontSize = ViewStyle.SmallSize,
			} ) );
		if ( emphasis.Length > 0 )
			spans.Add( new( emphasis, new Style
			{
				FontFamily = ViewStyle.CodeFontFamily,
				FontSize = ViewStyle.SmallSize,
				FontWeight = 600,
				Color = Theme.Current.Text,
			} ) );
		if ( tail.Length > 0 )
			spans.Add( new( tail, new Style
			{
				FontFamily = ViewStyle.CodeFontFamily,
				FontSize = ViewStyle.SmallSize,
			} ) );
		if ( note.Length > 0 )
			spans.Add( new( "  " + note, null! ) );

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
					// A path line stays clean; its chevron shows only once it is open.
					.. isPath && !expanded ? Array.Empty<Blob>() : [Theme.Current.BuildChevron( expanded )],
				],
			},
		];
		return button;
	}

	/// <summary>Defines how many characters of reasoning show before the rest folds, its last words fading out.</summary>
	private const int ThinkingPreviewChars = 320;

	/// <summary>Defines over how many characters folded reasoning fades out.</summary>
	private const double ThinkingFadeChars = 120;

	/// <summary>Builds reasoning inline in the flow, muted and without a header: long reasoning shows its start fading out until clicked, then all of it.</summary>
	private Blob BuildThinking( string key, string thinking )
	{
		var text = thinking.Trim();
		if ( text.Length == 0 )
			return new Container();
		var isLong = text.Length > ThinkingPreviewChars + ThinkingFadeChars / 2;
		var expanded = !isLong || _expanded.Contains( key );
		Blob body = expanded
			? BuildStreamText( key, text )
			: MarkdownView.Build( text[..ThinkingPreviewChars].TrimEnd() + "…", OpenLink, distance => Math.Clamp( distance / ThinkingFadeChars, 0.05, 1 ) );
		return new Container
		{
			Key = key,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Reasoning",
			Opacity = 0.6,
			FontStyle = FontStyle.Italic,
			Cursor = isLong ? Cursor.Pointer : Cursor.Default,
			OnClick = isLong ? () => context.Run( () => UpdateExpanded( key ) ) : null,
			Children = [body],
		};
	}

	/// <summary>Indents opened disclosure content under its label.</summary>
	private static Container BuildIndent( List<Blob> children ) => new()
	{
		Padding = new Edges( 0, 0, 0, Theme.Current.Spacing * 2 ),
		Gap = Theme.Current.Spacing,
		Children = children,
	};

	/// <summary>Opens a followed link through Lokn's link handlers, beside this conversation when the Browser module takes it; a Ctrl-click opens it
	/// in the system's default app instead: a web page in the browser, a file in the app its type opens with.</summary>
	/// <remarks>A link to a file by relative path, such as <c>src/app.cs</c>, opens that file in the chat's folder.</remarks>
	private void OpenLink( Uri uri )
	{
		var isExternal = MarkdownView.IsOpeningExternally;
		context.Run( () =>
		{
			var resolved = ResolveFileLink( uri );
			// A file link carries its path; a web page goes as it is; anything else stays in Loom.
			var target = !isExternal ? null
				: resolved.Scheme == LoknLinks.Scheme && resolved.Host == "editor" && resolved.Query.StartsWith( "?path=", StringComparison.Ordinal )
					? Uri.UnescapeDataString( resolved.Query[6..] )
				: resolved.Scheme is "http" or "https" ? resolved.AbsoluteUri : null;
			if ( target is null )
			{
				context.App.OpenLink( resolved, context.Id );
				return;
			}
			try
			{
				Process.Start( new ProcessStartInfo( target ) { UseShellExecute = true } )?.Dispose();
			}
			catch ( Exception error ) when ( error is System.ComponentModel.Win32Exception or InvalidOperationException )
			{
				context.App.ReportError( "Open link", error );
			}
		} );
	}

	/// <summary>Resolves the relative path of a <c>lokn://editor/open?path=…</c> link against the chat's folder; any other link stays as it is.</summary>
	private Uri ResolveFileLink( Uri uri )
	{
		if ( uri.Scheme != LoknLinks.Scheme || uri.Host != "editor" || !uri.Query.StartsWith( "?path=", StringComparison.Ordinal ) )
			return uri;
		var path = Uri.UnescapeDataString( uri.Query[6..] );
		return Path.IsPathFullyQualified( path ) ? uri
			: new Uri( $"{LoknLinks.Create( "editor", "open" )}?path={Uri.EscapeDataString( Path.GetFullPath( path, store.GetDirectory( conversationId ) ) )}" );
	}

	/// <summary>Opens or closes one disclosure by its stable key, which starts with its message identifier.</summary>
	private void UpdateExpanded( string key )
	{
		if ( !_expanded.Add( key ) )
			_expanded.Remove( key );
		MarkFoldChanged( key );
	}

	/// <summary>Rebuilds the row of a fold by its key, which starts with its message identifier, or the whole transcript without one.</summary>
	private void MarkFoldChanged( string key )
	{
		if ( key.Length >= 32 && Guid.TryParseExact( key.AsSpan( 0, 32 ), "N", out var messageId ) )
			MarkRowChanged( messageId );
		else
			MarkChanged();
	}

	/// <summary>Reports whether a fold shows open: in the run the agent works on now, a fold that follows it unless the reader closed it; otherwise one the reader opened.</summary>
	private bool IsOpen( string key, bool followsRun ) => followsRun && _isRowRunning ? !_runningClosed.Contains( key ) : _expanded.Contains( key );

	/// <summary>Opens or closes a fold: one of the running run in its own set, so it folds as usual once the run ends; any other as <see cref="UpdateExpanded"/>.</summary>
	private void UpdateOpen( string key, bool isRunning )
	{
		if ( !isRunning )
		{
			UpdateExpanded( key );
			return;
		}

		if ( !_runningClosed.Add( key ) )
			_runningClosed.Remove( key );
		MarkFoldChanged( key );
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
