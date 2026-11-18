using Goo;

namespace Lokn.Core;

/// <summary>Draws Markdown as native text; the Markdown module publishes it with <see cref="ModuleBuilder.Service{T}"/>.</summary>
/// <remarks>Resolve it with <c>app.Services.Get&lt;IMarkdownRenderer&gt;()</c> on each build, and show plain text when it is null, so a view needs no Markdown module to draw.</remarks>
public interface IMarkdownRenderer
{
	/// <summary>Builds the blocks of a Markdown text: headings, lists, checklists, tables, code and inline emphasis.</summary>
	/// <param name="markdown">The Markdown text.</param>
	/// <param name="openLink">Opens a link the reader follows.</param>
	Blob Build( string markdown, Action<Uri> openLink );

	/// <summary>Builds a framed, colored code block.</summary>
	/// <param name="code">The code.</param>
	/// <param name="language">The language name, such as <c>cs</c>, which picks its colors; empty shows it uncolored.</param>
	Blob BuildCode( string code, string language );
}
