using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Goo;
using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;
using Lokn.Modules.Remote;
using Lokn.Modules.Workspaces;
using StreamContent = Lokn.Modules.Remote.StreamContent;

namespace Lokn.Tests;

/// <summary>Checks the agents' <c>window_*</c> tools of docs/remote.md: their publishing, window matching, input mapping, <c>window.show</c> to the Loom that
/// shows the agent's chat, the stream panel it opens and reuses, and <c>cap.agent</c> to the window's viewers, with fake windows, captures and input.</summary>
internal static class RemoteAgentWindowChecks
{
	/// <summary>Defines how long one step may take.</summary>
	private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds( 30 );

	/// <summary>Defines the fake editor window's handle.</summary>
	private const long Editor = 101;

	/// <summary>Defines the fake terminal window's handle.</summary>
	private const long Terminal = 202;

	/// <summary>Lists the fake windows: two whose process names contain "code", one large.</summary>
	private static readonly CaptureWindow[] Windows =
	[
		new( Editor, "Notes - Editor", "code", 800, 600 ),
		new( Terminal, "Terminal", "wincode", 2560, 1440 ),
		new( 303, "Editor", "notepad", 640, 480 ),
	];

	/// <summary>Lists the tool names.</summary>
	private static readonly string[] ToolNames = ["window_list", "window_show", "window_capture", "window_click", "window_type", "window_key", "window_scroll"];

	/// <summary>Runs the checks.</summary>
	internal static async Task RunAsync()
	{
		CheckMatching();
		await CheckCapAgentAsync();
		CheckDaemonAndClient();
		CheckLocalRole();
		Console.WriteLine( "PASS remote agent windows: window_* tools follow remote.agent-windows and the role, match windows by id, title or process and refuse ambiguous ones, map clicks, keys and text to input in capture pixels, "
			+ "window.show reaches only paired connections attached to the agent's chat, Loom on this PC opens no stream panel in either role since its user sees the window and the control bar, "
			+ "Loom on this PC learns which chat used which window, "
			+ "and cap.agent reaches the connections that stream the window and Loom on this PC." );
	}

	/// <summary>Checks window matching and key parsing.</summary>
	private static void CheckMatching()
	{
		CoreChecks.Check( AgentWindowTools.Match( Windows, "202" ).Hwnd == Terminal, "A window id from window_list names its window." );
		CoreChecks.Check( AgentWindowTools.Match( Windows, "notes" ).Hwnd == Editor, "Part of a title names the one window whose title contains it, case-insensitive." );
		CoreChecks.Check( AgentWindowTools.Match( Windows, "notepad" ).Hwnd == 303, "Part of a process name names its window." );
		CoreChecks.Check( AgentWindowTools.Match( Windows, "editor" ).Hwnd == 303, "Of several matches, the one whose whole title is the text wins." );
		CoreChecks.Check( Fails<ArgumentException>( () => AgentWindowTools.Match( Windows, "code" ) ), "Text that matches several windows is refused." );
		CoreChecks.Check( Fails<KeyNotFoundException>( () => AgentWindowTools.Match( Windows, "browser" ) ), "Text that matches no window is refused." );

		var (key, modifiers) = AgentWindowTools.ParseKey( "Ctrl+S", ["Shift"] );
		CoreChecks.Check( key == 'S' && modifiers.SequenceEqual( [0x11, 0x10] ), "Ctrl+S with Shift reads as S under Ctrl and Shift." );
		CoreChecks.Check( AgentWindowTools.ParseKey( "F5", [] ).Key == 0x74 && AgentWindowTools.ParseKey( "enter", [] ).Key == 0x0D && AgentWindowTools.ParseKey( "7", [] ).Key == '7',
			"Function keys, named keys and digits read as their virtual keys." );
		CoreChecks.Check( Fails<ArgumentException>( () => AgentWindowTools.ParseKey( "Hyper+X", [] ) ) && Fails<ArgumentException>( () => AgentWindowTools.ParseKey( "F30", [] ) ),
			"Unknown keys and modifiers are refused." );
	}

