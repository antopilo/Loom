using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>The This PC section's daemon part of the Remote access panel: status, network, pairing, the pairings waiting for an answer, and devices.</summary>
public sealed partial class AccessContent
{
	/// <summary>Defines the drawn size of one QR module in logical pixels.</summary>
	private const int QrModule = 4;

	/// <summary>Defines the light margin around a QR code, in modules, which scanners need.</summary>
	private const int QrQuietZone = 4;

	/// <summary>Defines how often the daemon's status refreshes, in ticks.</summary>
	private const int StatusTicks = 5;

	/// <summary>Defines what the <c>view</c> scope allows, as the pairing and devices sections say it (docs/remote.md, Scopes).</summary>
	internal const string ViewText = "View: read chats and the store. No files, terminals, processes or window streaming.";

	/// <summary>Defines what the <c>control</c> scope allows, as the pairing and devices sections say it (docs/remote.md, Scopes).</summary>
	internal const string ControlText = "Control: also send prompts, change the store, and use this PC's files, terminals, processes and windows (stream and input) as you. "
		+ "Loom's own keys and device list stay off-limits, but a controlling device can run any program, so only allow devices you trust like this PC.";

	/// <summary>Lists the network modes with their labels, explanations and whether they are still a preview.</summary>
	private static readonly (string Mode, string Label, string Text, bool IsPreview)[] NetworkChoices =
	[
		("off", "Off", "Only Loom on this PC reaches the daemon, over a private pipe. Devices already connected are disconnected.", false),
		("lan", "LAN", "Devices on this network connect to this PC's addresses directly. Phones need Tailscale, the relay or Funnel for the web client.", true),
		("tailscale", "Tailscale", "Devices on your tailnet connect; phones open the web client over Tailscale's HTTPS.", true),
		("relay", "Relay", "Devices connect through a relay you run, which only passes encrypted bytes. The relay serves the web client's code, so use one you trust.", true),
		("funnel", "Funnel", "Devices anywhere connect over Tailscale Funnel's public HTTPS address.", true),
	];

	/// <summary>Holds the QR's dark and light colors: scanners need dark modules on a light ground whatever the theme.</summary>
	private static readonly (Color Dark, Color Light) QrColors = (Color.Parse( "#000000" ), Color.Parse( "#ffffff" ));

	/// <summary>Stores the daemon's last <c>admin.status</c> answer, or null before the first.</summary>
	private JsonObject? _status;

	/// <summary>Stores the daemon's last device list, or null before the first.</summary>
	private JsonArray? _devices;

	/// <summary>Stores the network mode being edited, or null to show the daemon's.</summary>
	private string? _networkDraft;

	/// <summary>Stores the port being edited, or null to show the daemon's.</summary>
	private string? _portDraft;

	/// <summary>Stores the relay address being edited, or null to show the daemon's.</summary>
	private string? _relayDraft;

	/// <summary>Stores the pairing offer shown, or null.</summary>
	private Pairing? _pairing;

	/// <summary>Stores whether the next pairing link grants control right away.</summary>
	private bool _isPairControl;

	/// <summary>Stores whether a status refresh runs.</summary>
	private bool _isRefreshing;

	/// <summary>Stores whether the panel asks to confirm stopping the daemon.</summary>
	private bool _isConfirmingStop;

	/// <summary>Counts ticks, so the status refreshes every few.</summary>
	private int _ticks;

