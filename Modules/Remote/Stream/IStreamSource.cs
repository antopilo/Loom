namespace Lokn.Modules.Remote;

/// <summary>Supplies a <see cref="StreamContent"/> with a host's windows, its frames and its input: in process (<see cref="LocalStreamSource"/>) or over the <c>cap.*</c> frames of docs/remote.md.</summary>
/// <remarks>One source streams one window at a time. Methods are called on the UI thread; events may be raised on any thread.</remarks>
public interface IStreamSource
{
	/// <summary>Raised for each frame. The receiver calls <see cref="Ack"/> once it has shown the frame, which lets the host send the next.</summary>
	event Action<StreamFrame>? FrameReceived;

	/// <summary>Raised when the stream ends without <see cref="Stop"/>, such as when the window closes or the connection drops, with a message for the user.</summary>
	event Action<string>? Ended;

	/// <summary>Raised when an agent acts in the streamed window, such as a click of its tools, so the panel marks it (<c>cap.agent</c>).</summary>
	event Action<AgentWindowUse>? AgentUsed;

	/// <summary>Gets whether this source may send input: the host granted the <c>control</c> scope.</summary>
	bool CanControl
	{
		get;
	}

	/// <summary>Lists the windows the host can stream (<c>cap.list</c>).</summary>
	Task<IReadOnlyList<CaptureWindow>> ListAsync( CancellationToken cancellation );

	/// <summary>Starts streaming a window (<c>cap.start</c>), stopping any stream already running.</summary>
	/// <param name="window">The window's <see cref="CaptureWindow.Hwnd"/>.</param>
	/// <param name="options">The frame rate, quality and width cap.</param>
	/// <param name="cancellation">Cancels the request.</param>
	Task StartAsync( long window, CaptureOptions options, CancellationToken cancellation );

	/// <summary>Stops the running stream (<c>cap.stop</c>); does nothing when none runs.</summary>
	void Stop();

	/// <summary>Acknowledges the last frame received (<c>cap.ack</c>).</summary>
	void Ack();

	/// <summary>Sends a pointer or key event to the streamed window (<c>cap.input</c>); ignored when <see cref="CanControl"/> is false or no stream runs.</summary>
	void Input( StreamInput input );
}
