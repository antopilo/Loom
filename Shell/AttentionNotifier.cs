using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Acknowledges a panel's request once the user looks at it, and opens the notifications Windows hands back to Lokn.</summary>
/// <remarks>
/// A request for the panel the user is already looking at is acknowledged at once, and returning to Lokn acknowledges the active panel's.
/// Recording requests, the chime and Windows notifications belong to the Notifications module. A click on a Windows notification relaunches
/// Lokn, which hands it to the running window; it opens the panel's newest feed entry where the panel belongs, else activates the panel.
/// It subscribes before the single-instance election, so a click that starts Lokn is not missed.
/// </remarks>
internal sealed class AttentionNotifier : IDisposable
{
	/// <summary>Stores the application whose panels are watched.</summary>
	private readonly LoknApplication _app;

	/// <summary>Stores the shell window whose focus decides whether the user can see a panel.</summary>
	private readonly Window _window;

	/// <summary>Watches panel requests, window focus, and activations.</summary>
	internal AttentionNotifier( LoknApplication app, Window window )
	{
		_app = app;
		_window = window;
		app.Panels.AttentionRequested += UpdateRequested;
		window.FocusChanged += UpdateFocus;
		Application.Activated += UpdateActivated;
	}

	/// <summary>Stops watching the application and window.</summary>
	public void Dispose()
	{
		_app.Panels.AttentionRequested -= UpdateRequested;
		_window.FocusChanged -= UpdateFocus;
		Application.Activated -= UpdateActivated;
	}

	/// <summary>Acknowledges a request for the panel the user is already looking at.</summary>
	private void UpdateRequested( PanelInfo panel, PanelAttention attention )
	{
		if ( _window.IsFocused && _app.Panels.ActivePanelId == panel.Id )
			_app.Panels.ClearAttention( panel.Id );
	}

	/// <summary>Acknowledges the active panel when the user returns to Lokn.</summary>
	private void UpdateFocus( bool isFocused )
	{
		if ( isFocused && _app.Panels.ActivePanelId is { } panelId )
			_app.Panels.ClearAttention( panelId );
	}

	/// <summary>Brings Lokn forward for a notification click or a second launch, opening the clicked notification where its panel belongs.</summary>
	private void UpdateActivated( ApplicationActivation activation )
	{
		if ( activation.Kind == ApplicationActivationKind.Notification && Guid.TryParse( activation.Payload, out var panelId ) )
		{
			if ( _app.Notifications.Items.FirstOrDefault( item => item.PanelId == panelId ) is { } item )
				_app.OpenNotification( item );
			else
				_app.Panels.Activate( _app.Panels.ResolvePanelId( panelId ) );
		}
		else if ( activation.IsInitial )
			return;

		_window.RequestActivation();
	}
}
