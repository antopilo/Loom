using System.Runtime.InteropServices;

namespace Lokn.Modules.Remote;

/// <summary>Holds one <c>cap.input</c> event; its camelCase JSON is the frame's fields.</summary>
/// <param name="Kind">One of <see cref="Move"/>, <see cref="Down"/>, <see cref="Up"/>, <see cref="Wheel"/> or <see cref="Key"/>.</param>
/// <param name="X">The pointer's horizontal position in frame pixels.</param>
/// <param name="Y">The pointer's vertical position in frame pixels.</param>
/// <param name="Button">For <c>down</c> and <c>up</c>, the button as in the DOM: 0 left, 1 middle, 2 right, 3 back, 4 forward.</param>
/// <param name="Delta">For <c>wheel</c>, the vertical amount in Windows wheel units: 120 per notch, positive away from the user (scrolls up).</param>
/// <param name="Vk">For <c>key</c>, the Windows virtual-key code.</param>
/// <param name="IsDown">For <c>key</c>, true when pressed and false when released; serialized as <c>down</c>.</param>
public sealed record StreamInput(
	string Kind,
	double X = 0,
	double Y = 0,
	int Button = 0,
	int Delta = 0,
	int Vk = 0,
	[property: System.Text.Json.Serialization.JsonPropertyName( "down" )] bool IsDown = false )
{
	/// <summary>Defines the kind that moves the pointer.</summary>
	public const string Move = "move";

	/// <summary>Defines the kind that presses a pointer button.</summary>
	public const string Down = "down";

	/// <summary>Defines the kind that releases a pointer button.</summary>
	public const string Up = "up";

	/// <summary>Defines the kind that turns the wheel.</summary>
	public const string Wheel = "wheel";

	/// <summary>Defines the kind that presses or releases a key.</summary>
	public const string Key = "key";
}

/// <summary>Sends <see cref="StreamInput"/> events to one window with <c>SendInput</c>, bringing the window to the foreground before presses, releases, wheel turns and keys.</summary>
/// <remarks>Frame pixels map to the client area by the ratio of the client size to the frame size, then to the screen in physical pixels, so scaling and display DPI do not shift clicks. Windows refuses input into a window of a process with higher integrity, such as one run as administrator, unless the host runs elevated too.</remarks>
public sealed class InputInjector( nint window )
{
	/// <summary>Defines the INPUT type of mouse events.</summary>
	private const uint MouseType = 0;

	/// <summary>Defines the INPUT type of keyboard events.</summary>
	private const uint KeyboardType = 1;

	/// <summary>Defines the mouse flag of movement.</summary>
	private const uint MoveFlag = 0x0001;

	/// <summary>Defines the mouse flag of a scrolled wheel.</summary>
	private const uint WheelFlag = 0x0800;

	/// <summary>Defines the mouse flag that maps coordinates to the whole virtual desktop.</summary>
	private const uint VirtualDesktopFlag = 0x4000;

	/// <summary>Defines the mouse flag of normalized absolute coordinates.</summary>
	private const uint AbsoluteFlag = 0x8000;

	/// <summary>Defines the mouse flag of an extra button press.</summary>
	private const uint ExtraDownFlag = 0x0080;

	/// <summary>Defines the mouse flag of an extra button release.</summary>
	private const uint ExtraUpFlag = 0x0100;

	/// <summary>Defines the key flag of extended keys, such as the arrows on their own keypad.</summary>
	private const uint ExtendedKeyFlag = 0x0001;

	/// <summary>Defines the key flag of a release.</summary>
	private const uint KeyUpFlag = 0x0002;

	/// <summary>Defines the key flag of a Unicode character sent in the scan code.</summary>
	private const uint UnicodeFlag = 0x0004;

	/// <summary>Defines the virtual key of Enter.</summary>
	private const ushort EnterKey = 0x0D;

	/// <summary>Defines the largest normalized absolute coordinate.</summary>
	private const int AbsoluteRange = 65535;

