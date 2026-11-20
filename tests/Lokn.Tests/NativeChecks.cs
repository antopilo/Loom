using System.Reflection;
using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules;

namespace Lokn.Tests;

/// <summary>Exercises the real Goo window, retained docking views, input routing, and palette.</summary>
internal static class NativeChecks
{
	/// <summary>Defines the native test client width in logical pixels.</summary>
	private const int WindowWidth = 1200;

	/// <summary>Defines the native test client height in logical pixels.</summary>
	private const int WindowHeight = 800;

	/// <summary>Defines the test pump frame count per stable UI action.</summary>
	private const int SettleFrames = 16;

	/// <summary>Runs UI integration checks on the calling thread's native Goo window.</summary>
	internal static void Run( string[] args )
	{
		Application.VerifyState = true;
		var app = new LoknApplication();
		app.Load( new ShellModule() );
		app.Load( new WelcomeModule() );
		var state = new RetainedState();
		var commandThread = 0;
		var continuationThread = 0;
		app.Load( new FixtureModule( "native", builder => builder.Panel( "editor", "Fixture", _ => new RetainedContent( state ) )
			.Panel( "broken-render", "Broken renderer", _ => new BrokenContent() )
			.Panel( "broken-factory", "Broken factory", _ => throw new InvalidOperationException( "factory failed" ) )
			.CommandAsync( "async", "Async UI command", async context =>
			{
				commandThread = Environment.CurrentManagedThreadId;
				await Task.Delay( 20, context.Lifetime );
				continuationThread = Environment.CurrentManagedThreadId;
				context.App.Panels.Open( "welcome.home" );
			}, new( Key.U, KeyModifiers.Primary ) ) ) );
		var panel = app.Panels.Open( "native.editor" );
		var shell = new ShellView( app ) { AppliesNativeWindowHooks = false };
		var semantics = new SemanticAdapter();
		var window = new Window
		{
			Title = "Lokn integration checks",
			Size = new( WindowWidth, WindowHeight ),
			Decorated = false,
			Transparent = true,
			Background = Theme.Current.Canvas,
			Foreground = Theme.Current.Text,
			Root = shell,
			AccessibilityAdapter = semantics,
		};
		app.BindDispatcher( window.Post );
		app.Commands.BindDispatcher( operation => window.InvokeAsync( operation ) );
		var startupPosts = 0;
		app.Load( new FixtureModule( "startup-post", _ => { }, context => context.Post( () => startupPosts++ ) ) );
		window.Open();
		Pump( window );
		if ( !args.Contains( "--settings-only" ) )
			CheckTitlebarActions( window, shell, app, semantics );
		CoreChecks.Check( startupPosts == 1, "Module startup work posted before Open reaches the UI thread." );
		CoreChecks.Check( state.MountCount == 1 && state.Input.IsMounted, "Feature Cell mounts once." );
		state.Input.Focus();
		window.PlatformInput.CommitText( "retained through docking" );
		PumpUntil( window, semantics, () => state.Text.Value == "retained through docking", "Native text input reaches the feature." );

		if ( args.Contains( "--settings-only" ) )
		{
			var preview = app.Panels.Open( "welcome.home" );
			Pump( window );
			var settingsCaptureIndex = Array.IndexOf( args, "--capture" );
			var settingsCapture = settingsCaptureIndex >= 0 && settingsCaptureIndex + 1 < args.Length ? args[settingsCaptureIndex + 1] : null;
			SettingsUiChecks.Run( window, shell, app, state, semantics, preview, settingsCapture );
			window.RequestClose();
			Pump( window );
			return;
		}

		var right = app.Panels.SplitActive( SplitAxis.Horizontal );
		app.Panels.Move( panel, right );
		Pump( window );
		CoreChecks.Check( state.MountCount == 1 && state.UnmountCount == 0, "Reparent-free docking retains nested Cell identity." );
		CoreChecks.Check( state.Text.Value == "retained through docking" && state.Input.IsMounted, "Docking retains editor state and handles." );
		var other = app.Panels.Open( "welcome.home" );
		Pump( window );
		app.Panels.Activate( panel );
		Pump( window );
		CoreChecks.Check( state.MountCount == 1, "All open panels remain visible and mounted." );

		var workspaceHandle = (ElementHandle)typeof( ShellView )
			.GetField( "_workspaceHandle", BindingFlags.Instance | BindingFlags.NonPublic )!
			.GetValue( shell )!;
		var workspace = workspaceHandle.BorderBox;
		var split = (DockSplit)app.Panels.Root;
		var geometry = DockGeometry.Build( app.Panels.Root, new( workspace.Width, workspace.Height ) );
		var divider = geometry.Dividers[split.Id];
		var dividerX = workspace.X + divider.X + divider.Width / 2;
		var dividerY = workspace.Y + divider.Y + divider.Height / 2;
		// A drag targets no focusable node, so the window first takes native input again if it lost the system focus.
		UiInput.Activate( window );
		PointerPress( window, dividerX, dividerY );
		PointerMove( window, dividerX + 80, dividerY );
		PointerRelease( window, dividerX + 80, dividerY );
		PumpUntil( window, semantics, () => ((DockSplit)app.Panels.Root).Ratio > split.Ratio, "Pointer-captured divider drag updates the native workspace." );

		geometry = DockGeometry.Build( app.Panels.Root, new( workspace.Width, workspace.Height ) );
		var source = geometry.Panes[right];
		var targetPane = app.Panels.Panes.First( pane => pane.Id != right );
		var target = geometry.Panes[targetPane.Id];
		var sourceGrip = DockGeometry.ComputeGrip( source );
		var sourceX = workspace.X + sourceGrip.X + sourceGrip.Width / 2;
		var sourceY = workspace.Y + sourceGrip.Y + sourceGrip.Height / 2;
		var targetX = workspace.X + target.X + target.Width / 2;
		var targetY = workspace.Y + target.Y + 24;
		UiInput.Activate( window );
		PointerPress( window, sourceX, sourceY );
		PointerMove( window, sourceX + 20, sourceY + 20 );
		Pump( window );
		PointerMove( window, targetX, targetY );
		Pump( window );
		PointerRelease( window, targetX, targetY );
		PumpUntil( window, semantics, () => app.Panels.Panes.Count == 3, "Native pane-grip drag docks at a target panel edge." );
		CoreChecks.Check( state.MountCount == 1, "Pointer docking preserves the nested feature Cell." );

		geometry = DockGeometry.Build( app.Panels.Root, new( workspace.Width, workspace.Height ) );
		var swapSourceId = app.Panels.ActivePaneId;
		var swapTarget = app.Panels.Panes.First( pane => pane.Id != swapSourceId && pane.PanelId is not null );
		var swapSourceBounds = DockGeometry.ComputeGrip( geometry.Panes[swapSourceId] );
		var swapTargetBounds = geometry.Panes[swapTarget.Id];
		var swapSourceX = workspace.X + swapSourceBounds.X + swapSourceBounds.Width / 2;
		var swapSourceY = workspace.Y + swapSourceBounds.Y + swapSourceBounds.Height / 2;
		var swapTargetX = workspace.X + swapTargetBounds.X + swapTargetBounds.Width / 2;
		var swapTargetY = workspace.Y + swapTargetBounds.Y + swapTargetBounds.Height / 2;
		UiInput.Activate( window );
		PointerPress( window, swapSourceX, swapSourceY );
		PointerMove( window, swapSourceX + 20, swapSourceY + 20 );
		Pump( window );
		PointerMove( window, swapTargetX, swapTargetY );
		Pump( window );
		PointerRelease( window, swapTargetX, swapTargetY );
		PumpUntil( window, semantics, () => app.Panels.Panes.Single( pane => pane.Id == swapTarget.Id ).PanelId == panel, "Native center drop swaps the dragged pane into its target." );
		CoreChecks.Check( app.Panels.Panes.Single( pane => pane.Id == swapSourceId ).PanelId == swapTarget.PanelId, "Native center drop preserves the displaced pane." );
		CoreChecks.Check( state.MountCount == 1 && state.Text.Value == "retained through docking", "Swapping retains feature Cells and editor state." );
		CoreChecks.CheckInvariants( app.Panels );

		geometry = DockGeometry.Build( app.Panels.Root, new( workspace.Width, workspace.Height ) );
		var menuPaneId = app.Panels.Panes.First( pane => pane.Id != app.Panels.ActivePaneId ).Id;
		var grip = DockGeometry.ComputeGrip( geometry.Panes[menuPaneId] );
		var gripX = workspace.X + grip.X + grip.Width / 2;
		var gripY = workspace.Y + grip.Y + grip.Height / 2;
		var menuField = typeof( ShellView ).GetField( "_menuPaneId", BindingFlags.Instance | BindingFlags.NonPublic )!;
		UiInput.Activate( window );
		PointerMove( window, gripX, gripY );
		Pump( window );
		PointerPress( window, gripX, gripY );
		PointerRelease( window, gripX, gripY );
		PumpUntil( window, semantics, () => (Guid?)menuField.GetValue( shell ) == menuPaneId && app.Panels.ActivePaneId == menuPaneId,
			"Clicking a pane grip activates the pane and opens its actions menu." );
		// Goo dismisses a native popup only on real OS input (Window.Host.gs configureHost: a native pointer press calls DismissChildMenus with
		// OutsideInteraction, a native Escape calls popup.Dismiss with Escape); PlatformInput bypasses both, so this check cannot exercise dismissal and
		// closes the menu in code, through the shell's own close.
		typeof( ShellView ).GetMethod( "ReleasePaneMenu", BindingFlags.Instance | BindingFlags.NonPublic )!.Invoke( shell, null );
		PumpUntil( window, semantics, () => menuField.GetValue( shell ) is null, "The pane actions menu closes." );
		CoreChecks.CheckInvariants( app.Panels );

		// Agent tools arrive on a transport thread, as Omp's host_tool_call frames do.
		var toolCall = Task.Run( () => app.Tools.InvokeAsync( "shell_focus_panel", System.Text.Json.JsonDocument.Parse( $$"""{ "panel": "{{panel}}" }""" ).RootElement, null, CancellationToken.None ) );
		while ( !toolCall.IsCompleted )
			Pump( window );
		CoreChecks.Check( toolCall.Result is { IsError: false } && app.Panels.ActivePanelId == panel, $"An agent tool called from another thread runs on the UI thread ({toolCall.Result.Text})." );

		Shortcut( window, Key.P, KeyModifiers.PrimaryShift );
		PumpUntil( window, semantics, () => app.NativeSurfacesOccluded, "The command palette hides native child surfaces while it is open." );
		window.PlatformInput.CommitText( "Open Welcome" );
		Pump( window );
		var count = app.Panels.Panels.Count;
		// The palette's search entry has the focus; the Enter goes to it as typed.
		window.PlatformInput.KeyPress( Key.Enter, KeyModifiers.None );
		window.PlatformInput.KeyRelease( Key.Enter );
		PumpUntil( window, semantics, () => app.Panels.Panels.Count == count + 1, "Global palette shortcut, search input, and Enter execute a registered command." );
		CoreChecks.Check( !app.NativeSurfacesOccluded, "Closing the command palette restores native child surfaces." );

		var asyncPanelCount = app.Panels.Panels.Count;
		Shortcut( window, Key.U, KeyModifiers.Primary );
		PumpUntil( window, semantics, () => app.Panels.Panels.Count == asyncPanelCount + 1, "Async commands can safely open panels after awaiting." );
		CoreChecks.Check( commandThread != 0 && commandThread == continuationThread, "Async global commands resume on the native UI thread." );

		var brokenRender = app.Panels.Open( "native.broken-render" );
		Pump( window );
		CoreChecks.Check( window.IsOpen, "A feature renderer failure stays inside its panel." );
		app.Panels.Close( brokenRender );
		var brokenFactory = app.Panels.Open( "native.broken-factory" );
		Pump( window );
		CoreChecks.Check( app.LastError!.Contains( "factory failed" ) && window.IsOpen, "A feature factory failure leaves the shell responsive." );
		app.Panels.Close( brokenFactory );
		app.ClearError();
		SettingsUiChecks.Run( window, shell, app, state, semantics, other );
		app.Unload( "native" );
		Pump( window );
		CoreChecks.Check( state.UnmountCount == 1, "Module unload unmounts its nested feature exactly once." );
		CoreChecks.Check( app.Panels.Panels.Any( item => item.Id == other ), "Module unload preserves unrelated native panels." );
		// Modules are turned on and off on the Settings panel's Modules tab.
		var manager = app.Panels.Open( "shell.settings" );
		Pump( window );
		app.Unload( "welcome" );
		Pump( window );
		CoreChecks.Check( app.Panels.Panels.Single().Id == manager, "Optional feature removal leaves the essential shell and its Settings panel running." );
		app.Enable( "welcome" );
		Pump( window );

		if ( Array.IndexOf( args, "--capture" ) is var captureIndex && captureIndex >= 0 && captureIndex + 1 < args.Length )
		{
			var pane = app.Panels.SplitActive( SplitAxis.Horizontal );
			app.Panels.Move( manager, pane );
			Pump( window );
			BuildCapture( window, args[captureIndex + 1], "workspace" );
			var preferences = app.Panels.Open( "shell.settings" );
			Pump( window );
			var previewGeometry = DockGeometry.Build( app.Panels.Root, new( workspaceHandle.BorderBox.Width, workspaceHandle.BorderBox.Height ) );
			var previewPane = app.Panels.Panes.MaxBy( item => previewGeometry.Panes[item.Id].Width )!;
			app.Panels.Move( preferences, previewPane.Id );
			Pump( window );
			BuildCapture( window, args[captureIndex + 1], "settings" );
			app.Panels.Close( preferences );
			Pump( window );
			var (tipX, tipY) = ComputeTitlebarAction( semantics, "Toggle sidebar" );
			UiInput.Activate( window );
			PointerMove( window, tipX, tipY );
			Pump( window );
			BuildCapture( window, args[captureIndex + 1], "titlebar-tooltip" );
			PointerMove( window, window.Size.Width / 2, TitleBar.BarHeight );
			Pump( window );
			Shortcut( window, Key.K, KeyModifiers.Primary );
			PumpUntil( window, semantics, () => app.NativeSurfacesOccluded, "Ctrl+K opens the command palette for its capture." );
			BuildCapture( window, args[captureIndex + 1], "palette" );
		}

		window.RequestClose();
		Pump( window );
		CoreChecks.Check( !window.IsOpen && app.Modules.Count == 0, "Window shutdown stops every module." );
		Console.WriteLine( "PASS native rendering, retained Cell state, pane swaps, pointer resizing, pointer docking, palette routing, module unload, and shutdown." );
	}

