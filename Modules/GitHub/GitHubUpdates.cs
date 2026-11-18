namespace Lokn.Modules.GitHub;

/// <summary>Finds what changed between two refreshes: checks, reviews, responses, merges, new review requests, and finished runs.</summary>
internal static class GitHubUpdates
{
	/// <summary>Lists the changes from one snapshot to the next, in the order they read best: the user's pull requests, then requests, then runs.</summary>
	internal static List<GitHubUpdate> Compute( GitHubSnapshot previous, GitHubSnapshot current, DateTimeOffset now )
	{
		var updates = new List<GitHubUpdate>();
		void Add( string text, string url, CheckState tone, string kind ) => updates.Add( new( now, text, url, tone, kind ) );

		var before = previous.Mine.ToDictionary( pull => pull.Url, StringComparer.Ordinal );
		foreach ( var pull in current.Mine )
		{
			if ( !before.TryGetValue( pull.Url, out var old ) )
				continue;

			var name = Name( pull );
			if ( pull.Checks != old.Checks && pull.Checks is CheckState.Success or CheckState.Failure )
			{
				var failed = pull.CheckRuns.Count( check => check.State == CheckState.Failure );
				Add( pull.Checks == CheckState.Success ? $"Checks passed on {name}" : $"{failed} check{(failed == 1 ? "" : "s")} failed on {name}",
					pull.CheckRuns.FirstOrDefault( check => check.State == CheckState.Failure && check.Url.Length > 0 )?.Url ?? pull.Url, pull.Checks,
					pull.Checks == CheckState.Success ? GitHubKinds.ChecksPassed : GitHubKinds.ChecksFailed );
			}
			if ( pull.ReviewDecision != old.ReviewDecision && pull.ReviewDecision is "APPROVED" or "CHANGES_REQUESTED" )
				Add( pull.ReviewDecision == "APPROVED" ? $"Approved: {name}" : $"Changes requested on {name}", pull.Url,
					pull.ReviewDecision == "APPROVED" ? CheckState.Success : CheckState.Failure,
					pull.ReviewDecision == "APPROVED" ? GitHubKinds.Approved : GitHubKinds.ChangesRequested );
			if ( pull.Activity > old.Activity )
			{
				var count = pull.Activity - old.Activity;
				Add( $"{count} new comment{(count == 1 ? "" : "s")} or review{(count == 1 ? "" : "s")} on {name}", pull.Url, CheckState.None, GitHubKinds.Comments );
			}
		}

		var open = current.Mine.Select( pull => pull.Url ).ToHashSet( StringComparer.Ordinal );
		var closed = current.Closed.ToDictionary( pull => pull.Url, StringComparer.Ordinal );
		foreach ( var old in previous.Mine )
		{
			if ( open.Contains( old.Url ) || !closed.TryGetValue( old.Url, out var pull ) )
				continue;

			Add( pull.State == "MERGED" ? $"Merged: {Name( pull )}" : $"Closed: {Name( pull )}", pull.Url,
				pull.State == "MERGED" ? CheckState.Success : CheckState.Neutral, pull.State == "MERGED" ? GitHubKinds.Merged : GitHubKinds.Closed );
		}

		var requested = previous.Reviews.Select( pull => pull.Url ).ToHashSet( StringComparer.Ordinal );
		foreach ( var pull in current.Reviews )
		{
			if ( !requested.Contains( pull.Url ) )
				Add( $"Review requested by {(pull.Author.Length > 0 ? pull.Author : "someone")}: {Name( pull )}", pull.Url, CheckState.Pending, GitHubKinds.ReviewRequested );
		}

		var running = previous.Runs.Where( run => run.State == CheckState.Pending ).Select( run => run.Id ).ToHashSet();
		foreach ( var run in current.Runs )
		{
			if ( running.Contains( run.Id ) && run.State != CheckState.Pending )
				Add( $"{run.Workflow} {Describe( run.State )} on {run.Repository} {run.Branch}", run.Url, run.State,
					run.State switch { CheckState.Success => GitHubKinds.ChecksPassed, CheckState.Failure => GitHubKinds.ChecksFailed, _ => GitHubKinds.RunEnded } );
		}
		return updates;
	}

	/// <summary>Names a pull request as <c>owner/name#number title</c>.</summary>
	private static string Name( PullRequestInfo pull ) => $"{pull.Repository}#{pull.Number} {pull.Title}";

	/// <summary>Describes how a run ended.</summary>
	internal static string Describe( CheckState state ) => state switch
	{
		CheckState.Success => "succeeded",
		CheckState.Failure => "failed",
		CheckState.Pending => "running",
		CheckState.Neutral => "ended",
		_ => "",
	};
}
