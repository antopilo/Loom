using System.Diagnostics;
using System.Text.Json;
using Goo;
using Lokn.Core;

namespace Lokn.Tests;

/// <summary>Exercises theme file parsing and live reapplication of the global theme file.</summary>
internal static class ThemeChecks
{
	/// <summary>Defines how long a check waits for the file watcher, in seconds.</summary>
	private const int WatchTimeoutSeconds = 20;

	/// <summary>Runs the theme checks without opening a native window.</summary>
	internal static void Run()
	{
		CheckParse();
		CheckCatalog();
		CheckModule();
		Console.WriteLine( "PASS theme file parsing, partial overrides, rejection, live reload, the Tern catalog, picked themes under file overrides, and preview." );
	}

	/// <summary>Verifies every Tern theme converts, is grouped by brightness, and maps its terminal and UI colors.</summary>
	private static void CheckCatalog()
	{
		var entries = ThemeCatalog.Entries;
		CoreChecks.Check( entries.Count == 103 && entries[0].Name == "lokn" && entries[0].Theme == Theme.Default, "Lokn's theme comes first, followed by all 102 Tern themes." );
		CoreChecks.Check( ThemeCatalog.Find( "porcelain" ) is { IsLight: true } && ThemeCatalog.Find( "dark-abyss" ) is { IsLight: false } && ThemeCatalog.Find( "amethyst" ) is { IsLight: false }, "Themes group by how light their panels are, not by name." );
		var faint = entries.Where( entry => entry.Name != ThemeCatalog.DefaultName ).SelectMany( entry =>
		{
			var theme = entry.Theme;
			Color[] surfaces = [theme.Canvas, theme.Panel, theme.Raised];
			Color[] rows = [.. surfaces, ThemeCatalog.Composite( theme.Hover, theme.Panel )];
			return new[] { ("text", theme.Text, 4.5, rows), ("muted", theme.Muted, 4.5, surfaces), ("accent", theme.Accent, 4.5, surfaces), ("success", theme.Success, 4.5, surfaces), ("error", theme.Error, 4.5, surfaces), ("inactive", theme.Inactive, 3.0, surfaces) }
				.Where( item => item.Item4.Min( surface => ThemeCatalog.ComputeContrast( item.Item2, surface ) ) < item.Item3 - 0.05 )
				.Select( item => $"{entry.Name}.{item.Item1}" );
		} ).ToArray();
		CoreChecks.Check( faint.Length == 0, $"Every Tern theme's text, accent and status colors are readable on all its surfaces: {string.Join( ", ", faint.Take( 8 ) )}" );
		var abyss = ThemeCatalog.Find( "DARK-ABYSS" )!.Theme;
		CoreChecks.Check( abyss.Panel == Color.Parse( "#05070B" ) && abyss.Accent == Color.Parse( "#3FD3FF" ) && abyss.Border == Color.Parse( "#142338" ) && abyss.AnsiMagenta == Color.Parse( "#c77dff" ), "Variables resolve to the terminal and UI colors they name." );
		CoreChecks.Check( abyss.FontFamily == Theme.Default.FontFamily && abyss.Spacing == Theme.Default.Spacing, "Catalog themes change colors only." );
		CoreChecks.Check( Theme.Parse( ThemeModule.FormatTemplate( abyss ), abyss ) == abyss, "A new theme file lists every token without overriding the picked theme." );
		CoreChecks.Check( Theme.Parse( """{ "accent": "#010203" }""", abyss ) == abyss with { Accent = Color.Parse( "#010203" ) }, "File tokens override the picked theme and keep its other colors." );
	}

	/// <summary>Verifies partial overrides keep defaults and invalid files are rejected.</summary>
	private static void CheckParse()
	{
		CoreChecks.Check( Theme.Parse( Theme.Default.Format() ) == Theme.Default, "A formatted theme parses back unchanged." );

		var partial = Theme.Parse( """
			{
				// Comments and trailing commas are allowed.
				"accent": "#ff8800",
				"Spacing": 10,
			}
			""" );
		CoreChecks.Check( partial.Accent == Color.Parse( "#ff8800" ) && partial.Spacing == 10, "A theme file overrides the tokens it names." );
		CoreChecks.Check( partial with
		{
			Accent = Theme.Default.Accent,
			Spacing = Theme.Default.Spacing
		} == Theme.Default, "Tokens a theme file leaves out keep their defaults." );

		CheckRejected( """{ "Acent": "#ff8800" }""", "Acent", "A misspelled token is rejected." );
		CheckRejected( """{ "Accent": "orangey" }""", "orangey", "An unparseable color is rejected." );
		CheckRejected( """{ "FontSize": 0 }""", "FontSize", "Unusable geometry is rejected." );
	}

