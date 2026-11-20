using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Lokn.Modules.Remote;

/// <summary>Describes one window the host can stream, as listed by <c>cap.list</c>.</summary>
/// <param name="Hwnd">The window handle, the <c>hwnd</c> a client passes to <c>cap.start</c>.</param>
/// <param name="Title">The window title.</param>
/// <param name="Process">The name of the process that owns the window, without extension.</param>
/// <param name="Width">The client area width in physical pixels, serialized as <c>w</c>.</param>
/// <param name="Height">The client area height in physical pixels, serialized as <c>h</c>.</param>
public sealed record CaptureWindow(
	long Hwnd,
	string Title,
	string Process,
	[property: JsonPropertyName( "w" )] int Width,
	[property: JsonPropertyName( "h" )] int Height );

/// <summary>Lists the host's visible top-level windows, the ones Alt+Tab would offer.</summary>
public static class WindowCatalog
{
	/// <summary>Lists visible, titled, uncloaked top-level windows that are not tool windows, top of the Z order first.</summary>
	/// <remarks>A minimized window is listed with its restored size, since starting a stream restores it.</remarks>
	public static IReadOnlyList<CaptureWindow> List()
	{
		var windows = new List<CaptureWindow>();
		var processes = new Dictionary<uint, string>();
		var shell = StreamNative.GetShellWindow();
		using var dpi = StreamNative.EnterPhysicalPixels();
		StreamNative.WindowVisitor visitor = ( window, _ ) =>
		{
			if ( window != shell && ReadWindow( window, processes ) is { } entry )
				windows.Add( entry );

			return true;
		};
		_ = StreamNative.EnumWindows( visitor, 0 );
		GC.KeepAlive( visitor );
		return windows;
	}

	/// <summary>Reads one window's entry, or null when it is hidden, cloaked, untitled, a tool window, an owned dialog, or has no area.</summary>
	private static CaptureWindow? ReadWindow( nint window, Dictionary<uint, string> processes )
	{
		if ( !StreamNative.IsWindowVisible( window ) )
			return null;

		var style = StreamNative.GetWindowLong( window, StreamNative.ExtendedStyleIndex );
		if ( (style & StreamNative.ToolWindowStyle) != 0 )
			return null;

		// Owned windows, such as dialogs, belong to their owner unless they ask for a taskbar button.
		if ( StreamNative.GetWindow( window, StreamNative.OwnerRelation ) != 0 && (style & StreamNative.AppWindowStyle) == 0 )
			return null;

		// Windows on another virtual desktop and suspended Store apps are visible but cloaked.
		if ( StreamNative.DwmGetWindowAttribute( window, StreamNative.CloakedAttribute, out var cloaked, sizeof( int ) ) == 0 && cloaked != 0 )
			return null;

		var title = ReadTitle( window );
		if ( title.Length == 0 )
			return null;

		var (width, height) = ReadSize( window );
		if ( width <= 0 || height <= 0 )
			return null;

		_ = StreamNative.GetWindowThreadProcessId( window, out var process );
		return new( window, title, ReadProcessName( process, processes ), width, height );
	}

	/// <summary>Reads a window's title, or empty when it has none.</summary>
	private static string ReadTitle( nint window )
	{
		var length = StreamNative.GetWindowTextLength( window );
		if ( length <= 0 )
			return "";

		var buffer = new char[length + 1];
		var copied = StreamNative.GetWindowText( window, buffer, buffer.Length );
		return new string( buffer, 0, Math.Clamp( copied, 0, length ) ).Trim();
	}

	/// <summary>Reads a window's client size, or its restored outer size while it is minimized.</summary>
	private static (int Width, int Height) ReadSize( nint window )
	{
		if ( StreamNative.IsIconic( window ) )
		{
			var placement = new StreamNative.WindowPlacement { Length = Marshal.SizeOf<StreamNative.WindowPlacement>() };
			if ( !StreamNative.GetWindowPlacement( window, ref placement ) )
				return (0, 0);

			var normal = placement.NormalPosition;
			return (normal.Right - normal.Left, normal.Bottom - normal.Top);
		}

		return StreamNative.GetClientRect( window, out var rect ) ? (rect.Right - rect.Left, rect.Bottom - rect.Top) : (0, 0);
	}

	/// <summary>Reads a process name once per process, or empty when the process is gone or protected.</summary>
	private static string ReadProcessName( uint id, Dictionary<uint, string> processes )
	{
		if ( processes.TryGetValue( id, out var name ) )
			return name;

		try
		{
			using var process = Process.GetProcessById( (int)id );
			name = process.ProcessName;
		}
		catch ( Exception error ) when ( error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception )
		{
			name = "";
		}

		processes[id] = name;
		return name;
	}
}
