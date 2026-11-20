using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lokn.Core;

namespace Lokn;

/// <summary>What the daemon's icon in the notification area asks of the daemon; each runs on the icon's thread.</summary>
/// <param name="Open">Opens Loom, on a panel type or null for its main window.</param>
/// <param name="IsStartAtLogin">Reads whether the daemon's sign-in entry exists.</param>
/// <param name="SetStartAtLogin">Turns the sign-in entry on or off.</param>
/// <param name="Stop">Stops the daemon.</param>
/// <param name="Log">Writes a line to the daemon's log.</param>
internal sealed record HostTrayActions( Action<string?> Open, Func<bool> IsStartAtLogin, Action<bool> SetStartAtLogin, Action Stop, Action<string> Log );

/// <summary>Shows the daemon's icon in the notification area, with its status in the tooltip and a coloured dot, its menu on a right click, Loom
/// on a left click or double-click, and a notification when a chat starts waiting for the user (docs/remote.md, Roles).</summary>
/// <remarks>
/// The icon belongs to a hidden top-level window on its own thread, per-monitor DPI aware, beside the session watcher's: a top-level window
/// receives Explorer's <c>TaskbarCreated</c> broadcast, after which the icon is added again. The daemon's loop hands each new status to
/// <see cref="Update"/>, and the icon's thread applies it; the menu's actions go back to the daemon through <see cref="HostTrayActions"/>.
/// Disposing removes the icon (<c>NIM_DELETE</c>) and ends the thread. One per process.
/// </remarks>
[SupportedOSPlatform( "windows" )]
internal sealed unsafe class HostTray : IDisposable
{
	/// <summary>Defines the window class name.</summary>
	private const string ClassName = "LoomHostTray";

	/// <summary>Defines the icon's identifier on its window.</summary>
	internal const uint IconId = 1;

	/// <summary>Defines the message the icon reports clicks with.</summary>
	private const uint CallbackMessage = 0x8001;

	/// <summary>Defines the message that applies the latest status.</summary>
	private const uint UpdateMessage = 0x8002;

	/// <summary>Defines the message that removes the icon and closes the window.</summary>
	private const uint CloseMessage = 0x8003;

	/// <summary>Defines how long disposing waits for the icon's thread, in milliseconds.</summary>
	private const int CloseTimeoutMilliseconds = 3000;

	/// <summary>Defines how long after a click a second one opens nothing more, in milliseconds, so a double-click opens Loom once.</summary>
	private const long OpenPauseMilliseconds = 1000;

	/// <summary>Stores the icon of this process, which the window procedure reaches.</summary>
	private static HostTray? _current;

	/// <summary>Stores what the icon asks of the daemon.</summary>
	private readonly HostTrayActions _actions;

	/// <summary>Stores the icon's thread.</summary>
	private readonly Thread _thread;

	/// <summary>Guards <see cref="_status"/>, <see cref="_window"/> and <see cref="_isClosed"/> between the daemon's loop and the icon's thread.</summary>
	private readonly Lock _gate = new();

	/// <summary>Stores the latest status, or null when nothing is published.</summary>
	private HostStatus? _status;

	/// <summary>Stores the hidden window once created, 0 before and after.</summary>
	private nint _window;

	/// <summary>Set once disposing asked the window to close.</summary>
	private bool _isClosed;

	/// <summary>Stores the number of Explorer's <c>TaskbarCreated</c> message; on the icon's thread.</summary>
	private uint _taskbarCreated;

	/// <summary>Stores whether the icon is in the notification area; on the icon's thread.</summary>
	private bool _isAdded;

	/// <summary>Stores whether a failed add was logged, so a missing taskbar is logged once; on the icon's thread.</summary>
	private bool _isAddFailureLogged;

	/// <summary>Stores the state last applied, so the notification shows once per change to needs you; on the icon's thread.</summary>
	private HostState? _lastState;

	/// <summary>Holds the devices whose pairing was announced or seen while Loom was connected, so each is announced at most once; on the icon's thread.</summary>
	private readonly HashSet<string> _shownPairings = new( StringComparer.Ordinal );

	/// <summary>Stores the panel the last notification opens Loom on when clicked, or null for Loom as it is; on the icon's thread.</summary>
	private string? _balloonPanel;

