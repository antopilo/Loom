using Lokn.Core;
using Lokn.Modules.Browser;

namespace Lokn.Tests;

/// <summary>Exercises address bar parsing and browser registration without opening a window.</summary>
internal static class BrowserChecks
{
	/// <summary>Defines the search template the checks use.</summary>
	private const string Search = "https://search.example/?q={0}";

	/// <summary>Runs the browser checks.</summary>
	internal static void Run()
	{
		CoreChecks.Check( BrowserAddress.Compute( "https://example.com/a?b=1", Search ) == "https://example.com/a?b=1", "A full address loads as typed." );
		CoreChecks.Check( BrowserAddress.Compute( "  example.com/docs ", Search ) == "https://example.com/docs" && BrowserAddress.Compute( "github.com", Search ) == "https://github.com/", "A host gets https." );
		CoreChecks.Check( BrowserAddress.Compute( "localhost:5173", Search ) == "http://localhost:5173/" && BrowserAddress.Compute( "127.0.0.1:8080/x", Search ) == "http://127.0.0.1:8080/x", "Local hosts use http." );
		CoreChecks.Check( BrowserAddress.Compute( @"C:\docs\index.html", Search ) == "file:///C:/docs/index.html", "A drive path opens the file." );
		CoreChecks.Check( BrowserAddress.Compute( "how to cook rice", Search ) == "https://search.example/?q=how%20to%20cook%20rice" && BrowserAddress.Compute( "dotnet", Search ) == "https://search.example/?q=dotnet", "Words are searched." );
		CoreChecks.Check( BrowserAddress.Compute( "javascript:alert(1)", Search )!.StartsWith( "https://search.example/", StringComparison.Ordinal ), "Script addresses are searched, not run." );
		CoreChecks.Check( BrowserAddress.Compute( "   ", Search ) is null, "Blank text loads nothing." );

		var app = new LoknApplication();
		CoreChecks.Check( app.Load( new BrowserModule() ), "The browser module loads." );
		CoreChecks.Check( app.Panels.Types.Any( type => type.Id == "browser.page" ) && app.Commands.Items.Any( command => command.Id == "browser.open-url" ), "The browser panel and its open command are registered." );
		CoreChecks.Check( !app.Settings.SetValue( app.Settings.Definitions.Single( definition => definition.Id == "browser.search" ), SettingsScope.Global, "https://no-placeholder.example/" ), "A search address without {0} is rejected." );
		CheckLinks( app );
		app.Shutdown();
		Console.WriteLine( "PASS browser address parsing, search fallback, local hosts and files, registration, and followed links in new tabs of the browser panel on screen." );
	}

