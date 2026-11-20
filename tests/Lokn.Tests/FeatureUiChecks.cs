using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Conversations;
using Lokn.Modules.Git;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;

namespace Lokn.Tests;

/// <summary>Checks new conversation surfaces through a native Goo window without making model requests.</summary>
internal static class FeatureUiChecks
{
	/// <summary>Defines the frame count used to settle layout and accessibility updates.</summary>
	private const int SettleFrames = 16;

	/// <summary>Runs project creation, history selection, rich rendering, and profile filtering checks.</summary>
	internal static void Run( string[] args )
	{
		Application.VerifyState = true;
		var directory = Path.Combine( Path.GetTempPath(), "lokn-native-features-" + Guid.NewGuid().ToString( "N" ) );
		var workspacePath = Path.Combine( directory, "workspace.json" );
		var historyPath = Path.Combine( directory, "conversations.json" );
		var reviewRoot = Path.Combine( directory, "review" );
		CreateReviewRepository( reviewRoot );
		var app = new LoknApplication( workspacePath: workspacePath );
		app.Load( new ShellModule() );
		var omp = new OmpModule();
		app.Load( omp );
		app.Load( new GitModule() );
		var projectsPath = Path.Combine( directory, "projects.json" );
		var store = new ConversationStore( new ProjectStore( projectsPath ), historyPath );
		var module = new ConversationsModule( store,
			ConversationsModule.BuildChat );
		ProjectChecks.LoadProjects( app, store );
		app.Load( module );
		var project = store.ProjectStore.Create( SettingsProfile.Personal, "Harness", Environment.CurrentDirectory );
		var conversation = store.CreateConversation( SettingsProfile.Personal, project.Id, "Build Lokn" );
		store.AppendMessage( conversation.Id, new( Guid.NewGuid(), "assistant", "Plan", DateTimeOffset.UtcNow,
			JsonSerializer.SerializeToElement( new
			{
				role = "assistant",
				content = new object[]
				{
					new { type = "text", text = "## Plan\n\n- [x] Inspect\n- [ ] Build\n\n1. First step\n2. Second step\n\n```csharp\nConsole.WriteLine(\"Lokn\");\n```" },
					new { type = "thinking", thinking = "Reviewing the implementation." },
					new { type = "toolCall", id = "tool-1", name = "bash", arguments = new { command = "dotnet build" } },
				},
			} ) ) );
		store.AppendMessage( conversation.Id, new( Guid.NewGuid(), "toolResult", "Build succeeded.", DateTimeOffset.UtcNow,
			JsonSerializer.SerializeToElement( new
			{
				role = "toolResult",
				toolCallId = "tool-1",
				toolName = "bash",
				content = new[] { new { type = "text", text = "Build succeeded." } },
				isError = false,
			} ) ) );
		var office = store.ProjectStore.Create( SettingsProfile.Work, "Office", Environment.CurrentDirectory );
		store.CreateConversation( SettingsProfile.Work, office.Id, "Office discussion" );
		var chatPanel = module.OpenConversation( app, ChatRef.Local( conversation.Id ) );
		var semantics = new SemanticAdapter();
		var window = new Window
		{
			Title = "Lokn conversation checks",
			Size = new( 1200, 800 ),
			Decorated = false,
			Transparent = true,
			Background = Theme.Current.Canvas,
			Foreground = Theme.Current.Text,
			Root = new ShellView( app ) { AppliesNativeWindowHooks = false },
			AccessibilityAdapter = semantics,
		};
		app.BindDispatcher( window.Post );
		app.Commands.BindDispatcher( operation => window.InvokeAsync( operation ) );
		window.Open();
		Pump( window );
		CoreChecks.Check( app.LastError is null, "Conversation surfaces build without contained failures." );
		CoreChecks.Check( Find( semantics, AccessibilityRole.Button, "Build Lokn" ) is not null, "The sidebar lists project conversations beside the chat." );
		CoreChecks.Check( app.Panels.Panels.Count == 1, "The sidebar is not a panel, so the chat is the only one." );
		CoreChecks.Check( HasText( semantics, "Inspect" ) && HasText( semantics, "First step" ) && Find( semantics, AccessibilityRole.Button, "Ran 1 command" ) is not null,
			"Rich chat renders task lists, numbered steps, and its tool run folded into a summary line." );
		CoreChecks.Check( HasText( semantics, "Console.WriteLine" ), "Rich chat renders fenced code." );

		Click( window, semantics, AccessibilityRole.Button, "Build Lokn" );
		PumpUntil( window, semantics, () => app.Panels.ActivePanelId == chatPanel && app.Panels.Panels.Count == 1, "Selecting existing history reuses its conversation panel." );
		// Another program taking the system focus makes Goo drop raw presses; the checks' input focuses its target first, so the click still lands.
		window.PlatformInput.FocusLost();
		Click( window, semantics, AccessibilityRole.Radio, "Switch to Work" );
		PumpUntil( window, semantics, () => app.Settings.ActiveProfile == SettingsProfile.Work, "The sidebar's profile switcher switches to Work, also after the window lost the system focus." );
		PumpUntil( window, semantics, () => HasText( semantics, "Office discussion" ) && !Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Build Lokn" ),
			"Work history shows its own projects and conversations." );
		Click( window, semantics, AccessibilityRole.Radio, "Work profile, active. Choose its icon." );
		Click( window, semantics, AccessibilityRole.Button, "Use 💼" );
		PumpUntil( window, semantics, () => app.Settings.Get( ShellSettings.ProfileIcon, SettingsScope.Work ) == "💼" && app.Settings.Get( ShellSettings.ProfileIcon, SettingsScope.Personal ) == "",
			"Clicking the active profile opens its icon picker, and a chosen emoji saves for that profile only." );
		CoreChecks.Check( store.Get( conversation.Id ).Profile == SettingsProfile.Personal, "An open conversation retains its Personal account after the global toggle." );
		Click( window, semantics, AccessibilityRole.Radio, "Switch to Personal" );
		PumpUntil( window, semantics, () => app.Settings.ActiveProfile == SettingsProfile.Personal && Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "New chat" ),
			"The sidebar's profile switcher switches back to Personal." );

