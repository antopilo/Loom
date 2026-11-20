using System.Text.Json.Nodes;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>The one-button part of the Remote access panel: Connect a device moves This PC's chats to the daemon, starts it at sign-in, opens the
/// network (Tailscale Funnel when Tailscale runs, so a phone needs nothing installed; else LAN, for Loom PCs) and shows a pairing code that grants
/// control; while phones cannot reach this PC, a checklist of what is left; then the paired devices and the Advanced toggle.</summary>
public sealed partial class AccessContent
{
	/// <summary>Defines Tailscale's download page.</summary>
	private const string TailscaleDownload = "https://tailscale.com/download";

	/// <summary>Defines the tailnet's DNS settings, where HTTPS certificates are turned on.</summary>
	private const string TailscaleDnsSettings = "https://login.tailscale.com/admin/dns";

	/// <summary>Stores Tailscale's state as Connect a device last read it, or null before the first; shown as the phone checklist.</summary>
	private TailscaleState? _tailscale;

	/// <summary>Stores why Funnel did not start at the last connect, or null.</summary>
	private string? _funnelError;

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
			foreach ( var blob in BuildPhoneChecklist( theme ) )
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
		if ( _module.RunningRole != RemoteModule.DaemonRole || _status is not { } status || (Frame.ReadString( status, "network" ) ?? "off") == "off" )
			yield break;

