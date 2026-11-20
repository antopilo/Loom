using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lokn.Core;
using Lokn.Modules.Remote;

namespace Lokn.Tests;

/// <summary>Checks the daemon's icon in the notification area: the status it shows, its menu, its drawing, and, in an interactive session, a real
/// bare daemon adding the icon, removing it when <c>remote.tray</c> turns off and when it is stopped.</summary>
/// <remarks>The process check uses a temporary bare folder only, never the user's daemon or data.</remarks>
internal static class HostTrayChecks
{
	/// <summary>Defines how long one step of the process check may take.</summary>
	private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds( 30 );

	/// <summary>Runs the headless checks: status computation, menu model, launch arguments and icon composition.</summary>
	internal static void Run()
	{
		CheckStatus();
		Console.WriteLine( "PASS host tray status: working, waiting, devices and errors give the running, needs-you and error states, their activity line and tooltip, pending pairings name the newest device, and an equal status publishes nothing." );
		CheckMenu();
		Console.WriteLine( "PASS host tray menu: Open Loom, the activity and connection lines, the error, the pairings waiting for an answer, Remote access, Start at login from the Run value only where allowed, and Stop daemon after a separator; the pairing notification shows once per device and only while Loom is not connected; --open-link is read from launch arguments." );
		if ( OperatingSystem.IsWindows() )
		{
			CheckIcon();
			Console.WriteLine( "PASS host tray icon: Loom's icon with a green, orange or red dot is an icon of the requested size at 16, 20, 24, 28, 32 and 48 pixels." );
		}
		else
			Console.WriteLine( "SKIP host tray icon: the icon is drawn on Windows only." );
	}

	/// <summary>Saves <c>remote.tray</c> in an application's Global settings, registering the setting under the Remote module's id the way it would.</summary>
	/// <param name="app">An application over the settings file a daemon will read, without the Remote module.</param>
	/// <param name="isShown">Whether the daemon shows its icon.</param>
	internal static void SaveTray( LoknApplication app, bool isShown )
	{
		if ( app.Modules.All( module => module.Id != "remote" ) )
			CoreChecks.Check( app.Load( new FixtureModule( "remote", builder => builder.Setting( RemoteModule.Tray ) ) ), "The tray setting registers. " + app.LastError );
		app.Settings.Set( RemoteModule.Tray, SettingsScope.Global, isShown );
	}

