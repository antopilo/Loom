using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Supplies a <see cref="SidebarPeek"/> with whether it is out, what it shows and how it reports the pointer.</summary>
/// <param name="IsOpen">Whether the sidebar is wanted out; a change slides it in or out.</param>
/// <param name="Width">The sidebar's width in logical pixels.</param>
/// <param name="Content">The sidebar's content, the same the docked sidebar shows.</param>
/// <param name="Enter">Runs when the pointer comes onto the floating sidebar.</param>
/// <param name="Leave">Runs when the pointer leaves it.</param>
internal sealed record SidebarPeekInput( bool IsOpen, double Width, Blob Content, Action Enter, Action Leave );

/// <summary>Floats the hidden sidebar over the workspace's left side while the pointer is at the window's edge, sliding it in and out, like Arc's.</summary>
/// <remarks>Only this cell rebuilds while it slides. With reduced motion it shows and hides at once. While fully in, nothing is drawn, so it takes no pointer.</remarks>
internal sealed class SidebarPeek : GuardedCell<SidebarPeekInput>
{
	/// <summary>Defines the slide's duration in milliseconds.</summary>
	private const double SlideMs = 180;

	/// <summary>Defines the space between the floating sidebar and the window's edges in logical pixels.</summary>
	internal const int Inset = 8;

	/// <summary>Tracks the slide from 0, out, to 1, in past the window's edge.</summary>
	private Anim<double>? _hidden;

	/// <summary>Stores whether the last input wanted the sidebar out.</summary>
	private bool? _isOpen;

	/// <summary>Slides the sidebar out or in when the input wants it.</summary>
	protected override void InputChanged( SidebarPeekInput input )
	{
		_hidden ??= Animate( 1.0 );
		if ( _isOpen == input.IsOpen )
			return;

		_isOpen = input.IsOpen;
		var target = input.IsOpen ? 0.0 : 1.0;
		if ( Motion.TimeScale <= 0 || Host.Preferences.ReducedMotion == true )
			_hidden.Play( target, target, Motion.Tween( 1, Easing.Linear ) );
		else
			_hidden.Play( _hidden.Value, target, Motion.Tween( SlideMs, Easing.EaseOut ) );
	}

	/// <summary>Builds the floating sidebar at the slide's current place; nothing while it is fully in.</summary>
	protected override Blob BuildView( SidebarPeekInput input )
	{
		var hidden = _hidden?.Value ?? 1;
		if ( hidden >= 1 && !input.IsOpen )
			return new Container { Key = "sidebar-peek-none" };

		var theme = Theme.Current;
		// A row, so the sidebar column fills the floating box's height as it fills the body's.
		return new Row
		{
			Key = "sidebar-peek",
			Position = PositionType.Absolute,
			Left = Inset,
			Top = Inset,
			Bottom = Inset,
			Width = input.Width,
			BackgroundColor = theme.Canvas,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius * 2,
			Overflow = Overflow.Hidden,
			BoxShadow = new BoxShadow { OffsetY = 12, Blur = 40, Color = Color.Black.WithAlpha( 0.45 ) },
			Transform = new PanelTransform { TranslateX = -hidden * (input.Width + Inset * 2) },
			Opacity = 1 - hidden,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Sidebar",
			OnPointerEnter = _ => input.Enter(),
			OnPointerLeave = _ => input.Leave(),
			Children = [input.Content],
		};
	}
}
