using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Goo;
using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;
using Lokn.Modules.Remote;
using Lokn.Modules.Workspaces;
using Lokn.Relay;

namespace Lokn.Tests;

/// <summary>Checks the Remote module's surfaces in native Goo windows: Remote access in the local and daemon roles and with a paired host, pairing with
/// its QR code and the Allow control right away switch, the pairing confirm dialog, the devices list, Add host and Remove, and the Window stream panel
/// with frames of a real window, through an in-process gateway and relay.</summary>
/// <remarks>
/// Every application keeps its settings and stores in memory and its files in a temporary folder. The daemon-role windows reach the in-process gateway
/// over in-memory connections, never this PC's daemon, and nothing presses Stop daemon, which would stop the user's. Add host dials the in-process
/// relay on loopback. Streams capture the checks' own window, and no input is sent to a stream. Input focuses its target first and every step waits on
/// the state it changes (<see cref="UiInput"/>), so a window that lost the system focus, as the daemon's does while the paired PC's window shows, still
/// takes it.
/// </remarks>
internal static class RemoteUiChecks
{
	/// <summary>Defines the title of the checks' windows, which the Window stream panel lists.</summary>
	private const string WindowTitle = "Lokn remote access checks";

	/// <summary>Defines the frame count used to settle layout and accessibility updates.</summary>
	private const int SettleFrames = 16;

	/// <summary>Defines how long one step may take; the daemon's status in Remote access refreshes every five seconds.</summary>
	private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds( 30 );

