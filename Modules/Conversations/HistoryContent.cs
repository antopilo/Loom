using Goo;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Projects;
using Lokn.Modules.Workspaces;

namespace Lokn.Modules.Conversations;

/// <summary>Builds a compact project sidebar and conversation management controls for the host the user is on, under the host switcher.</summary>
internal sealed partial class HistoryContent( PanelContext context, ConversationsModule module ) : PanelContent
{
	/// <summary>Defines how many chats outside projects show before View all; pinned chats and the open one always show.</summary>
	private const int ScratchLimit = 5;

	/// <summary>Defines the module whose link opens a project's overview, named rather than referenced so the overview stays optional.</summary>
	private const string OverviewModuleId = "overview";

	/// <summary>Identifies the Scratch heading's row and menu among the sidebar's rows.</summary>
	private static readonly Guid ScratchMenuId = new( "6c1d9f5e-2b7a-4e0b-9d53-5a0f3c2e8b71" );

	/// <summary>Holds the sidebar's row state, such as the hovered row, the open menu and the search, and builds its rows.</summary>
	private readonly SidebarRows _sidebar = new( context );

	/// <summary>Holds what tidying a project's chats would do while the user is asked to confirm it, or <see cref="TidyPlan.None"/> when nobody is asked.</summary>
	private readonly State<TidyPlan> _tidyPlan = new( TidyPlan.None );

	/// <summary>Holds the chats waiting for the user, read once per build.</summary>
	private HashSet<ChatRef> _waiting = [];

	/// <summary>Tracks whether every chat outside projects shows, after View all, rather than the latest few.</summary>
	private readonly State<bool> _isScratchExpanded = new( false );

	/// <summary>Tracks the list whose archived chats show under it: a project's id, <see cref="Guid.Empty"/> for the chats outside projects, or null.</summary>
	private readonly State<Guid?> _openArchive = new( null );

	/// <summary>Builds the host switcher and its banner.</summary>
	private HostSwitcher Switcher => field ??= new( context, module );

	/// <summary>Stores the host the last build listed, whose chats the rows' actions change.</summary>
	private ChatHost? _host;

	/// <summary>Stores the project rows and the host whose projects they create and change.</summary>
	private (ChatHost Host, ProjectRows Rows)? _projects;

	/// <summary>Gets the host the sidebar lists.</summary>
	private ChatHost Host => _host ?? module.Hosts.Current;

	/// <summary>Gets This PC's chats, where worktrees are.</summary>
	private ChatHost Local => module.Hosts.Local;

	/// <summary>Gets the listed host's new-project form and project editors, created on first use beside <see cref="_sidebar"/>.</summary>
	private ProjectRows Projects
	{
		get
		{
			var host = Host;
			if ( _projects is not { } known || known.Host != host )
				_projects = known = (host, new ProjectRows( context, _sidebar, host.Projects, host.Host ));
			return known.Rows;
		}
	}

	/// <summary>Builds the header (host switcher, New project and the contributed buttons, then New chat and Search), then the current host's projects and
	/// loose chats of the active profile.</summary>
	public override Blob Build()
	{
		var profile = context.App.Settings.ActiveProfile;
		var host = _host = module.Hosts.Current;
		_waiting = module.ComputeWaiting( context.App );
		var children = new List<Blob> { BuildHeader() };
		if ( Switcher.BuildBanner() is { } banner )
			children.Add( banner );

		// Projects and chats belong to the profile, so only they swipe on a profile switch; the actions above are shared and keep still.
		var rows = new List<Blob>();
		var shown = 0;
		foreach ( var project in host.Projects.Projects( profile ) )
		{
			// Pinned chats lead, each group still most recent first; archived chats wait in the project's archive.
			var all = host.Store.Conversations( profile, project.Id );
			ConversationInfo[] conversations = [.. all.Where( conversation => !conversation.IsArchived ).OrderByDescending( conversation => conversation.IsPinned )];
			rows.Add( BuildProject( project, conversations, [.. all.Where( conversation => conversation.IsArchived ).OrderByDescending( conversation => conversation.UpdatedAt )] ) );
			shown++;
		}

		var loose = host.Store.Conversations( profile );
		ConversationInfo[] ungrouped = [.. loose.Where( conversation => !conversation.IsArchived ).OrderByDescending( conversation => conversation.IsPinned )];
		ConversationInfo[] looseArchived = [.. loose.Where( conversation => conversation.IsArchived ).OrderByDescending( conversation => conversation.UpdatedAt )];
		if ( ungrouped.Length > 0 || looseArchived.Length > 0 )
			AddScratch( ungrouped, looseArchived, rows );

		if ( shown == 0 && ungrouped.Length == 0 )
			rows.Add( SidebarRows.BuildEmpty( host.IsSynced ? "Start a chat or add a project to organize your work." : $"Waiting for {host.Name}'s chats…" ) );

		var selected = module.GetSelected( context.App );
		var selectedId = selected is { } active && active.HostId == host.Id ? active.ConversationId : (Guid?)null;
		var profileRows = _sidebar.BuildSelectionContent( rows, selectedId );
		children.Add( Cell.Mount<ProfileSlideInput, ProfileSlide>( "profile-rows", new( profile, profileRows ) ) );
		if ( HostFolderPrompt.Build( context ) is { } prompt )
			children.Add( prompt );
		ReleaseWorktreeWatches();
		return _sidebar.BuildViewport( "Conversation history", children );
	}

