using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>Reaches a paired host's disk, processes and terminals over the workspace frames of docs/remote.md.</summary>
/// <remarks>
/// Construct it before <see cref="RpcPeer.Start"/>: the constructor registers the <c>fs.changed</c>, <c>pty.out</c> and <c>pty.exit</c>
/// push handlers. Paths are the host's and pass through unchanged. <see cref="Watch"/> and <see cref="OpenPty"/> return at once; a watch
/// the host refuses reports <see cref="WorkspaceChange.Lost"/>, and a terminal the host cannot start writes the reason and exits with -1.
/// When the connection closes, watches report <see cref="WorkspaceChange.Lost"/>, terminals exit with -1 and pending calls fail with
/// <see cref="IOException"/>. The host's errors arrive as <see cref="IOException"/>, a view-only device's as
/// <see cref="UnauthorizedAccessException"/>, and a program <c>proc.run</c> could not start as <see cref="Win32Exception"/>.
/// </remarks>
public sealed class RemoteWorkspaceHost : IWorkspaceHost, IDisposable
{
	/// <summary>The connection to the host.</summary>
	private readonly RpcPeer _peer;

	/// <summary>Guards the tables below and <see cref="_isClosed"/>.</summary>
	private readonly Lock _gate = new();

	/// <summary>The watches the host confirmed, by id.</summary>
	private readonly Dictionary<long, RemoteWatch> _watches = [];

	/// <summary>Every watch not yet disposed, confirmed or not, to report the close to.</summary>
	private readonly HashSet<RemoteWatch> _openWatches = [];

	/// <summary>The terminals the host confirmed, by id, until they exit.</summary>
	private readonly Dictionary<long, RemotePty> _ptys = [];

	/// <summary>Every terminal that has not exited, confirmed or not, to report the close to.</summary>
	private readonly HashSet<RemotePty> _livePtys = [];

	/// <summary>Terminal pushes that arrived before the <c>pty.open</c> answer, by id.</summary>
	private readonly Dictionary<long, List<JsonObject>> _early = [];

	/// <summary>Whether the connection closed or this host was disposed.</summary>
	private bool _isClosed;

	/// <summary>Creates the host and registers its push handlers; call before the peer starts.</summary>
	/// <param name="peer">The connection to the host.</param>
	public RemoteWorkspaceHost( RpcPeer peer )
	{
		_peer = peer;
		peer.OnPush( "fs.changed", ReadChanged );
		peer.OnPush( "pty.out", ReadPtyPush );
		peer.OnPush( "pty.exit", ReadPtyPush );
		peer.Closed += OnClosed;
	}

	/// <summary>Runs a program on the host through <c>proc.run</c>; cancelling stops waiting for it.</summary>
	public async Task<WorkspaceProcessResult> RunAsync( string file, IReadOnlyList<string> arguments, string directory, string? input, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken )
	{
		var frame = Frame.Create( "proc.run" );
		frame["file"] = file;
		frame["args"] = Frame.BuildStrings( arguments );
		frame["cwd"] = directory;
		if ( input is not null )
			frame["input"] = input;
		if ( WorkspaceFrames.BuildEnvironment( environment ) is { } variables )
			frame["env"] = variables;

		JsonObject answer;
		try
		{
			answer = await _peer.RequestAsync( frame, cancellationToken ).ConfigureAwait( false );
		}
		catch ( RemoteException error ) when ( error.Code == RemoteErrors.Failed )
		{
			throw new Win32Exception( error.Message );
		}
		catch ( RemoteException error )
		{
			throw TranslateError( error );
		}
		return new( (int)( Frame.ReadLong( answer, "code" ) ?? -1 ), Frame.ReadString( answer, "out" ) ?? "", Frame.ReadString( answer, "err" ) ?? "" );
	}

