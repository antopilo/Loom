using System.Text;
using System.Text.Json;
using Lokn.Core;
using Lokn.Modules.Git;
using Lokn.Modules.Omp;

namespace Lokn.Modules.Conversations;

/// <summary>Adds the chat to the Git views: reviewing the changes in an Omp <c>/review</c> chat whose findings mark the diff, and writing commit messages with the chat's small model.</summary>
internal static class GitReviewChat
{
	/// <summary>Defines the review action's key, which a Git panel saves with the review chat it linked.</summary>
	internal const string ActionKey = "conversations.review";

	/// <summary>Marks the start of the exact JSON result requested from the review chat.</summary>
	private const string StartTag = "<lokn-review>";

	/// <summary>Marks the end of the exact JSON result requested from the review chat.</summary>
	private const string EndTag = "</lokn-review>";

	/// <summary>Defines the instruction the commit message writer gives the model.</summary>
	private const string MessageQuestion = """
		Write a git commit message for the changes in the attached diff. Reply with the summary on the first line, imperative and at most 72 characters,
		then a blank line, then a short description of why the change was made, wrapped at 72 characters. Reply with the message only, without quotes or fences.
		""";

	/// <summary>Builds the working-tree action that starts a review chat in the repository, on the host of the panel it ran from, and links its findings to the panel.</summary>
	/// <remarks>A review is linked by its chat's text form; links saved before hosts are This PC's chats' <c>D</c> GUIDs, which still read.</remarks>
	internal static GitAction BuildAction( ConversationsModule module ) => new( ActionKey, "Review in chat", GitActionScope.Changes, context =>
	{
		var host = module.Hosts.Find( context.App.Panels.GetHostId( context.PanelId ) ) ?? module.Hosts.Local;
		var chat = module.StartConversationInDirectory( context.App, host, BuildPrompt( context.Root, context.Annotations ), context.Root );
		context.LinkReview( chat.ToString() );
	} )
	{
		// Lucide's speech bubble.
		Icon = Goo.VectorPath.Parse( "M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z", new Goo.ElementRect { Width = 24, Height = 24 } ),
		ReadReview = ( _, id ) => ChatRef.TryParse( id, out var chat ) && module.Hosts.Find( chat ) is { } review
			? new GitReviewStatus( ParseResult( review.Host.Store.GetMessages( chat.ConversationId ) ) )
			: null,
		OpenReview = ( app, id ) =>
		{
			if ( ChatRef.TryParse( id, out var chat ) && module.Hosts.Find( chat ) is not null )
				module.OpenConversation( app, chat );
		},
	};

	/// <summary>Builds the commit message writer: the profile's small model reads the changes and writes the summary and why.</summary>
	internal static GitMessageWriter BuildWriter() => new( "the chat's small model", async ( request, cancellationToken ) =>
	{
		var agent = request.App.Services.Get<IAgentService>() ?? throw new InvalidOperationException( "Writing a commit message needs the Omp module." );
		var material = request.Summary.Length > 0 ? $"The summary is already written; keep it as the first line: {request.Summary}\n\n{request.Patch}" : request.Patch;
		var answer = await agent.AskSmallAsync( request.Profile, MessageQuestion, material, cancellationToken ).ConfigureAwait( false );
		return ParseMessage( answer, request.Summary );
	} );

	/// <summary>Reads a written message: its first line as the summary, unless one was typed, and the rest as the description.</summary>
	internal static GitMessage ParseMessage( string answer, string summary )
	{
		var text = answer.Replace( "\r\n", "\n" ).Trim().Trim( '`' ).Trim();
		var newline = text.IndexOf( '\n' );
		var first = (newline < 0 ? text : text[..newline]).Trim();
		var rest = newline < 0 ? "" : text[(newline + 1)..].Trim();
		return new( summary.Length > 0 ? summary : first, rest );
	}

	/// <summary>Builds an Omp <c>/review</c> command for uncommitted work and embeds the user's line notes.</summary>
	internal static string BuildPrompt( string root, IReadOnlyList<GitReviewAnnotation> annotations )
	{
		var prompt = new StringBuilder();
		prompt.Append( "/review \"" ).Append( root.Replace( "\"", "" ) ).AppendLine( "\" Review the uncommitted changes." );
		if ( annotations.Count > 0 )
		{
			prompt.AppendLine( "Operator annotations:" );
			foreach ( var annotation in annotations )
			{
				var marker = annotation.Line.Kind == DiffLineKind.Added ? '+' : annotation.Line.Kind == DiffLineKind.Removed ? '-' : ' ';
				prompt.Append( "- `" ).Append( annotation.Line.Path ).Append( ':' ).Append( annotation.Line.Line ).Append( "`: " ).AppendLine( annotation.Note );
				prompt.Append( "  `" ).Append( marker ).Append( annotation.Line.RawLine.Replace( "`", "'" ) ).AppendLine( "`" );
			}
		}
		prompt.AppendLine( "After all reviewer tasks finish, end the final reply with exactly one machine-readable block using this schema:" );
		prompt.Append( StartTag ).Append( "{\"overall_correctness\":\"correct|incorrect\",\"explanation\":\"...\",\"confidence\":0.0," )
			.Append( "\"findings\":[{\"title\":\"...\",\"body\":\"...\",\"priority\":0,\"confidence\":0.0,\"file_path\":\"path\",\"line_start\":1,\"line_end\":1}]}" )
			.Append( EndTag );
		return prompt.ToString();
	}

	/// <summary>Reads the newest valid Lokn review block from assistant messages.</summary>
	internal static GitReviewResult? ParseResult( IReadOnlyList<ConversationMessage> messages )
	{
		foreach ( var message in messages.Reverse() )
		{
			if ( message.Role != "assistant" )
				continue;
			var start = message.Text.LastIndexOf( StartTag, StringComparison.Ordinal );
			if ( start < 0 )
				continue;
			start += StartTag.Length;
			var end = message.Text.IndexOf( EndTag, start, StringComparison.Ordinal );
			if ( end < 0 )
				continue;
			try
			{
				return JsonSerializer.Deserialize<GitReviewResult>( message.Text[start..end] );
			}
			catch ( JsonException )
			{
			}
		}
		return null;
	}
}
