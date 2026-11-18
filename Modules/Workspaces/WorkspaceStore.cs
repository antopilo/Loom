using System.Text.Json;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Workspaces;

/// <summary>Names one saved arrangement of panels.</summary>
/// <param name="Id">The stable workspace identifier.</param>
/// <param name="Name">The name shown on its tile.</param>
/// <param name="Layout">The layout captured when the user last left it, from <see cref="PanelWorkspace.CaptureLayout"/>; null until then.</param>
/// <param name="IsPinned">Whether the user pinned it, which lists it first; files written before pinning have no such field.</param>
/// <param name="LastUsed">When it was last on screen: set when it becomes current and when the user leaves it; files written before have no such field.</param>
internal sealed record SavedWorkspace( Guid Id, string Name, JsonElement? Layout, bool IsPinned = false, DateTimeOffset? LastUsed = null );

/// <summary>Describes a session: Home, a project, or the chats outside projects, each with its own workspaces.</summary>
/// <param name="Key">The stable session key, such as home or project:&lt;id&gt;.</param>
/// <param name="Title">The name the session was last opened under.</param>
/// <param name="LastOpened">When the user last opened it.</param>
public sealed record WorkspaceSessionInfo( string Key, string Title, DateTimeOffset LastOpened );

/// <summary>Describes a task: one saved workspace of a session, a project's or the one outside projects.</summary>
/// <param name="Id">The stable workspace identifier.</param>
/// <param name="Name">The task's name.</param>
/// <param name="IsPinned">Whether the user pinned it, which lists it first.</param>
/// <param name="LastUsed">When it was last on screen; a task saved before this was kept counts as used when its session was last opened if the session shows it, otherwise never.</param>
public sealed record WorkspaceTask( Guid Id, string Name, bool IsPinned = false, DateTimeOffset LastUsed = default )
{
	/// <summary>Gets the types of the panels it shows, in layout order without repeats, such as <c>terminal.shell</c>.</summary>
	public IReadOnlyList<string> PanelTypes { get; init; } = [];
}

/// <summary>Owns the sessions and their saved workspaces, and switches the panel layout between them, on the UI thread.</summary>
/// <remarks>
/// The current workspace of the current session is the live layout, which Lokn saves on its own; a workspace's stored
/// layout is refreshed whenever the user leaves it, by switching workspace or session. Switching keeps every panel open:
/// panels the new layout leaves out are hidden, and panels both layouts share slide to their new place.
/// </remarks>
internal sealed class WorkspaceStore( string path )
{
	/// <summary>Defines the supported document version.</summary>
	private const int DocumentVersion = 2;

	/// <summary>Defines the version that held one list of workspaces, read and moved into <see cref="LegacySession"/>.</summary>
	private const int LegacyVersion = 1;

	/// <summary>Defines the session a first start opens in.</summary>
	internal const string DefaultSession = "home";

	/// <summary>Defines the session that receives the workspaces of a version 1 file: the chats outside projects.</summary>
	internal const string LegacySession = "chats";

	/// <summary>Defines the longest workspace name.</summary>
	internal const int NameLimit = 40;

