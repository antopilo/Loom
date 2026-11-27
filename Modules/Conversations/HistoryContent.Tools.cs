using Goo;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Projects;
using Lokn.Modules.Workspaces;

namespace Lokn.Modules.Conversations;

/// <summary>Builds the right side of the sidebar's rows in fixed columns (a folder's tools, a project's pull requests, then the state or … column) and
/// opens the tools.</summary>
/// <remarks>
/// A tool is a task without a chat named after a <see cref="TaskStarter"/> such as Git, in a project's folder or worktree. Its icon on the folder's row
/// opens that task, or creates it with the starter the first time, so the tool always opens in the right folder; the task itself is left out of the
/// list, its icon stands for it. The columns keep their width when empty, so icons line up from row to row.
/// </remarks>
internal sealed partial class HistoryContent
{
	/// <summary>Defines the starters a folder's row offers as tools, in their order on the row.</summary>
	private static readonly string[] ToolTitles = ["Files", "Git", "Terminal"];

	/// <summary>Defines the title of a project's pull requests task.</summary>
	private const string PullRequestsTitle = "Pull Requests";

	/// <summary>Defines the GitHub module's pull request panel, opened by id so this module needs no reference to it.</summary>
	private const string PullRequestsPanel = "github.dashboard";

	/// <summary>Defines the pull requests icon while the panel type has none, the Segoe Fluent comments glyph, unlike the Git tool's branch.</summary>
	private const string PullRequestsGlyph = "\uE8F2";

	/// <summary>Defines a tool icon's width and height in logical pixels.</summary>
	private const int ToolSize = 20;

	/// <summary>Defines the space between tool icons in logical pixels.</summary>
	private const int ToolGap = 2;

	/// <summary>Defines the width of the state column, wide enough for the working thread, in logical pixels.</summary>
	private const int StateWidth = 24;

	/// <summary>Defines the fade in front of a row's floating buttons, which hides the end of a long title under them, in logical pixels.</summary>
	private const int FadeWidth = 16;

	/// <summary>Holds whether each repository folder has a GitHub remote, read once per folder.</summary>
	private readonly Dictionary<string, bool> _isGitHub = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Reports whether a task is a folder's tool, which its row's icon stands for rather than a row of its own.</summary>
	private static bool IsToolTask( ConversationInfo conversation ) =>
		!conversation.HasChat && (ToolTitles.Contains( conversation.Title, StringComparer.Ordinal ) || conversation.Title == PullRequestsTitle);

	/// <summary>Groups a project's chats as the sidebar shows them: by worktree as <see cref="ComputeWorktreeGroups"/> does, plus the main checkout on This PC
	/// once there are worktree labels, so its tools have a row even when no chat works there.</summary>
	private IReadOnlyList<WorktreeGroup> ComputeShownGroups( ProjectInfo project, IReadOnlyList<ConversationInfo> conversations, IReadOnlyList<WorktreeEntry>? worktrees )
	{
		var groups = ComputeWorktreeGroups( project, conversations, Host.IsLocal ? worktrees : null );
		if ( !Host.IsLocal || groups.All( group => group.IsMain ) || groups.Any( group => group.IsMain ) )
			return groups;
		return [.. groups, new WorktreeGroup( project.Directory, IsMain: true, [] )];
	}

	/// <summary>Builds a folder's tool icons, Files, Git and Terminal while their modules are loaded; an icon whose task exists is drawn brighter, and the
	/// open one in the accent color.</summary>
	/// <param name="project">The project.</param>
	/// <param name="directory">The folder: the project's own, or one of its worktrees.</param>
	/// <param name="tasks">The tasks working in that folder, among which the tools' tasks are found.</param>
	private Row BuildTools( ProjectInfo project, string directory, IReadOnlyList<ConversationInfo> tasks )
	{
		var starters = context.App.Extensions.Get<TaskStarter>( TaskStarter.Point )
			.Where( starter => ToolTitles.Contains( starter.Title, StringComparer.Ordinal ) )
			.OrderBy( starter => Array.IndexOf( ToolTitles, starter.Title ) );
		var place = ConversationStore.IsSameFolder( directory, project.Directory ) ? project.Name : ComputeWorktreeName( directory );
		var icons = starters.Select( starter => BuildTool( project, directory, tasks, starter.Title, starter.Glyph, $"{starter.Title} in {place}", starter.Open ) );
		return new Row { Key = "tools", Gap = ToolGap, Children = [.. icons] };
	}

