using Goo;
using Lokn.Core;

namespace Lokn.Modules.Chat;

/// <summary>Describes what the agent is doing and since when.</summary>
/// <param name="Label">The current phase, such as "Thinking" or a running tool's intent.</param>
/// <param name="Started">When the turn started, in UTC.</param>
internal sealed record WorkingInput( string Label, DateTime Started );

/// <summary>Shows a seam being stitched, the agent's current phase and the turn's elapsed time while it works.</summary>
/// <remarks>The clock runs while the indicator is seen (<see cref="LiveMotion.IsSeen"/>) and rebuilds it only when the elapsed time it shows changes.</remarks>
internal sealed class WorkingIndicator : GuardedCell<WorkingInput>
{
	/// <summary>Defines how often the elapsed time is read again, in milliseconds; the stitches animate on their own.</summary>
	private const double ClockMs = 250;

	/// <summary>Counts changes of the elapsed time shown; reading it rebuilds the indicator on each.</summary>
	private readonly State<int> _tick = new( 0 );

	/// <summary>Starts and stops the clock as the indicator comes into and out of view.</summary>
	private readonly MotionSwitch _motion;

	/// <summary>Holds the clock while the indicator is seen, or null; Goo stops it when the indicator unmounts.</summary>
	private WindowTimer? _clock;

	/// <summary>Stores the elapsed time the last build showed.</summary>
	private string _elapsed = "";

	/// <summary>Sweeps a soft light across the label while the agent works and the indicator is seen; one effect, so one layer.</summary>
	private readonly ShaderEffect _shimmer = new( ChatEffects.Shimmer, false, 0, 0 );

	/// <summary>Creates the indicator; its first build starts the clock once it is seen.</summary>
	public WorkingIndicator()
	{
		_motion = new( UpdateClock );
		// Half-width of the band, as a share of the label's width; seconds per sweep and rest; how far the band lights toward white.
		_shimmer.SetParameter( 0, new System.Numerics.Vector4( 0.22f, 2.4f, 0.55f, 0 ) );
	}

	/// <summary>Builds the stitches, the phase and the elapsed time.</summary>
	protected override Blob BuildView( WorkingInput input )
	{
		_ = _tick.Value;
		_motion.Update( Host, LiveMotion.IsSeen( Host ) );
		_elapsed = FormatElapsed( DateTime.UtcNow - input.Started );
		return new Row
		{
			Key = "working",
			AlignItems = AlignItems.Center,
			Gap = Theme.Current.Spacing / 2,
			Padding = new Edges( 0, Theme.Current.Spacing ),
			AccessibilityRole = AccessibilityRole.Status,
			AccessibilityName = input.Label,
			Children =
			[
				Cell.Mount( "working-stitches", () => new StitchMark() ),
				new Container
				{
					Key = "working-label-light",
					ShaderEffect = _shimmer,
					Children = [Cell.Mount<RollingTextInput, RollingText>( "working-label", new( $"{input.Label}…", ViewStyle.SmallSize, Theme.Current.Accent ) { FontFamily = ViewStyle.FontFamily } )],
				},
				// The clock only counts up, so even 59s to 1m 00s rolls up.
				Cell.Mount<RollingTextInput, RollingText>( "working-elapsed", new( _elapsed, ViewStyle.SmallSize, Theme.Current.Muted )
				{
					FontFamily = ViewStyle.FontFamily,
					Shrink = false,
					Rising = true,
				} ),
			],
		};
	}

	/// <summary>Runs the clock and the shimmer while the indicator is seen and stops them otherwise; the shimmer stays still with reduced motion.</summary>
	private void UpdateClock( bool isSeen )
	{
		_clock?.Dispose();
		_clock = isSeen ? Every( ClockMs, UpdateElapsed ) : null;
		_shimmer.Playing = isSeen && Motion.TimeScale > 0 && Host.Preferences.ReducedMotion != true;
	}

	/// <summary>Rebuilds the indicator once the elapsed time it shows is out of date.</summary>
	private void UpdateElapsed()
	{
		if ( FormatElapsed( DateTime.UtcNow - Input.Started ) != _elapsed )
			_tick.Value++;
	}

	/// <summary>Formats elapsed time as seconds, then minutes and seconds past a minute, then hours, minutes and seconds past an hour.</summary>
	internal static string FormatElapsed( TimeSpan elapsed )
	{
		var seconds = Math.Max( 0, (int)elapsed.TotalSeconds );
		return seconds < 60 ? $"{seconds}s"
			: seconds < 3600 ? $"{seconds / 60}m {seconds % 60:00}s"
			: $"{seconds / 3600}h {seconds / 60 % 60:00}m {seconds % 60:00}s";
	}
}