	/// <summary>Verifies the module applies the file at start, follows saves, keeps the theme on errors and resets on stop.</summary>
	private static void CheckModule()
	{
		var root = Path.Combine( Path.GetTempPath(), "Lokn.Tests", Guid.NewGuid().ToString( "N" ) );
		var file = Path.Combine( root, "theme.json" );
		Directory.CreateDirectory( root );
		File.WriteAllText( file, """{ "Accent": "#112233" }""" );

		var app = new LoknApplication();
		var queued = new List<Action>();
		app.BindDispatcher( action =>
		{
			lock ( queued )
				queued.Add( action );

			return true;
		} );

		try
		{
			CoreChecks.Check( app.Load( new ThemeModule( file ) ) && Theme.Current.Accent == Color.Parse( "#112233" ), "The theme file applies at startup." );

			File.WriteAllText( file, """{ "Accent": "#445566" }""" );
			ApplyUntil( queued, () => Theme.Current.Accent == Color.Parse( "#445566" ), "Saving the theme file reapplies it." );

			File.WriteAllText( file, """{ "Accent": """ );
			ApplyUntil( queued, () => app.LastError is not null, "A broken theme file is reported." );
			CoreChecks.Check( app.LastError!.StartsWith( "Theme" ) && Theme.Current.Accent == Color.Parse( "#445566" ), "A broken theme file keeps the running theme." );

			File.Delete( file );
			ApplyUntil( queued, () => Theme.Current == Theme.Default, "Deleting the theme file restores the built-in theme." );

			File.WriteAllText( file, """{ "Accent": "#778899" }""" );
			ApplyUntil( queued, () => Theme.Current.Accent == Color.Parse( "#778899" ), "Recreating the theme file applies it." );

			app.Settings.Set( ThemeModule.Selected, SettingsScope.Global, "porcelain" );
			var porcelain = ThemeCatalog.Find( "porcelain" )!.Theme;
			CoreChecks.Check( Theme.Current == porcelain with { Accent = Color.Parse( "#778899" ) }, "Picking a theme applies it under the file's tokens." );
			var module = app.GetModule<ThemeModule>();
			module.Preview( Abyss() );
			CoreChecks.Check( Theme.Current == Abyss(), "A preview shows a theme without changing the setting." );
			module.Preview( null );
			CoreChecks.Check( Theme.Current.Panel == porcelain.Panel && app.Settings.Get( ThemeModule.Selected ) == "porcelain", "Ending a preview returns to the picked theme." );
			app.Settings.SetActiveProfile( SettingsProfile.Work );
			app.Settings.Set( ThemeModule.Selected, SettingsScope.Work, "dark-nord" );
			CoreChecks.Check( Theme.Current.Panel == ThemeCatalog.Find( "dark-nord" )!.Theme.Panel, "Each profile can pick its own theme." );
			CoreChecks.Check( !app.Settings.Set( ThemeModule.Selected, SettingsScope.Global, "no-such-theme" ), "Unknown theme names are rejected." );
			CoreChecks.Check( !app.Unload( "theme" ) && Theme.Current.Panel == ThemeCatalog.Find( "dark-nord" )!.Theme.Panel, "The theme is part of the shell and cannot be disabled." );

			static Theme Abyss() => ThemeCatalog.Find( "dark-abyss" )!.Theme;
		}
		finally
		{
			app.Shutdown();
			Theme.Current = Theme.Default;
			Directory.Delete( root, recursive: true );
		}
	}

	/// <summary>Checks that parsing fails with a message naming the offending value.</summary>
	private static void CheckRejected( string json, string named, string message )
	{
		try
		{
			Theme.Parse( json );
		}
		catch ( JsonException error ) when ( error.Message.Contains( named ) )
		{
			return;
		}

		CoreChecks.Check( false, message );
	}

	/// <summary>Applies posted UI work like the UI thread until a condition holds.</summary>
	private static void ApplyUntil( List<Action> queued, Func<bool> condition, string message )
	{
		var clock = Stopwatch.StartNew();
		while ( clock.Elapsed.TotalSeconds < WatchTimeoutSeconds )
		{
			Action[] actions;
			lock ( queued )
			{
				actions = [.. queued];
				queued.Clear();
			}

			foreach ( var action in actions )
				action();

			if ( condition() )
				return;

			Thread.Sleep( 20 );
		}

		CoreChecks.Check( false, message );
	}
}
