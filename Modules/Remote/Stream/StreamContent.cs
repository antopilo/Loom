using System.Diagnostics;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>Shows one host window live: first a list of the host's windows, then the chosen window's frames scaled to fit, with its frame rate and latency, forwarding pointer and keys in frame pixels.</summary>
/// <remarks>Knows the host only through an <see cref="IStreamSource"/>. Each frame is decoded off the UI thread and acknowledged once it is on screen, so the host never sends faster than the panel shows.</remarks>
public sealed partial class StreamContent : PanelContent
{
	/// <summary>Defines the panel content inset in logical pixels.</summary>
	private const int ContentPadding = 16;

	/// <summary>Defines the panel heading size in logical pixels.</summary>
	private const int HeadingSize = 18;

	/// <summary>Defines how much each new latency sample moves the shown latency, smoothing jitter.</summary>
	private const double LatencySmoothing = 0.2;

	/// <summary>Stores the panel's guarded dispatch and lifetime.</summary>
	private readonly PanelContext _context;

	/// <summary>Stores the host connection.</summary>
	private readonly IStreamSource _source;

	/// <summary>Stores the options each stream starts with.</summary>
	private readonly CaptureOptions _options;

	/// <summary>Retains the frame surface for focus.</summary>
	private readonly ElementHandle _surface = new();

	/// <summary>Stores when recent frames arrived, as <see cref="Stopwatch"/> timestamps, for the frame rate.</summary>
	private readonly Queue<long> _arrivals = new();

	/// <summary>Counts changes that need a rebuild: frames, lists and status.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Stores the windows last listed, or null before the first list arrives.</summary>
	private IReadOnlyList<CaptureWindow>? _windows;

	/// <summary>Stores the streamed window, or null while the picker shows.</summary>
	private CaptureWindow? _streaming;

	/// <summary>Stores the frame on screen, or null before the first.</summary>
	private ImageSource? _frame;

	/// <summary>Publishes frames through one provider identity, so Goo keeps the displayed frame until its replacement upload completes.</summary>
	private readonly FrameImageSource _frameSource = new();

	/// <summary>Stores the status or error line, or empty.</summary>
	private string _status = "";

	/// <summary>Tracks whether a window list is being read.</summary>
	private bool _isListing;

	/// <summary>Stores when the last acknowledgement or start request went out, as a <see cref="Stopwatch"/> timestamp, or 0 when none is outstanding.</summary>
	private long _requestedAt;

	/// <summary>Stores the smoothed time from an acknowledgement to the next frame in milliseconds, or 0 before the first.</summary>
	private double _latency;

	/// <summary>Counts streams started, so frames of a stopped stream that arrive late are dropped.</summary>
	private int _generation;

	/// <summary>Stores the H.264 decoder of the running stream and the stream it decodes, created by its first H.264 frame; guarded by itself.</summary>
	private readonly System.Runtime.CompilerServices.StrongBox<(int Generation, H264Decoder? Decoder)> _decoder = new( (0, null) );

	/// <summary>Stores whether this PC decodes H.264, read once per process.</summary>
	private static readonly Lazy<bool> CanDecodeH264 = new( () => OperatingSystem.IsWindows() && H264Decoder.IsAvailable() );

	/// <summary>Creates the panel over a source and lists the source's windows, or streams the window it was opened for at once.</summary>
	/// <param name="context">The panel's context.</param>
	/// <param name="source">The host connection: <see cref="LocalStreamSource"/> or a remote one.</param>
	/// <param name="options">The options each stream starts with; null uses <see cref="CaptureOptions.Default"/>.</param>
	/// <param name="initial">The window to stream at once, such as one an agent works with or the one a saved layout streamed; null shows the window list.</param>
	public StreamContent( PanelContext context, IStreamSource source, CaptureOptions? options = null, StreamPanelInput? initial = null )
	{
		_context = context;
		_source = source;
		_options = (options ?? CaptureOptions.Default).Clamp() with { Codec = CanDecodeH264.Value ? CaptureOptions.H264 : CaptureOptions.Jpeg };
		_source.FrameReceived += QueueFrame;
		_source.Ended += QueueEnded;
		_source.AgentUsed += QueueAgent;
		_open.Add( this );
		if ( initial is not null )
			UpdateStart( new( initial.Hwnd, initial.Title, "", 0, 0 ), isChosen: false );
		else
			UpdateList();
	}

