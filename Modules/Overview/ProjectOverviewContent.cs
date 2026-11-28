using System.Globalization;
using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Browser;
using Lokn.Modules.Conversations;
using Lokn.Modules.Projects;
using Lokn.Modules.Workspaces;

namespace Lokn.Modules.Overview;

/// <summary>Shows one project's brief: the day's painting and a line on what matters, a prompt bar, then cards with its
/// chats and tasks by what they are doing, its designs and reports, and the sections other modules add (<see cref="BriefSection"/>), such as pull requests.</summary>
/// <remarks>
/// The lists follow the stores live: reading chats, working state, questions, notifications and reports during the build rebuilds the page when they change.
/// Designs are read from the project's <c>.lokn\design</c> folder on its host when the page opens, when the folder changes, and when one of the
/// project's agents stops working. Without a project the page lists the projects of the host to choose one. The page is printed in the paper and ink of
/// Loom's report kit (<see cref="BriefPaper"/>).
/// </remarks>
internal sealed partial class ProjectOverviewContent : PanelContent
{
	/// <summary>Defines the widest the page's column grows, in logical pixels.</summary>
	private const int ColumnWidth = 1040;

	/// <summary>Defines an artifact card's width in logical pixels.</summary>
	private const int CardWidth = 150;

	/// <summary>Defines small detail text's size in logical pixels.</summary>
	private const int DetailSize = 11;

	/// <summary>Defines the width of a section's title beside its content, in logical pixels.</summary>
	private const int AsideWidth = 250;

	/// <summary>Defines the column width under which a section's title goes above its content and the credit under the line, in logical pixels.</summary>
	private const int WideWidth = 640;

	/// <summary>Defines the width of each margin that holds the date or the time, in logical pixels.</summary>
	private const int RailWidth = 72;

	/// <summary>Defines the page width under which the margins with the date and time are left out, in logical pixels.</summary>
	private const int RailsMinWidth = 860;

	/// <summary>Defines the page's padding, in logical pixels.</summary>
	private const int PagePadding = 24;

	/// <summary>Defines the height assumed for a page row until layout measures it, in logical pixels.</summary>
	private const double EstimatedRowHeight = 34;

	/// <summary>Defines how long the design folder must be quiet before it is read again, in milliseconds.</summary>
	private const int RescanDelay = 600;

	/// <summary>Stores the panel's dispatch and host.</summary>
	private readonly PanelContext _context;

	/// <summary>Stores the module that keeps each project's brief line across pages.</summary>
	private readonly OverviewModule _module;

	/// <summary>Stores the module whose chats, tasks and projects the page lists and opens.</summary>
	private readonly ConversationsModule _conversations;

	/// <summary>Stores the module whose reports the page lists and shows.</summary>
	private readonly BrowserModule _browser;

	/// <summary>Stores the project shown, or null while the page lists projects to choose from.</summary>
	private readonly State<Guid?> _projectId;

	/// <summary>Stores the project's designs as last read, newest first.</summary>
	private readonly State<IReadOnlyList<DesignEntry>> _designs = new( [] );

	/// <summary>Stores a failure to read or open something, or empty.</summary>
	private readonly State<string> _status = new( "" );

	/// <summary>Targets the page to measure its width.</summary>
	private readonly ElementHandle _page = new();

	/// <summary>Stores how the page fits its width.</summary>
	private readonly State<PageLayout> _layout = new( new( true, 3, 400, true ) );

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

	/// <summary>Stores the address of the project's <c>origin</c> remote, or empty for none, for the sections other modules add.</summary>
	private readonly State<string> _remote = new( "" );

	/// <summary>Stores the chat host whose working changes the page follows.</summary>
	private ChatHost? _followed;

