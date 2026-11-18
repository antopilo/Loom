using Goo;
using Lokn.Core;

namespace Lokn.Modules.Notifications;

/// <summary>The few view pieces the feed, bell and banners share: their icon font, reading column, text and the button back to the newest row.</summary>
/// <remarks>They are the module's own, so the feed draws without any other module.</remarks>
internal static class NotificationViews
{
	/// <summary>Defines the size of small icons in logical pixels.</summary>
	internal const int IconSize = 12;

	/// <summary>Defines the widest reading column in logical pixels, like a chat's.</summary>
	private const int ColumnWidth = 760;

	/// <summary>Defines the Segoe Fluent ChevronDown glyph on the button back to the newest row.</summary>
	private const string LatestGlyph = "\uE70D";

	/// <summary>Holds the installed Windows icon font, registered once; Goo draws only fonts registered with it.</summary>
	private static readonly FontSource Icons = BuildIconFont();

	/// <summary>Gets the icon font's family.</summary>
	internal static string IconFont => Icons.Family;

	/// <summary>Gets the feed panel's inset in logical pixels.</summary>
	internal static int ContentPadding => Theme.Current.Spacing * 2;

	/// <summary>Builds a centered reading column at most <see cref="ColumnWidth"/> wide.</summary>
	internal static Container BuildColumn( string key, List<Blob> children, int gap ) => new()
	{
		Key = key,
		Width = Length.Percent( 100 ),
		MaxWidth = ColumnWidth,
		AlignSelf = AlignSelf.Center,
		FlexShrink = 0,
		Gap = gap,
		Children = children,
	};

	/// <summary>Builds selectable text, muted on request.</summary>
	internal static Text BuildText( string text, bool muted = false ) => new()
	{
		Content = text,
		Color = muted ? Theme.Current.Muted : Theme.Current.Text,
		FontSize = Theme.Current.FontSize,
		Selectable = true,
		MinWidth = 0,
	};

	/// <summary>Builds the round button, centered at the bottom of the feed, that returns to the newest row once the reader scrolls up.</summary>
	internal static Button BuildLatest( Action scrollToEnd )
	{
		var theme = Theme.Current;
		var size = theme.ControlHeight + 4;
		return new Button
		{
			Key = "notifications-latest",
			AccessibilityName = "Latest",
			Position = PositionType.Absolute,
			// Centered by its left edge at half the width, pulled back by half its own.
			Left = Length.Percent( 50 ),
			Margin = new Edges( 0, 0, 0, -size / 2 ),
			Bottom = theme.Spacing * 2,
			Width = size,
			Height = size,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = size / 2.0,
			BorderWidth = theme.BorderWidth,
			BorderColor = theme.Border,
			BackgroundColor = theme.Raised,
			Focusable = true,
			Cursor = Cursor.Pointer,
			OnClick = scrollToEnd,
			KeyBindings = [new( Key.Enter, KeyModifiers.None, scrollToEnd ), new( Key.Space, KeyModifiers.None, scrollToEnd )],
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			FocusStyle = new Style { BackgroundColor = theme.Hover },
			Children = [new Text { Content = LatestGlyph, FontFamily = IconFont, FontSize = IconSize, Color = theme.Text }],
		};
	}

	/// <summary>Registers Segoe Fluent Icons for the notification views.</summary>
	private static FontSource BuildIconFont()
	{
		var font = FontSource.FromPath( "Lokn Notification Icons", 400, italic: false, Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.Fonts ), "SegoeIcons.ttf" ) );
		font.Register();
		return font;
	}
}
