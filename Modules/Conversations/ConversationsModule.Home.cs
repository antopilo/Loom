using Lokn.Core;
using Lokn.Modules.Projects;
using Lokn.Modules.Workspaces;

namespace Lokn.Modules.Conversations;

/// <summary>Describes one card on Home: a recent project, or the chats outside projects, of one host.</summary>
/// <param name="Key">The session the card opens.</param>
/// <param name="Title">The card's name.</param>
/// <param name="Detail">The directory or description under the name.</param>
/// <param name="LastUsed">When the user last opened it or chatted in it, or null.</param>
/// <param name="Conversations">How many conversations it holds.</param>
/// <param name="Project">The project, or null for the chats outside projects.</param>
/// <param name="Host">The host whose project or chats it opens.</param>
internal sealed record HomeEntry( string Key, string Title, string Detail, DateTimeOffset? LastUsed, int Conversations, ProjectInfo? Project, ChatHost Host );

/// <summary>Opens Home, the empty conversation panel, and the Projects module's session per project, each with its own saved workspaces and layout.</summary>
/// <remarks>
/// Leaving a session keeps its panels running, hidden, so an agent working in one project keeps working while another is open.
/// Without the Workspaces module, the same actions arrange panels without sessions. A paired host's sessions are keyed like This PC's,
/// followed by <c>@hostId</c>; opening one makes its host the current one (<see cref="LoknHosts.Current"/>), and Home keeps the host the user is on.
/// </remarks>
public sealed partial class ConversationsModule
{
	/// <summary>Defines Home's own session.</summary>
	internal const string HomeSession = "home";

	/// <summary>Defines This PC's session of the chats outside projects; a paired host's is this key qualified with the host.</summary>
	internal const string ChatsKey = "chats";

	/// <summary>Defines the name of the chats outside projects, shown over them in the sidebar, on their Home card and tab group, and as their session's title.</summary>
	internal const string ChatsTitle = "Scratch";

	/// <summary>Defines the prefix of a conversation's session key.</summary>
	internal const string ConversationPrefix = "conversation:";

	/// <summary>Gets a chat's session key: <c>conversation:&lt;id&gt;</c>, followed by <c>@hostId</c> on a paired host.</summary>
	internal static string ConversationSession( ChatRef chat ) => HostSessionKeys.Qualify( ConversationPrefix + chat.ConversationId.ToString( "N" ), chat.HostId );

	/// <summary>Reads the chat a session key stands for, or null for any other session.</summary>
	internal static ChatRef? ReadConversationSession( string? key )
	{
		if ( key is null )
			return null;

		var (bare, host) = HostSessionKeys.Split( key );
		return bare.StartsWith( ConversationPrefix, StringComparison.Ordinal ) && host.Length > 0 && Guid.TryParseExact( bare[ConversationPrefix.Length..], "N", out var id )
			? new ChatRef( host, id )
			: null;
	}

	/// <summary>Gets the session key of a host's chats outside projects.</summary>
	internal static string ChatsSession( ChatHost host ) => HostSessionKeys.Qualify( ChatsKey, host.Id );

	/// <summary>Gets the session key of a host's project.</summary>
	internal static string ProjectSession( ChatHost host, Guid projectId ) => ProjectsModule.SessionKey( projectId, host.Id );

	/// <summary>Reads the host and project a session key stands for, or null for any other session or a host whose chats are not here.</summary>
	internal (ChatHost Host, Guid ProjectId)? ReadProjectSession( string? key )
	{
		if ( key is null )
			return null;

		var (bare, hostId) = HostSessionKeys.Split( key );
		return ProjectsModule.ReadSession( bare ) is { } id && Hosts.Find( hostId ) is { } host ? (host, id) : null;
	}

	/// <summary>Reads the host whose chats outside projects a session key stands for, or null for any other session or a host whose chats are not here.</summary>
	private ChatHost? ReadChatsSession( string? key )
	{
		if ( key is null )
			return null;

		var (bare, hostId) = HostSessionKeys.Split( key );
		return bare == ChatsKey ? Hosts.Find( hostId ) : null;
	}

	/// <summary>Reports whether a session key belongs to one host: a chat's, a project's or the chats outside projects; Home, the feed and other sessions belong to every host.</summary>
	private static bool IsHostSession( string key )
	{
		var bare = HostSessionKeys.Split( key ).Key;
		return bare.StartsWith( ConversationPrefix, StringComparison.Ordinal ) || bare.StartsWith( ProjectsModule.SessionPrefix, StringComparison.Ordinal ) || bare == ChatsKey;
	}

