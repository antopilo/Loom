using System.Text.Json;

namespace Lokn.Core;

/// <summary>Captures and applies whole layouts, keeping panels a layout leaves out alive but hidden.</summary>
/// <remarks>
/// A panel open in the workspace but in no pane is hidden: it keeps its state and its work, is not drawn, and comes back
/// when a layout that holds it is applied or when it is activated. This is what lets saved workspaces switch layouts
/// without closing chats or terminals.
/// </remarks>
public sealed partial class PanelWorkspace
{
	/// <summary>Maps the identifier a panel had before a module reload reopened it to its new one.</summary>
	private readonly Dictionary<Guid, Guid> _renamed = [];

	/// <summary>Reports whether a panel is on screen: in a pane, and not hidden behind the panel that is maximized.</summary>
	public bool IsShown( Guid panelId ) => FindPanelPane( Root, panelId ) is not null && (MaximizedPanelId is null || MaximizedPanelId == panelId);

	/// <summary>Fills the workspace with a shown panel while the others rest hidden in their panes, or brings them back when it already does.</summary>
	/// <remarks>A workspace with one pane has nothing to hide, so the panel stays as it is.</remarks>
	/// <param name="panelId">The panel.</param>
	/// <returns>Whether the panel is maximized now.</returns>
	public bool ToggleMaximize( Guid panelId )
	{
		if ( MaximizedPanelId == panelId )
		{
			MaximizedPanelId = null;
			_app.MarkChanged();
			return false;
		}

		if ( Panes.Count < 2 || Panes.FirstOrDefault( pane => pane.PanelId == panelId ) is not { } shown )
			return false;

		ActivePaneId = shown.Id;
		MaximizedPanelId = panelId;
		ClearAttention( panelId );
		_app.MarkChanged();
		return true;
	}

	/// <summary>Captures the current tree, the active pane, and the state of every shown panel as JSON for <see cref="ApplyLayout"/>.</summary>
	public JsonElement CaptureLayout() => JsonSerializer.SerializeToElement( BuildLayout() );

	/// <summary>Hides every panel behind one empty pane; the panels keep their state and work until a layout or activation shows them again.</summary>
	public void HideAll()
	{
		Root = new DockPane( Guid.NewGuid(), null );
		ActivePaneId = Root.Id;
		_app.MarkChanged();
	}

	/// <summary>Shows a panel in a pane and focuses it, hiding the panel the pane held; a panel shown elsewhere leaves its old pane.</summary>
	/// <returns>Whether the panel and the pane exist.</returns>
	public bool Show( Guid panelId, Guid paneId )
	{
		if ( !_sessions.ContainsKey( panelId ) || FindPane( Root, paneId ) is null )
			return false;

		var root = Root;
		if ( Panes.FirstOrDefault( pane => pane.PanelId == panelId ) is { } source )
		{
			if ( source.Id == paneId )
				return ActivatePane( paneId );

			// Removing a pane promotes its sibling with its identifier intact, so the target pane survives.
			root = RemovePane( root, source.Id ) ?? root;
		}

		var target = FindPane( root, paneId )!;
		Root = Replace( root, target.Id, target with
		{
			PanelId = panelId,
		} );
		ActivePaneId = target.Id;
		ClearAttention( panelId );
		PlaceWaiting();
		_app.MarkChanged();
		return true;
	}

	/// <summary>Shows a panel, shown or hidden, against a pane and focuses it: in a new split on one of the pane's sides, or in its place for
	/// <see cref="PanelPlacement.Swap"/>, where the pane's panel moves to where the panel was, or, for a hidden panel, beside it, so nothing leaves the screen.</summary>
	/// <returns>Whether the panel and the pane exist.</returns>
	public bool ShowAt( Guid panelId, Guid paneId, PanelPlacement placement )
	{
		if ( !_sessions.ContainsKey( panelId ) || FindPane( Root, paneId ) is not { } target )
			return false;
		if ( FindPanelPane( Root, panelId ) is { } shown && shown.Id == paneId )
			return ActivatePane( paneId );
		if ( target.PanelId is null )
			return Show( panelId, paneId );

		if ( FindPanelPane( Root, panelId ) is null )
		{
			// A hidden panel first gets a pane of its own beside the target, which the move then places or swaps.
			Root = Replace( Root, target.Id, new DockSplit( Guid.NewGuid(), SplitAxis.Horizontal, DefaultRatio, target, new DockPane( Guid.NewGuid(), panelId ) ) );
		}
		Move( panelId, paneId, placement );
		ClearAttention( panelId );
		PlaceWaiting();
		_app.MarkChanged();
		return true;
	}

