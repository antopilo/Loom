using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lokn.Modules.Remote;

/// <summary>Streams a host's window over the <c>cap.*</c> frames of docs/remote.md, for the stream panel of a client.</summary>
/// <remarks>
/// The constructor registers the <c>cap.frame</c> and <c>cap.end</c> push handlers, so construct the source before the peer's
/// <see cref="RpcPeer.Start"/>. A frame that arrives before the <c>cap.start</c> answer is held and delivered with it.
/// </remarks>
public sealed class RemoteStreamSource : IStreamSource
{
	/// <summary>Stores the peer the frames go through.</summary>
	private readonly RpcPeer _peer;

	/// <summary>Guards <see cref="_cap"/>, <see cref="_isStarting"/> and <see cref="_early"/>.</summary>
	private readonly Lock _lock = new();

	/// <summary>Stores the running stream's id, or 0 when none runs.</summary>
	private long _cap;

	/// <summary>Stores whether a <c>cap.start</c> awaits its answer.</summary>
	private bool _isStarting;

	/// <summary>Stores the last frame received while a start awaited its answer, with its stream id.</summary>
	private (long Cap, StreamFrame Frame)? _early;

	/// <summary>Stores the handle of the window the running stream shows, or of the last one.</summary>
	private long _window;

	/// <summary>Creates the source and registers its push handlers on a peer that has not started.</summary>
	/// <param name="peer">The connection to the host.</param>
	/// <param name="canControl">Whether the host granted this device the <c>control</c> scope.</param>
	public RemoteStreamSource( RpcPeer peer, bool canControl )
	{
		_peer = peer;
		CanControl = canControl;
		peer.OnPush( "cap.frame", ReceiveFrame );
		peer.OnPush( "cap.end", ReceiveEnd );
		peer.OnPush( "cap.agent", ReceiveAgent );
		peer.Closed += _ => End( null, "The connection to the host closed." );
	}

	/// <inheritdoc/>
	public event Action<StreamFrame>? FrameReceived;

	/// <inheritdoc/>
	public event Action<string>? Ended;

	/// <inheritdoc/>
	public event Action<AgentWindowUse>? AgentUsed;

	/// <inheritdoc/>
	public bool CanControl
	{
		get;
	}

	/// <summary>Asks the host for its windows.</summary>
	public async Task<IReadOnlyList<CaptureWindow>> ListAsync( CancellationToken cancellation )
	{
		var answer = await _peer.RequestAsync( Frame.Create( "cap.list" ), cancellation ).ConfigureAwait( false );
		return answer["windows"]?.Deserialize<List<CaptureWindow>>( StreamService.Wire ) ?? [];
	}

	/// <summary>Stops any running stream and starts one of a host window.</summary>
	public async Task StartAsync( long window, CaptureOptions options, CancellationToken cancellation )
	{
		Stop();
		lock ( _lock )
		{
			_isStarting = true;
			_early = null;
		}

		var request = Frame.Create( "cap.start" );
		request["hwnd"] = window;
		request["fps"] = options.Fps;
		request["quality"] = options.Quality;
		request["maxWidth"] = options.MaxWidth;
		// H.264 only when this PC can decode it; the host still sends JPEG when it cannot encode.
		request["codecs"] = options.Codec == CaptureOptions.H264 ? new JsonArray( CaptureOptions.H264, CaptureOptions.Jpeg ) : new JsonArray( CaptureOptions.Jpeg );
		long cap;
		try
		{
			var answer = await _peer.RequestAsync( request, cancellation ).ConfigureAwait( false );
			cap = Frame.ReadLong( answer, "cap" ) ?? throw new RemoteException( RemoteErrors.BadRequest, "The host did not name the stream." );
		}
		catch
		{
			lock ( _lock )
			{
				_isStarting = false;
				_early = null;
			}

			throw;
		}

		StreamFrame? early;
		lock ( _lock )
		{
			_isStarting = false;
			_cap = cap;
			_window = window;
			early = _early is { } held && held.Cap == cap ? held.Frame : null;
			_early = null;
		}

		if ( early is not null )
			FrameReceived?.Invoke( early );
	}

	/// <summary>Stops the running stream, telling the host without waiting.</summary>
	public void Stop()
	{
		long cap;
		lock ( _lock )
		{
			cap = _cap;
			_cap = 0;
		}

		if ( cap != 0 && !_peer.IsClosed )
			_ = SendQuietlyAsync( new JsonObject { ["t"] = "cap.stop", ["cap"] = cap } );
	}

