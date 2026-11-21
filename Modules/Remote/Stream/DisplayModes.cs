using System.Runtime.InteropServices;

namespace Lokn.Modules.Remote;

/// <summary>Holds one display mode of a screen.</summary>
/// <param name="Width">The width in physical pixels.</param>
/// <param name="Height">The height in physical pixels.</param>
/// <param name="Frequency">The refresh rate in hertz, or 0 when the driver does not say.</param>
public readonly record struct DisplayMode( int Width, int Height, int Frequency )
{
	/// <summary>Returns the size as the user reads it, such as <c>2560×1440</c>.</summary>
	public string FormatSize() => $"{Width}×{Height}";
}

/// <summary>Reads and sets the display modes of this PC's screens, the one seam every resolution change of a screen stream goes through, so checks
/// use a fake and never change the real screens.</summary>
/// <remarks>Calls may come from any thread, one at a time (<see cref="ScreenResolutions"/> serializes them).</remarks>
internal interface IDisplayModes
{
	/// <summary>Finds the display device of a screen stream source, such as <c>\\.\DISPLAY1</c>, or null when no such screen is connected.</summary>
	/// <param name="screen">The screen's negative <c>hwnd</c> (<see cref="ScreenCatalog"/>).</param>
	string? FindDevice( long screen );

	/// <summary>Reads a device's current mode, or null when it cannot be read.</summary>
	/// <param name="device">The display device.</param>
	DisplayMode? ReadCurrent( string device );

	/// <summary>Lists the modes a device supports in its current orientation and color depth.</summary>
	/// <param name="device">The display device.</param>
	IReadOnlyList<DisplayMode> ListModes( string device );

	/// <summary>Switches a device to a mode for now, without saving it as the screen's setting; false when Windows refused it.</summary>
	/// <param name="device">The display device.</param>
	/// <param name="mode">One of <see cref="ListModes"/>.</param>
	bool Apply( string device, DisplayMode mode );

	/// <summary>Puts a device back to the mode it had before <see cref="Apply"/>; false when Windows refused it.</summary>
	/// <param name="device">The display device.</param>
	/// <param name="original">The mode to return to.</param>
	bool Restore( string device, DisplayMode original );
}

/// <summary>Reads and sets display modes with user32: <c>EnumDisplaySettingsEx</c> and <c>ChangeDisplaySettingsEx</c>.</summary>
/// <remarks>A mode is applied with <c>CDS_FULLSCREEN</c>, a temporary change Windows never writes to the registry; restoring first returns the device to
/// its registry mode and applies the original mode, still without the registry, only when that differs.</remarks>
internal sealed class Win32DisplayModes : IDisplayModes
{
	/// <summary>Defines ENUM_CURRENT_SETTINGS.</summary>
	private const int CurrentSettings = -1;

	/// <summary>Defines DM_PELSWIDTH.</summary>
	private const uint WidthField = 0x80000;

	/// <summary>Defines DM_PELSHEIGHT.</summary>
	private const uint HeightField = 0x100000;

	/// <summary>Defines DM_DISPLAYFREQUENCY.</summary>
	private const uint FrequencyField = 0x400000;

	/// <summary>Defines CDS_FULLSCREEN: the change is temporary and not saved in the registry.</summary>
	private const uint Temporary = 0x4;

	/// <summary>Defines DISP_CHANGE_SUCCESSFUL.</summary>
	private const int Changed = 0;

	/// <inheritdoc/>
	public string? FindDevice( long screen ) => OperatingSystem.IsWindows() ? ScreenCatalog.FindDevice( screen ) : null;

	/// <inheritdoc/>
	public DisplayMode? ReadCurrent( string device )
	{
		var mode = Create();
		return OperatingSystem.IsWindows() && EnumDisplaySettingsEx( device, CurrentSettings, ref mode, 0 ) ? ToMode( mode ) : null;
	}

	/// <inheritdoc/>
	public IReadOnlyList<DisplayMode> ListModes( string device )
	{
		var current = Create();
		if ( !OperatingSystem.IsWindows() || !EnumDisplaySettingsEx( device, CurrentSettings, ref current, 0 ) )
			return [];

		var modes = new HashSet<DisplayMode>();
		var mode = Create();
		for ( var index = 0; EnumDisplaySettingsEx( device, index, ref mode, 0 ); index++ )
		{
			if ( mode.BitsPerPel == current.BitsPerPel )
				modes.Add( ToMode( mode ) );
			mode = Create();
		}
		return [.. modes];
	}

	/// <inheritdoc/>
	public bool Apply( string device, DisplayMode mode )
	{
		var settings = Create( mode );
		return OperatingSystem.IsWindows() && ChangeDisplaySettingsEx( device, ref settings, 0, Temporary, 0 ) == Changed;
	}

