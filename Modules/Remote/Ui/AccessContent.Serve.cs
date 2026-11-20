using System.Text.Json.Nodes;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>The Remote access page, laid out as Tern's: <b>Serve over</b> picks how devices reach this PC's chats and shows what is left to do,
/// <b>Pair</b> shows a code to control or to watch, and <b>Devices</b> lists the paired devices with Revoke.</summary>
/// <remarks>Any mode but Off runs This PC's chats in the daemon, which then starts at sign-in; choosing one moves the chats while Loom runs, after
/// running agents finish, and applies the mode once the daemon answers.</remarks>
internal sealed partial class AccessContent
{
	/// <summary>Defines Tailscale's download page.</summary>
	private const string TailscaleDownload = "https://tailscale.com/download";

	/// <summary>Lists the ways devices reach this PC, with their labels and what each needs.</summary>
	private static readonly (string Mode, string Label, string Text)[] ServeChoices =
	[
		("off", "Off", "Only Loom on this PC reaches its chats."),
		("tailscale", "Tailscale: my devices", "Your tailnet's devices open Loom over HTTPS. Install Tailscale and sign in on this PC and the phone, and turn on "
			+ "HTTPS certificates for the tailnet once."),
		("funnel", "Anywhere: a link (Tailscale Funnel)", "Nothing to install on the phone: it opens the pairing link from anywhere. Anyone can reach the page, "
			+ "but only paired devices get in. Needs Tailscale on this PC, HTTPS certificates on for the tailnet, and Funnel allowed for this PC."),
		("relay", "Through a relay", "For any network, without Tailscale: a relay you run forwards bytes it cannot read. The relay serves the page's code, so "
			+ "use a relay you run or trust."),
		("lan", "This network", "Other Loom PCs on this network connect directly. Phones need Tailscale or a relay, since browsers need HTTPS."),
	];

	/// <summary>Stores Tailscale's state as last read, or null before the first read; shown as what is left to do.</summary>
	private TailscaleState? _tailscale;

	/// <summary>Stores whether Tailscale's state is being read.</summary>
	private bool _isReadingTailscale;

	/// <summary>Stores the mode chosen while the chats move to the daemon or it starts, applied once it answers; null when none waits.</summary>
	private string? _pendingNetwork;

	/// <summary>Stores the relay address being typed, or null to show the saved one.</summary>
	private string? _relayDraft;

	/// <summary>Gets the mode shown as chosen: one waiting to apply, else the daemon's, else Off while This PC's chats run in this Loom.</summary>
	private string ChosenMode => _pendingNetwork
		?? (_module.RunningRole != RemoteModule.DaemonRole ? "off"
			: (_status is { } status ? Frame.ReadString( status, "network" ) : null) ?? _context.App.Settings.Get( RemoteModule.Network, SettingsScope.Global ));

	/// <summary>Builds Serve over: what the page does, one choice per mode, the chosen mode's needs, the relay address, the move under way, and what
	/// devices reach or what is left to do.</summary>
	private IEnumerable<Blob> BuildServe( Theme theme )
	{
		var chosen = ChosenMode;
		yield return BuildParagraph( "Remote access serves Loom's web app from the daemon on this PC, so a phone or another PC can follow your chats and, "
			+ "if you allow it, send prompts and answer questions. Nothing goes through Loom's servers; there aren't any.", theme.Muted );
		yield return BuildSection( "Serve over", theme );
		foreach ( var (mode, label, _) in ServeChoices )
			yield return BuildChoice( mode, label, mode == chosen );
		yield return BuildParagraph( ServeChoices.FirstOrDefault( choice => choice.Mode == chosen ).Text ?? "", theme.Muted );
		if ( chosen == "relay" )
		{
			var saved = _context.App.Settings.Get( RemoteModule.RelayUrl, SettingsScope.Global );
			yield return BuildEntry( "relay", "Relay", _relayDraft ?? saved, "https://relay.example.com", text => _relayDraft = text,
				_ => ChooseServe( "relay" ) );
			if ( _relayDraft is { } draft && draft != saved )
				yield return theme.BuildButton( "Save", () => ChooseServe( "relay" ), isEnabled: !_isBusy );
		}

		if ( _pendingNetwork is not null || _module.Switching is not null )
		{
			yield return BuildParagraph( DescribeProgress(), theme.Attention );
			if ( _module.Switching is not { IsWaiting: false } )
				yield return theme.BuildButton( "Cancel", () => _context.Run( () =>
				{
					_pendingNetwork = null;
					_module.CancelSwitch();
					_version.Value++;
				} ) );
			yield break;
		}

		foreach ( var blob in BuildReach( chosen, theme ) )
			yield return blob;
		foreach ( var blob in BuildOutdated( theme ) )
			yield return blob;
	}