	/// <summary>Checks the status the Remote module computes and the texts it shows.</summary>
	private static void CheckStatus()
	{
		var running = HostStatusFeed.Compute( 2, 0, ["local", "phone", "phone", "laptop"], null, isTrayShown: true, canStartAtLogin: true );
		CoreChecks.Check( running is { Working: 2, Waiting: 0, Devices: 2, IsLocalConnected: true, Error: null, State: HostState.Running, IsTrayShown: true, CanStartAtLogin: true },
			"Two agents and two devices (one with two connections) plus Loom on this PC are running." );
		CoreChecks.Check( running.Activity == "2 agents working · 2 devices connected" && running.Connection == "Loom on this PC is connected"
			&& HostTrayMenu.BuildTip( running ) == "Loom: 2 agents working · 2 devices connected", $"The running texts read well: {running.Activity}" );

		var waiting = HostStatusFeed.Compute( 1, 1, ["phone"], "  ", isTrayShown: false, canStartAtLogin: false );
		CoreChecks.Check( waiting is { State: HostState.NeedsYou, Devices: 1, IsLocalConnected: false, Error: null, IsTrayShown: false }
			&& waiting.Activity == "1 agent working · 1 device connected · 1 waiting for you" && waiting.Connection == "Loom on this PC is not open",
			$"A chat waiting for the user needs you, a blank error is none, and the counts are singular: {waiting.Activity}" );

		var error = HostStatusFeed.Compute( 0, 3, [], "Tailscale is not running.", isTrayShown: true, canStartAtLogin: true );
		CoreChecks.Check( error is { State: HostState.Error, Devices: 0 } && error.Summary == "Tailscale is not running."
			&& HostTrayMenu.BuildTip( error ) == "Loom: Tailscale is not running." && error.Activity == "0 agents working · 0 devices connected · 3 waiting for you",
			"An error wins over a waiting chat and is what the tooltip says." );
		var longTip = HostTrayMenu.BuildTip( error with { Error = new string( 'x', 300 ) } );
		CoreChecks.Check( longTip.Length == HostTrayMenu.TipLength && longTip.EndsWith( '\u2026' ), "A long tooltip is cut to what Windows shows." );
		CoreChecks.Check( HostTrayMenu.BuildBalloon( waiting ) == "A chat is waiting for your answer. Click to open Loom."
			&& HostTrayMenu.BuildBalloon( error ).StartsWith( "3 chats are waiting", StringComparison.Ordinal ), "The notification counts the waiting chats." );

		var paired = DateTimeOffset.UtcNow;
		var pending = HostStatusFeed.Compute( 0, 0, [], null, isTrayShown: true, canStartAtLogin: false,
			[new( "older", "Phone", paired.AddMinutes( -1 ) ), new( "newer", "Tablet", paired )] );
		CoreChecks.Check( pending is { PendingPairing: "Tablet", PendingPairingId: "newer", PendingPairings: 2, State: HostState.Running }
			&& pending.PairingBalloonTitle == "Tablet paired with this PC" && pending.PairingBalloon == "It can view your chats. Open Loom to allow control."
			&& pending.PairingLine == "2 pairings waiting for you" && running is { PendingPairing: null, PendingPairings: 0, PairingLine: null },
			"Pairings waiting for an answer name the newest device and count them; none gives no line." );

		var app = new LoknApplication();
		var changes = 0;
		app.HostStatusChanged += () => changes++;
		app.PublishHostStatus( running );
		app.PublishHostStatus( running with { } );
		app.PublishHostStatus( waiting );
		app.PublishHostStatus( null );
		CoreChecks.Check( changes == 3 && app.HostStatus is null, "Publishing an equal status raises nothing; a new one and null each raise once." );
		app.Shutdown();
	}

