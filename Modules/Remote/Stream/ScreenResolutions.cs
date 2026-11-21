using System.Text.Json;

namespace Lokn.Modules.Remote;

/// <summary>Switches a streamed screen to the resolution its viewer asked for (<c>cap.start resolution</c>) and back, through <see cref="IDisplayModes"/>.</summary>
/// <remarks>
/// The first viewer of a screen picks its mode; later viewers stream it as it is. The original mode returns when the last stream of the screen lets go
/// of its <see cref="ScreenLease"/> and when this keeper is disposed, as the daemon stops. Before a switch the original mode is written to
/// <see cref="RecordFile"/>, so a daemon that ended without restoring it puts it back when it starts again (<see cref="RestoreRecorded"/>).
/// Safe on any thread; calls into <see cref="IDisplayModes"/> run one at a time.
/// </remarks>
internal sealed class ScreenResolutions : IDisposable
{
	/// <summary>Defines how far, as a fraction, a mode's aspect ratio may be from the requested one and still count as the same.</summary>
	private const double AspectTolerance = 0.02;

	/// <summary>Defines the record file's JSON form.</summary>
	private static readonly JsonSerializerOptions Wire = new( JsonSerializerDefaults.Web );

	/// <summary>Stores the display modes seam.</summary>
	private readonly IDisplayModes _modes;

	/// <summary>Guards <see cref="_holds"/> and serializes the calls into <see cref="_modes"/>.</summary>
	private readonly Lock _lock = new();

	/// <summary>Stores the screens streams hold, by display device.</summary>
	private readonly Dictionary<string, Hold> _holds = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Creates a keeper over a display modes seam.</summary>
	/// <param name="modes">The display modes: <see cref="Win32DisplayModes"/>, or a fake in checks.</param>
	/// <param name="recordFile">The file the original modes are recorded in while they are switched.</param>
	public ScreenResolutions( IDisplayModes modes, string recordFile )
	{
		_modes = modes;
		RecordFile = recordFile;
	}

	/// <summary>Gets the file the original modes of switched screens are recorded in; it exists only while a screen is switched.</summary>
	public string RecordFile
	{
		get;
	}

	/// <summary>Puts back the modes a previous run recorded and did not restore, such as after a crash, then deletes the record.</summary>
	/// <returns>How many screens were put back.</returns>
	public int RestoreRecorded()
	{
		if ( !File.Exists( RecordFile ) )
			return 0;

		List<RecordedMode>? recorded;
		try
		{
			recorded = JsonSerializer.Deserialize<List<RecordedMode>>( File.ReadAllText( RecordFile ), Wire );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or JsonException )
		{
			recorded = null;
		}

		var restored = 0;
		lock ( _lock )
		{
			foreach ( var record in recorded ?? [] )
			{
				if ( record.Device is { Length: > 0 } && _modes.Restore( record.Device, new( record.Width, record.Height, record.Frequency ) ) )
					restored++;
			}

			DeleteRecord();
		}
		return restored;
	}

	/// <summary>Switches a screen to the supported mode closest to a size, unless another stream of it already chose one, and holds it until the lease is disposed.</summary>
	/// <param name="screen">The screen's negative <c>hwnd</c>.</param>
	/// <param name="width">The requested width in physical pixels.</param>
	/// <param name="height">The requested height in physical pixels.</param>
	/// <returns>The lease, with the screen's mode now and a message for the viewer when it is not the size asked for.</returns>
	public ScreenLease Acquire( long screen, int width, int height )
	{
		var requested = $"{width}×{height}";
		lock ( _lock )
		{
			if ( _modes.FindDevice( screen ) is not { } device )
				return new( this, null, null, "This screen cannot change its resolution; it streams at its own." );

			if ( _holds.TryGetValue( device, out var held ) )
			{
				held.Count++;
				return new( this, device, held.Mode, held.Mode.Width == width && held.Mode.Height == height ? null
					: $"Another viewer chose this screen's resolution; streaming at {held.Mode.FormatSize()}." );
			}

			if ( _modes.ReadCurrent( device ) is not { } current )
				return new( this, null, null, "This screen cannot change its resolution; it streams at its own." );

			var picked = Pick( _modes.ListModes( device ), width, height, current ) ?? current;
			var isExact = picked.Width == width && picked.Height == height;
			var noMode = isExact ? null : $"This screen has no {requested} mode; streaming at {picked.FormatSize()}.";
			if ( picked.Width == current.Width && picked.Height == current.Height )
			{
				_holds[device] = new( current, current, isChanged: false );
				return new( this, device, current, noMode );
			}

			// Recorded first: a daemon that dies with the screen switched puts it back when it starts.
			var hold = new Hold( current, picked, isChanged: true );
			_holds[device] = hold;
			WriteRecord();
			if ( !_modes.Apply( device, picked ) )
			{
				_ = _holds.Remove( device );
				WriteRecord();
				return new( this, null, current, $"This screen could not switch to {picked.FormatSize()}; streaming at {current.FormatSize()}." );
			}

			return new( this, device, picked, noMode );
		}
	}

	/// <summary>Puts every switched screen back and forgets every hold, as the daemon stops; later lease releases do nothing.</summary>
	public void Dispose()
	{
		lock ( _lock )
		{
			foreach ( var (device, hold) in _holds )
			{
				if ( hold.IsChanged )
					_ = _modes.Restore( device, hold.Original );
			}

			_holds.Clear();
			DeleteRecord();
		}
	}

