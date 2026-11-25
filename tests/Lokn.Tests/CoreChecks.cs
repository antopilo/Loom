using Goo;
using Lokn.Core;

namespace Lokn.Tests;

/// <summary>Exercises module ownership, command failures, and docking invariants.</summary>
internal static class CoreChecks
{
	/// <summary>Defines the deterministic docking stress operation count.</summary>
	private const int StressOperations = 400;

	/// <summary>Runs the core checks without opening a native window.</summary>
	internal static async Task RunAsync()
	{
		CheckAtomicRegistration();
		CheckModuleOwnership();
		await CheckCommandsAsync();
		CheckDocking();
		CheckMaximize();
		CheckAttention();
		CheckNotifications();
		CheckExtensions();
		CheckDockingStress();
		Console.WriteLine( "PASS module registration, dependencies, unload/re-enable, cancellation, commands, docking invariants, a maximized panel, panel attention, the notification feed, slots, extension points, services, and routes." );
	}

	/// <summary>Verifies failed registration and startup leave no contributions behind.</summary>
	private static void CheckAtomicRegistration()
	{
		var app = new LoknApplication();
		Check( !app.Load( new FixtureModule( "broken", builder =>
		{
			builder.Panel( "panel", "Broken", _ => new FixtureContent() );
			throw new InvalidOperationException( "registration failure" );
		} ) ), "Registration failure is contained." );
		Check( app.Commands.Items.Count == 0 && app.Panels.Types.Count == 0 && app.Modules.Count == 0, "Registration rolls back." );
		var startup = new FixtureModule( "startup", builder => builder.Panel( "panel", "Startup", _ => new FixtureContent() ), context =>
		{
			context.App.Panels.Open( "startup.panel" );
			throw new InvalidOperationException( "startup failure" );
		} );
		Check( !app.Load( startup ) && app.Panels.Panels.Count == 0 && app.Commands.Items.Count == 0, "Startup panels roll back." );
		Check( !app.Load( new FixtureModule( "duplicate", builder =>
		{
			builder.Panel( "panel", "First", _ => new FixtureContent() );
			builder.Panel( "panel", "Second", _ => new FixtureContent() );
		} ) ), "Duplicate contributions are rejected." );
		Check( app.Panels.Types.Count == 0, "Duplicate registration is atomic." );
		app.Shutdown();
	}

	/// <summary>Verifies unload removes only owned contributions and protects required modules.</summary>
	private static void CheckModuleOwnership()
	{
		var app = new LoknApplication();
		PanelContext? panelContext = null;
		var content = new FixtureContent();
		var queued = new List<Action>();
		var applied = 0;
		app.BindDispatcher( action =>
		{
			queued.Add( action );
			return true;
		} );
		var first = new FixtureModule( "first", builder => builder.Panel( "panel", "First", context =>
		{
			panelContext = context;
			return content;
		} ) );
		Check( app.Load( first ), "First module loads." );
		Check( app.Load( new FixtureModule( "second", builder => builder.Panel( "panel", "Second", _ => new FixtureContent() ) ) ), "Independent module loads." );
		Check( app.Load( new FixtureModule( "dependent", _ => { } )
		{
			Dependencies =
			[
				"first",
			],
		} ), "Dependency resolves." );
		var input = new object();
		var panel = app.Panels.Open( "first.panel", input: input );
		Check( ReferenceEquals( panelContext!.Input, input ), "Feature-defined panel input reaches its factory." );
		var other = app.Panels.Open( "second.panel" );
		Check( panelContext!.Post( () => applied++ ), "A live panel can post UI work." );
		Check( !app.Unload( "first" ), "A required module cannot unload." );
		Check( app.Unload( "dependent" ) && app.Unload( "first" ), "Dependent unloads before dependency." );
		queued.Single()();
		Check( applied == 0 && !panelContext!.Post( () => applied++ ), "Closed panels refuse queued and new UI work." );
		Check( panelContext!.Lifetime.IsCancellationRequested && content.CloseCount == 1, "Unload cancels and closes the panel exactly once." );
		Check( app.Panels.Panels.Single().Id == other && !app.Panels.Close( panel ), "Unrelated panel survives unload." );
		Check( app.Commands.Items.All( command => command.ModuleId == "second" ), "Owned commands disappear." );
		Check( app.Enable( "first" ), "Disabled module can be enabled again." );
		Check( !app.Load( new FixtureModule( "missing", _ => { } )
		{
			Dependencies =
			[
				"absent",
			],
		} ), "Missing dependencies are rejected." );
		app.Shutdown();
		Check( app.Modules.Count == 0 && app.Panels.Panels.Count == 0, "Shutdown removes all owned sessions." );
	}

