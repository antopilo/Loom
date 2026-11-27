using Lokn.Core;

namespace Lokn.Modules.GitHub;

/// <summary>Adds a project's pull requests to its brief, the Overview module's page, for a project whose <c>origin</c> remote is on GitHub: those
/// waiting for the user's review and the user's own that need them go under Top to-dos, the others and the ones closed lately under New updates.</summary>
/// <remarks>It reads the watcher's last refresh, so the brief follows each refresh, and needs no request of its own.</remarks>
internal static class GitHubBrief
{
	/// <summary>Defines how many recently closed or merged pull requests show.</summary>
	private const int ClosedLimit = 3;

	/// <summary>Defines the rows' icon, the branch Git uses.</summary>
	private const string Glyph = "\uF003";

	/// <summary>Creates the section, which reads the module's watcher while the module runs.</summary>
	/// <param name="watcher">Gets the running watcher, or null while the module is stopped.</param>
	internal static BriefSection Create( Func<GitHubWatcher?> watcher ) => new( "github.pulls", "Pull requests", 10, project => Read( watcher(), project ) );

	/// <summary>Reads the card for a project: nothing for a project off GitHub, a note until the first refresh, else its pull requests.</summary>
	private static BriefContent? Read( GitHubWatcher? watcher, BriefProject project )
	{
		if ( watcher is null || ParseRepository( project.Remote ) is not { } repository )
			return null;

		BriefLink[] links = [new( "Open pull requests", () => project.App.Panels.OpenBeside( GitHubModule.DashboardPanelType, project.PanelId ) )];
		if ( watcher.Snapshot is not { } snapshot )
		{
			var note = watcher.NeedsSignIn ? "Sign in to GitHub in Pull requests to see this repository's pull requests."
				: watcher.Status is { Length: > 0 } status ? status
				: "Reading GitHub…";
			return new( [] ) { Empty = note, Links = links };
		}

		var items = ComputeItems( snapshot, repository, url => Uri.TryCreate( url, UriKind.Absolute, out var uri ) ? () => project.App.OpenLink( uri, project.PanelId ) : null );
		return new( items ) { Empty = $"No pull requests of yours on {repository}.", Links = links };
	}

	/// <summary>Lists a repository's items: the pull requests waiting for the user's review, the user's open ones, then the few closed lately, each with
	/// what to do or what happened and when it last changed.</summary>
	/// <param name="snapshot">The watcher's last refresh.</param>
	/// <param name="repository">The repository, <c>owner/name</c>.</param>
	/// <param name="open">Makes the action that opens a pull request's page, or null when it cannot open.</param>
	internal static IReadOnlyList<BriefItem> ComputeItems( GitHubSnapshot snapshot, string repository, Func<string, Action?> open )
	{
		bool IsInRepository( PullRequestInfo pull ) => string.Equals( pull.Repository, repository, StringComparison.OrdinalIgnoreCase );
		BriefItem Item( PullRequestInfo pull, (string Detail, string Description, bool NeedsYou) state ) =>
			new( pull.Url, $"#{pull.Number} {pull.Title}", state.Detail )
			{
				Description = state.Description,
				Time = pull.UpdatedAt,
				Glyph = Glyph,
				NeedsYou = state.NeedsYou,
				Open = open( pull.Url ),
			};

		return
		[
			.. snapshot.Reviews.Where( IsInRepository ).Select( pull => Item( pull, pull.Author.Length > 0
				? ($"Review requested by {pull.Author}", $"{pull.Author} asks for your review on {pull.Branch}.", true)
				: ("Review requested", $"Your review is asked for on {pull.Branch}.", true) ) ),
			.. snapshot.Mine.Where( IsInRepository ).Select( pull => Item( pull, ComputeState( pull ) ) ),
			.. snapshot.Closed.Where( IsInRepository ).Take( ClosedLimit ).Select( pull => Item( pull, pull.State == "MERGED"
				? ("Merged", $"{pull.Branch} is merged.", false)
				: ("Closed", $"{pull.Branch} was closed without a merge.", false) ) ),
		];
	}

	/// <summary>Says where one of the user's open pull requests stands, in a word and in a sentence, and whether the next step is the user's: failing
	/// checks, requested changes, or an approval to merge.</summary>
	internal static (string Detail, string Description, bool NeedsYou) ComputeState( PullRequestInfo pull ) => pull switch
	{
		{ Checks: CheckState.Failure } => ("Checks failing",
			"Failing: " + string.Join( ", ", pull.CheckRuns.Where( check => check.State == CheckState.Failure ).Select( check => check.Name ).Take( 3 ) ) + ".", true),
		{ ReviewDecision: "CHANGES_REQUESTED" } => ("Changes requested", $"A reviewer asks for changes on {pull.Branch}.", true),
		{ IsDraft: true } => ("Draft", $"Still a draft on {pull.Branch}.", false),
		{ ReviewDecision: "APPROVED", Checks: CheckState.Pending } => ("Approved, checks running", "Approved; it can merge once its checks pass.", false),
		{ ReviewDecision: "APPROVED" } => ("Approved, ready to merge", $"Approved and green: {pull.Branch} is ready to merge.", true),
		{ Checks: CheckState.Pending } => ("Checks running", $"Checks are running on {pull.Branch}.", false),
		_ => ("Waiting for review", $"Waiting for a review on {pull.Branch}.", false),
	};

	/// <summary>Reads <c>owner/name</c> from a GitHub remote address, https or ssh, or null for another host.</summary>
	internal static string? ParseRepository( string remote )
	{
		var text = remote.Trim();
		foreach ( var start in (string[])["https://github.com/", "http://github.com/", "git@github.com:", "ssh://git@github.com/"] )
		{
			if ( !text.StartsWith( start, StringComparison.OrdinalIgnoreCase ) )
				continue;
			var path = text[start.Length..].TrimEnd( '/' );
			if ( path.EndsWith( ".git", StringComparison.OrdinalIgnoreCase ) )
				path = path[..^4];
			return path.Split( '/' ) is [{ Length: > 0 } owner, { Length: > 0 } name] ? $"{owner}/{name}" : null;
		}
		return null;
	}
}
