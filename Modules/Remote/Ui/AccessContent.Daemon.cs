using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>The daemon part of Remote access: the restart offer for an outdated daemon, the pairings waiting for an answer, the QR code, and the
/// <c>admin.*</c> requests behind the page.</summary>
internal sealed partial class AccessContent
{
	/// <summary>Defines the drawn size of one QR module in logical pixels.</summary>
	private const int QrModule = 4;

	/// <summary>Defines the light margin around a QR code, in modules, which scanners need.</summary>
	private const int QrQuietZone = 4;

	/// <summary>Defines how often the daemon's status refreshes, in ticks.</summary>
	private const int StatusTicks = 5;

	/// <summary>Defines what the <c>control</c> scope allows, as the pairing and devices sections say it (docs/remote.md, Scopes).</summary>
	internal const string ControlText = "Control: also send prompts, change the store, and use this PC's files, terminals, processes and windows (stream and input) as you. "
		+ "Loom's own keys and device list stay off-limits, but a controlling device can run any program, so only allow devices you trust like this PC.";

	/// <summary>Holds the QR's dark and light colors: scanners need dark modules on a light ground whatever the theme.</summary>
	private static readonly (Color Dark, Color Light) QrColors = (Color.Parse( "#000000" ), Color.Parse( "#ffffff" ));

	/// <summary>Stores the daemon's last <c>admin.status</c> answer, or null before the first.</summary>
	private JsonObject? _status;

	/// <summary>Stores the daemon's last device list, or null before the first.</summary>
	private JsonArray? _devices;

	/// <summary>Stores the pairing offer shown, or null.</summary>
	private Pairing? _pairing;

	/// <summary>Stores whether a status refresh runs.</summary>
	private bool _isRefreshing;

	/// <summary>Counts ticks, so the status refreshes every few.</summary>
	private int _ticks;

	/// <summary>Builds the daemon's restart offer when it runs another build of Loom than this window.</summary>
	private IEnumerable<Blob> BuildOutdated( Theme theme )
	{
		if ( _module.Daemon is not { IsOutdated: true } client )
			yield break;

		yield return BuildParagraph( $"The daemon runs another build of Loom ({client.HostVersion ?? "an older one"}; this window runs {RemoteProtocol.AppVersion}). "
			+ "Restart it to update; that stops its running agent turns.", theme.Attention );
		yield return theme.BuildButton( "Restart daemon", () => _context.Run( client.RestartDaemon ) );
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
				ShowMessage( "The pairing code expired. Pair again for a new one.", true );
			}
			else
				_version.Value++;
		}

		if ( _pendingNetwork is not null )
		{
			UpdateServe();
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

	/// <summary>Sends <c>admin.revoke</c> for a device, which signs it out at once, and shows the device list it answers.</summary>
	private async Task RevokeAsync( RemoteClient client, string deviceId )
	{
		var frame = Frame.Create( "admin.revoke" );
		frame["deviceId"] = deviceId;
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
