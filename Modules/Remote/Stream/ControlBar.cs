using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lokn.Modules.Remote;

/// <summary>Shows a bar at the top of the screen while an agent uses this PC's windows, like the bars of other agents that control a computer: which
/// window it uses, and a Stop button, with Loom's glowing frame around that window (<see cref="ControlFrame"/>). Esc stops too, for as long as the bar
/// shows.</summary>
/// <remarks>
/// The bar is a native top-most window on a thread of its own, so it shows over every app, on the screen of the window the agent uses, without taking
/// the focus, and is left out of screen captures and streams. <see cref="Show"/> and <see cref="Hide"/> may be called from any thread; the stop
/// callback runs on the bar's thread.
/// </remarks>
[SupportedOSPlatform( "windows" )]
internal sealed unsafe partial class ControlBar : IDisposable
{
	/// <summary>Defines the bar's size at 96 DPI.</summary>
	private const int BarWidth = 400, BarHeight = 40;

	/// <summary>Defines the Stop button's width at 96 DPI.</summary>
	private const int ButtonWidth = 92;

	/// <summary>Defines the messages the bar's thread takes from other threads.</summary>
	private const uint ShowMessage = 0x8001, HideMessage = 0x8002;

	/// <summary>Defines the id of the Esc hot key.</summary>
	private const int EscapeKey = 1;

	/// <summary>Defines the bar's colors as Windows COLORREF values (0x00BBGGRR): panel, border, text, muted text, accent.</summary>
	private const uint Panel = 0x00202122, Border = 0x00353B3B, Text = 0x00DFE6E8, Muted = 0x00969FA0, Accent = 0x0083A5D5;

	/// <summary>Holds the one bar of the process, which the window procedure reaches.</summary>
	private static ControlBar? s_bar;

	/// <summary>Runs when the user stops the agent, with the Stop button or Esc.</summary>
	private readonly Action _stop;

	/// <summary>Guards <see cref="_title"/> and <see cref="_target"/>.</summary>
	private readonly Lock _lock = new();

	/// <summary>Completes once the bar's window exists, or failed to.</summary>
	private readonly ManualResetEventSlim _ready = new();

	/// <summary>Stores the bar's window, or 0 before it exists or after it failed.</summary>
	private nint _window;

	/// <summary>Stores the title of the window the agent uses.</summary>
	private string _title = "";

	/// <summary>Stores the window the agent uses, whose screen the bar shows on.</summary>
	private nint _target;

	/// <summary>Stores the scale of the screen the bar is on, 1 at 96 DPI.</summary>
	private double _scale = 1;

	/// <summary>Stores whether the bar holds the Esc hot key.</summary>
	private bool _hasEscape;

	/// <summary>Defines the id of the timer that moves the frame's light, and its period in milliseconds.</summary>
	private const int FrameTimer = 2, FramePeriod = 33;

	/// <summary>Stores the frame around the agent's window, or null before the bar's window exists.</summary>
	private ControlFrame? _frame;

	/// <summary>Measures the time the frame's light has been moving since the bar showed.</summary>
	private readonly System.Diagnostics.Stopwatch _clock = new();

	/// <summary>Creates the bar, hidden, on its own thread.</summary>
	/// <param name="stop">Runs when the user stops the agent; on the bar's thread.</param>
	internal ControlBar( Action stop )
	{
		_stop = stop;
		s_bar = this;
		var thread = new Thread( Run ) { IsBackground = true, Name = "Loom control bar" };
		thread.SetApartmentState( ApartmentState.STA );
		thread.Start();
		_ready.Wait( TimeSpan.FromSeconds( 5 ) );
	}

	/// <summary>Shows the bar, or updates it, for an agent that uses a window.</summary>
	/// <param name="title">The window's title.</param>
	/// <param name="window">The window, whose screen the bar shows on.</param>
	internal void Show( string title, long window )
	{
		lock ( _lock )
		{
			_title = title.Length > 0 ? title : "a window";
			_target = (nint)window;
		}
		if ( _window != 0 )
			PostMessageW( _window, ShowMessage, 0, 0 );
	}

	/// <summary>Hides the bar and gives Esc back to the apps.</summary>
	internal void Hide()
	{
		if ( _window != 0 )
			PostMessageW( _window, HideMessage, 0, 0 );
	}

	/// <summary>Closes the bar's window and ends its thread.</summary>
	public void Dispose()
	{
		if ( _window != 0 )
			PostMessageW( _window, 0x0010 /* WM_CLOSE */, 0, 0 );
		if ( s_bar == this )
			s_bar = null;
	}

