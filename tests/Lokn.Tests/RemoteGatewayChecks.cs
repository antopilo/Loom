using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Goo;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Conversations;
using Lokn.Modules.Files;
using Lokn.Modules.Git;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;
using Lokn.Modules.Remote;
using Lokn.Modules.Workspaces;

namespace Lokn.Tests;

/// <summary>Checks the host gateway and the client end to end: in process over in-memory connections, and against a real bare daemon over its pipe.</summary>
/// <remarks>Both use the Omp fixture (this executable) as the agent and temporary folders only, never the user's data.</remarks>
internal static class RemoteGatewayChecks
{
	/// <summary>Defines how long one step may take.</summary>
	private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds( 30 );

	/// <summary>Runs the in-process, multi-host and the daemon process checks.</summary>
	internal static async Task RunAsync()
	{
		CheckInProcess();
		Console.WriteLine( "PASS remote gateway in process: a daemon-role client mirrors the host's stores, creates a chat, runs a fixture turn through a proxy session with its question answered, follows working pushes, reconnects, drops a closed chat's late pushes and catches up when it attaches again, and a view-only device gets scope; attach with since 0 sends the transcript, large transcripts come in pages, a connection made with stale scopes closes, and network Off closes devices but not the pipe; a view-only pairing waits for Loom's answer (push, admin.status, notification, dialog, host status), Allow control and View only answer it, revoking ends it, and a control link pairs with control." );
		CheckMultiHost();
		Console.WriteLine( "PASS remote multi-host: a local-role Loom links two paired hosts at once, each mirroring its own chats, stores, sessions, stream and tool calls; a host added live connects without a restart and becomes current, pairing again keeps its name, rename and removal follow remote-hosts.json, and a reload of Remote keeps the hosts and links them again." );
		CheckTwoHostsEndToEnd();
		Console.WriteLine( "PASS remote two hosts end to end: one Loom linked to two hosts at once switches between them without a restart, runs a fixture turn in each host's chat with its question answered there, and a Files tree and a Git panel on the second host read, run git and watch on that host's disk only." );
		CheckRoleMigration();
		Console.WriteLine( "PASS remote role migration: the former remote role becomes local and its paired host is imported and linked beside This PC." );
		CheckClientGuards();
		Console.WriteLine( "PASS remote client guards: a paired host's link fails workspace and stream calls until it connects and after it stops, the data folder lock admits one writer, upload names avoid device names, and Run key arguments are quoted." );
		if ( await CheckDaemonProcessAsync() )
			Console.WriteLine( "PASS remote daemon process: Loom.exe --host --bare runs the modules its modules.json turns on with Remote and what it needs, serves sync, store, attach and calls over its pipe for a whole fixture turn, follows modules.json live, and --stop ends it." );
	}