	/// <summary>Describes a host path through <c>fs.stat</c>.</summary>
	public async Task<WorkspaceStat> StatAsync( string path, CancellationToken cancellationToken )
	{
		var answer = await CallAsync( BuildPathFrame( "fs.stat", path ), cancellationToken ).ConfigureAwait( false );
		return Frame.ReadBool( answer, "exists" ) == true
			? new WorkspaceStat( true, Frame.ReadBool( answer, "dir" ) == true, Frame.ReadLong( answer, "size" ) ?? 0, WorkspaceFrames.ReadMtime( Frame.ReadLong( answer, "mtime" ) ?? 0 ) )
			: WorkspaceStat.Missing;
	}

	/// <summary>Lists a host folder through <c>fs.list</c>.</summary>
	public async Task<IReadOnlyList<WorkspaceEntry>> ListAsync( string path, CancellationToken cancellationToken )
	{
		var answer = await CallAsync( BuildPathFrame( "fs.list", path ), cancellationToken ).ConfigureAwait( false );
		var entries = new List<WorkspaceEntry>();
		if ( answer["entries"] is JsonArray items )
		{
			foreach ( var item in items )
			{
				if ( item is JsonObject entry && Frame.ReadString( entry, "name" ) is { } name )
					entries.Add( new( name, Frame.ReadBool( entry, "dir" ) == true, Frame.ReadLong( entry, "size" ) ?? 0, WorkspaceFrames.ReadMtime( Frame.ReadLong( entry, "mtime" ) ?? 0 ) ) );
			}
		}
		return entries;
	}

	/// <summary>Reads a host file through <c>fs.read</c>.</summary>
	public async Task<byte[]> ReadAsync( string path, CancellationToken cancellationToken )
	{
		var answer = await CallAsync( BuildPathFrame( "fs.read", path ), cancellationToken ).ConfigureAwait( false );
		try
		{
			return Convert.FromBase64String( Frame.ReadString( answer, "data" ) ?? "" );
		}
		catch ( FormatException error )
		{
			throw new IOException( "The host sent a file that is not base64.", error );
		}
	}

	/// <summary>Writes a host file through <c>fs.write</c>.</summary>
	public Task WriteAsync( string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken )
	{
		var frame = BuildPathFrame( "fs.write", path );
		frame["data"] = Convert.ToBase64String( data.Span );
		return CallAsync( frame, cancellationToken );
	}

	/// <summary>Deletes a host path through <c>fs.delete</c>.</summary>
	public Task DeleteAsync( string path, CancellationToken cancellationToken ) => CallAsync( BuildPathFrame( "fs.delete", path ), cancellationToken );

	/// <summary>Moves a host path through <c>fs.move</c>.</summary>
	public Task MoveAsync( string path, string to, CancellationToken cancellationToken )
	{
		var frame = BuildPathFrame( "fs.move", path );
		frame["to"] = to;
		return CallAsync( frame, cancellationToken );
	}

	/// <summary>Creates a host folder through <c>fs.mkdir</c>.</summary>
	public Task CreateDirectoryAsync( string path, CancellationToken cancellationToken ) => CallAsync( BuildPathFrame( "fs.mkdir", path ), cancellationToken );

	/// <summary>Follows a host folder through <c>fs.watch</c>; the handle returns at once and sends <c>fs.unwatch</c> when disposed.</summary>
	/// <remarks>A folder the host cannot watch, and the connection's close, report <see cref="WorkspaceChange.Lost"/> for the folder.</remarks>
	public IDisposable Watch( string path, bool recursive, Action<string, WorkspaceChange> changed )
	{
		var watch = new RemoteWatch( this, path, changed );
		lock ( _gate )
		{
			if ( _isClosed )
			{
				changed( path, WorkspaceChange.Lost );
				return watch;
			}
			_openWatches.Add( watch );
		}

		var frame = BuildPathFrame( "fs.watch", path );
		frame["recursive"] = recursive;
		_ = StartWatchAsync( watch, frame );
		return watch;
	}

