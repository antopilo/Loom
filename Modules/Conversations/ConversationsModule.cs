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
public sealed partial class ConversationsModule(
	ConversationStore store,
	Func<PanelContext, ConversationStore, Guid, PanelContent> createConversation ) : ILoknModule
{
	/// <summary>Defines the registered conversation panel identifier.</summary>
	public const string ChatPanelType = "conversations.chat";

	/// <summary>Tracks open conversation panels, so opening a conversation reuses one rather than splitting the window.</summary>
	private readonly Dictionary<Guid, ConversationPanel> _panels = [];

	/// <summary>Tracks each profile's selected conversation, so switching profile returns to that profile's chat.</summary>
	private readonly State<IReadOnlyDictionary<SettingsProfile, Guid>> _selected = new( new Dictionary<SettingsProfile, Guid>() );

	/// <summary>Stores the profile last seen active, to notice the user switching profile.</summary>
	private SettingsProfile _profile;

	/// <summary>Stores the running module context for error reporting.</summary>
	private ModuleContext? _context;

	/// <summary>Stores the Projects module while this module runs; it reloads with this one, so the reference stays current.</summary>
	private ProjectsModule? _projects;

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

	/// <summary>Gets the shared conversation history store.</summary>
	public ConversationStore Store => store;

	/// <summary>Gets the Projects module, whose projects group the chats, once this module has started.</summary>
	internal ProjectsModule Projects => _projects ?? throw new InvalidOperationException( "The Conversations module has not started." );

	/// <summary>Fills the sidebar with projects and chats, and registers the chat panel, the new-chat, notifications and clear-conversation palette commands,
	/// the chats and projects in the Search palette, the chat a new task can start with, the Git views' review in chat and commit message writer,
	/// and the point the sidebar reads worktree operations from.</summary>
	public void Register( ModuleBuilder builder )
	{
		builder
			.Setting( ChatContent.NewTaskCheck )
			.Setting( ChatContent.CaretAnimation )
			.Slot( ShellSlots.Sidebar, 0, context => new HistoryContent( context, this ) )
			.Slot( ShellSlots.Tabs, 0, context => new TabsContent( context, this ) )
			.ExtensionPoint<WorktreeActions>( "worktrees" )
			.Panel( "chat", "Conversation", BuildConversation, "\uE8BD" )
			.Contribute( NotificationOpener.Point, new NotificationOpener( ( app, item ) => item.PanelId is { } panelId && ShowPanel( app, app.Panels.ResolvePanelId( panelId ) ) ) )
			.Contribute( GitAction.Point, GitReviewChat.BuildAction( this ) )
			.Contribute( GitMessageWriter.Point, GitReviewChat.BuildWriter() )
			.Contribute( TaskStarter.Point, new TaskStarter( "Chat", ChatGlyph, 0, StartTaskChat ) )
			// A task's workspace works in its chat's folder, so the file tree, Git and new terminals open there.
			.Contribute( SessionFolder.Point, new SessionFolder( ComputeSessionFolder ) )
			// Questions agents wait on show and are answered in the notification feed and banners.
			.Contribute( NotificationPromptSource.Point, new NotificationPromptSource( ComputePrompts ) )
			.Command( "new", "New chat", context => NewConversation( context.App ) )
			.Command( "new-task", "New task", context => NewTask( context.App, ComputeCurrentProject( context.App ) ) )
			.Command( "home", "Home", context => OpenHome( context.App ), new( Key.H, KeyModifiers.PrimaryShift ) )
			.Command( "clear", "Clear conversation", context => FindActiveChat( context.App )?.UpdateClear(),
				canExecute: context => FindActiveChat( context.App ) is { CanClear: true } );
		RegisterSearch( builder );
	}

	/// <summary>Finds the chat of the active panel, or null when another kind of panel, or Home, is active.</summary>
	private ChatContent? FindActiveChat( LoknApplication app ) =>
		app.Panels.ActivePanelId is { } id && _panels.TryGetValue( id, out var panel ) ? panel.Chat : null;

	/// <summary>Gets the conversation a chat panel shows, such as the chat whose agent called a tool; null for any other panel or an empty chat panel.</summary>
	public ConversationInfo? FindConversation( Guid panelId ) =>
		_panels.TryGetValue( panelId, out var panel ) && panel.ConversationId is { } id ? store.Find( id ) : null;

	/// <summary>Lists the open conversation panels, dropping closed ones from the map.</summary>
	private List<(Guid Id, ConversationPanel Panel)> FindPanels( LoknApplication app )
	{
		foreach ( var closed in _panels.Keys.Where( id => app.Panels.Panels.All( panel => panel.Id != id ) ).ToArray() )
			_panels.Remove( closed );
		return [.. _panels.Select( item => (item.Key, item.Value) )];
	}

	/// <summary>Attaches the Projects module's projects, reads local history, attaches its errors and follows profile switches; each profile's selection starts from its saved session.</summary>
	public void Start( ModuleContext context )
	{
		_context = context;
		_projects = context.App.GetModule<ProjectsModule>();
		store.Attach( _projects.Store );
		store.EnsureLoaded();
		if ( store.LastError is { } error )
			context.App.ReportError( "Conversations", new IOException( error ) );
		store.Failed += MarkFailed;

		_profile = context.App.Settings.ActiveProfile;
		context.App.Settings.Changed += UpdateProfile;
		if ( FindWorkspaces( context.App ) is { } workspaces )
		{
			foreach ( var profile in Enum.GetValues<SettingsProfile>() )
			{
				if ( ReadConversationSession( workspaces.SessionFor( profile ) ) is { } id )
					Select( profile, id );
			}
		}

		// Lokn opens on Home, an empty conversation panel; the restored layout goes back into the session it belongs to.
		context.WhenStarted( () => OpenHome( context.App ) );
	}

	/// <summary>Writes history changes still pending, then detaches history, project and profile notifications when the module unloads.</summary>
	public void Stop()
	{
		// A replacing module reads history when it starts, after this.
		store.Flush();
		store.Failed -= MarkFailed;
		store.Detach();
		_context?.App.Settings.Changed -= UpdateProfile;
		_panels.Clear();
		_context = null;
	}

	/// <summary>Creates a conversation in the active profile and opens it, reusing a conversation panel as <see cref="OpenConversation"/> does.</summary>
	public Guid CreateConversation( LoknApplication app, Guid? projectId = null )
	{
		var conversation = store.CreateConversation( app.Settings.ActiveProfile, projectId );
		OpenConversation( app, conversation.Id );
		return conversation.Id;
	}

	/// <summary>Creates a conversation in the active profile and opens it in its own workspace, as <see cref="SelectConversation"/> does; for the sidebar and the New chat command.</summary>
	/// <param name="app">The application.</param>
	/// <param name="projectId">The project the chat belongs to, or null for one outside projects.</param>
	/// <param name="worktree">The project's git worktree the chat works in, or null for the project's own folder.</param>
	public Guid NewConversation( LoknApplication app, Guid? projectId = null, string? worktree = null )
	{
		var conversation = store.CreateConversation( app.Settings.ActiveProfile, projectId );
		if ( worktree is not null && projectId is not null )
			store.SetWorktree( conversation.Id, worktree );
		SelectConversation( app, conversation.Id );
		return conversation.Id;
	}

	/// <summary>Creates a task without a chat, a layout for other panels such as notes or the feed, and opens its empty workspace.</summary>
	/// <param name="app">The application.</param>
	/// <param name="projectId">The project the task belongs to, or null for one outside projects.</param>
	/// <param name="name">The task's name, such as the + grid starter it begins with; a chat started in it later may still rename it.</param>
	/// <param name="worktree">The project's git worktree the task works in, or null for the project's own folder.</param>
	public Guid NewTask( LoknApplication app, Guid? projectId = null, string name = WorkspacesModule.NewTaskName, string? worktree = null )
	{
		var task = store.CreateConversation( app.Settings.ActiveProfile, projectId, name, hasChat: false );
		if ( worktree is not null && projectId is not null )
			store.SetWorktree( task.Id, worktree );
		SelectConversation( app, task.Id );
		return task.Id;
	}

	/// <summary>Gives a task without a chat one and opens its workspace, where the chat joins the panels already there.</summary>
	public Guid StartChat( LoknApplication app, Guid taskId )
	{
		store.AddChat( taskId );
		return SelectConversation( app, taskId );
	}

	/// <summary>Starts a chat in the task whose empty workspace just opened, for the + grid's Chat; anywhere else, starts a new chat in the current project.</summary>
	private void StartTaskChat( LoknApplication app )
	{
		if ( FindSessionTask( app ) is { HasChat: false } task )
		{
			// The grid named the task after this starter; a chat's task is named like any new chat, by its first prompt.
			store.SetAutomaticTitle( task.Id, "New chat", isGenerated: false );
			StartChat( app, task.Id );
		}
		else
			NewConversation( app, ComputeSessionProject( app ) );
	}

	/// <summary>Gets the project the user is working in: the current project session's, else the selected task's; null outside projects.</summary>
	private Guid? ComputeCurrentProject( LoknApplication app ) =>
		ComputeSessionProject( app ) ?? (GetSelected( app ) is { } id && store.Find( id ) is { } selected && selected.Profile == app.Settings.ActiveProfile ? selected.ProjectId : null);

	/// <summary>Starts a conversation that sends <paramref name="prompt"/> at once, in <paramref name="panelId"/> when given, otherwise where <see cref="OpenConversation"/> would show it.</summary>
	/// <remarks>Inside the workspace of a task without a chat, the chat is that task's; otherwise it is a new task in <paramref name="projectId"/>.</remarks>
	/// <returns>The conversation's identifier.</returns>
	public Guid StartConversation( LoknApplication app, string prompt, Guid? projectId = null, Guid? panelId = null )
	{
		var conversation = FindSessionTask( app ) is { HasChat: false } task ? task : store.CreateConversation( app.Settings.ActiveProfile, projectId );
		store.SaveDraft( conversation.Id, prompt );
		var shown = OpenConversation( app, conversation.Id, panelId );

		// The composer opens holding the prompt as its draft; sending waits until the panel is in place.
		if ( _panels.TryGetValue( shown, out var panel ) && panel.Chat is { } chat )
			_context?.Post( chat.UpdateSend );
		return conversation.Id;
	}

	/// <summary>Starts a conversation in an exact repository folder, retaining a matching project or worktree when known.</summary>
	/// <param name="app">The application that owns the conversation workspace.</param>
	/// <param name="prompt">The prompt sent after the chat opens.</param>
	/// <param name="directory">The chat's working directory.</param>
	/// <returns>The conversation's identifier.</returns>
	public Guid StartConversationInDirectory( LoknApplication app, string prompt, string directory )
	{
		var profile = app.Settings.ActiveProfile;
		var root = Path.GetFullPath( directory );
		var projects = store.ProjectStore.Projects( profile );
		var project = projects.FirstOrDefault( item => ConversationStore.IsSameFolder( item.Directory, root ) );
		string? worktree = null;
		if ( project is null )
		{
			foreach ( var candidate in projects )
			{
				if ( store.Conversations( profile, candidate.Id ).FirstOrDefault( conversation =>
					ConversationStore.IsSameFolder( store.GetDirectory( conversation.Id ), root ) ) is not null )
				{
					project = candidate;
					worktree = root;
					break;
				}
			}
		}

		var conversation = store.CreateConversation( profile, project?.Id );
		store.SetWorktree( conversation.Id, worktree ?? root );
		store.SaveDraft( conversation.Id, prompt );
		var shown = OpenConversation( app, conversation.Id );
		if ( _panels.TryGetValue( shown, out var panel ) && panel.Chat is { } chat )
			_context?.Post( chat.UpdateSend );
		return conversation.Id;
	}

	/// <summary>Shows a conversation, reusing a conversation panel rather than opening another.</summary>
	/// <remarks>
	/// A panel already showing the conversation comes forward. Otherwise it goes into <paramref name="panelId"/>, the active
	/// conversation panel, an empty one on screen, or any on screen, in that order. A panel whose agent is working is never
	/// switched away from: the conversation opens in a new panel in its pane, and the working one keeps running, hidden.
	/// </remarks>
	/// <returns>The panel showing the conversation.</returns>
	public Guid OpenConversation( LoknApplication app, Guid conversationId, Guid? panelId = null )
	{
		var conversation = store.Get( conversationId );
		if ( conversation.Profile != app.Settings.ActiveProfile )
			throw new InvalidOperationException( "Switch to this conversation's profile before opening it." );

		// A task without a chat gains one by being shown in a chat panel.
		store.AddChat( conversation.Id );
		Select( conversation.Profile, conversation.Id );
		var panels = FindPanels( app );
		var target = FindTarget( app, panels, panelId );
		var existing = panels.FirstOrDefault( item => item.Panel.ConversationId == conversationId );
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
			reusable.Panel.Load( conversation );
			app.Panels.Activate( reusable.Id );
			return reusable.Id;
		}

		var opened = app.Panels.Open( ChatPanelType, input: new ConversationPanelInput( conversation.Id, conversation.Profile ) );
		if ( target is { } working )
			app.Panels.Show( opened, FindPane( app, working.Id ) );
		return opened;
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

	/// <summary>Deletes a conversation; panels that showed it show Home instead, and its workspace is forgotten, going Home first when it is open.</summary>
	public void DeleteConversation( LoknApplication app, Guid conversationId )
	{
		var profile = store.Get( conversationId ).Profile;
		var key = ConversationSession( conversationId );
		if ( FindWorkspaces( app ) is { } workspaces && workspaces.CurrentSession == key )
			OpenHome( app );

		foreach ( var (_, panel) in FindPanels( app ).Where( item => item.Panel.ConversationId == conversationId ) )
			panel.Unload();

		store.DeleteConversation( conversationId );
		FindWorkspaces( app )?.RemoveSession( key );
		if ( _selected.Value.GetValueOrDefault( profile ) == conversationId )
			Select( profile, Guid.Empty );
	}

	/// <summary>Gets the conversations whose agent finished or asked something and waits for the user, until the user looks at its panel.</summary>
	internal HashSet<Guid> ComputeWaiting( LoknApplication app ) =>
		[.. app.Panels.WaitingPanelIds.Select( id => _panels.GetValueOrDefault( id )?.ConversationId ).OfType<Guid>()];

	/// <summary>Gets the conversation the sidebar highlights: the task whose workspace is open, else the active panel's chat, else the profile's last.</summary>
	internal Guid? GetSelected( LoknApplication app )
	{
		if ( FindSessionTask( app ) is { } task )
			return task.Id;
		if ( app.Panels.ActivePanelId is { } panelId && _panels.TryGetValue( panelId, out var panel ) )
			return panel.ConversationId;
		return _selected.Value.GetValueOrDefault( app.Settings.ActiveProfile );
	}

	/// <summary>Lists the open chats whose agent waits on a question, so the notification feed and banners can show and answer them in place.</summary>
	/// <remarks>Reading it subscribes the reader to each open chat's questions only, not to its streaming.</remarks>
	internal IReadOnlyList<NotificationPrompt> ComputePrompts( LoknApplication app ) =>
		[.. FindPanels( app )
			.Where( item => item.Panel.Chat is { HasPendingDialog: true } && item.Panel.ConversationId is { } id && store.Find( id ) is not null )
			.Select( item =>
			{
				var conversation = store.Get( item.Panel.ConversationId!.Value );
				var chat = item.Panel.Chat!;
				return new NotificationPrompt( conversation.Id.ToString( "N" ), conversation.Title, item.Id, conversation.Profile,
					chat.BuildPendingDialog, () => SelectConversation( app, conversation.Id ) );
			} )];

	/// <summary>Travels to where a panel belongs: a chat to its conversation's workspace, any other panel to the session and workspace whose saved layout holds it, switching profile when it belongs to the other one.</summary>
	/// <remarks>Only a hidden panel no saved layout holds comes into the current layout, beside the active pane, since it belongs nowhere else.</remarks>
	/// <returns>Whether the panel, or the session that reopens it, was found.</returns>
	private bool ShowPanel( LoknApplication app, Guid panelId )
	{
		if ( FindPanels( app ).FirstOrDefault( entry => entry.Id == panelId ).Panel?.ConversationId is { } conversationId && store.Find( conversationId ) is { } conversation )
		{
			SwitchProfile( app, conversation.Profile );
			SelectConversation( app, conversation.Id );
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
	private PanelContent BuildConversation( PanelContext context )
	{
		ConversationPanelInput? input = context.RestoredState is { ValueKind: JsonValueKind.Object } restored ? restored.Deserialize<ConversationPanelInput>()
			: context.Input is ConversationPanelInput provided ? provided
			: context.Input is null ? null
			: throw new ArgumentException( "The conversation panel input is unsupported." );
		var panel = new ConversationPanel( context, this, store, createConversation );
		FindPanels( context.App );
		_panels[context.Id] = panel;
		if ( input is not { } shown || store.Find( shown.ConversationId ) is not { } conversation )
			return panel;

		if ( conversation.Profile != shown.Profile )
			throw new InvalidOperationException( "The saved conversation profile does not match its history." );

		panel.Load( conversation, shown.ContinueTurn );
		return panel;
	}

	/// <summary>Builds a chat panel connected to the loaded Omp module's service; it shows its folder's git state while the Git module is on.</summary>
	private static PanelContent BuildChat( PanelContext context, ConversationStore store, Guid conversationId )
	{
		return new ChatContent( context, store, conversationId, context.App.GetModule<OmpModule>().Service );
	}

	/// <summary>Exposes a local history failure through the shell notice; write failures arrive from the history writer's thread.</summary>
	private void MarkFailed( string error ) => _context?.Post( () => _context?.App.ReportError( "Conversations", new IOException( error ) ) );
}