	/// <summary>Reports whether the open session belongs to one host, such as a project's or a chat's, whose host stays current while it is open; Home, the
	/// feed and other sessions every host has keep whichever host is current, so another may become current without leaving them.</summary>
	/// <param name="app">The application.</param>
	public bool IsHostSessionOpen( LoknApplication app ) => FindWorkspaces( app )?.CurrentSession is { } key && IsHostSession( key );

	/// <summary>Makes the open session's host the current one; Home and other sessions every host has keep the host the user is on.</summary>
	/// <remarks>Runs after every session this module opens, whenever Workspaces changes session, and at start, so the sidebar, new panels and the working folder agree.</remarks>
	private static void FollowSessionHost( LoknApplication app )
	{
		if ( FindWorkspaces( app )?.CurrentSession is { } key && IsHostSession( key ) )
			app.Hosts.SetCurrent( HostSessionKeys.Split( key ).HostId );
	}

	/// <summary>Follows a session change made anywhere, such as by a recent tab or the Workspaces grid.</summary>
	private void UpdateSessionHost()
	{
		if ( _context is { } context )
			FollowSessionHost( context.App );
	}

	/// <summary>Opens a task in its own workspace: the layout the user left it in, or at first just its chat, full window; its host becomes the current one.</summary>
	/// <remarks>
	/// Without the Workspaces module, the conversation opens as <see cref="OpenConversation"/> shows it. A task without a chat
	/// starts as one empty pane, which offers to open a panel.
	/// </remarks>
	/// <returns>The panel showing the conversation, or an empty identifier for a task without a chat.</returns>
	public Guid SelectConversation( LoknApplication app, ChatRef chat )
	{
		var shown = GetChat( chat );
		var conversation = shown.Conversation;
		if ( conversation.Profile != app.Settings.ActiveProfile )
			throw new InvalidOperationException( "Switch to this conversation's profile before opening it." );
		UpdateSeen( app, chat );
		// The host is current before the layout opens, so panels it opens without a host of their own open on it.
		app.Hosts.SetCurrent( chat.HostId );
		if ( FindWorkspaces( app ) is not { } workspaces )
			return conversation.HasChat ? OpenConversation( app, chat ) : Guid.Empty;

		Select( conversation.Profile, chat );
		workspaces.OpenSession( app, ConversationSession( chat ), conversation.Title, () =>
		{
			if ( conversation.HasChat )
				ShowConversationAlone( app, shown );
			else
				app.Panels.HideAll();
		} );
		FollowSessionHost( app );
		if ( !conversation.HasChat )
			return Guid.Empty;

		// The user may have closed the chat while this workspace was open; it comes back beside what is left.
		var panel = FindPanels( app ).FirstOrDefault( item => item.Panel.Chat == chat ).Id;
		if ( panel == Guid.Empty )
			return OpenChatPanel( app, shown );

		// A chat the user moved into another of the task's workspaces is shown there, not pulled into this one.
		workspaces.ShowWorkspaceOf( app, panel );
		app.Panels.Activate( panel );
		return panel;
	}

	/// <summary>Shows a conversation's chat alone: its open panel, shown or hidden, else a new one; every other panel stays open, hidden.</summary>
	private void ShowConversationAlone( LoknApplication app, HostedChat chat )
	{
		var existing = FindPanels( app ).FirstOrDefault( item => item.Panel.Chat == chat.Ref ).Id;
		app.Panels.HideAll();
		if ( existing == Guid.Empty )
			OpenChatPanel( app, chat );
		else
			app.Panels.Show( existing, app.Panels.Root.Id );
	}