	/// <summary>Creates the window and pumps its messages until it closes.</summary>
	private void Run()
	{
		// Screen pixels everywhere, so the bar and the frame line up with the window on any screen's scale.
		SetThreadDpiAwarenessContext( -4 /* DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 */ );
		// A class name of its own per bar: after a reload of this module, a class registered under a shared name would still send its messages to
		// the window procedure of the unloaded copy, and the bar would never show.
		var name = "LoknControlBar-" + Guid.NewGuid().ToString( "N" );
		fixed ( char* className = name )
		{
			var windowClass = new WindowClass
			{
				Size = (uint)sizeof( WindowClass ),
				Procedure = &Procedure,
				Instance = GetModuleHandleW( 0 ),
				ClassName = className,
			};
			RegisterClassExW( &windowClass );
			// Top-most, not in the taskbar or Alt+Tab, and never activated, so the app the agent uses keeps the focus.
			_window = CreateWindowExW( 0x00000008 | 0x00000080 | 0x08000000, className, className, 0x80000000, 0, 0, BarWidth, BarHeight, 0, 0, windowClass.Instance, 0 );
			if ( _window != 0 )
				_frame = new ControlFrame( className, windowClass.Instance, Accent );
		}
		if ( _window != 0 )
			SetWindowDisplayAffinity( _window, 0x11 /* WDA_EXCLUDEFROMCAPTURE */ );
		_ready.Set();
		if ( _window == 0 )
			return;

		Message message;
		while ( GetMessageW( &message, 0, 0, 0 ) > 0 )
		{
			TranslateMessage( &message );
			DispatchMessageW( &message );
		}
	}

	/// <summary>Handles the bar's messages.</summary>
	[UnmanagedCallersOnly]
	private static nint Procedure( nint window, uint message, nint wParam, nint lParam )
	{
		if ( s_bar is not { } bar || bar._window != window )
			return DefWindowProcW( window, message, wParam, lParam );

		switch ( message )
		{
			case ShowMessage:
				bar.Place();
				return 0;
			case HideMessage:
				bar.Conceal();
				return 0;
			case 0x0312 /* WM_HOTKEY */ when wParam == EscapeKey:
				bar.Stop();
				return 0;
			case 0x0202 /* WM_LBUTTONUP */ when bar.IsOnButton( (short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF) ):
				bar.Stop();
				return 0;
			case 0x0021 /* WM_MOUSEACTIVATE */:
				return 3 /* MA_NOACTIVATE */;
			case 0x0020 /* WM_SETCURSOR */:
				Point point;
				GetCursorPos( &point );
				ScreenToClient( window, &point );
				SetCursor( LoadCursorW( 0, bar.IsOnButton( point.X, point.Y ) ? 32649 /* IDC_HAND */ : 32512 /* IDC_ARROW */ ) );
				return 1;
			case 0x0318 /* WM_PRINTCLIENT */:
				bar.Draw( wParam );
				return 0;
			case 0x0014 /* WM_ERASEBKGND */:
				return 1;
			case 0x000F /* WM_PAINT */:
				bar.Paint();
				return 0;
			case 0x0113 /* WM_TIMER */ when wParam == FrameTimer:
				bar.Animate();
				return 0;
			case 0x0002 /* WM_DESTROY */:
				if ( bar._hasEscape )
					UnregisterHotKey( window, EscapeKey );
				bar._frame?.Dispose();
				PostQuitMessage( 0 );
				return 0;
		}
		return DefWindowProcW( window, message, wParam, lParam );
	}

	/// <summary>Scales a length at 96 DPI to the bar's screen.</summary>
	private int Scale( int length ) => (int)Math.Round( length * _scale );

