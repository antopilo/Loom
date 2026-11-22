using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Lokn.Core;
using Lokn.Modules.Omp;

namespace Lokn.Tests;

/// <summary>Checks the agent tool registry, the workspace tools, and the Omp host-tool bridge.</summary>
internal static class AgentToolChecks
{
	/// <summary>Runs registry and workspace tool checks without a window or an agent.</summary>
	internal static async Task RunAsync()
	{
		await CheckRegistryAsync();
		await CheckHostAsync();
		await CheckWorkspaceAsync();
		Console.WriteLine( "PASS agent tool registration, failures as results, module removal, the calling host, and workspace state, panel capture, open, move, focus, close and command tools." );
	}

	/// <summary>Runs the host-tool protocol against a synthetic Omp child.</summary>
	internal static async Task RunBridgeAsync()
	{
		await CheckBridgeAsync();
		Console.WriteLine( "PASS Omp host tools: full publishing, economy group loading, calls answered with results, and withdrawal when agent control is off." );
	}

	/// <summary>Checks naming, duplicate rejection, failure containment and unload.</summary>
	private static async Task CheckRegistryAsync()
	{
		var app = new LoknApplication();
		var changes = 0;
		app.Tools.Changed += () => changes++;
		CoreChecks.Check( app.Load( new FixtureModule( "my.tools", builder => builder
			.Tool( "echo", "Echoes text.", """{ "type": "object" }""", ( context, arguments ) => new( arguments.GetProperty( "text" ).GetString()! ) )
			.Tool( "refuse", "Refuses.", """{ "type": "object" }""", ( _, _ ) => throw new ArgumentException( "Not like that." ) ) ) ), "A module registers agent tools." );
		CoreChecks.Check( app.Tools.Items.Select( tool => tool.Name ).SequenceEqual( ["my_tools_echo", "my_tools_refuse"] ) && changes == 1,
			"Tool names join module and local identifiers with only provider-safe characters, and registration reports a change." );

		var echoed = await app.Tools.InvokeAsync( "my_tools_echo", JsonDocument.Parse( """{ "text": "hi" }""" ).RootElement, null, CancellationToken.None );
		var refused = await app.Tools.InvokeAsync( "my_tools_refuse", default, null, CancellationToken.None );
		var missing = await app.Tools.InvokeAsync( "nothing", default, null, CancellationToken.None );
		CoreChecks.Check( echoed is { Text: "hi", IsError: false } && refused is { Text: "Not like that.", IsError: true } && missing.IsError && app.LastError is null,
			"Tools return results, and refusals and unknown names come back to the agent as errors without a shell notice." );

		CoreChecks.Check( !app.Load( new FixtureModule( "my_tools", builder => builder.Tool( "echo", "Clash.", """{ "type": "object" }""", ( _, _ ) => new( "" ) ) ) ),
			"A tool whose provider-safe name clashes with another is rejected with its module." );
		app.ClearError();
		app.Unload( "my.tools" );
		CoreChecks.Check( app.Tools.Items.Count == 0 && changes == 2, "Unloading a module removes its tools and reports the change." );
		app.Shutdown();
	}

	/// <summary>Checks that a tool sees the host whose agent called it, and none for This PC.</summary>
	private static async Task CheckHostAsync()
	{
		var app = new LoknApplication();
		app.Load( new FixtureModule( "hosted", builder => builder
			.Tool( "where", "Names the calling host.", """{ "type": "object" }""", ( context, _ ) => new( context.Host ?? "this PC" ) ) ) );
		var remote = await app.Tools.InvokeAsync( "hosted_where", default, null, CancellationToken.None, host: "desk" );
		var local = await app.Tools.InvokeAsync( "hosted_where", default, null, CancellationToken.None );
		CoreChecks.Check( remote.Text == "desk" && local.Text == "this PC", "A tool sees the paired host whose agent called it, and no host for This PC's agents." );
		app.Shutdown();
	}