	/// <summary>Checks the titlebar's icons, found by their accessible names, the split shortcuts, and reclaimed toolbar space.</summary>
	private static void CheckTitlebarActions( Window window, ShellView shell, LoknApplication app, SemanticAdapter semantics )
	{
		var active = app.Panels.ActivePanelId;
		var paneCount = app.Panels.Panes.Count;
		var panelCount = app.Panels.Panels.Count;
		var handle = (ElementHandle)typeof( ShellView )
			.GetField( "_workspaceHandle", BindingFlags.Instance | BindingFlags.NonPublic )!
			.GetValue( shell )!;
		CoreChecks.Check( handle.BorderBox.Y == TitleBar.BarHeight, "The workspace starts immediately below the titlebar." );

		var isSidebarShown = app.Settings.Get( ShellSettings.ShowSidebar );
		ClickTitlebarAction( window, semantics, "Toggle sidebar" );
		PumpUntil( window, semantics, () => app.Settings.Get( ShellSettings.ShowSidebar ) != isSidebarShown, "The titlebar sidebar icon shows or hides the sidebar." );
		ClickTitlebarAction( window, semantics, "Toggle sidebar" );
		PumpUntil( window, semantics, () => app.Settings.Get( ShellSettings.ShowSidebar ) == isSidebarShown, "The titlebar sidebar icon toggles back." );

		ClickTitlebarAction( window, semantics, "Settings" );
		PumpUntil( window, semantics, () => app.Panels.Panels.Count == panelCount + 1 && app.Panels.Panels.Single( item => item.Id == app.Panels.ActivePanelId ).TypeId == "shell.settings",
			"The titlebar Settings icon opens the Settings panel." );
		app.Panels.CloseActive();
		Pump( window );

		Shortcut( window, Key.BackSlash, KeyModifiers.Primary );
		PumpUntil( window, semantics, () => app.Panels.Panes.Count == paneCount + 1, "The split-right shortcut creates a pane." );
		app.Panels.CloseActive();
		Pump( window );
		Shortcut( window, Key.BackSlash, KeyModifiers.PrimaryShift );
		PumpUntil( window, semantics, () => app.Panels.Panes.Count == paneCount + 1, "The split-down shortcut creates a pane." );
		app.Panels.CloseActive();
		Pump( window );
		if ( active is { } panel )
			app.Panels.Activate( panel );

		PointerMove( window, window.Size.Width / 2, TitleBar.BarHeight );
		Pump( window );
		CoreChecks.Check( window.State == WindowState.Normal, "Workspace titlebar icons leave native window state unchanged." );
	}

