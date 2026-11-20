using System.Text.Json;
using Lokn.Core;
using Lokn.Modules.Browser;
using Lokn.Modules.Chat;
using Lokn.Modules.Conversations;
using Lokn.Modules.Files;
using Lokn.Modules.Git;
using Lokn.Modules.Markdown;
using Lokn.Modules.Notifications;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;
using Lokn.Modules.Terminal;
using Lokn.Modules.Workspaces;
using Lokn.Modules.Worktrees;

namespace Lokn.Tests;

/// <summary>Writes a data folder with this version's real stores, and loads one back headlessly, for upgrade and downgrade fixtures.</summary>
/// <remarks>
/// The modules are composed the way <c>Program</c> and the module host compose them, with every per-user file moved into the given folder,
/// so the files are exactly what this version writes and reads. No backend, terminal shell or web view ever starts.
/// </remarks>
internal static class FixtureDriver
{
	/// <summary>Holds a headless application over one data folder and the modules the driver uses.</summary>
	private sealed record Host(
		LoknApplication App,
		List<Action> Queued,
		ConversationsModule Conversations,
		ProjectsModule Projects,
		WorkspacesModule Workspaces,
		GitModule Git,
		ConversationStore Store );

	/// <summary>Creates a fixture data folder: projects, chats with messages, a task, settings, sessions, saved workspaces and a layout.</summary>
	internal static int MakeFixture( string folder )
	{
		var data = Path.GetFullPath( folder );
		EnsureNotUserData( data );
		if ( Directory.Exists( data ) && Directory.EnumerateFileSystemEntries( data ).Any() )
			throw new InvalidOperationException( $"{data} is not empty." );

		LoknPaths.UseDataRoot( data );
		var work = data + "-work";
		var demoFolder = Path.Combine( work, "demo-app" );
		var notesFolder = Path.Combine( work, "notes" );
		Directory.CreateDirectory( demoFolder );
		Directory.CreateDirectory( notesFolder );

		var host = Start( data );
		var app = host.App;
		var store = host.Store;
		Pump( host );

		// Settings › Modules: the user turned one module off.
		new ModuleStates( Path.Combine( data, "modules.json" ), isOffByDefault: false ).Set( "github", isOn: false );

		// Settings: two global values, then one per profile.
		Require( app.Settings.Set( ThemeModule.Selected, SettingsScope.Global, "dark-catppuccin" ), "theme.name" );
		Require( app.Settings.Set( TerminalModule.Shell, SettingsScope.Global, "pwsh -NoLogo" ), "terminal.shell" );
		Require( app.Settings.Set( OmpModule.Model, SettingsScope.Personal, "anthropic/claude-sonnet-4-5" ), "omp.model" );
		Require( app.Settings.Set( ShellSettings.ProfileIcon, SettingsScope.Work, "W" ), "shell.profile-icon" );

		var demo = store.ProjectStore.Create( SettingsProfile.Personal, "Demo app", demoFolder );
		var notes = store.ProjectStore.Create( SettingsProfile.Personal, "Notes", notesFolder );
		var start = DateTimeOffset.Parse( "2026-11-10T09:00:00Z" ).ToUnixTimeMilliseconds();

		// A chat outside projects.
		var loose = host.Conversations.NewConversation( app );
		Pump( host );
		Feed( host, loose, start,
			User( "What is a good way to structure weekly meeting notes?" ),
			Assistant( "Keep three headings: **Decisions**, **Action items** (owner and date), and **Open questions**. Start each week with last week's open questions." ) );

		// A task without a chat in Notes, holding a terminal.
		var task = host.Conversations.NewTask( app, notes.Id, "Weekly review" );
		Pump( host );
		app.Panels.Open( "terminal.shell" );
		Pump( host );

		// A chat in Notes, then the Notes project's own session.
		var outline = host.Conversations.NewConversation( app, notes.Id );
		Pump( host );
		store.SaveBackendSession( outline, Path.Combine( data, "sessions", nameof( SettingsProfile.Personal ), outline.ToString( "N" ) ) );
		Feed( host, outline, start + 60_000,
			User( "Draft an outline for the release notes." ),
			Assistant( new { type = "thinking", thinking = "The notes should group changes by area." },
				new { type = "text", text = "## Release notes\n\n1. Highlights\n2. Fixes\n3. Known issues" } ),
			User( "Add a section for upgrades." ),
			Assistant( new { type = "text", text = "Added **4. Upgrading** after Known issues." } ) );
		host.Conversations.OpenProject( app, notes );
		Pump( host );

		// The main chat in Demo app, with a tool run.
		var login = host.Conversations.NewConversation( app, demo.Id );
		Pump( host );
		store.SaveBackendSession( login, Path.Combine( data, "sessions", nameof( SettingsProfile.Personal ), login.ToString( "N" ) ) );
		Feed( host, login, start + 120_000,
			User( "Fix the login form validation\nThe email field accepts blanks." ),
			Assistant( new { type = "text", text = "I'll check the form first." },
				new { type = "toolCall", id = "call-1", name = "bash", arguments = new { command = "rg -n \"email\" src/login" } } ),
			ToolResult( "call-1", "bash", "src/login/form.ts:12:  email: string;" ),
			Assistant( new { type = "text", text = "The email field had no `required` rule. I added it and a test:\n\n```ts\nemail: z.string().min( 1 )\n```" } ) );
		var chatPanel = app.Panels.ActivePanelId ?? throw new InvalidOperationException( "The Demo app chat is not on screen." );

		// The task's first workspace: the chat beside the Git panel; then a second workspace with a browser and a terminal added.
		host.Git.OpenPanel( app, chatPanel, demoFolder );
		Pump( host );
		var firstWorkspace = host.Workspaces.CurrentTask;
		var session = host.Workspaces.CurrentSession;
		host.Workspaces.RenameTask( session, firstWorkspace, "Code" );
		var review = host.Workspaces.Create( app );
		host.Workspaces.RenameTask( session, review, "Review" );
		var browser = app.Panels.OpenBeside( "browser.page", chatPanel, new BrowserRequest( "https://example.com", FocusAddress: false ) );
		Pump( host );
		var page = app.Panels.FindContent<BrowserContent>() ?? throw new InvalidOperationException( "The browser panel did not open." );
		page.Content.OpenTab( "https://example.com/docs" );
		app.Panels.Activate( browser );
		app.Panels.SplitActive( SplitAxis.Vertical );
		app.Panels.Open( "terminal.shell" );
		app.Panels.Activate( chatPanel );
		Pump( host );

		// A finished chat the user was not looking at, as the Notifications module records it.
		var outlinePanel = app.Panels.Panels.FirstOrDefault( panel => panel.TypeId == ConversationsModule.ChatPanelType && host.Conversations.FindConversation( panel.Id )?.Id == outline );
		app.Notifications.Post( new( store.Get( outline ).Title, "Finished and waiting for you" ) { PanelId = outlinePanel?.Id, Source = ConversationsModule.ChatPanelType } );

		var error = app.LastError;
		Console.WriteLine( $"data      {data}" );
		Console.WriteLine( $"work      {work}" );
		Console.WriteLine( $"projects  demo-app={demo.Id} notes={notes.Id}" );
		Console.WriteLine( $"chats     login={login} outline={outline} loose={loose} task={task}" );
		Console.WriteLine( $"workspace session={session} code={firstWorkspace} review={review}" );
		Console.WriteLine( $"layout    {string.Join( ", ", ShownTypes( app ) )}" );
		Stop( host );
		Console.WriteLine( error is null && host.App.LastError is null ? "FIXTURE OK" : $"FIXTURE ERROR {error ?? host.App.LastError}" );
		return error is null && host.App.LastError is null ? 0 : 1;
	}