	/// <summary>Checks that an agent can inspect and arrange panels and run commands through the workspace tools.</summary>
	private static async Task CheckWorkspaceAsync()
	{
		var app = new LoknApplication();
		app.Load( new ShellModule() );
		app.Load( new FixtureModule( "fixture", builder => builder
			.Panel( "chat", "Chat", _ => new FixtureContent() )
			.Panel( "notes", "Notes", _ => new FixtureContent() ) ) );
		var chat = app.Panels.Open( "fixture.chat" );
		async Task<AgentToolResult> Call( string name, string arguments = "{}" ) =>
			await app.Tools.InvokeAsync( name, JsonDocument.Parse( arguments ).RootElement, chat, CancellationToken.None );

		var state = JsonDocument.Parse( ( await Call( "shell_state" ) ).Text ).RootElement;
		CoreChecks.Check( state.GetProperty( "self" ).GetGuid() == chat && state.GetProperty( "layout" ).GetProperty( "type" ).GetString() == "fixture.chat"
			&& state.GetProperty( "panelTypes" ).EnumerateArray().Any( type => type.GetProperty( "type" ).GetString() == "fixture.notes" ),
			"The state tool shows the agent's own panel, the layout and the openable panel types." );

		var region = new PanelCaptureRegion( 0.25, 0.125, 0.5, 0.75 );
		CoreChecks.Check( region.ToPixels( 100, 80 ) == (25, 10, 50, 60), "Panel capture regions map logical window fractions to image pixels." );
		var capture = await Call( "shell_capture_panel", """{ "panel": "self" }""" );
		CoreChecks.Check( capture.IsError && capture.Text.Contains( "mounted shell window", StringComparison.Ordinal ) && app.Panels.Panels.Count == 1,
			"The shell capture tool is available by default and fails without opening or changing panels when no window is mounted." );

		var opened = await Call( "shell_open_panel", """{ "type": "fixture.notes", "beside": "self", "placement": "left" }""" );
		var notes = JsonDocument.Parse( opened.Text ).RootElement.GetProperty( "panel" ).GetGuid();
		var root = app.Panels.Root as DockSplit;
		CoreChecks.Check( root is { Axis: SplitAxis.Horizontal, First: DockPane { PanelId: var left }, Second: DockPane { PanelId: var right } } && left == notes && right == chat,
			"Opening beside self on the left docks the new panel to the left of the agent's panel." );

		var moved = await Call( "shell_move_panel", $$"""{ "panel": "{{notes.ToString()[..8]}}", "target": "self", "placement": "bottom" }""" );
		CoreChecks.Check( !moved.IsError && app.Panels.Root is DockSplit { Axis: SplitAxis.Vertical, Second: DockPane { PanelId: var below } } && below == notes,
			"Moving by an id prefix docks a panel below another." );

		await Call( "shell_focus_panel", """{ "panel": "self" }""" );
		CoreChecks.Check( app.Panels.ActivePanelId == chat, "Focusing makes a panel active." );
		var closeSelf = await Call( "shell_close_panel", """{ "panel": "self" }""" );
		var badType = await Call( "shell_open_panel", """{ "type": "fixture.none" }""" );
		CoreChecks.Check( closeSelf.IsError && badType.IsError && app.Panels.Panels.Count == 2, "An agent cannot close its own panel, and unknown panel types are refused." );

		var commands = await Call( "shell_list_commands", """{ "query": "split right" }""" );
		var split = await Call( "shell_run_command", """{ "id": "shell.split-right" }""" );
		CoreChecks.Check( commands.Text.Contains( "shell.split-right" ) && !split.IsError && app.Panels.Panes.Count == 3, "Agents find palette commands and run them like the user." );

		await Call( "shell_close_panel", $$"""{ "panel": "{{notes}}" }""" );
		CoreChecks.Check( app.Panels.Panels.Count == 1 && app.LastError is null, "Closing another panel removes it." );
		app.Shutdown();
	}

