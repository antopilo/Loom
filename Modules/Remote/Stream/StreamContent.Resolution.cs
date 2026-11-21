using System.Diagnostics;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>Sizes a stream for this device: frames are capped to the panel's size in physical pixels, sent again once a resize settles (<c>cap.view</c>),
/// and a screen is asked to switch to the resolution of the screen this panel shows on (<c>cap.start resolution</c>), unless the user turns that off.</summary>
public sealed partial class StreamContent
{
	/// <summary>Defines the label of the switch that asks a streamed screen for this device's resolution.</summary>
	private const string DeviceResolutionName = "Use this device's resolution";

	/// <summary>Defines how long the panel's size must stay put before its new size goes to the host, so a drag does not restart the encoder at every step.</summary>
	private static readonly TimeSpan ViewSettle = TimeSpan.FromMilliseconds( 300 );

	/// <summary>Stores whether a screen stream asks the host for this device's resolution; on by default.</summary>
	private bool _isDeviceResolution = true;

	/// <summary>Stores the host's note about the screen's resolution, such as that it has no mode of the size asked for, or empty.</summary>
	private string _resolutionNote = "";

	/// <summary>Stores the frame surface's size in physical pixels, or null before its first layout.</summary>
	private (int Width, int Height)? _viewport;

	/// <summary>Stores the surface size the running stream was last capped to, or null when it has no cap.</summary>
	private (int Width, int Height)? _viewSent;

	/// <summary>Stores when the surface's size last changed, as a <see cref="Stopwatch"/> timestamp.</summary>
	private long _viewportAt;

	/// <summary>Tracks whether a cap waits for the surface's size to settle.</summary>
	private bool _isViewQueued;

	/// <summary>Combines the configured size cap with the surface's size: the smaller of the two, either alone, or 0 for none.</summary>
	/// <param name="configured">The cap the panel was created with, 0 for none.</param>
	/// <param name="viewport">The surface's size on that axis in physical pixels, or null before its first layout.</param>
	internal static int ComputeCap( int configured, int? viewport ) =>
		viewport is not { } size ? configured : configured > 0 ? Math.Min( configured, size ) : size;

	/// <summary>Builds a new stream's options: frames capped to the surface's size once it is known, and for a screen with the switch on, the size of the
	/// screen this panel shows on; on the UI thread.</summary>
	/// <param name="window">The window or screen to stream.</param>
	private CaptureOptions BuildStartOptions( long window )
	{
		_viewSent = _viewport;
		return _options with
		{
			MaxWidth = ComputeCap( _options.MaxWidth, _viewport?.Width ),
			MaxHeight = ComputeCap( _options.MaxHeight, _viewport?.Height ),
			Resolution = ScreenCatalog.IsScreen( window ) && _isDeviceResolution ? ReadDeviceScreen() : null,
		};
	}

	/// <summary>Reads the size in physical pixels of the screen this Loom's window is on, or null when it cannot be read.</summary>
	private (int Width, int Height)? ReadDeviceScreen()
	{
		if ( !OperatingSystem.IsWindows() || _context.App.Window is not { } window )
			return null;

		var handle = window.NativeHandle;
		return handle.Kind == NativeWindowKind.Win32 && handle.Handle != 0 ? ScreenCatalog.ReadScreenSize( handle.Handle ) : null;
	}

	/// <summary>Remembers the surface's size in physical pixels as layout gives it, and sends it to the host once it settles.</summary>
	/// <param name="size">The surface's size in layout units.</param>
	private void NoteViewport( LayoutSize size )
	{
		if ( !double.IsFinite( size.Width ) || !double.IsFinite( size.Height ) )
			return;

		var scale = _context.App.Window?.PixelScale ?? 1;
		var width = (int)Math.Round( size.Width * scale );
		var height = (int)Math.Round( size.Height * scale );
		if ( width <= 0 || height <= 0 || _viewport == (width, height) )
			return;

		_viewport = (width, height);
		_viewportAt = Stopwatch.GetTimestamp();
		if ( _isViewQueued )
			return;

		_isViewQueued = true;
		_ = QueueViewAsync();
	}

	/// <summary>Waits for the size to settle, then applies it on the UI thread.</summary>
	private async Task QueueViewAsync()
	{
		if ( await DelayAsync( ViewSettle ) )
			_context.Post( UpdateView );
	}

	/// <summary>Caps the running stream to the surface's size once it stopped changing.</summary>
	private void UpdateView()
	{
		if ( Stopwatch.GetElapsedTime( _viewportAt ) < ViewSettle )
		{
			_ = QueueViewAsync();
			return;
		}

		_isViewQueued = false;
		SendView();
	}

	/// <summary>Sends the surface's size to the running stream when it differs from the cap the stream has.</summary>
	private void SendView()
	{
		if ( _streaming is null || _viewport is not { } viewport || viewport == _viewSent )
			return;

		_viewSent = viewport;
		_source.View( ComputeCap( _options.MaxWidth, viewport.Width ), ComputeCap( _options.MaxHeight, viewport.Height ) );
	}

	/// <summary>Shows the host's note about the screen's resolution once the stream started, and sends a size that changed while it started.</summary>
	/// <param name="note">The note, or null.</param>
	/// <param name="generation">The stream it belongs to.</param>
	private void ApplyStarted( string? note, int generation )
	{
		if ( generation != _generation || _streaming is null )
			return;

		_resolutionNote = note ?? "";
		SendView();
		UpdateVersion();
	}

	/// <summary>Builds the switch that asks a streamed screen for this device's resolution, with its label; nothing for a window.</summary>
	private IEnumerable<Blob> BuildResolutionSwitch( CaptureWindow window, Theme theme )
	{
		if ( !ScreenCatalog.IsScreen( window.Hwnd ) )
			yield break;

		yield return theme.BuildSwitch( DeviceResolutionName, _isDeviceResolution, () => _context.Run( UpdateDeviceResolution ) );
		yield return new Text { Content = DeviceResolutionName, Color = theme.Muted, FontSize = ViewStyle.SmallSize, FlexShrink = 0, TextWrap = TextWrap.NoWrap, };
	}

	/// <summary>Turns asking for this device's resolution on or off and restarts the screen's stream with it; the host puts the screen back when it is off.</summary>
	private void UpdateDeviceResolution()
	{
		_isDeviceResolution = !_isDeviceResolution;
		if ( _streaming is { } window )
			UpdateStart( window, _isChosen );
	}
}
