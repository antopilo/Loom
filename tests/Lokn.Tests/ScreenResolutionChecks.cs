using System.Text.Json.Nodes;
using Lokn.Modules.Remote;

namespace Lokn.Tests;

/// <summary>Checks the stream's sizing for the viewer: a screen switched to the viewer's resolution and put back (<see cref="ScreenResolutions"/>), and
/// frames at full size or capped by the viewer's panel, over <c>cap.*</c>. Every display mode change goes to <see cref="FakeDisplayModes"/>; this PC's
/// screens are only read, never switched.</summary>
internal static class ScreenResolutionChecks
{
	/// <summary>Defines the screen the checks stream, the main one.</summary>
	private const long Screen = -1;

	/// <summary>Defines the fake screen's display device.</summary>
	private const string Device = @"\\.\DISPLAY1";

	/// <summary>Defines the fake screen's mode before any switch.</summary>
	private static readonly DisplayMode Original = new( 2560, 1440, 144 );

	/// <summary>Defines the fake screen's modes: 16:9 sizes, one at 16:10 and one at 4:3.</summary>
	private static readonly DisplayMode[] Modes =
	[
		new( 3840, 2160, 60 ), new( 2560, 1440, 60 ), new( 2560, 1440, 144 ), new( 1920, 1080, 60 ), new( 1920, 1080, 144 ),
		new( 1680, 1050, 60 ), new( 1280, 720, 60 ), new( 1024, 768, 60 ),
	];

	/// <summary>Defines the folder the checks' record files go in, deleted when they end.</summary>
	private static readonly string Root = Path.Combine( Path.GetTempPath(), "lokn-screen-modes-" + Guid.NewGuid().ToString( "N" ) );

	/// <summary>Runs the checks.</summary>
	internal static async Task RunAsync()
	{
		try
		{
			CheckPick();
			CheckLeases();
			CheckRecordedRestore();
			CheckRealModes();
			await CheckScreenStreamAsync();
			await CheckFrameSizeAsync();
		}
		finally
		{
			if ( Directory.Exists( Root ) )
				Directory.Delete( Root, recursive: true );
		}

		Console.WriteLine( "PASS screen resolution: closest mode of the same aspect without upscaling, first viewer wins, restore on stop, drop, daemon stop and from the record at start, "
			+ "the note when a size has no mode, windows untouched, and full-size frames capped by maxWidth, maxHeight and cap.view." );
	}

	/// <summary>Checks which mode a requested size picks.</summary>
	private static void CheckPick()
	{
		CoreChecks.Check( ScreenResolutions.Pick( Modes, 1920, 1080, Original ) == new DisplayMode( 1920, 1080, 144 ), "An exact size is picked at the current refresh rate." );
		CoreChecks.Check( ScreenResolutions.Pick( Modes, 2560, 1600, Original ) == new DisplayMode( 1680, 1050, 60 ),
			"A size with no mode picks the largest smaller one of the same aspect, at the highest refresh rate when the current one is missing." );
		CoreChecks.Check( ScreenResolutions.Pick( Modes, 3440, 1440, Original ) == new DisplayMode( 2560, 1440, 144 ), "An ultrawide size on a 16:9 screen picks the largest mode that fits." );
		CoreChecks.Check( ScreenResolutions.Pick( Modes, 5000, 3000, Original ) == new DisplayMode( 3840, 2160, 60 ), "A size larger than every mode picks the largest." );
		CoreChecks.Check( ScreenResolutions.Pick( Modes, 800, 600, Original ) == new DisplayMode( 1024, 768, 60 ), "A size smaller than every mode picks the smallest of its aspect, never a larger one first." );
		CoreChecks.Check( ScreenResolutions.Pick( [], 1920, 1080, Original ) is null && ScreenResolutions.Pick( Modes, 0, 1080, Original ) is null, "No modes or an empty size picks nothing." );
	}