	/// <summary>Verifies availability, failure isolation, duplicate shortcuts, and in-flight cancellation.</summary>
	private static async Task CheckCommandsAsync()
	{
		var app = new LoknApplication();
		var count = 0;
		var pending = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
		Check( app.Load( new FixtureModule( "commands", builder => builder
			.Command( "ok", "Increment", _ => count++, new( Key.L, KeyModifiers.Primary ) )
			.Command( "disabled", "Disabled", _ => count++, canExecute: _ => false )
			.Command( "predicate", "Bad predicate", _ => count++, canExecute: _ => throw new InvalidOperationException() )
			.Command( "fail", "Fail", _ => throw new InvalidOperationException( "command failure" ) )
			.CommandAsync( "wait", "Wait", async context =>
			{
				pending.TrySetResult();
				await Task.Delay( Timeout.Infinite, context.Lifetime );
			} ) ) ), "Commands register." );
		Check( await app.Commands.ExecuteAsync( "commands.ok" ) && count == 1, "Command executes." );
		Check( !await app.Commands.ExecuteAsync( "commands.disabled" ) && !app.Commands.CanExecute( "commands.predicate" ), "Unavailable commands stay disabled." );
		Check( !await app.Commands.ExecuteAsync( "commands.fail" ) && app.LastError!.Contains( "command failure" ), "Command exceptions are contained." );
		Check( app.Commands.FindShortcut( Key.L, KeyModifiers.Primary ) == "commands.ok", "Global shortcut shares the command registry." );
		Check( !app.Load( new FixtureModule( "conflict", builder => builder.Command( "x", "Conflict", _ => { }, new( Key.L, KeyModifiers.Primary ) ) ) ), "Ambiguous shortcuts are rejected." );
		Check( !app.Load( new FixtureModule( "reserved", builder => builder.Command( "x", "Reserved", _ => { }, new( Key.K, KeyModifiers.Primary ) ) ) ), "Feature shortcuts cannot shadow the palette." );
		var running = app.Commands.ExecuteAsync( "commands.wait" );
		await pending.Task;
		Check( !app.Commands.CanExecute( "commands.wait" ), "A running command cannot reenter." );
		app.Unload( "commands" );
		Check( !await running && app.Commands.Items.Count == 0, "Unload cancels in-flight commands and unregisters lookup." );
		app.Shutdown();
	}

