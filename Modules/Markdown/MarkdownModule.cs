using Goo;
using Lokn.Core;

namespace Lokn.Modules.Markdown;

/// <summary>Publishes the Markdown renderer other modules draw text with, such as chats and the notification feed.</summary>
/// <remarks>
/// A module that only shows Markdown resolves <see cref="IMarkdownRenderer"/> and falls back to plain text without it. A module that builds on the
/// renderer's parts, such as the chat's streaming, references this project and declares it as a dependency.
/// </remarks>
public sealed class MarkdownModule : ILoknModule, IMarkdownRenderer
{
	/// <summary>Gets the module identifier.</summary>
	public string Id => "markdown";

	/// <summary>Gets the display name.</summary>
	public string Title => "Markdown";

	/// <summary>Publishes the renderer.</summary>
	public void Register( ModuleBuilder builder ) => builder.Service<IMarkdownRenderer>( this );

	/// <summary>Builds the blocks of a Markdown text.</summary>
	public Blob Build( string markdown, Action<Uri> openLink ) => MarkdownView.Build( markdown, openLink );

	/// <summary>Lets code blocks copy their text to the clipboard of the app's window.</summary>
	public void Start( ModuleContext context ) => MarkdownView.CopyText = text => context.App.Window?.Clipboard.WriteText( text );

	/// <summary>Removes the copy action, which holds the app, so an unloaded module leaves nothing behind.</summary>
	public void Stop() => MarkdownView.CopyText = null;

	/// <summary>Builds a framed, colored code block.</summary>
	public Blob BuildCode( string code, string language ) => MarkdownView.BuildCode( code, language );
}
