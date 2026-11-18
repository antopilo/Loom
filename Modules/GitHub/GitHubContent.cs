using Goo;
using Lokn.Core;

namespace Lokn.Modules.GitHub;

/// <summary>Shows the user's GitHub pull requests with their checks, review requests, workflow runs, and what changed since Lokn started.</summary>
/// <remarks>Reads the module's <see cref="GitHubWatcher"/>, which polls on the interval set in <see cref="GitHubModule.RefreshSeconds"/> and posts changes to the notification feed.</remarks>
internal sealed class GitHubContent : PanelContent
{
	/// <summary>Defines the panel content inset in logical pixels.</summary>
	private const int ContentPadding = 24;

	/// <summary>Defines how long a pull request may go without changes before the panel hides it.</summary>
	private static readonly TimeSpan StaleAfter = TimeSpan.FromDays( 21 );

	/// <summary>Defines the section heading size in logical pixels.</summary>
	private const int SectionSize = 15;

	/// <summary>Defines the detail text size in logical pixels.</summary>
	private const int DetailSize = 12;

	/// <summary>Defines the status dot drawn before each row.</summary>
	private const string Dot = "●";

	/// <summary>Stores the panel's guarded dispatch and lifetime.</summary>
	private readonly PanelContext _context;

	/// <summary>Polls GitHub and holds what it read.</summary>
	private readonly GitHubWatcher _watcher;

	/// <summary>Tracks the pull requests whose checks are shown, by URL.</summary>
	private readonly State<IReadOnlySet<string>> _expanded = new( new HashSet<string>( StringComparer.Ordinal ) );

	/// <summary>Creates the panel over the module's watcher and asks it for fresh data.</summary>
	internal GitHubContent( PanelContext context, GitHubWatcher watcher )
	{
		_context = context;
		_watcher = watcher;
		_watcher.Refresh();
	}

