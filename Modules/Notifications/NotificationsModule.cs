using Lokn.Core;

namespace Lokn.Modules.Notifications;

/// <summary>Shows the notification feed in a panel, the bell in the titlebar with its popover, and the banners that announce each notification.</summary>
/// <remarks>
/// It depends on no other module. Any module posts with <see cref="NotificationFeed.Post"/>, and extends the views through extension points:
/// <see cref="NotificationKind.Point"/> gives a kind or source its icon, <see cref="NotificationOpener.Point"/> opens a notification better than
/// activating its panel, and <see cref="NotificationPromptSource.Point"/> lists what panels wait on, answered in the feed and banners; Conversations
/// contributes all three for chats. <c>lokn://notifications/feed</c> opens the feed.
/// </remarks>
public sealed class NotificationsModule : ILoknModule
{
	/// <summary>Defines the feed's panel type.</summary>
	internal const string PanelType = "notifications.feed";

	/// <summary>Gets the module identifier.</summary>
	public string Id => "notifications";

	/// <summary>Gets the display name.</summary>
	public string Title => "Notifications";

	/// <summary>Gets no dependencies: other modules extend the views instead.</summary>
	public IReadOnlyList<string> Dependencies => [];

	/// <summary>Stores the alerts while the module runs.</summary>
	private NotificationAlerts? _alerts;

	/// <summary>Declares the kind and opener points and registers the alert settings, the feed panel, the titlebar bell, the open command and the feed route.</summary>
	public void Register( ModuleBuilder builder )
	{
		builder
			.Setting( NotificationSettings.Sound )
			.Setting( NotificationSettings.Desktop )
			.ExtensionPoint<NotificationKind>( "kinds" )
			.ExtensionPoint<NotificationOpener>( "openers" )
			.ExtensionPoint<NotificationPromptSource>( "prompts" )
			.Panel( "feed", "Notifications", context => new NotificationsContent( context ) )
			.Slot( ShellSlots.TitleRight, 1000, context => new NotificationBell( context ) )
			// A Notifications row in the sidebar opens the feed like Settings: one page, shown or brought back.
			.Contribute( SidebarAction.Point, new SidebarAction( "Notifications", "\uEA8F", OpenFeed )
			{
				IsActive = app => app.Panels.ActivePanelId is { } active && app.Panels.Panels.Any( panel => panel.Id == active && panel.TypeId == PanelType ),
			} )
			.Command( "open", "Notifications", context => OpenFeed( context.App ) )
			.Route( "feed", request => OpenFeed( request.App ) );
	}

	/// <summary>Starts the chime and Windows notifications for panels that wait for the user.</summary>
	public void Start( ModuleContext context ) => _alerts = new( context );

	/// <summary>Stops the alerts.</summary>
	public void Stop()
	{
		_alerts?.Stop();
		_alerts = null;
	}

	/// <summary>Finds the kind a module contributed for a notification: by its kind, else its source, else its source's module; null when none did.</summary>
	internal static NotificationKind? FindKind( LoknApplication app, LoknNotification item ) =>
		NotificationsContent.FindKind( app.Extensions.Get<NotificationKind>( NotificationKind.Point ), item );

	/// <summary>Opens a notification as <see cref="LoknApplication.OpenNotification"/> does.</summary>
	/// <param name="app">The application.</param>
	/// <param name="item">The notification.</param>
	/// <param name="feedPanelId">The feed panel it was opened from, which a link opens beside; null from the popover, a banner or a Windows notification.</param>
	internal static void Open( LoknApplication app, LoknNotification item, Guid? feedPanelId ) => app.OpenNotification( item, feedPanelId );

	/// <summary>Shows the feed: its open panel, shown or hidden, else a new one beside the active pane.</summary>
	/// <remarks>Conversations opens it in a workspace session of its own through this same route.</remarks>
	internal static void OpenFeed( LoknApplication app )
	{
		if ( app.Panels.Panels.FirstOrDefault( panel => panel.TypeId == PanelType ) is { } feed )
			app.Panels.Activate( feed.Id );
		else
			app.Panels.Open( PanelType );
	}
}
