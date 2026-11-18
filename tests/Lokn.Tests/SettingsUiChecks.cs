using System.Reflection;
using Goo;
using Lokn.Core;
using Lokn.Modules;

namespace Lokn.Tests;

/// <summary>Checks the profile switch and settings editor through semantic UI bounds and native input.</summary>
internal static class SettingsUiChecks
{
	/// <summary>Runs live profile and editor checks while preserving the existing fixture panel.</summary>
	internal static void Run( Window window, ShellView shell, LoknApplication app, RetainedState state, SemanticAdapter semantics, Guid welcomePanel, string? captureDirectory = null )
	{
		foreach ( var panel in app.Panels.Panels.Where( panel => panel.TypeId == "welcome.home" && panel.Id != welcomePanel ) )
			app.Panels.Close( panel.Id );

		Pump( window );
		Click( window, Find( semantics, AccessibilityRole.Button, "Settings" ) );
		var settingsPanel = app.Panels.ActivePanelId!.Value;
		CoreChecks.Check( app.Panels.Panels.Single( panel => panel.Id == settingsPanel ).TypeId == "shell.settings", "The titlebar gear opens the Settings panel." );

		var handle = (ElementHandle)typeof( ShellView ).GetField( "_workspaceHandle", BindingFlags.Instance | BindingFlags.NonPublic )!.GetValue( shell )!;
		var geometry = DockGeometry.Build( app.Panels.Root, new( handle.BorderBox.Width, handle.BorderBox.Height ) );
		var largest = app.Panels.Panes.MaxBy( pane => geometry.Panes[pane.Id].Width )!;
		app.Panels.Move( settingsPanel, largest.Id );
		app.Settings.Set( WelcomeModule.Greeting, SettingsScope.Global, "Shared heading" );
		app.Settings.Set( WelcomeModule.Greeting, SettingsScope.Work, "Work heading" );
		app.Settings.Set( ShellSettings.ShowStatusBar, SettingsScope.Work, false );
		Pump( window );
		var panels = app.Panels.Panels.Select( panel => panel.Id ).ToHashSet();
		var personalHeight = handle.BorderBox.Height;

		Click( window, Find( semantics, AccessibilityRole.Switch ) );
		CoreChecks.Check( app.Settings.ActiveProfile == SettingsProfile.Work, "The titlebar switch selects Work." );
		CoreChecks.Check( Find( semantics, AccessibilityRole.Switch ).Checked == AccessibilityChecked.False, "The profile switch exposes its Work check state." );
		CoreChecks.Check( app.Settings.Get( WelcomeModule.Greeting ) == "Work heading" && handle.BorderBox.Height > personalHeight, "Profile settings update existing views and reclaim hidden status space live." );
		CoreChecks.Check( Flatten( semantics.Tree!.Root! ).Any( node => node.Role == AccessibilityRole.Text && (node.Name == "Work heading" || node.Value == "Work heading") ), "Existing Welcome views render the active profile heading." );
		CoreChecks.Check( panels.SetEquals( app.Panels.Panels.Select( panel => panel.Id ) ) && state.MountCount == 1, "Profile switching preserves panels and nested feature state." );

		window.PlatformInput.KeyPress( Key.Space, KeyModifiers.None );
		window.PlatformInput.KeyRelease( Key.Space );
		Pump( window );
		CoreChecks.Check( app.Settings.ActiveProfile == SettingsProfile.Personal && Find( semantics, AccessibilityRole.Switch ).Checked == AccessibilityChecked.True, "Keyboard activation switches back to Personal." );

		Click( window, FindEditorControl( semantics, AccessibilityRole.Button, "Work" ) );
		var group = FindEditorControl( semantics, AccessibilityRole.Group, "Welcome heading" );
		var editor = Flatten( group ).Single( node => node.Role == AccessibilityRole.TextInput );
		Click( window, editor );
		window.PlatformInput.KeyPress( Key.A, KeyModifiers.Primary );
		window.PlatformInput.KeyRelease( Key.A );
		window.PlatformInput.CommitText( "Office heading" );
		window.PlatformInput.KeyPress( Key.Enter, KeyModifiers.None );
		window.PlatformInput.KeyRelease( Key.Enter );
		Pump( window );
		CoreChecks.Check( app.Settings.Get( WelcomeModule.Greeting, SettingsScope.Work ) == "Office heading", "The Work editor saves a profile-specific value." );
		CoreChecks.Check( app.Settings.Get( WelcomeModule.Greeting ) == "Shared heading", "Editing Work never changes the active Personal value." );
		group = FindEditorControl( semantics, AccessibilityRole.Group, "Welcome heading" );
		Click( window, Flatten( group ).Single( node => node.Role == AccessibilityRole.Button && node.Name == "Use global" ) );
		CoreChecks.Check( !app.Settings.HasValue( WelcomeModule.Greeting, SettingsScope.Work ), "Use global removes the profile override." );

		group = FindEditorControl( semantics, AccessibilityRole.Group, "Welcome heading" );
		Click( window, Flatten( group ).Single( node => node.Role == AccessibilityRole.TextInput ) );
		window.PlatformInput.KeyPress( Key.A, KeyModifiers.Primary );
		window.PlatformInput.KeyRelease( Key.A );
		window.PlatformInput.CommitText( "Left heading" );
		Click( window, FindEditorControl( semantics, AccessibilityRole.Button, "Work" ) );
		Pump( window );
		CoreChecks.Check( app.Settings.Get( WelcomeModule.Greeting, SettingsScope.Work ) == "Left heading", "Leaving a text editor saves its value without Enter." );
		group = FindEditorControl( semantics, AccessibilityRole.Group, "Welcome heading" );
		Click( window, Flatten( group ).Single( node => node.Role == AccessibilityRole.Button && node.Name == "Use global" ) );

		Click( window, FindEditorControl( semantics, AccessibilityRole.Button, "Personal" ) );
		group = FindEditorControl( semantics, AccessibilityRole.Group, "Welcome heading" );
		Click( window, Flatten( group ).Single( node => node.Role == AccessibilityRole.Button && node.Name == "Override" ) );
		app.Settings.Set( WelcomeModule.Greeting, SettingsScope.Global, "Changed global heading" );
		Pump( window );
		CoreChecks.Check( app.Settings.Get( WelcomeModule.Greeting ) == "Shared heading", "An explicit Personal override stays independent of later global edits." );
		group = FindEditorControl( semantics, AccessibilityRole.Group, "Welcome heading" );
		Click( window, Flatten( group ).Single( node => node.Role == AccessibilityRole.Button && node.Name == "Use global" ) );
		CoreChecks.Check( app.Settings.Get( WelcomeModule.Greeting ) == "Changed global heading", "Removing an override immediately resumes global inheritance." );

		app.Settings.Reset( WelcomeModule.Greeting, SettingsScope.Global );
		app.Settings.Reset( ShellSettings.ShowStatusBar, SettingsScope.Work );
		if ( captureDirectory is not null )
		{
			Click( window, FindEditorControl( semantics, AccessibilityRole.Button, "Global defaults" ) );
			NativeChecks.BuildCapture( window, captureDirectory, "settings" );
		}

		app.Panels.Close( settingsPanel );
		Pump( window );
		CoreChecks.Check( state.MountCount == 1 && state.Text.Value == "retained through docking", "Settings edits preserve existing feature state." );
		Console.WriteLine( "PASS titlebar profile pointer/keyboard switch, live inherited values, Settings editor, explicit overrides, and reset actions." );
	}

