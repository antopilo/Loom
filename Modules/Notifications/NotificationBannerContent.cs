using Goo;
using Lokn.Core;

namespace Lokn.Modules.Notifications;

/// <summary>Announces one notification as it arrives, in a compact card under the titlebar bell, with the question an agent waits on answered in place.</summary>
/// <remarks>
/// The card shows the source's icon with its mark, the title and one line of the message. It leaves on its own after a few seconds, later
/// while the pointer is on it. While its chat waits on a question it stays, outlined in the waiting color, until that is answered or
/// withdrawn, the user opens the chat, or closes the card; the shell also removes it once the user looks at its panel. Its title opens the
/// notification as the feed does. The question scrolls inside the card past <see cref="QuestionHeight"/>, so the header stays.
/// </remarks>
internal sealed class NotificationBannerContent : PanelContent
{
	/// <summary>Defines how long a banner without a question stays.</summary>
	private static readonly TimeSpan Duration = TimeSpan.FromSeconds( 6 );

	/// <summary>Defines the tallest the question grows inside the card before it scrolls, in logical pixels.</summary>
	private const int QuestionHeight = 360;

	/// <summary>Defines the Segoe Fluent Cancel glyph that closes the banner.</summary>
	private const string CloseGlyph = "\uE711";

	/// <summary>Stores the banner's context, whose lifetime ends when the shell removes it.</summary>
	private readonly PanelContext _context;

	/// <summary>Stores the notification the banner announces.</summary>
	private readonly LoknNotification _item;

	/// <summary>Asks the shell to remove the banner.</summary>
	private readonly Action _dismiss;

	/// <summary>Becomes true once the banner's time is up; it then stays only while its chat waits on a question.</summary>
	private readonly State<bool> _isExpired = new( false );

	/// <summary>Tracks whether the pointer is on the banner, which holds it past its time.</summary>
	private bool _isHovered;

	/// <summary>Tracks whether the banner asked to be removed, so it asks once.</summary>
	private bool _isDismissed;

	/// <summary>Creates the banner and starts its time.</summary>
	internal NotificationBannerContent( PanelContext context, LoknNotification item, Action dismiss )
	{
		_context = context;
		_item = item;
		_dismiss = dismiss;
		_ = UpdateExpiryAsync();
	}

	/// <summary>Builds the card: the header with the icon, title and message, then the question its chat waits on, or the notification's buttons.</summary>
	public override Blob Build()
	{
		var theme = Theme.Current;
		var question = NotificationsContent.ComputeWaiting( [_item], NotificationPromptSource.ListAll( _context.App ), id => _context.App.Panels.ResolvePanelId( id ) )
			.GetValueOrDefault( _item.Id );
		if ( _isExpired.Value && question is null )
		{
			// Removing it changes the shell, which must not happen while it builds.
			UpdateDismiss();
			return new Container { Key = "banner-leaving" };
		}

		var dialog = question?.Build( "banner" );
		var children = new List<Blob> { BuildHeader( dialog is not null ) };
		if ( dialog is not null )
			children.Add( new Container
			{
				Key = "question",
				MinWidth = 0,
				MaxHeight = QuestionHeight,
				OverflowY = Overflow.Scroll,
				ScrollbarY = ViewStyle.BuildScrollbar(),
				Children = [dialog],
			} );
		else if ( NotificationsContent.BuildActions( _item, OpenLink ) is { } actions )
		{
			actions.Padding = new Edges( 0, theme.Spacing * 1.5, theme.Spacing * 1.5, NotificationViews.TextInset + theme.Spacing / 2 );
			children.Add( actions );
		}

		return new Container
		{
			Key = "banner",
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = dialog is null ? $"{_item.Title}: {_item.Message}" : $"{_item.Title} is waiting for you",
			Width = Length.Percent( 100 ),
			MinWidth = 0,
			BackgroundColor = theme.Panel,
			BorderColor = dialog is null ? theme.Border : theme.Attention,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius * 2,
			BoxShadow = new BoxShadow { OffsetY = 6, Blur = 18, Color = Color.Black.WithAlpha( 0.35 ) },
			Overflow = Overflow.Hidden,
			FontFamily = ViewStyle.FontFamily,
			FontSize = theme.FontSize,
			Color = theme.Text,
			OnPointerEnter = _ =>
			{
				_isHovered = true;
			},
			OnPointerLeave = _ =>
			{
				_isHovered = false;
			},
			Children = children,
		};
	}

