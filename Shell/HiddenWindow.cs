using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lokn;

/// <summary>Creates the hidden top-level windows the daemon's own threads receive Windows messages with, and pumps their messages.</summary>
/// <remarks>A top-level window that is never shown has no taskbar entry but receives broadcasts, such as the session's end and Explorer's
/// <c>TaskbarCreated</c>; a message-only window would not.</remarks>
[SupportedOSPlatform( "windows" )]
internal static unsafe class HiddenWindow
{
	/// <summary>Registers a window class with a procedure and creates one hidden top-level window of it on the calling thread.</summary>
	/// <param name="className">The class name, also the window's title; one class per name in the process.</param>
	/// <param name="procedure">The window procedure.</param>
	/// <returns>The window, or 0 when it could not be created.</returns>
	internal static nint Create( string className, delegate* unmanaged<nint, uint, nint, nint, nint> procedure )
	{
		var instance = GetModuleHandleW( 0 );
		fixed ( char* name = className )
		{
			var windowClass = new WindowClass
			{
				Size = (uint)sizeof( WindowClass ),
				Procedure = (nint)procedure,
				Instance = instance,
				ClassName = name,
			};
			if ( RegisterClassExW( &windowClass ) == 0 )
				return 0;

			return CreateWindowExW( 0, name, name, 0, 0, 0, 0, 0, 0, 0, instance, 0 );
		}
	}

	/// <summary>Dispatches the calling thread's messages until it receives the quit message.</summary>
	internal static void Pump()
	{
		Message message;
		while ( GetMessageW( &message, 0, 0, 0 ) > 0 )
			DispatchMessageW( &message );
	}

	/// <summary>Gives a message the default handling.</summary>
	[DllImport( "user32.dll" )]
	internal static extern nint DefWindowProcW( nint window, uint message, nint wParam, nint lParam );

	/// <summary>Queues a message for a window's thread; any thread may call it.</summary>
	[DllImport( "user32.dll" )]
	internal static extern int PostMessageW( nint window, uint message, nint wParam, nint lParam );

	/// <summary>Destroys a window of the calling thread.</summary>
	[DllImport( "user32.dll" )]
	internal static extern int DestroyWindow( nint window );

	/// <summary>Ends the calling thread's message loop.</summary>
	[DllImport( "user32.dll" )]
	internal static extern void PostQuitMessage( int exitCode );

	/// <summary>Gets the executable's module handle.</summary>
	[DllImport( "kernel32.dll" )]
	internal static extern nint GetModuleHandleW( nint name );

	/// <summary>Registers a window class.</summary>
	[DllImport( "user32.dll" )]
	private static extern ushort RegisterClassExW( WindowClass* windowClass );

	/// <summary>Creates a window.</summary>
	[DllImport( "user32.dll" )]
	private static extern nint CreateWindowExW( uint extendedStyle, char* className, char* title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter );

	/// <summary>Waits for the next message of this thread; returns 0 when the loop should end and -1 on failure.</summary>
	[DllImport( "user32.dll" )]
	private static extern int GetMessageW( Message* message, nint window, uint first, uint last );

	/// <summary>Sends a message to its window's procedure.</summary>
	[DllImport( "user32.dll" )]
	private static extern nint DispatchMessageW( Message* message );

	/// <summary>Mirrors <c>WNDCLASSEXW</c>.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct WindowClass
	{
		/// <summary>The structure's size in bytes.</summary>
		public uint Size;

		/// <summary>The class styles.</summary>
		public uint Style;

		/// <summary>The window procedure.</summary>
		public nint Procedure;

		/// <summary>Extra bytes after the class structure.</summary>
		public int ClassExtra;

		/// <summary>Extra bytes after each window.</summary>
		public int WindowExtra;

		/// <summary>The module that owns the class.</summary>
		public nint Instance;

		/// <summary>The large icon.</summary>
		public nint Icon;

		/// <summary>The cursor.</summary>
		public nint Cursor;

		/// <summary>The background brush.</summary>
		public nint Background;

		/// <summary>The menu resource name.</summary>
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
		/// <summary>The window the message is for.</summary>
		public nint Window;

		/// <summary>The message number.</summary>
		public uint Number;

		/// <summary>The first parameter.</summary>
		public nint WParam;

		/// <summary>The second parameter.</summary>
		public nint LParam;

		/// <summary>When the message was posted.</summary>
		public uint Time;

		/// <summary>The cursor's horizontal position when it was posted.</summary>
		public int X;

		/// <summary>The cursor's vertical position when it was posted.</summary>
		public int Y;

		/// <summary>Reserved.</summary>
		public uint Private;
	}
}
