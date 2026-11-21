using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lokn.Modules.Remote;

/// <summary>Offers each connection the host's windows over the <c>cap.*</c> frames of docs/remote.md: listing, streaming with acknowledged frames, input, and stopping, all with the <c>control</c> scope.</summary>
/// <remarks>
/// Each <c>cap.start</c> runs its own <see cref="CaptureSession"/> under a new <c>cap</c> id of that connection, at most <see cref="MaxCaptures"/> at once; a capture that fails pushes
/// <c>cap.end</c>, and every session of a connection stops when it closes. Capture and input need Windows; elsewhere the list is empty and
/// <c>cap.start</c> fails. Handlers touch no application state, so nothing is posted to the UI thread.
/// </remarks>
public sealed class StreamService : IHostService
{
	/// <summary>The most windows one connection streams at once.</summary>
	public const int MaxCaptures = 4;

	/// <summary>Defines the wire form of <see cref="CaptureWindow"/> and <see cref="StreamInput"/>: camelCase JSON.</summary>
	internal static readonly JsonSerializerOptions Wire = new( JsonSerializerDefaults.Web );

	/// <summary>Gets or sets the window lister; null lists this PC's screens and windows with <see cref="ScreenCatalog.ListSources"/>. Tests supply their own.</summary>
	internal Func<IReadOnlyList<CaptureWindow>>? ListWindows
	{
		get; init;
	}

	/// <summary>Gets or sets the capturer factory by window handle; null opens a <see cref="WindowCapture"/>. Tests supply fake capturers.</summary>
	internal Func<long, IWindowCapturer>? CreateCapturer
	{
		get; init;
	}

	/// <summary>Gets or sets the frame encoder; null encodes JPEG with <see cref="JpegEncoder.EncodeAsync"/>.</summary>
	internal Func<BgraFrame, double, CancellationToken, Task<byte[]>>? Encode
	{
		get; init;
	}

	/// <summary>Gets or sets the input sink by window handle, event and frame size; null injects with an <see cref="InputInjector"/>.</summary>
	internal Action<long, StreamInput, int, int>? Inject
	{
		get; init;
	}

	/// <summary>Holds the streams of every open connection, so <see cref="NotifyAgent"/> finds the ones that show a window.</summary>
	private readonly ConcurrentDictionary<RemoteConnection, ConcurrentDictionary<long, Capture>> _connections = new();