	/// <summary>Defines MapVirtualKey's virtual-key to scan code translation.</summary>
	private const uint VirtualKeyToScanCode = 0;

	/// <summary>Holds the virtual keys Windows sends with the extended flag.</summary>
	private static readonly HashSet<int> ExtendedKeys =
	[
		0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // Page Up, Page Down, End, Home, arrows.
		0x2D, 0x2E, // Insert, Delete.
		0x5B, 0x5C, 0x5D, // Windows keys, Menu.
		0x6F, 0x90, // Keypad divide, Num Lock.
		0xA3, 0xA5, // Right Ctrl, right Alt.
	];

	/// <summary>Gets the window input goes to.</summary>
	public nint Window => window;

	/// <summary>Sends one event. Pointer coordinates are in pixels of a frame of the given size.</summary>
	/// <param name="input">The event.</param>
	/// <param name="frameWidth">The width of the frame the coordinates refer to.</param>
	/// <param name="frameHeight">The height of the frame the coordinates refer to.</param>
	/// <returns>False when the window or screen is gone, the frame size is unknown, the kind or button is unknown, or Windows refused the input.</returns>
	/// <remarks>On a screen (<see cref="ScreenCatalog"/>), input goes wherever the user's focus is, as at the PC; no window is raised.</remarks>
	public bool Send( StreamInput input, int frameWidth, int frameHeight )
	{
		var isScreen = ScreenCatalog.IsScreen( window );
		if ( !ScreenCatalog.Exists( window ) )
			return false;

		using var dpi = StreamNative.EnterPhysicalPixels();
		// Hovering needs no focus; raising the window on every move would also pull it over whatever the user looks at.
		if ( input.Kind != StreamInput.Move && !isScreen )
		{
			UpdateForeground();
			// Keys and clicks go wherever the focus is, so they are dropped unless the streamed window, or a popup it owns, really has it.
			if ( !IsForeground() )
				return false;
		}

		if ( input.Kind == StreamInput.Key )
			return SendKey( input.Vk, input.IsDown );

		if ( frameWidth <= 0 || frameHeight <= 0 )
			return false;

		StreamNative.NativePoint screen;
		if ( isScreen )
		{
			if ( ScreenCatalog.Find( window ) is not { } found )
				return false;

			var bounds = found.Bounds;
			var point = ComputeClientPoint( input.X, input.Y, frameWidth, frameHeight, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top );
			screen = new StreamNative.NativePoint { X = bounds.Left + point.X, Y = bounds.Top + point.Y };
		}
		else
		{
			if ( !StreamNative.GetClientRect( window, out var client ) )
				return false;

			var point = ComputeClientPoint( input.X, input.Y, frameWidth, frameHeight, client.Right - client.Left, client.Bottom - client.Top );
			screen = new StreamNative.NativePoint { X = point.X, Y = point.Y };
			if ( !StreamNative.ClientToScreen( window, ref screen ) )
				return false;
		}

		var desktop = (Left: StreamNative.GetSystemMetrics( StreamNative.VirtualScreenLeft ), Top: StreamNative.GetSystemMetrics( StreamNative.VirtualScreenTop ),
			Width: StreamNative.GetSystemMetrics( StreamNative.VirtualScreenWidth ), Height: StreamNative.GetSystemMetrics( StreamNative.VirtualScreenHeight ));
		var (x, y) = ComputeAbsolute( screen.X, screen.Y, desktop.Left, desktop.Top, desktop.Width, desktop.Height );
		var flags = MoveFlag | AbsoluteFlag | VirtualDesktopFlag;
		var data = 0;
		switch ( input.Kind )
		{
			case StreamInput.Move:
				break;
			case StreamInput.Down or StreamInput.Up:
				if ( ComputeButtonFlags( input.Button, input.Kind == StreamInput.Down ) is not { } button )
					return false;

				flags |= button.Flags;
				data = button.Data;
				break;
			case StreamInput.Wheel:
				flags |= WheelFlag;
				data = input.Delta;
				break;
			default:
				return false;
		}

		var mouse = new StreamNative.Input
		{
			Type = MouseType,
			Data = new() { Mouse = new() { X = x, Y = y, MouseData = data, Flags = flags } },
		};
		return StreamNative.SendInput( 1, [mouse], Marshal.SizeOf<StreamNative.Input>() ) == 1;
	}

