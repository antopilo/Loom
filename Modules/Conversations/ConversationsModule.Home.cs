using Lokn.Core;
using Lokn.Modules.Projects;
using Lokn.Modules.Workspaces;

namespace Lokn.Modules.Conversations;

/// <summary>Describes one card on Home: a recent project, or the chats outside projects.</summary>
/// <param name="Key">The session the card opens.</param>
/// <param name="Title">The card's name.</param>
/// <param name="Detail">The directory or description under the name.</param>
/// <param name="LastUsed">When the user last opened it or chatted in it, or null.</param>
/// <param name="Conversations">How many conversations it holds.</param>
/// <param name="Project">The project, or null for the chats outside projects.</param>
internal sealed record HomeEntry( string Key, string Title, string Detail, DateTimeOffset? LastUsed, int Conversations, ProjectInfo? Project );

/// <summary>Opens Home, the empty conversation panel, and the Projects module's session per project, each with its own saved workspaces and layout.</summary>
/// <remarks>
/// Leaving a session keeps its panels running, hidden, so an agent working in one project keeps working while another is open.
/// Without the Workspaces module, the same actions arrange panels without sessions.
/// </remarks>
public sealed partial class ConversationsModule
{
	/// <summary>Defines Home's own session.</summary>
	internal const string HomeSession = "home";

	/// <summary>Defines the session of the chats outside projects.</summary>
	internal const string ChatsSession = "chats";

	/// <summary>Defines the name of the chats outside projects, shown over them in the sidebar, on their Home card and tab group, and as their session's title.</summary>
	internal const string ChatsTitle = "Scratch";

	/// <summary>Defines the prefix of a conversation's session key.</summary>
	internal const string ConversationPrefix = "conversation:";

	/// <summary>Gets a conversation's session key.</summary>
	internal static string ConversationSession( Guid conversationId ) => ConversationPrefix + conversationId.ToString( "N" );

	/// <summary>Reads the conversation a session key stands for, or null for any other session.</summary>
	internal static Guid? ReadConversationSession( string? key ) =>
		key is not null && key.StartsWith( ConversationPrefix, StringComparison.Ordinal ) && Guid.TryParseExact( key[ConversationPrefix.Length..], "N", out var id ) ? id : null;

	/// <summary>Opens a task in its own workspace: the layout the user left it in, or at first just its chat, full window.</summary>
	/// <remarks>
	/// Without the Workspaces module, the conversation opens as <see cref="OpenConversation"/> shows it. A task without a chat
	/// starts as one empty pane, which offers to open a panel.
	/// </remarks>
	/// <returns>The panel showing the conversation, or an empty identifier for a task without a chat.</returns>
	public Guid SelectConversation( LoknApplication app, Guid conversationId )
	{
		var conversation = store.Get( conversationId );
		if ( conversation.Profile != app.Settings.ActiveProfile )
			throw new InvalidOperationException( "Switch to this conversation's profile before opening it." );
		if ( FindWorkspaces( app ) is not { } workspaces )
			return conversation.HasChat ? OpenConversation( app, conversationId ) : Guid.Empty;

		Select( conversation.Profile, conversation.Id );
		workspaces.OpenSession( app, ConversationSession( conversation.Id ), conversation.Title, () =>
		{
			if ( conversation.HasChat )
				ShowConversationAlone( app, conversation );
			else
				app.Panels.HideAll();
		} );
		if ( !conversation.HasChat )
			return Guid.Empty;

		// The user may have closed the chat while this workspace was open; it comes back beside what is left.
		var panel = FindPanels( app ).FirstOrDefault( item => item.Panel.ConversationId == conversation.Id ).Id;
		if ( panel == Guid.Empty )
			return app.Panels.Open( ChatPanelType, input: new ConversationPanelInput( conversation.Id, conversation.Profile ) );

		// A chat the user moved into another of the task's workspaces is shown there, not pulled into this one.
		workspaces.ShowWorkspaceOf( app, panel );
		app.Panels.Activate( panel );
		return panel;
	}

	/// <summary>Shows a conversation's chat alone: its open panel, shown or hidden, else a new one; every other panel stays open, hidden.</summary>
	private void ShowConversationAlone( LoknApplication app, ConversationInfo conversation )
	{
		var existing = FindPanels( app ).FirstOrDefault( item => item.Panel.ConversationId == conversation.Id ).Id;
		app.Panels.HideAll();
		if ( existing == Guid.Empty )
			app.Panels.Open( ChatPanelType, input: new ConversationPanelInput( conversation.Id, conversation.Profile ) );
		else
			app.Panels.Show( existing, app.Panels.Root.Id );
	}

	/// <summary>Records a profile's selected conversation, or clears it with an empty identifier.</summary>
	private void Select( SettingsProfile profile, Guid conversationId )
	{
		if ( _selected.Value.GetValueOrDefault( profile ) == conversationId )
			return;

		_selected.Value = new Dictionary<SettingsProfile, Guid>( _selected.Value ) { [profile] = conversationId };
	}

