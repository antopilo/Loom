namespace Lokn.Modules.Remote;

/// <summary>Streams a window of this PC in process, with the same host classes the gateway uses, so the stream panel works without a network.</summary>
/// <remarks>Input drives this PC's real pointer and keyboard: a click in the panel moves the pointer onto the streamed window and raises it. Watch locally; drive from another PC.</remarks>
public sealed class LocalStreamSource : IStreamSource, IDisposable
{
	/// <summary>Stores the checks' capturer factory by window handle, or null for <see cref="WindowCapturers.Open"/> on a window that exists.</summary>
	private readonly Func<long, IWindowCapturer>? _open;

	/// <summary>Stores the running session, or null.</summary>
	private CaptureSession? _session;

	/// <summary>Stores the injector of the running session's window, or null.</summary>
	private InputInjector? _injector;

	/// <summary>Stores the streamed window's handle, or 0 while none streams.</summary>
	private long _window;

	/// <summary>Creates the source.</summary>
	/// <param name="open">The checks' capturer factory by window handle, or null for this PC's windows.</param>
	public LocalStreamSource( Func<long, IWindowCapturer>? open = null ) => _open = open;

	/// <inheritdoc/>
	public event Action<StreamFrame>? FrameReceived;

	/// <inheritdoc/>
	public event Action<string>? Ended;

	/// <inheritdoc/>
	public event Action<AgentWindowUse>? AgentUsed;

	/// <summary>Gets true: an in-process source always controls its own desktop.</summary>
	public bool CanControl => true;

	/// <summary>Lists this PC's windows off the calling thread.</summary>
	public Task<IReadOnlyList<CaptureWindow>> ListAsync( CancellationToken cancellation ) => Task.Run( WindowCatalog.List, cancellation );

	/// <summary>Starts capturing a window of this PC, and follows what agents do in it (<see cref="AgentWindowActivity"/>).</summary>
	/// <exception cref="InvalidOperationException">The window does not exist.</exception>
	public Task StartAsync( long window, CaptureOptions options, CancellationToken cancellation )
	{
		Stop();
		var handle = (nint)window;
		if ( _open is null && !StreamNative.IsWindow( handle ) )
			throw new InvalidOperationException( "The window was closed." );

		CaptureSession? session = null;
		session = new( _open?.Invoke( window ) ?? WindowCapturers.Open( handle ), options, frame => FrameReceived?.Invoke( frame ), error =>
		{
			// Only the session still running reports its end; a stopped one ended on purpose.
			if ( Interlocked.CompareExchange( ref _session, null, session ) == session )
				Ended?.Invoke( error.Message );
		} );
		_session = session;
		_injector = new( handle );
		Interlocked.Exchange( ref _window, window );
		AgentWindowActivity.Used += RaiseAgentUsed;
		session.Start();
		return Task.CompletedTask;
	}

	/// <summary>Stops the running capture and stops following agents.</summary>
	public void Stop()
	{
		AgentWindowActivity.Used -= RaiseAgentUsed;
		Interlocked.Exchange( ref _window, 0 );
		Interlocked.Exchange( ref _session, null )?.Dispose();
		_injector = null;
	}

	/// <summary>Acknowledges the last frame.</summary>
	public void Ack() => Volatile.Read( ref _session )?.Ack();

	/// <summary>Sends the event to the streamed window, in the coordinates of the last frame.</summary>
	public void Input( StreamInput input )
	{
		if ( Volatile.Read( ref _session ) is not { } session || _injector is not { } injector )
			return;

		var (width, height) = session.FrameSize;
		_ = injector.Send( input, width, height );
	}

	/// <summary>Stops the running capture.</summary>
	public void Dispose() => Stop();

	/// <summary>Passes on what an agent did in the streamed window.</summary>
	private void RaiseAgentUsed( AgentWindowUse use )
	{
		if ( use.Hwnd == Interlocked.Read( ref _window ) )
			AgentUsed?.Invoke( use );
	}
}
