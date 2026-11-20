using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Lokn.Modules.Remote;

/// <summary>Speaks the request, answer and push protocol over one <see cref="IFrameConnection"/>: numbers requests, matches answers, answers incoming requests through handlers and delivers pushes.</summary>
/// <remarks>
/// Register handlers, then call <see cref="Start"/>; one reader loop then runs on the thread pool. Request handlers run concurrently, at most
/// <see cref="MaxRunningRequests"/> at a time (more are answered <c>busy</c>), and may make requests of their own; push handlers run in arrival
/// order on the reader loop, so they must not block. Neither runs on the UI thread.
/// </remarks>
/// <param name="connection">The connection; the peer owns it and disposes it when it closes.</param>
public sealed class RpcPeer( IFrameConnection connection ) : IAsyncDisposable
{
	/// <summary>The most incoming requests of one connection that run at once; more are refused with <see cref="RemoteErrors.Busy"/>.</summary>
	public const int MaxRunningRequests = 64;

	/// <summary>Holds the request handlers by frame type.</summary>
	private readonly ConcurrentDictionary<string, Func<JsonObject, Task<JsonObject?>>> _handlers = new( StringComparer.Ordinal );

	/// <summary>Holds the push handlers by frame type.</summary>
	private readonly ConcurrentDictionary<string, Action<JsonObject>> _pushHandlers = new( StringComparer.Ordinal );

	/// <summary>Holds the requests awaiting an answer by their number.</summary>
	private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();

	/// <summary>Ends the reader loop when the peer is disposed.</summary>
	private readonly CancellationTokenSource _stopping = new();

	/// <summary>Keeps this side's own requests in flight below <see cref="MaxRunningRequests"/>, so the other side never needs to answer <c>busy</c>; more wait in order.</summary>
	private readonly SemaphoreSlim _requesting = new( MaxRunningRequests, MaxRunningRequests );

	/// <summary>Stores the last request number used.</summary>
	private long _lastId;

	/// <summary>Is 1 once the peer closed, so it closes once.</summary>
	private int _closed;

	/// <summary>Counts the incoming requests whose handlers run.</summary>
	private int _running;

	/// <summary>Stores the reader loop once started.</summary>
	private Task? _reader;

	/// <summary>Raised once when the connection closes, with the exception that closed it or null when the other side closed it; on the reader's thread.</summary>
	public event Action<Exception?>? Closed;

	/// <summary>Gets whether the connection closed.</summary>
	public bool IsClosed => Volatile.Read( ref _closed ) == 1;

	/// <summary>Gets the connection the peer speaks over.</summary>
	public IFrameConnection Connection => connection;

	/// <summary>Answers incoming requests of a type: the handler's fields go into the <c>ok</c> answer; a <see cref="RemoteException"/> becomes an <c>err</c> with its code, any other exception <c>failed</c>.</summary>
	public RpcPeer On( string type, Func<JsonObject, Task<JsonObject?>> handler )
	{
		_handlers[type] = handler;
		return this;
	}

	/// <summary>Delivers incoming pushes of a type, in arrival order.</summary>
	public RpcPeer OnPush( string type, Action<JsonObject> handler )
	{
		_pushHandlers[type] = handler;
		return this;
	}

	/// <summary>Starts the reader loop; call once, after registering handlers.</summary>
	public void Start()
	{
		if ( _reader is not null )
			throw new InvalidOperationException( "The peer has already started." );
		_reader = Task.Run( ReadAsync );
	}

	/// <summary>Sends a request, numbering it, and returns its <c>ok</c> answer, whose fields are the handler's; while <see cref="MaxRunningRequests"/> of
	/// this side's requests await answers, it first waits for one to end.</summary>
	/// <exception cref="RemoteException">The other side answered <c>err</c>.</exception>
	/// <exception cref="IOException">The connection closed before the answer.</exception>
	public async Task<JsonObject> RequestAsync( JsonObject frame, CancellationToken cancellationToken )
	{
		await _requesting.WaitAsync( cancellationToken ).ConfigureAwait( false );
		try
		{
			return await SendRequestAsync( frame, cancellationToken ).ConfigureAwait( false );
		}
		finally
		{
			_requesting.Release();
		}
	}

	/// <summary>Numbers and sends a request and awaits its answer.</summary>
	private async Task<JsonObject> SendRequestAsync( JsonObject frame, CancellationToken cancellationToken )
	{
		var id = Interlocked.Increment( ref _lastId );
		frame["id"] = id;
		var answer = new TaskCompletionSource<JsonObject>( TaskCreationOptions.RunContinuationsAsynchronously );
		if ( IsClosed )
			throw new IOException( "The connection closed." );
		_pending[id] = answer;
		// A close between the check and the registration has already failed the others; fail this one too.
		if ( IsClosed )
			FailPending();
		try
		{
			try
			{
				await connection.SendAsync( frame ).ConfigureAwait( false );
			}
			catch ( Exception exception ) when ( IsClosed && exception is not IOException )
			{
				throw new IOException( "The connection closed.", exception );
			}
			using ( cancellationToken.Register( () => answer.TrySetCanceled( cancellationToken ) ) )
				return await answer.Task.ConfigureAwait( false );
		}
		finally
		{
			_pending.TryRemove( id, out _ );
		}
	}