	/// <summary>Verifies a followed link opens a browser panel beside its source, later links open new focused tabs in the browser panel on screen, and agent reports reuse their own tab.</summary>
	private static void CheckLinks( LoknApplication app )
	{
		app.ClearError();
		CoreChecks.Check( app.Load( new FixtureModule( "source", builder => builder.Panel( "page", "Source", _ => new SourceContent() ) ) ), "A source panel type registers." );
		var source = app.Panels.Open( "source.page" );
		var sourcePane = app.Panels.Panes.Single( pane => pane.PanelId == source ).Id;
		app.OpenLink( new Uri( "https://github.com/a/b/pull/1" ), source );
		var browsers = app.Panels.Panels.Where( panel => panel.TypeId == "browser.page" ).ToArray();
		CoreChecks.Check( browsers.Length == 1 && app.Panels.Panes.Any( pane => pane.Id == sourcePane && pane.PanelId == source ), "A followed link opens a browser panel beside its source, which keeps its pane." );
		CoreChecks.Check( app.Panels.Root is DockSplit { Axis: SplitAxis.Horizontal }, "The browser opens to the side of the source." );

		app.Panels.Activate( source );
		app.OpenLink( new Uri( "https://github.com/a/b/pull/2" ), source );
		CoreChecks.Check( app.Panels.Panels.Count( panel => panel.TypeId == "browser.page" ) == 1 && app.Panels.ActivePanelId == browsers[0].Id, "A later link reuses and focuses the side panel." );
		var saved = Capture( app, browsers[0].Id );
		CoreChecks.Check( saved.GetProperty( "Address" ).GetString() == "https://github.com/a/b/pull/2" && ReadTabs( saved ) is ["https://github.com/a/b/pull/1", "https://github.com/a/b/pull/2"] && saved.GetProperty( "Selected" ).GetInt32() == 1, "A later link opens in a new tab, which is selected." );

		app.Panels.Close( browsers[0].Id );
		var own = app.Panels.Open( "browser.page" );
		app.Panels.Activate( source );
		app.OpenLink( new Uri( "https://example.com/" ), source );
		var mine = Capture( app, own );
		CoreChecks.Check( app.Panels.Panels.Count( panel => panel.TypeId == "browser.page" ) == 1 && app.Panels.ActivePanelId == own, "A link opens in the browser panel the user opened, and focuses it." );
		CoreChecks.Check( ReadTabs( mine ).Length == 2 && mine.GetProperty( "Address" ).GetString() == "https://example.com/" && mine.GetProperty( "Selected" ).GetInt32() == 1, "The link opens as a new selected tab beside the panel's home page." );

		app.OpenLink( new Uri( "mailto:someone@example.com" ), source );
		CoreChecks.Check( app.Panels.Panels.Count( panel => panel.TypeId == "browser.page" ) == 1 && app.LastError is null, $"Links that are not web pages are left to other handlers ({app.LastError})." );

		// An agent's html report is saved and shown in a new tab of the browser panel beside its conversation.
		var side = own;
		var arguments = System.Text.Json.JsonDocument.Parse( """{ "html": "<h1>Bench</h1>", "title": "Bench: Results v2!" }""" ).RootElement;
		var result = app.Tools.InvokeAsync( "browser_show", arguments, source, CancellationToken.None ).GetAwaiter().GetResult();
		var report = Directory.GetFiles( BrowserModule.ReportsPath, "*-bench-results-v2.html" ).OrderBy( file => file ).LastOrDefault();
		try
		{
			CoreChecks.Check( !result.IsError && report is not null && File.ReadAllText( report ) == "<h1>Bench</h1>" && result.Text.Contains( report ), $"An html report is saved under a readable name ({result.Text})." );
			var state = Capture( app, side );
			CoreChecks.Check( state.GetProperty( "Address" ).GetString() == new Uri( report! ).AbsoluteUri && ReadTabs( state ).Length == 3 && app.Panels.Panels.Count( panel => panel.TypeId == "browser.page" ) == 1, "The report loads in a new tab of the existing browser panel." );
			var again = System.Text.Json.JsonDocument.Parse( """{ "url": "https://example.com/v3" }""" ).RootElement;
			CoreChecks.Check( !app.Tools.InvokeAsync( "browser_show", again, source, CancellationToken.None ).GetAwaiter().GetResult().IsError, "A second page shows." );
			var replaced = Capture( app, side );
			CoreChecks.Check( replaced.GetProperty( "Address" ).GetString() == "https://example.com/v3" && ReadTabs( replaced ).Length == 3, "The next report replaces the previous report's tab." );
		}
		finally
		{
			if ( report is not null )
				File.Delete( report );
		}

		var both = System.Text.Json.JsonDocument.Parse( """{ "html": "<p>x</p>", "url": "https://example.com" }""" ).RootElement;
		var missing = System.Text.Json.JsonDocument.Parse( """{ "path": "C:\\no\\such\\report.html" }""" ).RootElement;
		CoreChecks.Check( app.Tools.InvokeAsync( "browser_show", both, source, CancellationToken.None ).GetAwaiter().GetResult().IsError
			&& app.Tools.InvokeAsync( "browser_show", missing, source, CancellationToken.None ).GetAwaiter().GetResult().IsError, "Ambiguous arguments and missing files are refused." );
		CheckBackground( app );
	}