	/// <summary>Builds the daemon section: its status, start and stop, a restart when it runs another build, sign-in, network, pairing, the pairings
	/// waiting for an answer, and devices; only its status and log while This PC's chats move.</summary>
	private IEnumerable<Blob> BuildDaemon( Theme theme )
	{
		if ( _module.Daemon is not { } client )
			yield break;

		yield return BuildSection( "Daemon", theme );
		yield return BuildStatusRow( client, theme );
		yield return BuildParagraph( "Its log, with every error it reports: " + RemoteClient.DaemonLogPath, theme.Muted, selectable: true );
		// The chats are moving: starting or stopping the daemon now would fight the switch.
		if ( _module.Switching is { IsWaiting: false } )
			yield break;

		if ( client.Status == ClientState.Stopped )
			yield return theme.BuildButton( "Start daemon", () => _context.Run( client.Reconnect ) );
		else if ( _isConfirmingStop )
		{
			yield return BuildParagraph( "Stopping the daemon stops its running agent turns and disconnects every device until it starts again.", theme.Attention );
			yield return new Row
			{
				Gap = theme.Spacing,
				Children =
				[
					theme.BuildButton( "Stop daemon", () => _context.Run( () =>
					{
						_isConfirmingStop = false;
						client.StopDaemon();
					} ) ),
					theme.BuildButton( "Cancel", () => _context.Run( () =>
					{
						_isConfirmingStop = false;
						_version.Value++;
					} ) ),
				],
			};
		}
		else
		{
			yield return theme.BuildButton( "Stop daemon\u2026", () => _context.Run( () =>
			{
				_isConfirmingStop = true;
				_version.Value++;
			} ) );
		}

		if ( client.IsOutdated )
		{
			yield return BuildParagraph( $"The daemon runs another build of Loom ({client.HostVersion ?? "an older one"}; this window runs {RemoteProtocol.AppVersion}). "
				+ "Restart it to update; that stops its running agent turns.", theme.Attention );
			yield return theme.BuildButton( "Restart daemon", () => _context.Run( client.RestartDaemon ) );
		}

		var isLoginItem = _context.App.Settings.Get( RemoteModule.LoginItem, SettingsScope.Global );
		yield return BuildSwitch( "Start the daemon at sign-in", isLoginItem, true,
			() => _context.App.Settings.Set( RemoteModule.LoginItem, SettingsScope.Global, !isLoginItem ) );
		var isTrayShown = _context.App.Settings.Get( RemoteModule.Tray, SettingsScope.Global );
		yield return BuildSwitch( "Show in the notification area", isTrayShown, true,
			() => _context.App.Settings.Set( RemoteModule.Tray, SettingsScope.Global, !isTrayShown ) );

		if ( client.Status != ClientState.Connected || _status is not { } status )
			yield break;

		yield return BuildParagraph( $"{Frame.ReadString( status, "hostName" )} · host id {Frame.ReadString( status, "hostId" )} · process {Frame.ReadLong( status, "pid" )}",
			theme.Muted, selectable: true );
		foreach ( var blob in BuildNetwork( client, status, theme ) )
			yield return blob;
		foreach ( var blob in BuildPairing( client, theme ) )
			yield return blob;
		foreach ( var blob in BuildPending( theme ) )
			yield return blob;
		foreach ( var blob in BuildDevices( client, theme ) )
			yield return blob;
	}

	/// <summary>Builds the network editor: mode buttons with the chosen mode's explanation, the port, the relay address, Apply, and what the mode reaches.</summary>
	private IEnumerable<Blob> BuildNetwork( RemoteClient client, JsonObject status, Theme theme )
	{
		var network = _networkDraft ?? Frame.ReadString( status, "network" ) ?? "off";
		var port = _portDraft ?? (Frame.ReadLong( status, "port" ) ?? RemoteModule.Port.DefaultValue).ToString( CultureInfo.InvariantCulture );
		var relay = _relayDraft ?? Frame.ReadString( status, "relayUrl" ) ?? "";
		yield return BuildSection( "Network access", theme );
		yield return new Row
		{
			Gap = theme.Spacing,
			FlexWrap = FlexWrap.Wrap,
			Children = [.. NetworkChoices.Select( choice => BuildChoice( "network:" + choice.Mode, choice.Label, choice.Mode == network, () =>
			{
				_networkDraft = choice.Mode;
				_version.Value++;
			}, choice.IsPreview ) )],
		};
		yield return BuildParagraph( NetworkChoices.Where( choice => choice.Mode == network ).Select( choice => choice.Text ).FirstOrDefault() ?? "", theme.Muted );
		if ( network != "off" )
			yield return BuildEntry( "port", "Port", port, "47390", text => _portDraft = text );
		if ( network == "relay" )
			yield return BuildEntry( "relay", "Relay", relay, "https://relay.example.com", text => _relayDraft = text );
		yield return theme.BuildButton( "Apply", () => RunRequest( () => UpdateNetworkAsync( client, network, port, relay ) ), isEnabled: !_isBusy );

		if ( Frame.ReadStrings( status, "addresses" ) is { Count: > 0 } addresses )
			yield return BuildParagraph( "Devices reach this PC at " + string.Join( ", ", addresses ) + ".", theme.Muted, selectable: true );
		if ( Frame.ReadString( status, "webUrl" ) is { } webUrl )
			yield return BuildParagraph( "Web client: " + webUrl, theme.Muted, selectable: true );
		if ( Frame.ReadString( status, "relayStatus" ) is { } relayStatus )
			yield return BuildParagraph( "Relay: " + relayStatus, theme.Muted );
		if ( Frame.ReadString( status, "error" ) is { } error )
			yield return BuildParagraph( error, theme.Error, selectable: true );
	}

