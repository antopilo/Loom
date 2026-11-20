using Lokn.Core;

namespace Lokn.Modules.Conversations;

/// <summary>Counts the agents working on This PC from its conversation store: this Loom's own sessions in the <c>local</c> role, and in the <c>daemon</c>
/// role the daemon's, which its link mirrors into the same store.</summary>
internal sealed class LocalAgentActivity : IAgentActivity
{
	/// <summary>Stores This PC's conversations.</summary>
	private readonly ConversationStore _store;

	/// <summary>Creates the counter over This PC's conversations; it lives as long as the store, which belongs to the same module instance.</summary>
	/// <param name="store">This PC's conversations.</param>
	public LocalAgentActivity( ConversationStore store )
	{
		_store = store;
		store.WorkingChanged += ( _, _ ) => Changed?.Invoke();
	}

	/// <summary>Raised on the UI thread when a chat's agent starts or stops working.</summary>
	public event Action? Changed;

	/// <summary>Gets how many of This PC's chats have an agent working now.</summary>
	public int Working => _store.WorkingCount;
}
