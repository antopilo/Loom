using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Conversations;
using Lokn.Modules.Markdown;
using Lokn.Modules.Notifications;
using Lokn.Modules.Omp;

namespace Lokn.Tests;

/// <summary>Checks the notification page, the bell's popover and the banners in a native window, with questions of every shape waiting, and captures each.</summary>
/// <remarks>Captures go to the folder after <c>--capture</c> as raw RGBA with a size file, one per view, for a reviewer to look at.</remarks>
internal static class NotificationViewChecks
{
	/// <summary>Defines the frame count used to settle layout and accessibility updates.</summary>
	private const int SettleFrames = 6;

	/// <summary>Runs the page, popover and banner checks.</summary>
	internal static void Run( string[] args )
	{
		Application.VerifyState = true;
		var captureAt = Array.IndexOf( args, "--capture" );
		var captures = captureAt >= 0 && captureAt + 1 < args.Length ? args[captureAt + 1] : null;
		var directory = Path.Combine( Path.GetTempPath(), "lokn-notification-view-" + Guid.NewGuid().ToString( "N" ) );
		var app = new LoknApplication( workspacePath: Path.Combine( directory, "workspace.json" ) );
		app.Load( new ShellModule() );
		var omp = new OmpModule();
		app.Load( omp );
		app.Load( new MarkdownModule() );
		var store = new ConversationStore();

		// Chats with a question of each shape waiting, each offered to the notification views like Conversations offers its chats'.
		var chats = new List<(string Title, Guid PanelId, ChatContent Chat)>();
		ChatContent Ask( string title, object frame )
		{
			var conversation = store.CreateConversation( SettingsProfile.Personal, title: title );
			var context = new PanelContext( app, Guid.NewGuid(), CancellationToken.None, input: null );
			var chat = ChatChecks.BuildChat( context, store, conversation.Id, omp.Service );
			chat.Session.UpdateFrame( JsonSerializer.SerializeToElement( frame ) );
			chats.Add( (title, context.Id, chat) );
			return chat;
		}

		Ask( "Migrate settings to the profile store", new
		{
			type = "extension_ui_request",
			id = "path",
			method = "ask",
			questions = new object[]
			{
				new
				{
					id = "path",
					question = "The old store keeps settings in one global JSON file at `%LOCALAPPDATA%\\Lokn\\settings.json`, and three modules (Omp, Worktrees and Remote) still read it directly on start. If I move them now, a user who downgrades to 0.4.1 loses their per-profile overrides. Which path do you want me to take for this release?",
					options = new object[]
					{
						new { label = "Move all three now and write a one-way migration that keeps a backup copy of the old file next to the new store", description = "Safest for users who stay on 0.5; a downgrade restores from the backup by hand" },
						new { label = "Keep both stores for one release", description = "Write to both, read from the new one; drop the old file in 0.6" },
						new { label = @"C:\Users\antopilo\AppData\Local\Lokn\profiles\Personal\settings.override.json", description = "Unbroken path" },
					},
					recommended = 0,
				},
			},
		} );
		Ask( "Plan the token cache change", new
		{
			type = "extension_ui_request",
			id = "plan",
			method = "ask",
			title = "Plan the token cache change",
			questions = new object[]
			{
				new { id = "storage", question = "Which storage?", options = new object[] { new { label = "SQLite", description = "One file, no new service" }, new { label = "Memory", description = "Lost on restart" } } },
				new { id = "keep", question = "Keep the old file for one release?", options = new object[] { new { label = "Yes" }, new { label = "No" } } },
				new { id = "checks", question = "Which checks should run?", multi = true, options = new object[] { new { label = "default" }, new { label = "chat" }, new { label = "worktrees" } } },
			},
		} );
		Ask( "Release 0.4.2", new
		{
			type = "extension_ui_request",
			id = "release",
			method = "form",
			html = """
				<form>
				  <p>Release settings</p>
				  <fieldset><legend>Channel</legend>
				    <label><input type="radio" name="channel" value="stable" checked> Stable</label>
				    <label><input type="radio" name="channel" value="beta"> Beta</label>
				    <label><input type="radio" name="channel" value="nightly"> Nightly</label>
				  </fieldset>
				  <label>Version <input type="text" name="version" value="0.4.2" required></label>
				  <label><input type="checkbox" name="modules" value="omp" checked> Omp</label>
				  <label><input type="checkbox" name="modules" value="git" checked> Git</label>
				  <label><input type="checkbox" name="modules" value="browser"> Browser</label>
				  <label>Target <select name="target"><option value="main" selected>main</option><option value="release">release/0.4</option></select></label>
				  <label>Notes <textarea name="notes" placeholder="Optional"></textarea></label>
				</form>
				""",
		} );
		Ask( "Pick the target branch", new
		{
			type = "extension_ui_request",
			id = "branch",
			method = "select",
			title = "Pick the target branch",
			options = Enumerable.Range( 0, 14 ).Select( index => index == 0 ? "main" : $"feature/branch-{index}" ).ToArray(),
		} );

		NotificationPrompt[] Prompts() =>
		[
			.. chats.Where( entry => entry.Chat.Session.PendingDialog is not null )
				.Select( entry => new NotificationPrompt( entry.PanelId.ToString( "N" ), entry.Title, entry.PanelId, null, entry.Chat.BuildPendingDialog, () => { } ) ),
		];
		app.Load( new FixtureModule( "prompts", builder => builder.Contribute( NotificationPromptSource.Point, new NotificationPromptSource( _ => Prompts() ) ) ) );

		var feed = app.Notifications;
		feed.Post( new( "PR #212 merged", "antopilo/loom-modules · Now Playing 1.3" ) { Source = "github", Tone = NotificationTone.Success, Project = "loom-modules" } );
		feed.Post( new( "Refactor auth flow", "Done. 4 files changed, all checks pass.\n\n- Moved the token cache\n- Added `AuthChecks`\n\n```csharp\nvar cache = TokenCache.Open( path );\n```" )
		{
			Source = "conversations.chat",
			Tone = NotificationTone.Success,
			Project = "Lokn",
			Details = ["auth-v2", "4 files · +182 −4"],
		} );
		for ( var index = 0; index < 4; index++ )
			feed.Post( new( $"Chat {index + 1}", "Finished and waiting for you" ) { Source = "conversations.chat", Tone = NotificationTone.Success, Project = "Lokn" } );
		feed.Post( new( "Download finished", "Loom-win-Setup.exe · 84 MB" ) { Source = "browser.page" } );
		feed.Post( new( "Updates", "Loom 0.4.17 is ready. Restart Loom to use it." ) { Source = "updates", Actions = [new( "Restart now", LoknLinks.Create( "updates", "restart" ) )] } );
		feed.Post( new( "GitHub", "Checks passed on antopilo/Lokn#88 Compact question card" ) { Source = "github", Tone = NotificationTone.Success, Project = "antopilo/Lokn", Details = ["main", "8d01e4b"] } );
		feed.Post( new( "GitHub", "2 checks failed on antopilo/Lokn#87 Profile store" ) { Source = "github", Tone = NotificationTone.Failure, Project = "antopilo/Lokn", Details = ["main", "3f2c1a9"] } );
		foreach ( var (title, panelId, _) in chats )
			feed.Post( new( title, "Your input is needed" ) { PanelId = panelId, Source = "conversations.chat", Tone = NotificationTone.Attention, Project = "Lokn" } );

		app.Load( new NotificationsModule() );
		var semantics = new SemanticAdapter();
		var window = new Window
		{
			Title = "Lokn notification checks",
			Size = new( 1440, 900 ),
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
		void Capture( string name )
		{
			Pump( window );
			if ( captures is not null )
				NativeChecks.BuildCapture( window, captures, name );
		}

		// The page: filters, the list and the reading pane; each question shows in the reading pane when its row is selected.
		NotificationsModule.OpenFeed( app );
		PumpUntil( window, semantics, () => HasNode( semantics, AccessibilityRole.Group, "Filters" ), "The Notifications page opens with its filter rail." );
		CoreChecks.Check( app.LastError is null, $"The page builds without contained failures: {app.LastError}" );
		CoreChecks.Check( HasName( semantics, "Waiting for you, 4" ) && HasName( semantics, "Chats, 9" ) && HasName( semantics, "GitHub, 3" ) && HasName( semantics, "Lokn, 9" ),
			"The rail counts what waits, each source and each project." );
		foreach ( var (title, file) in new[] { (chats[0].Title, "page-ask"), (chats[1].Title, "page-steps"), (chats[2].Title, "page-form"), (chats[3].Title, "page-select") } )
		{
			Click( window, semantics, node => node.Role == AccessibilityRole.Button && node.Name?.Contains( title + ", Is waiting for you", StringComparison.Ordinal ) == true, title );
			PumpUntil( window, semantics, () => HasNode( semantics, AccessibilityRole.Group, "Tool question" ) && HasName( semantics, title + " asks" ), $"Selecting {title} shows its question in the reading pane." );
			CoreChecks.Check( app.LastError is null, $"{title}'s question builds without contained failures: {app.LastError}" );
			Capture( file );
			CoreChecks.Check( HasName( semantics, title + " asks" ) && chats.Count( entry => HasName( semantics, entry.Title + " asks" ) ) == 1, $"The reading pane still shows {title}'s question alone after it settled." );
		}

		Click( window, semantics, node => node.Role == AccessibilityRole.Button && node.Name?.Contains( "Refactor auth flow, Done.", StringComparison.Ordinal ) == true, "Refactor auth flow" );
		PumpUntil( window, semantics, () => HasText( semantics, "Moved the token cache" ), "Selecting a chat's reply shows its Markdown in the reading pane." );
		Capture( "page-message" );

		Click( window, semantics, node => node.Role == AccessibilityRole.Button && node.Name == "GitHub, 3", "GitHub filter" );
		PumpUntil( window, semantics, () => !HasText( semantics, "Download finished" ) && HasText( semantics, "Profile store" ), "The GitHub filter keeps only GitHub's notifications." );
		Capture( "page-github" );
		Click( window, semantics, node => node.Role == AccessibilityRole.Button && node.Name?.StartsWith( "All,", StringComparison.Ordinal ) == true, "All filter" );

		// The popover under the bell.
		Click( window, semantics, node => node.Role == AccessibilityRole.Button && node.Name?.StartsWith( "Notifications, ", StringComparison.Ordinal ) == true && node.Name.EndsWith( " unread", StringComparison.Ordinal ), "bell" );
		PumpUntil( window, semantics, () => HasNode( semantics, AccessibilityRole.Dialog, "Notifications" ), "The bell opens its popover." );
		CoreChecks.Check( app.LastError is null, $"The popover builds without contained failures: {app.LastError}" );
		Capture( "popover" );
		CoreChecks.Check( HasNode( semantics, AccessibilityRole.Button, "Restart now" ), "A notification's own buttons show on its row in the popover, without opening it." );
		Click( window, semantics, node => node.Role == AccessibilityRole.Button && node.Name?.StartsWith( "Waiting", StringComparison.Ordinal ) == true, "Waiting tab" );
		PumpUntil( window, semantics, () => !HasText( semantics, "Download finished" ), "The Waiting tab keeps only what waits." );
		Capture( "popover-waiting" );
		UiInput.PressKey( window, Nodes( semantics ).First( node => node.Role == AccessibilityRole.Dialog ), Key.Escape, KeyModifiers.None );
		PumpUntil( window, semantics, () => !HasNode( semantics, AccessibilityRole.Dialog, "Notifications" ), "Escape closes the popover." );

		// Banners: a chat's question with its card, and a plain update.
		feed.Post( new( "GitHub", "Checks passed on antopilo/Lokn#89 Banner" ) { Source = "github", Tone = NotificationTone.Success, Project = "antopilo/Lokn" } );
		feed.Post( new( chats[0].Title, "Your input is needed" ) { PanelId = chats[0].PanelId, Source = "conversations.chat", Tone = NotificationTone.Attention } );
		PumpUntil( window, semantics, () => HasNode( semantics, AccessibilityRole.Group, "New notifications" ) && HasName( semantics, chats[0].Title + " is waiting for you" ), "A banner announces each notification, a chat's question answered in it." );
		CoreChecks.Check( app.LastError is null, $"The banners build without contained failures: {app.LastError}" );
		Capture( "banners" );

		while ( Nodes( semantics ).FirstOrDefault( node => node.Role == AccessibilityRole.Button && node.Name == "Close notification" ) is { } close )
		{
			Click( window, semantics, node => node.Id == close.Id, "Close notification" );
			Pump( window );
		}

		// Narrow: the list alone with its filters as tabs, a picked notification in its place with a way back.
		window.Size = new( 560, 900 );
		PumpUntil( window, semantics, () => !HasNode( semantics, AccessibilityRole.Group, "Filters" ) && HasNode( semantics, AccessibilityRole.Group, "Show" ),
			"A narrow page hides the rail and shows its filters as tabs over the list." );
		CoreChecks.Check( HasNode( semantics, AccessibilityRole.Button, "Restart now" ), "A notification's own buttons show on its row in the page's list." );
		Capture( "page-narrow" );
		Click( window, semantics, node => node.Role == AccessibilityRole.Button && node.Name?.Contains( "Refactor auth flow, Done.", StringComparison.Ordinal ) == true, "Refactor auth flow" );
		PumpUntil( window, semantics, () => HasNode( semantics, AccessibilityRole.Button, "Back to notifications" ) && !HasNode( semantics, AccessibilityRole.Group, "Show" ),
			"A narrow page shows the picked notification in the list's place, with a way back." );
		Capture( "page-narrow-reading" );
		Click( window, semantics, node => node.Role == AccessibilityRole.Button && node.Name == "Back to notifications", "Back" );
		PumpUntil( window, semantics, () => HasNode( semantics, AccessibilityRole.Group, "Show" ), "Back returns to the list." );
		window.Size = new( 1060, 900 );
		PumpUntil( window, semantics, () => HasNode( semantics, AccessibilityRole.Group, "Show" ) && HasNode( semantics, AccessibilityRole.Group, "Notification" ),
			"A medium page shows the list with filter tabs beside the reading pane." );
		Capture( "page-medium" );

		window.RequestClose();
		Pump( window );
		Console.WriteLine( "PASS notification page with filters and reading pane, popover with tabs, banners with questions answered in place." );
	}

	/// <summary>Clicks the first node a predicate finds, through native pointer input.</summary>
	private static void Click( Window window, SemanticAdapter semantics, Func<AccessibilityNode, bool> match, string what ) =>
		UiInput.Click( window, () => Nodes( semantics ).FirstOrDefault( match ), () => Pump( window ), what );

	/// <summary>Reports whether a node with the role and exact name shows.</summary>
	private static bool HasNode( SemanticAdapter semantics, AccessibilityRole role, string name ) => Nodes( semantics ).Any( node => node.Role == role && node.Name == name );

	/// <summary>Reports whether a node with the exact name shows.</summary>
	private static bool HasName( SemanticAdapter semantics, string name ) => Nodes( semantics ).Any( node => node.Name == name );

	/// <summary>Checks semantic text for a rendered fragment.</summary>
	private static bool HasText( SemanticAdapter semantics, string fragment ) =>
		Nodes( semantics ).Any( node => (node.Name?.Contains( fragment, StringComparison.Ordinal ) ?? false) || (node.Value?.Contains( fragment, StringComparison.Ordinal ) ?? false) );

	/// <summary>Enumerates the semantic tree.</summary>
	private static IEnumerable<AccessibilityNode> Nodes( SemanticAdapter semantics ) => semantics.Tree?.Root is { } root ? Flatten( root ) : [];

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

	/// <summary>Pumps until a condition holds, failing with what shows after a timeout.</summary>
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