	/// <summary>Checks that <c>cap.agent</c> goes to the connections that stream the window and to Loom on this PC, and that a client's stream raises it.</summary>
	private static async Task CheckCapAgentAsync()
	{
		var service = new StreamService
		{
			ListWindows = () => Windows,
			CreateCapturer = _ => new FakeCapturer( isStreamed: true ),
			Encode = ( frame, _, _ ) => Task.FromResult( new[] { frame.Pixels[0] } ),
			Inject = ( _, _, _, _ ) => { },
		};
		var (streamHost, streamEnd) = MemoryConnection.CreatePair();
		var (idleHost, idleEnd) = MemoryConnection.CreatePair();
		var (localHost, localEnd) = MemoryConnection.CreatePair();
		await using var streaming = new RpcPeer( streamEnd );
		await using var idle = new RpcPeer( idleEnd );
		await using var local = new RpcPeer( localEnd );
		var hosts = new[] { new RpcPeer( streamHost ), new RpcPeer( idleHost ), new RpcPeer( localHost ) };
		service.Register( new( "device", "Device", DeviceScopes.Controlling, hosts[0] ) );
		service.Register( new( "idle", "Idle", DeviceScopes.Controlling, hosts[1] ) );
		service.Register( new( HostGateway.LocalDevice, HostGateway.LocalName, DeviceScopes.Controlling, hosts[2] ) );
		var source = new RemoteStreamSource( streaming, canControl: true );
		var idleAgent = 0;
		idle.OnPush( "cap.agent", _ => Interlocked.Increment( ref idleAgent ) );
		var localAgent = new TaskCompletionSource<JsonObject>( TaskCreationOptions.RunContinuationsAsynchronously );
		local.OnPush( "cap.agent", frame => localAgent.TrySetResult( frame ) );
		var used = new TaskCompletionSource<AgentWindowUse>( TaskCreationOptions.RunContinuationsAsynchronously );
		source.AgentUsed += use => used.TrySetResult( use );
		foreach ( var peer in hosts.Concat( [streaming, idle, local] ) )
			peer.Start();

		await source.StartAsync( Editor, CaptureOptions.Default, CancellationToken.None ).WaitAsync( StepTimeout );
		var sent = service.NotifyAgent( new( Editor, AgentWindowUse.Click, 0.25, 0.5 ) );
		var use = await used.Task.WaitAsync( StepTimeout );
		var pushed = await localAgent.Task.WaitAsync( StepTimeout );
		CoreChecks.Check( sent == 2 && use is { Hwnd: Editor, Kind: AgentWindowUse.Click, X: 0.25, Y: 0.5 } && Frame.ReadLong( pushed, "hwnd" ) == Editor,
			"cap.agent reaches the connection that streams the window, as AgentUsed of its source, and Loom on this PC." );
		await Task.Delay( 200 );
		CoreChecks.Check( Volatile.Read( ref idleAgent ) == 0, "A connection that streams no such window gets no cap.agent." );
		source.Stop();
		foreach ( var peer in hosts )
			await peer.DisposeAsync();
	}