	/// <summary>Checks set_host_tools, a host_tool_call answered with host_tool_result, and withdrawal through the setting.</summary>
	private static async Task CheckBridgeAsync()
	{
		const string script = """
			[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
			[Console]::WriteLine('{"type":"ready","supportedProtocolVersions":[1]}')
			$loadRequested = $false
			while (($line = [Console]::ReadLine()) -ne $null) {
				$r = $line | ConvertFrom-Json
				if ($r.type -eq 'set_host_tools') {
					$names = @($r.tools | ForEach-Object { $_.name })
					[Console]::WriteLine((@{ type = 'response'; id = $r.id; command = $r.type; success = $true; data = @{ toolNames = $names } } | ConvertTo-Json -Compress -Depth 5))
					[Console]::WriteLine((@{ type = 'fixture_tools'; names = $names; count = $names.Count } | ConvertTo-Json -Compress -Depth 5))
					if (($names -contains 'lokn_load_tools') -and -not $loadRequested) {
						$loadRequested = $true
						[Console]::WriteLine('{"type":"host_tool_call","id":"host_load","toolCallId":"tl","toolName":"lokn_load_tools","arguments":{"groups":["fixture"]}}')
					}
					if ($names -contains 'fixture_echo') { [Console]::WriteLine('{"type":"host_tool_call","id":"host_1","toolCallId":"t1","toolName":"fixture_echo","arguments":{"text":"hi"}}') }
					continue
				}
				if ($r.type -eq 'host_tool_result') {
					[Console]::WriteLine((@{ type = 'fixture_result'; id = $r.id; isError = $r.isError; text = $r.result.content[0].text } | ConvertTo-Json -Compress -Depth 5))
				}
			}
			""";
		var start = new ProcessStartInfo
		{
			FileName = "powershell.exe",
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		foreach ( var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", script } )
			start.ArgumentList.Add( argument );

		var app = new LoknApplication();
		app.Load( new OmpModule() );
		app.Settings.Set( OmpModule.Economy, SettingsScope.Work, true );
		app.Load( new FixtureModule( "fixture", builder => builder.Tool( "echo", "Echoes text and the caller.", """{ "type": "object" }""",
			( context, arguments ) => new( $"{arguments.GetProperty( "text" ).GetString()} from {context.Caller}" ) ) ) );
		var caller = Guid.NewGuid();
		var frames = new BlockingCollection<JsonElement>();
		var session = new OmpSession( SettingsProfile.Work, start );
		session.FrameReceived += frame => frames.Add( frame );
		using var timeout = new CancellationTokenSource( TimeSpan.FromSeconds( 20 ) );
		JsonElement Next( string type )
		{
			while ( true )
			{
				var frame = frames.Take( timeout.Token );
				if ( frame.GetProperty( "type" ).GetString() == type )
					return frame;
			}
		}

		try
		{
			await session.StartAsync( timeout.Token );
			using var bridge = new OmpToolBridge( session, app, () => caller, SettingsProfile.Work, action =>
			{
				action();
				return true;
			} );
			bridge.Publish();
			var initial = Next( "fixture_tools" );
			var published = Next( "fixture_tools" );
			JsonElement result;
			do
				result = Next( "fixture_result" );
			while ( result.GetProperty( "id" ).GetString() != "host_1" );
			var initialNames = initial.GetProperty( "names" ).EnumerateArray().Select( name => name.GetString() ).ToArray();
			CoreChecks.Check( initialNames.Contains( "lokn_load_tools" ) && !initialNames.Contains( "fixture_echo" ),
				"Economy mode initially offers a loader instead of an unrelated feature group's schemas." );
			CoreChecks.Check( published.GetProperty( "names" ).EnumerateArray().Any( name => name.GetString() == "fixture_echo" ),
				"The loader publishes a requested Lokn feature group for this chat." );
			CoreChecks.Check( result.GetProperty( "text" ).GetString() == $"hi from {caller}" && !result.GetProperty( "isError" ).GetBoolean(),
				"A host_tool_call runs the newly loaded tool for the calling panel and is answered with host_tool_result." );

			app.Settings.Set( OmpModule.HostTools, SettingsScope.Global, false );
			CoreChecks.Check( Next( "fixture_tools" ).GetProperty( "count" ).GetInt32() == 0, "Turning agent control off withdraws every tool from connected sessions." );
		}
		finally
		{
			await session.StopAsync();
			app.Shutdown();
		}
	}
}