	/// <summary>Stores the tooltip last shown; on the icon's thread.</summary>
	private string _tip = "";

	/// <summary>Stores the icon handle shown, or 0; on the icon's thread.</summary>
	private nint _icon;

	/// <summary>Stores the size <see cref="_icon"/> was drawn at; on the icon's thread.</summary>
	private int _iconSize;

	/// <summary>Stores the state <see cref="_icon"/> shows; on the icon's thread.</summary>
	private HostState _iconState;

	/// <summary>Stores when a click last opened Loom, in <see cref="Environment.TickCount64"/> milliseconds; on the icon's thread.</summary>
	private long _lastOpen = long.MinValue / 2;

	/// <summary>Starts the icon's thread; the icon shows once <see cref="Update"/> hands it a status that asks for it.</summary>
	/// <param name="actions">What the icon asks of the daemon.</param>
	internal HostTray( HostTrayActions actions )
	{
		_actions = actions;
		_current = this;
		_thread = new Thread( Run ) { IsBackground = true, Name = "Loom host tray" };
		_thread.Start();
	}

	/// <summary>Hands the icon a new status; null or one with <see cref="HostStatus.IsTrayShown"/> off removes the icon. Any thread may call it.</summary>
	/// <param name="status">The daemon's status, or null.</param>
	internal void Update( HostStatus? status )
	{
		nint window;
		lock ( _gate )
		{
			_status = status;
			window = _window;
		}
		if ( window != 0 )
			HiddenWindow.PostMessageW( window, UpdateMessage, 0, 0 );
	}

	/// <summary>Removes the icon, closes the window and waits a while for the icon's thread to end.</summary>
	public void Dispose()
	{
		nint window;
		lock ( _gate )
		{
			_isClosed = true;
			window = _window;
		}
		if ( window != 0 )
			HiddenWindow.PostMessageW( window, CloseMessage, 0, 0 );
		if ( Thread.CurrentThread != _thread )
			_thread.Join( CloseTimeoutMilliseconds );
	}

	/// <summary>Creates the hidden window and pumps its messages until it closes, then destroys the icon handle.</summary>
	private void Run()
	{
		// The icon's size and the menu follow the taskbar's DPI rather than a scaled 96.
		SetThreadDpiAwarenessContext( PerMonitorAwareV2 );
		fixed ( char* name = "TaskbarCreated" )
			_taskbarCreated = RegisterWindowMessageW( name );
		var window = HiddenWindow.Create( ClassName, &HandleMessage );
		if ( window == 0 )
		{
			_actions.Log( "TRAY no window: the icon does not show" );
			return;
		}

		bool isClosed;
		lock ( _gate )
		{
			_window = window;
			isClosed = _isClosed;
		}
		if ( isClosed )
			Close( window );
		else
			Apply();

		HiddenWindow.Pump();
		if ( _icon != 0 )
			HostTrayIcon.DestroyIcon( _icon );
		_icon = 0;
	}

	/// <summary>Routes the window's messages to the icon; nothing may escape to native code.</summary>
	[UnmanagedCallersOnly]
	private static nint HandleMessage( nint window, uint message, nint wParam, nint lParam )
	{
		try
		{
			if ( _current?.Handle( window, message, wParam, lParam ) is { } result )
				return result;
		}
		catch ( Exception error )
		{
			_current?._actions.Log( $"ERROR tray: {error}" );
		}
		return HiddenWindow.DefWindowProcW( window, message, wParam, lParam );
	}

	/// <summary>Handles one message of the window, or returns null for the default handling.</summary>
	private nint? Handle( nint window, uint message, nint wParam, nint lParam )
	{
		switch ( message )
		{
			case CallbackMessage:
				HandleIcon( window, (uint)(lParam & 0xFFFF), wParam );
				return 0;
			case UpdateMessage:
				Apply();
				return 0;
			case CloseMessage:
				Close( window );
				return 0;
			case DestroyMessage:
				HiddenWindow.PostQuitMessage( 0 );
				return 0;
			case DisplayChangeMessage or DpiChangedMessage:
				Apply();
				return null;
		}

		if ( message == _taskbarCreated && message != 0 )
		{
			// Explorer restarted: its notification area is empty, and its DPI may differ, so the icon is drawn and added again.
			_isAdded = false;
			_iconSize = 0;
			Apply();
			return 0;
		}
		return null;
	}

