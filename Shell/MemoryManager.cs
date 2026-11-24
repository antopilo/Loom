using Goo;

namespace Lokn;

/// <summary>Trims memory when Lokn loses focus and reclaims it when regaining focus, reducing background memory usage.</summary>
/// <remarks>When the window is not in focus, calling GC.Collect() and WaitForPendingFinalizers() frees unused memory back to the OS.
/// This is particularly effective for Lokn since panels with complex renderers can hold significant memory that is not needed when minimized or backgrounded.</remarks>
internal sealed class MemoryManager
{
	/// <summary>Subscribes to window focus changes and manages memory accordingly.</summary>
	internal static void Attach( Window window )
	{
		var manager = new MemoryManager();
		window.FocusChanged += manager.OnFocusChanged;
	}

	/// <summary>Trims memory when window loses focus, reclaims it when regaining focus.</summary>
	private void OnFocusChanged( bool isFocused )
	{
		if ( !isFocused )
		{
			// Window lost focus: trim memory to reduce background usage.
			TrimMemory();
		}
		// When regaining focus, normal GC will handle memory as needed; no explicit action required.
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