	/// <summary>Builds one Serve over choice: a dot, filled when chosen, and the mode's label.</summary>
	private Button BuildChoice( string mode, string label, bool isChosen )
	{
		var theme = Theme.Current;
		void Choose() => ChooseServe( mode );
		return new Button
		{
			Key = "serve:" + mode,
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Padding = new Edges( theme.Spacing / 2, 0 ),
			Focusable = true,
			Cursor = Cursor.Pointer,
			AccessibilityName = isChosen ? $"{label}, selected" : label,
			OnClick = Choose,
			KeyBindings = [new( Key.Enter, KeyModifiers.None, Choose ), new( Key.Space, KeyModifiers.None, Choose )],
			FocusStyle = new Style { BackgroundColor = theme.Hover },
			Children =
			[
				new Container
				{
					Width = 14,
					Height = 14,
					FlexShrink = 0,
					BorderRadius = 7,
					BorderWidth = isChosen ? 4 : theme.BorderWidth,
					BorderColor = isChosen ? theme.Accent : theme.Border,
				},
				new Text { Content = label, Color = isChosen ? theme.Text : theme.Muted },
			],
		};
	}

	/// <summary>Builds what the chosen mode reaches, or for Tailscale and Funnel without an address yet, the setup steps with what is done and a button
	/// for each step left, and Check again.</summary>
	private IEnumerable<Blob> BuildReach( string chosen, Theme theme )
	{
		if ( chosen == "off" || _module.RunningRole != RemoteModule.DaemonRole || _status is not { } status )
			yield break;

		if ( Frame.ReadString( status, "webUrl" ) is { } webUrl )
			yield return BuildParagraph( "Devices open " + webUrl, theme.Text, selectable: true );
		else if ( Frame.ReadStrings( status, "addresses" ) is { Count: > 0 } addresses )
			yield return BuildParagraph( "Loom PCs reach this PC at " + string.Join( ", ", addresses ) + ".", theme.Text, selectable: true );
		if ( Frame.ReadString( status, "relayStatus" ) is { } relayStatus )
			yield return BuildParagraph( "Relay: " + relayStatus, theme.Muted );

		var error = Frame.ReadString( status, "error" );
		if ( chosen is "tailscale" or "funnel" && Frame.ReadString( status, "webUrl" ) is null )
		{
			if ( _tailscale is not { } tailscale )
			{
				ReadTailscaleSoon();
				yield break;
			}

			var steps = ListSteps( tailscale, chosen == "funnel" );
			foreach ( var (text, isDone, label, url) in steps )
			{
				var row = new List<Blob>
				{
					new Text { Content = isDone ? "\u2713" : "\u25CB", Color = isDone ? theme.Success : theme.Attention, Width = 16, FlexShrink = 0 },
					new Text { Content = text, Color = isDone ? theme.Muted : theme.Text, FlexGrow = 1, FlexShrink = 1, TextWrap = TextWrap.Wrap },
				};
				if ( !isDone && url is not null )
					row.Add( theme.BuildButton( label, () => _context.Run( () => _context.App.OpenLink( new Uri( url ) ) ) ) );
				yield return new Row { Key = "step:" + text, AlignItems = AlignItems.Center, Gap = theme.Spacing, Children = row };
			}
			// Once every step is done, what still fails is Tailscale's own answer.
			if ( error is not null && steps.All( step => step.IsDone ) )
				yield return BuildParagraph( error, theme.Error, selectable: true );
			yield return theme.BuildButton( "Check again", () => ChooseServe( chosen ), isEnabled: !_isBusy );
		}
		else if ( error is not null )
			yield return BuildParagraph( error, theme.Error, selectable: true );
	}