	/// <summary>Finds a control within this settings editor rather than another open panel.</summary>
	private static AccessibilityNode FindEditorControl( SemanticAdapter semantics, AccessibilityRole role, string name )
	{
		var editor = Find( semantics, AccessibilityRole.Group, "Settings editor" );
		return Flatten( editor ).Single( node => node.Role == role && node.Name == name );
	}

	/// <summary>Finds one current semantic control by role and optional exact name.</summary>
	private static AccessibilityNode Find( SemanticAdapter semantics, AccessibilityRole role, string? name = null )
	{
		return Flatten( semantics.Tree!.Root! ).Single( node => node.Role == role && (name is null || node.Name == name) );
	}

	/// <summary>Enumerates the current semantic subtree in source order.</summary>
	private static IEnumerable<AccessibilityNode> Flatten( AccessibilityNode node )
	{
		yield return node;
		foreach ( var child in node.Children )
		{
			foreach ( var descendant in Flatten( child ) )
				yield return descendant;
		}
	}

	/// <summary>Clicks a visible semantic control using Goo's real pointer routing.</summary>
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

	/// <summary>Settles native layout and semantic delivery after a UI action.</summary>
	private static void Pump( Window window )
	{
		for ( var index = 0; index < 16; index++ )
		{
			window.Pump( 0.016 );
			Thread.Sleep( 10 );
		}
	}
}

/// <summary>Receives the app's live semantic tree for native integration checks.</summary>
internal sealed class SemanticAdapter : AccessibilityAdapter
{
	/// <summary>Gets the latest retained semantic tree.</summary>
	internal AccessibilityTree? Tree
	{
		get; private set;
	}

	/// <summary>Receives the current semantic tree from the real Goo window.</summary>
	public void Update( AccessibilityTree tree ) => Tree = tree;
}
