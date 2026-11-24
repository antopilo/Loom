using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Browser;
using Lokn.Modules.Conversations;
using Lokn.Modules.Projects;
using Lokn.Modules.Workspaces;

namespace Lokn.Modules.Overview;

/// <summary>Shows one project's overview: its chats and tasks with what each is doing, a timeline of its activity, and its designs and reports.</summary>
/// <remarks>
/// The lists follow the stores live: reading chats, working state, questions, notifications and reports during the build rebuilds the page when they change.
/// Designs are read from the project's <c>.lokn\design</c> folder on its host when the page opens, when the folder changes, and when one of the
/// project's agents stops working. Without a project the page lists the projects of the host to choose one.
/// </remarks>
internal sealed class ProjectOverviewContent : PanelContent
{
	/// <summary>Defines the widest the page's column grows, in logical pixels.</summary>
	private const int ColumnWidth = 1040;

	/// <summary>Defines how many recent chats and tasks show under those that need the user, before Show all.</summary>
	private const int RecentLimit = 3;

	/// <summary>Defines how many GitHub updates show.</summary>
	private const int GitHubLimit = 6;

	/// <summary>Defines the GitHub module's pull request panel, opened by id so the page needs no reference to that module.</summary>
	private const string PullRequestsPanel = "github.dashboard";

	/// <summary>Defines an artifact card's width in logical pixels.</summary>
	private const int CardWidth = 150;

	/// <summary>Defines small detail text's size in logical pixels.</summary>
	private const int DetailSize = 11;

	/// <summary>Defines the width each of the page's two columns starts from before it grows, in logical pixels; narrower than both, the lists stack in one.</summary>
	private const int ColumnBasis = 320;

	/// <summary>Defines the height assumed for a page row until layout measures it, in logical pixels.</summary>
	private const double EstimatedRowHeight = 34;

	/// <summary>Defines how long the design folder must be quiet before it is read again, in milliseconds.</summary>
	private const int RescanDelay = 600;

	/// <summary>Stores the panel's dispatch and host.</summary>
	private readonly PanelContext _context;

	/// <summary>Stores the module whose chats, tasks and projects the page lists and opens.</summary>
	private readonly ConversationsModule _conversations;

	/// <summary>Stores the module whose reports the page lists and shows.</summary>
	private readonly BrowserModule _browser;

	/// <summary>Stores the project shown, or null while the page lists projects to choose from.</summary>
	private readonly State<Guid?> _projectId;

	/// <summary>Stores whether every chat and task shows rather than the first <see cref="ChatLimit"/>.</summary>
	private readonly State<bool> _isShowingAll = new( false );

	/// <summary>Stores the project's designs as last read, newest first.</summary>
	private readonly State<IReadOnlyList<DesignEntry>> _designs = new( [] );

	/// <summary>Stores a failure to read or open something, or empty.</summary>
	private readonly State<string> _status = new( "" );

	/// <summary>Targets the page to measure whether its columns fit side by side and how many artifact cards fit in a row.</summary>
	private readonly ElementHandle _page = new();

	/// <summary>Stores whether the chats and the designs, reports and GitHub updates fit side by side.</summary>
	private readonly State<bool> _isWide = new( true );

	/// <summary>Stores how many artifact cards fit in a row.</summary>
	private readonly State<int> _cardColumns = new( 2 );

	/// <summary>Stores the watch on the project's design folder, or null.</summary>
	private IDisposable? _designWatch;

	/// <summary>Stores the folder <see cref="_designWatch"/> watches.</summary>
	private string? _watchedFolder;

	/// <summary>Stores the pending design read after a change, cancelled by the next change.</summary>
	private CancellationTokenSource? _rescan;

	/// <summary>Stores what is typed in the prompt bar.</summary>
	private readonly State<string> _prompt = new( "" );

	/// <summary>Stores the project's checked-out branch, or empty when it is no git repository.</summary>
	private readonly State<string> _branch = new( "" );

	/// <summary>Stores the project's GitHub repository as <c>owner/name</c>, read from its <c>origin</c> remote, or empty for none.</summary>
	private readonly State<string> _repository = new( "" );

	/// <summary>Stores the chat host whose working changes the page follows.</summary>
	private ChatHost? _followed;

