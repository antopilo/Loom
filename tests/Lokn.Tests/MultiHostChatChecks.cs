using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;
using Lokn.Modules.Workspaces;

namespace Lokn.Tests;

/// <summary>Checks chats on several hosts: chat references and host session keys, paired hosts' mirrors, navigation, Search, links, waiting chats and removing a host.</summary>
internal static class MultiHostChatChecks
{
	/// <summary>Runs the multi-host chat checks headlessly, with fixture chats instead of Omp's.</summary>
	internal static void Run()
	{
		CheckText();
		CheckHosts();
		CheckWaiting();
		Console.WriteLine( "PASS multi-host chats: chat references and host session keys, paired hosts' mirrors added once and detached, navigation and host switching, panels beside a paired chat, Search and links across hosts, waiting keyed by host, session folders, panels waiting for a host's chats, and removing a host." );
	}

	/// <summary>Checks the text forms of chats and session keys: This PC's stay as they were, a paired host's carry <c>@hostId</c>.</summary>
	private static void CheckText()
	{
		var id = Guid.NewGuid();
		var local = ChatRef.Local( id );
		var paired = new ChatRef( "h1", id );
		CoreChecks.Check( local.IsLocal && local.HostId == LoknHost.LocalId && local.ToString() == id.ToString( "N" ) && !paired.IsLocal && paired.ToString() == id.ToString( "N" ) + "@h1",
			"A chat on This PC reads as its N GUID, as before hosts, and a paired host's chat adds @ and the host." );
		CoreChecks.Check( ChatRef.TryParse( local.ToString(), out var readLocal ) && readLocal == local && ChatRef.TryParse( paired.ToString(), out var readPaired ) && readPaired == paired
			&& ChatRef.TryParse( id.ToString( "D" ), out var readD ) && readD == local && ChatRef.TryParse( id.ToString( "D" ) + "@h1", out var readPairedD ) && readPairedD == paired,
			"A chat's text reads back to the same chat, from N or D GUIDs, with or without a host." );
		string?[] bad = [null, "", "nope", "@h1", id.ToString( "N" ) + "@", id.ToString( "N" ) + "@a@b", id.ToString( "N" ) + "@a/b", id.ToString( "B" )];
		CoreChecks.Check( bad.All( text => !ChatRef.TryParse( text, out var none ) && none == default ), "Text that names no chat, or a host that cannot be one, is refused." );

		CoreChecks.Check( HostSessionKeys.Qualify( "chats", LoknHost.LocalId ) == "chats" && HostSessionKeys.Qualify( "chats", "h1" ) == "chats@h1"
			&& HostSessionKeys.Split( "chats" ) == ("chats", LoknHost.LocalId) && HostSessionKeys.Split( "conversation:x@h1" ) == ("conversation:x", "h1"),
			"Session keys of This PC stay unqualified, a paired host's end with @ and the host, and a key splits back into both." );
		CoreChecks.Check( ConversationsModule.ConversationSession( local ) == "conversation:" + id.ToString( "N" ) && ConversationsModule.ConversationSession( paired ) == $"conversation:{id:N}@h1"
			&& ConversationsModule.ReadConversationSession( ConversationsModule.ConversationSession( local ) ) == local
			&& ConversationsModule.ReadConversationSession( ConversationsModule.ConversationSession( paired ) ) == paired
			&& ConversationsModule.ReadConversationSession( ConversationsModule.HomeSession ) is null && ConversationsModule.ReadConversationSession( "conversation:nope" ) is null
			&& ConversationsModule.ReadConversationSession( null ) is null,
			"A chat's session key is unchanged on This PC, qualified on a paired host, and reads back to the chat." );
	}

