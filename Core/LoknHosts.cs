namespace Lokn.Core;

/// <summary>What a host's link is doing.</summary>
public enum LoknHostState
{
	/// <summary>This PC in the local role: always available.</summary>
	Local,

	/// <summary>Dialing, starting the daemon, or not synced yet.</summary>
	Connecting,

	/// <summary>Synced; the host's <see cref="LoknHost.Workspace"/> and <see cref="LoknHost.BrowserTabs"/> are the host's.</summary>
	Connected,

	/// <summary>The last attempt failed; the link retries.</summary>
	Failed,

	/// <summary>The user stopped the daemon, or the Remote module is not running.</summary>
	Stopped,

	/// <summary>A placeholder for an identifier no longer in the list, such as one a restored layout names.</summary>
	Removed,
}

/// <summary>One machine whose chats, disk and browser tabs this Loom shows: This PC or a paired host.</summary>
/// <remarks>Everything mutates on the UI thread. <see cref="Name"/>, <see cref="State"/>, <see cref="StatusText"/> and <see cref="CanControl"/> are
/// observable: a view that reads them while it builds rebuilds when they change.</remarks>
public sealed class LoknHost
{
	/// <summary>Defines the identifier of This PC, in-process or through its daemon.</summary>
	public const string LocalId = "local";

	/// <summary>Stores the owning application, whose shell refreshes and whose current workspace follows this host.</summary>
	private readonly LoknApplication _app;

	/// <summary>Stores the observable display name.</summary>
	private readonly Goo.State<string> _name;

	/// <summary>Stores the observable link state.</summary>
	private readonly Goo.State<LoknHostState> _state;

	/// <summary>Stores the observable status line.</summary>
	private readonly Goo.State<string> _statusText;

	/// <summary>Stores whether this PC may control the host, observable.</summary>
	private readonly Goo.State<bool> _canControl;

	/// <summary>Creates a host with its workspace and browser tabs.</summary>
	/// <param name="app">The owning application.</param>
	/// <param name="id">The host identifier: <see cref="LocalId"/> or a paired host's identifier.</param>
	/// <param name="name">The name the user sees.</param>
	/// <param name="workspace">Where the host's disk, processes and terminals are reached.</param>
	/// <param name="browserTabs">The host's browser tabs.</param>
	/// <param name="state">The link state it starts in; null is <see cref="LoknHostState.Local"/> for This PC and <see cref="LoknHostState.Connecting"/> otherwise.</param>
	/// <param name="statusText">The status line it starts with.</param>
	/// <remarks>The start state is given here rather than set after, because a placeholder can be made while a view builds, when no state may be written.</remarks>
	internal LoknHost( LoknApplication app, string id, string name, IWorkspaceHost workspace, BrowserTabs browserTabs, LoknHostState? state = null, string statusText = "" )
	{
		_app = app;
		Id = id;
		_name = new( name );
		_state = new( state ?? (id == LocalId ? LoknHostState.Local : LoknHostState.Connecting) );
		_statusText = new( statusText );
		_canControl = new( id == LocalId );
		Workspace = workspace;
		BrowserTabs = browserTabs;
	}

	/// <summary>Gets the host identifier: <see cref="LocalId"/> for This PC, else the paired host's identifier.</summary>
	public string Id
	{
		get;
	}

	/// <summary>Gets whether this host is This PC.</summary>
	public bool IsLocal => Id == LocalId;

	/// <summary>Gets or sets the name the user sees, "This PC" for <see cref="LocalId"/>; setting another raises <see cref="Changed"/>. Observable.</summary>
	public string Name
	{
		get => _name.Value;
		set
		{
			ArgumentNullException.ThrowIfNull( value );
			if ( _name.Value == value )
				return;

			_name.Value = value;
			RaiseChanged();
		}
	}

	/// <summary>Gets what the host's link is doing. Observable.</summary>
	public LoknHostState State => _state.Value;

	/// <summary>Gets the link's status line, such as "Connected via 192.168.1.4:47390."; empty when there is nothing to say. Observable.</summary>
	public string StatusText => _statusText.Value;

	/// <summary>Gets whether this PC may control the host: always for This PC, for a paired host when its scopes hold control. Observable.</summary>
	public bool CanControl => _canControl.Value;

