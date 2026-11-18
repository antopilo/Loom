using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Lokn.Core;
using Lokn.Modules.Omp;

namespace Lokn.Tests;

/// <summary>Checks profile pinning, lossless transport, request correlation, and owned subprocess shutdown without provider calls.</summary>
internal static class OmpChecks
{
	/// <summary>Runs protocol checks with an isolated synthetic JSONL child rather than authenticating or invoking a model.</summary>
	internal static async Task RunAsync()
	{
		CheckProfiles();
		CheckMcpConfig();
		CheckChunks();
		CheckModelCatalog();
		CheckModelRouting();
		CheckSubagents();
		CheckUsage();
		await CheckSmallAskAsync();
		await CheckTransportAsync();
		Console.WriteLine( "PASS Omp profile isolation, v2 chunks, model catalog and routing details, subagent snapshots, usage limits and resets, request correlation, errors, cancellation, and child shutdown." );
	}

	/// <summary>Checks that bounded auxiliary questions select Omp's small-model role without a provider request.</summary>
	private static async Task CheckSmallAskAsync()
	{
		var app = new LoknApplication();
		var module = new OmpModule();
		CoreChecks.Check( app.Load( module ), "Omp loads for the isolated small-model question check." );
		app.Settings.Set( OmpModule.Executable, SettingsScope.Global, Path.Combine( AppContext.BaseDirectory, "Lokn.Tests.exe" ) );
		var answer = await module.Service.AskSmallAsync( SettingsProfile.Work, "Name this.", "Bounded material." );
		CoreChecks.Check( answer.Trim() == "Fixture small title", "Auxiliary questions use the small-model role in an isolated print process." );
		var usage = await module.Service.GetUsageAsync( SettingsProfile.Work );
		CoreChecks.Check( usage.Accounts is [{ Provider: "fixture", Limits.Count: 2 }] && ReferenceEquals( module.Service.GetCachedUsage( SettingsProfile.Work ), usage ),
			"An explicit usage read retains the latest profile report for prompt-bar reuse." );
		app.Shutdown();
	}

	/// <summary>Checks that a usage document becomes accounts with their limits, resets and saved resets, and that waits read plainly.</summary>
	private static void CheckUsage()
	{
		var report = OmpService.BuildUsage( """
			{ "generatedAt": 1794948590035, "reports": [
				{ "provider": "anthropic",
					"limits": [
						{ "label": "Claude 5 Hour", "window": { "resetsAt": 1794949800000 }, "amount": { "usedFraction": 1 }, "status": "exhausted" },
						{ "label": "Claude 7 Day", "window": { "resetsAt": 1795053600000 }, "amount": { "usedFraction": 0.37 }, "status": "ok" },
						{ "label": "No window" }
					],
					"metadata": { "email": "someone@example.com", "accountId": "abc" },
					"resetCredits": { "availableCount": 4, "redeemableCount": 1 } },
				{ "provider": "local" }
			] }
			""" );
		var account = report.Accounts[0];
		CoreChecks.Check( report.Accounts.Count == 2 && report.FetchedAt == DateTimeOffset.FromUnixTimeMilliseconds( 1794948590035 ) && account is { Provider: "anthropic", Account: "someone@example.com", SavedResets: 4, UsableResets: 1 },
			"A usage report keeps each account's provider, name and saved resets." );
		CoreChecks.Check( account.Limits[0] is { Label: "Claude 5 Hour", UsedFraction: 1, IsExhausted: true } && account.Limits[0].ResetsAt == DateTimeOffset.FromUnixTimeMilliseconds( 1794949800000 ) && account.Limits[1] is { UsedFraction: 0.37, IsExhausted: false },
			"A limit keeps its spent share, its reset time and whether it is exhausted." );
		CoreChecks.Check( account.Limits[2] is { UsedFraction: 0, ResetsAt: null, IsExhausted: false } && report.Accounts[1] is { Account: "", Limits.Count: 0, SavedResets: 0 },
			"A limit or account with missing fields reads as empty rather than failing." );

		var now = DateTimeOffset.FromUnixTimeMilliseconds( 1794948590035 );
		CoreChecks.Check( OmpUsageContent.FormatDuration( TimeSpan.FromMinutes( 20.2 ) ) == "21m" && OmpUsageContent.FormatDuration( new( 2, 10, 0 ) ) == "2h 10m" && OmpUsageContent.FormatDuration( new( 1, 5, 30, 0 ) ) == "1d 5h",
			"A wait shows its two largest units." );
		CoreChecks.Check( OmpUsageContent.FormatReset( account.Limits[0].ResetsAt, now ).StartsWith( "resets in 21m (", StringComparison.Ordinal ) && OmpUsageContent.FormatReset( null, now ) == "" && OmpUsageContent.FormatReset( now.AddMinutes( -1 ), now ) == "resets now",
			"A reset shows the time left and the clock time, nothing when unknown, and now when it has passed." );
	}

