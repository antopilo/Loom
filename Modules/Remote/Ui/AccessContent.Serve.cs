using System.Text.Json.Nodes;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>The Sharing part of Remote access: a three-level picker (Off, Local only, Anywhere), the picked level's settings, Tailscale's setup steps
/// for Anywhere, and <b>Pair a device</b>; <b>Devices</b> lists the paired devices with Revoke.</summary>
/// <remarks>Sharing runs This PC's chats in the daemon, which then starts at sign-in; picking Local only or Anywhere moves the chats while Loom runs,
/// after running agents finish, and applies the way devices reach this PC once the daemon answers.</remarks>
internal sealed partial class AccessContent
{
	/// <summary>Defines Tailscale's download page.</summary>
	private const string TailscaleDownload = "https://tailscale.com/download";

	/// <summary>Defines the way devices reach this PC when Anywhere is picked and none was set before: a link through Tailscale Funnel, which needs nothing on the phone.</summary>
	private const string DefaultAnywhereMode = "funnel";

	/// <summary>Defines the size of a level's icon in logical pixels.</summary>
	private const int LevelIconSize = 22;

	/// <summary>Retains the installed Segoe Fluent Icons face drawn by the level picker.</summary>
	private static readonly FontSource IconFont = SystemFonts.Register( "Lokn Remote Icons", 400, italic: false, "SegoeIcons.ttf" );

	/// <summary>Lists the three sharing levels: their id, icon glyph, label and one line on what they do.</summary>
	private static readonly (string Level, string Glyph, string Label, string Text)[] Levels =
	[
		("off", "\uE7E8", "Off", "Only Loom on this PC reaches its chats."),
		("local", "\uE80F", "Local only", "Other Loom PCs on this network connect directly. Nothing to set up."),
		("anywhere", "\uE774", "Anywhere", "Your phone and other PCs open your chats from any network, over HTTPS through Tailscale."),
	];

	/// <summary>Lists the ways Anywhere reaches this PC, shown under Advanced.</summary>
	private static readonly (string Mode, string Label, string Text)[] AnywhereModes =
	[
		("funnel", "Public link", "Devices open a link; only paired devices get in. Nothing to install on the phone."),
		("tailscale", "My Tailscale devices", "Only devices signed in to your tailnet reach this PC. Install Tailscale on the phone too."),
		("relay", "Relay", "A relay you run forwards bytes it cannot read. No Tailscale needed."),
	];

	/// <summary>Stores Tailscale's state as last read, or null before the first read; shown as the setup steps.</summary>
	private TailscaleState? _tailscale;

	/// <summary>Stores whether Tailscale's state is being read.</summary>
	private bool _isReadingTailscale;

	/// <summary>Stores the mode chosen while the chats move to the daemon or it starts, applied once it answers; null when none waits.</summary>
	private string? _pendingNetwork;

	/// <summary>Stores the relay address being typed, or null to show the saved one.</summary>
	private string? _relayDraft;

	/// <summary>Stores whether Anywhere's Advanced choices show.</summary>
	private bool _isAdvancedOpen;

	/// <summary>Stores the mode whose Tailscale setup steps show, which the view checks again every few seconds; null while none show.</summary>
	private string? _setupMode;

	/// <summary>Stores the page Tailscale last named to allow Funnel for this PC, kept while the daemon retries: each retry clears its error for a
	/// moment, and the Allow Funnel button that read only the error came and went too fast to click.</summary>
	private string? _funnelLink;

	/// <summary>Gets the mode shown as chosen: one waiting to apply, else the daemon's, else Off while This PC's chats run in this Loom.</summary>
	private string ChosenMode => _pendingNetwork
		?? (_module.RunningRole != RemoteModule.DaemonRole ? "off"
			: (_status is { } status ? Frame.ReadString( status, "network" ) : null) ?? _context.App.Settings.Get( RemoteModule.Network, SettingsScope.Global ));

	/// <summary>Maps a mode to its level: Off, Local only for this network, and Anywhere for Tailscale, Funnel and the relay.</summary>
	private static string ComputeLevel( string mode ) => mode switch
	{
		"off" => "off",
		"lan" => "local",
		_ => "anywhere",
	};