	/// <summary>Gets whether the host can be used now: This PC in the local role, or a connected host.</summary>
	public bool IsOnline => State is LoknHostState.Local or LoknHostState.Connected;

	/// <summary>Gets where the host's disk, processes and terminals are reached; never null, an <see cref="OfflineWorkspaceHost"/> while not connected.</summary>
	public IWorkspaceHost Workspace
	{
		get; private set;
	}

	/// <summary>Gets the host's browser tabs.</summary>
	public BrowserTabs BrowserTabs
	{
		get;
	}

	/// <summary>Sets the link state, its status line and whether this PC may control the host; on the UI thread. Raises <see cref="Changed"/> when any differs.</summary>
	/// <param name="state">The link state.</param>
	/// <param name="statusText">The status line.</param>
	/// <param name="canControl">Whether this PC may control the host.</param>
	public void Update( LoknHostState state, string statusText, bool canControl )
	{
		ArgumentNullException.ThrowIfNull( statusText );
		if ( _state.Value == state && _statusText.Value == statusText && _canControl.Value == canControl )
			return;

		_state.Value = state;
		_statusText.Value = statusText;
		_canControl.Value = canControl;
		RaiseChanged();
	}

	/// <summary>Replaces the workspace, such as the host's after a connect or an offline one after a disconnect; on the UI thread. Raises
	/// <see cref="WorkspaceChanged"/> when it differs.</summary>
	/// <param name="workspace">The new workspace.</param>
	public void SetWorkspace( IWorkspaceHost workspace )
	{
		ArgumentNullException.ThrowIfNull( workspace );
		if ( ReferenceEquals( Workspace, workspace ) )
			return;

		Workspace = workspace;
		WorkspaceChanged?.Invoke();
		if ( ReferenceEquals( _app.Hosts.Current, this ) )
			_app.RaiseWorkspaceChanged();
		_app.MarkChanged();
	}

	/// <summary>Raised on the UI thread after the name, state, status line or control changed.</summary>
	public event Action? Changed;

	/// <summary>Raised on the UI thread after <see cref="Workspace"/> was replaced, so panels on this host read and watch through the new one.</summary>
	public event Action? WorkspaceChanged;

	/// <summary>Reports a change to subscribers and the shell.</summary>
	private void RaiseChanged()
	{
		Changed?.Invoke();
		_app.MarkChanged();
	}

	/// <summary>Marks a host taken out of the list.</summary>
	/// <param name="statusText">Why it is gone.</param>
	internal void MarkRemoved( string statusText ) => Update( LoknHostState.Removed, statusText, canControl: false );
}

/// <summary>This PC and every paired host, and the host the user is on now; <see cref="LoknApplication.Hosts"/>.</summary>
/// <remarks>Everything mutates on the UI thread. <see cref="All"/> and <see cref="Current"/> are observable.</remarks>
public sealed class LoknHosts
{
	/// <summary>Defines the status line of a host no longer in the list.</summary>
	private const string RemovedText = "This host is no longer paired with this PC.";

	/// <summary>Stores the owning application.</summary>
	private readonly LoknApplication _app;

	/// <summary>Stores This PC and the paired hosts, This PC first, then in the order added.</summary>
	private readonly List<LoknHost> _hosts = [];

	/// <summary>Stores the placeholders of identifiers not in the list, such as removed hosts restored layouts still name.</summary>
	private readonly Dictionary<string, LoknHost> _removed = new( StringComparer.Ordinal );

	/// <summary>Counts list changes, so views reading <see cref="All"/> rebuild.</summary>
	private readonly Goo.State<int> _version = new( 0 );

	/// <summary>Stores the host the user is on now.</summary>
	private readonly Goo.State<LoknHost> _current;

	/// <summary>Creates the list with This PC.</summary>
	/// <param name="app">The owning application.</param>
	/// <param name="localTabs">This PC's browser tabs.</param>
	internal LoknHosts( LoknApplication app, BrowserTabs localTabs )
	{
		_app = app;
		Local = new( app, LoknHost.LocalId, "This PC", LocalWorkspaceHost.Shared, localTabs );
		Local.Changed += RaiseChanged;
		_hosts.Add( Local );
		_current = new( Local );
	}

	/// <summary>Gets This PC.</summary>
	public LoknHost Local
	{
		get;
	}