	/// <summary>Checks that the first viewer's request wins, the screen comes back when the last lease goes, and what each viewer is told.</summary>
	private static void CheckLeases()
	{
		var modes = new FakeDisplayModes();
		var file = TempFile();
		using var resolutions = new ScreenResolutions( modes, file );

		var first = resolutions.Acquire( Screen, 1920, 1080 );
		CoreChecks.Check( modes.Current == new DisplayMode( 1920, 1080, 144 ) && first.Message is null && first.Mode == modes.Current, "The first viewer switches the screen to its size." );
		CoreChecks.Check( File.Exists( file ) && File.ReadAllText( file ).Contains( "2560", StringComparison.Ordinal ), "The original mode is recorded while the screen is switched." );
		var second = resolutions.Acquire( Screen, 1280, 720 );
		CoreChecks.Check( modes.Current == new DisplayMode( 1920, 1080, 144 ) && modes.Applied == 1 && second.Message == "Another viewer chose this screen's resolution; streaming at 1920×1080.",
			"A second viewer keeps the first one's mode and is told so." );
		first.Dispose();
		CoreChecks.Check( modes.Current == new DisplayMode( 1920, 1080, 144 ), "The screen stays switched while a viewer remains." );
		second.Dispose();
		second.Dispose();
		CoreChecks.Check( modes.Current == Original && modes.Restored == 1 && !File.Exists( file ), "The last viewer puts the screen back, once, and the record goes." );

		var missing = resolutions.Acquire( Screen, 2560, 1600 );
		CoreChecks.Check( modes.Current == new DisplayMode( 1680, 1050, 60 ) && missing.Message == "This screen has no 2560×1600 mode; streaming at 1680×1050.",
			"A size with no mode switches to the closest and says so." );
		missing.Dispose();

		var same = resolutions.Acquire( Screen, 2560, 1440 );
		CoreChecks.Check( modes.Applied == 2 && same.Message is null && !File.Exists( file ), "A size the screen already has changes and records nothing." );
		same.Dispose();
		CoreChecks.Check( modes.Restored == 2, "A screen that was not switched is not restored." );

		modes.IsRefusing = true;
		var refused = resolutions.Acquire( Screen, 1920, 1080 );
		CoreChecks.Check( modes.Current == Original && refused.Message == "This screen could not switch to 1920×1080; streaming at 2560×1440." && !File.Exists( file ),
			"A mode Windows refuses keeps the screen as it is and says so." );
		refused.Dispose();
		modes.IsRefusing = false;

		var gone = resolutions.Acquire( -9, 1920, 1080 );
		CoreChecks.Check( gone.Mode is null && gone.Message is not null && modes.Current == Original, "A screen with no display device is left alone." );

		_ = resolutions.Acquire( Screen, 1920, 1080 );
		resolutions.Dispose();
		CoreChecks.Check( modes.Current == Original && !File.Exists( file ), "Stopping puts every switched screen back." );
	}

	/// <summary>Checks that a daemon that ended with a screen switched puts it back from the record when it starts again.</summary>
	private static void CheckRecordedRestore()
	{
		var modes = new FakeDisplayModes();
		var file = TempFile();
		// The first daemon switches the screen and dies without letting go.
		_ = new ScreenResolutions( modes, file ).Acquire( Screen, 1920, 1080 );
		CoreChecks.Check( modes.Current != Original && File.Exists( file ), "A switched screen is recorded." );

		var restarted = new ScreenResolutions( modes, file );
		CoreChecks.Check( restarted.RestoreRecorded() == 1 && modes.Current == Original && !File.Exists( file ), "The next start puts the recorded mode back and deletes the record." );
		CoreChecks.Check( restarted.RestoreRecorded() == 0 && modes.Restored == 1, "Without a record, starting restores nothing." );

		File.WriteAllText( file, "not json" );
		CoreChecks.Check( restarted.RestoreRecorded() == 0 && !File.Exists( file ), "A broken record is dropped." );
	}

	/// <summary>Reads this PC's modes through user32 without changing them, checking the DEVMODE layout, and reports a skip without a screen.</summary>
	private static void CheckRealModes()
	{
		var real = new Win32DisplayModes();
		if ( !OperatingSystem.IsWindows() || real.FindDevice( Screen ) is not { } device )
		{
			Console.WriteLine( "SKIP screen resolution: no screen in this session; this PC's display modes were not read." );
			return;
		}

		var current = real.ReadCurrent( device );
		var modes = real.ListModes( device );
		CoreChecks.Check( current is { Width: > 0, Height: > 0 } && modes.Count > 0, "The main screen reports its mode and the modes it supports." );
		CoreChecks.Check( modes.Any( mode => mode.Width == current!.Value.Width && mode.Height == current.Value.Height ), "The main screen's current size is among its modes." );
	}