	/// <summary>Creates the page for the project its input or restored state names, on its panel's host.</summary>
	public ProjectOverviewContent( PanelContext context, ConversationsModule conversations, BrowserModule browser )
	{
		_context = context;
		_conversations = conversations;
		_browser = browser;
		Guid? project = context.Input as Guid?;
		if ( project is null && context.RestoredState is { ValueKind: JsonValueKind.String } saved && Guid.TryParse( saved.GetString(), out var restored ) )
			project = restored;
		_projectId = new( project );
		Follow();
		ScanDesigns();
		_ = ReadGitAsync();
		context.Lifetime.Register( Release );
		_page.MetricsChanged += metrics =>
		{
			if ( !metrics.IsMounted )
				return;

			var theme = Theme.Current;
			var width = Math.Min( metrics.ContentBox.Width, ColumnWidth );
			var gap = theme.Spacing * 4;
			var isWide = width >= ColumnBasis * 2 + gap;
			var side = isWide ? ColumnBasis + (width - ColumnBasis * 2 - gap) * 2 / 5 : width;
			var cardGap = theme.Spacing * 1.5;
			var columns = Math.Max( 1, (int)((side + cardGap) / (CardWidth + cardGap)) );
			if ( isWide != _isWide.Value || columns != _cardColumns.Value )
			{
				_context.Post( () =>
				{
					_isWide.Value = isWide;
					_cardColumns.Value = columns;
				} );
			}
		};
	}

	/// <summary>Gets the project shown, or null while the page lists projects.</summary>
	public Guid? ProjectId => _projectId.Value;

	/// <summary>Gets the panel's id.</summary>
	public Guid Id => _context.Id;

	/// <summary>Gets the id of the host the page's project is on.</summary>
	public string ProjectHostId => _context.Host.Id;

	/// <summary>Reports whether the panel closed.</summary>
	public bool IsClosed => _context.Lifetime.IsCancellationRequested;

	/// <summary>Gets the chats of the panel's host, or null before the host's chats are known.</summary>
	private ChatHost? ChatHost => _conversations.Hosts.Find( _context.Host.Id );

	/// <summary>Remembers the project shown.</summary>
	public override JsonElement? CaptureState() => _projectId.Value is { } id ? JsonSerializer.SerializeToElement( id.ToString( "N" ) ) : null;