	/// <summary>Gets This PC first, then the paired hosts in the order added. Observable.</summary>
	public IReadOnlyList<LoknHost> All
	{
		get
		{
			_ = _version.Value;
			return [.. _hosts];
		}
	}

	/// <summary>Gets the host the user is on now: the host of the current session, This PC by default. Observable.</summary>
	public LoknHost Current => _current.Value;

	/// <summary>Makes a host the current one; null or an unknown identifier makes This PC current. Raises <see cref="Changed"/> when it differs.</summary>
	/// <param name="hostId">The host identifier.</param>
	public void SetCurrent( string? hostId )
	{
		var next = Find( hostId ) ?? Local;
		var previous = _current.Value;
		if ( ReferenceEquals( previous, next ) )
			return;

		_current.Value = next;
		RaiseChanged();
		if ( !ReferenceEquals( previous.Workspace, next.Workspace ) )
			_app.RaiseWorkspaceChanged();
	}

	/// <summary>Finds a host in the list: null or <see cref="LoknHost.LocalId"/> is This PC; an unknown identifier gives null.</summary>
	/// <param name="hostId">The host identifier.</param>
	public LoknHost? Find( string? hostId )
	{
		if ( hostId is null or LoknHost.LocalId )
			return Local;

		foreach ( var host in _hosts )
		{
			if ( host.Id == hostId )
				return host;
		}
		return null;
	}

	/// <summary>Gets a host like <see cref="Find"/>, but an unknown identifier gives a placeholder in the <see cref="LoknHostState.Removed"/> state, the
	/// same one for the same identifier, which is not in <see cref="All"/>.</summary>
	/// <param name="hostId">The host identifier.</param>
	public LoknHost Get( string? hostId )
	{
		if ( Find( hostId ) is { } host )
			return host;

		if ( _removed.TryGetValue( hostId!, out var placeholder ) )
			return placeholder;

		placeholder = new( _app, hostId!, hostId!, new OfflineWorkspaceHost( hostId! ), new BrowserTabs(), LoknHostState.Removed, RemovedText );
		_removed.Add( hostId!, placeholder );
		return placeholder;
	}

	/// <summary>Adds a paired host, or returns the one with that identifier. A new host starts <see cref="LoknHostState.Connecting"/> with an
	/// <see cref="OfflineWorkspaceHost"/> and its own browser tabs in memory.</summary>
	/// <remarks>A host removed before, or the placeholder a restored layout named (<see cref="Get"/>), comes back as the same object, so the panels,
	/// browser views and git readers built on it while it was gone follow it again instead of staying bound to a dead copy.</remarks>
	/// <param name="hostId">The paired host's identifier; never <see cref="LoknHost.LocalId"/>.</param>
	/// <param name="name">The name the user sees.</param>
	public LoknHost Add( string hostId, string name )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( hostId );
		ArgumentNullException.ThrowIfNull( name );
		if ( hostId == LoknHost.LocalId )
			throw new ArgumentException( "This PC is always in the list.", nameof( hostId ) );

		if ( Find( hostId ) is { } existing )
			return existing;

		LoknHost host;
		if ( _removed.Remove( hostId, out var removed ) )
		{
			host = removed;
			host.Name = name;
			host.SetWorkspace( new OfflineWorkspaceHost( name ) );
			host.Update( LoknHostState.Connecting, "Connecting…", canControl: false );
		}
		else
			host = new LoknHost( _app, hostId, name, new OfflineWorkspaceHost( name ), new BrowserTabs(), LoknHostState.Connecting, "Connecting…" );

		host.Changed += RaiseChanged;
		_hosts.Add( host );
		_version.Value++;
		RaiseChanged();
		return host;
	}

	/// <summary>Removes a paired host: closes its panels, stops its browser tabs and their proxy, and makes This PC current when it was.</summary>
	/// <param name="hostId">The paired host's identifier.</param>
	/// <returns>Whether the host was in the list; This PC never is removed.</returns>
	public bool Remove( string hostId )
	{
		if ( hostId == LoknHost.LocalId || Find( hostId ) is not { } host )
			return false;

		_app.Panels.CloseHost( hostId );
		host.BrowserTabs.Stop();
		host.Changed -= RaiseChanged;
		_hosts.Remove( host );
		_removed[hostId] = host;
		host.MarkRemoved( RemovedText );
		host.SetWorkspace( new OfflineWorkspaceHost( host.Name ) );
		_version.Value++;
		if ( ReferenceEquals( _current.Value, host ) )
			SetCurrent( null );
		RaiseChanged();
		Removed?.Invoke( host );
		return true;
	}

	/// <summary>Raised on the UI thread after the list, a host's name or state, or <see cref="Current"/> changed.</summary>
	public event Action? Changed;

	/// <summary>Raised on the UI thread after a paired host was removed, with the host, now in the <see cref="LoknHostState.Removed"/> state.</summary>
	public event Action<LoknHost>? Removed;

	/// <summary>Reports a change to subscribers.</summary>
	private void RaiseChanged() => Changed?.Invoke();
}