	/// <summary>Opens a new panel against a pane and focuses it, as <see cref="ShowAt"/> shows one: in a new split on one of the pane's sides, or in its place
	/// for <see cref="PanelPlacement.Swap"/>, the pane's panel then beside it.</summary>
	/// <returns>The new panel.</returns>
	public Guid OpenAt( string typeId, Guid paneId, PanelPlacement placement, object? input = null, string? hostId = null )
	{
		ActivatePane( paneId );
		var id = Open( typeId, SplitActive( SplitAxis.Horizontal ), input, hostId );
		ShowAt( id, paneId, placement );
		return id;
	}

	/// <summary>Replaces the tree with a captured layout, reusing open panels so they keep their state and slide into place.</summary>
	/// <remarks>
	/// Each panel in the layout is matched to an open panel with the same identifier, then to one of the same type whose
	/// captured state is equal; otherwise a new panel opens from the saved state. Open panels the layout leaves out stay
	/// open but hidden. Panel types no longer registered are left out. A malformed layout changes nothing.
	/// </remarks>
	/// <returns>Whether the layout was applied.</returns>
	public bool ApplyLayout( JsonElement layout )
	{
		SavedLayout saved;
		try
		{
			saved = layout.Deserialize<SavedLayout>() ?? throw new InvalidDataException( "The layout is empty." );
			EnsureValidLayout( saved );
			saved = ResolveFormerTypes( saved );
		}
		catch ( Exception error ) when ( error is JsonException or InvalidDataException or NotSupportedException )
		{
			_app.ReportError( "Apply layout", error );
			return false;
		}

		var claimed = new HashSet<Guid>();
		var map = new Dictionary<Guid, Guid>();
		foreach ( var panel in saved.Panels )
		{
			var id = ResolvePanelId( panel.Id );
			if ( _sessions.TryGetValue( id, out var session ) && session.Info.TypeId == panel.TypeId && claimed.Add( id ) )
				map[panel.Id] = id;
		}

		var states = new Dictionary<Guid, string?>();
		foreach ( var panel in saved.Panels.Where( panel => !map.ContainsKey( panel.Id ) ) )
		{
			if ( OpenSavedPanel( panel, claimed, states ) is { } id )
				map[panel.Id] = id;
		}

		Root = BuildAppliedNode( saved.Root, map ) ?? new DockPane( Guid.NewGuid(), null );
		ActivePaneId = saved.ActivePaneId;
		EnsureActivePane();
		PlaceWaiting();
		_app.MarkChanged();
		return true;
	}

	/// <summary>Finds an open panel a saved one stands for by its captured state, else opens it from that state; the panel found or opened is claimed.</summary>
	/// <remarks>After a restart or a reload, identical panels such as the same chat still match by their captured state, on the same host. A panel that says which
	/// host it works on now, such as a conversation panel that shows any host's chat, names that host in its state, which alone then decides: the host it
	/// was opened on may differ from the one a layout saved it under.</remarks>
	/// <param name="panel">The saved panel.</param>
	/// <param name="claimed">The panels already standing for saved ones, which are not matched again.</param>
	/// <param name="states">The captured states read so far, by panel, so each open panel captures once.</param>
	/// <returns>The panel, or null when its type is no longer registered.</returns>
	private Guid? OpenSavedPanel( SavedPanel panel, HashSet<Guid> claimed, Dictionary<Guid, string?> states )
	{
		var state = panel.State?.GetRawText();
		var host = panel.Host ?? LoknHost.LocalId;
		var match = _sessions.Values.FirstOrDefault( session => !claimed.Contains( session.Info.Id ) && session.Info.TypeId == panel.TypeId
			&& (session.HostId is null || session.HostId == host || session.Content?.HostId is not null)
			&& (states.TryGetValue( session.Info.Id, out var captured ) ? captured : states[session.Info.Id] = CaptureState( session )?.GetRawText()) == state );
		if ( match is not null )
		{
			claimed.Add( match.Info.Id );
			return match.Info.Id;
		}

		if ( !_types.TryGetValue( panel.TypeId, out var type ) || type.Lifetime.IsCancellationRequested )
			return null;

		var id = Guid.NewGuid();
		_sessions.Add( id, BuildSession( type, id, input: null, panel.State, panel.Title, RestoreHost( type, panel.Host ) ) );
		claimed.Add( id );
		return id;
	}

