using Goo;
using Lokn.Core;

namespace Lokn.Modules.Notifications;

/// <summary>The bell the module adds to the titlebar: its unread badge, the popover with the feed under it, and the banners that announce each notification.</summary>
/// <remarks>
/// A banner closes when its content dismisses itself, when the user looks at its panel, when a newer notification from the same panel or
/// title replaces it, or when the popover opens; at most <see cref="BannerLimit"/> show. While the popover or a banner shows, native
/// surfaces such as browser pages are hidden so they do not draw over it.
/// </remarks>
internal sealed class NotificationBell : PanelContent
{
	/// <summary>Defines the Segoe Fluent Ringer glyph.</summary>
	private const string BellGlyph = "\uEA8F";

	/// <summary>Defines the bell button's size in logical pixels, matching the titlebar's other actions.</summary>
	private const int ButtonWidth = 32;

	/// <summary>Defines the bell button's height in logical pixels.</summary>
	private const int ButtonHeight = 28;

	/// <summary>Defines the bell glyph size in logical pixels.</summary>
	private const int GlyphSize = 14;

	/// <summary>Defines the unread badge's height and minimum width in logical pixels.</summary>
	private const int BadgeSize = 14;

	/// <summary>Defines the largest count the badge spells out; more shows as 9+.</summary>
	private const int BadgeLimit = 9;

	/// <summary>Defines the popover width in logical pixels.</summary>
	private const int PopoverWidth = 420;

	/// <summary>Defines the popover height in logical pixels.</summary>
	private const int PopoverHeight = 560;

	/// <summary>Defines the banner column's width in logical pixels.</summary>
	private const int BannerWidth = 380;

	/// <summary>Defines how many banners show at once; a further one closes the oldest.</summary>
	private const int BannerLimit = 3;

	/// <summary>Stores the bell's context, which lives until the module unloads.</summary>
	private readonly PanelContext _context;

	/// <summary>Anchors the popover and banners under the bell.</summary>
	private readonly ElementHandle _handle = new();

	/// <summary>Stores the banners on screen, oldest first.</summary>
	private readonly List<Overlay> _banners = [];

	/// <summary>Bumps on every popover or banner change, rebuilding the bell.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Stores the open popover, or null while closed.</summary>
	private Overlay? _popover;

	/// <summary>Creates the bell and starts announcing new notifications.</summary>
	internal NotificationBell( PanelContext context )
	{
		_context = context;
		context.App.Notifications.Posted += UpdatePosted;
		context.App.Panels.AttentionCleared += UpdateCleared;
	}