	/// <summary>Switches to a profile for a navigation that picks its own destination, so the profile's last session does not open after it.</summary>
	private void SwitchProfile( LoknApplication app, SettingsProfile profile )
	{
		if ( app.Settings.ActiveProfile == profile )
			return;

		// The profile switch posts its own session only when it differs from the one already recorded.
		_profile = profile;
		app.Settings.SetActiveProfile( profile );
	}

	/// <summary>Gets the profile a session belongs to: its conversation's or project's; null for Home, the chats outside projects and the feed, which every profile has.</summary>
	private SettingsProfile? ComputeSessionProfile( string key )
	{
		if ( ReadConversationSession( key ) is { } id )
			return store.Find( id )?.Profile;

		return ProjectsModule.ReadSession( key ) is { } projectId ? store.ProjectStore.Find( projectId )?.Profile : null;
	}

	/// <summary>Returns to the newly active profile's own session when the user switches profile.</summary>
	private void UpdateProfile()
	{
		if ( _context is not { } context || context.App.Settings.ActiveProfile == _profile )
			return;

		_profile = context.App.Settings.ActiveProfile;
		// Settings notify inside the change; the layout moves once it completes.
		context.Post( () => OpenProfileSession( context.App ) );
	}

	/// <summary>Opens the active profile's current session, or Home when it has none that still exists.</summary>
	private void OpenProfileSession( LoknApplication app )
	{
		if ( !OpenSession( app, FindWorkspaces( app )?.SessionFor( app.Settings.ActiveProfile ) ) )
			OpenHome( app );
	}

	/// <summary>Opens a session by key for the active profile: a chat's, a project's, the chats outside projects, the notification feed, or Home.</summary>
	/// <returns>Whether the key names a session of the active profile that still exists.</returns>
	private bool OpenSession( LoknApplication app, string? key )
	{
		var profile = app.Settings.ActiveProfile;
		if ( ReadConversationSession( key ) is { } conversationId && store.Find( conversationId ) is { } conversation && conversation.Profile == profile )
			SelectConversation( app, conversation.Id );
		else if ( ProjectsModule.ReadSession( key ) is { } projectId && store.ProjectStore.Find( projectId ) is { } project && project.Profile == profile )
			OpenProject( app, project );
		else if ( key == ChatsSession )
			OpenChats( app );
		else if ( key == HomeSession )
			OpenHome( app );
		else
			return false;
		return true;
	}

	/// <summary>Opens Home: one empty conversation panel alone, keeping the session it leaves as it is.</summary>
	internal void OpenHome( LoknApplication app )
	{
		FindWorkspaces( app )?.OpenSession( app, HomeSession, "Home", () => ShowHomeAlone( app ) );

		// Home stays one empty panel even when it was split, or a chat was opened in it, while it was current.
		if ( !IsHomeAlone( app ) )
			ShowHomeAlone( app );
	}

	/// <summary>Opens a project's session with its workspaces and layout through the Projects module; a first opening shows its latest chat.</summary>
	internal void OpenProject( LoknApplication app, ProjectInfo project ) => Projects.OpenProject( app, project.Id, () =>
	{
		app.Panels.HideAll();
		if ( store.Conversations( project.Profile, project.Id ).Where( conversation => conversation.HasChat ).MaxBy( conversation => conversation.UpdatedAt ) is { } latest )
			OpenConversation( app, latest.Id );
		else
			CreateConversation( app, project.Id );
	} );

	/// <summary>Opens the session of the chats outside projects; a first opening shows the latest of them full window.</summary>
	internal void OpenChats( LoknApplication app ) => OpenSession( app, ChatsSession, ChatsTitle, () =>
	{
		app.Panels.HideAll();
		if ( store.Conversations( app.Settings.ActiveProfile ).Where( conversation => conversation.HasChat ).MaxBy( conversation => conversation.UpdatedAt ) is { } latest )
			OpenConversation( app, latest.Id );
		else
			CreateConversation( app );
	} );

	/// <summary>Gets the task whose workspace is open, or null in Home, a project's session, the chats outside projects or the feed.</summary>
	internal ConversationInfo? FindSessionTask( LoknApplication app ) =>
		ReadConversationSession( FindWorkspaces( app )?.CurrentSession ) is { } id && store.Find( id ) is { } task && task.Profile == app.Settings.ActiveProfile ? task : null;

	/// <summary>Gets the folder a task's session works in: its project's worktree or directory, else the folder chosen for a chat outside projects;
	/// null for any other session, a task of another profile, or a chat outside projects with no folder chosen.</summary>
	private string? ComputeSessionFolder( LoknApplication app, string key ) =>
		ReadConversationSession( key ) is { } id && store.Find( id ) is { } task && task.Profile == app.Settings.ActiveProfile
			? task.ProjectId is null ? task.Directory : store.GetDirectory( task.Id )
			: null;