	/// <summary>Builds a project's heading (its chevron in the left margin, icon, name that opens its overview, then its tools, pull requests and menu
	/// floating over the row's end on hover), then its chats, flat or under a label per worktree once its repository has a linked worktree or its chats work in several; its menu
	/// creates and manages worktrees while Worktrees is loaded, for This PC's projects only.</summary>
	private Container BuildProject( ProjectInfo project, IReadOnlyList<ConversationInfo> conversations, IReadOnlyList<ConversationInfo> archived )
	{
		var host = Host;
		var isCollapsed = _sidebar.IsCollapsed( project.Id );
		var isTidying = module.IsTidying( project.Id );
		var worktreeActions = host.IsLocal ? FindWorktreeActions() : null;
		var worktrees = ReadWorktrees( project, worktreeActions );
		var groups = ComputeShownGroups( project, conversations, worktrees );
		var isFlat = groups.All( group => group.IsMain );
		var listed = conversations.Where( conversation => !IsToolTask( conversation ) ).ToArray();
		var hasOverview = context.App.Modules.Any( loaded => loaded.Id == OverviewModuleId );
		var icon = module.Projects.BuildProjectIcon( project, host.Id, SidebarRows.GroupIconSize );
		var indent = SidebarRows.ComputeChildIndent( icon is not null );
		var heading = Projects.BuildHeading( project, icon, isCollapsed,
			hasOverview ? $"{project.Name}, open overview{(isTidying ? ", tidying" : "")}" : $"{project.Name}, {(isCollapsed ? "collapsed" : "expanded")}",
			isTidying ? [Cell.Mount<Color, ActivityOrb>( "tidying", Theme.Current.Accent )] : null,
			// Without the overview, the name folds the project like its chevron, so the heading keeps its layout either way.
			hasOverview ? () => OpenOverview( host, project ) : () => _sidebar.ToggleCollapsed( project.Id ) );
		// A folded project still shows whether one of its chats works or waits.
		var mark = isCollapsed ? BuildStateMark( listed.Select( conversation => host.Ref( conversation.Id ) ).ToArray() ) : null;
		var tools = isFlat && host.IsLocal ? BuildTools( project, project.Directory, conversations ) : null;
		var pullRequests = host.IsLocal ? BuildPullRequests( project, conversations ) : null;
		var row = _sidebar.BuildHoverRow( project.Id,
		[
			heading,
			BuildEndColumns( project.Id, $"{project.Name} options", tools, pullRequests, mark ),
		] );
		var children = new List<Blob> { row };
		if ( _sidebar.IsMenuOpen( project.Id ) )
		{
			var entries = new List<MenuEntry>
			{
				new MenuAction( "New chat", () => context.Run( () => module.NewConversation( context.App, host, project.Id ) ) ) { AccessKey = Key.N },
				new MenuAction( "New task", () => context.Run( () => module.NewTask( context.App, host, project.Id ) ) ) { AccessKey = Key.T },
			};
			if ( hasOverview )
				entries.Add( new MenuAction( "Overview", () => context.Run( () => OpenOverview( host, project ) ) ) { AccessKey = Key.O } );
			AddWorktreeEntries( project, worktrees, worktreeActions, entries );
			entries.Add( new MenuSeparator() );
			AddArchiveEntry( project.Id, archived.Count, entries );
			entries.Add( new MenuAction( "Tidy chats", () => context.Run( () => BeginTidy( project ) ) ) { AccessKey = Key.Y } );
			entries.Add( new MenuAction( "Rename", () => context.Run( () => Projects.BeginRename( project ) ) ) { AccessKey = Key.R } );
			entries.Add( new MenuAction( "Change folder…", () => context.Run( () => Projects.BeginChangeFolder( project ) ) ) { AccessKey = Key.F } );
			// A folder's own icon image wins, so an icon is chosen only for a project without one.
			if ( !module.Projects.HasImageIcon( project, host.Id ) )
				entries.Add( new MenuAction( project.Icon is null ? "Set icon…" : "Change icon…", () => context.Run( () => Projects.BeginChooseIcon( project ) ) ) { AccessKey = Key.I } );
			entries.Add( new MenuSeparator() );
			entries.Add( new MenuAction( "Remove project", () => context.Run( () => host.Projects.Delete( project.Id ) ) ) { AccessKey = Key.D, IsDestructive = true } );
			children.Add( _sidebar.BuildMenu( project.Id, $"{project.Name} options", entries ) );
		}
		if ( Projects.BuildIconPicker( project ) is { } picker )
			children.Add( picker );
		if ( worktreeActions is not null && IsCreatingWorktree( project.Id ) )
			children.Add( BuildNewWorktree( project, worktreeActions ) );
		if ( _tidyPlan.Value.ProjectId == project.Id )
			children.Add( BuildTidy( project, _tidyPlan.Value ) );
		if ( _openArchive.Value == project.Id )
			AddArchived( archived, children, indent );

		if ( !isCollapsed )
			AddProjectChats( project, groups, worktrees, worktreeActions, indent, children );

		return SidebarRows.BuildGroup( project.Id, children );
	}

