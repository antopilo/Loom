using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Writes startup phases, startup steps and the frames the window presents to the file <c>LOKN_PERF_TRACE</c> names, for <c>scripts/bench.ps1</c>.</summary>
/// <remarks>
/// Off unless the variable is set, so a normal start pays one environment read. Each line is <c>&lt;ms since the process started&gt; &lt;event&gt;</c>:
/// a startup phase, <c>first-frame</c> once the window presented, <c>interactive</c> once the UI thread ran work queued after it, one
/// <c>frames &lt;n&gt;</c> line per second with the frames presented in it, and the window's focus and state changes. A step, measured with
/// <see cref="Step"/>, writes <c>step &lt;name&gt; &lt;ms&gt; jit &lt;methods&gt; &lt;ms&gt;</c> when it ends: how long it took on its thread, and
/// how many methods that thread compiled in it and for how long.
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

	/// <summary>Starts measuring a step of startup on the calling thread; disposing it writes the step's line.</summary>
	/// <param name="name">The step, words without spaces such as <c>module.load.conversations</c>.</param>
	/// <returns>The running measurement; free while tracing is off.</returns>
	internal static Measurement Step( string name ) => _writer is null ? default : new( name );

	/// <summary>Starts measuring a step of startup about one item, such as one module, on the calling thread.</summary>
	/// <param name="name">The step, a word such as <c>module</c>.</param>
	/// <param name="item">The item, a word such as a module ID; the step is written as <c>&lt;name&gt;.&lt;item&gt;</c>.</param>
	/// <returns>The running measurement; free while tracing is off.</returns>
	internal static Measurement Step( string name, string item ) => _writer is null ? default : new( name + "." + item );

	/// <summary>Records that startup reached a phase.</summary>
	/// <param name="phase">The phase, one word.</param>
	internal static void Mark( string phase ) => Write( phase );

	/// <summary>Writes one line stamped with the time since the process started.</summary>
	/// <param name="text">The event.</param>
	private static void Write( string text )
	{
		if ( _writer is null )
			return;

		var elapsed = (DateTime.UtcNow - _processStart).TotalMilliseconds;
		lock ( _gate )
		{
			_writer.WriteLine( string.Create( CultureInfo.InvariantCulture, $"{elapsed:F0} {text}" ) );
			_writer.Flush();
		}
	}

	/// <summary>Measures one startup step on one thread: its duration and the methods the thread compiled meanwhile.</summary>
	internal readonly struct Measurement : IDisposable
	{
		/// <summary>Stores the step's name, or null when tracing is off.</summary>
		private readonly string? _name;

		/// <summary>Stores when the step started.</summary>
		private readonly long _start;

		/// <summary>Stores how many methods the thread had compiled when the step started.</summary>
		private readonly long _methods;

		/// <summary>Stores how long the thread had spent compiling when the step started.</summary>
		private readonly TimeSpan _compiling;

		/// <summary>Starts measuring a step.</summary>
		/// <param name="name">The step.</param>
		internal Measurement( string name )
		{
			_name = name;
			_start = Stopwatch.GetTimestamp();
			_methods = JitInfo.GetCompiledMethodCount( currentThread: true );
			_compiling = JitInfo.GetCompilationTime( currentThread: true );
		}

		/// <summary>Writes the step's line.</summary>
		public void Dispose()
		{
			if ( _name is null )
				return;

			var elapsed = Stopwatch.GetElapsedTime( _start ).TotalMilliseconds;
			var methods = JitInfo.GetCompiledMethodCount( currentThread: true ) - _methods;
			var compiling = (JitInfo.GetCompilationTime( currentThread: true ) - _compiling).TotalMilliseconds;
			Write( string.Create( CultureInfo.InvariantCulture, $"step {_name} {elapsed:F0} jit {methods} {compiling:F0}" ) );
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
			if ( Volatile.Read( ref _switchStart ) != 0 )
			{
				lock ( _switchPresents )
					_switchPresents.Add( Stopwatch.GetTimestamp() );
			}
			if ( Interlocked.Increment( ref _presented ) != 1 )
				return;

			Mark( "first-frame" );
			WriteCompiled();
			// Work queued before the first frame runs first, so this marks when the UI thread is free for input.
			window.Post( () => Mark( "interactive" ) );
		};
		window.FocusChanged += isFocused => Mark( isFocused ? "focused" : "unfocused" );
		window.StateChanged += state => Mark( "state " + state );
		window.VisibilityChanged += isVisible => Mark( isVisible ? "visible" : "hidden" );
		_counter = new Timer( _ => Mark( "frames " + Interlocked.Exchange( ref _frames, 0 ) ), null, CountMs, CountMs );
	}

	/// <summary>Defines how long after a switch its frames are measured, in milliseconds.</summary>
	private const int SwitchWindowMs = 900;

	/// <summary>Defines the frame interval above which a frame counts as dropped, in milliseconds: one and a half 60 Hz frames.</summary>
	private const double DroppedMs = 25;

	/// <summary>Holds when the switch being measured started, or zero while none is.</summary>
	private static long _switchStart;

	/// <summary>Holds the times the frames after the measured switch were presented.</summary>
	private static readonly List<long> _switchPresents = [];

	/// <summary>Runs the switches <c>LOKN_PERF_DRIVE</c> lists, for <c>scripts/bench.ps1</c>'s switch scenario, and writes one line per switch.</summary>
	/// <remarks>
	/// The variable is <c>&lt;interval ms&gt;|&lt;step&gt;|&lt;step&gt;…</c>: each step is <c>profile</c>, which toggles Work and Personal, or a <c>lokn://</c>
	/// link, opened as a click in Loom would. The steps run in a loop, one per interval. Each writes
	/// <c>switch &lt;step&gt; handler &lt;ms&gt; first &lt;ms&gt; frames &lt;n&gt; maxgap &lt;ms&gt; dropped &lt;n&gt;</c>: the time the step itself took on
	/// the UI thread, from the step to the first presented frame, the frames presented in the next <see cref="SwitchWindowMs"/>, the longest time
	/// between two of them (from the step for the first), and how many of those times exceed <see cref="DroppedMs"/>.
	/// </remarks>
	internal static void Drive( LoknApplication app, Window window )
	{
		if ( _writer is null || Environment.GetEnvironmentVariable( "LOKN_PERF_DRIVE" ) is not { Length: > 0 } drive )
			return;

		var parts = drive.Split( '|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
		if ( parts.Length < 2 || !int.TryParse( parts[0], CultureInfo.InvariantCulture, out var interval ) )
			return;

		var step = 0;
		_driver = new Timer( _ => window.Post( () =>
		{
			var name = parts[1 + step++ % (parts.Length - 1)];
			lock ( _switchPresents )
				_switchPresents.Clear();
			var start = Stopwatch.GetTimestamp();
			Volatile.Write( ref _switchStart, start );
			if ( name == "profile" )
				app.Settings.ToggleProfile();
			else
				app.OpenLink( new Uri( name ) );
			var handler = Stopwatch.GetElapsedTime( start ).TotalMilliseconds;
			_ = Task.Delay( SwitchWindowMs ).ContinueWith( _ => WriteSwitch( name, start, handler ), TaskScheduler.Default );
		} ), null, interval, interval );
	}

	/// <summary>Writes the line of one measured switch and stops collecting its frames.</summary>
	private static void WriteSwitch( string name, long start, double handler )
	{
		long[] presents;
		lock ( _switchPresents )
		{
			presents = [.. _switchPresents];
			_switchPresents.Clear();
		}

		Interlocked.CompareExchange( ref _switchStart, 0, start );
		var previous = start;
		var longest = 0.0;
		var dropped = 0;
		var gaps = new List<string>( presents.Length );
		foreach ( var present in presents )
		{
			var gap = Stopwatch.GetElapsedTime( previous, present ).TotalMilliseconds;
			longest = Math.Max( longest, gap );
			if ( gap > DroppedMs )
				dropped++;
			gaps.Add( gap.ToString( "F0", CultureInfo.InvariantCulture ) );
			previous = present;
		}

		var first = presents.Length > 0 ? Stopwatch.GetElapsedTime( start, presents[0] ).TotalMilliseconds : -1;
		var label = name == "profile" ? "profile" : name[(name.LastIndexOf( '/' ) + 1)..];
		Write( string.Create( CultureInfo.InvariantCulture,
			$"switch {label} handler {handler:F1} first {first:F1} frames {presents.Length} maxgap {longest:F1} dropped {dropped} gaps {string.Join( ',', gaps )}" ) );
	}

	/// <summary>Runs the switches while driving; kept so it is not collected.</summary>
	private static Timer? _driver;

	/// <summary>Writes <c>jit &lt;methods&gt; &lt;ms&gt;</c>: how many methods the whole process compiled so far, on every thread, and for how long.</summary>
	private static void WriteCompiled()
	{
		var compiling = JitInfo.GetCompilationTime( currentThread: false ).TotalMilliseconds;
		Write( string.Create( CultureInfo.InvariantCulture, $"jit {JitInfo.GetCompiledMethodCount( currentThread: false )} {compiling:F0}" ) );
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