	/// <summary>Acknowledges the last frame, letting the host send the next.</summary>
	public void Ack()
	{
		var cap = ReadCap();
		if ( cap != 0 && !_peer.IsClosed )
			_ = PushQuietlyAsync( new JsonObject { ["t"] = "cap.ack", ["cap"] = cap } );
	}

	/// <summary>Sends a pointer or key event, when this device may control and a stream runs.</summary>
	public void Input( StreamInput input )
	{
		var cap = ReadCap();
		if ( !CanControl || cap == 0 || _peer.IsClosed )
			return;

		var request = JsonSerializer.SerializeToNode( input, StreamService.Wire )!.AsObject();
		request["t"] = "cap.input";
		request["cap"] = cap;
		_ = SendQuietlyAsync( request );
	}

	/// <summary>Reads the running stream's id, or 0.</summary>
	private long ReadCap()
	{
		lock ( _lock )
			return _cap;
	}

	/// <summary>Delivers a <c>cap.frame</c> of the running stream, or holds it while a start awaits its answer.</summary>
	private void ReceiveFrame( JsonObject frame )
	{
		if ( Frame.ReadLong( frame, "cap" ) is not { } cap
			|| Frame.ReadLong( frame, "w" ) is not { } width
			|| Frame.ReadLong( frame, "h" ) is not { } height )
			return;

		// A host before H.264 names no codec and sends JPEG in "jpeg".
		var codec = Frame.ReadString( frame, "codec" ) ?? CaptureOptions.Jpeg;
		if ( Frame.ReadString( frame, codec == CaptureOptions.Jpeg ? "jpeg" : "data" ) is not { } encoded )
			return;

		byte[] bytes;
		try
		{
			bytes = Convert.FromBase64String( encoded );
		}
		catch ( FormatException )
		{
			return;
		}

		var received = new StreamFrame( (int)width, (int)height, bytes, codec );
		lock ( _lock )
		{
			if ( _isStarting )
			{
				_early = (cap, received);
				return;
			}

			if ( cap != _cap )
				return;
		}

		FrameReceived?.Invoke( received );
	}

	/// <summary>Passes on a <c>cap.agent</c> of the streamed window: an agent acted in it.</summary>
	private void ReceiveAgent( JsonObject frame )
	{
		if ( Frame.ReadLong( frame, "hwnd" ) is not { } window )
			return;

		lock ( _lock )
		{
			if ( _cap == 0 || _window != window )
				return;
		}

		AgentUsed?.Invoke( new( window, Frame.ReadString( frame, "kind" ) ?? "", ReadFraction( frame, "x" ), ReadFraction( frame, "y" ) ) );
	}

	/// <summary>Reads a coordinate fraction of <c>cap.agent</c>, or -1 when it is missing.</summary>
	private static double ReadFraction( JsonObject frame, string name ) =>
		frame[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<double>( out var number ) ? number : -1;

	/// <summary>Ends the running stream on the host's <c>cap.end</c>.</summary>
	private void ReceiveEnd( JsonObject frame )
	{
		if ( Frame.ReadLong( frame, "cap" ) is { } cap )
			End( cap, Frame.ReadString( frame, "message" ) ?? "The stream ended." );
	}

	/// <summary>Forgets the running stream and raises <see cref="Ended"/> when it is <paramref name="cap"/>, or any stream when null.</summary>
	private void End( long? cap, string message )
	{
		lock ( _lock )
		{
			if ( _cap == 0 || ( cap is { } id && id != _cap ) )
				return;

			_cap = 0;
		}

		Ended?.Invoke( message );
	}

	/// <summary>Sends a request whose answer does not matter, ignoring its failure; a refused input or a closed connection changes nothing here.</summary>
	private async Task SendQuietlyAsync( JsonObject request )
	{
		try
		{
			_ = await _peer.RequestAsync( request, CancellationToken.None ).ConfigureAwait( false );
		}
		catch ( Exception )
		{
			// The stream's end, if any, arrives as cap.end or the peer's Closed event.
		}
	}

	/// <summary>Sends a push, ignoring the failure of a closed connection.</summary>
	private async Task PushQuietlyAsync( JsonObject frame )
	{
		try
		{
			await _peer.PushAsync( frame ).ConfigureAwait( false );
		}
		catch ( Exception )
		{
			// The connection closed; the peer's Closed event ends the stream.
		}
	}
}