	/// <summary>Builds a project's pull requests icon while the GitHub module is loaded and the project's repository is on GitHub; null otherwise.</summary>
	private Button? BuildPullRequests( ProjectInfo project, IReadOnlyList<ConversationInfo> tasks )
	{
		if ( context.App.Panels.Types.FirstOrDefault( type => type.Id == PullRequestsPanel ) is not { } panel || !IsGitHub( project.Directory ) )
			return null;

		return BuildTool( project, project.Directory, tasks, PullRequestsTitle, panel.Glyph ?? PullRequestsGlyph, $"Pull requests of {project.Name}",
			app => app.Panels.Open( PullRequestsPanel ) );
	}

	/// <summary>Builds one tool icon, which opens its task in the folder, or creates it there and fills it with <paramref name="open"/>.</summary>
	private Button BuildTool( ProjectInfo project, string directory, IReadOnlyList<ConversationInfo> tasks, string title, string glyph, string accessibilityName,
		Action<LoknApplication> open )
	{
		var theme = Theme.Current;
		var host = Host;
		var existing = tasks.FirstOrDefault( task => !task.HasChat && task.Title == title && !task.IsArchived );
		var isOpen = existing is not null && module.GetSelected( context.App ) == host.Ref( existing.Id );
		var button = _sidebar.BuildIconButton( glyph, existing is null ? accessibilityName : $"{accessibilityName}, open", () => OpenTool( project, directory, existing, title, open ) );
		button.Key = $"tool:{title}";
		button.Width = ToolSize;
		button.Height = ToolSize;
		button.Children =
		[
			new Text
			{
				Content = glyph,
				FontFamily = SidebarRows.IconFont.Family,
				FontSize = SidebarRows.IconSize,
				Color = isOpen ? theme.Accent : existing is not null ? theme.Text : theme.Muted,
			},
		];
		return button;
	}

	/// <summary>Opens a folder's tool: selects its task when it has one, else creates the task in the folder, named after the tool, and fills it.</summary>
	private void OpenTool( ProjectInfo project, string directory, ConversationInfo? existing, string title, Action<LoknApplication> open )
	{
		var app = context.App;
		var host = Host;
		if ( existing is not null )
		{
			module.SelectConversation( app, host.Ref( existing.Id ) );
			return;
		}

		var worktree = ConversationStore.IsSameFolder( directory, project.Directory ) ? null : directory;
		module.NewTask( app, host, project.Id, title, worktree );
		open( app );
	}

	/// <summary>Reports whether a repository's origin is on GitHub, from its <c>.git/config</c>, read once per folder; a folder without one is not.</summary>
	private bool IsGitHub( string directory )
	{
		if ( _isGitHub.TryGetValue( directory, out var known ) )
			return known;

		var config = Path.Combine( directory, ".git", "config" );
		try
		{
			known = File.Exists( config ) && File.ReadAllText( config ).Contains( "github.com", StringComparison.OrdinalIgnoreCase );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			known = false;
		}

		_isGitHub[directory] = known;
		return known;
	}

	/// <summary>Builds a heading's right side: its state mark at rest, and its tools, pull requests and … button floating over the row's right end while it is
	/// revealed. The buttons take no width, so the name keeps the whole row and nothing in it moves when they appear.</summary>
	/// <param name="id">The row, whose menu the … button opens.</param>
	/// <param name="menuName">The menu's accessible name, such as "Lokn options".</param>
	/// <param name="tools">The folder's tools, or null for none.</param>
	/// <param name="pullRequests">The pull requests icon, or null for none.</param>
	/// <param name="mark">The state mark, such as the working thread of a folded project, or null for none.</param>
	private Container BuildEndColumns( Guid id, string menuName, Blob? tools, Blob? pullRequests, Blob? mark ) =>
		BuildStateColumn( id, menuName, mark, [.. tools is null ? Array.Empty<Blob>() : [tools], .. pullRequests is null ? Array.Empty<Blob>() : [pullRequests]] );