	/// <summary>Verifies model identity, split collapse, active selection, and final empty workspace behavior.</summary>
	private static void CheckDocking()
	{
		var app = CreateWorkspace();
		var workspace = app.Panels;
		var first = workspace.Open( "fixture.panel" );
		var second = workspace.Open( "fixture.panel" );
		var instance = workspace.Sessions.First( session => session.Info.Id == first ).Content;
		Check( workspace.Panes.Count == 2, "Opening another panel creates a visible split instead of a tab." );
		var firstPane = workspace.Panes.Single( pane => pane.PanelId == first );
		var secondPane = workspace.Panes.Single( pane => pane.PanelId == second );
		Check( workspace.Move( first, secondPane.Id ), "Center docking swaps occupied panes." );
		Check( workspace.Panes.Single( pane => pane.Id == firstPane.Id ).PanelId == second, "The target panel swaps into the source pane." );
		Check( workspace.Panes.Single( pane => pane.Id == secondPane.Id ).PanelId == first, "The moved panel occupies the target pane." );
		Check( !workspace.Move( first, secondPane.Id ), "Dropping a pane onto itself is a no-op." );
		workspace.Move( first, firstPane.Id );

		var target = workspace.SplitActive( SplitAxis.Horizontal );
		Check( workspace.Move( first, target ), "Panel moves into an empty split." );
		Check( ReferenceEquals( instance, workspace.Sessions.First( session => session.Info.Id == first ).Content ), "Panel model identity survives moving." );
		Check( workspace.Move( second, target, PanelPlacement.Bottom ), "Panel docks onto an edge." );
		Check( workspace.Panes.Count == 2, "Moving a panel collapses its old pane." );
		var split = (DockSplit)workspace.Root;
		Check( workspace.UpdateSplitRatio( split.Id, 20 ) && ((DockSplit)workspace.Root).Ratio < 1, "Ratios are bounded." );
		Check( !workspace.UpdateSplitRatio( split.Id, double.NaN ), "Nonfinite ratios are rejected." );
		workspace.Close( first );
		Check( workspace.Root is DockPane && workspace.Panels.Single().Id == second, "Closing promotes the surviving sibling." );
		workspace.Close( second );
		Check( workspace.Root is DockPane { PanelId: null } && workspace.ActivePanelId is null, "Closing the final panel preserves a usable empty pane." );
		CheckInvariants( workspace );
		app.Shutdown();
	}

	/// <summary>Verifies a maximized panel hides the others without closing them, and that opening, closing or focusing another panel brings them back.</summary>
	private static void CheckMaximize()
	{
		var app = CreateWorkspace();
		var workspace = app.Panels;
		var only = workspace.Open( "fixture.panel" );
		Check( !workspace.ToggleMaximize( only ) && workspace.MaximizedPanelId is null, "A lone panel has nothing to maximize." );

		var other = workspace.Open( "fixture.panel" );
		Check( workspace.ToggleMaximize( only ) && workspace.MaximizedPanelId == only, "A panel in a split maximizes." );
		Check( workspace.IsShown( only ) && !workspace.IsShown( other ) && workspace.Panels.Count == 2 && workspace.Panes.Count == 2,
			"The panels it hides stay open in their panes and count as not on screen." );
		Check( workspace.ActivePanelId == only, "Maximizing focuses the panel." );
		Check( !workspace.ToggleMaximize( only ) && workspace.MaximizedPanelId is null && workspace.IsShown( other ), "Maximizing again restores the split." );
		Check( !workspace.ToggleMaximize( Guid.NewGuid() ), "An unknown panel cannot be maximized." );

		workspace.ToggleMaximize( only );
		workspace.Activate( only );
		Check( workspace.MaximizedPanelId == only, "Focusing the maximized panel keeps it maximized." );
		workspace.Activate( other );
		Check( workspace.MaximizedPanelId is null && workspace.IsShown( only ) && workspace.IsShown( other ), "Focusing another panel restores the split." );

		workspace.ToggleMaximize( only );
		var third = workspace.Open( "fixture.panel" );
		Check( workspace.MaximizedPanelId is null && workspace.IsShown( third ), "Opening a panel restores the split, so it is not opened hidden." );

		workspace.ToggleMaximize( third );
		workspace.Close( third );
		Check( workspace.MaximizedPanelId is null && workspace.IsShown( only ) && workspace.IsShown( other ), "Closing the maximized panel restores the others." );
		CheckInvariants( workspace );
		app.Shutdown();
	}