	/// <summary>Builds the pairing section: Pair device with what a new device may do and the Allow control right away switch with its warning, or the
	/// shown offer's QR code, link, web address and countdown.</summary>
	private IEnumerable<Blob> BuildPairing( RemoteClient client, Theme theme )
	{
		yield return BuildSection( "Pair a device", theme );
		if ( _pairing is not { } pairing )
		{
			yield return BuildParagraph( "Show a link to scan or paste on the device. It works once, for five minutes, and only while it is shown here.", theme.Muted );
			yield return BuildParagraph( "A new device gets view only until you allow control. " + ViewText, theme.Muted );
			var isControl = _isPairControl;
			yield return BuildSwitch( "Allow control right away", isControl, !_isBusy, () =>
			{
				_isPairControl = !isControl;
				_version.Value++;
			} );
			if ( isControl )
				yield return BuildParagraph( "Anyone who opens this link in the next 5 minutes can run programs on this PC as you.", theme.Error );
			yield return theme.BuildButton( "Pair device", () => RunRequest( () => UpdatePairAsync( client, isControl ) ), isEnabled: !_isBusy );
			yield break;
		}

		var left = pairing.Expires - DateTimeOffset.UtcNow;
		yield return BuildQr( pairing.Modules );
		yield return BuildParagraph( $"Expires in {(int)left.TotalMinutes}:{left.Seconds:00}. Scan the code with the phone's camera, or paste the link in Loom on the other PC. "
			+ "Anyone who sees this code can pair, so show it to the device only.", theme.Muted );
		if ( pairing.Control )
			yield return BuildParagraph( "Pairs with control.", theme.Error );
		yield return BuildParagraph( pairing.Link, theme.Text, selectable: true );
		if ( pairing.WebUrl is { } webUrl )
			yield return BuildParagraph( "Web client: " + webUrl, theme.Muted, selectable: true );
		yield return theme.BuildButton( "Done", () => _context.Run( () =>
		{
			_pairing = null;
			_version.Value++;
			_ = WithdrawPairingAsync( client );
		} ) );
	}

	/// <summary>Builds the devices that paired with view only and wait for the user's answer, each with Allow control and View only.</summary>
	private IEnumerable<Blob> BuildPending( Theme theme )
	{
		if ( _module.Prompts is not { } prompts || prompts.Pending is not { Count: > 0 } pending )
			yield break;

		yield return BuildSection( "Waiting for your answer", theme );
		foreach ( var device in pending )
		{
			var isAnswering = prompts.IsAnswering( device.DeviceId );
			var allow = theme.BuildButton( "Allow control", () => _context.Run( () => prompts.Answer( device.DeviceId, control: true ) ), isEnabled: !isAnswering );
			allow.AccessibilityName = $"Allow {device.Name} to control this PC";
			var keep = theme.BuildButton( "View only", () => _context.Run( () => prompts.Answer( device.DeviceId, control: false ) ), isEnabled: !isAnswering );
			keep.AccessibilityName = $"Keep {device.Name} view only";
			yield return new Container
			{
				Key = "pending:" + device.DeviceId,
				Padding = theme.Spacing,
				Gap = theme.Spacing / 2,
				BorderRadius = theme.Radius,
				BorderWidth = theme.BorderWidth,
				BorderColor = theme.Attention,
				Children =
				[
					new Text { Content = device.Name, },
					BuildParagraph( $"Paired {device.Paired.ToLocalTime():g} with view only. Allow it to control this PC?", theme.Muted ),
					new Row
					{
						Gap = theme.Spacing,
						Children = [allow, keep],
					},
				],
			};
		}
	}

