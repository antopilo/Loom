using Goo;

namespace Lokn.Core;

/// <summary>Owns panel instances and immutable docking topology on the application UI thread.</summary>
public sealed partial class PanelWorkspace
{
	/// <summary>Defines the initial equally sized split ratio.</summary>
	private const double DefaultRatio = 0.5;

	/// <summary>Defines the minimum stored split fraction.</summary>
	private const double MinimumRatio = 0.05;

	/// <summary>Stores the owning application.</summary>
	private readonly LoknApplication _app;

	/// <summary>Stores registered panel factories with their owning module lifetimes.</summary>
	private readonly Dictionary<string, RegisteredPanel> _types = new( StringComparer.Ordinal );

	/// <summary>Maps panel type identifiers saved layouts may still hold to the registered types that took them over.</summary>
	private readonly Dictionary<string, string> _formerTypes = new( StringComparer.Ordinal );

	/// <summary>Stores live panel sessions by stable instance identifier.</summary>
	private readonly Dictionary<Guid, PanelSession> _sessions = [];

	/// <summary>Gets the immutable current split tree.</summary>
	public DockNode Root
	{
		get; private set;
	}

	/// <summary>Gets the pane receiving new panels and workspace commands.</summary>
	public Guid ActivePaneId
	{
		get; private set;
	}

	/// <summary>Gets the active panel, or null when the active pane is empty.</summary>
	public Guid? ActivePanelId => FindPane( Root, ActivePaneId )?.PanelId;

	/// <summary>Gets the registered panel type metadata.</summary>
	public IReadOnlyList<PanelTypeInfo> Types => _types.Values.Select( type => type.Definition.Info ).ToArray();

	/// <summary>Gets every open panel, shown or hidden by the current layout; <see cref="IsShown"/> tells them apart.</summary>
	public IReadOnlyList<PanelInfo> Panels => _sessions.Values.Select( panel => panel.Info ).ToArray();

	/// <summary>Gets the visible panes in visual tree order.</summary>
	public IReadOnlyList<DockPane> Panes => EnumeratePanes( Root ).ToArray();

	/// <summary>Gets sessions for the shell without exposing mutable ownership to feature modules.</summary>
	internal IReadOnlyCollection<PanelSession> Sessions => _sessions.Values;

	/// <summary>Finds the first open panel, shown or hidden, whose content is of a type, such as a module's single setup page.</summary>
	/// <typeparam name="T">The content type.</typeparam>
	/// <returns>The panel's identifier and content, or null when none is open.</returns>
	public (Guid Id, T Content)? FindContent<T>() where T : PanelContent
	{
		foreach ( var session in _sessions.Values )
		{
			if ( session.Content is T content )
				return (session.Info.Id, content);
		}
		return null;
	}

	/// <summary>Creates a workspace containing one empty pane.</summary>
	internal PanelWorkspace( LoknApplication app )
	{
		_app = app;
		Root = new DockPane( Guid.NewGuid(), null );
		ActivePaneId = Root.Id;
	}

	/// <summary>Opens a visible panel, filling an empty pane or splitting an occupied pane to its right.</summary>
	/// <param name="typeId">The panel type to open.</param>
	/// <param name="paneId">The pane to open in, or null for the active pane.</param>
	/// <param name="input">The new panel's input.</param>
	/// <param name="hostId">The host a panel of a host-bound type works on, or null for the current host; ignored for other types.</param>
	public Guid Open( string typeId, Guid? paneId = null, object? input = null, string? hostId = null )
	{
		if ( !_types.TryGetValue( typeId, out var type ) || type.Lifetime.IsCancellationRequested )
			throw new InvalidOperationException( $"Panel type is not registered: {typeId}" );

		var pane = FindPane( Root, paneId ?? ActivePaneId )
			?? throw new InvalidOperationException( "The target pane no longer exists." );
		var id = Guid.NewGuid();
		_sessions.Add( id, BuildSession( type, id, input, hostId: ResolveHost( type, hostId ) ) );
		Place( id, pane );
		return id;
	}

	/// <summary>Closes every open panel, shown or hidden, that works on a host, such as when the host is removed.</summary>
	/// <param name="hostId">The host identifier, compared with <see cref="GetHostId"/>.</param>
	/// <returns>How many panels closed.</returns>
	public int CloseHost( string hostId )
	{
		ArgumentNullException.ThrowIfNull( hostId );
		var closed = 0;
		foreach ( var panelId in _sessions.Keys.ToArray() )
		{
			// Closing a panel also closes the never-seen panels that waited beside it.
			if ( _sessions.ContainsKey( panelId ) && GetHostId( panelId ) == hostId && Close( panelId ) )
				closed++;
		}
		return closed;
	}