	/// <summary>Checks paired hosts' chats: mirrors added once and detached, keys, navigation, host switching, Search, links, waiting, session folders and removal.</summary>
	private static void CheckHosts()
	{
		var path = Path.Combine( Path.GetTempPath(), "lokn-chat-hosts-" + Guid.NewGuid().ToString( "N" ) + ".json" );
		var (app, workspaces, store, module) = Start( path );
		var profile = app.Settings.ActiveProfile;
		var local = module.Hosts.Local;
		CoreChecks.Check( local.IsLocal && local.IsSynced && ReferenceEquals( local.Store, store ) && module.Hosts.All is [var first] && ReferenceEquals( first, local )
			&& ReferenceEquals( module.Hosts.Find( LoknHost.LocalId ), local ) && ReferenceEquals( module.Hosts.Current, local ),
			"This PC's chats are the module's own store, always synced, and the only host at first." );

		// A paired host's chats are mirrors the Remote module fills; until then they refuse changes and their sessions are offline.
		var desk = app.Hosts.Add( "h1", "Desk" );
		var chats = module.Hosts.Add( desk );
		CoreChecks.Check( ReferenceEquals( module.Hosts.Add( desk ), chats ) && ReferenceEquals( module.Hosts.Find( "h1" ), chats ) && ReferenceEquals( module.Hosts.Add( app.Hosts.Local ), local )
			&& module.Hosts.All.Count == 2 && ReferenceEquals( module.Hosts.All[0], local ) && !chats.IsSynced && !chats.IsLocal && chats.Name == "Desk"
			&& chats.Store.IsMirror && chats.Projects.IsMirror && !ReferenceEquals( chats.Store, store ) && module.Hosts.Find( "zz" ) is null,
			"A paired host's chats are mirror stores of their own, added once per host after This PC, and not synced yet." );
		var op = StoreOp.Build( StoreOp.Conversations, nameof( ConversationStore.SetPinned ), new { id = Guid.NewGuid(), isPinned = true } );
		var offline = chats.Sessions.GetOrCreate( Guid.NewGuid() );
		CoreChecks.Check( chats.Store.Forward?.Invoke( op ) == false && chats.Projects.Forward?.Invoke( op ) == false && offline.IsRemote,
			"A detached host's mirrors refuse every change and its sessions are offline proxies." );
		chats.Sessions.Remove( offline.ConversationId );

		chats.Store.Forward = _ => true;
		chats.Projects.Forward = _ => true;
		var rig = chats.Projects.Create( profile, "Rig", Path.Combine( Path.GetTempPath(), "rig" ) );
		var deskChat = chats.Ref( chats.Store.CreateConversation( profile, title: "Desk chat" ).Id );
		var rigTask = chats.Ref( chats.Store.CreateConversation( profile, rig.Id, "Rig task", hasChat: false ).Id );
		var synced = 0;
		chats.Synced += () => synced++;
		chats.MarkSynced();
		CoreChecks.Check( chats.IsSynced && synced == 1, "Marking a host's chats synced raises Synced." );
		var localChat = ChatRef.Local( store.CreateConversation( profile, title: "Local chat" ).Id );

		CoreChecks.Check( ConversationsModule.ChatsSession( local ) == ConversationsModule.ChatsKey && ConversationsModule.ChatsSession( chats ) == ConversationsModule.ChatsKey + "@h1"
			&& ConversationsModule.ProjectSession( chats, rig.Id ) == ProjectsModule.SessionKey( rig.Id, "h1" )
			&& module.ReadProjectSession( ConversationsModule.ProjectSession( chats, rig.Id ) ) is { } project && ReferenceEquals( project.Host, chats ) && project.ProjectId == rig.Id
			&& module.ReadProjectSession( ProjectsModule.SessionKey( rig.Id, "zz" ) ) is null,
			"A paired host's chats and projects have session keys of their own, which read back to the host." );
		CoreChecks.Check( module.Hosts.Find( deskChat ) is { } found && ReferenceEquals( found.Host, chats ) && found.Ref == deskChat
			&& module.Hosts.Find( ChatRef.Local( deskChat.ConversationId ) ) is null && module.Hosts.Find( new ChatRef( "zz", deskChat.ConversationId ) ) is null,
			"A chat is found on its own host only." );

		// Opening a paired host's chat goes to its own session on that host.
		var deskPanel = module.SelectConversation( app, deskChat );
		CoreChecks.Check( workspaces.CurrentSession == ConversationsModule.ConversationSession( deskChat ) && app.Hosts.Current.Id == "h1" && ReferenceEquals( module.Hosts.Current, chats )
			&& app.Panels.GetHostId( deskPanel ) == "h1" && module.GetSelected( app ) == deskChat && module.FindConversation( deskPanel )?.Ref == deskChat,
			"Opening a paired host's chat opens its own session, keyed with the host, makes the host current, and its panel works on that host." );
		var beside = app.Panels.OpenBeside( "beside.panel", deskPanel );
		CoreChecks.Check( app.Panels.GetHostId( beside ) == "h1", "A panel opened beside a paired host's chat, such as Git, works on that host." );
		app.Panels.Close( beside );
		module.OpenHome( app );
		CoreChecks.Check( workspaces.CurrentSession == ConversationsModule.HomeSession && app.Hosts.Current.Id == "h1", "Home keeps the host the user is on." );

		// Switching host opens that host's last session, else Home, and never changes the profile.
		module.SwitchHost( app, LoknHost.LocalId );
		CoreChecks.Check( app.Hosts.Current.IsLocal && workspaces.CurrentSession == ConversationsModule.HomeSession && app.Settings.ActiveProfile == profile,
			"Switching to a host without sessions of its own opens Home on it." );
		var localPanel = module.SelectConversation( app, localChat );
		module.SwitchHost( app, "h1" );
		CoreChecks.Check( app.Hosts.Current.Id == "h1" && workspaces.CurrentSession == ConversationsModule.ConversationSession( deskChat ) && app.Settings.ActiveProfile == profile,
			"Switching to a paired host opens its most recently opened session, in the same profile." );
		module.SwitchHost( app, LoknHost.LocalId );
		CoreChecks.Check( app.Hosts.Current.IsLocal && workspaces.CurrentSession == ConversationsModule.ConversationSession( localChat ) && app.Panels.IsShown( localPanel ),
			"Switching back to This PC returns to its own last session." );
		var bench = module.Hosts.Add( app.Hosts.Add( "h2", "Bench" ) );
		bench.MarkSynced();
		module.SwitchHost( app, "h2" );
		CoreChecks.Check( app.Hosts.Current.Id == "h2" && workspaces.CurrentSession == ConversationsModule.HomeSession && app.Settings.ActiveProfile == profile
			&& app.Panels.Panes is [{ PanelId: { } homePanel }] && app.Panels.GetHostId( homePanel ) == "h2",
			"A host with no sessions opens Home, which works on that host." );

		// Search lists every host's chats and projects; a paired host's name its host and run on it.
		module.SwitchHost( app, LoknHost.LocalId );
		var items = app.Search.Search( "", "chats" ).SelectMany( group => group.Hits ).Select( hit => hit.Item ).ToList();
		CoreChecks.Check( items.FirstOrDefault( item => item.Title == "Local chat" ) is { Detail: null } localItem && localItem.Id == localChat.ToString()
			&& items.FirstOrDefault( item => item.Title == "Desk chat" ) is { Detail: "Desk" } deskItem && deskItem.Id == deskChat.ToString()
			&& items.FirstOrDefault( item => item.Title == "Rig task" ) is { Detail: var rigDetail } && rigDetail == "Rig · Desk",
			"Search lists every host's chats, naming the host and its project off This PC, with ids that name the host." );
		var rigItem = app.Search.Search( "rig", "projects" ).SelectMany( group => group.Hits ).Select( hit => hit.Item ).FirstOrDefault( item => item.Title == "Rig" );
		CoreChecks.Check( rigItem is { Detail: var rigFolder } && rigFolder == $"{rig.Directory} · Desk" && rigItem.Id == ConversationsModule.ProjectSession( chats, rig.Id ),
			"Search lists a paired host's projects with the host after their folder." );
		app.Search.Run( app.Search.Search( "desk chat", "chats" ).SelectMany( group => group.Hits ).First( hit => hit.Item.Id == deskChat.ToString() ) );
		CoreChecks.Check( app.Hosts.Current.Id == "h1" && workspaces.CurrentSession == ConversationsModule.ConversationSession( deskChat ), "Running a paired host's chat in Search opens it on its host." );

		module.SwitchHost( app, LoknHost.LocalId );
		app.OpenLink( LoknLinks.Create( "conversations", "open", deskChat.ToString() ) );
		CoreChecks.Check( app.Hosts.Current.Id == "h1" && workspaces.CurrentSession == ConversationsModule.ConversationSession( deskChat ),
			"A lokn://conversations/open/<id>@<host> link opens the paired host's chat in its own session." );

		// A paired host's task and project sessions work in the project's folder on that host.
		string? Folder( string key ) => app.Extensions.Get<SessionFolder>( SessionFolder.Point ).Select( folder => folder.Compute( app, key ) ).FirstOrDefault( directory => directory is not null );
		CoreChecks.Check( Folder( ConversationsModule.ConversationSession( rigTask ) ) == rig.Directory && Folder( ConversationsModule.ProjectSession( chats, rig.Id ) ) == rig.Directory
			&& Folder( ConversationsModule.ConversationSession( new ChatRef( "zz", rigTask.ConversationId ) ) ) is null,
			"A paired host's task and project sessions work in its project's folder; a host whose chats are not here gives none." );
		module.SelectConversation( app, rigTask );
		CoreChecks.Check( app.Services.Get<IWorkingFolder>()?.Current == rig.Directory, "The working folder of a paired host's task is its project's folder." );
		module.OpenProject( app, chats, rig );
		CoreChecks.Check( workspaces.CurrentSession == ConversationsModule.ProjectSession( chats, rig.Id ) && app.Hosts.Current.Id == "h1", "A paired host's project opens its own session on that host." );

		// The same conversation identifier on two hosts is two chats: waiting and notifications keep them apart.
		module.SwitchHost( app, LoknHost.LocalId );
		var twin = store.CreateConversation( profile, title: "Twin" ).Id;
		chats.Store.Apply( StoreOp.Build( StoreOp.Conversations, nameof( ConversationStore.CreateConversation ), new { id = twin, profile, title = "Twin" } ) );
		var (localTwin, deskTwin) = (ChatRef.Local( twin ), chats.Ref( twin ));
		var asking = local.Sessions.GetOrCreate( twin );
		asking.UpdateFrame( Frame( """{"type":"agent_start"}""" ) );
		asking.UpdateFrame( Frame( """{"type":"extension_ui_request","id":"q1","method":"confirm","title":"Proceed?"}""" ) );
		CoreChecks.Check( module.ComputeWaiting( app ).SetEquals( [localTwin] ), "A chat no panel shows waits under its own host only." );
		var twinPanel = module.SelectConversation( app, deskTwin );
		app.Panels.RequestAttention( twinPanel, new( "Twin", "Finished and waiting for you" ) );
		CoreChecks.Check( module.ComputeWaiting( app ).SetEquals( [localTwin, deskTwin] ),
			"Opening a paired host's chat with the same identifier leaves This PC's waiting, and each waits apart." );
		app.Panels.ClearAttention( twinPanel );

		// Removing a host lets go of its chats: panels show Home, sessions stop, its sessions go, and This PC is current again.
		module.SelectConversation( app, deskChat );
		chats.Sessions.GetOrCreate( deskChat.ConversationId );
		var removed = 0;
		module.Hosts.Removed += _ => removed++;
		module.Hosts.Remove( "h1" );
		CoreChecks.Check( module.Hosts.Find( "h1" ) is null && module.Hosts.Find( deskChat ) is null && module.Hosts.All.Count == 2 && removed == 1,
			"A removed host's chats are gone from the list." );
		CoreChecks.Check( app.Panels.Sessions.Select( session => session.Content ).OfType<ConversationPanel>().All( panel => panel.Chat?.HostId != "h1" && panel.Waiting?.Host != "h1" )
			&& chats.Sessions.Sessions.Count == 0,
			"Panels that showed a removed host's chats show Home, and its sessions stop." );
		CoreChecks.Check( app.Hosts.Current.IsLocal && workspaces.CurrentSession == ConversationsModule.HomeSession
			&& workspaces.Sessions.All( session => HostSessionKeys.Split( session.Key ).HostId != "h1" ) && !module.ComputeWaiting( app ).Contains( deskTwin ),
			"Removing the current host goes Home on This PC and forgets the host's sessions." );
		app.Shutdown();
		File.Delete( path );
	}

