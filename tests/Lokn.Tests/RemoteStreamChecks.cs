using System.Text.Json.Nodes;
using System.Threading.Channels;
using Lokn.Modules.Remote;

namespace Lokn.Tests;

/// <summary>Checks the <c>cap.*</c> frames of docs/remote.md end to end: <see cref="StreamService"/> on the host and <see cref="RemoteStreamSource"/> on the client, over memory with a fake capturer.</summary>
internal static class RemoteStreamChecks
{
	/// <summary>Bounds every wait, so a broken check fails rather than hangs.</summary>
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds( 10 );

	/// <summary>Defines how long a check watches for frames that must not come.</summary>
	private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds( 250 );

	/// <summary>Defines the fake window's handle.</summary>
	private const long Window = 4242;

	/// <summary>Runs the remote stream checks.</summary>
	internal static async Task RunAsync()
	{
		await CheckStreamAsync();
		await CheckViewOnlyAsync();
		await CheckCaptureLimitAsync();
		await CheckCaptureFailureAsync();
		await CheckCloseAsync();
		Console.WriteLine( "PASS remote stream: cap.list, cap.start with frames gated by cap.ack, cap.* with the control scope only, the capture limit, cap.stop, cap.end on capture failure, and cleanup on close." );
	}

	/// <summary>Checks listing, starting, acknowledgement gating, input and stopping for a controlling device.</summary>
	private static async Task CheckStreamAsync()
	{
		var capturer = new FakeCapturer();
		var inputs = Channel.CreateUnbounded<(long Window, StreamInput Input, int Width, int Height)>();
		var service = BuildService( _ => capturer, ( window, input, width, height ) => inputs.Writer.TryWrite( (window, input, width, height) ) );
		await using var pair = Connect( service, canControl: true );
		var source = pair.Source;
		var frames = Watch( source );

		var windows = await source.ListAsync( CancellationToken.None ).WaitAsync( Patience );
		CoreChecks.Check( windows.Count == 1 && windows[0].Hwnd == Window && windows[0].Title == "Fake" && windows[0].Width == FakeCapturer.Width, "cap.list answers the host's windows." );

		await source.StartAsync( Window, new( 60, 0.5, 640 ), CancellationToken.None ).WaitAsync( Patience );
		var first = await frames.Reader.ReadAsync().AsTask().WaitAsync( Patience );
		CoreChecks.Check( first.Width == FakeCapturer.Width && first.Height == FakeCapturer.Height && first.Jpeg.Length == 1, "cap.start streams cap.frame with its size and bytes." );
		CoreChecks.Check( !await ArrivesAsync( frames ), "No frame follows until the client acknowledges." );
		source.Ack();
		var second = await frames.Reader.ReadAsync().AsTask().WaitAsync( Patience );
		CoreChecks.Check( second.Jpeg[0] != first.Jpeg[0], "cap.ack lets the next frame through." );

		source.Input( new( StreamInput.Down, 1, 1, Button: 0 ) );
		source.Input( new( StreamInput.Up, 1, 1, Button: 0 ) );
		var down = await inputs.Reader.ReadAsync().AsTask().WaitAsync( Patience );
		var up = await inputs.Reader.ReadAsync().AsTask().WaitAsync( Patience );
		CoreChecks.Check( down.Window == Window && down.Input.Kind == StreamInput.Down && up.Input.Kind == StreamInput.Up, "cap.input reaches the streamed window in order." );
		CoreChecks.Check( down.Width == FakeCapturer.Width && down.Height == FakeCapturer.Height, "cap.input maps against the last frame's size." );

		source.Stop();
		await WaitUntilAsync( () => capturer.IsDisposed );
		CoreChecks.Check( capturer.IsDisposed, "cap.stop stops the capture and releases the capturer." );
		source.Ack();
		CoreChecks.Check( !await ArrivesAsync( frames ), "No frame follows cap.stop." );
	}

	/// <summary>Checks that a connection streams at most <see cref="StreamService.MaxCaptures"/> windows at once.</summary>
	private static async Task CheckCaptureLimitAsync()
	{
		await using var pair = Connect( BuildService( _ => new FakeCapturer(), null ), canControl: true );
		for ( var i = 0; i < StreamService.MaxCaptures; i++ )
			await pair.Client.RequestAsync( BuildStart(), CancellationToken.None ).WaitAsync( Patience );
		CoreChecks.Check( await RequestCodeAsync( pair, BuildStart() ) == RemoteErrors.Failed, "A connection may not stream more windows than allowed." );
	}