	/// <summary>Builds the switch of <see cref="RemoteModule.AgentWindows"/>: whether agents of chats on this PC may see and use its windows.</summary>
	private Row BuildAgentWindows( Theme theme )
	{
		var settings = _module.Context.App.Settings;
		var isOn = settings.Get( RemoteModule.AgentWindows, SettingsScope.Global );
		return BuildSettingRow( "agent-windows", RemoteModule.AgentWindows.Title, RemoteModule.AgentWindows.Description,
			theme.BuildSwitch( RemoteModule.AgentWindows.Title, isOn, () => _context.Run( () =>
			{
				settings.Set( RemoteModule.AgentWindows, SettingsScope.Global, !isOn );
				_version.Value++;
			} ) ), theme );
	}

	/// <summary>Builds Sharing: the level picker, then the picked level's own settings, the move under way, and Pair a device.</summary>
	private IEnumerable<Blob> BuildServe( Theme theme )
	{
		var chosen = ChosenMode;
		_setupMode = null;
		var level = ComputeLevel( chosen );
		yield return BuildSection( "Sharing", theme );
		yield return BuildLevels( level, theme );
		yield return BuildParagraph( Levels.First( item => item.Level == level ).Text, theme.Muted );
		if ( OperatingSystem.IsWindows() )
			yield return BuildGroup( theme, [BuildAgentWindows( theme )] );

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

		if ( level == "off" )
			yield break;

		if ( level == "anywhere" )
			yield return BuildAdvanced( chosen, theme );
		foreach ( var blob in BuildReach( chosen, theme ) )
			yield return blob;
		if ( BuildPair( theme ) is { } pair )
			yield return BuildGroup( theme, [pair] );
		foreach ( var blob in BuildOutdated( theme ) )
			yield return blob;
	}

