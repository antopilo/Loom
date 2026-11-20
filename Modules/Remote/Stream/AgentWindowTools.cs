using System.Collections.Concurrent;
using System.Text.Json;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>Checks' replacements for the window list, capture, encoding and input of <see cref="AgentWindowTools"/>; null members use this PC's own.</summary>
internal sealed record AgentWindowHooks
{
	/// <summary>Gets the window lister, or null for <see cref="WindowCatalog.List"/>.</summary>
	public Func<IReadOnlyList<CaptureWindow>>? ListWindows
	{
		get; init;
	}

	/// <summary>Gets the capturer factory by window handle, or null for <see cref="WindowCapturers.Open"/>.</summary>
	public Func<long, IWindowCapturer>? CreateCapturer
	{
		get; init;
	}

	/// <summary>Gets the image encoder, or null for <see cref="JpegEncoder.EncodeAsync"/>.</summary>
	public Func<BgraFrame, CancellationToken, Task<byte[]>>? Encode
	{
		get; init;
	}

	/// <summary>Gets the pointer and key sink by window handle, event and capture size, answering whether the input went through; null uses an <see cref="InputInjector"/>.</summary>
	public Func<long, StreamInput, int, int, bool>? Inject
	{
		get; init;
	}

	/// <summary>Gets the text sink by window handle, answering whether the text went through; null uses <see cref="InputInjector.SendText"/>.</summary>
	public Func<long, string, bool>? Type
	{
		get; init;
	}
}

/// <summary>Offers agents this PC's windows: <c>window_list</c>, <c>window_show</c>, <c>window_capture</c>, <c>window_click</c>, <c>window_type</c>,
/// <c>window_key</c> and <c>window_scroll</c> (docs/remote.md, Host tools).</summary>
/// <remarks>
/// The tools are offered only while <see cref="Publish"/> says so: in the daemon and in a local-role Loom, where chats' agents run, while the user lets
/// them (<see cref="RemoteModule.AgentWindows"/>). Every tool but <c>window_list</c> shows the window to the user in a stream panel beside the chat. Input
/// coordinates are pixels of the last capture of the window, which is at most <see cref="CaptureWidth"/> wide; each input tool returns a fresh capture.
/// Handlers start on the UI thread, which showing the panel needs.
/// </remarks>
internal sealed class AgentWindowTools : IDisposable
{
	/// <summary>Names the tool that lists windows.</summary>
	internal const string ListTool = "window_list";

	/// <summary>Names the tool that shows a window to the user.</summary>
	internal const string ShowTool = "window_show";

	/// <summary>Names the tool that captures a window.</summary>
	internal const string CaptureTool = "window_capture";

	/// <summary>Names the tool that clicks in a window.</summary>
	internal const string ClickTool = "window_click";

	/// <summary>Names the tool that types into a window.</summary>
	internal const string TypeTool = "window_type";

	/// <summary>Names the tool that presses a key in a window.</summary>
	internal const string KeyTool = "window_key";

	/// <summary>Names the tool that scrolls in a window.</summary>
	internal const string ScrollTool = "window_scroll";

	/// <summary>Defines the widest capture, in pixels; wider windows are scaled down to it, and input coordinates follow.</summary>
	internal const int CaptureWidth = 1280;

	/// <summary>Defines the JPEG quality of captures.</summary>
	private const double CaptureQuality = 0.8;

	/// <summary>Defines how long a capture waits for the window's first frame: Windows.Graphics.Capture delivers it a few milliseconds after it starts.</summary>
	private static readonly TimeSpan FirstFrameWait = TimeSpan.FromSeconds( 1 );

	/// <summary>Defines how often a capture asks for the first frame while it waits.</summary>
	private static readonly TimeSpan FirstFramePoll = TimeSpan.FromMilliseconds( 15 );

	/// <summary>Defines the Windows wheel units of one notch.</summary>
	private const int WheelNotch = 120;

	/// <summary>Defines how many windows an error lists.</summary>
	private const int MaxListed = 25;

	/// <summary>Defines how long an input tool waits for the window to react before its capture.</summary>
	internal static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds( 150 );

