using System.Diagnostics;
using System.Globalization;
using Goo;

namespace Lokn;

/// <summary>Writes startup phases and the frames the window presents to the file <c>LOKN_PERF_TRACE</c> names, for <c>scripts/bench.ps1</c>.</summary>
/// <remarks>
/// Off unless the variable is set, so a normal start pays one environment read. Each line is <c>&lt;ms since the process started&gt; &lt;event&gt;</c>:
/// a startup phase, <c>first-frame</c> once the window presented, <c>interactive</c> once the UI thread ran work queued after it, one
/// <c>frames &lt;n&gt;</c> line per second with the frames presented in it, and the window's focus and state changes.
/// </remarks>
internal static class PerfTrace
{
	/// <summary>Defines how often presented frames are counted into a line, in milliseconds.</summary>
	private const int CountMs = 1000;

	/// <summary>Holds the trace file, or null while tracing is off.</summary>
	private static readonly StreamWriter? _writer = Open();

	/// <summary>Guards the writer, which the UI thread and the counting timer share.</summary>
	private static readonly Lock _gate = new();

	/// <summary>Holds when the process started, which every timestamp counts from.</summary>
	private static readonly DateTime _processStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();

	/// <summary>Counts frames presented since the last count line.</summary>
	private static int _frames;

	/// <summary>Counts every frame presented, so the first one is marked once.</summary>
	private static long _presented;

	/// <summary>Writes the count lines while tracing; kept so it is not collected.</summary>
	private static Timer? _counter;

	/// <summary>Gets whether tracing is on.</summary>
	internal static bool IsEnabled => _writer is not null;

	/// <summary>Records that startup reached a phase.</summary>
	/// <param name="phase">The phase, one word.</param>
	internal static void Mark( string phase )
	{
		if ( _writer is null )
			return;

		var elapsed = (DateTime.UtcNow - _processStart).TotalMilliseconds;
		lock ( _gate )
		{
			_writer.WriteLine( string.Create( CultureInfo.InvariantCulture, $"{elapsed:F0} {phase}" ) );
			_writer.Flush();
		}
	}

	/// <summary>Counts the window's presented frames and records its focus and state changes; call before the window opens.</summary>
	/// <param name="window">The main window.</param>
	internal static void Attach( Window window )
	{
		if ( _writer is null )
			return;

		window.PresentationAccepted += _ =>
		{
			Interlocked.Increment( ref _frames );
			if ( Interlocked.Increment( ref _presented ) != 1 )
				return;

			Mark( "first-frame" );
			// Work queued before the first frame runs first, so this marks when the UI thread is free for input.
			window.Post( () => Mark( "interactive" ) );
		};
		window.FocusChanged += isFocused => Mark( isFocused ? "focused" : "unfocused" );
		window.StateChanged += state => Mark( "state " + state );
		window.VisibilityChanged += isVisible => Mark( isVisible ? "visible" : "hidden" );
		_counter = new Timer( _ => Mark( "frames " + Interlocked.Exchange( ref _frames, 0 ) ), null, CountMs, CountMs );
	}

	/// <summary>Opens the trace file the environment names, or returns null.</summary>
	private static StreamWriter? Open()
	{
		if ( Environment.GetEnvironmentVariable( "LOKN_PERF_TRACE" ) is not { Length: > 0 } path )
			return null;

		try
		{
			return new StreamWriter( new FileStream( path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite ) );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException )
		{
			return null;
		}
	}
}