		var captureIndex = Array.IndexOf( args, "--capture" );
		if ( captureIndex >= 0 && captureIndex + 1 < args.Length )
			NativeChecks.BuildCapture( window, args[captureIndex + 1], "conversations" );

		Click( window, semantics, AccessibilityRole.Button, "New chat" );
		PumpUntil( window, semantics, () => store.Conversations( SettingsProfile.Personal ).Count == 1, "New chat creates a profile-pinned ungrouped conversation." );
		CoreChecks.Check( app.Panels.Panels.Count == 1 && app.Panels.ActivePanelId == chatPanel, "New chat reuses the chat panel instead of opening another." );
		var sentConversation = store.Conversations( SettingsProfile.Personal ).Single();
		store.SaveBackendSession( sentConversation.Id, Path.Combine( Path.GetTempPath(), "lokn-ui-session-" + Guid.NewGuid().ToString( "N" ) ) );
		app.Settings.Set( OmpModule.Executable, SettingsScope.Global, Path.Combine( AppContext.BaseDirectory, "Lokn.Tests.exe" ) );
		Pump( window );
		UpdateText( window, semantics, AccessibilityRole.TextEditor, "Message", "Exercise the local fixture" );
		Click( window, semantics, AccessibilityRole.Button, "Send" );
		PumpUntil( window, semantics, () => Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Confirm" ), "The fixture's question shows its Confirm button." );
		CoreChecks.Check( store.GetMessages( sentConversation.Id ).Count == 2, "Streaming updates replace the assistant message instead of appending duplicates." );
		CoreChecks.Check( HasText( semantics, "Streaming complete." ), "The composer sends through a real child and renders its streaming output." );
		Click( window, semantics, AccessibilityRole.Button, "Confirm" );
		PumpUntil( window, semantics, () => store.GetMessages( sentConversation.Id ).Any( message => message.Text == "Fixture approval received." ), "The fixture receives the approval." );
		PumpUntil( window, semantics, () => !Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Confirm" ),
			"The native confirmation replies over RPC and dismisses the dialog." );
		if ( captureIndex >= 0 && captureIndex + 1 < args.Length )
			NativeChecks.BuildCapture( window, args[captureIndex + 1], "conversation-stream" );
		OmpModule.OpenRouting( app, SettingsProfile.Work );
		PumpUntil( window, semantics, () => HasText( semantics, "fixture/reviewer:high" ) && HasText( semantics, "fixture/two:low" ), "Model routing lists the fixture's roles." );
		CoreChecks.Check( HasText( semantics, "Careful" ) && Find( semantics, AccessibilityRole.Button, "high" ) is not null,
			"Model routing renders Omp role assignments, fallback chains, presets, and the profile's thinking level." );

		var hubSession = omp.Service.OpenSessionAsync(
			SettingsProfile.Personal,
			reviewRoot,
			Path.Combine( directory, "agent-hub-session" ) ).GetAwaiter().GetResult();
		OmpModule.OpenAgentHub( app, hubSession, app.Panels.ActivePanelId!.Value );
		PumpUntil( window, semantics, () => HasText( semantics, "Fixture subagent transcript." ), "The Agent Hub shows the fixture subagent's transcript." );
		CoreChecks.Check( HasText( semantics, "Index fixture repository" ) && HasText( semantics, "fixture/worker:low" ) && HasText( semantics, "context 2k/32k" ),
			"Agent Hub renders the live roster, routed model, context, current tool, and persisted transcript." );

		// Routing and the Agent Hub are checked; closing them gives the Git panel room, as the note editor needs a usable width.
		foreach ( var checkedPanel in app.Panels.Panels.Where( item => item.TypeId is "omp.routing" or "omp.agents" ).Select( item => item.Id ).ToArray() )
			app.Panels.Close( checkedPanel );
		// The Git panel opens on the commit graph; a changed file opens in it the way a click in the Files side view's Changes mode or in a chat does.
		var gitPanel = app.Panels.Open( GitModule.PanelType, input: new GitPanelInput( reviewRoot, Focus: Path.Combine( reviewRoot, "review.txt" ) ) );
		PumpUntil( window, semantics, () => Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Annotate line 2" ), "The changed file's diff offers line notes." );
		// The removed old line 2 and the added new line 2 both offer a note; the added one comes second in the unified diff.
		Click( window, semantics, Nodes( semantics ).Last( node => node.Role == AccessibilityRole.Button && node.Name == "Annotate line 2" ) );
		UpdateText( window, semantics, AccessibilityRole.TextInput, "Review annotation", "Preserve the configured value." );
		Click( window, semantics, AccessibilityRole.Button, "Save" );
		Click( window, semantics, AccessibilityRole.Button, "Review in chat: local changes" );
		PumpUntil( window, semantics, () => store.Conversations( SettingsProfile.Personal ).Any( item =>
			store.GetMessages( item.Id ).Any( message => message.Text.Contains( "<lokn-review>", StringComparison.Ordinal ) ) ), "Review in chat sends the review to a chat." );
		app.Panels.Activate( gitPanel );
		PumpUntil( window, semantics, () => HasText( semantics, "Preserve the fixture value" ), "The review's finding returns to the Git panel." );
		CoreChecks.Check( HasText( semantics, "P1" ) && HasText( semantics, "Review · Changes requested · 98%" ),
			"Git line notes launch Omp review and its structured verdict and P1 finding return to the native panel." );
		CheckHostPanes( app, window, semantics );

		app.Panels.Open( "omp.accounts" );
		Pump( window );
		CoreChecks.Check( Find( semantics, AccessibilityRole.Group, "Omp sign-in" ) is not null && HasText( semantics, "used by Personal chats" ), "The Sign in page renders for the selected profile without starting a login." );
		CoreChecks.Check( app.LastError is null, "Conversation interactions remain free of contained failures." );
		var savedTree = app.Panels.Root;
		var savedActivePane = app.Panels.ActivePaneId;
		window.RequestClose();
		Pump( window );
		var reopened = new LoknApplication( workspacePath: workspacePath );
		reopened.Load( new ShellModule() );
		var reopenedOmp = new OmpModule();
		reopened.Load( reopenedOmp );
		reopened.Load( new GitModule() );
		var reopenedHistory = new ConversationStore( new ProjectStore( projectsPath ), historyPath );
		ProjectChecks.LoadProjects( reopened, reopenedHistory );
		reopened.Load( new ConversationsModule( reopenedHistory,
			ConversationsModule.BuildChat ) );
		CoreChecks.Check( reopened.RestoreWorkspace() && reopened.Panels.Root == savedTree && reopened.Panels.ActivePaneId == savedActivePane, "Closing the actual native window saves the complete live layout before module teardown." );
		CoreChecks.Check( reopened.LastError is null && reopenedHistory.GetMessages( sentConversation.Id ).Last().Text == "Fixture approval received.", "Reopening restores real chat panel identity and completed rich messages without starting a backend." );
		reopened.Shutdown();
		Console.WriteLine( "PASS native projects, history reuse, rich blocks, profile filtering, composer-to-process streaming, RPC tool confirmation, model routing, Agent Hub, Git review, "
			+ "host chips and offline covers on panes, and native window restart (no provider request)." );
	}

	/// <summary>Checks a paired host's pane: its chip and labeled menu, the offline cover while its link fails, and Close panel once the host is gone.</summary>
	private static void CheckHostPanes( LoknApplication app, Window window, SemanticAdapter semantics )
	{
		app.Load( new HostPanelModule() );
		var desk = app.Hosts.Add( "desk-ui", "Desk" );
		desk.Update( LoknHostState.Connected, "Connected via fixture.", canControl: true );
		var hosted = app.Panels.Open( "uihosts.bound", hostId: desk.Id );
		PumpUntil( window, semantics, () => HasText( semantics, "On Desk" ) && HasText( semantics, "Bound · Desk options" ) && !HasText( semantics, "Desk is offline" ),
			"A panel on a paired host names the host in a chip and in its pane's options, without a cover while the host is connected." );

		desk.Update( LoknHostState.Failed, "Could not reach Desk.", canControl: true );
		PumpUntil( window, semantics, () => HasText( semantics, "Desk is offline" ) && HasText( semantics, "Could not reach Desk." ) && HasText( semantics, "Hosted on Desk" ),
			"A failed link covers the host's panel with its status while the panel stays mounted." );
		desk.Update( LoknHostState.Connected, "Connected via fixture.", canControl: true );
		PumpUntil( window, semantics, () => !HasText( semantics, "Desk is offline" ), "Reconnecting takes the cover away." );

		// The sidebar's host switcher names the current host with its state, and its menu lists every host with theirs and switches to one, which shows Home on it.
		bool HasButton( AccessibilityRole role, string name ) => Nodes( semantics ).Any( node => node.Role == role && node.Name == name );
		var conversations = app.GetModule<ConversationsModule>();
		conversations.Hosts.Add( desk );
		PumpUntil( window, semantics, () => HasButton( AccessibilityRole.Button, "Host: This PC, This PC. Switch host" ),
			"Once a paired host exists, the sidebar's host switcher shows This PC with its state." );
		Click( window, semantics, AccessibilityRole.Button, "Host: This PC, This PC. Switch host" );
		PumpUntil( window, semantics, () => HasButton( AccessibilityRole.MenuItem, "✓  This PC" ) && HasButton( AccessibilityRole.MenuItem, "Desk" ) && HasText( semantics, "Connected" ),
			"The hosts menu lists every host with its state, the current one checked." );
		Click( window, semantics, AccessibilityRole.MenuItem, "Desk" );
		PumpUntil( window, semantics, () => app.Hosts.Current == desk && HasButton( AccessibilityRole.Button, "Host: Desk, Connected. Switch host" ) && !HasButton( AccessibilityRole.MenuItem, "Desk" ),
			"Choosing a host in the menu makes it current, closes the menu, and the switcher shows that host's state." );
		desk.Update( LoknHostState.Failed, "Could not reach Desk.", canControl: true );
		PumpUntil( window, semantics, () => HasButton( AccessibilityRole.Button, "Host: Desk, Offline. Switch host" ) && HasText( semantics, "Desk is offline · Could not reach Desk." ),
			"The switcher and its banner say the current host is offline." );
		desk.Update( LoknHostState.Connected, "Connected via fixture.", canControl: true );
		Pump( window );

		// The tab strip leads with the same host chip while more than one host exists.
		app.Settings.Set( ShellSettings.Navigation, SettingsScope.Global, ShellSettings.TabsNavigation );
		PumpUntil( window, semantics, () => HasButton( AccessibilityRole.Button, "Host: Desk, Connected. Switch host" ) && !HasText( semantics, "Desk is offline" ),
			"The tab strip's host chip names the current host and its state." );
		app.Settings.Set( ShellSettings.Navigation, SettingsScope.Global, ShellSettings.SidebarNavigation );
		conversations.SwitchHost( app, LoknHost.LocalId );
		conversations.Hosts.Remove( desk.Id );
		Pump( window );

		app.Hosts.Remove( desk.Id );
		var gone = app.Panels.Open( "uihosts.bound", hostId: "gone-ui" );
		PumpUntil( window, semantics, () => !app.Panels.Panels.Any( panel => panel.Id == hosted ) && HasText( semantics, "gone-ui was removed" ),
			"Removing a host closes its panels, and a panel restored for a host no longer paired says it was removed." );
		Click( window, semantics, AccessibilityRole.Button, "Close panel" );
		PumpUntil( window, semantics, () => !app.Panels.Panels.Any( panel => panel.Id == gone ), "Close panel closes the panel of a removed host." );
		CoreChecks.Check( app.LastError is null, $"The host panes report no failure ({app.LastError})." );
		app.Unload( "uihosts" );
		Pump( window );
	}

	/// <summary>Registers a host-bound panel that names its host.</summary>
	private sealed class HostPanelModule : ILoknModule
	{
		/// <summary>Gets the fixture module identifier.</summary>
		public string Id => "uihosts";

		/// <summary>Gets the fixture module name.</summary>
		public string Title => "Host panes fixture";

		/// <summary>Registers the host-bound panel type.</summary>
		public void Register( ModuleBuilder builder ) => builder.Panel( "bound", "Bound", context => new HostPanel( context ), onHost: true );
	}

	/// <summary>Shows the name of the host its panel works on.</summary>
	/// <param name="context">The panel's context.</param>
	private sealed class HostPanel( PanelContext context ) : PanelContent
	{
		/// <summary>Builds the host's name.</summary>
		public override Blob Build() => new Text( $"Hosted on {context.Host.Name}" );
	}

	/// <summary>Gets semantic nodes in source order.</summary>
	private static IEnumerable<AccessibilityNode> Nodes( SemanticAdapter semantics ) => Flatten( semantics.Tree!.Root! );

	/// <summary>Enumerates a semantic subtree.</summary>
	private static IEnumerable<AccessibilityNode> Flatten( AccessibilityNode node )
	{
		yield return node;
		foreach ( var child in node.Children )
		{
			foreach ( var descendant in Flatten( child ) )
				yield return descendant;
		}
	}

	/// <summary>Finds an exact accessible control.</summary>
	private static AccessibilityNode Find( SemanticAdapter semantics, AccessibilityRole role, string name ) =>
		Nodes( semantics ).Single( node => node.Role == role && node.Name == name );

	/// <summary>Checks semantic text for a rendered fragment.</summary>
	private static bool HasText( SemanticAdapter semantics, string fragment ) =>
		Nodes( semantics ).Any( node => (node.Name?.Contains( fragment, StringComparison.Ordinal ) ?? false) || (node.Value?.Contains( fragment, StringComparison.Ordinal ) ?? false) );

	/// <summary>Replaces the text of the one field with the role and exact name using native keyboard input, focusing it first (<see cref="UiInput"/>).</summary>
	private static void UpdateText( Window window, SemanticAdapter semantics, AccessibilityRole role, string name, string text ) =>
		UiInput.Type( window, () => Nodes( semantics ).SingleOrDefault( node => node.Role == role && node.Name == name ), () => Pump( window ), name, text );

	/// <summary>Clicks the one control with the role and exact name, once it shows, through native pointer routing, focusing it first (<see cref="UiInput"/>).</summary>
	private static void Click( Window window, SemanticAdapter semantics, AccessibilityRole role, string name ) =>
		UiInput.Click( window, () => Nodes( semantics ).SingleOrDefault( node => node.Role == role && node.Name == name ), () => Pump( window ), name );

	/// <summary>Clicks a control found before, followed by its identifier, through native pointer routing, focusing it first (<see cref="UiInput"/>).</summary>
	private static void Click( Window window, SemanticAdapter semantics, AccessibilityNode node ) =>
		UiInput.Click( window, () => Nodes( semantics ).FirstOrDefault( candidate => candidate.Id == node.Id ), () => Pump( window ), node.Name ?? node.Role.ToString() );

	/// <summary>Creates a deterministic repository with one changed review line for native Git review checks.</summary>
	private static void CreateReviewRepository( string repository )
	{
		Directory.CreateDirectory( repository );
		File.WriteAllText( Path.Combine( repository, "review.txt" ), "fixture\nold value\n" );
		GitProcess.RunAsync( LocalWorkspaceHost.Shared, repository, CancellationToken.None, "init", "--quiet", "--initial-branch=main" ).GetAwaiter().GetResult();
		GitProcess.RunAsync( LocalWorkspaceHost.Shared, repository, CancellationToken.None, "add", "." ).GetAwaiter().GetResult();
		GitProcess.RunAsync( LocalWorkspaceHost.Shared, repository, CancellationToken.None,
			"-c", "user.name=Lokn Checks", "-c", "user.email=checks@example.invalid", "commit", "--quiet", "-m", "Initial" ).GetAwaiter().GetResult();
		File.WriteAllText( Path.Combine( repository, "review.txt" ), "fixture\nnew value\n" );
	}

	/// <summary>Pumps native events until the state an action changes holds, failing with what was expected and what shows after 20 seconds.</summary>
	private static void PumpUntil( Window window, SemanticAdapter semantics, Func<bool> predicate, string what )
	{
		var timeout = System.Diagnostics.Stopwatch.StartNew();
		while ( !predicate() )
		{
			if ( timeout.Elapsed > TimeSpan.FromSeconds( 20 ) )
			{
				var shown = string.Join( " | ", Nodes( semantics ).Select( node => node.Name ).Where( name => name is { Length: > 0 } ).Distinct() );
				CoreChecks.Check( false, $"{what} (timed out; showing: {shown[..Math.Min( shown.Length, 3000 )]})" );
			}
			Pump( window );
		}
		Pump( window );
	}

	/// <summary>Settles layout and accessibility delivery.</summary>
	private static void Pump( Window window )
	{
		for ( var index = 0; index < SettleFrames; index++ )
		{
			window.Pump( 0.016 );
			Thread.Sleep( 10 );
		}
	}
}