	/// <summary>Checks that a view-only device can neither list, stream nor send input.</summary>
	private static async Task CheckViewOnlyAsync()
	{
		var injected = 0;
		var started = 0;
		var service = BuildService( _ =>
		{
			Interlocked.Increment( ref started );
			return new FakeCapturer();
		}, ( _, _, _, _ ) => Interlocked.Increment( ref injected ) );
		await using var pair = Connect( service, canControl: false );
		CoreChecks.Check( !pair.Source.CanControl, "A view-only source reports it cannot control." );
		CoreChecks.Check( await RequestCodeAsync( pair, Frame.Create( "cap.list" ) ) == RemoteErrors.Scope, "cap.list from a view-only device is refused with scope." );
		CoreChecks.Check( await RequestCodeAsync( pair, BuildStart() ) == RemoteErrors.Scope && Volatile.Read( ref started ) == 0, "cap.start from a view-only device is refused with scope and captures nothing." );

		var request = Frame.Create( "cap.input" );
		request["cap"] = 1;
		request["kind"] = StreamInput.Move;
		request["x"] = 1;
		request["y"] = 1;
		CoreChecks.Check( await RequestCodeAsync( pair, request ) == RemoteErrors.Scope && Volatile.Read( ref injected ) == 0, "cap.input from a view-only device is refused with scope." );
	}

	/// <summary>Builds a <c>cap.start</c> request for the fake window.</summary>
	private static JsonObject BuildStart()
	{
		var start = Frame.Create( "cap.start" );
		start["hwnd"] = Window;
		return start;
	}

	/// <summary>Sends a request and returns the error code it was refused with, or null when it succeeded.</summary>
	private static async Task<string?> RequestCodeAsync( Pair pair, JsonObject request )
	{
		try
		{
			_ = await pair.Client.RequestAsync( request, CancellationToken.None ).WaitAsync( Patience );
			return null;
		}
		catch ( RemoteException error )
		{
			return error.Code;
		}
	}

	/// <summary>Checks that a capture failure ends the stream with <c>cap.end</c>.</summary>
	private static async Task CheckCaptureFailureAsync()
	{
		var capturer = new FakeCapturer { Error = new InvalidOperationException( "The window was closed." ) };
		await using var pair = Connect( BuildService( _ => capturer, null ), canControl: true );
		var ended = new TaskCompletionSource<string>( TaskCreationOptions.RunContinuationsAsynchronously );
		pair.Source.Ended += message => ended.TrySetResult( message );
		await pair.Source.StartAsync( Window, new( 60, 0.5, 640 ), CancellationToken.None ).WaitAsync( Patience );
		var message = await ended.Task.WaitAsync( Patience );
		CoreChecks.Check( message == "The window was closed.", "A failed capture pushes cap.end with its message." );
		await WaitUntilAsync( () => capturer.IsDisposed );
		CoreChecks.Check( capturer.IsDisposed, "A failed capture releases the capturer." );
	}

	/// <summary>Checks that closing the connection stops the host's streams and ends the client's.</summary>
	private static async Task CheckCloseAsync()
	{
		var capturer = new FakeCapturer();
		var pair = Connect( BuildService( _ => capturer, null ), canControl: true );
		var frames = Watch( pair.Source );
		var ended = new TaskCompletionSource<string>( TaskCreationOptions.RunContinuationsAsynchronously );
		pair.Source.Ended += message => ended.TrySetResult( message );
		await pair.Source.StartAsync( Window, new( 60, 0.5, 640 ), CancellationToken.None ).WaitAsync( Patience );
		_ = await frames.Reader.ReadAsync().AsTask().WaitAsync( Patience );
		await pair.DisposeAsync();
		_ = await ended.Task.WaitAsync( Patience );
		await WaitUntilAsync( () => capturer.IsDisposed );
		CoreChecks.Check( capturer.IsDisposed, "Closing the connection stops its streams on the host and ends the client's." );
	}