	/// <summary>Determines whether the foreground window is the streamed window or one it owns, such as its menu or dialog.</summary>
	private bool IsForeground()
	{
		var foreground = StreamNative.GetForegroundWindow();
		return foreground != 0 && StreamNative.GetAncestor( foreground, StreamNative.RootOwner ) == StreamNative.GetAncestor( window, StreamNative.RootOwner );
	}

	/// <summary>Maps a point in frame pixels to client pixels, clamped inside the client area.</summary>
	/// <param name="x">The horizontal frame coordinate.</param>
	/// <param name="y">The vertical frame coordinate.</param>
	/// <param name="frameWidth">The frame width.</param>
	/// <param name="frameHeight">The frame height.</param>
	/// <param name="clientWidth">The client width in physical pixels.</param>
	/// <param name="clientHeight">The client height in physical pixels.</param>
	public static (int X, int Y) ComputeClientPoint( double x, double y, int frameWidth, int frameHeight, int clientWidth, int clientHeight )
	{
		var clientX = (int)Math.Floor( x * clientWidth / Math.Max( 1, frameWidth ) );
		var clientY = (int)Math.Floor( y * clientHeight / Math.Max( 1, frameHeight ) );
		return (Math.Clamp( clientX, 0, Math.Max( 0, clientWidth - 1 ) ), Math.Clamp( clientY, 0, Math.Max( 0, clientHeight - 1 ) ));
	}

	/// <summary>Maps a screen point to SendInput's normalized 0..65535 coordinates over the virtual desktop.</summary>
	/// <param name="screenX">The horizontal screen coordinate in physical pixels.</param>
	/// <param name="screenY">The vertical screen coordinate in physical pixels.</param>
	/// <param name="left">The virtual desktop's left edge.</param>
	/// <param name="top">The virtual desktop's top edge.</param>
	/// <param name="width">The virtual desktop's width.</param>
	/// <param name="height">The virtual desktop's height.</param>
	public static (int X, int Y) ComputeAbsolute( int screenX, int screenY, int left, int top, int width, int height )
	{
		// Rounding to the nearest step lands on the intended pixel; truncating drifts up and left on large desktops.
		var x = (int)Math.Round( (double)(screenX - left) * AbsoluteRange / Math.Max( 1, width - 1 ) );
		var y = (int)Math.Round( (double)(screenY - top) * AbsoluteRange / Math.Max( 1, height - 1 ) );
		return (Math.Clamp( x, 0, AbsoluteRange ), Math.Clamp( y, 0, AbsoluteRange ));
	}

	/// <summary>Computes the mouse flags and extra data of a DOM button number, or null for an unknown button.</summary>
	internal static (uint Flags, int Data)? ComputeButtonFlags( int button, bool isDown ) => button switch
	{
		0 => (isDown ? 0x0002u : 0x0004u, 0),
		1 => (isDown ? 0x0020u : 0x0040u, 0),
		2 => (isDown ? 0x0008u : 0x0010u, 0),
		3 => (isDown ? ExtraDownFlag : ExtraUpFlag, 1),
		4 => (isDown ? ExtraDownFlag : ExtraUpFlag, 2),
		_ => null,
	};