	/// <summary>Builds the page: the project's header, then its prompt bar and chats beside its designs, reports and GitHub updates, each list building only
	/// the rows in view; narrower than both columns, the lists stack in one.</summary>
	public override Blob Build()
	{
		var theme = Theme.Current;
		var host = ChatHost;
		var project = host is not null && _projectId.Value is { } id ? host.Projects.Find( id ) : null;
		Blob? header = null;
		List<BuiltRow> main, side = [];
		if ( host is null )
		{
			main = [new( "waiting", () => BuildNote( "waiting", $"Waiting for {_context.Host.Name}'s chats…" ) )];
		}
		else if ( project is null )
		{
			header = new Text { Key = "title", Content = "Project overview", FontSize = 22, FontWeight = 600 };
			main = BuildChooser( host );
		}
		else
		{
			header = BuildHeader( host, project );
			(main, side) = BuildProject( host, project );
		}
		if ( _status.Value is { Length: > 0 } status )
			main.Add( new( "status", () => BuildNote( "status", status ) ) );

		Blob lists = _isWide.Value && side.Count > 0
			? new Row
			{
				Key = "lists",
				FlexGrow = 1,
				MinHeight = 0,
				Gap = theme.Spacing * 4,
				Children = [BuildList( "main", main, 3, ColumnBasis ), BuildList( "side", side, 2, ColumnBasis )],
			}
			: BuildList( "rows", [.. main, .. side], 1, 0 );
		return new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = project is null ? "Project overview" : $"{project.Name} overview",
			Handle = _page,
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			AlignItems = AlignItems.Center,
			Padding = new Edges( 28, 32, 0, 32 ),
			Color = theme.Text,
			Children =
			[
				new Column
				{
					Key = "column",
					Width = Length.Percent( 100 ),
					MaxWidth = ColumnWidth,
					FlexGrow = 1,
					MinHeight = 0,
					Gap = theme.Spacing * 3,
					Children = header is null ? [lists] : [header, lists],
				},
			],
		};
	}

	/// <summary>Builds a list of the page that builds only the rows in view, with its own scrollbar.</summary>
	private static Virtual BuildList( string key, List<BuiltRow> rows, double flexGrow, double flexBasis ) => new()
	{
		Key = key,
		Source = new MeasuredRowsSource<BuiltRow>( rows, BuildPageRow, static _ => EstimatedRowHeight, FollowEnd: false ),
		FlexGrow = flexGrow,
		FlexBasis = flexBasis,
		MinWidth = 0,
		MinHeight = 0,
		Padding = new Edges( 0, 0, 28, 0 ),
		OverflowX = Overflow.Hidden,
		ScrollbarY = ViewStyle.BuildScrollbar(),
	};

	/// <summary>Builds one row of a list with the gap below it.</summary>
	private static Container BuildPageRow( BuiltRow row ) => new() { Padding = new Edges( 0, 0, 2, 0 ), Children = [row.Build()] };

	/// <summary>Lists the rows of a project's overview: its prompt bar and chats, and beside them its designs and reports, then its GitHub updates.</summary>
	private (List<BuiltRow> Main, List<BuiltRow> Side) BuildProject( ChatHost host, ProjectInfo project )
	{
		var theme = Theme.Current;
		var chats = ComputeChats( host, project );
		var reports = ComputeReports( host, chats );
		var designs = _designs.Value;
		var now = DateTimeOffset.UtcNow;
		List<BuiltRow> main = [new( "prompt", () => BuildPrompt( host, project ) ), .. BuildChats( host, project, chats, now )];
		List<BuiltRow> side = [BuildSection( "artifacts", "Designs and reports", _isWide.Value ? 0 : theme.Spacing * 4 ), .. BuildArtifacts( host, project, chats, designs, reports, now )];
		if ( _repository.Value.Length > 0 )
			side.AddRange( [BuildSection( "github", "GitHub", theme.Spacing * 3 ), .. BuildGitHub( now )] );
		return (main, side);
	}

	/// <summary>Builds the project's icon, name, folder and branch.</summary>
	private Blob BuildHeader( ChatHost host, ProjectInfo project )
	{
		var theme = Theme.Current;
		var icon = _context.App.GetModule<ProjectsModule>().BuildProjectIcon( project, host.Id, 34 );
		var folder = host.IsLocal ? FormatDirectory( project.Directory ) : $"{project.Directory}  ·  {host.Name}";
		if ( _branch.Value.Length > 0 )
			folder += $"  ·  \u2387 {_branch.Value}";
		return new Row
		{
			Key = "header",
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing * 1.5,
			MinWidth = 0,
			Children =
			[
				.. icon is null ? Array.Empty<Blob>() : [icon],
				new Column
				{
					MinWidth = 0,
					FlexShrink = 1,
					Gap = 2,
					Children =
					[
						new Text { Key = "name", Content = project.Name, FontFamily = ViewStyle.FontFamily, FontSize = 22, FontWeight = 600, MinWidth = 0, TextMaxLines = 1, TextTrimming = TextTrimming.Ellipsis },
						new Text { Key = "folder", Content = folder, FontSize = DetailSize + 1, Color = theme.Muted, MinWidth = 0, TextMaxLines = 1, TextTrimming = TextTrimming.Ellipsis },
					],
				},
			],
		};
	}

	/// <summary>Builds the prompt bar: Enter starts a chat in the project with what was typed, Ctrl+Enter a task named after it.</summary>
	private Blob BuildPrompt( ChatHost host, ProjectInfo project )
	{
		var theme = Theme.Current;
		const int height = 44;
		return new TextEntry
		{
			Key = "prompt",
			Value = _prompt.Value,
			Controlled = true,
			Placeholder = $"Ask anything about {project.Name}…   Enter: chat   Ctrl+Enter: task",
			AccessibilityName = $"Start a chat in {project.Name}",
			Width = Length.Percent( 100 ),
			Height = height,
			FlexShrink = 0,
			Padding = new Edges( 0, 14 ),
			FontSize = theme.FontSize + 1,
			BackgroundColor = theme.Raised,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = 12,
			FocusStyle = new Style { BorderColor = theme.Accent },
			OnChange = value => _context.Run( () => _prompt.Value = value ),
			OnSubmit = value => _context.Run( () => UpdateStart( host, project, value, isTask: false ) ),
			KeyBindings = [new( Key.Enter, KeyModifiers.Primary, () => _context.Run( () => UpdateStart( host, project, _prompt.Value, isTask: true ) ) )],
		};
	}

	/// <summary>Starts a chat that sends the typed prompt, or a task named after it, in the project, and empties the bar.</summary>
	private void UpdateStart( ChatHost host, ProjectInfo project, string text, bool isTask )
	{
		text = text.Trim();
		if ( text.Length == 0 )
			return;
		_prompt.Value = "";
		if ( isTask )
			_conversations.NewTask( _context.App, host, project.Id, text.Split( '\n' )[0] );
		else
			_conversations.StartConversation( _context.App, host, text, project.Id );
	}

	/// <summary>Lists the rows of the project's chats and tasks: those waiting for the user or working under Needs you, then the few most recent, and a link to all of them.</summary>
	private List<BuiltRow> BuildChats( ChatHost host, ProjectInfo project, IReadOnlyList<ChatEntry> chats, DateTimeOffset now )
	{
		if ( chats.Count == 0 )
			return [new( "chats-none", () => BuildNote( "none", "No chats or tasks yet. Type above to start one." ) )];

		BuiltRow Row( ChatEntry chat ) => new( "chat:" + chat.Conversation.Id.ToString( "N" ), () => BuildRow(
			chat.Conversation.Id.ToString( "N" ),
			chat.Conversation.HasChat ? "\uE8BD" : "\uE9D5",
			chat.Conversation.Title,
			chat.State switch
			{
				ChatState.Waiting => "Waiting for you",
				ChatState.Working => "Working",
				_ => FormatAge( chat.Conversation.UpdatedAt, now ),
			},
			chat.State switch
			{
				ChatState.Waiting => Theme.Current.Attention,
				ChatState.Working => Theme.Current.Accent,
				_ => Theme.Current.Inactive,
			},
			$"Open {chat.Conversation.Title}",
			() => OpenChat( host.Ref( chat.Conversation.Id ) ) ) );

		var active = chats.Where( chat => chat.State != ChatState.Idle ).ToList();
		var idle = chats.Where( chat => chat.State == ChatState.Idle ).ToList();
		var gap = Theme.Current.Spacing * 2;
		var rows = new List<BuiltRow>();
		if ( active.Count > 0 )
			rows.AddRange( [BuildSection( "needs-you", "Needs you", gap ), .. active.Select( Row )] );
		if ( idle.Count > 0 )
		{
			rows.AddRange( [BuildSection( "recent", "Recent", gap ), .. (_isShowingAll.Value ? idle : idle.Take( RecentLimit )).Select( Row )] );
			if ( idle.Count > RecentLimit )
			{
				var label = _isShowingAll.Value ? "Show less" : $"All {chats.Count} chats and tasks";
				rows.Add( new( "more", () => BuildLink( "more", label, () => _isShowingAll.Value = !_isShowingAll.Value ) ) );
			}
		}
		return rows;
	}

	/// <summary>Lists the rows of the GitHub updates of the project's repository from the notification feed, newest first, and a link to the pull requests.</summary>
	/// <remarks>The GitHub module posts what changed on pull requests and runs to the feed with their page; the repository's are those whose page is under it.</remarks>
	private List<BuiltRow> BuildGitHub( DateTimeOffset now )
	{
		var theme = Theme.Current;
		var repository = _repository.Value;
		var prefix = $"https://github.com/{repository}/";
		var items = _context.App.Notifications.Items
			.Where( item => item.Source == "github" && item.Url is { } url && url.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) )
			.Take( GitHubLimit )
			.ToList();
		var rows = new List<BuiltRow>();
		if ( items.Count == 0 )
			rows.Add( new( "github-none", () => BuildNote( "none", $"No pull request comments, reviews or checks on {repository} yet." ) ) );
		foreach ( var item in items )
		{
			var link = new Uri( item.Url! );
			rows.Add( new( $"github-{item.Id:N}", () => BuildRow( $"github-{item.Id:N}", item.IsRead ? "\uE8BD" : "\uE91F", item.Message, FormatAge( item.Time, now ),
				item.IsRead ? theme.Inactive : theme.Attention, item.Message, () => _context.App.OpenLink( link, _context.Id ) ) ) );
		}
		if ( _context.App.Panels.Types.Any( type => type.Id == PullRequestsPanel ) )
			rows.Add( new( "pulls", () => BuildLink( "pulls", "Open pull requests", () => _context.App.Panels.OpenBeside( PullRequestsPanel, _context.Id ) ) ) );
		return rows;
	}

	/// <summary>Reads the project's branch and GitHub repository with git on its host; a folder that is no repository leaves both empty.</summary>
	private async Task ReadGitAsync()
	{
		var host = ChatHost;
		if ( host is null || _projectId.Value is not { } id || host.Projects.Find( id ) is not { } project )
			return;
		var workspace = _context.Host.Workspace;
		async Task<string> RunAsync( params string[] arguments )
		{
			try
			{
				var result = await workspace.RunAsync( "git", arguments, project.Directory, null, null, _context.Lifetime );
				return result.Code == 0 ? result.Output.Trim() : "";
			}
			catch ( Exception error ) when ( error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException )
			{
				return "";
			}
		}
		var branch = await RunAsync( "rev-parse", "--abbrev-ref", "HEAD" );
		var remote = await RunAsync( "remote", "get-url", "origin" );
		_context.Post( () =>
		{
			_branch.Value = branch == "HEAD" ? "detached" : branch;
			_repository.Value = ParseGitHubRepository( remote ) ?? "";
		} );
	}

	/// <summary>Reads <c>owner/name</c> from a GitHub remote address, https or ssh, or null for another host.</summary>
	internal static string? ParseGitHubRepository( string remote )
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

	/// <summary>Lists the rows of cards of the project's designs, newest first, then its reports, newest first, as many cards to a row as fit.</summary>
	private List<BuiltRow> BuildArtifacts( ChatHost host, ProjectInfo project, IReadOnlyList<ChatEntry> chats, IReadOnlyList<DesignEntry> designs, IReadOnlyList<BrowserReport> reports, DateTimeOffset now )
	{
		var titles = chats.ToDictionary( chat => chat.Conversation.Id, chat => chat.Conversation.Title );
		var cards = new List<(string Key, Func<Blob> Build)>();
		foreach ( var design in designs )
		{
			var variants = design.Variants switch { 0 => "one page", 1 => "1 variant", var count => $"{count} variants" };
			var key = $"design:{design.Name}";
			cards.Add( (key, () => BuildCard( key, "\uE790", design.Name, $"Design  ·  {variants}", FormatAge( design.Modified, now ),
				host.IsLocal ? $"Open design {design.Name} in the studio" : $"Design {design.Name} on {host.Name}",
				host.IsLocal ? () => OpenDesign( host, design ) : null )) );
		}
		foreach ( var report in reports )
		{
			var from = report.Conversation is { } id && titles.TryGetValue( id, out var title ) ? title : "Report";
			var key = $"report:{report.Path}";
			cards.Add( (key, () => BuildCard( key, "\uE9F9", report.Title, from, FormatAge( report.Created, now ), $"Show report {report.Title}",
				() => _browser.ShowReport( _context.App, report, _context.Id ) )) );
		}
		if ( cards.Count == 0 )
		{
			return [new( "artifacts-none", () => BuildNote( "none", host.IsLocal
				? "No designs or reports yet. Agents add them with the design tools and browser_show."
				: $"No designs yet. Reports of {host.Name}'s chats show on that PC." ) )];
		}

		var gap = Theme.Current.Spacing * 1.5;
		return [.. cards.Chunk( _cardColumns.Value ).Select( row => new BuiltRow( "cards:" + row[0].Key,
			() => new Row { Gap = gap, Padding = new Edges( 0, 0, gap, 0 ), Children = [.. row.Select( card => card.Build() )] } ) )];
	}

	/// <summary>Lists the rows of the host's projects to choose one, for a page opened outside a project.</summary>
	private List<BuiltRow> BuildChooser( ChatHost host )
	{
		var theme = Theme.Current;
		List<BuiltRow> rows =
		[
			BuildSection( "projects", "Choose a project", 0 ),
			.. host.Projects.Projects( _context.App.Settings.ActiveProfile ).Select( project => new BuiltRow( "project:" + project.Id.ToString( "N" ),
				() => BuildRow( project.Id.ToString( "N" ), "\uE8B7", project.Name, host.IsLocal ? FormatDirectory( project.Directory ) : project.Directory, theme.Inactive,
					$"Show the overview of {project.Name}", () => UpdateProject( project.Id ) ) ) ),
		];
		if ( rows.Count == 1 )
			rows.Add( new( "projects-none", () => BuildNote( "none", "Add a project in the sidebar first." ) ) );
		return rows;
	}

	/// <summary>Opens the chosen project's overview on its own page, then closes this list, which has done its work.</summary>
	private void UpdateProject( Guid projectId )
	{
		var host = _context.Host;
		_context.App.OpenLink( LoknLinks.Create( "overview", "project", host.IsLocal ? projectId.ToString( "N" ) : $"{projectId:N}@{host.Id}" ), _context.Id );
		_context.App.Panels.Close( _context.Id );
	}

	/// <summary>Lists the project's chats and tasks that are not archived: waiting ones first, then working ones, then the rest by last use.</summary>
	private static List<ChatEntry> ComputeChats( ChatHost host, ProjectInfo project )
	{
		var waiting = host.Sessions.Waiting.Select( session => session.ConversationId ).ToHashSet();
		return [.. host.Store.Conversations( project.Profile, project.Id )
			.Where( conversation => !conversation.IsArchived )
			.Select( conversation => new ChatEntry( conversation,
				waiting.Contains( conversation.Id ) ? ChatState.Waiting
				: host.Store.IsWorking( conversation.Id ) ? ChatState.Working
				: ChatState.Idle ) )
			.OrderBy( chat => chat.State )
			.ThenByDescending( chat => chat.Conversation.UpdatedAt )];
	}

	/// <summary>Lists the reports the project's chats showed on This PC, newest first.</summary>
	private List<BrowserReport> ComputeReports( ChatHost host, IReadOnlyList<ChatEntry> chats )
	{
		var ids = chats.Select( chat => chat.Conversation.Id ).ToHashSet();
		return [.. _browser.Reports.Where( report => report.Host == host.Id && report.Conversation is { } id && ids.Contains( id ) )];
	}

	/// <summary>Opens a chat or task in its own workspace, as the sidebar does.</summary>
	private void OpenChat( ChatRef chat ) => _conversations.SelectConversation( _context.App, chat );

	/// <summary>Opens a This PC design in the Design studio beside this page, through the Design module's tool.</summary>
	private void OpenDesign( ChatHost host, DesignEntry design )
	{
		if ( host.IsLocal )
			_ = OpenDesignAsync( design );
	}

	/// <summary>Asks the Design module to show a design's studio and reports why it could not.</summary>
	private async Task OpenDesignAsync( DesignEntry design )
	{
		var arguments = JsonSerializer.SerializeToElement( new { name = design.Name, folder = design.Project } );
		var result = await _context.App.Tools.InvokeAsync( "design_open", arguments, _context.Id, _context.Lifetime );
		_context.Post( () => _status.Value = result.IsError ? $"Could not open design {design.Name}: {result.Text}" : "" );
	}

	/// <summary>Reads the project's designs from its <c>.lokn\design</c> folder on its host, and watches the folder for changes.</summary>
	private void ScanDesigns()
	{
		_rescan?.Cancel();
		_rescan = CancellationTokenSource.CreateLinkedTokenSource( _context.Lifetime );
		var host = ChatHost;
		if ( host is null || _projectId.Value is not { } id || host.Projects.Find( id ) is not { } project )
		{
			_designs.Value = [];
			return;
		}
		_ = ScanDesignsAsync( _context.Host.Workspace, project.Directory, _rescan.Token );
	}

	/// <summary>Lists each design folder with its variants and newest change, then shows them on the UI thread.</summary>
	private async Task ScanDesignsAsync( IWorkspaceHost workspace, string project, CancellationToken cancellation )
	{
		var root = Path.Combine( project, ".lokn", "design" );
		var designs = new List<DesignEntry>();
		try
		{
			if ( await workspace.DirectoryExistsAsync( root, cancellation ) )
			{
				foreach ( var folder in (await workspace.ListAsync( root, cancellation )).Where( entry => entry.IsDirectory ) )
				{
					var entries = await workspace.ListAsync( Path.Combine( root, folder.Name ), cancellation );
					var variants = entries.Count( entry => entry.IsDirectory && entry.Name != "exports" );
					var modified = entries.Select( entry => entry.Modified ).Append( folder.Modified ).Max();
					designs.Add( new DesignEntry( folder.Name, project, variants, new DateTimeOffset( DateTime.SpecifyKind( modified, DateTimeKind.Local ) ) ) );
				}
			}
		}
		catch ( OperationCanceledException )
		{
			return;
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			_context.Post( () => _status.Value = $"Could not read the designs: {error.Message}" );
			return;
		}

		_context.Post( () =>
		{
			if ( cancellation.IsCancellationRequested )
				return;
			_designs.Value = [.. designs.OrderByDescending( design => design.Modified )];
			Watch( workspace, designs.Count > 0 ? root : project );
		} );
	}

	/// <summary>Watches a folder for changes that may add or change designs: the design folder once it exists, else the project folder's top level.</summary>
	private void Watch( IWorkspaceHost workspace, string folder )
	{
		if ( _watchedFolder == folder )
			return;
		_designWatch?.Dispose();
		_designWatch = null;
		_watchedFolder = folder;
		try
		{
			_designWatch = workspace.Watch( folder, recursive: folder.EndsWith( "design", StringComparison.OrdinalIgnoreCase ), ( path, _ ) =>
			{
				if ( path.Contains( $"{Path.DirectorySeparatorChar}.lokn", StringComparison.OrdinalIgnoreCase ) )
					_context.Post( ScheduleScan );
			} );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException )
		{
			_watchedFolder = null;
		}
	}

	/// <summary>Reads the designs again once their folder is quiet for <see cref="RescanDelay"/>.</summary>
	private void ScheduleScan()
	{
		_rescan?.Cancel();
		_rescan = CancellationTokenSource.CreateLinkedTokenSource( _context.Lifetime );
		var token = _rescan.Token;
		_ = Task.Delay( RescanDelay, token ).ContinueWith( _ => _context.Post( () =>
		{
			if ( !token.IsCancellationRequested )
				ScanDesigns();
		} ), TaskContinuationOptions.OnlyOnRanToCompletion );
	}

	/// <summary>Follows the host's agents, so the designs are read again when one of the project's agents stops working.</summary>
	private void Follow()
	{
		_followed = ChatHost;
		if ( _followed is not null )
			_followed.Store.WorkingChanged += OnWorkingChanged;
	}

	/// <summary>Reads the designs again when an agent of the project stops working, since it may have written one.</summary>
	private void OnWorkingChanged( Guid conversationId, bool isWorking )
	{
		if ( isWorking || _followed is not { } host || _projectId.Value is not { } id )
			return;
		if ( host.Store.Find( conversationId ) is { ProjectId: { } project } && project == id )
			_context.Post( ScheduleScan );
	}

	/// <summary>Stops following the host and the design folder once the panel closes.</summary>
	private void Release()
	{
		if ( _followed is not null )
			_followed.Store.WorkingChanged -= OnWorkingChanged;
		_followed = null;
		_designWatch?.Dispose();
		_designWatch = null;
		_rescan?.Cancel();
	}

	/// <summary>Builds the title row of a section of the page, set apart from what comes before by <paramref name="top"/> logical pixels.</summary>
	private static BuiltRow BuildSection( string key, string title, double top ) => new( key, () => new Text
	{
		Key = "title",
		Content = title,
		FontSize = DetailSize + 1,
		FontWeight = 600,
		Color = Theme.Current.Muted,
		Margin = new Edges( top, 0, 6, 0 ),
	} );

	/// <summary>Builds a clickable row: an icon, a title and a muted detail on the right.</summary>
	private Button BuildRow( string key, string glyph, string title, string detail, Color detailColor, string name, Action open )
	{
		var theme = Theme.Current;
		void Run() => _context.Run( open );
		return new Button
		{
			Key = key,
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			MinHeight = 32,
			Padding = new Edges( 0, 10 ),
			BorderRadius = theme.Radius + 1,
			BackgroundColor = Color.Transparent,
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			FocusStyle = new Style { BackgroundColor = theme.Hover },
			Cursor = Cursor.Pointer,
			Focusable = true,
			AccessibilityName = name,
			OnClick = Run,
			KeyBindings = [new( Key.Enter, KeyModifiers.None, Run ), new( Key.Space, KeyModifiers.None, Run )],
			Children =
			[
				SidebarRows.BuildIcon( glyph ),
				new Text { Content = title, MinWidth = 0, FlexShrink = 1, FlexGrow = 1, TextMaxLines = 1, TextTrimming = TextTrimming.Ellipsis },
				new Text { Content = detail, FontSize = DetailSize, Color = detailColor, TextMaxLines = 1, FlexShrink = 0 },
			],
		};
	}

	/// <summary>Builds a quiet card for a design or report, which opens it when clicked; a card that cannot open is drawn muted.</summary>
	private Button BuildCard( string key, string glyph, string title, string detail, string footnote, string name, Action? open )
	{
		var theme = Theme.Current;
		void Run()
		{
			if ( open is not null )
				_context.Run( open );
		}
		return new Button
		{
			Key = key,
			Width = CardWidth,
			MinHeight = 88,
			Padding = new Edges( 12, 14 ),
			Gap = 3,
			FlexDirection = FlexDirection.Column,
			AlignItems = AlignItems.Stretch,
			BorderRadius = theme.Radius + 3,
			BackgroundColor = theme.Raised,
			HoverStyle = open is null ? null : new Style { BackgroundColor = theme.Hover },
			FocusStyle = new Style { BorderColor = theme.Accent },
			BorderWidth = theme.BorderWidth,
			BorderColor = Color.Transparent,
			Cursor = open is null ? Cursor.Default : Cursor.Pointer,
			Focusable = true,
			AccessibilityName = name,
			OnClick = Run,
			KeyBindings = [new( Key.Enter, KeyModifiers.None, Run ), new( Key.Space, KeyModifiers.None, Run )],
			Children =
			[
				new Row
				{
					AlignItems = AlignItems.Center,
					Gap = theme.Spacing,
					MinWidth = 0,
					Children =
					[
						SidebarRows.BuildIcon( glyph ),
						new Text { Content = title, FontWeight = 600, MinWidth = 0, FlexShrink = 1, TextMaxLines = 1, TextTrimming = TextTrimming.Ellipsis, Color = open is null ? theme.Muted : theme.Text },
					],
				},
				new Text { Content = detail, FontSize = DetailSize, Color = theme.Muted, MinWidth = 0, TextMaxLines = 1 },
				new Spacer(),
				new Text { Content = footnote, FontSize = DetailSize, Color = theme.Inactive, MinWidth = 0, TextMaxLines = 1 },
			],
		};
	}

	/// <summary>Builds a small text link, such as Show all.</summary>
	private Button BuildLink( string key, string label, Action action )
	{
		var theme = Theme.Current;
		void Run() => _context.Run( action );
		return new Button
		{
			Key = key,
			AlignSelf = AlignSelf.FlexStart,
			Padding = new Edges( 4, 10 ),
			BorderRadius = theme.Radius,
			BackgroundColor = Color.Transparent,
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			Cursor = Cursor.Pointer,
			Focusable = true,
			AccessibilityName = label,
			OnClick = Run,
			KeyBindings = [new( Key.Enter, KeyModifiers.None, Run )],
			Children = [new Text { Content = label, FontSize = DetailSize + 1, Color = theme.Accent }],
		};
	}

	/// <summary>Builds a muted line of explanation.</summary>
	private static Text BuildNote( string key, string text ) => new()
	{
		Key = key,
		Content = text,
		Color = Theme.Current.Muted,
		TextWrap = TextWrap.Wrap,
		Padding = new Edges( 6, 10 ),
	};

	/// <summary>Formats how long ago something happened, in the largest whole unit.</summary>
	internal static string FormatAge( DateTimeOffset date, DateTimeOffset now )
	{
		var age = now - date;
		return age.TotalMinutes < 1 ? "just now"
			: age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
			: age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
			: age.TotalDays < 2 ? "yesterday"
			: age.TotalDays < 60 ? $"{(int)age.TotalDays} days ago"
			: date.ToLocalTime().ToString( "MMM d, yyyy" );
	}

	/// <summary>Shortens a directory under the user's profile to start with ~.</summary>
	private static string FormatDirectory( string directory )
	{
		var profile = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
		return profile.Length > 0 && directory.StartsWith( profile, StringComparison.OrdinalIgnoreCase ) ? "~" + directory[profile.Length..] : directory;
	}

	/// <summary>Orders a chat's state on the page: chats waiting for the user first, then working ones, then the rest.</summary>
	private enum ChatState
	{
		/// <summary>Its agent waits on a question for the user.</summary>
		Waiting,

		/// <summary>Its agent works now.</summary>
		Working,

		/// <summary>Nothing runs in it.</summary>
		Idle,
	}

	/// <summary>Holds one of the project's chats or tasks with what it is doing.</summary>
	private sealed record ChatEntry( ConversationInfo Conversation, ChatState State );

	/// <summary>Holds one of the project's designs as read from its folder.</summary>
	/// <param name="Name">The design's name, its folder's name.</param>
	/// <param name="Project">The project folder the design is in.</param>
	/// <param name="Variants">How many variant folders it has; 0 for a single page.</param>
	/// <param name="Modified">When its folder or a file at its top last changed.</param>
	private sealed record DesignEntry( string Name, string Project, int Variants, DateTimeOffset Modified );
}