	/// <summary>Runs the daemon with the fake windows and a daemon-role Loom in this process, joined by in-memory connections.</summary>
	private static void CheckDaemonAndClient()
	{
		var folder = Path.Combine( Path.GetTempPath(), "lokn-agent-windows-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( folder );
		var hostQueue = new List<Action>();
		var clientQueue = new List<Action>();
		var host = BuildApp( hostQueue, isHost: true );
		var client = BuildApp( clientQueue, isHost: false );
		var inputs = new ConcurrentQueue<(long Window, StreamInput Input, int Width, int Height)>();
		var typed = new ConcurrentQueue<(long Window, string Text)>();
		var hooks = BuildHooks( inputs, typed );
		try
		{
			bool PumpUntil( Func<bool> condition )
			{
				var clock = Stopwatch.StartNew();
				while ( !condition() && clock.Elapsed < StepTimeout )
				{
					Pump( hostQueue );
					Pump( clientQueue );
					Thread.Sleep( 10 );
				}
				return condition();
			}

			T PumpFor<T>( Task<T> task )
			{
				PumpUntil( () => task.IsCompleted );
				return task.IsCompleted ? task.GetAwaiter().GetResult() : throw new TimeoutException( "A tool call did not finish." );
			}

			void Settle()
			{
				for ( var round = 0; round < 20; round++ )
				{
					Pump( hostQueue );
					Pump( clientQueue );
					Thread.Sleep( 10 );
				}
			}

			AgentToolResult Call( string name, object arguments, Guid? chat ) =>
				PumpFor( host.Tools.InvokeAsync( name, JsonSerializer.SerializeToElement( arguments ), null, CancellationToken.None, conversation: chat ) );

			// The daemon: in-memory stores, the gateway without a pipe, and the fake windows.
			LoadOmp( host );
			var hostStore = new ConversationStore( new ProjectStore() );
			CoreChecks.Check( ProjectChecks.LoadProjects( host, hostStore ) && host.Load( new WorkspacesModule( Path.Combine( folder, "workspaces.json" ) ) )
				&& host.Load( new ConversationsModule( hostStore, ConversationsModule.BuildChat ) ), "The daemon loads Projects, Workspaces and Conversations." );
			var hostRemote = new RemoteModule( new RemoteHooks { DataFolder = Path.Combine( folder, "host" ), Windows = hooks } );
			CoreChecks.Check( host.Load( hostRemote ) && hostRemote.Gateway is not null, "The daemon's Remote module starts the gateway." );
			var gateway = hostRemote.Gateway!;
			CoreChecks.Check( ToolNames.All( name => host.Tools.Items.Any( tool => tool.Name == name ) ), "The daemon offers its agents the window tools." );
			host.Settings.Set( RemoteModule.AgentWindows, SettingsScope.Global, false );
			CoreChecks.Check( !host.Tools.Items.Any( tool => ToolNames.Contains( tool.Name ) ), "Turning remote.agent-windows off withdraws the window tools." );
			host.Settings.Set( RemoteModule.AgentWindows, SettingsScope.Global, true );
			CoreChecks.Check( ToolNames.All( name => host.Tools.Items.Any( tool => tool.Name == name ) ), "Turning remote.agent-windows on offers them again." );

			// Loom on this PC in the daemon role, with a probe panel to stand in for the chat's panel.
			LoadOmp( client );
			var clientStore = new ConversationStore( new ProjectStore() );
			CoreChecks.Check( ProjectChecks.LoadProjects( client, clientStore ) && client.Load( new ConversationsModule( clientStore, ConversationsModule.BuildChat ) )
				&& client.Load( new FixtureModule( "probe", builder => builder.Panel( "chat", "Probe", _ => new ProbeContent(), onHost: true ) ) ),
				"The daemon-role Loom loads Projects, Conversations and a probe panel." );
			var clientRemote = new RemoteModule( new RemoteHooks
			{
				DataFolder = Path.Combine( folder, "client" ),
				Role = RemoteModule.DaemonRole,
				Windows = hooks,
				Dial = ( _, _ ) =>
				{
					var (hostEnd, clientEnd) = MemoryConnection.CreatePair();
					_ = gateway.RunConnectionAsync( hostEnd, HostGateway.LocalDevice, HostGateway.LocalName, DeviceScopes.Controlling );
					return Task.FromResult( new ClientCarrier( clientEnd, DeviceScopes.Controlling, "pipe" ) );
				},
			} );
			CoreChecks.Check( client.Load( clientRemote ) && PumpUntil( () => clientRemote.Daemon?.Status == ClientState.Connected ), "The daemon-role Loom connects to the daemon." );
			CoreChecks.Check( !client.Tools.Items.Any( tool => ToolNames.Contains( tool.Name ) ), "A daemon-role Loom leaves the window tools to its daemon." );

			// A device that streams nothing, which window.show must not reach while Loom shows the chat.
			var (deviceHost, deviceEnd) = MemoryConnection.CreatePair();
			_ = gateway.RunConnectionAsync( deviceHost, "phone", "Phone", DeviceScopes.Controlling );
			var device = new RpcPeer( deviceEnd );
			var deviceShows = 0;
			device.OnPush( "window.show", _ => Interlocked.Increment( ref deviceShows ) );
			device.Start();

			// The chat, shown in the probe panel and attached on the daemon as that panel.
			var chat = clientStore.CreateConversation( SettingsProfile.Personal );
			CoreChecks.Check( PumpUntil( () => hostStore.Find( chat.Id ) is not null ), "The chat is created on the daemon." );
			var chatPanel = client.Panels.Open( "probe.chat" );
			client.GetModule<ConversationsModule>().Hosts.Local.Sessions.GetOrCreate( chat.Id ).Attach( chatPanel );
			var hostSessions = host.GetModule<ConversationsModule>().Hosts.Local.Sessions;
			CoreChecks.Check( PumpUntil( () => hostSessions.Find( chat.Id ) is not null ), "Attaching the chat attaches it on the daemon." );
			var chatPane = client.Panels.ActivePaneId;

			var ambiguous = Call( "window_show", new { window = "code" }, chat.Id );
			CoreChecks.Check( ambiguous.IsError && ambiguous.Text.Contains( "101", StringComparison.Ordinal ) && ambiguous.Text.Contains( "202", StringComparison.Ordinal ),
				"window_show refuses a window that matches several, listing their ids." );

			var uses = new System.Collections.Concurrent.ConcurrentQueue<AgentWindowUse>();
			void Record( AgentWindowUse use ) => uses.Enqueue( use );
			AgentWindowActivity.Used += Record;
			var shown = Call( "window_show", new { window = "Notes" }, chat.Id );
			Settle();
			CoreChecks.Check( !shown.IsError && client.Panels.FindContent<StreamContent>() is null && client.Panels.ActivePaneId == chatPane,
				"window_show opens no stream panel in Loom on this PC, whose user sees the window on their own screen, under the control bar." );
			CoreChecks.Check( Volatile.Read( ref deviceShows ) == 0, "window.show goes only to the connections attached to the chat." );

			var captured = Call( "window_capture", new { window = Editor }, chat.Id );
			var path = captured.Text.Split( " to ", 2 )[1].Split( ". Read", 2 )[0];
			CoreChecks.Check( !captured.IsError && File.Exists( path ) && path.StartsWith( Path.Combine( folder, "host", "screenshots" ), StringComparison.OrdinalIgnoreCase )
				&& captured.Text.Contains( $"{FakeCapturer.Width}×{FakeCapturer.Height}", StringComparison.Ordinal ),
				"window_capture saves the capture in the daemon's screenshots folder and says its size." );
			Settle();
			AgentWindowActivity.Used -= Record;
			CoreChecks.Check( uses.Any( use => use is { Hwnd: Editor, Kind: AgentWindowUse.Capture } && use.Chat == chat.Id && use.Title.Length > 0 ),
				"Loom on this PC learns which chat's agent used which window, by title, so its control bar names the window and stops that chat." );

			// Input maps to the window in pixels of its last capture; a window never captured uses the size a capture would have.
			var clicked = Call( "window_click", new { window = Editor, x = 10, y = 20, @double = true }, chat.Id );
			var clicks = inputs.ToArray();
			CoreChecks.Check( !clicked.IsError && clicks.Length == 5 && clicks.All( item => item is { Window: Editor, Width: FakeCapturer.Width, Height: FakeCapturer.Height, Input.X: 10, Input.Y: 20 } )
				&& clicks.Select( item => item.Input.Kind ).SequenceEqual( [StreamInput.Move, StreamInput.Down, StreamInput.Up, StreamInput.Down, StreamInput.Up] ),
				"window_click double-clicks at the point in pixels of the window's last capture." );
			CoreChecks.Check( InputInjector.ComputeClientPoint( 10, 20, FakeCapturer.Width, FakeCapturer.Height, 800, 600 ) == (40, 120),
				"The injector maps capture pixels to the window's client area." );
			CoreChecks.Check( clicked.Text.Contains( "capture of the result", StringComparison.Ordinal ), "An input tool returns a fresh capture." );
			inputs.Clear();

			var big = Call( "window_click", new { window = Terminal, x = 640, y = 360, button = "right" }, chat.Id );
			CoreChecks.Check( !big.IsError && inputs.TryPeek( out var first ) && first is { Window: Terminal, Width: 1280, Height: 720, Input.Kind: StreamInput.Move }
				&& inputs.Any( item => item.Input is { Kind: StreamInput.Down, Button: 2 } ),
				"A window never captured takes coordinates in the size a capture would have, scaled to 1280 pixels wide." );
			var outside = Call( "window_click", new { window = Editor, x = 500, y = 20 }, chat.Id );
			CoreChecks.Check( outside.IsError, "A point outside the last capture is refused." );
			inputs.Clear();

			var pressed = Call( "window_key", new { window = Editor, key = "Ctrl+S" }, chat.Id );
			var keys = inputs.Select( item => (item.Input.Vk, item.Input.IsDown) ).ToArray();
			CoreChecks.Check( !pressed.IsError && keys.SequenceEqual( [(0x11, true), ('S', true), ('S', false), (0x11, false)] ), "window_key holds the modifiers around the key and releases them." );
			var typedResult = Call( "window_type", new { window = Editor, text = "hello\n" }, chat.Id );
			CoreChecks.Check( !typedResult.IsError && typed.TryDequeue( out var text ) && text == (Editor, "hello\n"), "window_type types the text into the window." );
			inputs.Clear();
			var scrolled = Call( "window_scroll", new { window = Editor, x = 1, y = 1, delta = -2 }, chat.Id );
			CoreChecks.Check( !scrolled.IsError && inputs.Any( item => item.Input is { Kind: StreamInput.Wheel, Delta: -240 } ), "window_scroll turns the wheel by 120 per notch." );

			// With the chat not shown anywhere, Loom on this PC still opens no stream panel.
			client.Panels.Close( chatPanel );
			Settle();
			CoreChecks.Check( !Call( "window_show", new { window = 303 }, Guid.NewGuid() ).IsError && client.Panels.Panels.All( panel => panel.TypeId != StreamPanel.TypeId ),
				"window.show for a chat no Loom shows opens no stream panel on this PC either." );
			_ = device.DisposeAsync();
		}
		finally
		{
			client.Shutdown();
			host.Shutdown();
			TryDelete( folder );
		}
	}

	/// <summary>Runs a local-role Loom with the fake windows, whose agents' window_show opens the stream panel in process.</summary>
	private static void CheckLocalRole()
	{
		var folder = Path.Combine( Path.GetTempPath(), "lokn-agent-windows-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( folder );
		var queue = new List<Action>();
		var app = BuildApp( queue, isHost: false );
		try
		{
			LoadOmp( app );
			var store = new ConversationStore( new ProjectStore() );
			CoreChecks.Check( ProjectChecks.LoadProjects( app, store ) && app.Load( new ConversationsModule( store, ConversationsModule.BuildChat ) )
				&& app.Load( new FixtureModule( "probe", builder => builder.Panel( "chat", "Probe", _ => new ProbeContent(), onHost: true ) ) )
				&& app.Load( new RemoteModule( new RemoteHooks { DataFolder = folder, Role = RemoteModule.LocalRole, Windows = BuildHooks( new(), new() ) } ) ),
				"The local-role Loom loads Conversations, a probe panel and Remote." );
			CoreChecks.Check( ToolNames.All( name => app.Tools.Items.Any( tool => tool.Name == name ) ), "A local-role Loom offers its agents the window tools." );

			var chatPanel = app.Panels.Open( "probe.chat" );
			var pane = app.Panels.ActivePaneId;
			var task = app.Tools.InvokeAsync( "window_show", JsonSerializer.SerializeToElement( new { window = Editor } ), chatPanel, CancellationToken.None );
			Pump( queue );
			CoreChecks.Check( task.IsCompletedSuccessfully && !task.Result.IsError && app.Panels.FindContent<StreamContent>() is null && app.Panels.ActivePaneId == pane,
				"window_show in a local-role Loom opens no stream panel: the user sees the window on their own screen, under the control bar." );
		}
		finally
		{
			app.Shutdown();
			TryDelete( folder );
		}
	}

	/// <summary>Builds the fake windows, captures and input sinks.</summary>
	private static AgentWindowHooks BuildHooks( ConcurrentQueue<(long, StreamInput, int, int)> inputs, ConcurrentQueue<(long, string)> typed ) => new()
	{
		ListWindows = () => Windows,
		CreateCapturer = _ => new FakeCapturer( isStreamed: false ),
		Encode = ( frame, _ ) => Task.FromResult( new[] { frame.Pixels[0] } ),
		Inject = ( window, input, width, height ) =>
		{
			inputs.Enqueue( (window, input, width, height) );
			return true;
		},
		Type = ( window, text ) =>
		{
			typed.Enqueue( (window, text) );
			return true;
		},
	};

	/// <summary>Reports whether an action throws an exception of a type.</summary>
	private static bool Fails<T>( Action action ) where T : Exception
	{
		try
		{
			action();
			return false;
		}
		catch ( T )
		{
			return true;
		}
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

	/// <summary>Deletes a temporary folder, ignoring files still in use.</summary>
	private static void TryDelete( string folder )
	{
		try
		{
			Directory.Delete( folder, recursive: true );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			// A file still in use stays in the temporary folder.
		}
	}

	/// <summary>An empty panel standing in for a chat's panel.</summary>
	private sealed class ProbeContent : PanelContent
	{
		/// <summary>Builds nothing.</summary>
		public override Blob Build() => new Container();
	}

	/// <summary>Returns 200×100 frames for the tools' captures; for a stream panel's capture, frames only when <paramref name="isStreamed"/> is set.</summary>
	/// <param name="isStreamed">Whether stream sessions, which ask for wider frames than the tools, get frames too.</param>
	private sealed class FakeCapturer( bool isStreamed ) : IWindowCapturer
	{
		/// <summary>Defines the frame width.</summary>
		internal const int Width = 200;

		/// <summary>Defines the frame height.</summary>
		internal const int Height = 100;

		/// <summary>Stores the value the next frame is filled with.</summary>
		private int _value;

		/// <summary>Gets the frame size.</summary>
		public (int Width, int Height) SourceSize => (Width, Height);

		/// <summary>Returns a frame filled with a new value, or null for a stream that should get none.</summary>
		public BgraFrame? Capture( int maxWidth )
		{
			if ( !isStreamed && maxWidth != AgentWindowTools.CaptureWidth )
				return null;

			var pixels = new byte[Width * Height * 4];
			Array.Fill( pixels, (byte)Interlocked.Increment( ref _value ) );
			return new( Width, Height, pixels );
		}

		/// <summary>Releases nothing.</summary>
		public void Dispose()
		{
		}
	}
}
