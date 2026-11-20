using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>Serves the workspace frames of docs/remote.md (<c>proc.*</c>, <c>fs.*</c>, <c>pty.*</c>) to each connection from the host's own disk.</summary>
/// <remarks>
/// Every frame needs the <c>control</c> scope, reads included, and none reaches Loom's own keys and pairing state or a network path (<see cref="WorkspaceGuard"/>).
/// Each connection owns its watches and terminals, which end when it closes, as do its running processes; it may hold <see cref="MaxWatches"/> watches
/// and <see cref="MaxPtys"/> terminals, read files up to <see cref="MaxReadBytes"/>, and fall <see cref="MaxPendingPushes"/> pushes behind before it is dropped.
/// </remarks>
/// <param name="app">The host application; the workspace of its <see cref="LoknHosts.Local"/> host is this PC's disk in the daemon.</param>
public sealed class WorkspaceService( LoknApplication app ) : IHostService
{
	/// <summary>The largest file <c>fs.read</c> returns, so its base64 answer fits one frame.</summary>
	public const long MaxReadBytes = (Frame.MaxBytes - 64 * 1024) / 4 * 3;

	/// <summary>The most watches one connection holds at once.</summary>
	public const int MaxWatches = 32;

	/// <summary>The most terminals one connection holds at once.</summary>
	public const int MaxPtys = 16;

	/// <summary>The most pushes of one connection waiting to be sent; a connection further behind is closed.</summary>
	public const int MaxPendingPushes = 4096;

	/// <summary>Registers the workspace handlers for one connection, against the workspace the host has now.</summary>
	/// <param name="connection">The connection.</param>
	public void Register( RemoteConnection connection )
	{
		var session = new Session( connection, app.Hosts.Local.Workspace );
		var peer = connection.Peer;
		peer.On( "proc.run", session.RunAsync );
		peer.On( "fs.stat", session.StatAsync );
		peer.On( "fs.list", session.ListAsync );
		peer.On( "fs.read", session.ReadAsync );
		peer.On( "fs.write", session.WriteAsync );
		peer.On( "fs.delete", session.DeleteAsync );
		peer.On( "fs.move", session.MoveAsync );
		peer.On( "fs.mkdir", session.CreateDirectoryAsync );
		peer.On( "fs.watch", session.WatchAsync );
		peer.On( "fs.unwatch", session.UnwatchAsync );
		peer.On( "pty.open", session.OpenPtyAsync );
		peer.On( "pty.in", session.WritePtyAsync );
		peer.On( "pty.resize", session.ResizePtyAsync );
		peer.On( "pty.close", session.ClosePtyAsync );
		peer.Closed += _ => session.Dispose();
	}

	/// <summary>Holds one connection's watches, terminals and running processes.</summary>
	/// <param name="connection">The connection.</param>
	/// <param name="host">The workspace the frames reach.</param>
	private sealed class Session( RemoteConnection connection, IWorkspaceHost host ) : IDisposable
	{
		/// <summary>The answer with no fields.</summary>
		private static JsonObject? Empty => null;

		/// <summary>Cancels running processes when the connection closes.</summary>
		private readonly CancellationTokenSource _closing = new();

		/// <summary>The open watches by id.</summary>
		private readonly ConcurrentDictionary<long, IDisposable> _watches = new();

		/// <summary>The open terminals by id.</summary>
		private readonly ConcurrentDictionary<long, IWorkspacePty> _ptys = new();

		/// <summary>Guards <see cref="_pushes"/> and <see cref="_isClosed"/>.</summary>
		private readonly Lock _gate = new();

		/// <summary>The last push queued; pushes go out in the order they were made.</summary>
		private Task _pushes = Task.CompletedTask;

		/// <summary>The last watch or terminal id handed out.</summary>
		private long _lastId;

		/// <summary>Counts the pushes queued and not yet sent.</summary>
		private int _pendingPushes;

		/// <summary>Whether the connection closed; later watches and terminals are refused.</summary>
		private bool _isClosed;

