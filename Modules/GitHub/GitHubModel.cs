using System.Globalization;
using System.Text.Json;

namespace Lokn.Modules.GitHub;

/// <summary>Summarizes a check, a pull request's checks together, or a workflow run.</summary>
internal enum CheckState
{
	/// <summary>Nothing reported.</summary>
	None,

	/// <summary>Queued or running.</summary>
	Pending,

	/// <summary>Finished successfully.</summary>
	Success,

	/// <summary>Failed, errored, or timed out.</summary>
	Failure,

	/// <summary>Finished without a verdict: cancelled, skipped, or neutral.</summary>
	Neutral,
}

/// <summary>Describes one check or commit status on a pull request's latest commit.</summary>
/// <param name="Name">The check run or status context name.</param>
/// <param name="State">Its normalized state.</param>
/// <param name="Url">Its details page, or empty.</param>
internal sealed record CheckInfo( string Name, CheckState State, string Url );

/// <summary>Describes one pull request as the dashboard shows it.</summary>
/// <param name="Repository">The owner/name of its repository.</param>
/// <param name="Number">Its number in the repository.</param>
/// <param name="Title">Its title.</param>
/// <param name="Url">Its page, which also identifies it.</param>
/// <param name="Author">Its author's login, or empty for a deleted account.</param>
/// <param name="Branch">Its head branch.</param>
/// <param name="IsDraft">Whether it is a draft.</param>
/// <param name="State">GitHub's OPEN, CLOSED, or MERGED.</param>
/// <param name="ReviewDecision">GitHub's APPROVED, CHANGES_REQUESTED, REVIEW_REQUIRED, or empty.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="Activity">Its comments and reviews together, so a rise means someone responded.</param>
/// <param name="Checks">The combined state of its latest commit's checks.</param>
/// <param name="CheckRuns">Its latest commit's checks, failures first.</param>
internal sealed record PullRequestInfo( string Repository, int Number, string Title, string Url, string Author, string Branch, bool IsDraft,
	string State, string ReviewDecision, DateTimeOffset UpdatedAt, int Activity, CheckState Checks, IReadOnlyList<CheckInfo> CheckRuns );

/// <summary>Describes one GitHub Actions workflow run.</summary>
/// <param name="Id">GitHub's run identifier.</param>
/// <param name="Repository">The owner/name of its repository.</param>
/// <param name="Workflow">The workflow name.</param>
/// <param name="Title">The run's display title, usually the commit or pull request title.</param>
/// <param name="Branch">The branch it ran on.</param>
/// <param name="Event">What started it, such as push or pull_request.</param>
/// <param name="State">Its normalized state.</param>
/// <param name="Url">Its page.</param>
/// <param name="UpdatedAt">When it last changed.</param>
internal sealed record WorkflowRunInfo( long Id, string Repository, string Workflow, string Title, string Branch, string Event, CheckState State, string Url, DateTimeOffset UpdatedAt );

/// <summary>Holds everything one refresh read from GitHub.</summary>
/// <param name="Login">The signed-in user's login.</param>
/// <param name="Mine">The user's open pull requests, most recently updated first.</param>
/// <param name="Reviews">Open pull requests awaiting the user's review.</param>
/// <param name="Closed">The user's recently closed or merged pull requests.</param>
/// <param name="Runs">Workflow runs in the watched repositories, newest first.</param>
internal sealed record GitHubSnapshot( string Login, IReadOnlyList<PullRequestInfo> Mine, IReadOnlyList<PullRequestInfo> Reviews,
	IReadOnlyList<PullRequestInfo> Closed, IReadOnlyList<WorkflowRunInfo> Runs );

/// <summary>Describes one change noticed between two refreshes.</summary>
/// <param name="Time">When it was noticed.</param>
/// <param name="Text">What changed, in one line.</param>
/// <param name="Url">The page it concerns.</param>
/// <param name="Tone">How it should be colored.</param>
/// <param name="Kind">The notification kind, one of <see cref="GitHubKinds"/>, which picks its summary in the feed.</param>
internal sealed record GitHubUpdate( DateTimeOffset Time, string Text, string Url, CheckState Tone, string Kind );

