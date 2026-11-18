using System.Runtime.Versioning;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Notifications;

/// <summary>Defines the settings the Notifications module owns.</summary>
internal static class NotificationSettings
{
	/// <summary>Defines whether a soft chime plays when a panel starts waiting for the user out of sight, or a notification arrives.</summary>
	internal static readonly Setting<bool> Sound = new(
		"notifications.sound",
		"Chime",
		"Play a soft sound when a chat finishes or asks you something.",
		defaultValue: true,
		category: "Alerts" );

	/// <summary>Defines whether a Windows notification appears when a panel starts waiting while Lokn is in the background.</summary>
	internal static readonly Setting<bool> Desktop = new(
		"notifications.desktop",
		"Windows notifications",
		"Show a Windows notification when Lokn is in the background.",
		defaultValue: true,
		category: "Alerts" );
}

/// <summary>Records panels that wait for the user in the feed, chimes, and shows Windows notifications while Lokn is in the background.</summary>
/// <remarks>
/// A request the user cannot see goes into the feed, a soft chime plays, and while Lokn is in the background a silent Windows notification
/// appears; the shell opens the clicked notification. Acknowledging the request marks its feed entries read and removes its Windows
/// notification. Notifications posted straight to the feed, such as GitHub updates, chime once per burst.
/// </remarks>
internal sealed class NotificationAlerts
{
	/// <summary>Defines how long after a chime further feed posts stay silent, so a burst of updates chimes once.</summary>
	private static readonly TimeSpan ChimeQuiet = TimeSpan.FromSeconds( 2 );

	/// <summary>Stores the module context, whose dispatcher reports failures from worker threads.</summary>
	private readonly ModuleContext _context;

	/// <summary>Stores the application whose panels are watched.</summary>
	private readonly LoknApplication _app;

	/// <summary>Tracks panels whose Windows notification may still be in the notification center.</summary>
	private readonly HashSet<Guid> _notified = [];

	/// <summary>Chains native notification work on worker threads so it runs in order and off the UI thread.</summary>
	private Task _work = Task.CompletedTask;

	/// <summary>Reports whether a failure was already shown, so a broken setup is reported once.</summary>
	private bool _hasReportedFailure;

	/// <summary>Stores when the last chime for a feed post played.</summary>
	private DateTimeOffset _lastChime;

	/// <summary>Starts watching panel requests and the feed, and registers for Windows notifications when they are on.</summary>
	internal NotificationAlerts( ModuleContext context )
	{
		_context = context;
		_app = context.App;
		var app = _app;
		app.Panels.AttentionRequested += UpdateRequested;
		app.Panels.AttentionCleared += UpdateCleared;
		app.Notifications.Posted += UpdatePosted;
		// Windows may ignore notifications for a shortcut it has only just indexed, so register well before the first one.
		if ( OperatingSystem.IsWindows() && app.ApplicationId.Length > 0 && app.Settings.Get( NotificationSettings.Desktop ) )
			UpdateRegistration();
	}

	/// <summary>Stops watching.</summary>
	internal void Stop()
	{
		_app.Panels.AttentionRequested -= UpdateRequested;
		_app.Panels.AttentionCleared -= UpdateCleared;
		_app.Notifications.Posted -= UpdatePosted;
	}

	/// <summary>Records, chimes and, in the background, notifies for a panel that started waiting out of the user's sight.</summary>
	/// <remarks>While Lokn has focus, a panel the current workspace shows is in sight already: its outline marks the request, so nothing is posted.</remarks>
	private void UpdateRequested( PanelInfo panel, PanelAttention attention )
	{
		var isFocused = _app.Window?.IsFocused ?? false;
		if ( isFocused && _app.Panels.IsShown( panel.Id ) )
			return;

		_app.Notifications.Post( new( attention.Title, attention.Message ) { PanelId = panel.Id, Source = panel.TypeId } );
		if ( _app.Settings.Get( NotificationSettings.Sound ) )
			AttentionChime.Play();
		if ( !isFocused && _app.ApplicationId.Length > 0 && _app.Settings.Get( NotificationSettings.Desktop ) && OperatingSystem.IsWindows() )
			UpdateNotification( panel.Id, attention );
	}

	/// <summary>Chimes for a notification posted straight to the feed; panel requests already chimed when they were recorded.</summary>
	private void UpdatePosted( LoknNotification notification )
	{
		var now = DateTimeOffset.Now;
		if ( notification.PanelId is not null || now - _lastChime < ChimeQuiet || !_app.Settings.Get( NotificationSettings.Sound ) )
			return;

		_lastChime = now;
		AttentionChime.Play();
	}

	/// <summary>Shows a silent notification for a waiting panel; the chime is the only sound.</summary>
	[SupportedOSPlatform( "windows" )]
	private void UpdateNotification( Guid panelId, PanelAttention attention )
	{
		var tag = panelId.ToString( "N" );
		var content = WindowsToast.BuildContent( NotificationIdentity.Scheme, tag, attention.Title, attention.Message, tag );
		_notified.Add( panelId );
		UpdateRegistration();
		var identifier = _app.ApplicationId;
		Enqueue( () => WindowsToast.Show( identifier, tag, content ), isReported: true );
	}

	/// <summary>Marks the feed entries of a panel the user acknowledged read, and removes its Windows notification.</summary>
	private void UpdateCleared( Guid panelId )
	{
		_app.Notifications.MarkPanelRead( panelId );
		if ( _notified.Remove( panelId ) && OperatingSystem.IsWindows() )
			UpdateRemoval( panelId.ToString( "N" ) );
	}

	/// <summary>Removes a notification from the notification center.</summary>
	[SupportedOSPlatform( "windows" )]
	private void UpdateRemoval( string tag )
	{
		var identifier = _app.ApplicationId;
		// A notification the user already clicked or dismissed is gone, which Windows may report as a failure.
		Enqueue( () => WindowsToast.Remove( identifier, tag ), isReported: false );
	}

	/// <summary>Registers the shortcut and protocol Windows requires, which is a no-op once they match this executable.</summary>
	[SupportedOSPlatform( "windows" )]
	private void UpdateRegistration()
	{
		var identifier = _app.ApplicationId;
		Enqueue( () => NotificationIdentity.Install( identifier ), isReported: true );
	}

	/// <summary>Runs native notification work after the work queued before it.</summary>
	private void Enqueue( Action action, bool isReported )
	{
		_work = _work.ContinueWith( _ =>
		{
			try
			{
				action();
			}
			catch ( Exception error ) when ( isReported )
			{
				_context.Post( () => ReportFailure( error ) );
			}
			catch
			{
				// Unreported work is best-effort cleanup.
			}
		}, TaskScheduler.Default );
	}

	/// <summary>Shows the first notification failure in the shell notice.</summary>
	private void ReportFailure( Exception error )
	{
		if ( _hasReportedFailure )
			return;

		_hasReportedFailure = true;
		_app.ReportError( "Notifications", error );
	}
}
