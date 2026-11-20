using Goo;

namespace Lokn.Modules.Remote;

/// <summary>Turns pointer, wheel and key input on the frame surface into <see cref="StreamInput"/> events in frame pixels.</summary>
public sealed partial class StreamContent
{
	/// <summary>Defines the Windows wheel units of one notch.</summary>
	private const int WheelNotch = 120;

	/// <summary>Holds the virtual keys pressed through the panel and not yet released, so they are released when focus leaves.</summary>
	private readonly HashSet<int> _heldKeys = [];

	/// <summary>Stores where the frame was last drawn, or null when no frame is on screen.</summary>
	private FrameFit? _fit;

	/// <summary>Stores the last frame point a move reported, so moves go out once per frame pixel.</summary>
	private (int X, int Y) _lastMove = (-1, -1);

	/// <summary>Accumulates fractional wheel movement from precise touchpads, in wheel units.</summary>
	private double _wheel;

	/// <summary>Tracks whether a pointer button is held on the surface, so its release reaches the window even outside the frame.</summary>
	private bool _isPressed;

	/// <summary>Tracks whether the frame surface has keyboard focus.</summary>
	private bool _isFocused;

	/// <summary>Claims every key while the surface is focused and may control the host, so keys such as Ctrl+W reach the streamed window; Ctrl+Shift combinations stay with the application.</summary>
	public override bool ClaimsKey( Key key, KeyModifiers modifiers ) => _isFocused && _streaming is not null && _source.CanControl && !(modifiers.Ctrl && modifiers.Shift) && !modifiers.Super;

	/// <summary>Tracks focus and releases held keys when it leaves, so none stays down on the host.</summary>
	private void UpdateFocus( bool isFocused )
	{
		_isFocused = isFocused;
		if ( !isFocused )
			ReleaseKeys();
	}

	/// <summary>Sends a key press or release as its virtual key.</summary>
	private void UpdateKey( KeyEvent e, bool isDown )
	{
		if ( e.IsComposing || !CanSend() || ComputeVirtualKey( e.Key ) is not (> 0 and var virtualKey) )
			return;

		e.PreventDefault();
		e.StopPropagation();
		if ( isDown )
			_heldKeys.Add( virtualKey );
		else if ( !_heldKeys.Remove( virtualKey ) )
			return;

		_source.Input( new( StreamInput.Key, Vk: virtualKey, IsDown: isDown ) );
	}

	/// <summary>Focuses the surface, then moves the host pointer to the press and presses the button.</summary>
	private void UpdatePointerDown( PointerEvent e )
	{
		_surface.Focus();
		if ( !CanSend() || ComputeButton( e.Button ) is not { } button || MapPoint( e.Position, clamp: false ) is not { } point )
			return;

		e.Capture();
		_isPressed = true;
		SendMove( point );
		_source.Input( new( StreamInput.Down, point.X, point.Y, Button: button ) );
	}

	/// <summary>Moves the host pointer, once per frame pixel; while a button is held, past the frame's edge too.</summary>
	private void UpdatePointerMove( PointerEvent e )
	{
		if ( CanSend() && MapPoint( e.Position, clamp: _isPressed ) is { } point )
			SendMove( point );
	}

	/// <summary>Releases the button at the pointer, at the nearest frame edge when outside.</summary>
	private void UpdatePointerUp( PointerEvent e )
	{
		if ( !_isPressed )
			return;

		_isPressed = false;
		e.ReleaseCapture();
		if ( CanSend() && ComputeButton( e.Button ) is { } button && MapPoint( e.Position, clamp: true ) is { } point )
			_source.Input( new( StreamInput.Up, point.X, point.Y, Button: button ) );
	}

	/// <summary>Turns the host wheel at the pointer, in whole wheel units.</summary>
	private void UpdateWheel( WheelEvent e )
	{
		if ( !CanSend() || MapPoint( e.Position, clamp: false ) is not { } point )
			return;

		e.StopPropagation();
		_wheel += e.Delta.Y * WheelNotch;
		var delta = (int)Math.Truncate( _wheel );
		_wheel -= delta;
		if ( delta != 0 )
			_source.Input( new( StreamInput.Wheel, point.X, point.Y, Delta: delta ) );
	}

