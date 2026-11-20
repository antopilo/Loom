namespace Lokn.Tests;

/// <summary>Runs window checks on a thread of their own in a single-threaded apartment, which WebView2 needs and the checks' async main thread is not.</summary>
internal static class UiThread
{
	/// <summary>Runs an action on a new single-threaded apartment thread, waits for it, and rethrows its failure here.</summary>
	internal static void Run( Action action )
	{
		Exception? failure = null;
		var thread = new Thread( () =>
		{
			try
			{
				action();
			}
			catch ( Exception error )
			{
				failure = error;
			}
		} );
		if ( OperatingSystem.IsWindows() )
			thread.SetApartmentState( ApartmentState.STA );
		thread.Start();
		thread.Join();
		if ( failure is not null )
			System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw( failure );
	}
}