	/// <summary>Builds the paired devices, each with its state, what it may do, an Allow control switch and Revoke.</summary>
	private IEnumerable<Blob> BuildDevices( RemoteClient client, Theme theme )
	{
		yield return BuildSection( "Devices", theme );
		if ( _devices is not { Count: > 0 } devices )
		{
			yield return BuildParagraph( "No device is paired.", theme.Muted );
			yield break;
		}

		yield return BuildParagraph( ControlText, theme.Muted );
		foreach ( var device in devices.OfType<JsonObject>() )
		{
			var deviceId = Frame.ReadString( device, "deviceId" ) ?? "";
			var canControl = Frame.ReadStrings( device, "scopes" )?.Contains( DeviceScopes.Control ) == true;
			var paired = DateTimeOffset.FromUnixTimeSeconds( Frame.ReadLong( device, "paired" ) ?? 0 ).ToLocalTime();
			var state = Frame.ReadBool( device, "connected" ) == true ? "connected" : "not connected";
			yield return new Container
			{
				Key = "device:" + deviceId,
				Padding = theme.Spacing,
				Gap = theme.Spacing / 2,
				BorderRadius = theme.Radius,
				BorderWidth = theme.BorderWidth,
				BorderColor = theme.Border,
				Children =
				[
					new Text { Content = Frame.ReadString( device, "name" ) ?? deviceId, },
					BuildParagraph( $"Paired {paired:g} · {state} · {(canControl ? "view and control" : "view only")}", theme.Muted ),
					new Row
					{
						AlignItems = AlignItems.Center,
						Gap = theme.Spacing * 2,
						Children =
						[
							BuildSwitch( "Allow control", canControl, !_isBusy, () => RunRequest( () => UpdateDevicesAsync( client, "admin.scopes", deviceId, !canControl ) ) ),
							theme.BuildButton( "Revoke", () => RunRequest( () => UpdateDevicesAsync( client, "admin.revoke", deviceId, null ), "The device is revoked." ), isEnabled: !_isBusy ),
						],
					},
				],
			};
		}
	}

	/// <summary>Builds a toggle button among choices, the chosen one outlined in the accent color, with the Preview tag for a choice still in preview.</summary>
	private Button BuildChoice( string key, string label, bool isChosen, Action choose, bool isPreview = false )
	{
		var theme = Theme.Current;
		void Choose() => _context.Run( choose );
		var text = new Text { Content = label, Color = isChosen ? theme.Accent : theme.Text, };
		return new Button
		{
			Key = key,
			Height = theme.ControlHeight,
			Padding = new Edges( 0, theme.Spacing ),
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			Gap = theme.Spacing / 2,
			BorderRadius = theme.Radius,
			BorderWidth = theme.BorderWidth,
			BorderColor = isChosen ? theme.Accent : theme.Border,
			Focusable = true,
			Cursor = Cursor.Pointer,
			AccessibilityName = $"{label}{(isChosen ? ", chosen" : "")}",
			OnClick = Choose,
			KeyBindings = [new( Key.Enter, KeyModifiers.None, Choose ), new( Key.Space, KeyModifiers.None, Choose )],
			HoverStyle = new Style { BackgroundColor = theme.Hover, },
			FocusStyle = new Style { BackgroundColor = theme.Hover, },
			Children = isPreview ? [text, BuildPreviewTag( theme )] : [text],
		};
	}

