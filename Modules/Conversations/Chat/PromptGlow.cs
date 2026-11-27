using System.Numerics;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Chat;

/// <summary>Describes the prompt's edge glow: whether the agent works, and the corner radius of the prompt's frame.</summary>
/// <param name="IsWorking">Whether the agent works, which is the only time the light runs.</param>
/// <param name="Radius">The frame's corner radius in layout units.</param>
internal sealed record PromptGlowInput( bool IsWorking, double Radius );

/// <summary>Runs a light around the prompt's edge while the agent works, so the chat shows at a glance that a turn is under way.</summary>
/// <remarks>
/// One shader effect over a box that reaches <see cref="Reach"/> past the frame on every side, so the light spills a little outside the edge.
/// It plays only while the agent works and the chat is seen, with motion allowed; at rest nothing is mounted, so it costs no layer and no frame.
/// </remarks>
internal sealed class PromptGlow : GuardedCell<PromptGlowInput>
{
	/// <summary>Defines how far the light reaches past the frame's edge, in layout units.</summary>
	private const double Reach = 10;

	/// <summary>Defines the seconds the light takes to go once around.</summary>
	private const float LapSeconds = 3.2f;

	/// <summary>Defines the tail's length as a share of a lap.</summary>
	private const float Tail = 0.22f;

	/// <summary>Defines the faint glow the whole edge keeps behind the light, from 0 to 1.</summary>
	private const float Steady = 0.12f;

	/// <summary>Defines the light's overall strength, from 0 to 1.</summary>
	private const float Strength = 0.85f;

	/// <summary>Holds the effect; its size and color are updated in place, without rebuilding.</summary>
	private readonly ShaderEffect _effect = new( ChatEffects.Glow, false, 0, 0 );

	/// <summary>Tracks the glow box's size.</summary>
	private readonly ElementHandle _handle = new();

	/// <summary>Plays the light only while the chat is seen.</summary>
	private readonly MotionSwitch _motion;

	/// <summary>Stores the size last given to the effect.</summary>
	private Vector2 _size;

	/// <summary>Creates the glow and follows its box's size.</summary>
	public PromptGlow()
	{
		_motion = new( UpdatePlaying );
		_handle.MetricsChanged += UpdateSize;
		_effect.SetParameter( 2, new Vector4( LapSeconds, Tail, Steady, Strength ) );
	}

	/// <summary>Builds the box the light draws in while the agent works, or nothing.</summary>
	protected override Blob BuildView( PromptGlowInput input )
	{
		var isSeen = input.IsWorking && LiveMotion.IsSeen( Host ) && Motion.TimeScale > 0 && Host.Preferences.ReducedMotion != true;
		_motion.Update( Host, isSeen );
		if ( !input.IsWorking )
			return new Container { Key = "prompt-glow-off", Position = PositionType.Absolute, Width = 0, Height = 0, HitTestSelf = false };

		var accent = Theme.Current.Accent;
		_effect.SetParameter( 1, new Vector4( accent.R, accent.G, accent.B, accent.A ) );
		_effect.SetParameter( 0, new Vector4( _size.X, _size.Y, (float)(input.Radius + Reach), (float)Reach ) );
		return new Container
		{
			Key = "prompt-glow",
			Handle = _handle,
			Position = PositionType.Absolute,
			Left = -Reach,
			Top = -Reach,
			Right = -Reach,
			Bottom = -Reach,
			HitTestSelf = false,
			ShaderEffect = _effect,
			Accessibility = new Accessibility { Hidden = true },
		};
	}

	/// <summary>Starts or stops the light.</summary>
	private void UpdatePlaying( bool isSeen ) => _effect.Playing = isSeen;

	/// <summary>Gives the effect the box's size in layout units, which it draws the rounded edge from.</summary>
	private void UpdateSize( ElementMetrics metrics )
	{
		if ( !metrics.IsMounted )
			return;
		var size = new Vector2( (float)metrics.BorderBox.Width, (float)metrics.BorderBox.Height );
		if ( size == _size )
			return;
		_size = size;
		_effect.SetParameter( 0, new Vector4( size.X, size.Y, (float)(Input.Radius + Reach), (float)Reach ) );
	}
}