	/// <summary>Finds the center of the titlebar button with the accessible name, in layout coordinates.</summary>
	private static (double X, double Y) ComputeTitlebarAction( SemanticAdapter semantics, string name )
	{
		var node = Flatten( semantics.Tree!.Root! ).Single( node => node.Role == AccessibilityRole.Button && node.Name == name && node.Bounds.Y < TitleBar.BarHeight );
		var bounds = node.VisibleBounds ?? node.Bounds;
		return (bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
	}

	/// <summary>Clicks a titlebar icon, found by its accessible name, through native pointer input after focusing it in code (<see cref="UiInput"/>).</summary>
	private static void ClickTitlebarAction( Window window, SemanticAdapter semantics, string name ) =>
		UiInput.Click( window, () => semantics.Tree?.Root is { } root
			? Flatten( root ).SingleOrDefault( node => node.Role == AccessibilityRole.Button && node.Name == name && node.Bounds.Y < TitleBar.BarHeight )
			: null, () => Pump( window ), $"The titlebar's {name} icon" );

	/// <summary>Presses and releases a window shortcut through native input, after letting the window take native input again if it lost the system focus.</summary>
	private static void Shortcut( Window window, Key key, KeyModifiers modifiers )
	{
		UiInput.Activate( window );
		window.PlatformInput.KeyPress( key, modifiers );
		window.PlatformInput.KeyRelease( key );
	}

	/// <summary>Pumps until the state an action changes holds, failing with what was expected and what shows after 20 seconds.</summary>
	private static void PumpUntil( Window window, SemanticAdapter semantics, Func<bool> predicate, string what )
	{
		var timer = System.Diagnostics.Stopwatch.StartNew();
		while ( !predicate() )
		{
			if ( timer.Elapsed > TimeSpan.FromSeconds( 20 ) )
			{
				var shown = semantics.Tree?.Root is { } root
					? string.Join( " | ", Flatten( root ).Select( node => node.Name ).Where( name => name is { Length: > 0 } ).Distinct() )
					: "no accessibility tree";
				CoreChecks.Check( false, $"{what} (timed out; focused: {window.IsFocused}; showing: {shown[..Math.Min( shown.Length, 3000 )]})" );
			}
			Pump( window );
		}
		Pump( window );
	}

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

	/// <summary>Processes a bounded sequence of real native frames.</summary>
	private static void Pump( Window window )
	{
		for ( var index = 0; index < SettleFrames && window.IsOpen; index++ )
		{
			window.Pump( 0.016 );
			Thread.Sleep( 10 );
		}
	}

	/// <summary>Presses the primary mouse button in window layout coordinates.</summary>
	private static void PointerPress( Window window, double x, double y )
	{
		window.PlatformInput.PointerPress( 1, PointerDevice.Mouse, x * window.UiScale, y * window.UiScale, PointerButton.Primary, KeyModifiers.None, 0.0f );
	}

	/// <summary>Moves the mouse in window layout coordinates.</summary>
	private static void PointerMove( Window window, double x, double y )
	{
		window.PlatformInput.PointerMove( 1, PointerDevice.Mouse, x * window.UiScale, y * window.UiScale, KeyModifiers.None, 0.0f );
	}

	/// <summary>Releases the primary mouse button in window layout coordinates.</summary>
	private static void PointerRelease( Window window, double x, double y )
	{
		window.PlatformInput.PointerRelease( 1, PointerDevice.Mouse, x * window.UiScale, y * window.UiScale, PointerButton.Primary, KeyModifiers.None, 0.0f );
	}

	/// <summary>Exports a completed native render for optional visual QA.</summary>
	internal static void BuildCapture( Window window, string directory, string name )
	{
		CoreChecks.Check( window.RequestCapture() == WindowCaptureRequestStatus.Accepted, "Native render capture is accepted." );
		WindowCapture? capture = null;
		for ( var index = 0; index < 300 && capture is null; index++ )
		{
			window.Pump( 0.016 );
			capture = window.PollCapture();
			Thread.Sleep( 10 );
		}

		CoreChecks.Check( capture is not null, "Native render capture completes." );
		Directory.CreateDirectory( directory );
		File.WriteAllBytes( Path.Combine( directory, $"{name}.rgba" ), capture!.Pixels );
		File.WriteAllText( Path.Combine( directory, $"{name}.json" ), JsonSerializer.Serialize( new
		{
			capture.Width,
			capture.Height
		} ) );
	}
}

/// <summary>Owns native fixture state independently of its view mount.</summary>
internal sealed class RetainedState
{
	/// <summary>Retains the fixture input handle.</summary>
	internal ElementHandle Input { get; } = new();