	/// <summary>Verifies a report from a chat the user is not looking at, in another profile, never lands in the layout on screen, and appears beside that chat when it comes back.</summary>
	private static void CheckBackground( LoknApplication app )
	{
		CoreChecks.Check( app.Load( new FixtureModule( "work", builder => builder.Panel( "chat", "Work chat", _ => new SourceContent( SettingsProfile.Work ) ) ) ), "A Work chat stand-in registers." );
		var chat = app.Panels.Open( "work.chat" );
		var screen = app.Panels.CaptureLayout();
		app.Panels.HideAll();
		var personal = app.Panels.Open( "browser.page" );
		var before = app.Panels.CaptureLayout().GetRawText();
		Guid[] earlier = [.. app.Panels.Panels.Where( panel => panel.TypeId == "browser.page" ).Select( panel => panel.Id )];
		var html = System.Text.Json.JsonDocument.Parse( """{ "html": "<p>work</p>", "title": "work report" }""" ).RootElement;
		var result = app.Tools.InvokeAsync( "browser_show", html, chat, CancellationToken.None ).GetAwaiter().GetResult();
		var opened = app.Panels.Panels.Where( panel => panel.TypeId == "browser.page" ).Select( panel => panel.Id ).Except( earlier ).ToArray();
		try
		{
			CoreChecks.Check( !result.IsError && opened is [var hidden] && !app.Panels.IsShown( hidden ) && app.Panels.GetWaitingBeside( hidden ) == chat
				&& app.Panels.CaptureLayout().GetRawText() == before && ReadTabs( Capture( app, personal ) ).Length == 1,
				$"A background chat's report opens in its own hidden browser, leaving the layout and browser on screen untouched ({result.Text})." );
			CoreChecks.Check( app.Panels.GetProfile( opened[0] ) == SettingsProfile.Work, "The chat's browser uses the chat's profile." );

			var navigate = System.Text.Json.JsonDocument.Parse( """{ "url": "https://example.com/work" }""" ).RootElement;
			app.Tools.InvokeAsync( "browser_navigate", navigate, chat, CancellationToken.None ).GetAwaiter().GetResult();
			CoreChecks.Check( ReadTabs( Capture( app, personal ) ).Length == 1, "A background chat's browser tools never drive the browser on screen." );

			static bool IsBeside( DockNode node, Guid left, Guid right ) => node is DockSplit split
				&& (split is { First: DockPane { PanelId: var first }, Second: DockPane { PanelId: var second } } && first == left && second == right
					|| IsBeside( split.First, left, right ) || IsBeside( split.Second, left, right ));
			CoreChecks.Check( app.Panels.ApplyLayout( screen ) && app.Panels.IsShown( chat ) && IsBeside( app.Panels.Root, chat, opened[0] ),
				"When the chat comes back on screen, its browser appears beside it." );
		}
		finally
		{
			foreach ( var file in Directory.GetFiles( BrowserModule.ReportsPath, "*-work-report.html" ) )
				File.Delete( file );
		}
	}

	/// <summary>Captures a browser panel's saved state.</summary>
	private static System.Text.Json.JsonElement Capture( LoknApplication app, Guid panelId ) => app.Panels.Sessions.Single( session => session.Info.Id == panelId ).Content!.CaptureState()!.Value;

	/// <summary>Reads the tab addresses from a browser panel's saved state.</summary>
	private static string[] ReadTabs( System.Text.Json.JsonElement state ) => [.. state.GetProperty( "Tabs" ).EnumerateArray().Select( tab => tab.GetString()! )];

	/// <summary>Stands in for a panel links are followed in, such as a chat, optionally pinned to a profile.</summary>
	private sealed class SourceContent( SettingsProfile? profile = null ) : PanelContent
	{
		/// <summary>Gets the profile the stand-in works in.</summary>
		public override SettingsProfile? Profile => profile;

		/// <summary>Builds an empty view.</summary>
		public override Goo.Blob Build() => new Goo.Container();
	}
}
