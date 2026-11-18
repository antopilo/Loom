using Goo;
using Lokn.Core;

namespace Lokn.Modules.GitHub;

/// <summary>The kinds of notification the GitHub module posts, with GitHub's mark and the wording of their summary lines in the feed.</summary>
internal static class GitHubKinds
{
	/// <summary>A pull request's checks, or a watched workflow run, passed.</summary>
	internal const string ChecksPassed = "github.checks-passed";

	/// <summary>A pull request's checks, or a watched workflow run, failed.</summary>
	internal const string ChecksFailed = "github.checks-failed";

	/// <summary>A watched workflow run ended neither passing nor failing, such as cancelled.</summary>
	internal const string RunEnded = "github.run-ended";

	/// <summary>A pull request was approved.</summary>
	internal const string Approved = "github.approved";

	/// <summary>A reviewer asked for changes on a pull request.</summary>
	internal const string ChangesRequested = "github.changes-requested";

	/// <summary>A pull request got new comments or reviews.</summary>
	internal const string Comments = "github.comments";

	/// <summary>A pull request was merged.</summary>
	internal const string Merged = "github.merged";

	/// <summary>A pull request was closed without merging.</summary>
	internal const string Closed = "github.closed";

	/// <summary>Someone asked the user to review a pull request.</summary>
	internal const string ReviewRequested = "github.review-requested";

	/// <summary>Holds GitHub's mark, filled, in a 16 by 16 box.</summary>
	private static readonly VectorPath Mark = VectorPath.Parse(
		"M8 0c4.42 0 8 3.58 8 8a8.013 8.013 0 0 1-5.45 7.59c-.4.08-.55-.17-.55-.38 0-.27.01-1.13.01-2.2 0-.75-.25-1.23-.54-1.48 1.78-.2 3.65-.88 3.65-3.95 0-.88-.31-1.59-.82-2.15.08-.2.36-1.02-.08-2.12 0 0-.67-.22-2.2.82-.64-.18-1.32-.27-2-.27-.68 0-1.36.09-2 .27-1.53-1.03-2.2-.82-2.2-.82-.44 1.1-.16 1.92-.08 2.12-.51.56-.82 1.28-.82 2.15 0 3.06 1.86 3.75 3.64 3.95-.23.2-.44.55-.51 1.07-.46.21-1.61.55-2.33-.66-.15-.24-.6-.83-1.23-.82-.67.01-.27.38.01.53.34.19.73.9.82 1.13.16.45.68 1.31 2.69.94 0 .67.01 1.3.01 1.49 0 .21-.15.45-.55.38A7.995 7.995 0 0 1 0 8c0-4.42 3.58-8 8-8Z",
		new ElementRect { Width = 16, Height = 16 } );

	/// <summary>Gets the kinds the module contributes: every one a short update with GitHub's mark, worded for a summary line.</summary>
	internal static IReadOnlyList<NotificationKind> All { get; } =
	[
		Build( "github", ( count, first ) => count == 1 ? first.Message : $"{count} GitHub updates" ),
		Build( ChecksPassed, ( count, _ ) => $"{Count( count, "check", "checks" )} passed" ),
		Build( ChecksFailed, ( count, _ ) => $"{Count( count, "check", "checks" )} failed" ),
		Build( RunEnded, ( count, _ ) => $"{Count( count, "run", "runs" )} ended" ),
		Build( Approved, ( count, _ ) => count == 1 ? "approved" : $"{count} approved" ),
		Build( ChangesRequested, ( count, _ ) => count == 1 ? "changes requested" : $"changes requested on {count}" ),
		Build( Comments, ( count, _ ) => $"new comments on {Count( count, "pull request", "pull requests" )}" ),
		Build( Merged, ( count, _ ) => count == 1 ? "merged" : $"{count} merged" ),
		Build( Closed, ( count, _ ) => count == 1 ? "closed" : $"{count} closed" ),
		Build( ReviewRequested, ( count, _ ) => count == 1 ? "review requested" : $"{count} reviews requested" ),
	];

	/// <summary>Builds one kind with GitHub's mark.</summary>
	private static NotificationKind Build( string kind, Func<int, LoknNotification, string> summarize ) =>
		new( kind, "\uE8F4" ) { Icon = Mark, IsUpdate = true, Summarize = summarize };

	/// <summary>Formats a count with the singular or plural noun.</summary>
	private static string Count( int count, string one, string many ) => $"{count} {(count == 1 ? one : many)}";
}