	/// <summary>Builds the bell, and the popover or banners anchored under it.</summary>
	public override Blob Build()
	{
		_ = _version.Value;
		var theme = Theme.Current;
		var unread = _context.App.Notifications.UnreadCount;
		var children = new List<Blob>
		{
			new Text { Content = BellGlyph, FontFamily = NotificationViews.IconFont, FontSize = GlyphSize },
		};
		if ( unread > 0 )
			children.Add( new Container
			{
				Key = "badge",
				Position = PositionType.Absolute,
				Top = 1,
				Right = 1,
				MinWidth = BadgeSize,
				Height = BadgeSize,
				Padding = new Edges( 0, 3 ),
				AlignItems = AlignItems.Center,
				JustifyContent = JustifyContent.Center,
				BorderRadius = BadgeSize / 2.0,
				BackgroundColor = theme.Attention,
				Color = theme.Canvas,
				Children = [new Text { Content = unread > BadgeLimit ? $"{BadgeLimit}+" : unread.ToString( System.Globalization.CultureInfo.CurrentCulture ), FontSize = 9, FontWeight = 700 }],
			} );
		if ( _popover is { } popover )
			children.Add( BuildPopover( popover ) );
		else if ( _banners.Count > 0 )
			children.Add( BuildBanners() );

		void Toggle() => _context.Run( UpdatePopover );
		return new Button
		{
			Key = "bell",
			Handle = _handle,
			Width = ButtonWidth,
			Height = ButtonHeight,
			Margin = new Edges( 0, theme.Spacing ),
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = theme.Radius,
			Focusable = true,
			Cursor = Cursor.Pointer,
			AccessibilityName = unread > 0 ? $"Notifications, {unread} unread" : "Notifications",
			OnClick = Toggle,
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, Toggle ),
				new( Key.Space, KeyModifiers.None, Toggle ),
			],
			HoverStyle = new Style { BackgroundColor = theme.CaptionHover, Color = theme.Text },
			FocusStyle = new Style { BackgroundColor = theme.CaptionHover },
			Children = children,
		};
	}

	/// <summary>Stops announcing, and closes the popover and banners with the module.</summary>
	public override void Closed()
	{
		_context.App.Notifications.Posted -= UpdatePosted;
		_context.App.Panels.AttentionCleared -= UpdateCleared;
		if ( _popover is { } popover )
			Release( popover );
		foreach ( var banner in _banners )
			Release( banner );
		_popover = null;
		_banners.Clear();
		_context.App.CoverNativeSurfaces( this, false );
	}

	/// <summary>Opens the popover, closing the banners it shows anyway, or closes it when open.</summary>
	private void UpdatePopover()
	{
		if ( _popover is { } open )
		{
			Release( open );
			_popover = null;
			UpdateChanged();
			return;
		}

		foreach ( var banner in _banners )
			Release( banner );
		_banners.Clear();
		var (context, lifetime) = _context.CreateChild();
		Overlay? created = null;
		created = new( null, new NotificationsContent( context, () => context.Post( () =>
		{
			if ( _popover == created )
				UpdatePopover();
		} ) ), lifetime );
		_popover = created;
		UpdateChanged();
	}

	/// <summary>Announces a notification in a banner, replacing an earlier one from the same panel or title; the open popover shows it already.</summary>
	private void UpdatePosted( LoknNotification item )
	{
		if ( _popover is not null || _context.Lifetime.IsCancellationRequested )
			return;

		foreach ( var replaced in _banners.Where( banner => IsSameSource( banner.Item!, item ) ).ToArray() )
			Remove( replaced );
		while ( _banners.Count >= BannerLimit )
			Remove( _banners[0] );

		var (context, lifetime) = _context.CreateChild();
		Overlay? added = null;
		added = new( item, new NotificationBannerContent( context, item, () => context.Post( () => Remove( added! ) ) ), lifetime );
		_banners.Add( added );
		UpdateChanged();
	}

	/// <summary>Closes the banners of a panel the user looked at, or that stopped waiting.</summary>
	private void UpdateCleared( Guid panelId )
	{
		foreach ( var banner in _banners.Where( banner => banner.Item!.PanelId == panelId ).ToArray() )
			Remove( banner );
	}

	/// <summary>Reports whether two notifications come from the same panel, or, without panels, the same title such as GitHub.</summary>
	private static bool IsSameSource( LoknNotification shown, LoknNotification posted ) =>
		posted.PanelId is { } panelId ? shown.PanelId == panelId : shown.PanelId is null && shown.Title == posted.Title;

	/// <summary>Closes one banner; one already closed is left alone.</summary>
	private void Remove( Overlay banner )
	{
		if ( !_banners.Remove( banner ) )
			return;

		Release( banner );
		UpdateChanged();
	}

	/// <summary>Ends an overlay's content and its lifetime.</summary>
	private static void Release( Overlay overlay )
	{
		overlay.Lifetime.Cancel();
		overlay.Content.Closed();
		overlay.Lifetime.Dispose();
	}

	/// <summary>Hides native surfaces while an overlay shows, and rebuilds the bell.</summary>
	private void UpdateChanged()
	{
		_context.App.CoverNativeSurfaces( this, _popover is not null || _banners.Count > 0 );
		_version.Value++;
	}

	/// <summary>Builds the popover anchored under the bell around the feed, entering like every other popup.</summary>
	private Portal BuildPopover( Overlay popover )
	{
		var theme = Theme.Current;
		var portal = new Portal
		{
			Key = "notifications-popover",
			Anchor = _handle,
			Placement = PortalPlacement.BottomEnd,
			Width = PopoverWidth,
			Height = PopoverHeight,
			FocusScope = new FocusScopeOptions { Modal = true },
			AccessibilityRole = AccessibilityRole.Dialog,
			AccessibilityName = "Notifications",
			OnDismiss = _ => _context.Run( () =>
			{
				if ( _popover is not null )
					UpdatePopover();
			} ),
		};
		var surface = new Container
		{
			FlexGrow = 1,
			BackgroundColor = theme.Panel,
			Color = theme.Text,
			FontFamily = theme.FontFamily,
			FontSize = theme.FontSize,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius * 2,
			Overflow = Overflow.Hidden,
			Children = [popover.Content.Build()],
		};
		portal.Add( Cell.Mount<PopupEntranceInput, PopupEntrance>( "surface", new( surface, -PopupMotion.PopoverRise ) { Fill = true } ) );
		return portal;
	}

	/// <summary>Builds the banners in a column anchored under the bell, newest first, each entering like a popup; it takes no focus, so typing elsewhere goes on.</summary>
	private Portal BuildBanners()
	{
		var portal = new Portal
		{
			Key = "notification-banners",
			Anchor = _handle,
			Placement = PortalPlacement.BottomEnd,
			Width = BannerWidth,
			Padding = new Edges( Theme.Current.Spacing, 0, 0, 0 ),
			Gap = Theme.Current.Spacing,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "New notifications",
		};
		foreach ( var banner in Enumerable.Reverse( _banners ) )
		{
			var content = new Container { Children = [banner.Content.Build()] };
			portal.Add( Cell.Mount<PopupEntranceInput, PopupEntrance>( "banner:" + banner.Item!.Id.ToString( "N" ), new( content, -PopupMotion.PopoverRise ) ) );
		}

		return portal;
	}

	/// <summary>Holds the popover or one banner: the notification a banner announces, its content and the source that ends its lifetime.</summary>
	private sealed record Overlay( LoknNotification? Item, PanelContent Content, CancellationTokenSource Lifetime );
}