	/// <summary>Stops the stream and lets go of the source's events and the last frame.</summary>
	public override void Closed()
	{
		_open.Remove( this );
		_source.FrameReceived -= QueueFrame;
		_source.Ended -= QueueEnded;
		_source.AgentUsed -= QueueAgent;
		ReleaseKeys();
		if ( _streaming is not null )
			_source.Stop();

		_frameSource.Dispose();
		_frame = null;
		ClearPreviews();
		lock ( _decoder )
		{
			_decoder.Value.Decoder?.Dispose();
			_decoder.Value = (0, null);
		}
	}

	/// <summary>Builds the window picker, or the streamed window with its toolbar.</summary>
	public override Blob Build()
	{
		_ = _version.Value;
		var theme = Theme.Current;
		return new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Window stream",
			FlexGrow = 1,
			MinHeight = 0,
			Children = [_streaming is { } window ? BuildViewer( window, theme ) : BuildPicker( theme )],
		};
	}

	/// <summary>Builds the toolbar with the way back, the window title, the frame rate and latency, and the frame surface below it.</summary>
	private Container BuildViewer( CaptureWindow window, Theme theme )
	{
		var stats = _frame is { } frame
			? $"{frame.Width}×{frame.Height} · {ComputeFrameRate( Stopwatch.GetTimestamp() )} fps · {_latency:0} ms"
			: "Waiting for the first frame…";
		if ( !_source.CanControl )
			stats += " · view only";

		var toolbar = new Row
		{
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Padding = new Edges( theme.Spacing / 2, theme.Spacing ),
			Children =
			[
				theme.BuildButton( "Windows", () => _context.Run( UpdateBack ) ),
				new Text { Content = window.Title, FlexShrink = 1, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis, },
				.. BuildAgentLabel( theme ),
				.. BuildResolutionSwitch( window, theme ),
				new Text { Content = stats, Color = theme.Muted, FontSize = ViewStyle.SmallSize, FlexShrink = 0, TextWrap = TextWrap.NoWrap, },
			],
		};

		var picture = Blob.Sized( BuildFrame );
		picture.FlexGrow = 1;
		picture.MinHeight = 0;
		var surface = new Container
		{
			Handle = _surface,
			FlexGrow = 1,
			MinHeight = 0,
			MinWidth = 0,
			Overflow = Overflow.Hidden,
			BackgroundColor = theme.Canvas,
			Focusable = true,
			// A stream an agent opened must not take the focus from the chat composer.
			AutoFocus = _isChosen,
			Cursor = _source.CanControl ? Cursor.Default : Cursor.NotAllowed,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = $"Stream of {window.Title}",
			OnFocus = _ => _context.Run( () => UpdateFocus( true ) ),
			OnBlur = _ => _context.Run( () => UpdateFocus( false ) ),
			OnKeyDown = e => _context.Run( () => UpdateKey( e, isDown: true ) ),
			OnKeyUp = e => _context.Run( () => UpdateKey( e, isDown: false ) ),
			OnPointerDown = e => _context.Run( () => UpdatePointerDown( e ) ),
			OnPointerMove = e => _context.Run( () => UpdatePointerMove( e ) ),
			OnPointerUp = e => _context.Run( () => UpdatePointerUp( e ) ),
			OnWheel = e => _context.Run( () => UpdateWheel( e ) ),
			Children = [picture],
		};

		var children = new List<Blob> { toolbar };
		if ( _status.Length > 0 )
			children.Add( new Text { Content = _status, Color = theme.Error, Selectable = true, Padding = new Edges( 0, theme.Spacing ), } );
		if ( _resolutionNote.Length > 0 )
			children.Add( new Text { Content = _resolutionNote, Color = theme.Muted, FontSize = ViewStyle.SmallSize, Selectable = true, Padding = new Edges( 0, theme.Spacing ), } );

		children.Add( surface );
		return new Container
		{
			FlexGrow = 1,
			MinHeight = 0,
			Children = children,
		};
	}

	/// <summary>Draws the frame centered and scaled to fit the space given, and remembers where, for pointer mapping.</summary>
	private Blob BuildFrame( LayoutSize size )
	{
		_ = _version.Value;
		NoteViewport( size );
		if ( _frame is not { } frame || !double.IsFinite( size.Width ) || !double.IsFinite( size.Height ) || size.Width <= 0 || size.Height <= 0 )
		{
			_fit = null;
			return new Container
			{
				Width = Length.Percent( 100 ),
				Height = Length.Percent( 100 ),
				AlignItems = AlignItems.Center,
				JustifyContent = JustifyContent.Center,
				Children = [new Text { Content = _frame is null ? "Waiting for the first frame…" : "", Color = Theme.Current.Muted, }],
			};
		}

		var fit = ComputeFit( size.Width, size.Height, frame.Width, frame.Height );
		_fit = fit;
		return new Container
		{
			Width = Length.Percent( 100 ),
			Height = Length.Percent( 100 ),
			Children =
			[
				new Image
				{
					Source = _frameSource,
					Fit = ImageFit.Fill,
					Position = PositionType.Absolute,
					Left = fit.Left,
					Top = fit.Top,
					Width = frame.Width * fit.Scale,
					Height = frame.Height * fit.Scale,
				},
				.. BuildAgentMarker( fit, frame.Width, frame.Height ),
			],
		};
	}

	/// <summary>Computes where a frame sits when scaled to fit an area and centered: its top-left corner and its scale.</summary>
	/// <param name="width">The area width in logical pixels.</param>
	/// <param name="height">The area height in logical pixels.</param>
	/// <param name="frameWidth">The frame width in pixels.</param>
	/// <param name="frameHeight">The frame height in pixels.</param>
	public static FrameFit ComputeFit( double width, double height, int frameWidth, int frameHeight )
	{
		var scale = Math.Min( width / Math.Max( 1, frameWidth ), height / Math.Max( 1, frameHeight ) );
		return new( (width - frameWidth * scale) / 2, (height - frameHeight * scale) / 2, scale );
	}

	/// <summary>Reads the window list off the UI thread.</summary>
	private void UpdateList()
	{
		if ( _isListing )
			return;

		_isListing = true;
		UpdateVersion();
		_ = UpdateListAsync();
	}

	/// <summary>Reads the window list and applies it or its error on the UI thread.</summary>
	private async Task UpdateListAsync()
	{
		IReadOnlyList<CaptureWindow>? windows = null;
		var status = "";
		try
		{
			windows = await _source.ListAsync( _context.Lifetime ).ConfigureAwait( false );
		}
		catch ( Exception error )
		{
			status = error is OperationCanceledException ? "" : $"Could not list windows: {error.Message}";
		}

		_context.Post( () =>
		{
			_windows = windows ?? _windows;
			if ( windows is not null )
				UpdatePreviews( windows );
			_status = status;
			_isListing = false;
			UpdateVersion();
		} );
	}

	/// <summary>Starts streaming a window.</summary>
	/// <param name="window">The window.</param>
	/// <param name="isChosen">Whether the user chose it in the list, which focuses the stream; false for one opened for an agent or restored.</param>
	private void UpdateStart( CaptureWindow window, bool isChosen )
	{
		StopPreviews();
		_isChosen = isChosen;
		_streaming = window;
		_status = "";
		_resolutionNote = "";
		_latency = 0;
		_arrivals.Clear();
		_lastMove = (-1, -1);
		ClearFrame();
		_requestedAt = Stopwatch.GetTimestamp();
		var generation = ++_generation;
		UpdateVersion();
		_ = UpdateStartAsync( window, BuildStartOptions( window.Hwnd ), generation );
	}

	/// <summary>Asks the source to start, shows the host's note about the screen's resolution, and returns to the picker with the error when it cannot.</summary>
	private async Task UpdateStartAsync( CaptureWindow window, CaptureOptions options, int generation )
	{
		try
		{
			var note = await _source.StartAsync( window.Hwnd, options, _context.Lifetime ).ConfigureAwait( false );
			_context.Post( () => ApplyStarted( note, generation ) );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			_context.Post( () =>
			{
				if ( generation == _generation )
					UpdateEnded( $"Could not stream {window.Title}: {error.Message}" );
			} );
		}
	}

	/// <summary>Stops the stream and shows the picker again with a fresh list.</summary>
	private void UpdateBack()
	{
		ReleaseKeys();
		_generation++;
		_source.Stop();
		_streaming = null;
		ClearFrame();
		_status = "";
		_resolutionNote = "";
		UpdateList();
		UpdateVersion();
	}

	/// <summary>Shows the picker with the reason the stream ended.</summary>
	private void UpdateEnded( string message )
	{
		ReleaseKeys();
		_generation++;
		_streaming = null;
		ClearFrame();
		_status = message;
		_resolutionNote = "";
		UpdateList();
		UpdateVersion();
	}

	/// <summary>Decodes a frame on the thread that received it and shows it on the UI thread.</summary>
	/// <remarks>H.264 frames go through one decoder per stream, in the order they arrive; one that gives no picture yet is still acknowledged.</remarks>
	private void QueueFrame( StreamFrame frame )
	{
		var generation = Volatile.Read( ref _generation );
		ImageSource? image;
		try
		{
			image = frame.Codec == CaptureOptions.H264 ? DecodeH264( frame, generation ) : ImageSource.Decode( frame.Data );
		}
		catch ( Exception error ) when ( error is FormatException or InvalidOperationException or ArgumentException or NotSupportedException or ImageLimitException or System.Runtime.InteropServices.COMException )
		{
			image = null;
		}

		if ( image is null )
		{
			// A broken frame, or one the decoder holds back, is skipped; acknowledging it asks for the next.
			_ = _context.Post( _source.Ack );
			return;
		}

		if ( !_context.Post( () => ApplyFrame( image, generation ) ) )
			image.Dispose();
	}

	/// <summary>Decodes an H.264 frame with the decoder of its stream, starting one for a new stream; null while the decoder gives no picture.</summary>
	private ImageSource? DecodeH264( StreamFrame frame, int generation )
	{
		lock ( _decoder )
		{
			if ( _decoder.Value.Generation != generation || _decoder.Value.Decoder is null )
			{
				_decoder.Value.Decoder?.Dispose();
				_decoder.Value = (generation, new H264Decoder());
			}

			return _decoder.Value.Decoder!.Decode( frame.Data, frame.Width, frame.Height ) is { } picture
				? ImageSource.Transfer( picture.Width, picture.Height, picture.Pixels, static () => { } )
				: null;
		}
	}

	/// <summary>Puts a decoded frame on screen, updates the frame rate and latency, and acknowledges it.</summary>
	private void ApplyFrame( ImageSource image, int generation )
	{
		if ( generation != _generation || _streaming is null )
		{
			image.Dispose();
			return;
		}

		var now = Stopwatch.GetTimestamp();
		if ( _requestedAt != 0 )
		{
			var sample = Stopwatch.GetElapsedTime( _requestedAt, now ).TotalMilliseconds;
			_latency = _latency == 0 ? sample : _latency + (sample - _latency) * LatencySmoothing;
		}

		_arrivals.Enqueue( now );
		_frame = image;
		_frameSource.Publish( image );
		_requestedAt = Stopwatch.GetTimestamp();
		_source.Ack();
		UpdateVersion();
	}

	/// <summary>Releases the current frame and resets the stable provider before the viewer leaves or starts another stream.</summary>
	private void ClearFrame()
	{
		_frame = null;
		_frameSource.Clear();
	}

	/// <summary>Returns to the picker when the source ends the stream.</summary>
	private void QueueEnded( string message ) => _context.Post( () =>
	{
		if ( _streaming is not null )
			UpdateEnded( message );
	} );

	/// <summary>Counts the frames that arrived in the last second.</summary>
	private int ComputeFrameRate( long now )
	{
		while ( _arrivals.Count > 0 && Stopwatch.GetElapsedTime( _arrivals.Peek(), now ) > TimeSpan.FromSeconds( 1 ) )
			_ = _arrivals.Dequeue();

		return _arrivals.Count;
	}

	/// <summary>Requests a rebuild.</summary>
	private void UpdateVersion() => _version.Value++;
}