	/// <summary>Registers the <c>cap.*</c> handlers for one connection and stops its streams when it closes.</summary>
	public void Register( RemoteConnection connection )
	{
		var peer = connection.Peer;
		var streams = new ConcurrentDictionary<long, Capture>();
		long lastCap = 0;
		_connections[connection] = streams;

		peer.On( "cap.list", _ =>
		{
			connection.RequireControl();
			return Task.FromResult<JsonObject?>( new JsonObject
			{
				["windows"] = JsonSerializer.SerializeToNode( ReadWindows(), Wire ),
			} );
		} );

		peer.On( "cap.start", frame =>
		{
			connection.RequireControl();
			var window = Frame.ReadLong( frame, "hwnd" ) ?? throw new RemoteException( RemoteErrors.BadRequest, "cap.start needs hwnd." );
			if ( streams.Count >= MaxCaptures )
				throw new RemoteException( RemoteErrors.Failed, $"A connection may stream at most {MaxCaptures} windows at once." );
			var defaults = CaptureOptions.Default;
			// A client lists the codecs it decodes, best first; one that lists none, such as an older Loom, decodes JPEG only.
			var codecs = frame["codecs"] is JsonArray listed ? listed.Select( item => item is JsonValue value && value.TryGetValue<string>( out var name ) ? name : null ).ToArray() : [];
			var options = new CaptureOptions(
				(int)( Frame.ReadLong( frame, "fps" ) ?? defaults.Fps ),
				ReadDouble( frame, "quality" ) ?? defaults.Quality,
				(int)( Frame.ReadLong( frame, "maxWidth" ) ?? defaults.MaxWidth ) )
			{
				Codec = codecs.Contains( CaptureOptions.H264 ) ? CaptureOptions.H264 : CaptureOptions.Jpeg,
			};
			var capturer = BuildCapturer( window );
			var cap = Interlocked.Increment( ref lastCap );
			var session = new CaptureSession( capturer, options,
				emitted => PushQuietly( peer, BuildFrame( cap, emitted ) ),
				error =>
				{
					if ( streams.TryRemove( cap, out var ended ) )
					{
						ended.Session.Dispose();
						PushQuietly( peer, new JsonObject { ["t"] = "cap.end", ["cap"] = cap, ["message"] = error.Message } );
					}
				},
				Encode );
			streams[cap] = new( window, session );
			if ( peer.IsClosed )
			{
				// The connection closed while this stream was being set up, after the sweep on Closed.
				if ( streams.TryRemove( cap, out var orphan ) )
					orphan.Session.Dispose();
				throw new RemoteException( RemoteErrors.Failed, "The connection closed." );
			}

			session.Start();
			return Task.FromResult<JsonObject?>( new JsonObject { ["cap"] = cap, ["codec"] = session.Options.Codec } );
		} );

		peer.OnPush( "cap.ack", frame =>
		{
			if ( Frame.ReadLong( frame, "cap" ) is { } cap && streams.TryGetValue( cap, out var capture ) )
				capture.Session.Ack();
		} );

		// Synchronous on purpose: the peer runs a request handler up to its first await in arrival order, so presses and releases keep their order.
		peer.On( "cap.input", frame =>
		{
			connection.RequireControl();
			var capture = ReadCapture( streams, frame );
			var input = frame.Deserialize<StreamInput>( Wire ) ?? throw new RemoteException( RemoteErrors.BadRequest, "cap.input needs an event." );
			if ( input.Kind is not (StreamInput.Move or StreamInput.Down or StreamInput.Up or StreamInput.Wheel or StreamInput.Key) )
				throw new RemoteException( RemoteErrors.BadRequest, $"Unknown input kind \"{input.Kind}\"." );

			var (width, height) = capture.Session.FrameSize;
			if ( Inject is { } inject )
				inject( capture.Window, input, width, height );
			else
				capture.Send( input, width, height );

			return Task.FromResult<JsonObject?>( null );
		} );

		peer.On( "cap.stop", frame =>
		{
			if ( Frame.ReadLong( frame, "cap" ) is { } cap && streams.TryRemove( cap, out var capture ) )
				capture.Session.Dispose();

			return Task.FromResult<JsonObject?>( null );
		} );

		peer.Closed += _ =>
		{
			_connections.TryRemove( KeyValuePair.Create( connection, streams ) );
			foreach ( var cap in streams.Keys )
			{
				if ( streams.TryRemove( cap, out var capture ) )
					capture.Session.Dispose();
			}
		};
	}

	/// <summary>Pushes <c>cap.agent</c> to every connection that streams the window an agent used, and to Loom on this PC (<see cref="HostGateway.LocalDevice"/>),
	/// whose panels stream in process; safe on any thread.</summary>
	/// <param name="use">What the agent did, where.</param>
	/// <returns>How many connections it went to.</returns>
	public int NotifyAgent( AgentWindowUse use )
	{
		var sent = 0;
		foreach ( var (connection, streams) in _connections )
		{
			if ( connection.DeviceId != HostGateway.LocalDevice && !streams.Values.Any( capture => capture.Window == use.Hwnd ) )
				continue;

			var push = new JsonObject { ["t"] = "cap.agent", ["hwnd"] = use.Hwnd, ["kind"] = use.Kind };
			if ( use.HasPoint )
			{
				push["x"] = use.X;
				push["y"] = use.Y;
			}
			PushQuietly( connection.Peer, push );
			sent++;
		}
		return sent;
	}

