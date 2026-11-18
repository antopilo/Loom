using Goo;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Projects;
using Lokn.Modules.Workspaces;

namespace Lokn.Modules.Conversations;

/// <summary>Builds a compact project sidebar and conversation management controls.</summary>
internal sealed partial class HistoryContent( PanelContext context, ConversationsModule module ) : PanelContent
{
	/// <summary>Defines the chat panel type, left out of a chat's panel icons.</summary>
	private const string ChatPanelType = "conversations.chat";

	/// <summary>Defines how many chats outside projects show before View all; pinned chats and the open one always show.</summary>
	private const int ScratchLimit = 5;

	/// <summary>Holds the sidebar's row state, such as the hovered row, the open menu and the search, and builds its rows.</summary>
	private readonly SidebarRows _sidebar = new( context );

	/// <summary>Holds what tidying a project's chats would do while the user is asked to confirm it, or <see cref="TidyPlan.None"/> when nobody is asked.</summary>
	private readonly State<TidyPlan> _tidyPlan = new( TidyPlan.None );

	/// <summary>Holds the chats waiting for the user, read once per build.</summary>
	private HashSet<Guid> _waiting = [];

	/// <summary>Tracks whether every chat outside projects shows, after View all, rather than the latest few.</summary>
	private readonly State<bool> _isScratchExpanded = new( false );

	/// <summary>Gets the new-project form and project editors, created on first use beside <see cref="_sidebar"/>.</summary>
	private ProjectRows Projects => field ??= new( context, _sidebar, module.Store.ProjectStore );

