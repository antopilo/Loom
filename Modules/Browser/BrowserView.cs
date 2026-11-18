using System.Runtime.InteropServices;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Browser;

/// <summary>Names the panel a page area belongs to.</summary>
/// <param name="Content">The panel that owns the page.</param>
/// <param name="PanelId">The panel's identifier, to tell whether the current layout shows it.</param>
internal sealed record BrowserViewInput( BrowserContent Content, Guid PanelId );

/// <summary>Reserves the page area and keeps the native page on it: moved with the panel, hidden while the panel is hidden or a shell overlay is open.</summary>
/// <remarks>Geometry and shell occlusion are checked on a short timer as well as on direct changes, because a layout switch can hide the panel without moving it.</remarks>
internal sealed partial class BrowserView : GuardedCell<BrowserViewInput>
{
	/// <summary>Defines how often the placement is checked, in milliseconds.</summary>
	private const double SyncMs = 50;

	/// <summary>Retains the page area.</summary>
	private readonly ElementHandle _area = new();

	/// <summary>Stores Lokn's native window once borrowed.</summary>
	private nint _handle;

	/// <summary>Stores the last placement sent to the panel, to send only changes.</summary>
	private System.Drawing.Rectangle? _placed;

	/// <summary>Tracks whether any placement was sent yet.</summary>
	private bool _hasPlaced;

	/// <summary>Builds the empty page area the native page covers.</summary>
	protected override Blob BuildView( BrowserViewInput input ) => new Container
	{
		Handle = _area,
		FlexGrow = 1,
		MinHeight = 0,
		MinWidth = 0,
	};

	/// <summary>Starts following the page area, and borrows Lokn's native window handle without blocking the UI thread.</summary>
	protected override void Mounted()
	{
		_area.MetricsChanged += UpdateMetrics;
		Input.Content.Context.App.NativeSurfaceOcclusionChanged += UpdateOcclusion;
		Every( SyncMs, UpdatePlacement );
		_ = AcquireHandleAsync();
	}

	/// <summary>Reads the window handle through a native dialog owner lease, the public way to get it, and releases it at once; placement starts once it is known.</summary>
	/// <remarks>Waiting on the lease with <c>.Result</c> blocked the UI thread, and a failed lease ended the process.</remarks>
	private async Task AcquireHandleAsync()
	{
		try
		{
			var lease = await Host.AcquireNativeDialogOwnerAsync( Lifetime );
			if ( lease.Owner is not { } owner )
				return;
			_handle = owner.Handle;
			owner.Dispose();
			if ( Lifetime.IsCancellationRequested )
				return;
			UpdatePlacement();
		}
		catch ( Exception error ) when ( error is OperationCanceledException or InvalidOperationException or ObjectDisposedException )
		{
			// The panel closed, or the window cannot lend its handle now; the page stays hidden.
		}
	}

	/// <summary>Hides the page when its area leaves the screen, such as while the panel moves to another view.</summary>
	protected override void Unmounting()
	{
		_area.MetricsChanged -= UpdateMetrics;
		Input.Content.Context.App.NativeSurfaceOcclusionChanged -= UpdateOcclusion;
		Send( null );
		_handle = 0;
	}

	/// <summary>Follows a layout change of the page area at once, between timer ticks.</summary>
	private void UpdateMetrics( ElementMetrics metrics ) => UpdatePlacement();

	/// <summary>Hides the native page before a shell overlay opens; the timer restores it after the overlay closes.</summary>
	private void UpdateOcclusion()
	{
		if ( Input.Content.Context.App.NativeSurfacesOccluded )
			Send( null );
	}

	/// <summary>Sends the area in window pixels, or null when the panel is hidden or has no room.</summary>
	private void UpdatePlacement()
	{
		if ( _handle == 0 || !Host.IsOpen )
			return;

		var input = Input;
		var box = _area.BorderBox;
		if ( !_area.IsMounted || box.Width < 1 || box.Height < 1 || !input.Content.IsShown( input.PanelId ) || input.Content.Context.App.NativeSurfacesOccluded )
		{
			Send( null );
			return;
		}

		// Goo lays out in scaled logical units; the page is placed in the window's physical pixels.
		var scale = Host.NativeWidth > 0 && GetClientRect( _handle, out var client ) ? Host.UiScale * (client.Right - client.Left) / Host.NativeWidth : Host.UiScale;
		var left = (int)Math.Round( box.X * scale );
		var top = (int)Math.Round( box.Y * scale );
		Send( new System.Drawing.Rectangle( left, top, (int)Math.Round( (box.X + box.Width) * scale ) - left, (int)Math.Round( (box.Y + box.Height) * scale ) - top ) );
	}

	/// <summary>Sends a placement when it changed.</summary>
	private void Send( System.Drawing.Rectangle? placement )
	{
		if ( _hasPlaced && placement == _placed )
			return;

		_hasPlaced = true;
		_placed = placement;
		Input.Content.Place( Host, _handle, placement );
	}

	/// <summary>Reads a window's client area in pixels.</summary>
	[LibraryImport( "user32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static partial bool GetClientRect( nint window, out ClientRect rect );

	/// <summary>Mirrors the Win32 RECT structure.</summary>
	[StructLayout( LayoutKind.Sequential )]
	private struct ClientRect
	{
		/// <summary>The left edge.</summary>
		public int Left;

		/// <summary>The top edge.</summary>
		public int Top;

		/// <summary>The right edge.</summary>
		public int Right;

		/// <summary>The bottom edge.</summary>
		public int Bottom;
	}
}
