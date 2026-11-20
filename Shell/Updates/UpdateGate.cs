namespace Lokn;

/// <summary>What asking to apply an app update does, given the agents working on This PC.</summary>
internal enum UpdateDecision
{
	/// <summary>No agent works, or the user chose to stop them: restart into the new version now.</summary>
	RestartNow,

	/// <summary>Agents work: ask whether to update when they finish, or now, which stops them.</summary>
	Ask,

	/// <summary>The user chose to update when the agents finish, and some still work: keep waiting.</summary>
	Wait,
}

/// <summary>Holds an app update back while agents work on This PC, in this Loom or its daemon, so an update never cuts a turn off unless the user
/// says so; once the user chose to update when they finish, it restarts as soon as none works.</summary>
/// <param name="working">Reads how many agents work on This PC now; on the UI thread.</param>
/// <param name="restart">Closes Loom and applies the update; on the UI thread.</param>
internal sealed class UpdateGate( Func<int> working, Action restart )
{
	/// <summary>Gets whether the user chose to update once the agents finish, and the update still waits for them.</summary>
	internal bool IsWaiting
	{
		get; private set;
	}

	/// <summary>Decides what applying an update does now.</summary>
	/// <param name="working">How many agents work on This PC.</param>
	/// <param name="isWaiting">Whether the user already chose to update when the agents finish.</param>
	/// <param name="isForced">Whether the user chose to update now, stopping the agents.</param>
	internal static UpdateDecision Decide( int working, bool isWaiting, bool isForced ) =>
		isForced || working <= 0 ? UpdateDecision.RestartNow : isWaiting ? UpdateDecision.Wait : UpdateDecision.Ask;

	/// <summary>Builds the label of the action that updates at once: <c>Update now (stops 2 agents)</c>.</summary>
	internal static string DescribeForce( int working ) => $"Update now (stops {working} {(working == 1 ? "agent" : "agents")})";

	/// <summary>Applies the update now when no agent works or <paramref name="isForced"/> is set, else tells the caller to ask or to keep waiting.</summary>
	internal UpdateDecision Request( bool isForced )
	{
		var decision = Decide( working(), IsWaiting, isForced );
		if ( decision == UpdateDecision.RestartNow )
		{
			IsWaiting = false;
			restart();
		}
		return decision;
	}

	/// <summary>Updates once no agent works: at once when none does now, else at the first <see cref="Update"/> that finds none.</summary>
	internal void WaitForAgents()
	{
		IsWaiting = true;
		Update();
	}

	/// <summary>Stops waiting for the agents, such as when the update no longer waits.</summary>
	internal void Cancel() => IsWaiting = false;

	/// <summary>Restarts into the update when the user chose to wait and no agent works any more; call it whenever the agents' work may have changed.</summary>
	/// <returns>Whether it restarted.</returns>
	internal bool Update()
	{
		if ( !IsWaiting || working() > 0 )
			return false;

		IsWaiting = false;
		restart();
		return true;
	}
}