	/// <summary>Gets the per-user workspaces file.</summary>
	internal static string DefaultPath => Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "Lokn", "workspaces.json" );

	/// <summary>Rebuilds the views that read the workspaces when they change.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Stores the sessions by key.</summary>
	private readonly Dictionary<string, Session> _sessions = new( StringComparer.Ordinal );

	/// <summary>Stores the current session's key.</summary>
	private string _session = DefaultSession;

	/// <summary>Stores each profile's current session, so switching profile returns to where that profile was.</summary>
	private readonly Dictionary<SettingsProfile, string> _profiles = [];

	/// <summary>Reports a file that failed to read, which this session must not overwrite.</summary>
	private bool _isInvalid;

	/// <summary>Gets the current session's workspaces in tile order, subscribing the building view.</summary>
	internal IReadOnlyList<SavedWorkspace> Items
	{
		get
		{
			_ = _version.Value;
			return Active.Items.ToArray();
		}
	}

	/// <summary>Gets the current session's current workspace, subscribing the building view.</summary>
	internal Guid Current
	{
		get
		{
			_ = _version.Value;
			return Active.Current;
		}
	}

	/// <summary>Gets the current session's key, subscribing the building view.</summary>
	internal string SessionKey
	{
		get
		{
			_ = _version.Value;
			return _session;
		}
	}

	/// <summary>Gets every session, most recently opened first, subscribing the building view.</summary>
	internal IReadOnlyList<WorkspaceSessionInfo> Sessions
	{
		get
		{
			_ = _version.Value;
			return _sessions.Select( pair => new WorkspaceSessionInfo( pair.Key, pair.Value.Title, pair.Value.LastOpened ) ).OrderByDescending( session => session.LastOpened ).ToArray();
		}
	}

	/// <summary>Gets a session's workspaces in tile order, or none when it was never opened; subscribes the building view.</summary>
	internal IReadOnlyList<SavedWorkspace> ItemsOf( string key )
	{
		_ = _version.Value;
		return _sessions.TryGetValue( key, out var session ) ? session.Items.ToArray() : [];
	}

	/// <summary>Gets the workspace a session shows when it opens, or empty when it was never opened; subscribes the building view.</summary>
	internal Guid CurrentOf( string key )
	{
		_ = _version.Value;
		return _sessions.TryGetValue( key, out var session ) ? session.Current : Guid.Empty;
	}

	/// <summary>Gets when a session was last opened, or never when it was not; subscribes the building view.</summary>
	internal DateTimeOffset LastOpenedOf( string key )
	{
		_ = _version.Value;
		return _sessions.TryGetValue( key, out var session ) ? session.LastOpened : DateTimeOffset.MinValue;
	}

	/// <summary>Gets a profile's current session, or null when that profile has not opened one; subscribes the building view.</summary>
	internal string? SessionFor( SettingsProfile profile )
	{
		_ = _version.Value;
		return _profiles.GetValueOrDefault( profile );
	}

	/// <summary>Gets the latest read or write failure, or null.</summary>
	internal string? LastError
	{
		get; private set;
	}

	/// <summary>Gets the current session.</summary>
	private Session Active => _sessions[_session];

	/// <summary>Reads the saved sessions; with none, the current layout becomes Home's first workspace.</summary>
	internal void Load()
	{
		_sessions.Clear();
		_profiles.Clear();
		_session = DefaultSession;
		try
		{
			if ( File.Exists( path ) )
				Read( File.ReadAllText( path ) );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException )
		{
			// The file stays as it is so nothing the user saved is lost; this session keeps its workspaces in memory.
			_isInvalid = true;
			_sessions.Clear();
			_session = DefaultSession;
			LastError = $"Could not read {path}: {error.Message}";
		}

		EnsureSession( _session, "Home" );
		_version.Value++;
	}

	/// <summary>Records the current session as a profile's, when that profile has none yet, such as on a first start.</summary>
	internal void EnsureProfile( SettingsProfile profile ) => _profiles.TryAdd( profile, _session );

	/// <summary>Leaves the current session for another, keeping its live layout, and shows the other session's current workspace.</summary>
	/// <param name="panels">The panel workspace.</param>
	/// <param name="key">The session to open.</param>
	/// <param name="title">The session's name, refreshed on every opening.</param>
	/// <param name="profile">The profile opening it, which remembers it as its current session.</param>
	/// <param name="createLayout">Builds the layout of a session that has none yet, or whose layout could not be applied.</param>
	internal void OpenSession( PanelWorkspace panels, string key, string title, SettingsProfile profile, Action createLayout )
	{
		var now = DateTimeOffset.UtcNow;
		if ( key != _session )
		{
			var leaving = panels.CaptureLayout();
			Replace( Active, Active.Current, item => item with { Layout = leaving, LastUsed = now } );
			var target = EnsureSession( key, title );
			_session = key;
			var workspace = target.Items.First( item => item.Id == target.Current );
			if ( workspace.Layout is not { } layout || !panels.ApplyLayout( layout ) )
				createLayout();
		}

		Active.Title = title;
		Active.LastOpened = now;
		Replace( Active, Active.Current, item => item with { LastUsed = now } );
		_profiles[profile] = key;
		Save();
	}

	/// <summary>Forgets a session and its workspaces, such as a deleted conversation's; the current session cannot be forgotten.</summary>
	/// <returns>Whether the session was forgotten.</returns>
	internal bool RemoveSession( string key )
	{
		if ( key == _session || !_sessions.Remove( key ) )
			return false;

		foreach ( var profile in _profiles.Where( pair => pair.Value == key ).Select( pair => pair.Key ).ToArray() )
			_profiles.Remove( profile );
		Save();
		return true;
	}

	/// <summary>Leaves the current workspace for another of the session, keeping the one it leaves as it is now.</summary>
	/// <returns>Whether the other workspace is now current.</returns>
	internal bool Switch( PanelWorkspace panels, Guid id )
	{
		var session = Active;
		if ( id == session.Current )
			return true;

		var index = session.Items.FindIndex( item => item.Id == id );
		if ( index < 0 )
			return false;

		var target = session.Items[index];
		var leaving = panels.CaptureLayout();
		if ( target.Layout is { } layout && !panels.ApplyLayout( layout ) )
			return false;

		var now = DateTimeOffset.UtcNow;
		Replace( session, session.Current, item => item with { Layout = leaving, LastUsed = now } );
		Replace( session, id, item => item with { LastUsed = now } );
		session.Current = id;
		Save();
		return true;
	}

	/// <summary>Saves the current layout as a new workspace of the session and makes it current; nothing on screen changes.</summary>
	internal Guid Create( PanelWorkspace panels, string? name = null )
	{
		var session = Active;
		var layout = panels.CaptureLayout();
		var now = DateTimeOffset.UtcNow;
		Replace( session, session.Current, item => item with { Layout = layout, LastUsed = now } );
		var created = new SavedWorkspace( Guid.NewGuid(), ComputeName( session, name ), layout, LastUsed: now );
		session.Items.Add( created );
		session.Current = created.Id;
		Save();
		return created.Id;
	}

	/// <summary>Renames a workspace of the current session; a blank name keeps the old one.</summary>
	internal void Rename( Guid id, string name ) => Rename( _session, id, name );

	/// <summary>Renames a workspace of any session; a blank name keeps the old one, and an unknown session changes nothing.</summary>
	internal void Rename( string key, Guid id, string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) || !_sessions.TryGetValue( key, out var session ) )
			return;

		Replace( session, id, item => item with { Name = Trim( name ) } );
		Save();
	}

	/// <summary>Pins or unpins a workspace of any session; an unknown session changes nothing.</summary>
	internal void SetPinned( string key, Guid id, bool isPinned )
	{
		if ( !_sessions.TryGetValue( key, out var session ) )
			return;

		Replace( session, id, item => item with { IsPinned = isPinned } );
		Save();
	}

	/// <summary>Deletes a workspace of the current session, first switching away from it when it is current; the last workspace stays.</summary>
	/// <returns>Whether the workspace was deleted.</returns>
	internal bool Delete( PanelWorkspace panels, Guid id ) => Delete( panels, _session, id );

	/// <summary>Deletes a workspace of any session; the last workspace of a session stays.</summary>
	/// <remarks>The workspace on screen is first switched away from; another session's current workspace passes to its neighbor.</remarks>
	/// <returns>Whether the workspace was deleted.</returns>
	internal bool Delete( PanelWorkspace panels, string key, Guid id )
	{
		if ( !_sessions.TryGetValue( key, out var session ) )
			return false;

		var index = session.Items.FindIndex( item => item.Id == id );
		if ( index < 0 || session.Items.Count == 1 )
			return false;

		if ( id == session.Current )
		{
			var neighbor = session.Items[index == 0 ? 1 : index - 1].Id;
			if ( key != _session )
				session.Current = neighbor;
			else if ( !Switch( panels, neighbor ) )
				return false;
		}

		session.Items.RemoveAt( index );
		Save();
		return true;
	}

	/// <summary>Moves a workspace to another place among the session's tiles.</summary>
	internal void Reorder( Guid id, int index )
	{
		var items = Active.Items;
		var from = items.FindIndex( item => item.Id == id );
		if ( from < 0 )
			return;

		var item = items[from];
		items.RemoveAt( from );
		items.Insert( Math.Clamp( index, 0, items.Count ), item );
		Save();
	}

	/// <summary>Moves a workspace of any session to another's place; the workspaces between shift toward where it was. An unknown session or workspace changes nothing.</summary>
	internal void Move( string key, Guid id, Guid target )
	{
		if ( !_sessions.TryGetValue( key, out var session ) )
			return;

		var items = session.Items;
		var from = items.FindIndex( item => item.Id == id );
		var to = items.FindIndex( item => item.Id == target );
		if ( from < 0 || to < 0 || from == to )
			return;

		var item = items[from];
		items.RemoveAt( from );
		items.Insert( to, item );
		Save();
	}

	/// <summary>Finds a workspace of the session by identifier, identifier prefix, or name, ignoring case.</summary>
	internal SavedWorkspace? Find( string key )
	{
		key = key.Trim();
		var items = Active.Items;
		return items.FirstOrDefault( item => string.Equals( item.Name, key, StringComparison.OrdinalIgnoreCase ) )
			?? (key.Length >= 4 ? items.FirstOrDefault( item => item.Id.ToString().StartsWith( key, StringComparison.OrdinalIgnoreCase ) ) : null);
	}

	/// <summary>Finds the most recently opened session whose saved workspaces hold a panel, following reload renames; the current session's live layout is not searched.</summary>
	/// <returns>The session key, or null when no saved layout holds the panel.</returns>
	internal string? FindSession( PanelWorkspace panels, Guid panelId )
	{
		foreach ( var (key, session) in _sessions.OrderByDescending( pair => pair.Value.LastOpened ) )
		{
			// The current session's current workspace is the live layout, so its saved copy is out of date.
			if ( session.Items.Any( item => (key != _session || item.Id != session.Current) && Holds( panels, item, panelId ) ) )
				return key;
		}
		return null;
	}

	/// <summary>Finds the workspace of the current session, other than the one on screen, whose saved layout holds a panel.</summary>
	/// <returns>The workspace, or null when none of the others holds the panel.</returns>
	internal Guid? FindWorkspace( PanelWorkspace panels, Guid panelId ) =>
		Active.Items.FirstOrDefault( item => item.Id != Active.Current && Holds( panels, item, panelId ) )?.Id;

	/// <summary>Reports whether one saved layout of any session holds both panels; the current session's current workspace is the live layout, so its saved copy is skipped.</summary>
	internal bool HoldsTogether( PanelWorkspace panels, Guid first, Guid second ) =>
		_sessions.Any( pair => pair.Value.Items.Any( item => (pair.Key != _session || item.Id != pair.Value.Current)
			&& Holds( panels, item, first ) && Holds( panels, item, second ) ) );

	/// <summary>Reports whether a saved workspace's layout holds a panel, following reload renames.</summary>
	private static bool Holds( PanelWorkspace panels, SavedWorkspace workspace, Guid panelId )
	{
		static IEnumerable<Guid> ListPanels( DockNode node ) => node switch
		{
			DockPane { PanelId: { } id } => [id],
			DockSplit split => ListPanels( split.First ).Concat( ListPanels( split.Second ) ),
			_ => [],
		};

		return workspace.Layout is { } layout && PanelWorkspace.ReadLayoutTree( layout ) is { } tree && ListPanels( tree ).Any( id => panels.ResolvePanelId( id ) == panelId );
	}

	/// <summary>Reads a version 2 document, or moves a version 1 document's workspaces into <see cref="LegacySession"/>.</summary>
	private void Read( string json )
	{
		using var document = JsonDocument.Parse( json );
		var version = document.RootElement.TryGetProperty( "Version", out var value ) && value.TryGetInt32( out var number ) ? number : 0;
		if ( version == LegacyVersion )
		{
			var legacy = document.RootElement.Deserialize<LegacyDocument>() ?? throw new InvalidDataException( "The workspaces file is empty." );
			Add( LegacySession, new( "Chats", legacy.Current, Clean( legacy.Items ), DateTimeOffset.MinValue ) );
			_session = LegacySession;
			return;
		}

		if ( version != DocumentVersion )
			throw new InvalidDataException( "The workspaces file version is unsupported." );

		var saved = document.RootElement.Deserialize<SessionsDocument>() ?? throw new InvalidDataException( "The workspaces file is empty." );
		if ( saved.Sessions is null )
			throw new InvalidDataException( "The workspaces file structure is unsupported." );

		foreach ( var session in saved.Sessions.Where( session => session is not null && !string.IsNullOrWhiteSpace( session.Key ) ) )
			Add( session.Key, new( session.Title ?? session.Key, session.Current, Clean( session.Items ), session.LastOpened ) );
		if ( saved.Session is { } current && _sessions.ContainsKey( current ) )
			_session = current;
		foreach ( var (profile, key) in saved.Profiles ?? [] )
		{
			if ( key is not null && _sessions.ContainsKey( key ) )
				_profiles[profile] = key;
		}
	}

	/// <summary>Adds a session, giving it a first workspace when it has none and a current workspace that exists.</summary>
	private void Add( string key, Session session )
	{
		if ( session.Items.Count == 0 )
			session.Items.Add( new( Guid.NewGuid(), "Main", null ) );
		if ( session.Items.All( item => item.Id != session.Current ) )
			session.Current = session.Items[0].Id;
		_sessions[key] = session;
	}

	/// <summary>Gets a session, creating it with one empty workspace when it is new.</summary>
	private Session EnsureSession( string key, string title )
	{
		if ( !_sessions.TryGetValue( key, out var session ) )
			Add( key, session = new( title, Guid.Empty, [], DateTimeOffset.MinValue ) );

		return session;
	}

	/// <summary>Drops unusable and repeated workspace records.</summary>
	private static List<SavedWorkspace> Clean( SavedWorkspace[]? items ) =>
		(items ?? []).Where( item => item is not null && item.Id != Guid.Empty && !string.IsNullOrWhiteSpace( item.Name ) ).DistinctBy( item => item.Id ).ToList();

	/// <summary>Picks a name: the given one, or the first free "Workspace N" of the session.</summary>
	private static string ComputeName( Session session, string? name )
	{
		if ( !string.IsNullOrWhiteSpace( name ) )
			return Trim( name );

		for ( var number = session.Items.Count + 1; ; number++ )
		{
			var candidate = $"Workspace {number}";
			if ( session.Items.All( item => !string.Equals( item.Name, candidate, StringComparison.OrdinalIgnoreCase ) ) )
				return candidate;
		}
	}

	/// <summary>Replaces one workspace record of a session.</summary>
	private static void Replace( Session session, Guid id, Func<SavedWorkspace, SavedWorkspace> update )
	{
		var index = session.Items.FindIndex( item => item.Id == id );
		if ( index >= 0 )
			session.Items[index] = update( session.Items[index] );
	}

	/// <summary>Writes the sessions beside the file and then replaces it, unless the file failed to read.</summary>
	private void Save()
	{
		_version.Value++;
		if ( _isInvalid )
			return;

		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( path )! );
			var temporary = path + ".tmp";
			var document = new SessionsDocument( DocumentVersion, _session,
				[.. _sessions.Select( pair => new SavedSession( pair.Key, pair.Value.Title, pair.Value.Current, [.. pair.Value.Items], pair.Value.LastOpened ) )],
				new Dictionary<SettingsProfile, string>( _profiles ) );
			File.WriteAllText( temporary, JsonSerializer.Serialize( document, new JsonSerializerOptions { WriteIndented = true } ) );
			File.Move( temporary, path, overwrite: true );
			LastError = null;
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			LastError = $"Could not save workspaces: {error.Message}";
		}
	}

	/// <summary>Trims a name and keeps it short enough for a tile.</summary>
	private static string Trim( string name )
	{
		name = name.Trim();
		return name.Length <= NameLimit ? name : name[..NameLimit];
	}

	/// <summary>Holds one session's workspaces while Lokn runs.</summary>
	/// <param name="title">The session's name.</param>
	/// <param name="current">The current workspace.</param>
	/// <param name="items">The workspaces in tile order.</param>
	/// <param name="lastOpened">When the user last opened the session.</param>
	private sealed class Session( string title, Guid current, List<SavedWorkspace> items, DateTimeOffset lastOpened )
	{
		/// <summary>Gets or sets the session's name.</summary>
		internal string Title { get; set; } = title;

		/// <summary>Gets or sets the current workspace.</summary>
		internal Guid Current { get; set; } = current;

		/// <summary>Gets the workspaces in tile order.</summary>
		internal List<SavedWorkspace> Items { get; } = items;

		/// <summary>Gets or sets when the user last opened the session.</summary>
		internal DateTimeOffset LastOpened { get; set; } = lastOpened;
	}

	/// <summary>Stores the versioned sessions document; files written before profiles had their own session have no <c>Profiles</c>.</summary>
	private sealed record SessionsDocument( int Version, string? Session, SavedSession[]? Sessions, Dictionary<SettingsProfile, string>? Profiles = null );

	/// <summary>Stores one session of the document.</summary>
	private sealed record SavedSession( string Key, string? Title, Guid Current, SavedWorkspace[]? Items, DateTimeOffset LastOpened );

	/// <summary>Stores a version 1 document: one list of workspaces.</summary>
	private sealed record LegacyDocument( int Version, Guid Current, SavedWorkspace[]? Items );
}