		/// <summary>Answers <c>proc.run</c>; the run is killed when the connection closes.</summary>
		public async Task<JsonObject?> RunAsync( JsonObject frame )
		{
			connection.RequireControl();
			var file = WorkspaceFrames.ReadRequiredString( frame, "file" );
			var arguments = Frame.ReadStrings( frame, "args" ) ?? [];
			var directory = WorkspaceFrames.ReadRequiredString( frame, "cwd" );
			CheckCommand( file, arguments, directory );
			var result = await host.RunAsync( file, arguments, directory, Frame.ReadString( frame, "input" ), WorkspaceFrames.ReadEnvironment( frame ), _closing.Token ).ConfigureAwait( false );
			return new JsonObject { ["code"] = result.Code, ["out"] = result.Output, ["err"] = result.Error };
		}

		/// <summary>Answers <c>fs.stat</c>.</summary>
		public async Task<JsonObject?> StatAsync( JsonObject frame )
		{
			var stat = await host.StatAsync( ReadPath( frame, "path" ), _closing.Token ).ConfigureAwait( false );
			return new JsonObject { ["exists"] = stat.Exists, ["dir"] = stat.IsDirectory, ["size"] = stat.Size, ["mtime"] = WorkspaceFrames.ComputeMtime( stat.Modified ) };
		}

		/// <summary>Answers <c>fs.list</c>.</summary>
		public async Task<JsonObject?> ListAsync( JsonObject frame )
		{
			var entries = await host.ListAsync( ReadPath( frame, "path" ), _closing.Token ).ConfigureAwait( false );
			var items = new JsonArray();
			foreach ( var entry in entries )
				items.Add( new JsonObject { ["name"] = entry.Name, ["dir"] = entry.IsDirectory, ["size"] = entry.Size, ["mtime"] = WorkspaceFrames.ComputeMtime( entry.Modified ) } );
			return new JsonObject { ["entries"] = items };
		}

		/// <summary>Answers <c>fs.read</c>; a file larger than <see cref="MaxReadBytes"/> is refused before it is read.</summary>
		public async Task<JsonObject?> ReadAsync( JsonObject frame )
		{
			var path = ReadPath( frame, "path" );
			var stat = await host.StatAsync( path, _closing.Token ).ConfigureAwait( false );
			if ( stat.Size > MaxReadBytes )
				throw TooLarge();
			var bytes = await host.ReadAsync( path, _closing.Token ).ConfigureAwait( false );
			if ( bytes.Length > MaxReadBytes )
				throw TooLarge();
			return new JsonObject { ["data"] = Convert.ToBase64String( bytes ) };
		}

		/// <summary>Answers <c>fs.write</c>.</summary>
		public async Task<JsonObject?> WriteAsync( JsonObject frame )
		{
			var path = ReadPath( frame, "path" );
			await host.WriteAsync( path, WorkspaceFrames.ReadData( frame, "data" ), _closing.Token ).ConfigureAwait( false );
			return Empty;
		}

		/// <summary>Answers <c>fs.delete</c>.</summary>
		public async Task<JsonObject?> DeleteAsync( JsonObject frame )
		{
			await host.DeleteAsync( ReadPath( frame, "path", true ), _closing.Token ).ConfigureAwait( false );
			return Empty;
		}

		/// <summary>Answers <c>fs.move</c>.</summary>
		public async Task<JsonObject?> MoveAsync( JsonObject frame )
		{
			await host.MoveAsync( ReadPath( frame, "path", true ), ReadPath( frame, "to", true ), _closing.Token ).ConfigureAwait( false );
			return Empty;
		}

		/// <summary>Answers <c>fs.mkdir</c>.</summary>
		public async Task<JsonObject?> CreateDirectoryAsync( JsonObject frame )
		{
			await host.CreateDirectoryAsync( ReadPath( frame, "path" ), _closing.Token ).ConfigureAwait( false );
			return Empty;
		}