	/// <summary>Creates the page for the project its input or restored state names, on its panel's host.</summary>
	public ProjectOverviewContent( PanelContext context, OverviewModule module, ConversationsModule conversations, BrowserModule browser )
	{
		_context = context;
		_module = module;
		_conversations = conversations;
		_browser = browser;
		Guid? project = context.Input as Guid?;
		if ( project is null && context.RestoredState is { ValueKind: JsonValueKind.String } saved && Guid.TryParse( saved.GetString(), out var restored ) )
			project = restored;
		_projectId = new( project );
		Follow();
		ScanDesigns();
		_ = ReadGitAsync();
		StartBrief();
		context.Lifetime.Register( Release );
		_page.MetricsChanged += metrics =>
		{
			if ( !metrics.IsMounted )
				return;
			var layout = ComputeLayout( metrics.ContentBox.Width, Theme.Current.Spacing * 1.5 );
			if ( layout != _layout.Value )
				_context.Post( () => _layout.Value = layout );
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

	/// <summary>Fits the page to its width: the margins with the date and time when there is room for them, the column beside them, the painting's
	/// height from the column's width, whether section titles sit beside their content, and how many artifact cards fit in a row.</summary>
	/// <param name="width">The page's width inside its padding.</param>
	/// <param name="cardGap">The gap between artifact cards.</param>
	internal static PageLayout ComputeLayout( double width, double cardGap )
	{
		var hasRails = width >= RailsMinWidth;
		var column = Math.Min( width - (hasRails ? RailWidth * 2 : 0) - ViewStyle.TrackWidth, ColumnWidth );
		var isWide = column >= WideWidth;
		var content = column - (isWide ? AsideWidth : 0);
		var cards = Math.Max( 1, (int)((content + cardGap) / (CardWidth + cardGap)) );
		return new( isWide, cards, Math.Round( Math.Clamp( column * 0.42, 220, 440 ) ), hasRails );
	}

	/// <summary>Builds the page on its paper: the date and time in the margins when they fit, and between them one column that builds only the rows in
	/// view, with its own scrollbar.</summary>
	public override Blob Build()
	{
		var paper = BriefPaper.Current;
		var layout = _layout.Value;
		var host = ChatHost;
		var project = host is not null && _projectId.Value is { } id ? host.Projects.Find( id ) : null;
		List<BuiltRow> rows = host is null ? [new( "waiting", () => BuildNote( "waiting", $"Waiting for {_context.Host.Name}'s chats…" ) )]
			: project is null ? BuildChooser( host, layout )
			: BuildProject( host, project, layout );
		if ( _status.Value is { Length: > 0 } status )
			rows.Add( new( "status", () => BuildNote( "status", status ) ) );

		var list = new Virtual
		{
			Key = "rows",
			Source = new MeasuredRowsSource<BuiltRow>( rows, BuildPageRow, static _ => EstimatedRowHeight, FollowEnd: false ),
			FlexGrow = 1,
			FlexShrink = 1,
			MaxWidth = ColumnWidth + ViewStyle.TrackWidth,
			MinWidth = 0,
			MinHeight = 0,
			Padding = new Edges( 0, ViewStyle.TrackWidth, 48, 0 ),
			OverflowX = Overflow.Hidden,
			ScrollbarY = ViewStyle.GetScrollbar(),
		};
		var now = _now.Value;
		return new Row
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = project is null ? "Project overview" : $"{project.Name} brief",
			Handle = _page,
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			JustifyContent = JustifyContent.Center,
			Padding = new Edges( PagePadding, PagePadding, 0, PagePadding ),
			BackgroundColor = paper.Paper,
			Color = paper.Ink,
			Children = layout.HasRails && project is not null
				?
				[
					BuildRail( "date", now.ToString( "d MMM yyyy", CultureInfo.CurrentCulture ).ToUpper( CultureInfo.CurrentCulture ), -90, layout ),
					list,
					BuildRail( "time", now.ToString( "t", CultureInfo.CurrentCulture ).ToUpper( CultureInfo.CurrentCulture ), 90, layout ),
				]
				: [list],
		};
	}

	/// <summary>Builds one row of the page in a container without a key, since a virtual list's row may carry only its own key.</summary>
	private static Container BuildPageRow( BuiltRow row ) => new() { Children = [row.Build()] };

	/// <summary>Lists the rows of a project's brief: its name, the painting, the line under it, the prompt bar, then Top to-dos, New updates, Your day,
	/// and the designs and reports.</summary>
	private List<BuiltRow> BuildProject( ChatHost host, ProjectInfo project, PageLayout layout )
	{
		var chats = ComputeChats( host, project );
		var reports = ComputeReports( host, chats );
		var added = ReadSections( project );
		List<BuiltRow> rows =
		[
			new( "kicker", () => BuildKicker( host, project ) ),
			new( "hero", () => BuildHero( layout ) ),
			new( "lede", () => BuildLede( chats, added, layout ) ),
			new( "prompt", () => BuildPrompt( host, project ) ),
		];
		AddBrief( rows, host, chats, reports, added, DateTimeOffset.UtcNow, layout );
		return rows;
	}

	/// <summary>Describes the page's project to the sections other modules add.</summary>
	private BriefProject ComputeBriefProject( ProjectInfo project ) =>
		new( _context.App, project.Id, project.Name, project.Directory, _context.Host.Id, project.Profile, _branch.Value, _remote.Value, _context.Id );

	/// <summary>Reads the sections other modules add for the project, by their order, leaving out those with nothing to show.</summary>
	/// <remarks>Each section reads its module's state while the page builds, so the page builds again when that state changes; reading the module list
	/// does the same when a module that adds a section loads or unloads.</remarks>
	private List<(BriefSection Section, BriefContent Content)> ReadSections( ProjectInfo project )
	{
		_ = _context.App.Modules;
		var brief = ComputeBriefProject( project );
		var sections = new List<(BriefSection, BriefContent)>();
		foreach ( var section in _context.App.Extensions.Get<BriefSection>( BriefSection.Point ).OrderBy( section => section.Order ) )
		{
			BriefContent? content;
			try
			{
				content = section.Read( brief );
			}
			catch ( Exception error ) when ( error is not OutOfMemoryException )
			{
				// Another module's code: its failure shows on its card rather than breaking the page.
				content = new( [] ) { Empty = $"Could not read {section.Title}: {error.Message}" };
			}
			if ( content is not null && (content.Items.Count > 0 || content.Empty.Length > 0) )
				sections.Add( (section, content) );
		}
		return sections;
	}

	/// <summary>Builds the prompt bar on the paper: Enter starts a chat in the project with what was typed, Ctrl+Enter a task named after it.</summary>
	private Container BuildPrompt( ChatHost host, ProjectInfo project )
	{
		var theme = Theme.Current;
		var paper = BriefPaper.Current;
		return new Container
		{
			Key = "prompt",
			Padding = new Edges( 28, 0, 4, 0 ),
			Children =
			[
				new TextEntry
				{
					Key = "entry",
					Value = _prompt.Value,
					Controlled = true,
					Placeholder = "Ask anything…   Enter: chat   Ctrl+Enter: task",
					PlaceholderColor = paper.Ink3,
					AccessibilityName = $"Start a chat in {project.Name}",
					Width = Length.Percent( 100 ),
					Height = 46,
					FlexShrink = 0,
					Padding = new Edges( 0, 16 ),
					FontSize = theme.FontSize + 1,
					Color = paper.Ink,
					BackgroundColor = paper.Paper,
					BorderColor = paper.Rule,
					BorderWidth = 1,
					BorderRadius = 12,
					FocusStyle = new Style { BorderColor = paper.Ink2 },
					OnChange = value => _context.Run( () => _prompt.Value = value ),
					OnSubmit = value => _context.Run( () => UpdateStart( host, project, value, isTask: false ) ),
					KeyBindings = [new( Key.Enter, KeyModifiers.Primary, () => _context.Run( () => UpdateStart( host, project, _prompt.Value, isTask: true ) ) )],
				},
			],
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
			_conversations.StartConversationInNewTask( _context.App, host, text, project.Id );
	}

	/// <summary>Reads the project's branch and <c>origin</c> remote with git on its host; a folder that is no repository leaves both empty.</summary>
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
			_remote.Value = remote;
		} );
	}

	/// <summary>Lists the rows of cards of the project's designs, newest first, then its reports, newest first, as many cards to a row as fit.</summary>
	private List<BuiltRow> BuildArtifacts( ChatHost host, IReadOnlyList<ChatEntry> chats, IReadOnlyList<DesignEntry> designs, IReadOnlyList<BrowserReport> reports, DateTimeOffset now, PageLayout layout )
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
			return [new( "none", () => BuildNote( "none", host.IsLocal
				? "No designs or reports yet. Agents add them with the design tools and browser_report."
				: $"No designs yet. Reports of {host.Name}'s chats show on that PC." ) )];
		}

		var gap = Theme.Current.Spacing * 1.5;
		var chunks = cards.Chunk( layout.CardColumns ).ToList();
		return [.. chunks.Select( ( row, index ) => new BuiltRow( row[0].Key, () => new Row
		{
			Gap = gap,
			Padding = new Edges( 0, 0, index < chunks.Count - 1 ? gap : 0, 0 ),
			Children = [.. row.Select( card => card.Build() ), .. Enumerable.Range( 0, layout.CardColumns - row.Length ).Select( _ => new Container { FlexGrow = 1, FlexBasis = CardWidth, MinWidth = 0 } )],
		} ) )];
	}

	/// <summary>Lists the rows of the host's projects to choose one, for a page opened outside a project.</summary>
	private List<BuiltRow> BuildChooser( ChatHost host, PageLayout layout )
	{
		List<BuiltRow> projects =
		[
			.. host.Projects.Projects( _context.App.Settings.ActiveProfile ).Select( project => new BuiltRow( project.Id.ToString( "N" ),
				() => BuildRow( project.Id.ToString( "N" ), "\uE8B7", project.Name, host.IsLocal ? FormatDirectory( project.Directory ) : project.Directory, BriefPaper.Current.Ink3,
					$"Show the overview of {project.Name}", () => UpdateProject( project.Id ) ) ) ),
		];
		if ( projects.Count == 0 )
			projects.Add( new( "none", () => BuildNote( "none", "Add a project in the sidebar first." ) ) );
		List<BuiltRow> rows = [new( "title", () => new Text { Key = "title", Content = "Choose a project", FontFamily = BriefArt.DisplayFontFamily, FontSize = 40, Padding = new Edges( 8, 0, 0, 0 ) } )];
		AddSection( rows, "projects", "Projects", projects, layout );
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

	/// <summary>Stops following the host and the design folder, and releases the painting, once the panel closes.</summary>
	private void Release()
	{
		if ( _followed is not null )
			_followed.Store.WorkingChanged -= OnWorkingChanged;
		_followed = null;
		_designWatch?.Dispose();
		_designWatch = null;
		_rescan?.Cancel();
		_painting.Value?.Dispose();
	}

	/// <summary>Adds a section's rows to the page, each item its own row, so a long section still builds only the rows in view: its title in an italic
	/// serif beside the first item, or, for <see cref="SectionStyle.Numbered"/> and <see cref="SectionStyle.Full"/>, on a line above them; narrow, the
	/// title always goes above.</summary>
	/// <param name="rows">The page's rows.</param>
	/// <param name="key">Keys the section's rows.</param>
	/// <param name="title">The section's title.</param>
	/// <param name="items">The section's items.</param>
	/// <param name="layout">How the page fits its width.</param>
	/// <param name="style">Where the title goes and what the column beside the items holds.</param>
	/// <param name="numbered">How many items, from the first, get a number beside them under <see cref="SectionStyle.Numbered"/>.</param>
	private static void AddSection( List<BuiltRow> rows, string key, string title, IReadOnlyList<BuiltRow> items, PageLayout layout,
		SectionStyle style = SectionStyle.Beside, int numbered = 0 )
	{
		var isAbove = style != SectionStyle.Beside || !layout.IsWide;
		if ( isAbove )
			rows.Add( new( key + ":title", () => new Container { Padding = new Edges( 44, 0, 16, 0 ), Children = [BuildSectionTitle( title )] } ) );
		for ( var i = 0; i < items.Count; i++ )
		{
			var item = items[i];
			var (isFirst, number) = (i == 0, i < numbered ? (i + 1).ToString( "00", CultureInfo.InvariantCulture ) : null);
			rows.Add( new( $"{key}:{item.Key}", () => BuildSectionRow( isFirst && !isAbove ? title : null, isFirst && !isAbove, number, style, item.Build(), layout ) ) );
		}
	}

	/// <summary>Builds a section's title in an italic serif.</summary>
	private static Text BuildSectionTitle( string title ) => new()
	{
		Key = "title",
		Content = title,
		FontFamily = BriefArt.SerifFontFamily,
		FontStyle = FontStyle.Italic,
		FontSize = 24,
		LineHeight = 1.2,
		Color = BriefPaper.Current.Ink,
		MinWidth = 0,
		TextWrap = TextWrap.Wrap,
	};

	/// <summary>Builds one item row of a section on the paper: wide, a left column with the section's title on the first row, or the item's number set
	/// against the column's right edge so it sits beside the item, unless the item takes the full width; narrow, the number before the item.</summary>
	private static Container BuildSectionRow( string? title, bool isTop, string? number, SectionStyle style, Blob content, PageLayout layout )
	{
		var body = new Container { Key = "body", FlexGrow = 1, MinWidth = 0, Children = [content] };
		Blob? aside = title is not null ? BuildSectionTitle( title )
			: number is not null ? new Text { Key = "number", Content = number, FontFamily = ViewStyle.CodeFontFamily, FontSize = 10, Color = BriefPaper.Current.Ink3, Padding = new Edges( 5, 0, 0, 0 ) }
			: null;
		Blob[] children = style == SectionStyle.Full ? [body]
			: layout.IsWide ? [new Container { Key = "aside", Width = AsideWidth, FlexShrink = 0, AlignItems = title is null ? AlignItems.FlexEnd : AlignItems.Stretch,
				Padding = new Edges( 0, title is null ? 16 : 0, 0, 0 ), Children = aside is null ? [] : [aside] }, body]
			: aside is null ? [body]
			: [new Container { Key = "aside", Width = 30, FlexShrink = 0, Children = [aside] }, body];
		return new Container
		{
			Key = "section-row",
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.FlexStart,
			Padding = new Edges( isTop ? 44 : 0, 0, 0, 0 ),
			Children = children,
		};
	}

	/// <summary>Builds a row: an icon, a title and a quieter detail on the right; a row with an action opens it when clicked, one without only informs.</summary>
	private Button BuildRow( string key, string glyph, string title, string detail, Color detailColor, string name, Action? open )
	{
		var theme = Theme.Current;
		var paper = BriefPaper.Current;
		void Run()
		{
			if ( open is not null )
				_context.Run( open );
		}
		var icon = SidebarRows.BuildIcon( glyph );
		icon.Color = paper.Ink3;
		return new Button
		{
			Key = key,
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			MinHeight = 34,
			Padding = new Edges( 0, 10 ),
			BorderRadius = theme.Radius + 2,
			BackgroundColor = Color.Transparent,
			HoverStyle = open is null ? null : new Style { BackgroundColor = paper.Hover },
			FocusStyle = new Style { BackgroundColor = paper.Hover },
			Cursor = open is null ? Cursor.Default : Cursor.Pointer,
			Focusable = true,
			AccessibilityName = name,
			OnClick = Run,
			KeyBindings = [new( Key.Enter, KeyModifiers.None, Run ), new( Key.Space, KeyModifiers.None, Run )],
			Children =
			[
				icon,
				new Text { Content = title, Color = paper.Ink, MinWidth = 0, FlexShrink = 1, FlexGrow = 1, TextMaxLines = 1, TextTrimming = TextTrimming.Ellipsis },
				new Text { Content = detail, FontSize = DetailSize, Color = detailColor, MinWidth = 0, TextMaxLines = 1, TextTrimming = TextTrimming.Ellipsis },
			],
		};
	}

	/// <summary>Builds a card for a design or report on the paper, outlined with a hairline, which opens it when clicked; a card that cannot open is
	/// drawn faint.</summary>
	private Button BuildCard( string key, string glyph, string title, string detail, string footnote, string name, Action? open )
	{
		var theme = Theme.Current;
		var paper = BriefPaper.Current;
		void Run()
		{
			if ( open is not null )
				_context.Run( open );
		}
		var icon = SidebarRows.BuildIcon( glyph );
		icon.Color = paper.Ink3;
		return new Button
		{
			Key = key,
			FlexGrow = 1,
			FlexBasis = CardWidth,
			MinWidth = 0,
			MinHeight = 88,
			Padding = new Edges( 12, 14 ),
			Gap = 3,
			FlexDirection = FlexDirection.Column,
			AlignItems = AlignItems.Stretch,
			BorderRadius = theme.Radius + 3,
			BackgroundColor = paper.Paper,
			HoverStyle = open is null ? null : new Style { BorderColor = paper.Ink3 },
			FocusStyle = new Style { BorderColor = paper.Ink2 },
			BorderWidth = 1,
			BorderColor = paper.Rule,
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
						icon,
						new Text { Content = title, FontWeight = 600, MinWidth = 0, FlexShrink = 1, TextMaxLines = 1, TextTrimming = TextTrimming.Ellipsis, Color = open is null ? paper.Ink2 : paper.Ink },
					],
				},
				new Text { Content = detail, FontSize = DetailSize, Color = paper.Ink2, MinWidth = 0, TextMaxLines = 1, TextTrimming = TextTrimming.Ellipsis },
				new Spacer(),
				new Text { Content = footnote, FontSize = DetailSize, Color = paper.Ink3, MinWidth = 0, TextMaxLines = 1, TextTrimming = TextTrimming.Ellipsis },
			],
		};
	}

	/// <summary>Builds a small text link, such as Show all.</summary>
	private Button BuildLink( string key, string label, Action action )
	{
		var theme = Theme.Current;
		var paper = BriefPaper.Current;
		void Run() => _context.Run( action );
		return new Button
		{
			Key = key,
			AlignSelf = AlignSelf.FlexStart,
			Padding = new Edges( 4, 10 ),
			BorderRadius = theme.Radius,
			BackgroundColor = Color.Transparent,
			HoverStyle = new Style { BackgroundColor = paper.Hover },
			Cursor = Cursor.Pointer,
			Focusable = true,
			AccessibilityName = label,
			OnClick = Run,
			KeyBindings = [new( Key.Enter, KeyModifiers.None, Run )],
			Children = [new Text { Content = label, FontSize = DetailSize + 1, FontWeight = 600, Color = paper.Ink2 }],
		};
	}

	/// <summary>Builds a quiet line of explanation.</summary>
	private static Text BuildNote( string key, string text ) => new()
	{
		Key = key,
		Content = text,
		Color = BriefPaper.Current.Ink2,
		TextWrap = TextWrap.Wrap,
		Padding = new Edges( 4, 0 ),
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

	/// <summary>Holds how the page fits its width.</summary>
	/// <param name="IsWide">Whether section titles sit beside their content and the painting's credit beside the line under it.</param>
	/// <param name="CardColumns">How many design and report cards fit in a row.</param>
	/// <param name="HeroHeight">The painting's height in logical pixels.</param>
	/// <param name="HasRails">Whether the margins with the date and the time show.</param>
	internal sealed record PageLayout( bool IsWide, int CardColumns, double HeroHeight, bool HasRails );

	/// <summary>Names where a section's title goes and what the column beside its items holds.</summary>
	private enum SectionStyle
	{
		/// <summary>The title beside the first item, the column empty beside the rest.</summary>
		Beside,

		/// <summary>The title on a line above, each item's number beside it.</summary>
		Numbered,

		/// <summary>The title on a line above, the items across the full width.</summary>
		Full,
	}
}
