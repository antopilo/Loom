using Goo;
using Lokn.Core;

namespace Lokn.Modules.Notifications;

/// <summary>The popover under the titlebar bell: its title line, the All, Waiting and Unread tabs, the list, and its footer.</summary>
internal sealed partial class NotificationsContent
{
	/// <summary>Defines the popover header's height in logical pixels.</summary>
	private const int HeaderHeight = 44;

	/// <summary>Defines the Segoe Fluent FullScreen glyph that opens the full page from the popover.</summary>
	private const string FullFeedGlyph = "\uE740";

	/// <summary>Builds the popover: the title line, the tabs, the list with each waiting question answered under its row, and the footer.</summary>
	private Container BuildPopover()
	{
		var theme = Theme.Current;
		var view = ReadView();
		var filter = _filter.Value;
		var unread = context.App.Notifications.UnreadCount;
		var rows = ComputeRows( view, filter, "" );
		var list = new List<Blob>();
		if ( filter.Kind is NotificationFilterKind.All or NotificationFilterKind.Waiting )
		{
			foreach ( var prompt in view.Unannounced )
			{
				list.Add( BuildPromptRow( prompt, isSelected: false, () => Open( prompt ) ) );
				if ( BuildPromptCard( prompt, "feed", PromptHeight ) is { } card )
					list.Add( BuildInset( "prompt-inset:" + prompt.Key, card ) );
			}
		}
		foreach ( var row in rows )
		{
			if ( row.Kind == NotificationRowKind.Day )
			{
				list.Add( BuildDay( row.Key, row.Label ) );
				continue;
			}

			var latest = row.Items[0];
			list.Add( BuildRow( row, view, isSelected: false, () => Open( latest ) ) );
			if ( BuildRowActions( row ) is { } actions )
				list.Add( actions );
			if ( row.Items.Count == 1 && view.Waiting.TryGetValue( latest.Id, out var waiting ) && BuildPromptCard( waiting, "feed", PromptHeight ) is { } card )
				list.Add( BuildInset( "prompt-inset:" + waiting.Key, card ) );
		}
		if ( list.Count == 0 )
			list.Add( BuildEmpty( filter.Kind switch
			{
				NotificationFilterKind.Waiting => "Nothing waits for you.",
				NotificationFilterKind.Unread => "You have read everything.",
				_ => "No notifications yet. Chats that finish or need you while you are elsewhere, and GitHub updates, appear here.",
			} ) );

		return new Container
		{
			Key = "notifications",
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Notification feed",
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			FontFamily = ViewStyle.FontFamily,
			FontSize = theme.FontSize,
			Color = theme.Text,
			Children =
			[
				BuildPopoverHeader( unread ),
				BuildTabs( view, unread ),
				new Container
				{
					Key = "notifications-list",
					FlexGrow = 1,
					MinHeight = 0,
					Padding = new Edges( theme.Spacing / 2, 0 ),
					OverflowY = Overflow.Scroll,
					ScrollbarY = ViewStyle.BuildScrollbar(),
					Children = list,
				},
				new Row
				{
					Key = "notifications-footer",
					FlexShrink = 0,
					AlignItems = AlignItems.Center,
					Padding = new Edges( theme.Spacing / 2, theme.Spacing ),
					BorderWidth = new Edges( theme.BorderWidth, 0, 0, 0 ),
					BorderColor = theme.Divider,
					Children =
					[
						NotificationViews.BuildQuietButton( "Clear read", () => context.Run( UpdateClearRead ), context.App.Notifications.Items.Any( item => item.IsRead ) ),
						new Spacer(),
						NotificationViews.BuildQuietButton( "Settings", () => context.Run( OpenSettings ), glyph: SettingsGlyph ),
					],
				},
			],
		};
	}

	/// <summary>Builds the popover's title line: its name and unread count, marking everything read, and the way to the full page.</summary>
	private Row BuildPopoverHeader( int unread )
	{
		var theme = Theme.Current;
		var full = NotificationViews.BuildIconButton( "Open the Notifications page", FullFeedGlyph, () => context.Run( () =>
		{
			dismiss!();
			NotificationsModule.OpenFeed( context.App );
		} ) );
		var title = new List<Blob> { new Text { Content = "Notifications", FontWeight = NotificationViews.TitleWeight, FlexShrink = 1, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis } };
		if ( unread > 0 )
			title.Add( NotificationViews.BuildCount( unread ) );
		return new Row
		{
			Key = "notifications-header",
			Height = HeaderHeight,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Padding = new Edges( 0, theme.Spacing / 2, 0, theme.Spacing * 1.5 ),
			BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
			BorderColor = theme.Divider,
			Children =
			[
				new Row { AlignItems = AlignItems.Center, Gap = theme.Spacing * 0.75, FlexShrink = 1, MinWidth = 0, Children = title },
				new Spacer(),
				NotificationViews.BuildQuietButton( "Mark all read", () => context.Run( () => context.App.Notifications.MarkAllRead() ), unread > 0 ),
				full,
			],
		};
	}

	/// <summary>Builds the tabs: All, Waiting with how many questions wait, Unread with how many are unread, then any further filters, wrapping onto more lines.</summary>
	private Row BuildTabs( FeedView view, int unread, IReadOnlyList<(string Label, NotificationFilter Filter)>? more = null )
	{
		var theme = Theme.Current;
		var filter = _filter.Value;
		Button Tab( string label, NotificationFilter target )
		{
			var isOn = filter == target;
			var tab = theme.BuildButton( label, () => context.Run( () => UpdateFilter( target ) ) );
			tab.Key = "tab:" + target.Kind + ":" + target.Value;
			tab.AccessibilityName = label;
			tab.Height = 24;
			tab.FlexShrink = 0;
			tab.Padding = new Edges( 0, theme.Spacing * 1.25 );
			tab.BorderWidth = 0;
			tab.BorderRadius = 12;
			tab.FontSize = theme.FontSize;
			tab.BackgroundColor = isOn ? theme.Raised : Color.Transparent;
			tab.Color = isOn ? theme.Text : theme.Muted;
			tab.HoverStyle = new Style { BackgroundColor = isOn ? theme.Raised : theme.Hover, Color = theme.Text };
			return tab;
		}

		var tabs = new List<Blob>
		{
			Tab( "All", new( NotificationFilterKind.All ) ),
			Tab( view.WaitingCount > 0 ? $"Waiting · {view.WaitingCount}" : "Waiting", new( NotificationFilterKind.Waiting ) ),
			Tab( unread > 0 ? $"Unread · {unread}" : "Unread", new( NotificationFilterKind.Unread ) ),
		};
		foreach ( var (label, target) in more ?? [] )
			tabs.Add( Tab( label, target ) );
		return new Row
		{
			Key = "notifications-tabs",
			FlexShrink = 0,
			FlexWrap = FlexWrap.Wrap,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Padding = new Edges( theme.Spacing * 0.75, theme.Spacing * 1.5 ),
			BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
			BorderColor = theme.Divider,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Show",
			Children = tabs,
		};
	}

	/// <summary>Insets a question's card under its row, lined up with the row's text.</summary>
	private static Container BuildInset( string key, Blob card ) => new()
	{
		Key = key,
		FlexShrink = 0,
		MinWidth = 0,
		Padding = new Edges( 0, Theme.Current.Spacing * 1.5, Theme.Current.Spacing, Theme.Current.Spacing * 1.5 ),
		Children = [card],
	};
}
