namespace Lokn.Modules.Remote;

/// <summary>Runs work from a peer's threads on the UI thread and awaits it, since application state changes only there.</summary>
internal static class UiInvoke
{
	/// <summary>Runs a function on the UI thread and completes with its result or failure.</summary>
	/// <param name="post">Posts an action to the UI thread and reports whether it was accepted.</param>
	/// <param name="function">The work.</param>
	/// <exception cref="RemoteException">The UI thread no longer takes work, such as while the module stops.</exception>
	public static Task<T> RunAsync<T>( Func<Action, bool> post, Func<T> function ) => RunAsync( post, () => Task.FromResult( function() ) );

	/// <summary>Runs an action on the UI thread and completes once it ran.</summary>
	/// <param name="post">Posts an action to the UI thread and reports whether it was accepted.</param>
	/// <param name="action">The work.</param>
	/// <exception cref="RemoteException">The UI thread no longer takes work.</exception>
	public static Task RunAsync( Func<Action, bool> post, Action action ) => RunAsync( post, () =>
	{
		action();
		return true;
	} );

	/// <summary>Starts an asynchronous function on the UI thread and completes with its result or failure.</summary>
	/// <param name="post">Posts an action to the UI thread and reports whether it was accepted.</param>
	/// <param name="function">The work; it starts on the UI thread and may continue anywhere.</param>
	/// <exception cref="RemoteException">The UI thread no longer takes work.</exception>
	public static Task<T> RunAsync<T>( Func<Action, bool> post, Func<Task<T>> function )
	{
		var completion = new TaskCompletionSource<T>( TaskCreationOptions.RunContinuationsAsynchronously );
		if ( !post( () => _ = ForwardAsync( function, completion ) ) )
			completion.TrySetException( new RemoteException( RemoteErrors.Failed, "Loom is shutting down." ) );
		return completion.Task;
	}

	/// <summary>Awaits the work and copies its outcome to the completion.</summary>
	private static async Task ForwardAsync<T>( Func<Task<T>> function, TaskCompletionSource<T> completion )
	{
		try
		{
			completion.TrySetResult( await function().ConfigureAwait( false ) );
		}
		catch ( OperationCanceledException exception )
		{
			completion.TrySetCanceled( exception.CancellationToken );
		}
		catch ( Exception exception )
		{
			completion.TrySetException( exception );
		}
	}
}
