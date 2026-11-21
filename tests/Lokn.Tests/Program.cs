using Lokn.Tests;

// The terminal sign-in runs "<omp> --profile <name> login"; the fixture ends at once so it never starts a suite.
if ( args.Contains( "--profile" ) && args.Contains( "login" ) )
{
	if ( Environment.GetEnvironmentVariable( OmpUiFixture.SignInVariable ) is { Length: > 0 } marker )
		File.WriteAllText( marker, "signed in" );
	Console.WriteLine( "Fixture login" );
	return;
}

if ( args.Contains( "usage" ) && args.Contains( "--json" ) )
{
	OmpUiFixture.BuildUsage();
	return;
}

if ( args.Contains( "models" ) && args.Contains( "--json" ) )
{
	OmpUiFixture.BuildModelList( args );
	return;
}

if ( args is ["--version"] )
{
	Console.WriteLine( "omp/1.2.3" );
	return;
}

if ( args.Contains( "config" ) && args.Contains( "get" ) )
{
	var key = args[Array.IndexOf( args, "get" ) + 1];
	var value = key switch
	{
		"modelRoles" when args.Contains( "work" ) => """{ "default": "fixture/one:high", "reviewer": "fixture/reviewer:high" }""",
		"modelRoles" => "{}",
		"retry.fallbackChains" => """{ "default": [ "fixture/two:low" ], "reviewer": [] }""",
		"modelPresets" => """{ "Careful": { "modelRoles": { "default": "fixture/one:high", "reviewer": "fixture/reviewer:high" }, "defaultThinkingLevel": "high" } }""",
		"defaultThinkingLevel" => "\"high\"",
		_ => "null",
	};
	Console.WriteLine( $$"""{ "key": "{{key}}", "value": {{value}} }""" );
	return;
}

if ( args.Contains( "config" ) && args.Contains( "list" ) )
{
	// The fixture compacts at 80%, with a fixed trigger for one model and a window cap for the rest of its provider.
	Console.WriteLine( """
		{
			"compaction.enabled": { "value": true, "type": "boolean" },
			"compaction.thresholdPercent": { "value": 80, "type": "number" },
			"compaction.thresholdTokens": { "value": -1, "type": "number" },
			"compaction.reserveTokens": { "type": "number" },
			"compaction.modelThresholds": { "value": { "fixture/one": "f150000", "fixture/*": 100000 }, "type": "object" }
		}
		""" );
	return;
}

if ( args.Contains( "-p" ) )
{
	// The tidy question gets a title per numbered chat; any other question gets one answer.
	Console.WriteLine( args.Any( arg => arg.Contains( "one line per chat", StringComparison.Ordinal ) ) ? "1: Tidied by host\n2: Tidied by host"
		: args.Contains( "--smol" ) ? "Fixture small title" : "Fixture regular answer" );
	return;
}

if ( args.Contains( "--mode" ) && args.Contains( "rpc-ui" ) )
{
	if ( args.Contains( "broken" ) )
	{
		Console.Error.WriteLine( "No default model selected. Sign in and select a model." );
		Console.Error.WriteLine( "Or create a models configuration file." );
		Environment.ExitCode = 1;
		return;
	}
	await OmpUiFixture.RunAsync();
	return;
}

if ( args.Contains( "--omp-setup-only" ) )
{
	await OmpSetupChecks.RunAsync( args );
	return;
}

if ( args.Contains( "--startup-only" ) )
{
	StartupUiChecks.Run();
	return;
}

if ( args.Contains( "--chat-view-only" ) )
{
	ChatChecks.Run();
	ChatViewChecks.Run( args );
	return;
}

if ( args.Contains( "--terminal-view-only" ) )
{
	TerminalUiChecks.Run( args );
	return;
}

if ( args.Contains( "--hotload-only" ) )
{
	HotloadChecks.Run();
	return;
}

if ( args.Contains( "--updates-only" ) )
{
	UpdateChecks.Run();
	await UpgradeChecks.RunAsync();
	return;
}