	/// <summary>Checks the menu's items, their state, and reading the panel to open from launch arguments.</summary>
	private static void CheckMenu()
	{
		var status = new HostStatus( 1, 0, 2, true, null ) { CanStartAtLogin = true };
		var items = HostTrayMenu.Build( status, isStartAtLogin: true );
		CoreChecks.Check( items.Select( item => item.Command ).SequenceEqual( [HostTrayCommand.Open, HostTrayCommand.None, HostTrayCommand.None, HostTrayCommand.RemoteAccess,
			HostTrayCommand.StartAtLogin, HostTrayCommand.None, HostTrayCommand.Stop] ), "The menu lists Open Loom, two lines, Remote access, Start at login, a separator and Stop daemon." );
		CoreChecks.Check( items[0] is { Text: "Open Loom", IsEnabled: true } && items[1] is { IsEnabled: false } && items[1].Text == status.Activity
			&& items[2] is { IsEnabled: false } && items[2].Text == status.Connection && items[3].Text == "Remote access\u2026"
			&& items[4] is { Text: "Start at login", IsEnabled: true, IsChecked: true } && items[5].IsSeparator && items[6] is { Text: "Stop daemon", IsEnabled: true },
			"The information lines are disabled; Start at login is checked from the Run value." );
		CoreChecks.Check( HostTrayMenu.Build( status, isStartAtLogin: false )[4] is { IsEnabled: true, IsChecked: false }, "Without the Run value, Start at login is unchecked." );
		CoreChecks.Check( HostTrayMenu.Build( status with { CanStartAtLogin = false }, isStartAtLogin: true )[4] is { IsEnabled: false, IsChecked: false },
			"A bare daemon, or one whose chats run elsewhere, cannot change the sign-in entry." );
		var failing = HostTrayMenu.Build( status with { Error = "Port 47390 is in use." }, isStartAtLogin: false );
		CoreChecks.Check( failing.Count == 8 && failing[3] is { Command: HostTrayCommand.None, IsEnabled: false, Text: "Port 47390 is in use." },
			"An error adds a disabled line after the connection." );
		var waiting = HostTrayMenu.Build( status with { PendingPairing = "Tablet", PendingPairingId = "tablet", PendingPairings = 1 }, isStartAtLogin: false );
		CoreChecks.Check( waiting.Count == 8 && waiting[3] is { Command: HostTrayCommand.None, IsEnabled: false, Text: "1 pairing waiting for you" } && waiting[4].Command == HostTrayCommand.RemoteAccess,
			"A pairing waiting for an answer adds a disabled line before Remote access." );

		var shown = new HashSet<string>( StringComparer.Ordinal );
		var away = new HostStatus( 0, 0, 1, false, null ) { PendingPairing = "Tablet", PendingPairingId = "tablet", PendingPairings = 1 };
		var attached = away with { IsLocalConnected = true, PendingPairing = "Watch", PendingPairingId = "watch", PendingPairings = 2 };
		CoreChecks.Check( HostTrayMenu.ShouldShowPairing( away, shown ) && !HostTrayMenu.ShouldShowPairing( away, shown ) && !HostTrayMenu.ShouldShowPairing( away with { Devices = 2 }, shown ),
			"A device that paired while no Loom is connected is announced once." );
		CoreChecks.Check( !HostTrayMenu.ShouldShowPairing( attached, shown ) && !HostTrayMenu.ShouldShowPairing( attached with { IsLocalConnected = false }, shown )
			&& !HostTrayMenu.ShouldShowPairing( null, shown ) && !HostTrayMenu.ShouldShowPairing( new HostStatus( 0, 0, 0, false, null ), shown ),
			"A device first seen while Loom is connected is never announced, not even after Loom leaves, and no pending device announces nothing." );

		CoreChecks.Check( AttentionNotifier.ReadLink( ["--bare", @"C:\demo", AttentionNotifier.OpenLinkArgument, HostTrayMenu.RemoteAccessLink] ) == "lokn://remote/role"
			&& AttentionNotifier.ReadLink( ["--bare", @"C:\demo"] ) is null && AttentionNotifier.ReadLink( [AttentionNotifier.OpenLinkArgument] ) is null,
			"A launch asks for a link with --open-link and the link; without one it asks for none." );
	}

	/// <summary>Checks that the icon has the requested size, the state's dot colour at the dot's centre, and Loom's icon away from the dot.</summary>
	[SupportedOSPlatform( "windows" )]
	private static void CheckIcon()
	{
		var module = LoadLibraryExW( typeof( HostRunner ).Assembly.Location, 0, AsDataFile | AsImageResource );
		CoreChecks.Check( module != 0, "Loom's assembly loads as a resource file." );
		try
		{
			foreach ( var size in (int[])[16, 20, 24, 28, 32, 48] )
			{
				foreach ( var state in Enum.GetValues<HostState>() )
				{
					var icon = HostTrayIcon.Create( module, size, state );
					CoreChecks.Check( icon != 0, $"The {state} icon is created at {size} pixels." );
					try
					{
						var read = HostTrayIcon.ReadPixels( icon );
						CoreChecks.Check( read is { } shown && shown.Width == size && shown.Height == size, $"The {state} icon is {size} pixels square." );
						var pixels = read!.Value.Pixels;
						var (x, y, _) = HostTrayIcon.ComputeDot( size );
						var dot = pixels[(int)y * size + (int)x];
						CoreChecks.Check( dot == HostTrayIcon.ComputeColor( state ), $"The {state} icon at {size} pixels has its dot colour {HostTrayIcon.ComputeColor( state ):X8} at the dot's centre, not {dot:X8}." );
						var upper = pixels.Take( size * (size / 2) ).Count( pixel => pixel >> 24 > 0 );
						CoreChecks.Check( upper > size, $"The {state} icon at {size} pixels draws Loom's icon in its upper half." );
					}
					finally
					{
						HostTrayIcon.DestroyIcon( icon );
					}
				}
			}
		}
		finally
		{
			FreeLibrary( module );
		}
	}