	/// <summary>Builds sign-in or the changes, pull request, review, and run sections.</summary>
	public override Blob Build()
	{
		var snapshot = _watcher.Snapshot;
		var status = _watcher.Status;
		var needsSignIn = _watcher.NeedsSignIn;
		var children = new List<Blob>();
		if ( status.Length > 0 )
			children.Add( new Text
			{
				Content = status,
				Color = needsSignIn ? Theme.Current.Text : Theme.Current.Error,
				Selectable = true,
			} );

		if ( needsSignIn )
			children.Add( BuildSignIn() );
		else if ( snapshot is null )
			children.Add( BuildMuted( _watcher.IsBusy ? "Loading your pull requests…" : "Nothing loaded yet." ) );
		else
		{
			var now = DateTimeOffset.Now;
			var cutoff = now - StaleAfter;
			var mine = snapshot.Mine.Where( pull => pull.UpdatedAt >= cutoff ).ToArray();
			var reviews = snapshot.Reviews.Where( pull => pull.UpdatedAt >= cutoff ).ToArray();
			var updates = _watcher.Updates;
			if ( updates.Count > 0 )
				children.Add( BuildSection( "Updates", updates.Count, [.. updates.Select( update => BuildUpdate( update, now ) )], "",
					Theme.Current.BuildButton( "Clear", () => _context.Run( _watcher.ClearUpdates ) ) ) );
			children.Add( BuildSection( "Pull Requests", mine.Length, [.. mine.Select( pull => BuildPull( pull, now, showAuthor: false ) )], "None of your open pull requests changed in the last three weeks." ) );
			children.Add( BuildSection( "Review requests", reviews.Length, [.. reviews.Select( pull => BuildPull( pull, now, showAuthor: true ) )], "Nobody is waiting on your review." ) );
			children.Add( BuildSection( "Workflow runs", snapshot.Runs.Count, [.. snapshot.Runs.Select( run => BuildRun( run, now ) )],
				"No workflow runs. Your open pull requests' repositories show your runs; add repositories to GitHub › Watched repositories in Settings to see everyone's." ) );
		}

		return new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Pull Requests",
			FlexGrow = 1,
			MinHeight = 0,
			Padding = ContentPadding,
			Gap = Theme.Current.Spacing * 2,
			OverflowY = Overflow.Scroll,
			Children = children,
		};
	}

	/// <summary>Formats how long ago something happened, in the largest whole unit.</summary>
	internal static string FormatAge( DateTimeOffset time, DateTimeOffset now )
	{
		var age = now - time;
		return age.TotalMinutes < 1 ? "just now"
			: age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
			: age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago"
			: age.TotalDays < 30 ? $"{(int)age.TotalDays}d ago"
			: time.ToLocalTime().ToString( "yyyy-MM-dd" );
	}

	/// <summary>Builds the sign-in action and where the credential comes from.</summary>
	private Container BuildSignIn() => new()
	{
		Gap = Theme.Current.Spacing,
		AlignItems = AlignItems.FlexStart,
		Children =
		[
			Theme.Current.BuildButton( "Sign in to GitHub", () => _context.Run( _watcher.SignIn ), !_watcher.IsBusy ),
			BuildMuted( "Opens Git Credential Manager's GitHub sign-in and keeps the credential with Git, where git push also finds it. Lokn stores no token. You can also set GH_TOKEN before starting Lokn." ),
		],
	};

	/// <summary>Builds a titled section with its count, an optional action, and its rows or an empty note.</summary>
	private static Container BuildSection( string title, int count, List<Blob> rows, string empty, Blob? action = null )
	{
		var header = new List<Blob>
		{
			new Text
			{
				Content = title,
				FontSize = SectionSize,
			},
			BuildMuted( count.ToString() ),
			new Spacer(),
		};
		if ( action is not null )
			header.Add( action );

		var children = new List<Blob> { new Row { Gap = Theme.Current.Spacing, AlignItems = AlignItems.Center, Children = header } };
		if ( rows.Count > 0 )
			children.AddRange( rows );
		else if ( empty.Length > 0 )
			children.Add( BuildMuted( empty ) );
		return new Container
		{
			Key = title,
			Gap = Theme.Current.Spacing / 2,
			Children = children,
		};
	}

	/// <summary>Builds one pull request: its check state, title, and details, opening it on click, with its checks on demand.</summary>
	private Container BuildPull( PullRequestInfo pull, DateTimeOffset now, bool showAuthor )
	{
		var details = new List<string> { $"{pull.Repository}#{pull.Number}" };
		if ( showAuthor && pull.Author.Length > 0 )
			details.Add( "by " + pull.Author );
		if ( pull.IsDraft )
			details.Add( "draft" );
		details.Add( pull.State switch
		{
			"MERGED" => "merged",
			"CLOSED" => "closed",
			_ => pull.ReviewDecision switch
			{
				"APPROVED" => "approved",
				"CHANGES_REQUESTED" => "changes requested",
				"REVIEW_REQUIRED" => "review required",
				_ => "",
			},
		} );
		if ( pull.CheckRuns.Count > 0 )
		{
			var failed = pull.CheckRuns.Count( check => check.State == CheckState.Failure );
			var running = pull.CheckRuns.Count( check => check.State == CheckState.Pending );
			details.Add( failed > 0 ? $"{failed} of {pull.CheckRuns.Count} checks failing"
				: running > 0 ? $"{running} of {pull.CheckRuns.Count} checks running"
				: $"{pull.CheckRuns.Count} checks done" );
		}
		details.Add( FormatAge( pull.UpdatedAt, now ) );

		var tone = pull.State switch
		{
			"MERGED" => Theme.Current.AnsiMagenta,
			"CLOSED" => Theme.Current.Muted,
			_ => ComputeColor( pull.Checks ),
		};
		var isOpen = _expanded.Value.Contains( pull.Url );
		var row = new List<Blob>
		{
			BuildLink( pull.Url, $"{pull.Repository}#{pull.Number} {pull.Title}", tone, pull.Title, string.Join( " · ", details.Where( detail => detail.Length > 0 ) ) ),
		};
		if ( pull.State == "OPEN" && pull.CheckRuns.Count > 0 )
		{
			var toggle = Theme.Current.BuildButton( "", () => _context.Run( () => UpdateExpanded( pull.Url ) ) );
			toggle.AccessibilityName = $"{(isOpen ? "Hide" : "Show")} checks of {pull.Repository}#{pull.Number}";
			toggle.Width = Theme.Current.ControlHeight;
			toggle.Padding = 0;
			toggle.FlexShrink = 0;
			toggle.Children = [Theme.Current.BuildChevron( isOpen )];
			row.Add( toggle );
		}

		var children = new List<Blob> { new Row { AlignItems = AlignItems.Center, Gap = Theme.Current.Spacing / 2, Children = row } };
		if ( isOpen )
			children.Add( new Container
			{
				// Checks are indented under their pull request.
				Padding = new Edges( Theme.Current.Spacing / 2, 0, 0, Theme.Current.Spacing * 2 ),
				Gap = Theme.Current.Spacing / 4,
				Children = [.. pull.CheckRuns.Select( check => (Blob)BuildLink( check.Url, check.Name, ComputeColor( check.State ), check.Name, ComputeCheckLabel( check.State ) ) )],
			} );
		return new Container
		{
			Key = pull.Url,
			Children = children,
		};
	}

	/// <summary>Builds one workflow run, opening it on click.</summary>
	private Button BuildRun( WorkflowRunInfo run, DateTimeOffset now ) => BuildLink( run.Url, $"{run.Workflow} {run.Title}", ComputeColor( run.State ),
		run.Title.Length > 0 && run.Title != run.Workflow ? $"{run.Workflow} · {run.Title}" : run.Workflow,
		string.Join( " · ", new[] { run.Repository, run.Branch, run.Event, GitHubUpdates.Describe( run.State ), FormatAge( run.UpdatedAt, now ) }.Where( part => part.Length > 0 ) ) );

	/// <summary>Builds one noticed change, opening what it concerns on click.</summary>
	private Button BuildUpdate( GitHubUpdate update, DateTimeOffset now ) =>
		BuildLink( update.Url, update.Text, ComputeColor( update.Tone ), update.Text, FormatAge( update.Time, now ) );

	/// <summary>Builds a full-width row with a colored dot, a title, and a muted detail line that opens a GitHub page; only hover and focus give it a background.</summary>
	private Button BuildLink( string url, string name, Color tone, string title, string detail ) => new()
	{
		FlexGrow = 1,
		MinWidth = 0,
		FlexDirection = FlexDirection.Row,
		AlignItems = AlignItems.Center,
		Gap = Theme.Current.Spacing,
		Padding = new Edges( Theme.Current.Spacing / 2, Theme.Current.Spacing ),
		BorderRadius = Theme.Current.Radius,
		Color = Theme.Current.Text,
		Focusable = true,
		Cursor = Cursor.Pointer,
		AccessibilityName = name,
		OnClick = () => _context.Run( () => UpdateOpen( url ) ),
		KeyBindings = [new( Key.Enter, KeyModifiers.None, () => _context.Run( () => UpdateOpen( url ) ) )],
		HoverStyle = new Style
		{
			BackgroundColor = Theme.Current.Hover,
		},
		FocusStyle = new Style
		{
			BackgroundColor = Theme.Current.Hover,
		},
		Children =
		[
			new Text
			{
				Content = Dot,
				Color = tone,
				FlexShrink = 0,
			},
			new Container
			{
				FlexGrow = 1,
				MinWidth = 0,
				Children =
				[
					new Text
					{
						Content = title,
						TextWrap = TextWrap.NoWrap,
						TextTrimming = TextTrimming.Ellipsis,
					},
					new Text
					{
						Content = detail,
						FontSize = DetailSize,
						Color = Theme.Current.Muted,
						TextWrap = TextWrap.NoWrap,
						TextTrimming = TextTrimming.Ellipsis,
					},
				],
			},
		],
	};

	/// <summary>Builds muted body text.</summary>
	private static Text BuildMuted( string text ) => new()
	{
		Content = text,
		Color = Theme.Current.Muted,
		MinWidth = 0,
	};

	/// <summary>Colors a state: green passed, red failed, yellow running, muted otherwise.</summary>
	private static Color ComputeColor( CheckState state ) => state switch
	{
		CheckState.Success => Theme.Current.Success,
		CheckState.Failure => Theme.Current.Error,
		CheckState.Pending => Theme.Current.AnsiYellow,
		_ => Theme.Current.Muted,
	};

	/// <summary>Describes a single check's state.</summary>
	private static string ComputeCheckLabel( CheckState state ) => state switch
	{
		CheckState.Success => "passed",
		CheckState.Failure => "failed",
		CheckState.Pending => "running",
		CheckState.Neutral => "skipped or cancelled",
		_ => "",
	};

	/// <summary>Shows or hides one pull request's checks.</summary>
	private void UpdateExpanded( string url )
	{
		var expanded = new HashSet<string>( _expanded.Value, StringComparer.Ordinal );
		if ( !expanded.Remove( url ) )
			expanded.Add( url );
		_expanded.Value = expanded;
	}

	/// <summary>Opens a GitHub page through Lokn's link handlers, in a browser panel beside this one when the Browser module is loaded; anything but an https link is ignored.</summary>
	private void UpdateOpen( string url )
	{
		if ( !Uri.TryCreate( url, UriKind.Absolute, out var uri ) || uri.Scheme != Uri.UriSchemeHttps )
			return;

		_context.App.OpenLink( uri, _context.Id );
	}
}
