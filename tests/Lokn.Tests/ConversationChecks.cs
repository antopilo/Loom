using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Conversations;
using Lokn.Modules.Notifications;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;
using Lokn.Modules.Workspaces;

namespace Lokn.Tests;

/// <summary>Checks profile isolation, project management, and persisted conversation history.</summary>
internal static class ConversationChecks
{
	/// <summary>Runs conversation checks using isolated temporary history files.</summary>
	internal static void Run()
	{
		var directory = Path.Combine( Path.GetTempPath(), "lokn-conversations-" + Guid.NewGuid().ToString( "N" ) );
		var path = Path.Combine( directory, "history.json" );
		var projectsPath = Path.Combine( directory, "projects.json" );
		ConversationStore Reopen() => new( new ProjectStore( projectsPath ), path );
		var projects = new ProjectStore( projectsPath );
		var store = new ConversationStore( projects, path );
		var work = projects.Create( SettingsProfile.Work, "Office", directory );
		var personal = projects.Create( SettingsProfile.Personal, "Home", directory );
		var chat = store.CreateConversation( SettingsProfile.Work, work.Id );
		Check( projects.Projects( SettingsProfile.Personal ).Single().Id == personal.Id, "Projects stay within their profile." );
		Check( store.Conversations( SettingsProfile.Personal, work.Id ).Count == 0, "Work conversations never appear in Personal." );
		try
		{
			store.CreateConversation( SettingsProfile.Personal, work.Id );
			throw new InvalidOperationException( "Cross-profile projects must be rejected." );
		}
		catch ( ArgumentException )
		{
		}

		store.UpdateConversation( chat.Id, "Plan the release", work.Id );
		store.SaveBackendSession( chat.Id, Path.Combine( directory, "session" ) );
		var message = new ConversationMessage( Guid.NewGuid(), "assistant", "First", DateTimeOffset.UtcNow,
			JsonSerializer.SerializeToElement( new
			{
				type = "text",
				text = "First"
			} ) );
		store.AppendMessage( chat.Id, message );
		store.UpdateMessage( chat.Id, message with
		{
			Text = "First complete",
			RichContent = JsonSerializer.SerializeToElement( new
			{
				type = "text",
				text = "First complete"
			} )
		} );
		projects.Update( work.Id, "Work", directory );
		store.SaveDraft( chat.Id, "Unsent prompt" );
		Check( store.Get( chat.Id ).IsTitleChosen && !store.SetAutomaticTitle( chat.Id, "Generated", isGenerated: true ) && store.Get( chat.Id ).Title == "Plan the release",
			"A title the user chose is never replaced by a generated one." );
		var loose = store.CreateConversation( SettingsProfile.Work, work.Id );
		Check( store.SetAutomaticTitle( loose.Id, "fix the login", isGenerated: false ) && store.SetAutomaticTitle( loose.Id, "Login fix", isGenerated: true )
			&& store.Get( loose.Id ) is { Title: "Login fix", IsTitleGenerated: true, IsTitleChosen: false }, "Automatic titles replace each other and record a model summary." );
		store.SetPinned( chat.Id, true );
		store.Flush();
		File.WriteAllText( path, File.ReadAllText( path ).Replace( "\"Login fix\"", "\"\\uD83D\\uDCAC FEED: Login fix for portal users\"" ) );

		var reopened = Reopen();
		Check( reopened.Get( chat.Id ).Draft == "Unsent prompt", "An unsent prompt survives restart." );
		Check( reopened.Get( chat.Id ).IsPinned && !reopened.Get( loose.Id ).IsPinned && reopened.Get( chat.Id ).CreatedAt == store.Get( chat.Id ).CreatedAt,
			"Pins and start times survive restart." );
		Check( reopened.Get( loose.Id ) is { Title: "Login fix for portal", IsTitleGenerated: false, IsTitleChosen: false },
			"A generated title saved in Omp's icon-and-code form loads cut down, and is asked for again on the next turn." );
		reopened.DeleteConversation( loose.Id );
		reopened.SaveDraft( chat.Id, "" );
		reopened.Flush();
		Check( Reopen().Get( chat.Id ).Draft is null, "Clearing the composer removes the saved prompt." );
		Check( reopened.LastError is null && reopened.Get( chat.Id ).Title == "Plan the release", "Conversation metadata survives restart." );
		Check( reopened.Get( chat.Id ).Profile == SettingsProfile.Work && reopened.Get( chat.Id ).BackendSessionPath is not null, "Restart preserves the account and backend session directory." );
		Check( reopened.GetMessages( chat.Id ).Single().Text == "First complete", "Streaming replacements preserve one final message." );
		Check( reopened.GetMessages( chat.Id ).Single().RichContent!.Value.GetProperty( "text" ).GetString() == "First complete", "Rich JSON survives restart." );
		reopened.ProjectStore.Delete( work.Id );
		Check( reopened.Get( chat.Id ).ProjectId is null && reopened.GetMessages( chat.Id ).Count == 1, "Removing a project retains its conversations and messages." );
		reopened.Flush();
		Check( Reopen() is var detached && detached.ProjectStore.Find( work.Id ) is null && detached.Get( chat.Id ).ProjectId is null && detached.Get( chat.Id ).Title == "Plan the release",
			"Deleting a project takes its chats out of it for good, keeping them." );
		reopened.DeleteConversation( chat.Id );
		reopened.Flush();
		Check( Reopen().Conversations( SettingsProfile.Work ).Count == 0 && !File.Exists( Path.Combine( directory, "history", chat.Id.ToString( "N" ) + ".json" ) ),
			"Deleting a chat persists its removal and deletes its messages file." );

		var ordered = new ConversationStore();
		var older = ordered.CreateConversation( SettingsProfile.Personal );
		var newer = ordered.CreateConversation( SettingsProfile.Personal );
		ordered.AppendMessage( older.Id, new( Guid.NewGuid(), "user", "Again", DateTimeOffset.UtcNow ) );
		Check( ordered.Conversations( SettingsProfile.Personal ) is [var top, var bottom] && top.Id == newer.Id && bottom.Id == older.Id,
			"Activity never moves a chat; chats stay newest first by when they started." );
		ordered.MoveConversation( newer.Id, older.Id );
		var latest = ordered.CreateConversation( SettingsProfile.Personal );
		Check( ordered.Conversations( SettingsProfile.Personal ).Select( item => item.Id ).SequenceEqual( [latest.Id, older.Id, newer.Id] ),
			"A dragged chat keeps the place it was moved to, and chats started afterwards list above the placed ones." );
		var dropLine = typeof( Lokn.Modules.Workspaces.SidebarRows ).GetMethod( "BuildDropLine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static )!;
		Check( dropLine.Invoke( null, [true] ) is Goo.Container && dropLine.Invoke( null, [false] ) is Goo.Container,
			"The line that shows where a dragged task lands builds above and below a row, so dragging never breaks the sidebar." );

		File.WriteAllText( path, "{broken" );
		var broken = new ConversationStore( path );
		broken.CreateConversation( SettingsProfile.Personal );
		Check( broken.LastError is not null && File.ReadAllText( path ) == "{broken", "Unreadable history remains preserved after in-memory edits." );
		CheckDirectories( directory );
		CheckPanels();
		CheckSessions();
		CheckNotifications();
		CheckBackgroundChats();
		CheckMirrorStarts();
		CheckTidy( directory );
		CheckArchive( directory );
		Console.WriteLine( "PASS conversation profile isolation, project management, message replacement, rich history restart, chat folders, invalid-file preservation, conversation workspaces per profile, the notification feed workspace, chats no panel shows, deletes by store operation, chats started over a mirror, tidying a project's chats, and archiving idle chats." );
	}

	/// <summary>Checks which folder each chat works in, that a chosen folder survives restart, and that history from before chats chose folders loads.</summary>
	private static void CheckDirectories( string directory )
	{
		var path = Path.Combine( directory, "folders.json" );
		var projects = new ProjectStore( Path.Combine( directory, "folder-projects.json" ) );
		var store = new ConversationStore( projects, path );
		var project = projects.Create( SettingsProfile.Personal, "Repo", Path.Combine( directory, "repo" ) );
		var inProject = store.CreateConversation( SettingsProfile.Personal, project.Id );
		var loose = store.CreateConversation( SettingsProfile.Personal );
		Check( store.GetDirectory( loose.Id ) == ConversationStore.DefaultDirectory, "A chat outside projects works in the home folder, never Lokn's own, until it chooses a folder." );
		var chosen = Path.Combine( directory, "chosen" );
		store.SetDirectory( loose.Id, chosen );
		Check( store.GetDirectory( loose.Id ) == chosen && store.GetDirectory( inProject.Id ) == project.Directory,
			"A chat outside projects works in the folder chosen for it, and a project chat in its project's folder." );
		var isRefused = false;
		try
		{
			store.SetDirectory( inProject.Id, chosen );
		}
		catch ( InvalidOperationException )
		{
			isRefused = true;
		}
		Check( isRefused && store.Get( inProject.Id ).Directory is null, "A project chat cannot choose another folder." );

		var worktree = Path.Combine( directory, "repo.worktrees", "feature" );
		var parallel = store.CreateConversation( SettingsProfile.Personal, project.Id );
		store.SetWorktree( parallel.Id, worktree );
		Check( store.GetDirectory( parallel.Id ) == worktree && store.GetDirectory( inProject.Id ) == project.Directory,
			"A project chat moved into a worktree works there, and the project's other chats stay in its directory." );
		var groups = HistoryContent.ComputeWorktreeGroups( project, [store.Get( parallel.Id ), store.Get( inProject.Id )] );
		Check( groups is [{ IsMain: false } first, { IsMain: true } last] && first.Conversations.Single().Id == parallel.Id && last.Conversations.Single().Id == inProject.Id,
			"The sidebar lists a project's chats under their worktree, then the main checkout's last." );
		Check( HistoryContent.ComputeWorktreeGroups( project, [store.Get( inProject.Id )] ).Count == 1, "Chats that all work in the project's directory stay in one flat list." );

		store.Flush();
		var reopened = new ConversationStore( new ProjectStore( Path.Combine( directory, "folder-projects.json" ) ), path );
		Check( reopened.LastError is null && reopened.Get( loose.Id ).Directory == chosen && reopened.GetDirectory( loose.Id ) == chosen, "A chat's chosen folder survives restart." );
		Check( reopened.GetDirectory( parallel.Id ) == worktree, "A chat's worktree survives restart." );
		reopened.ReleaseWorktree( worktree + Path.DirectorySeparatorChar, project.Directory );
		Check( reopened.GetDirectory( parallel.Id ) == project.Directory && reopened.Get( parallel.Id ).Worktree is null, "Removing a worktree moves its chats back to the project's directory." );
		reopened.UpdateConversation( loose.Id, reopened.Get( loose.Id ).Title, project.Id );
		Check( reopened.GetDirectory( loose.Id ) == project.Directory, "A project's folder wins over the folder a chat chose before joining it." );
		reopened.ProjectStore.Delete( project.Id );
		Check( reopened.GetDirectory( loose.Id ) == chosen && reopened.GetDirectory( inProject.Id ) == ConversationStore.DefaultDirectory,
			"Without a project, a chat works in its chosen folder again, or else the home folder." );

		var legacyPath = Path.Combine( directory, "legacy.json" );
		var legacyId = Guid.NewGuid();
		var startedId = Guid.NewGuid();
		File.WriteAllText( legacyPath, $$$"""
			{"Version":1,"Projects":[],"Conversations":[
				{"Id":"{{{legacyId}}}","Profile":"Personal","ProjectId":null,"Title":"Old chat","UpdatedAt":"2026-01-01T00:00:00+00:00"},
				{"Id":"{{{startedId}}}","Profile":"Personal","ProjectId":null,"Title":"Started chat","UpdatedAt":"2026-01-01T00:00:00+00:00","BackendSessionPath":"C:\\sessions\\started"}],
				"Messages":{"{{{legacyId}}}":[{"Id":"{{{Guid.NewGuid()}}}","Role":"user","Text":"Kept from one file","CreatedAt":"2025-12-31T00:00:00+00:00"}]}}
			""" );
		var legacy = new ConversationStore( legacyPath );
		Check( legacy.LastError is null && legacy.Get( legacyId ).Directory is null && legacy.GetDirectory( legacyId ) == ConversationStore.DefaultDirectory,
			"History saved before chats chose folders loads, and a chat that never started works in the home folder." );
		Check( legacy.Get( startedId ).Directory == Environment.CurrentDirectory,
			"A chat outside projects that already ran Omp in the application's directory keeps that folder." );
		legacy.Flush();
		var migrated = new ConversationStore( legacyPath );
		Check( !File.ReadAllText( legacyPath ).Contains( "Kept from one file" ) && migrated.LastError is null
			&& migrated.GetMessages( legacyId ).Single().Text == "Kept from one file" && migrated.GetMessages( startedId ).Count == 0,
			"History kept in one file moves each chat's messages into a file of their own, and reads them back." );
	}

	/// <summary>Checks feature-owned panel reuse and account-boundary enforcement.</summary>
	private static void CheckPanels()
	{
		var app = new LoknApplication();
		var store = new ConversationStore( new ProjectStore() );
		var module = new ConversationsModule( store, ( context, host, id ) => new FixtureContent( host.Store.Get( id ) ) );
		Check( ProjectChecks.LoadProjects( app, store ) && app.Load( module ), "Conversation panels register without a backend dependency in a pure fixture." );
		var id = module.CreateConversation( app, module.Hosts.Local );
		var firstPanel = app.Panels.ActivePanelId;
		Check( module.OpenConversation( app, id ) == firstPanel && app.Panels.Panels.Count == 1, "Selecting an open conversation focuses its existing panel." );
		var other = ChatRef.Local( store.CreateConversation( SettingsProfile.Personal ).Id );
		app.Panels.RequestAttention( firstPanel!.Value, new( "Chat", "Finished and waiting for you" ) );
		Check( module.ComputeWaiting( app ).SetEquals( [id] ) && !module.ComputeWaiting( app ).Contains( other ), "A chat whose panel waits for the user is marked waiting in the sidebar, and only that chat." );
		app.Panels.ClearAttention( firstPanel.Value );
		Check( module.ComputeWaiting( app ).Count == 0, "A chat stops waiting once its panel's request is cleared." );
		app.Settings.SetActiveProfile( SettingsProfile.Work );
		try
		{
			module.OpenConversation( app, id );
			throw new Exception( "Opening a Personal conversation from Work must be rejected." );
		}
		catch ( InvalidOperationException )
		{
		}

		Check( store.Get( id.ConversationId ).Profile == SettingsProfile.Personal, "Profile switching never changes an existing conversation's account." );
		var mismatch = app.Panels.Open( ConversationsModule.ChatPanelType, input: new ConversationPanelInput( id.ConversationId, SettingsProfile.Work ) );
		Check( app.LastError?.Contains( "profile" ) == true, "A mismatched saved chat profile produces a contained panel error." );
		app.Panels.Close( mismatch );
		module.DeleteConversation( app, id );
		Check( app.Panels.Panels.Count == 1 && app.Panels.Sessions.Single().Content is ConversationPanel { IsEmpty: true, ConversationId: null },
			"Deleting a conversation leaves its panel open, showing Home." );
		app.Shutdown();
	}

	/// <summary>Checks the notification feed: its own full-window workspace, rows grouped by source and day, icons by source, and opening a chat's notification.</summary>
	private static void CheckNotifications()
	{
		var path = Path.Combine( Path.GetTempPath(), "lokn-notification-sessions-" + Guid.NewGuid().ToString( "N" ) + ".json" );
		var app = new LoknApplication();
		app.BindDispatcher( action =>
		{
			action();
			return true;
		} );
		app.Load( new WorkspacesModule( path ) );
		var store = new ConversationStore( new ProjectStore() );
		var module = new ConversationsModule( store, ( context, host, id ) => new FixtureContent( host.Store.Get( id ) ) );
		Check( ProjectChecks.LoadProjects( app, store ) && app.Load( module ) && app.Load( new NotificationsModule() ), "Conversations and Notifications load for the notification feed." );
		var chat = ChatRef.Local( store.CreateConversation( SettingsProfile.Personal, title: "Build fix" ).Id );
		var chatPanel = module.SelectConversation( app, chat );
		var fromChat = app.Notifications.Post( new( "Build fix", "Finished and waiting for you" ) { PanelId = chatPanel, Source = "conversations.chat" } );
		// A module ships its notification kinds: short updates with their own summaries, and no code for them in the feed.
		NotificationKind[] kinds =
		[
			new( "ci.passed", "\uE73E" ) { IsUpdate = true, Summarize = ( count, first ) => count == 1 ? first.Message : $"{count} checks passed" },
			new( "ci.review", "\uE73E" ) { IsUpdate = true, Summarize = ( count, _ ) => count == 1 ? "review requested" : $"{count} reviews requested" },
		];
		app.Notifications.Post( new( "CI", "Checks passed on #12" ) { Link = new( "https://example.com/o/r/pull/12" ), Source = "ci", Kind = "ci.passed" } );
		app.Notifications.Post( new( "CI", "Review requested on #13" ) { Link = new( "https://example.com/o/r/pull/13" ), Source = "ci", Kind = "ci.review" } );
		app.Notifications.Post( new( "Build fix", "The build passes again.\n\nI fixed the **missing reference** in `Lokn.csproj`." ) { PanelId = chatPanel, Source = "conversations.chat" } );

		NotificationsModule.OpenFeed( app );
		var feed = app.Panels.Panels.Single( panel => panel.TypeId == NotificationsModule.PanelType ).Id;
		Check( app.Panels.IsShown( feed ) && app.Panels.ActivePanelId == feed,
			"The feed opens as a page of its own, like Settings, and is the active panel." );

		var rows = NotificationsContent.ComputeRows( app.Notifications.Items, DateTimeOffset.Now, kinds );
		Check( rows.Select( row => row.Kind ).SequenceEqual( [NotificationRowKind.Day, NotificationRowKind.Message, NotificationRowKind.Activity, NotificationRowKind.Activity] )
			&& rows[0].Label == "Today" && rows[1].Label == "Build fix" && rows[2].Items.Count == 2 && rows[3].Label == "Build fix finished",
			"The feed lists newest first under each day: short updates from one source fold into one run, and a message worth reading stands alone." );
		Check( rows[2].Label == "Checks passed on #12, review requested"
			&& NotificationsContent.ComputeSummary( [.. Enumerable.Range( 0, 3 ).Select( index => app.Notifications.Post( new( "CI", $"CI succeeded on o/r main {index}" ) { Source = "ci", Kind = "ci.passed" } ) )], kinds ) == "3 checks passed",
			"A run's summary counts its updates by kind, worded by the module that posted them." );
		Check( NotificationsContent.IsShort( app.Notifications.Post( new( "CI", "A long line that would not fold on its own, " + new string( 'x', 120 ) ) { Source = "ci", Kind = "ci.passed" } ), kinds ),
			"A kind marked as an update folds even when its text is long." );
		Check( NotificationsContent.ComputeSource( fromChat, _ => null ) == NotificationSource.Agent
			&& NotificationsContent.ComputeSource( fromChat with { Source = "terminal.shell" }, _ => null ) == NotificationSource.Other
			&& NotificationsContent.ComputeSource( fromChat with { Source = null, PanelId = null }, _ => null ) == NotificationSource.Other,
			"Each source gets its built-in icon, and anything else the bell." );
		var tagged = app.Notifications.Post( new( "GitHub", "Checks failed on #14" ) { Source = "github", Tone = NotificationTone.Failure, Project = "o/r" } );
		var feedInput = new NotificationsContent.FeedViewInput( app.Notifications.Items, new HashSet<Guid> { fromChat.Id } );
		Check( NotificationsContent.ComputeVisible( feedInput, new( NotificationFilterKind.Project, "o/r" ), "", _ => null ) is [var inProject] && inProject.Id == tagged.Id
			&& NotificationsContent.ComputeVisible( feedInput, new( NotificationFilterKind.Source, "GitHub" ), "", _ => null ) is [var fromGitHub] && fromGitHub.Id == tagged.Id
			&& NotificationsContent.ComputeVisible( feedInput, new( NotificationFilterKind.Waiting ), "", _ => null ) is [var asked] && asked.Id == fromChat.Id
			&& NotificationsContent.ComputeVisible( feedInput, new( NotificationFilterKind.All ), "MISSING reference", _ => null ) is [{ Title: "Build fix" }],
			"The Notifications page filters by project, by source, by what waits for the user, and by search text in any case." );
		Check( app.Load( new FixtureModule( "kinds", builder => builder.Contribute( NotificationKind.Point, new NotificationKind( "terminal", "\uE756" ) ) ) )
			&& NotificationsModule.FindKind( app, fromChat with { Source = "terminal.shell" } )?.Glyph == "\uE756" && NotificationsModule.FindKind( app, fromChat ) is null
			&& app.Unload( "kinds" ) && NotificationsModule.FindKind( app, fromChat with { Source = "terminal.shell" } ) is null,
			"A module's kind applies to its kind or source's module only while that module is loaded." );

		NotificationsModule.Open( app, fromChat, feed );
		Check( app.Notifications.Items.Single( item => item.Id == fromChat.Id ).IsRead && app.Panels.IsShown( chatPanel ) && app.Panels.ActivePanelId == chatPanel,
			"Opening a chat's notification marks it read and shows that conversation, active; the feed is a page and stays where it was opened." );

		// From another task, a clicked notification goes back to the task its panel belongs to instead of pulling the panel in.
		app.Load( new FixtureModule( "scratch", builder => builder.Panel( "panel", "Scratch", _ => new Lokn.Tests.FixtureContent() ) ) );
		var scratch = app.Panels.Open( "scratch.panel", app.Panels.SplitActive( SplitAxis.Horizontal ) );
		var other = ChatRef.Local( store.CreateConversation( SettingsProfile.Personal, title: "Other" ).Id );
		var otherPanel = module.SelectConversation( app, other );
		var finished = app.Notifications.Post( new( "Build fix", "Finished and waiting for you" ) { PanelId = chatPanel, Source = "conversations.chat" } );
		app.OpenNotification( finished );
		Check( app.Panels.ActivePanelId == chatPanel && !app.Panels.IsShown( otherPanel ) && module.GetSelected( app ) == chat,
			"A clicked Windows notification for a chat in another task opens that task, not the current one." );
		module.SelectConversation( app, other );
		NotificationsModule.Open( app, app.Notifications.Post( new( "Scratch", "Done" ) { PanelId = scratch, Source = "scratch.panel" } ), feedPanelId: null );
		Check( app.Panels.ActivePanelId == scratch && app.Panels.IsShown( chatPanel ) && !app.Panels.IsShown( otherPanel ),
			"A notification for another kind of panel opens the task whose layout holds it." );

		// A chat the user keeps in another workspace of its task opens there, not beside what that task's current workspace shows.
		var workspaces = app.GetModule<WorkspacesModule>();
		var moved = ChatRef.Local( store.CreateConversation( SettingsProfile.Personal, title: "Moved" ).Id );
		var movedPanel = module.SelectConversation( app, moved );
		workspaces.Create( app );
		app.Panels.HideAll();
		var notes = app.Panels.Open( "scratch.panel" );
		module.SelectConversation( app, other );
		NotificationsModule.Open( app, app.Notifications.Post( new( "Moved", "Finished and waiting for you" ) { PanelId = movedPanel, Source = "conversations.chat" } ), feedPanelId: null );
		Check( app.Panels.Panes is [{ PanelId: var alone }] && alone == movedPanel && app.Panels.ActivePanelId == movedPanel && !app.Panels.IsShown( notes ),
			"A chat's notification travels to the workspace of its task that holds it, instead of pulling it into the one on screen." );

		// A chat of the other profile switches profile and opens its own task there, not the profile's last session and not the current layout.
		var workChat = ChatRef.Local( store.CreateConversation( SettingsProfile.Work, title: "Work fix" ).Id );
		app.Settings.SetActiveProfile( SettingsProfile.Work );
		var workPanel = module.SelectConversation( app, workChat );
		var workOther = ChatRef.Local( store.CreateConversation( SettingsProfile.Work, title: "Work other" ).Id );
		module.SelectConversation( app, workOther );
		app.Settings.SetActiveProfile( SettingsProfile.Personal );
		module.SelectConversation( app, other );
		NotificationsModule.Open( app, app.Notifications.Post( new( "Work fix", "Finished and waiting for you" ) { PanelId = workPanel, Source = "conversations.chat" } ), feedPanelId: null );
		Check( app.Settings.ActiveProfile == SettingsProfile.Work && app.Panels.Panes is [{ PanelId: var travelled }] && travelled == workPanel
			&& module.GetSelected( app ) == workChat && !app.Panels.IsShown( otherPanel ),
			"A chat's notification from the other profile switches to it and opens that chat's own task alone." );
		app.Shutdown();
		File.Delete( path );
	}

	/// <summary>Checks that each conversation opens in its own workspace, alone at first, and that each profile returns to its own.</summary>
	private static void CheckSessions()
	{
		var path = Path.Combine( Path.GetTempPath(), "lokn-conversation-sessions-" + Guid.NewGuid().ToString( "N" ) + ".json" );
		var app = new LoknApplication();
		app.BindDispatcher( action =>
		{
			action();
			return true;
		} );
		var workspaces = new WorkspacesModule( path );
		Check( app.Load( workspaces ), "The Workspaces module loads for conversation sessions." );
		var store = new ConversationStore( new ProjectStore() );
		var module = new ConversationsModule( store, ( context, host, id ) => new FixtureContent( host.Store.Get( id ) ) );
		Check( ProjectChecks.LoadProjects( app, store ) && app.Load( module ) && app.Load( new NotificationsModule() ), "Conversations and Notifications load beside Workspaces." );
		var first = ChatRef.Local( store.CreateConversation( SettingsProfile.Personal ).Id );
		var second = ChatRef.Local( store.CreateConversation( SettingsProfile.Personal ).Id );

		var firstPanel = module.SelectConversation( app, first );
		Check( app.Panels.Panes is [{ PanelId: var only }] && only == firstPanel, "A conversation's first opening shows just its chat." );
		app.Panels.SplitActive( SplitAxis.Horizontal );
		var secondPanel = module.SelectConversation( app, second );
		Check( app.Panels.Panes is [{ PanelId: var alone }] && alone == secondPanel && !app.Panels.IsShown( firstPanel ) && app.Panels.Panels.Any( panel => panel.Id == firstPanel ),
			"Another conversation opens alone in its own workspace; the first chat stays open, hidden." );
		Check( module.SelectConversation( app, first ) == firstPanel && app.Panels.Panes.Count == 2 && !app.Panels.IsShown( secondPanel ),
			"Returning to a conversation restores the layout it was left in." );
		Check( module.GetSelected( app ) == first, "The sidebar highlights the conversation whose workspace is open." );

		// A task without a chat is only its own layout: it opens empty, keeps the panels opened in it, and can gain a chat later.
		var project = store.ProjectStore.Create( SettingsProfile.Personal, "Repo", Path.GetTempPath() );
		var task = module.NewTask( app, projectId: project.Id );
		Check( store.Get( task.ConversationId ) is { HasChat: false, Title: "New task" } && workspaces.CurrentSession == ConversationsModule.ConversationSession( task )
			&& app.Panels.Panes is [{ PanelId: null }] && !app.Panels.IsShown( firstPanel ), "A new task without a chat opens as one empty pane in its own workspace." );
		var folder = app.Services.Get<IWorkingFolder>();
		Check( folder?.Current == project.Directory, "A project task's workspace works in its project's directory, so the file tree and terminals open there." );
		var worktree = Path.Combine( Path.GetTempPath(), "lokn-task-worktree" );
		store.SetWorktree( task.ConversationId, worktree );
		Check( folder?.Current == Path.GetFullPath( worktree ), "A task moved into a worktree makes that worktree the working folder." );
		store.SetWorktree( task.ConversationId, project.Directory );
		var feed = app.Panels.Open( NotificationsModule.PanelType );
		module.SelectConversation( app, first );
		Check( module.SelectConversation( app, task ) == Guid.Empty && app.Panels.Panes is [{ PanelId: var kept }] && kept == feed,
			"Returning to a task without a chat shows the panels left in it and opens no chat." );
		Check( module.StartChat( app, task ) == task, "Starting a chat in a task keeps the task's chat." );
		var taskChat = FindPanel( app, task );
		Check( store.Get( task.ConversationId ).HasChat && app.Panels.Panes.Count == 2 && app.Panels.IsShown( taskChat ) && app.Panels.IsShown( feed ),
			"Starting a chat in a task opens it beside the task's panels." );
		store.RenameConversation( task.ConversationId, "Release notes" );
		Check( store.Get( task.ConversationId ) is { Title: "Release notes", IsTitleChosen: true } && !store.SetAutomaticTitle( task.ConversationId, "Generated", isGenerated: true ),
			"Renaming a task keeps the name from generated titles." );
		// The + grid names a task after its starter; Chat's task is named as a new chat is, so its first prompt names it.
		var chatTask = module.NewTask( app, projectId: project.Id, name: "Chat" );
		app.Extensions.Get<TaskStarter>( TaskStarter.Point ).Single( starter => starter.Title == "Chat" ).Open( app );
		Check( store.Get( chatTask.ConversationId ) is { Title: "New chat", HasChat: true, IsTitleChosen: false } && workspaces.CurrentSession == ConversationsModule.ConversationSession( chatTask ),
			"A task started with the grid's Chat becomes a new chat in that task, named by its first prompt rather than after the starter." );

		// A chat started from an empty conversation panel inside a task's workspace belongs with that task, not to the chats outside projects.
		module.SelectConversation( app, task );
		var extra = module.StartConversation( app, module.Hosts.Local, "hi", module.ComputeSessionProject( app ), app.Panels.Open( ConversationsModule.ChatPanelType ) );
		Check( extra != task && store.Get( extra.ConversationId ).ProjectId == project.Id,
			"A second chat started inside a task's workspace is a new task in the same project, not one outside projects." );
		var bare = module.NewTask( app, projectId: project.Id );
		var bareHome = app.Panels.Open( ConversationsModule.ChatPanelType );
		var started = module.StartConversation( app, module.Hosts.Local, "hi", module.ComputeSessionProject( app ), bareHome );
		module.SelectConversation( app, first );
		module.SelectConversation( app, bare );
		Check( started == bare && store.Get( bare.ConversationId ).HasChat && module.GetSelected( app ) == bare && store.Conversations( SettingsProfile.Personal ).All( item => item.Id != started.ConversationId ),
			"A chat started inside a task without one becomes that task's chat, and clicking the task highlights it." );
		var tabs = app.Slots.Get( ShellSlots.Tabs ).Single( item => item.ModuleId == "conversations" ).Content;
		Check( tabs.Build() is Row { Children: [Row { Key: var currentGroup, Children: [Button { Key: "label" }, Button { Key: var currentTab }, .., Button { Key: "new" }] }, .., Button { Key: "new-chat" }] } strip
			&& currentGroup == ProjectsModule.SessionKey( project.Id ) && currentTab == bare.ConversationId.ToString()
			&& strip.Children.OfType<Row>().SingleOrDefault( group => group.Key == ConversationsModule.ChatsKey ) is { Children: var chatTabs }
			&& chatTabs.OfType<Button>().Any( tab => tab.Key == first.ConversationId.ToString() ),
			"The chat tabs group chats by project, the current chat first in its group and its group first, each group with its +, then the + that starts a chat outside projects." );
		module.SelectConversation( app, first );

		// Scratch shows the latest few chats outside projects, and the open one even when older, then View all; expanded, every chat, then Show less.
		var sidebar = app.Slots.Get( ShellSlots.Sidebar ).Single( item => item.ModuleId == "conversations" ).Content;
		for ( var index = 0; index < 6; index++ )
			store.CreateConversation( SettingsProfile.Personal );
		var loose = store.Conversations( SettingsProfile.Personal ).Count;
		// The profile's rows sit inside the profile slide's mounted input.
		static IEnumerable<Blob> Expand( Blob row ) => row.GetType().GetFields( System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public )
			.Select( field => field.GetValue( row ) ).OfType<ProfileSlideInput>().FirstOrDefault() is { Content: Container { Children: var inner } } ? inner : [row];
		List<Blob> ReadScratch() => sidebar.Build() is Container { Children: [Container { Key: "content", Children: var rows }] }
			? [.. rows.SelectMany( Expand ).SkipWhile( row => row is not Text { Content: ConversationsModule.ChatsTitle } ).Skip( 1 )]
			: [];
		var folded = ReadScratch();
		Check( loose == 8 && folded.Count( row => row is Container ) == 6 && folded.Any( row => row is Container { Key: var key } && key == first.ConversationId.ToString() )
			&& folded[^1] is Button { Key: "scratch-toggle" } viewAll && viewAll.AccessibilityName == $"View all {loose} {ConversationsModule.ChatsTitle} chats",
			"Scratch shows the 5 latest chats outside projects plus the older open one, then View all with the full count." );
		( (Button)folded[^1] ).OnClick!();
		var expanded = ReadScratch();
		Check( expanded.Count( row => row is Container ) == loose && expanded[^1] is Button { Key: "scratch-toggle" } showLess && showLess.AccessibilityName!.StartsWith( "Show fewer", StringComparison.Ordinal ),
			"View all lists every chat outside projects, then Show less." );

		app.Settings.SetActiveProfile( SettingsProfile.Work );
		var work = ChatRef.Local( store.CreateConversation( SettingsProfile.Work ).Id );
		var workPanel = module.SelectConversation( app, work );
		app.Settings.SetActiveProfile( SettingsProfile.Personal );
		Check( app.Panels.IsShown( firstPanel ) && !app.Panels.IsShown( workPanel ) && module.GetSelected( app ) == first,
			"Switching to Personal returns to Personal's conversation and its layout." );
		app.Settings.SetActiveProfile( SettingsProfile.Work );
		Check( app.Panels.IsShown( workPanel ) && !app.Panels.IsShown( firstPanel ) && module.GetSelected( app ) == work,
			"Switching to Work returns to Work's own conversation." );

		var saved = new WorkspaceStore( path );
		saved.Load();
		Check( saved.SessionFor( SettingsProfile.Work ) == ConversationsModule.ConversationSession( work )
			&& saved.SessionFor( SettingsProfile.Personal ) == ConversationsModule.ConversationSession( first ),
			"Each profile's current workspace is saved." );

		app.Settings.SetActiveProfile( SettingsProfile.Personal );
		module.DeleteConversation( app, first );
		Check( workspaces.CurrentSession == ConversationsModule.HomeSession && workspaces.Sessions.All( session => session.Key != ConversationsModule.ConversationSession( first ) )
			&& module.GetSelected( app ) != first, "Deleting the open conversation goes Home, forgets its workspace, and clears the selection." );
		app.Shutdown();
		File.Delete( path );
	}

	/// <summary>Checks which chats tidying removes and renames, that it removes only what the user was shown, and how the title question is written and read.</summary>
	private static void CheckTidy( string directory )
	{
		var path = Path.Combine( directory, "tidy.json" );
		var projectId = Guid.NewGuid();
		var otherId = Guid.NewGuid();
		var (old, pinned, named, recent, task) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
		var now = DateTimeOffset.UtcNow;
		var stale = now.AddDays( -30 ).ToString( "O" );
		File.WriteAllText( path, $$$"""
			{"Version":2,"Projects":[
				{"Id":"{{{projectId}}}","Profile":"Personal","Name":"Repo","Directory":"{{{directory.Replace( "\\", "\\\\" )}}}"},
				{"Id":"{{{otherId}}}","Profile":"Personal","Name":"Other","Directory":"{{{directory.Replace( "\\", "\\\\" )}}}"}],
			"Conversations":[
				{"Id":"{{{old}}}","Profile":"Personal","ProjectId":"{{{projectId}}}","Title":"Old topic","UpdatedAt":"{{{stale}}}","CreatedAt":"{{{stale}}}"},
				{"Id":"{{{pinned}}}","Profile":"Personal","ProjectId":"{{{projectId}}}","Title":"Pinned topic","UpdatedAt":"{{{stale}}}","CreatedAt":"{{{stale}}}","IsPinned":true},
				{"Id":"{{{named}}}","Profile":"Personal","ProjectId":"{{{projectId}}}","Title":"My name","UpdatedAt":"{{{stale}}}","CreatedAt":"{{{stale}}}","IsTitleChosen":true},
				{"Id":"{{{recent}}}","Profile":"Personal","ProjectId":"{{{projectId}}}","Title":"fix the login page","UpdatedAt":"{{{now:O}}}","CreatedAt":"{{{stale}}}"},
				{"Id":"{{{task}}}","Profile":"Personal","ProjectId":"{{{projectId}}}","Title":"New task","UpdatedAt":"{{{stale}}}","CreatedAt":"{{{stale}}}","HasChat":false}]}
			""" );
		var messages = Path.Combine( directory, "tidy", recent.ToString( "N" ) + ".json" );
		Directory.CreateDirectory( Path.GetDirectoryName( messages )! );
		File.WriteAllText( messages, $$$"""
			{"Version":2,"ConversationId":"{{{recent}}}","Messages":[
				{"Id":"{{{Guid.NewGuid()}}}","Role":"user","Text":"fix the login page","CreatedAt":"{{{stale}}}"},
				{"Id":"{{{Guid.NewGuid()}}}","Role":"assistant","Text":"Fixed.","CreatedAt":"{{{stale}}}","RichContent":{"type":"text"}},
				{"Id":"{{{Guid.NewGuid()}}}","Role":"user","Text":"now add a\ndark mode toggle","CreatedAt":"{{{now:O}}}"}]}
			""" );

		// The fixture is history from before projects had a file of their own: its projects move to the projects file as it loads.
		var store = new ConversationStore( new ProjectStore( Path.Combine( directory, "tidy-projects.json" ), path ), path );
		var app = new LoknApplication();
		var module = new ConversationsModule( store, ( context, host, id ) => new FixtureContent( host.Store.Get( id ) ) );
		Check( ProjectChecks.LoadProjects( app, store ) && app.Load( module ) && store.LastError is null && store.ProjectStore.Find( projectId )?.Name == "Repo",
			"The tidy fixture loads, its projects imported." );
		var empty = store.CreateConversation( SettingsProfile.Personal, projectId );
		var open = module.CreateConversation( app, module.Hosts.Local, projectId ).ConversationId;
		var plan = module.ComputeTidy( app, projectId, now );
		Check( plan.Stale.Select( item => item.Id ).Order().SequenceEqual( new[] { old, empty.Id }.Order() ),
			"Tidying removes chats never started or quiet for two weeks, never pinned or named ones, tasks without a chat, or a chat open in a panel." );
		Check( plan.Renamed.Select( item => item.Id ).Order().SequenceEqual( new[] { pinned, recent }.Order() ),
			"Tidying renames every started chat it keeps except those the user named." );

		var prompts = store.ReadPromptsAsync( [recent, pinned], CancellationToken.None ).GetAwaiter().GetResult();
		Check( prompts[recent] is ["fix the login page", "now add a\ndark mode toggle"] && prompts[pinned].Count == 0 && store.Find( recent ) is not null,
			"The title question reads a chat's prompts from its file, without its replies." );
		var material = ConversationsModule.BuildTidyMaterial( store.ProjectStore.Get( projectId ), [store.Get( recent )], prompts );
		Check( material.Contains( "1. Current title: fix the login page" ) && material.Contains( "- now add a dark mode toggle" ),
			"The title question numbers each chat and keeps each prompt on one line." );
		var titles = ConversationsModule.ReadTidyTitles( "Here are the titles:\n1: Dark mode toggle.\n**2.** \"Release notes\"\n2: Duplicate\n7: Out of range", 2 );
		Check( titles.Count == 2 && titles[1] == "Dark mode toggle" && titles[2] == "Release notes",
			"The answer's numbered lines become cleaned titles; other lines, repeats and unknown numbers are ignored." );

		var otherEmpty = store.CreateConversation( SettingsProfile.Personal, otherId );
		var otherPlan = module.ComputeTidy( app, otherId, now );
		var later = store.CreateConversation( SettingsProfile.Personal, otherId );
		module.Tidy( app, otherPlan );
		Check( store.Find( otherEmpty.Id ) is null && store.Find( later.Id ) is not null && !module.IsTidying( otherId ),
			"Tidying removes only the stale chats the user was shown." );
		Check( store.Find( open ) is not null && store.Find( old ) is not null, "Tidying one project leaves the others alone." );
		app.Shutdown();
	}

	/// <summary>Checks which chats are archived: those untouched past the age, and the oldest beyond the kept count, never pinned, open, drafted or
	/// chat-less ones; that an archived chat's messages leave memory and read back from its file; that the flag survives a restart; and that opening
	/// an archived chat brings it back.</summary>
	private static void CheckArchive( string directory )
	{
		var path = Path.Combine( directory, "archive.json" );
		var projectId = Guid.NewGuid();
		var now = DateTimeOffset.UtcNow;
		var (old, pinned, drafted) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
		var fresh = Enumerable.Range( 1, 4 ).Select( _ => Guid.NewGuid() ).ToArray();
		string Entry( Guid id, int daysAgo, string extra = "" ) =>
			$$"""{"Id":"{{id}}","Profile":"Personal","ProjectId":"{{projectId}}","Title":"Chat {{daysAgo}}","UpdatedAt":"{{now.AddDays( -daysAgo ):O}}","CreatedAt":"{{now.AddDays( -daysAgo ):O}}"{{extra}}}""";
		File.WriteAllText( path, $$$"""
			{"Version":2,"Projects":[{"Id":"{{{projectId}}}","Profile":"Personal","Name":"Repo","Directory":"{{{directory.Replace( "\\", "\\\\" )}}}"}],
			"Conversations":[{{{string.Join( ",", [Entry( old, 30 ), Entry( pinned, 40, ",\"IsPinned\":true" ), Entry( drafted, 40, ",\"Draft\":\"half a thought\"" ),
				.. fresh.Select( ( id, index ) => Entry( id, index + 1 ) )] )}}}]}
			""" );
		var messages = Path.Combine( directory, "archive", old.ToString( "N" ) + ".json" );
		Directory.CreateDirectory( Path.GetDirectoryName( messages )! );
		File.WriteAllText( messages, $$$"""
			{"Version":2,"ConversationId":"{{{old}}}","Messages":[{"Id":"{{{Guid.NewGuid()}}}","Role":"user","Text":"prompt 30","CreatedAt":"{{{now.AddDays( -30 ):O}}}"}]}
			""" );
		var store = new ConversationStore( new ProjectStore( Path.Combine( directory, "archive-projects.json" ), path ), path );
		var app = new LoknApplication();
		var module = new ConversationsModule( store, ( context, host, id ) => new FixtureContent( host.Store.Get( id ) ) );
		Check( ProjectChecks.LoadProjects( app, store ) && app.Load( module ) && store.LastError is null && store.GetMessages( old ) is [_], "The archive fixture loads." );
		app.Settings.Set( ConversationsModule.ArchiveAfterDays, SettingsScope.Global, 14 );
		app.Settings.Set( ConversationsModule.KeepChats, SettingsScope.Global, 3 );
		var archive = module.ComputeArchive( app, now ).Select( item => item.Id ).ToHashSet();
		Check( archive.SetEquals( [old, fresh[3]] ),
			"A chat untouched past the age is archived, and so is the oldest beyond the kept count; pinned and drafted chats stay and do not count." );

		module.ArchiveIdle( app );
		store.Flush();
		Check( store.Get( old ).IsArchived && !store.Get( pinned ).IsArchived && !store.Get( drafted ).IsArchived && !store.Get( fresh[0] ).IsArchived,
			"Archiving marks only the chats it listed." );
		Check( store.GetMessages( old ) is [{ Text: "prompt 30" }], "An archived chat's messages read back from its file once needed." );
		Check( new ConversationStore( path ).Find( old ) is { IsArchived: true }, "The archived flag survives a restart." );
		Check( module.ComputeArchive( app, now ).Count == 0, "Archived chats are not archived again, and the rest stay within the limits." );
		app.Settings.Set( ConversationsModule.ArchiveAfterDays, SettingsScope.Global, 0 );
		app.Settings.Set( ConversationsModule.KeepChats, SettingsScope.Global, 0 );
		Check( module.ComputeArchive( app, now.AddDays( 400 ) ).Count == 0, "With both limits at 0 nothing is archived." );

		module.SelectConversation( app, module.Hosts.Local.Ref( old ) );
		Check( !store.Get( old ).IsArchived, "Opening an archived chat brings it back to its list." );
		app.Shutdown();
	}

	/// <summary>Checks that a chat no panel shows reaches the user when its agent asks or finishes, and that a delete by store operation stops its session and empties its panel.</summary>
	private static void CheckBackgroundChats()
	{
		var app = new LoknApplication();
		app.BindDispatcher( action =>
		{
			action();
			return true;
		} );
		var store = new ConversationStore( new ProjectStore() );
		var module = new ConversationsModule( store, ( context, host, id ) => new FixtureContent( host.Store.Get( id ) ) );
		Check( app.Load( new OmpModule() ) && ProjectChecks.LoadProjects( app, store ) && app.Load( module ), "Omp, Projects and Conversations load for chats no panel shows." );
		static JsonElement Frame( string json ) => JsonDocument.Parse( json ).RootElement.Clone();

		var asking = store.CreateConversation( SettingsProfile.Personal, title: "Asking" ).Id;
		var session = module.Hosts.Local.Sessions.GetOrCreate( asking );
		session.UpdateFrame( Frame( """{"type":"agent_start"}""" ) );
		session.UpdateFrame( Frame( """{"type":"extension_ui_request","id":"q1","method":"confirm","title":"Proceed?"}""" ) );
		var link = LoknLinks.Create( "conversations", "open", asking.ToString( "N" ) ).AbsoluteUri;
		var asked = app.Notifications.Items.FirstOrDefault( item => item.Url == link );
		Check( asked is { Title: "Asking", Message: "Proceed?", PanelId: null, IsRead: false, Source: ConversationsModule.ChatPanelType },
			"A question from a chat no panel shows posts a notification whose link opens the chat." );
		Check( module.ComputeWaiting( app ).SetEquals( [ChatRef.Local( asking )] ), "A chat no panel shows is marked waiting in the sidebar when its agent asks." );
		Check( module.ComputePrompts( app ) is [{ PanelId: null } prompt] && prompt.Key == asking.ToString( "N" ) && prompt.Title == "Asking" && prompt.Build( "feed" ) is null,
			"The notification feed lists the question of a chat no panel shows, as a link to the chat." );

		app.OpenLink( new Uri( link ) );
		var shown = app.Panels.Sessions.Select( item => item.Content ).OfType<ConversationPanel>().SingleOrDefault( panel => panel.ConversationId == asking );
		Check( shown is not null && module.ComputeWaiting( app ).Count == 0 && app.Notifications.Items.Single( item => item.Id == asked!.Id ).IsRead,
			"The notification's link opens the chat, which then stops waiting and marks its notification read." );

		var finishing = store.CreateConversation( SettingsProfile.Personal, title: "Finishing" ).Id;
		var background = module.Hosts.Local.Sessions.GetOrCreate( finishing );
		background.UpdateFrame( Frame( """{"type":"agent_start"}""" ) );
		background.UpdateFrame( Frame( """{"type":"agent_end"}""" ) );
		var finished = app.Notifications.Items.FirstOrDefault( item => item.Title == "Finishing" );
		Check( finished is { Message: "Finished and waiting for you", PanelId: null } && module.ComputeWaiting( app ).Contains( ChatRef.Local( finishing ) ),
			"A turn that finishes in a chat no panel shows posts a notification and marks the chat waiting." );
		background.UpdateFrame( Frame( """{"type":"agent_start"}""" ) );
		Check( !module.ComputeWaiting( app ).Contains( ChatRef.Local( finishing ) ) && app.Notifications.Items.Single( item => item.Id == finished!.Id ).IsRead,
			"A chat whose agent works on alone stops waiting." );

		store.Apply( StoreOp.Build( StoreOp.Conversations, nameof( ConversationStore.DeleteConversation ), new { id = asking } ) );
		Check( module.Hosts.Local.Sessions.Find( asking ) is null && !store.IsWorking( asking ) && shown!.IsEmpty && module.ComputePrompts( app ).Count == 0,
			"A conversation deleted by a store operation, as another device deletes it, stops its session and its panel shows Home." );
		store.Apply( StoreOp.Build( StoreOp.Conversations, nameof( ConversationStore.DeleteConversation ), new { id = finishing } ) );
		Check( module.Hosts.Local.Sessions.Find( finishing ) is null && !store.IsWorking( finishing ), "Deleting a working chat no panel shows stops its session too." );
		app.Shutdown();
	}

	/// <summary>Checks that chats start over a mirror whose host has not answered yet: a task gains its chat and a started chat holds its prompt at once.</summary>
	private static void CheckMirrorStarts()
	{
		var app = new LoknApplication();
		var store = new ConversationStore( new ProjectStore() );
		var sent = new List<StoreOp>();
		store.Forward = op =>
		{
			sent.Add( op );
			return true;
		};
		var module = new ConversationsModule( store, ( context, host, id ) => new FixtureContent( host.Store.Get( id ) ) );
		Check( ProjectChecks.LoadProjects( app, store ) && app.Load( module ), "Conversations loads over a mirror store." );
		var task = module.NewTask( app );
		Check( module.StartChat( app, task ) == task && store.Get( task.ConversationId ).HasChat, "A task over a mirror gains its chat at once, and the chat opens." );
		var started = module.StartConversation( app, module.Hosts.Local, "Typed on Home" );
		Check( store.Get( started.ConversationId ).Draft == "Typed on Home" && sent.Any( op => op.Op == nameof( ConversationStore.SaveDraft ) ),
			"A chat started over a mirror holds its prompt at once, and the host is told." );
		app.Shutdown();
	}

	/// <summary>Gets the open conversation panel that shows a chat.</summary>
	internal static Guid FindPanel( LoknApplication app, ChatRef chat ) =>
		app.Panels.Sessions.First( session => session.Content is ConversationPanel { Chat: { } shown } && shown == chat ).Info.Id;

	/// <summary>Provides minimal conversation content to isolate history and panel ownership checks.</summary>
	private sealed class FixtureContent( ConversationInfo conversation ) : PanelContent
	{
		/// <summary>Builds a fresh empty declaration for the fixture.</summary>
		public override Blob Build() => new Container();

		/// <summary>Captures the fixture's account-pinned conversation identity.</summary>
		public override JsonElement? CaptureState() => JsonSerializer.SerializeToElement( new ConversationPanelInput( conversation.Id, conversation.Profile ) );
	}

	/// <summary>Throws when a focused history check fails.</summary>
	private static void Check( bool condition, string message )
	{
		if ( !condition )
			throw new InvalidOperationException( message );
	}
}
