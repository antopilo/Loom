using System.Runtime.InteropServices;

namespace Lokn.Modules.Remote;

/// <summary>Lists the host's screens as stream sources beside its windows: a screen is a <see cref="CaptureWindow"/> whose <c>hwnd</c> is negative,
/// -1 for the main screen, then -2, -3 for the others in the order Windows lists them.</summary>
/// <remarks>Older clients show a screen as one more window; older hosts list no screens. Bounds are in physical pixels.</remarks>
public static class ScreenCatalog
{
	/// <summary>Defines MONITORINFOF_PRIMARY, set on the main screen.</summary>
	private const uint PrimaryFlag = 1;

	/// <summary>Defines MONITOR_DEFAULTTONEAREST.</summary>
	private const uint NearestMonitor = 2;

	/// <summary>Determines whether a stream source is a screen rather than a window.</summary>
	/// <param name="source">The <c>hwnd</c> of a <c>cap.start</c>.</param>
	public static bool IsScreen( long source ) => source < 0;

	/// <summary>Lists the screens, then the windows, as <c>cap.list</c> and the stream panel offer them.</summary>
	public static IReadOnlyList<CaptureWindow> ListSources() => [.. List(), .. WindowCatalog.List()];

	/// <summary>Determines whether a stream source still exists: a connected screen or an open window.</summary>
	/// <param name="source">The <c>hwnd</c> of a <c>cap.start</c>.</param>
	public static bool Exists( long source ) => IsScreen( source ) ? Find( source ) is not null : StreamNative.IsWindow( (nint)source );

	/// <summary>Lists the connected screens, the main one first.</summary>
	public static IReadOnlyList<CaptureWindow> List() =>
		[.. ReadScreens().Select( ( screen, index ) => new CaptureWindow( -(index + 1), index == 0 ? $"Screen {index + 1} (main)" : $"Screen {index + 1}", "",
			screen.Bounds.Right - screen.Bounds.Left, screen.Bounds.Bottom - screen.Bounds.Top ) )];

	/// <summary>Finds a screen's monitor handle and bounds in physical pixels, or null when no such screen is connected.</summary>
	/// <param name="source">The screen's negative <c>hwnd</c>.</param>
	internal static (nint Monitor, StreamNative.NativeRect Bounds)? Find( long source )
	{
		if ( !IsScreen( source ) )
			return null;

		var screens = ReadScreens();
		var index = (int)(-source - 1);
		return index < screens.Count ? screens[index] : null;
	}

	/// <summary>Finds a screen's display device, such as <c>\\.\DISPLAY1</c>, whose modes <see cref="Win32DisplayModes"/> reads and sets; null when no such screen is connected.</summary>
	/// <param name="source">The screen's negative <c>hwnd</c>.</param>
	internal static string? FindDevice( long source )
	{
		if ( Find( source ) is not { } screen )
			return null;

		var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>(), Device = "" };
		return GetMonitorInfoEx( screen.Monitor, ref info ) && info.Device.Length > 0 ? info.Device : null;
	}

	/// <summary>Reads the size in physical pixels of the screen a window is on, or null when it cannot be read.</summary>
	/// <param name="window">The window, such as the one a stream panel shows in.</param>
	internal static (int Width, int Height)? ReadScreenSize( nint window )
	{
		using var dpi = StreamNative.EnterPhysicalPixels();
		var monitor = MonitorFromWindow( window, NearestMonitor );
		var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
		if ( monitor == 0 || !GetMonitorInfo( monitor, ref info ) )
			return null;

		var (width, height) = (info.Bounds.Right - info.Bounds.Left, info.Bounds.Bottom - info.Bounds.Top);
		return width > 0 && height > 0 ? (width, height) : null;
	}

	/// <summary>Reads every screen, the main one first, then in the order Windows lists them.</summary>
	private static List<(nint Monitor, StreamNative.NativeRect Bounds)> ReadScreens()
	{
		var screens = new List<(nint Monitor, StreamNative.NativeRect Bounds, bool IsMain)>();
		using var dpi = StreamNative.EnterPhysicalPixels();
		MonitorVisitor visitor = ( monitor, _, _, _ ) =>
		{
			var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
			if ( GetMonitorInfo( monitor, ref info ) )
				screens.Add( (monitor, info.Bounds, (info.Flags & PrimaryFlag) != 0) );
			return true;
		};
		_ = EnumDisplayMonitors( 0, 0, visitor, 0 );
		GC.KeepAlive( visitor );
		return [.. screens.OrderByDescending( screen => screen.IsMain ).Select( screen => (screen.Monitor, screen.Bounds) )];
	}

	/// <summary>Calls back once per screen; returning false stops the enumeration.</summary>
	private delegate bool MonitorVisitor( nint monitor, nint context, nint bounds, nint state );

	/// <summary>Holds MONITORINFO.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct MonitorInfo
	{
		/// <summary>The structure's size in bytes.</summary>
		public int Size;

		/// <summary>The screen's bounds on the virtual desktop.</summary>
		public StreamNative.NativeRect Bounds;

		/// <summary>The bounds without the taskbar.</summary>
		public StreamNative.NativeRect WorkArea;

		/// <summary>MONITORINFOF_PRIMARY on the main screen.</summary>
		public uint Flags;
	}

	/// <summary>Holds MONITORINFOEXW: MONITORINFO and the display device's name.</summary>
	[StructLayout( LayoutKind.Sequential, CharSet = CharSet.Unicode )]
	private struct MonitorInfoEx
	{
		/// <summary>The structure's size in bytes.</summary>
		public int Size;

		/// <summary>The screen's bounds on the virtual desktop.</summary>
		public StreamNative.NativeRect Bounds;

		/// <summary>The bounds without the taskbar.</summary>
		public StreamNative.NativeRect WorkArea;

		/// <summary>MONITORINFOF_PRIMARY on the main screen.</summary>
		public uint Flags;

		/// <summary>The display device, such as <c>\\.\DISPLAY1</c>.</summary>
		[MarshalAs( UnmanagedType.ByValTStr, SizeConst = 32 )]
		public string Device;
	}

	/// <summary>Visits every screen.</summary>
	[DllImport( "user32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static extern bool EnumDisplayMonitors( nint context, nint clip, MonitorVisitor visitor, nint state );

	/// <summary>Reads a screen's bounds and flags.</summary>
	[DllImport( "user32.dll", EntryPoint = "GetMonitorInfoW" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static extern bool GetMonitorInfo( nint monitor, ref MonitorInfo info );

	/// <summary>Reads a screen's bounds, flags and display device.</summary>
	[DllImport( "user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static extern bool GetMonitorInfoEx( nint monitor, ref MonitorInfoEx info );

	/// <summary>Finds the screen a window is on.</summary>
	[DllImport( "user32.dll" )]
	private static extern nint MonitorFromWindow( nint window, uint flags );
}
