using System.Text;
using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Markdown;

namespace Lokn.Modules.Chat;

/// <summary>Offers a right-click menu on each message bubble that copies its selection, text, Markdown, or code.</summary>
public sealed partial class ChatContent
{
	/// <summary>Defines the message menu's width in logical pixels.</summary>
	private const int MessageMenuWidth = 220;

	/// <summary>Stores the open message menu, or null when none is.</summary>
	private MessageMenu? _messageMenu;

	/// <summary>Describes an open message menu: the bubble it belongs to, where it was opened, and the text selected then.</summary>
	/// <param name="MessageId">The message whose bubble was right-clicked.</param>
	/// <param name="Start">The first block of the bubble's part, telling apart bubbles of one message split around activity.</param>
	/// <param name="At">The pointer position in the transcript viewport's coordinates.</param>
	/// <param name="Selection">The read-only text selected when the menu opened; empty when none was.</param>
	private readonly record struct MessageMenu( Guid MessageId, int Start, Point At, string Selection );

	/// <summary>Opens the message menu at the pointer on a secondary press; other presses pass through untouched.</summary>
	/// <remarks>
	/// A handler's own pointer position inside the virtual transcript leaves out its scroll offset, which put the menu far off screen
	/// in a scrolled transcript; the window position against the transcript viewport does not. A primary press must not run a panel
	/// change: the rebuild it causes replaced the link text's handlers between press and release, so links in messages never opened.
	/// </remarks>
	private Action<PointerEvent> BuildMessageMenuOpener( TranscriptPart part ) => pointer =>
	{
		if ( pointer.Button == PointerButton.Secondary )
			context.Run( () => OpenMessageMenu( part, pointer.WindowPosition ) );
	};

	/// <summary>Opens a bubble's menu, capturing the selection before the menu takes focus, and rebuilds the rows involved.</summary>
	/// <param name="part">The bubble's part.</param>
	/// <param name="at">The pointer position in window coordinates.</param>
	private void OpenMessageMenu( TranscriptPart part, Point at )
	{
		if ( _messageMenu is { } previous )
			MarkRowChanged( previous.MessageId );
		var viewport = _transcript.Handle.BorderBox;
		_messageMenu = new( part.Message.Id, part.Start, new Point { X = at.X - viewport.X, Y = at.Y - viewport.Y }, ReadSelectedText() );
		MarkRowChanged( part.Message.Id );
	}

	/// <summary>Closes the message menu and rebuilds its row.</summary>
	private void CloseMessageMenu()
	{
		if ( _messageMenu is not { } menu )
			return;

		_messageMenu = null;
		MarkRowChanged( menu.MessageId );
	}

	/// <summary>Reads the text selected in focused read-only text, such as a selectable message; the prompt's selection does not count.</summary>
	private string ReadSelectedText()
	{
		if ( context.App.Window?.Focus.Editor is not { IsReadOnly: true, IsPassword: false } editor )
			return "";

		var start = Math.Clamp( Math.Min( editor.SelectionStart, editor.SelectionEnd ), 0, editor.Text.Length );
		var end = Math.Clamp( Math.Max( editor.SelectionStart, editor.SelectionEnd ), 0, editor.Text.Length );
		return editor.Text[start..end];
	}

