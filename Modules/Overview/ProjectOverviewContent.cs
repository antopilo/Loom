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

	/// <summary>Builds the page: the project's header, then its prompt bar and chats beside its designs, reports and GitHub updates.</summary>
	public override Blob Build()
	{
		var theme = Theme.Current;
		var host = ChatHost;
		var project = host is not null && _projectId.Value is { } id ? host.Projects.Find( id ) : null;
		List<Blob> children = host is null ? [BuildNote( "waiting", $"Waiting for {_context.Host.Name}'s chats…" )]
			: project is null ? BuildChooser( host )
			: BuildProject( host, project );
		if ( _status.Value.Length > 0 )
			children.Add( BuildNote( "status", _status.Value ) );

		return new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = project is null ? "Project overview" : $"{project.Name} overview",
			FlexGrow = 1,
			MinHeight = 0,
			OverflowY = Overflow.Scroll,
			AlignItems = AlignItems.Center,
			Padding = new Edges( 28, 32 ),
			Color = theme.Text,
			Children =
			[
				new Column
				{
					Key = "column",
					Width = Length.Percent( 100 ),
					MaxWidth = ColumnWidth,
					Gap = theme.Spacing * 3,
					Children = children,
				},
			],
		};
	}

	/// <summary>Builds the overview of a project.</summary>
	private List<Blob> BuildProject( ChatHost host, ProjectInfo project )
	{
		var theme = Theme.Current;
		var chats = ComputeChats( host, project );
		var reports = ComputeReports( host, chats );
		var designs = _designs.Value;
		var now = DateTimeOffset.UtcNow;
		var side = new List<Blob> { BuildSection( "artifacts", "Designs and reports", BuildArtifacts( host, project, chats, designs, reports, now ) ) };
		if ( _repository.Value.Length > 0 )
			side.Add( BuildSection( "github", "GitHub", BuildGitHub( now ) ) );
		return
		[
			BuildHeader( host, project ),
			new Row
			{
				Key = "lists",
				FlexWrap = FlexWrap.Wrap,
				AlignItems = AlignItems.FlexStart,
				Gap = theme.Spacing * 4,
				Children =
				[
					new Column
					{
						Key = "main",
						FlexGrow = 3,
						FlexBasis = 320,
						MinWidth = 0,
						Gap = theme.Spacing * 2,
						Children = [BuildPrompt( host, project ), .. BuildChats( host, project, chats, now )],
					},
					new Column { Key = "side", FlexGrow = 2, FlexBasis = 320, MinWidth = 0, Gap = theme.Spacing * 3, Children = side },
				],
			},
		];
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
						new Text { Key = "name", Content = project.Name, FontFamily = ViewStyle.FontFamily, FontSize = 22, FontWeight = 600, TextMaxLines = 1 },
						new Text { Key = "folder", Content = folder, FontSize = DetailSize + 1, Color = theme.Muted, TextMaxLines = 1 },
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

	/// <summary>Builds the project's chats and tasks: those waiting for the user or working under Needs you, then the few most recent, and a link to all of them.</summary>
	private List<Blob> BuildChats( ChatHost host, ProjectInfo project, IReadOnlyList<ChatEntry> chats, DateTimeOffset now )
	{
		if ( chats.Count == 0 )
			return [BuildNote( "none", "No chats or tasks yet. Type above to start one." )];

		Blob Row( ChatEntry chat ) => BuildRow(
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
			() => OpenChat( host.Ref( chat.Conversation.Id ) ) );

		var active = chats.Where( chat => chat.State != ChatState.Idle ).ToList();
		var idle = chats.Where( chat => chat.State == ChatState.Idle ).ToList();
		var sections = new List<Blob>();
		if ( active.Count > 0 )
			sections.Add( BuildSection( "needs-you", "Needs you", [.. active.Select( Row )] ) );
		if ( idle.Count > 0 )
		{
			List<Blob> rows = [.. (_isShowingAll.Value ? idle : idle.Take( RecentLimit )).Select( Row )];
			if ( idle.Count > RecentLimit )
				rows.Add( BuildLink( "more", _isShowingAll.Value ? "Show less" : $"All {chats.Count} chats and tasks", () => _isShowingAll.Value = !_isShowingAll.Value ) );
			sections.Add( BuildSection( "recent", "Recent", rows ) );
		}
		return sections;
	}

	/// <summary>Builds the GitHub updates of the project's repository from the notification feed, newest first, and a link to the pull requests.</summary>
	/// <remarks>The GitHub module posts what changed on pull requests and runs to the feed with their page; the repository's are those whose page is under it.</remarks>
	private List<Blob> BuildGitHub( DateTimeOffset now )
	{
		var theme = Theme.Current;
		var prefix = $"https://github.com/{_repository.Value}/";
		var items = _context.App.Notifications.Items
			.Where( item => item.Source == "github" && item.Url is { } url && url.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) )
			.Take( GitHubLimit )
			.ToList();
		var rows = new List<Blob>();
		if ( items.Count == 0 )
			rows.Add( BuildNote( "none", $"No pull request comments, reviews or checks on {_repository.Value} yet." ) );
		foreach ( var item in items )
		{
			var link = new Uri( item.Url! );
			rows.Add( BuildRow( $"github-{item.Id:N}", item.IsRead ? "\uE8BD" : "\uE91F", item.Message, FormatAge( item.Time, now ),
				item.IsRead ? theme.Inactive : theme.Attention, item.Message, () => _context.App.OpenLink( link, _context.Id ) ) );
		}
		if ( _context.App.Panels.Types.Any( type => type.Id == PullRequestsPanel ) )
			rows.Add( BuildLink( "pulls", "Open pull requests", () => _context.App.Panels.OpenBeside( PullRequestsPanel, _context.Id ) ) );
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

	/// <summary>Builds the cards of the project's designs, newest first, then its reports, newest first.</summary>
	private List<Blob> BuildArtifacts( ChatHost host, ProjectInfo project, IReadOnlyList<ChatEntry> chats, IReadOnlyList<DesignEntry> designs, IReadOnlyList<BrowserReport> reports, DateTimeOffset now )
	{
		var titles = chats.ToDictionary( chat => chat.Conversation.Id, chat => chat.Conversation.Title );
		var cards = new List<Blob>();
		foreach ( var design in designs )
		{
			var variants = design.Variants switch { 0 => "one page", 1 => "1 variant", var count => $"{count} variants" };
			cards.Add( BuildCard( $"design:{design.Name}", "\uE790", design.Name, $"Design  ·  {variants}", FormatAge( design.Modified, now ),
				host.IsLocal ? $"Open design {design.Name} in the studio" : $"Design {design.Name} on {host.Name}",
				host.IsLocal ? () => OpenDesign( host, design ) : null ) );
		}
		foreach ( var report in reports )
		{
			var from = report.Conversation is { } id && titles.TryGetValue( id, out var title ) ? title : "Report";
			cards.Add( BuildCard( $"report:{report.Path}", "\uE9F9", report.Title, from, FormatAge( report.Created, now ), $"Show report {report.Title}",
				() => _browser.ShowReport( _context.App, report, _context.Id ) ) );
		}
		if ( cards.Count == 0 )
			return [BuildNote( "none", host.IsLocal
				? "No designs or reports yet. Agents add them with the design tools and browser_show."
				: $"No designs yet. Reports of {host.Name}'s chats show on that PC." )];

		return [new Row { Key = "cards", FlexWrap = FlexWrap.Wrap, Gap = Theme.Current.Spacing * 1.5, Children = cards }];
	}

	/// <summary>Builds the list of the host's projects to choose one, for a page opened outside a project.</summary>
	private List<Blob> BuildChooser( ChatHost host )
	{
		var theme = Theme.Current;
		var projects = host.Projects.Projects( _context.App.Settings.ActiveProfile );
		var rows = projects.Select( project => (Blob)BuildRow( project.Id.ToString( "N" ), "\uE8B7", project.Name,
			host.IsLocal ? FormatDirectory( project.Directory ) : project.Directory, theme.Inactive, $"Show the overview of {project.Name}",
			() => UpdateProject( project.Id ) ) ).ToList();
		return
		[
			new Text { Key = "title", Content = "Project overview", FontSize = 22, FontWeight = 600 },
			BuildSection( "projects", "Choose a project", rows.Count > 0 ? rows : [BuildNote( "none", "Add a project in the sidebar first." )] ),
		];
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

	/// <summary>Builds a titled section of the page.</summary>
	private static Column BuildSection( string key, string title, List<Blob> rows, double flexGrow = 1 ) => new()
	{
		Key = key,
		FlexGrow = flexGrow,
		FlexBasis = 320,
		MinWidth = 0,
		Gap = 2,
		Children =
		[
			new Text { Key = "title", Content = title, FontSize = DetailSize + 1, FontWeight = 600, Color = Theme.Current.Muted, Margin = new Edges( 0, 0, 6, 0 ) },
			.. rows,
		],
	};

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
			Height = 32,
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
			Height = 88,
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