	/// <summary>Moves the bar to the top middle of the work area of the agent's window's screen, at that screen's scale, shows it without activating it
	/// and takes Esc.</summary>
	private void Place()
	{
		nint target;
		lock ( _lock )
			target = _target;
		var dpi = target != 0 ? GetDpiForWindow( target ) : 0;
		_scale = (dpi == 0 ? 96 : dpi) / 96.0;
		var monitor = new MonitorInfo { Size = (uint)sizeof( MonitorInfo ) };
		GetMonitorInfoW( MonitorFromWindow( target, 2 /* MONITOR_DEFAULTTONEAREST */ ), &monitor );
		var work = monitor.Work;
		int width = Scale( BarWidth ), height = Scale( BarHeight );
		SetWindowPos( _window, -1 /* HWND_TOPMOST */, work.Left + (work.Right - work.Left - width) / 2, work.Top + Scale( 10 ), width, height, 0x0010 | 0x0040 /* NOACTIVATE | SHOWWINDOW */ );
		SetWindowRgn( _window, CreateRoundRectRgn( 0, 0, width + 1, height + 1, Scale( 14 ), Scale( 14 ) ), 1 );
		InvalidateRect( _window, null, 0 );
		if ( !_hasEscape )
			_hasEscape = RegisterHotKey( _window, EscapeKey, 0x4000 /* MOD_NOREPEAT */, 0x1B /* VK_ESCAPE */ ) != 0;
		if ( !_clock.IsRunning )
		{
			_clock.Restart();
			SetTimer( _window, FrameTimer, FramePeriod, 0 );
		}
		Animate();
	}

	/// <summary>Moves the frame's light and keeps the frame on the agent's window, which may have moved.</summary>
	private void Animate()
	{
		nint target;
		lock ( _lock )
			target = _target;
		_frame?.Update( target, _scale, _clock.Elapsed.TotalSeconds );
	}

	/// <summary>Hides the bar and the frame, and gives Esc back to the apps.</summary>
	private void Conceal()
	{
		ShowWindow( _window, 0 /* SW_HIDE */ );
		KillTimer( _window, FrameTimer );
		_clock.Reset();
		_frame?.Hide();
		if ( _hasEscape )
		{
			UnregisterHotKey( _window, EscapeKey );
			_hasEscape = false;
		}
	}

	/// <summary>Hides the bar and tells the owner the user stopped the agent.</summary>
	private void Stop()
	{
		Conceal();
		_stop();
	}

	/// <summary>Gets the Stop button's rectangle in the bar.</summary>
	private Rect ButtonRect()
	{
		int width = Scale( BarWidth ), height = Scale( BarHeight ), inset = Scale( 6 );
		return new Rect { Left = width - Scale( ButtonWidth ) - inset, Top = inset, Right = width - inset, Bottom = height - inset };
	}

	/// <summary>Reports whether a point of the bar is on the Stop button.</summary>
	private bool IsOnButton( int x, int y )
	{
		var button = ButtonRect();
		return x >= button.Left && x < button.Right && y >= button.Top && y < button.Bottom;
	}

	/// <summary>Paints the bar on screen.</summary>
	private void Paint()
	{
		PaintInfo paint;
		Draw( BeginPaint( _window, &paint ) );
		EndPaint( _window, &paint );
	}

