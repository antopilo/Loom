using Goo;

namespace Lokn.Core;

/// <summary>Tracks panels that are waiting for the user until the user looks at them or the panel withdraws.</summary>
public sealed partial class PanelWorkspace
{
	/// <summary>Stores each waiting panel's latest request by panel identifier.</summary>
	private readonly Dictionary<Guid, PanelAttention> _attention = [];

	/// <summary>Counts changes to the waiting panels, so views that read them rebuild when one starts or stops waiting.</summary>
	private readonly State<int> _attentionVersion = new( 0 );

	/// <summary>Reports a panel that newly started waiting, after its request is recorded; repeated requests do not raise it.</summary>
	public event Action<PanelInfo, PanelAttention>? AttentionRequested;

	/// <summary>Reports a panel whose request was acknowledged, withdrawn, or closed with it.</summary>
	public event Action<Guid>? AttentionCleared;

	/// <summary>Gets a panel's pending request for the user, or null when it is not waiting; reading it during a build rebuilds the reader when any panel starts or stops waiting.</summary>
	public PanelAttention? GetAttention( Guid panelId )
	{
		_ = _attentionVersion.Value;
		return _attention.GetValueOrDefault( panelId );
	}

	/// <summary>Gets the panels waiting for the user; reading it during a build rebuilds the reader when any panel starts or stops waiting.</summary>
	public IReadOnlyCollection<Guid> WaitingPanelIds
	{
		get
		{
			_ = _attentionVersion.Value;
			return [.. _attention.Keys];
		}
	}

	/// <summary>Marks an open panel as waiting for the user until it is activated or clears the request itself.</summary>
	/// <returns>False when the panel is not open.</returns>
	public bool RequestAttention( Guid panelId, PanelAttention attention )
	{
		ArgumentNullException.ThrowIfNull( attention );
		if ( !_sessions.TryGetValue( panelId, out var session ) )
			return false;

		var isNew = !_attention.ContainsKey( panelId );
		_attention[panelId] = attention;
		_attentionVersion.Value++;
		_app.MarkChanged();
		// A panel already waiting only updates its message, so the user is told once per request.
		if ( isNew )
			AttentionRequested?.Invoke( session.Info, attention );
		return true;
	}

	/// <summary>Withdraws a panel's request for the user.</summary>
	/// <returns>False when the panel was not waiting.</returns>
	public bool ClearAttention( Guid panelId )
	{
		if ( !_attention.Remove( panelId ) )
			return false;

		_attentionVersion.Value++;
		_app.MarkChanged();
		AttentionCleared?.Invoke( panelId );
		return true;
	}
}

/// <summary>Describes why a panel is waiting for the user.</summary>
/// <param name="Title">What is waiting, such as a conversation title.</param>
/// <param name="Message">What it needs from the user.</param>
public sealed record PanelAttention( string Title, string Message );