	/// <summary>Mounts the open menu for this bubble, or null when its menu is not open.</summary>
	/// <remarks>The menu is a native popup, so it sits out of the bubble's flow in a zero-size absolute holder.</remarks>
	private Blob? BuildMessageMenu( TranscriptPart part, string title )
	{
		if ( _messageMenu is not { } menu || menu.MessageId != part.Message.Id || menu.Start != part.Start )
			return null;

		var message = part.Message;
		var source = ComputeMessageSource( message );
		var code = ComputeCodeBlocks( source );
		var isMarkdown = message.Role == "assistant";
		var entries = new List<MenuEntry>
		{
			new MenuAction( "Copy", () => CopyText( menu.Selection ) ) { Shortcut = "Ctrl+C", IsEnabled = menu.Selection.Length > 0 },
			new MenuSeparator(),
			new MenuAction( "Copy message", () => CopyText( isMarkdown ? ComputePlainText( source ) : source ) ) { AccessKey = Key.M, IsEnabled = source.Length > 0 },
		};
		if ( isMarkdown )
			entries.Add( new MenuAction( "Copy as Markdown", () => CopyText( source ) ) { AccessKey = Key.D, IsEnabled = source.Length > 0 } );
		if ( code.Length > 0 )
			entries.Add( new MenuAction( "Copy code blocks", () => CopyText( code ) ) { AccessKey = Key.B } );

		return new Container
		{
			Key = "message-menu",
			Position = PositionType.Absolute,
			Width = 0,
			Height = 0,
			Children =
			[
				Cell.Mount<ContextMenuInput, ContextMenu>( "message-menu", new( _transcript.Handle, title + " message options", entries, () => context.Run( CloseMessageMenu ) )
				{
					AnchorRect = new ElementRect { X = menu.At.X, Y = menu.At.Y },
					Width = MessageMenuWidth,
				} ),
			],
		};
	}

	/// <summary>Writes text to the clipboard.</summary>
	private void CopyText( string text ) => context.Run( () => context.App.Window?.Clipboard.WriteText( text ) );

	/// <summary>Computes a message's raw text: its text blocks separated by blank lines, or its stored text.</summary>
	private static string ComputeMessageSource( ConversationMessage message )
	{
		if ( message.RichContent is not { ValueKind: JsonValueKind.Object } rich || !rich.TryGetProperty( "content", out var content ) )
			return message.Text;
		if ( content.ValueKind == JsonValueKind.String )
			return content.GetString() ?? "";
		if ( content.ValueKind != JsonValueKind.Array )
			return message.Text;

		return string.Join( "\n\n", content.EnumerateArray()
			.Where( block => ReadText( block, "type" ) == "text" )
			.Select( block => ReadText( block, "text" ).Trim( '\n', '\r' ) )
			.Where( text => !string.IsNullOrWhiteSpace( text ) ) );
	}

	/// <summary>Computes the contents of every fenced code block, quotes included, separated by blank lines.</summary>
	private static string ComputeCodeBlocks( string markdown )
	{
		if ( !markdown.Contains( "```", StringComparison.Ordinal ) && !markdown.Contains( "~~~", StringComparison.Ordinal ) )
			return "";

		var code = new List<string>();
		AddCodeBlocks( markdown, code );
		return string.Join( "\n\n", code );
	}

	/// <summary>Adds the contents of the fenced code blocks in Markdown, looking into quotes.</summary>
	private static void AddCodeBlocks( string markdown, List<string> code )
	{
		foreach ( var block in MarkdownView.Parse( markdown ) )
		{
			if ( block.Kind == "code" )
				code.Add( block.Text );
			else if ( block.Kind == "quote" )
				AddCodeBlocks( block.Text, code );
		}
	}

	/// <summary>Computes the text Markdown shows, without its emphasis, link, heading, and fence syntax.</summary>
	private static string ComputePlainText( string markdown )
	{
		var text = new StringBuilder();
		string? previous = null;
		foreach ( var block in MarkdownView.Parse( markdown ) )
		{
			if ( block.Kind == "rule" )
				continue;
			if ( previous is not null )
				text.Append( previous == "list" && block.Kind == "list" ? "\n" : "\n\n" );
			previous = block.Kind;
			switch ( block.Kind )
			{
				case "code":
				case "table":
					text.Append( block.Text );
					break;
				case "quote":
					text.Append( ComputePlainText( block.Text ) );
					break;
				case "list":
					text.Append( '\t', block.Depth ).Append( block.Marker ).Append( ' ' ).Append( ComputeInlineText( block.Text ) );
					break;
				default:
					text.Append( ComputeInlineText( block.Text ) );
					break;
			}
		}
		return text.ToString();
	}

	/// <summary>Computes the visible text of inline Markdown.</summary>
	private static string ComputeInlineText( string text ) => string.Concat( MarkdownView.ParseInline( text ).Select( span => span.Text ) );
}