	/// <summary>Handles the icon's own messages: the low word of lParam is the event, wParam the anchor point in screen pixels.</summary>
	private void HandleIcon( nint window, uint iconEvent, nint wParam )
	{
		switch ( iconEvent )
		{
			case SelectEvent or KeySelectEvent or DoubleClickEvent:
				var now = Environment.TickCount64;
				if ( now - _lastOpen < OpenPauseMilliseconds )
					return;
				_lastOpen = now;
				Open( null );
				return;
			case BalloonClickEvent:
				Open( _balloonPanel );
				return;
			case ContextMenuEvent:
				ShowMenu( window, (short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF) );
				return;
		}
	}

	/// <summary>Opens Loom, letting it take the foreground this click gave the daemon.</summary>
	private void Open( string? panel )
	{
		AllowSetForegroundWindow( AnyProcess );
		_actions.Open( panel );
	}

	/// <summary>Shows the latest status: adds, changes or removes the icon, and shows the notification when a chat starts waiting for the user or a
	/// device paired and waits for an answer while Loom is not open.</summary>
	private void Apply()
	{
		HostStatus? status;
		nint window;
		lock ( _gate )
		{
			status = _status;
			window = _window;
		}
		var previous = _lastState;
		_lastState = status?.State;
		if ( window == 0 )
			return;

		if ( status is not { IsTrayShown: true } )
		{
			Remove( window );
			return;
		}

		var replaced = DrawIcon( status.State );
		var tip = HostTrayMenu.BuildTip( status );
		var data = BuildData( window, MessageFlag | IconFlag | TipFlag | ShowTipFlag );
		data.CallbackMessage = CallbackMessage;
		data.Icon = _icon;
		Copy( tip, data.Tip, 128 );
		if ( _isAdded && (replaced != 0 || tip != _tip) && Shell_NotifyIconW( ModifyCommand, &data ) == 0 )
			_isAdded = false;
		if ( !_isAdded )
			Add( &data );
		_tip = tip;
		if ( replaced != 0 )
			HostTrayIcon.DestroyIcon( replaced );

		if ( _isAdded && status.State == HostState.NeedsYou && previous != HostState.NeedsYou )
		{
			_balloonPanel = null;
			ShowBalloon( window, HostTrayMenu.BalloonTitle, HostTrayMenu.BuildBalloon( status ) );
		}
		if ( _isAdded && HostTrayMenu.ShouldShowPairing( status, _shownPairings ) )
		{
			_balloonPanel = HostTrayMenu.RemoteAccessPanel;
			ShowBalloon( window, status.PairingBalloonTitle, status.PairingBalloon );
		}
	}

	/// <summary>Adds the icon and asks for version 4 behaviour; a taskbar that is not there yet adds it on <c>TaskbarCreated</c>.</summary>
	private void Add( NotifyIconData* data )
	{
		if ( Shell_NotifyIconW( AddCommand, data ) == 0 )
		{
			if ( !_isAddFailureLogged )
				_actions.Log( "TRAY the icon could not be added; it is added when the taskbar starts" );
			_isAddFailureLogged = true;
			return;
		}

		_isAdded = true;
		_isAddFailureLogged = false;
		data->Version = NotifyIconVersion4;
		Shell_NotifyIconW( SetVersionCommand, data );
		_actions.Log( "TRAY added" );
	}

	/// <summary>Removes the icon when it shows.</summary>
	private void Remove( nint window )
	{
		if ( !_isAdded )
			return;

		_isAdded = false;
		var data = BuildData( window, 0 );
		_actions.Log( Shell_NotifyIconW( DeleteCommand, &data ) != 0 ? "TRAY removed" : "TRAY the icon could not be removed" );
	}

	/// <summary>Removes the icon and destroys the window, which ends the thread's message loop.</summary>
	private void Close( nint window )
	{
		EndMenu();
		Remove( window );
		lock ( _gate )
			_window = 0;
		HiddenWindow.DestroyWindow( window );
	}