	/// <summary>Starts a bare daemon with the tray on, checks its icon is in the notification area, turns <c>remote.tray</c> off and on in its settings
	/// file, stops it with <c>--stop</c> and checks the icon was removed and the process ended.</summary>
	/// <returns>False when the check was skipped: not Windows, or no interactive session with a taskbar.</returns>
	internal static bool RunDaemonIcon()
	{
		if ( !OperatingSystem.IsWindows() )
		{
			Console.WriteLine( "SKIP host tray daemon: the daemon runs on Windows only." );
			return false;
		}
		if ( !Environment.UserInteractive || FindTaskbar() == 0 )
		{
			Console.WriteLine( "SKIP host tray daemon: no interactive session with a taskbar." );
			return false;
		}

		CheckDaemonIcon();
		Console.WriteLine( "PASS host tray daemon: Loom.exe --host --bare adds its icon near the clock, removes and adds it as remote.tray changes, and removes it when --stop ends it." );
		return true;
	}

	/// <summary>Runs the bare daemon of <see cref="RunDaemonIcon"/> and checks its icon.</summary>
	[SupportedOSPlatform( "windows" )]
	private static void CheckDaemonIcon()
	{
		var loom = RemoteGatewayChecks.FindLoom() ?? throw new InvalidOperationException( "No built Loom.exe under the repository's bin folder or next to the checks; run scripts/build.ps1 first." );
		var bare = Path.Combine( Path.GetTempPath(), "lokn-tray-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( bare );
		var settings = new LoknApplication( Path.Combine( bare, "settings.json" ) );
		SaveTray( settings, isShown: true );

		using var daemon = RemoteGatewayChecks.StartLoom( loom, "--host", "--bare", bare );
		try
		{
			nint window = 0;
			CoreChecks.Check( WaitUntil( () => (window = FindTrayWindow( daemon.Id )) != 0 && CountLog( bare, "TRAY added" ) == 1 ),
				"The bare daemon creates its tray window and adds its icon. " + RemoteGatewayChecks.ReadLog( bare ) );
			CoreChecks.Check( WaitUntil( () => IsIconShown( window ) ), "The notification area knows the daemon's icon. " + RemoteGatewayChecks.ReadLog( bare ) );

			// The daemon follows its settings file, as it does when Loom on this PC saves the switch in Remote access.
			SaveTray( settings, isShown: false );
			CoreChecks.Check( WaitUntil( () => CountLog( bare, "TRAY removed" ) == 1 && !IsIconShown( window ) ),
				"Turning remote.tray off removes the icon while the daemon runs. " + RemoteGatewayChecks.ReadLog( bare ) );
			SaveTray( settings, isShown: true );
			CoreChecks.Check( WaitUntil( () => CountLog( bare, "TRAY added" ) == 2 && IsIconShown( window ) ),
				"Turning it on again adds the icon again. " + RemoteGatewayChecks.ReadLog( bare ) );

			using ( var stop = RemoteGatewayChecks.StartLoom( loom, "--host", "--stop", "--bare", bare ) )
				stop.WaitForExit( (int)StepTimeout.TotalMilliseconds );
			var hasExited = daemon.WaitForExit( (int)StepTimeout.TotalMilliseconds );
			CoreChecks.Check( hasExited, "Loom.exe --host --stop ends the bare daemon. " + RemoteGatewayChecks.ReadLog( bare ) );
			CoreChecks.Check( CountLog( bare, "TRAY removed" ) == 2 && !IsIconShown( window ), "Stopping the daemon removes its icon before it exits. " + RemoteGatewayChecks.ReadLog( bare ) );
		}
		finally
		{
			settings.Shutdown();
			if ( !daemon.HasExited )
			{
				using ( var stop = RemoteGatewayChecks.StartLoom( loom, "--host", "--stop", "--bare", bare ) )
					stop.WaitForExit( (int)StepTimeout.TotalMilliseconds );
				if ( !daemon.WaitForExit( (int)StepTimeout.TotalMilliseconds ) )
					daemon.Kill( entireProcessTree: true );
			}
			RemoteGatewayChecks.TryDelete( bare );
		}
	}

	/// <summary>Counts the lines of a bare daemon's log that end with a text.</summary>
	private static int CountLog( string bare, string text )
	{
		try
		{
			var path = Path.Combine( bare, "host.log" );
			if ( !File.Exists( path ) )
				return 0;

			using var stream = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete );
			using var reader = new StreamReader( stream );
			var count = 0;
			while ( reader.ReadLine() is { } line )
			{
				if ( line.EndsWith( text, StringComparison.Ordinal ) )
					count++;
			}
			return count;
		}
		catch ( IOException )
		{
			return 0;
		}
	}

