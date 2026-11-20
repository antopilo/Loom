using System.Text.Json.Nodes;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>The one-button part of the Remote access panel: Connect a device moves This PC's chats to the daemon, starts it at sign-in, opens the
/// network (Tailscale when it runs, else LAN) and shows a pairing code that grants control; then the paired devices and the Advanced toggle.</summary>
public sealed partial class AccessContent
{
	/// <summary>Defines the hint shown while the daemon listens on the LAN only.</summary>
	private const string TailscaleHint = "Only devices on this network can connect. Install Tailscale (tailscale.com/download) on this PC and the device to connect from anywhere.";

	/// <summary>Stores whether Connect a device runs: it waits for the chats to move and the daemon to connect, then opens the network and pairs.</summary>
	private bool _isConnecting;

	/// <summary>Stores whether the Advanced settings show.</summary>
	private bool _isAdvanced;

	/// <summary>Builds the Connect a device part: the pairing code while one is shown, the progress of a connect under way with Cancel, or the button
	/// with how devices reach this PC.</summary>
	private IEnumerable<Blob> BuildConnect( Theme theme )
	{
		if ( _module.Daemon is { } shown && _pairing is { } pairing )
		{
			foreach ( var blob in BuildOffer( shown, pairing, theme ) )
				yield return blob;
			yield break;
		}

		if ( _isConnecting || _module.Switching is not null )
		{
			yield return BuildParagraph( DescribeProgress(), theme.Attention );
			if ( _module.Switching is { IsWaiting: false } )
				yield break;

			var cancel = theme.BuildButton( "Cancel", () => _context.Run( () =>
			{
				_isConnecting = false;
				_module.CancelSwitch();
				_version.Value++;
			} ) );
			cancel.AccessibilityName = "Cancel connecting a device";
			yield return cancel;
			yield break;
		}

		yield return BuildParagraph( "Use this PC's chats from your phone or another PC. Scan one code; Loom does the rest.", theme.Muted );
		yield return theme.BuildButton( _isBusy ? "Connecting\u2026" : "Connect a device", StartConnect, isEnabled: !_isBusy );
		if ( _module.RunningRole != RemoteModule.DaemonRole || _status is not { } status )
			yield break;

		var network = Frame.ReadString( status, "network" ) ?? "off";
		if ( network == "off" )
			yield break;

		var label = NetworkChoices.Where( choice => choice.Mode == network ).Select( choice => choice.Label ).FirstOrDefault() ?? network;
		yield return BuildParagraph( $"Devices reach this PC over {label}.", theme.Muted );
		if ( network == "lan" )
			yield return BuildParagraph( TailscaleHint, theme.Muted, selectable: true );
		if ( Frame.ReadString( status, "error" ) is { } error )
			yield return BuildParagraph( error, theme.Error, selectable: true );
	}

	/// <summary>Builds the shown pairing offer: its QR code, countdown, link and web address, and Done.</summary>
	private IEnumerable<Blob> BuildOffer( RemoteClient client, Pairing pairing, Theme theme )
	{
		var left = pairing.Expires - DateTimeOffset.UtcNow;
		yield return BuildQr( pairing.Modules );
		yield return BuildParagraph( "Scan the code with the phone's camera, or paste the link in Loom on the other PC. "
			+ $"It works once and expires in {(int)left.TotalMinutes}:{left.Seconds:00}.", theme.Text );
		if ( pairing.Control )
			yield return BuildParagraph( "The device gets control of this PC, so show the code to that device only.", theme.Attention );
		yield return BuildParagraph( pairing.Link, theme.Muted, selectable: true );
		if ( pairing.WebUrl is { } webUrl )
			yield return BuildParagraph( "Web client: " + webUrl, theme.Muted, selectable: true );
		yield return theme.BuildButton( "Done", () => _context.Run( () =>
		{
			_pairing = null;
			_version.Value++;
			_ = WithdrawPairingAsync( client );
		} ) );
	}

	/// <summary>Builds the devices paired with this PC's daemon: a dot for whether each is connected, its name, Allow control for one with view only,
	/// and Remove.</summary>
	private IEnumerable<Blob> BuildDevices( Theme theme )
	{
		if ( _module.Daemon is not { } client || _devices is not { Count: > 0 } devices )
			yield break;

		yield return BuildSection( "Your devices", theme );
		foreach ( var device in devices.OfType<JsonObject>() )
		{
			var deviceId = Frame.ReadString( device, "deviceId" ) ?? "";
			var name = Frame.ReadString( device, "name" ) ?? deviceId;
			var canControl = Frame.ReadStrings( device, "scopes" )?.Contains( DeviceScopes.Control ) == true;
			var isConnected = Frame.ReadBool( device, "connected" ) == true;
			var children = new List<Blob>
			{
				BuildDot( isConnected ? theme.Success : theme.Muted ),
				new Text { Content = name, FlexGrow = 1, FlexShrink = 1, TextWrap = TextWrap.Wrap, },
			};
			if ( !canControl )
			{
				children.Add( new Text { Content = "View only", Color = theme.Muted, } );
				var allow = theme.BuildButton( "Allow control", () => RunRequest( () => UpdateDevicesAsync( client, "admin.scopes", deviceId, true ) ), isEnabled: !_isBusy );
				allow.AccessibilityName = $"Allow {name} to control this PC";
				children.Add( allow );
			}
			var remove = theme.BuildButton( "Remove", () => RunRequest( () => UpdateDevicesAsync( client, "admin.revoke", deviceId, null ), $"{name} is removed." ),
				isEnabled: !_isBusy );
			remove.AccessibilityName = $"Remove {name}";
			children.Add( remove );
			yield return new Row
			{
				Key = "device:" + deviceId,
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing,
				Children = children,
			};
		}
	}

