using Goo;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;

namespace Lokn.Modules.Conversations;

/// <summary>One host's chats: its Core host, its conversation and project stores, and its chat sessions.</summary>
/// <remarks>
/// This PC's are the Conversations module's own stores; a paired host's are mirrors without files that the Remote module fills from the host and
/// binds to its link. A paired host's mirrors refuse changes and its sessions are offline proxies until then, and again after <see cref="Detach"/>.
/// </remarks>
public sealed class ChatHost
{
	/// <summary>Stores the application the offline sessions use.</summary>
	private readonly LoknApplication _app;

	/// <summary>Gets the Omp service the offline sessions report models with.</summary>
	private readonly Func<OmpService> _backend;

	/// <summary>Posts to the UI thread while the Conversations module runs.</summary>
	private readonly Func<Action, bool> _post;

	/// <summary>Holds whether the host's chats arrived in this process.</summary>
	private readonly State<bool> _isSynced;

	/// <summary>Creates one host's chats.</summary>
	/// <param name="host">The Core host.</param>
	/// <param name="store">The host's conversations, with its projects attached.</param>
	/// <param name="sessions">The host's chat sessions.</param>
	/// <param name="app">The application the offline sessions use.</param>
	/// <param name="backend">Gets the Omp service the offline sessions report models with.</param>
	/// <param name="post">Posts to the UI thread while the Conversations module runs.</param>
	internal ChatHost( LoknHost host, ConversationStore store, ChatSessionService sessions, LoknApplication app, Func<OmpService> backend, Func<Action, bool> post )
	{
		Host = host;
		Store = store;
		Sessions = sessions;
		_app = app;
		_backend = backend;
		_post = post;
		_isSynced = new( host.IsLocal );
	}

	/// <summary>Gets the Core host: its name, link state, workspace and browser tabs.</summary>
	public LoknHost Host
	{
		get;
	}

	/// <summary>Gets the host's identifier: <see cref="LoknHost.LocalId"/> for This PC.</summary>
	public string Id => Host.Id;

	/// <summary>Gets whether the host is This PC.</summary>
	public bool IsLocal => Host.IsLocal;

	/// <summary>Gets the host's display name; reading it rebuilds the reader when it changes.</summary>
	public string Name => Host.Name;

	/// <summary>Gets the host's conversations.</summary>
	public ConversationStore Store
	{
		get;
	}

	/// <summary>Gets the host's projects.</summary>
	public ProjectStore Projects => Store.ProjectStore;

	/// <summary>Gets each of the host's conversations' chat session.</summary>
	public ChatSessionService Sessions
	{
		get;
	}

	/// <summary>Gets whether the host's chats are here: always for This PC, for a paired host once its first snapshot arrived in this process; reading it rebuilds the reader when it changes.</summary>
	public bool IsSynced => _isSynced.Value;

	/// <summary>Raised on the UI thread after each snapshot of the host's chats was applied.</summary>
	public event Action? Synced;

	/// <summary>Marks the host's chats as arrived; the Remote client calls it on the UI thread after each applied snapshot, so panels waiting for a chat load it.</summary>
	public void MarkSynced()
	{
		_isSynced.Value = true;
		Synced?.Invoke();
	}

	/// <summary>Leaves the mirror refusing changes and the sessions as offline proxies, with no delegate of the Remote module's
	/// assembly left behind, so a reload of Remote can unload it.</summary>
	/// <remarks>Changing the sessions' source stops every session; the Conversations module shows its open chats again over offline ones.</remarks>
	public void Detach()
	{
		Store.Forward = OfflineForward;
		Projects.Forward = OfflineForward;
		Sessions.Source = id => new RemoteChatSession( _app, Store, id, _backend(), OfflineChatChannel.Instance, _post );
	}

	/// <summary>Gets a chat of this host.</summary>
	/// <param name="conversationId">The conversation.</param>
	public ChatRef Ref( Guid conversationId ) => new( Id, conversationId );

	/// <summary>Refuses to send a change while no host takes it; the mirror keeps it until the next snapshot replaces it.</summary>
	private static bool OfflineForward( StoreOp op ) => false;
}

/// <summary>A conversation and the host it lives on.</summary>
/// <param name="Host">The host.</param>
/// <param name="Conversation">The conversation.</param>
public sealed record HostedChat( ChatHost Host, ConversationInfo Conversation )
{
	/// <summary>Gets the chat's reference.</summary>
	public ChatRef Ref => Host.Ref( Conversation.Id );
}