	/// <summary>Gets the project the current session belongs to: a project's own, or the open task's; null elsewhere, so a chat started there lands in it.</summary>
	internal Guid? ComputeSessionProject( LoknApplication app )
	{
		if ( FindSessionTask( app ) is { } task )
			return task.ProjectId;

		return ProjectsModule.ReadSession( FindWorkspaces( app )?.CurrentSession ) is { } id && store.ProjectStore.Find( id )?.Profile == app.Settings.ActiveProfile
			? id : null;
	}

	/// <summary>Lists Home's cards: the active profile's projects and, when it has any, the chats outside projects, most recently used first.</summary>
	internal IReadOnlyList<HomeEntry> ComputeEntries( LoknApplication app )
	{
		var profile = app.Settings.ActiveProfile;
		var opened = (FindWorkspaces( app )?.Sessions ?? []).ToDictionary( session => session.Key, session => session.LastOpened, StringComparer.Ordinal );
		DateTimeOffset? Latest( string key, IReadOnlyList<ConversationInfo> conversations )
		{
			var last = new[] { opened.GetValueOrDefault( key ), conversations.MaxBy( conversation => conversation.UpdatedAt )?.UpdatedAt ?? DateTimeOffset.MinValue }.Max();
			return last == DateTimeOffset.MinValue ? null : last;
		}

		var entries = store.ProjectStore.Projects( profile ).Select( project =>
		{
			var key = ProjectsModule.SessionKey( project.Id );
			var conversations = store.Conversations( profile, project.Id );
			return new HomeEntry( key, project.Name, project.Directory, Latest( key, conversations ), conversations.Count, project );
		} ).ToList();

		var loose = store.Conversations( profile );
		if ( loose.Count > 0 )
			entries.Add( new( ChatsSession, ChatsTitle, "Conversations outside projects", Latest( ChatsSession, loose ), loose.Count, null ) );

		return entries
			.OrderByDescending( entry => entry.LastUsed ?? DateTimeOffset.MinValue )
			.ThenBy( entry => entry.Title, StringComparer.CurrentCultureIgnoreCase )
			.ToArray();
	}

	/// <summary>Opens what a card stands for.</summary>
	internal void Open( LoknApplication app, HomeEntry entry )
	{
		if ( entry.Project is { } project )
			OpenProject( app, project );
		else
			OpenChats( app );
	}

	/// <summary>Opens the project for a folder, creating it in the active profile when no project has that directory.</summary>
	internal void OpenFolder( LoknApplication app, string directory )
	{
		directory = Path.TrimEndingDirectorySeparator( Path.GetFullPath( directory ) );
		var profile = app.Settings.ActiveProfile;
		var project = store.ProjectStore.Projects( profile ).FirstOrDefault( item => string.Equals( Path.TrimEndingDirectorySeparator( Path.GetFullPath( item.Directory ) ), directory, StringComparison.OrdinalIgnoreCase ) )
			?? store.ProjectStore.Create( profile, Path.GetFileName( directory ) is { Length: > 0 } name ? name : directory, directory );
		OpenProject( app, project );
	}

	/// <summary>Reports whether one empty conversation panel is all that shows.</summary>
	private bool IsHomeAlone( LoknApplication app ) =>
		app.Panels.Panes is [{ PanelId: { } id }] && _panels.TryGetValue( id, out var panel ) && panel.IsEmpty;

	/// <summary>Shows one empty conversation panel alone: an empty one already open, else a shown chat that is not working, emptied, else a new one.</summary>
	/// <remarks>A chat that is working is never emptied; it stays open, hidden, and Home gets a panel of its own.</remarks>
	private void ShowHomeAlone( LoknApplication app )
	{
		var live = FindPanels( app );
		var reuse = live.FirstOrDefault( item => item.Panel.IsEmpty ).Id;
		if ( reuse == Guid.Empty && live.FirstOrDefault( item => app.Panels.IsShown( item.Id ) && !item.Panel.IsBusy ) is { Panel: { } idle } shown )
		{
			idle.Unload();
			reuse = shown.Id;
		}

		app.Panels.HideAll();
		if ( reuse == Guid.Empty )
			app.Panels.Open( ChatPanelType );
		else
			app.Panels.Show( reuse, app.Panels.Root.Id );
	}

	/// <summary>Opens a Workspaces session, or arranges its first layout directly when the Workspaces module is not loaded.</summary>
	private static void OpenSession( LoknApplication app, string key, string title, Action createLayout )
	{
		if ( FindWorkspaces( app ) is { } workspaces )
			workspaces.OpenSession( app, key, title, createLayout );
		else
			createLayout();
	}

	/// <summary>Gets the Workspaces module, which owns sessions, or null when it is not loaded.</summary>
	internal static WorkspacesModule? FindWorkspaces( LoknApplication app ) =>
		app.Modules.Any( module => module.Id == "workspaces" ) ? app.GetModule<WorkspacesModule>() : null;
}