	/// <summary>Builds the Advanced toggle and, while open, where This PC's chats run, the daemon's controls, network and view-only pairing, and the
	/// paired hosts' names and browser lending.</summary>
	private IEnumerable<Blob> BuildAdvanced( Theme theme )
	{
		var isOpen = _isAdvanced;
		var toggle = theme.BuildButton( isOpen ? "\u25BE Advanced" : "\u25B8 Advanced", () => _context.Run( () =>
		{
			_isAdvanced = !isOpen;
			_version.Value++;
		} ) );
		toggle.AccessibilityName = isOpen ? "Hide advanced settings" : "Show advanced settings";
		yield return toggle;
		if ( !isOpen )
			yield break;

		foreach ( var blob in BuildRoles( theme ) )
			yield return blob;
		if ( _module.RunningRole == RemoteModule.DaemonRole )
		{
			foreach ( var blob in BuildDaemon( theme ) )
				yield return blob;
		}
		foreach ( var blob in BuildHostSettings( theme ) )
			yield return blob;
	}

	/// <summary>Describes a connect under way: the agents the move of the chats waits for, the move itself, or the daemon starting.</summary>
	private string DescribeProgress()
	{
		if ( _module.Switching is { } pending )
		{
			var waiting = _module.CountWaiting();
			if ( pending.IsWaiting && waiting > 0 )
				return $"Waiting for {waiting} {(waiting == 1 ? "agent" : "agents")} to finish, then the chats move to the background daemon. Chat panels stay open.";
			return "Moving this PC's chats to the background daemon\u2026";
		}

		return _module.Daemon is { } daemon ? "Starting the daemon\u2026 " + daemon.StatusText : "Starting the daemon\u2026";
	}

	/// <summary>Starts Connect a device: the daemon starts at sign-in from now on, This PC's chats move to it unless they run there, and a stopped daemon
	/// starts; <see cref="UpdateConnect"/> continues once it is connected.</summary>
	private void StartConnect() => _context.Run( () =>
	{
		if ( _isBusy || _isConnecting )
			return;

		_isConnecting = true;
		_message = "";
		_context.App.Settings.Set( RemoteModule.LoginItem, SettingsScope.Global, true );
		if ( _module.RunningRole != RemoteModule.DaemonRole )
			_ = _module.SwitchRole( RemoteModule.DaemonRole );
		else if ( _module.Daemon is { Status: ClientState.Stopped } stopped )
			stopped.Reconnect();
		_version.Value++;
		UpdateConnect();
	} );

	/// <summary>Continues a connect under way once the daemon is connected: opens the network and shows a pairing code. A move of the chats that was
	/// cancelled or failed ends the connect; the module's notice says why.</summary>
	private void UpdateConnect()
	{
		if ( !_isConnecting || _isBusy )
			return;

		if ( _module.RunningRole != RemoteModule.DaemonRole )
		{
			if ( _module.Switching is null )
			{
				_isConnecting = false;
				_version.Value++;
			}
			return;
		}

		if ( _module.Daemon is not { Status: ClientState.Connected } client )
			return;

		_isConnecting = false;
		RunRequest( () => ConnectAsync( client ) );
	}

	/// <summary>Opens the network when it is off, Tailscale when it runs on this PC, else the LAN, falling back to the LAN when Tailscale refuses; then
	/// shows a pairing code that grants control.</summary>
	private async Task ConnectAsync( RemoteClient client )
	{
		var status = await client.RequestAsync( Frame.Create( "admin.status" ), _context.Lifetime ).ConfigureAwait( false );
		if ( (Frame.ReadString( status, "network" ) ?? "off") == "off" )
		{
			var port = (int)(Frame.ReadLong( status, "port" ) ?? RemoteModule.Port.DefaultValue);
			var relay = Frame.ReadString( status, "relayUrl" ) ?? "";
			var isTailscale = await IsTailscaleRunningAsync().ConfigureAwait( false );
			status = await RequestNetworkAsync( client, isTailscale ? "tailscale" : "lan", port, relay ).ConfigureAwait( false );
			if ( isTailscale && Frame.ReadString( status, "error" ) is not null )
				status = await RequestNetworkAsync( client, "lan", port, relay ).ConfigureAwait( false );
		}

		var opened = status;
		_context.Post( () =>
		{
			_status = opened;
			_version.Value++;
		} );
		await UpdatePairAsync( client, control: true ).ConfigureAwait( false );
	}

	/// <summary>Sends <c>admin.network</c> and answers the daemon's new status.</summary>
	private Task<JsonObject> RequestNetworkAsync( RemoteClient client, string network, int port, string relay )
	{
		var frame = Frame.Create( "admin.network" );
		frame["network"] = network;
		frame["port"] = port;
		frame["relayUrl"] = relay;
		return client.RequestAsync( frame, _context.Lifetime );
	}

	/// <summary>Reports whether Tailscale is installed, running and signed in on this PC.</summary>
	private async Task<bool> IsTailscaleRunningAsync()
	{
		if ( TailscaleCli.FindExecutable() is null )
			return false;

		try
		{
			return (await TailscaleCli.GetStatusAsync( _context.Lifetime ).ConfigureAwait( false )).IsRunning;
		}
		catch ( TailscaleException )
		{
			return false;
		}
	}
}
