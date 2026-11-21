namespace Lokn.Modules.Remote;

/// <summary>One paired host's window stream, across reconnects of its link: it passes each call to the stream of the connection the
/// client holds now, and fails or ends the stream while there is none.</summary>
/// <remarks>Created once per client, so a stream panel opened before the first connection, or kept open over a reconnect, works with the next one.
/// <see cref="Bind"/> runs on the UI thread, as do the <see cref="IStreamSource"/> calls.</remarks>
public sealed class ClientStreamSource : IStreamSource
{
	/// <summary>Stores the connection's stream, or null while disconnected.</summary>
	private RemoteStreamSource? _current;

	/// <summary>Raised for each frame of the current connection's stream.</summary>
	public event Action<StreamFrame>? FrameReceived;

	/// <summary>Raised when the stream ends, also when the connection closes under it.</summary>
	public event Action<string>? Ended;

	/// <summary>Raised when an agent acts in the window the current connection's stream shows.</summary>
	public event Action<AgentWindowUse>? AgentUsed;

	/// <summary>Gets whether the connected host grants control; false while disconnected.</summary>
	public bool CanControl => _current?.CanControl ?? false;

	/// <summary>Switches to a new connection's stream, or to none with null; a stream that ran on the old one ends.</summary>
	/// <param name="stream">The new connection's stream, or null.</param>
	public void Bind( RemoteStreamSource? stream )
	{
		var previous = _current;
		if ( previous == stream )
			return;

		if ( previous is not null )
		{
			previous.FrameReceived -= RaiseFrame;
			previous.Ended -= RaiseEnded;
			previous.AgentUsed -= RaiseAgentUsed;
			previous.Stop();
		}
		_current = stream;
		if ( stream is not null )
		{
			stream.FrameReceived += RaiseFrame;
			stream.Ended += RaiseEnded;
			stream.AgentUsed += RaiseAgentUsed;
		}
		else if ( previous is not null )
			Ended?.Invoke( "The connection to the host closed." );
	}

	/// <summary>Lists the host's windows.</summary>
	/// <exception cref="IOException">The client is not connected.</exception>
	public Task<IReadOnlyList<CaptureWindow>> ListAsync( CancellationToken cancellation ) =>
		(_current ?? throw new IOException( "Not connected to the host." )).ListAsync( cancellation );

	/// <summary>Starts streaming a window of the host.</summary>
	/// <exception cref="IOException">The client is not connected.</exception>
	public Task<string?> StartAsync( long window, CaptureOptions options, CancellationToken cancellation ) =>
		(_current ?? throw new IOException( "Not connected to the host." )).StartAsync( window, options, cancellation );

	/// <summary>Caps the running stream's frames; ignored while disconnected.</summary>
	public void View( int maxWidth, int maxHeight ) => _current?.View( maxWidth, maxHeight );

	/// <summary>Stops the running stream, if any.</summary>
	public void Stop() => _current?.Stop();

	/// <summary>Acknowledges the last frame.</summary>
	public void Ack() => _current?.Ack();

	/// <summary>Sends input to the streamed window; ignored while disconnected.</summary>
	public void Input( StreamInput input ) => _current?.Input( input );

	/// <summary>Passes a frame on.</summary>
	private void RaiseFrame( StreamFrame frame ) => FrameReceived?.Invoke( frame );

	/// <summary>Passes the end of a stream on.</summary>
	private void RaiseEnded( string message ) => Ended?.Invoke( message );

	/// <summary>Passes an agent's action on.</summary>
	private void RaiseAgentUsed( AgentWindowUse use ) => AgentUsed?.Invoke( use );
}