	/// <summary>Checks a screen stream over <c>cap.*</c>: it switches the screen and comes back on stop, on a dropped connection and when the service stops, it
	/// passes the note to the client, and a window stream never switches anything.</summary>
	private static async Task CheckScreenStreamAsync()
	{
		var modes = new FakeDisplayModes();
		var file = TempFile();
		using var service = BuildService( new ScreenResolutions( modes, file ), new SizedCapturer( 2560, 1440 ) );
		var options = new CaptureOptions( 30, 0.5, 0 ) { Resolution = (1920, 1080) };

		await using ( var pair = RemoteStreamChecks.Connect( service, canControl: true ) )
		{
			var note = await pair.Source.StartAsync( Screen, options, CancellationToken.None ).WaitAsync( RemoteStreamChecks.Patience );
			CoreChecks.Check( note is null && modes.Current == new DisplayMode( 1920, 1080, 144 ), "cap.start with a resolution switches the screen before streaming it." );
			pair.Source.Stop();
			await RemoteStreamChecks.WaitUntilAsync( () => modes.Current == Original );
			CoreChecks.Check( modes.Current == Original, "cap.stop puts the screen back." );

			note = await pair.Source.StartAsync( Screen, options with { Resolution = (2560, 1600) }, CancellationToken.None ).WaitAsync( RemoteStreamChecks.Patience );
			CoreChecks.Check( note == "This screen has no 2560×1600 mode; streaming at 1680×1050.", "The cap.start answer tells the client the screen has no mode of its size." );
			pair.Source.Stop();
			await RemoteStreamChecks.WaitUntilAsync( () => modes.Current == Original );

			note = await pair.Source.StartAsync( 4242, options, CancellationToken.None ).WaitAsync( RemoteStreamChecks.Patience );
			CoreChecks.Check( note is null && modes.Applied == 2, "A window stream ignores the resolution." );
			pair.Source.Stop();

			_ = await pair.Source.StartAsync( Screen, options, CancellationToken.None ).WaitAsync( RemoteStreamChecks.Patience );
			CoreChecks.Check( modes.Current != Original, "The screen is switched again." );
		}

		await RemoteStreamChecks.WaitUntilAsync( () => modes.Current == Original );
		CoreChecks.Check( modes.Current == Original && !File.Exists( file ), "A dropped connection puts the screen back." );

		var other = RemoteStreamChecks.Connect( service, canControl: true );
		_ = await other.Source.StartAsync( Screen, options, CancellationToken.None ).WaitAsync( RemoteStreamChecks.Patience );
		CoreChecks.Check( modes.Current != Original, "Another stream switches the screen." );
		service.Dispose();
		CoreChecks.Check( modes.Current == Original && !File.Exists( file ), "Stopping the service, as the daemon stops, puts the screen back." );
		await other.DisposeAsync();
	}

	/// <summary>Checks the frame size over <c>cap.*</c>: full size without a cap, an older client's maxWidth 1600, a panel's maxWidth and maxHeight, and <c>cap.view</c>.</summary>
	private static async Task CheckFrameSizeAsync()
	{
		var capturer = new SizedCapturer( 3440, 1440 );
		using var service = BuildService( null, capturer );
		await using var pair = RemoteStreamChecks.Connect( service, canControl: true );
		var frames = RemoteStreamChecks.Watch( pair.Source );

		_ = await pair.Source.StartAsync( 4242, new( 30, 0.5, 0 ), CancellationToken.None ).WaitAsync( RemoteStreamChecks.Patience );
		var frame = await frames.Reader.ReadAsync().AsTask().WaitAsync( RemoteStreamChecks.Patience );
		CoreChecks.Check( frame.Width == 3440 && frame.Height == 1440, "cap.start without maxWidth streams the source at its full size." );

		var legacy = Frame.Create( "cap.start" );
		legacy["hwnd"] = 4242;
		legacy["maxWidth"] = 1600;
		var answer = await pair.Client.RequestAsync( legacy, CancellationToken.None ).WaitAsync( RemoteStreamChecks.Patience );
		await RemoteStreamChecks.WaitUntilAsync( () => capturer.Widths.Contains( 1600 ) );
		CoreChecks.Check( capturer.Widths.Contains( 1600 ), "An older client's maxWidth 1600 still caps the frame." );
		_ = await pair.Client.RequestAsync( new JsonObject { ["t"] = "cap.stop", ["cap"] = answer["cap"]!.GetValue<long>() }, CancellationToken.None ).WaitAsync( RemoteStreamChecks.Patience );

		_ = await pair.Source.StartAsync( 4242, new CaptureOptions( 30, 0.5, 1720 ) { MaxHeight = 2000 }, CancellationToken.None ).WaitAsync( RemoteStreamChecks.Patience );
		await RemoteStreamChecks.WaitUntilAsync( () => capturer.Widths.Contains( 1720 ) );
		CoreChecks.Check( capturer.Widths.Contains( 1720 ), "A panel's maxWidth and maxHeight fit the frame inside the panel." );
		pair.Source.View( 3000, 600 );
		await AckUntilAsync( pair.Source, () => capturer.LastWidth == 1433 );
		CoreChecks.Check( capturer.LastWidth == 1433, "cap.view caps a running stream by the panel's tighter side." );
		pair.Source.View( 0, 0 );
		await AckUntilAsync( pair.Source, () => capturer.LastWidth == 3440 );
		CoreChecks.Check( capturer.LastWidth == 3440, "cap.view without a cap returns the stream to full size." );
	}