	/// <summary>Checks that the model list keeps each model's limits, prices and abilities, and that recent choices stay short and newest first.</summary>
	private static void CheckModelCatalog()
	{
		var models = OmpService.BuildModels( """
			{ "models": [
				{ "provider": "anthropic", "id": "fable", "name": "Fable", "contextWindow": 1000000, "maxTokens": 128000, "reasoning": true, "input": ["text", "image"], "cost": { "input": 10, "output": 50 } },
				{ "provider": "copilot", "id": "free", "contextWindow": 128000, "cost": { "input": 0, "output": 0 } },
				{ "provider": "local", "id": "bare" }
			] }
			""" );
		CoreChecks.Check( models[0] is { Selector: "anthropic/fable", ContextWindow: 1_000_000, MaxOutput: 128_000, InputCost: 10, OutputCost: 50, IsReasoning: true, HasImages: true }, "A model keeps its context window, output limit, prices and abilities." );
		CoreChecks.Check( OmpModelPicker.FormatTokens( models[0].ContextWindow ) == "1M" && OmpModelPicker.FormatTokens( 272_000 ) == "272k" && OmpModelPicker.FormatPrice( models[0] ) == "$10 · $50",
			"The picker shows context windows as 1M or 272k and prices as input · output per million." );
		CoreChecks.Check( OmpModelPicker.FormatPrice( models[1] ) == "free" && OmpModelPicker.FormatPrice( models[2] ) == "" && models[2] is { ContextWindow: 0, IsReasoning: false },
			"A model without prices or limits shows nothing for them rather than failing." );

		var app = new LoknApplication();
		var module = new OmpModule();
		app.Load( module );
		foreach ( var selector in new[] { "a/1", "a/2", "a/3", "a/4", "a/5", "a/6", "a/2" } )
			module.Service.RecordRecentModel( SettingsProfile.Work, selector );
		CoreChecks.Check( module.Service.GetRecentModels( SettingsProfile.Work ).SequenceEqual( ["a/2", "a/6", "a/5", "a/4", "a/3"] ) && module.Service.GetRecentModels( SettingsProfile.Personal ).Count == 0,
			"Recent models are per profile, newest first, without repeats, and capped." );
		app.Shutdown();

		CoreChecks.Check( OmpService.FormatModelName( "claude-opus-4-5-20251101" ) == "Claude Opus 4.5" && OmpService.FormatModelName( "gpt-5.1-codex" ) == "Gpt 5.1 Codex",
			"A model the catalog has not listed yet reads as a name, with version parts joined and release dates dropped." );
	}

	/// <summary>Checks role, explicit-empty fallback and preset parsing from Omp configuration documents.</summary>
	private static void CheckModelRouting()
	{
		var routing = OmpService.BuildModelRouting(
			"""{ "value": { "default": "openai/main:high", "smol": "openai/small" } }""",
			"""{ "value": { "default": ["anthropic/backup"], "tiny": [] } }""",
			"""{ "value": { "economy": { "modelRoles": { "default": "openai/small:low" }, "defaultThinkingLevel": "low" } } }""",
			"""{ "value": "high" }""" );
		CoreChecks.Check( routing.Roles["default"] == "openai/main:high" && routing.Fallbacks["default"].SequenceEqual( ["anthropic/backup"] )
			&& routing.Fallbacks["tiny"].Count == 0, "Routing keeps thinking suffixes, fallback order and an explicit empty fallback chain." );
		CoreChecks.Check( routing.Presets["economy"].Roles["default"] == "openai/small:low"
			&& routing.Presets["economy"].DefaultThinkingLevel == "low" && routing.DefaultThinkingLevel == "high",
			"Named presets retain role assignments and their default thinking level." );
	}