		var network = Frame.ReadString( status, "network" )!;
		var label = NetworkChoices.Where( choice => choice.Mode == network ).Select( choice => choice.Label ).FirstOrDefault() ?? network;
		yield return BuildParagraph( $"Devices reach this PC over {label}.", theme.Muted );
		foreach ( var blob in BuildPhoneChecklist( theme ) )
			yield return blob;
	}

	/// <summary>Builds the shown pairing offer: the QR code of its web address, which a phone's camera opens, or without one only the link a Loom PC
	/// opens; the countdown, and Done.</summary>
	private IEnumerable<Blob> BuildOffer( RemoteClient client, Pairing pairing, Theme theme )
	{
		var left = pairing.Expires - DateTimeOffset.UtcNow;
		var expires = $"It works once and expires in {(int)left.TotalMinutes}:{left.Seconds:00}.";
		if ( pairing.WebUrl is { } webUrl )
		{
			yield return BuildQr( pairing.Modules );
			yield return BuildParagraph( "Scan the code with the phone's camera, or open the link in Loom on the other PC. " + expires, theme.Text );
			yield return BuildParagraph( "Web client: " + webUrl, theme.Muted, selectable: true );
		}
		else
			yield return BuildParagraph( "Open this link in Loom on the other PC, or paste it in its Remote access. " + expires, theme.Text );
		if ( pairing.Control )
			yield return BuildParagraph( "The device gets control of this PC, so show the code to that device only.", theme.Attention );
		yield return BuildParagraph( pairing.Link, theme.Muted, selectable: true );
		yield return theme.BuildButton( "Done", () => _context.Run( () =>
		{
			_pairing = null;
			_version.Value++;
			_ = WithdrawPairingAsync( client );
		} ) );
	}

	/// <summary>Builds what is left before a phone can connect, while the daemon has no web address: install Tailscale, sign in, or what Funnel needs,
	/// each with the page that does it, and Check again.</summary>
	private IEnumerable<Blob> BuildPhoneChecklist( Theme theme )
	{
		if ( _status is not { } status || Frame.ReadString( status, "webUrl" ) is not null || _tailscale is not { } tailscale )
			yield break;

		yield return BuildSection( "To connect a phone", theme );
		yield return BuildParagraph( "Phones open Loom in the browser, which needs Tailscale on this PC. Nothing to install on the phone.", theme.Muted );
		var (text, label, url) = DescribeStep( tailscale, _funnelError );
		yield return BuildParagraph( text, theme.Attention, selectable: true );
		var buttons = new List<Blob>();
		if ( url is not null )
			buttons.Add( theme.BuildButton( label, () => _context.Run( () => _context.App.OpenLink( new Uri( url ) ) ) ) );
		buttons.Add( theme.BuildButton( "Check again", StartConnect, isEnabled: !_isBusy ) );
		yield return new Row { Gap = theme.Spacing, Children = buttons };
	}

	/// <summary>Describes the next step before Funnel can serve this PC, with the button label and page that does it, or no page for a step done on
	/// this PC itself.</summary>
	private static (string Text, string Label, string? Url) DescribeStep( TailscaleState tailscale, string? funnelError )
	{
		if ( !tailscale.IsInstalled )
			return ("Install Tailscale on this PC and sign in.", "Install Tailscale", TailscaleDownload);
		if ( tailscale.Status is not { IsRunning: true } )
			return (tailscale.Status is { } stopped ? TailscaleCli.DescribeProblem( stopped ) ?? "" : tailscale.Problem ?? "Tailscale is not running.", "Sign in", tailscale.Status?.AuthUrl);
		if ( funnelError is null )
			return ("Choose Check again to open this PC to phones.", "", null);
		// Tailscale names the page that allows Funnel for this PC in its refusal; HTTPS certificates are turned on in the DNS settings.
		var link = System.Text.RegularExpressions.Regex.Match( funnelError, @"https://\S+" );
		if ( link.Success )
			return (funnelError, "Allow Funnel", link.Value.TrimEnd( '.', ',', ')' ));
		return (funnelError + " Turn on HTTPS certificates for the tailnet, then check again.", "Open DNS settings", TailscaleDnsSettings);
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

	/// <summary>Opens the network for phones when the daemon has no web address: Tailscale Funnel when Tailscale runs, keeping the LAN for Loom PCs when
	/// it refuses, else the LAN when the network is off; then shows a pairing code that grants control.</summary>
	private async Task ConnectAsync( RemoteClient client )
	{
		var status = await client.RequestAsync( Frame.Create( "admin.status" ), _context.Lifetime ).ConfigureAwait( false );
		var tailscale = await ReadTailscaleAsync().ConfigureAwait( false );
		string? funnelError = null;
		if ( Frame.ReadString( status, "webUrl" ) is null )
		{
			var network = Frame.ReadString( status, "network" ) ?? "off";
			var port = (int)(Frame.ReadLong( status, "port" ) ?? RemoteModule.Port.DefaultValue);
			var relay = Frame.ReadString( status, "relayUrl" ) ?? "";
			if ( tailscale.Status is { IsRunning: true } )
			{
				status = await RequestNetworkAsync( client, "funnel", port, relay ).ConfigureAwait( false );
				funnelError = Frame.ReadString( status, "error" );
				if ( funnelError is not null )
					status = await RequestNetworkAsync( client, network is "off" or "funnel" ? "lan" : network, port, relay ).ConfigureAwait( false );
			}
			else if ( network == "off" )
				status = await RequestNetworkAsync( client, "lan", port, relay ).ConfigureAwait( false );
		}

		var opened = status;
		_context.Post( () =>
		{
			_status = opened;
			_tailscale = tailscale;
			_funnelError = funnelError;
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

	/// <summary>Reads whether Tailscale is installed on this PC and its state, or why it could not be read.</summary>
	private async Task<TailscaleState> ReadTailscaleAsync()
	{
		if ( TailscaleCli.FindExecutable() is null )
			return new( false, null, null );

		try
		{
			return new( true, await TailscaleCli.GetStatusAsync( _context.Lifetime ).ConfigureAwait( false ), null );
		}
		catch ( TailscaleException exception )
		{
			return new( true, null, exception.Message );
		}
	}

	/// <summary>Tailscale on this PC as Connect a device found it.</summary>
	/// <param name="IsInstalled">Whether <c>tailscale.exe</c> was found.</param>
	/// <param name="Status">Its state, or null when it could not be read.</param>
	/// <param name="Problem">Why the state could not be read, or null.</param>
	private sealed record TailscaleState( bool IsInstalled, TailscaleStatus? Status, string? Problem );
}