	/// <summary>Builds the level picker: three equal tiles with an icon and a label, the picked one raised with an accent border.</summary>
	private Row BuildLevels( string picked, Theme theme )
	{
		var tiles = new List<Blob>();
		foreach ( var (level, glyph, label, _) in Levels )
		{
			var isPicked = level == picked;
			void Pick() => PickLevel( level );
			tiles.Add( new Button
			{
				Key = "level:" + level,
				FlexGrow = 1,
				FlexBasis = 0,
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing * 0.75,
				Padding = new Edges( theme.Spacing * 1.5, theme.Spacing ),
				BorderRadius = theme.Radius + 2,
				BackgroundColor = isPicked ? theme.Raised : Color.Transparent,
				BorderColor = isPicked ? theme.Accent : Color.Transparent,
				BorderWidth = theme.BorderWidth,
				Color = isPicked ? theme.Text : theme.Muted,
				Disabled = _isBusy,
				Focusable = true,
				Cursor = Cursor.Pointer,
				AccessibilityName = isPicked ? $"{label}, selected" : label,
				OnClick = Pick,
				KeyBindings = [new( Key.Enter, KeyModifiers.None, Pick ), new( Key.Space, KeyModifiers.None, Pick )],
				HoverStyle = new Style { BackgroundColor = isPicked ? theme.Raised : theme.Hover, Color = theme.Text },
				FocusStyle = new Style { BorderColor = theme.Accent },
				Children =
				[
					new Text { Content = glyph, FontFamily = IconFont.Family, FontSize = LevelIconSize, Color = isPicked ? theme.Accent : theme.Muted },
					new Text { Content = label, FontWeight = isPicked ? 600 : 400 },
				],
			} );
		}

		return new Row
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Sharing",
			Padding = 3,
			Gap = 3,
			BackgroundColor = theme.Canvas,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius + 4,
			Children = tiles,
		};
	}

	/// <summary>Picks a level: Off and Local only map to one mode each; Anywhere keeps the Anywhere mode set before, else a public link.</summary>
	private void PickLevel( string level )
	{
		var saved = _context.App.Settings.Get( RemoteModule.Network, SettingsScope.Global );
		ChooseServe( level switch
		{
			"off" => "off",
			"local" => "lan",
			_ => ComputeLevel( ChosenMode ) == "anywhere" ? ChosenMode : ComputeLevel( saved ) == "anywhere" ? saved : DefaultAnywhereMode,
		} );
	}

	/// <summary>Builds Anywhere's Advanced disclosure: how devices reach this PC, and the relay address when the relay is picked.</summary>
	private Container BuildAdvanced( string chosen, Theme theme )
	{
		void Toggle() => _context.Run( () =>
		{
			_isAdvancedOpen = !_isAdvancedOpen;
			_version.Value++;
		} );
		var children = new List<Blob>
		{
			new Button
			{
				Key = "advanced",
				FlexDirection = FlexDirection.Row,
				AlignItems = AlignItems.Center,
				AlignSelf = AlignSelf.FlexStart,
				Gap = theme.Spacing / 2,
				Color = theme.Muted,
				Focusable = true,
				Cursor = Cursor.Pointer,
				AccessibilityName = "Advanced",
				OnClick = Toggle,
				KeyBindings = [new( Key.Enter, KeyModifiers.None, Toggle ), new( Key.Space, KeyModifiers.None, Toggle )],
				HoverStyle = new Style { Color = theme.Text },
				Children = [theme.BuildChevron( _isAdvancedOpen ), new Text { Content = "Advanced" }],
			},
		};
		if ( _isAdvancedOpen )
		{
			var mode = AnywhereModes.FirstOrDefault( item => item.Mode == chosen );
			children.Add( theme.BuildSegments( "Connect with", [.. AnywhereModes.Select( item => (item.Mode, item.Label) )], chosen, ChooseServe ) );
			children.Add( BuildParagraph( mode.Text ?? "", theme.Muted ) );
			if ( chosen == "relay" )
			{
				var saved = _context.App.Settings.Get( RemoteModule.RelayUrl, SettingsScope.Global );
				var relay = new List<Blob> { BuildEntry( "relay", "Relay", _relayDraft ?? saved, "https://relay.example.com", text => _relayDraft = text, _ => ChooseServe( "relay" ) ) };
				if ( _relayDraft is { } draft && draft != saved )
					relay.Add( theme.BuildButton( "Save", () => ChooseServe( "relay" ), isEnabled: !_isBusy ) );
				children.Add( new Row { AlignItems = AlignItems.Center, Gap = theme.Spacing, Children = relay } );
			}
		}
		return new Container { Gap = theme.Spacing, Children = children };
	}

	/// <summary>Builds what the chosen mode reaches: the address once devices can open it, else for Tailscale and Funnel the setup steps.</summary>
	private IEnumerable<Blob> BuildReach( string chosen, Theme theme )
	{
		if ( _module.RunningRole != RemoteModule.DaemonRole || _status is not { } status )
			yield break;

		var webUrl = Frame.ReadString( status, "webUrl" );
		var error = Frame.ReadString( status, "error" );
		if ( chosen is "tailscale" or "funnel" && webUrl is null )
		{
			if ( _tailscale is not { } tailscale )
			{
				ReadTailscaleSoon();
				yield return BuildParagraph( "Checking Tailscale\u2026", theme.Muted );
				yield break;
			}

			_setupMode = chosen;
			yield return BuildSetup( chosen, tailscale, error, theme );
			yield break;
		}

		// Funnel works or another mode is chosen, so a page to allow it is no longer needed.
		_funnelLink = null;
		if ( webUrl is not null )
			yield return BuildAddress( "Devices open", webUrl, theme );
		else if ( Frame.ReadStrings( status, "addresses" ) is { Count: > 0 } addresses )
			yield return BuildAddress( "Loom PCs reach this PC at", string.Join( ", ", addresses ), theme );
		if ( chosen == "lan" )
			yield return BuildParagraph( "Phones can't open it here: browsers need HTTPS. Pick Anywhere for a phone.", theme.Muted );
		if ( Frame.ReadString( status, "relayStatus" ) is { } relayStatus )
			yield return BuildParagraph( "Relay: " + relayStatus, theme.Muted );
		if ( error is not null )
			yield return BuildParagraph( error, theme.Error, selectable: true );
	}

	/// <summary>Builds the Tailscale setup guide: numbered steps, each done, current with its button, or still to come, then Check again.</summary>
	private Container BuildSetup( string chosen, TailscaleState tailscale, string? error, Theme theme )
	{
		var isRunning = tailscale.Status is { IsRunning: true };
		var named = error is null ? null : System.Text.RegularExpressions.Regex.Match( error, @"https://login\.tailscale\.com/f/funnel\S*" ) is { Success: true } match
			? match.Value.TrimEnd( '.', ',', ')' ) : null;
		var funnelLink = chosen == "funnel" ? (_funnelLink = named ?? _funnelLink ?? TailscaleCli.BuildFunnelUrl( tailscale.Status?.NodeId )) : null;
		var signIn = tailscale.Status?.AuthUrl;
		var steps = new List<(string Title, string Text, bool IsDone, string? Button, string? Url)>
		{
			("Install Tailscale on this PC", "A free app that gives this PC a private HTTPS address.", tailscale.IsInstalled, "Download", TailscaleDownload),
			("Sign in to Tailscale", isRunning || tailscale.Status is null ? "Open Tailscale and sign in with any account."
				: TailscaleCli.DescribeProblem( tailscale.Status ) ?? "Open Tailscale and sign in.", isRunning, signIn is null ? null : "Sign in", signIn),
			("Turn on HTTPS certificates", "In Tailscale's DNS settings, under HTTPS Certificates, choose Enable.", tailscale.Status is { HasHttps: true }, "Open DNS settings", TailscaleCli.DnsSettingsUrl),
		};
		if ( chosen == "funnel" )
			steps.Add( ("Allow Funnel for this PC", "Lets a link reach this PC from any network. Tailscale asks once.", tailscale.Status is { HasFunnel: true }, "Allow Funnel", funnelLink) );
		else
			steps.Add( ("Install Tailscale on your phone", "Sign in with the same account; the phone then reaches this PC.", false, "Get the app", TailscaleDownload) );

		var current = steps.FindIndex( step => !step.IsDone );
		var rows = new List<Blob>();
		for ( var index = 0; index < steps.Count; index++ )
			rows.Add( BuildStep( index, steps[index], index == current, theme ) );

		return new Container
		{
			Gap = theme.Spacing,
			Children =
			[
				new Text { Content = "Set up Tailscale", FontWeight = 600 },
				BuildGroup( theme, rows ),
				new Row
				{
					AlignItems = AlignItems.Center,
					Gap = theme.Spacing,
					Children =
					[
						theme.BuildButton( _isBusy ? "Checking\u2026" : "Check again", () => ChooseServe( chosen ), isEnabled: !_isBusy ),
						BuildParagraph( "Loom shows the address once Tailscale is ready.", theme.Muted ),
					],
				},
			],
		};
	}

	/// <summary>Builds one setup step: a numbered circle, or a check once done, its title and text, and its button while it is the current step.</summary>
	private Row BuildStep( int index, (string Title, string Text, bool IsDone, string? Button, string? Url) step, bool isCurrent, Theme theme )
	{
		var color = step.IsDone ? theme.Success : isCurrent ? theme.Accent : theme.Muted;
		var children = new List<Blob>
		{
			new Container
			{
				Width = 22,
				Height = 22,
				FlexShrink = 0,
				AlignItems = AlignItems.Center,
				JustifyContent = JustifyContent.Center,
				BorderRadius = 11,
				BorderColor = color,
				BorderWidth = theme.BorderWidth,
				Children = [step.IsDone
					? new Text { Content = "\uE73E", FontFamily = IconFont.Family, FontSize = 11, Color = color }
					: new Text { Content = (index + 1).ToString(), FontSize = ViewStyle.SmallSize, Color = color }],
			},
			new Container
			{
				FlexGrow = 1,
				FlexShrink = 1,
				Gap = 2,
				Children =
				[
					new Text { Content = step.Title, Color = step.IsDone ? theme.Muted : theme.Text, FontWeight = isCurrent ? 600 : 400 },
					new Text { Content = step.Text, FontSize = ViewStyle.SmallSize, Color = theme.Muted, TextWrap = TextWrap.Wrap },
				],
			},
		};
		if ( !step.IsDone && step.Button is { } label && step.Url is { } url )
			children.Add( theme.BuildButton( label, () => _context.Run( () => _context.App.OpenLink( new Uri( url ) ) ) ) );
		return BuildGroupRow( "step:" + index, theme, [.. children] );
	}

	/// <summary>Builds Pair a device while the daemon serves devices: buttons that show a code to control or to watch, or the shown code.</summary>
	private Blob? BuildPair( Theme theme )
	{
		if ( _module.Daemon is not { Status: ClientState.Connected } client || _pendingNetwork is not null )
			return null;

		if ( _pairing is { } pairing )
			return new Container { Key = "pair", Gap = theme.Spacing, Padding = new Edges( theme.Spacing, theme.Spacing * 1.5 ), Children = [.. BuildOffer( client, pairing, theme )] };

		return BuildSettingRow( "pair", "Pair a device", "Show a one-time code for a phone or another PC. Control lets it send prompts and use this PC; Watch only "
			+ "follows your chats.", new Row
		{
			Gap = theme.Spacing,
			FlexShrink = 0,
			Children =
			[
				theme.BuildButton( "Control\u2026", () => RunRequest( () => UpdatePairAsync( client, control: true ) ), isEnabled: !_isBusy ),
				theme.BuildButton( "Watch\u2026", () => RunRequest( () => UpdatePairAsync( client, control: false ) ), isEnabled: !_isBusy ),
			],
		}, theme );
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
		var rows = new List<Blob>();
		foreach ( var device in devices.OfType<JsonObject>() )
		{
			var deviceId = Frame.ReadString( device, "deviceId" ) ?? "";
			var name = Frame.ReadString( device, "name" ) ?? deviceId;
			var canControl = Frame.ReadStrings( device, "scopes" )?.Contains( DeviceScopes.Control ) == true;
			var isConnected = Frame.ReadBool( device, "connected" ) == true;
			var revoke = theme.BuildButton( "Revoke", () => RunRequest( () => RevokeAsync( client, deviceId ), $"{name} is signed out." ),
				isEnabled: !_isBusy );
			revoke.AccessibilityName = "Revoke " + name;
			rows.Add( BuildGroupRow( "device:" + deviceId, theme,
				BuildDot( isConnected ? theme.Success : theme.Inactive ),
				new Container
				{
					FlexGrow = 1,
					FlexShrink = 1,
					Children =
					[
						new Text { Content = name, TextWrap = TextWrap.Wrap },
						new Text { Content = isConnected ? "Connected" : "Offline", FontSize = ViewStyle.SmallSize, Color = theme.Muted },
					],
				},
				BuildBadge( canControl ? "Control" : "Watch", canControl ? theme.Accent : theme.Muted, theme ),
				revoke ) );
		}
		yield return BuildGroup( theme, rows );
	}

	/// <summary>Builds the address devices use, in a card with a label above the selectable address.</summary>
	private static Container BuildAddress( string label, string address, Theme theme ) => new()
	{
		Gap = 2,
		Padding = new Edges( theme.Spacing, theme.Spacing * 1.5 ),
		BackgroundColor = theme.Raised,
		BorderColor = theme.Border,
		BorderWidth = theme.BorderWidth,
		BorderRadius = theme.Radius * 2,
		Children =
		[
			new Text { Content = label, FontSize = ViewStyle.SmallSize, Color = theme.Muted },
			new Text { Content = address, FontFamily = ViewStyle.CodeFontFamily, Color = theme.Accent, Selectable = true, TextWrap = TextWrap.Wrap },
		],
	};

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

	/// <summary>Checks the setup again in the background while its steps show: applies the mode once more, so the daemon serves as soon as Tailscale is
	/// ready, and reads Tailscale's state, so each step shows done as the user finishes it. Failures show in the next status.</summary>
	private void UpdateSetup()
	{
		if ( _setupMode is not { } mode || _isBusy || _isReadingTailscale || _module.Daemon is not { Status: ClientState.Connected } client )
			return;

		_isReadingTailscale = true;
		_ = Task.Run( async () =>
		{
			try
			{
				await ServeAsync( client, mode ).ConfigureAwait( false );
			}
			catch ( Exception ) when ( !_context.Lifetime.IsCancellationRequested )
			{
				// The steps keep their last state; the next check tries again.
			}
			finally
			{
				_context.Post( () => _isReadingTailscale = false );
			}
		} );
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