	/// <summary>Checks normalization of Omp's nested subagent progress snapshot.</summary>
	private static void CheckSubagents()
	{
		using var document = JsonDocument.Parse( """
			{ "subagents": [{
				"id": "ReviewOne", "agent": "reviewer", "status": "running", "sessionFile": "review.jsonl",
				"progress": {
					"id": "ReviewOne", "agent": "reviewer", "status": "running", "task": "Review auth",
					"stage": "Inspecting tokens", "currentTool": "read", "currentToolArgs": "Auth.cs",
					"resolvedModel": "openai/gpt-5.6:high", "tokens": 1234, "contextTokens": 500, "contextWindow": 200000,
					"cost": 0.12, "durationMs": 4500
				}
			}] }
			""" );
		var agent = OmpAgentHubContent.BuildSubagents( document.RootElement ).Single();
		CoreChecks.Check( agent is { Id: "ReviewOne", Agent: "reviewer", Status: "running", Task: "Review auth", Activity: "Inspecting tokens",
			CurrentTool: "read Auth.cs", Model: "openai/gpt-5.6:high", Tokens: 1234, ContextTokens: 500, ContextWindow: 200000, Cost: 0.12,
			DurationMilliseconds: 4500, SessionFile: "review.jsonl" }, "Agent Hub snapshots keep identity, activity, tool, model, usage, context, cost and transcript." );
	}

	/// <summary>Checks explicit Work and Personal profile arguments and scoped settings resolution.</summary>
	private static void CheckProfiles()
	{
		var app = new LoknApplication();
		var module = new OmpModule();
		CoreChecks.Check( app.Load( module ), "Omp registers its own settings and account panel." );
		app.Settings.Set( OmpModule.ProfileName, SettingsScope.Work, "company" );
		app.Settings.Set( OmpModule.Model, SettingsScope.Work, "provider/work-model" );
		var directory = Path.Combine( Path.GetTempPath(), "Lokn.OmpChecks", Guid.NewGuid().ToString( "N" ) );
		var work = module.Service.BuildStartInfo( SettingsProfile.Work, Environment.CurrentDirectory, directory );
		var personal = module.Service.BuildStartInfo( SettingsProfile.Personal, Environment.CurrentDirectory, directory );
		CoreChecks.Check( work.ArgumentList.Contains( "company" ) && work.ArgumentList.Contains( "provider/work-model" ), "Work captures its credential profile and model." );
		CoreChecks.Check( personal.ArgumentList.Contains( "personal" ) && !personal.ArgumentList.Contains( "provider/work-model" ), "Personal inherits independently of Work." );
		CoreChecks.Check( !work.UseShellExecute && work.ArgumentList.Contains( "rpc-ui" ) && !work.ArgumentList.Contains( "--no-ui" ), "Agent requests use shell-free structured RPC with tool dialogs." );

		// Omp keeps only the last --append-system-prompt, so Lokn's part and the user's must arrive as one.
		app.Settings.Set( OmpModule.SystemPrompt, SettingsScope.Work, "  Answer briefly.\nUse British spelling.  " );
		static string[] Appended( System.Diagnostics.ProcessStartInfo start ) =>
			[.. start.ArgumentList.Select( ( argument, index ) => (argument, index) ).Where( item => item.argument == "--append-system-prompt" ).Select( item => start.ArgumentList[item.index + 1] )];
		var instructed = Appended( module.Service.BuildStartInfo( SettingsProfile.Work, Environment.CurrentDirectory, directory ) );
		CoreChecks.Check( instructed is [var both] && both.StartsWith( OmpService.HostToolsPrompt, StringComparison.Ordinal ) && both.EndsWith( "\n\nAnswer briefly.\nUse British spelling.", StringComparison.Ordinal ),
			"A profile's chat system prompt follows Lokn's own in one appended prompt, trimmed." );
		CoreChecks.Check( Appended( module.Service.BuildStartInfo( SettingsProfile.Personal, Environment.CurrentDirectory, directory ) ) is [OmpService.HostToolsPrompt],
			"Another profile's chat system prompt stays out of a chat." );
		app.Settings.Set( OmpModule.HostTools, SettingsScope.Global, false );
		CoreChecks.Check( Appended( module.Service.BuildStartInfo( SettingsProfile.Work, Environment.CurrentDirectory, directory ) ) is ["Answer briefly.\nUse British spelling."]
			&& Appended( module.Service.BuildStartInfo( SettingsProfile.Personal, Environment.CurrentDirectory, directory ) ).Length == 0,
			"Without Lokn's tools a chat gets only the user's system prompt, and nothing when it is blank." );
		app.Shutdown();
	}