		/// <summary>Answers <c>fs.watch</c> and pushes <c>fs.changed</c> for each change until <c>fs.unwatch</c> or the close.</summary>
		public Task<JsonObject?> WatchAsync( JsonObject frame )
		{
			var path = ReadPath( frame, "path" );
			var recursive = Frame.ReadBool( frame, "recursive" ) ?? false;
			if ( _watches.Count >= MaxWatches )
				throw new RemoteException( RemoteErrors.Failed, $"A connection may watch at most {MaxWatches} folders." );
			var id = Interlocked.Increment( ref _lastId );
			var watch = host.Watch( path, recursive, ( changed, kind ) => Push( new JsonObject { ["t"] = "fs.changed", ["watch"] = id, ["path"] = changed, ["kind"] = WorkspaceFrames.BuildKind( kind ) } ) );
			Track( _watches, id, watch );
			return Task.FromResult<JsonObject?>( new JsonObject { ["watch"] = id } );
		}

		/// <summary>Answers <c>fs.unwatch</c>; an unknown watch is not an error.</summary>
		public Task<JsonObject?> UnwatchAsync( JsonObject frame )
		{
			if ( _watches.TryRemove( WorkspaceFrames.ReadRequiredLong( frame, "watch" ), out var watch ) )
				watch.Dispose();
			return Task.FromResult( Empty );
		}

		/// <summary>Answers <c>pty.open</c> and pushes <c>pty.out</c> and then <c>pty.exit</c>.</summary>
		public Task<JsonObject?> OpenPtyAsync( JsonObject frame )
		{
			connection.RequireControl();
			var file = WorkspaceFrames.ReadRequiredString( frame, "file" );
			var arguments = Frame.ReadStrings( frame, "args" ) ?? [];
			var directory = Frame.ReadString( frame, "cwd" );
			CheckCommand( file, arguments, directory );
			if ( _ptys.Count >= MaxPtys )
				throw new RemoteException( RemoteErrors.Failed, $"A connection may hold at most {MaxPtys} terminals." );
			var columns = (int)Math.Clamp( Frame.ReadLong( frame, "cols" ) ?? 80, 1, short.MaxValue );
			var rows = (int)Math.Clamp( Frame.ReadLong( frame, "rows" ) ?? 24, 1, short.MaxValue );
			var id = Interlocked.Increment( ref _lastId );
			var pty = host.OpenPty( file, arguments, directory, columns, rows, WorkspaceFrames.ReadEnvironment( frame ),
				text => Push( new JsonObject { ["t"] = "pty.out", ["pty"] = id, ["data"] = Convert.ToBase64String( Encoding.UTF8.GetBytes( text ) ) } ),
				code =>
				{
					_ptys.TryRemove( id, out _ );
					Push( new JsonObject { ["t"] = "pty.exit", ["pty"] = id, ["code"] = code } );
				} );
			Track( _ptys, id, pty );
			return Task.FromResult<JsonObject?>( new JsonObject { ["pty"] = id } );
		}

		/// <summary>Answers <c>pty.in</c>.</summary>
		public Task<JsonObject?> WritePtyAsync( JsonObject frame )
		{
			connection.RequireControl();
			FindPty( frame )?.Write( WorkspaceFrames.ReadRequiredString( frame, "data" ) );
			return Task.FromResult( Empty );
		}

		/// <summary>Answers <c>pty.resize</c>.</summary>
		public Task<JsonObject?> ResizePtyAsync( JsonObject frame )
		{
			connection.RequireControl();
			var columns = (int)Math.Clamp( WorkspaceFrames.ReadRequiredLong( frame, "cols" ), 1, short.MaxValue );
			var rows = (int)Math.Clamp( WorkspaceFrames.ReadRequiredLong( frame, "rows" ), 1, short.MaxValue );
			FindPty( frame )?.Resize( columns, rows );
			return Task.FromResult( Empty );
		}

		/// <summary>Answers <c>pty.close</c>; the terminal still pushes <c>pty.exit</c> once it ends.</summary>
		public Task<JsonObject?> ClosePtyAsync( JsonObject frame )
		{
			connection.RequireControl();
			FindPty( frame )?.Dispose();
			return Task.FromResult( Empty );
		}

