namespace Lokn.Core;

/// <summary>Tells how many agents work on This PC, whether they run in this Loom or in its daemon, so the shell can hold an app update back until they
/// finish without referencing the module that runs them.</summary>
/// <remarks>The Conversations module publishes it with <see cref="ModuleBuilder.Service{T}"/>; readers resolve it with
/// <c>app.Services.Get&lt;IAgentActivity&gt;()</c> on each use, since a reload replaces it.</remarks>
public interface IAgentActivity
{
	/// <summary>Gets how many of This PC's chats have an agent working now: this Loom's own in the <c>local</c> role, the daemon's in the <c>daemon</c>
	/// role; on the UI thread.</summary>
	int Working
	{
		get;
	}

	/// <summary>Raised on the UI thread when <see cref="Working"/> may have changed.</summary>
	event Action? Changed;
}
