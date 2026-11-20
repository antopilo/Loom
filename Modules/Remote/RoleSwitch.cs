namespace Lokn.Modules.Remote;

/// <summary>A move of This PC's chats between this Loom and the daemon while Loom runs (docs/remote.md, Roles): it waits until no agent works on This
/// PC, which can be cancelled, then moves the chats.</summary>
/// <param name="role">The role the chats move to: <see cref="RemoteModule.LocalRole"/> or <see cref="RemoteModule.DaemonRole"/>.</param>
/// <param name="reason">Why Loom moves them by itself, such as after the daemon kept failing, which skips the wait and is told once they moved; null when
/// the user chose.</param>
internal sealed class RoleSwitch( string role, string? reason )
{
	/// <summary>Completes once the switch did or did not happen.</summary>
	private readonly TaskCompletionSource<bool> _done = new( TaskCreationOptions.RunContinuationsAsynchronously );

	/// <summary>Gets the role the chats move to.</summary>
	public string Role => role;

	/// <summary>Gets why Loom moves the chats by itself, or null when the user chose.</summary>
	public string? Reason => reason;

	/// <summary>Gets or sets whether the switch still waits for This PC's agents to finish, when it can be cancelled; false once the chats move.</summary>
	public bool IsWaiting { get; set; } = reason is null;

	/// <summary>Gets a task that ends once the switch did or did not happen, with whether this Loom then runs in <see cref="Role"/>.</summary>
	public Task<bool> Completion => _done.Task;

	/// <summary>Ends the switch, with whether this Loom runs in <see cref="Role"/> now.</summary>
	public void Complete( bool isDone ) => _done.TrySetResult( isDone );
}

/// <summary>Counts the daemon's failures in the <c>daemon</c> role: <see cref="Limit"/> of them within <see cref="Window"/> send This PC's chats back to
/// this Loom.</summary>
internal sealed class DaemonFailures
{
	/// <summary>Defines how many failures within <see cref="Window"/> make Loom give up on the daemon.</summary>
	public const int Limit = 3;

	/// <summary>Defines how far back failures count.</summary>
	public static readonly TimeSpan Window = TimeSpan.FromMinutes( 10 );

	/// <summary>Holds when each failure still counted happened, oldest first.</summary>
	private readonly Queue<DateTimeOffset> _times = new();

	/// <summary>Records a failure and reports whether it is the <see cref="Limit"/>th within <see cref="Window"/>, which starts the count over.</summary>
	/// <param name="now">When it happened.</param>
	public bool Add( DateTimeOffset now )
	{
		while ( _times.Count > 0 && now - _times.Peek() >= Window )
			_times.Dequeue();
		_times.Enqueue( now );
		if ( _times.Count < Limit )
			return false;

		_times.Clear();
		return true;
	}

	/// <summary>Forgets every failure, such as after This PC's chats moved.</summary>
	public void Clear() => _times.Clear();
}
