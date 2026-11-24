using System.Runtime.InteropServices;

namespace Lokn.Modules.Remote;

/// <summary>Holds the Win32 calls the stream host uses to list, capture and drive windows.</summary>
/// <remarks>Every coordinate is read inside <see cref="EnterPhysicalPixels"/>, so window sizes, screen points and the virtual screen agree in physical pixels whatever the process's own DPI awareness is.</remarks>
internal static class StreamNative
{
	/// <summary>Defines GetWindow's owner relation.</summary>
	internal const uint OwnerRelation = 4;

	/// <summary>Defines the GetWindowLong index of the extended style.</summary>
	internal const int ExtendedStyleIndex = -20;

	/// <summary>Defines the extended style of tool windows, which the taskbar and Alt+Tab skip.</summary>
	internal const int ToolWindowStyle = 0x00000080;

	/// <summary>Defines the extended style that puts an owned window on the taskbar.</summary>
	internal const int AppWindowStyle = 0x00040000;

	/// <summary>Defines the DWM attribute reporting whether a window is cloaked, such as on another virtual desktop.</summary>
	internal const int CloakedAttribute = 14;

	/// <summary>Defines PrintWindow's flag that draws only the client area.</summary>
	internal const uint PrintClientOnly = 0x1;

	/// <summary>Defines PrintWindow's flag that asks DirectComposition content, such as GPU-drawn apps, to draw too.</summary>
	internal const uint PrintRenderFullContent = 0x2;

	/// <summary>Defines the BitBlt raster operation that copies the source.</summary>
	internal const uint SourceCopy = 0x00CC0020;

	/// <summary>Defines the BitBlt flag that includes layered windows on top.</summary>
	internal const uint CaptureLayered = 0x40000000;

	/// <summary>Defines the StretchBlt mode that averages source pixels, for a smooth downscale.</summary>
	internal const int HalftoneMode = 4;

	/// <summary>Defines CreateDIBSection's color table usage of RGB values.</summary>
	internal const uint RgbColors = 0;

	/// <summary>Defines the message that asks a window for its icon.</summary>
	internal const uint GetIconMessage = 0x007F;

	/// <summary>Defines WM_GETICON's large icon.</summary>
	internal const nint IconBig = 1;

	/// <summary>Defines WM_GETICON's small icon, else the one Windows derives from the large icon.</summary>
	internal const nint IconSmall2 = 2;

	/// <summary>Defines SendMessageTimeout's flag that returns at once when the window does not answer.</summary>
	internal const uint AbortIfHung = 0x0002;

	/// <summary>Defines the GetClassLongPtr index of the class's large icon.</summary>
	internal const int ClassIcon = -14;

	/// <summary>Defines the GetClassLongPtr index of the class's small icon.</summary>
	internal const int ClassSmallIcon = -34;

	/// <summary>Defines DrawIconEx's flag that draws the icon with its mask.</summary>
	internal const uint DrawIconNormal = 0x0003;

	/// <summary>Defines ShowWindow's restore without activation.</summary>
	internal const int ShowNoActivate = 4;

	/// <summary>Defines ShowWindow's restore of a minimized window.</summary>
	internal const int ShowRestore = 9;

	/// <summary>Defines the system metric of the virtual screen's left edge.</summary>
	internal const int VirtualScreenLeft = 76;

	/// <summary>Defines the system metric of the virtual screen's top edge.</summary>
	internal const int VirtualScreenTop = 77;

	/// <summary>Defines the system metric of the virtual screen's width.</summary>
	internal const int VirtualScreenWidth = 78;

	/// <summary>Defines the system metric of the virtual screen's height.</summary>
	internal const int VirtualScreenHeight = 79;

	/// <summary>Defines the DPI awareness context that reports physical pixels on every monitor.</summary>
	private const nint PerMonitorAware = -4;

	/// <summary>Calls back once per top-level window; returning false stops the enumeration.</summary>
	internal delegate bool WindowVisitor( nint window, nint state );

	/// <summary>Switches the calling thread to per-monitor DPI awareness until the returned scope is disposed.</summary>
	internal static DpiScope EnterPhysicalPixels() => new( SetThreadDpiAwarenessContext( PerMonitorAware ) );

	/// <summary>Restores a thread's previous DPI awareness when disposed.</summary>
	/// <param name="previous">The awareness context to restore, or 0 when switching failed.</param>
	internal readonly struct DpiScope( nint previous ) : IDisposable
	{
		/// <summary>Restores the previous DPI awareness.</summary>
		public void Dispose()
		{
			if ( previous != 0 )
				_ = SetThreadDpiAwarenessContext( previous );
		}
	}