	/// <summary>Checks conversation panels of a paired host whose chats have not arrived: they wait, save what they show, load once synced, and show Home when the chat is gone.</summary>
	private static void CheckWaiting()
	{
		var path = Path.Combine( Path.GetTempPath(), "lokn-chat-waiting-" + Guid.NewGuid().ToString( "N" ) + ".json" );
		var (app, _, _, module) = Start( path );
		var profile = app.Settings.ActiveProfile;
		var laptop = module.Hosts.Add( app.Hosts.Add( "h3", "Laptop" ) );
		var awaited = Guid.NewGuid();
		var input = new ConversationPanelInput( awaited, profile, Host: "h3" );
		var opened = app.Panels.Open( ConversationsModule.ChatPanelType, input: input, hostId: "h3" );
		var panel = Panel( app, opened );
		CoreChecks.Check( panel.Waiting == input && !panel.IsEmpty && panel.Chat is null && panel.CaptureState()?.Deserialize<ConversationPanelInput>() == input
			&& app.Panels.GetHostId( opened ) == "h3",
			"A chat panel of a host whose chats have not arrived waits for them, saves the input it opened with, and works on that host." );

		// A restored layout brings the panel back waiting, from the state it saved.
		var layout = app.Panels.CaptureLayout();
		app.Panels.Close( opened );
		CoreChecks.Check( app.Panels.ApplyLayout( layout ), "The layout with a waiting chat panel applies." );
		var restored = app.Panels.Sessions.Select( session => session.Content ).OfType<ConversationPanel>().Single( item => item.Waiting?.ConversationId == awaited );
		CoreChecks.Check( restored.Waiting == input, "A restored chat panel of a host whose chats have not arrived waits for them too." );

		laptop.Store.Load( [new ConversationInfo( awaited, profile, null, "Late chat", DateTimeOffset.UtcNow )] );
		CoreChecks.Check( restored.Waiting is not null, "A waiting panel keeps waiting until the host's chats are marked synced." );
		laptop.MarkSynced();
		CoreChecks.Check( restored.Waiting is null && restored.Chat == laptop.Ref( awaited ) && restored.Content is ChatFixture && restored.CaptureState()?.Deserialize<ConversationPanelInput>() == input,
			"Once the host's chats arrive, the waiting panel shows its chat." );

		var old = module.Hosts.Add( app.Hosts.Add( "h4", "Old box" ) );
		var missing = Panel( app, app.Panels.Open( ConversationsModule.ChatPanelType, input: new ConversationPanelInput( Guid.NewGuid(), profile, Host: "h4" ), hostId: "h4" ) );
		CoreChecks.Check( missing.Waiting is not null, "A panel waits for a chat of a host that is not synced." );
		old.MarkSynced();
		CoreChecks.Check( missing.IsEmpty && missing.Waiting is null && missing.Chat is null, "A waiting panel whose chat the host does not have shows Home." );

		// A panel restored before the Remote module adds its host waits for the host, then for its chats.
		var later = Guid.NewGuid();
		var early = Panel( app, app.Panels.Open( ConversationsModule.ChatPanelType, input: new ConversationPanelInput( later, profile, Host: "h5" ), hostId: "h5" ) );
		CoreChecks.Check( early.Waiting is { Host: "h5" }, "A panel of a host not paired yet waits for it." );
		var added = module.Hosts.Add( app.Hosts.Add( "h5", "New box" ) );
		added.Store.Load( [new ConversationInfo( later, profile, null, "Later chat", DateTimeOffset.UtcNow )] );
		CoreChecks.Check( early.Waiting is not null, "A host added later keeps its panels waiting until its chats arrive." );
		added.MarkSynced();
		CoreChecks.Check( early.Waiting is null && early.Chat == added.Ref( later ), "Once the added host's chats arrive, the panel shows its chat." );
		app.Shutdown();
		File.Delete( path );
	}

