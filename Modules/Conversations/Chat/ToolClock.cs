using Goo;
using Lokn.Core;

namespace Lokn.Modules.Chat;

/// <summary>Describes a tool's run time: when it started and, once it ended, when.</summary>
/// <param name="Started">When the tool started, in UTC.</param>
/// <param name="Ended">When the tool ended, in UTC, or null while it runs.</param>
internal sealed record ToolClockInput( DateTime Started, DateTime? Ended );

/// <summary>Shows how long a tool ran, in tenths of a second, counting up while it runs.</summary>
internal sealed class ToolClock : GuardedCell<ToolClockInput>
{
	/// <summary>Defines how often a running clock reads the time again, in milliseconds.</summary>
	private const double TickMs = 100;

	/// <summary>Counts ticks; reading it rebuilds the clock on each.</summary>
	private readonly State<int> _tick = new( 0 );

	/// <summary>Holds the timer while the tool runs, or null.</summary>
	private WindowTimer? _timer;

	/// <summary>Starts or stops the timer as the tool runs or ends.</summary>
	protected override void InputChanged( ToolClockInput input )
	{
		if ( input.Ended is null )
			_timer ??= Every( TickMs, () => _tick.Value++ );
		else
		{
			_timer?.Dispose();
			_timer = null;
		}
	}

	/// <summary>Builds the elapsed time in muted small text.</summary>
	protected override Blob BuildView( ToolClockInput input )
	{
		_ = _tick.Value;
		return new Text
		{
			Content = Format( (input.Ended ?? DateTime.UtcNow) - input.Started ),
			Color = Theme.Current.Muted,
			FontFamily = ViewStyle.CodeFontFamily,
			FontSize = ViewStyle.SmallSize,
		};
	}

	/// <summary>Formats a run time as tenths of a second under a minute, else minutes and seconds.</summary>
	internal static string Format( TimeSpan elapsed )
	{
		var seconds = Math.Max( 0, elapsed.TotalSeconds );
		return seconds < 60 ? $"{seconds:0.0}s" : $"{(int)seconds / 60}m {(int)seconds % 60:00}s";
	}
}
