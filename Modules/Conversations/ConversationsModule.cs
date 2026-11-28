using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Git;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;
using Lokn.Modules.Workspaces;

namespace Lokn.Modules.Conversations;

/// <summary>Contributes the sidebar's projects and chats, Home, and account-pinned conversation panels that show Home while empty; the projects are the Projects module's.</summary>
/// <remarks>
/// Chats live on hosts: This PC's in <paramref name="store"/>, each paired host's in the mirrors the Remote module fills (<see cref="Hosts"/>). The sidebar
/// lists the host the user is on, <see cref="LoknHosts.Current"/>, which follows the Workspaces session that is open.
/// </remarks>
/// <param name="store">This PC's conversations.</param>
/// <param name="createConversation">Builds the view of a host's conversation in a panel.</param>
public sealed partial class ConversationsModule(
	ConversationStore store,
	Func<PanelContext, ChatHost, Guid, PanelContent> createConversation ) : ILoknModule
{
	/// <summary>Defines the registered conversation panel identifier.</summary>
	public const string ChatPanelType = "conversations.chat";

	/// <summary>Defines the route of <c>lokn://conversations/open/&lt;chat&gt;</c> links, which open a chat in its own workspace.</summary>
	private const string OpenRoute = "open";

	/// <summary>Tracks open conversation panels, so opening a conversation reuses one rather than splitting the window.</summary>
	private readonly Dictionary<Guid, ConversationPanel> _panels = [];

	/// <summary>Tracks each profile's selected conversation per host, so switching profile or host returns to that one's chat.</summary>
	private readonly State<IReadOnlyDictionary<(SettingsProfile Profile, string Host), Guid>> _selected = new( new Dictionary<(SettingsProfile, string), Guid>() );

	/// <summary>Holds the handlers that follow each host's chats while this module runs.</summary>
	private readonly Dictionary<ChatHost, HostHandlers> _followed = [];

	/// <summary>Stores the profile last seen active, to notice the user switching profile.</summary>
	private SettingsProfile _profile;

	/// <summary>Stores the running module context for error reporting.</summary>
	private ModuleContext? _context;

	/// <summary>Stores the Projects module while this module runs; it reloads with this one, so the reference stays current.</summary>
	private ProjectsModule? _projects;

	/// <summary>Stores every host's chats while this module runs.</summary>
	private ChatHosts? _hosts;

	/// <summary>Stores the Workspaces module whose session changes this module follows, while it runs.</summary>
	private WorkspacesModule? _workspaces;

	/// <summary>Holds the chats no panel showed when their agent finished or asked something, each with the notification posted for it, until the user opens the chat or the agent works on alone.</summary>
	private readonly State<IReadOnlyDictionary<ChatRef, Guid>> _background = new( new Dictionary<ChatRef, Guid>() );

	/// <summary>Creates the module over the per-user history file with Omp chat panels that show their folder's git state; used when the application loads the module by itself.</summary>
	public ConversationsModule() : this( new ConversationStore( ConversationStore.DefaultPath ), BuildChat )
	{
		Dependencies = ["omp", "workspaces", "projects", "markdown"];
	}

	/// <summary>Gets the feature's stable module identifier.</summary>
	public string Id => "conversations";

	/// <summary>Gets the feature's display name.</summary>
	public string Title => "Conversations";

	/// <summary>Gets the explicitly composed backend modules required by this conversation feature.</summary>
	public IReadOnlyList<string> Dependencies { get; init; } = [];

	/// <summary>Gets every host's chats, This PC's first, once this module has started; the Remote module adds paired hosts' and binds them to their links.</summary>
	public ChatHosts Hosts => _hosts ?? throw new InvalidOperationException( "The Conversations module has not started." );

	/// <summary>Gets the Projects module, whose projects group the chats, once this module has started.</summary>
	internal ProjectsModule Projects => _projects ?? throw new InvalidOperationException( "The Conversations module has not started." );

	/// <summary>Fills the sidebar with projects and chats, and registers the chat panel, the new-chat, notifications and clear-conversation palette commands,
	/// the chats and projects in the Search palette, the chat a new task can start with, the Git views' review in chat and commit message writer,
	/// the link that opens a chat, and the point the sidebar reads worktree operations from.</summary>
	public void Register( ModuleBuilder builder )
	{
		builder
			.Setting( ChatContent.NewTaskCheck )
			.Setting( ChatContent.AutoRename )
			.Setting( ArchiveAfterDays )
			.Setting( KeepChats )
			// The shell holds an app update back while This PC's agents work, here or in the daemon this store mirrors.
			.Service<IAgentActivity>( new LocalAgentActivity( store ) )
			.Setting( ChatContent.CaretAnimation )
			.Slot( ShellSlots.Sidebar, 0, context => new HistoryContent( context, this ) )
			.Slot( ShellSlots.Tabs, 0, context => new TabsContent( context, this ) )
			.ExtensionPoint<WorktreeActions>( "worktrees" )
			// A chat panel works on its chat's host, which it reports itself; Home works on the host the user is on.
			.Panel( "chat", "Conversation", BuildConversation, "\uE8BD", onHost: true )
			.Contribute( NotificationOpener.Point, new NotificationOpener( ( app, item ) => item.PanelId is { } panelId && ShowPanel( app, app.Panels.ResolvePanelId( panelId ) ) ) )
			.Contribute( GitAction.Point, GitReviewChat.BuildAction( this ) )
			.Contribute( GitMessageWriter.Point, GitReviewChat.BuildWriter() )
			.Contribute( TaskStarter.Point, new TaskStarter( "Chat", ChatGlyph, 0, StartTaskChat ) )
			// A task's workspace works in its chat's folder, so the file tree, Git and new terminals open there.
			.Contribute( SessionFolder.Point, new SessionFolder( ComputeSessionFolder ) )
			// Another module, such as the browser for a report's comments, sends a message to the chat that made something (ChatMessage.Point).
			.Contribute( ChatMessage.Point, new Func<string, Guid, string, string?>( SendMessage ) )
			// Questions agents wait on show and are answered in the notification feed and banners.
			.Contribute( NotificationPromptSource.Point, new NotificationPromptSource( ComputePrompts ) )
			.Command( "new", "New chat", context => NewConversation( context.App ) )
			.Command( "new-task", "New task", context => NewTask( context.App, projectId: ComputeCurrentProject( context.App ) ) )
			.Command( "home", "Home", context => OpenHome( context.App ), new( Key.H, KeyModifiers.PrimaryShift ) )
			.Command( "clear", "Clear conversation", context => FindActiveChat( context.App )?.UpdateClear(),
				canExecute: context => FindActiveChat( context.App ) is { CanClear: true } )
			// lokn://conversations/open/<chat> opens a chat in its own workspace, such as from the notification of a chat no panel showed.
			.Route( OpenRoute, request => ShowConversation( request.App, request.Path ) );
		RegisterSearch( builder );
	}

	/// <summary>Finds the chat of the active panel, or null when another kind of panel, or Home, is active.</summary>
	private ChatContent? FindActiveChat( LoknApplication app ) =>
		app.Panels.ActivePanelId is { } id && _panels.TryGetValue( id, out var panel ) ? panel.View : null;

	/// <summary>Gets the conversation a chat panel shows, such as the chat whose agent called a tool; null for any other panel or an empty chat panel.</summary>
	public HostedChat? FindConversation( Guid panelId ) =>
		_panels.TryGetValue( panelId, out var panel ) && panel.Chat is { } chat && _hosts is { } hosts ? hosts.Find( chat ) : null;

	/// <summary>Gets the chat whose agent made a tool call: the conversation the calling panel shows, else the one the call names on the host whose agent
	/// made it, as it does during a turn no panel shows; null when no chat's agent made it.</summary>
	public HostedChat? FindConversation( AgentToolContext context ) =>
		(context.Caller is { } panelId ? FindConversation( panelId ) : null)
		?? (context.Conversation is { } id && _hosts is { } hosts ? hosts.Find( new ChatRef( context.Host ?? LoknHost.LocalId, id ) ) : null);

	/// <summary>Lists the open conversation panels, dropping closed ones from the map.</summary>
	private List<(Guid Id, ConversationPanel Panel)> FindPanels( LoknApplication app )
	{
		foreach ( var closed in _panels.Keys.Where( id => app.Panels.Panels.All( panel => panel.Id != id ) ).ToArray() )
			_panels.Remove( closed );
		return [.. _panels.Select( item => (item.Key, item.Value) )];
	}

	/// <summary>Attaches the Projects module's projects, reads local history, attaches its errors, creates This PC's chats and follows profile switches,
	/// session changes and paired hosts' chats; each profile's selection starts from its saved session.</summary>
	/// <remarks>It also follows deleted conversations, whoever deleted them, and the requests of chats no panel shows, on every host.</remarks>
	public void Start( ModuleContext context )
	{
		// Home's mark places its characters from geometry computed once; computing it now, beside the rest of startup, keeps it off the first frame.
		_ = Task.Run( () => HomeLogo.Points.Count );
		_context = context;
		var app = context.App;
		_projects = app.GetModule<ProjectsModule>();
		store.Attach( _projects.Store );
		store.EnsureLoaded();
		if ( store.LastError is { } error )
			app.ReportError( "Conversations", new IOException( error ) );
		store.Failed += MarkFailed;
		// A long chat's messages are read off the UI thread, so opening it does not stall the window.
		store.Post = context.Post;
		Func<OmpService> backend = () => app.GetModule<OmpModule>().Service;
		var local = new ChatHost( app.Hosts.Local, store, new ChatSessionService( app, store, backend, context.Post ), app, backend, context.Post );
		_hosts = new ChatHosts( app, local, backend, context.Post );
		_hosts.Added += UpdateHostAdded;
		_hosts.Removed += UpdateHostRemoved;
		Follow( local );

		_profile = app.Settings.ActiveProfile;
		app.Settings.Changed += UpdateProfile;
		if ( FindWorkspaces( app ) is { } workspaces )
		{
			foreach ( var profile in Enum.GetValues<SettingsProfile>() )
			{
				if ( ReadConversationSession( workspaces.SessionFor( profile ) ) is { } chat )
					Select( profile, chat );
			}

			_workspaces = workspaces;
			workspaces.SessionChanged += UpdateSessionHost;
			FollowSessionHost( app );
		}

		LoadWhatsNew( app );
		// Lokn opens on Home, an empty conversation panel; the restored layout goes back into the session it belongs to.
		context.WhenStarted( () => OpenHome( app ) );
		// Archiving waits until the restored layout is open, so the chats it shows count as in use.
		context.WhenStarted( () => _ = RunArchiveAsync( context ) );
	}

	/// <summary>Writes history changes still pending, then detaches every host's sessions, history, project, session and profile notifications when the module unloads.</summary>
	public void Stop()
	{
		// Sessions stop their agents and save streaming history first; a replacing module reads history when it starts, after this.
		if ( _hosts is { } hosts )
		{
			hosts.Added -= UpdateHostAdded;
			hosts.Removed -= UpdateHostRemoved;
			foreach ( var host in _followed.Keys.ToArray() )
				Unfollow( host );
			hosts.StopSessions();
		}
		_hosts = null;
		store.Flush();
		store.Failed -= MarkFailed;
		store.Post = null;
		store.Detach();
		_workspaces?.SessionChanged -= UpdateSessionHost;
		_workspaces = null;
		_context?.App.Settings.Changed -= UpdateProfile;
		_panels.Clear();
		_background.Value = new Dictionary<ChatRef, Guid>();
		_context = null;
	}

	/// <summary>Follows a host's chats: requests of chats no panel shows, a new session source, deletions, and snapshots that pending panels wait for.</summary>
	private void Follow( ChatHost host )
	{
		var handlers = new HostHandlers(
			( session, title, message ) => UpdateBackgroundAttention( host, session, title, message ),
			session => UpdateBackgroundCleared( host, session ),
			() => UpdateSessionSource( host ),
			id => UpdateDeleted( host, id ),
			() => UpdateWaiting( host ) );
		_followed[host] = handlers;
		host.Sessions.AttentionRequested += handlers.Requested;
		host.Sessions.AttentionCleared += handlers.Cleared;
		host.Sessions.SourceChanged += handlers.SourceChanged;
		host.Store.Deleted += handlers.Deleted;
		host.Synced += handlers.Synced;
	}

	/// <summary>Stops following a host's chats.</summary>
	private void Unfollow( ChatHost host )
	{
		if ( !_followed.Remove( host, out var handlers ) )
			return;

		host.Sessions.AttentionRequested -= handlers.Requested;
		host.Sessions.AttentionCleared -= handlers.Cleared;
		host.Sessions.SourceChanged -= handlers.SourceChanged;
		host.Store.Deleted -= handlers.Deleted;
		host.Synced -= handlers.Synced;
	}

	/// <summary>Follows a paired host's chats once added, and loads the panels that waited for it, such as restored ones.</summary>
	private void UpdateHostAdded( ChatHost host )
	{
		Follow( host );
		UpdateWaiting( host );
	}

	/// <summary>Lets go of a removed host's chats: panels that showed or waited for them show Home, their notifications and selection go, and its
	/// Workspaces sessions are forgotten after going Home when one is open; the user lands on This PC when the host was current.</summary>
	private void UpdateHostRemoved( ChatHost host )
	{
		Unfollow( host );
		if ( _context?.App is not { } app )
			return;

		foreach ( var (_, panel) in FindPanels( app ).Where( item => item.Panel.Chat?.HostId == host.Id || item.Panel.Waiting?.Host == host.Id ) )
			panel.Unload();

		foreach ( var chat in _background.Value.Keys.Where( chat => chat.HostId == host.Id ).ToArray() )
			UpdateSeen( app, chat );
		_selected.Value = _selected.Value.Where( item => item.Key.Host != host.Id ).ToDictionary();

		// Home panels report the current host, so This PC becomes current before Core closes the host's other panels.
		if ( app.Hosts.Current.Id == host.Id )
			app.Hosts.SetCurrent( null );
		if ( FindWorkspaces( app ) is not { } workspaces )
			return;

		if ( HostSessionKeys.Split( workspaces.CurrentSession ).HostId == host.Id )
			OpenHome( app );
		foreach ( var session in workspaces.Sessions.Where( session => HostSessionKeys.Split( session.Key ).HostId == host.Id ).ToArray() )
			workspaces.RemoveSession( session.Key );
	}

	/// <summary>Loads the panels that wait for a host's chat once its chats are here; a chat the host no longer has, or of another profile, leaves Home.</summary>
	private void UpdateWaiting( ChatHost host )
	{
		if ( _context?.App is not { } app || !host.IsSynced )
			return;

		foreach ( var (_, panel) in FindPanels( app ) )
		{
			if ( panel.Waiting is not { } waiting || (waiting.Host ?? LoknHost.LocalId) != host.Id )
				continue;

			if ( host.Store.Find( waiting.ConversationId ) is { } conversation && conversation.Profile == waiting.Profile )
			{
				panel.Load( new HostedChat( host, conversation ), waiting.ContinueTurn );
				UpdateSeen( app, host.Ref( conversation.Id ) );
			}
			else
				panel.Unload();
		}
	}

	/// <summary>Creates a conversation on a host in the active profile and opens it, reusing a conversation panel as <see cref="OpenConversation"/> does.</summary>
	public ChatRef CreateConversation( LoknApplication app, ChatHost host, Guid? projectId = null )
	{
		var conversation = host.Store.CreateConversation( app.Settings.ActiveProfile, projectId );
		var chat = host.Ref( conversation.Id );
		OpenConversation( app, chat );
		return chat;
	}

	/// <summary>Creates a conversation in the active profile and opens it in its own workspace, as <see cref="SelectConversation"/> does; for the sidebar and the New chat command.</summary>
	/// <param name="app">The application.</param>
	/// <param name="host">The host the chat lives on, or null for the host the user is on.</param>
	/// <param name="projectId">The project the chat belongs to, or null for one outside projects.</param>
	/// <param name="worktree">The project's git worktree the chat works in, or null for the project's own folder.</param>
	public ChatRef NewConversation( LoknApplication app, ChatHost? host = null, Guid? projectId = null, string? worktree = null )
	{
		host ??= Hosts.Current;
		var conversation = host.Store.CreateConversation( app.Settings.ActiveProfile, projectId );
		if ( worktree is not null && projectId is not null )
			host.Store.SetWorktree( conversation.Id, worktree );
		var chat = host.Ref( conversation.Id );
		SelectConversation( app, chat );
		return chat;
	}

	/// <summary>Creates a task without a chat, a layout for other panels such as notes or the feed, and opens its empty workspace.</summary>
	/// <param name="app">The application.</param>
	/// <param name="host">The host the task lives on, or null for the host the user is on.</param>
	/// <param name="projectId">The project the task belongs to, or null for one outside projects.</param>
	/// <param name="name">The task's name, such as the + grid starter it begins with; a chat started in it later may still rename it.</param>
	/// <param name="worktree">The project's git worktree the task works in, or null for the project's own folder.</param>
	public ChatRef NewTask( LoknApplication app, ChatHost? host = null, Guid? projectId = null, string name = WorkspacesModule.NewTaskName, string? worktree = null )
	{
		host ??= Hosts.Current;
		var task = host.Store.CreateConversation( app.Settings.ActiveProfile, projectId, name, hasChat: false );
		if ( worktree is not null && projectId is not null )
			host.Store.SetWorktree( task.Id, worktree );
		var chat = host.Ref( task.Id );
		SelectConversation( app, chat );
		return chat;
	}

	/// <summary>Gives a task without a chat one and opens its workspace, where the chat joins the panels already there.</summary>
	public ChatRef StartChat( LoknApplication app, ChatRef task )
	{
		GetChat( task ).Host.Store.AddChat( task.ConversationId );
		SelectConversation( app, task );
		return task;
	}

	/// <summary>Starts a chat in the task whose empty workspace just opened, for the + grid's Chat; anywhere else, starts a new chat in the current project.</summary>
	private void StartTaskChat( LoknApplication app )
	{
		if ( FindSessionTask( app ) is { Conversation.HasChat: false } task )
		{
			// The grid named the task after this starter; a chat's task is named like any new chat, by its first prompt.
			task.Host.Store.SetAutomaticTitle( task.Conversation.Id, "New chat", isGenerated: false );
			StartChat( app, task.Ref );
		}
		else
			NewConversation( app, projectId: ComputeSessionProject( app ) );
	}

	/// <summary>Gets the project the user is working in on the current host: the current project session's, else the selected task's; null outside projects.</summary>
	private Guid? ComputeCurrentProject( LoknApplication app ) =>
		ComputeSessionProject( app )
		?? (GetSelected( app ) is { } chat && Hosts.Find( chat ) is { } selected && selected.Host == Hosts.Current && selected.Conversation.Profile == app.Settings.ActiveProfile
			? selected.Conversation.ProjectId : null);

	/// <summary>Starts a conversation on a host that sends <paramref name="prompt"/> at once, in <paramref name="panelId"/> when given, otherwise where <see cref="OpenConversation"/> would show it.</summary>
	/// <remarks>Inside the workspace of a task without a chat on that host, the chat is that task's; otherwise it is a new task in <paramref name="projectId"/>.</remarks>
	/// <returns>The chat.</returns>
	public ChatRef StartConversation( LoknApplication app, ChatHost host, string prompt, Guid? projectId = null, Guid? panelId = null )
	{
		var conversation = FindSessionTask( app ) is { Conversation.HasChat: false } task && task.Host == host
			? task.Conversation
			: host.Store.CreateConversation( app.Settings.ActiveProfile, projectId );
		var chat = host.Ref( conversation.Id );
		UpdateStart( app, chat, prompt, panelId );
		return chat;
	}

	/// <summary>Starts a chat in a new task of a project, in that task's own workspace, and sends <paramref name="prompt"/>; for places that are not a
	/// task's workspace, such as a project's overview, where a chat opened in place would land beside it.</summary>
	/// <returns>The chat.</returns>
	public ChatRef StartConversationInNewTask( LoknApplication app, ChatHost host, string prompt, Guid? projectId )
	{
		var task = NewTask( app, host, projectId, "New chat" );
		// Named like any new chat, so its first prompt names it; the task's workspace is current now, so the chat opens there.
		host.Store.SetAutomaticTitle( task.ConversationId, "New chat", isGenerated: false );
		host.Store.AddChat( task.ConversationId );
		UpdateStart( app, task, prompt, null );
		return task;
	}

	/// <summary>Opens a new conversation and sends its first prompt once its chat is in place.</summary>
	/// <remarks>The prompt is also its draft, so it stays with the chat if it never reaches the agent; the chat gets the prompt itself rather than reading the draft back.</remarks>
	private void UpdateStart( LoknApplication app, ChatRef chat, string prompt, Guid? panelId )
	{
		GetChat( chat ).Host.Store.SaveDraft( chat.ConversationId, prompt );
		var shown = OpenConversation( app, chat, panelId );
		if ( _panels.TryGetValue( shown, out var panel ) && panel.View is { } view )
			_context?.Post( () => view.UpdateStart( prompt ) );
	}

	/// <summary>Starts a conversation on a host in an exact repository folder, retaining a matching project or worktree when known.</summary>
	/// <param name="app">The application that owns the conversation workspace.</param>
	/// <param name="host">The host the folder is on.</param>
	/// <param name="prompt">The prompt sent after the chat opens.</param>
	/// <param name="directory">The chat's working directory.</param>
	/// <returns>The chat.</returns>
	public ChatRef StartConversationInDirectory( LoknApplication app, ChatHost host, string prompt, string directory )
	{
		var profile = app.Settings.ActiveProfile;
		var history = host.Store;
		var root = Path.GetFullPath( directory );
		var projects = host.Projects.Projects( profile );
		var project = projects.FirstOrDefault( item => ConversationStore.IsSameFolder( item.Directory, root ) );
		string? worktree = null;
		if ( project is null )
		{
			foreach ( var candidate in projects )
			{
				if ( history.Conversations( profile, candidate.Id ).FirstOrDefault( conversation =>
					ConversationStore.IsSameFolder( history.GetDirectory( conversation.Id ), root ) ) is not null )
				{
					project = candidate;
					worktree = root;
					break;
				}
			}
		}

		var conversation = history.CreateConversation( profile, project?.Id );
		history.SetWorktree( conversation.Id, worktree ?? root );
		var chat = host.Ref( conversation.Id );
		UpdateStart( app, chat, prompt, panelId: null );
		return chat;
	}

	/// <summary>Shows a conversation, reusing a conversation panel rather than opening another.</summary>
	/// <remarks>
	/// A panel already showing the conversation comes forward. Otherwise it goes into <paramref name="panelId"/>, the active
	/// conversation panel, an empty one on screen, or any on screen, in that order. A panel whose agent is working is never
	/// switched away from: the conversation opens in a new panel in its pane, and the working one keeps running, hidden.
	/// </remarks>
	/// <returns>The panel showing the conversation.</returns>
	public Guid OpenConversation( LoknApplication app, ChatRef chat, Guid? panelId = null )
	{
		var shown = GetChat( chat );
		var conversation = shown.Conversation;
		if ( conversation.Profile != app.Settings.ActiveProfile )
			throw new InvalidOperationException( "Switch to this conversation's profile before opening it." );

		UpdateSeen( app, chat );
		// A task without a chat gains one by being shown in a chat panel.
		shown.Host.Store.AddChat( conversation.Id );
		Select( conversation.Profile, chat );
		var panels = FindPanels( app );
		var target = FindTarget( app, panels, panelId );
		var existing = panels.FirstOrDefault( item => item.Panel.Chat == chat );
		if ( existing.Panel is not null )
		{
			if ( app.Panels.IsShown( existing.Id ) )
				app.Panels.Activate( existing.Id );
			else
				app.Panels.Show( existing.Id, target is { } host ? FindPane( app, host.Id ) : app.Panels.SplitActive( SplitAxis.Horizontal ) );
			return existing.Id;
		}

		if ( target is { Panel.IsBusy: false } reusable )
		{
			reusable.Panel.Load( shown );
			app.Panels.Activate( reusable.Id );
			return reusable.Id;
		}

		var opened = OpenChatPanel( app, shown );
		if ( target is { } working )
			app.Panels.Show( opened, FindPane( app, working.Id ) );
		return opened;
	}

	/// <summary>Shows a conversation against a pane it was dragged onto: beside it in a new split, or in its place for <see cref="PanelPlacement.Swap"/>.</summary>
	/// <remarks>A panel already showing the conversation moves there; otherwise one opens in a new pane first, then moves, so no other chat is replaced.</remarks>
	public void OpenConversationAt( LoknApplication app, ChatRef chat, Guid paneId, PanelPlacement placement )
	{
		var shown = GetChat( chat );
		if ( shown.Conversation.Profile != app.Settings.ActiveProfile )
			return;
		UpdateSeen( app, chat );
		shown.Host.Store.AddChat( shown.Conversation.Id );
		Select( shown.Conversation.Profile, chat );
		var panelId = FindPanels( app ).FirstOrDefault( item => item.Panel.Chat == chat ).Id;
		if ( panelId == Guid.Empty )
			app.Panels.OpenAt( ChatPanelType, paneId, placement, BuildInput( chat, shown.Conversation.Profile ), shown.Host.Id );
		else
			app.Panels.ShowAt( panelId, paneId, placement );
	}

	/// <summary>Reports whether a task without a chat can bring its panel onto a pane of the layout on screen: its workspace is not the one on screen and shows a
	/// panel. Subscribes the building view.</summary>
	public bool CanMoveTaskPanel( LoknApplication app, ChatRef task ) =>
		FindWorkspaces( app ) is { } workspaces && workspaces.CanMovePanel( ConversationSession( task ) );

	/// <summary>Moves a task without a chat onto a pane of the layout on screen, as a chat dragged there shows: the panel its workspace shows leaves that
	/// workspace and shows against the pane. A tool's task (Files, Git, Terminal, Pull requests) exists only for its panel, so it is deleted once the panel
	/// left, as deleting it from its menu would, and its icon starts it afresh.</summary>
	/// <param name="app">The application.</param>
	/// <param name="task">The task.</param>
	/// <param name="paneId">The pane it was dropped on.</param>
	/// <param name="placement">Where against the pane.</param>
	/// <param name="isTool">Whether the task is a tool's.</param>
	public void MoveTaskPanel( LoknApplication app, ChatRef task, Guid paneId, PanelPlacement placement, bool isTool )
	{
		if ( FindWorkspaces( app ) is { } workspaces && workspaces.MovePanel( app, ConversationSession( task ), paneId, placement ) is not null && isTool )
			DeleteConversation( app, task );
	}

	/// <summary>Shows a chat on the right of another, opening that one first when it is not on screen, so the two read as one split.</summary>
	public void SplitConversations( LoknApplication app, ChatRef dragged, ChatRef target )
	{
		if ( dragged == target )
			return;
		var targetPanel = OpenConversation( app, target );
		if ( app.Panels.Panes.FirstOrDefault( pane => pane.PanelId == targetPanel ) is { } pane )
			OpenConversationAt( app, dragged, pane.Id, PanelPlacement.Right );
	}

	/// <summary>Lists the splits: the chats each workspace shows side by side, in pane order, two or more per split, whether or not that workspace is on
	/// screen, so a split reads as one row in the sidebar wherever the user is. A chat lives in one place: the first split that shows it keeps it.</summary>
	internal List<List<(ChatRef Chat, Guid PanelId)>> ComputeSplits( LoknApplication app )
	{
		var panels = FindPanels( app );
		ChatRef? ChatOf( Guid id ) => panels.FirstOrDefault( item => item.Id == id ).Panel is { IsEmpty: false, Chat: { } chat } ? chat : null;
		IEnumerable<IReadOnlyList<Guid>> layouts = FindWorkspaces( app ) is { } workspaces
			? workspaces.ListShownPanels( app ).Select( session => session.Panels )
			: [[.. app.Panels.Panes.Select( pane => pane.PanelId ).OfType<Guid>()]];
		var splits = new List<List<(ChatRef Chat, Guid PanelId)>>();
		var placed = new HashSet<ChatRef>();
		foreach ( var layout in layouts )
		{
			// A chat two panels of one layout show counts once, so it never reads as a split with itself.
			var split = layout.Select( id => (Chat: ChatOf( id ), PanelId: id) ).Where( item => item.Chat is { } chat && !placed.Contains( chat ) )
				.Select( item => (item.Chat!.Value, item.PanelId) ).DistinctBy( item => item.Item1 ).ToList();
			if ( split.Count < 2 )
				continue;
			placed.UnionWith( split.Select( item => item.Item1 ) );
			splits.Add( split );
		}
		return splits;
	}

	/// <summary>Opens a new conversation panel on a chat's host, in the active pane.</summary>
	private static Guid OpenChatPanel( LoknApplication app, HostedChat chat ) =>
		app.Panels.Open( ChatPanelType, input: BuildInput( chat.Ref, chat.Conversation.Profile ), hostId: chat.Host.Id );

	/// <summary>Builds the input a chat's panel opens with; This PC's chats carry no host, as before hosts.</summary>
	internal static ConversationPanelInput BuildInput( ChatRef chat, SettingsProfile profile, bool continueTurn = false ) =>
		new( chat.ConversationId, profile, continueTurn, chat.IsLocal ? null : chat.HostId );

	/// <summary>Gets a chat on its host.</summary>
	/// <exception cref="InvalidOperationException">The host is unknown.</exception>
	/// <exception cref="KeyNotFoundException">The host does not have the conversation.</exception>
	private HostedChat GetChat( ChatRef chat )
	{
		var host = Hosts.Find( chat.HostId ) ?? throw new InvalidOperationException( $"No chats of host {chat.HostId} are here." );
		return new( host, host.Store.Get( chat.ConversationId ) );
	}

	/// <summary>Picks the conversation panel a conversation opens in: the requested one when shown, the active one, an empty one on screen, or any on screen.</summary>
	private static (Guid Id, ConversationPanel Panel)? FindTarget( LoknApplication app, List<(Guid Id, ConversationPanel Panel)> panels, Guid? requested )
	{
		var shown = panels.Where( item => app.Panels.IsShown( item.Id ) ).ToArray();
		foreach ( var candidate in new[] { requested, app.Panels.ActivePanelId } )
		{
			if ( candidate is { } id && shown.FirstOrDefault( item => item.Id == id ) is { Panel: not null } match )
				return match;
		}

		return shown.FirstOrDefault( item => item.Panel.IsEmpty ) is { Panel: not null } empty ? empty
			: shown.FirstOrDefault() is { Panel: not null } any ? any
			: null;
	}

	/// <summary>Gets the pane a shown panel is in.</summary>
	private static Guid FindPane( LoknApplication app, Guid panelId ) => app.Panels.Panes.First( pane => pane.PanelId == panelId ).Id;

	/// <summary>Deletes a conversation; panels that showed it show Home instead, its session stops, and its workspace is forgotten, going Home first when it is open.</summary>
	/// <remarks>The store's <see cref="ConversationStore.Deleted"/> does all but the deletion, in the application the module runs in, as it does for a conversation another device deleted.</remarks>
	/// <param name="app">The application the conversation is shown in, the one the module runs in.</param>
	/// <param name="chat">The chat.</param>
	public void DeleteConversation( LoknApplication app, ChatRef chat ) => Hosts.Find( chat.HostId )?.Store.DeleteConversation( chat.ConversationId );

	/// <summary>Lets go of a deleted conversation, whoever deleted it: panels that showed it show Home, its session stops, its workspace is forgotten after
	/// going Home when it is open, and it is no longer selected or waiting.</summary>
	private void UpdateDeleted( ChatHost host, Guid conversationId )
	{
		if ( _context?.App is not { } app )
			return;

		var chat = host.Ref( conversationId );
		foreach ( var (_, panel) in FindPanels( app ).Where( item => item.Panel.Chat == chat ) )
			panel.Unload();

		host.Sessions.Remove( conversationId );
		UpdateSeen( app, chat );
		var key = ConversationSession( chat );
		if ( FindWorkspaces( app ) is { } workspaces )
		{
			if ( workspaces.CurrentSession == key )
				OpenHome( app );
			workspaces.RemoveSession( key );
		}

		if ( _selected.Value.Any( item => item.Key.Host == host.Id && item.Value == conversationId ) )
			_selected.Value = _selected.Value.Where( item => item.Key.Host != host.Id || item.Value != conversationId ).ToDictionary();
	}

	/// <summary>Gets the chats whose agent finished or asked something and waits for the user: those whose panel waits, until the user looks at it,
	/// and those no panel showed, until the user opens them.</summary>
	internal HashSet<ChatRef> ComputeWaiting( LoknApplication app ) =>
		[.. app.Panels.WaitingPanelIds.Select( id => _panels.GetValueOrDefault( id )?.Chat ).OfType<ChatRef>(), .. _background.Value.Keys];

	/// <summary>Gets the chat the sidebar highlights: the task whose workspace is open, else the active panel's chat, else the profile's last on the current host.</summary>
	internal ChatRef? GetSelected( LoknApplication app )
	{
		if ( FindSessionTask( app ) is { } task )
			return task.Ref;
		if ( app.Panels.ActivePanelId is { } panelId && _panels.TryGetValue( panelId, out var panel ) )
			return panel.Chat;

		var host = Hosts.Current.Id;
		return _selected.Value.TryGetValue( (app.Settings.ActiveProfile, host), out var id ) ? new ChatRef( host, id ) : null;
	}

	/// <summary>Lists every host's chats whose agent waits on a question, so the notification feed and banners show them: answered in place while a panel
	/// shows the chat, otherwise as a link that opens it; a paired host's are titled with its name.</summary>
	/// <remarks>
	/// Reading it subscribes the reader to the sessions' questions only, not to their streaming. A chat in another process that no panel shows is left
	/// out: its session stopped following the host, so its last question may be gone.
	/// </remarks>
	internal IReadOnlyList<NotificationPrompt> ComputePrompts( LoknApplication app )
	{
		if ( _hosts is not { } hosts )
			return [];

		var panels = FindPanels( app );
		var prompts = new List<NotificationPrompt>();
		foreach ( var host in hosts.All )
		{
			foreach ( var session in host.Sessions.Waiting )
			{
				if ( host.Store.Find( session.ConversationId ) is not { } conversation )
					continue;

				var chat = host.Ref( conversation.Id );
				var shown = panels.FirstOrDefault( item => item.Panel.Chat == chat && item.Panel.View is not null );
				if ( shown.Panel?.View is null && session.IsRemote )
					continue;

				var view = shown.Panel?.View;
				prompts.Add( new NotificationPrompt( chat.ToString(), FormatTitle( conversation.Title, host ), view is null ? null : shown.Id, conversation.Profile,
					view is null ? static _ => null : view.BuildPendingDialog, () => ShowConversation( app, chat ) ) );
			}
		}
		return prompts;
	}

	/// <summary>Names something of a host where several hosts' things mix: as it is on This PC, followed by the host's name on a paired host.</summary>
	internal static string FormatTitle( string title, ChatHost host ) => host.IsLocal ? title : $"{title} · {host.Name}";

	/// <summary>Opens a chat in its own workspace, switching to its profile; a conversation that no longer exists is ignored.</summary>
	private void ShowConversation( LoknApplication app, ChatRef chat )
	{
		if ( _hosts?.Find( chat ) is not { } shown )
			return;

		SwitchProfile( app, shown.Conversation.Profile );
		SelectConversation( app, chat );
	}

	/// <summary>Opens the chat a link's path names, as <see cref="ShowConversation(LoknApplication, ChatRef)"/> does.</summary>
	private void ShowConversation( LoknApplication app, string path )
	{
		if ( ChatRef.TryParse( path, out var chat ) )
			ShowConversation( app, chat );
	}

	/// <summary>Tells the user about a chat no panel shows whose agent finished or asked something: a notification whose link opens the chat, and the sidebar's waiting mark.</summary>
	/// <remarks>A chat a panel shows, even hidden, asks through its panel; a daemon has no window, and tells devices by web push instead.</remarks>
	private void UpdateBackgroundAttention( ChatHost host, IChatSession session, string title, string message )
	{
		var chat = host.Ref( session.ConversationId );
		if ( _context is not { } context || context.App.IsHost || host.Store.Find( session.ConversationId ) is null
			|| FindPanels( context.App ).Any( item => item.Panel.Chat == chat ) )
			return;

		var notifications = context.App.Notifications;
		if ( _background.Value.TryGetValue( chat, out var previous ) )
			notifications.MarkRead( previous );
		var posted = notifications.Post( new( FormatTitle( title, host ), message )
		{
			Source = ChatPanelType,
			Link = LoknLinks.Create( Id, OpenRoute, chat.ToString() ),
			Tone = session.PendingDialog is not null ? NotificationTone.Attention : session.Status == "Ready" ? NotificationTone.Success : NotificationTone.None,
			Project = host.Store.Find( session.ConversationId )?.ProjectId is { } projectId ? host.Projects.Find( projectId )?.Name : null,
		} );
		_background.Value = new Dictionary<ChatRef, Guid>( _background.Value ) { [chat] = posted.Id };
	}

	/// <summary>Withdraws the request of a chat no panel shows once its agent works on alone.</summary>
	private void UpdateBackgroundCleared( ChatHost host, IChatSession session )
	{
		if ( _context is { } context )
			UpdateSeen( context.App, host.Ref( session.ConversationId ) );
	}

	/// <summary>Takes back the waiting mark of a chat no panel showed and marks its notification read, once the user opens it or it no longer waits.</summary>
	private void UpdateSeen( LoknApplication app, ChatRef chat )
	{
		if ( !_background.Value.TryGetValue( chat, out var notification ) )
			return;

		app.Notifications.MarkRead( notification );
		var waiting = new Dictionary<ChatRef, Guid>( _background.Value );
		waiting.Remove( chat );
		_background.Value = waiting;
	}

	/// <summary>Shows a host's open chats again over a session from the new source, after its sessions' source changed and stopped the old ones.</summary>
	private void UpdateSessionSource( ChatHost host )
	{
		if ( _context is not { } context )
			return;

		foreach ( var (_, panel) in FindPanels( context.App ) )
		{
			if ( panel.View is not null && panel.Chat is { } chat && chat.HostId == host.Id && host.Store.Find( chat.ConversationId ) is { } conversation )
				panel.Load( new HostedChat( host, conversation ) );
		}
	}

	/// <summary>Travels to where a panel belongs: a chat to its conversation's workspace, any other panel to the session and workspace whose saved layout holds it, switching profile when it belongs to the other one.</summary>
	/// <remarks>Only a hidden panel no saved layout holds comes into the current layout, beside the active pane, since it belongs nowhere else.</remarks>
	/// <returns>Whether the panel, or the session that reopens it, was found.</returns>
	private bool ShowPanel( LoknApplication app, Guid panelId )
	{
		if ( FindPanels( app ).FirstOrDefault( entry => entry.Id == panelId ).Panel?.Chat is { } chat && Hosts.Find( chat ) is { } shown )
		{
			SwitchProfile( app, shown.Conversation.Profile );
			SelectConversation( app, chat );
			return true;
		}

		if ( app.Panels.IsShown( panelId ) )
			return app.Panels.Activate( panelId );

		if ( FindWorkspaces( app ) is { } workspaces && workspaces.FindSession( app, panelId ) is { } key )
		{
			if ( ComputeSessionProfile( key ) is { } profile )
				SwitchProfile( app, profile );
			if ( OpenSession( app, key ) )
				workspaces.ShowWorkspaceOf( app, panelId );
		}

		return app.Panels.Activate( panelId );
	}

	/// <summary>Builds a conversation panel: the requested or restored chat, keeping its pinned account profile, or Home when there is none or it was deleted.</summary>
	/// <remarks>A paired host's chat whose host has not sent its chats yet, or is not paired (yet), waits for it in the panel.</remarks>
	private PanelContent BuildConversation( PanelContext context )
	{
		ConversationPanelInput? input = context.RestoredState is { ValueKind: JsonValueKind.Object } restored ? restored.Deserialize<ConversationPanelInput>()
			: context.Input is ConversationPanelInput provided ? provided
			: context.Input is null ? null
			: throw new ArgumentException( "The conversation panel input is unsupported." );
		var panel = new ConversationPanel( context, this, createConversation );
		FindPanels( context.App );
		_panels[context.Id] = panel;
		if ( input is not { } shown )
			return panel;

		var chat = new ChatRef( shown.Host ?? LoknHost.LocalId, shown.ConversationId );
		if ( Hosts.Find( chat.HostId ) is not { IsSynced: true } host )
		{
			panel.Wait( chat, shown );
			return panel;
		}

		if ( host.Store.Find( chat.ConversationId ) is not { } conversation )
			return panel;

		if ( conversation.Profile != shown.Profile )
			throw new InvalidOperationException( "The saved conversation profile does not match its history." );

		panel.Load( new HostedChat( host, conversation ), shown.ContinueTurn );
		UpdateSeen( context.App, chat );
		return panel;
	}

	/// <summary>Builds a chat panel over the conversation's session on its host; it shows its folder's git state while the Git module is on.</summary>
	internal static PanelContent BuildChat( PanelContext context, ChatHost host, Guid conversationId ) =>
		new ChatContent( context, host.Store, host.Sessions.GetOrCreate( conversationId ) );

	/// <summary>Exposes a local history failure through the shell notice; write failures arrive from the history writer's thread.</summary>
	private void MarkFailed( string error ) => _context?.Post( () => _context?.App.ReportError( "Conversations", new IOException( error ) ) );

	/// <summary>The handlers this module follows one host's chats with.</summary>
	/// <param name="Requested">Handles <see cref="ChatSessionService.AttentionRequested"/>.</param>
	/// <param name="Cleared">Handles <see cref="ChatSessionService.AttentionCleared"/>.</param>
	/// <param name="SourceChanged">Handles <see cref="ChatSessionService.SourceChanged"/>.</param>
	/// <param name="Deleted">Handles <see cref="ConversationStore.Deleted"/>.</param>
	/// <param name="Synced">Handles <see cref="ChatHost.Synced"/>.</param>
	private sealed record HostHandlers( Action<IChatSession, string, string> Requested, Action<IChatSession> Cleared, Action SourceChanged, Action<Guid> Deleted, Action Synced );
}