	/// <summary>Builds Pair, while the daemon serves devices: Pair to control and Pair to watch, or the shown code.</summary>
	private IEnumerable<Blob> BuildPair( Theme theme )
	{
		if ( _module.Daemon is not { Status: ClientState.Connected } client || _pendingNetwork is not null || ChosenMode == "off" )
			yield break;

		yield return BuildSection( "Pair", theme );
		if ( _pairing is { } pairing )
		{
			foreach ( var blob in BuildOffer( client, pairing, theme ) )
				yield return blob;
			yield break;
		}

		yield return new Row
		{
			Gap = theme.Spacing,
			Children =
			[
				theme.BuildButton( "Pair to control\u2026", () => RunRequest( () => UpdatePairAsync( client, control: true ) ), isEnabled: !_isBusy ),
				theme.BuildButton( "Pair to watch\u2026", () => RunRequest( () => UpdatePairAsync( client, control: false ) ), isEnabled: !_isBusy ),
			],
		};
	}

	/// <summary>Builds the shown code: the QR code of its web address, which a phone's camera opens, or without one only the link a Loom PC opens; the
	/// countdown, and Done.</summary>
	private IEnumerable<Blob> BuildOffer( RemoteClient client, Pairing pairing, Theme theme )
	{
		var left = pairing.Expires - DateTimeOffset.UtcNow;
		var expires = $"It works once and expires in {(int)left.TotalMinutes}:{left.Seconds:00}.";
		if ( pairing.WebUrl is not null )
		{
			yield return BuildQr( pairing.Modules );
			yield return BuildParagraph( "Scan it with the phone's camera, or open the link in Loom on the other PC. " + expires, theme.Text );
		}
		else
			yield return BuildParagraph( "Open this link in Loom on the other PC. " + expires, theme.Text );
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

	/// <summary>Builds Devices: each paired device with whether it is connected and may control or watch, and Revoke; then the pairings waiting for an answer.</summary>
	private IEnumerable<Blob> BuildDevices( Theme theme )
	{
		if ( _module.Daemon is not { } client || _devices is not { Count: > 0 } devices )
			yield break;

		yield return BuildSection( "Devices", theme );
		foreach ( var device in devices.OfType<JsonObject>() )
		{
			var deviceId = Frame.ReadString( device, "deviceId" ) ?? "";
			var name = Frame.ReadString( device, "name" ) ?? deviceId;
			var canControl = Frame.ReadStrings( device, "scopes" )?.Contains( DeviceScopes.Control ) == true;
			var isConnected = Frame.ReadBool( device, "connected" ) == true;
			var revoke = theme.BuildButton( "Revoke", () => RunRequest( () => RevokeAsync( client, deviceId ), $"{name} is signed out." ),
				isEnabled: !_isBusy );
			revoke.AccessibilityName = "Revoke " + name;
			yield return new Row
			{
				Key = "device:" + deviceId,
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing,
				Children =
				[
					BuildDot( isConnected ? theme.Success : theme.Muted ),
					new Text { Content = name, FlexGrow = 1, FlexShrink = 1, TextWrap = TextWrap.Wrap, },
					new Text { Content = canControl ? "control" : "watch", Color = theme.Muted, },
					revoke,
				],
			};
		}
	}

	/// <summary>Describes a move under way: the agents the move of the chats waits for, the move itself, or the daemon starting.</summary>
	private string DescribeProgress()
	{
		if ( _module.Switching is { } pending )
		{
			var waiting = _module.CountWaiting();
			if ( pending.IsWaiting && waiting > 0 )
				return $"Waiting for {waiting} {(waiting == 1 ? "agent" : "agents")} to finish, then the chats move to the daemon on this PC. Chat panels stay open.";
			return "Moving this PC's chats to the daemon\u2026";
		}

		return _module.Daemon is { } daemon ? "Starting the daemon\u2026 " + daemon.StatusText : "Starting the daemon\u2026";
	}

	/// <summary>Chooses how devices reach this PC. Off closes the network and keeps the daemon; any other mode moves This PC's chats to the daemon unless
	/// they run there, starts it at sign-in from now on, and applies the mode once it answers (<see cref="UpdateServe"/>).</summary>
	private void ChooseServe( string mode ) => _context.Run( () =>
	{
		if ( _isBusy )
			return;

		_message = "";
		_tailscale = null;
		var settings = _context.App.Settings;
		if ( mode == "off" )
		{
			_pendingNetwork = null;
			if ( _module.Daemon is { Status: ClientState.Connected } client )
				RunRequest( () => ServeAsync( client, "off" ) );
			else
				settings.Set( RemoteModule.Network, SettingsScope.Global, "off" );
			_version.Value++;
			return;
		}

		_pendingNetwork = mode;
		settings.Set( RemoteModule.LoginItem, SettingsScope.Global, true );
		if ( _module.RunningRole != RemoteModule.DaemonRole )
			_ = _module.SwitchRole( RemoteModule.DaemonRole );
		else if ( _module.Daemon is { Status: ClientState.Stopped } stopped )
			stopped.Reconnect();
		_version.Value++;
		UpdateServe();
	} );

	/// <summary>Applies the mode that waits once the daemon answers; a move of the chats that was cancelled or failed drops it, and the module's notice
	/// says why.</summary>
	private void UpdateServe()
	{
		if ( _pendingNetwork is not { } mode || _isBusy )
			return;

		if ( _module.RunningRole != RemoteModule.DaemonRole )
		{
			if ( _module.Switching is null )
			{
				_pendingNetwork = null;
				_version.Value++;
			}
			return;
		}

		if ( _module.Daemon is not { Status: ClientState.Connected } client )
			return;

		_pendingNetwork = null;
		RunRequest( () => ServeAsync( client, mode ) );
	}

	/// <summary>Sends <c>admin.network</c> with the mode, the saved port and the relay address, which the daemon saves in the settings, and shows its new
	/// status; for Tailscale and Funnel it reads Tailscale's state too, for what is left to do.</summary>
	private async Task ServeAsync( RemoteClient client, string mode )
	{
		var relay = (_relayDraft ?? _context.App.Settings.Get( RemoteModule.RelayUrl, SettingsScope.Global )).Trim();
		var frame = Frame.Create( "admin.network" );
		frame["network"] = mode;
		frame["port"] = _context.App.Settings.Get( RemoteModule.Port, SettingsScope.Global );
		frame["relayUrl"] = relay;
		var status = await client.RequestAsync( frame, _context.Lifetime ).ConfigureAwait( false );
		TailscaleState? tailscale = mode is "tailscale" or "funnel" ? await ReadTailscaleAsync().ConfigureAwait( false ) : null;
		_context.Post( () =>
		{
			_status = status;
			_tailscale = tailscale;
			_relayDraft = null;
			_version.Value++;
		} );
	}

	/// <summary>Reads Tailscale's state in the background once, for a page opened on Tailscale or Funnel without an address.</summary>
	private void ReadTailscaleSoon()
	{
		if ( _isReadingTailscale )
			return;

		_isReadingTailscale = true;
		_ = Task.Run( async () =>
		{
			var tailscale = await ReadTailscaleAsync().ConfigureAwait( false );
			_context.Post( () =>
			{
				_isReadingTailscale = false;
				_tailscale = tailscale;
				_version.Value++;
			} );
		} );
	}

	/// <summary>Lists the steps before Tailscale serves this PC, in order, each with whether it is done and the button and page that does it: install,
	/// sign in, HTTPS certificates, and for Funnel, allowing Funnel for this PC. A step after one not done shows as not done.</summary>
	private static List<(string Text, bool IsDone, string Label, string? Url)> ListSteps( TailscaleState tailscale, bool isFunnel )
	{
		var status = tailscale.Status;
		var isRunning = status is { IsRunning: true };
		var steps = new List<(string Text, bool IsDone, string Label, string? Url)>
		{
			("Install Tailscale on this PC", tailscale.IsInstalled, "Install Tailscale", TailscaleDownload),
			(isRunning || status is null ? "Sign in to Tailscale" : TailscaleCli.DescribeProblem( status ) ?? "Sign in to Tailscale", tailscale.IsInstalled && isRunning,
				"Sign in", status?.AuthUrl),
			("Turn on HTTPS certificates for the tailnet", isRunning && status!.HasHttps, "Open DNS settings", TailscaleCli.DnsSettingsUrl),
		};
		if ( isFunnel )
			steps.Add( ("Allow Funnel for this PC", isRunning && status!.HasFunnel, "Allow Funnel", TailscaleCli.BuildFunnelUrl( status?.NodeId )) );
		return steps;
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

	/// <summary>Tailscale on this PC as last read.</summary>
	/// <param name="IsInstalled">Whether <c>tailscale.exe</c> was found.</param>
	/// <param name="Status">Its state, or null when it could not be read.</param>
	/// <param name="Problem">Why the state could not be read, or null.</param>
	private sealed record TailscaleState( bool IsInstalled, TailscaleStatus? Status, string? Problem );
}
