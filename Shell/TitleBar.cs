using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Builds the custom titlebar and native window controls.</summary>
internal sealed partial class TitleBar : GuardedCell<TitleBarInput>
{
	/// <summary>Defines the titlebar height in logical pixels; the full-window notification feed starts below it.</summary>
	internal const int BarHeight = 36;

	/// <summary>Defines each window control's width in logical pixels.</summary>
	private const int ControlWidth = 46;

	/// <summary>Defines the control glyph size in logical pixels.</summary>
	private const int ControlFontSize = 10;

	/// <summary>Defines the caption icon font weight on the OpenType scale.</summary>
	private const int CaptionFontWeight = 400;

	/// <summary>Retains the registered system caption font for the application lifetime; the notification views use its icons too.</summary>
	internal static readonly FontSource CaptionFont = BuildCaptionFont();

	/// <summary>Defines the native Windows minimize glyph.</summary>
	private const string MinimizeGlyph = "\uE921";

	/// <summary>Defines the native Windows maximize glyph.</summary>
	private const string MaximizeGlyph = "\uE922";

	/// <summary>Defines the native Windows restore glyph.</summary>
	private const string RestoreGlyph = "\uE923";

	/// <summary>Defines the native Windows close glyph.</summary>
	private const string CloseGlyph = "\uE8BB";

	/// <summary>Defines the titlebar divider thickness in logical pixels.</summary>
	private const int DividerWidth = 1;

	/// <summary>Defines the close control hover color in sRGB, matching the Windows caption convention.</summary>
	private static readonly Color CloseHighlight = Color.Parse( "#c42b1c" );

	/// <summary>Builds the draggable titlebar and its window controls, with content such as the tabs in its middle.</summary>
	protected override Blob BuildView( TitleBarInput input )
	{
		var isMaximized = Host.State == WindowState.Maximized;

		// Goo excludes interactive descendants from native dragging and double-clicks.
		return Window.DragRegion( new Container
		{
			FlexDirection = FlexDirection.Row,
			Height = BarHeight,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Color = Host.IsFocused ? Theme.Current.Text : Theme.Current.Inactive,
			BorderWidth = new Edges( 0, 0, DividerWidth, 0 ),
			BorderColor = Theme.Current.Divider,
			FontFamily = Theme.Current.FontFamily,
			Children =
			[
				BuildWorkspaceActions( input ),
				.. input.Left.Select( BuildSlot ),
				input.Middle ?? new Spacer(),
				.. input.Right.Select( BuildSlot ),
				BuildControl( "Minimize", MinimizeGlyph, () => Host.State = WindowState.Minimized ),
				BuildControl( isMaximized ? "Restore" : "Maximize", isMaximized ? RestoreGlyph : MaximizeGlyph, UpdateMaximized ),
				BuildControl( "Close", CloseGlyph, Host.RequestClose, isClose: true ),
			],
		} );
	}

	/// <summary>Builds one module contribution, sized by its own content and centered in the bar's height.</summary>
	private Blob BuildSlot( SlotItem item ) => new Container
	{
		Key = $"slot:{item.Id}",
		FlexShrink = 0,
		Height = BarHeight,
		// The slot is a column, so its content is centered down the bar by justifying, and across by aligning.
		JustifyContent = JustifyContent.Center,
		AlignItems = AlignItems.Center,
		Children = [Mount<ContentSurface>( $"slot-surface:{item.Id}", () => new( item ) )],
	};

	/// <summary>Builds a labeled window control with pointer and keyboard activation.</summary>
	private Button BuildControl( string label, string glyph, Action action, bool isClose = false )
	{
		return new Button
		{
			Width = ControlWidth,
			Height = BarHeight,
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
					FontFamily = CaptionFont.Family,
				},
			],
		};
	}

	/// <summary>Registers the installed Windows icon font used by the caption controls.</summary>
	private static FontSource BuildCaptionFont()
	{
		var fontPath = Path.Combine(
			Environment.GetFolderPath( Environment.SpecialFolder.Fonts ),
			"SegoeIcons.ttf" );
		var font = FontSource.FromPath( "Lokn Caption", CaptionFontWeight, italic: false, fontPath );

		// Goo needs explicit registration to render the font's private-use caption glyphs.
		font.Register();
		return font;
	}

	/// <summary>Toggles the window between normal and maximized states.</summary>
	private void UpdateMaximized()
	{
		Host.State = Host.State == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
	}
}