	/// <summary>Starts a program in a host pseudoterminal through <c>pty.open</c>; the handle returns at once and queues input until the host answers.</summary>
	public IWorkspacePty OpenPty( string file, IReadOnlyList<string> arguments, string? directory, int columns, int rows, IReadOnlyDictionary<string, string>? environment, Action<string> output, Action<int> exited )
	{
		var frame = Frame.Create( "pty.open" );
		frame["file"] = file;
		frame["args"] = Frame.BuildStrings( arguments );
		if ( directory is not null )
			frame["cwd"] = directory;
		frame["cols"] = columns;
		frame["rows"] = rows;
		if ( WorkspaceFrames.BuildEnvironment( environment ) is { } variables )
			frame["env"] = variables;

		var opened = new TaskCompletionSource<long>( TaskCreationOptions.RunContinuationsAsynchronously );
		var pty = new RemotePty( this, opened.Task, output, exited );
		lock ( _gate )
		{
			if ( _isClosed )
			{
				opened.SetException( new IOException( "The connection closed." ) );
				pty.Exit( -1 );
				return pty;
			}
			_livePtys.Add( pty );
		}
		_ = StartPtyAsync( pty, frame, opened );
		return pty;
	}

	/// <summary>Stops following pushes and ends this host's watches and terminals on the host; the peer stays open.</summary>
	public void Dispose()
	{
		_peer.Closed -= OnClosed;
		RemoteWatch[] watches;
		RemotePty[] ptys;
		lock ( _gate )
		{
			if ( _isClosed )
				return;
			_isClosed = true;
			watches = [.. _openWatches];
			ptys = [.. _livePtys];
		}
		foreach ( var watch in watches )
			watch.Dispose();
		foreach ( var pty in ptys )
			pty.Dispose();
	}

	/// <summary>Builds a request with one <c>path</c> field.</summary>
	private static JsonObject BuildPathFrame( string type, string path )
	{
		var frame = Frame.Create( type );
		frame["path"] = path;
		return frame;
	}

	/// <summary>Turns a host error into the exception <see cref="IWorkspaceHost"/> callers expect.</summary>
	private static Exception TranslateError( RemoteException error ) => error.Code == RemoteErrors.Scope
		? new UnauthorizedAccessException( error.Message, error )
		: new IOException( error.Message, error );

	/// <summary>Sends a request and returns its answer, with host errors translated.</summary>
	private async Task<JsonObject> CallAsync( JsonObject frame, CancellationToken cancellationToken )
	{
		try
		{
			return await _peer.RequestAsync( frame, cancellationToken ).ConfigureAwait( false );
		}
		catch ( RemoteException error )
		{
			throw TranslateError( error );
		}
	}

	/// <summary>Sends <c>fs.watch</c> and files the watch under the host's id, or reports it lost.</summary>
	private async Task StartWatchAsync( RemoteWatch watch, JsonObject frame )
	{
		long id;
		try
		{
			id = Frame.ReadLong( await CallAsync( frame, CancellationToken.None ).ConfigureAwait( false ), "watch" ) ?? throw new IOException( "The host sent no watch id." );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			if ( ForgetWatch( watch ) )
				watch.Report( watch.Path, WorkspaceChange.Lost );
			return;
		}

		bool isWanted;
		lock ( _gate )
		{
			isWanted = _openWatches.Contains( watch );
			if ( isWanted )
				_watches[id] = watch;
		}
		watch.Id = id;
		if ( !isWanted )
			SendUnwatch( id );
	}

	/// <summary>Forgets a watch; returns whether it was still open.</summary>
	private bool ForgetWatch( RemoteWatch watch )
	{
		lock ( _gate )
		{
			if ( watch.Id is { } id )
				_watches.Remove( id );
			return _openWatches.Remove( watch );
		}
	}

	/// <summary>Sends <c>fs.unwatch</c> without waiting for the answer.</summary>
	private void SendUnwatch( long id )
	{
		var frame = Frame.Create( "fs.unwatch" );
		frame["watch"] = id;
		_ = SendQuietlyAsync( frame );
	}

	/// <summary>Sends a request whose answer does not matter; a closed connection is fine.</summary>
	private async Task SendQuietlyAsync( JsonObject frame )
	{
		try
		{
			await _peer.RequestAsync( frame, CancellationToken.None ).ConfigureAwait( false );
		}
		catch ( Exception error ) when ( error is IOException or RemoteException or ObjectDisposedException or InvalidOperationException )
		{
			// The host already let go, or the connection is gone and took the watch or terminal with it.
		}
	}