		/// <summary>Ends everything the connection started.</summary>
		public void Dispose()
		{
			lock ( _gate )
				_isClosed = true;

			_closing.Cancel();
			_closing.Dispose();
			foreach ( var id in _watches.Keys )
			{
				if ( _watches.TryRemove( id, out var watch ) )
					watch.Dispose();
			}
			foreach ( var id in _ptys.Keys )
			{
				if ( _ptys.TryRemove( id, out var pty ) )
					pty.Dispose();
			}
		}

		/// <summary>Finds the terminal a frame names; one that already ended is null.</summary>
		private IWorkspacePty? FindPty( JsonObject frame ) =>
			_ptys.TryGetValue( WorkspaceFrames.ReadRequiredLong( frame, "pty" ), out var pty ) ? pty : null;

		/// <summary>Keeps a watch or terminal for the close, or ends it at once when the connection already closed.</summary>
		private void Track<T>( ConcurrentDictionary<long, T> items, long id, T item ) where T : IDisposable
		{
			lock ( _gate )
			{
				if ( !_isClosed )
				{
					items[id] = item;
					return;
				}
			}
			item.Dispose();
			throw new IOException( "The connection closed." );
		}

		/// <summary>Builds the refusal of a file too large for one <c>fs.read</c> answer.</summary>
		private static RemoteException TooLarge() =>
			new( RemoteErrors.Failed, $"The file is larger than {MaxReadBytes / 1024 / 1024} MB, the most a remote read returns." );

		/// <summary>Reads a path field once the connection may use the workspace, refusing network paths and Loom's secrets for remote devices;
		/// Loom on this PC, over the pipe, is the user and keeps every path.</summary>
		/// <param name="frame">The frame.</param>
		/// <param name="name">The field.</param>
		/// <param name="isFolderChange">Whether the frame moves or deletes the path.</param>
		private string ReadPath( JsonObject frame, string name, bool isFolderChange = false )
		{
			connection.RequireControl();
			var path = WorkspaceFrames.ReadRequiredString( frame, name );
			return IsLocal ? path : WorkspaceGuard.CheckPath( path, isFolderChange );
		}

		/// <summary>Gets whether the connection is Loom on this PC over the named pipe.</summary>
		private bool IsLocal => connection.DeviceId == HostGateway.LocalDevice;

		/// <summary>Checks a process or terminal start of a remote device with <see cref="WorkspaceGuard.CheckCommand"/>.</summary>
		private void CheckCommand( string file, IReadOnlyList<string> arguments, string? directory )
		{
			if ( !IsLocal )
				WorkspaceGuard.CheckCommand( file, arguments, directory );
		}

		/// <summary>Queues a push after the ones before it; a closed connection drops it, and one too far behind is closed.</summary>
		private void Push( JsonObject frame )
		{
			if ( Interlocked.Increment( ref _pendingPushes ) > MaxPendingPushes )
			{
				Interlocked.Decrement( ref _pendingPushes );
				// The device does not keep up, such as with a recursive watch of a busy folder; dropping changes silently would leave it wrong.
				_ = connection.Peer.DisposeAsync().AsTask();
				return;
			}
			lock ( _gate )
				_pushes = SendAfterAsync( _pushes, frame );
		}

		/// <summary>Sends a push once the previous one went out.</summary>
		private async Task SendAfterAsync( Task previous, JsonObject frame )
		{
			try
			{
				await previous.ConfigureAwait( ConfigureAwaitOptions.SuppressThrowing );
				if ( connection.Peer.IsClosed )
					return;

				await connection.Peer.PushAsync( frame ).ConfigureAwait( false );
			}
			catch ( Exception error ) when ( error is IOException or ObjectDisposedException or InvalidOperationException or InvalidDataException )
			{
				// The connection is closing; its pushes no longer matter.
			}
			finally
			{
				Interlocked.Decrement( ref _pendingPushes );
			}
		}
	}
}