	/// <summary>Sends a push, which has no number and gets no answer.</summary>
	public Task PushAsync( JsonObject frame ) => connection.SendAsync( frame );

	/// <summary>Closes the connection, which ends the reader loop; safe to call from <see cref="Closed"/> and handlers.</summary>
	public async ValueTask DisposeAsync()
	{
		await _stopping.CancelAsync().ConfigureAwait( false );
		await CloseAsync( null ).ConfigureAwait( false );
	}

	/// <summary>Reads frames until the connection closes, completing answers, starting request handlers and delivering pushes.</summary>
	private async Task ReadAsync()
	{
		Exception? failure = null;
		try
		{
			while ( await connection.ReceiveAsync( _stopping.Token ).ConfigureAwait( false ) is { } frame )
				Dispatch( frame );
		}
		catch ( OperationCanceledException ) when ( _stopping.IsCancellationRequested )
		{
		}
		catch ( Exception exception )
		{
			failure = exception;
		}
		await CloseAsync( failure ).ConfigureAwait( false );
	}

	/// <summary>Routes one incoming frame by its type and fields.</summary>
	private void Dispatch( JsonObject frame )
	{
		var type = Frame.ReadType( frame );
		if ( type is Frame.Ok or Frame.Err && Frame.ReadRe( frame ) is { } re )
		{
			if ( _pending.TryRemove( re, out var answer ) )
			{
				if ( type == Frame.Ok )
					answer.TrySetResult( frame );
				else
					answer.TrySetException( new RemoteException( Frame.ReadString( frame, "code" ) ?? RemoteErrors.Failed, Frame.ReadString( frame, "message" ) ?? "The request failed." ) );
			}
			return;
		}

		if ( Frame.ReadId( frame ) is { } id )
		{
			if ( Interlocked.Increment( ref _running ) > MaxRunningRequests )
			{
				Interlocked.Decrement( ref _running );
				_ = RefuseAsync( id );
				return;
			}
			_ = AnswerAsync( id, type, frame );
			return;
		}

		if ( _pushHandlers.TryGetValue( type, out var push ) )
			push( frame );
	}

	/// <summary>Runs the handler of one incoming request and sends its answer.</summary>
	private async Task AnswerAsync( long id, string type, JsonObject frame )
	{
		JsonObject answer;
		try
		{
			if ( !_handlers.TryGetValue( type, out var handler ) )
				throw new RemoteException( RemoteErrors.BadRequest, $"Unknown request \"{type}\"." );
			answer = Frame.BuildOk( id, await handler( frame ).ConfigureAwait( false ) );
		}
		catch ( RemoteException exception )
		{
			answer = Frame.BuildErr( id, exception.Code, exception.Message );
		}
		catch ( Exception exception )
		{
			answer = Frame.BuildErr( id, RemoteErrors.Failed, exception.Message );
		}
		finally
		{
			Interlocked.Decrement( ref _running );
		}

		await SendAnswerAsync( id, answer ).ConfigureAwait( false );
	}

	/// <summary>Answers a request that arrived while <see cref="MaxRunningRequests"/> others ran.</summary>
	private Task RefuseAsync( long id ) =>
		SendAnswerAsync( id, Frame.BuildErr( id, RemoteErrors.Busy, $"The other side already runs {MaxRunningRequests} requests of this connection." ) );

	/// <summary>Sends a request's answer; an answer too large for the carrier becomes a <c>failed</c> one, and any other failure closes the peer.</summary>
	private async Task SendAnswerAsync( long id, JsonObject answer )
	{
		try
		{
			try
			{
				await connection.SendAsync( answer ).ConfigureAwait( false );
			}
			catch ( InvalidDataException exception )
			{
				// The answer is too large for the carrier; the request still gets one.
				await connection.SendAsync( Frame.BuildErr( id, RemoteErrors.Failed, exception.Message ) ).ConfigureAwait( false );
			}
		}
		catch ( Exception exception )
		{
			// A send that fails leaves the connection unusable, so the peer closes with the reason.
			await CloseAsync( IsClosed ? null : exception ).ConfigureAwait( false );
		}
	}

	/// <summary>Closes the connection once, fails every pending request and raises <see cref="Closed"/>.</summary>
	private async Task CloseAsync( Exception? failure )
	{
		if ( Interlocked.Exchange( ref _closed, 1 ) == 1 )
			return;
		await connection.DisposeAsync().ConfigureAwait( false );
		FailPending();
		Closed?.Invoke( failure );
	}

	/// <summary>Fails every request still awaiting an answer.</summary>
	private void FailPending()
	{
		foreach ( var id in _pending.Keys )
		{
			if ( _pending.TryRemove( id, out var answer ) )
				answer.TrySetException( new IOException( "The connection closed." ) );
		}
	}
}
