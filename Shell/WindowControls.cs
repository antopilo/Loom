using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Builds the window's minimize, maximize or restore, and close buttons: in the titlebar, or in the top-right pane once the sidebar holds the
/// titlebar's actions.</summary>
internal static class WindowControls
{
	/// <summary>Defines each window control's width in logical pixels.</summary>
	internal const int ControlWidth = 46;

	/// <summary>Defines the width of the three controls together in logical pixels.</summary>
	internal const int Width = ControlWidth * 3;

	/// <summary>Defines the control glyph size in logical pixels.</summary>
	private const int ControlFontSize = 10;

	/// <summary>Defines the native Windows minimize glyph.</summary>
	private const string MinimizeGlyph = "\uE921";

	/// <summary>Defines the native Windows maximize glyph.</summary>
	private const string MaximizeGlyph = "\uE922";

	/// <summary>Defines the native Windows restore glyph.</summary>
	private const string RestoreGlyph = "\uE923";

	/// <summary>Defines the native Windows close glyph.</summary>
	private const string CloseGlyph = "\uE8BB";

	/// <summary>Defines the close control hover color in sRGB, matching the Windows caption convention.</summary>
	private static readonly Color CloseHighlight = Color.Parse( "#c42b1c" );

	/// <summary>Builds the three controls of a window side by side.</summary>
	/// <param name="window">The window they act on.</param>
	/// <param name="height">The controls' height in logical pixels.</param>
	/// <param name="cornerRadius">The radius of the close control's top-right corner, so it follows a rounded pane; 0 in the titlebar.</param>
	internal static Row Build( Window window, double height, double cornerRadius )
	{
		var isMaximized = window.State == WindowState.Maximized;
		var close = BuildControl( "Close", CloseGlyph, window.RequestClose, height, isClose: true );
		close.BorderTopRightRadius = cornerRadius;
		return new Row
		{
			Key = "window-controls",
			FlexShrink = 0,
			Children =
			[
				BuildControl( "Minimize", MinimizeGlyph, () => window.State = WindowState.Minimized, height ),
				BuildControl( isMaximized ? "Restore" : "Maximize", isMaximized ? RestoreGlyph : MaximizeGlyph,
					() => window.State = window.State == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized, height ),
				close,
			],
		};
	}

	/// <summary>Builds a labeled window control with pointer and keyboard activation.</summary>
	private static Button BuildControl( string label, string glyph, Action action, double height, bool isClose = false ) => new()
	{
		Key = "window:" + label,
		Width = ControlWidth,
		Height = height,
		FlexShrink = 0,
		AlignItems = AlignItems.Center,
		JustifyContent = JustifyContent.Center,
		Focusable = true,
		AccessibilityName = label,
		OnClick = action,
		KeyBindings =
		[
			new KeyBinding( Key.Enter, KeyModifiers.None, action ),
			new KeyBinding( Key.Space, KeyModifiers.None, action ),
		],
		HoverStyle = new Style
		{
			BackgroundColor = isClose ? CloseHighlight : Theme.Current.CaptionHover,
			Color = isClose ? Color.White : Theme.Current.Text,
		},
		FocusStyle = new Style
		{
			BackgroundColor = Theme.Current.CaptionHover,
		},
		Children =
		[
			new Text
			{
				Content = glyph,
				FontSize = ControlFontSize,
				FontFamily = TitleBar.CaptionFont.Family,
			},
		],
	};
}
