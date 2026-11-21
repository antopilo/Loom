using Goo;

namespace Lokn.Core;

/// <summary>Tells views with continuous motion, such as a looping animation, a spinner or a blinking caret, whether anyone can see it, so the motion
/// runs only then and an idle window draws no frames.</summary>
/// <remarks>
/// A view is seen while the shell draws it and its window is not minimized: the shell provides <see cref="IsShown"/> false under a panel the current
/// layout hides and under the workspace while a modal window covers it. Motion that only decorates, or only says that work goes on, also waits for the
/// window to have the keyboard (<see cref="IsAttended"/>); what the user reads, such as an elapsed time, follows <see cref="IsSeen"/>.
/// Read both in Build: Goo rebuilds the view when the answer changes. Timers and animations cannot start during Build, so a view switches them with a
/// <see cref="MotionSwitch"/>.
/// </remarks>
public static class LiveMotion
{
	/// <summary>Holds whether the views under it are on screen; the window's value is true.</summary>
	public static readonly Context<bool> IsShown = new();

	/// <summary>Reports, during a view's Build, whether the view is on screen and its window is not minimized, and rebuilds the view when that changes.</summary>
	/// <param name="window">The view's window, <c>Cell.Host</c>.</param>
	public static bool IsSeen( Window window )
	{
		// Windows without the shell, such as checks and popup windows, have no provider: their views are shown.
		if ( !IsShown.TryRead( window, out _ ) )
			IsShown.Set( window, true );

		return IsShown.Read() && window.State != WindowState.Minimized;
	}

	/// <summary>Reports, during a view's Build, whether the view is seen and its window has the keyboard, and rebuilds the view when that changes.</summary>
	/// <param name="window">The view's window, <c>Cell.Host</c>.</param>
	public static bool IsAttended( Window window ) => IsSeen( window ) && window.IsFocused;
}

/// <summary>Starts and stops a view's continuous motion as <see cref="LiveMotion"/> answers change, outside Build where timers and animations can start.</summary>
/// <param name="apply">Starts the motion with true and stops it with false, on the UI thread; it runs only when the wanted state differs from the applied one.</param>
public sealed class MotionSwitch( Action<bool> apply )
{
	/// <summary>Stores the state the last build asked for, or null before the first.</summary>
	private bool? _wanted;

	/// <summary>Stores the state last applied, or null before the first.</summary>
	private bool? _applied;

	/// <summary>Asks for the motion to run or stop; call it in Build with the <see cref="LiveMotion"/> answer, and the change applies on the window's next pump.</summary>
	/// <param name="window">The view's window, <c>Cell.Host</c>.</param>
	/// <param name="isLive">Whether the motion should run.</param>
	/// <returns>The same <paramref name="isLive"/>, for the build to draw with.</returns>
	public bool Update( Window window, bool isLive )
	{
		if ( isLive != _wanted )
		{
			_wanted = isLive;
			window.Post( Apply );
		}

		return isLive;
	}

	/// <summary>Applies the wanted state when it differs from the applied one.</summary>
	private void Apply()
	{
		if ( _wanted is not { } wanted || wanted == _applied )
			return;

		_applied = wanted;
		apply( wanted );
	}
}
