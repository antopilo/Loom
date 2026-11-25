using Goo;
using Lokn.Core;
using Lokn.Modules.Browser;
using Lokn.Modules.Conversations;
using Lokn.Modules.Overview;
using Lokn.Modules.Projects;
using Lokn.Modules.Workspaces;

namespace Lokn.Tests;

/// <summary>Checks a project's overview: its chats and tasks with the working ones first, the activity and reports of its own chats only, and its designs.</summary>
internal static class OverviewChecks
{
	/// <summary>Runs the overview checks headlessly, with fixture chats instead of Omp.</summary>
	internal static async Task RunAsync()
	{
		var root = Path.Combine( Path.GetTempPath(), "Lokn", "overview-checks-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( Path.Combine( root, "app", ".lokn", "design", "landing", "b" ) );
		Directory.CreateDirectory( Path.Combine( root, "other" ) );
		File.WriteAllText( Path.Combine( root, "app", ".lokn", "design", "landing", "index.html" ), "<p>a</p>" );
		var queue = new List<Action>();
		var app = new LoknApplication();
		app.BindDispatcher( action =>
		{
			lock ( queue )
				queue.Add( action );
			return true;
		} );
		try
		{
			var profile = app.Settings.ActiveProfile;
			var store = new ConversationStore( new ProjectStore() );
			var project = store.ProjectStore.Create( profile, "App", Path.Combine( root, "app" ) );
			var other = store.ProjectStore.Create( profile, "Other", Path.Combine( root, "other" ) );
			app.Load( new WorkspacesModule( Path.Combine( root, "workspaces.json" ) ) );
			app.Load( new ProjectsModule( store.ProjectStore ) );
			var conversations = new ConversationsModule( store, ( context, host, id ) => new ChatFixture() ) { Dependencies = ["workspaces", "projects"] };
			CoreChecks.Check( app.Load( conversations ), "Conversations loads for the overview." );
			var browser = new BrowserModule( root );
			CoreChecks.Check( app.Load( browser ) && app.Load( new OverviewModule() ), "The overview loads after Conversations and Browser." );

			var idle = store.CreateConversation( profile, project.Id, "Idle chat" );
			var busy = store.CreateConversation( profile, project.Id, "Busy chat" );
			var task = store.CreateConversation( profile, project.Id, "Plan the launch", hasChat: false );
			var elsewhere = store.CreateConversation( profile, other.Id, "Other project's chat" );
			store.SetWorking( busy.Id, true );
			app.Notifications.Post( new NotificationRequest( "Busy chat needs you", "Keep the fallback?" )
			{
				Link = LoknLinks.Create( "conversations", "open", ChatRef.Local( busy.Id ).ToString() ),
			} );
			app.Notifications.Post( new NotificationRequest( "Elsewhere finished", "Done." )
			{
				Link = LoknLinks.Create( "conversations", "open", ChatRef.Local( elsewhere.Id ).ToString() ),
			} );
			var report = Path.Combine( root, "bench.html" );
			File.WriteAllText( report, "<p>bench</p>" );
			browser.RecordReport( report, "Benchmark", new AgentToolContext( app, null, CancellationToken.None ) { Conversation = idle.Id } );
			// Showing the same file again, spelled with the other slashes, lists it once; twice would give the overview two cards with one key.
			browser.RecordReport( report.Replace( '\\', '/' ), "Benchmark", new AgentToolContext( app, null, CancellationToken.None ) { Conversation = idle.Id } );
			CoreChecks.Check( browser.Reports.Count == 1, "A report shown twice is listed once." );
			var listed = File.ReadAllText( Path.Combine( browser.ReportsPath, "reports.json" ) );
			Directory.CreateDirectory( Path.Combine( root, "older", "reports" ) );
			File.WriteAllText( Path.Combine( root, "older", "reports", "reports.json" ), listed.Replace( "[", "[" + listed.Trim()[1..^1] + "," ) );
			CoreChecks.Check( new BrowserModule( Path.Combine( root, "older" ) ).Reports.Count == 1, "A list saved with a report twice reads it once." );
			var stray = Path.Combine( root, "stray.html" );
			File.WriteAllText( stray, "<p>stray</p>" );
			browser.RecordReport( stray, "Stray report", new AgentToolContext( app, null, CancellationToken.None ) { Conversation = elsewhere.Id } );

			var sidebar = app.Slots.Get( ShellSlots.Sidebar ).Single( item => item.ModuleId == "conversations" ).Content;
			var name = Descendants( sidebar.Build() ).OfType<Button>().SingleOrDefault( button => button.AccessibilityName == "App, open overview" );
			CoreChecks.Check( name is not null, "A project's name in the sidebar links to its overview while the Overview module is loaded." );
			var workspaces = app.GetModule<WorkspacesModule>();
			var scratch = app.Panels.Open( "conversations.chat" );
			// An overview opened as a panel in another layout, as 0.4.13 did, stays in that layout.
			var older = app.Panels.Open( OverviewModule.PanelType, input: (Guid?)project.Id, hostId: LoknHost.LocalId );
			var link = LoknLinks.Create( "overview", "project", project.Id.ToString( "N" ) );
			// Clicking the name follows the overview link.
			name!.OnClick!();
			var overview = app.Panels.ActivePanelId;
			CoreChecks.Check( workspaces.CurrentSession == "overview:" + project.Id.ToString( "N" ) && app.Panels.Panes.Count == 1 && !app.Panels.IsShown( scratch )
				&& !app.Panels.IsShown( older ) && overview is { } shown && shown != older && app.Panels.Panels.Single( panel => panel.Id == shown ).TypeId == OverviewModule.PanelType,
				"The overview link opens the project's overview as its own page: its own session, with the overview alone, full window." );
			conversations.OpenHome( app );
			app.OpenLink( link );
			CoreChecks.Check( app.Panels.Panels.Count( panel => panel.TypeId == OverviewModule.PanelType ) == 2 && app.Panels.ActivePanelId == overview && app.Panels.Panes.Count == 1,
				"Opening the overview again comes back to the same page." );
			app.Settings.ToggleProfile();
			app.Settings.ToggleProfile();
			CoreChecks.Check( workspaces.CurrentSession == "overview:" + project.Id.ToString( "N" ) && app.Panels.ActivePanelId == overview,
				"Switching profile away and back returns to the overview page." );
			var page = app.Panels.FindContent<ProjectOverviewContent>()!.Value.Content;
			for ( var tries = 0; tries < 50 && !Texts( page.Build() ).Contains( "landing" ); tries++ )
			{
				await Task.Delay( 50 );
				Action[] pending;
				lock ( queue )
				{
					pending = [.. queue];
					queue.Clear();
				}
				foreach ( var action in pending )
					action();
			}

			var texts = Texts( page.Build() );
			var order = texts.IndexOf( "Busy chat" );
			CoreChecks.Check( order >= 0 && order < texts.IndexOf( "Idle chat" ) && texts.Contains( "Working" ) && texts.Contains( "Plan the launch" ),
				"The overview lists the project's chats and tasks, the working chat first." );
			CoreChecks.Check( !texts.Contains( "Other project's chat" ) && !texts.Contains( "Elsewhere finished" ) && !texts.Contains( "Stray report" ),
				"Chats, notifications and reports of other projects stay off the overview." );
			CoreChecks.Check( texts.Contains( "Benchmark" ), "The overview's reports list the ones its chats showed." );
			CoreChecks.Check( ProjectOverviewContent.ParseGitHubRepository( "git@github.com:antopilo/loom.git" ) == "antopilo/loom"
				&& ProjectOverviewContent.ParseGitHubRepository( "https://github.com/antopilo/loom/" ) == "antopilo/loom"
				&& ProjectOverviewContent.ParseGitHubRepository( "https://gitlab.com/antopilo/loom.git" ) is null
				&& ProjectOverviewContent.ParseGitHubRepository( "" ) is null,
				"The overview reads owner/name from a GitHub remote over https or ssh, and nothing from other hosts." );
			CoreChecks.Check( texts.Contains( "landing" ) && texts.Contains( "Design  ·  1 variant" ), "The overview lists the project's designs with their variants." );
		}
		finally
		{
			app.BindDispatcher( null );
			// The page reads the project's branch and remote with git, which may still hold the folder for a moment.
			for ( var tries = 0; ; tries++ )
			{
				try
				{
					Directory.Delete( root, recursive: true );
					break;
				}
				catch ( IOException ) when ( tries < 40 )
				{
					await Task.Delay( 50 );
				}
			}
		}
		Console.WriteLine( "PASS project overview: chats that need the user first then the recent ones, reports of the project's own chats, its designs, and its GitHub repository from its remote." );
	}

	/// <summary>Lists the text of a declaration and every blob below it.</summary>
	private static List<string> Texts( Blob blob ) => [.. Descendants( blob ).OfType<Text>().Select( text => text.Content ).OfType<string>()];

	/// <summary>Lists a declaration and every blob below it, through every row of its virtual lists and the sidebar's profile slide, which holds its rows in its input.</summary>
	private static IEnumerable<Blob> Descendants( Blob blob )
	{
		yield return blob;
		if ( blob.GetType().GetProperty( "Children" )?.GetValue( blob ) is IEnumerable<Blob> children )
		{
			foreach ( var child in children.SelectMany( Descendants ) )
				yield return child;
		}
		if ( blob is Virtual list )
		{
			foreach ( var child in CoreChecks.BuildRows( list ).SelectMany( Descendants ) )
				yield return child;
		}
		var fields = blob.GetType().GetFields( System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public );
		foreach ( var slide in fields.Select( field => field.GetValue( blob ) ).OfType<ProfileSlideInput>() )
		{
			foreach ( var child in Descendants( slide.Content ) )
				yield return child;
		}
	}

	/// <summary>Stands in for a chat panel.</summary>
	private sealed class ChatFixture : PanelContent
	{
		/// <summary>Builds nothing.</summary>
		public override Blob Build() => new Text( "" );
	}
}