	/// <summary>Holds a rectangle in pixels.</summary>
	[StructLayout( LayoutKind.Sequential )]
	internal struct NativeRect
	{
		/// <summary>The left edge.</summary>
		public int Left;

		/// <summary>The top edge.</summary>
		public int Top;

		/// <summary>The right edge, exclusive.</summary>
		public int Right;

		/// <summary>The bottom edge, exclusive.</summary>
		public int Bottom;
	}

	/// <summary>Holds a point in pixels.</summary>
	[StructLayout( LayoutKind.Sequential )]
	internal struct NativePoint
	{
		/// <summary>The horizontal coordinate.</summary>
		public int X;

		/// <summary>The vertical coordinate.</summary>
		public int Y;
	}

	/// <summary>Holds a window's show state and restored position.</summary>
	[StructLayout( LayoutKind.Sequential )]
	internal struct WindowPlacement
	{
		/// <summary>The structure size in bytes.</summary>
		public int Length;

		/// <summary>The placement flags.</summary>
		public int Flags;

		/// <summary>The current show state.</summary>
		public int ShowCommand;

		/// <summary>The minimized position.</summary>
		public NativePoint MinPosition;

		/// <summary>The maximized position.</summary>
		public NativePoint MaxPosition;

		/// <summary>The restored window rectangle.</summary>
		public NativeRect NormalPosition;
	}

	/// <summary>Describes a 32-bit top-down device-independent bitmap.</summary>
	[StructLayout( LayoutKind.Sequential )]
	internal struct BitmapInfo
	{
		/// <summary>The header size in bytes.</summary>
		public int Size;

		/// <summary>The width in pixels.</summary>
		public int Width;

		/// <summary>The height in pixels; negative for rows stored top first.</summary>
		public int Height;

		/// <summary>The plane count, always 1.</summary>
		public short Planes;

		/// <summary>The bits per pixel.</summary>
		public short BitCount;

		/// <summary>The compression, 0 for none.</summary>
		public int Compression;

		/// <summary>The image size in bytes, 0 for uncompressed.</summary>
		public int SizeImage;

		/// <summary>The horizontal resolution.</summary>
		public int XPelsPerMeter;

		/// <summary>The vertical resolution.</summary>
		public int YPelsPerMeter;

		/// <summary>The palette size.</summary>
		public int ClrUsed;

		/// <summary>The important palette entries.</summary>
		public int ClrImportant;

		/// <summary>The single unused color table entry that completes BITMAPINFO.</summary>
		public int Colors;
	}

	/// <summary>Holds one synthesized input event.</summary>
	[StructLayout( LayoutKind.Sequential )]
	internal struct Input
	{
		/// <summary>The event type: 0 for mouse, 1 for keyboard.</summary>
		public uint Type;

		/// <summary>The event data.</summary>
		public InputData Data;
	}

	/// <summary>Overlays the mouse and keyboard event data.</summary>
	[StructLayout( LayoutKind.Explicit )]
	internal struct InputData
	{
		/// <summary>The mouse event.</summary>
		[FieldOffset( 0 )]
		public MouseInput Mouse;

		/// <summary>The keyboard event.</summary>
		[FieldOffset( 0 )]
		public KeyboardInput Keyboard;
	}

	/// <summary>Holds one synthesized mouse event.</summary>
	[StructLayout( LayoutKind.Sequential )]
	internal struct MouseInput
	{
		/// <summary>The horizontal position, normalized to 0..65535 when absolute.</summary>
		public int X;

		/// <summary>The vertical position, normalized to 0..65535 when absolute.</summary>
		public int Y;

		/// <summary>The wheel amount or extra button.</summary>
		public int MouseData;

		/// <summary>The event flags.</summary>
		public uint Flags;

		/// <summary>The timestamp, 0 for the system's.</summary>
		public uint Time;

		/// <summary>The extra information.</summary>
		public nint ExtraInfo;
	}

	/// <summary>Holds one synthesized key event.</summary>
	[StructLayout( LayoutKind.Sequential )]
	internal struct KeyboardInput
	{
		/// <summary>The virtual-key code.</summary>
		public ushort VirtualKey;

		/// <summary>The hardware scan code.</summary>
		public ushort ScanCode;

		/// <summary>The event flags.</summary>
		public uint Flags;

		/// <summary>The timestamp, 0 for the system's.</summary>
		public uint Time;

		/// <summary>The extra information.</summary>
		public nint ExtraInfo;
	}

