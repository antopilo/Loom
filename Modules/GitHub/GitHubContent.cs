using Goo;
using Lokn.Core;

namespace Lokn.Modules.GitHub;

/// <summary>Names the kinds of rows in the GitHub panel's list.</summary>
internal enum GitHubRowKind
{
	/// <summary>The watcher's status or error.</summary>
	Status,

	/// <summary>The sign-in action.</summary>
	SignIn,

	/// <summary>Muted text: loading, or a section's empty note.</summary>
	Note,

	/// <summary>A section heading with its count.</summary>
	Section,

	/// <summary>A noticed change.</summary>
	Update,

	/// <summary>A pull request, with its checks when open.</summary>
	Pull,

	/// <summary>A workflow run.</summary>
	Run,
}

/// <summary>One row of the GitHub panel's list.</summary>
/// <param name="Key">The stable row key.</param>
/// <param name="Kind">What the row shows.</param>
/// <param name="Top">The space above the row in logical pixels, which separates sections.</param>
/// <param name="Text">A status, note or section title.</param>
/// <param name="Count">How many items a section holds.</param>
/// <param name="IsError">Whether a status is an error.</param>
/// <param name="IsBusy">Whether sign-in is in progress.</param>
/// <param name="CanClear">Whether a section heading offers Clear.</param>
/// <param name="ShowAuthor">Whether a pull request names its author.</param>
/// <param name="IsOpen">Whether a pull request shows its checks.</param>
/// <param name="Pull">The pull request of a pull request row.</param>
/// <param name="Run">The workflow run of a run row.</param>
/// <param name="Update">The change of an update row.</param>
/// <param name="Now">The time ages are measured from.</param>
internal sealed record GitHubRow( string Key, GitHubRowKind Kind, double Top, string Text = "", int Count = 0, bool IsError = false, bool IsBusy = false, bool CanClear = false, bool ShowAuthor = false, bool IsOpen = false, PullRequestInfo? Pull = null, WorkflowRunInfo? Run = null, GitHubUpdate? Update = null, DateTimeOffset Now = default ) : IMeasuredRow;

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

	/// <summary>Builds one list row; kept as one delegate so the list reuses unchanged rows.</summary>
	private readonly Func<GitHubRow, Blob> _buildRow;

	/// <summary>Creates the panel over the module's watcher and asks it for fresh data.</summary>
	internal GitHubContent( PanelContext context, GitHubWatcher watcher )
	{
		_context = context;
		_watcher = watcher;
		_buildRow = BuildRow;
		_watcher.Refresh();
	}

	/// <summary>Builds sign-in or the changes, pull request, review, and run sections as one virtual list that scrolls.</summary>
	public override Blob Build() => new Virtual
	{
		Source = new MeasuredRowsSource<GitHubRow>( ComputeRows(), _buildRow, ComputeEstimate, FollowEnd: false ),
		AccessibilityRole = AccessibilityRole.Group,
		AccessibilityName = "Pull Requests",
		FlexGrow = 1,
		MinWidth = 0,
		MinHeight = 0,
		Padding = ContentPadding,
		OverflowX = Overflow.Hidden,
		ScrollbarY = ViewStyle.BuildScrollbar(),
	};

	/// <summary>Lists the status, then sign-in, or the Updates, Pull Requests, Review requests and Workflow runs sections with their items or empty notes.</summary>
	private List<GitHubRow> ComputeRows()
	{
		var section = Theme.Current.Spacing * 2;
		var snapshot = _watcher.Snapshot;
		var status = _watcher.Status;
		var needsSignIn = _watcher.NeedsSignIn;
		var rows = new List<GitHubRow>();
		if ( status.Length > 0 )
			rows.Add( new( "status", GitHubRowKind.Status, 0, status, IsError: !needsSignIn ) );
		if ( needsSignIn )
		{
			rows.Add( new( "sign-in", GitHubRowKind.SignIn, rows.Count > 0 ? section : 0, IsBusy: _watcher.IsBusy ) );
			return rows;
		}
		if ( snapshot is null )
		{
			rows.Add( new( "loading", GitHubRowKind.Note, rows.Count > 0 ? section : 0, _watcher.IsBusy ? "Loading your pull requests…" : "Nothing loaded yet." ) );
			return rows;
		}

		var now = DateTimeOffset.Now;
		var cutoff = now - StaleAfter;
		var item = Theme.Current.Spacing / 2;
		var updates = _watcher.Updates;
		if ( updates.Count > 0 )
		{
			rows.Add( new( "Updates", GitHubRowKind.Section, rows.Count > 0 ? section : 0, "Updates", updates.Count, CanClear: true ) );
			foreach ( var update in updates )
				rows.Add( new( $"update:{update.Url}:{update.Kind}:{update.Time.UtcTicks}", GitHubRowKind.Update, item, Update: update, Now: now ) );
		}
		AddPulls( rows, "Pull Requests", snapshot.Mine.Where( pull => pull.UpdatedAt >= cutoff ).ToArray(), now, false, "None of your open pull requests changed in the last three weeks." );
		AddPulls( rows, "Review requests", snapshot.Reviews.Where( pull => pull.UpdatedAt >= cutoff ).ToArray(), now, true, "Nobody is waiting on your review." );
		rows.Add( new( "Workflow runs", GitHubRowKind.Section, section, "Workflow runs", snapshot.Runs.Count ) );
		foreach ( var run in snapshot.Runs )
			rows.Add( new( $"run:{run.Id}", GitHubRowKind.Run, item, Run: run, Now: now ) );
		if ( snapshot.Runs.Count == 0 )
			rows.Add( new( "Workflow runs:empty", GitHubRowKind.Note, item, "No workflow runs. Your open pull requests' repositories show your runs; add repositories to GitHub › Watched repositories in Settings to see everyone's." ) );
		return rows;
	}

	/// <summary>Adds a pull request section: its heading, then its pull requests or an empty note.</summary>
	private void AddPulls( List<GitHubRow> rows, string title, PullRequestInfo[] pulls, DateTimeOffset now, bool showAuthor, string empty )
	{
		var item = Theme.Current.Spacing / 2;
		rows.Add( new( title, GitHubRowKind.Section, rows.Count > 0 ? Theme.Current.Spacing * 2 : 0, title, pulls.Length ) );
		foreach ( var pull in pulls )
			rows.Add( new( $"{title}:{pull.Url}", GitHubRowKind.Pull, item, ShowAuthor: showAuthor, IsOpen: _expanded.Value.Contains( pull.Url ), Pull: pull, Now: now ) );
		if ( pulls.Length == 0 )
			rows.Add( new( title + ":empty", GitHubRowKind.Note, item, empty ) );
	}

	/// <summary>Estimates a row's height until layout measures it.</summary>
	private static double ComputeEstimate( GitHubRow row ) => row.Top + row.Kind switch
	{
		GitHubRowKind.SignIn => Theme.Current.ControlHeight * 3,
		GitHubRowKind.Section => Theme.Current.ControlHeight,
		GitHubRowKind.Update or GitHubRowKind.Pull or GitHubRowKind.Run => Theme.Current.ControlHeight + Theme.Current.Spacing * 2,
		_ => Theme.Current.FontSize * 1.5,
	};

	/// <summary>Builds one list row by its kind, with its space above.</summary>
	private Blob BuildRow( GitHubRow row ) => new Container
	{
		Padding = new Edges( row.Top, 0, 0, 0 ),
		Children =
		[
			row.Kind switch
			{
				GitHubRowKind.Status => (Blob)new Text { Content = row.Text, Color = row.IsError ? Theme.Current.Error : Theme.Current.Text, MinWidth = 0, Selectable = true },
				GitHubRowKind.SignIn => BuildSignIn( row.IsBusy ),
				GitHubRowKind.Note => BuildMuted( row.Text ),
				GitHubRowKind.Section => BuildSection( row.Text, row.Count, row.CanClear ),
				GitHubRowKind.Update => BuildUpdate( row.Update!, row.Now ),
				GitHubRowKind.Pull => BuildPull( row.Pull!, row.Now, row.ShowAuthor, row.IsOpen ),
				_ => BuildRun( row.Run!, row.Now ),
			},
		],
	};

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
	private Container BuildSignIn( bool isBusy ) => new()
	{
		Gap = Theme.Current.Spacing,
		AlignItems = AlignItems.FlexStart,
		Children =
		[
			Theme.Current.BuildButton( "Sign in to GitHub", () => _context.Run( _watcher.SignIn ), !isBusy ),
			BuildMuted( "Opens Git Credential Manager's GitHub sign-in and keeps the credential with Git, where git push also finds it. Lokn stores no token. You can also set GH_TOKEN before starting Lokn." ),
		],
	};

	/// <summary>Builds a section heading with its count, and Clear when the section offers it.</summary>
	private Row BuildSection( string title, int count, bool canClear )
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
		if ( canClear )
		{
			var clear = Theme.Current.BuildButton( "Clear", () => _context.Run( _watcher.ClearUpdates ) );
			clear.Height = Length.Auto;
			clear.MinHeight = Theme.Current.ControlHeight;
			header.Add( clear );
		}

		return new Row { Gap = Theme.Current.Spacing, AlignItems = AlignItems.Center, Children = header };
	}

	/// <summary>Builds one pull request: its check state, title, and details, opening it on click, with its checks on demand.</summary>
	private Container BuildPull( PullRequestInfo pull, DateTimeOffset now, bool showAuthor, bool isOpen )
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