/// <summary>The workspace of a host that is not connected: every call fails, so Git, Files, Editor and Terminal never act on this PC's disk in place of
/// the host's.</summary>
/// <remarks>File and process calls throw <see cref="IOException"/>, a watch reports <see cref="WorkspaceChange.Lost"/> at once, and a terminal writes
/// why and exits with -1, as a paired host's workspace does once its connection closed (docs/remote.md, Workspace frames).</remarks>
/// <param name="hostName">The host's name, which the failures name.</param>
public sealed class OfflineWorkspaceHost( string hostName ) : IWorkspaceHost
{
	/// <summary>Gets the text the calls fail with.</summary>
	private string Message => $"{hostName} is not connected.";

	/// <summary>Fails: there is no host to run the program on.</summary>
	public Task<WorkspaceProcessResult> RunAsync( string file, IReadOnlyList<string> arguments, string directory, string? input, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken ) =>
		Task.FromException<WorkspaceProcessResult>( new IOException( Message ) );

	/// <summary>Fails: there is no host to read.</summary>
	public Task<WorkspaceStat> StatAsync( string path, CancellationToken cancellationToken ) => Task.FromException<WorkspaceStat>( new IOException( Message ) );

	/// <summary>Fails: there is no host to read.</summary>
	public Task<IReadOnlyList<WorkspaceEntry>> ListAsync( string path, CancellationToken cancellationToken ) =>
		Task.FromException<IReadOnlyList<WorkspaceEntry>>( new IOException( Message ) );

	/// <summary>Fails: there is no host to read.</summary>
	public Task<byte[]> ReadAsync( string path, CancellationToken cancellationToken ) => Task.FromException<byte[]>( new IOException( Message ) );

	/// <summary>Fails: there is no host to write.</summary>
	public Task WriteAsync( string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken ) => Task.FromException( new IOException( Message ) );

	/// <summary>Fails: there is no host to change.</summary>
	public Task DeleteAsync( string path, CancellationToken cancellationToken ) => Task.FromException( new IOException( Message ) );

	/// <summary>Fails: there is no host to change.</summary>
	public Task MoveAsync( string path, string to, CancellationToken cancellationToken ) => Task.FromException( new IOException( Message ) );

	/// <summary>Fails: there is no host to change.</summary>
	public Task CreateDirectoryAsync( string path, CancellationToken cancellationToken ) => Task.FromException( new IOException( Message ) );

	/// <summary>Reports the folder lost at once, on a background thread as a watch's changes arrive.</summary>
	public IDisposable Watch( string path, bool recursive, Action<string, WorkspaceChange> changed )
	{
		_ = Task.Run( () => changed( path, WorkspaceChange.Lost ) );
		return new Nothing();
	}

	/// <summary>Writes why no terminal opens and exits with -1, on a background thread as a terminal's output arrives.</summary>
	public IWorkspacePty OpenPty( string file, IReadOnlyList<string> arguments, string? directory, int columns, int rows, IReadOnlyDictionary<string, string>? environment, Action<string> output, Action<int> exited )
	{
		var message = Message;
		_ = Task.Run( () =>
		{
			output( message + "\r\n" );
			exited( -1 );
		} );
		return new Nothing();
	}

	/// <summary>A watch or terminal that holds nothing.</summary>
	private sealed class Nothing : IWorkspacePty
	{
		/// <summary>Ignores the input.</summary>
		public void Write( string text )
		{
		}

		/// <summary>Ignores the size.</summary>
		public void Resize( int columns, int rows )
		{
		}

		/// <summary>Holds nothing to release.</summary>
		public void Dispose()
		{
		}
	}
}