if ( args.Contains( "--community-only" ) )
{
	CommunityChecks.Run();
	return;
}

var featureChecks = args.Contains( "--features-only" );
var layoutChecks = args.Contains( "--layout-only" );
var conversationChecks = args.Contains( "--conversations-only" );
var ompChecks = args.Contains( "--omp-only" );
var chatChecks = args.Contains( "--chat-only" );
var worktreeChecks = args.Contains( "--worktrees-only" );
var terminalChecks = args.Contains( "--terminal-only" );
var browserChecks = args.Contains( "--browser-only" );
var gitChecks = args.Contains( "--git-only" );
var remoteChecks = args.Contains( "--remote-only" );
var focused = featureChecks || layoutChecks || conversationChecks || ompChecks || chatChecks || worktreeChecks || terminalChecks || browserChecks || gitChecks || remoteChecks;

if ( !focused )
{
	if ( !args.Contains( "--settings-only" ) )
	{
		await CoreChecks.RunAsync();
		IsolationChecks.Run();
		await WorkspaceHostChecks.RunAsync();
		SearchChecks.Run();
		await AgentToolChecks.RunAsync();
		HotloadChecks.Run();
		UpdateChecks.Run();
		await UpgradeChecks.RunAsync();
		CommunityChecks.Run();
		ThemeChecks.Run();
	}
	SettingsChecks.Run();
	if ( args.Contains( "--ui" ) )
		NativeChecks.Run( args );
}
else
{
	if ( featureChecks || layoutChecks )
	{
		LayoutChecks.Run();
		await WorkspaceChecks.RunAsync();
		await HomeChecks.RunAsync();
	}
	if ( featureChecks || conversationChecks )
	{
		ConversationChecks.Run();
		ProjectChecks.Run();
		SearchChecks.RunFeatures();
		MultiHostChatChecks.Run();
	}
	if ( featureChecks || ompChecks )
	{
		await OmpChecks.RunAsync();
		await AgentToolChecks.RunBridgeAsync();
	}
	if ( featureChecks || chatChecks )
	{
		ChatChecks.Run();
		ChatRemoteChecks.Run();
	}
	if ( featureChecks || worktreeChecks )
		await WorktreeChecks.RunAsync();
	if ( featureChecks || terminalChecks )
		await TerminalChecks.RunAsync();
	if ( featureChecks || browserChecks )
		BrowserChecks.Run();
	// The remote suite runs the browser protocol checks too; the browser suite runs them on its own.
	if ( browserChecks && !remoteChecks )
		RemoteBrowserChecks.Run();
	if ( featureChecks || gitChecks )
		await GitChecks.RunAsync();
	if ( featureChecks || remoteChecks )
	{
		await RemoteChecks.RunAsync();
		MemoryTrimChecks.Run();
		RelayChecks.Run();
		StreamChecks.Run();
		await RemoteStreamChecks.RunAsync();
		await RemoteAgentWindowChecks.RunAsync();
		await RemoteWorkspaceChecks.RunAsync();
		await RemoteRelayChecks.RunAsync();
		await RemoteWebChecks.RunAsync();
		await RemoteGatewayChecks.RunAsync();
		RemoteToolChecks.Run();
		RemoteBrowserChecks.Run();
		HostTrayChecks.Run();
		await HandoverChecks.RunAsync();
	}
	if ( args.Contains( "--ui" ) )
	{
		// WebView2 needs a single-threaded apartment, which the async main's thread is not; the window checks share one such thread, Goo's UI thread.
		UiThread.Run( () =>
		{
			FeatureUiChecks.Run( args );
			if ( featureChecks || remoteChecks )
			{
				RemoteUiChecks.Run();
				HostTrayChecks.RunDaemonIcon();
			}
			if ( featureChecks || remoteChecks || browserChecks )
				BrowserUiChecks.Run();
		} );
	}
}

Console.WriteLine( "All requested Lokn checks passed." );