	/// <summary>Retains fixture text across layout changes.</summary>
	internal State<string> Text { get; } = new( "" );

	/// <summary>Counts native feature mounts.</summary>
	internal int MountCount
	{
		get; set;
	}

	/// <summary>Counts native feature unmounts.</summary>
	internal int UnmountCount
	{
		get; set;
	}
}

/// <summary>Builds a nested feature Cell to test genuine retained Goo identity.</summary>
internal sealed class RetainedContent( RetainedState state ) : PanelContent
{
	/// <summary>Builds a nested mounted view under the stable panel surface.</summary>
	public override Blob Build() => Cell.Mount<RetainedView>( "editor", () => new( state ) );
}

/// <summary>Exposes an editable nested feature view with measurable native mount lifetime.</summary>
internal sealed class RetainedView( RetainedState state ) : Cell
{
	/// <summary>Builds the retained native text entry.</summary>
	public override Blob Build()
	{
		return new TextEntry
		{
			Handle = state.Input,
			Value = state.Text.Value,
			Controlled = true,
			Height = 40,
			OnChange = value => state.Text.Value = value,
		};
	}

	/// <summary>Records the native feature mount.</summary>
	protected override void Mounted() => state.MountCount++;

	/// <summary>Records the native feature unmount.</summary>
	protected override void Unmounting() => state.UnmountCount++;
}

/// <summary>Provides a failing root renderer to verify panel-local error containment.</summary>
internal sealed class BrokenContent : PanelContent
{
	/// <summary>Throws a feature error during root rendering.</summary>
	public override Blob Build() => throw new InvalidOperationException( "renderer failed" );
}