/// <summary>Places a frame inside the stream panel's surface.</summary>
/// <param name="Left">The frame's left edge in logical pixels from the surface's.</param>
/// <param name="Top">The frame's top edge in logical pixels from the surface's.</param>
/// <param name="Scale">Logical pixels per frame pixel.</param>
public readonly record struct FrameFit( double Left, double Top, double Scale )
{
	/// <summary>Maps a point on the surface to frame pixels, or null when it is outside a frame of the given size.</summary>
	/// <param name="x">The horizontal surface coordinate.</param>
	/// <param name="y">The vertical surface coordinate.</param>
	/// <param name="frameWidth">The frame width in pixels.</param>
	/// <param name="frameHeight">The frame height in pixels.</param>
	/// <param name="clamp">Whether a point outside is moved to the nearest frame edge instead of being dropped, as for a release after a drag.</param>
	public (double X, double Y)? ComputeFramePoint( double x, double y, int frameWidth, int frameHeight, bool clamp )
	{
		if ( Scale <= 0 )
			return null;

		var frameX = (x - Left) / Scale;
		var frameY = (y - Top) / Scale;
		if ( frameX >= 0 && frameY >= 0 && frameX < frameWidth && frameY < frameHeight )
			return (frameX, frameY);

		return clamp ? (Math.Clamp( frameX, 0, frameWidth - 1 ), Math.Clamp( frameY, 0, frameHeight - 1 )) : null;
	}
}