	/// <summary>Takes a panel out of a captured layout, such as a saved workspace's, so it can show in this one instead: its pane leaves the layout's tree,
	/// its neighbor taking the space, and the panel is found open, or opened from its saved state as <see cref="ApplyLayout"/> would.</summary>
	/// <param name="layout">The captured layout.</param>
	/// <param name="panelId">The panel to take, or null for the layout's active one, else its first.</param>
	/// <returns>The open panel and the layout without it, or null when the layout holds no such panel, its type is gone, or it is no layout.</returns>
	public (Guid PanelId, JsonElement Layout)? TakeFromLayout( JsonElement layout, Guid? panelId = null )
	{
		SavedLayout saved;
		try
		{
			saved = layout.Deserialize<SavedLayout>() ?? throw new InvalidDataException( "The layout is empty." );
			EnsureValidLayout( saved );
			saved = ResolveFormerTypes( saved );
		}
		catch ( Exception error ) when ( error is JsonException or InvalidDataException or NotSupportedException )
		{
			return null;
		}

		static IEnumerable<SavedNode> ListPanes( SavedNode node ) =>
			node.Kind == "pane" ? [node] : ListPanes( node.First! ).Concat( ListPanes( node.Second! ) );
		var panes = ListPanes( saved.Root ).Where( pane => pane.PanelId is not null ).ToList();
		var pane = panelId is { } wanted ? panes.FirstOrDefault( candidate => ResolvePanelId( candidate.PanelId!.Value ) == wanted )
			: panes.FirstOrDefault( candidate => candidate.Id == saved.ActivePaneId ) ?? panes.FirstOrDefault();
		if ( pane is null || saved.Panels.FirstOrDefault( panel => panel.Id == pane.PanelId ) is not { } entry )
			return null;

		// The panels shown here are not taken by their state: they are this layout's, not the saved one's.
		var claimed = EnumeratePanes( Root ).Select( shown => shown.PanelId ).OfType<Guid>().ToHashSet();
		var id = ResolvePanelId( entry.Id );
		var open = _sessions.TryGetValue( id, out var session ) && session.Info.TypeId == entry.TypeId ? id : OpenSavedPanel( entry, claimed, [] );
		if ( open is not { } taken )
			return null;

		static SavedNode? Remove( SavedNode node, Guid paneId )
		{
			if ( node.Kind == "pane" )
				return node.Id == paneId ? null : node;
			var first = Remove( node.First!, paneId );
			var second = Remove( node.Second!, paneId );
			return first is null ? second : second is null ? first : node with { First = first, Second = second };
		}
		var root = Remove( saved.Root, pane.Id ) ?? new SavedNode( "pane", Guid.NewGuid(), null, default, 0, null, null );
		var active = saved.ActivePaneId != pane.Id ? saved.ActivePaneId : ListPanes( root ).First().Id;
		var rest = new SavedLayout( saved.Version, active, root, [.. saved.Panels.Where( panel => panel.Id != entry.Id )] );
		return (taken, JsonSerializer.SerializeToElement( rest ));
	}

	/// <summary>Gets the tree of a captured layout for previews, or null when it is not a layout.</summary>
	public static DockNode? ReadLayoutTree( JsonElement layout )
	{
		try
		{
			var saved = layout.Deserialize<SavedLayout>() ?? throw new InvalidDataException( "The layout is empty." );
			EnsureValidLayout( saved );
			return BuildRestoredNode( saved.Root, null );
		}
		catch ( Exception error ) when ( error is JsonException or InvalidDataException or NotSupportedException )
		{
			return null;
		}
	}

	/// <summary>Builds the serializable layout of the current tree and its shown panels, each with the host it works on.</summary>
	private SavedLayout BuildLayout()
	{
		var panels = EnumeratePanes( Root )
			.Where( pane => pane.PanelId is { } id && _sessions.ContainsKey( id ) )
			.Select( pane => BuildSavedPanel( _sessions[pane.PanelId!.Value] ) )
			.ToArray();
		return new( LayoutVersion, ActivePaneId, BuildSavedNode( Root ), panels );
	}

	/// <summary>Follows module reload renames to a panel's current identifier, for identifiers saved before a reload, such as in layouts or notifications.</summary>
	/// <returns>The panel's identifier now, or <paramref name="id"/> when it was never renamed.</returns>
	public Guid ResolvePanelId( Guid id )
	{
		for ( var step = 0; step < _renamed.Count && _renamed.TryGetValue( id, out var next ); step++ )
			id = next;
		return id;
	}

	/// <summary>Finds the pane holding a panel.</summary>
	private static DockPane? FindPanelPane( DockNode node, Guid panelId ) => node switch
	{
		DockPane pane => pane.PanelId == panelId ? pane : null,
		DockSplit split => FindPanelPane( split.First, panelId ) ?? FindPanelPane( split.Second, panelId ),
		_ => null,
	};

	/// <summary>Rebuilds a layout's tree with its panels mapped to open ones, collapsing panes whose panel could not open.</summary>
	private static DockNode? BuildAppliedNode( SavedNode node, Dictionary<Guid, Guid> map )
	{
		if ( node.Kind == "pane" )
		{
			if ( node.PanelId is not { } panel )
				return new DockPane( node.Id, null );
			return map.TryGetValue( panel, out var id ) ? new DockPane( node.Id, id ) : null;
		}

		var first = BuildAppliedNode( node.First!, map );
		var second = BuildAppliedNode( node.Second!, map );
		if ( first is null )
			return second;
		if ( second is null )
			return first;

		return new DockSplit( node.Id, node.Axis, node.Ratio, first, second );
	}
}