	/// <summary>Polls a condition until it holds or a step's time is up.</summary>
	private static bool WaitUntil( Func<bool> condition )
	{
		var clock = Stopwatch.StartNew();
		while ( !condition() && clock.Elapsed < StepTimeout )
			Thread.Sleep( 100 );
		return condition();
	}

	/// <summary>Finds the taskbar, which an interactive desktop has.</summary>
	private static nint FindTaskbar() => FindWindowExW( 0, 0, "Shell_TrayWnd", null );

	/// <summary>Finds the daemon's hidden tray window among the top-level windows, by its class and process.</summary>
	private static nint FindTrayWindow( int processId )
	{
		for ( var window = FindWindowExW( 0, 0, "LoomHostTray", null ); window != 0; window = FindWindowExW( 0, window, "LoomHostTray", null ) )
		{
			GetWindowThreadProcessId( window, out var owner );
			if ( owner == processId )
				return window;
		}
		return 0;
	}

	/// <summary>Asks the notification area whether it holds the icon of a window.</summary>
	[SupportedOSPlatform( "windows" )]
	private static bool IsIconShown( nint window )
	{
		if ( window == 0 )
			return false;

		var identifier = new NotifyIconIdentifier { Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = window, Id = HostTray.IconId };
		return Shell_NotifyIconGetRect( ref identifier, out _ ) == 0;
	}

	/// <summary>Defines <c>LOAD_LIBRARY_AS_DATAFILE</c>.</summary>
	private const uint AsDataFile = 0x02;

	/// <summary>Defines <c>LOAD_LIBRARY_AS_IMAGE_RESOURCE</c>.</summary>
	private const uint AsImageResource = 0x20;

	/// <summary>Loads a module, here as a resource file only.</summary>
	[DllImport( "kernel32.dll", CharSet = CharSet.Unicode )]
	private static extern nint LoadLibraryExW( string path, nint file, uint flags );

	/// <summary>Unloads a module.</summary>
	[DllImport( "kernel32.dll" )]
	private static extern int FreeLibrary( nint module );

	/// <summary>Finds a top-level window by class after another.</summary>
	[DllImport( "user32.dll", CharSet = CharSet.Unicode )]
	private static extern nint FindWindowExW( nint parent, nint after, string className, string? title );

	/// <summary>Gets a window's thread and process.</summary>
	[DllImport( "user32.dll" )]
	private static extern uint GetWindowThreadProcessId( nint window, out uint processId );

	/// <summary>Gets where a notification-area icon shows; fails when the notification area does not hold it.</summary>
	[DllImport( "shell32.dll" )]
	private static extern int Shell_NotifyIconGetRect( ref NotifyIconIdentifier identifier, out Rect rect );

	/// <summary>Mirrors <c>NOTIFYICONIDENTIFIER</c>.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct NotifyIconIdentifier
	{
		/// <summary>The structure's size in bytes.</summary>
		public uint Size;

		/// <summary>The icon's window.</summary>
		public nint Window;

		/// <summary>The icon's identifier on its window.</summary>
		public uint Id;

		/// <summary>The icon's GUID, unused.</summary>
		public Guid Item;
	}

	/// <summary>Mirrors <c>RECT</c>.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct Rect
	{
		/// <summary>The left edge.</summary>
		public int Left;

		/// <summary>The top edge.</summary>
		public int Top;

		/// <summary>The right edge.</summary>
		public int Right;

		/// <summary>The bottom edge.</summary>
		public int Bottom;
	}
}