	/// <summary>Records a profile's selected chat on its host.</summary>
	private void Select( SettingsProfile profile, ChatRef chat )
	{
		var key = (profile, chat.HostId);
		if ( _selected.Value.TryGetValue( key, out var selected ) && selected == chat.ConversationId )
			return;

		_selected.Value = new Dictionary<(SettingsProfile, string), Guid>( _selected.Value ) { [key] = chat.ConversationId };
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
		if ( ReadConversationSession( key ) is { } chat )
			return Hosts.Find( chat )?.Conversation.Profile;

		return ReadProjectSession( key ) is { } project ? project.Host.Projects.Find( project.ProjectId )?.Profile : null;
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

	/// <summary>Opens a session by key for the active profile: a chat's, a project's, the chats outside projects, of any host, the notification feed, or Home.</summary>
	/// <remarks>A paired host's session opens even before the host sent its chats: Workspaces restores its saved layout, whose chat panels wait for them.</remarks>
	/// <returns>Whether the key names a session of the active profile that still exists, or one of a host whose chats are still to come.</returns>
	private bool OpenSession( LoknApplication app, string? key )
	{
		var profile = app.Settings.ActiveProfile;
		if ( ReadConversationSession( key ) is { } chat )
		{
			if ( Hosts.Find( chat.HostId ) is { IsSynced: false } pending )
			{
				OpenPendingSession( app, key!, pending, () =>
				{
					app.Panels.HideAll();
					app.Panels.Open( ChatPanelType, input: BuildInput( chat, profile ), hostId: chat.HostId );
				} );
				return true;
			}

			if ( Hosts.Find( chat ) is not { } shown || shown.Conversation.Profile != profile )
				return false;
			SelectConversation( app, chat );
		}
		else if ( ReadProjectSession( key ) is { } project )
		{
			if ( !project.Host.IsSynced )
			{
				OpenPendingSession( app, key!, project.Host, app.Panels.HideAll );
				return true;
			}

			if ( project.Host.Projects.Find( project.ProjectId ) is not { } info || info.Profile != profile )
				return false;
			OpenProject( app, project.Host, info );
		}
		else if ( ReadChatsSession( key ) is { } host )
		{
			if ( host.IsSynced )
				OpenChats( app, host );
			else
				OpenPendingSession( app, key!, host, app.Panels.HideAll );
		}
		else if ( key == HomeSession )
			OpenHome( app );
		else
			return false;
		return true;
	}

	/// <summary>Opens a session of a host whose chats are still to come, under the name it was last opened with, and makes the host current.</summary>
	private static void OpenPendingSession( LoknApplication app, string key, ChatHost host, Action createLayout )
	{
		app.Hosts.SetCurrent( host.Id );
		var title = FindWorkspaces( app )?.Sessions.FirstOrDefault( session => session.Key == key )?.Title ?? host.Name;
		OpenSession( app, key, title, createLayout );
		FollowSessionHost( app );
	}

	/// <summary>Switches to a host: makes it current and opens its most recently opened session of the active profile, else Home on it.</summary>
	/// <param name="app">The application.</param>
	/// <param name="hostId">The host; an unknown one is This PC.</param>
	public void SwitchHost( LoknApplication app, string hostId )
	{
		var host = Hosts.Find( hostId ) ?? Hosts.Local;
		app.Hosts.SetCurrent( host.Id );
		if ( FindWorkspaces( app ) is { } workspaces )
		{
			// Sessions are listed most recently opened first; one of another profile or one that is gone is passed over.
			foreach ( var session in workspaces.Sessions.ToArray() )
			{
				if ( IsHostSession( session.Key ) && HostSessionKeys.Split( session.Key ).HostId == host.Id && OpenSession( app, session.Key ) )
					return;
			}
		}

		OpenHome( app );
	}

	/// <summary>Opens Home: one empty conversation panel alone, keeping the session it leaves as it is, and the host the user is on.</summary>
	internal void OpenHome( LoknApplication app )
	{
		FindWorkspaces( app )?.OpenSession( app, HomeSession, "Home", () => ShowHomeAlone( app ) );

		// Home stays one empty panel even when it was split, or a chat was opened in it, while it was current.
		if ( !IsHomeAlone( app ) )
			ShowHomeAlone( app );
	}

	/// <summary>Opens a host's project's session with its workspaces and layout through the Projects module; a first opening shows its latest chat.</summary>
	internal void OpenProject( LoknApplication app, ChatHost host, ProjectInfo project )
	{
		app.Hosts.SetCurrent( host.Id );
		Projects.OpenProject( app, project, host.Id, () =>
		{
			app.Panels.HideAll();
			if ( host.Store.Conversations( project.Profile, project.Id ).Where( conversation => conversation.HasChat ).MaxBy( conversation => conversation.UpdatedAt ) is { } latest )
				OpenConversation( app, host.Ref( latest.Id ) );
			else
				CreateConversation( app, host, project.Id );
		} );
		FollowSessionHost( app );
	}

	/// <summary>Opens the session of a host's chats outside projects; a first opening shows the latest of them full window.</summary>
	internal void OpenChats( LoknApplication app, ChatHost host )
	{
		app.Hosts.SetCurrent( host.Id );
		OpenSession( app, ChatsSession( host ), FormatTitle( ChatsTitle, host ), () =>
		{
			app.Panels.HideAll();
			if ( host.Store.Conversations( app.Settings.ActiveProfile ).Where( conversation => conversation.HasChat ).MaxBy( conversation => conversation.UpdatedAt ) is { } latest )
				OpenConversation( app, host.Ref( latest.Id ) );
			else
				CreateConversation( app, host );
		} );
		FollowSessionHost( app );
	}

	/// <summary>Gets the task whose workspace is open, on any host, or null in Home, a project's session, the chats outside projects or the feed.</summary>
	internal HostedChat? FindSessionTask( LoknApplication app ) =>
		ReadConversationSession( FindWorkspaces( app )?.CurrentSession ) is { } chat && Hosts.Find( chat ) is { } task && task.Conversation.Profile == app.Settings.ActiveProfile
			? task : null;

	/// <summary>Gets the folder a session works in: a task's project's worktree or directory, else the folder chosen for a chat outside projects, and a paired
	/// host's project's directory (This PC's projects are the Projects module's); null for any other session, a task of another profile, or a chat outside
	/// projects with no folder chosen.</summary>
	private string? ComputeSessionFolder( LoknApplication app, string key )
	{
		var profile = app.Settings.ActiveProfile;
		if ( ReadConversationSession( key ) is { } chat )
		{
			return Hosts.Find( chat ) is { } task && task.Conversation.Profile == profile
				? task.Conversation.ProjectId is null ? task.Conversation.Directory : task.Host.Store.GetDirectory( task.Conversation.Id )
				: null;
		}

		return ReadProjectSession( key ) is { Host.IsLocal: false } project && project.Host.Projects.Find( project.ProjectId ) is { } info && info.Profile == profile
			? info.Directory
			: null;
	}

	/// <summary>Gets the project the current session belongs to on the current host: a project's own, or the open task's; null elsewhere, so a chat started there lands in it.</summary>
	internal Guid? ComputeSessionProject( LoknApplication app )
	{
		var host = Hosts.Current;
		if ( FindSessionTask( app ) is { } task )
			return task.Host == host ? task.Conversation.ProjectId : null;

		return ReadProjectSession( FindWorkspaces( app )?.CurrentSession ) is { } project && project.Host == host
			&& host.Projects.Find( project.ProjectId )?.Profile == app.Settings.ActiveProfile
			? project.ProjectId : null;
	}

	/// <summary>Lists Home's cards of a host: the active profile's projects and, when it has any, the chats outside projects, most recently used first.</summary>
	internal IReadOnlyList<HomeEntry> ComputeEntries( LoknApplication app, ChatHost host )
	{
		var profile = app.Settings.ActiveProfile;
		var opened = (FindWorkspaces( app )?.Sessions ?? []).ToDictionary( session => session.Key, session => session.LastOpened, StringComparer.Ordinal );
		DateTimeOffset? Latest( string key, IReadOnlyList<ConversationInfo> conversations )
		{
			var last = new[] { opened.GetValueOrDefault( key ), conversations.MaxBy( conversation => conversation.UpdatedAt )?.UpdatedAt ?? DateTimeOffset.MinValue }.Max();
			return last == DateTimeOffset.MinValue ? null : last;
		}

		var entries = host.Projects.Projects( profile ).Select( project =>
		{
			var key = ProjectSession( host, project.Id );
			var conversations = host.Store.Conversations( profile, project.Id );
			return new HomeEntry( key, project.Name, project.Directory, Latest( key, conversations ), conversations.Count, project, host );
		} ).ToList();

		var loose = host.Store.Conversations( profile );
		if ( loose.Count > 0 )
		{
			var key = ChatsSession( host );
			entries.Add( new( key, ChatsTitle, "Conversations outside projects", Latest( key, loose ), loose.Count, null, host ) );
		}

		return entries
			.OrderByDescending( entry => entry.LastUsed ?? DateTimeOffset.MinValue )
			.ThenBy( entry => entry.Title, StringComparer.CurrentCultureIgnoreCase )
			.ToArray();
	}

	/// <summary>Opens what a card stands for.</summary>
	internal void Open( LoknApplication app, HomeEntry entry )
	{
		if ( entry.Project is { } project )
			OpenProject( app, entry.Host, project );
		else
			OpenChats( app, entry.Host );
	}

	/// <summary>Opens the project for a host's folder, creating it in the active profile when no project of the host has that directory.</summary>
	internal void OpenFolder( LoknApplication app, ChatHost host, string directory )
	{
		directory = Path.TrimEndingDirectorySeparator( Path.GetFullPath( directory ) );
		var profile = app.Settings.ActiveProfile;
		var project = host.Projects.Projects( profile ).FirstOrDefault( item => string.Equals( Path.TrimEndingDirectorySeparator( Path.GetFullPath( item.Directory ) ), directory, StringComparison.OrdinalIgnoreCase ) )
			?? host.Projects.Create( profile, Path.GetFileName( directory ) is { Length: > 0 } name ? name : directory, directory );
		OpenProject( app, host, project );
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