	/// <summary>Picks the mode to show a viewer of a given size: the largest that fits it, else the smallest; one of the same aspect ratio first, and the
	/// current refresh rate when that size has it, else its highest.</summary>
	/// <param name="modes">The supported modes.</param>
	/// <param name="width">The requested width.</param>
	/// <param name="height">The requested height.</param>
	/// <param name="current">The screen's mode now, for its refresh rate.</param>
	/// <returns>The mode, or null when there is none or the size is empty.</returns>
	internal static DisplayMode? Pick( IReadOnlyList<DisplayMode> modes, int width, int height, DisplayMode current )
	{
		if ( modes.Count == 0 || width <= 0 || height <= 0 )
			return null;

		var aspect = (double)width / height;
		bool IsSameAspect( (int Width, int Height) size ) => Math.Abs( (double)size.Width / size.Height - aspect ) <= aspect * AspectTolerance;
		var sizes = modes.Where( mode => mode.Width > 0 && mode.Height > 0 ).Select( mode => (mode.Width, mode.Height) ).Distinct().ToList();
		if ( sizes.Count == 0 )
			return null;

		var fitting = sizes.Where( size => size.Width <= width && size.Height <= height ).ToList();
		var size = fitting.Count > 0
			? fitting.OrderByDescending( IsSameAspect ).ThenByDescending( size => (long)size.Width * size.Height ).First()
			: sizes.OrderByDescending( IsSameAspect ).ThenBy( size => (long)size.Width * size.Height ).First();
		var rates = modes.Where( mode => mode.Width == size.Width && mode.Height == size.Height ).ToList();
		return rates.Any( mode => mode.Frequency == current.Frequency ) ? rates.First( mode => mode.Frequency == current.Frequency ) : rates.MaxBy( mode => mode.Frequency );
	}

	/// <summary>Lets go of one stream's hold of a screen, and puts the screen back when it was the last.</summary>
	/// <param name="device">The display device.</param>
	internal void Release( string device )
	{
		lock ( _lock )
		{
			if ( !_holds.TryGetValue( device, out var hold ) || --hold.Count > 0 )
				return;

			_ = _holds.Remove( device );
			if ( !hold.IsChanged )
				return;

			_ = _modes.Restore( device, hold.Original );
			WriteRecord();
		}
	}

	/// <summary>Writes the original modes of the switched screens, or deletes the record when none is switched; under the lock.</summary>
	private void WriteRecord()
	{
		var recorded = _holds.Where( pair => pair.Value.IsChanged )
			.Select( pair => new RecordedMode( pair.Key, pair.Value.Original.Width, pair.Value.Original.Height, pair.Value.Original.Frequency ) )
			.ToList();
		if ( recorded.Count == 0 )
		{
			DeleteRecord();
			return;
		}

		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( RecordFile )! );
			File.WriteAllText( RecordFile, JsonSerializer.Serialize( recorded, Wire ) );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			// Without the record only a crash leaves the screen switched; stopping still restores it.
		}
	}

	/// <summary>Deletes the record file.</summary>
	private void DeleteRecord()
	{
		try
		{
			File.Delete( RecordFile );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			// The next start tries again and finds the screens already back.
		}
	}

	/// <summary>Holds one screen's streams: its mode before, the mode they use and how many there are.</summary>
	/// <param name="original">The mode before the first stream.</param>
	/// <param name="mode">The mode the streams use.</param>
	/// <param name="isChanged">Whether the mode was switched, so it must be put back.</param>
	private sealed class Hold( DisplayMode original, DisplayMode mode, bool isChanged )
	{
		/// <summary>Gets the mode before the first stream.</summary>
		public DisplayMode Original => original;

		/// <summary>Gets the mode the streams use.</summary>
		public DisplayMode Mode => mode;

		/// <summary>Gets whether the mode was switched.</summary>
		public bool IsChanged => isChanged;

		/// <summary>Gets or sets how many streams hold the screen.</summary>
		public int Count
		{
			get; set;
		} = 1;
	}

	/// <summary>Holds one line of the record file: a switched screen's original mode.</summary>
	/// <param name="Device">The display device.</param>
	/// <param name="Width">The original width.</param>
	/// <param name="Height">The original height.</param>
	/// <param name="Frequency">The original refresh rate, or 0.</param>
	private sealed record RecordedMode( string Device, int Width, int Height, int Frequency );
}

/// <summary>Holds one screen stream's hold of its screen's resolution (<see cref="ScreenResolutions.Acquire"/>); disposing lets go of it once.</summary>
internal sealed class ScreenLease : IDisposable
{
	/// <summary>Stores the keeper.</summary>
	private readonly ScreenResolutions _owner;

	/// <summary>Stores the held display device, or null when nothing is held.</summary>
	private string? _device;

	/// <summary>Creates a lease.</summary>
	/// <param name="owner">The keeper.</param>
	/// <param name="device">The held display device, or null when the stream holds nothing.</param>
	/// <param name="mode">The screen's mode now, or null when unknown.</param>
	/// <param name="message">What the viewer should read when the screen is not at the size asked for, or null.</param>
	internal ScreenLease( ScreenResolutions owner, string? device, DisplayMode? mode, string? message )
	{
		_owner = owner;
		_device = device;
		Mode = mode;
		Message = message;
	}

	/// <summary>Gets the screen's mode now, or null when unknown.</summary>
	public DisplayMode? Mode
	{
		get;
	}

	/// <summary>Gets what the viewer should read when the screen is not at the size asked for, or null when it is.</summary>
	public string? Message
	{
		get;
	}

	/// <summary>Lets go of the screen; the last stream to do so puts it back.</summary>
	public void Dispose()
	{
		if ( Interlocked.Exchange( ref _device, null ) is { } device )
			_owner.Release( device );
	}
}
