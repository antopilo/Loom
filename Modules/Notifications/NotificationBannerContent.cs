using Goo;
using Lokn.Core;

namespace Lokn.Modules.Notifications;

/// <summary>Announces one notification as it arrives, in a card under the titlebar bell, with the question an agent waits on answered in place.</summary>
/// <remarks>
/// The card leaves on its own after a few seconds, later while the pointer is on it. While its chat waits on a question it stays
/// until that is answered or withdrawn, the user opens the chat, or closes the card; the shell also removes it once the user looks
/// at its panel. Its name opens the notification as the feed does.
/// </remarks>
internal sealed class NotificationBannerContent : PanelContent
{
	/// <summary>Defines how long a banner without a question stays.</summary>
	private static readonly TimeSpan Duration = TimeSpan.FromSeconds( 6 );

	/// <summary>Defines the longest message preview in characters.</summary>
	private const int PreviewLimit = 160;

	/// <summary>Defines the emphasized weight of the source name.</summary>
	private const int SourceWeight = 600;

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

	/// <summary>Builds the card: the source's icon and name, then the question its chat waits on, or else the message.</summary>
	public override Blob Build()
	{
		var theme = Theme.Current;
		var question = _item.PanelId is { } panelId && _context.App.Panels.ResolvePanelId( panelId ) is var waiting
			? NotificationPromptSource.ListAll( _context.App ).FirstOrDefault( prompt => prompt.PanelId == waiting )
			: null;
		if ( _isExpired.Value && question is null )
		{
			// Removing it changes the shell, which must not happen while it builds.
			UpdateDismiss();
			return new Container { Key = "banner-leaving" };
		}

		var children = new List<Blob> { BuildHeader( question is not null ) };
		if ( question?.Build( "banner" ) is { } dialog )
			children.Add( dialog );
		else
		{
			children.Add( new Text
			{
				Key = "message",
				Content = ComputePreview( _item.Message ),
				Color = theme.Muted,
				Padding = new Edges( 0, 0, 0, theme.Spacing ),
			} );
			if ( NotificationsContent.BuildActions( _item, OpenLink ) is { } actions )
				children.Add( actions );
		}

		return new Container
		{
			Key = "banner",
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = question is null ? $"{_item.Title}: {_item.Message}" : $"{_item.Title} is waiting for you",
			Width = Length.Percent( 100 ),
			Padding = new Edges( theme.Spacing * 1.5, theme.Spacing * 1.5 ),
			Gap = theme.Spacing,
			BackgroundColor = theme.Panel,
			BorderColor = question is null ? theme.Border : theme.Attention,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius * 2,
			BoxShadow = new BoxShadow { OffsetY = 6, Blur = 18, Color = Color.Black.WithAlpha( 0.35 ) },
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

	/// <summary>Shortens a message to its first line of prose, cut at <see cref="PreviewLimit"/> characters, without Markdown syntax: fences and table rules are skipped, table cells are joined with " · ".</summary>
	internal static string ComputePreview( string message )
	{
		var line = "";
		foreach ( var raw in message.Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
		{
			if ( raw.StartsWith( "```", StringComparison.Ordinal ) || raw.StartsWith( "~~~", StringComparison.Ordinal ) || raw.Trim( '|', '-', ':', ' ', '*', '_', '=' ).Length == 0 )
				continue;
			line = raw.TrimStart( '#', '>', ' ' );
			if ( line.StartsWith( "- ", StringComparison.Ordinal ) || line.StartsWith( "* ", StringComparison.Ordinal ) )
				line = line[2..];
			if ( line.StartsWith( '|' ) )
				line = string.Join( " · ", line.Trim( '|' ).Split( '|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) );
			line = line.Replace( "**", "", StringComparison.Ordinal ).Replace( "`", "", StringComparison.Ordinal );
			break;
		}
		return line.Length > PreviewLimit ? line[..(PreviewLimit - 1)].TrimEnd() + "…" : line;
	}

	/// <summary>Builds the source's icon and name, which open the notification, and the button that closes the banner.</summary>
	private Row BuildHeader( bool isWaiting )
	{
		var theme = Theme.Current;
		var name = new List<Blob>
		{
			NotificationsContent.BuildSourceIcon( _context.App, _item, panelId => _context.App.Panels.Panels.FirstOrDefault( panel => panel.Id == panelId )?.TypeId ),
			new Text
			{
				Content = _item.Title,
				FontWeight = SourceWeight,
				FlexShrink = 1,
				MinWidth = 0,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
		};
		if ( isWaiting )
			name.Add( new Text { Content = "is waiting for you", Color = theme.Muted, FlexShrink = 0 } );

		void Open() => _context.Run( () =>
		{
			UpdateDismiss();
			NotificationsModule.Open( _context.App, _item, feedPanelId: null );
		} );
		var open = new Button
		{
			Key = "open",
			FlexShrink = 1,
			MinWidth = 0,
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Padding = new Edges( 2, theme.Spacing / 2 ),
			Margin = new Edges( 0, 0, 0, -theme.Spacing / 2 ),
			BorderRadius = theme.Radius,
			Focusable = true,
			Cursor = Cursor.Pointer,
			AccessibilityName = $"Open {_item.Title}",
			OnClick = Open,
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, Open ),
				new( Key.Space, KeyModifiers.None, Open ),
			],
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			FocusStyle = new Style { BackgroundColor = theme.Hover },
			Children = name,
		};

		var close = theme.BuildButton( "Close", () => _context.Run( UpdateDismiss ) );
		close.AccessibilityName = "Close notification";
		close.Width = theme.ControlHeight;
		close.Height = theme.ControlHeight;
		close.Padding = 0;
		close.FlexShrink = 0;
		close.BackgroundColor = Color.Transparent;
		close.BorderWidth = 0;
		close.HoverStyle = new Style { BackgroundColor = theme.Hover };
		close.Children = [new Text { Content = CloseGlyph, FontFamily = NotificationViews.IconFont, FontSize = NotificationViews.IconSize, Color = theme.Muted }];

		return new Row
		{
			Key = "header",
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			MinWidth = 0,
			Children = [open, new Spacer(), close],
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