	/// <summary>Loads a data folder the way this version starts on it, and prints what it read.</summary>
	internal static int LoadData( string folder )
	{
		var data = Path.GetFullPath( folder );
		EnsureNotUserData( data );
		if ( !Directory.Exists( data ) )
			throw new DirectoryNotFoundException( $"{data} does not exist." );

		LoknPaths.UseDataRoot( data );
		var problems = new List<string>();
		var host = Start( data );
		var app = host.App;
		var store = host.Store;
		Console.WriteLine( $"data {data}" );
		Console.WriteLine( $"modules.json github on: {new ModuleStates( Path.Combine( data, "modules.json" ), isOffByDefault: false ).IsOn( "github" )}" );

		// The panels' saved state as the file holds it; a restored panel may report another, such as a terminal whose folder is missing.
		// An unreadable file is left to the restore below, which reports it.
		var saved = new Dictionary<Guid, string>();
		try
		{
			using var document = JsonDocument.Parse( File.ReadAllText( Path.Combine( data, "workspace.json" ) ) );
			foreach ( var panel in document.RootElement.GetProperty( "Panels" ).EnumerateArray() )
				saved[panel.GetProperty( "Id" ).GetGuid()] = panel.TryGetProperty( "State", out var state ) ? state.GetRawText() : "null";
		}
		catch ( Exception error ) when ( error is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException )
		{
		}

		var restored = app.RestoreWorkspace();
		Console.WriteLine( $"layout restored: {restored}" );
		if ( !restored )
			problems.Add( "workspace.json was not restored" );
		foreach ( var session in app.Panels.Sessions.Where( session => app.Panels.IsShown( session.Info.Id ) ) )
			Console.WriteLine( $"  panel {session.Info.TypeId} saved {saved.GetValueOrDefault( session.Info.Id, "?" )}" );
		app.MarkStarted();
		Pump( host );
		Console.WriteLine( $"after start, session {host.Workspaces.CurrentSession}: {string.Join( ", ", ShownTypes( app ) )}" );

		Console.WriteLine( $"settings active={app.Settings.ActiveProfile} theme.name={app.Settings.Get( ThemeModule.Selected, SettingsScope.Global )} terminal.shell={app.Settings.Get( TerminalModule.Shell, SettingsScope.Global )}"
			+ $" omp.model(Personal)={app.Settings.Get( OmpModule.Model, SettingsScope.Personal )} shell.profile-icon(Work)={app.Settings.Get( ShellSettings.ProfileIcon, SettingsScope.Work )}" );
		foreach ( var profile in Enum.GetValues<SettingsProfile>() )
		{
			var projects = store.ProjectStore.Projects( profile );
			var conversations = store.Conversations( profile ).Concat( projects.SelectMany( project => store.Conversations( profile, project.Id ) ) ).ToArray();
			Console.WriteLine( $"{profile}: {projects.Count} projects, {conversations.Length} chats and tasks" );
			foreach ( var project in projects )
				Console.WriteLine( $"  project {project.Id} \"{project.Name}\" {project.Directory}" );
			foreach ( var conversation in conversations )
			{
				var messages = store.GetMessages( conversation.Id );
				var project = conversation.ProjectId is { } id ? store.ProjectStore.Find( id )?.Name : null;
				Console.WriteLine( $"  {(conversation.HasChat ? "chat" : "task")} {conversation.Id} \"{conversation.Title}\" project={project ?? "-"} messages={messages.Count}"
					+ $" [{string.Join( ",", messages.Select( message => message.Role ) )}] rich={messages.Count( message => message.RichContent is not null )}" );
			}
		}
		if ( store.LastError is { } historyError )
			problems.Add( $"conversations: {historyError}" );
		if ( store.ProjectStore.LastError is { } projectError )
			problems.Add( $"projects: {projectError}" );

		var sessions = host.Workspaces.Sessions;
		Console.WriteLine( $"workspaces: {sessions.Count} sessions" );
		foreach ( var session in sessions )
		{
			Console.WriteLine( $"  session {session.Key} \"{session.Title}\"" );
			foreach ( var task in host.Workspaces.Tasks( session.Key ) )
				Console.WriteLine( $"    workspace {task.Id} \"{task.Name}\" [{string.Join( ", ", task.PanelTypes )}]" );
		}
		if ( host.Workspaces.Store.LastError is { } workspaceError )
			problems.Add( $"workspaces: {workspaceError}" );

		Console.WriteLine( $"notifications: {app.Notifications.Items.Count}" );
		if ( app.Notifications.LoadError is { } notificationError )
			problems.Add( $"notifications: {notificationError}" );
		if ( app.Settings.LastError is { } settingsError )
			problems.Add( $"settings: {settingsError}" );
		if ( app.LastError is { } appError )
			problems.Add( $"app: {appError}" );

		Stop( host );
		if ( host.App.LastError is { } stopError && !problems.Contains( $"app: {stopError}" ) )
			problems.Add( $"app after shutdown: {stopError}" );
		foreach ( var problem in problems )
			Console.WriteLine( $"PROBLEM {problem}" );
		Console.WriteLine( problems.Count == 0 ? "LOAD OK" : "LOAD FAILED" );
		return problems.Count == 0 ? 0 : 1;
	}