	/// <summary>Draws the icon for a state at the taskbar's small icon size when it differs from the one shown.</summary>
	/// <returns>The icon it replaced, to destroy once the notification area took the new one, or 0.</returns>
	private nint DrawIcon( HostState state )
	{
		var size = ComputeIconSize();
		if ( _icon != 0 && _iconState == state && _iconSize == size )
			return 0;

		var icon = HostTrayIcon.Create( HiddenWindow.GetModuleHandleW( 0 ), size, state );
		if ( icon == 0 )
			return 0;

		var replaced = _icon;
		_icon = icon;
		_iconState = state;
		_iconSize = size;
		return replaced;
	}

	/// <summary>Gets the small icon size at the taskbar's DPI, else the system's.</summary>
	private static int ComputeIconSize()
	{
		nint taskbar;
		fixed ( char* name = "Shell_TrayWnd" )
			taskbar = FindWindowW( name, null );
		var dpi = taskbar != 0 ? GetDpiForWindow( taskbar ) : 0;
		if ( dpi == 0 )
			dpi = GetDpiForSystem();
		var size = GetSystemMetricsForDpi( SmallIconWidthMetric, dpi );
		return size > 0 ? size : 16;
	}

	/// <summary>Shows a notification, such as that a chat waits for the user; Windows holds it back during quiet hours and focus.</summary>
	private static void ShowBalloon( nint window, string title, string text )
	{
		var data = BuildData( window, InfoFlag );
		Copy( title, data.InfoTitle, 64 );
		Copy( text, data.Info, 256 );
		data.InfoFlags = InfoIcon | RespectQuietTime;
		Shell_NotifyIconW( ModifyCommand, &data );
	}

	/// <summary>Shows the menu at a point and runs the chosen item; the window comes forward first so the menu closes when the user clicks away.</summary>
	private void ShowMenu( nint window, int x, int y )
	{
		HostStatus? status;
		lock ( _gate )
			status = _status;
		if ( status is null )
			return;

		var items = HostTrayMenu.Build( status, status.CanStartAtLogin && _actions.IsStartAtLogin() );
		var menu = CreatePopupMenu();
		if ( menu == 0 )
			return;

		HostTrayCommand command;
		try
		{
			foreach ( var item in items )
			{
				var flags = item.IsSeparator ? SeparatorFlag : (item.IsEnabled ? 0 : GrayedFlag) | (item.IsChecked ? CheckedFlag : 0);
				fixed ( char* text = item.Text.Replace( "&", "&&", StringComparison.Ordinal ) )
					AppendMenuW( menu, flags, (nuint)item.Command, item.IsSeparator ? null : text );
			}
			SetMenuDefaultItem( menu, (uint)HostTrayCommand.Open, 0 );
			SetForegroundWindow( window );
			var alignment = GetSystemMetrics( MenuDropAlignmentMetric ) != 0 ? RightAlignFlag : 0;
			command = (HostTrayCommand)TrackPopupMenuEx( menu, RightButtonFlag | ReturnCommandFlag | NoNotifyFlag | BottomAlignFlag | alignment, x, y, window, 0 );
			HiddenWindow.PostMessageW( window, NullMessage, 0, 0 );
		}
		finally
		{
			DestroyMenu( menu );
		}

		switch ( command )
		{
			case HostTrayCommand.Open:
				Open( null );
				break;
			case HostTrayCommand.RemoteAccess:
				Open( HostTrayMenu.RemoteAccessPanel );
				break;
			case HostTrayCommand.StartAtLogin:
				_actions.SetStartAtLogin( !items.First( item => item.Command == HostTrayCommand.StartAtLogin ).IsChecked );
				break;
			case HostTrayCommand.Stop:
				_actions.Log( "TRAY stop" );
				_actions.Stop();
				break;
		}
	}

	/// <summary>Builds the data that names the icon, with the fields a command uses.</summary>
	private static NotifyIconData BuildData( nint window, uint flags ) => new()
	{
		Size = (uint)sizeof( NotifyIconData ),
		Window = window,
		Id = IconId,
		Flags = flags,
	};

	/// <summary>Copies a text into a fixed buffer, cut to its capacity and null-terminated.</summary>
	private static void Copy( string text, char* buffer, int capacity )
	{
		var length = Math.Min( text.Length, capacity - 1 );
		text.AsSpan( 0, length ).CopyTo( new Span<char>( buffer, capacity ) );
		buffer[length] = '\0';
	}

	/// <summary>Defines <c>WM_NULL</c>, posted after the menu so it closes properly.</summary>
	private const uint NullMessage = 0x0000;