	/// <summary>Draws the bar into a device context: a dark panel with a hairline border, an accent dot, "Loom is using" and the window's title, then the
	/// Stop button with its Esc hint. Printing the window draws it too, which tools that picture windows use, since screen captures leave the bar out.</summary>
	private void Draw( nint dc )
	{
		int width = Scale( BarWidth ), height = Scale( BarHeight );
		var whole = CreateRoundRectRgn( 0, 0, width + 1, height + 1, Scale( 14 ), Scale( 14 ) );
		var panel = CreateSolidBrush( Panel );
		var border = CreateSolidBrush( Border );
		var accent = CreateSolidBrush( Accent );
		FillRgn( dc, whole, panel );
		FrameRgn( dc, whole, border, 1, 1 );

		var dotSize = Scale( 8 );
		var dot = CreateEllipticRgn( Scale( 16 ), (height - dotSize) / 2, Scale( 16 ) + dotSize, (height + dotSize) / 2 );
		FillRgn( dc, dot, accent );

		var font = CreateFontW( -Scale( 13 ), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5 /* CLEARTYPE_QUALITY */, 0, "Segoe UI Variable Text" );
		var bold = CreateFontW( -Scale( 13 ), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI Variable Text" );
		SetBkMode( dc, 1 /* TRANSPARENT */ );

		string title;
		lock ( _lock )
			title = _title;
		var button = ButtonRect();
		var label = new Rect { Left = Scale( 32 ), Top = 0, Right = button.Left - Scale( 10 ), Bottom = height };
		var old = SelectObject( dc, font );
		SetTextColor( dc, Muted );
		const string prefix = "Loom is using ";
		var measured = new Rect { Left = label.Left, Top = 0, Right = label.Right, Bottom = height };
		DrawTextW( dc, prefix, prefix.Length, &measured, 0x0020 | 0x0400 /* SINGLELINE | CALCRECT */ );
		DrawTextW( dc, prefix, prefix.Length, &label, 0x0020 | 0x0004 /* SINGLELINE | VCENTER */ );
		SelectObject( dc, bold );
		SetTextColor( dc, Text );
		var name = new Rect { Left = measured.Right, Top = 0, Right = label.Right, Bottom = height };
		DrawTextW( dc, title, title.Length, &name, 0x0020 | 0x0004 | 0x8000 /* END_ELLIPSIS */ );

		var buttonRegion = CreateRoundRectRgn( button.Left, button.Top, button.Right + 1, button.Bottom + 1, Scale( 10 ), Scale( 10 ) );
		FillRgn( dc, buttonRegion, accent );
		SetTextColor( dc, Panel );
		const string stop = "Stop  \u00B7  Esc";
		DrawTextW( dc, stop, stop.Length, &button, 0x0020 | 0x0004 | 0x0001 /* CENTER */ );

		SelectObject( dc, old );
		foreach ( var item in new[] { whole, panel, border, accent, dot, font, bold, buttonRegion } )
			DeleteObject( item );
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

	/// <summary>Mirrors <c>POINT</c>.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct Point
	{
		/// <summary>The horizontal coordinate.</summary>
		public int X;

		/// <summary>The vertical coordinate.</summary>
		public int Y;
	}

	/// <summary>Mirrors <c>MONITORINFO</c>.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct MonitorInfo
	{
		/// <summary>The structure's size.</summary>
		public uint Size;

		/// <summary>The monitor's rectangle.</summary>
		public Rect Monitor;

		/// <summary>The monitor's work area, without the taskbar.</summary>
		public Rect Work;

		/// <summary>The monitor's flags.</summary>
		public uint Flags;
	}

	/// <summary>Mirrors <c>PAINTSTRUCT</c>.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct PaintInfo
	{
		/// <summary>The device context to paint with.</summary>
		public nint Dc;

		/// <summary>Whether the background must be erased.</summary>
		public int Erase;

		/// <summary>The rectangle to paint.</summary>
		public Rect Area;

		/// <summary>Reserved.</summary>
		public int Restore;

		/// <summary>Reserved.</summary>
		public int Update;

		/// <summary>Reserved.</summary>
		public fixed byte Reserved[32];
	}

	/// <summary>Mirrors <c>WNDCLASSEXW</c>.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct WindowClass
	{
		/// <summary>The structure's size.</summary>
		public uint Size;

		/// <summary>The class styles.</summary>
		public uint Style;

		/// <summary>The window procedure.</summary>
		public delegate* unmanaged<nint, uint, nint, nint, nint> Procedure;

		/// <summary>Extra class bytes.</summary>
		public int ClassExtra;

		/// <summary>Extra window bytes.</summary>
		public int WindowExtra;

		/// <summary>The module.</summary>
		public nint Instance;

		/// <summary>The icon.</summary>
		public nint Icon;

		/// <summary>The cursor.</summary>
		public nint Cursor;

		/// <summary>The background brush.</summary>
		public nint Background;

		/// <summary>The menu name.</summary>
		public char* MenuName;

		/// <summary>The class name.</summary>
		public char* ClassName;

		/// <summary>The small icon.</summary>
		public nint SmallIcon;
	}

	/// <summary>Mirrors <c>MSG</c>.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct Message
	{
		/// <summary>The window.</summary>
		public nint Window;

		/// <summary>The message.</summary>
		public uint Id;

		/// <summary>The first parameter.</summary>
		public nint WParam;

		/// <summary>The second parameter.</summary>
		public nint LParam;

		/// <summary>The time.</summary>
		public uint Time;

		/// <summary>The cursor position.</summary>
		public Point Cursor;

		/// <summary>Reserved.</summary>
		public uint Private;
	}

	/// <summary>Registers a window class.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial ushort RegisterClassExW( WindowClass* windowClass );

	/// <summary>Creates a window.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial nint CreateWindowExW( uint extendedStyle, char* className, char* title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter );

	/// <summary>Gets the executable's module handle.</summary>
	[LibraryImport( "kernel32.dll" )]
	private static partial nint GetModuleHandleW( nint name );

	/// <summary>Waits for the next message of this thread.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int GetMessageW( Message* message, nint window, uint first, uint last );

	/// <summary>Translates key messages.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int TranslateMessage( Message* message );

	/// <summary>Sends a message to its window's procedure.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial nint DispatchMessageW( Message* message );

	/// <summary>Gives a message the default handling.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial nint DefWindowProcW( nint window, uint message, nint wParam, nint lParam );

	/// <summary>Queues a message for a window's thread.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int PostMessageW( nint window, uint message, nint wParam, nint lParam );

	/// <summary>Ends the calling thread's message loop.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial void PostQuitMessage( int exitCode );

	/// <summary>Keeps a window out of screen captures.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int SetWindowDisplayAffinity( nint window, uint affinity );

	/// <summary>Registers a system-wide hot key.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int RegisterHotKey( nint window, int id, uint modifiers, uint key );

	/// <summary>Frees a hot key.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int UnregisterHotKey( nint window, int id );

	/// <summary>Starts a timer that posts WM_TIMER to a window.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial nuint SetTimer( nint window, nuint id, uint milliseconds, nint callback );

	/// <summary>Stops a window's timer.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int KillTimer( nint window, nuint id );

	/// <summary>Sets how the calling thread's windows see the screen's scale.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial nint SetThreadDpiAwarenessContext( nint context );

	/// <summary>Moves, sizes and orders a window.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int SetWindowPos( nint window, nint after, int x, int y, int width, int height, uint flags );

	/// <summary>Shows or hides a window.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int ShowWindow( nint window, int command );

	/// <summary>Sets the shape of a window.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int SetWindowRgn( nint window, nint region, int redraw );

	/// <summary>Marks a window to be painted again.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int InvalidateRect( nint window, Rect* area, int erase );

	/// <summary>Gets the DPI a window is shown at.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial uint GetDpiForWindow( nint window );

	/// <summary>Finds the monitor a window is on.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial nint MonitorFromWindow( nint window, uint flags );

	/// <summary>Reads a monitor's rectangles.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int GetMonitorInfoW( nint monitor, MonitorInfo* info );

	/// <summary>Reads the cursor's screen position.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int GetCursorPos( Point* point );

	/// <summary>Turns a screen point into a window's client coordinates.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int ScreenToClient( nint window, Point* point );

	/// <summary>Loads a system cursor.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial nint LoadCursorW( nint instance, nint name );

	/// <summary>Sets the cursor.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial nint SetCursor( nint cursor );

	/// <summary>Starts painting a window.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial nint BeginPaint( nint window, PaintInfo* paint );

	/// <summary>Ends painting a window.</summary>
	[LibraryImport( "user32.dll" )]
	private static partial int EndPaint( nint window, PaintInfo* paint );

	/// <summary>Draws text in a rectangle.</summary>
	[LibraryImport( "user32.dll", StringMarshalling = StringMarshalling.Utf16 )]
	private static partial int DrawTextW( nint dc, string text, int length, Rect* area, uint format );

	/// <summary>Makes a rounded rectangle region.</summary>
	[LibraryImport( "gdi32.dll" )]
	private static partial nint CreateRoundRectRgn( int left, int top, int right, int bottom, int width, int height );

	/// <summary>Makes an ellipse region.</summary>
	[LibraryImport( "gdi32.dll" )]
	private static partial nint CreateEllipticRgn( int left, int top, int right, int bottom );

	/// <summary>Makes a solid brush.</summary>
	[LibraryImport( "gdi32.dll" )]
	private static partial nint CreateSolidBrush( uint color );

	/// <summary>Fills a region.</summary>
	[LibraryImport( "gdi32.dll" )]
	private static partial int FillRgn( nint dc, nint region, nint brush );

	/// <summary>Draws a region's border.</summary>
	[LibraryImport( "gdi32.dll" )]
	private static partial int FrameRgn( nint dc, nint region, nint brush, int width, int height );

	/// <summary>Makes a font.</summary>
	[LibraryImport( "gdi32.dll", StringMarshalling = StringMarshalling.Utf16 )]
	private static partial nint CreateFontW( int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet,
		uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face );

	/// <summary>Selects an object into a device context.</summary>
	[LibraryImport( "gdi32.dll" )]
	private static partial nint SelectObject( nint dc, nint item );

	/// <summary>Deletes a GDI object.</summary>
	[LibraryImport( "gdi32.dll" )]
	private static partial int DeleteObject( nint item );

	/// <summary>Sets how text's background is drawn.</summary>
	[LibraryImport( "gdi32.dll" )]
	private static partial int SetBkMode( nint dc, int mode );

	/// <summary>Sets the text color.</summary>
	[LibraryImport( "gdi32.dll" )]
	private static partial uint SetTextColor( nint dc, uint color );
}