	/// <summary>Refuses the per-user data folder, which belongs to the user's own Loom.</summary>
	private static void EnsureNotUserData( string data )
	{
		var user = Path.GetFullPath( LoknPaths.DefaultDataRoot ).TrimEnd( Path.DirectorySeparatorChar ) + Path.DirectorySeparatorChar;
		if ( (data.TrimEnd( Path.DirectorySeparatorChar ) + Path.DirectorySeparatorChar).StartsWith( user, StringComparison.OrdinalIgnoreCase ) )
			throw new InvalidOperationException( $"{data} is the user's own data folder." );
	}

	/// <summary>Composes the application and its modules over one data folder, as <c>Program</c> and the module host do.</summary>
	private static Host Start( string data )
	{
		var app = new LoknApplication( Path.Combine( data, "settings.json" ), Path.Combine( data, "workspace.json" ), Path.Combine( data, "notifications.json" ) );
		var queued = new List<Action>();
		app.BindDispatcher( action =>
		{
			lock ( queued )
				queued.Add( action );
			return true;
		} );
		app.Settings.MoveSaved( "workspace.", "shell." );
		Require( app.Load( new ThemeModule( Path.Combine( data, "theme.json" ) ) ), "Theme", app );

		var projects = new ProjectsModule( Path.Combine( data, "projects.json" ), Path.Combine( data, "conversations.json" ) );
		var conversations = new ConversationsModule( new ConversationStore( Path.Combine( data, "conversations.json" ) ),
			( context, history, id ) => new ChatContent( context, history, id, context.App.GetModule<OmpModule>().Service ) )
		{
			Dependencies = ["omp", "workspaces", "projects", "markdown"],
		};
		var workspaces = new WorkspacesModule( Path.Combine( data, "workspaces.json" ) );
		var git = new GitModule();
		ILoknModule[] modules =
		[
			new MarkdownModule(),
			new OmpModule(),
			new NotificationsModule(),
			git,
			new FilesModule(),
			workspaces,
			projects,
			conversations,
			new WorktreesModule( conversations.Store, Path.Combine( data, "worktrees.json" ) ),
			new TerminalModule(),
			new BrowserModule(),
			new ShellModule(),
		];
		foreach ( var module in modules )
			Require( app.Load( module ), module.Id, app );

		return new( app, queued, conversations, projects, workspaces, git, conversations.Store );
	}