	/// <summary>Verifies slots, extension points, services and routes belong to their module: shared, ordered, checked at load, and withdrawn at unload.</summary>
	private static void CheckExtensions()
	{
		var app = new LoknApplication();
		var started = false;
		var isStartedFirst = false;
		var nav = new FixtureContent();
		var top = new FixtureContent();
		var opened = new List<string>();
		CoreChecks.Check( app.Load( new FixtureModule( "nav", builder => builder
			.Slot( ShellSlots.Sidebar, 10, _ =>
			{
				isStartedFirst = started;
				return nav;
			} )
			.ExtensionPoint<string>( "names" )
			.Service<IComparer<string>>( StringComparer.Ordinal )
			.Route( "chat", request => opened.Add( request.Path + "|" + request.Query( "q" ) ) ), _ => started = true ) ), "A module can declare slots, points, services and routes." );
		Check( app.Load( new FixtureModule( "other", builder => builder
			.Slot( ShellSlots.Sidebar, 0, _ => top )
			.Contribute( "nav.names", "a" ) ) ), "A second module can fill the same slot and contribute to the first module's point." );
		Check( isStartedFirst && app.Slots.Get( ShellSlots.Sidebar ).Select( item => item.Content ).SequenceEqual( [top, nav] ) && app.Panels.Panels.Count == 0,
			"Slot content is created after its module starts, sorted by order, and is not a panel." );
		Check( app.Extensions.Get<string>( "nav.names" ).SequenceEqual( ["a"] ), "Contributions are read by point." );
		Check( !app.Load( new FixtureModule( "wrong", builder => builder.Contribute( "nav.names", new object() ) ) ) && app.LastError!.Contains( "takes String" ),
			"A contribution of the wrong type is refused at load." );
		Check( !app.Load( new FixtureModule( "rival", builder => builder.Service<IComparer<string>>( StringComparer.OrdinalIgnoreCase ) ) ) && app.Services.Get<IComparer<string>>() == StringComparer.Ordinal,
			"A second publisher of a service is refused and the first keeps it." );
		app.OpenLink( LoknLinks.Create( "nav", "chat", "a b", "c" ) );
		app.OpenLink( new Uri( "lokn://nav/chat/x?q=1%202" ) );
		Check( opened.SequenceEqual( ["a b/c|", "x|1 2"] ), "A lokn:// link reaches its module's route with the rest of its path and query unescaped." );
		app.ClearError();
		app.OpenLink( new Uri( "lokn://nav/missing" ) );
		Check( app.LastError?.Contains( "No loaded module opens" ) == true, "A link no route opens is reported, not sent to the browser." );
		Check( app.Unload( "other" ) && top.CloseCount == 1 && app.Slots.Get( ShellSlots.Sidebar ).Single().Content == nav && app.Extensions.Get<string>( "nav.names" ).Count == 0,
			"Unloading a module closes its slot content and withdraws its contributions." );
		Check( app.Unload( "nav" ) && nav.CloseCount == 1 && app.Services.Get<IComparer<string>>() is null && app.Extensions.Points.Count == 0, "Unloading withdraws services and points." );
		app.Shutdown();
	}

	/// <summary>Verifies a waiting panel is announced once per request and cleared by activation or closing.</summary>
	private static void CheckAttention()
	{
		var app = CreateWorkspace();
		var workspace = app.Panels;
		var first = workspace.Open( "fixture.panel" );
		var second = workspace.Open( "fixture.panel" );
		var requested = new List<Guid>();
		var cleared = new List<Guid>();
		workspace.AttentionRequested += ( panel, _ ) => requested.Add( panel.Id );
		workspace.AttentionCleared += cleared.Add;
		workspace.RequestAttention( first, new( "Chat", "Question" ) );
		workspace.RequestAttention( first, new( "Chat", "Finished" ) );
		Check( requested.SequenceEqual( [first] ) && workspace.GetAttention( first )?.Message == "Finished", "A panel already waiting updates its message without announcing again." );
		Check( !workspace.RequestAttention( Guid.NewGuid(), new( "Gone", "Question" ) ), "Only open panels can ask for the user." );
		workspace.Activate( first );
		Check( workspace.GetAttention( first ) is null && cleared.SequenceEqual( [first] ), "Activating a waiting panel acknowledges it." );
		workspace.RequestAttention( first, new( "Chat", "Question" ) );
		Check( requested.Count == 2, "A new request after acknowledgement is announced again." );
		workspace.RequestAttention( second, new( "Chat", "Question" ) );
		workspace.Close( second );
		Check( workspace.GetAttention( second ) is null && cleared[^1] == second, "Closing a waiting panel withdraws its request." );
		app.Shutdown();
	}