	/// <summary>Builds the new-chat, search and new-project actions, then the active profile's projects and loose chats.</summary>
	public override Blob Build()
	{
		var profile = context.App.Settings.ActiveProfile;
		_waiting = module.ComputeWaiting( context.App );
		var children = new List<Blob> { _sidebar.BuildIconAction( "New chat", SidebarRows.AddGlyph, () => module.NewConversation( context.App ) ) };
		children.Add( _sidebar.BuildSearch() );
		children.Add( Projects.BuildNewProjectAction( project => module.OpenProject( context.App, project ) ) );
		_sidebar.AddContributedActions( children );

		// Projects and chats belong to the profile, so only they swipe on a profile switch; the actions above are shared and keep still.
		var rows = new List<Blob>();
		var shown = 0;
		foreach ( var project in module.Store.ProjectStore.Projects( profile ) )
		{
			// Pinned chats lead, each group still most recent first.
			ConversationInfo[] conversations = [.. module.Store.Conversations( profile, project.Id ).OrderByDescending( conversation => conversation.IsPinned )];
			rows.Add( BuildProject( project, conversations ) );
			shown++;
		}

		ConversationInfo[] ungrouped = [.. module.Store.Conversations( profile ).OrderByDescending( conversation => conversation.IsPinned )];
		if ( ungrouped.Length > 0 )
			AddScratch( ungrouped, rows );

		if ( shown == 0 && ungrouped.Length == 0 )
			rows.Add( SidebarRows.BuildEmpty( "Start a chat or add a project to organize your work." ) );

		children.Add( Cell.Mount<ProfileSlideInput, ProfileSlide>( "profile-rows", new( profile, new Container { MinHeight = 0, Gap = 2, Children = rows } ) ) );
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
	/// has a linked worktree or its chats work in several; its menu creates and manages worktrees while Worktrees is loaded.</summary>
	private Container BuildProject( ProjectInfo project, IReadOnlyList<ConversationInfo> conversations )
	{
		var isCollapsed = _sidebar.IsCollapsed( project.Id );
		var isTidying = module.IsTidying( project.Id );
		var heading = Projects.BuildHeading( project, module.Projects.GetIcon( project ), isCollapsed,
			$"{project.Name}, {(isCollapsed ? "collapsed" : "expanded")}{(isTidying ? ", tidying" : "")}",
			isTidying ? [Cell.Mount<Color, ActivityOrb>( "tidying", Theme.Current.Accent )] : null );
		// Row actions stay out of sight until the pointer is on the row, or its menu or + grid is open.
		var isRevealed = _sidebar.IsRevealed( project.Id );
		var worktreeActions = FindWorktreeActions();
		var worktrees = ReadWorktrees( project, worktreeActions );
		var children = new List<Blob>
		{
			_sidebar.BuildHoverRow( project.Id, isSelected: false,
			[
				heading,
				_sidebar.BuildAddButton( project.Id, $"New chat in {project.Name}", () => module.NewConversation( context.App, project.Id ), isRevealed ),
				_sidebar.BuildIconButton( TidyGlyph, $"Tidy chats in {project.Name}", () => BeginTidy( project ), isRevealed ),
				_sidebar.BuildMoreButton( project.Id, $"{project.Name} options", isRevealed ),
			] ),
		};
		if ( _sidebar.BuildStarters( project.Id, project.Name, name => module.NewTask( context.App, project.Id, name ) ) is { } starters )
			children.Add( starters );
		if ( _sidebar.IsMenuOpen( project.Id ) )
		{
			var entries = new List<MenuEntry>
			{
				new MenuAction( "New chat", () => context.Run( () => module.NewConversation( context.App, project.Id ) ) ) { AccessKey = Key.N },
				new MenuAction( "New task", () => context.Run( () => module.NewTask( context.App, project.Id ) ) ) { AccessKey = Key.T },
			};
			AddWorktreeEntries( project, worktrees, worktreeActions, entries );
			entries.Add( new MenuAction( "Rename", () => context.Run( () => Projects.BeginRename( project ) ) ) { AccessKey = Key.R } );
			entries.Add( new MenuAction( "Change folder…", () => context.Run( () => Projects.BeginChangeFolder( project ) ) ) { AccessKey = Key.F } );
			entries.Add( new MenuAction( "Tidy chats", () => context.Run( () => BeginTidy( project ) ) ) { AccessKey = Key.Y } );
			entries.Add( new MenuSeparator() );
			entries.Add( new MenuAction( "Remove project", () => context.Run( () => module.Store.ProjectStore.Delete( project.Id ) ) ) { AccessKey = Key.D, IsDestructive = true } );
			children.Add( _sidebar.BuildMenu( project.Id, $"{project.Name} options", entries ) );
		}
		if ( worktreeActions is not null && IsCreatingWorktree( project.Id ) )
			children.Add( BuildNewWorktree( project, worktreeActions ) );
		if ( _tidyPlan.Value.ProjectId == project.Id )
			children.Add( BuildTidy( project, _tidyPlan.Value ) );

		if ( !isCollapsed )
			AddProjectChats( project, conversations, worktrees, worktreeActions, children );

		return SidebarRows.BuildGroup( project.Id, children );
	}

	/// <summary>Adds the Scratch heading and the chats outside projects: the latest few, pinned ones and the open one, then View all when more are
	/// hidden; once expanded, every chat, then Show less.</summary>
	/// <param name="ungrouped">The chats outside projects, pinned first, each group most recent first.</param>
	/// <param name="children">The sidebar rows to add to.</param>
	private void AddScratch( ConversationInfo[] ungrouped, List<Blob> children )
	{
		children.Add( SidebarRows.BuildSectionHeading( ConversationsModule.ChatsTitle ) );
		var isExpanded = _isScratchExpanded.Value;
		var selected = module.GetSelected( context.App );
		var hidden = 0;
		for ( var index = 0; index < ungrouped.Length; index++ )
		{
			var conversation = ungrouped[index];
			// The index stays the chat's place in the whole list, so dragging still orders it among every chat outside projects.
			if ( isExpanded || index < ScratchLimit || conversation.IsPinned || conversation.Id == selected )
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
		// Pinned tasks lead, so a task moves only among those pinned as it is, and only within the list it shows in.
		var order = new RowOrder( $"{conversation.ProjectId}:{list}:{conversation.IsPinned}", index,
			dragged => module.Store.MoveConversation( dragged, conversation.Id ) );
		var isWorking = module.Store.IsWorking( conversation.Id );
		var isWaiting = !isWorking && _waiting.Contains( conversation.Id );
		var isMenuOpen = _sidebar.IsMenuOpen( conversation.Id );
		var isHovered = _sidebar.IsHovered( conversation.Id );
		Blob main;
		if ( _sidebar.IsRenaming( conversation.Id ) )
		{
			main = _sidebar.BuildRenameEntry( conversation.Id, conversation.Title, "Rename task", title =>
			{
				// A typed title is the user's choice, which no generated title replaces.
				if ( module.Store.Find( conversation.Id ) is not null )
					module.Store.RenameConversation( conversation.Id, title );
			} );
		}
		else
		{
			var button = _sidebar.BuildRowButton( conversation.Title, () => module.SelectConversation( context.App, conversation.Id ),
				() => _sidebar.BeginRename( conversation.Id, conversation.Title ),
				isWorking ? $"{conversation.Title}, working" : isWaiting ? $"{conversation.Title}, waiting for you" : null );
			button.DragSource = _sidebar.BuildRowDrag( conversation.Id, order );
			// A long title stays on one line and ends with an ellipsis rather than growing the row; a generated title replacing the first line rolls in.
			// Tiny icons for the panels the chat's workspace holds, its chat first, such as a terminal or a browser, lead the title.
			List<Blob> content = [];
			if ( ConversationsModule.FindWorkspaces( context.App ) is { } workspaces )
			{
				// The chat icon leads only for a task that has a chat; a task of other panels, such as Git, shows just theirs.
				var panels = workspaces.Tasks( ConversationsModule.ConversationSession( conversation.Id ) ).SelectMany( task => task.PanelTypes ).Where( type => type != ChatPanelType );
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
			() => module.Store.SetPinned( conversation.Id, !conversation.IsPinned ),
			conversation.IsPinned || isHovered || isMenuOpen );
		var children = new List<Blob>
		{
			_sidebar.BuildHoverRow( conversation.Id, module.GetSelected( context.App ) == conversation.Id,
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
				new MenuAction( conversation.IsPinned ? "Unpin" : "Pin", () => context.Run( () => module.Store.SetPinned( conversation.Id, !conversation.IsPinned ) ) ) { AccessKey = Key.P },
				new MenuAction( "Rename", () => context.Run( () => _sidebar.BeginRename( conversation.Id, conversation.Title ) ) ) { AccessKey = Key.R },
			};
			if ( !conversation.HasChat )
				entries.Add( new MenuAction( "Start a chat here", () => context.Run( () => module.StartChat( context.App, conversation.Id ) ) ) { AccessKey = Key.C } );
			entries.Add( new MenuSeparator() );
			entries.Add( new MenuAction( "Delete task", () => context.Run( () => module.DeleteConversation( context.App, conversation.Id ) ) ) { AccessKey = Key.D, IsDestructive = true } );
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
