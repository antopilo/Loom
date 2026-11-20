using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Conversations;
using Lokn.Modules.Git;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;

namespace Lokn.Tests;

/// <summary>Checks Omp-style chat rendering and raw Unicode round trips without unrelated workspace interactions.</summary>
internal static class ChatViewChecks
{
	/// <summary>Defines the settling frame count for the native chat view.</summary>
	private const int SettleFrames = 4;

	/// <summary>Defines the Unicode prompt sent to the local fixture, including the previously corrupted emoji.</summary>
	private const string UnicodePrompt = "Check café — 👋 and curly quotes ‘hello’.";

	/// <summary>Runs transcript layout, tool grouping, composer retention, and UTF-8 streaming checks.</summary>
	internal static void Run( string[] args )
	{
		Application.VerifyState = true;
		var captureAt = Array.IndexOf( args, "--capture" );
		RunGitPanel( captureAt >= 0 && captureAt + 1 < args.Length ? args[captureAt + 1] : null );
		var app = new LoknApplication();
		app.Load( new ShellModule() );
		var omp = new OmpModule();
		app.Load( omp );
		app.Settings.Set( OmpModule.Executable, SettingsScope.Global, Path.Combine( AppContext.BaseDirectory, "Lokn.Tests.exe" ) );
		app.Settings.Set( OmpModule.Model, SettingsScope.Global, "fixture/one" );
		app.Settings.Set( OmpModule.Economy, SettingsScope.Personal, true );
		var store = new ConversationStore( new ProjectStore() );
		var project = store.ProjectStore.Create( SettingsProfile.Personal, "Lokn", Environment.CurrentDirectory );
		var conversation = store.CreateConversation( SettingsProfile.Personal, project.Id, "Omp conversation" );
		store.SaveBackendSession( conversation.Id, Path.Combine( Path.GetTempPath(), "lokn-chat-view-" + Guid.NewGuid().ToString( "N" ) ) );
		store.AppendMessage( conversation.Id, new( Guid.NewGuid(), "user", UnicodePrompt, DateTimeOffset.UtcNow ) );
		store.AppendMessage( conversation.Id, new( Guid.NewGuid(), "assistant", "Plan", DateTimeOffset.UtcNow,
			JsonSerializer.SerializeToElement( new
			{
				role = "assistant",
				content = new object[]
				{
					new { type = "toolCall", id = "plan", name = "todo", arguments = new { op = "start", task = "Render the todo card" } },
					new { type = "text", text = "## Plan\n\nI’ll inspect the project, then verify the changes.\n\n- [x] Inspect the source\n- [ ] Verify the result\n\n```csharp\nConsole.WriteLine(\"Hello, Lokn!\");\n```" },
					new { type = "thinking", thinking = "Checking the relevant implementation." },
					new { type = "toolCall", id = "build", name = "bash", arguments = new { command = "dotnet build" } },
				},
			} ) ) );
		store.AppendMessage( conversation.Id, new( Guid.NewGuid(), "toolResult", "Build succeeded — café 👋", DateTimeOffset.UtcNow,
			JsonSerializer.SerializeToElement( new
			{
				role = "toolResult",
				toolCallId = "build",
				toolName = "bash",
				content = new[] { new { type = "text", text = "\u001b[32mBuild succeeded — café 👋\u001b[0m" } },
				isError = false,
			} ) ) );
		store.AppendMessage( conversation.Id, new( Guid.NewGuid(), "toolResult", "", DateTimeOffset.UtcNow,
			JsonSerializer.SerializeToElement( new
			{
				role = "toolResult",
				toolCallId = "plan",
				toolName = "todo",
				content = new[] { new { type = "text", text = "Remaining items (2)" } },
				details = new
				{
					phases = new object[]
					{
						new { name = "Inspect", tasks = new object[] { new { content = "Read the transcript", status = "completed" } } },
						new
						{
							name = "Display",
							tasks = new object[]
							{
								new { content = "Render the todo card", status = "in_progress" },
								new { content = "Color the code blocks", status = "pending" },
							},
						},
					},
				},
			} ) ) );
		ProjectChecks.LoadProjects( app, store );
		app.Load( new ConversationsModule( store, ConversationsModule.BuildChat ) );
		app.Panels.Open( ConversationsModule.ChatPanelType, input: new ConversationPanelInput( conversation.Id, conversation.Profile ) );
		var semantics = new SemanticAdapter();
		var window = new Window
		{
			Title = "Lokn Omp chat view checks",
			Size = new( 1100, 850 ),
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
		var editor = Find( semantics, AccessibilityRole.TextEditor, "Message" );
		CoreChecks.Check( editor.Bounds.Y > window.Size.Height / 2, "The Omp composer docks below the growing transcript." );
		CoreChecks.Check( !Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Text && (node.Value ?? "") == "Build succeeded — café 👋" ), "Finished tool output stays folded until it is opened." );
		CoreChecks.Check( !Nodes( semantics ).Any( node => (node.Value ?? "").Contains( '\u001b' ) ), "Tool output never displays raw ANSI escape controls." );
		CoreChecks.Check( HasText( semantics, UnicodePrompt ), "The transcript preserves punctuation, accents, and emoji." );
		CoreChecks.Check( HasText( semantics, "Render the todo card" ) && HasText( semantics, "Color the code blocks" ) && !HasText( semantics, "Read the transcript" ),
			"A todo list shows open phases' tasks unfolded and folds a finished phase to its header." );
		Click( window, editor );
		window.PlatformInput.CommitText( UnicodePrompt );
		Pump( window );
		// The bash call folds into its run's summary line, which opens onto the tool.
		Click( window, Find( semantics, AccessibilityRole.Button, "Ran 1 command" ) );
		PumpUntil( window, () => Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "bash · Done" ) );
		Click( window, Find( semantics, AccessibilityRole.Button, "bash · Done" ) );
		// Virtual rows settle their measured heights over a few frames after the opened tool grows.
		PumpUntil( window, () => Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Text && (node.Value ?? "") == "Build succeeded — café 👋" ) );
		CoreChecks.Check( Nodes( semantics ).Count( node => node.Role == AccessibilityRole.Text && (node.Value ?? "") == "Build succeeded — café 👋" ) == 1, "Opening a tool shows its paired result once." );
		CoreChecks.Check( ComputeComposerText( app ) == UnicodePrompt, "Expanding tool details retains the composer and its text." );
		var capture = Array.IndexOf( args, "--capture" );
		if ( capture >= 0 && capture + 1 < args.Length )
			NativeChecks.BuildCapture( window, args[capture + 1], "chat-omp" );
		Click( window, Find( semantics, AccessibilityRole.Button, "Send" ) );
		PumpUntil( window, () => Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Confirm" ) );
		CoreChecks.Check( store.GetMessages( conversation.Id ).Last( message => message.Role == "user" ).Text == UnicodePrompt, "Real subprocess JSONL round-trips the Unicode prompt without mojibake." );
		Click( window, Find( semantics, AccessibilityRole.Button, "Confirm" ) );
		PumpUntil( window, () => store.GetMessages( conversation.Id ).Any( message => message.Text == "Fixture approval received." ) );
		PumpUntil( window, () => Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Open token and cost details" ) );
		Click( window, Find( semantics, AccessibilityRole.Button, "Open token and cost details" ) );
		// The details open in a native tooltip popup, a separate surface whose text is not in this window's semantic tree;
		// ChatChecks checks the details' lines (ContextMeter.Describe).
		PumpUntil( window, () => Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Close token and cost details" ) );
		Click( window, Find( semantics, AccessibilityRole.Button, "Close token and cost details" ) );
		PumpUntil( window, () => Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Open token and cost details" ) );
		PumpUntil( window, () => Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Open usage and resets" ) );
		Click( window, Find( semantics, AccessibilityRole.Button, "Open usage and resets" ) );
		// The usage panel is a native menu popup: while it is open, the window's semantic tree is its dialog alone.
		PumpUntil( window, () => HasText( semantics, "37% used · resets in" ) );
		CoreChecks.Check( Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Dialog && node.Name == "Usage and resets" )
			&& HasText( semantics, "Fixture · person@example.com" ) && HasText( semantics, "2 saved limit resets, 1 usable now" ),
			"The prompt usage control shows the connected provider's reset times, account and saved resets." );
		window.PlatformInput.KeyPress( Key.Escape, KeyModifiers.None );
		window.PlatformInput.KeyRelease( Key.Escape );
		PumpUntil( window, () => Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Open usage and resets" ) );
		CoreChecks.Check( HasText( semantics, "Usage 63% left" ), "Escape closes usage, and the prompt rail shows the connected provider's remaining share." );
		CoreChecks.Check( app.LastError is null, "The Omp layout handles streaming and conditional dialogs without a render failure." );
		var first = Nodes( semantics ).First( node => node.Role == AccessibilityRole.Group && (node.Name ?? "").EndsWith( " message", StringComparison.Ordinal ) );
		window.PerformAccessibilityAction( first.Id, new( AccessibilityAction.ScrollIntoView ) );
		PumpUntil( window, () => Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Latest" ) );
		Click( window, Find( semantics, AccessibilityRole.Button, "Latest" ) );
		PumpUntil( window, () => !Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Latest" ) );
		if ( capture >= 0 && capture + 1 < args.Length )
			NativeChecks.BuildCapture( window, args[capture + 1], "chat-omp-stream" );
		window.RequestClose();
		Pump( window );
		Console.WriteLine( "PASS Omp-style docked composer, grouped tool output, details expansion, Unicode rendering, raw UTF-8 streaming, and tool dialogs (no provider request)." );
		RunLongTranscript( capture >= 0 && capture + 1 < args.Length ? args[capture + 1] : null );
	}

	/// <summary>Checks the Git panel on a real repository: the graph beside the working tree, a file's diff with its hunks unified and split, staging
	/// one hunk from it, a commit's file read-only, and new files appearing.</summary>
	private static void RunGitPanel( string? captureDirectory )
	{
		var root = Directory.CreateTempSubdirectory( "lokn-git-view-" ).FullName;
		var origin = root + "-origin";
		try
		{
			var source = Path.Combine( AppContext.BaseDirectory, "Lokn.Tests.dll" );
			var code = string.Join( "\n", Enumerable.Range( 1, 160 ).Select( line => $"\tvar value{line} = Compute( {line} );" ) );
			Directory.CreateDirectory( Path.Combine( root, "Modules", "Chat" ) );
			File.WriteAllText( Path.Combine( root, "Modules", "Chat", "View.cs" ), $"class View\n{{\n{code}\n}}\n" );
			ChatChecks.RunGit( root, "init", "-q", "--initial-branch=master" );
			ChatChecks.RunGit( root, "config", "core.autocrlf", "false" );
			ChatChecks.RunGit( root, "add", "." );
			ChatChecks.RunGit( root, "commit", "-q", "-m", "first" );

			// Branches, a merge, an author of their own and a remote give the graph lanes, avatars and remote badges to draw.
			void Commit( string file, string author, string message )
			{
				Directory.CreateDirectory( Path.Combine( root, "notes" ) );
				File.WriteAllText( Path.Combine( root, "notes", file ), message + "\n" );
				ChatChecks.RunGit( root, "add", "notes" );
				ChatChecks.RunGit( root, "-c", $"user.name={author}", "-c", "user.email=" + author.Replace( ' ', '.' ).ToLowerInvariant() + "@example.invalid", "commit", "-q", "-m", message );
			}
			ChatChecks.RunGit( root, "checkout", "-q", "-b", "feature/lightmapper" );
			Commit( "light.txt", "Sam Pavlovic", "Bake light probes for every mesh" );
			ChatChecks.RunGit( root, "checkout", "-q", "master" );
			Commit( "hair.txt", "Dana Adams", "Hair pigtails fix" );
			ChatChecks.RunGit( root, "merge", "-q", "--no-ff", "-m", "Merge feature/lightmapper", "feature/lightmapper" );
			ChatChecks.RunGit( root, "init", "-q", "--bare", origin );
			ChatChecks.RunGit( root, "remote", "add", "origin", origin );
			ChatChecks.RunGit( root, "push", "-q", "origin", "master", "feature/lightmapper" );
			ChatChecks.RunGit( root, "checkout", "-q", "-b", "ui-sentry-crash" );
			Commit( "sentry.txt", "Lorenz Junglas", "Fix UI GPU crash" );
			ChatChecks.RunGit( root, "checkout", "-q", "master" );
			File.WriteAllText( Path.Combine( root, "Modules", "Chat", "View.cs" ), $"class View\n{{\n{code.Replace( "value40 ", "renamed40 " ).Replace( "value120 ", "renamed120 " )}\n}}\n" );
			File.WriteAllText( Path.Combine( root, "README.md" ), "# Readme\n\nNew notes.\n" );
			Directory.CreateDirectory( Path.Combine( root, "Shell" ) );
			File.Copy( source, Path.Combine( root, "Shell", "Binary.dll" ) );

			var app = new LoknApplication();
			app.Load( new ShellModule() );
			var git = new GitModule();
			app.Load( git );
			// Avatars show initials only, so the check never reaches the network.
			app.Settings.Set( GitModule.Avatars, SettingsScope.Global, false );
			var store = new ConversationStore( new ProjectStore() );
			ProjectChecks.LoadProjects( app, store );
			app.Load( new ConversationsModule( store, ( context, _, _ ) => throw new InvalidOperationException( "No chats in this check." ) ) );
			var panelId = app.Panels.Open( GitModule.PanelType, input: new GitPanelInput( root ) );
			var semantics = new SemanticAdapter();
			var window = new Window
			{
				Title = "Lokn Git panel checks",
				Size = new( 1280, 850 ),
				Decorated = false,
				Transparent = true,
				Background = Theme.Current.Canvas,
				Foreground = Theme.Current.Text,
				Root = new ShellView( app ) { AppliesNativeWindowHooks = false },
				AccessibilityAdapter = semantics,
			};
			app.BindDispatcher( window.Post );
			window.Open();
			Pump( window );
			const string view = "Modules/Chat/View.cs";
			PumpUntil( window, () => Nodes( semantics ).Any( node => node.Name?.StartsWith( "Working tree, 3 unstaged", StringComparison.Ordinal ) == true )
				&& Nodes( semantics ).Any( node => node.Name?.StartsWith( "first,", StringComparison.Ordinal ) == true ) );
			CoreChecks.Check( HasText( semantics, "Branch / Tag" ) && HasText( semantics, "Commit message" ) && Nodes( semantics ).Any( node => node.Name == "Fetch every remote" )
				&& Nodes( semantics ).Any( node => node.Name == "On branch master" ),
				"The Git panel shows its icon toolbar with the branch, and the commit table under a Working tree row." );
			if ( captureDirectory is not null )
				NativeChecks.BuildCapture( window, captureDirectory, "git-graph" );

			// A file clicked in the Files side view's Changes mode or in a chat opens the panel on it.
			git.OpenPanel( app, panelId, root, Path.Combine( root, "Modules", "Chat", "View.cs" ) );
			PumpUntil( window, () => Nodes( semantics ).Any( node => node.Name == "Stage change 2" ) && HasText( semantics, "Compute( 40 )" ) );
			CoreChecks.Check( HasText( semantics, "Index" ) && HasText( semantics, "Working tree" ) && HasText( semantics, "1/2" ),
				"Opening the panel on a changed file turns it into the file's diff, on its first of two hunks." );
			if ( captureDirectory is not null )
				NativeChecks.BuildCapture( window, captureDirectory, "git-file-unified" );

			Activate( window, Find( semantics, AccessibilityRole.Button, "Split" ) );
			PumpUntil( window, () => Nodes( semantics ).Any( node => node.Name == "Split, on" ) );
			Activate( window, Find( semantics, AccessibilityRole.Button, "Next hunk" ) );
			PumpUntil( window, () => HasText( semantics, "2/2" ) );
			if ( captureDirectory is not null )
				NativeChecks.BuildCapture( window, captureDirectory, "git-file-split" );

			Activate( window, Find( semantics, AccessibilityRole.Button, "Stage change 1" ) );
			PumpUntil( window, () => HasText( semantics, "1/1" ) && !HasText( semantics, "Compute( 40 )" ) );
			CoreChecks.Check( HasText( semantics, "Compute( 120 )" ) && Nodes( semantics ).Any( node => node.Name == "Staged" ),
				"Staging one hunk leaves the other unstaged in the file's diff, and the staged side opens." );
			Activate( window, Find( semantics, AccessibilityRole.Button, "Staged" ) );
			PumpUntil( window, () => Nodes( semantics ).Any( node => node.Name == "Unstage change 1" ) && HasText( semantics, "Compute( 40 )" ) );

			Activate( window, Find( semantics, AccessibilityRole.Button, "Close the file and show the commit graph" ) );
			PumpUntil( window, () => HasText( semantics, "Commit message" ) );
			Activate( window, Nodes( semantics ).First( node => node.Name?.StartsWith( "first,", StringComparison.Ordinal ) == true ) );
			PumpUntil( window, () => Nodes( semantics ).Any( node => node.Name == $"Show the diff of {view}" ) );
			if ( captureDirectory is not null )
				NativeChecks.BuildCapture( window, captureDirectory, "git-commit" );
			Activate( window, Find( semantics, AccessibilityRole.Button, $"Show the diff of {view}" ) );
			PumpUntil( window, () => HasText( semantics, "value40" ) );
			CoreChecks.Check( !Nodes( semantics ).Any( node => node.Name?.StartsWith( "Stage change", StringComparison.Ordinal ) == true ),
				"A commit's file shows read-only, without hunk actions." );

			Activate( window, Find( semantics, AccessibilityRole.Button, "Close the file and show the commit graph" ) );
			Activate( window, Nodes( semantics ).First( node => node.Name?.StartsWith( "Working tree,", StringComparison.Ordinal ) == true ) );
			File.WriteAllText( Path.Combine( root, "late.txt" ), "written while the panel is open\n" );
			PumpUntil( window, () => Nodes( semantics ).Any( node => node.Name?.StartsWith( "Working tree, 4 unstaged", StringComparison.Ordinal ) == true ) );
			CoreChecks.Check( app.LastError is null, $"The Git panel renders and reloads without a failure. {app.LastError}" );
			window.RequestClose();
			Pump( window );
			Console.WriteLine( "PASS Git panel: icon toolbar, graph under the Working tree row, a file's diff unified and split with hunk navigation, staging one hunk, a commit's file read-only, and reload on file changes." );
		}
		finally
		{
			ChatChecks.DeleteRepository( root );
			if ( Directory.Exists( origin ) )
				ChatChecks.DeleteRepository( origin );
		}
	}

	/// <summary>Checks that a long transcript opens at its newest message and mounts only the rows near the viewport, before and after a resize.</summary>
	private static void RunLongTranscript( string? captureDirectory )
	{
		const int Count = 2000;
		var app = new LoknApplication();
		app.Load( new ShellModule() );
		var omp = new OmpModule();
		app.Load( omp );
		var store = new ConversationStore( new ProjectStore() );
		var conversation = store.CreateConversation( SettingsProfile.Personal, title: "Long conversation" );
		var paragraph = string.Concat( Enumerable.Repeat( "A long paragraph that wraps across several lines in the reading column. ", 12 ) );
		for ( var i = 0; i < Count; i++ )
		{
			var text = i == Count - 1 ? "Newest reply" : i % 2 == 0 ? $"Question {i}" : $"Answer {i}\n\n{paragraph}";
			store.AppendMessage( conversation.Id, new( Guid.NewGuid(), i % 2 == 0 ? "user" : "assistant", text, DateTimeOffset.UtcNow ), persist: false );
		}
		ProjectChecks.LoadProjects( app, store );
		app.Load( new ConversationsModule( store, ConversationsModule.BuildChat ) );
		app.Panels.Open( ConversationsModule.ChatPanelType, input: new ConversationPanelInput( conversation.Id, conversation.Profile ) );
		var semantics = new SemanticAdapter();
		var window = new Window
		{
			Title = "Lokn long transcript checks",
			Size = new( 1100, 850 ),
			Decorated = false,
			Transparent = true,
			Root = new ShellView( app ) { AppliesNativeWindowHooks = false },
			AccessibilityAdapter = semantics,
		};
		app.BindDispatcher( window.Post );
		window.Open();
		Pump( window );
		int CountMounted() => Nodes( semantics ).Count( node => node.Role == AccessibilityRole.Group && (node.Name ?? "").EndsWith( " message", StringComparison.Ordinal ) );
		CoreChecks.Check( HasText( semantics, "Newest reply" ), "A long transcript opens at its newest message." );
		CoreChecks.Check( CountMounted() < 100, $"A {Count}-message transcript mounts only rows near the viewport ({CountMounted()} mounted)." );
		var timer = System.Diagnostics.Stopwatch.StartNew();
		window.Size = new( 700, 850 );
		Pump( window );
		timer.Stop();
		CoreChecks.Check( HasText( semantics, "Newest reply" ) && CountMounted() < 100, "A resize keeps the newest message in view and the mounted rows bounded." );

		// The prompt floats over the transcript: the newest message ends above it, and the wheel over the blur beside the prompt still scrolls the transcript.
		// The resize itself jumps to the end, but the rows it rewrapped are measured over the next frames, and the end they move glides into view (MeasuredRowsState).
		var editor = Find( semantics, AccessibilityRole.TextEditor, "Message" ).Bounds;
		PumpUntil( window, () => Nodes( semantics ).First( node => node.Role == AccessibilityRole.Text && (node.Value ?? "") == "Newest reply" ).Bounds is var newest
			&& newest.Y + newest.Height <= editor.Y );
		if ( captureDirectory is not null )
			NativeChecks.BuildCapture( window, captureDirectory, "chat-long-end" );
		var (x, y) = (Nodes( semantics ).First( node => node.Name == "Conversation transcript" ).Bounds.X + ViewStyle.TrackWidth + 2, editor.Y + editor.Height / 2);
		window.PlatformInput.PointerMove( 1, PointerDevice.Mouse, x, y, KeyModifiers.None, 0.0f );
		window.PlatformInput.PointerWheel( x, y, 0, 6, KeyModifiers.None );
		Pump( window );
		CoreChecks.Check( Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Latest" ), "The wheel over the blur beside the prompt scrolls the transcript beneath it." );
		if ( captureDirectory is not null )
			NativeChecks.BuildCapture( window, captureDirectory, "chat-long-scrolled" );

		// While rows stream in, the scroll range changes between the reader's scroll steps. Scrolling up must release the end,
		// and scrolling back down must take it again, though rows arriving during the smooth scroll leave it short of the end.
		Click( window, Find( semantics, AccessibilityRole.Button, "Latest" ) );
		var streamed = 0;
		void Stream( int frames, double wheel )
		{
			for ( var frame = 0; frame < frames; frame++ )
			{
				// One row every three frames: some 600 pixels a second, faster than a model writes.
				if ( frame % 3 == 0 )
					store.AppendMessage( conversation.Id, new( Guid.NewGuid(), "assistant", $"Streamed reply {++streamed}", DateTimeOffset.UtcNow ), persist: false );
				if ( wheel != 0 )
					window.PlatformInput.PointerWheel( x, y, 0, wheel, KeyModifiers.None );
				window.Pump( 0.016 );
				Thread.Sleep( 10 );
			}
		}
		Stream( 20, 6 );
		Stream( 10, 0 );
		Pump( window );
		CoreChecks.Check( Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Latest" ) && !HasText( semantics, $"Streamed reply {streamed}" ),
			"Scrolling up while rows stream in keeps the reader's place instead of returning to the end." );
		Stream( 15, -60 );
		Stream( 15, 0 );
		Pump( window );
		CoreChecks.Check( !Nodes( semantics ).Any( node => node.Role == AccessibilityRole.Button && node.Name == "Latest" ) && HasText( semantics, $"Streamed reply {streamed}" ),
			"Scrolling back to the end while rows stream in follows the newest row again." );
		// A drag across a reply selects its text, and a right-click on it opens the message menu. The view is at the end, under the
		// streamed rows, so the newest wrapped reply is scrolled into view first.
		bool IsWrapped( AccessibilityNode node ) => node.Role == AccessibilityRole.Text && (node.Value ?? "").Contains( "wraps across", StringComparison.Ordinal );
		window.PerformAccessibilityAction( Nodes( semantics ).Last( IsWrapped ).Id, new( AccessibilityAction.ScrollIntoView ) );
		Pump( window );
		var reply = Nodes( semantics ).Last( IsWrapped );
		var line = reply.VisibleBounds ?? reply.Bounds;
		var (fromX, toX, lineY) = ((line.X + 2) * window.UiScale, (line.X + line.Width - 2) * window.UiScale, (line.Y + 8) * window.UiScale);
		// A drag targets no focusable element; the window takes presses again first in case it lost the system focus.
		UiInput.Activate( window, Nodes( semantics ) );
		window.PlatformInput.PointerMove( 1, PointerDevice.Mouse, fromX, lineY, KeyModifiers.None, 0.0f );
		window.PlatformInput.PointerPress( 1, PointerDevice.Mouse, fromX, lineY, PointerButton.Primary, KeyModifiers.None, 0.0f );
		for ( var step = 1; step <= 10; step++ )
		{
			window.PlatformInput.PointerMove( 1, PointerDevice.Mouse, fromX + (toX - fromX) * step / 10, lineY, KeyModifiers.None, 0.0f );
			window.Pump( 0.016 );
		}
		window.PlatformInput.PointerRelease( 1, PointerDevice.Mouse, toX, lineY, PointerButton.Primary, KeyModifiers.None, 0.0f );
		Pump( window );
		// Selectable text is not a text editor: its selection is the focus's selection, with no editor snapshot.
		var selected = window.Focus.Selection is { } selection ? Math.Abs( selection.Active.Offset - selection.Anchor.Offset ) : 0;
		CoreChecks.Check( selected > 10, $"A drag across a reply selects its text ({selected} characters selected)." );
		window.PlatformInput.PointerPress( 1, PointerDevice.Mouse, fromX, lineY, PointerButton.Secondary, KeyModifiers.None, 0.0f );
		window.PlatformInput.PointerRelease( 1, PointerDevice.Mouse, fromX, lineY, PointerButton.Secondary, KeyModifiers.None, 0.0f );
		Pump( window );
		// The menu opens in a native popup, a separate window whose items are not in this window's semantic tree, so the chat's open menu
		// is read instead: it must sit at the pointer, which a position left without the transcript's scroll offset put far off screen.
		var chat = app.Panels.Sessions.Select( session => session.Content ).OfType<ConversationPanel>().Select( panel => panel.View ).OfType<ChatContent>().Single();
		var opened = typeof( ChatContent ).GetField( "_messageMenu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance )!.GetValue( chat );
		var at = opened?.GetType().GetProperty( "At" )!.GetValue( opened ) as Point?;
		var viewport = Nodes( semantics ).First( node => node.Name == "Conversation transcript" ).Bounds;
		CoreChecks.Check( at is { } point && Math.Abs( viewport.X + point.X - fromX / window.UiScale ) <= 1 && Math.Abs( viewport.Y + point.Y - lineY / window.UiScale ) <= 1,
			$"A right-click on a reply opens the message menu at the pointer (menu at {at})." );

		window.RequestClose();
		Pump( window );
		Console.WriteLine( $"PASS {Count}-message virtual transcript: opens at the newest message, bounded mounted rows, resize settled in {timer.ElapsedMilliseconds} ms." );
	}

	/// <summary>Reads retained editor text to verify controller ownership across structural UI changes.</summary>
	private static string ComputeComposerText( LoknApplication app )
	{
		var chat = app.Panels.Sessions.Select( session => session.Content ).OfType<ConversationPanel>().Select( panel => panel.View ).OfType<ChatContent>().Single();
		var composer = (TextEditorController)typeof( ChatContent ).GetField( "_composer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance )!.GetValue( chat )!;
		return composer.Document.GetText();
	}

	/// <summary>Finds one current native control by role and name.</summary>
	private static AccessibilityNode Find( SemanticAdapter semantics, AccessibilityRole role, string name ) => Nodes( semantics ).Single( node => node.Role == role && node.Name == name );

	/// <summary>Gets current semantic nodes.</summary>
	private static IEnumerable<AccessibilityNode> Nodes( SemanticAdapter semantics ) => Flatten( semantics.Tree!.Root! );

	/// <summary>Enumerates one semantic subtree.</summary>
	private static IEnumerable<AccessibilityNode> Flatten( AccessibilityNode node )
	{
		yield return node;
		foreach ( var child in node.Children )
		{
			foreach ( var descendant in Flatten( child ) )
				yield return descendant;
		}
	}

	/// <summary>Checks either semantic text representation for rendered content.</summary>
	private static bool HasText( SemanticAdapter semantics, string text ) => Nodes( semantics ).Any( node => (node.Value?.Contains( text, StringComparison.Ordinal ) ?? false) || (node.Name?.Contains( text, StringComparison.Ordinal ) ?? false) );

	/// <summary>Clicks a semantic control through native pointer routing once it holds still, focusing it first so the click lands even when the window lost
	/// the system focus (<see cref="UiInput"/>).</summary>
	private static void Click( Window window, AccessibilityNode node ) => UiInput.Click( window, node, () => Pump( window ) );

	/// <summary>Activates a semantic control through its accessibility action, as assistive technology does.</summary>
	private static void Activate( Window window, AccessibilityNode node )
	{
		window.PerformAccessibilityAction( node.Id, new( AccessibilityAction.Activate ) );
		Pump( window );
	}

	/// <summary>Pumps the local fixture until an observable stream event arrives.</summary>
	private static void PumpUntil( Window window, Func<bool> predicate )
	{
		var timer = System.Diagnostics.Stopwatch.StartNew();
		while ( !predicate() )
		{
			if ( timer.Elapsed > TimeSpan.FromSeconds( 20 ) )
				throw new TimeoutException( "The local chat fixture did not reach its expected state." );
			Pump( window );
		}
		Pump( window );
	}

	/// <summary>Settles native layout after one chat action.</summary>
	private static void Pump( Window window )
	{
		for ( var frame = 0; frame < SettleFrames; frame++ )
		{
			window.Pump( 0.016 );
			Thread.Sleep( 10 );
		}
	}
}
