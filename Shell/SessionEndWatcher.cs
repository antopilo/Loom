using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lokn;

/// <summary>Tells the headless daemon when the Windows session ends, so it shuts down cleanly before logoff or shutdown ends the process.</summary>
/// <remarks>
/// Windows asks top-level windows, not processes, whether the session may end; a process with no window is simply ended. A hidden top-level
/// window on its own thread receives <c>WM_ENDSESSION</c> and holds the session open until the daemon finished shutting down. A message-only
/// window would not do: it never receives broadcast messages.
/// </remarks>
[SupportedOSPlatform( "windows" )]
internal static unsafe class SessionEndWatcher
{
	/// <summary>Defines the message that asks whether the session may end.</summary>
	private const uint QueryEndSessionMessage = 0x0011;

	/// <summary>Defines the message that tells the session is ending.</summary>
	private const uint EndSessionMessage = 0x0016;

	/// <summary>Defines the window class name.</summary>
	private const string ClassName = "LoomHostSession";

	/// <summary>Defines what the shutdown screen shows while the daemon saves.</summary>
	private const string BlockReason = "Loom is saving your chats.";

	/// <summary>Stores what runs when the session ends; it returns once the daemon shut down.</summary>
	private static Action? _ending;

	/// <summary>Starts watching on a background thread with its own hidden window and message loop.</summary>
	/// <param name="ending">Runs on the watcher's thread when the session ends; the session waits until it returns.</param>
	internal static void Start( Action ending )
	{
		_ending = ending;
		new Thread( Run ) { IsBackground = true, Name = "Loom host session" }.Start();
	}

	/// <summary>Creates the hidden window and pumps its messages; a window that cannot be created leaves the daemon without this signal.</summary>
	private static void Run()
	{
		if ( HiddenWindow.Create( ClassName, &HandleMessage ) == 0 )
			return;

		HiddenWindow.Pump();
	}

	/// <summary>Answers the session messages: the session may end, and when it does the daemon shuts down before the answer, with a reason Windows
	/// shows meanwhile, so the shutdown screen waits for it instead of ending it after a few seconds.</summary>
	[UnmanagedCallersOnly]
	private static nint HandleMessage( nint window, uint message, nint wParam, nint lParam )
	{
		if ( message == QueryEndSessionMessage )
		{
			fixed ( char* reason = BlockReason )
				ShutdownBlockReasonCreate( window, reason );
			return 1;
		}

		if ( message == EndSessionMessage )
		{
			// The process may be ended as soon as this returns, so the shutdown finishes first; nothing may escape to native code.
			try
			{
				if ( wParam != 0 )
					_ending?.Invoke();
			}
			catch ( Exception )
			{
			}
			finally
			{
				ShutdownBlockReasonDestroy( window );
			}

			return 0;
		}

		return HiddenWindow.DefWindowProcW( window, message, wParam, lParam );
	}

	/// <summary>Tells Windows why the session waits for this process.</summary>
	[DllImport( "user32.dll" )]
	private static extern int ShutdownBlockReasonCreate( nint window, char* reason );

	/// <summary>Withdraws the reason.</summary>
	[DllImport( "user32.dll" )]
	private static extern int ShutdownBlockReasonDestroy( nint window );
}
