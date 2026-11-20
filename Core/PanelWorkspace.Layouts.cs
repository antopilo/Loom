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

	/// <summary>Reports whether a panel is in a pane; open panels that are not are hidden by the current layout.</summary>
	public bool IsShown( Guid panelId ) => FindPanelPane( Root, panelId ) is not null;

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

		// After a restart or a reload, identical panels such as the same chat still match by their captured state, on the same host. A panel that says which
		// host it works on now, such as a conversation panel that shows any host's chat, names that host in its state, which alone then decides: the host it
		// was opened on may differ from the one a layout saved it under.
		var states = new Dictionary<Guid, string?>();
		foreach ( var panel in saved.Panels.Where( panel => !map.ContainsKey( panel.Id ) ) )
		{
			var state = panel.State?.GetRawText();
			var host = panel.Host ?? LoknHost.LocalId;
			var match = _sessions.Values.FirstOrDefault( session => !claimed.Contains( session.Info.Id ) && session.Info.TypeId == panel.TypeId
				&& (session.HostId is null || session.HostId == host || session.Content?.HostId is not null)
				&& (states.TryGetValue( session.Info.Id, out var captured ) ? captured : states[session.Info.Id] = CaptureState( session )?.GetRawText()) == state );
			if ( match is not null )
			{
				claimed.Add( match.Info.Id );
				map[panel.Id] = match.Info.Id;
				continue;
			}

			if ( !_types.TryGetValue( panel.TypeId, out var type ) || type.Lifetime.IsCancellationRequested )
				continue;

			var id = Guid.NewGuid();
			_sessions.Add( id, BuildSession( type, id, input: null, panel.State, panel.Title, RestoreHost( type, panel.Host ) ) );
			map[panel.Id] = id;
		}

		Root = BuildAppliedNode( saved.Root, map ) ?? new DockPane( Guid.NewGuid(), null );
		ActivePaneId = saved.ActivePaneId;
		EnsureActivePane();
		PlaceWaiting();
		_app.MarkChanged();
		return true;
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