	/// <summary>Builds a row's state column: its state mark at rest, which keeps its width, and the row's buttons ending with … floating over the row's right
	/// end, on the hover color with a short fade, while the pointer is on the row or its menu is open. Hidden buttons stay see-through yet focusable, so the
	/// keyboard still reaches them; they never take width, so the title keeps its place.</summary>
	/// <param name="id">The row, whose menu the … button opens.</param>
	/// <param name="menuName">The menu's accessible name.</param>
	/// <param name="mark">The state mark at rest, or null for none.</param>
	/// <param name="buttons">Buttons before …, such as a folder's tools.</param>
	/// <param name="hasMore">Whether the row shows a … button; a row without one opens its menu by right-click only.</param>
	private Container BuildStateColumn( Guid id, string menuName, Blob? mark, IReadOnlyList<Blob>? buttons = null, bool hasMore = true )
	{
		var theme = Theme.Current;
		var isRevealed = _sidebar.IsHovered( id ) || _sidebar.IsMenuOpen( id );
		// Each button fades on its own, as the sidebar's icon buttons do; hidden ones stay focusable, and their focus or hover shows them.
		foreach ( var button in (buttons ?? []).SelectMany( Flatten ).OfType<Button>() )
			button.Opacity = isRevealed ? 1 : 0;
		var children = new List<Blob>( buttons ?? [] );
		if ( hasMore )
		{
			var more = _sidebar.BuildMoreButton( id, menuName, isRevealed );
			more.Width = StateWidth;
			more.Height = ToolSize;
			children.Add( more );
		}
		var overlay = new Row
		{
			Key = "buttons",
			Position = PositionType.Absolute,
			Right = 0,
			Top = 0,
			Height = ToolSize,
			AlignItems = AlignItems.Center,
			Gap = ToolGap,
			Padding = new Edges( 0, 0, 0, FadeWidth ),
			BackgroundGradient = isRevealed && children.Count > 0 ? new LinearGradient( 90, [theme.Hover.WithAlpha( 0 ), theme.Hover, theme.Hover, theme.Hover] ) : null,
			Children = children,
		};
		return new Container
		{
			Key = "state",
			Width = mark is not null ? StateWidth : 0,
			Height = ToolSize,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			Children = mark is not null && !(isRevealed && children.Count > 0) ? [mark, overlay] : [overlay],
		};
	}

	/// <summary>Lists a blob and every container blob below it, so the tools inside their row fade with the other buttons; a button's own content is not walked.</summary>
	private static IEnumerable<Blob> Flatten( Blob blob ) => blob switch
	{
		Container { Children: { } children } => [blob, .. children.SelectMany( Flatten )],
		Row { Children: { } children } => [blob, .. children.SelectMany( Flatten )],
		_ => [blob],
	};

	/// <summary>Builds the state of a folded list: the working thread while one of its chats works, else the waiting dot while one waits; null otherwise.</summary>
	private Blob? BuildStateMark( IReadOnlyList<ChatRef> chats )
	{
		var store = Host.Store;
		if ( chats.Any( chat => store.IsWorking( chat.ConversationId ) ) )
			return BuildWorkingMark();
		return chats.Any( _waiting.Contains ) ? BuildWaitingDot() : null;
	}

	/// <summary>Builds the thread the chat shows while its agent works, sewing along the Loom weft.</summary>
	private static Blob BuildWorkingMark() => Cell.Mount( "working", () => new StitchMark() );

	/// <summary>Builds the muted pin of a pinned task at rest.</summary>
	private static Text BuildPinMark()
	{
		var pin = SidebarRows.BuildIcon( SidebarRows.PinGlyph );
		pin.Key = "pinned";
		return pin;
	}
}