	/// <summary>Focuses the pane containing a panel, first bringing a hidden panel back into the layout beside the active pane.</summary>
	public bool Activate( Guid panelId )
	{
		if ( !_sessions.ContainsKey( panelId ) )
			return false;

		if ( Panes.FirstOrDefault( candidate => candidate.PanelId == panelId ) is not { } pane )
		{
			Place( panelId, FindPane( Root, ActivePaneId )! );
			PlaceWaiting();
			return true;
		}

		return ActivatePane( pane.Id );
	}

	/// <summary>Focuses the pane receiving subsequent panel and split commands, acknowledging its panel's request for the user.</summary>
	public bool ActivatePane( Guid paneId )
	{
		if ( FindPane( Root, paneId ) is not { } pane )
			return false;

		if ( pane.PanelId is { } panelId )
			ClearAttention( panelId );
		if ( ActivePaneId == paneId )
			return true;

		ActivePaneId = paneId;
		_app.MarkChanged();
		return true;
	}

	/// <summary>Splits the active pane and focuses the newly created empty pane.</summary>
	public Guid SplitActive( SplitAxis axis )
	{
		var pane = FindPane( Root, ActivePaneId )!;
		var next = new DockPane( Guid.NewGuid(), null );
		Root = Replace( Root, pane.Id, new DockSplit( Guid.NewGuid(), axis, DefaultRatio, pane, next ) );
		ActivePaneId = next.Id;
		_app.MarkChanged();
		return next.Id;
	}

	/// <summary>Swaps a panel with its target or docks it beside the target without recreating content.</summary>
	public bool Move( Guid panelId, Guid targetPaneId, PanelPlacement placement = PanelPlacement.Swap )
	{
		var source = Panes.FirstOrDefault( pane => pane.PanelId == panelId );
		var target = FindPane( Root, targetPaneId );
		if ( source is null || target is null || source.Id == target.Id )
			return false;

		DockNode root;
		DockPane destination;
		if ( placement == PanelPlacement.Swap || target.PanelId is null )
		{
			destination = target with
			{
				PanelId = panelId,
			};
			root = Replace( Root, target.Id, destination );
			root = target.PanelId is { } other
				? Replace( root, source.Id, source with
				{
					PanelId = other,
				} )
				: RemovePane( root, source.Id )!;
		}
		else
		{
			destination = new( Guid.NewGuid(), panelId );
			var axis = placement is PanelPlacement.Left or PanelPlacement.Right ? SplitAxis.Horizontal : SplitAxis.Vertical;
			var isFirst = placement is PanelPlacement.Left or PanelPlacement.Top;
			root = Replace( Root, target.Id, new DockSplit(
				Guid.NewGuid(), axis, DefaultRatio,
				isFirst ? destination : target,
				isFirst ? target : destination ) );
			root = RemovePane( root, source.Id )!;
		}

		Root = root;
		ActivePaneId = destination.Id;
		_app.MarkChanged();
		return true;
	}

	/// <summary>Closes a panel, shown or hidden, cancels its work, and promotes the surviving neighboring pane.</summary>
	public bool Close( Guid panelId )
	{
		if ( !_sessions.Remove( panelId, out var session ) )
			return false;

		if ( Panes.FirstOrDefault( candidate => candidate.PanelId == panelId ) is { } pane )
		{
			Root = RemovePane( Root, pane.Id ) ?? new DockPane( pane.Id, null );
			EnsureActivePane();
		}
		ReleasePanel( session );
		ReleaseWaiting( panelId );
		_app.MarkChanged();
		return true;
	}

	/// <summary>Closes the focused panel or removes the focused empty split pane.</summary>
	public void CloseActive()
	{
		if ( ActivePanelId is { } panelId )
		{
			Close( panelId );
			return;
		}

		Root = RemovePane( Root, ActivePaneId ) ?? Root;
		EnsureActivePane();
		_app.MarkChanged();
	}

	/// <summary>Updates a divider's first-child fraction while keeping both children reachable.</summary>
	public bool UpdateSplitRatio( Guid splitId, double ratio )
	{
		if ( !double.IsFinite( ratio ) || FindNode( Root, splitId ) is not DockSplit split )
			return false;

		Root = Replace( Root, splitId, split with
		{
			Ratio = Math.Clamp( ratio, MinimumRatio, 1.0 - MinimumRatio )
		} );
		_app.MarkChanged();
		return true;
	}

	/// <summary>Checks panel type identifiers, and the former identifiers they restore, before publishing any module contributions.</summary>
	internal void EnsureAvailable( IReadOnlyList<PanelDefinition> definitions )
	{
		var identifiers = _types.Keys.Concat( _formerTypes.Keys ).ToHashSet( StringComparer.Ordinal );
		foreach ( var definition in definitions )
		{
			if ( !identifiers.Add( definition.Info.Id ) )
				throw new InvalidOperationException( $"Duplicate panel type: {definition.Info.Id}" );
		}
		foreach ( var former in definitions.SelectMany( definition => definition.FormerIds ) )
		{
			if ( !identifiers.Add( former ) )
				throw new InvalidOperationException( $"Former panel type is registered: {former}" );
		}
	}

