using System.Diagnostics;
using System.Text.Json;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Conversations;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;
using Lokn.Modules.Remote;
using Lokn.Modules.Workspaces;

namespace Lokn.Tests;

/// <summary>Checks host tools (docs/remote.md, Host tools): a daemon-role client lends the host its tools, whose calls run on the client, and a
/// remote-role client lends none; in process over in-memory connections, with temporary folders only.</summary>
internal static class RemoteToolChecks
{
	/// <summary>Defines how long one step may take.</summary>
	private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds( 30 );

	/// <summary>Runs the checks.</summary>
	internal static void Run()
	{
		var folder = Path.Combine( Path.GetTempPath(), "lokn-tools-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( folder );
		var hostQueue = new List<Action>();
		var clientQueue = new List<Action>();
		var otherQueue = new List<Action>();
		var host = BuildApp( hostQueue, isHost: true );
		var client = BuildApp( clientQueue, isHost: false );
		var other = BuildApp( otherQueue, isHost: false );
		var isClientDown = false;
		try
		{
			bool PumpUntil( Func<bool> condition )
			{
				var clock = Stopwatch.StartNew();
				while ( !condition() && clock.Elapsed < StepTimeout )
				{
					Pump( hostQueue );
					Pump( clientQueue );
					Pump( otherQueue );
					Thread.Sleep( 10 );
				}
				return condition();
			}

			T PumpFor<T>( Task<T> task )
			{
				PumpUntil( () => task.IsCompleted );
				return task.IsCompleted ? task.GetAwaiter().GetResult() : throw new TimeoutException( "A tool call did not finish." );
			}

			// The host, a daemon with Omp, the stores and the gateway without a pipe.
			LoadOmp( host );
			var hostStore = new ConversationStore( new ProjectStore() );
			CoreChecks.Check( ProjectChecks.LoadProjects( host, hostStore ) && host.Load( new WorkspacesModule( Path.Combine( folder, "workspaces.json" ) ) )
				&& host.Load( new ConversationsModule( hostStore, ConversationsModule.BuildChat ) ), "The tool host loads Projects, Workspaces and Conversations." );
			var hostRemote = new RemoteModule( new RemoteHooks { DataFolder = Path.Combine( folder, "host" ) } );
			CoreChecks.Check( host.Load( hostRemote ) && hostRemote.Gateway is not null, "The tool host starts the gateway." );
			var gateway = hostRemote.Gateway!;
			var hostSessions = host.GetModule<ConversationsModule>().Hosts.Local.Sessions;
			var hostOwn = host.Tools.Items.Select( tool => tool.Name ).ToHashSet( StringComparer.Ordinal );

			// The daemon-role client with tools of its own: one that echoes its arguments and caller, one that fails, one that waits to be cancelled.
			var cancelled = 0;
			LoadOmp( client );
			CoreChecks.Check( client.Load( new FixtureModule( "probe", builder => builder
				.Tool( "echo", "Echoes text and the caller.", """{ "type": "object" }""",
					( context, arguments ) => new( $"{arguments.GetProperty( "text" ).GetString()}|{context.Caller:N}|{context.Host ?? "local"}", Details: new { Seen = true } ) )
				.Tool( "fail", "Fails.", """{ "type": "object" }""", ( _, _ ) => AgentToolResult.Error( "Probe failed." ) )
				.ToolAsync( "wait", "Waits until cancelled.", """{ "type": "object" }""", async ( context, _ ) =>
				{
					try
					{
						await Task.Delay( Timeout.Infinite, context.Cancellation );
					}
					catch ( OperationCanceledException )
					{
						Interlocked.Increment( ref cancelled );
						throw;
					}
					return new( "Never." );
				} ) ) ), "The daemon-role client loads a module with tools." );
			var clientStore = new ConversationStore( new ProjectStore() );
			CoreChecks.Check( ProjectChecks.LoadProjects( client, clientStore ) && client.Load( new ConversationsModule( clientStore, ConversationsModule.BuildChat ) ),
				"The daemon-role client loads Projects and Conversations." );
			var clientRemote = new RemoteModule( new RemoteHooks
			{
				DataFolder = Path.Combine( folder, "client" ),
				Role = RemoteModule.DaemonRole,
				Dial = ( hostId, cancellationToken ) =>
				{
					var (hostEnd, clientEnd) = MemoryConnection.CreatePair();
					_ = gateway.RunConnectionAsync( hostEnd, HostGateway.LocalDevice, HostGateway.LocalName, DeviceScopes.Controlling );
					return Task.FromResult( new ClientCarrier( clientEnd, DeviceScopes.Controlling, "pipe" ) );
				},
			} );
			CoreChecks.Check( client.Load( clientRemote ) && clientRemote.Daemon is not null, "The daemon-role client starts." );
			CoreChecks.Check( PumpUntil( () => host.Tools.Items.Any( tool => tool.Name == "probe_echo" ) ),
				"The host publishes the tools the daemon-role client offers." );
			var proxy = host.Tools.Items.Single( tool => tool.Name == "probe_echo" );
			CoreChecks.Check( proxy.ModuleId == "probe" && proxy.Description == "Echoes text and the caller." && proxy.Parameters.GetProperty( "type" ).GetString() == "object"
				&& hostOwn.All( name => host.Tools.Items.Any( tool => tool.Name == name ) ),
				"An offered tool keeps its name, module, description and schema, and the host keeps its own tools." );

			// A chat the client shows is attached on the host as the client's panel, which becomes the caller of the agent's tool calls.
			var chat = clientStore.CreateConversation( SettingsProfile.Personal );
			CoreChecks.Check( PumpUntil( () => hostStore.Find( chat.Id ) is not null ), "The tool check's chat reaches the host." );
			var panel = Guid.NewGuid();
			client.GetModule<ConversationsModule>().Hosts.Local.Sessions.GetOrCreate( chat.Id ).Attach( panel );
			CoreChecks.Check( PumpUntil( () => hostSessions.Find( chat.Id ) is ChatSession { PanelId: { } attached } && attached == panel ),
				"The host's session is attached as the client panel the attach frame names." );
			var caller = ((ChatSession)hostSessions.Find( chat.Id )!).PanelId;

			// The host's agent calls the tools as its Omp bridge would: with the session's panel as the caller.
			var echo = PumpFor( host.Tools.InvokeAsync( "probe_echo", JsonSerializer.SerializeToElement( new { text = "hello" } ), caller, CancellationToken.None, "call-1" ) );
			CoreChecks.Check( !echo.IsError && echo.Text == $"hello|{panel:N}|local" && echo.SerializeDetails() is { } details && details.GetProperty( "seen" ).GetBoolean(),
				"A host call of an offered tool runs on the client with its arguments, the client panel as caller and This PC as host, and returns its result and details." );
			var failed = PumpFor( host.Tools.InvokeAsync( "probe_fail", JsonSerializer.SerializeToElement( new { } ), caller, CancellationToken.None ) );
			CoreChecks.Check( failed.IsError && failed.Text == "Probe failed.", "A failing offered tool returns its error to the host." );
			using ( var abandon = new CancellationTokenSource() )
			{
				var waiting = host.Tools.InvokeAsync( "probe_wait", JsonSerializer.SerializeToElement( new { } ), caller, abandon.Token );
				// Let the call reach the client and start before the agent abandons it.
				var settle = Stopwatch.StartNew();
				PumpUntil( () => settle.ElapsedMilliseconds > 300 );
				// Another module's tools reaching the client make it offer again; the probe's tools did not change, so their running call goes on.
				CoreChecks.Check( client.Load( new FixtureModule( "extra", builder => builder.Tool( "ping", "Pings.", """{ "type": "object" }""", ( _, _ ) => new( "pong" ) ) ) )
					&& PumpUntil( () => host.Tools.Items.Any( tool => tool.Name == "extra_ping" ) ) && !waiting.IsCompleted && Volatile.Read( ref cancelled ) == 0,
					"A new offer keeps the offers and running calls of the modules whose tools did not change." );
				abandon.Cancel();
				var abandoned = PumpFor( waiting );
				CoreChecks.Check( abandoned.IsError && PumpUntil( () => Volatile.Read( ref cancelled ) == 1 ),
					"An abandoned host call ends with an error and tool.cancel cancels the tool on the client." );
			}

			// A paired host's link offers nothing, and a paired device may not offer tools.
			var viewerId = Guid.NewGuid().ToString( "N" );
			using ( var viewerKeys = RemoteKeys.Create() )
				gateway.Devices.Save( new PairedDevice { DeviceId = viewerId, Name = "Laptop", DeviceKey = viewerKeys.PublicKeyText, Scopes = DeviceScopes.Controlling, Paired = DateTimeOffset.UtcNow } );
			LoadOmp( other );
			CoreChecks.Check( other.Load( new FixtureModule( "elsewhere", builder => builder.Tool( "echo", "Echoes.", """{ "type": "object" }""", ( _, _ ) => new( "" ) ) ) ),
				"The paired PC loads a module with a tool." );
			var otherStore = new ConversationStore( new ProjectStore() );
			CoreChecks.Check( ProjectChecks.LoadProjects( other, otherStore ) && other.Load( new ConversationsModule( otherStore, ConversationsModule.BuildChat ) ),
				"The paired PC loads Projects and Conversations." );
			var otherData = Path.Combine( folder, "other" );
			PairedHosts.Save( otherData, [new PairedHost( gateway.Keys.HostId, gateway.Keys.PublicKeyText, [], null, viewerId, "Tool host", DateTimeOffset.UtcNow )] );
			var otherRemote = new RemoteModule( new RemoteHooks
			{
				DataFolder = otherData,
				Role = RemoteModule.LocalRole,
				Dial = ( hostId, cancellationToken ) =>
				{
					var (hostEnd, clientEnd) = MemoryConnection.CreatePair();
					_ = gateway.RunConnectionAsync( hostEnd, viewerId, "Laptop", DeviceScopes.Controlling );
					return Task.FromResult( new ClientCarrier( clientEnd, DeviceScopes.Controlling, "relay" ) );
				},
			} );
			CoreChecks.Check( other.Load( otherRemote ) && otherRemote.FindLink( gateway.Keys.HostId ) is { } otherClient && PumpUntil( () => otherClient.Status == ClientState.Connected ),
				"The paired PC's link connects." );
			var offer = Frame.Create( "tools.offer" );
			offer["tools"] = WireJson.BuildNode( other.Tools.Items );
			var refused = otherRemote.FindLink( gateway.Keys.HostId )!.RequestAsync( offer, CancellationToken.None );
			PumpUntil( () => refused.IsCompleted );
			CoreChecks.Check( refused.Exception?.GetBaseException() is RemoteException { Code: RemoteErrors.Scope } && !host.Tools.Items.Any( tool => tool.ModuleId == "elsewhere" ),
				"A paired PC lends none of its other tools, and the host refuses a paired device's offer of tools that are not its browser's." );

			// Closing the daemon-role client's connection removes its tools from the host.
			client.Shutdown();
			isClientDown = true;
			CoreChecks.Check( PumpUntil( () => !host.Tools.Items.Any( tool => tool.ModuleId == "probe" ) )
				&& hostOwn.All( name => host.Tools.Items.Any( tool => tool.Name == name ) ),
				"When the daemon-role client disconnects, the host removes its tools and keeps its own." );
		}
		finally
		{
			other.Shutdown();
			if ( !isClientDown )
				client.Shutdown();
			host.Shutdown();
			TryDelete( folder );
		}
		Console.WriteLine( "PASS remote tools: a daemon-role client's tools are published on the host, run on the client with the chat panel as caller and no paired host, return results, errors and cancellation, and leave with the connection; a paired PC lends none but its browser tools." );
	}

	/// <summary>Creates an application whose UI thread is a queue the check pumps.</summary>
	private static LoknApplication BuildApp( List<Action> queue, bool isHost )
	{
		var app = new LoknApplication() { IsHost = isHost };
		app.BindDispatcher( action =>
		{
			lock ( queue )
				queue.Add( action );
			return true;
		} );
		return app;
	}

	/// <summary>Loads Omp with the fixture (this executable) as its executable and its first model.</summary>
	private static void LoadOmp( LoknApplication app )
	{
		CoreChecks.Check( app.Load( new OmpModule() ), "Omp loads with the fixture." );
		app.Settings.Set( OmpModule.Executable, SettingsScope.Global, Path.Combine( AppContext.BaseDirectory, "Lokn.Tests.exe" ) );
		app.Settings.Set( OmpModule.Model, SettingsScope.Global, "fixture/one" );
	}

	/// <summary>Runs every queued action.</summary>
	private static void Pump( List<Action> queue )
	{
		Action[] actions;
		lock ( queue )
		{
			actions = [.. queue];
			queue.Clear();
		}
		foreach ( var action in actions )
			action();
	}

	/// <summary>Deletes a temporary folder, leaving it when a file is still in use.</summary>
	private static void TryDelete( string folder )
	{
		try
		{
			Directory.Delete( folder, recursive: true );
		}
		catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
		{
		}
	}
}
