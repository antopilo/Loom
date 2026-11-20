using System.Text.Json;

namespace Lokn.Core;

/// <summary>Keeps a module's panels in their panes while its implementation is replaced.</summary>
public sealed partial class PanelWorkspace
{
	/// <summary>Gets whether panels are being captured and closed so a reloaded module can reopen them.</summary>
	/// <remarks>
	/// True only while <see cref="PanelContent.CaptureState"/> and <see cref="PanelContent.Closed"/> run for a module reload, so a
	/// panel can tell a reload, which reopens it at once, from closing or shutting down.
	/// </remarks>
	public bool IsSuspending
	{
		get; private set;
	}

	/// <summary>Closes a module's panels while leaving their emptied panes in place for the replacement; hidden panels stay hidden.</summary>
	internal IReadOnlyList<SuspendedPanel> SuspendModule( string moduleId )
	{
		var suspended = new List<SuspendedPanel>();
		var sessions = _sessions.Values
			.Where( session => _types[session.Info.TypeId].Definition.Info.ModuleId == moduleId )
			.ToArray();

		IsSuspending = true;
		try
		{
			foreach ( var session in sessions )
			{
				var pane = FindPanelPane( Root, session.Info.Id );
				suspended.Add( new( session.Info.Id, pane?.Id, session.Info.TypeId, CaptureState( session ), session.HostId ) );

				_sessions.Remove( session.Info.Id );
				if ( pane is not null )
					Root = Replace( Root, pane.Id, pane with
					{
						PanelId = null,
					} );
				ReleasePanel( session );
			}
		}
		finally
		{
			IsSuspending = false;
		}

		return suspended;
	}

	/// <summary>Reopens suspended panels in their original panes from the replacement module's factories.</summary>
	internal void ResumePanels( IReadOnlyList<SuspendedPanel> panels )
	{
		foreach ( var panel in panels )
		{
			if ( !_types.TryGetValue( ResolveTypeId( panel.TypeId ), out var type ) )
			{
				// The replacement no longer offers this panel type, so its emptied pane collapses.
				if ( panel.PaneId is { } paneId && FindPane( Root, paneId ) is { PanelId: null } emptied )
				{
					Root = RemovePane( Root, emptied.Id ) ?? Root;
					EnsureActivePane();
				}
				continue;
			}

			DockPane? pane = null;
			if ( panel.PaneId is { } target )
			{
				// Replacement startup runs first and may have closed or filled the pane; its choice stands.
				pane = FindPane( Root, target );
				if ( pane is not { PanelId: null } )
					continue;
			}

			// The shell retains views by panel id, so a fresh id mounts the replacement content; saved layouts follow the rename. The panel stays on its host.
			var id = Guid.NewGuid();
			_sessions.Add( id, BuildSession( type, id, input: null, panel.State, hostId: RestoreHost( type, panel.HostId ) ) );
			_renamed[panel.PanelId] = id;
			if ( pane is not null )
				Root = Replace( Root, pane.Id, pane with
				{
					PanelId = id,
				} );
		}

		_app.MarkChanged();
	}

	/// <summary>Captures a panel's restorable state, containing failures in feature code.</summary>
	private JsonElement? CaptureState( PanelSession session )
	{
		try
		{
			return session.Content?.CaptureState();
		}
		catch ( Exception error )
		{
			_app.ReportError( session.Info.Title, error );
			return null;
		}
	}
}

/// <summary>Identifies a panel closed for a module reload and the pane that held it.</summary>
/// <param name="PanelId">The panel's identifier before the reload, which saved layouts may hold.</param>
/// <param name="PaneId">The pane kept empty for the replacement panel, or null for a panel the layout hid.</param>
/// <param name="TypeId">The fully qualified panel type identifier.</param>
/// <param name="State">The feature-owned JSON captured before the panel closed.</param>
/// <param name="HostId">The host the panel worked on, or null for a type that is not host-bound.</param>
internal readonly record struct SuspendedPanel( Guid PanelId, Guid? PaneId, string TypeId, JsonElement? State, string? HostId = null );
