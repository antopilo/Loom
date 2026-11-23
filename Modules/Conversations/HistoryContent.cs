using Goo;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Projects;
using Lokn.Modules.Workspaces;

namespace Lokn.Modules.Conversations;

/// <summary>Builds a compact project sidebar and conversation management controls for the host the user is on, under the host switcher.</summary>
internal sealed partial class HistoryContent( PanelContext context, ConversationsModule module ) : PanelContent
{
	/// <summary>Defines the chat panel type, left out of a chat's panel icons.</summary>
	private const string ChatPanelType = "conversations.chat";

	/// <summary>Defines how many chats outside projects show before View all; pinned chats and the open one always show.</summary>
	private const int ScratchLimit = 5;

	/// <summary>Defines the module whose link opens a project's overview, named rather than referenced so the overview stays optional.</summary>
	private const string OverviewModuleId = "overview";

	/// <summary>Defines the Segoe Fluent ViewAll glyph of a project's overview button.</summary>
	private const string OverviewGlyph = "\uE8A9";

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

	/// <summary>Builds the host switcher, the new-chat, search and new-project actions, then the current host's projects and loose chats of the active profile.</summary>
	public override Blob Build()
	{
		var profile = context.App.Settings.ActiveProfile;
		var host = _host = module.Hosts.Current;
		_waiting = module.ComputeWaiting( context.App );
		var children = new List<Blob>();
		if ( Switcher.BuildRow() is { } switcher )
			children.Add( switcher );
		if ( Switcher.BuildBanner() is { } banner )
			children.Add( banner );
		children.Add( _sidebar.BuildIconAction( "New chat", SidebarRows.AddGlyph, () => module.NewConversation( context.App, Host ) ) );
		children.Add( _sidebar.BuildSearch() );
		children.Add( Projects.BuildNewProjectAction( project => module.OpenProject( context.App, Host, project ) ) );
		_sidebar.AddContributedActions( children );

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

		children.Add( Cell.Mount<ProfileSlideInput, ProfileSlide>( "profile-rows", new( profile, new Container { MinHeight = 0, Gap = 2, Children = rows } ) ) );
		if ( HostFolderPrompt.Build( context ) is { } prompt )
			children.Add( prompt );
		ReleaseWorktreeWatches();
		return new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Conversation history",
			Padding = SidebarRows.ContentPadding,
			Gap = 2,
			OverflowY = Overflow.Scroll,
			// The swipe stays inside the sidebar rather than painting over the workspace.
			OverflowX = Overflow.Hidden,
			Children = children,
		};
	}

	/// <summary>Builds a bold project heading with its disclosure chevron, then its chats at the same indent, or under a label per worktree once its repository
	/// has a linked worktree or its chats work in several; its menu creates and manages worktrees while Worktrees is loaded, for This PC's projects only.</summary>
	private Container BuildProject( ProjectInfo project, IReadOnlyList<ConversationInfo> conversations, IReadOnlyList<ConversationInfo> archived )
	{
		var host = Host;
		var isCollapsed = _sidebar.IsCollapsed( project.Id );
		var isTidying = module.IsTidying( project.Id );
		var heading = Projects.BuildHeading( project, module.Projects.GetIcon( project, host.Id ), isCollapsed,
			$"{project.Name}, {(isCollapsed ? "collapsed" : "expanded")}{(isTidying ? ", tidying" : "")}",
			isTidying ? [Cell.Mount<Color, ActivityOrb>( "tidying", Theme.Current.Accent )] : null );
		// Row actions stay out of sight until the pointer is on the row, or its menu or + grid is open.
		var isRevealed = _sidebar.IsRevealed( project.Id );
		var worktreeActions = host.IsLocal ? FindWorktreeActions() : null;
		var worktrees = ReadWorktrees( project, worktreeActions );
		List<Blob> actions =
		[
			heading,
			_sidebar.BuildAddButton( project.Id, $"New chat in {project.Name}", () => module.NewConversation( context.App, host, project.Id ), isRevealed ),
			_sidebar.BuildIconButton( TidyGlyph, $"Tidy chats in {project.Name}", () => BeginTidy( project ), isRevealed ),
		];
		var hasOverview = context.App.Modules.Any( loaded => loaded.Id == OverviewModuleId );
		if ( hasOverview )
			actions.Insert( 2, _sidebar.BuildIconButton( OverviewGlyph, $"Overview of {project.Name}", () => OpenOverview( host, project ), isRevealed ) );
		// The archive shows whenever the project has archived chats, so they can always be found.
		if ( archived.Count > 0 )
			actions.Add( BuildArchiveButton( project.Id, project.Name, archived.Count ) );
		actions.Add( _sidebar.BuildMoreButton( project.Id, $"{project.Name} options", isRevealed ) );
		var children = new List<Blob> { _sidebar.BuildHoverRow( project.Id, isSelected: false, actions ) };
		if ( _sidebar.BuildStarters( project.Id, project.Name, name => module.NewTask( context.App, host, project.Id, name ) ) is { } starters )
			children.Add( starters );
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
			entries.Add( new MenuAction( "Rename", () => context.Run( () => Projects.BeginRename( project ) ) ) { AccessKey = Key.R } );
			entries.Add( new MenuAction( "Change folder…", () => context.Run( () => Projects.BeginChangeFolder( project ) ) ) { AccessKey = Key.F } );
			entries.Add( new MenuAction( "Tidy chats", () => context.Run( () => BeginTidy( project ) ) ) { AccessKey = Key.Y } );
			entries.Add( new MenuSeparator() );
			entries.Add( new MenuAction( "Remove project", () => context.Run( () => host.Projects.Delete( project.Id ) ) ) { AccessKey = Key.D, IsDestructive = true } );
			children.Add( _sidebar.BuildMenu( project.Id, $"{project.Name} options", entries ) );
		}
		if ( worktreeActions is not null && IsCreatingWorktree( project.Id ) )
			children.Add( BuildNewWorktree( project, worktreeActions ) );
		if ( _tidyPlan.Value.ProjectId == project.Id )
			children.Add( BuildTidy( project, _tidyPlan.Value ) );
		if ( _openArchive.Value == project.Id )
			AddArchived( archived, children );

		if ( !isCollapsed )
			AddProjectChats( project, conversations, worktrees, worktreeActions, children );

		return SidebarRows.BuildGroup( project.Id, children );
	}

	/// <summary>Opens a project's overview through the Overview module's link: <c>&lt;project&gt;</c> on This PC, <c>&lt;project&gt;@&lt;host&gt;</c> on a paired host.</summary>
	private void OpenOverview( ChatHost host, ProjectInfo project ) =>
		context.App.OpenLink( LoknLinks.Create( OverviewModuleId, "project", host.IsLocal ? project.Id.ToString( "N" ) : $"{project.Id:N}@{host.Id}" ), context.Id );

	/// <summary>Adds the Scratch heading and the chats outside projects: the latest few, pinned ones and the open one, then View all when more are
	/// hidden; once expanded, every chat, then Show less.</summary>
	/// <param name="ungrouped">The chats outside projects, pinned first, each group most recent first.</param>
	/// <param name="archived">The archived chats outside projects, most recent first, which show under the heading while their archive is open.</param>
	/// <param name="children">The sidebar rows to add to.</param>
	private void AddScratch( ConversationInfo[] ungrouped, ConversationInfo[] archived, List<Blob> children )
	{
		children.Add( archived.Length == 0 ? SidebarRows.BuildSectionHeading( ConversationsModule.ChatsTitle ) : new Row
		{
			AlignItems = AlignItems.Center,
			Children = [SidebarRows.BuildSectionHeading( ConversationsModule.ChatsTitle ), new Container { FlexGrow = 1 }, BuildArchiveButton( Guid.Empty, ConversationsModule.ChatsTitle, archived.Length )],
		} );
		if ( _openArchive.Value == Guid.Empty )
			AddArchived( archived, children );
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
			isExpanded ? $"Show fewer {ConversationsModule.ChatsTitle} chats" : $"View all {ungrouped.Length} {ConversationsModule.ChatsTitle} chats" );
		toggle.Key = "scratch-toggle";
		toggle.Color = Theme.Current.Muted;
		children.Add( toggle );
	}

	/// <summary>Builds a list's archive button: the archive glyph with how many chats it holds, which shows or hides them under the list's heading.</summary>
	/// <param name="list">The project's id, or <see cref="Guid.Empty"/> for the chats outside projects.</param>
	/// <param name="name">The list's name, for the accessible name.</param>
	/// <param name="count">How many chats are archived.</param>
	private Button BuildArchiveButton( Guid list, string name, int count )
	{
		var isOpen = _openArchive.Value == list;
		var button = _sidebar.BuildIconButton( ArchiveGlyph, $"{(isOpen ? "Hide" : "Show")} {count} archived {(count == 1 ? "chat" : "chats")} in {name}",
			() => _openArchive.Value = isOpen ? null : list );
		button.Color = isOpen ? Theme.Current.Accent : Theme.Current.Muted;
		return button;
	}

	/// <summary>Adds a list's archived chats, most recent first, each with how long ago it was used; choosing one brings it back and opens it.</summary>
	private void AddArchived( IReadOnlyList<ConversationInfo> archived, List<Blob> children )
	{
		var host = Host;
		foreach ( var conversation in archived )
		{
			var chat = host.Ref( conversation.Id );
			var restore = _sidebar.BuildAction( $"{conversation.Title} · {ComputeAge( conversation.UpdatedAt )}", () => module.RestoreConversation( context.App, chat ),
				$"Restore {conversation.Title}" );
			restore.Key = "archived:" + conversation.Id;
			restore.Color = Theme.Current.Muted;
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
	/// Double-click the title, press F2 on it, or choose Rename in its menu to rename it in place. Drag it onto another task of its list,
	/// pinned or not as it is, to move it there.
	/// </remarks>
	/// <param name="conversation">The task.</param>
	/// <param name="index">The task's place in the list it shows in, top first.</param>
	/// <param name="list">The list it shows in among its project's, such as a worktree's; empty for the only one.</param>
	private Blob BuildConversation( ConversationInfo conversation, int index, string list = "" )
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
		var isHovered = _sidebar.IsHovered( conversation.Id );
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
				isWorking ? $"{conversation.Title}, working" : isWaiting ? $"{conversation.Title}, waiting for you" : null );
			button.DragSource = _sidebar.BuildRowDrag( conversation.Id, order );
			// A long title stays on one line and ends with an ellipsis rather than growing the row; a generated title replacing the first line rolls in.
			// Tiny icons for the panels the chat's workspace holds, its chat first, such as a terminal or a browser, lead the title.
			List<Blob> content = [];
			if ( ConversationsModule.FindWorkspaces( context.App ) is { } workspaces )
			{
				// The chat icon leads only for a task that has a chat; a task of other panels, such as Git, shows just theirs.
				var panels = workspaces.Tasks( ConversationsModule.ConversationSession( chat ) ).SelectMany( task => task.PanelTypes ).Where( type => type != ChatPanelType );
				var types = (conversation.HasChat ? panels.Prepend( ChatPanelType ) : panels).Distinct().ToArray();
				content.AddRange( _sidebar.BuildPanelIcons( new WorkspaceTask( conversation.Id, conversation.Title ) { PanelTypes = types } ) );
			}
			content.Add( Cell.Mount<RollingTextInput, RollingText>( "title", new( conversation.Title, Theme.Current.FontSize, Theme.Current.Text ) ) );
			if ( isWorking )
				content.Add( Cell.Mount<Color, ActivityOrb>( "activity", Theme.Current.Accent ) );
			else if ( isWaiting )
				content.Add( BuildWaitingDot() );
			button.Children = content;
			main = button;
		}

		// A pinned task always shows its pin, which turns into unpin under the pointer; others offer pinning on hover.
		var pin = _sidebar.BuildIconButton( conversation.IsPinned && isHovered ? SidebarRows.UnpinGlyph : SidebarRows.PinGlyph,
			conversation.IsPinned ? $"Unpin {conversation.Title}" : $"Pin {conversation.Title}",
			() => history.SetPinned( conversation.Id, !conversation.IsPinned ),
			conversation.IsPinned || isHovered || isMenuOpen );
		var children = new List<Blob>
		{
			_sidebar.BuildHoverRow( conversation.Id, module.GetSelected( context.App ) == chat,
			[
				main,
				pin,
				_sidebar.BuildMoreButton( conversation.Id, $"{conversation.Title} options", isHovered || isMenuOpen ),
			], order ),
		};
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
