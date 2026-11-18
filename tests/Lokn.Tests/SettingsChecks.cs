using System.Text.Json;
using Goo;
using Lokn.Core;

namespace Lokn.Tests;

/// <summary>Checks typed settings, layered inheritance, persistence, and module ownership.</summary>
internal static class SettingsChecks
{
	/// <summary>Defines the fixture's validated whole-number setting.</summary>
	private static readonly Setting<int> Number = new(
		"preferences.number", "Number", "A whole number from 1 to 100.", 10, "Fixture",
		isValid: value => value is >= 1 and <= 100 );

	/// <summary>Defines the fixture's Boolean setting.</summary>
	private static readonly Setting<bool> Enabled = new( "preferences.enabled", "Enabled", "A Boolean fixture value.", true, "Fixture" );

	/// <summary>Defines the fixture's text setting.</summary>
	private static readonly Setting<string> Name = new( "preferences.name", "Name", "A text fixture value.", "Default", "Fixture" );

	/// <summary>Runs settings regression checks against isolated temporary files.</summary>
	internal static void Run()
	{
		CheckInheritance();
		CheckPersistence();
		CheckInvalidFiles();
		CheckOwnership();
		CheckShellBackdrop();
		Console.WriteLine( "PASS settings inheritance, explicit reset, profile persistence, invalid input, failed writes, module ownership, and shell backdrop choices." );
	}

	/// <summary>Checks profile isolation and true inheritance rather than copied defaults.</summary>
	private static void CheckInheritance()
	{
		var app = BuildApp();
		var settings = app.Settings;
		CoreChecks.Check( settings.ActiveProfile == SettingsProfile.Personal && settings.Get( Number ) == 10, "Fresh settings use built-in defaults and Personal." );
		settings.Set( Number, SettingsScope.Global, 20 );
		settings.Set( Number, SettingsScope.Work, 30 );
		CoreChecks.Check( settings.Get( Number ) == 20 && settings.Get( Number, SettingsScope.Work ) == 30, "Work overrides never leak into Personal." );
		settings.SetActiveProfile( SettingsProfile.Work );
		CoreChecks.Check( settings.Get( Number ) == 30, "The selected profile supplies effective settings." );
		settings.Set( Number, SettingsScope.Personal, 20 );
		settings.Set( Number, SettingsScope.Global, 40 );
		CoreChecks.Check( settings.Get( Number, SettingsScope.Personal ) == 20, "An explicit equal-to-default override remains independent." );
		settings.Reset( Number, SettingsScope.Personal );
		CoreChecks.Check( settings.Get( Number, SettingsScope.Personal ) == 40, "Reset resumes live global inheritance." );
		settings.Reset( Number, SettingsScope.Work );
		settings.Reset( Number, SettingsScope.Global );
		CoreChecks.Check( settings.Get( Number ) == 10, "Resetting all layers restores the built-in default." );
		CoreChecks.Check( !settings.Set( Number, SettingsScope.Work, 101 ) && settings.Get( Number ) == 10, "Invalid values do not replace valid defaults." );
		CoreChecks.Check( !settings.SetValue( Number, SettingsScope.Global, "wrong type" ), "Untyped editors cannot store mismatched types." );
		app.Shutdown();
	}

	/// <summary>Checks saved profile selection and values after constructing a new application.</summary>
	private static void CheckPersistence()
	{
		var path = Path.Combine( Path.GetTempPath(), "lokn-settings-" + Guid.NewGuid().ToString( "N" ), "settings.json" );
		var app = BuildApp( path );
		app.Settings.Set( Number, SettingsScope.Global, 25 );
		app.Settings.Set( Number, SettingsScope.Work, 50 );
		app.Settings.Set( Enabled, SettingsScope.Personal, false );
		app.Settings.Set( Name, SettingsScope.Work, "Office" );
		app.Settings.SetActiveProfile( SettingsProfile.Work );
		app.Shutdown();

		var reopened = BuildApp( path );
		CoreChecks.Check( reopened.Settings.ActiveProfile == SettingsProfile.Work && reopened.Settings.Get( Number ) == 50, "Profile selection and overrides survive restart." );
		CoreChecks.Check( reopened.Settings.Get( Name ) == "Office" && !reopened.Settings.Get( Enabled, SettingsScope.Personal ), "Text and Boolean values round-trip." );
		CoreChecks.Check( reopened.Settings.Get( Number, SettingsScope.Personal ) == 25, "Unspecified profile values still inherit after restart." );
		using var document = JsonDocument.Parse( File.ReadAllText( path ) );
		CoreChecks.Check( !document.RootElement.GetProperty( "Personal" ).TryGetProperty( Number.Id, out _ ), "Inherited defaults are not copied into saved profiles." );
		reopened.Shutdown();

		// A module renamed with its settings, as the shell's workspace module was, keeps the user's values in every scope.
		File.WriteAllText( path, File.ReadAllText( path ).Replace( "\"preferences.", "\"old." ) );
		var renamed = BuildApp( path );
		CoreChecks.Check( renamed.Settings.Get( Number ) == 10, "Values saved under old names do not apply by themselves." );
		renamed.Settings.MoveSaved( "old.", "preferences." );
		renamed.Settings.MoveSaved( "old.", "preferences." );
		CoreChecks.Check( renamed.Settings.Get( Number ) == 50 && renamed.Settings.Get( Number, SettingsScope.Personal ) == 25 && renamed.Settings.Get( Name ) == "Office"
			&& !File.ReadAllText( path ).Contains( "\"old.", StringComparison.Ordinal ),
			"Moving saved values to a module's new names keeps them in every scope, saves the move, and does nothing the second time." );
		renamed.Shutdown();
	}

