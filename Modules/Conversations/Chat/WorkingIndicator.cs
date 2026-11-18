using Goo;
using Lokn.Core;

namespace Lokn.Modules.Chat;

/// <summary>Describes what the agent is doing and since when.</summary>
/// <param name="Label">The current phase, such as "Thinking" or a running tool's intent.</param>
/// <param name="Started">When the turn started, in UTC.</param>
internal sealed record WorkingInput( string Label, DateTime Started );

/// <summary>Shows a seam being stitched, the agent's current phase and the turn's elapsed time while it works.</summary>
internal sealed class WorkingIndicator : GuardedCell<WorkingInput>
{
	/// <summary>Defines how often the elapsed time is read again, in milliseconds; the stitches animate on their own.</summary>
	private const double ClockMs = 250;

	/// <summary>Counts clock ticks; reading it rebuilds the indicator on every tick.</summary>
	private readonly State<int> _tick = new( 0 );

	/// <summary>Starts the clock, which stops when the indicator unmounts.</summary>
	protected override void Mounted() => Every( ClockMs, () => _tick.Value++ );

	/// <summary>Builds the stitches, the phase and the elapsed time.</summary>
	protected override Blob BuildView( WorkingInput input )
	{
		_ = _tick.Value;
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
				Cell.Mount<RollingTextInput, RollingText>( "working-label", new( $"{input.Label}…", ViewStyle.SmallSize, Theme.Current.Accent ) { FontFamily = ViewStyle.FontFamily } ),
				// The clock only counts up, so even 59s to 1m 00s rolls up.
				Cell.Mount<RollingTextInput, RollingText>( "working-elapsed", new( FormatElapsed( DateTime.UtcNow - input.Started ), ViewStyle.SmallSize, Theme.Current.Muted )
				{
					FontFamily = ViewStyle.FontFamily,
					Shrink = false,
					Rising = true,
				} ),
			],
		};
	}

	/// <summary>Formats elapsed time as seconds, or minutes and seconds past a minute.</summary>
	internal static string FormatElapsed( TimeSpan elapsed )
	{
		var seconds = Math.Max( 0, (int)elapsed.TotalSeconds );
		return seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m {seconds % 60:00}s";
	}
}