	/// <inheritdoc/>
	public bool Restore( string device, DisplayMode original )
	{
		if ( !OperatingSystem.IsWindows() )
			return false;

		// Back to the registry mode, which ends the temporary change; it is the original mode unless the user had a temporary one too.
		_ = ResetDisplaySettings( device, 0, 0, 0, 0 );
		if ( ReadCurrent( device ) is { } now && now.Width == original.Width && now.Height == original.Height
			&& (original.Frequency == 0 || now.Frequency == original.Frequency) )
			return true;

		var settings = Create( original );
		return ChangeDisplaySettingsEx( device, ref settings, 0, 0, 0 ) == Changed;
	}

	/// <summary>Creates an empty DEVMODEW with its size set.</summary>
	private static DevMode Create() => new() { DeviceName = "", FormName = "", Size = (ushort)Marshal.SizeOf<DevMode>() };

	/// <summary>Creates a DEVMODEW that sets a mode's size and, when known, its refresh rate.</summary>
	private static DevMode Create( DisplayMode mode )
	{
		var settings = Create();
		settings.PelsWidth = (uint)mode.Width;
		settings.PelsHeight = (uint)mode.Height;
		settings.Fields = WidthField | HeightField;
		if ( mode.Frequency > 1 )
		{
			settings.DisplayFrequency = (uint)mode.Frequency;
			settings.Fields |= FrequencyField;
		}
		return settings;
	}

	/// <summary>Reads a DEVMODEW's mode; refresh rates of 0 and 1 mean the hardware default.</summary>
	private static DisplayMode ToMode( DevMode mode ) => new( (int)mode.PelsWidth, (int)mode.PelsHeight, mode.DisplayFrequency > 1 ? (int)mode.DisplayFrequency : 0 );

	/// <summary>Holds DEVMODEW, display fields only.</summary>
	[StructLayout( LayoutKind.Sequential, CharSet = CharSet.Unicode )]
	private struct DevMode
	{
		/// <summary>The device name.</summary>
		[MarshalAs( UnmanagedType.ByValTStr, SizeConst = 32 )]
		public string DeviceName;

		/// <summary>The structure version.</summary>
		public ushort SpecVersion;

		/// <summary>The driver version.</summary>
		public ushort DriverVersion;

		/// <summary>The structure's size in bytes.</summary>
		public ushort Size;

		/// <summary>The driver's extra bytes after the structure.</summary>
		public ushort DriverExtra;

		/// <summary>Which fields are set.</summary>
		public uint Fields;

		/// <summary>The screen's left edge on the desktop.</summary>
		public int PositionX;

		/// <summary>The screen's top edge on the desktop.</summary>
		public int PositionY;

		/// <summary>The screen's orientation.</summary>
		public uint DisplayOrientation;

		/// <summary>How a lower resolution is shown on a fixed-resolution panel.</summary>
		public uint DisplayFixedOutput;

		/// <summary>A printer field.</summary>
		public short Color;

		/// <summary>A printer field.</summary>
		public short Duplex;

		/// <summary>A printer field.</summary>
		public short YResolution;

		/// <summary>A printer field.</summary>
		public short TtOption;

		/// <summary>A printer field.</summary>
		public short Collate;

		/// <summary>A printer field.</summary>
		[MarshalAs( UnmanagedType.ByValTStr, SizeConst = 32 )]
		public string FormName;

		/// <summary>The logical pixels per inch.</summary>
		public ushort LogPixels;

		/// <summary>The color depth.</summary>
		public uint BitsPerPel;

		/// <summary>The width in pixels.</summary>
		public uint PelsWidth;

		/// <summary>The height in pixels.</summary>
		public uint PelsHeight;

		/// <summary>The display flags.</summary>
		public uint DisplayFlags;

		/// <summary>The refresh rate in hertz.</summary>
		public uint DisplayFrequency;

		/// <summary>An ICM field.</summary>
		public uint IcmMethod;

		/// <summary>An ICM field.</summary>
		public uint IcmIntent;

		/// <summary>A printer field.</summary>
		public uint MediaType;

		/// <summary>A printer field.</summary>
		public uint DitherType;

		/// <summary>Reserved.</summary>
		public uint Reserved1;

		/// <summary>Reserved.</summary>
		public uint Reserved2;

		/// <summary>Unused.</summary>
		public uint PanningWidth;

		/// <summary>Unused.</summary>
		public uint PanningHeight;
	}

	/// <summary>Reads the current mode (<see cref="CurrentSettings"/>) or the supported mode at an index of a display device.</summary>
	[DllImport( "user32.dll", EntryPoint = "EnumDisplaySettingsExW", CharSet = CharSet.Unicode )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static extern bool EnumDisplaySettingsEx( string device, int mode, ref DevMode settings, uint flags );

	/// <summary>Changes a display device's mode.</summary>
	[DllImport( "user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode )]
	private static extern int ChangeDisplaySettingsEx( string device, ref DevMode settings, nint window, uint flags, nint parameter );

	/// <summary>Returns a display device to its registry mode: <c>ChangeDisplaySettingsEx</c> with no mode.</summary>
	[DllImport( "user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode )]
	private static extern int ResetDisplaySettings( string device, nint settings, nint window, uint flags, nint parameter );
}
