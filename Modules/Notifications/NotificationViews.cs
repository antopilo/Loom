using System.Globalization;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Notifications;

/// <summary>The view pieces the feed page, the popover and the banners share: their icon font, a notification's icon with its tone mark, small
/// buttons, pills, and how times and previews read.</summary>
/// <remarks>They are the module's own, so the views draw without any other module.</remarks>
internal static class NotificationViews
{
	/// <summary>Defines the size of small icons in logical pixels.</summary>
	internal const int IconSize = 12;

	/// <summary>Defines the box a notification's icon sits in, in logical pixels.</summary>
	internal const int IconBox = 20;

	/// <summary>Defines a source icon's size in logical pixels.</summary>
	internal const int SourceIconSize = 16;

	/// <summary>Defines the tone mark's diameter in logical pixels, its ring included.</summary>
	private const int MarkSize = 13;

	/// <summary>Defines the width of the ring that sets the tone mark off the icon, in logical pixels.</summary>
	private const int MarkRing = 2;

	/// <summary>Defines the left inset of a row's text: the icon's box and the gap after it, from the row's own inset.</summary>
	internal const int TextInset = 40;

	/// <summary>Defines the longest message preview in characters.</summary>
	private const int PreviewLimit = 160;

	/// <summary>Defines the emphasized weight of titles.</summary>
	internal const int TitleWeight = 600;

	/// <summary>Defines the Segoe Fluent CheckMark glyph of the done mark.</summary>
	private const string SuccessGlyph = "\uE73E";

	/// <summary>Defines the Segoe Fluent Cancel glyph of the failed mark.</summary>
	private const string FailureGlyph = "\uE711";

	/// <summary>Holds the installed icon font, registered once; Goo draws only fonts registered with it.</summary>
	private static readonly FontSource Icons = SystemFonts.Register( "Lokn Notification Icons", 400, italic: false, "SegoeIcons.ttf" );

	/// <summary>Gets the icon font's family.</summary>
	internal static string IconFont => Icons.Family;

	/// <summary>Builds selectable text, muted on request.</summary>
	internal static Text BuildText( string text, bool muted = false ) => new()
	{
		Content = text,
		Color = muted ? Theme.Current.Muted : Theme.Current.Text,
		FontSize = Theme.Current.FontSize,
		Selectable = true,
		MinWidth = 0,
	};

	/// <summary>Builds one icon glyph.</summary>
	internal static Text BuildGlyph( string glyph, Color? color = null, int size = IconSize ) => new()
	{
		Content = glyph,
		FontFamily = IconFont,
		FontSize = size,
		FlexShrink = 0,
		Color = color ?? Theme.Current.Muted,
	};

	/// <summary>Builds a notification's icon in its box, with its tone's mark at the lower right.</summary>
	/// <param name="source">The source icon.</param>
	/// <param name="tone">The mark: done, failed, waiting, or none.</param>
	/// <param name="ring">The color behind the icon, which rings the mark so it stands off the icon.</param>
	internal static Container BuildIcon( Blob source, NotificationTone tone, Color ring )
	{
		var children = new List<Blob> { source };
		if ( BuildMark( tone, ring ) is { } mark )
			children.Add( mark );
		return new Container
		{
			Key = "icon",
			Width = IconBox,
			Height = IconBox,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			Children = children,
		};
	}

	/// <summary>Builds a tone's mark, a small filled circle with its sign, or null for no tone.</summary>
	private static Container? BuildMark( NotificationTone tone, Color ring )
	{
		var theme = Theme.Current;
		var (color, sign) = tone switch
		{
			NotificationTone.Success => (theme.Success, (Blob)BuildGlyph( SuccessGlyph, ring, 6 )),
			NotificationTone.Failure => (theme.Error, BuildGlyph( FailureGlyph, ring, 6 )),
			NotificationTone.Attention => (theme.Attention, new Text { Content = "?", FontSize = 8, FontWeight = 700, Color = ring }),
			_ => (Color.Transparent, (Blob?)null),
		};
		if ( sign is null )
			return null;

		return new Container
		{
			Key = "mark",
			Position = PositionType.Absolute,
			Right = -3,
			Bottom = -3,
			Width = MarkSize,
			Height = MarkSize,
			BorderRadius = MarkSize / 2.0,
			BorderWidth = MarkRing,
			BorderColor = ring,
			BackgroundColor = color,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			Children = [sign],
		};
	}