	/// <summary>Sends one key press or release, with its scan code so programs that read scan codes see it too.</summary>
	private static bool SendKey( int virtualKey, bool isDown )
	{
		if ( virtualKey is <= 0 or > 0xFE )
			return false;

		var flags = isDown ? 0u : KeyUpFlag;
		if ( ExtendedKeys.Contains( virtualKey ) )
			flags |= ExtendedKeyFlag;

		var key = new StreamNative.Input
		{
			Type = KeyboardType,
			Data = new()
			{
				Keyboard = new()
				{
					VirtualKey = (ushort)virtualKey,
					ScanCode = (ushort)StreamNative.MapVirtualKey( (uint)virtualKey, VirtualKeyToScanCode ),
					Flags = flags,
				},
			},
		};
		return StreamNative.SendInput( 1, [key], Marshal.SizeOf<StreamNative.Input>() ) == 1;
	}

	/// <summary>Types text into the window, or wherever the focus is on a screen, as Unicode characters independent of the keyboard layout; a line break presses Enter.</summary>
	/// <param name="text">The text; carriage returns are skipped.</param>
	/// <returns>False when the window or screen is gone, the window could not be brought to the foreground, or Windows refused the input.</returns>
	public bool SendText( string text )
	{
		if ( !ScreenCatalog.Exists( window ) )
			return false;

		if ( !ScreenCatalog.IsScreen( window ) )
		{
			UpdateForeground();
			if ( !IsForeground() )
				return false;
		}

		var inputs = new List<StreamNative.Input>( text.Length * 2 );
		foreach ( var character in text )
		{
			if ( character == '\r' )
				continue;

			var isEnter = character == '\n';
			foreach ( var isDown in (bool[])[true, false] )
			{
				inputs.Add( new()
				{
					Type = KeyboardType,
					Data = new()
					{
						Keyboard = isEnter
							? new() { VirtualKey = EnterKey, ScanCode = (ushort)StreamNative.MapVirtualKey( EnterKey, VirtualKeyToScanCode ), Flags = isDown ? 0u : KeyUpFlag }
							: new() { ScanCode = character, Flags = UnicodeFlag | (isDown ? 0u : KeyUpFlag) },
					},
				} );
			}
		}
		return inputs.Count == 0 || StreamNative.SendInput( (uint)inputs.Count, [.. inputs], Marshal.SizeOf<StreamNative.Input>() ) == inputs.Count;
	}

	/// <summary>Reports whether Windows would refuse this process's input into a window: its process runs elevated, such as as administrator, and this one does not.</summary>
	/// <param name="window">The window.</param>
	public static bool IsRefused( nint window )
	{
		if ( StreamNative.IsElevated( StreamNative.GetCurrentProcess() ) == true )
			return false;

		_ = StreamNative.GetWindowThreadProcessId( window, out var id );
		var process = StreamNative.OpenProcess( StreamNative.ProcessQueryLimited, false, id );
		if ( process == 0 )
			return false;

		try
		{
			// An elevated process lets a process that is not read its handle but not its token.
			return StreamNative.IsElevated( process ) is null or true;
		}
		finally
		{
			_ = StreamNative.CloseHandle( process );
		}
	}

	/// <summary>Restores the window when minimized and makes it the foreground window, borrowing the foreground thread's input state when Windows refuses a plain request.</summary>
	private void UpdateForeground()
	{
		if ( StreamNative.IsIconic( window ) )
			_ = StreamNative.ShowWindow( window, StreamNative.ShowRestore );

		var foreground = StreamNative.GetForegroundWindow();
		if ( foreground == window || StreamNative.SetForegroundWindow( window ) )
			return;

		// Windows lets only the foreground thread hand the foreground over; sharing its input state for a moment counts as that.
		var current = StreamNative.GetCurrentThreadId();
		var owner = foreground != 0 ? StreamNative.GetWindowThreadProcessId( foreground, out _ ) : 0;
		var isAttached = owner != 0 && owner != current && StreamNative.AttachThreadInput( current, owner, true );
		try
		{
			_ = StreamNative.BringWindowToTop( window );
			_ = StreamNative.SetForegroundWindow( window );
		}
		finally
		{
			if ( isAttached )
				_ = StreamNative.AttachThreadInput( current, owner, false );
		}
	}
}