	/// <summary>Visits every top-level window.</summary>
	[DllImport( "user32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool EnumWindows( WindowVisitor visitor, nint state );

	/// <summary>Reports whether a handle names an existing window.</summary>
	[DllImport( "user32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool IsWindow( nint window );

	/// <summary>Reports whether a window has the visible style.</summary>
	[DllImport( "user32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool IsWindowVisible( nint window );

	/// <summary>Reports whether a window is minimized.</summary>
	[DllImport( "user32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool IsIconic( nint window );

	/// <summary>Gets a related window, such as the owner.</summary>
	[DllImport( "user32.dll" )]
	internal static extern nint GetWindow( nint window, uint relation );

	/// <summary>Gets the desktop's shell window.</summary>
	[DllImport( "user32.dll" )]
	internal static extern nint GetShellWindow();

	/// <summary>Reads a 32-bit window value, such as the extended style.</summary>
	[DllImport( "user32.dll", EntryPoint = "GetWindowLongW" )]
	internal static extern int GetWindowLong( nint window, int index );

	/// <summary>Gets the length of a window's title.</summary>
	[DllImport( "user32.dll", EntryPoint = "GetWindowTextLengthW" )]
	internal static extern int GetWindowTextLength( nint window );

	/// <summary>Copies a window's title.</summary>
	[DllImport( "user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode )]
	internal static extern int GetWindowText( nint window, [Out] char[] text, int capacity );

	/// <summary>Gets the thread and process that created a window.</summary>
	[DllImport( "user32.dll" )]
	internal static extern uint GetWindowThreadProcessId( nint window, out uint process );

	/// <summary>Reads a window's show state and restored rectangle.</summary>
	[DllImport( "user32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool GetWindowPlacement( nint window, ref WindowPlacement placement );

	/// <summary>Reads a 32-bit DWM window attribute.</summary>
	[DllImport( "dwmapi.dll" )]
	internal static extern int DwmGetWindowAttribute( nint window, int attribute, out int value, int size );

	/// <summary>Gets a window's client rectangle, whose origin is always 0, 0.</summary>
	[DllImport( "user32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool GetClientRect( nint window, out NativeRect rect );

	/// <summary>Converts a client point to screen coordinates.</summary>
	[DllImport( "user32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool ClientToScreen( nint window, ref NativePoint point );

	/// <summary>Shows, hides or restores a window.</summary>
	[DllImport( "user32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool ShowWindow( nint window, int command );

	/// <summary>Gets the window with keyboard focus system-wide.</summary>
	[DllImport( "user32.dll" )]
	internal static extern nint GetForegroundWindow();

	/// <summary>Asks <see cref="GetAncestor"/> for the root window's owner chain end, so owned popups map to the window that owns them.</summary>
	internal const int RootOwner = 3;

	/// <summary>Gets an ancestor of a window, such as its root owner.</summary>
	[DllImport( "user32.dll" )]
	internal static extern nint GetAncestor( nint window, int flags );

	/// <summary>Brings a window to the foreground and gives it keyboard focus.</summary>
	[DllImport( "user32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool SetForegroundWindow( nint window );

	/// <summary>Raises a window to the top of the Z order.</summary>
	[DllImport( "user32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool BringWindowToTop( nint window );

	/// <summary>Shares or stops sharing input state between two threads.</summary>
	[DllImport( "user32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool AttachThreadInput( uint thread, uint target, [MarshalAs( UnmanagedType.Bool )] bool attach );

	/// <summary>Gets the calling thread's identifier.</summary>
	[DllImport( "kernel32.dll" )]
	internal static extern uint GetCurrentThreadId();

	/// <summary>Reads a system metric, such as the virtual screen bounds.</summary>
	[DllImport( "user32.dll" )]
	internal static extern int GetSystemMetrics( int index );

	/// <summary>Synthesizes mouse and keyboard events.</summary>
	[DllImport( "user32.dll" )]
	internal static extern uint SendInput( uint count, Input[] inputs, int size );

	/// <summary>Translates a virtual-key code to a scan code.</summary>
	[DllImport( "user32.dll", EntryPoint = "MapVirtualKeyW" )]
	internal static extern uint MapVirtualKey( uint code, uint mapType );

	/// <summary>Sets the calling thread's DPI awareness, returning the previous context.</summary>
	[DllImport( "user32.dll" )]
	internal static extern nint SetThreadDpiAwarenessContext( nint context );

	/// <summary>Gets a device context for a window, or the screen when the handle is 0.</summary>
	[DllImport( "user32.dll" )]
	internal static extern nint GetDC( nint window );

	/// <summary>Releases a device context from <see cref="GetDC"/>.</summary>
	[DllImport( "user32.dll" )]
	internal static extern int ReleaseDC( nint window, nint dc );

	/// <summary>Sends a message and waits at most a timeout for the answer; 0 when it timed out or failed.</summary>
	[DllImport( "user32.dll" )]
	internal static extern nint SendMessageTimeout( nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result );

	/// <summary>Reads a value of a window's class, such as its icon.</summary>
	[DllImport( "user32.dll", EntryPoint = "GetClassLongPtrW" )]
	internal static extern nint GetClassLongPtr( nint window, int index );

	/// <summary>Draws an icon at a size into a device context.</summary>
	[DllImport( "user32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool DrawIconEx( nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags );

	/// <summary>Draws a window into a device context.</summary>
	[DllImport( "user32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool PrintWindow( nint window, nint dc, uint flags );

	/// <summary>Creates a memory device context.</summary>
	[DllImport( "gdi32.dll" )]
	internal static extern nint CreateCompatibleDC( nint dc );

	/// <summary>Deletes a memory device context.</summary>
	[DllImport( "gdi32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool DeleteDC( nint dc );

	/// <summary>Creates a bitmap whose pixels the caller can read directly.</summary>
	[DllImport( "gdi32.dll" )]
	internal static extern nint CreateDIBSection( nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset );

	/// <summary>Selects an object into a device context, returning the one it replaced.</summary>
	[DllImport( "gdi32.dll" )]
	internal static extern nint SelectObject( nint dc, nint item );

	/// <summary>Deletes a GDI object.</summary>
	[DllImport( "gdi32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool DeleteObject( nint item );

	/// <summary>Copies pixels between device contexts.</summary>
	[DllImport( "gdi32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool BitBlt( nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation );

	/// <summary>Copies and resizes pixels between device contexts.</summary>
	[DllImport( "gdi32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool StretchBlt( nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation );

	/// <summary>Sets how StretchBlt combines pixels.</summary>
	[DllImport( "gdi32.dll" )]
	internal static extern int SetStretchBltMode( nint dc, int mode );

	/// <summary>Sets the brush origin, which halftone stretching needs after its mode is set.</summary>
	[DllImport( "gdi32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool SetBrushOrgEx( nint dc, int x, int y, nint previous );

	/// <summary>Finishes pending GDI drawing so bitmap bits can be read.</summary>
	[DllImport( "gdi32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool GdiFlush();

	/// <summary>Defines OpenProcess's right to query limited information, which a process of any integrity grants.</summary>
	internal const uint ProcessQueryLimited = 0x1000;

	/// <summary>Defines OpenProcessToken's right to query a token.</summary>
	internal const uint TokenQuery = 0x0008;

	/// <summary>Defines GetTokenInformation's class reporting whether a token is elevated.</summary>
	internal const int TokenElevationClass = 20;

	/// <summary>Opens a process.</summary>
	[DllImport( "kernel32.dll", SetLastError = true )]
	internal static extern nint OpenProcess( uint access, [MarshalAs( UnmanagedType.Bool )] bool inherit, uint process );

	/// <summary>Gets a pseudo handle of the calling process, which needs no closing.</summary>
	[DllImport( "kernel32.dll" )]
	internal static extern nint GetCurrentProcess();

	/// <summary>Closes a kernel handle.</summary>
	[DllImport( "kernel32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool CloseHandle( nint handle );

	/// <summary>Opens a process's access token.</summary>
	[DllImport( "advapi32.dll", SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool OpenProcessToken( nint process, uint access, out nint token );

	/// <summary>Reads a 32-bit piece of information about a token, such as whether it is elevated.</summary>
	[DllImport( "advapi32.dll", SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool GetTokenInformation( nint token, int informationClass, out int information, int length, out int returned );

	/// <summary>Reports whether a process runs elevated, or null when its token cannot be read, as for an elevated process read from one that is not.</summary>
	/// <param name="process">A process handle with query rights, or <see cref="GetCurrentProcess"/>.</param>
	internal static bool? IsElevated( nint process )
	{
		if ( !OpenProcessToken( process, TokenQuery, out var token ) )
			return null;

		try
		{
			return GetTokenInformation( token, TokenElevationClass, out var elevated, sizeof( int ), out _ ) ? elevated != 0 : null;
		}
		finally
		{
			_ = CloseHandle( token );
		}
	}
}