	/// <summary>Starts an application with Workspaces, Omp for offline sessions, Projects, Conversations over fixture chats, and a host-bound fixture panel.</summary>
	private static (LoknApplication App, WorkspacesModule Workspaces, ConversationStore Store, ConversationsModule Module) Start( string path )
	{
		var app = new LoknApplication();
		app.BindDispatcher( action =>
		{
			action();
			return true;
		} );
		var workspaces = new WorkspacesModule( path );
		var store = new ConversationStore( new ProjectStore() );
		var module = new ConversationsModule( store, ( context, host, id ) => new ChatFixture( host.Ref( id ), host.Store.Get( id ).Profile ) );
		CoreChecks.Check( app.Load( workspaces ) && app.Load( new OmpModule() ) && ProjectChecks.LoadProjects( app, store ) && app.Load( module )
			&& app.Load( new FixtureModule( "beside", builder => builder.Panel( "panel", "Beside", _ => new FixtureContent(), onHost: true ) ) ),
			"Workspaces, Omp, Projects, Conversations and a host-bound fixture panel load for the multi-host chat checks." );
		return (app, workspaces, store, module);
	}

	/// <summary>Gets an open conversation panel by identifier.</summary>
	private static ConversationPanel Panel( LoknApplication app, Guid panelId ) =>
		(ConversationPanel)app.Panels.Sessions.Single( session => session.Info.Id == panelId ).Content!;

	/// <summary>Parses one Omp frame.</summary>
	private static JsonElement Frame( string json ) => JsonDocument.Parse( json ).RootElement.Clone();

	/// <summary>Stands in for a chat of any host, identified by its chat reference.</summary>
	private sealed class ChatFixture( ChatRef chat, SettingsProfile profile ) : PanelContent
	{
		/// <summary>Builds the chat's reference.</summary>
		public override Blob Build() => new Text( chat.ToString() );

		/// <summary>Captures the chat the panel shows, as a chat panel does: its host only off This PC.</summary>
		public override JsonElement? CaptureState() => JsonSerializer.SerializeToElement( ConversationsModule.BuildInput( chat, profile ) );
	}
}
