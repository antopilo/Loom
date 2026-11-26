using Goo;

namespace Lokn;

/// <summary>Trims memory once Lokn has been in the background for a while, reducing background memory usage.</summary>
/// <remarks>The trim is a blocking, compacting full collection on the UI thread, and the heap grows back on the next use. Run at once on every focus
/// loss, it froze the window as the user switched away and slowed the first switch after they came back, so it waits until the window has stayed in
/// the background for <see cref="TrimDelay"/>, and a window that gets the focus back first is never trimmed.</remarks>
internal sealed class MemoryManager
{
	/// <summary>Defines how long the window stays in the background before its memory is trimmed.</summary>
	private static readonly TimeSpan TrimDelay = TimeSpan.FromSeconds( 30 );

	/// <summary>Stores the window, whose UI thread runs the trim.</summary>
	private readonly Window _window;

	/// <summary>Waits out the delay while the window is in the background, or null.</summary>
	private Timer? _timer;

	/// <summary>Counts focus changes, so a trim queued before the window got the focus back does not run.</summary>
	private int _generation;

	/// <summary>Creates the manager of one window.</summary>
	private MemoryManager( Window window ) => _window = window;

	/// <summary>Subscribes to window focus changes and manages memory accordingly.</summary>
	internal static void Attach( Window window )
	{
		var manager = new MemoryManager( window );
		window.FocusChanged += manager.OnFocusChanged;
	}

	/// <summary>Starts the delay when the window loses the focus, and cancels it when the window gets it back.</summary>
	private void OnFocusChanged( bool isFocused )
	{
		var generation = Interlocked.Increment( ref _generation );
		_timer?.Dispose();
		_timer = isFocused ? null : new Timer( _ => _window.Post( () =>
		{
			if ( Volatile.Read( ref _generation ) == generation )
				TrimMemory();
		} ), null, TrimDelay, Timeout.InfiniteTimeSpan );
	}

	/// <summary>Performs aggressive garbage collection to free memory when backgrounded.</summary>
	private static void TrimMemory()
	{
		try
		{
			// Full collection to free memory from panels, caches, and other resources.
			GC.Collect( GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true );
			GC.WaitForPendingFinalizers();
			GC.Collect(); // Second pass to free objects freed by finalizers.
		}
		catch
		{
			// Ignore any GC failures; memory trimming is best-effort.
		}
	}
}