	/// <summary>Checks malformed files, invalid stored values, and failed-write behavior.</summary>
	private static void CheckInvalidFiles()
	{
		var directory = Path.Combine( Path.GetTempPath(), "lokn-settings-errors-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( directory );
		var path = Path.Combine( directory, "settings.json" );
		File.WriteAllText( path, "{broken" );
		var app = BuildApp( path );
		CoreChecks.Check( app.LastError is not null && app.Settings.Get( Number ) == 10, "Malformed settings leave the app usable with built-in defaults." );
		CoreChecks.Check( !app.Settings.Set( Number, SettingsScope.Global, 20 ) && File.ReadAllText( path ) == "{broken", "Malformed source files are never overwritten by a later edit." );
		app.Shutdown();

		File.WriteAllText( path, """
		{"Version":1,"ActiveProfile":"Work","Global":{"preferences.number":20},"Work":{"preferences.number":"invalid"},"Personal":{}}
		""" );
		app = BuildApp( path );
		CoreChecks.Check( app.Settings.Get( Number ) == 20 && !app.Settings.HasValue( Number, SettingsScope.Work ), "Invalid stored overrides fall back to valid global defaults." );
		app.Shutdown();

		var blocked = Path.Combine( directory, "not-a-directory" );
		File.WriteAllText( blocked, "preserve" );
		app = BuildApp( Path.Combine( blocked, "settings.json" ) );
		CoreChecks.Check( !app.Settings.Set( Number, SettingsScope.Global, 35 ), "A failed settings write is reported." );
		CoreChecks.Check( app.Settings.Get( Number ) == 35 && app.LastError!.Contains( "session" ), "Failed persistence keeps the live session usable and exposes a warning." );
		CoreChecks.Check( File.ReadAllText( blocked ) == "preserve", "A failed write preserves existing files." );
		File.Move( blocked, blocked + ".original" );
		CoreChecks.Check( app.Settings.Set( Number, SettingsScope.Global, 35 ), "Submitting the same live value retries a previously failed save." );
		CoreChecks.Check( File.Exists( Path.Combine( blocked, "settings.json" ) ), "A corrected storage path receives the retried save." );
		app.Shutdown();
	}

	/// <summary>Checks registration rollback and value retention when a feature is disabled.</summary>
	private static void CheckOwnership()
	{
		var app = BuildApp();
		app.Settings.Set( Number, SettingsScope.Work, 60 );
		app.Unload( "preferences" );
		CoreChecks.Check( app.Settings.Definitions.Count == 0, "Module unload removes its settings schema." );
		app.Enable( "preferences" );
		CoreChecks.Check( app.Settings.Get( Number, SettingsScope.Work ) == 60, "Re-enabling a module restores its saved overrides." );
		CoreChecks.Check( !app.Load( new FixtureModule( "duplicate", builder => builder.Setting( new Setting<int>( "preferences.number", "Invalid owner", "", 1, "Fixture" ) ) ) ), "Modules cannot contribute settings in another module's namespace." );
		CoreChecks.Check( app.Settings.Definitions.Count == 3, "Failed settings registration preserves existing definitions." );
		CoreChecks.Check( !app.Load( new FixtureModule( "staged", builder =>
		{
			var value = new Setting<bool>( "staged.enabled", "Enabled", "", true, "Fixture" );
			builder.Setting( value ).Setting( value );
		} ) ), "Duplicate staged settings are rejected atomically." );
		app.Shutdown();
	}

	/// <summary>Checks the shell setting maps each choice to an opaque or translucent native window configuration.</summary>
	private static void CheckShellBackdrop()
	{
		var app = new LoknApplication();
		CoreChecks.Check( app.Load( new ShellModule() ), "The shell settings register." );
		CoreChecks.Check( app.Settings.Get( ShellSettings.Backdrop ) == ShellSettings.MicaBackdrop, "Mica is the built-in shell backdrop." );

		var window = new Window { Transparent = true };
		ShellBackdrop.Apply( window, ShellSettings.AcrylicBackdrop );
		CoreChecks.Check( window.Backdrop == WindowBackdrop.Blur && window.WindowsBackdrop.Material == WindowsBackdropMaterial.Acrylic
			&& ShellBackdrop.Alpha( ShellSettings.AcrylicBackdrop, 40 ) == 0.4,
			"Acrylic enables the native blur and the chosen surface opacity." );
		ShellBackdrop.Apply( window, ShellSettings.NoBackdrop );
		CoreChecks.Check( window.Backdrop == WindowBackdrop.None && window.Background == Theme.Current.Canvas
			&& ShellBackdrop.Alpha( ShellSettings.NoBackdrop, 40 ) == 1,
			"Turning the backdrop off restores an opaque native window and ignores the opacity settings." );
		app.Shutdown();
	}

	/// <summary>Creates a fixture application without reading or writing the user's settings.</summary>
	private static LoknApplication BuildApp( string? path = null )
	{
		var app = new LoknApplication( path );
		CoreChecks.Check( app.Load( new FixtureModule( "preferences", builder => builder.Setting( Number ).Setting( Enabled ).Setting( Name ) ) ), "Fixture settings register." );
		return app;
	}
}