	/// <summary>Draws a QR code as one path of dark runs on a light square with the quiet zone.</summary>
	private static Container BuildQr( bool[,] modules )
	{
		var size = modules.GetLength( 0 );
		var total = size + QrQuietZone * 2;
		var path = new StringBuilder();
		for ( var y = 0; y < size; y++ )
		{
			var x = 0;
			while ( x < size )
			{
				if ( !modules[y, x] )
				{
					x++;
					continue;
				}

				var start = x;
				while ( x < size && modules[y, x] )
					x++;
				var (left, right, top, bottom) = (start + QrQuietZone, x + QrQuietZone, y + QrQuietZone, y + QrQuietZone + 1);
				path.Append( CultureInfo.InvariantCulture, $"M{left} {top}L{right} {top}L{right} {bottom}L{left} {bottom}Z" );
			}
		}

		var pixels = total * QrModule;
		return new Container
		{
			Width = pixels,
			Height = pixels,
			FlexShrink = 0,
			BackgroundColor = QrColors.Light,
			AccessibilityName = "Pairing QR code",
			Children =
			[
				new Shape
				{
					Path = VectorPath.Parse( path.ToString(), new ElementRect { Width = total, Height = total } ),
					Fit = ShapeFit.Contain,
					Width = pixels,
					Height = pixels,
					Fill = new VectorPaint( QrColors.Dark ),
				},
			],
		};
	}

	/// <summary>Moves the pairing countdown, drops an expired offer, and refreshes the daemon's status every few ticks.</summary>
	private void UpdateTick()
	{
		if ( _pairing is { } pairing )
		{
			if ( pairing.Expires <= DateTimeOffset.UtcNow )
			{
				_pairing = null;
				ShowMessage( "The pairing link expired. Show a new one to pair.", true );
			}
			else
				_version.Value++;
		}

		if ( ++_ticks % StatusTicks == 0 )
			UpdateDaemonStatus();
	}

	/// <summary>Reads the daemon's status and devices in the background, in the daemon role while connected.</summary>
	private void UpdateDaemonStatus()
	{
		if ( _module.RunningRole != RemoteModule.DaemonRole || _module.Daemon is not { Status: ClientState.Connected } client || _isRefreshing )
			return;

		_isRefreshing = true;
		_ = RefreshAsync( client );
	}

	/// <summary>Requests the daemon's status and devices and shows them; a failure leaves the last ones. A shown offer the daemon no longer holds
	/// was used by a device, so its code goes away.</summary>
	private async Task RefreshAsync( RemoteClient client )
	{
		var asked = DateTimeOffset.UtcNow;
		JsonObject? status = null;
		JsonArray? devices = null;
		try
		{
			status = await client.RequestAsync( Frame.Create( "admin.status" ), _context.Lifetime ).ConfigureAwait( false );
			devices = (await client.RequestAsync( Frame.Create( "admin.devices" ), _context.Lifetime ).ConfigureAwait( false ))["devices"] as JsonArray;
		}
		catch ( Exception exception ) when ( exception is RemoteException or IOException or OperationCanceledException )
		{
		}

		_context.Post( () =>
		{
			_isRefreshing = false;
			_status = status ?? _status;
			_devices = devices ?? _devices;
			if ( status is not null && _pairing is { } pairing && pairing.Shown < asked && pairing.Expires > DateTimeOffset.UtcNow && status["pairing"] is null )
			{
				_pairing = null;
				ShowMessage( pairing.Control ? "A device paired with control." : "A device paired. It has view only until you allow control.", false );
				return;
			}
			_version.Value++;
		} );
	}