	/// <summary>Builds the header: the host switcher, then New project and the buttons other modules contribute, such as Notifications; under them, the wide
	/// New chat button and the Search button.</summary>
	private Container BuildHeader()
	{
		var theme = Theme.Current;
		var top = new List<Blob>();
		if ( Switcher.BuildRow() is { } switcher )
			top.Add( switcher );
		top.Add( new Container { Key = "header-space", FlexGrow = 1 } );
		top.Add( _sidebar.BuildHeaderButton( SidebarRows.NewFolderGlyph, "New project", () => Projects.BeginCreate( project => module.OpenProject( context.App, Host, project ) ) ) );
		_sidebar.AddContributedHeaderButtons( top );
		return new Container
		{
			Key = "header",
			Gap = theme.Spacing,
			Padding = new Edges( 0, 0, theme.Spacing, 0 ),
			Children =
			[
				new Row { Key = "header-top", AlignItems = AlignItems.Center, Gap = 2, Children = top },
				new Row
				{
					Key = "header-new",
					Gap = theme.Spacing / 2 + 2,
					Children =
					[
						_sidebar.BuildPrimaryAction( "New chat", SidebarRows.AddGlyph, () => module.NewConversation( context.App, Host ) ),
						_sidebar.BuildSearchButton(),
					],
				},
			],
		};
	}

	/// <summary>Opens a project's overview through the Overview module's link: <c>&lt;project&gt;</c> on This PC, <c>&lt;project&gt;@&lt;host&gt;</c> on a paired host.</summary>
	private void OpenOverview( ChatHost host, ProjectInfo project ) =>
		context.App.OpenLink( LoknLinks.Create( OverviewModuleId, "project", host.IsLocal ? project.Id.ToString( "N" ) : $"{project.Id:N}@{host.Id}" ), context.Id );

