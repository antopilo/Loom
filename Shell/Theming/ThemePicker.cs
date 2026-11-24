using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Builds the theme setting's editor: a card per theme that previews it across the app on hover and picks it on click.</summary>
internal static class ThemePicker
{
	/// <summary>Defines a card's width in logical pixels.</summary>
	private const int CardWidth = 132;

	/// <summary>Defines the sample's height in logical pixels.</summary>
	private const int SampleHeight = 58;

	/// <summary>Defines the sample text size in logical pixels.</summary>
	private const int SampleSize = 15;

	/// <summary>Defines a color dot's diameter in logical pixels.</summary>
	private const int DotSize = 9;

	/// <summary>Defines the theme name size in logical pixels.</summary>
	private const int NameSize = 12;

	/// <summary>Defines the selected sample's border width in logical pixels.</summary>
	private const int SelectedBorder = 2;

	/// <summary>Builds the light and dark groups for the scope being edited.</summary>
	internal static Blob Build( LoknApplication app, SettingsScope scope )
	{
		var module = app.GetModule<ThemeModule>();
		var selected = (string)app.Settings.GetValue( ThemeModule.Selected, scope );
		void Pick( ThemeEntry entry )
		{
			app.Settings.Set( ThemeModule.Selected, scope, entry.Name );
			module.Preview( null );
		}

		Blob Group( string title, IEnumerable<ThemeEntry> entries ) => new Container
		{
			Key = title,
			Gap = Theme.Current.Spacing / 2,
			Children =
			[
				new Text
				{
					Content = title,
					Color = Theme.Current.Muted,
					FontSize = NameSize,
				},
				new Row
				{
					FlexWrap = FlexWrap.Wrap,
					Gap = Theme.Current.Spacing,
					Children = [.. entries.Select( entry => BuildCard( entry, string.Equals( entry.Name, selected, StringComparison.OrdinalIgnoreCase ), () => Pick( entry ), module.Hover ) )],
				},
			],
		};

		return new Container
		{
			Gap = Theme.Current.Spacing,
			// Leaving the whole picker ends any preview, even when a card's own leave was missed.
			OnPointerLeave = _ => module.Hover( null ),
			Children =
			[
				Group( "Light", ThemeCatalog.Entries.Where( entry => entry.IsLight ) ),
				Group( "Dark", ThemeCatalog.Entries.Where( entry => !entry.IsLight ) ),
			],
		};
	}

	/// <summary>Builds one card: the theme's panel with sample text and color dots, then its name.</summary>
	private static Button BuildCard( ThemeEntry entry, bool isSelected, Action pick, Action<Theme?> preview )
	{
		var theme = entry.Theme;
		var current = Theme.Current;
		Color[] swatches = [theme.AnsiRed, theme.AnsiYellow, theme.AnsiGreen, theme.AnsiCyan, theme.AnsiBlue, theme.AnsiMagenta, theme.Accent];
		return new Button
		{
			Key = entry.Name,
			Width = CardWidth,
			Padding = current.Spacing / 2,
			Gap = current.Spacing / 2,
			BorderRadius = current.Radius + current.Spacing / 2,
			BackgroundColor = isSelected ? current.Hover : Color.Transparent,
			Focusable = true,
			Cursor = Cursor.Pointer,
			AccessibilityName = isSelected ? $"{entry.Name} theme, selected" : $"{entry.Name} theme",
			HoverStyle = new Style
			{
				BackgroundColor = current.Hover,
			},
			FocusStyle = new Style
			{
				BackgroundColor = current.Hover,
			},
			OnClick = pick,
			OnPointerEnter = _ => preview( theme ),
			OnPointerLeave = _ => preview( null ),
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, pick ),
				new( Key.Space, KeyModifiers.None, pick ),
			],
			Children =
			[
				new Container
				{
					Height = SampleHeight,
					Padding = current.Spacing,
					JustifyContent = JustifyContent.SpaceBetween,
					BackgroundColor = theme.Panel,
					BorderColor = isSelected ? current.Accent : theme.Border,
					BorderWidth = isSelected ? SelectedBorder : current.BorderWidth,
					BorderRadius = current.Radius,
					Children =
					[
						new Text
						{
							Content = "Aa",
							Color = theme.Text,
							FontSize = SampleSize,
						},
						new Row
						{
							Gap = DotSize / 2,
							Children = [.. swatches.Select( color => new Container
							{
								Width = DotSize,
								Height = DotSize,
								BorderRadius = DotSize / 2.0,
								BackgroundColor = color,
							} )],
						},
					],
				},
				new Text
				{
					Content = entry.Name,
					Color = isSelected ? current.Text : current.Muted,
					FontSize = NameSize,
					TextWrap = TextWrap.NoWrap,
					TextTrimming = TextTrimming.Ellipsis,
				},
			],
		};
	}
}
