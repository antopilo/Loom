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
			( context, history, id ) => new ChatContent( context, history, id, omp.Service ) );
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
		var chatPanel = module.OpenConversation( app, conversation.Id );
		var semantics = new SemanticAdapter();
		var window = new Window
		{
			Title = "Lokn conversation checks",
			Size = new( 1200, 800 ),
			Decorated = false,
			Background = Theme.Current.Canvas,
			Foreground = Theme.Current.Text,
			Root = new ShellView( app ),
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

		Click( window, Find( semantics, AccessibilityRole.Button, "Build Lokn" ) );
		CoreChecks.Check( app.Panels.ActivePanelId == chatPanel && app.Panels.Panels.Count == 1, "Selecting existing history reuses its conversation panel." );
		Click( window, Find( semantics, AccessibilityRole.Radio, "Switch to Work" ) );
		CoreChecks.Check( app.Settings.ActiveProfile == SettingsProfile.Work, "The sidebar's profile switcher switches to Work." );
		CoreChecks.Check( HasText( semantics, "Office discussion" ) && !Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Build Lokn" ), "Work history shows its own projects and conversations." );
		Click( window, Find( semantics, AccessibilityRole.Radio, "Work profile, active. Choose its icon." ) );
		Click( window, Find( semantics, AccessibilityRole.Button, "Use 💼" ) );
		CoreChecks.Check( app.Settings.Get( ShellSettings.ProfileIcon, SettingsScope.Work ) == "💼" && app.Settings.Get( ShellSettings.ProfileIcon, SettingsScope.Personal ) == "",
			"Clicking the active profile opens its icon picker, and a chosen emoji saves for that profile only." );
		CoreChecks.Check( store.Get( conversation.Id ).Profile == SettingsProfile.Personal, "An open conversation retains its Personal account after the global toggle." );
		Click( window, Find( semantics, AccessibilityRole.Radio, "Switch to Personal" ) );
		Pump( window );

		var captureIndex = Array.IndexOf( args, "--capture" );
		if ( captureIndex >= 0 && captureIndex + 1 < args.Length )
			NativeChecks.BuildCapture( window, args[captureIndex + 1], "conversations" );

		Click( window, Find( semantics, AccessibilityRole.Button, "New chat" ) );
		CoreChecks.Check( store.Conversations( SettingsProfile.Personal ).Count == 1, "New chat creates a profile-pinned ungrouped conversation." );
		CoreChecks.Check( app.Panels.Panels.Count == 1 && app.Panels.ActivePanelId == chatPanel, "New chat reuses the chat panel instead of opening another." );
		var sentConversation = store.Conversations( SettingsProfile.Personal ).Single();
		store.SaveBackendSession( sentConversation.Id, Path.Combine( Path.GetTempPath(), "lokn-ui-session-" + Guid.NewGuid().ToString( "N" ) ) );
		app.Settings.Set( OmpModule.Executable, SettingsScope.Global, Path.Combine( AppContext.BaseDirectory, "Lokn.Tests.exe" ) );
		Pump( window );
		UpdateText( window, Find( semantics, AccessibilityRole.TextEditor, "Message" ), "Exercise the local fixture" );
		Click( window, Find( semantics, AccessibilityRole.Button, "Send" ) );
		PumpUntil( window, () => Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Confirm" ) );
		CoreChecks.Check( store.GetMessages( sentConversation.Id ).Count == 2, "Streaming updates replace the assistant message instead of appending duplicates." );
		CoreChecks.Check( HasText( semantics, "Streaming complete." ), "The composer sends through a real child and renders its streaming output." );
		Click( window, Find( semantics, AccessibilityRole.Button, "Confirm" ) );
		PumpUntil( window, () => store.GetMessages( sentConversation.Id ).Any( message => message.Text == "Fixture approval received." ) );
		CoreChecks.Check( !Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Confirm" ), "The native confirmation replies over RPC and dismisses the dialog." );
		if ( captureIndex >= 0 && captureIndex + 1 < args.Length )
			NativeChecks.BuildCapture( window, args[captureIndex + 1], "conversation-stream" );
		OmpModule.OpenRouting( app, SettingsProfile.Work );
		PumpUntil( window, () => HasText( semantics, "fixture/reviewer:high" ) && HasText( semantics, "fixture/two:low" ) );
		CoreChecks.Check( HasText( semantics, "Careful" ) && Find( semantics, AccessibilityRole.Button, "high" ) is not null,
			"Model routing renders Omp role assignments, fallback chains, presets, and the profile's thinking level." );

		var hubSession = omp.Service.OpenSessionAsync(
			SettingsProfile.Personal,
			reviewRoot,
			Path.Combine( directory, "agent-hub-session" ) ).GetAwaiter().GetResult();
		OmpModule.OpenAgentHub( app, hubSession, app.Panels.ActivePanelId!.Value );
		PumpUntil( window, () => HasText( semantics, "Fixture subagent transcript." ) );
		CoreChecks.Check( HasText( semantics, "Index fixture repository" ) && HasText( semantics, "fixture/worker:low" ) && HasText( semantics, "context 2k/32k" ),
			"Agent Hub renders the live roster, routed model, context, current tool, and persisted transcript." );

		var gitPanel = app.Panels.Open( GitModule.PanelType, input: new GitPanelInput( reviewRoot ) );
		PumpUntil( window, () => Nodes( semantics ).Any( node => node.Name?.StartsWith( "review.txt,", StringComparison.Ordinal ) == true ) );
		Click( window, Nodes( semantics ).First( node => node.Name?.StartsWith( "review.txt,", StringComparison.Ordinal ) == true ) );
		PumpUntil( window, () => Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Annotate line 2" ) );
		Click( window, Find( semantics, AccessibilityRole.Button, "Annotate line 2" ) );
		UpdateText( window, Find( semantics, AccessibilityRole.TextInput, "Review annotation" ), "Preserve the configured value." );
		Click( window, Find( semantics, AccessibilityRole.Button, "Save" ) );
		Click( window, Find( semantics, AccessibilityRole.Button, "Review in chat: local changes" ) );
		PumpUntil( window, () => store.Conversations( SettingsProfile.Personal ).Any( item =>
			store.GetMessages( item.Id ).Any( message => message.Text.Contains( "<lokn-review>", StringComparison.Ordinal ) ) ) );
		app.Panels.Activate( gitPanel );
		PumpUntil( window, () => HasText( semantics, "Preserve the fixture value" ) );
		CoreChecks.Check( HasText( semantics, "P1" ) && HasText( semantics, "Review · Changes requested · 98%" ),
			"Git line notes launch Omp review and its structured verdict and P1 finding return to the native panel." );

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
			( context, history, id ) => new ChatContent( context, history, id, reopenedOmp.Service ) ) );
		CoreChecks.Check( reopened.RestoreWorkspace() && reopened.Panels.Root == savedTree && reopened.Panels.ActivePaneId == savedActivePane, "Closing the actual native window saves the complete live layout before module teardown." );
		CoreChecks.Check( reopened.LastError is null && reopenedHistory.GetMessages( sentConversation.Id ).Last().Text == "Fixture approval received.", "Reopening restores real chat panel identity and completed rich messages without starting a backend." );
		reopened.Shutdown();
		Console.WriteLine( "PASS native projects, history reuse, rich blocks, profile filtering, composer-to-process streaming, RPC tool confirmation, model routing, Agent Hub, Git review, and native window restart (no provider request)." );
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

	/// <summary>Finds an accessible control whose name starts with a stable prefix.</summary>
	private static AccessibilityNode FindStarting( SemanticAdapter semantics, AccessibilityRole role, string prefix ) =>
		Nodes( semantics ).Single( node => node.Role == role && node.Name?.StartsWith( prefix, StringComparison.Ordinal ) == true );

	/// <summary>Finds an exact accessible control.</summary>
	private static AccessibilityNode Find( SemanticAdapter semantics, AccessibilityRole role, string name ) =>
		Nodes( semantics ).Single( node => node.Role == role && node.Name == name );

	/// <summary>Checks semantic text for a rendered fragment.</summary>
	private static bool HasText( SemanticAdapter semantics, string fragment ) =>
		Nodes( semantics ).Any( node => (node.Name?.Contains( fragment, StringComparison.Ordinal ) ?? false) || (node.Value?.Contains( fragment, StringComparison.Ordinal ) ?? false) );

	/// <summary>Replaces a field using native keyboard input.</summary>
	private static void UpdateText( Window window, AccessibilityNode node, string text )
	{
		Click( window, node );
		window.PlatformInput.KeyPress( Key.A, KeyModifiers.Primary );
		window.PlatformInput.KeyRelease( Key.A );
		window.PlatformInput.CommitText( text );
		Pump( window );
	}

	/// <summary>Clicks a semantic control through native pointer routing.</summary>
	private static void Click( Window window, AccessibilityNode node )
	{
		var bounds = node.VisibleBounds ?? node.Bounds;
		CoreChecks.Check( bounds.Width > 0 && bounds.Height > 0, $"{node.Name} is visible." );
		var x = (bounds.X + bounds.Width / 2) * window.UiScale;
		var y = (bounds.Y + bounds.Height / 2) * window.UiScale;
		window.PlatformInput.PointerMove( 1, PointerDevice.Mouse, x, y, KeyModifiers.None, 0.0f );
		window.PlatformInput.PointerPress( 1, PointerDevice.Mouse, x, y, PointerButton.Primary, KeyModifiers.None, 0.0f );
		window.PlatformInput.PointerRelease( 1, PointerDevice.Mouse, x, y, PointerButton.Primary, KeyModifiers.None, 0.0f );
		Pump( window );
	}

	/// <summary>Creates a deterministic repository with one changed review line for native Git review checks.</summary>
	private static void CreateReviewRepository( string repository )
	{
		Directory.CreateDirectory( repository );
		File.WriteAllText( Path.Combine( repository, "review.txt" ), "fixture\nold value\n" );
		GitProcess.RunAsync( repository, CancellationToken.None, "init", "--quiet", "--initial-branch=main" ).GetAwaiter().GetResult();
		GitProcess.RunAsync( repository, CancellationToken.None, "add", "." ).GetAwaiter().GetResult();
		GitProcess.RunAsync( repository, CancellationToken.None,
			"-c", "user.name=Lokn Checks", "-c", "user.email=checks@example.invalid", "commit", "--quiet", "-m", "Initial" ).GetAwaiter().GetResult();
		File.WriteAllText( Path.Combine( repository, "review.txt" ), "fixture\nnew value\n" );
	}

	/// <summary>Pumps native events until the local child has completed an observable protocol step.</summary>
	private static void PumpUntil( Window window, Func<bool> predicate )
	{
		var timeout = System.Diagnostics.Stopwatch.StartNew();
		while ( !predicate() )
		{
			if ( timeout.Elapsed > TimeSpan.FromSeconds( 20 ) )
				throw new TimeoutException( "The local Omp fixture did not reach the expected native UI state." );
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