	/// <summary>Runs the local role and stream checks, then the daemon, paired host and devices checks against one gateway, failing when a view faulted.</summary>
	internal static void Run()
	{
		Application.VerifyState = true;
		var faults = new List<string>();
		void Record( Type view, Exception error ) => faults.Add( $"{view.Name}: {error.Message}" );
		CellFaults.Faulted += Record;
		var folder = Path.Combine( Path.GetTempPath(), "lokn-remote-ui-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( folder );
		try
		{
			CheckLocalRole( folder );
			CoreChecks.Check( faults.Count == 0, "No view faulted in the local role: " + string.Join( "; ", faults ) );
			CheckHostRoles( folder );
			CoreChecks.Check( faults.Count == 0, "No view faulted in the daemon and paired host roles: " + string.Join( "; ", faults ) );
		}
		finally
		{
			CellFaults.Faulted -= Record;
		}

		TryDelete( folder );
		Console.WriteLine( "PASS native Remote access: This PC's role choice and the empty Hosts section; the daemon role's status bar entry, status, stop confirmation, relay network mode, pairing QR code withdrawn by Done, by closing Remote access and by closing Loom, a link that pairs with control behind its warning, and devices with Allow control and Revoke; the pairing confirm dialog's Allow control and View only; Add host's refused links, pairing through a relay with the link the daemon's window shows, as view only, control granted, and Remove; the Window stream panel's window list and frames of a real window, in process and over the relay; clicks that land after the window lost the system focus; no faulted views." );
	}

	/// <summary>Checks Remote access in the <c>local</c> role, then the Window stream panel over <see cref="LocalStreamSource"/>: its list and a frame of this window.</summary>
	private static void CheckLocalRole( string folder )
	{
		var app = BuildApp();
		var module = new RemoteModule( new RemoteHooks { DataFolder = Path.Combine( folder, "local" ), Role = RemoteModule.LocalRole } );
		var screen = new Screen( app, [], () => CoreChecks.Check( app.Load( module ) && module.Daemon is null && module.Gateway is null && module.Links.Count == 0,
			"The Remote module loads in the local role without a link." ) );

		app.Panels.Open( AccessContent.TypeId, input: AccessContent.AddHostInput );
		screen.PumpUntil( () => screen.HasText( "Chats on this PC run in" ), "Remote access opens in the local role." );
		CoreChecks.Check( screen.HasButton( "This Loom, chosen" ) && screen.HasButton( "Daemon on this PC" ) && !screen.HasButton( "Paired host" )
			&& !screen.HasText( "Network access" ) && screen.HasText( "No host is paired yet." ) && screen.Has( AccessibilityRole.TextInput, "Link" )
			&& screen.Find( node => node.Role == AccessibilityRole.Button && node.Name == "Add" ) is { Disabled: true },
			"In the local role Remote access offers This Loom (chosen) and the daemon for This PC, no daemon section, and an empty Hosts section whose Add waits for a link." );
		CoreChecks.Check( screen.Find( node => node.Role == AccessibilityRole.Button && node.Name?.StartsWith( "Daemon: ", StringComparison.Ordinal ) == true ) is null,
			"The local role shows no connection in the status bar." );
		// Another program taking the system focus makes Goo drop raw presses; the click focuses its target first, so it still lands.
		screen.Window.PlatformInput.FocusLost();
		screen.ClickButton( "Daemon on this PC", () => screen.HasButton( "Daemon on this PC, chosen" ) && !screen.HasText( "until it restarts" ) && screen.HasText( "Preview" ),
			"Choosing the daemon marks it without asking for a restart, also after the window lost the system focus, and the Hosts section is marked as a preview." );
		CoreChecks.Check( app.Settings.Get( RemoteModule.Role, SettingsScope.Global ) == RemoteModule.DaemonRole && module.RunningRole == RemoteModule.LocalRole && module.Switching is null,
			"The chosen role is saved; this check fixes the module's role, so it moves no chats (HandoverChecks moves them)." );

		app.Panels.Open( $"remote.{StreamPanel.Id}" );
		screen.PumpUntil( () => screen.HasButton( "Stream " + WindowTitle ), "The Window stream panel lists this PC's windows, this check's among them." );
		CoreChecks.Check( screen.HasText( "Stream a window" ) && screen.HasButton( "Refresh" ), "The window list has its heading and Refresh." );
		screen.ClickButton( "Stream " + WindowTitle, () => screen.HasText( " fps · " ),
			"A frame of this window arrives from the local stream source and the toolbar shows its size, frame rate and latency.", retry: false );
		// The stream's toolbar marks a view-only stream " · view only"; Remote access, still open beside it, mentions view only in its Hosts help.
		CoreChecks.Check( screen.Find( node => node.Name == "Stream of " + WindowTitle ) is { Bounds: { Width: > 0, Height: > 0 } } && !screen.HasText( " · view only" ),
			"The frame surface is named for the streamed window, and a local stream is not view only." );
		screen.ClickButton( "Windows", () => screen.HasButton( "Stream " + WindowTitle ), "Windows stops the stream and lists the windows again.", retry: false );
		CoreChecks.Check( app.LastError is null, $"The local role's panels report no failure ({app.LastError})." );
		screen.Close();
	}

	/// <summary>Runs a host with its gateway and an in-process relay, and checks the daemon role, the paired host role and the devices list against them.</summary>
	private static void CheckHostRoles( string folder )
	{
		var relay = RelayApp.Build( ["http://127.0.0.1:0"], ["--Logging:LogLevel:Default=None"] );
		relay.StartAsync().GetAwaiter().GetResult();
		var relayUrl = relay.Urls.Single();
		var hostQueue = new List<Action>();
		var host = new LoknApplication { IsHost = true };
		host.BindDispatcher( action =>
		{
			lock ( hostQueue )
				hostQueue.Add( action );
			return true;
		} );
		try
		{
			LoadOmp( host );
			var hostStore = new ConversationStore( new ProjectStore() );
			var hostRemote = new RemoteModule( new RemoteHooks { DataFolder = Path.Combine( folder, "host" ) } );
			CoreChecks.Check( ProjectChecks.LoadProjects( host, hostStore ) && host.Load( new WorkspacesModule( Path.Combine( folder, "workspaces.json" ) ) )
				&& host.Load( new ConversationsModule( hostStore, ConversationsModule.BuildChat ) ) && host.Load( hostRemote ) && hostRemote.Gateway is not null,
				"The host loads its stores and starts the gateway." );
			var gateway = hostRemote.Gateway!;

			// The daemon's window keeps showing its link while the paired PC pastes it, as a link works only while it is shown.
			var (withdrawn, live, daemon) = CheckDaemonRole( Path.Combine( folder, "daemon" ), hostQueue, gateway, relayUrl );
			var deviceId = CheckRemoteRole( Path.Combine( folder, "remote" ), hostQueue, gateway, relayUrl, withdrawn, live );
			CloseShowingLink( daemon, gateway );
			CheckDevices( Path.Combine( folder, "daemon-again" ), hostQueue, gateway, deviceId );
			CoreChecks.Check( host.LastError is null, $"The host reports no failure ({host.LastError})." );
		}
		finally
		{
			host.Shutdown();
			using var now = new CancellationTokenSource( TimeSpan.FromMilliseconds( 500 ) );
			try
			{
				relay.StopAsync( now.Token ).GetAwaiter().GetResult();
			}
			catch ( OperationCanceledException )
			{
				// The relay's sockets were aborted.
			}
			relay.DisposeAsync().AsTask().GetAwaiter().GetResult();
		}
	}

	/// <summary>Checks Remote access in the <c>daemon</c> role: the status bar entry, the daemon's status, the stop confirmation, the relay mode, and pairing
	/// with its QR code, withdrawn by Done and by closing Remote access.</summary>
	/// <returns>The link withdrawn with Done, the live link shown last, and the daemon's window, which still shows it.</returns>
	private static (string Withdrawn, string Live, Screen Screen) CheckDaemonRole( string dataFolder, List<Action> hostQueue, HostGateway gateway, string relayUrl )
	{
		var (app, module, screen) = OpenDaemonClient( dataFolder, hostQueue, gateway );
		var client = module.Daemon!;
		screen.PumpUntil( () => client.Status == ClientState.Connected, "The daemon-role client connects to the gateway." );
		bool IsAccessOpen() => app.Panels.Panels.Any( panel => panel.TypeId == AccessContent.TypeId );
		screen.ClickUntil( node => node.Role == AccessibilityRole.Button && node.Name?.StartsWith( $"Daemon: {Environment.MachineName}. ", StringComparison.Ordinal ) == true,
			"the status bar's daemon connection", IsAccessOpen, "The status bar's daemon connection opens Remote access." );
		screen.PumpUntil( () => screen.HasText( "Network access" ), "Remote access reads the daemon's status." );
		CoreChecks.Check( screen.HasButton( "Daemon on this PC, chosen" ) && screen.HasText( "Connected to " + Environment.MachineName ) && screen.HasButton( "Stop daemon\u2026" )
			&& screen.HasButton( "Start the daemon at sign-in: off" ) && screen.HasText( "host id " + gateway.Keys.HostId ) && screen.HasText( RemoteClient.DaemonLogPath )
			&& screen.HasButton( "Off, chosen" ) && screen.HasText( "Only Loom on this PC reaches the daemon" ),
			"In the daemon role Remote access shows the daemon's connection, its log, the sign-in switch, the host id and the network mode." );
		CoreChecks.Check( screen.HasText( "A new device gets view only until you allow control. " + AccessContent.ViewText ) && screen.HasText( "No device is paired." )
			&& screen.HasButton( "Allow control right away: off" ) && !screen.HasText( "Anyone who opens this link" ),
			"Before pairing, Remote access says what a new device may do, offers control right away switched off, and lists no devices." );

		screen.ClickButton( "Stop daemon\u2026", () => screen.HasText( "Stopping the daemon stops its running agent turns" ) && screen.HasButton( "Stop daemon" ) && screen.HasButton( "Cancel" ),
			"Stop daemon asks to confirm first." );
		screen.ClickButton( "Cancel", () => screen.HasButton( "Stop daemon\u2026" ) && !screen.HasText( "Stopping the daemon stops" ), "Cancel keeps the daemon." );
		CoreChecks.Check( client.Status == ClientState.Connected, "Cancelling the stop leaves the daemon connected." );

		var port = FindFreePort();
		screen.ClickButton( "Relay", () => screen.HasButton( "Relay, chosen" ) && screen.Has( AccessibilityRole.TextInput, "Port" ) && screen.Has( AccessibilityRole.TextInput, "Relay" ),
			"Choosing Relay shows the port and relay entries." );
		CoreChecks.Check( screen.HasText( "Devices connect through a relay you run" ), "The chosen network mode is explained." );
		screen.Type( "Port", port.ToString( CultureInfo.InvariantCulture ) );
		screen.Type( "Relay", relayUrl );
		screen.ClickButton( "Apply", () => screen.HasText( "The daemon listens with the new settings." ), "Apply sends the network settings and says the daemon applied them." );
		CoreChecks.Check( gateway.Network is { Mode: "relay" } network && network.Port == port && network.RelayUrl == relayUrl, "The gateway runs the relay mode with the port and relay entered." );
		screen.PumpUntil( () => screen.HasText( "Relay: Connected to the relay at 127.0.0.1:" ), "The refreshed status shows the gateway connected to the relay." );

		screen.ClickButton( "Allow control right away: off",
			() => screen.HasButton( "Allow control right away: on" ) && screen.HasText( "Anyone who opens this link in the next 5 minutes can run programs on this PC as you." ),
			"Allow control right away warns what such a link allows." );
		screen.ClickButton( "Pair device", () => ReadLink( screen ) is not null && screen.HasText( "Pairs with control." ), "A link that grants control says so beside its code." );
		CoreChecks.Check( gateway.LiveOffer is { Control: true }, "The daemon's live offer grants control." );
		screen.ClickButton( "Done", () => screen.HasButton( "Pair device" ) && gateway.LiveOffer is null && screen.HasButton( "Allow control right away: off" ),
			"Done withdraws the control offer, and the switch is off again for the next link." );

		screen.ClickButton( "Pair device", () => ReadLink( screen ) is not null && screen.Find( IsQrCode ) is not null, "Pair device shows a QR code and a link." );
		var withdrawn = ReadLink( screen )!;
		CoreChecks.Check( screen.Find( IsQrCode ) is { Bounds: { Width: >= 100 } bounds } && Math.Abs( bounds.Width - bounds.Height ) < 1, "The QR code is square and large enough to scan." );
		CoreChecks.Check( PairingLink.TryParse( withdrawn ) is { Addrs.Count: 0 } link && link.HostId == gateway.Keys.HostId && link.Relay == relayUrl,
			"The pairing link names the host and the relay." );
		CoreChecks.Check( screen.HasText( "Expires in " ) && screen.HasText( "Web client: " + RelayDialer.BuildPageUrl( relayUrl, gateway.Keys.HostId ) + PairingLink.WebMarker )
			&& gateway.LiveOffer is not null, "The offer shows its countdown and the relay's web client address, and is live on the daemon." );
		screen.ClickButton( "Done", () => screen.Find( IsQrCode ) is null && screen.HasButton( "Pair device" ) && gateway.LiveOffer is null,
			"Done hides the code and withdraws the offer on the daemon." );

		// A link works only while it is shown: closing Remote access withdraws the one it shows.
		screen.ClickButton( "Pair device", () => ReadLink( screen ) is { } shown && shown != withdrawn && gateway.LiveOffer is not null, "Pair device shows a new link, live on the daemon." );
		app.Panels.Close( app.Panels.Panels.Single( panel => panel.TypeId == AccessContent.TypeId ).Id );
		screen.PumpUntil( () => gateway.LiveOffer is null && !IsAccessOpen(), "Closing Remote access withdraws the link it showed on the daemon." );
		app.Panels.Open( AccessContent.TypeId );
		screen.PumpUntil( () => screen.HasButton( "Pair device" ), "Remote access opens again in the daemon role." );
		screen.ClickButton( "Pair device", () => ReadLink( screen ) is { } next && next != withdrawn && gateway.LiveOffer is not null, "Pair device shows another new link." );
		var live = ReadLink( screen )!;
		CoreChecks.Check( app.LastError is null, $"The daemon role reports no failure ({app.LastError})." );
		return (withdrawn, live, screen);
	}

	/// <summary>Shows a new link in the daemon's window and closes the window, which shuts its Loom down: the daemon withdraws the link.</summary>
	private static void CloseShowingLink( Screen daemon, HostGateway gateway )
	{
		// The daemon's window lost the system focus to the paired PC's while it waited; its clicks still land.
		daemon.PumpUntil( () => daemon.HasButton( "Pair device" ), "The daemon's window shows Pair device again once a device used its link." );
		daemon.ClickButton( "Pair device", () => ReadLink( daemon ) is not null && gateway.LiveOffer is not null, "The daemon's window, back after the paired PC's, shows a new link." );
		daemon.Close();
	}

	/// <summary>Checks the Hosts section in the <c>local</c> role: Add host refuses a pasted text that is no link and a withdrawn link, pairs through the
	/// relay as view only and shows the host at once; the Window stream on that host is refused to a view-only device and works over the relay once
	/// control is granted; and Remove forgets the host.</summary>
	/// <returns>The paired device's id.</returns>
	private static string CheckRemoteRole( string dataFolder, List<Action> hostQueue, HostGateway gateway, string relayUrl, string withdrawn, string live )
	{
		var app = BuildApp();
		var module = new RemoteModule( new RemoteHooks { DataFolder = dataFolder, Role = RemoteModule.LocalRole } );
		var screen = new Screen( app, hostQueue, () => CoreChecks.Check( app.Load( module ) && module.Links.Count == 0, "The Remote module starts in the local role with no paired host." ) );
		var access = app.Panels.Open( AccessContent.TypeId, input: AccessContent.AddHostInput );
		screen.PumpUntil( () => screen.HasText( "Add host" ), "Remote access opens with its Hosts section." );
		CoreChecks.Check( screen.HasText( "No host is paired yet." ) && screen.Has( AccessibilityRole.TextInput, "Link" )
			&& screen.Find( node => node.Role == AccessibilityRole.Button && node.Name == "Add" ) is { Disabled: true },
			"Before pairing, Remote access shows no paired host and the Add host form with Add disabled until a link is pasted." );

		screen.Type( "Link", "not a pairing link" );
		screen.ClickButton( "Add", () => screen.HasText( "That is not a Loom pairing link." ), "Text that holds no pairing link is refused with the reason." );
		screen.Type( "Link", withdrawn );
		screen.ClickButton( "Add", () => screen.HasText( "The pairing link is wrong, used or expired." ), "The host refuses, through the relay, a link withdrawn with Done." );
		CoreChecks.Check( gateway.Devices.Devices.Count == 0 && PairedHosts.Read( dataFolder ).Count == 0 && module.Links.Count == 0, "A refused link pairs no device and adds no host." );
		screen.Type( "Link", live );
		screen.ClickButton( "Add", () => module.Links is [{ Status: ClientState.Connected }] && screen.HasText( "Paired with " ),
			"The live link, still shown by the daemon's window, pairs through the relay and the host connects at once." );
		var client = module.Links.Single();
		var device = gateway.Devices.Devices.Single();
		CoreChecks.Check( device.Name == Environment.MachineName && device.Scopes.SequenceEqual( DeviceScopes.Paired ) && !client.Scopes.Contains( DeviceScopes.Control )
			&& PairedHosts.Read( dataFolder ).Single().HostId == gateway.Keys.HostId && app.Hosts.Find( gateway.Keys.HostId ) is not null,
			"The paired PC is stored under its name with view only, and the host is saved and registered." );
		screen.PumpUntil( () => screen.HasText( "via relay" ) && screen.HasText( "View only until its user allows control" ) && screen.HasButton( "Remove\u2026" ),
			"Remote access shows the host's connection through the relay, that this PC may only view, and Remove." );
		CoreChecks.Check( screen.Find( node => node.Role == AccessibilityRole.Button && node.Name?.StartsWith( "Daemon: ", StringComparison.Ordinal ) == true ) is null,
			"The status bar shows no daemon entry for a paired host; the sidebar's host switcher lists it." );

		var stream = app.Panels.Open( $"remote.{StreamPanel.Id}", hostId: client.Host.Id );
		screen.PumpUntil( () => screen.HasText( "Could not list windows" ), "A view-only device's Window stream panel says it cannot list the host's windows." );
		CoreChecks.Check( !screen.HasButton( "Stream " + WindowTitle ), "A view-only device sees none of the host's windows." );

		// Loom on the host's PC grants control as its Allow control switch does; the device reconnects with it.
		var (adminHost, adminEnd) = MemoryConnection.CreatePair();
		_ = gateway.RunConnectionAsync( adminHost, HostGateway.LocalDevice, HostGateway.LocalName, DeviceScopes.Controlling );
		var admin = new RpcPeer( adminEnd );
		admin.Start();
		var grant = Frame.Create( "admin.scopes" );
		grant["deviceId"] = device.DeviceId;
		grant["control"] = true;
		var granted = admin.RequestAsync( grant, CancellationToken.None );
		screen.PumpUntil( () => granted.IsCompleted, "The daemon answers admin.scopes." );
		CoreChecks.Check( granted.IsCompletedSuccessfully, $"The daemon grants control ({granted.Exception?.GetBaseException().Message})." );
		admin.DisposeAsync().AsTask().GetAwaiter().GetResult();
		screen.PumpUntil( () => client.Status == ClientState.Connected && client.Scopes.Contains( DeviceScopes.Control ), "Granting control reconnects the device with it." );
		app.Panels.Activate( access );
		screen.PumpUntil( () => screen.HasText( "via relay" ) && screen.HasText( "View and control" ), "With control, Remote access says this PC may view and control the host." );

		app.Panels.Activate( stream );
		screen.ClickButton( "Refresh", () => screen.HasButton( "Stream " + WindowTitle ), "With control, the Window stream panel lists the host's windows over the relay.", retry: false );
		screen.ClickButton( "Stream " + WindowTitle, () => screen.HasText( " fps · " ), "A frame of the host's window arrives over the relay.", retry: false );
		CoreChecks.Check( !screen.HasText( " · view only" ), "A controlling device's stream is not view only." );
		screen.ClickButton( "Windows", () => screen.HasButton( "Stream " + WindowTitle ), "Windows ends the stream over the relay.", retry: false );
		CoreChecks.Check( app.LastError is null, $"The paired host reports no failure ({app.LastError})." );

		app.Panels.Activate( access );
		screen.ClickButton( "Remove\u2026", () => screen.HasText( "keeps this device until you revoke it there." ) && screen.HasButton( "Remove" ),
			"Remove asks first and says the host keeps this device." );
		screen.ClickButton( "Remove", () => screen.HasText( "No host is paired yet." ) && module.Links.Count == 0, "Remove forgets the host in this Loom." );
		CoreChecks.Check( PairedHosts.Read( dataFolder ).Count == 0 && app.Hosts.Find( gateway.Keys.HostId ) is null && app.Panels.Sessions.All( session => session.Info.Id != stream )
			&& gateway.Devices.Find( device.DeviceId ) is not null, "Removing the host drops its entry and its panels, and the host keeps the device." );
		screen.Close();
		return device.DeviceId;
	}

	/// <summary>Checks the devices list in the <c>daemon</c> role: the paired PC with control, the Allow control switch withdrawing it, and Revoke; then
	/// the pairing confirm dialog for devices that pair with view only, answered Allow control and View only.</summary>
	private static void CheckDevices( string dataFolder, List<Action> hostQueue, HostGateway gateway, string deviceId )
	{
		var (app, module, screen) = OpenDaemonClient( dataFolder, hostQueue, gateway );
		screen.PumpUntil( () => gateway.LiveOffer is null, "Closing Loom while it showed a link withdrew it on the daemon." );
		screen.PumpUntil( () => module.Daemon!.Status == ClientState.Connected, "The daemon-role client connects again." );
		app.Panels.Open( AccessContent.TypeId );
		screen.PumpUntil( () => screen.HasText( " · view and control" ), "The devices list shows the paired PC with view and control." );
		CoreChecks.Check( screen.HasText( AccessContent.ControlText ) && screen.HasButton( "Allow control: on" ) && screen.HasButton( "Revoke" ) && screen.HasText( Environment.MachineName ),
			"Each device shows its name, what control allows, its Allow control switch and Revoke." );
		screen.ClickButton( "Allow control: on", () => screen.HasButton( "Allow control: off" ) && screen.HasText( " · view only" ),
			"Allow control's switch withdraws control and the device shows view only." );
		CoreChecks.Check( gateway.Devices.Find( deviceId ) is { } device && device.Scopes.SequenceEqual( DeviceScopes.Paired ), "The switch withdraws control on the daemon." );
		screen.ClickButton( "Revoke", () => screen.HasText( "The device is revoked." ) && screen.HasText( "No device is paired." ), "Revoke forgets the device and the list empties." );
		CoreChecks.Check( gateway.Devices.Find( deviceId ) is null && app.LastError is null, $"The daemon forgets the revoked device and the daemon role reports no failure ({app.LastError})." );

		string PairDevice( string name )
		{
			using var keys = RemoteKeys.Create();
			var offer = gateway.CreateOffer( ["127.0.0.1:9"], null );
			var id = Guid.NewGuid().ToString( "N" );
			var (hostEnd, deviceEnd) = MemoryConnection.CreatePair();
			_ = SecureChannel.AcceptAsync( hostEnd, gateway.Keys, gateway.Devices, gateway.Offers, CancellationToken.None );
			var pairing = SecureChannel.PairAsync( deviceEnd, keys, id, name, offer.Link, CancellationToken.None );
			screen.PumpUntil( () => pairing.IsCompleted, $"{name} pairs." );
			CoreChecks.Check( pairing.IsCompletedSuccessfully, $"{name} pairs ({pairing.Exception?.GetBaseException().Message})." );
			return id;
		}

		string DescribePairing() =>
			$"waiting on the daemon: {string.Join( ", ", gateway.Pending.Select( pending => pending.Name ) )}; asked in this Loom: "
			+ string.Join( ", ", module.Prompts?.Pending.Select( pending => pending.Name ) ?? [] );

		var tablet = PairDevice( "Tablet" );
		screen.PumpUntil( () => screen.Has( AccessibilityRole.Dialog, "Allow Tablet to control this PC?" ) && screen.HasButton( "Allow control" ) && screen.HasButton( "View only" ),
			"A device that pairs with view only opens the confirm dialog with Allow control and View only.", DescribePairing );
		// The dialog is modal, so Remote access's list behind it is out of the accessibility tree until it closes; the list reads the same pending devices.
		CoreChecks.Check( module.Prompts?.Pending is [{ Name: "Tablet" }] && app.Notifications.Items.Any( item => item.Title == "Tablet paired with this PC" ),
			$"The device waits for an answer, and a notification asks too (notifications: {string.Join( " | ", app.Notifications.Items.Select( item => item.Title ) )})." );
		screen.ClickButton( "Allow control", () => !screen.Has( AccessibilityRole.Dialog, "Allow Tablet to control this PC?" ) && !screen.HasText( "Waiting for your answer" ),
			"Allow control closes the dialog and the device stops waiting.", detail: DescribePairing );
		CoreChecks.Check( gateway.Devices.Find( tablet )?.Scopes.Contains( DeviceScopes.Control ) == true && gateway.Pending.Count == 0, "Allow control grants the device control on the daemon." );
		var watch = PairDevice( "Watch" );
		screen.PumpUntil( () => screen.Has( AccessibilityRole.Dialog, "Allow Watch to control this PC?" ), "The next view-only pairing asks again.", DescribePairing );
		screen.ClickButton( "View only", () => !screen.Has( AccessibilityRole.Dialog, "Allow Watch to control this PC?" ) && gateway.Pending.Count == 0,
			"View only closes the dialog and the device stops waiting.", detail: DescribePairing );
		CoreChecks.Check( gateway.Devices.Find( watch )?.Scopes.SequenceEqual( DeviceScopes.Paired ) == true && app.LastError is null,
			$"View only keeps the device view-only, and the daemon role reports no failure ({app.LastError})." );
		screen.Close();
	}

	/// <summary>Opens a window over a <c>daemon</c>-role application whose dialer hands the gateway the other end of an in-memory pair as Loom on this PC.</summary>
	private static (LoknApplication App, RemoteModule Module, Screen Screen) OpenDaemonClient( string dataFolder, List<Action> hostQueue, HostGateway gateway )
	{
		var app = BuildApp();
		var module = new RemoteModule( new RemoteHooks
		{
			DataFolder = dataFolder,
			Role = RemoteModule.DaemonRole,
			Dial = ( hostId, cancellationToken ) =>
			{
				var (hostEnd, clientEnd) = MemoryConnection.CreatePair();
				_ = gateway.RunConnectionAsync( hostEnd, HostGateway.LocalDevice, HostGateway.LocalName, DeviceScopes.Controlling );
				return Task.FromResult( new ClientCarrier( clientEnd, DeviceScopes.Controlling, "pipe" ) );
			},
		} );
		// The daemon's window stays open beside the paired PC's, whose Window stream lists both; its own title keeps the paired PC's window the one to stream.
		var screen = new Screen( app, hostQueue, () =>
		{
			CoreChecks.Check( app.Load( module ) && module.Daemon is not null, "The Remote module links with the daemon in the daemon role." );
			app.Settings.Set( RemoteModule.Role, SettingsScope.Global, RemoteModule.DaemonRole );
		}, WindowTitle + " (daemon)" );
		return (app, module, screen);
	}

	/// <summary>Creates an application with settings and stores in memory: the shell, Omp with the fixture, Projects and Conversations.</summary>
	private static LoknApplication BuildApp()
	{
		var app = new LoknApplication();
		CoreChecks.Check( app.Load( new ShellModule() ), "The shell loads." );
		LoadOmp( app );
		var store = new ConversationStore( new ProjectStore() );
		CoreChecks.Check( ProjectChecks.LoadProjects( app, store ) && app.Load( new ConversationsModule( store, ConversationsModule.BuildChat ) ), "Projects and Conversations load." );
		return app;
	}

	/// <summary>Loads Omp with the fixture (this executable) as its executable, so no real agent starts.</summary>
	private static void LoadOmp( LoknApplication app )
	{
		CoreChecks.Check( app.Load( new OmpModule() ), "Omp loads." );
		app.Settings.Set( OmpModule.Executable, SettingsScope.Global, Path.Combine( AppContext.BaseDirectory, "Lokn.Tests.exe" ) );
	}

	/// <summary>Determines whether a semantic node is the pairing QR code.</summary>
	private static bool IsQrCode( AccessibilityNode node ) => node.Name == "Pairing QR code";

	/// <summary>Reads the native pairing link Remote access shows, or null when none shows.</summary>
	private static string? ReadLink( Screen screen ) => screen.Nodes
		.SelectMany( node => (string?[])[node.Name, node.Value] )
		.FirstOrDefault( text => text?.StartsWith( PairingLink.UriPrefix, StringComparison.Ordinal ) == true );

	/// <summary>Finds a loopback port no listener uses.</summary>
	private static int FindFreePort()
	{
		var listener = new TcpListener( IPAddress.Loopback, 0 );
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		return port;
	}

	/// <summary>Enumerates a semantic subtree.</summary>
	private static IEnumerable<AccessibilityNode> Flatten( AccessibilityNode node )
	{
		yield return node;
		foreach ( var child in node.Children )
		{
			foreach ( var descendant in Flatten( child ) )
				yield return descendant;
		}
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

	/// <summary>One native window over an application, which also runs the host's queued work between frames.</summary>
	private sealed class Screen
	{
		/// <summary>Defines how long a click may take to show its result before it counts as not pressed and is pressed again.</summary>
		private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds( 2 );

		/// <summary>Defines how many times a click that showed no result is pressed.</summary>
		private const int MaxPresses = 3;

		/// <summary>Stores the host application's queued work, run on this thread as its UI thread.</summary>
		private readonly List<Action> _host;

		/// <summary>Stores the window's accessibility tree.</summary>
		private readonly SemanticAdapter _semantics = new();

		/// <summary>Creates the window over the application and binds the application's dispatcher to it, runs <paramref name="load"/> so modules it
		/// starts post to the window, then opens the window.</summary>
		/// <param name="app">The application the window shows.</param>
		/// <param name="host">The host application's queue, or an empty one.</param>
		/// <param name="load">Loads the modules that need the dispatcher at startup.</param>
		/// <param name="title">The window's title.</param>
		public Screen( LoknApplication app, List<Action> host, Action load, string title = WindowTitle )
		{
			_host = host;
			Window = new Window
			{
				Title = title,
				Size = new( 1200, 800 ),
				Decorated = false,
				Transparent = true,
				Background = Theme.Current.Canvas,
				Foreground = Theme.Current.Text,
				Root = new ShellView( app ) { AppliesNativeWindowHooks = false },
				AccessibilityAdapter = _semantics,
			};
			app.BindDispatcher( Window.Post );
			app.Commands.BindDispatcher( operation => Window.InvokeAsync( operation ) );
			load();
			Window.Open();
			Pump();
		}

		/// <summary>Gets the native window.</summary>
		public Window Window
		{
			get;
		}

		/// <summary>Gets the semantic nodes in source order.</summary>
		public IEnumerable<AccessibilityNode> Nodes => Flatten( _semantics.Tree!.Root! );

		/// <summary>Finds the first node that matches, or null.</summary>
		public AccessibilityNode? Find( Func<AccessibilityNode, bool> match ) => Nodes.FirstOrDefault( match );

		/// <summary>Determines whether a node has the role and exact name.</summary>
		public bool Has( AccessibilityRole role, string name ) => Nodes.Any( node => node.Role == role && node.Name == name );

		/// <summary>Determines whether a button has the exact name.</summary>
		public bool HasButton( string name ) => Has( AccessibilityRole.Button, name );

		/// <summary>Determines whether a node's name or value contains a fragment.</summary>
		public bool HasText( string fragment ) =>
			Nodes.Any( node => (node.Name?.Contains( fragment, StringComparison.Ordinal ) ?? false) || (node.Value?.Contains( fragment, StringComparison.Ordinal ) ?? false) );

		/// <summary>Clicks the one button with the exact name and waits until the state the click changes holds.</summary>
		/// <param name="name">The button's name.</param>
		/// <param name="done">Whether the click took effect.</param>
		/// <param name="what">What the click does, for failures.</param>
		/// <param name="retry">Whether a press that showed nothing is made again; false for a button whose effect takes long and would start twice.</param>
		/// <param name="detail">Describes the state behind a failure, or null.</param>
		public void ClickButton( string name, Func<bool> done, string what, bool retry = true, Func<string>? detail = null ) =>
			ClickUntil( node => node.Role == AccessibilityRole.Button && node.Name == name, name, done, what, retry, detail );

		/// <summary>Clicks the one matching node (<see cref="UiInput.Click"/>) and waits until the state the click changes holds. A press that did not take,
		/// as the node still shows unchanged and enabled after <see cref="RetryAfter"/>, is made again; a node the click changed, hid or disabled, such as a
		/// switch whose name says its new state or a button busy with its request, is never pressed twice.</summary>
		/// <param name="match">Selects the node.</param>
		/// <param name="target">Names the node in failures.</param>
		/// <param name="done">Whether the click took effect.</param>
		/// <param name="what">What the click does, for failures.</param>
		/// <param name="retry">Whether a press that showed nothing is made again.</param>
		/// <param name="detail">Describes the state behind a failure, or null.</param>
		public void ClickUntil( Func<AccessibilityNode, bool> match, string target, Func<bool> done, string what, bool retry = true, Func<string>? detail = null )
		{
			for ( var press = 1; ; press++ )
			{
				UiInput.Click( Window, () => Nodes.SingleOrDefault( match ), Pump, target );
				var clock = Stopwatch.StartNew();
				while ( retry && !done() && clock.Elapsed < RetryAfter )
					Pump();
				if ( !retry || done() || press == MaxPresses || Nodes.Count( match ) != 1 || Nodes.First( match ).Disabled )
					break;
			}
			PumpUntil( done, what, detail );
		}

		/// <summary>Replaces the text of the one text entry with the exact name through native keyboard input, focusing it first (<see cref="UiInput.Type"/>).</summary>
		public void Type( string name, string text ) =>
			UiInput.Type( Window, () => Nodes.SingleOrDefault( node => node.Role == AccessibilityRole.TextInput && node.Name == name ), Pump, name + " entry", text );

		/// <summary>Pumps until a condition holds, failing with what was expected, the state behind it and what shows after <see cref="StepTimeout"/>.</summary>
		public void PumpUntil( Func<bool> condition, string what, Func<string>? detail = null )
		{
			var clock = Stopwatch.StartNew();
			while ( !condition() )
			{
				if ( clock.Elapsed > StepTimeout )
				{
					var shown = string.Join( " | ", Nodes.Where( node => node.Name is { Length: > 0 } ).Select( node => node.Disabled ? node.Name + " (disabled)" : node.Name ).Distinct() );
					CoreChecks.Check( false, $"{what} (timed out; {detail?.Invoke() ?? "no detail"}; showing: {shown[..Math.Min( shown.Length, 3000 )]})" );
				}
				Pump();
			}
			Pump();
		}

		/// <summary>Settles layout and accessibility delivery, running the host's work between frames.</summary>
		public void Pump()
		{
			for ( var index = 0; index < SettleFrames; index++ )
			{
				Action[] actions;
				lock ( _host )
				{
					actions = [.. _host];
					_host.Clear();
				}
				foreach ( var action in actions )
					action();
				Window.Pump( 0.016 );
				Thread.Sleep( 10 );
			}
		}

		/// <summary>Closes the window, which shuts its application down.</summary>
		public void Close()
		{
			Window.RequestClose();
			Pump();
		}
	}
}