	/// <summary>Holds the virtual keys of the key names <c>window_key</c> takes besides letters, digits and F1 to F24.</summary>
	private static readonly Dictionary<string, int> NamedKeys = new( StringComparer.OrdinalIgnoreCase )
	{
		["Enter"] = 0x0D, ["Return"] = 0x0D, ["Tab"] = 0x09, ["Escape"] = 0x1B, ["Esc"] = 0x1B, ["Backspace"] = 0x08, ["Space"] = 0x20,
		["Delete"] = 0x2E, ["Del"] = 0x2E, ["Insert"] = 0x2D, ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
		["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
	};

	/// <summary>Holds the virtual keys of the modifiers <c>window_key</c> takes.</summary>
	private static readonly Dictionary<string, int> Modifiers = new( StringComparer.OrdinalIgnoreCase )
	{
		["Ctrl"] = 0x11, ["Control"] = 0x11, ["Shift"] = 0x10, ["Alt"] = 0x12,
	};

	/// <summary>Defines the window argument every tool but <c>window_list</c> takes.</summary>
	private const string WindowArgument = """
		"window": { "type": ["string", "integer"], "description": "The window: its id from window_list, or part of its title or process name (case-insensitive) that names one window." }
		""";

	/// <summary>Defines the point arguments of the pointer tools.</summary>
	private const string PointArguments = """
		"x": { "type": "number", "description": "Pixels from the left edge of the window's last capture (window_capture's image, or the one the last input tool returned)." },
		"y": { "type": "number", "description": "Pixels from the top edge of the window's last capture." }
		""";

	/// <summary>Stores the application.</summary>
	private readonly LoknApplication _app;

	/// <summary>Stores the checks' replacements, or null.</summary>
	private readonly AgentWindowHooks? _hooks;

	/// <summary>Stores the folder captures are saved in.</summary>
	private readonly string _screenshots;

	/// <summary>Shows a window to the user beside the calling agent's chat, answering whether a Loom window shows it; on the UI thread.</summary>
	private readonly Func<AgentToolContext, CaptureWindow, bool> _show;

	/// <summary>Tells the window's viewers what the agent did.</summary>
	private readonly Action<AgentWindowUse> _notify;

	/// <summary>Holds the size of each window's last capture, which input coordinates refer to.</summary>
	private readonly ConcurrentDictionary<long, (int Width, int Height)> _sizes = new();

	/// <summary>Holds the injector of each window an agent sent input to.</summary>
	private readonly ConcurrentDictionary<long, InputInjector> _injectors = new();

	/// <summary>Stores the offer of the tools while they are published, or null.</summary>
	private IDisposable? _offer;

	/// <summary>Creates the tools, unpublished.</summary>
	/// <param name="app">The application.</param>
	/// <param name="hooks">The checks' replacements, or null.</param>
	/// <param name="screenshots">The folder captures are saved in.</param>
	/// <param name="show">Shows a window beside the calling agent's chat, answering whether a Loom window shows it; on the UI thread.</param>
	/// <param name="notify">Tells the window's viewers what the agent did.</param>
	internal AgentWindowTools( LoknApplication app, AgentWindowHooks? hooks, string screenshots, Func<AgentToolContext, CaptureWindow, bool> show, Action<AgentWindowUse> notify )
	{
		_app = app;
		_hooks = hooks;
		_screenshots = screenshots;
		_show = show;
		_notify = notify;
	}

	/// <summary>Gets whether the tools are offered to agents.</summary>
	internal bool IsPublished => _offer is not null;

	/// <summary>Offers the tools to agents or withdraws them; on the UI thread.</summary>
	/// <param name="isOn">Whether agents may use this PC's windows here.</param>
	internal void Publish( bool isOn )
	{
		if ( isOn && _offer is null )
			_offer = _app.Tools.Offer( BuildInfos(), ExecuteAsync );
		else if ( !isOn && _offer is { } offer )
		{
			_offer = null;
			offer.Dispose();
		}
	}

	/// <summary>Withdraws the tools; on the UI thread.</summary>
	public void Dispose() => Publish( false );

	/// <summary>Finds the window an agent named: its id, or the one window whose title or process contains the text, preferring an exact title.</summary>
	/// <param name="windows">The windows to choose from.</param>
	/// <param name="text">An id from <c>window_list</c>, or part of a title or process name.</param>
	/// <exception cref="KeyNotFoundException">No window matches.</exception>
	/// <exception cref="ArgumentException">Several windows match, or the text is empty.</exception>
	internal static CaptureWindow Match( IReadOnlyList<CaptureWindow> windows, string text )
	{
		text = text.Trim();
		if ( text.Length == 0 )
			throw new ArgumentException( "Pass window: an id from window_list, or part of a window's title or process name." );

		if ( long.TryParse( text, out var hwnd ) && windows.FirstOrDefault( window => window.Hwnd == hwnd ) is { } byId )
			return byId;

		CaptureWindow[] found = [.. windows.Where( window => window.Title.Contains( text, StringComparison.OrdinalIgnoreCase ) || window.Process.Contains( text, StringComparison.OrdinalIgnoreCase ) )];
		if ( found.Length > 1 && found.Where( window => window.Title.Equals( text, StringComparison.OrdinalIgnoreCase ) ).ToArray() is [var exact] )
			return exact;

		return found switch
		{
			[var one] => one,
			[] => throw new KeyNotFoundException( $"No window matches \"{text}\". Open windows: {Describe( windows )}" ),
			_ => throw new ArgumentException( $"\"{text}\" matches {found.Length} windows; pass the id of one: {Describe( found )}" ),
		};
	}

	/// <summary>Reads a key name and its modifiers, such as <c>Ctrl+S</c>, into virtual keys.</summary>
	/// <param name="key">The key: Enter, Tab, Escape, Backspace, Delete, Insert, Home, End, PageUp, PageDown, arrows, Space, F1 to F24, A to Z, 0 to 9;
	/// it may start with modifiers joined by <c>+</c>.</param>
	/// <param name="modifiers">More modifiers: Ctrl, Shift or Alt.</param>
	/// <exception cref="ArgumentException">A name is unknown.</exception>
	internal static (int Key, int[] Modifiers) ParseKey( string key, IEnumerable<string> modifiers )
	{
		var parts = key.Split( '+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length == 0 )
			throw new ArgumentException( "Pass key, such as Enter, Tab, Escape, F5 or A." );

		var held = new List<int>();
		foreach ( var name in parts[..^1].Concat( modifiers ) )
		{
			var modifier = Modifiers.TryGetValue( name.Trim(), out var code ) ? code : throw new ArgumentException( $"Unknown modifier \"{name}\"; use Ctrl, Shift or Alt." );
			if ( !held.Contains( modifier ) )
				held.Add( modifier );
		}

		var last = parts[^1];
		var virtualKey = NamedKeys.TryGetValue( last, out var named ) ? named
			: last.Length == 1 && char.IsAsciiLetterOrDigit( last[0] ) ? char.ToUpperInvariant( last[0] )
			: last.Length is 2 or 3 && last[0] is 'F' or 'f' && int.TryParse( last[1..], out var function ) && function is >= 1 and <= 24 ? 0x6F + function
			: throw new ArgumentException( $"Unknown key \"{last}\"; use a letter, a digit, F1 to F24, or one of {string.Join( ", ", NamedKeys.Keys )}." );
		return (virtualKey, [.. held]);
	}

	/// <summary>Builds the tools' descriptions and schemas.</summary>
	private static AgentToolInfo[] BuildInfos() =>
	[
		BuildInfo( ListTool,
			"Lists the visible windows of the PC your chat runs on: id, title, process and client size in pixels. Pass an id, or part of a title, as window to the other window_ tools.",
			"""{ "type": "object", "properties": {}, "additionalProperties": false }""" ),
		BuildInfo( ShowTool,
			"Shows a window of this PC to the user live, in a Window stream panel beside your conversation, without taking their focus. The other window_ tools show it too.",
			$$"""{ "type": "object", "properties": { {{WindowArgument}} }, "required": ["window"], "additionalProperties": false }""" ),
		BuildInfo( CaptureTool,
			$"Captures a window of this PC as an image at most {CaptureWidth} pixels wide, saves it and returns its path; read that file to see the window. window_click and window_scroll take x and y in pixels of the window's last capture. Also shows the window to the user.",
			$$"""{ "type": "object", "properties": { {{WindowArgument}} }, "required": ["window"], "additionalProperties": false }""" ),
		BuildInfo( ClickTool,
			"Clicks in a window of this PC with the real mouse, bringing the window to the front, then returns a fresh capture. x and y are pixels of the window's last capture (call window_capture first).",
			$$"""
			{
				"type": "object",
				"properties": {
					{{WindowArgument}},
					{{PointArguments}},
					"button": { "type": "string", "enum": ["left", "right", "middle"], "description": "Defaults to left." },
					"double": { "type": "boolean", "description": "Double-clicks." }
				},
				"required": ["window", "x", "y"],
				"additionalProperties": false
			}
			""" ),
		BuildInfo( TypeTool,
			"Types text into a window of this PC where its keyboard focus is, as real keyboard input, bringing the window to the front, then returns a fresh capture. A line break presses Enter; click a field first to focus it.",
			$$"""{ "type": "object", "properties": { {{WindowArgument}}, "text": { "type": "string" } }, "required": ["window", "text"], "additionalProperties": false }""" ),
		BuildInfo( KeyTool,
			"Presses a key in a window of this PC, with modifiers held, bringing the window to the front, then returns a fresh capture.",
			$$"""
			{
				"type": "object",
				"properties": {
					{{WindowArgument}},
					"key": { "type": "string", "description": "Enter, Tab, Escape, Backspace, Delete, Insert, Home, End, PageUp, PageDown, Left, Up, Right, Down, Space, F1 to F24, A to Z or 0 to 9; may start with modifiers, such as Ctrl+S." },
					"modifiers": { "type": "array", "items": { "type": "string", "enum": ["Ctrl", "Shift", "Alt"] }, "description": "Keys held while it is pressed." }
				},
				"required": ["window", "key"],
				"additionalProperties": false
			}
			""" ),
		BuildInfo( ScrollTool,
			"Turns the mouse wheel over a point of a window of this PC, bringing the window to the front, then returns a fresh capture. x and y are pixels of the window's last capture.",
			$$"""
			{
				"type": "object",
				"properties": {
					{{WindowArgument}},
					{{PointArguments}},
					"delta": { "type": "integer", "description": "Wheel notches: positive scrolls up, negative scrolls down; about three lines each." }
				},
				"required": ["window", "x", "y", "delta"],
				"additionalProperties": false
			}
			""" ),
	];

	/// <summary>Builds one tool's metadata.</summary>
	private static AgentToolInfo BuildInfo( string name, string description, string parameters )
	{
		using var schema = JsonDocument.Parse( parameters );
		return new( name, description, schema.RootElement.Clone(), "remote" );
	}

	/// <summary>Runs one call of the tools.</summary>
	private Task<AgentToolResult> ExecuteAsync( AgentToolContext context, string name, JsonElement arguments ) => name switch
	{
		ListTool => Task.FromResult( List() ),
		ShowTool => Task.FromResult( Show( context, arguments ) ),
		CaptureTool => CaptureAsync( context, arguments ),
		ClickTool => ClickAsync( context, arguments ),
		TypeTool => TypeAsync( context, arguments ),
		KeyTool => PressAsync( context, arguments ),
		ScrollTool => ScrollAsync( context, arguments ),
		_ => throw new KeyNotFoundException( $"No window tool is named {name}." ),
	};

	/// <summary>Lists the windows.</summary>
	private AgentToolResult List()
	{
		var windows = ReadWindows();
		return windows.Count == 0
			? new( "This PC shows no windows." )
			: AgentToolResult.Json( windows.Select( window => new { Id = window.Hwnd, window.Title, window.Process, window.Width, window.Height } ) );
	}

	/// <summary>Shows a window to the user.</summary>
	private AgentToolResult Show( AgentToolContext context, JsonElement arguments )
	{
		var window = Resolve( arguments );
		return new( $"{DescribeShown( context, window )}" );
	}

	/// <summary>Captures a window, shows it to the user and saves the capture.</summary>
	private async Task<AgentToolResult> CaptureAsync( AgentToolContext context, JsonElement arguments )
	{
		var window = Resolve( arguments );
		var shown = DescribeShown( context, window );
		_notify( new( window.Hwnd, AgentWindowUse.Capture ) );
		var (path, width, height) = await SaveCaptureAsync( window, context.Cancellation );
		return new( $"Saved a {width}×{height} capture of \"{window.Title}\" (id {window.Hwnd}) to {path}. Read that file to see it; window_click and window_scroll take x and y in its pixels. {shown}" );
	}

	/// <summary>Clicks a point of a window.</summary>
	private Task<AgentToolResult> ClickAsync( AgentToolContext context, JsonElement arguments )
	{
		var button = ReadString( arguments, "button" )?.ToLowerInvariant() switch
		{
			null or "left" => 0,
			"middle" => 1,
			"right" => 2,
			var other => throw new ArgumentException( $"Unknown button \"{other}\"; use left, right or middle." ),
		};
		var isDouble = arguments.TryGetProperty( "double", out var flag ) && flag.ValueKind == JsonValueKind.True;
		return ActAsync( context, arguments, AgentWindowUse.Click, ( window, size ) =>
		{
			var (x, y) = ReadPoint( arguments, size );
			var isSent = Send( window, new( StreamInput.Move, x, y ), size );
			for ( var click = 0; click < (isDouble ? 2 : 1) && isSent; click++ )
				isSent = Send( window, new( StreamInput.Down, x, y, Button: button ), size ) && Send( window, new( StreamInput.Up, x, y, Button: button ), size );
			var name = button switch { 1 => "middle", 2 => "right", _ => "left" };
			return (isSent, x, y, $"{(isDouble ? "Double-clicked" : "Clicked")} {name} at {x:0}, {y:0}");
		} );
	}

	/// <summary>Types text into a window.</summary>
	private Task<AgentToolResult> TypeAsync( AgentToolContext context, JsonElement arguments )
	{
		var text = ReadString( arguments, "text" ) is { Length: > 0 } typed ? typed : throw new ArgumentException( "Pass the text to type." );
		return ActAsync( context, arguments, AgentWindowUse.Type, ( window, _ ) =>
		{
			var isSent = _hooks?.Type is { } type ? type( window.Hwnd, text ) : FindInjector( window ).SendText( text );
			return (isSent, -1, -1, $"Typed {text.Length} characters");
		} );
	}

	/// <summary>Presses a key, with modifiers held, in a window.</summary>
	private Task<AgentToolResult> PressAsync( AgentToolContext context, JsonElement arguments )
	{
		var name = ReadString( arguments, "key" ) ?? throw new ArgumentException( "Pass key, such as Enter, Tab, Escape, F5 or A." );
		var (key, modifiers) = ParseKey( name, ReadModifiers( arguments ) );
		return ActAsync( context, arguments, AgentWindowUse.Key, ( window, size ) =>
		{
			var isSent = true;
			var pressed = new List<int>();
			foreach ( var virtualKey in modifiers.Append( key ) )
			{
				isSent = Send( window, new( StreamInput.Key, Vk: virtualKey, IsDown: true ), size );
				if ( !isSent )
					break;

				pressed.Add( virtualKey );
			}
			// Whatever went down comes up again, so no modifier stays held on the PC.
			for ( var index = pressed.Count - 1; index >= 0; index-- )
				isSent &= Send( window, new( StreamInput.Key, Vk: pressed[index], IsDown: false ), size );
			return (isSent, -1, -1, $"Pressed {name}");
		} );
	}

	/// <summary>Turns the wheel over a point of a window.</summary>
	private Task<AgentToolResult> ScrollAsync( AgentToolContext context, JsonElement arguments )
	{
		var notches = arguments.TryGetProperty( "delta", out var delta ) && delta.ValueKind == JsonValueKind.Number && delta.TryGetInt32( out var value ) && value != 0
			? Math.Clamp( value, -50, 50 )
			: throw new ArgumentException( "Pass delta: wheel notches, positive to scroll up and negative to scroll down." );
		return ActAsync( context, arguments, AgentWindowUse.Scroll, ( window, size ) =>
		{
			var (x, y) = ReadPoint( arguments, size );
			var isSent = Send( window, new( StreamInput.Move, x, y ), size ) && Send( window, new( StreamInput.Wheel, x, y, Delta: notches * WheelNotch ), size );
			return (isSent, x, y, $"Scrolled {Math.Abs( notches )} notches {(notches > 0 ? "up" : "down")} at {x:0}, {y:0}");
		} );
	}

	/// <summary>Runs an input action in a window an agent named: shows it, refuses a window that is gone or elevated, sends the input in the coordinates
	/// of its last capture, tells its viewers, waits for it to react and returns a fresh capture.</summary>
	/// <param name="context">The call.</param>
	/// <param name="arguments">The call's arguments.</param>
	/// <param name="kind">The <see cref="AgentWindowUse"/> kind viewers see.</param>
	/// <param name="act">Sends the input at the capture size: whether it went through, its point in capture pixels or -1, and what it did.</param>
	private async Task<AgentToolResult> ActAsync( AgentToolContext context, JsonElement arguments, string kind, Func<CaptureWindow, (int Width, int Height), (bool IsSent, double X, double Y, string Done)> act )
	{
		var window = Resolve( arguments );
		var shown = DescribeShown( context, window );
		EnsureControllable( window );
		var size = ReadSize( window );
		var (isSent, x, y, done) = act( window, size );
		if ( !isSent )
			throw new InvalidOperationException( $"Windows did not let Loom send input to \"{window.Title}\": it is closed, or could not be brought to the front. Call window_capture to check it." );

		_notify( x >= 0 ? new( window.Hwnd, kind, x / Math.Max( 1, size.Width ), y / Math.Max( 1, size.Height ) ) : new( window.Hwnd, kind ) );
		await Task.Delay( SettleDelay, context.Cancellation );
		var (path, width, height) = await SaveCaptureAsync( window, context.Cancellation );
		return new( $"{done} in \"{window.Title}\". Saved a {width}×{height} capture of the result to {path}; read it to see the window now. {shown}" );
	}

	/// <summary>Shows a window to the user and says whether they see it.</summary>
	private string DescribeShown( AgentToolContext context, CaptureWindow window ) => _show( context, window )
		? "The user sees the window live in a Window stream panel."
		: "No Loom window shows your conversation now, so the user does not see the window.";

	/// <summary>Refuses input into a window that is gone or that Windows protects from this process.</summary>
	/// <exception cref="InvalidOperationException">The window is closed or runs elevated.</exception>
	private void EnsureControllable( CaptureWindow window )
	{
		if ( _hooks?.Inject is not null )
			return;

		if ( !OperatingSystem.IsWindows() )
			throw new InvalidOperationException( "Using windows needs Windows." );
		if ( !StreamNative.IsWindow( (nint)window.Hwnd ) )
			throw new InvalidOperationException( $"\"{window.Title}\" was closed; call window_list." );
		if ( InputInjector.IsRefused( (nint)window.Hwnd ) )
			throw new InvalidOperationException( $"\"{window.Title}\" runs as administrator, and Windows refuses input into it from Loom, which does not. Ask the user to use it, or to run Loom as administrator." );
	}

	/// <summary>Sends one pointer or key event in capture pixels.</summary>
	private bool Send( CaptureWindow window, StreamInput input, (int Width, int Height) size ) =>
		_hooks?.Inject is { } inject ? inject( window.Hwnd, input, size.Width, size.Height ) : FindInjector( window ).Send( input, size.Width, size.Height );

	/// <summary>Gets the injector of a window, creating it on first use.</summary>
	private InputInjector FindInjector( CaptureWindow window ) => _injectors.GetOrAdd( window.Hwnd, hwnd => new( (nint)hwnd ) );

	/// <summary>Gets the size input coordinates of a window refer to: its last capture's, else the size a capture of it would have now.</summary>
	private (int Width, int Height) ReadSize( CaptureWindow window ) =>
		_sizes.TryGetValue( window.Hwnd, out var size ) ? size : WindowCapture.ComputeScaledSize( window.Width, window.Height, CaptureWidth );

	/// <summary>Captures a window, encodes and saves the capture, and remembers its size for input.</summary>
	/// <returns>The saved file and the capture's size.</returns>
	/// <exception cref="InvalidOperationException">The window has nothing to capture now.</exception>
	private async Task<(string Path, int Width, int Height)> SaveCaptureAsync( CaptureWindow window, CancellationToken cancellation )
	{
		BgraFrame? frame;
		using ( var capturer = await Task.Run( () => OpenCapturer( window ), cancellation ) )
		{
			var deadline = DateTime.UtcNow + FirstFrameWait;
			while ( ( frame = await Task.Run( () => capturer.Capture( CaptureWidth ), cancellation ) ) is null && DateTime.UtcNow < deadline )
				await Task.Delay( FirstFramePoll, cancellation );
		}

		if ( frame is null )
			throw new InvalidOperationException( $"\"{window.Title}\" shows nothing to capture now, such as while it is minimized or closing." );
		var image = _hooks?.Encode is { } encode ? await encode( frame, cancellation ) : await JpegEncoder.EncodeAsync( frame, CaptureQuality, cancellation );
		_sizes[window.Hwnd] = (frame.Width, frame.Height);
		Directory.CreateDirectory( _screenshots );
		var path = Path.Combine( _screenshots, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-window-{window.Hwnd:x}.jpg" );
		await File.WriteAllBytesAsync( path, image, cancellation );
		return (path, frame.Width, frame.Height);
	}

	/// <summary>Opens a capturer of a window, the same as a stream's (<see cref="WindowCapturers.Open"/>).</summary>
	/// <exception cref="InvalidOperationException">The window is closed, or capture needs Windows.</exception>
	private IWindowCapturer OpenCapturer( CaptureWindow window )
	{
		if ( _hooks?.CreateCapturer is { } create )
			return create( window.Hwnd );

		if ( !OperatingSystem.IsWindows() )
			throw new InvalidOperationException( "Capturing windows needs Windows." );
		if ( !StreamNative.IsWindow( (nint)window.Hwnd ) )
			throw new InvalidOperationException( $"\"{window.Title}\" was closed; call window_list." );

		return WindowCapturers.Open( (nint)window.Hwnd );
	}

	/// <summary>Reads the windows: the checks' list, else this PC's.</summary>
	/// <exception cref="InvalidOperationException">Not on Windows.</exception>
	private IReadOnlyList<CaptureWindow> ReadWindows() =>
		_hooks?.ListWindows?.Invoke() ?? (OperatingSystem.IsWindows() ? WindowCatalog.List() : throw new InvalidOperationException( "Listing windows needs Windows." ));

	/// <summary>Finds the window a call names.</summary>
	private CaptureWindow Resolve( JsonElement arguments )
	{
		var text = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty( "window", out var window )
			? window.ValueKind switch
			{
				JsonValueKind.Number => window.GetRawText(),
				JsonValueKind.String => window.GetString() ?? "",
				_ => "",
			}
			: "";
		return Match( ReadWindows(), text );
	}

	/// <summary>Reads x and y, refusing a point outside the window's last capture.</summary>
	private static (double X, double Y) ReadPoint( JsonElement arguments, (int Width, int Height) size )
	{
		double Read( string name ) => arguments.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.Number ? value.GetDouble()
			: throw new ArgumentException( $"Pass {name} in pixels of the window's last capture." );

		var (x, y) = (Read( "x" ), Read( "y" ));
		if ( x < 0 || y < 0 || x >= size.Width || y >= size.Height )
			throw new ArgumentException( $"{x:0}, {y:0} is outside the window's last capture, which is {size.Width}×{size.Height} pixels; call window_capture to see it again." );

		return (x, y);
	}

	/// <summary>Reads the modifiers argument: an array of names, or names joined by <c>+</c>.</summary>
	private static IEnumerable<string> ReadModifiers( JsonElement arguments )
	{
		if ( !arguments.TryGetProperty( "modifiers", out var modifiers ) )
			return [];

		return modifiers.ValueKind switch
		{
			JsonValueKind.Array => [.. modifiers.EnumerateArray().Select( item => item.GetString() ?? "" )],
			JsonValueKind.String => modifiers.GetString()!.Split( '+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries ),
			_ => [],
		};
	}

	/// <summary>Reads a string argument, or null.</summary>
	private static string? ReadString( JsonElement arguments, string name ) =>
		arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	/// <summary>Lists windows for an error: id, title and process.</summary>
	private static string Describe( IEnumerable<CaptureWindow> windows )
	{
		var listed = windows.Take( MaxListed ).Select( window => $"{window.Hwnd} \"{window.Title}\" ({window.Process})" ).ToArray();
		return listed.Length == 0 ? "none" : string.Join( "; ", listed );
	}
}