	/// <summary>Sends <c>admin.network</c> and shows the daemon's new status, with what failed when the mode did not fully start.</summary>
	private async Task UpdateNetworkAsync( RemoteClient client, string network, string port, string relay )
	{
		if ( !int.TryParse( port, NumberStyles.None, CultureInfo.InvariantCulture, out var number ) || number is < 1 or > 65535 )
			throw new ArgumentException( "The port must be a number from 1 to 65535." );

		var frame = Frame.Create( "admin.network" );
		frame["network"] = network;
		frame["port"] = number;
		frame["relayUrl"] = relay.Trim();
		var status = await client.RequestAsync( frame, _context.Lifetime ).ConfigureAwait( false );
		var error = Frame.ReadString( status, "error" );
		_context.Post( () =>
		{
			_status = status;
			_networkDraft = null;
			_portDraft = null;
			_relayDraft = null;
			ShowMessage( error is null ? "The daemon listens with the new settings." : "The daemon applied the settings, but: " + error, error is not null );
		} );
	}

	/// <summary>Sends <c>admin.pair</c>, with <c>control</c> for a link that grants control right away, and shows the offer. A daemon that does not echo
	/// <c>control</c> is older than this Loom and would pair view only, so its offer is withdrawn.</summary>
	/// <exception cref="InvalidOperationException">The daemon cannot pair with control.</exception>
	private async Task UpdatePairAsync( RemoteClient client, bool control )
	{
		var request = Frame.Create( "admin.pair" );
		if ( control )
			request["control"] = true;
		var answer = await client.RequestAsync( request, _context.Lifetime ).ConfigureAwait( false );
		if ( control && Frame.ReadBool( answer, "control" ) != true )
		{
			await WithdrawPairingAsync( client ).ConfigureAwait( false );
			throw new InvalidOperationException( "The daemon is older than this Loom; restart it to pair with control." );
		}

		var link = Frame.ReadString( answer, "link" ) ?? throw new IOException( "The daemon answered no pairing link." );
		var webUrl = Frame.ReadString( answer, "webUrl" );
		var modules = QrCode.Encode( webUrl ?? link );
		var pairing = new Pairing( link, webUrl, DateTimeOffset.FromUnixTimeSeconds( Frame.ReadLong( answer, "expires" ) ?? 0 ), modules, DateTimeOffset.UtcNow, control );
		_context.Post( () =>
		{
			_pairing = pairing;
			_isPairControl = false;
			_version.Value++;
		} );
	}

	/// <summary>Sends <c>admin.unpair</c>, so the hidden code no longer pairs; a daemon that is gone has no offer left anyway.</summary>
	private async Task WithdrawPairingAsync( RemoteClient client )
	{
		try
		{
			await client.RequestAsync( Frame.Create( "admin.unpair" ), CancellationToken.None ).ConfigureAwait( false );
		}
		catch ( Exception exception ) when ( exception is RemoteException or IOException or ObjectDisposedException or OperationCanceledException )
		{
			// The offer expires within five minutes regardless.
		}
	}

	/// <summary>Sends <c>admin.scopes</c> or <c>admin.revoke</c> for a device and shows the device list it answers.</summary>
	private async Task UpdateDevicesAsync( RemoteClient client, string type, string deviceId, bool? control )
	{
		var frame = Frame.Create( type );
		frame["deviceId"] = deviceId;
		if ( control is { } isOn )
			frame["control"] = isOn;
		var answer = await client.RequestAsync( frame, _context.Lifetime ).ConfigureAwait( false );
		var devices = answer["devices"] as JsonArray;
		_context.Post( () =>
		{
			_devices = devices ?? _devices;
			_version.Value++;
		} );
	}

	/// <summary>A pairing offer shown with its QR code.</summary>
	/// <param name="Link">The native pairing link.</param>
	/// <param name="WebUrl">The web client's pairing address, or null.</param>
	/// <param name="Expires">When the link stops working.</param>
	/// <param name="Modules">The QR code's modules, of the web address when there is one, else of the link.</param>
	/// <param name="Shown">When the panel received it, so a status asked for earlier does not hide it.</param>
	/// <param name="Control">Whether the device that pairs through it gets control at once.</param>
	private sealed record Pairing( string Link, string? WebUrl, DateTimeOffset Expires, bool[,] Modules, DateTimeOffset Shown, bool Control );
}
