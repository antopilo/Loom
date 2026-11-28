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
		Click( window, semantics, Find( semantics, AccessibilityRole.Button, "Loom menu" ) );
		Pump( window );
		Click( window, semantics, Find( semantics, AccessibilityRole.MenuItem, "Settings" ) );
		var settingsPanel = app.Panels.ActivePanelId!.Value;
		CoreChecks.Check( app.Panels.Panels.Single( panel => panel.Id == settingsPanel ).TypeId == "shell.settings", "The Loom menu's Settings opens the Settings panel." );

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

		Click( window, semantics, Find( semantics, AccessibilityRole.Radio, "Switch to Work" ) );
		PumpUntil( window, () => app.Settings.ActiveProfile == SettingsProfile.Work && Flatten( semantics.Tree!.Root! ).Any( node => node.Role == AccessibilityRole.Radio
			&& node.Name == "Work profile, active. Choose its icon." ), "The sidebar's profile switcher selects Work." );
		CoreChecks.Check( Find( semantics, AccessibilityRole.Radio, "Work profile, active. Choose its icon." ).Checked == AccessibilityChecked.True
			&& Find( semantics, AccessibilityRole.Radio, "Switch to Personal" ).Checked == AccessibilityChecked.False, "The profile buttons expose which profile is active." );
		CoreChecks.Check( app.Settings.Get( WelcomeModule.Greeting ) == "Work heading" && handle.BorderBox.Height > personalHeight, "Profile settings update existing views and reclaim hidden status space live." );
		CoreChecks.Check( Flatten( semantics.Tree!.Root! ).Any( node => node.Role == AccessibilityRole.Text && (node.Name == "Work heading" || node.Value == "Work heading") ), "Existing Welcome views render the active profile heading." );
		CoreChecks.Check( panels.SetEquals( app.Panels.Panels.Select( panel => panel.Id ) ) && state.MountCount == 1, "Profile switching preserves panels and nested feature state." );

		// The clicked Work button holds the focus, focused again in code so the keys land even after the window lost the system focus; Tab moves it to Personal.
		UiInput.Focus( window, Find( semantics, AccessibilityRole.Radio, "Work profile, active. Choose its icon." ) );
		window.PlatformInput.KeyPress( Key.Tab, KeyModifiers.None );
		window.PlatformInput.KeyRelease( Key.Tab );
		window.PlatformInput.KeyPress( Key.Space, KeyModifiers.None );
		window.PlatformInput.KeyRelease( Key.Space );
		PumpUntil( window, () => app.Settings.ActiveProfile == SettingsProfile.Personal && Flatten( semantics.Tree!.Root! ).Any( node => node.Role == AccessibilityRole.Radio
			&& node.Name == "Personal profile, active. Choose its icon." && node.Checked == AccessibilityChecked.True ), "Keyboard activation switches back to Personal." );

		Click( window, semantics, FindEditorControl( semantics, AccessibilityRole.Button, "Welcome settings" ) );
		Click( window, semantics, FindEditorControl( semantics, AccessibilityRole.Button, "Work" ) );
		Click( window, semantics, FindEditorControl( semantics, AccessibilityRole.TextInput, "Heading" ) );
		window.PlatformInput.KeyPress( Key.A, KeyModifiers.Primary );
		window.PlatformInput.KeyRelease( Key.A );
		window.PlatformInput.CommitText( "Office heading" );
		window.PlatformInput.KeyPress( Key.Enter, KeyModifiers.None );
		window.PlatformInput.KeyRelease( Key.Enter );
		PumpUntil( window, () => app.Settings.Get( WelcomeModule.Greeting, SettingsScope.Work ) == "Office heading", "The Work editor saves a profile-specific value." );
		CoreChecks.Check( app.Settings.Get( WelcomeModule.Greeting ) == "Shared heading", "Editing Work never changes the active Personal value." );
		Click( window, semantics, FindEditorControl( semantics, AccessibilityRole.Button, "Use default Heading" ) );
		PumpUntil( window, () => !app.Settings.HasValue( WelcomeModule.Greeting, SettingsScope.Work ), "Use default removes the profile override." );

		Click( window, semantics, FindEditorControl( semantics, AccessibilityRole.TextInput, "Heading" ) );
		window.PlatformInput.KeyPress( Key.A, KeyModifiers.Primary );
		window.PlatformInput.KeyRelease( Key.A );
		window.PlatformInput.CommitText( "Left heading" );
		Click( window, semantics, FindEditorControl( semantics, AccessibilityRole.Button, "Work, selected" ) );
		PumpUntil( window, () => app.Settings.Get( WelcomeModule.Greeting, SettingsScope.Work ) == "Left heading", "Leaving a text editor saves its value without Enter." );
		Click( window, semantics, FindEditorControl( semantics, AccessibilityRole.Button, "Use default Heading" ) );

		Click( window, semantics, FindEditorControl( semantics, AccessibilityRole.Button, "Personal" ) );
		Click( window, semantics, FindEditorControl( semantics, AccessibilityRole.Button, "Override Heading" ) );
		app.Settings.Set( WelcomeModule.Greeting, SettingsScope.Global, "Changed global heading" );
		Pump( window );
		CoreChecks.Check( app.Settings.Get( WelcomeModule.Greeting ) == "Shared heading", "An explicit Personal override stays independent of later global edits." );
		Click( window, semantics, FindEditorControl( semantics, AccessibilityRole.Button, "Use default Heading" ) );
		PumpUntil( window, () => app.Settings.Get( WelcomeModule.Greeting ) == "Changed global heading", "Removing an override immediately resumes global inheritance." );

		app.Settings.Reset( WelcomeModule.Greeting, SettingsScope.Global );
		app.Settings.Reset( ShellSettings.ShowStatusBar, SettingsScope.Work );
		if ( captureDirectory is not null )
		{
			Click( window, semantics, FindEditorControl( semantics, AccessibilityRole.Button, "Defaults" ) );
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

	/// <summary>Clicks a semantic control, followed by its identifier, using Goo's real pointer routing after focusing it in code (<see cref="UiInput"/>).</summary>
	private static void Click( Window window, SemanticAdapter semantics, AccessibilityNode node ) =>
		UiInput.Click( window, () => Flatten( semantics.Tree!.Root! ).FirstOrDefault( candidate => candidate.Id == node.Id ), () => Pump( window ), node.Name ?? node.Role.ToString() );

	/// <summary>Pumps until the state an action changes holds, failing with what was expected after ten seconds.</summary>
	private static void PumpUntil( Window window, Func<bool> condition, string what )
	{
		var clock = System.Diagnostics.Stopwatch.StartNew();
		while ( !condition() )
		{
			CoreChecks.Check( clock.Elapsed < TimeSpan.FromSeconds( 10 ), $"{what} (timed out)" );
			Pump( window );
		}
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