	/// <summary>Reads the sources to offer: the test lister, this PC's screens and windows on Windows, or none elsewhere.</summary>
	private IReadOnlyList<CaptureWindow> ReadWindows()
	{
		if ( ListWindows is { } list )
			return list();

		return OperatingSystem.IsWindows() ? ScreenCatalog.ListSources() : [];
	}

	/// <summary>Builds the capturer of a window or a screen, refusing ones that do not exist and platforms without capture.</summary>
	/// <exception cref="RemoteException">The window or screen does not exist or capture is unavailable.</exception>
	private IWindowCapturer BuildCapturer( long window )
	{
		if ( CreateCapturer is { } create )
			return create( window );

		if ( !OperatingSystem.IsWindows() )
			throw new RemoteException( RemoteErrors.Failed, "Window streaming needs Windows." );

		if ( !ScreenCatalog.Exists( window ) )
			throw new RemoteException( RemoteErrors.BadRequest, ScreenCatalog.IsScreen( window ) ? "The screen was disconnected." : "The window was closed." );

		return WindowCapturers.Open( (nint)window );
	}

	/// <summary>Reads the running stream a frame's <c>cap</c> names.</summary>
	/// <exception cref="RemoteException">No such stream runs on this connection.</exception>
	private static Capture ReadCapture( ConcurrentDictionary<long, Capture> streams, JsonObject frame ) =>
		Frame.ReadLong( frame, "cap" ) is { } cap && streams.TryGetValue( cap, out var capture )
			? capture
			: throw new RemoteException( RemoteErrors.BadRequest, "No such stream." );

	/// <summary>Builds the <c>cap.frame</c> push of a frame: JPEG in <c>jpeg</c>, as every client reads it, any other codec in <c>data</c> with its <c>codec</c>.</summary>
	private static JsonObject BuildFrame( long cap, StreamFrame frame )
	{
		var push = new JsonObject { ["t"] = "cap.frame", ["cap"] = cap, ["w"] = frame.Width, ["h"] = frame.Height, ["codec"] = frame.Codec };
		push[frame.Codec == CaptureOptions.Jpeg ? "jpeg" : "data"] = Convert.ToBase64String( frame.Data );
		return push;
	}

	/// <summary>Reads a number field as a double, or null when it is missing or not a number.</summary>
	private static double? ReadDouble( JsonObject frame, string name ) =>
		frame[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<double>( out var number ) ? number : null;

	/// <summary>Sends a push without waiting, ignoring a connection that closed meanwhile; the peer closes itself on send failures elsewhere.</summary>
	private static void PushQuietly( RpcPeer peer, JsonObject frame ) => _ = PushQuietlyAsync( peer, frame );

	/// <summary>Sends a push and swallows the failure of a closed connection.</summary>
	private static async Task PushQuietlyAsync( RpcPeer peer, JsonObject frame )
	{
		try
		{
			await peer.PushAsync( frame ).ConfigureAwait( false );
		}
		catch ( Exception ) when ( peer.IsClosed )
		{
			// The connection closed; its Closed handler stops the streams.
		}
		catch ( Exception )
		{
			// A frame that cannot be sent ends the connection, which stops the streams.
			await peer.DisposeAsync().ConfigureAwait( false );
		}
	}

	/// <summary>Holds one running stream of a connection: its window, its session and its input injector.</summary>
	/// <param name="Window">The streamed window's handle.</param>
	/// <param name="Session">The capture session.</param>
	private sealed record Capture( long Window, CaptureSession Session )
	{
		/// <summary>Stores the injector, created on the first input.</summary>
		private InputInjector? _injector;

		/// <summary>Sends an event to the window in the coordinates of a frame of the given size.</summary>
		public void Send( StreamInput input, int width, int height )
		{
			_injector ??= new( (nint)Window );
			_ = _injector.Send( input, width, height );
		}
	}
}