	/// <summary>Verifies the feed's read state, capacity, and that it survives a restart without overwriting a file it could not read.</summary>
	private static void CheckNotifications()
	{
		var directory = Path.Combine( Path.GetTempPath(), "lokn-notifications-" + Guid.NewGuid().ToString( "N" ) );
		var path = Path.Combine( directory, "notifications.json" );
		try
		{
			var panel = Guid.NewGuid();
			var app = new LoknApplication( notificationsPath: path );
			var feed = app.Notifications;
			var first = feed.Post( new( "Chat", "Finished" ) { PanelId = panel } );
			feed.Post( new( "Chat", "Asks a question" ) { PanelId = panel } );
			var posted = new List<LoknNotification>();
			feed.Posted += posted.Add;
			var other = feed.Post( new( "GitHub", "Review requested" )
			{
				Link = new( "https://github.com/owner/repo/pull/1" ),
				Kind = "github.review",
				Actions = [new( "Approve", LoknLinks.Create( "github", "approve", "1" ) )],
				Tone = NotificationTone.Attention,
				Project = "owner/repo",
				Details = ["main", "3f2c1a9"],
			} );
			Check( posted.SingleOrDefault() == other, "Posting announces the recorded notification once." );
			Check( feed.Items[0] == other && feed.UnreadCount == 3, "The newest notification is first and new ones are unread." );
			Check( feed.MarkPanelRead( panel ) == 2 && feed.UnreadCount == 1, "Looking at a panel reads only its notifications." );
			Check( !feed.MarkRead( first.Id ) && feed.MarkRead( other.Id ) && feed.UnreadCount == 0, "Marking read reports only a change." );
			Check( feed.Remove( first.Id ) && feed.Items.Count == 2, "A notification can be removed." );

			var reloaded = new LoknApplication( notificationsPath: path ).Notifications;
			Check( reloaded.Items.Select( item => (item.Id, item.IsRead, item.PanelId, item.Url, item.Kind) ).SequenceEqual( feed.Items.Select( item => (item.Id, item.IsRead, item.PanelId, item.Url, item.Kind) ) )
				&& reloaded.Items[0] is { Url: "https://github.com/owner/repo/pull/1", Kind: "github.review", Actions: [{ Label: "Approve", Link.AbsoluteUri: "lokn://github/approve/1" }],
					Tone: NotificationTone.Attention, Project: "owner/repo", Details: ["main", "3f2c1a9"] },
				"The feed is restored in order with its read state, panels, links, kinds, buttons, tones, projects and facts." );

			for ( var index = 0; index < NotificationFeed.Capacity + 5; index++ )
				reloaded.Post( new( "Chat", $"Update {index}" ) );
			Check( reloaded.Items.Count == NotificationFeed.Capacity && reloaded.Items[0].Message == $"Update {NotificationFeed.Capacity + 4}",
				"The feed keeps only the newest notifications." );

			File.WriteAllText( path, "{ not json" );
			var broken = new LoknApplication( notificationsPath: path );
			broken.Notifications.Post( new( "Chat", "Finished" ) );
			Check( broken.LastError?.StartsWith( "Notifications:", StringComparison.Ordinal ) == true && File.ReadAllText( path ) == "{ not json",
				"A feed file that cannot be read is reported and never overwritten." );
		}
		finally
		{
			if ( Directory.Exists( directory ) )
				Directory.Delete( directory, recursive: true );
		}
	}

