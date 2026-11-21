using Goo;

namespace Lokn.Core;

/// <summary>Defines the app's one entrance for floating surfaces: a short fade with a small scale and rise, used by menus, popovers, tooltips and
/// dialogs so they all open the same way.</summary>
/// <remarks>Only the entrance moves: a popup closes at once, since its owner removes it from the tree.</remarks>
public static class PopupMotion
{
	/// <summary>Defines the entrance's duration in milliseconds.</summary>
	public const double EnterMs = 150;

	/// <summary>Defines how far a menu, popover or tooltip starts from its place, in logical pixels.</summary>
	public const double PopoverRise = 4;

	/// <summary>Defines how far a dialog starts below its place, in logical pixels.</summary>
	public const double DialogRise = 10;

	/// <summary>Defines the scale a surface starts at.</summary>
	private const double StartScale = 0.96;

	/// <summary>Plays the entrance on a cell's animation from 0 to 1 and returns it.</summary>
	/// <param name="appear">An animation the cell created with <c>Animate( 0.0 )</c>.</param>
	public static Anim<double> Start( Anim<double> appear )
	{
		appear.Play( 0, 1, Motion.Tween( EnterMs, Easing.EaseOut ) );
		return appear;
	}

	/// <summary>Sets a surface's opacity, scale and offset for a point of the entrance.</summary>
	/// <param name="surface">The popup's outer container.</param>
	/// <param name="progress">The entrance from 0, hidden, to 1, at rest.</param>
	/// <param name="rise">How far the surface starts below its place; negative starts above it.</param>
	public static void Apply( Container surface, double progress, double rise )
	{
		if ( progress >= 1 )
			return;

		surface.Opacity = progress;
		surface.Transform = new PanelTransform { Scale = StartScale + (1 - StartScale) * progress, TranslateY = (1 - progress) * rise };
	}
}
