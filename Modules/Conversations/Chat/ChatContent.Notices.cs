using System.Text.Json;
using System.Text.RegularExpressions;
using Goo;
using Lokn.Core;
using Lokn.Modules.Markdown;

namespace Lokn.Modules.Chat;

/// <summary>Describes how the chat shows one custom message Omp displays.</summary>
/// <param name="Title">The one-line summary, also the fold's label when there is a body.</param>
/// <param name="Glyph">The Segoe Fluent Icons glyph that leads the line.</param>
/// <param name="Body">The text inside the fold, or empty for a single line.</param>
/// <param name="IsPlain">Whether the body is command output shown as plain code rather than Markdown.</param>
/// <param name="IsKnown">Whether the custom type is one the chat understands, so its raw details stay hidden.</param>
internal sealed record ChatNotice( string Title, string Glyph, string Body, bool IsPlain, bool IsKnown );

/// <summary>Renders the custom messages Omp displays: background job results, messages from other agents, and process exits.</summary>
/// <remarks>
/// Omp writes these for the model, wrapped in tags such as <c>&lt;system-notice&gt;</c> and with directions such as how to
/// reply. The chat takes labels from the structured <c>details</c> and shows only the text a person needs, so it never
/// shows the tags.
/// </remarks>
public sealed partial class ChatContent
{
	/// <summary>Defines the line Omp writes to the model before a background job's result.</summary>
	private const string ResumeDirective = "Resume your work using the result below.";

	/// <summary>Defines the background job glyph from Segoe Fluent Icons.</summary>
	private const string JobGlyph = "\uE823";

	/// <summary>Defines the agent message glyph from Segoe Fluent Icons.</summary>
	private const string MessageGlyph = "\uE8BD";

	/// <summary>Defines the supervised process glyph from Segoe Fluent Icons.</summary>
	private const string ProcessGlyph = "\uE756";

	/// <summary>Defines the generic notice glyph from Segoe Fluent Icons.</summary>
	private const string NoticeGlyph = "\uE946";

	/// <summary>Matches text that one tag wraps whole, such as <c>&lt;system-notice&gt;…&lt;/system-notice&gt;</c>.</summary>
	private static readonly Regex Wrapper = new( @"^\s*<([a-z][a-z0-9-]*)(?:\s[^>]*)?>\s*(.*?)\s*</\1>\s*$", RegexOptions.Singleline | RegexOptions.CultureInvariant );

	/// <summary>Builds a displayed custom message: a fold when it has a body, otherwise one muted line.</summary>
	private IEnumerable<Blob> BuildNotice( string key, JsonElement rich )
	{
		var notice = ComputeNotice( rich );
		if ( notice.Body.Length == 0 )
		{
			yield return new Row
			{
				AlignItems = AlignItems.Center,
				Gap = Theme.Current.Spacing,
				MinWidth = 0,
				Children = [ChatIcons.BuildIcon( notice.Glyph ), BuildText( notice.Title, muted: true )],
			};
		}
		else
		{
			var body = notice.IsPlain ? MarkdownView.BuildCode( TerminalText.BuildPlain( notice.Body ), "text" ) : MarkdownView.Build( notice.Body, OpenLink );
			yield return BuildFold( key + ":notice", notice.Title, [body], notice.Glyph );
		}

		if ( !notice.IsKnown && rich.TryGetProperty( "details", out var details ) )
		{
			foreach ( var item in BuildDetails( key, details ) )
				yield return item;
		}
	}

	/// <summary>Computes the title and body of a displayed custom message from its type, content and details.</summary>
	internal static ChatNotice ComputeNotice( JsonElement rich )
	{
		var content = ComputeMessageText( rich ).Trim();
		var details = rich.TryGetProperty( "details", out var value ) ? value : default;
		switch ( ReadText( rich, "customType" ) )
		{
			case "async-result":
			{
				var job = details.ValueKind == JsonValueKind.Object && details.TryGetProperty( "jobs", out var jobs )
					&& jobs.ValueKind == JsonValueKind.Array && jobs.GetArrayLength() > 0 ? jobs[0] : default;
				var title = "Background job " + ReadText( job, "status", "finished" );
				if ( job.ValueKind == JsonValueKind.Object && job.TryGetProperty( "durationMs", out var duration ) && duration.TryGetDouble( out var milliseconds ) )
					title += " · " + WorkingIndicator.FormatElapsed( TimeSpan.FromMilliseconds( milliseconds ) );
				var label = ReadText( job, "label" );
				if ( label.Length > 0 )
					title += " · " + label;
				return new( title, JobGlyph, ComputeJobOutput( content ), ReadText( job, "type" ) == "bash", true );
			}
			case "irc:incoming":
			{
				var from = ReadText( details, "from", "another agent" );
				var message = ReadText( details, "message" ).Trim();
				if ( message.StartsWith( from + ":", StringComparison.Ordinal ) )
					message = message[(from.Length + 1)..].TrimStart();
				return new( "Message from " + from, MessageGlyph, message.Length > 0 ? message : Unwrap( content ), false, true );
			}
			case "launch-completion":
				return new( Unwrap( content ), ProcessGlyph, "", false, true );
			case var type:
				return new( type.Length > 0 ? type : "Notice", NoticeGlyph, Unwrap( content ), false, false );
		}
	}

	/// <summary>Takes a background job's result out of Omp's notice: an agent's output, or what follows the direction to the model.</summary>
	private static string ComputeJobOutput( string content )
	{
		var start = content.IndexOf( "<output>", StringComparison.Ordinal );
		var end = content.LastIndexOf( "</output>", StringComparison.Ordinal );
		if ( start >= 0 && end > start )
			return content[(start + "<output>".Length)..end].Trim();

		var inner = Unwrap( content );
		var directive = inner.IndexOf( ResumeDirective, StringComparison.Ordinal );
		return directive >= 0 ? inner[(directive + ResumeDirective.Length)..].Trim() : inner;
	}

	/// <summary>Removes one tag that wraps the whole text; other text comes back trimmed.</summary>
	private static string Unwrap( string text )
	{
		var match = Wrapper.Match( text );
		return match.Success ? match.Groups[2].Value : text.Trim();
	}
}
