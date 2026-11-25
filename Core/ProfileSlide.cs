using Goo;

namespace Lokn.Core;

/// <summary>Supplies a <see cref="ProfileSlide"/> with the profile shown and the content that belongs to it.</summary>
/// <param name="Profile">The profile the content belongs to; a change swipes the content.</param>
/// <param name="Content">The content, such as a profile's projects and chats.</param>
public sealed record ProfileSlideInput( SettingsProfile Profile, Blob Content );

/// <summary>Swipes content that differs between profiles in from the side of the profile chosen: Personal from the right, Work from the left, like the switcher's order.</summary>
/// <remarks>Wrap only what changes with the profile; actions both profiles share stay outside, so they keep still during a switch.</remarks>
public sealed class ProfileSlide : GuardedCell<ProfileSlideInput>
{
	/// <summary>Defines the swipe's duration in milliseconds.</summary>
	private const double SlideMs = 220;

	/// <summary>Defines how far the content starts from its place, in logical pixels.</summary>
	private const double SlideDistance = 48;

	/// <summary>Tracks the swipe from -1 or 1, a full step to the left or right with the content transparent, to 0 at rest.</summary>
	private Anim<double>? _offset;

	/// <summary>Stores the profile shown, to tell which way a switch swipes.</summary>
	private SettingsProfile? _profile;

	/// <summary>Starts a swipe when the profile changes; the first input shows the content at rest.</summary>
	protected override void InputChanged( ProfileSlideInput input )
	{
		_offset ??= Animate( 0.0 );
		if ( _profile is { } previous && previous != input.Profile )
			_offset.Play( input.Profile > previous ? 1 : -1, 0, Motion.Tween( SlideMs, Easing.EaseOut ) );
		_profile = input.Profile;
	}

	/// <summary>Builds the content at the swipe's current offset and opacity.</summary>
	protected override Blob BuildView( ProfileSlideInput input )
	{
		var offset = _offset?.Value ?? 0;
		return new Container
		{
			FlexGrow = 1,
			MinHeight = 0,
			Gap = 2,
			Transform = new PanelTransform { TranslateX = offset * SlideDistance },
			Opacity = 1 - Math.Abs( offset ),
			Children = [input.Content],
		};
	}
}