	/// <summary>Builds a service over one fake window, a capturer factory, a one-byte encoder and an input sink.</summary>
	private static StreamService BuildService( Func<long, IWindowCapturer> capturer, Action<long, StreamInput, int, int>? inject ) => new()
	{
		ListWindows = () => [new( Window, "Fake", "fake.exe", FakeCapturer.Width, FakeCapturer.Height )],
		CreateCapturer = capturer,
		Encode = ( frame, _, _ ) => Task.FromResult( new[] { frame.Pixels[0] } ),
		Inject = inject ?? (( _, _, _, _ ) => { }),
	};

	/// <summary>Connects a client source to a host service over memory and starts both peers.</summary>
	private static Pair Connect( StreamService service, bool canControl )
	{
		var (first, second) = MemoryConnection.CreatePair();
		var host = new RpcPeer( first );
		var client = new RpcPeer( second );
		service.Register( new( "device", "Device", canControl ? DeviceScopes.Controlling : DeviceScopes.Paired, host ) );
		var source = new RemoteStreamSource( client, canControl );
		host.Start();
		client.Start();
		return new( host, client, source );
	}

	/// <summary>Collects a source's frames.</summary>
	private static Channel<StreamFrame> Watch( IStreamSource source )
	{
		var frames = Channel.CreateUnbounded<StreamFrame>();
		source.FrameReceived += frame => frames.Writer.TryWrite( frame );
		return frames;
	}

	/// <summary>Reports whether a frame arrives within <see cref="Quiet"/>, consuming it; a read that times out is cancelled, so it cannot take a later frame.</summary>
	private static async Task<bool> ArrivesAsync( Channel<StreamFrame> frames )
	{
		using var quiet = new CancellationTokenSource( Quiet );
		try
		{
			_ = await frames.Reader.ReadAsync( quiet.Token );
			return true;
		}
		catch ( OperationCanceledException ) when ( quiet.IsCancellationRequested )
		{
			return false;
		}
	}

	/// <summary>Waits until a condition holds or <see cref="Patience"/> passes.</summary>
	private static async Task WaitUntilAsync( Func<bool> condition )
	{
		var deadline = DateTime.UtcNow + Patience;
		while ( !condition() && DateTime.UtcNow < deadline )
			await Task.Delay( 10 );
	}

	/// <summary>Holds both peers of a check and the client's source; disposing closes the connection.</summary>
	/// <param name="Host">The host peer.</param>
	/// <param name="Client">The client peer.</param>
	/// <param name="Source">The client's stream source.</param>
	private sealed record Pair( RpcPeer Host, RpcPeer Client, RemoteStreamSource Source ) : IAsyncDisposable
	{
		/// <summary>Closes both peers.</summary>
		public async ValueTask DisposeAsync()
		{
			await Client.DisposeAsync();
			await Host.DisposeAsync();
		}
	}

	/// <summary>Returns 4×2 frames whose bytes change with every capture, or throws <see cref="Error"/>.</summary>
	private sealed class FakeCapturer : IWindowCapturer
	{
		/// <summary>Defines the frame width.</summary>
		internal const int Width = 4;

		/// <summary>Defines the frame height.</summary>
		internal const int Height = 2;

		/// <summary>Stores the value the next frame is filled with.</summary>
		private int _value;

		/// <summary>Gets or sets an error every capture throws, or null.</summary>
		internal Exception? Error
		{
			get; init;
		}

		/// <summary>Gets whether the session released the capturer.</summary>
		internal bool IsDisposed => Volatile.Read( ref _disposed ) == 1;

		/// <summary>Is 1 once released.</summary>
		private int _disposed;

		/// <summary>Gets the frame size.</summary>
		public (int Width, int Height) SourceSize => (Width, Height);

		/// <summary>Returns a frame filled with a new value.</summary>
		public BgraFrame? Capture( int maxWidth )
		{
			if ( Error is { } error )
				throw error;

			var pixels = new byte[Width * Height * 4];
			Array.Fill( pixels, (byte)Interlocked.Increment( ref _value ) );
			return new( Width, Height, pixels );
		}

		/// <summary>Records the release.</summary>
		public void Dispose() => Volatile.Write( ref _disposed, 1 );
	}
}