	/// <summary>Acknowledges frames until a condition holds or the patience runs out.</summary>
	private static async Task AckUntilAsync( IStreamSource source, Func<bool> condition )
	{
		var deadline = DateTime.UtcNow + RemoteStreamChecks.Patience;
		while ( !condition() && DateTime.UtcNow < deadline )
		{
			source.Ack();
			await Task.Delay( 10 );
		}
	}

	/// <summary>Builds a service over one capturer, a one-byte encoder and, when given, the screen resolutions.</summary>
	private static StreamService BuildService( ScreenResolutions? resolutions, IWindowCapturer capturer ) => new()
	{
		ListWindows = () => [],
		CreateCapturer = _ => capturer,
		Encode = ( frame, _, _ ) => Task.FromResult( new[] { frame.Pixels[0] } ),
		Inject = ( _, _, _, _ ) => { },
		Resolutions = resolutions,
	};

	/// <summary>Returns a record file path in a fresh folder under <see cref="Root"/>.</summary>
	private static string TempFile() => Path.Combine( Root, Guid.NewGuid().ToString( "N" ), "screen-modes.json" );

	/// <summary>Stands in for this PC's screens: one screen, <see cref="Device"/>, with <see cref="Modes"/>, counting switches and restores.</summary>
	private sealed class FakeDisplayModes : IDisplayModes
	{
		/// <summary>Guards the state.</summary>
		private readonly Lock _lock = new();

		/// <summary>Stores the current mode.</summary>
		private DisplayMode _current = Original;

		/// <summary>Gets the current mode.</summary>
		public DisplayMode Current
		{
			get
			{
				lock ( _lock )
					return _current;
			}
		}

		/// <summary>Gets how many switches were applied.</summary>
		public int Applied
		{
			get; private set;
		}

		/// <summary>Gets how many restores ran.</summary>
		public int Restored
		{
			get; private set;
		}

		/// <summary>Gets or sets whether switches are refused, as Windows may.</summary>
		public bool IsRefusing
		{
			get; set;
		}

		/// <summary>Finds the one screen.</summary>
		public string? FindDevice( long screen ) => screen == Screen ? Device : null;

		/// <summary>Reads the current mode.</summary>
		public DisplayMode? ReadCurrent( string device ) => device == Device ? Current : null;

		/// <summary>Lists the modes.</summary>
		public IReadOnlyList<DisplayMode> ListModes( string device ) => device == Device ? Modes : [];

		/// <summary>Switches to a listed mode unless refusing.</summary>
		public bool Apply( string device, DisplayMode mode )
		{
			lock ( _lock )
			{
				if ( IsRefusing || device != Device || !Modes.Contains( mode ) )
					return false;

				_current = mode;
				Applied++;
				return true;
			}
		}

		/// <summary>Returns to a mode.</summary>
		public bool Restore( string device, DisplayMode original )
		{
			lock ( _lock )
			{
				_current = original;
				Restored++;
				return true;
			}
		}
	}

	/// <summary>Captures a source of a fixed size, scaled to the width each capture asks for, and records those widths.</summary>
	/// <param name="width">The source width.</param>
	/// <param name="height">The source height.</param>
	private sealed class SizedCapturer( int width, int height ) : IWindowCapturer
	{
		/// <summary>Stores the value the next frame is filled with.</summary>
		private int _value;

		/// <summary>Stores the output width of the last capture.</summary>
		private int _lastWidth;

		/// <summary>Stores each output width captured.</summary>
		private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _widths = new();

		/// <summary>Gets the output widths captured.</summary>
		public ICollection<int> Widths => _widths.Keys;

		/// <summary>Gets the output width of the last capture.</summary>
		public int LastWidth => Volatile.Read( ref _lastWidth );

		/// <summary>Gets the source size.</summary>
		public (int Width, int Height) SourceSize => (width, height);

		/// <summary>Returns a frame of the scaled size whose first pixel byte is new; its pixels are not all there, as the checks' encoder reads only that byte.</summary>
		public BgraFrame? Capture( int maxWidth )
		{
			var (frameWidth, frameHeight) = WindowCapture.ComputeScaledSize( width, height, maxWidth );
			_widths[frameWidth] = true;
			Volatile.Write( ref _lastWidth, frameWidth );
			return new( frameWidth, frameHeight, [(byte)Interlocked.Increment( ref _value ), 0, 0, 0] );
		}

		/// <summary>Does nothing: the checks reuse the capturer across streams.</summary>
		public void Dispose()
		{
		}
	}
}