	/// <summary>Builds a small rounded count, such as how many updates a group holds.</summary>
	internal static Container BuildCount( int count, bool isAttention = false )
	{
		var theme = Theme.Current;
		return new Container
		{
			Key = "count",
			FlexShrink = 0,
			Padding = new Edges( 0, 6 ),
			BorderRadius = 999,
			BackgroundColor = isAttention ? theme.Attention : theme.Raised,
			Children =
			[
				new Text
				{
					Content = count.ToString( CultureInfo.CurrentCulture ),
					FontSize = ViewStyle.SmallSize,
					FontWeight = isAttention ? TitleWeight : 400,
					Color = isAttention ? theme.Canvas : theme.Muted,
				},
			],
		};
	}

	/// <summary>Builds a borderless muted text action, highlighted under the pointer.</summary>
	internal static Button BuildQuietButton( string label, Action action, bool isEnabled = true, string? glyph = null )
	{
		var theme = Theme.Current;
		var button = theme.BuildButton( label, action, isEnabled );
		button.BackgroundColor = Color.Transparent;
		button.BorderWidth = 0;
		button.Color = theme.Muted;
		button.FontSize = ViewStyle.SmallSize;
		button.FlexShrink = 0;
		button.HoverStyle = new Style { BackgroundColor = theme.Hover, Color = theme.Text };
		if ( glyph is not null )
		{
			button.FlexDirection = FlexDirection.Row;
			button.AlignItems = AlignItems.Center;
			button.Gap = theme.Spacing / 2;
			button.Children = [BuildGlyph( glyph ), new Text { Content = label }];
		}
		return button;
	}

	/// <summary>Builds a square icon button, muted until hovered.</summary>
	internal static Button BuildIconButton( string name, string glyph, Action action )
	{
		var theme = Theme.Current;
		var button = theme.BuildButton( name, action );
		button.AccessibilityName = name;
		button.Width = theme.ControlHeight;
		button.Height = theme.ControlHeight;
		button.Padding = 0;
		button.FlexShrink = 0;
		button.JustifyContent = JustifyContent.Center;
		button.BackgroundColor = Color.Transparent;
		button.BorderWidth = 0;
		button.HoverStyle = new Style { BackgroundColor = theme.Hover };
		button.Children = [BuildGlyph( glyph )];
		return button;
	}

	/// <summary>Builds a filled button, in the accent color when primary.</summary>
	internal static Button BuildButton( string label, Action action, bool isPrimary = false, string? glyph = null )
	{
		var theme = Theme.Current;
		var button = theme.BuildButton( label, action );
		button.FlexShrink = 0;
		button.BorderWidth = 0;
		var color = isPrimary ? theme.Canvas : theme.Text;
		button.BackgroundColor = isPrimary ? theme.Accent : theme.Text.WithAlpha( 0.07 );
		button.Color = color;
		button.HoverStyle = new Style { BackgroundColor = isPrimary ? theme.Accent.WithAlpha( 0.86 ) : theme.Text.WithAlpha( 0.13 ) };
		if ( glyph is not null )
		{
			button.FlexDirection = FlexDirection.Row;
			button.AlignItems = AlignItems.Center;
			button.Gap = theme.Spacing / 2;
			button.Children = [BuildGlyph( glyph, color ), new Text { Content = label }];
		}
		return button;
	}

	/// <summary>Formats how long ago a notification came, short enough for a row's right edge: now, minutes, hours today, else the time of day.</summary>
	/// <remarks>The day heading above a row names its day, so older rows need only their time.</remarks>
	internal static string FormatAge( DateTimeOffset time, DateTimeOffset now )
	{
		var age = now - time;
		if ( age < TimeSpan.FromMinutes( 1 ) )
			return "now";
		if ( age < TimeSpan.FromHours( 1 ) )
			return $"{(int)age.TotalMinutes}m";
		if ( age < TimeSpan.FromHours( 6 ) && time.ToLocalTime().Date == now.ToLocalTime().Date )
			return $"{(int)age.TotalHours}h";
		return time.ToLocalTime().ToString( "t", CultureInfo.CurrentCulture );
	}

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
}