	/// <summary>Sends a move unless the pointer is still on the frame pixel last reported.</summary>
	private void SendMove( (double X, double Y) point )
	{
		var pixel = ((int)point.X, (int)point.Y);
		if ( pixel == _lastMove )
			return;

		_lastMove = pixel;
		_source.Input( new( StreamInput.Move, point.X, point.Y ) );
	}

	/// <summary>Maps a surface point to frame pixels, or null when no frame is on screen or the point is outside it and not clamped.</summary>
	private (double X, double Y)? MapPoint( Point position, bool clamp ) =>
		_fit is { } fit && _frame is { } frame ? fit.ComputeFramePoint( position.X, position.Y, frame.Width, frame.Height, clamp ) : null;

	/// <summary>Reports whether input may go to the host: a frame is streaming and the source has the control scope.</summary>
	private bool CanSend() => _streaming is not null && _source.CanControl;

	/// <summary>Releases every key pressed through the panel.</summary>
	private void ReleaseKeys()
	{
		if ( _streaming is not null && _source.CanControl )
		{
			foreach ( var key in _heldKeys )
				_source.Input( new( StreamInput.Key, Vk: key, IsDown: false ) );
		}

		_heldKeys.Clear();
	}

	/// <summary>Computes the DOM button number <see cref="StreamInput.Button"/> uses, or null for no button.</summary>
	internal static int? ComputeButton( PointerButton button ) => button switch
	{
		PointerButton.Primary => 0,
		PointerButton.Middle => 1,
		PointerButton.Secondary => 2,
		PointerButton.Back => 3,
		PointerButton.Forward => 4,
		_ => null,
	};

	/// <summary>Computes the Windows virtual-key code of a Goo key, or 0 for a key Windows has no code for.</summary>
	public static int ComputeVirtualKey( Key key ) => key switch
	{
		>= Key.A and <= Key.Z => 0x41 + (key - Key.A),
		>= Key.Number0 and <= Key.Number9 => 0x30 + (key - Key.Number0),
		>= Key.F1 and <= Key.F24 => 0x70 + (key - Key.F1),
		>= Key.Keypad0 and <= Key.Keypad9 => 0x60 + (key - Key.Keypad0),
		Key.Space => 0x20,
		Key.Apostrophe => 0xDE,
		Key.Comma => 0xBC,
		Key.Minus => 0xBD,
		Key.Period => 0xBE,
		Key.Slash => 0xBF,
		Key.Semicolon => 0xBA,
		Key.Equal => 0xBB,
		Key.LeftBracket => 0xDB,
		Key.BackSlash => 0xDC,
		Key.RightBracket => 0xDD,
		Key.GraveAccent => 0xC0,
		Key.World1 => 0xE2,
		Key.Escape => 0x1B,
		Key.Enter or Key.KeypadEnter => 0x0D,
		Key.Tab => 0x09,
		Key.Backspace => 0x08,
		Key.Insert => 0x2D,
		Key.Delete => 0x2E,
		Key.Right => 0x27,
		Key.Left => 0x25,
		Key.Down => 0x28,
		Key.Up => 0x26,
		Key.PageUp => 0x21,
		Key.PageDown => 0x22,
		Key.Home => 0x24,
		Key.End => 0x23,
		Key.CapsLock => 0x14,
		Key.ScrollLock => 0x91,
		Key.NumLock => 0x90,
		Key.PrintScreen => 0x2C,
		Key.Pause => 0x13,
		Key.KeypadDecimal => 0x6E,
		Key.KeypadDivide => 0x6F,
		Key.KeypadMultiply => 0x6A,
		Key.KeypadSubtract => 0x6D,
		Key.KeypadAdd => 0x6B,
		Key.ShiftLeft => 0xA0,
		Key.ShiftRight => 0xA1,
		Key.ControlLeft => 0xA2,
		Key.ControlRight => 0xA3,
		Key.AltLeft => 0xA4,
		Key.AltRight => 0xA5,
		Key.SuperLeft => 0x5B,
		Key.SuperRight => 0x5C,
		Key.Menu => 0x5D,
		_ => 0,
	};
}