	/// <summary>Adds the Scratch heading, whose menu shows its archived chats while it has some, and the chats outside projects: the latest few, pinned ones
	/// and the open one, then View all when more are hidden; once expanded, every chat, then Show less.</summary>
	/// <param name="ungrouped">The chats outside projects, pinned first, each group most recent first.</param>
	/// <param name="archived">The archived chats outside projects, most recent first, which show under the heading while their archive is open.</param>
	/// <param name="children">The sidebar rows to add to.</param>
	private void AddScratch( ConversationInfo[] ungrouped, ConversationInfo[] archived, List<Blob> children )
	{
		var title = ConversationsModule.ChatsTitle;
		children.Add( archived.Length == 0 ? (Blob)SidebarRows.BuildSectionHeading( title ) : _sidebar.BuildHoverRow( ScratchMenuId,
		[
			SidebarRows.BuildSectionHeading( title ),
			new Container { FlexGrow = 1 },
			BuildEndColumns( ScratchMenuId, $"{title} options", null, null, null ),
		] ) );
		if ( _sidebar.IsMenuOpen( ScratchMenuId ) )
		{
			var entries = new List<MenuEntry>();
			AddArchiveEntry( Guid.Empty, archived.Length, entries );
			children.Add( _sidebar.BuildMenu( ScratchMenuId, $"{title} options", entries ) );
		}
		if ( _openArchive.Value == Guid.Empty )
			AddArchived( archived, children, 0 );
		var isExpanded = _isScratchExpanded.Value;
		var selected = module.GetSelected( context.App );
		var host = Host;
		var hidden = 0;
		for ( var index = 0; index < ungrouped.Length; index++ )
		{
			var conversation = ungrouped[index];
			// The index stays the chat's place in the whole list, so dragging still orders it among every chat outside projects.
			if ( isExpanded || index < ScratchLimit || conversation.IsPinned || host.Ref( conversation.Id ) == selected )
				children.Add( BuildConversation( conversation, index ) );
			else
				hidden++;
		}

		// Expanded, Show less stays only while folding would hide something.
		if ( isExpanded ? ungrouped.Length <= ScratchLimit : hidden == 0 )
			return;
		var toggle = _sidebar.BuildAction( isExpanded ? "Show less" : $"View all ({ungrouped.Length})", () => _isScratchExpanded.Value = !isExpanded,
			isExpanded ? $"Show fewer {title} chats" : $"View all {ungrouped.Length} {title} chats" );
		toggle.Key = "scratch-toggle";
		toggle.Color = Theme.Current.Muted;
		toggle.FontSize = Theme.Current.FontSize - 1;
		children.Add( toggle );
	}

	/// <summary>Adds a list's archive entry to its menu, Show archived (n) or Hide archived, which shows or hides its archived chats under its heading;
	/// nothing when the list has none.</summary>
	/// <param name="list">The project's id, or <see cref="Guid.Empty"/> for the chats outside projects.</param>
	/// <param name="count">How many chats are archived.</param>
	/// <param name="entries">The menu's entries.</param>
	private void AddArchiveEntry( Guid list, int count, List<MenuEntry> entries )
	{
		if ( count == 0 )
			return;

		var isOpen = _openArchive.Value == list;
		entries.Add( new MenuAction( isOpen ? "Hide archived" : $"Show archived ({count})", () => context.Run( () => _openArchive.Value = isOpen ? null : list ) )
		{
			AccessKey = Key.H,
		} );
	}

	/// <summary>Adds a list's archived chats, most recent first, each with how long ago it was used; choosing one brings it back and opens it.</summary>
	/// <param name="archived">The archived chats.</param>
	/// <param name="children">The rows to add to.</param>
	/// <param name="indent">How far the rows are inset, so their titles line up with the list's chats.</param>
	private void AddArchived( IReadOnlyList<ConversationInfo> archived, List<Blob> children, int indent )
	{
		var host = Host;
		foreach ( var conversation in archived )
		{
			var chat = host.Ref( conversation.Id );
			var restore = _sidebar.BuildAction( $"{conversation.Title} · {ComputeAge( conversation.UpdatedAt )}", () => module.RestoreConversation( context.App, chat ),
				$"Restore {conversation.Title}" );
			restore.Key = "archived:" + conversation.Id;
			restore.Color = Theme.Current.Muted;
			restore.Margin = new Edges( 0, 0, 0, indent );
			children.Add( restore );
		}
	}

	/// <summary>Formats how long ago a chat was used, such as 3d or 2w.</summary>
	private static string ComputeAge( DateTimeOffset updated )
	{
		var age = DateTimeOffset.UtcNow - updated;
		return age.TotalDays < 1 ? "today" : age.TotalDays < 14 ? $"{(int)age.TotalDays}d" : age.TotalDays < 60 ? $"{(int)(age.TotalDays / 7)}w" : $"{(int)(age.TotalDays / 30)}mo";
	}