	/// <summary>Runs a host application and a daemon-role client application in this process, joined by in-memory connections the client's dialer makes.</summary>
	private static void CheckInProcess()
	{
		var folder = Path.Combine( Path.GetTempPath(), "lokn-gateway-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( folder );
		var hostQueue = new List<Action>();
		var clientQueue = new List<Action>();
		var host = BuildApp( hostQueue, isHost: true );
		var client = BuildApp( clientQueue, isHost: false );
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
				return task.IsCompleted ? task.GetAwaiter().GetResult() : throw new TimeoutException( "A remote request did not finish." );
			}

			Exception? PumpForFailure( Task task )
			{
				PumpUntil( () => task.IsCompleted );
				return task.Exception?.GetBaseException();
			}

			// The host: the fixture agent, in-memory stores, and the gateway without a pipe.
			LoadOmp( host );
			var hostStore = new ConversationStore( new ProjectStore() );
			CoreChecks.Check( ProjectChecks.LoadProjects( host, hostStore ) && host.Load( new WorkspacesModule( Path.Combine( folder, "workspaces.json" ) ) )
				&& host.Load( new ConversationsModule( hostStore, ConversationsModule.BuildChat ) ), "The host loads Projects, Workspaces and Conversations." );
			var hostRemote = new RemoteModule( new RemoteHooks { DataFolder = Path.Combine( folder, "host" ) } );
			CoreChecks.Check( host.Load( hostRemote ) && hostRemote.Gateway is not null && hostRemote.RunningRole == RemoteModule.HostRole, "The host's Remote module starts the gateway." );
			var gateway = hostRemote.Gateway!;
			var hostSessions = host.GetModule<ConversationsModule>().Hosts.Local.Sessions;

			// The client: a daemon-role Loom whose dialer hands the gateway the other end of a new in-memory pair each time.
			LoadOmp( client );
			var clientStore = new ConversationStore( new ProjectStore() );
			var dials = 0;
			CoreChecks.Check( ProjectChecks.LoadProjects( client, clientStore ) && client.Load( new ConversationsModule( clientStore, ConversationsModule.BuildChat ) ),
				"The client loads Projects and Conversations." );
			var clientRemote = new RemoteModule( new RemoteHooks
			{
				DataFolder = Path.Combine( folder, "client" ),
				Role = RemoteModule.DaemonRole,
				Dial = ( hostId, cancellationToken ) =>
				{
					Interlocked.Increment( ref dials );
					var (hostEnd, clientEnd) = MemoryConnection.CreatePair();
					_ = gateway.RunConnectionAsync( hostEnd, HostGateway.LocalDevice, HostGateway.LocalName, DeviceScopes.Controlling );
					return Task.FromResult( new ClientCarrier( clientEnd, DeviceScopes.Controlling, "pipe" ) );
				},
			} );
			CoreChecks.Check( client.Load( clientRemote ) && clientRemote.Daemon is not null && clientRemote.Prompts is not null, "The client's Remote module links with the daemon in the daemon role." );
			var remote = clientRemote.Daemon!;
			CoreChecks.Check( PumpUntil( () => remote.Status == ClientState.Connected ) && clientStore.IsMirror, "The client connects, syncs and mirrors the host's stores." );

			// A view-only device sees working pushes but may not change anything.
			var viewerId = Guid.NewGuid().ToString( "N" );
			using ( var viewerKeys = RemoteKeys.Create() )
				gateway.Devices.Save( new PairedDevice { DeviceId = viewerId, Name = "Phone", DeviceKey = viewerKeys.PublicKeyText, Scopes = DeviceScopes.Paired, Paired = DateTimeOffset.UtcNow } );
			var (viewerHost, viewerEnd) = MemoryConnection.CreatePair();
			_ = gateway.RunConnectionAsync( viewerHost, viewerId, "Phone", DeviceScopes.Paired );
			var viewer = new RpcPeer( viewerEnd );
			var working = new ConcurrentQueue<(Guid Id, bool IsBusy)>();
			viewer.OnPush( "working", frame => working.Enqueue( (Frame.ReadChannel( frame ) ?? Guid.Empty, Frame.ReadBool( frame, "busy" ) == true) ) );
			viewer.Start();
			var viewerSync = PumpFor( viewer.RequestAsync( Frame.Create( "sync" ), CancellationToken.None ) );
			CoreChecks.Check( Frame.ReadString( viewerSync, "role" ) == RemoteModule.HostRole && Frame.ReadString( viewerSync, "boot" ) is { Length: > 0 }
				&& Frame.ReadString( viewerSync, "version" ) == RemoteProtocol.AppVersion && Frame.ReadLong( viewerSync, "rev" ) is not null,
				"A view-only device may sync and learns the host's role, build, list revision and boot id." );
			CoreChecks.Check( PumpUntil( () => host.HostStatus is { IsLocalConnected: true, Devices: 1, State: HostState.Running, IsTrayShown: true, CanStartAtLogin: false } ),
				"The host publishes its status for the notification area: Loom on this PC and one device connected, and a check's host never offers the sign-in entry." );

			// The device's scopes changed while a connection was being made with the old ones: it closes before any of its requests runs.
			var (staleHost, staleEnd) = MemoryConnection.CreatePair();
			_ = gateway.RunConnectionAsync( staleHost, viewerId, "Phone", DeviceScopes.Controlling );
			var stale = new RpcPeer( staleEnd );
			stale.Start();
			var staleSync = PumpForFailure( stale.RequestAsync( Frame.Create( "sync" ), CancellationToken.None ) );
			CoreChecks.Check( staleSync is IOException && stale.IsClosed, "A connection made with scopes the device no longer holds is closed before it is served." );

			// A chat created on the client's mirror is created on the host under the same id.
			var chat = clientStore.CreateConversation( SettingsProfile.Personal );
			CoreChecks.Check( PumpUntil( () => hostStore.Find( chat.Id ) is not null ), "A chat the client creates is created on the host through a store request." );
			hostStore.SaveBackendSession( chat.Id, Path.Combine( folder, "omp-session" ) );
			CoreChecks.Check( PumpUntil( () => clientStore.Get( chat.Id ).BackendSessionPath == hostStore.Get( chat.Id ).BackendSessionPath ),
				"A host's own store change reaches the client as a store push." );

			var scope = PumpForFailure( viewer.RequestAsync( BuildCall( chat.Id, ChatSessionCall.Send, "Not allowed", Array.Empty<string>() ), CancellationToken.None ) );
			var storeScope = PumpForFailure( viewer.RequestAsync( BuildStore( StoreOp.Build( StoreOp.Conversations, nameof( ConversationStore.DeleteConversation ), new { id = chat.Id } ) ), CancellationToken.None ) );
			var adminScope = PumpForFailure( viewer.RequestAsync( Frame.Create( "admin.status" ), CancellationToken.None ) );
			CoreChecks.Check( scope is RemoteException { Code: RemoteErrors.Scope } && storeScope is RemoteException { Code: RemoteErrors.Scope }
				&& adminScope is RemoteException { Code: RemoteErrors.Scope } && hostStore.Find( chat.Id ) is not null,
				"A view-only device gets scope for a prompt, a store change and the admin requests." );
			var taskCheck = PumpForFailure( viewer.RequestAsync( BuildCall( chat.Id, ChatSessionCall.CheckNewTask ), CancellationToken.None ) );
			CoreChecks.Check( taskCheck is RemoteException { Code: RemoteErrors.Scope }, "A view-only device may not run the task check, a paid model request on the host's account." );

			// The client opens the chat's session, which is a proxy over the host's, and runs a whole turn through it.
			var session = client.GetModule<ConversationsModule>().Hosts.Local.Sessions.GetOrCreate( chat.Id );
			CoreChecks.Check( session is RemoteChatSession, "The client's chat sessions are proxies of the host's." );
			var sawBusy = false;
			session.Changed += () => sawBusy |= session.IsBusy;
			var panel = Guid.NewGuid();
			session.Attach( panel );
			CoreChecks.Check( PumpUntil( () => hostSessions.Find( chat.Id ) is not null ), "Attaching the proxy creates and attaches the host's session." );
			session.Send( "Run gateway", [] );
			CoreChecks.Check( PumpUntil( () => session.PendingDialog is not null ) && sawBusy && session.PendingDialog!.Value.GetProperty( "id" ).GetString() == "approval",
				"A prompt sent from the client runs on the host; the client shows the agent working and its question." );
			CoreChecks.Check( PumpUntil( () => host.HostStatus is { State: HostState.NeedsYou, Waiting: 1, Working: 1 } ),
				"While the host's turn asks its question, its status says an agent works and a chat needs the user." );
			session.AnswerDialog( "approval", new { type = "extension_ui_response", id = "approval", confirmed = true } );
			CoreChecks.Check( PumpUntil( () => !session.IsBusy && session.PendingDialog is null && hostSessions.Find( chat.Id ) is { IsBusy: false } ),
				"Answering the question from the client lets the host's turn settle, and the client follows it to idle." );
			CoreChecks.Check( PumpUntil( () => host.HostStatus is { State: HostState.Running, Waiting: 0, Working: 0 } ), "Once the turn settled, the host's status is running again." );
			CoreChecks.Check( PumpUntil( () => clientStore.GetMessages( chat.Id ).Any( message => message.Role == "assistant" && message.Text.StartsWith( "Streaming complete.", StringComparison.Ordinal ) ) )
				&& clientStore.GetMessages( chat.Id ).Any( message => message.Role == "user" && message.Text == "Run gateway" ) && !clientStore.IsWorking( chat.Id ),
				"The client's mirror receives the turn's messages through message pushes and shows the chat idle." );
			CoreChecks.Check( PumpUntil( () => working.Any( item => item.Id == chat.Id && item.IsBusy ) && working.LastOrDefault( item => item.Id == chat.Id ) is { IsBusy: false } ),
				"Every connection gets working pushes as the chat starts and stops working." );

			// A dropped connection reconnects, re-syncs and re-attaches the open chat.
			remote.Reconnect();
			CoreChecks.Check( PumpUntil( () => Volatile.Read( ref dials ) >= 2 && remote.Status == ClientState.Connected ), "The client reconnects after its connection drops." );
			hostStore.RenameConversation( chat.Id, "Renamed on the host" );
			CoreChecks.Check( PumpUntil( () => clientStore.Get( chat.Id ).Title == "Renamed on the host" ), "After reconnecting, the client follows the host's store again." );
			session.UpdateStatus( "Still attached" );
			CoreChecks.Check( PumpUntil( () => session.Status == "Still attached" ), "After reconnecting, the client's calls reach the host's session and its state comes back." );

			// A push the host sent before it took the detach does not reach the closed chat's proxy; the next attach brings the host's state as it is then
			// (the host's session stopped its idle process once no panel showed it, which changed its status again).
			session.Detach( panel );
			hostSessions.Find( chat.Id )!.UpdateStatus( "After detach" );
			// The connection's frames leave the host in order, so once a later request's answer is in, the push came before it and was posted here.
			PumpFor( remote.RequestAsync( Frame.Create( "admin.status" ), CancellationToken.None ) );
			Pump( clientQueue );
			CoreChecks.Check( session.Status == "Still attached", "A push for a chat no panel shows any more, sent before the host took its detach, is dropped." );
			session.Attach( panel );
			CoreChecks.Check( PumpUntil( () => session.Status != "Still attached" && session.Status == hostSessions.Find( chat.Id )!.Status ),
				"Attaching again brings the host's state the closed chat missed." );

			var status = PumpFor( remote.RequestAsync( Frame.Create( "admin.status" ), CancellationToken.None ) );
			CoreChecks.Check( Frame.ReadString( status, "role" ) == RemoteModule.HostRole && Frame.ReadString( status, "hostId" ) == gateway.Keys.HostId,
				"Loom on this PC reads the daemon's status." );

			// A device that pairs with view only waits for the answer of the user of this PC.
			var prompts = clientRemote.Prompts!;
			string PairDevice( string name, bool control )
			{
				using var deviceKeys = RemoteKeys.Create();
				var offer = gateway.CreateOffer( ["127.0.0.1:9"], null, control );
				var deviceId = Guid.NewGuid().ToString( "N" );
				var (pairHost, pairDevice) = MemoryConnection.CreatePair();
				var accepted = SecureChannel.AcceptAsync( pairHost, gateway.Keys, gateway.Devices, gateway.Offers, CancellationToken.None );
				PumpFor( SecureChannel.PairAsync( pairDevice, deviceKeys, deviceId, name, offer.Link, CancellationToken.None ) );
				PumpUntil( () => accepted.IsCompleted );
				return deviceId;
			}

			var tablet = PairDevice( "Tablet", control: false );
			CoreChecks.Check( PumpUntil( () => prompts.Pending.Any( device => device.DeviceId == tablet ) ) && gateway.Pending.Single().DeviceId == tablet
				&& client.Notifications.Items.FirstOrDefault() is { Title: "Tablet paired with this PC", Actions.Count: 2 }
				&& prompts.BuildDialog() is { Title: "Allow Tablet to control this PC?", Accept: "Allow control", CancelLabel: "View only" },
				"A view-only pairing pushes admin.pairing to Loom on this PC, which posts a notification with both answers and asks in a dialog." );
			CoreChecks.Check( PumpUntil( () => host.HostStatus is { PendingPairings: 1, PendingPairing: "Tablet" } status && status.PendingPairingId == tablet ),
				"The host's status names the device waiting for an answer, for its notification-area icon." );
			var listed = PumpFor( remote.RequestAsync( Frame.Create( "admin.status" ), CancellationToken.None ) );
			CoreChecks.Check( (listed["pending"] as JsonArray)?.OfType<JsonObject>().Select( entry => Frame.ReadString( entry, "deviceId" ) ).SequenceEqual( [tablet] ) == true,
				"admin.status lists the device waiting for an answer." );
			var tabletNotice = client.Notifications.Items[0].Id;
			prompts.Answer( tablet, control: true );
			CoreChecks.Check( PumpUntil( () => prompts.Pending.Count == 0 && gateway.Pending.Count == 0 ) && gateway.Devices.Find( tablet )?.Scopes.Contains( DeviceScopes.Control ) == true
				&& client.Notifications.Items.First( item => item.Id == tabletNotice ).IsRead && prompts.BuildDialog() is null
				&& PumpUntil( () => host.HostStatus is { PendingPairings: 0, PendingPairing: null } ),
				"Allow control sends admin.confirm: the device gets control, stops waiting, the dialog closes and the notification is read." );

			var watch = PairDevice( "Watch", control: false );
			CoreChecks.Check( PumpUntil( () => prompts.Pending.Any( device => device.DeviceId == watch ) ), "A second view-only pairing waits too." );
			prompts.AnswerLink( "view/" + watch );
			CoreChecks.Check( PumpUntil( () => prompts.Pending.Count == 0 && gateway.Pending.Count == 0 ) && gateway.Devices.Find( watch )?.Scopes.SequenceEqual( DeviceScopes.Paired ) == true,
				"The notification's View only answer keeps the device view-only and ends its wait." );

			var spare = PairDevice( "Spare", control: false );
			CoreChecks.Check( PumpUntil( () => prompts.Pending.Any( device => device.DeviceId == spare ) ), "A third view-only pairing waits." );
			var revoke = Frame.Create( "admin.revoke" );
			revoke["deviceId"] = spare;
			PumpFor( remote.RequestAsync( revoke, CancellationToken.None ) );
			CoreChecks.Check( PumpUntil( () => prompts.Pending.Count == 0 && gateway.Pending.Count == 0 ), "Revoking a device that waits ends its wait in Loom on this PC." );
			var confirmTwice = Frame.Create( "admin.confirm" );
			confirmTwice["deviceId"] = tablet;
			confirmTwice["control"] = true;
			CoreChecks.Check( PumpForFailure( remote.RequestAsync( confirmTwice, CancellationToken.None ) ) is RemoteException { Code: RemoteErrors.BadRequest },
				"admin.confirm for a device that does not wait is a bad request." );

			var laptop = PairDevice( "Laptop", control: true );
			CoreChecks.Check( PumpUntil( () => client.Notifications.Items.Any( item => item.Title == "Laptop paired with control" ) ) && gateway.Pending.Count == 0
				&& gateway.Devices.Find( laptop )?.Scopes.Contains( DeviceScopes.Control ) == true,
				"A link that grants control pairs with control at once, and Loom on this PC hears admin.paired instead of a question." );

			// A chat untouched since the host started has sequence number 0; an attach with since 0, a client's first, still gets its transcript.
			var idle = hostStore.CreateConversation( SettingsProfile.Personal );
			var idleAttach = Frame.Create( "attach" ).WithChannel( idle.Id );
			idleAttach["since"] = 0;
			var idleAnswer = PumpFor( viewer.RequestAsync( idleAttach, CancellationToken.None ) );
			CoreChecks.Check( Frame.ReadSeq( idleAnswer ) == 0 && idleAnswer["messages"] is JsonArray,
				"An attach with since 0 gets the messages even while the chat's sequence number is still 0, as after a host restart." );
			PumpFor( viewer.RequestAsync( Frame.Create( "detach" ).WithChannel( idle.Id ), CancellationToken.None ) );

			// A transcript too large for one frame comes as pages the client asks for after the attach answer.
			var large = hostStore.CreateConversation( SettingsProfile.Personal );
			var block = new string( 'x', 1024 * 1024 );
			for ( var index = 0; index < 6; index++ )
				hostStore.AppendMessage( large.Id, new ConversationMessage( Guid.NewGuid(), "user", $"{index}:{block}", DateTimeOffset.UtcNow ) );
			var largeAttach = Frame.Create( "attach" ).WithChannel( large.Id );
			largeAttach["since"] = -1;
			var largeAnswer = PumpFor( viewer.RequestAsync( largeAttach, CancellationToken.None ) );
			var pages = Frame.ReadLong( largeAnswer, "pages" ) ?? 1;
			var received = (largeAnswer["messages"] as JsonArray)?.Count ?? 0;
			for ( var page = 1L; page < pages; page++ )
			{
				var request = Frame.Create( "attach.page" ).WithChannel( large.Id );
				request["page"] = page;
				received += (PumpFor( viewer.RequestAsync( request, CancellationToken.None ) )["messages"] as JsonArray)?.Count ?? 0;
			}
			CoreChecks.Check( pages > 1 && received == 6, "A 6 MiB transcript is attached in pages that hold every message." );
			PumpFor( viewer.RequestAsync( Frame.Create( "detach" ).WithChannel( large.Id ), CancellationToken.None ) );

			// Turning network access off cuts paired devices off at once; Loom on this PC keeps its connection.
			var networkOff = gateway.ApplyNetworkSettingsAsync( "off", RemoteModule.Port.DefaultValue, "" );
			CoreChecks.Check( PumpUntil( () => networkOff.IsCompleted && viewer.IsClosed ) && remote.Status == ClientState.Connected && !remote.StatusText.Contains( "Reconnecting", StringComparison.Ordinal ),
				"Network access Off closes every paired device's connection and keeps the pipe's." );
			CoreChecks.Check( PumpUntil( () => host.HostStatus is { Devices: 0, IsLocalConnected: true } ), "With network access Off, the host's status counts no device but Loom on this PC." );

			_ = viewer.DisposeAsync();
		}
		finally
		{
			client.Shutdown();
			host.Shutdown();
			TryDelete( folder );
		}
	}

	/// <summary>Runs three hosts and a local-role Loom in this process: the Loom links the two hosts its <c>remote-hosts.json</c> lists at once over
	/// in-memory connections its dialer makes by host id, adds the third live, re-pairs, renames and removes hosts, and reloads the Remote module.</summary>
	private static void CheckMultiHost()
	{
		var folder = Path.Combine( Path.GetTempPath(), "lokn-multihost-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( folder );
		List<Action>[] queues = [[], [], [], []];
		var hostA = BuildApp( queues[0], isHost: true );
		var hostB = BuildApp( queues[1], isHost: true );
		var hostC = BuildApp( queues[2], isHost: true );
		var client = BuildApp( queues[3], isHost: false );
		try
		{
			bool PumpUntil( Func<bool> condition )
			{
				var clock = Stopwatch.StartNew();
				while ( !condition() && clock.Elapsed < StepTimeout )
				{
					foreach ( var queue in queues )
						Pump( queue );
					Thread.Sleep( 10 );
				}
				return condition();
			}

			T PumpFor<T>( Task<T> task )
			{
				PumpUntil( () => task.IsCompleted );
				return task.IsCompleted ? task.GetAwaiter().GetResult() : throw new TimeoutException( "A multi-host step did not finish." );
			}

			(HostGateway Gateway, ConversationStore Store) StartHost( LoknApplication app, string name )
			{
				LoadOmp( app );
				var store = new ConversationStore( new ProjectStore() );
				CoreChecks.Check( ProjectChecks.LoadProjects( app, store ) && app.Load( new WorkspacesModule( Path.Combine( folder, name + "-workspaces.json" ) ) )
					&& app.Load( new ConversationsModule( store, ConversationsModule.BuildChat ) ), $"Host {name} loads Projects, Workspaces and Conversations." );
				var hostRemote = new RemoteModule( new RemoteHooks { DataFolder = Path.Combine( folder, name ) } );
				CoreChecks.Check( app.Load( hostRemote ) && hostRemote.Gateway is not null, $"Host {name} starts its gateway." );
				return (hostRemote.Gateway!, store);
			}

			string AddDevice( HostGateway gateway )
			{
				var deviceId = Guid.NewGuid().ToString( "N" );
				using var keys = RemoteKeys.Create();
				gateway.Devices.Save( new PairedDevice { DeviceId = deviceId, Name = "Laptop", DeviceKey = keys.PublicKeyText, Scopes = DeviceScopes.Controlling, Paired = DateTimeOffset.UtcNow } );
				return deviceId;
			}

			static PairedHost Entry( HostGateway gateway, string deviceId, string name ) =>
				new( gateway.Keys.HostId, gateway.Keys.PublicKeyText, [], null, deviceId, name, DateTimeOffset.UtcNow );

			var (gatewayA, storeA) = StartHost( hostA, "a" );
			var (gatewayB, storeB) = StartHost( hostB, "b" );
			var (gatewayC, _) = StartHost( hostC, "c" );
			var (idA, idB, idC) = (gatewayA.Keys.HostId, gatewayB.Keys.HostId, gatewayC.Keys.HostId);
			var links = new ConcurrentDictionary<string, (HostGateway Gateway, string DeviceId)>( StringComparer.Ordinal )
			{
				[idA] = (gatewayA, AddDevice( gatewayA )),
				[idB] = (gatewayB, AddDevice( gatewayB )),
				[idC] = (gatewayC, AddDevice( gatewayC )),
			};
			var clientData = Path.Combine( folder, "client" );
			PairedHosts.Save( clientData, [Entry( gatewayA, links[idA].DeviceId, "Alpha" ), Entry( gatewayB, links[idB].DeviceId, "" )] );

			// This PC: its own chats, a host-bound probe panel type, and a browser tool it lends to the hosts' agents, which says whose agent called it.
			LoadOmp( client );
			var clientStore = new ConversationStore( new ProjectStore() );
			CoreChecks.Check( ProjectChecks.LoadProjects( client, clientStore ) && client.Load( new ConversationsModule( clientStore, ConversationsModule.BuildChat ) )
				&& client.Load( new FixtureModule( "browser", builder => builder
					.Tool( "probe", "Says whose agent called.", """{ "type": "object" }""", ( context, _ ) => new( context.Host ?? LoknHost.LocalId ) )
					.Panel( "probe", "Probe", _ => new ProbeContent(), onHost: true ) ) ),
				"The multi-host client loads Projects, Conversations and a lent browser tool." );
			var hooks = new RemoteHooks
			{
				DataFolder = clientData,
				Role = RemoteModule.LocalRole,
				Dial = ( hostId, cancellationToken ) =>
				{
					var (gateway, deviceId) = links[hostId];
					var (hostEnd, clientEnd) = MemoryConnection.CreatePair();
					_ = gateway.RunConnectionAsync( hostEnd, deviceId, "Laptop", DeviceScopes.Controlling );
					return Task.FromResult( new ClientCarrier( clientEnd, DeviceScopes.Controlling, "memory" ) );
				},
				Pair = ( link, cancellationToken ) =>
				{
					var hostId = link == "pair-c" ? idC : idA;
					var (gateway, deviceId) = links[hostId];
					return Task.FromResult<(PairedHost, IReadOnlyList<string>)>( (Entry( gateway, deviceId, "" ), DeviceScopes.Controlling) );
				},
			};
			var remote = new RemoteModule( hooks );
			CoreChecks.Check( client.Load( remote ) && remote.RunningRole == RemoteModule.LocalRole && remote.Daemon is null && remote.Links.Count == 2
				&& client.Hosts.All.Select( host => host.Id ).SequenceEqual( [LoknHost.LocalId, idA, idB] ),
				"A local-role Loom links every paired host at start, listed after This PC." );
			var linkA = remote.FindLink( idA )!;
			var linkB = remote.FindLink( idB )!;
			CoreChecks.Check( PumpUntil( () => linkA.Status == ClientState.Connected && linkB.Status == ClientState.Connected )
				&& client.Hosts.Find( idA ) is { State: LoknHostState.Connected, Name: "Alpha", CanControl: true }
				&& client.Hosts.Find( idB ) is { State: LoknHostState.Connected } bravo && bravo.Name == Environment.MachineName && client.Hosts.Local.State == LoknHostState.Local,
				"Both hosts connect at once; a host keeps the name the user gave it, else shows the one it reports, and This PC stays local." );

			var conversations = client.GetModule<ConversationsModule>();
			var chatsA = conversations.Hosts.Find( idA )!;
			var chatsB = conversations.Hosts.Find( idB )!;
			CoreChecks.Check( chatsA.IsSynced && chatsB.IsSynced && ReferenceEquals( chatsA.Host, client.Hosts.Find( idA ) ) && chatsA.Store.IsMirror && !clientStore.IsMirror,
				"Each paired host has its own synced mirror, and This PC's store stays its own." );
			var onA = storeA.CreateConversation( SettingsProfile.Personal );
			var onB = storeB.CreateConversation( SettingsProfile.Personal );
			CoreChecks.Check( PumpUntil( () => chatsA.Store.Find( onA.Id ) is not null && chatsB.Store.Find( onB.Id ) is not null )
				&& chatsA.Store.Find( onB.Id ) is null && chatsB.Store.Find( onA.Id ) is null && clientStore.Find( onA.Id ) is null && clientStore.Find( onB.Id ) is null,
				"Each host's chats reach its own mirror only, never This PC's or the other host's." );
			chatsA.Store.RenameConversation( onA.Id, "Renamed on A" );
			CoreChecks.Check( PumpUntil( () => storeA.Get( onA.Id ).Title == "Renamed on A" ) && storeB.Find( onA.Id ) is null,
				"A change made in one host's mirror reaches that host only." );
			chatsA.Sessions.GetOrCreate( onA.Id ).Attach( Guid.NewGuid() );
			CoreChecks.Check( PumpUntil( () => hostA.GetModule<ConversationsModule>().Hosts.Local.Sessions.Find( onA.Id ) is not null )
				&& hostB.GetModule<ConversationsModule>().Hosts.Local.Sessions.Find( onA.Id ) is null && chatsA.Sessions.Find( onA.Id ) is RemoteChatSession,
				"Attaching a chat of one host attaches it on that host alone." );
			CoreChecks.Check( linkA.Stream is not null && linkB.Stream is not null && !ReferenceEquals( linkA.Stream, linkB.Stream ), "Each paired host has its own window stream source." );

			string RunProbe( LoknApplication host )
			{
				Task<AgentToolResult>? call = null;
				PumpUntil( () => (call ??= host.Hosts.Local.BrowserTabs.Windows?.TryRun( new AgentToolContext( host, null, CancellationToken.None ), "browser_probe",
					JsonSerializer.SerializeToElement( new { } ) )) is not null );
				return call is null ? "no lender" : PumpFor( call ).Text;
			}

			CoreChecks.Check( RunProbe( hostA ) == idA && RunProbe( hostB ) == idB, "A tool call of a host's agent runs on this PC with that host as the call's host." );

			// A host added from its pairing link connects at once, without a restart, and becomes the current host.
			var added = PumpFor( remote.AddHostAsync( "pair-c", CancellationToken.None ) );
			CoreChecks.Check( added.HostId == idC && remote.Links.Count == 3 && PairedHosts.Read( clientData ).Any( host => host.HostId == idC )
				&& client.Hosts.Find( idC ) is not null && conversations.Hosts.Find( idC ) is not null && client.Hosts.Current.Id == idC
				&& PumpUntil( () => remote.FindLink( idC ) is { Status: ClientState.Connected } ),
				"Adding a host saves it, registers it with Core and Conversations, connects it without a restart and switches to it." );

			// Pairing again with a host replaces its entry, keeps its name and starts a new link on the same host.
			var hostOfA = client.Hosts.Find( idA );
			links[idA] = (gatewayA, AddDevice( gatewayA ));
			var repaired = PumpFor( remote.AddHostAsync( "pair-a", CancellationToken.None ) );
			CoreChecks.Check( repaired.Name == "Alpha" && repaired.DeviceId == links[idA].DeviceId && remote.Links.Count == 3 && ReferenceEquals( client.Hosts.Find( idA ), hostOfA )
				&& PumpUntil( () => remote.FindLink( idA ) is { Status: ClientState.Connected } link && link != linkA ) && linkA.Status == ClientState.Stopped,
				"Pairing again with a host replaces its entry, keeps its name, and relinks the same host." );

			remote.RenameHost( idB, "Bravo" );
			CoreChecks.Check( client.Hosts.Find( idB )!.Name == "Bravo" && PairedHosts.Read( clientData ).Single( host => host.HostId == idB ).Name == "Bravo", "A rename shows at once and is saved." );
			remote.RenameHost( idB, "" );
			CoreChecks.Check( client.Hosts.Find( idB )!.Name == Environment.MachineName, "An empty name shows the name the host reports again." );

			var probe = client.Panels.Open( "browser.probe", hostId: idB );
			CoreChecks.Check( client.Panels.GetHostId( probe ) == idB, "A host-bound panel opens on the host it is asked for." );
			remote.RemoveHost( idB );
			CoreChecks.Check( remote.Links.Count == 2 && remote.FindLink( idB ) is null && client.Hosts.Find( idB ) is null && conversations.Hosts.Find( idB ) is null
				&& PairedHosts.Read( clientData ).All( host => host.HostId != idB ) && client.Panels.Sessions.All( session => session.Info.Id != probe ) && linkB.Status == ClientState.Stopped,
				"Removing a host stops its link, forgets its chats, closes its panels and drops its entry." );

			// A reload of Remote keeps the hosts and their chats, stopped and offline meanwhile, and links them again by id.
			var chatsOfA = conversations.Hosts.Find( idA );
			client.Unload( "remote" );
			CoreChecks.Check( hostOfA is { State: LoknHostState.Stopped, Workspace: OfflineWorkspaceHost } && ReferenceEquals( client.Hosts.Find( idA ), hostOfA )
				&& ReferenceEquals( conversations.Hosts.Find( idA ), chatsOfA ), "Turning Remote off keeps the paired hosts and their chats, stopped and offline." );
			var reloaded = new RemoteModule( hooks );
			CoreChecks.Check( client.Load( reloaded ) && reloaded.Links.Count == 2 && PumpUntil( () => hostOfA!.State == LoknHostState.Connected )
				&& ReferenceEquals( conversations.Hosts.Find( idA ), chatsOfA ) && ReferenceEquals( reloaded.FindLink( idA )!.Host, chatsOfA ),
				"Loading Remote again links the same hosts and chats by id." );
		}
		finally
		{
			client.Shutdown();
			hostA.Shutdown();
			hostB.Shutdown();
			hostC.Shutdown();
			TryDelete( folder );
		}
	}

	/// <summary>Runs two hosts, each with its own disk recorder, and one local-role Loom linked to both at once: the Loom switches hosts without a restart,
	/// each host's chat runs a whole fixture turn on that host, and a Files tree and a Git panel opened on the second host reach that host's disk alone.</summary>
	private static void CheckTwoHostsEndToEnd()
	{
		var folder = Path.Combine( Path.GetTempPath(), "lokn-two-hosts-" + Guid.NewGuid().ToString( "N" ) );
		var repository = Path.Combine( folder, "repository" );
		Directory.CreateDirectory( repository );
		File.WriteAllText( Path.Combine( repository, "readme.txt" ), "one\n" );
		GitProcess.RunAsync( LocalWorkspaceHost.Shared, repository, CancellationToken.None, "init", "--quiet", "--initial-branch=main" ).GetAwaiter().GetResult();
		List<Action>[] queues = [[], [], []];
		var hostA = BuildApp( queues[0], isHost: true );
		var hostB = BuildApp( queues[1], isHost: true );
		var client = BuildApp( queues[2], isHost: false );
		try
		{
			bool PumpUntil( Func<bool> condition )
			{
				var clock = Stopwatch.StartNew();
				while ( !condition() && clock.Elapsed < StepTimeout )
				{
					foreach ( var queue in queues )
						Pump( queue );
					Thread.Sleep( 10 );
				}
				return condition();
			}

			(HostGateway Gateway, ConversationStore Store, RecordingDisk Disk) StartHost( LoknApplication app, string name )
			{
				var disk = new RecordingDisk();
				app.Hosts.Local.SetWorkspace( disk );
				LoadOmp( app );
				var store = new ConversationStore( new ProjectStore() );
				CoreChecks.Check( ProjectChecks.LoadProjects( app, store ) && app.Load( new WorkspacesModule( Path.Combine( folder, name + "-workspaces.json" ) ) )
					&& app.Load( new ConversationsModule( store, ConversationsModule.BuildChat ) ), $"Host {name} loads Projects, Workspaces and Conversations." );
				var hostRemote = new RemoteModule( new RemoteHooks { DataFolder = Path.Combine( folder, name ) } );
				CoreChecks.Check( app.Load( hostRemote ) && hostRemote.Gateway is not null, $"Host {name} starts its gateway." );
				return (hostRemote.Gateway!, store, disk);
			}

			var (gatewayA, storeA, diskA) = StartHost( hostA, "a" );
			var (gatewayB, storeB, diskB) = StartHost( hostB, "b" );
			var (idA, idB) = (gatewayA.Keys.HostId, gatewayB.Keys.HostId);
			var links = new Dictionary<string, (HostGateway Gateway, string DeviceId)>( StringComparer.Ordinal );
			foreach ( var gateway in new[] { gatewayA, gatewayB } )
			{
				var deviceId = Guid.NewGuid().ToString( "N" );
				using var keys = RemoteKeys.Create();
				gateway.Devices.Save( new PairedDevice { DeviceId = deviceId, Name = "Laptop", DeviceKey = keys.PublicKeyText, Scopes = DeviceScopes.Controlling, Paired = DateTimeOffset.UtcNow } );
				links[gateway.Keys.HostId] = (gateway, deviceId);
			}
			var clientData = Path.Combine( folder, "client" );
			PairedHosts.Save( clientData,
			[
				new( idA, gatewayA.Keys.PublicKeyText, [], null, links[idA].DeviceId, "Alpha", DateTimeOffset.UtcNow ),
				new( idB, gatewayB.Keys.PublicKeyText, [], null, links[idB].DeviceId, "Bravo", DateTimeOffset.UtcNow ),
			] );

			LoadOmp( client );
			var clientStore = new ConversationStore( new ProjectStore() );
			var git = new GitModule();
			CoreChecks.Check( ProjectChecks.LoadProjects( client, clientStore ) && client.Load( new WorkspacesModule( Path.Combine( folder, "client-workspaces.json" ) ) )
				&& client.Load( new ConversationsModule( clientStore, ConversationsModule.BuildChat ) ) && client.Load( new FilesModule() ) && client.Load( git ),
				"The two-host client loads Projects, Workspaces, Conversations, Files and Git." );
			client.Settings.Set( GitModule.Avatars, SettingsScope.Global, false );
			var remote = new RemoteModule( new RemoteHooks
			{
				DataFolder = clientData,
				Role = RemoteModule.LocalRole,
				Dial = ( hostId, cancellationToken ) =>
				{
					var (gateway, deviceId) = links[hostId];
					var (hostEnd, clientEnd) = MemoryConnection.CreatePair();
					_ = gateway.RunConnectionAsync( hostEnd, deviceId, "Laptop", DeviceScopes.Controlling );
					return Task.FromResult( new ClientCarrier( clientEnd, DeviceScopes.Controlling, "memory" ) );
				},
				Pair = ( link, cancellationToken ) => Task.FromResult<(PairedHost, IReadOnlyList<string>)>(
					(new PairedHost( idB, gatewayB.Keys.PublicKeyText, [], null, links[idB].DeviceId, "", DateTimeOffset.UtcNow ), DeviceScopes.Controlling) ),
			} );
			CoreChecks.Check( client.Load( remote ) && PumpUntil( () => remote.FindLink( idA ) is { Status: ClientState.Connected } && remote.FindLink( idB ) is { Status: ClientState.Connected } ),
				"The client links both hosts at the same time." );
			var conversations = client.GetModule<ConversationsModule>();
			var (alpha, bravo) = (client.Hosts.Find( idA )!, client.Hosts.Find( idB )!);
			var (chatsA, chatsB) = (conversations.Hosts.Find( idA )!, conversations.Hosts.Find( idB )!);
			CoreChecks.Check( PairedHosts.Read( clientData ).All( host => host.HostName == Environment.MachineName ),
				"Each host's reported name is saved with its entry, so it shows while the host is offline." );

			// Switching hosts moves the current host, its chats and the application's workspace alias, with both links staying up.
			foreach ( var (target, chats) in new[] { (alpha, chatsA), (bravo, chatsB), (client.Hosts.Local, conversations.Hosts.Local) } )
			{
				conversations.SwitchHost( client, target.Id );
				CoreChecks.Check( ReferenceEquals( client.Hosts.Current, target ) && ReferenceEquals( conversations.Hosts.Current, chats ) && ReferenceEquals( client.Workspace, target.Workspace )
					&& alpha.State == LoknHostState.Connected && bravo.State == LoknHostState.Connected, $"Switching to {target.Name} makes it current without a restart; both hosts stay connected." );
			}

			// Each host's chat runs a whole fixture turn on that host, at the same time.
			var chatA = chatsA.Store.CreateConversation( SettingsProfile.Personal );
			var chatB = chatsB.Store.CreateConversation( SettingsProfile.Personal );
			CoreChecks.Check( PumpUntil( () => storeA.Find( chatA.Id ) is not null && storeB.Find( chatB.Id ) is not null ) && storeA.Find( chatB.Id ) is null && storeB.Find( chatA.Id ) is null,
				"A chat created in a host's list is created on that host alone." );
			var sessionA = chatsA.Sessions.GetOrCreate( chatA.Id );
			var sessionB = chatsB.Sessions.GetOrCreate( chatB.Id );
			sessionA.Attach( Guid.NewGuid() );
			sessionB.Attach( Guid.NewGuid() );
			sessionA.Send( "Run on Alpha", [] );
			sessionB.Send( "Run on Bravo", [] );
			CoreChecks.Check( PumpUntil( () => sessionA.PendingDialog is not null && sessionB.PendingDialog is not null ), "Both hosts' turns run at once and each asks its question." );
			sessionA.AnswerDialog( "approval", new { type = "extension_ui_response", id = "approval", confirmed = true } );
			sessionB.AnswerDialog( "approval", new { type = "extension_ui_response", id = "approval", confirmed = true } );
			static bool HasTurn( ConversationStore store, Guid chat, string prompt ) =>
				store.GetMessages( chat ).Any( message => message.Role == "user" && message.Text == prompt )
				&& store.GetMessages( chat ).Any( message => message.Role == "assistant" && message.Text.StartsWith( "Streaming complete.", StringComparison.Ordinal ) );
			CoreChecks.Check( PumpUntil( () => !sessionA.IsBusy && !sessionB.IsBusy && HasTurn( storeA, chatA.Id, "Run on Alpha" ) && HasTurn( storeB, chatB.Id, "Run on Bravo" )
				&& HasTurn( chatsA.Store, chatA.Id, "Run on Alpha" ) && HasTurn( chatsB.Store, chatB.Id, "Run on Bravo" ) ),
				"Each host's turn completes on that host, and its messages reach that host's mirror." );

			void Settle()
			{
				for ( var round = 0; round < 30; round++ )
				{
					foreach ( var queue in queues )
						Pump( queue );
					Thread.Sleep( 10 );
				}
			}

			// Pairing with Bravo again while a project of This PC is open keeps This PC current, so panels opened from the palette stay on it, and Bravo's
			// window stream carries on over the new link.
			var localProject = clientStore.ProjectStore.Create( SettingsProfile.Personal, "Local repository", repository );
			conversations.OpenProject( client, conversations.Hosts.Local, localProject );
			var streamOfBravo = remote.FindLink( idB )!.Stream;
			var repairing = remote.AddHostAsync( "pair-b", CancellationToken.None );
			CoreChecks.Check( PumpUntil( () => repairing.IsCompleted ) && repairing.IsCompletedSuccessfully && ReferenceEquals( client.Hosts.Current, client.Hosts.Local )
				&& client.Panels.GetHostId( client.Panels.Open( "files.tree" ) ) == LoknHost.LocalId && ReferenceEquals( remote.FindLink( idB )!.Stream, streamOfBravo )
				&& PumpUntil( () => remote.FindLink( idB ) is { Status: ClientState.Connected } ),
				$"Adding a host while This PC's project is open keeps This PC current and the host's window stream ({repairing.Exception?.GetBaseException().Message})." );

			// A Files tree and a Git panel on Bravo while This PC's project is open get no folder: none of This PC's paths reach Bravo.
			diskA.Clear();
			diskB.Clear();
			var away = client.Panels.Open( "files.tree", hostId: idB );
			var awayContent = client.Panels.Sessions.Single( session => session.Info.Id == away ).Content!;
			var noFolderGit = client.Panels.Open( GitModule.PanelType, hostId: idB );
			awayContent.Build();
			Settle();
			CoreChecks.Check( ((IFileTreeView)awayContent).Root.Length == 0 && client.Panels.Sessions.Single( session => session.Info.Id == noFolderGit ).Content is not GitContent
				&& !diskB.Has( "list " ) && !diskB.Has( "watch " ) && !diskB.Has( "run " ),
				"A Files tree and a Git panel on a paired host without a project open there show no folder, and send the host no path of This PC's." );
			client.Panels.Close( away );
			client.Panels.Close( noFolderGit );

			// Once a project of Bravo is open, a Files tree and a Git panel there reach Bravo's disk only.
			var onBravo = chatsB.Projects.Create( SettingsProfile.Personal, "Repository", repository );
			CoreChecks.Check( PumpUntil( () => storeB.ProjectStore.Find( onBravo.Id ) is not null ), "A project created in Bravo's list is created on Bravo." );
			conversations.OpenProject( client, chatsB, onBravo );
			diskA.Clear();
			diskB.Clear();
			var tree = client.Panels.Open( "files.tree", hostId: idB );
			var treeContent = client.Panels.Sessions.Single( session => session.Info.Id == tree ).Content!;
			CoreChecks.Check( ReferenceEquals( client.Hosts.Current, bravo ) && PumpUntil( () =>
			{
				treeContent.Build();
				return diskB.Has( $"list {repository}" ) && diskB.Has( $"watch {repository}" );
			} ), "A Files tree on the second host lists and watches its project's folder on that host." );
			git.OpenPanel( client, tree, repository );
			var gitPanel = client.Panels.Panels.Select( panel => panel.Id ).Single( id => client.Panels.Sessions.Single( session => session.Info.Id == id ).Info.TypeId == GitModule.PanelType );
			CoreChecks.Check( client.Panels.GetHostId( gitPanel ) == idB && PumpUntil( () => diskB.Has( $"run {repository}" ) && diskB.Has( $"watch {repository}" ) ),
				"The Git panel opened beside it is on the second host, and runs git and watches the repository there." );
			CoreChecks.Check( !diskA.Has( "list " ) && !diskA.Has( "watch " ) && !diskA.Has( "run " ), "No call of the second host's panels reaches the first host." );
			CoreChecks.Check( client.LastError is null && hostA.LastError is null && hostB.LastError is null, $"The two-host run has no errors. {client.LastError ?? hostA.LastError ?? hostB.LastError}" );
		}
		finally
		{
			client.Shutdown();
			hostA.Shutdown();
			hostB.Shutdown();
			// Git marks its objects read-only; the folder goes even when a host's watch on it is still closing.
			foreach ( var file in Directory.EnumerateFiles( folder, "*", SearchOption.AllDirectories ) )
				File.SetAttributes( file, FileAttributes.Normal );
			TryDelete( folder );
		}
	}

	/// <summary>Starts a Loom whose settings still name the former <c>remote</c> role and whose data folder holds its <c>remote-host.json</c>: the role
	/// becomes <c>local</c> and the host is imported and linked beside This PC.</summary>
	private static void CheckRoleMigration()
	{
		const string roleSlot = "Lokn.Modules.Remote.Role";
		var folder = Path.Combine( Path.GetTempPath(), "lokn-migration-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( folder );
		var settingsPath = Path.Combine( folder, "settings.json" );
		var seed = new LoknApplication( settingsPath );
		CoreChecks.Check( seed.Load( new FixtureModule( "remote", builder => builder.Setting( RemoteModule.Role ) ) ) && seed.Settings.Set( RemoteModule.Role, SettingsScope.Global, "remote" ),
			"An older Loom's settings name the remote role." );
		seed.Shutdown();
		var data = Path.Combine( folder, "data" );
		Directory.CreateDirectory( data );
		using var hostKeys = RemoteKeys.Create();
		File.WriteAllText( PairedHosts.ComputeLegacyPath( data ), JsonSerializer.Serialize( new { hostId = hostKeys.HostId, hostKey = hostKeys.PublicKeyText, addrs = Array.Empty<string>(), deviceId = "device" } ) );

		// The latched role is process-wide; this check starts from none and leaves none.
		AppDomain.CurrentDomain.SetData( roleSlot, null );
		var queue = new List<Action>();
		var app = BuildApp( queue, isHost: false, settingsPath );
		try
		{
			LoadOmp( app );
			var store = new ConversationStore( new ProjectStore() );
			CoreChecks.Check( ProjectChecks.LoadProjects( app, store ) && app.Load( new ConversationsModule( store, ConversationsModule.BuildChat ) ), "The migrating Loom loads Projects and Conversations." );
			var module = new RemoteModule( new RemoteHooks
			{
				DataFolder = data,
				Dial = async ( hostId, cancellationToken ) =>
				{
					await Task.Delay( Timeout.Infinite, cancellationToken );
					throw new OperationCanceledException( cancellationToken );
				},
			} );
			CoreChecks.Check( app.Load( module ) && module.RunningRole == RemoteModule.LocalRole && module.Daemon is null
				&& app.Settings.Get( RemoteModule.Role, SettingsScope.Global ) == RemoteModule.LocalRole,
				"The former remote role is rewritten to local before it is latched, so This PC's own chats show again." );
			CoreChecks.Check( module.Links.Count == 1 && app.Hosts.Find( hostKeys.HostId ) is { State: LoknHostState.Connecting } && !File.Exists( PairedHosts.ComputeLegacyPath( data ) )
				&& PairedHosts.Read( data ).Single().HostId == hostKeys.HostId, "Its paired host is imported into remote-hosts.json and linked beside This PC." );
		}
		finally
		{
			app.Shutdown();
			AppDomain.CurrentDomain.SetData( roleSlot, null );
			TryDelete( folder );
		}
	}

	/// <summary>Checks what keeps a client from acting on the wrong data: a paired host's offline workspace and stream before it connects and after
	/// its link stops, the data folder's single-writer lock, upload names and the Run key's quoting.</summary>
	private static void CheckClientGuards()
	{
		var folder = Path.Combine( Path.GetTempPath(), "lokn-guards-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( folder );
		var queue = new List<Action>();
		var app = BuildApp( queue, isHost: false );
		try
		{
			// A paired host that never answers: nothing may fall back to this PC's disk.
			LoadOmp( app );
			var store = new ConversationStore( new ProjectStore() );
			CoreChecks.Check( ProjectChecks.LoadProjects( app, store ) && app.Load( new ConversationsModule( store, ConversationsModule.BuildChat ) ),
				"The guard client loads Projects and Conversations." );
			using var hostKeys = RemoteKeys.Create();
			var dataFolder = Path.Combine( folder, "client" );
			PairedHosts.Save( dataFolder, [new PairedHost( hostKeys.HostId, hostKeys.PublicKeyText, [], null, "device", "Silent", DateTimeOffset.UtcNow )] );
			var module = new RemoteModule( new RemoteHooks
			{
				DataFolder = dataFolder,
				Role = RemoteModule.LocalRole,
				Dial = async ( hostId, cancellationToken ) =>
				{
					await Task.Delay( Timeout.Infinite, cancellationToken );
					throw new OperationCanceledException( cancellationToken );
				},
			} );
			CoreChecks.Check( app.Load( module ) && module.FindLink( hostKeys.HostId ) is { Stream: not null }, "The paired host's link starts without a connection." );
			var stream = module.FindLink( hostKeys.HostId )!.Stream!;
			var paired = app.Hosts.Find( hostKeys.HostId )!;
			var file = Path.Combine( folder, "local.txt" );
			File.WriteAllText( file, "this PC" );
			var read = paired.Workspace.ReadAsync( file, CancellationToken.None );
			CoreChecks.Check( paired.Workspace is not LocalWorkspaceHost && read.IsFaulted && read.Exception?.GetBaseException() is IOException
				&& ReferenceEquals( app.Hosts.Local.Workspace, LocalWorkspaceHost.Shared ),
				"Before the first connection a paired host's workspace fails instead of reading this PC's file at the host's path; This PC's stays local." );
			Exception? listed = null;
			try
			{
				_ = stream.ListAsync( CancellationToken.None );
			}
			catch ( IOException exception )
			{
				listed = exception;
			}
			CoreChecks.Check( listed is not null && !stream.CanControl, "A stream panel opened before the first connection gets the link's stream, which fails until it connects." );
			app.Unload( "remote" );
			CoreChecks.Check( paired is { State: LoknHostState.Stopped } && paired.Workspace is not LocalWorkspaceHost && paired.Workspace.StatAsync( file, CancellationToken.None ).IsFaulted,
				"A stopped link leaves its host stopped with an offline workspace, not this PC's." );
		}
		finally
		{
			app.Shutdown();
		}

		// One process at a time writes a data folder's chats.
		var data = Path.Combine( folder, "data" );
		using ( var first = DataLock.TryTake( data ) )
			CoreChecks.Check( first is not null && DataLock.TryTake( data ) is null, "The data folder lock admits one writer at a time." );
		using ( var again = DataLock.TryTake( data ) )
			CoreChecks.Check( again is not null, "A released data folder lock can be taken again." );

		CoreChecks.Check( ChatService.BuildSafeName( "NUL" ) == "_NUL" && ChatService.BuildSafeName( "com1.txt" ) == "_com1.txt"
			&& ChatService.BuildSafeName( @"..\x/report.txt" ) == "report.txt" && ChatService.BuildSafeName( "notes. " ) == "notes",
			"Upload names keep their last segment, lose trailing dots and spaces, and never name a Windows device." );
		if ( OperatingSystem.IsWindows() )
		{
			CoreChecks.Check( DaemonControl.QuoteArgument( "--host" ) == "--host" && DaemonControl.QuoteArgument( @"C:\My Folder\" ) == "\"C:\\My Folder\\\\\""
				&& DaemonControl.QuoteArgument( "a\"b c" ) == "\"a\\\"b c\"" && DaemonControl.QuoteArgument( "" ) == "\"\"",
				"Run key arguments are quoted so a folder ending in a backslash keeps its closing quote." );
		}
		TryDelete( folder );
	}

	/// <summary>Starts the built Loom.exe as a bare daemon over a temporary folder, runs a fixture turn over its pipe, then stops it.</summary>
	/// <returns>False when the check was skipped because the daemon runs on Windows only.</returns>
	private static async Task<bool> CheckDaemonProcessAsync()
	{
		if ( !OperatingSystem.IsWindows() )
		{
			Console.WriteLine( "SKIP remote daemon process: the daemon runs on Windows only." );
			return false;
		}
		var loom = FindLoom() ?? throw new InvalidOperationException( "No built Loom.exe under the repository's bin folder or next to the checks; run scripts/build.ps1 first." );

		var bare = Path.Combine( Path.GetTempPath(), "lokn-daemon-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( bare );
		var settings = new LoknApplication( Path.Combine( bare, "settings.json" ) );
		LoadOmp( settings );
		// The icon in the notification area has its own window check; this headless one leaves the desktop alone.
		HostTrayChecks.SaveTray( settings, isShown: false );
		settings.Shutdown();
		// The window's choices: Git on, Browser off. Remote and the modules it needs run whatever the file says.
		File.WriteAllText( Path.Combine( bare, "modules.json" ), """{ "On": [ "git" ], "Off": [ "browser" ] }""" );

		using var daemon = StartLoom( loom, "--host", "--bare", bare );
		RpcPeer? peer = null;
		try
		{
			var pipeName = PipeHost.ComputePipeName( bare );
			PipeConnection? connection = null;
			var clock = Stopwatch.StartNew();
			while ( connection is null && clock.Elapsed < StepTimeout && !daemon.HasExited )
				connection = await PipeDialer.ConnectAsync( pipeName, TimeSpan.FromSeconds( 1 ), CancellationToken.None );
			CoreChecks.Check( connection is not null, "The bare daemon opens its pipe. " + ReadLog( bare ) );

			var pushes = new ConcurrentQueue<JsonObject>();
			peer = new RpcPeer( connection! );
			foreach ( var type in (string[])["state", "msg", "msgs", "working"] )
				peer.OnPush( type, pushes.Enqueue );
			peer.Start();

			var sync = await peer.RequestAsync( Frame.Create( "sync" ), CancellationToken.None );
			CoreChecks.Check( Frame.ReadString( sync, "role" ) == RemoteModule.HostRole && sync["conversations"] is JsonArray, "The daemon answers sync with its store." );

			var chatId = Guid.NewGuid();
			await peer.RequestAsync( BuildStore( StoreOp.Build( StoreOp.Conversations, nameof( ConversationStore.CreateConversation ), new
			{
				id = chatId,
				createdAt = DateTimeOffset.UtcNow,
				profile = SettingsProfile.Personal,
				projectId = (Guid?)null,
				title = "Daemon check",
				hasChat = true,
			} ) ), CancellationToken.None );
			await peer.RequestAsync( BuildStore( StoreOp.Build( StoreOp.Conversations, nameof( ConversationStore.SaveBackendSession ), new
			{
				id = chatId,
				sessionDirectory = Path.Combine( bare, "omp-session" ),
			} ) ), CancellationToken.None );

			var attach = Frame.Create( "attach" ).WithChannel( chatId );
			attach["since"] = -1;
			var attached = await peer.RequestAsync( attach, CancellationToken.None );
			CoreChecks.Check( attached["state"] is JsonObject && attached["messages"] is JsonArray, "The daemon answers attach with the chat's state and messages." );

			await peer.RequestAsync( BuildCall( chatId, ChatSessionCall.Send, "Run daemon", Array.Empty<string>() ), CancellationToken.None );
			CoreChecks.Check( await WaitUntilAsync( () => FindStates( pushes, chatId ).Any( state => state["dialogs"] is JsonArray { Count: > 0 } ) ),
				"The daemon's turn asks the fixture's question in a state push. " + ReadLog( bare ) );
			await peer.RequestAsync( BuildCall( chatId, ChatSessionCall.AnswerDialog, "approval", new { type = "extension_ui_response", id = "approval", confirmed = true } ), CancellationToken.None );
			CoreChecks.Check( await WaitUntilAsync( () => FindStates( pushes, chatId ).Any( state => Frame.ReadBool( state, "isBusy" ) == true )
				&& FindStates( pushes, chatId ).LastOrDefault() is { } last && Frame.ReadBool( last, "isBusy" ) == false && last["dialogs"] is JsonArray { Count: 0 } ),
				"The answered turn settles: state pushes go from busy to idle." );
			CoreChecks.Check( await WaitUntilAsync( () => pushes.Any( push => Frame.ReadType( push ) == "msg" && Frame.ReadChannel( push ) == chatId
				&& push["m"] is JsonObject message && Frame.ReadString( message, "role" ) == "assistant" && Frame.ReadString( message, "text" )?.StartsWith( "Streaming complete.", StringComparison.Ordinal ) == true ) )
				&& pushes.Any( push => Frame.ReadType( push ) == "working" && Frame.ReadChannel( push ) == chatId && Frame.ReadBool( push, "busy" ) == true ),
				"The daemon pushes the turn's messages and the chat's working state." );

			var ready = ReadLog( bare ).Split( '\n' ).FirstOrDefault( line => line.Contains( "READY modules: ", StringComparison.Ordinal ) ) ?? "";
			string[] started = [.. (ready.Split( "READY modules: " ).ElementAtOrDefault( 1 )?.Trim().Split( ", " ) ?? []).Order( StringComparer.Ordinal )];
			CoreChecks.Check( started is ["conversations", "git", "markdown", "omp", "projects", "remote", "workspaces"],
				"The bare daemon starts the modules its modules.json turns on, with Remote and every module it needs, and not Browser. " + ready );
			File.WriteAllText( Path.Combine( bare, "modules.json" ), """{ "On": [ "browser" ], "Off": [ "git" ] }""" );
			CoreChecks.Check( await WaitUntilAsync( () => ReadLog( bare ).Contains( "DISABLED git", StringComparison.Ordinal ) && ReadLog( bare ).Contains( "ENABLED browser", StringComparison.Ordinal ) ),
				"The daemon follows modules.json live: Git stops and Browser starts. " + ReadLog( bare ) );
		}
		finally
		{
			if ( peer is not null )
				await peer.DisposeAsync();
			using ( var stop = StartLoom( loom, "--host", "--stop", "--bare", bare ) )
				stop.WaitForExit( (int)StepTimeout.TotalMilliseconds );
			var hasExited = daemon.WaitForExit( (int)StepTimeout.TotalMilliseconds );
			if ( !hasExited )
				daemon.Kill( entireProcessTree: true );
			CoreChecks.Check( hasExited, "Loom.exe --host --stop ends the bare daemon. " + ReadLog( bare ) );
			TryDelete( bare );
		}
		return true;
	}

	/// <summary>Creates an application whose UI thread is a queue the check pumps, with its settings in memory or in a file.</summary>
	private static LoknApplication BuildApp( List<Action> queue, bool isHost, string? settingsPath = null )
	{
		var app = settingsPath is null ? new LoknApplication() { IsHost = isHost } : new LoknApplication( settingsPath ) { IsHost = isHost };
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

	/// <summary>Builds a <c>call</c> request with its arguments serialized as the protocol does.</summary>
	private static JsonObject BuildCall( Guid chatId, string method, params object?[] args )
	{
		var frame = Frame.Create( "call" ).WithChannel( chatId );
		frame["m"] = method;
		frame["a"] = new JsonArray( [.. ChatSessionCall.BuildArgs( args ).Select( arg => JsonSerializer.SerializeToNode( arg ) )] );
		return frame;
	}

	/// <summary>Builds a <c>store</c> request.</summary>
	private static JsonObject BuildStore( StoreOp op )
	{
		var frame = Frame.Create( "store" );
		frame["op"] = JsonSerializer.SerializeToNode( op, StoreOp.JsonOptions );
		return frame;
	}

	/// <summary>Lists a chat's pushed states, oldest first.</summary>
	private static IEnumerable<JsonObject> FindStates( ConcurrentQueue<JsonObject> pushes, Guid chatId ) => pushes
		.Where( push => Frame.ReadType( push ) == "state" && Frame.ReadChannel( push ) == chatId )
		.Select( push => push["s"] ).OfType<JsonObject>();

	/// <summary>Polls a condition until it holds or a step's time is up.</summary>
	private static async Task<bool> WaitUntilAsync( Func<bool> condition )
	{
		var clock = Stopwatch.StartNew();
		while ( !condition() && clock.Elapsed < StepTimeout )
			await Task.Delay( 50 );
		return condition();
	}

	/// <summary>Finds the built Loom.exe: in the repository's bin folder for this build's configuration, then the other one, then next to the checks with its modules.</summary>
	internal static string? FindLoom()
	{
		var baseDirectory = AppContext.BaseDirectory;
		var configuration = baseDirectory.Contains( $"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase ) ? "Debug" : "Release";
		for ( var directory = new DirectoryInfo( baseDirectory ); directory is not null; directory = directory.Parent )
		{
			if ( !File.Exists( Path.Combine( directory.FullName, "Lokn.csproj" ) ) )
				continue;

			foreach ( var candidate in new[] { configuration, configuration == "Debug" ? "Release" : "Debug" }.Select( name => Path.Combine( directory.FullName, "bin", name, "net10.0", "Loom.exe" ) ) )
			{
				if ( File.Exists( candidate ) )
					return candidate;
			}
			break;
		}

		var beside = Path.Combine( baseDirectory, "Loom.exe" );
		return File.Exists( beside ) && Directory.Exists( Path.Combine( baseDirectory, "modules" ) ) ? beside : null;
	}

	/// <summary>Starts Loom.exe hidden with arguments.</summary>
	internal static Process StartLoom( string loom, params string[] args )
	{
		var info = new ProcessStartInfo( loom ) { UseShellExecute = false, CreateNoWindow = true };
		foreach ( var arg in args )
			info.ArgumentList.Add( arg );
		return Process.Start( info ) ?? throw new InvalidOperationException( "Loom.exe did not start." );
	}

	/// <summary>Reads the end of a bare daemon's log, for a failure message.</summary>
	internal static string ReadLog( string bare )
	{
		var path = Path.Combine( bare, "host.log" );
		try
		{
			return File.Exists( path ) ? "Log: " + string.Join( " | ", File.ReadAllLines( path ).TakeLast( 8 ) ) : "No host.log.";
		}
		catch ( IOException )
		{
			return "The host.log is busy.";
		}
	}

	/// <summary>Deletes a temporary folder, leaving it when a file is still in use.</summary>
	internal static void TryDelete( string folder )
	{
		try
		{
			Directory.Delete( folder, recursive: true );
		}
		catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
		{
		}
	}

	/// <summary>Stands for one host's disk: passes every call to this PC's and records it, as <c>run</c>, <c>list</c>, <c>watch</c> and so on with the path,
	/// so a check sees which host a call reached; git runs as the executable this PC found.</summary>
	private sealed class RecordingDisk : IWorkspaceHost
	{
		/// <summary>Stores the calls, such as <c>list C:\folder</c>.</summary>
		private readonly List<string> _calls = [];

		/// <summary>Gets whether a call starting with a prefix was recorded, the path compared without case.</summary>
		internal bool Has( string prefix )
		{
			lock ( _calls )
				return _calls.Any( call => call.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) );
		}

		/// <summary>Forgets the recorded calls.</summary>
		internal void Clear()
		{
			lock ( _calls )
				_calls.Clear();
		}

		/// <summary>Records a call.</summary>
		private void Record( string kind, string path )
		{
			lock ( _calls )
				_calls.Add( $"{kind} {Path.TrimEndingDirectorySeparator( path )}" );
		}

		/// <summary>Runs a program on this PC and records its folder.</summary>
		public Task<WorkspaceProcessResult> RunAsync( string file, IReadOnlyList<string> arguments, string directory, string? input, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken )
		{
			Record( "run", directory );
			return LocalWorkspaceHost.Shared.RunAsync( file == "git" ? GitProcess.Executable : file, arguments, directory, input, environment, cancellationToken );
		}

		/// <summary>Describes a path on this PC and records it.</summary>
		public Task<WorkspaceStat> StatAsync( string path, CancellationToken cancellationToken )
		{
			Record( "stat", path );
			return LocalWorkspaceHost.Shared.StatAsync( path, cancellationToken );
		}

		/// <summary>Lists a folder on this PC and records it.</summary>
		public Task<IReadOnlyList<WorkspaceEntry>> ListAsync( string path, CancellationToken cancellationToken )
		{
			Record( "list", path );
			return LocalWorkspaceHost.Shared.ListAsync( path, cancellationToken );
		}

		/// <summary>Reads a file on this PC and records it.</summary>
		public Task<byte[]> ReadAsync( string path, CancellationToken cancellationToken )
		{
			Record( "read", path );
			return LocalWorkspaceHost.Shared.ReadAsync( path, cancellationToken );
		}

		/// <summary>Writes a file on this PC and records it.</summary>
		public Task WriteAsync( string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken )
		{
			Record( "write", path );
			return LocalWorkspaceHost.Shared.WriteAsync( path, data, cancellationToken );
		}

		/// <summary>Deletes a path on this PC and records it.</summary>
		public Task DeleteAsync( string path, CancellationToken cancellationToken )
		{
			Record( "delete", path );
			return LocalWorkspaceHost.Shared.DeleteAsync( path, cancellationToken );
		}

		/// <summary>Moves a path on this PC and records it.</summary>
		public Task MoveAsync( string path, string to, CancellationToken cancellationToken )
		{
			Record( "move", path );
			return LocalWorkspaceHost.Shared.MoveAsync( path, to, cancellationToken );
		}

		/// <summary>Creates a folder on this PC and records it.</summary>
		public Task CreateDirectoryAsync( string path, CancellationToken cancellationToken )
		{
			Record( "mkdir", path );
			return LocalWorkspaceHost.Shared.CreateDirectoryAsync( path, cancellationToken );
		}

		/// <summary>Watches a folder on this PC and records it.</summary>
		public IDisposable Watch( string path, bool recursive, Action<string, WorkspaceChange> changed )
		{
			Record( "watch", path );
			return LocalWorkspaceHost.Shared.Watch( path, recursive, changed );
		}

		/// <summary>Opens a pseudoconsole on this PC and records its folder.</summary>
		public IWorkspacePty OpenPty( string file, IReadOnlyList<string> arguments, string? directory, int columns, int rows, IReadOnlyDictionary<string, string>? environment, Action<string> output, Action<int> exited )
		{
			Record( "pty", directory ?? "" );
			return LocalWorkspaceHost.Shared.OpenPty( file, arguments, directory, columns, rows, environment, output, exited );
		}
	}

	/// <summary>An empty panel: the content of the host-bound probe panel type.</summary>
	private sealed class ProbeContent : PanelContent
	{
		/// <summary>Builds nothing.</summary>
		public override Blob Build() => new Container();
	}
}
