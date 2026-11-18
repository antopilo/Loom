using Goo;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Conversations;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;

namespace Lokn.Tests;

/// <summary>Checks blank-chat startup through the standalone application scheduler.</summary>
internal static class StartupUiChecks
{
	/// <summary>Defines the maximum initial render wait in seconds.</summary>
	private const int StartupSeconds = 10;

	/// <summary>Defines the interval between event-loop readiness observations in milliseconds.</summary>
	private const int ObservationMilliseconds = 250;

	/// <summary>Defines the settling time between independent input actions in milliseconds.</summary>
	private const int InputSettleMilliseconds = 500;

	/// <summary>Runs real scheduler input checks using the profile and hidden status bar from the reported freeze.</summary>
	internal static void Run()
	{
		var app = new LoknApplication();
		app.Load( new ShellModule() );
		app.Settings.SetActiveProfile( SettingsProfile.Work );
		app.Settings.Set( ShellSettings.ShowStatusBar, SettingsScope.Work, false );
		var omp = new OmpModule();
		app.Load( omp );
		var store = new ConversationStore( new ProjectStore() );
		var conversations = new ConversationsModule( store,
			( context, history, id ) => new ChatContent( context, history, id, omp.Service ) );
		ProjectChecks.LoadProjects( app, store );
		app.Load( conversations );
		conversations.CreateConversation( app );
		var semantics = new SemanticAdapter();
		var window = new Window
		{
			Title = "Lokn standalone startup checks",
			Decorated = false,
			Background = Theme.Current.Canvas,
			Foreground = Theme.Current.Text,
			Size = new( 1200, 800 ),
			Root = new ShellView( app ),
			AccessibilityAdapter = semantics,
		};
		app.BindDispatcher( window.Post );
		app.Commands.BindDispatcher( operation => window.InvokeAsync( operation ) );
		Exception? failure = null;
		var worker = Task.Run( async () =>
		{
			try
			{
				var ready = false;
				var startup = System.Diagnostics.Stopwatch.StartNew();
				while ( !ready && startup.Elapsed < TimeSpan.FromSeconds( StartupSeconds ) )
				{
					await Task.Delay( ObservationMilliseconds );
					await UpdateOnWindowAsync( window, () => ready = semantics.Tree?.Root is not null );
				}
				await UpdateOnWindowAsync( window, () => window.RequestActivation() );
				await Task.Delay( InputSettleMilliseconds );
				await UpdateOnWindowAsync( window, () =>
				{
					CoreChecks.Check( semantics.Tree?.Root is not null, "The standalone scheduler renders the initial blank chat." );
					var editor = Flatten( semantics.Tree!.Root! ).Single( node => node.Role == AccessibilityRole.TextEditor && node.Name == "Message" );
					UpdateClick( window, editor );
				} );
				await Task.Delay( InputSettleMilliseconds );
				await UpdateOnWindowAsync( window, () => window.PlatformInput.CommitText( "Startup input" ) );
				await Task.Delay( InputSettleMilliseconds );
				await UpdateOnWindowAsync( window, () =>
				{
					var chat = app.Panels.Sessions.Select( session => session.Content ).OfType<ConversationPanel>().Select( panel => panel.Chat ).OfType<ChatContent>().Single();
					var composer = (TextEditorController)typeof( ChatContent ).GetField( "_composer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic )!.GetValue( chat )!;
					CoreChecks.Check( composer.Document.GetText() == "Startup input", "The standalone scheduler accepts blank-chat text input." );
					var button = Flatten( semantics.Tree!.Root! ).First( node => node.Role == AccessibilityRole.Button && node.Name == "New chat" );
					UpdateClick( window, button );
				} );
				await Task.Delay( InputSettleMilliseconds );
				await UpdateOnWindowAsync( window, () =>
				{
					CoreChecks.Check( store.Conversations( SettingsProfile.Work ).Count == 2, "The standalone scheduler processes the New chat click." );
					CoreChecks.Check( app.LastError is null, "Startup input produces no contained error." );
				} );
			}
			catch ( Exception error )
			{
				failure = error;
			}
			finally
			{
				window.Post( window.RequestClose );
			}
		} );
		Application.Run( window );
		worker.GetAwaiter().GetResult();
		if ( failure is not null )
			throw new InvalidOperationException( "Standalone startup check failed.", failure );
		Console.WriteLine( "PASS standalone Application.Run startup, blank composer input, and New chat input with Work profile and hidden status bar." );
	}

	/// <summary>Posts one checked action to the standalone event loop and observes its completion.</summary>
	private static Task UpdateOnWindowAsync( Window window, Action action )
	{
		var completion = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
		window.Post( () =>
		{
			try
			{
				action();
				completion.SetResult();
			}
			catch ( Exception error )
			{
				completion.SetException( error );
			}
		} );
		return completion.Task.WaitAsync( TimeSpan.FromSeconds( StartupSeconds ) );
	}

	/// <summary>Enumerates the current native semantic subtree.</summary>
	private static IEnumerable<AccessibilityNode> Flatten( AccessibilityNode node )
	{
		yield return node;
		foreach ( var child in node.Children )
		{
			foreach ( var descendant in Flatten( child ) )
				yield return descendant;
		}
	}

	/// <summary>Clicks a current native control without manually pumping frames.</summary>
	private static void UpdateClick( Window window, AccessibilityNode node )
	{
		var bounds = node.VisibleBounds ?? node.Bounds;
		var x = (bounds.X + bounds.Width / 2) * window.UiScale;
		var y = (bounds.Y + bounds.Height / 2) * window.UiScale;
		window.PlatformInput.PointerMove( 1, PointerDevice.Mouse, x, y, KeyModifiers.None, 0.0f );
		window.PlatformInput.PointerPress( 1, PointerDevice.Mouse, x, y, PointerButton.Primary, KeyModifiers.None, 0.0f );
		window.PlatformInput.PointerRelease( 1, PointerDevice.Mouse, x, y, PointerButton.Primary, KeyModifiers.None, 0.0f );
	}
}
