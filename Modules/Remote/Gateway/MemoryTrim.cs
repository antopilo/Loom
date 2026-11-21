using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>Gives the daemon's unused .NET memory back to Windows once it is idle.</summary>
/// <remarks>
/// Loading the chats at start, or a burst such as a long transcript, makes the GC commit far more memory than the daemon keeps live; it measured
/// 244 MB committed around 4 MB of live data. The GC returns committed memory only during later collections, and an idle daemon allocates too little
/// to trigger any, so the memory stayed for good. Every <see cref="CheckInterval"/>, when the committed memory exceeds the live heap by
/// <see cref="WasteBytes"/> and no agent works, one aggressive collection decommits it, at most once per <see cref="MinimumGap"/>.
/// </remarks>
internal sealed class MemoryTrim : IDisposable
{
	/// <summary>Defines how often the daemon checks its memory.</summary>
	internal static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds( 30 );

	/// <summary>Defines the shortest time between two trims, so a busy stretch never pays for repeated full collections.</summary>
	internal static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes( 2 );

	/// <summary>Defines how much committed memory beyond the live heap is worth a trim.</summary>
	internal const long WasteBytes = 64L * 1024 * 1024;

	/// <summary>Stores the daemon's application, whose status counts the working agents.</summary>
	private readonly LoknApplication _app;

	/// <summary>Stores the timer that runs the checks on a thread-pool thread.</summary>
	private readonly Timer _timer;

	/// <summary>Stores when the last trim ran.</summary>
	private DateTime _lastTrim = DateTime.MinValue;

	/// <summary>Starts checking the daemon's memory, the first time one interval after start, once its chats are loaded.</summary>
	/// <param name="app">The daemon's application.</param>
	internal MemoryTrim( LoknApplication app )
	{
		_app = app;
		_timer = new Timer( _ => Check(), null, CheckInterval, CheckInterval );
	}

	/// <summary>Decides whether a trim is due: enough committed memory is unused, no agent works and the last trim is old enough.</summary>
	/// <param name="committed">The bytes the GC holds committed.</param>
	/// <param name="heap">The bytes the heap holds after the last collection.</param>
	/// <param name="working">The agents working now.</param>
	/// <param name="sinceLast">The time since the last trim.</param>
	internal static bool IsDue( long committed, long heap, int working, TimeSpan sinceLast ) =>
		committed - heap >= WasteBytes && working == 0 && sinceLast >= MinimumGap;

	/// <summary>Trims when <see cref="IsDue"/> says so; the aggressive collection decommits the free regions the GC keeps.</summary>
	private void Check()
	{
		var info = GC.GetGCMemoryInfo();
		// The status is an immutable record replaced as a whole, so reading it from this thread sees one consistent value.
		var working = _app.HostStatus?.Working ?? 0;
		if ( !IsDue( info.TotalCommittedBytes, info.HeapSizeBytes, working, DateTime.UtcNow - _lastTrim ) )
			return;

		_lastTrim = DateTime.UtcNow;
		GC.Collect( GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true );
	}

	/// <summary>Stops the checks.</summary>
	public void Dispose() => _timer.Dispose();
}