	/// <summary>Sends <c>pty.open</c> and files the terminal under the host's id, with any output that came first.</summary>
	private async Task StartPtyAsync( RemotePty pty, JsonObject frame, TaskCompletionSource<long> opened )
	{
		long id;
		try
		{
			id = Frame.ReadLong( await CallAsync( frame, CancellationToken.None ).ConfigureAwait( false ), "pty" ) ?? throw new IOException( "The host sent no terminal id." );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			opened.TrySetException( error );
			pty.Fail( error.Message );
			return;
		}

		lock ( _gate )
		{
			_early.Remove( id, out var early );
			if ( _livePtys.Contains( pty ) )
			{
				_ptys[id] = pty;
				pty.Id = id;
				// Output the host sent before its answer goes first; holding the gate keeps later pushes behind it.
				foreach ( var push in early ?? [] )
					pty.Read( push );
			}
		}
		opened.TrySetResult( id );
	}

	/// <summary>Delivers an <c>fs.changed</c> push to its watch.</summary>
	private void ReadChanged( JsonObject frame )
	{
		RemoteWatch? watch;
		lock ( _gate )
			watch = Frame.ReadLong( frame, "watch" ) is { } id && _watches.TryGetValue( id, out var found ) ? found : null;

		if ( watch is not null && Frame.ReadString( frame, "path" ) is { } path )
			watch.Report( path, WorkspaceFrames.ReadKind( Frame.ReadString( frame, "kind" ) ) );
	}

	/// <summary>Delivers a <c>pty.out</c> or <c>pty.exit</c> push to its terminal, or holds it until <c>pty.open</c> answers.</summary>
	private void ReadPtyPush( JsonObject frame )
	{
		if ( Frame.ReadLong( frame, "pty" ) is not { } id )
			return;

		lock ( _gate )
		{
			if ( _ptys.TryGetValue( id, out var pty ) )
				pty.Read( frame );
			else if ( !_isClosed )
			{
				if ( !_early.TryGetValue( id, out var early ) )
					_early[id] = early = [];
				early.Add( frame );
			}
		}
	}

	/// <summary>Forgets a terminal that exited.</summary>
	private void ForgetPty( RemotePty pty )
	{
		lock ( _gate )
		{
			_livePtys.Remove( pty );
			if ( pty.Id is { } id )
				_ptys.Remove( id );
		}
	}

	/// <summary>Reports the connection's close to every watch and terminal.</summary>
	private void OnClosed( Exception? _ )
	{
		RemoteWatch[] watches;
		RemotePty[] ptys;
		lock ( _gate )
		{
			_isClosed = true;
			watches = [.. _openWatches];
			ptys = [.. _livePtys];
			_openWatches.Clear();
			_watches.Clear();
			_early.Clear();
		}
		foreach ( var watch in watches )
			watch.Report( watch.Path, WorkspaceChange.Lost );
		foreach ( var pty in ptys )
			pty.Exit( -1 );
	}

	/// <summary>One folder followed on the host.</summary>
	/// <param name="owner">The host it came from.</param>
	/// <param name="path">The folder.</param>
	/// <param name="changed">Receives the changes.</param>
	private sealed class RemoteWatch( RemoteWorkspaceHost owner, string path, Action<string, WorkspaceChange> changed ) : IDisposable
	{
		/// <summary>Gets the folder.</summary>
		public string Path => path;

		/// <summary>Gets or sets the host's id once it answered.</summary>
		public long? Id { get; set; }

		/// <summary>Passes one change to the holder.</summary>
		public void Report( string changedPath, WorkspaceChange kind ) => changed( changedPath, kind );

		/// <summary>Stops following the folder and tells the host.</summary>
		public void Dispose()
		{
			if ( owner.ForgetWatch( this ) && Id is { } id )
				owner.SendUnwatch( id );
		}
	}