/// <summary>Publishes immutable stream frames through one Goo image-provider identity, retaining the displayed generation until Goo uploads its replacement.</summary>
internal sealed class FrameImageSource : ImageSourceProvider, IDisposable
{
	/// <summary>Stores the newest frame owned by this provider, or null before one is published.</summary>
	private ImageSource? _image;

	/// <summary>Counts published and cleared generations.</summary>
	private ulong _contentVersion;

	/// <summary>Tracks whether this provider released its owner reference.</summary>
	private bool _isDisposed;

	/// <inheritdoc/>
	public ulong ContentVersion => _contentVersion;

	/// <inheritdoc/>
	public event Action? ContentChanged;

	/// <inheritdoc/>
	public ImageSourceLease Acquire()
	{
		if ( _image is { } image )
			return image.Acquire();

		var lease = new ImageSourceLease();
		_ = lease.Fail();
		return lease;
	}

	/// <summary>Publishes a new frame and takes ownership of it.</summary>
	/// <param name="image">The immutable frame.</param>
	public void Publish( ImageSource image )
	{
		ObjectDisposedException.ThrowIf( _isDisposed, this );
		var previous = _image;
		_image = image;
		_contentVersion++;
		previous?.Dispose();
		ContentChanged?.Invoke();
	}

	/// <summary>Clears and releases the published frame.</summary>
	public void Clear()
	{
		if ( _image is null )
			return;

		var previous = _image;
		_image = null;
		_contentVersion++;
		previous.Dispose();
		ContentChanged?.Invoke();
	}

	/// <summary>Releases this provider's current frame owner.</summary>
	public void Dispose()
	{
		if ( _isDisposed )
			return;

		_isDisposed = true;
		_image?.Dispose();
		_image = null;
	}
}