	/// <summary>Defines <c>WM_DESTROY</c>.</summary>
	private const uint DestroyMessage = 0x0002;

	/// <summary>Defines <c>WM_DISPLAYCHANGE</c>.</summary>
	private const uint DisplayChangeMessage = 0x007E;

	/// <summary>Defines <c>WM_DPICHANGED</c>.</summary>
	private const uint DpiChangedMessage = 0x02E0;

	/// <summary>Defines <c>WM_CONTEXTMENU</c>, the icon's right click or Shift+F10.</summary>
	private const uint ContextMenuEvent = 0x007B;

	/// <summary>Defines <c>WM_LBUTTONDBLCLK</c>.</summary>
	private const uint DoubleClickEvent = 0x0203;

	/// <summary>Defines <c>NIN_SELECT</c>, the icon's left click.</summary>
	private const uint SelectEvent = 0x0400;

	/// <summary>Defines <c>NIN_KEYSELECT</c>, Enter or Space on the icon.</summary>
	private const uint KeySelectEvent = 0x0401;

	/// <summary>Defines <c>NIN_BALLOONUSERCLICK</c>, a click on the notification.</summary>
	private const uint BalloonClickEvent = 0x0405;

	/// <summary>Defines <c>NIM_ADD</c>.</summary>
	private const uint AddCommand = 0;

	/// <summary>Defines <c>NIM_MODIFY</c>.</summary>
	private const uint ModifyCommand = 1;

	/// <summary>Defines <c>NIM_DELETE</c>.</summary>
	private const uint DeleteCommand = 2;

	/// <summary>Defines <c>NIM_SETVERSION</c>.</summary>
	private const uint SetVersionCommand = 4;

	/// <summary>Defines <c>NOTIFYICON_VERSION_4</c>.</summary>
	private const uint NotifyIconVersion4 = 4;

	/// <summary>Defines <c>NIF_MESSAGE</c>.</summary>
	private const uint MessageFlag = 0x01;

	/// <summary>Defines <c>NIF_ICON</c>.</summary>
	private const uint IconFlag = 0x02;

	/// <summary>Defines <c>NIF_TIP</c>.</summary>
	private const uint TipFlag = 0x04;

	/// <summary>Defines <c>NIF_INFO</c>.</summary>
	private const uint InfoFlag = 0x10;

	/// <summary>Defines <c>NIF_SHOWTIP</c>, which version 4 needs for the standard tooltip.</summary>
	private const uint ShowTipFlag = 0x80;

	/// <summary>Defines <c>NIIF_INFO</c>.</summary>
	private const uint InfoIcon = 0x01;

	/// <summary>Defines <c>NIIF_RESPECT_QUIET_TIME</c>.</summary>
	private const uint RespectQuietTime = 0x80;

	/// <summary>Defines <c>MF_GRAYED</c>.</summary>
	private const uint GrayedFlag = 0x0001;

	/// <summary>Defines <c>MF_CHECKED</c>.</summary>
	private const uint CheckedFlag = 0x0008;

	/// <summary>Defines <c>MF_SEPARATOR</c>.</summary>
	private const uint SeparatorFlag = 0x0800;

	/// <summary>Defines <c>TPM_RIGHTBUTTON</c>.</summary>
	private const uint RightButtonFlag = 0x0002;

	/// <summary>Defines <c>TPM_RIGHTALIGN</c>.</summary>
	private const uint RightAlignFlag = 0x0008;

	/// <summary>Defines <c>TPM_BOTTOMALIGN</c>.</summary>
	private const uint BottomAlignFlag = 0x0020;

	/// <summary>Defines <c>TPM_NONOTIFY</c>.</summary>
	private const uint NoNotifyFlag = 0x0080;

	/// <summary>Defines <c>TPM_RETURNCMD</c>.</summary>
	private const uint ReturnCommandFlag = 0x0100;

	/// <summary>Defines <c>SM_MENUDROPALIGNMENT</c>.</summary>
	private const int MenuDropAlignmentMetric = 40;

	/// <summary>Defines <c>SM_CXSMICON</c>.</summary>
	private const int SmallIconWidthMetric = 49;

	/// <summary>Defines <c>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2</c>.</summary>
	private const nint PerMonitorAwareV2 = -4;