	/// <summary>Follows one of the notification's buttons, marks it read, and lets the banner go.</summary>
	private void OpenLink( Uri uri ) => _context.Run( () =>
	{
		UpdateDismiss();
		_context.App.Notifications.MarkRead( _item.Id );
		_context.App.OpenLink( uri );
	} );

	/// <summary>Builds the header: the icon with its mark, the title and one line of the message, which open the notification, and the button that closes the banner.</summary>
	/// <param name="isWaiting">Whether the card holds a question, which then takes the message's place and marks the icon as waiting.</param>
	private Row BuildHeader( bool isWaiting )
	{
		var theme = Theme.Current;
		var icon = NotificationsContent.BuildSourceIcon( _context.App, _item, panelId => _context.App.Panels.Panels.FirstOrDefault( panel => panel.Id == panelId )?.TypeId );
		var title = new List<Blob>
		{
			new Text
			{
				Content = _item.Title,
				FontWeight = NotificationViews.TitleWeight,
				FlexShrink = 1,
				MinWidth = 0,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
		};
		if ( isWaiting )
			title.Add( new Text { Content = "· waiting", Color = theme.Muted, FlexShrink = 0, TextWrap = TextWrap.NoWrap } );
		var text = new List<Blob> { new Row { Key = "title", AlignItems = AlignItems.Center, Gap = theme.Spacing / 2, MinWidth = 0, Children = title } };
		if ( !isWaiting && NotificationViews.ComputePreview( _item.Message ) is { Length: > 0 } preview )
			text.Add( new Text
			{
				Key = "message",
				Content = preview,
				Color = theme.Muted,
				MinWidth = 0,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			} );

		void Open() => _context.Run( () =>
		{
			UpdateDismiss();
			NotificationsModule.Open( _context.App, _item, feedPanelId: null );
		} );
		var open = new Button
		{
			Key = "open",
			FlexGrow = 1,
			FlexShrink = 1,
			MinWidth = 0,
			FlexDirection = FlexDirection.Row,
			AlignItems = isWaiting ? AlignItems.Center : AlignItems.FlexStart,
			Gap = theme.Spacing * 1.25,
			Padding = new Edges( theme.Spacing * 1.25, theme.Spacing / 2, theme.Spacing * 1.25, theme.Spacing * 1.5 ),
			Focusable = true,
			Cursor = Cursor.Pointer,
			AccessibilityName = $"Open {_item.Title}",
			OnClick = Open,
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, Open ),
				new( Key.Space, KeyModifiers.None, Open ),
			],
			FocusStyle = new Style { BackgroundColor = theme.Hover },
			Children =
			[
				NotificationViews.BuildIcon( icon, isWaiting ? NotificationTone.Attention : _item.Tone, theme.Panel ),
				new Container { Key = "text", FlexGrow = 1, FlexShrink = 1, MinWidth = 0, Gap = 1, Children = text },
			],
		};

		var close = NotificationViews.BuildIconButton( "Close notification", CloseGlyph, () => _context.Run( UpdateDismiss ) );
		close.Margin = new Edges( theme.Spacing * 0.75, theme.Spacing * 0.75, 0, 0 );
		return new Row
		{
			Key = "header",
			AlignItems = AlignItems.FlexStart,
			MinWidth = 0,
			Children = [open, close],
		};
	}

	/// <summary>Waits out the banner's time, then lets it go.</summary>
	private async Task UpdateExpiryAsync()
	{
		try
		{
			await Task.Delay( Duration, _context.Lifetime );
		}
		catch ( OperationCanceledException )
		{
			return;
		}

		_context.Post( UpdateExpired );
	}

	/// <summary>Ends the banner's time, or starts it again while the pointer is on the banner; a banner with a question stays until it is dealt with.</summary>
	private void UpdateExpired()
	{
		if ( _isHovered )
			_ = UpdateExpiryAsync();
		else
			_isExpired.Value = true;
	}

	/// <summary>Asks the shell once, after the current build, to remove the banner.</summary>
	private void UpdateDismiss()
	{
		if ( _isDismissed )
			return;

		_isDismissed = true;
		_context.Post( _dismiss );
	}
}
