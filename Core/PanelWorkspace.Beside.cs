namespace Lokn.Core;

/// <summary>Opens panels beside the panel that asked for them, even when that panel is not on screen.</summary>
/// <remarks>
/// A chat working in the background, in another session or profile, must not drop its report into the layout the user is
/// looking at. A panel opened beside a hidden panel stays hidden and waits; the first time its source comes on screen,
/// through a session switch, a saved workspace or the user opening it, the waiting panel appears in a split beside it.
/// </remarks>
public sealed partial class PanelWorkspace
{
	/// <summary>Stores each hidden panel that waits to appear, by identifier, with the panel it opens beside.</summary>
	private readonly Dictionary<Guid, Guid> _waiting = [];

	/// <summary>Opens a panel beside another: in a split next to it when it is on screen, otherwise hidden until it is.</summary>
	/// <param name="typeId">The panel type to open.</param>
	/// <param name="besidePanelId">The panel it belongs beside, such as the chat whose agent asked for it.</param>
	/// <param name="input">The new panel's input.</param>
	/// <param name="hostId">The host a panel of a host-bound type works on, or null for the host of <paramref name="besidePanelId"/>, else the current
	/// host; ignored for other types.</param>
	/// <returns>The new panel's identifier. Without an open <paramref name="besidePanelId"/>, the panel opens in the active pane.</returns>
	public Guid OpenBeside( string typeId, Guid besidePanelId, object? input = null, string? hostId = null )
	{
		besidePanelId = ResolvePanelId( besidePanelId );
		hostId ??= GetHostId( besidePanelId );
		if ( !_sessions.ContainsKey( besidePanelId ) )
			return Open( typeId, null, input, hostId );

		if ( Panes.FirstOrDefault( pane => pane.PanelId == besidePanelId ) is { } shown )
			return Open( typeId, shown.Id, input, hostId );

		if ( !_types.TryGetValue( typeId, out var type ) || type.Lifetime.IsCancellationRequested )
			throw new InvalidOperationException( $"Panel type is not registered: {typeId}" );

		var id = Guid.NewGuid();
		_sessions.Add( id, BuildSession( type, id, input, hostId: ResolveHost( type, hostId ) ) );
		_waiting[id] = besidePanelId;
		_app.MarkChanged();
		return id;
	}

	/// <summary>Gets the panel a hidden panel waits to appear beside, or null when it waits for none.</summary>
	public Guid? GetWaitingBeside( Guid panelId ) =>
		_waiting.TryGetValue( ResolvePanelId( panelId ), out var beside ) ? ResolvePanelId( beside ) : null;

	/// <summary>Gets the settings profile an open panel works in, or null when it follows the active profile or is not open.</summary>
	public SettingsProfile? GetProfile( Guid panelId ) =>
		_sessions.TryGetValue( ResolvePanelId( panelId ), out var session ) ? session.Content?.Profile : null;

	/// <summary>Gets the host an open panel works on now: its content's <see cref="PanelContent.HostId"/>, else the host it opened on.</summary>
	/// <returns>The host identifier, or null for a panel that is not open or whose type is not host-bound.</returns>
	public string? GetHostId( Guid panelId ) =>
		_sessions.TryGetValue( ResolvePanelId( panelId ), out var session ) ? session.Content?.HostId ?? session.HostId : null;

	/// <summary>Gets the host an open panel works on now, a <see cref="LoknHostState.Removed"/> placeholder for one no longer paired.</summary>
	/// <returns>The host, or null for a panel that is not open or whose type is not host-bound.</returns>
	public LoknHost? GetHost( Guid panelId ) => GetHostId( panelId ) is { } hostId ? _app.Hosts.Get( hostId ) : null;

	/// <summary>Shows every waiting panel whose source is now on screen, in a split beside it, without moving the focus.</summary>
	/// <remarks>Forgets waits that ended otherwise: the panel was shown, such as by the user, or closed.</remarks>
	private void PlaceWaiting()
	{
		foreach ( var (waiting, beside) in _waiting.ToArray() )
		{
			var panelId = ResolvePanelId( waiting );
			if ( !_sessions.ContainsKey( panelId ) || IsShown( panelId ) )
			{
				_waiting.Remove( waiting );
				continue;
			}

			var besideId = ResolvePanelId( beside );
			if ( Panes.FirstOrDefault( pane => pane.PanelId == besideId ) is not { } pane )
				continue;

			Root = Replace( Root, pane.Id, new DockSplit( Guid.NewGuid(), SplitAxis.Horizontal, DefaultRatio, pane, new DockPane( Guid.NewGuid(), panelId ) ) );
			_waiting.Remove( waiting );
		}
	}

	/// <summary>Forgets a closed panel's wait, and closes the never-seen panels that waited beside it.</summary>
	private void ReleaseWaiting( Guid closedId )
	{
		_waiting.Remove( closedId );
		foreach ( var (waiting, beside) in _waiting.ToArray() )
		{
			if ( ResolvePanelId( beside ) != closedId )
				continue;

			_waiting.Remove( waiting );
			Close( ResolvePanelId( waiting ) );
		}
	}
}