/// <summary>Reads GitHub's GraphQL and REST payloads into dashboard records.</summary>
internal static class GitHubModel
{
	/// <summary>Reads the pull requests of one GraphQL search result.</summary>
	internal static List<PullRequestInfo> ReadPullRequests( JsonElement search )
	{
		var pulls = new List<PullRequestInfo>();
		if ( !search.TryGetProperty( "nodes", out var nodes ) || nodes.ValueKind != JsonValueKind.Array )
			return pulls;

		foreach ( var node in nodes.EnumerateArray() )
		{
			// Search can return an empty object for results the token may not see.
			if ( ReadText( node, "url" ).Length == 0 )
				continue;

			var rollup = node.TryGetProperty( "commits", out var commits ) && commits.TryGetProperty( "nodes", out var last )
				&& last.ValueKind == JsonValueKind.Array && last.GetArrayLength() > 0
				&& last[0].TryGetProperty( "commit", out var commit ) && commit.TryGetProperty( "statusCheckRollup", out var value )
				&& value.ValueKind == JsonValueKind.Object ? value : default;
			var checks = new List<CheckInfo>();
			if ( rollup.ValueKind == JsonValueKind.Object && rollup.TryGetProperty( "contexts", out var contexts )
				&& contexts.TryGetProperty( "nodes", out var items ) && items.ValueKind == JsonValueKind.Array )
			{
				foreach ( var item in items.EnumerateArray() )
				{
					if ( ReadText( item, "__typename" ) == "CheckRun" )
						checks.Add( new( ReadText( item, "name" ), ReadCheckRunState( ReadText( item, "status" ), ReadText( item, "conclusion" ) ), ReadText( item, "detailsUrl" ) ) );
					else if ( ReadText( item, "__typename" ) == "StatusContext" )
						checks.Add( new( ReadText( item, "context" ), ReadStatusState( ReadText( item, "state" ) ), ReadText( item, "targetUrl" ) ) );
				}
			}

			pulls.Add( new(
				ReadText( Read( node, "repository" ), "nameWithOwner" ),
				node.TryGetProperty( "number", out var number ) && number.TryGetInt32( out var parsed ) ? parsed : 0,
				ReadText( node, "title" ),
				ReadText( node, "url" ),
				ReadText( Read( node, "author" ), "login" ),
				ReadText( node, "headRefName" ),
				node.TryGetProperty( "isDraft", out var draft ) && draft.ValueKind == JsonValueKind.True,
				ReadText( node, "state" ),
				ReadText( node, "reviewDecision" ),
				ReadTime( ReadText( node, "updatedAt" ) ),
				ReadCount( node, "comments" ) + ReadCount( node, "reviews" ),
				rollup.ValueKind == JsonValueKind.Object ? ReadStatusState( ReadText( rollup, "state" ) ) : CheckState.None,
				[.. checks.OrderBy( check => check.State switch { CheckState.Failure => 0, CheckState.Pending => 1, _ => 2 } )] ) );
		}
		return pulls;
	}

	/// <summary>Reads the workflow runs of a REST <c>actions/runs</c> response, leaving out skipped runs, which only add noise.</summary>
	internal static List<WorkflowRunInfo> ReadRuns( JsonElement response )
	{
		var runs = new List<WorkflowRunInfo>();
		if ( !response.TryGetProperty( "workflow_runs", out var items ) || items.ValueKind != JsonValueKind.Array )
			return runs;

		foreach ( var run in items.EnumerateArray() )
		{
			var conclusion = ReadText( run, "conclusion" );
			if ( conclusion == "skipped" )
				continue;

			runs.Add( new(
				run.TryGetProperty( "id", out var id ) && id.TryGetInt64( out var parsed ) ? parsed : 0,
				ReadText( Read( run, "repository" ), "full_name" ),
				ReadText( run, "name" ),
				ReadText( run, "display_title" ),
				ReadText( run, "head_branch" ),
				ReadText( run, "event" ),
				ReadCheckRunState( ReadText( run, "status" ).ToUpperInvariant(), conclusion.ToUpperInvariant() ),
				ReadText( run, "html_url" ),
				ReadTime( ReadText( run, "updated_at" ) ) ) );
		}
		return runs;
	}

	/// <summary>Normalizes a check run's or workflow run's status and conclusion.</summary>
	internal static CheckState ReadCheckRunState( string status, string conclusion ) => status != "COMPLETED"
		? status.Length == 0 ? CheckState.None : CheckState.Pending
		: conclusion switch
		{
			"SUCCESS" => CheckState.Success,
			"FAILURE" or "TIMED_OUT" or "STARTUP_FAILURE" or "ACTION_REQUIRED" => CheckState.Failure,
			_ => CheckState.Neutral,
		};

	/// <summary>Normalizes a commit status or check rollup state.</summary>
	internal static CheckState ReadStatusState( string state ) => state switch
	{
		"SUCCESS" => CheckState.Success,
		"FAILURE" or "ERROR" => CheckState.Failure,
		"PENDING" or "EXPECTED" => CheckState.Pending,
		_ => CheckState.None,
	};

	/// <summary>Reads an optional object property, or an undefined element.</summary>
	private static JsonElement Read( JsonElement element, string property ) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty( property, out var value ) ? value : default;

	/// <summary>Reads an optional string property, or empty.</summary>
	private static string ReadText( JsonElement element, string property ) =>
		Read( element, property ) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? "" : "";

	/// <summary>Reads a connection's <c>totalCount</c>, or zero.</summary>
	private static int ReadCount( JsonElement element, string property ) =>
		Read( Read( element, property ), "totalCount" ) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32( out var count ) ? count : 0;

	/// <summary>Reads an ISO 8601 timestamp, or the minimum value.</summary>
	private static DateTimeOffset ReadTime( string text ) =>
		DateTimeOffset.TryParse( text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time ) ? time : DateTimeOffset.MinValue;
}