	/// <summary>Registers validated panel factories, and the former identifiers they restore, under their module lifetime.</summary>
	internal void Register( IEnumerable<PanelDefinition> definitions, CancellationToken lifetime )
	{
		foreach ( var definition in definitions )
		{
			_types.Add( definition.Info.Id, new( definition, lifetime ) );
			foreach ( var former in definition.FormerIds )
				_formerTypes.Add( former, definition.Info.Id );
		}
	}

	/// <summary>Closes owned panels before removing their module's factories and former identifiers.</summary>
	internal void RemoveModule( string moduleId )
	{
		var types = Types.Where( type => type.ModuleId == moduleId ).Select( type => type.Id ).ToHashSet();
		foreach ( var panel in Panels.Where( panel => types.Contains( panel.TypeId ) ) )
			Close( panel.Id );

		foreach ( var id in types )
			_types.Remove( id );
		foreach ( var former in _formerTypes.Where( entry => types.Contains( entry.Value ) ).Select( entry => entry.Key ).ToArray() )
			_formerTypes.Remove( former );
	}

	/// <summary>Gets the registered panel type a saved identifier names: itself, or the type that took a former identifier over.</summary>
	private string ResolveTypeId( string typeId ) => !_types.ContainsKey( typeId ) && _formerTypes.TryGetValue( typeId, out var current ) ? current : typeId;

	/// <summary>Gets the host a new panel of a type works on: for a host-bound type the given host, else the current one; null for other types.</summary>
	private string? ResolveHost( RegisteredPanel type, string? hostId ) =>
		type.Definition.Info.IsHostBound ? hostId ?? _app.Hosts.Current.Id : null;

	/// <summary>Gets the host a saved panel of a type works on again: for a host-bound type the saved host, This PC when none was saved; null for other
	/// types.</summary>
	private static string? RestoreHost( RegisteredPanel type, string? hostId ) =>
		type.Definition.Info.IsHostBound ? hostId ?? LoknHost.LocalId : null;

	/// <summary>Reports whether an open panel takes a key ahead of the global shortcuts; a throwing panel claims nothing.</summary>
	internal bool ClaimsKey( Key key, KeyModifiers modifiers )
	{
		foreach ( var session in _sessions.Values )
		{
			try
			{
				if ( session.Content?.ClaimsKey( key, modifiers ) == true )
					return true;
			}
			catch ( Exception )
			{
				// Queried for every key press before dispatch; a failing panel must not block global shortcuts.
			}
		}

		return false;
	}

	/// <summary>Cancels a panel's lifetime and runs its guarded close hook exactly once.</summary>
	private void ReleasePanel( PanelSession session )
	{
		ClearAttention( session.Info.Id );
		try
		{
			session.Lifetime.Cancel();
		}
		catch ( Exception error ) { _app.ReportError( session.Info.Title, error ); }

		try
		{
			session.Content?.Closed();
		}
		catch ( Exception error ) { _app.ReportError( session.Info.Title, error ); }

		// Closing releases cancellation registrations rather than waiting for application exit.
		session.Lifetime.Dispose();
	}

	/// <summary>Puts a panel in a pane, filling it when empty or splitting it to open on its right, and makes it active.</summary>
	private void Place( Guid panelId, DockPane pane )
	{
		var destination = pane.PanelId is null ? pane with
		{
			PanelId = panelId,
		} : new DockPane( Guid.NewGuid(), panelId );
		Root = Replace( Root, pane.Id, pane.PanelId is null ? destination
			: new DockSplit( Guid.NewGuid(), SplitAxis.Horizontal, DefaultRatio, pane, destination ) );
		ActivePaneId = destination.Id;
		_app.MarkChanged();
	}

	/// <summary>Selects a surviving pane after tree normalization.</summary>
	private void EnsureActivePane()
	{
		if ( FindPane( Root, ActivePaneId ) is null )
			ActivePaneId = Panes[0].Id;
	}

	/// <summary>Stores a panel factory and its owning module cancellation token.</summary>
	private sealed record RegisteredPanel( PanelDefinition Definition, CancellationToken Lifetime );
}

/// <summary>Owns a panel model for its entire open lifetime, including docking and swapping.</summary>
/// <param name="Info">The panel's identity and title.</param>
/// <param name="Content">The panel's content, or null when its factory failed.</param>
/// <param name="Failure">Why the factory failed, or null.</param>
/// <param name="Lifetime">The panel's lifetime.</param>
/// <param name="HostId">The host the panel was opened on; null for a type that is not host-bound.</param>
internal sealed record PanelSession(
	PanelInfo Info,
	PanelContent? Content,
	string? Failure,
	CancellationTokenSource Lifetime,
	string? HostId = null );
