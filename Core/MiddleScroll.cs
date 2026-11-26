using Goo;

namespace Lokn.Core;

/// <summary>Scrolls a view with the middle mouse button, as Windows apps do: press it and move the pointer away from where it was pressed, and the
/// view scrolls that way, faster the farther the pointer is.</summary>
/// <remarks>
/// A click without moving keeps scrolling until the next click; a press dragged away scrolls until the button is released. Give a scrolling
/// element's container <see cref="OnPointerDown"/>, <see cref="OnPointerMove"/> and <see cref="OnPointerUp"/>; events from the content bubble to it.
/// </remarks>
/// <param name="handle">The handle of the element that scrolls.</param>
/// <param name="post">Posts to the UI thread, such as <see cref="PanelContext.Post"/>.</param>
public sealed class MiddleScroll( ElementHandle handle, Func<Action, bool> post ) : IDisposable
{
	/// <summary>Defines how far the pointer may move from where it was pressed, in logical pixels, before the view scrolls.</summary>
	private const double DeadZone = 10;

	/// <summary>Defines how far a press must be dragged, in logical pixels, to scroll only until its release rather than until the next click.</summary>
	private const double DragDistance = 6;

	/// <summary>Defines the time between scroll steps, in milliseconds: about one frame.</summary>
	private const int StepMs = 16;

	/// <summary>Defines the pixels scrolled per step for each pixel the pointer is past the dead zone.</summary>
	private const double Speed = 0.12;

	/// <summary>Defines the most pixels scrolled per step.</summary>
	private const double MaxStep = 120;

	/// <summary>Holds the timer that steps the scroll while active; null while inactive.</summary>
	private Timer? _timer;

	/// <summary>Stores where the middle button was pressed, in window coordinates.</summary>
	private Point _anchor;

	/// <summary>Stores where the pointer is now, in window coordinates.</summary>
	private Point _pointer;

	/// <summary>Stores whether the pointer moved past <see cref="DragDistance"/> since the press.</summary>
	private bool _isDragged;

	/// <summary>Stores whether a step is posted and not run yet, so a slow UI thread is not flooded.</summary>
	private int _isStepPosted;

	/// <summary>Gets whether the view is scrolling.</summary>
	public bool IsActive => _timer is not null;

	/// <summary>Starts scrolling on a middle press, or stops on any press while scrolling.</summary>
	public void OnPointerDown( PointerEvent e )
	{
		if ( IsActive )
		{
			Stop();
			return;
		}
		if ( e.Button != PointerButton.Middle )
			return;

		_anchor = _pointer = e.WindowPosition;
		_isDragged = false;
		_timer = new Timer( _ =>
		{
			if ( Interlocked.Exchange( ref _isStepPosted, 1 ) == 0 && !post( Step ) )
				_isStepPosted = 0;
		}, null, StepMs, StepMs );
	}

	/// <summary>Follows the pointer while scrolling.</summary>
	public void OnPointerMove( PointerEvent e )
	{
		if ( !IsActive )
			return;
		_pointer = e.WindowPosition;
		if ( Math.Abs( _pointer.X - _anchor.X ) > DragDistance || Math.Abs( _pointer.Y - _anchor.Y ) > DragDistance )
			_isDragged = true;
	}

	/// <summary>Stops scrolling when a dragged middle press is released.</summary>
	public void OnPointerUp( PointerEvent e )
	{
		if ( IsActive && e.Button == PointerButton.Middle && _isDragged )
			Stop();
	}

	/// <summary>Stops scrolling.</summary>
	public void Dispose() => Stop();

	/// <summary>Stops the timer.</summary>
	private void Stop()
	{
		_timer?.Dispose();
		_timer = null;
	}

	/// <summary>Scrolls one step toward the pointer, on the UI thread; stops once the view is gone.</summary>
	private void Step()
	{
		_isStepPosted = 0;
		if ( !IsActive )
			return;
		if ( !handle.IsMounted )
		{
			Stop();
			return;
		}

		var offset = handle.ScrollOffset;
		var range = handle.ScrollRange;
		var dx = ComputeStep( _pointer.X - _anchor.X );
		var dy = ComputeStep( _pointer.Y - _anchor.Y );
		if ( dx == 0 && dy == 0 )
			return;
		handle.ScrollTo(
			dx == 0 ? null : Math.Clamp( offset.X + dx, 0, range.X ),
			dy == 0 ? null : Math.Clamp( offset.Y + dy, 0, range.Y ) );
	}

	/// <summary>Gets the pixels to scroll this step for the pointer's distance from the anchor on one axis.</summary>
	private static double ComputeStep( double distance )
	{
		var past = Math.Abs( distance ) - DeadZone;
		return past <= 0 ? 0 : Math.Sign( distance ) * Math.Min( MaxStep, past * Speed );
	}
}
