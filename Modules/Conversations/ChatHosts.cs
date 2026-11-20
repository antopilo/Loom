using Goo;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;

namespace Lokn.Modules.Conversations;

/// <summary>Every host's chats; <see cref="ConversationsModule.Hosts"/>.</summary>
/// <remarks>
/// This PC's chats exist while the Conversations module runs. The Remote module adds a paired host's when it starts, before the restored layout
/// looks for them, and only re-binds them after a reload of its own; they go when the host is removed or the Conversations module stops.
/// Everything here happens on the UI thread.
/// </remarks>
public sealed class ChatHosts
{
	/// <summary>Stores the application whose hosts these are.</summary>
	private readonly LoknApplication _app;

	/// <summary>Gets the Omp service new sessions run on.</summary>
	private readonly Func<OmpService> _backend;

	/// <summary>Posts to the UI thread while the Conversations module runs.</summary>
	private readonly Func<Action, bool> _post;

	/// <summary>Maps each host's identifier to its chats, This PC's included.</summary>
	private readonly Dictionary<string, ChatHost> _hosts = new( StringComparer.Ordinal );

	/// <summary>Changes when a host is added or removed, so readers of the list rebuild.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Creates the list with This PC's chats.</summary>
	/// <param name="app">The application whose hosts these are.</param>
	/// <param name="local">This PC's chats.</param>
	/// <param name="backend">Gets the Omp service new sessions run on.</param>
	/// <param name="post">Posts to the UI thread while the Conversations module runs.</param>
	internal ChatHosts( LoknApplication app, ChatHost local, Func<OmpService> backend, Func<Action, bool> post )
	{
		_app = app;
		_backend = backend;
		_post = post;
		Local = local;
		_hosts.Add( local.Id, local );
	}

	/// <summary>Gets This PC's chats.</summary>
	public ChatHost Local
	{
		get;
	}

	/// <summary>Gets every host's chats in the order of <see cref="LoknHosts.All"/>, This PC first; reading it rebuilds the reader when a host is added or removed.</summary>
	public IReadOnlyList<ChatHost> All
	{
		get
		{
			_ = _version.Value;
			var order = _app.Hosts.All.Select( ( host, index ) => (host.Id, index) ).ToDictionary( item => item.Id, item => item.index, StringComparer.Ordinal );
			return [.. _hosts.Values.OrderBy( host => host.IsLocal ? -1 : order.GetValueOrDefault( host.Id, int.MaxValue ) )];
		}
	}

	/// <summary>Gets the chats of the host the user is on, <see cref="LoknHosts.Current"/>, else This PC's; reading it rebuilds the reader when either changes.</summary>
	public ChatHost Current => Find( _app.Hosts.Current.Id ) ?? Local;

	/// <summary>Raised on the UI thread after a host's chats were added.</summary>
	public event Action<ChatHost>? Added;

	/// <summary>Raised on the UI thread when a host's chats are removed, before its sessions stop.</summary>
	public event Action<ChatHost>? Removed;

	/// <summary>Finds a host's chats; null or <see cref="LoknHost.LocalId"/> is This PC; reading it rebuilds the reader when a host is added or removed.</summary>
	/// <returns>The host's chats, or null for a host that has none here.</returns>
	public ChatHost? Find( string? hostId )
	{
		_ = _version.Value;
		return hostId is null || hostId == LoknHost.LocalId ? Local : _hosts.GetValueOrDefault( hostId );
	}

	/// <summary>Finds a chat on its host.</summary>
	/// <returns>The chat and its host, or null when the host is unknown or does not have the conversation (yet).</returns>
	public HostedChat? Find( ChatRef chat ) =>
		Find( chat.HostId ) is { } host && host.Store.Find( chat.ConversationId ) is { } conversation ? new( host, conversation ) : null;

	/// <summary>Adds a paired host's chats: mirror stores without files (new ConversationStore( new ProjectStore( path: null ), path: null )) and a
	/// session service; idempotent per id (a reload of Remote finds the existing one).</summary>
	/// <remarks>The new host's mirrors refuse changes and its sessions stay offline until the Remote module binds them to the host's link.</remarks>
	/// <param name="host">The Core host, from <see cref="LoknHosts.Add"/>.</param>
	/// <returns>The host's chats.</returns>
	public ChatHost Add( LoknHost host )
	{
		if ( host.IsLocal )
			return Local;
		if ( _hosts.TryGetValue( host.Id, out var known ) )
			return known;

		var store = new ConversationStore( new ProjectStore( path: null ), path: null );
		var chats = new ChatHost( host, store, new ChatSessionService( _app, store, _backend, _post ), _app, _backend, _post );
		chats.Detach();
		_hosts.Add( host.Id, chats );
		_version.Value++;
		Added?.Invoke( chats );
		return chats;
	}

	/// <summary>Forgets a host's chats: panels showing them show Home, sessions stop, its Workspaces sessions (keys "@hostId") go.</summary>
	/// <remarks>The Conversations module does the panels and sessions on <see cref="Removed"/>; This PC's chats are never removed.</remarks>
	/// <param name="hostId">The paired host.</param>
	public void Remove( string hostId )
	{
		if ( hostId == LoknHost.LocalId || !_hosts.Remove( hostId, out var host ) )
			return;

		_version.Value++;
		Removed?.Invoke( host );
		host.Sessions.Dispose();
	}

	/// <summary>Stops every host's sessions when the Conversations module stops; the hosts go with the module.</summary>
	internal void StopSessions()
	{
		foreach ( var host in _hosts.Values )
			host.Sessions.Dispose();
	}
}