	/// <summary>Shuts the application down, which saves the layout and flushes history, and runs what it posted.</summary>
	private static void Stop( Host host )
	{
		Pump( host );
		host.App.Shutdown();
		Pump( host );
	}

	/// <summary>Runs posted UI work until none is left.</summary>
	private static void Pump( Host host )
	{
		for ( var round = 0; round < 50; round++ )
		{
			Action[] actions;
			lock ( host.Queued )
			{
				actions = [.. host.Queued];
				host.Queued.Clear();
			}
			if ( actions.Length == 0 )
				return;
			foreach ( var action in actions )
				action();
		}
	}

	/// <summary>Streams messages into a chat as Omp's RPC frames, through the chat panel that records them.</summary>
	private static void Feed( Host host, Guid conversationId, long timestamp, params JsonElement[] messages )
	{
		var panel = host.App.Panels.Sessions.Select( session => session.Content ).OfType<ConversationPanel>().Single( item => item.ConversationId == conversationId );
		var chat = panel.Chat ?? throw new InvalidOperationException( "The chat panel shows no chat." );
		foreach ( var message in messages )
		{
			var node = System.Text.Json.Nodes.JsonNode.Parse( message.GetRawText() )!.AsObject();
			node["timestamp"] = timestamp += 15_000;
			var key = Guid.NewGuid().ToString( "N" );
			chat.UpdateFrame( JsonSerializer.SerializeToElement( new { type = "message_start", messageId = key, message = node } ) );
			chat.UpdateFrame( JsonSerializer.SerializeToElement( new { type = "message_end", messageId = key, message = node } ) );
		}
		Pump( host );
	}

	/// <summary>Builds a user message.</summary>
	private static JsonElement User( string text ) => JsonSerializer.SerializeToElement( new { role = "user", content = new[] { new { type = "text", text } } } );

	/// <summary>Builds an assistant message with one text block.</summary>
	private static JsonElement Assistant( string text ) => Assistant( new { type = "text", text } );

	/// <summary>Builds an assistant message from content blocks.</summary>
	private static JsonElement Assistant( params object[] content ) => JsonSerializer.SerializeToElement( new
	{
		role = "assistant",
		content,
		api = "anthropic-messages",
		provider = "anthropic",
		model = "claude-sonnet-4-5",
		usage = new { input = 1200, output = 180, cacheRead = 0, cacheWrite = 0, totalTokens = 1380 },
		stopReason = content.Any( block => JsonSerializer.SerializeToElement( block ).GetProperty( "type" ).GetString() == "toolCall" ) ? "toolUse" : "stop",
	} );

	/// <summary>Builds a tool result message.</summary>
	private static JsonElement ToolResult( string callId, string tool, string text ) => JsonSerializer.SerializeToElement( new
	{
		role = "toolResult",
		toolCallId = callId,
		toolName = tool,
		content = new[] { new { type = "text", text } },
		isError = false,
	} );

	/// <summary>Lists the shown panels in layout order with their saved state.</summary>
	private static IEnumerable<(string TypeId, string State)> ShownPanels( LoknApplication app ) =>
		app.Panels.Sessions.Where( session => app.Panels.IsShown( session.Info.Id ) )
			.Select( session => (session.Info.TypeId, session.Content?.CaptureState()?.GetRawText() ?? "null") );

	/// <summary>Lists the types of the shown panels.</summary>
	private static IEnumerable<string> ShownTypes( LoknApplication app ) => ShownPanels( app ).Select( panel => panel.TypeId );

	/// <summary>Fails when a module or setting was refused.</summary>
	private static void Require( bool isDone, string what, LoknApplication? app = null )
	{
		if ( !isDone )
			throw new InvalidOperationException( $"{what} failed: {app?.LastError}" );
	}
}