	/// <summary>Builds one selectable task row, the same whether or not the task has a chat, its context menu while open, and its title editor while renaming.</summary>
	/// <remarks>
	/// The title fills the row; the state column on the right shows the thread sewing while the agent works, a dot while it waits for the user, a pin
	/// on a pinned task, and the … button instead while the pointer is on the row or its menu is open. Double-click the title, press F2 on it, or
	/// choose Rename in its menu to rename it in place. Drag it onto another task of its list, pinned or not as it is, to move it there.
	/// </remarks>
	/// <param name="conversation">The task.</param>
	/// <param name="index">The task's place in the list it shows in, top first.</param>
	/// <param name="list">The list it shows in among its project's, such as a worktree's; empty for the only one.</param>
	/// <param name="indent">How far the row is inset, so a project's titles start where its name does.</param>
	private Blob BuildConversation( ConversationInfo conversation, int index, string list = "", int indent = 0 )
	{
		var host = Host;
		var history = host.Store;
		var chat = host.Ref( conversation.Id );
		// Pinned tasks lead, so a task moves only among those pinned as it is, and only within the list it shows in.
		var order = new RowOrder( $"{conversation.ProjectId}:{list}:{conversation.IsPinned}", index,
			dragged => history.MoveConversation( dragged, conversation.Id ) );
		var isWorking = history.IsWorking( conversation.Id );
		var isWaiting = !isWorking && _waiting.Contains( chat );
		var isMenuOpen = _sidebar.IsMenuOpen( conversation.Id );
		Blob main;
		if ( _sidebar.IsRenaming( conversation.Id ) )
		{
			main = _sidebar.BuildRenameEntry( conversation.Id, conversation.Title, "Rename task", title =>
			{
				// A typed title is the user's choice, which no generated title replaces.
				if ( history.Find( conversation.Id ) is not null )
					history.RenameConversation( conversation.Id, title );
			} );
		}
		else
		{
			var button = _sidebar.BuildRowButton( conversation.Title, () => module.SelectConversation( context.App, chat ),
				() => _sidebar.BeginRename( conversation.Id, conversation.Title ),
				isWorking ? $"{conversation.Title}, working" : isWaiting ? $"{conversation.Title}, waiting for you"
				: conversation.IsPinned ? $"{conversation.Title}, pinned" : null );
			button.DragSource = _sidebar.BuildRowDrag( conversation.Id, order );
			// A long title stays on one line and ends with an ellipsis rather than growing the row; a generated title replacing the first line rolls in.
			// Emoji the user typed in it fall back to the emoji face.
			button.Children = [Cell.Mount<RollingTextInput, RollingText>( "title", new( conversation.Title, Theme.Current.FontSize, Theme.Current.Text ) { FontFamily = ViewStyle.FontFamily } )];
			main = button;
		}

		var mark = isWorking ? BuildWorkingMark() : isWaiting ? BuildWaitingDot() : conversation.IsPinned ? BuildPinMark() : null;
		var row = _sidebar.BuildHoverRow( conversation.Id,
		[
			main,
			BuildStateColumn( conversation.Id, $"{conversation.Title} options", mark ),
		], order );
		row.Margin = new Edges( 0, 0, 0, indent );
		var children = new List<Blob> { row };
		if ( isMenuOpen )
		{
			var entries = new List<MenuEntry>
			{
				new MenuAction( conversation.IsPinned ? "Unpin" : "Pin", () => context.Run( () => history.SetPinned( conversation.Id, !conversation.IsPinned ) ) ) { AccessKey = Key.P },
				new MenuAction( "Rename", () => context.Run( () => _sidebar.BeginRename( conversation.Id, conversation.Title ) ) ) { AccessKey = Key.R },
				new MenuAction( "Archive", () => context.Run( () => module.ArchiveConversation( context.App, chat ) ) ) { AccessKey = Key.A },
			};
			if ( !conversation.HasChat )
				entries.Add( new MenuAction( "Start a chat here", () => context.Run( () => module.StartChat( context.App, chat ) ) ) { AccessKey = Key.C } );
			entries.Add( new MenuSeparator() );
			entries.Add( new MenuAction( "Delete task", () => context.Run( () => module.DeleteConversation( context.App, chat ) ) ) { AccessKey = Key.D, IsDestructive = true } );
			children.Add( _sidebar.BuildMenu( conversation.Id, $"{conversation.Title} options", entries ) );
		}

		return new Container
		{
			Key = conversation.Id.ToString(),
			Gap = 2,
			Children = children,
		};
	}
}