	/// <summary>One program running in a host pseudoterminal.</summary>
	/// <param name="owner">The host it runs on.</param>
	/// <param name="opened">Finishes with the host's id once <c>pty.open</c> answers.</param>
	/// <param name="output">Receives decoded output.</param>
	/// <param name="exited">Receives the exit code once.</param>
	private sealed class RemotePty( RemoteWorkspaceHost owner, Task<long> opened, Action<string> output, Action<int> exited ) : IWorkspacePty
	{
		/// <summary>Decodes output, keeping a character split across pushes.</summary>
		private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

		/// <summary>Guards <see cref="_requests"/>.</summary>
		private readonly Lock _gate = new();

		/// <summary>The last request queued; input, resizes and the close reach the host in order.</summary>
		private Task _requests = opened;

		/// <summary>Whether the exit was reported.</summary>
		private int _hasExited;

		/// <summary>Whether <see cref="Dispose"/> ran.</summary>
		private int _isDisposed;

		/// <summary>Gets the host's id once it answered.</summary>
		public long? Id { get; set; }

		/// <summary>Sends text through <c>pty.in</c>.</summary>
		public void Write( string text )
		{
			if ( text.Length > 0 )
				Send( "pty.in", frame => frame["data"] = text );
		}

		/// <summary>Resizes through <c>pty.resize</c>.</summary>
		public void Resize( int columns, int rows ) => Send( "pty.resize", frame =>
		{
			frame["cols"] = columns;
			frame["rows"] = rows;
		} );

		/// <summary>Ends the program through <c>pty.close</c>; the exit still arrives as <c>pty.exit</c>.</summary>
		public void Dispose()
		{
			if ( Interlocked.Exchange( ref _isDisposed, 1 ) == 0 )
				Send( "pty.close", _ => { } );
		}

		/// <summary>Handles a <c>pty.out</c> or <c>pty.exit</c> push; the owner calls it in arrival order.</summary>
		public void Read( JsonObject frame )
		{
			if ( Frame.ReadType( frame ) == "pty.exit" )
			{
				Exit( (int)( Frame.ReadLong( frame, "code" ) ?? -1 ) );
				return;
			}

			byte[] bytes;
			try
			{
				bytes = Convert.FromBase64String( Frame.ReadString( frame, "data" ) ?? "" );
			}
			catch ( FormatException )
			{
				return;
			}
			var characters = new char[Encoding.UTF8.GetMaxCharCount( bytes.Length )];
			var length = _decoder.GetChars( bytes, 0, bytes.Length, characters, 0, flush: false );
			if ( length > 0 )
				output( new string( characters, 0, length ) );
		}

		/// <summary>Shows why the host could not start the program, then exits with -1.</summary>
		public void Fail( string message )
		{
			if ( Volatile.Read( ref _hasExited ) == 0 )
				output( message + "\r\n" );
			Exit( -1 );
		}

		/// <summary>Reports the exit once and forgets the terminal.</summary>
		public void Exit( int code )
		{
			if ( Interlocked.Exchange( ref _hasExited, 1 ) == 1 )
				return;
			owner.ForgetPty( this );
			exited( code );
		}

		/// <summary>Queues a request after the earlier ones, once the host named the terminal.</summary>
		private void Send( string type, Action<JsonObject> fill )
		{
			lock ( _gate )
				_requests = SendAfterAsync( _requests, type, fill );
		}

		/// <summary>Sends one request after the previous one finished; nothing is sent for a terminal that never opened or already exited.</summary>
		private async Task SendAfterAsync( Task previous, string type, Action<JsonObject> fill )
		{
			await previous.ConfigureAwait( ConfigureAwaitOptions.SuppressThrowing );
			if ( !opened.IsCompletedSuccessfully || Volatile.Read( ref _hasExited ) == 1 )
				return;

			var frame = Frame.Create( type );
			frame["pty"] = opened.Result;
			fill( frame );
			await owner.SendQuietlyAsync( frame ).ConfigureAwait( false );
		}
	}
}
