using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>Gives agents this PC's desktop apps: list windows, open a program, take a screenshot of a window, and click, type and press keys in it.
/// Each call that works in a window tells the paired devices that may control this PC, which then stream that window (<see cref="FollowHostScreen"/>).</summary>
/// <remarks>The tools act on the real desktop of the signed-in user, as the user; only Windows has them. Coordinates are screenshot pixels: a click at
/// (x, y) lands where (x, y) was in the window's last screenshot, or in client pixels before any.</remarks>
public sealed partial class RemoteModule
{
	/// <summary>Defines the widest screenshot the tools take, in pixels.</summary>
	private const int ScreenshotWidth = 1600;

	/// <summary>Defines how long <c>desktop_open</c> waits for the program's window.</summary>
	private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds( 15 );

	/// <summary>Defines the virtual-key codes of the named keys <c>desktop_act</c> presses.</summary>
	private static readonly Dictionary<string, int> NamedKeys = new( StringComparer.OrdinalIgnoreCase )
	{
		["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09, ["escape"] = 0x1B, ["esc"] = 0x1B, ["backspace"] = 0x08, ["delete"] = 0x2E,
		["space"] = 0x20, ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27, ["home"] = 0x24, ["end"] = 0x23,
		["pageup"] = 0x21, ["pagedown"] = 0x22, ["insert"] = 0x2D, ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12,
		["win"] = 0x5B,
	};

	/// <summary>Stores each window's last screenshot size, which <c>desktop_act</c> coordinates are in.</summary>
	private readonly Dictionary<long, (int Width, int Height)> _shotSizes = [];

	/// <summary>Stores an input injector per window the agents act in.</summary>
	private readonly Dictionary<long, InputInjector> _injectors = [];

	/// <summary>Defines the optional window argument the window tools take.</summary>
	private const string WindowArgument = """
		"hwnd": { "type": "integer", "description": "The window id from remote_desktop_windows or remote_desktop_open." }
		""";

	/// <summary>Registers the desktop tools.</summary>
	private void RegisterDesktopTools( ModuleBuilder builder ) => builder
		.ToolAsync( "desktop_windows",
			"Lists the open app windows on the user's PC (the desktop Loom's host runs on): window id (hwnd), title, process and size. Use it before acting on an app that is already open.",
			"""{ "type": "object", "properties": {}, "additionalProperties": false }""",
			( _, _ ) => RunDesktopAsync( () => AgentToolResult.Json( WindowCatalog.List() ) ) )
		.ToolAsync( "desktop_open",
			"Starts a program or opens a file or address with its default app on the user's PC, waits for its window and returns it. Devices that control this PC see the window live.",
			"""
			{
				"type": "object",
				"properties": {
					"path": { "type": "string", "description": "A program such as notepad or C:\\Tools\\app.exe, or a file or address to open with its default app." },
					"args": { "type": "string", "description": "Command-line arguments for a program." }
				},
				"required": ["path"],
				"additionalProperties": false
			}
			""",
			( context, arguments ) => RunDesktopAsync( () => OpenDesktop( arguments, context.Cancellation ) ) )
		.ToolAsync( "desktop_screenshot",
			"Saves a JPEG screenshot of an app window on the user's PC and returns its path and size; read that image to see the window. Coordinates for remote_desktop_act are pixels of this image.",
			$$"""{ "type": "object", "properties": { {{WindowArgument}} }, "required": ["hwnd"], "additionalProperties": false }""",
			( context, arguments ) => RunDesktopAsync( () => ScreenshotDesktop( arguments, context.Cancellation ) ) )
		.ToolAsync( "desktop_act",
			"Clicks, scrolls, types or presses keys in an app window on the user's PC, bringing it to the front. x and y are pixels of the window's last remote_desktop_screenshot. Take a new screenshot to check the result.",
			$$"""
			{
				"type": "object",
				"properties": {
					{{WindowArgument}},
					"action": { "type": "string", "enum": ["click", "double_click", "right_click", "move", "scroll", "type", "key"] },
					"x": { "type": "number" },
					"y": { "type": "number" },
					"text": { "type": "string", "description": "For type: the text to type; a line break presses Enter." },
					"key": { "type": "string", "description": "For key: a key or chord such as enter, ctrl+s, alt+f4, shift+tab or a single letter." },
					"amount": { "type": "integer", "description": "For scroll: notches, positive scrolls up. Defaults to -3." }
				},
				"required": ["hwnd", "action"],
				"additionalProperties": false
			}
			""",
			( context, arguments ) => RunDesktopAsync( () => ActDesktop( arguments ) ) );

	/// <summary>Captures one region of Loom's own window without opening a stream, including native child surfaces such as browser pages.</summary>
	private static async Task<PanelCaptureImage?> CapturePanelAsync( Window window, PanelCaptureRegion region, CancellationToken cancellation )
	{
		if ( !OperatingSystem.IsWindows() )
			return null;

		var lease = await window.AcquireNativeDialogOwnerAsync( cancellation );
		if ( lease.Owner is not { } owner )
			throw new InvalidOperationException( "Windows did not provide Loom's native window for capture." );
		var handle = owner.Handle;
		owner.Dispose();

		var frame = await Task.Run( () => CapturePanelFrame( handle, cancellation ), cancellation );
		var (x, y, width, height) = region.ToPixels( frame.Width, frame.Height );
		var pixels = new byte[checked(width * height * 4)];
		var sourceStride = checked(frame.Width * 4);
		var targetStride = checked(width * 4);
		for ( var row = 0; row < height; row++ )
			Buffer.BlockCopy( frame.Pixels, (y + row) * sourceStride + x * 4, pixels, row * targetStride, targetStride );

		var image = await JpegEncoder.EncodeAsync( new( width, height, pixels ), 0.9, cancellation );
		return new( ".jpg", width, height, image );
	}

	/// <summary>Waits for the first compositor capture of Loom's native window.</summary>
	private static BgraFrame CapturePanelFrame( nint window, CancellationToken cancellation )
	{
		using var capturer = WindowCapturers.Open( window );
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds( 1 );
		BgraFrame? frame;
		while ( (frame = capturer.Capture( 1920 )) is null && DateTime.UtcNow < deadline )
		{
			cancellation.ThrowIfCancellationRequested();
			Thread.Sleep( 15 );
		}

		return frame ?? throw new InvalidOperationException( "Loom's window has nothing to capture now." );
	}

	/// <summary>Runs a desktop tool off the UI thread, refusing it on platforms without the desktop.</summary>
	private static Task<AgentToolResult> RunDesktopAsync( Func<AgentToolResult> run ) => OperatingSystem.IsWindows()
		? Task.Run( run )
		: Task.FromResult( AgentToolResult.Error( "The desktop tools need Windows." ) );

	/// <summary>Starts a program and waits for a new window, preferring one of the program's process.</summary>
	private AgentToolResult OpenDesktop( JsonElement arguments, CancellationToken cancellation )
	{
		var path = ReadString( arguments, "path" ) ?? throw new ArgumentException( "path is required." );
		var before = WindowCatalog.List().Select( window => window.Hwnd ).ToHashSet();
		using var process = Process.Start( new ProcessStartInfo( path, ReadString( arguments, "args" ) ?? "" ) { UseShellExecute = true } );
		var name = process is null ? null : TryReadName( process );
		var deadline = Stopwatch.StartNew();
		while ( deadline.Elapsed < OpenTimeout )
		{
			cancellation.ThrowIfCancellationRequested();
			var fresh = WindowCatalog.List().Where( window => !before.Contains( window.Hwnd ) ).ToArray();
			if ( (fresh.FirstOrDefault( window => name is not null && window.Process.Equals( name, StringComparison.OrdinalIgnoreCase ) ) ?? fresh.FirstOrDefault()) is { } found )
			{
				ShowToDevices( found );
				return AgentToolResult.Json( found );
			}
			Thread.Sleep( 250 );
		}
		return AgentToolResult.Error( $"Started {path}, but no new window showed within {OpenTimeout.TotalSeconds:0} seconds. It may have reused a window that was open; see remote_desktop_windows." );
	}

	/// <summary>Captures a window, saves it as JPEG and remembers its size for the coordinates of later actions.</summary>
	private AgentToolResult ScreenshotDesktop( JsonElement arguments, CancellationToken cancellation )
	{
		var window = FindWindow( arguments );
		using var capturer = WindowCapturers.Open( (nint)window.Hwnd );
		BgraFrame? frame = null;
		// A new capturer may need a moment before its first image.
		for ( var attempt = 0; attempt < 20 && frame is null; attempt++ )
		{
			frame = capturer.Capture( ScreenshotWidth );
			if ( frame is null )
				Thread.Sleep( 100 );
		}
		if ( frame is null )
			return AgentToolResult.Error( "The window shows nothing to capture; it may be minimized." );

		var bytes = JpegEncoder.EncodeAsync( frame, CaptureOptions.Default.Quality, cancellation ).GetAwaiter().GetResult();
		var folder = Path.Combine( Path.GetTempPath(), "Lokn", "desktop" );
		Directory.CreateDirectory( folder );
		var path = Path.Combine( folder, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{window.Hwnd:X}.jpg" );
		File.WriteAllBytes( path, bytes );
		lock ( _shotSizes )
			_shotSizes[window.Hwnd] = (frame.Width, frame.Height);
		ShowToDevices( window );
		return new( $"Saved a {frame.Width}x{frame.Height} screenshot of \"{window.Title}\" to {path}. Read that file to see it." );
	}

	/// <summary>Sends one action to a window.</summary>
	private AgentToolResult ActDesktop( JsonElement arguments )
	{
		var window = FindWindow( arguments );
		var action = ReadString( arguments, "action" ) ?? throw new ArgumentException( "action is required." );
		InputInjector injector;
		(int Width, int Height) size;
		lock ( _shotSizes )
		{
			if ( !_injectors.TryGetValue( window.Hwnd, out injector! ) )
				_injectors[window.Hwnd] = injector = new( (nint)window.Hwnd );
			size = _shotSizes.TryGetValue( window.Hwnd, out var shot ) ? shot : (window.Width, window.Height);
		}

		ShowToDevices( window );
		var x = ReadNumber( arguments, "x" );
		var y = ReadNumber( arguments, "y" );
		bool Point( string kind, int button = 0, int delta = 0 ) =>
			injector.Send( new StreamInput( kind, x ?? throw new ArgumentException( "x is required." ), y ?? throw new ArgumentException( "y is required." ), button, delta ), size.Width, size.Height );
		bool Click( int button ) => Point( StreamInput.Move ) && Point( StreamInput.Down, button ) && Point( StreamInput.Up, button );

		var isSent = action switch
		{
			"click" => Click( 0 ),
			"right_click" => Click( 2 ),
			"double_click" => Click( 0 ) && Click( 0 ),
			"move" => Point( StreamInput.Move ),
			"scroll" => Point( StreamInput.Move ) && Point( StreamInput.Wheel, delta: 120 * (arguments.TryGetProperty( "amount", out var amount ) && amount.ValueKind == JsonValueKind.Number ? amount.GetInt32() : -3) ),
			"type" => injector.SendText( ReadString( arguments, "text" ) ?? throw new ArgumentException( "text is required." ) ),
			"key" => PressChord( injector, ReadString( arguments, "key" ) ?? throw new ArgumentException( "key is required." ), size ),
			_ => throw new ArgumentException( $"Unknown action \"{action}\"." ),
		};
		return isSent
			? new( $"Done: {action} in \"{window.Title}\"." )
			: AgentToolResult.Error( $"Windows did not take the {action}: the window may be closed, or another window, such as one run as administrator, kept the focus." );
	}

	/// <summary>Presses a chord such as <c>ctrl+s</c>: modifiers down in order, the key, then everything up in reverse.</summary>
	private static bool PressChord( InputInjector injector, string chord, (int Width, int Height) size )
	{
		var keys = new List<int>();
		foreach ( var part in chord.Split( '+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries ) )
		{
			if ( NamedKeys.TryGetValue( part, out var named ) )
				keys.Add( named );
			else if ( part.Length > 1 && (part[0] is 'f' or 'F') && int.TryParse( part[1..], out var function ) && function is >= 1 and <= 24 )
				keys.Add( 0x6F + function );
			else if ( part.Length == 1 && char.IsAsciiLetterOrDigit( part[0] ) )
				keys.Add( char.ToUpperInvariant( part[0] ) );
			else
				throw new ArgumentException( $"Unknown key \"{part}\"." );
		}

		var isSent = true;
		foreach ( var key in keys )
			isSent &= injector.Send( new StreamInput( StreamInput.Key, Vk: key, IsDown: true ), size.Width, size.Height );
		for ( var index = keys.Count - 1; index >= 0; index-- )
			isSent &= injector.Send( new StreamInput( StreamInput.Key, Vk: keys[index], IsDown: false ), size.Width, size.Height );
		return isSent;
	}

	/// <summary>Finds the window the arguments name among the open ones.</summary>
	private static CaptureWindow FindWindow( JsonElement arguments )
	{
		if ( !arguments.TryGetProperty( "hwnd", out var value ) || !value.TryGetInt64( out var hwnd ) )
			throw new ArgumentException( "hwnd is required." );

		return WindowCatalog.List().FirstOrDefault( window => window.Hwnd == hwnd )
			?? throw new InvalidOperationException( "No open window has that id; list them again with remote_desktop_windows." );
	}

	/// <summary>Tells the devices that may control this PC to stream a window, when this process serves devices.</summary>
	private void ShowToDevices( CaptureWindow window )
	{
		if ( Gateway is { } gateway )
			Context.Post( () => gateway.ShowWindow( window ) );
	}

	/// <summary>Follows a paired host's <c>cap.show</c>: streams the window in a stream panel on that host, bringing forward the one that streams it
	/// already, unless the setting is off or this device may only view.</summary>
	private void FollowWindow( RemoteClient link, JsonObject frame )
	{
		var app = Context.App;
		if ( !app.Settings.Get( FollowHostScreen, SettingsScope.Global ) || link.Stream is not { CanControl: true } || Frame.ReadLong( frame, "hwnd" ) is not { } hwnd )
			return;

		StreamPanel.Show( app, link.Host.Id, null, hwnd, Frame.ReadString( frame, "title" ) ?? "" );
	}

	/// <summary>Reads a process's name, or null once it exited or is protected.</summary>
	private static string? TryReadName( Process process )
	{
		try
		{
			return process.ProcessName;
		}
		catch ( Exception error ) when ( error is InvalidOperationException or System.ComponentModel.Win32Exception )
		{
			return null;
		}
	}

	/// <summary>Reads a string argument, or null when it is missing or empty.</summary>
	private static string? ReadString( JsonElement arguments, string name ) =>
		arguments.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

	/// <summary>Reads a number argument, or null when it is missing.</summary>
	private static double? ReadNumber( JsonElement arguments, string name ) =>
		arguments.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}