	/// <summary>Checks that Lokn edits Omp's MCP file the way Omp reads it: per-profile path, order and unknown fields kept, Omp's validation rules.</summary>
	private static void CheckMcpConfig()
	{
		var app = new LoknApplication();
		var module = new OmpModule();
		CoreChecks.Check( app.Load( module ), "Omp loads for the MCP config check." );
		var root = Path.Combine( Path.GetTempPath(), "Lokn.OmpChecks", Guid.NewGuid().ToString( "N" ) );
		module.Service.ProfilesDirectory = Path.Combine( root, "profiles" );
		app.Settings.Set( OmpModule.ProfileName, SettingsScope.Personal, "default" );
		CoreChecks.Check( module.Service.GetMcpConfigPath( SettingsProfile.Work ) == Path.Combine( root, "profiles", "work", "agent", "mcp.json" )
			&& module.Service.GetMcpConfigPath( SettingsProfile.Personal ) == Path.Combine( root, "agent", "mcp.json" ),
			"A named Omp profile keeps its own MCP file and the default profile uses the shared agent folder." );

		var path = module.Service.GetMcpConfigPath( SettingsProfile.Work );
		Directory.CreateDirectory( Path.GetDirectoryName( path )! );
		File.WriteAllText( path, """
			{ "mcpServers": {
				"a": { "command": "npx", "args": ["-y", "a"], "timeout": 5000 },
				"b": { "type": "http", "url": "https://b.example/mcp" } },
			  "disabledServers": ["b"], "custom": 1 }
			""" );
		CoreChecks.Check( OmpMcpConfig.Read( path ) is [{ Name: "a", Transport: "stdio", Target: "npx -y a", IsEnabled: true }, { Name: "b", Transport: "http", IsEnabled: false }],
			"Servers read in file order with inferred transports, and the user denylist marks one off." );

		OmpMcpConfig.Save( path, "renamed", """{ "command": "uvx", "args": ["a"] }""", "a" );
		OmpMcpConfig.SetEnabled( path, "b", true );
		OmpMcpConfig.Save( path, "c", OmpMcpConfig.Template, null );
		OmpMcpConfig.SetEnabled( path, "c", false );
		var saved = JsonDocument.Parse( File.ReadAllText( path ) ).RootElement;
		CoreChecks.Check( saved.EnumerateObject().First().Name == "$schema" && saved.GetProperty( "custom" ).GetInt32() == 1 && !saved.TryGetProperty( "disabledServers", out _ )
			&& OmpMcpConfig.Read( path ) is [{ Name: "renamed", Target: "uvx a", IsEnabled: true }, { Name: "b", IsEnabled: true }, { Name: "c", IsEnabled: false }],
			"A rename keeps the server's place, enabling clears the denylist, and fields Lokn does not know survive." );

		OmpMcpConfig.Remove( path, "b" );
		CoreChecks.Check( OmpMcpConfig.Read( path ).Select( server => server.Name ).SequenceEqual( ["renamed", "c"] ), "Remove deletes only that server." );

		static bool Rejects( Action action )
		{
			try
			{
				action();
				return false;
			}
			catch ( Exception exception ) when ( exception is FormatException or InvalidOperationException )
			{
				return true;
			}
		}

		CoreChecks.Check( Rejects( () => OmpMcpConfig.Save( path, "c", OmpMcpConfig.Template, null ) )
			&& Rejects( () => OmpMcpConfig.Save( path, "two  spaces", OmpMcpConfig.Template, null ) )
			&& Rejects( () => OmpMcpConfig.Save( path, "x", """{ "command": "a", "url": "https://x" }""", null ) )
			&& Rejects( () => OmpMcpConfig.Save( path, "x", """{ "type": "http" }""", null ) )
			&& Rejects( () => OmpMcpConfig.Save( path, "x", """{ "type": "ws", "url": "https://x" }""", null ) )
			&& Rejects( () => OmpMcpConfig.Save( path, "x", "[1]", null ) ),
			"Duplicate or invalid names and definitions Omp would reject are refused." );
		CoreChecks.Check( OmpMcpConfig.Read( path ).Count == 2, "A refused save leaves the file unchanged." );
		app.Shutdown();
		Directory.Delete( root, true );
	}

	/// <summary>Checks chunk ordering and exact UTF-8 reconstruction across a split multibyte character.</summary>
	private static void CheckChunks()
	{
		var decoder = new OmpFrameDecoder();
		var expected = "{\"type\":\"message\",\"text\":\"A世界\"}";
		var bytes = Encoding.UTF8.GetBytes( expected );
		var cut = Array.IndexOf( bytes, (byte)0xe4 ) + 1;
		string BuildChunk( int index, byte[] content ) => JsonSerializer.Serialize( new
		{
			type = "rpc_chunk",
			chunkId = "frame",
			index,
			count = 2,
			byteLength = bytes.Length,
			data = Convert.ToBase64String( content )
		} );
		CoreChecks.Check( decoder.Decode( BuildChunk( 0, bytes[..cut] ) ) is null, "Partial chunks remain unpublished." );
		CoreChecks.Check( decoder.Decode( BuildChunk( 1, bytes[cut..] ) )!.Value.GetProperty( "text" ).GetString() == "A世界", "Chunk decoder preserves Unicode exactly." );
		try
		{
			new OmpFrameDecoder().Decode( BuildChunk( 1, bytes[cut..] ) );
			throw new InvalidOperationException( "Out-of-order chunks were accepted." );
		}
		catch ( InvalidDataException ) { }
	}

