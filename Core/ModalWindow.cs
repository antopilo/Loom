using Goo;

namespace Lokn.Core;

/// <summary>Supplies a <see cref="ModalWindow"/> with its content and the way to close it.</summary>
/// <param name="App">The application whose native surfaces, such as browser pages, the window hides while it is open, since they draw over it.</param>
/// <param name="Title">The window's name for screen readers, such as "Settings".</param>
/// <param name="Content">What the window shows; it fills the window, which clips it to its rounded corners.</param>
/// <param name="Dismiss">Closes the window; runs on Escape, a click on the scrim and the close button.</param>
public sealed record ModalWindowInput( LoknApplication App, string Title, IReadOnlyList<Blob> Content, Action Dismiss )
{
	/// <summary>Gets the window's width in logical pixels, or null to fit its content.</summary>
	public double? Width { get; init; }

	/// <summary>Gets the window's height in logical pixels, or null to fit its content.</summary>
	public double? Height { get; init; }

	/// <summary>Gets the widest the window grows, as a share of the shell from 0 to 100.</summary>
	public double MaxWidthPercent { get; init; } = 95;

	/// <summary>Gets the tallest the window grows, as a share of the shell from 0 to 100.</summary>
	public double MaxHeightPercent { get; init; } = 90;

	/// <summary>Gets the window's distance from the top of the shell in logical pixels, or null to center it.</summary>
	public double? Top { get; init; }

	/// <summary>Gets the control that holds focus when the window opens, or null for the first one.</summary>
	public ElementHandle? InitialFocus { get; init; }

	/// <summary>Gets whether a close button sits in the window's top right corner.</summary>
	public bool ShowsClose { get; init; }

	/// <summary>Gets whether Escape and the scrim may close the window now; false while its work runs.</summary>
	public bool CanDismiss { get; init; } = true;
}

/// <summary>The app's one modal window: a raised card over a scrim that takes all input until it closes, used by Search, confirmations and popup panels.</summary>
/// <remarks>Mount it with <c>Cell.Mount&lt;ModalWindowInput, ModalWindow&gt;</c> while the window is open and leave it out to close it.</remarks>
public sealed class ModalWindow : GuardedCell<ModalWindowInput>
{
	/// <summary>Defines the opacity of the scrim over the shell.</summary>
	private const double ScrimOpacity = 0.45;

	/// <summary>Defines how much rounder the window is than the theme's controls, in logical pixels.</summary>
	private const int ExtraRadius = 4;

	/// <summary>Gets or sets the space kept clear above the window in logical pixels; the shell sets it to its title bar height.</summary>
	internal static double TitleBarClearance { get; set; }

	/// <summary>Defines the Segoe Fluent Cancel glyph of the close button.</summary>
	private const string CloseGlyph = "\uE711";

	/// <summary>Defines the close button's size in logical pixels.</summary>
	private const int CloseSize = 28;

	/// <summary>Tracks the window's entrance from 0, faded out, scaled down and lowered, to 1 at rest.</summary>
	private Anim<double>? _appear;

	/// <summary>Starts the entrance on the first input.</summary>
	protected override void InputChanged( ModalWindowInput input ) => _appear ??= PopupMotion.Start( Animate( 0.0 ) );

	/// <summary>Hides native surfaces, such as browser pages, which would draw over the scrim and window.</summary>
	protected override void Mounted() => Input.App.CoverNativeSurfaces( this, true );

	/// <summary>Shows native surfaces again.</summary>
	protected override void Unmounting() => Input.App.CoverNativeSurfaces( this, false );

	/// <summary>Builds the scrim filling the shell and the window on it, centered or at its top offset.</summary>
	protected override Blob BuildView( ModalWindowInput input )
	{
		var theme = Theme.Current;
		var children = new List<Blob>( input.Content );
		if ( input.ShowsClose )
		{
			var close = new Button
			{
				Key = "modal-close",
				Position = PositionType.Absolute,
				Top = theme.Spacing,
				Right = theme.Spacing,
				Width = CloseSize,
				Height = CloseSize,
				AlignItems = AlignItems.Center,
				JustifyContent = JustifyContent.Center,
				BorderRadius = theme.Radius,
				BackgroundColor = Color.Transparent,
				Color = theme.Muted,
				Focusable = false,
				Cursor = Cursor.Pointer,
				AccessibilityName = "Close",
				OnClick = () => InvokeInputCallback( input.Dismiss ),
				HoverStyle = new Style { BackgroundColor = theme.CaptionHover, Color = theme.Text },
				Children = [new Text { Content = CloseGlyph, FontFamily = ViewStyle.IconFontFamily, FontSize = 12 }],
			};
			children.Add( close );
		}

		var window = new Container
		{
			Key = "modal-window",
			MaxWidth = Length.Percent( input.MaxWidthPercent ),
			MaxHeight = Length.Percent( input.MaxHeightPercent ),
			MinHeight = 0,
			FontFamily = theme.FontFamily,
			FontSize = theme.FontSize,
			Color = theme.Text,
			BackgroundColor = theme.Raised,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius + ExtraRadius,
			Overflow = Overflow.Hidden,
			BoxShadow = new BoxShadow { OffsetY = 12, Blur = 40, Color = Color.Black.WithAlpha( 0.45 ) },
			Children = children,
		};
		PopupMotion.Apply( window, _appear?.Value ?? 1, PopupMotion.DialogRise );
		if ( input.Width is { } width )
			window.Width = width;
		if ( input.Height is { } height )
			window.Height = height;

		// The portal fills the shell and places the window; the modal scope keeps focus and input inside it.
		var portal = new Portal
		{
			Key = "modal",
			Left = 0,
			Top = 0,
			Width = Length.Percent( 100 ),
			Height = Length.Percent( 100 ),
			AlignItems = AlignItems.Center,
			JustifyContent = input.Top is null ? JustifyContent.Center : JustifyContent.FlexStart,
			Padding = new Edges( Math.Max( input.Top ?? 0, TitleBarClearance ), 0, 0, 0 ),
			Scrim = Color.Black.WithAlpha( ScrimOpacity * (_appear?.Value ?? 1) ),
			FocusScope = new FocusScopeOptions { Modal = true, InitialFocus = input.InitialFocus },
			AccessibilityRole = AccessibilityRole.Dialog,
			AccessibilityName = input.Title,
			OnDismiss = _ =>
			{
				if ( input.CanDismiss )
					InvokeInputCallback( input.Dismiss );
			},
		};
		portal.Add( window );
		return portal;
	}
}