	/// <summary>Exercises repeated mixed docking operations with deterministic randomized input.</summary>
	private static void CheckDockingStress()
	{
		var app = CreateWorkspace();
		var workspace = app.Panels;
		var random = new Random( 42 );
		for ( var index = 0; index < StressOperations; index++ )
		{
			var panels = workspace.Panels;
			var panes = workspace.Panes;
			switch ( random.Next( 5 ) )
			{
				case 0:
					workspace.Open( "fixture.panel", panes[random.Next( panes.Count )].Id );
					break;
				case 1 when panels.Count > 0:
					workspace.Close( panels[random.Next( panels.Count )].Id );
					break;
				case 2 when panels.Count > 0:
					workspace.Move( panels[random.Next( panels.Count )].Id, panes[random.Next( panes.Count )].Id, (PanelPlacement)random.Next( 5 ) );
					break;
				case 3 when panes.Count < 12:
					workspace.SplitActive( (SplitAxis)random.Next( 2 ) );
					break;
				default:
					workspace.ActivatePane( panes[random.Next( panes.Count )].Id );
					break;
			}

			CheckInvariants( workspace );
		}

		app.Shutdown();
	}

	/// <summary>Creates an application with one independently registered fixture panel type.</summary>
	private static LoknApplication CreateWorkspace()
	{
		var app = new LoknApplication();
		app.Load( new FixtureModule( "fixture", builder => builder.Panel( "panel", "Fixture", _ => new FixtureContent() ) ) );
		return app;
	}

	/// <summary>Checks one-to-one panel ownership and valid active selections after tree changes.</summary>
	internal static void CheckInvariants( PanelWorkspace workspace )
	{
		var ids = workspace.Panes.Where( pane => pane.PanelId is not null ).Select( pane => pane.PanelId!.Value ).ToArray();
		Check( ids.Length == ids.Distinct().Count(), "Each panel occurs in exactly one visible pane." );
		Check( ids.ToHashSet().SetEquals( workspace.Panels.Select( panel => panel.Id ) ), "Topology matches live sessions." );
		Check( workspace.Panes.Any( pane => pane.Id == workspace.ActivePaneId ), "The active pane exists." );

	}

	/// <summary>Fails a check with a readable invariant description.</summary>
	internal static void Check( bool condition, string message )
	{
		if ( !condition )
			throw new InvalidOperationException( message );
	}
}

/// <summary>Defines a focused test module with configurable startup and dependencies.</summary>
internal sealed class FixtureModule( string id, Action<ModuleBuilder> register, Action<ModuleContext>? start = null ) : ILoknModule
{
	/// <summary>Gets the fixture module identifier.</summary>
	public string Id => id;

	/// <summary>Gets the fixture module title.</summary>
	public string Title => id;

	/// <summary>Gets the fixture's required module identifiers.</summary>
	public IReadOnlyList<string> Dependencies { get; init; } = [];

	/// <summary>Registers fixture contributions.</summary>
	public void Register( ModuleBuilder builder ) => register( builder );

	/// <summary>Runs optional fixture startup.</summary>
	public void Start( ModuleContext context ) => start?.Invoke( context );
}

/// <summary>Retains a close count to verify owned panel lifetime.</summary>
internal sealed class FixtureContent : PanelContent
{
	/// <summary>Gets the number of close hook calls.</summary>
	internal int CloseCount
	{
		get; private set;
	}

	/// <summary>Builds a minimal fixture surface.</summary>
	public override Blob Build() => new Text
	{
		Content = "Fixture",
	};

	/// <summary>Records panel closure.</summary>
	public override void Closed() => CloseCount++;
}