	/// <summary>Checks a real redirected child with synthetic protocol frames and no provider credentials.</summary>
	private static async Task CheckTransportAsync()
	{
		const string script = "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); [Console]::WriteLine('{\"type\":\"ready\",\"supportedProtocolVersions\":[1,2]}'); while (($line = [Console]::ReadLine()) -ne $null) { $r = $line | ConvertFrom-Json; if ($r.type -eq 'wait') { continue }; $result = @{type='response';id=$r.id;command=$r.type;success=($r.type -ne 'fail');data=@{echo=$r.type;text='café — 👋 日本語'};error='Fixture rejection'}; [Console]::WriteLine(($result | ConvertTo-Json -Compress)) }; Start-Sleep -Milliseconds 200";
		var start = new ProcessStartInfo
		{
			FileName = "powershell.exe",
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.Latin1,
		};
		foreach ( var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", script } )
			start.ArgumentList.Add( argument );

		var session = new OmpSession( SettingsProfile.Work, start );
		using var timeout = new CancellationTokenSource( TimeSpan.FromSeconds( 15 ) );
		try
		{
			await session.StartAsync( timeout.Token );
			var responses = await Task.WhenAll( session.RequestAsync( "one", cancellationToken: timeout.Token ), session.RequestAsync( "two", cancellationToken: timeout.Token ) );
			CoreChecks.Check( responses[0].GetProperty( "echo" ).GetString() == "one" && responses[1].GetProperty( "echo" ).GetString() == "two", "Concurrent responses correlate by request id." );
			CoreChecks.Check( responses[0].GetProperty( "text" ).GetString() == "café — 👋 日本語", "Raw UTF-8 punctuation, accents, emoji, and CJK survive a Windows subprocess despite the inherited code page." );
			CoreChecks.Check( start.StandardInputEncoding!.GetPreamble().Length == 0, "Omp input never receives a UTF-8 BOM before its first JSON frame." );
			CoreChecks.Check( session.Profile == SettingsProfile.Work, "An opened session retains its captured profile." );
			try
			{
				await session.RequestAsync( "fail", cancellationToken: timeout.Token );
				throw new Exception( "Backend failure was not surfaced." );
			}
			catch ( InvalidOperationException error ) when ( error.Message == "Fixture rejection" ) { }

			using var cancelled = new CancellationTokenSource();
			var pending = session.RequestAsync( "wait", cancellationToken: cancelled.Token );
			cancelled.Cancel();
			try
			{
				await pending;
				throw new Exception( "Cancelled request remained active." );
			}
			catch ( OperationCanceledException ) { }
		}
		finally
		{
			var uiContext = new NonPumpingContext();
			var previous = SynchronizationContext.Current;
			Task closing;
			try
			{
				SynchronizationContext.SetSynchronizationContext( uiContext );
				closing = session.StopAsync();
			}
			finally
			{
				SynchronizationContext.SetSynchronizationContext( previous );
			}

			var unloading = Task.Run( () =>
			{
				var stopping = session.StopAsync();
				CoreChecks.Check( ReferenceEquals( closing, stopping ), "Panel close and module unload share the same cleanup completion task." );
				stopping.GetAwaiter().GetResult();
			} );
			await unloading.WaitAsync( TimeSpan.FromSeconds( 5 ) );
			CoreChecks.Check( closing.IsCompletedSuccessfully && ReferenceEquals( closing, session.StopAsync() ), "Repeated callers observe completed child cleanup." );
			CoreChecks.Check( uiContext.Posts == 0, "Child cleanup completes without posting to a blocked UI context." );
		}
	}
	/// <summary>Represents a closing window whose UI synchronization context no longer pumps callbacks.</summary>
	private sealed class NonPumpingContext : SynchronizationContext
	{
		/// <summary>Counts attempted UI posts during child cleanup.</summary>
		private int _posts;

		/// <summary>Gets the count of callbacks that would stall on the closing window.</summary>
		internal int Posts => Volatile.Read( ref _posts );

		/// <summary>Records callbacks without running them, matching a blocked shutdown UI.</summary>
		public override void Post( SendOrPostCallback callback, object? state ) => Interlocked.Increment( ref _posts );
	}

}