	/// <summary>Defines <c>ASFW_ANY</c>.</summary>
	private const uint AnyProcess = uint.MaxValue;

	/// <summary>Adds, changes or removes a notification-area icon.</summary>
	[DllImport( "shell32.dll" )]
	private static extern int Shell_NotifyIconW( uint command, NotifyIconData* data );

	/// <summary>Gets the number of a registered message, here <c>TaskbarCreated</c>.</summary>
	[DllImport( "user32.dll" )]
	private static extern uint RegisterWindowMessageW( char* name );

	/// <summary>Sets the calling thread's DPI awareness.</summary>
	[DllImport( "user32.dll" )]
	private static extern nint SetThreadDpiAwarenessContext( nint context );

	/// <summary>Gets a window's DPI.</summary>
	[DllImport( "user32.dll" )]
	private static extern uint GetDpiForWindow( nint window );

	/// <summary>Gets the system DPI.</summary>
	[DllImport( "user32.dll" )]
	private static extern uint GetDpiForSystem();

	/// <summary>Gets a system metric at a DPI.</summary>
	[DllImport( "user32.dll" )]
	private static extern int GetSystemMetricsForDpi( int index, uint dpi );

	/// <summary>Gets a system metric.</summary>
	[DllImport( "user32.dll" )]
	private static extern int GetSystemMetrics( int index );

	/// <summary>Finds a top-level window by class.</summary>
	[DllImport( "user32.dll" )]
	private static extern nint FindWindowW( char* className, char* title );

	/// <summary>Lets another process take the foreground.</summary>
	[DllImport( "user32.dll" )]
	private static extern int AllowSetForegroundWindow( uint processId );

	/// <summary>Brings a window to the foreground.</summary>
	[DllImport( "user32.dll" )]
	private static extern int SetForegroundWindow( nint window );

	/// <summary>Creates an empty popup menu.</summary>
	[DllImport( "user32.dll" )]
	private static extern nint CreatePopupMenu();

	/// <summary>Appends an item to a menu.</summary>
	[DllImport( "user32.dll" )]
	private static extern int AppendMenuW( nint menu, uint flags, nuint id, char* text );

	/// <summary>Shows an item in bold as the menu's default.</summary>
	[DllImport( "user32.dll" )]
	private static extern int SetMenuDefaultItem( nint menu, uint id, uint byPosition );

	/// <summary>Shows a popup menu and returns the chosen item's identifier, or 0.</summary>
	[DllImport( "user32.dll" )]
	private static extern int TrackPopupMenuEx( nint menu, uint flags, int x, int y, nint window, nint parameters );

	/// <summary>Destroys a menu.</summary>
	[DllImport( "user32.dll" )]
	private static extern int DestroyMenu( nint menu );

	/// <summary>Closes the menu this thread shows, if any.</summary>
	[DllImport( "user32.dll" )]
	private static extern int EndMenu();

	/// <summary>Mirrors <c>NOTIFYICONDATAW</c>.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct NotifyIconData
	{
		/// <summary>The structure's size in bytes.</summary>
		public uint Size;

		/// <summary>The window that receives the icon's messages.</summary>
		public nint Window;

		/// <summary>The icon's identifier on its window.</summary>
		public uint Id;

		/// <summary>Which fields are valid.</summary>
		public uint Flags;

		/// <summary>The message the icon reports clicks with.</summary>
		public uint CallbackMessage;

		/// <summary>The icon.</summary>
		public nint Icon;

		/// <summary>The tooltip.</summary>
		public fixed char Tip[128];

		/// <summary>The icon's state.</summary>
		public uint State;

		/// <summary>Which state bits are valid.</summary>
		public uint StateMask;

		/// <summary>The notification's text.</summary>
		public fixed char Info[256];

		/// <summary>The behaviour version for <c>NIM_SETVERSION</c>; the timeout before Vista.</summary>
		public uint Version;

		/// <summary>The notification's title.</summary>
		public fixed char InfoTitle[64];

		/// <summary>The notification's icon and options.</summary>
		public uint InfoFlags;

		/// <summary>The icon's GUID, unused.</summary>
		public Guid Item;

		/// <summary>The notification's own icon, unused.</summary>
		public nint BalloonIcon;
	}
}
