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

	/// <summary>Defines how many chats and tasks show before Show all.</summary>
	private const int ChatLimit = 12;

	/// <summary>Defines how many activity rows show.</summary>
	private const int ActivityLimit = 30;

	/// <summary>Defines an artifact card's width in logical pixels.</summary>
	private const int CardWidth = 196;

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

	/// <summary>Builds the page: the project's header, its chats and tasks beside its activity, then its designs and reports.</summary>
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
		var chats = ComputeChats( host, project );
		var reports = ComputeReports( host, chats );
		var designs = _designs.Value;
		var now = DateTimeOffset.UtcNow;
		return
		[
			BuildHeader( host, project, chats, designs.Count, reports.Count ),
			new Row
			{
				Key = "lists",
				FlexWrap = FlexWrap.Wrap,
				AlignItems = AlignItems.FlexStart,
				Gap = Theme.Current.Spacing * 3,
				Children =
				[
					BuildSection( "chats", "Chats and tasks", BuildChats( host, project, chats, now ), flexGrow: 3 ),
					BuildSection( "activity", "Activity", BuildActivity( host, chats, designs, reports, now ), flexGrow: 2 ),
				],
			},
			BuildSection( "artifacts", "Designs and reports", BuildArtifacts( host, project, chats, designs, reports, now ) ),
		];
	}

	/// <summary>Builds the project's icon, name, folder and host, its counts, and the actions that start work in it.</summary>
	private Blob BuildHeader( ChatHost host, ProjectInfo project, IReadOnlyList<ChatEntry> chats, int designs, int reports )
	{
		var theme = Theme.Current;
		var working = chats.Count( chat => chat.State == ChatState.Working );
		var waiting = chats.Count( chat => chat.State == ChatState.Waiting );
		var counts = new List<string>();
		if ( waiting > 0 )
			counts.Add( $"{waiting} waiting for you" );
		if ( working > 0 )
			counts.Add( $"{working} working" );
		counts.Add( Count( chats.Count( chat => chat.Conversation.HasChat ), "chat" ) );
		counts.Add( Count( chats.Count( chat => !chat.Conversation.HasChat ), "task" ) );
		counts.Add( Count( designs, "design" ) );
		counts.Add( Count( reports, "report" ) );

		var icon = _context.App.GetModule<ProjectsModule>().GetIcon( project, host.Id );
		var title = new Row
		{
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing * 1.5,
			MinWidth = 0,
			Children =
			[
				.. icon is null ? Array.Empty<Blob>() : [ProjectsModule.BuildIcon( icon, project.Name, 34 )],
				new Column
				{
					MinWidth = 0,
					FlexShrink = 1,
					Gap = 2,
					Children =
					[
						new Text { Key = "name", Content = project.Name, FontSize = 22, FontWeight = 600, TextMaxLines = 1 },
						new Text
						{
							Key = "folder",
							Content = host.IsLocal ? FormatDirectory( project.Directory ) : $"{project.Directory}  ·  {host.Name}",
							FontSize = DetailSize + 1,
							Color = theme.Muted,
							TextMaxLines = 1,
						},
					],
				},
			],
		};
		var actions = new Row
		{
			Gap = theme.Spacing,
			Children =
			[
				theme.BuildButton( "New chat", () => _context.Run( () => _conversations.NewConversation( _context.App, host, project.Id ) ) ),
				theme.BuildButton( "New task", () => _context.Run( () => _conversations.NewTask( _context.App, host, project.Id ) ) ),
				theme.BuildButton( "Open project", () => _context.Run( () => _context.App.GetModule<ProjectsModule>().OpenProject( _context.App, project, host.Id ) ) ),
			],
		};
		return new Column
		{
			Key = "header",
			Gap = theme.Spacing * 1.5,
			Children =
			[
				new Row { AlignItems = AlignItems.Center, JustifyContent = JustifyContent.SpaceBetween, FlexWrap = FlexWrap.Wrap, Gap = theme.Spacing * 2, Children = [title, actions] },
				new Text { Key = "counts", Content = string.Join( "  ·  ", counts ), Color = waiting > 0 ? theme.Attention : theme.Muted },
			],
		};
	}

	/// <summary>Builds the rows of the project's chats and tasks: those waiting for the user, then working ones, then the rest by last use.</summary>
	private List<Blob> BuildChats( ChatHost host, ProjectInfo project, IReadOnlyList<ChatEntry> chats, DateTimeOffset now )
	{
		if ( chats.Count == 0 )
			return [BuildNote( "none", "No chats or tasks yet. Start one with New chat." )];

		var shown = _isShowingAll.Value ? chats : chats.Take( ChatLimit );
		var rows = shown.Select( chat => (Blob)BuildRow(
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
			() => OpenChat( host.Ref( chat.Conversation.Id ) ) ) ).ToList();
		if ( chats.Count > ChatLimit )
		{
			var label = _isShowingAll.Value ? "Show less" : $"Show all {chats.Count}";
			rows.Add( BuildLink( "more", label, () => _isShowingAll.Value = !_isShowingAll.Value ) );
		}
		return rows;
	}

	/// <summary>Builds the project's timeline, newest first: notifications of its chats, chats and tasks started and updated, designs changed and reports shown.</summary>
	private List<Blob> BuildActivity( ChatHost host, IReadOnlyList<ChatEntry> chats, IReadOnlyList<DesignEntry> designs, IReadOnlyList<BrowserReport> reports, DateTimeOffset now )
	{
		var byId = chats.ToDictionary( chat => chat.Conversation.Id, chat => chat.Conversation );
		var items = new List<ActivityEntry>();
		foreach ( var item in _context.App.Notifications.Items )
		{
			if ( FindChat( item ) is { } chat && chat.HostId == host.Id && byId.ContainsKey( chat.ConversationId ) )
			{
				Action open = item.Url is { } url && Uri.TryCreate( url, UriKind.Absolute, out var link )
					? () => _context.App.OpenLink( link, _context.Id )
					: () => OpenChat( chat );
				items.Add( new( item.Time, item.Title, item.Message, "\uEA8F", open ) );
			}
		}
		foreach ( var chat in byId.Values )
		{
			var kind = chat.HasChat ? "chat" : "task";
			Action open = () => OpenChat( host.Ref( chat.Id ) );
			if ( chat.CreatedAt != default )
				items.Add( new( chat.CreatedAt, chat.Title, $"Started {kind}", chat.HasChat ? "\uE8BD" : "\uE9D5", open ) );
			if ( chat.CreatedAt == default || chat.UpdatedAt - chat.CreatedAt > TimeSpan.FromMinutes( 1 ) )
				items.Add( new( chat.UpdatedAt, chat.Title, $"Updated {kind}", "\uE70F", open ) );
		}
		var canOpenDesigns = host.IsLocal && _context.App.Tools.Items.Any( tool => tool.Name == "design_open" );
		foreach ( var design in designs )
			items.Add( new( design.Modified, design.Name, "Design changed", "\uE790", canOpenDesigns ? () => OpenDesign( design ) : () => { } ) );
		foreach ( var report in reports )
		{
			var detail = report.Conversation is { } id && byId.TryGetValue( id, out var chat ) ? $"Report from {chat.Title}" : "Report";
			items.Add( new( report.Created, report.Title, detail, "\uE9F9", () => _browser.ShowReport( _context.App, report, _context.Id ) ) );
		}

		if ( items.Count == 0 )
			return [BuildNote( "none", "Nothing happened here yet." )];

		return [.. items
			.OrderByDescending( item => item.Time )
			.Take( ActivityLimit )
			.Select( ( item, index ) => (Blob)BuildRow( $"activity-{index}", item.Glyph, item.Title, $"{item.Detail}  ·  {FormatAge( item.Time, now )}",
				Theme.Current.Inactive, $"{item.Detail}: {item.Title}", item.Open ) )];
	}

	/// <summary>Builds the cards of the project's designs, newest first, then its reports, newest first.</summary>
	private List<Blob> BuildArtifacts( ChatHost host, ProjectInfo project, IReadOnlyList<ChatEntry> chats, IReadOnlyList<DesignEntry> designs, IReadOnlyList<BrowserReport> reports, DateTimeOffset now )
	{
		var titles = chats.ToDictionary( chat => chat.Conversation.Id, chat => chat.Conversation.Title );
		var cards = new List<Blob>();
		// A design opens in the Design studio, which only This PC serves, and only while the Design module offers its tool.
		var canOpen = host.IsLocal && _context.App.Tools.Items.Any( tool => tool.Name == "design_open" );
		foreach ( var design in designs )
		{
			var variants = design.Variants switch { 0 => "one page", 1 => "1 variant", var count => $"{count} variants" };
			cards.Add( BuildCard( $"design:{design.Name}", "\uE790", design.Name, $"Design  ·  {variants}", FormatAge( design.Modified, now ),
				canOpen ? $"Open design {design.Name} in the studio" : $"Design {design.Name}",
				canOpen ? () => OpenDesign( design ) : null ) );
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

	/// <summary>Shows another project's overview in this panel.</summary>
	private void UpdateProject( Guid projectId )
	{
		_projectId.Value = projectId;
		_isShowingAll.Value = false;
		ScanDesigns();
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

	/// <summary>Finds the chat a notification is about: the chat its link opens, else the chat its panel shows; null for any other.</summary>
	private ChatRef? FindChat( LoknNotification item )
	{
		if ( item.Url is { } url && Uri.TryCreate( url, UriKind.Absolute, out var link ) && link.Scheme == LoknLinks.Scheme && link.Host == "conversations" )
		{
			var segments = link.AbsolutePath.Split( '/', StringSplitOptions.RemoveEmptyEntries );
			if ( segments is ["open", var path] && ChatRef.TryParse( Uri.UnescapeDataString( path ), out var chat ) )
				return chat;
		}
		return item.PanelId is { } panel ? _conversations.FindConversation( panel )?.Ref : null;
	}

	/// <summary>Opens a chat or task in its own workspace, as the sidebar does.</summary>
	private void OpenChat( ChatRef chat ) => _conversations.SelectConversation( _context.App, chat );

	/// <summary>Opens a This PC design in the Design studio beside this page, through the Design module's tool.</summary>
	private void OpenDesign( DesignEntry design ) => _ = OpenDesignAsync( design );

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

	/// <summary>Formats a count with its noun, such as 1 chat or 3 chats.</summary>
	private static string Count( int count, string noun ) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

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

	/// <summary>Holds one row of the project's timeline.</summary>
	private sealed record ActivityEntry( DateTimeOffset Time, string Title, string Detail, string Glyph, Action Open );

	/// <summary>Holds one of the project's designs as read from its folder.</summary>
	/// <param name="Name">The design's name, its folder's name.</param>
	/// <param name="Project">The project folder the design is in.</param>
	/// <param name="Variants">How many variant folders it has; 0 for a single page.</param>
	/// <param name="Modified">When its folder or a file at its top last changed.</param>
	private sealed record DesignEntry( string Name, string Project, int Variants, DateTimeOffset Modified );
}
