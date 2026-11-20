using System.Text;
using System.Text.Json.Nodes;
using Lokn.Core;
using Lokn.Modules.Remote;

namespace Lokn.Tests;

/// <summary>Checks the workspace frames of docs/remote.md end to end: <see cref="WorkspaceService"/> on one peer, <see cref="RemoteWorkspaceHost"/> on the other.</summary>
internal static class RemoteWorkspaceChecks
{
	/// <summary>Bounds every wait, so a broken check fails rather than hangs.</summary>
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds( 15 );

	/// <summary>Runs every remote workspace check.</summary>
	internal static async Task RunAsync()
	{
		var folder = Path.Combine( Path.GetTempPath(), "lokn-remote-workspace-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( folder );
		try
		{
			await CheckFilesAsync( folder );
			await CheckRunAsync( folder );
			await CheckWatchAsync( folder );
			await CheckPtyAsync( folder );
			await CheckViewOnlyAsync( folder );
			await CheckGuardAsync( folder );
			await CheckCloseAsync( folder );
		}
		finally
		{
			await DeleteWhenReleasedAsync( folder );
		}
	}

	/// <summary>Deletes the check folder once nothing holds it. A closed connection's host terminals and watches end in the background
	/// (<see cref="IWorkspacePty.Dispose"/> does not wait for the program), and a terminal's program keeps its working folder open until it exits.</summary>
	private static async Task DeleteWhenReleasedAsync( string folder )
	{
		var deadline = DateTime.UtcNow + Patience;
		while ( true )
		{
			try
			{
				Directory.Delete( folder, true );
				return;
			}
			catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline )
			{
				await Task.Delay( 100 );
			}
		}
	}

	/// <summary>Connects a client to a host service over memory and starts both peers.</summary>
	/// <param name="canControl">Whether the device has the control scope.</param>
	private static Link Connect( bool canControl )
	{
		var (first, second) = MemoryConnection.CreatePair();
		var host = new RpcPeer( first );
		var client = new RpcPeer( second );
		var app = new LoknApplication();
		CoreChecks.Check( ReferenceEquals( app.Hosts.Local.Workspace, LocalWorkspaceHost.Shared ), "The host serves This PC's local workspace." );
		IReadOnlyList<string> scopes = canControl ? [DeviceScopes.View, DeviceScopes.Control] : [DeviceScopes.View];
		new WorkspaceService( app ).Register( new RemoteConnection( "test", "Test device", scopes, host ) );
		var workspace = new RemoteWorkspaceHost( client );
		host.Start();
		client.Start();
		return new( host, client, workspace );
	}

	/// <summary>Holds both ends of one test connection and closes them together.</summary>
	/// <param name="Host">The host's peer.</param>
	/// <param name="Client">The client's peer.</param>
	/// <param name="Workspace">The client's workspace over the connection.</param>
	private sealed record Link( RpcPeer Host, RpcPeer Client, RemoteWorkspaceHost Workspace ) : IAsyncDisposable
	{
		/// <summary>Disposes the client's workspace and both peers.</summary>
		public async ValueTask DisposeAsync()
		{
			Workspace.Dispose();
			await Client.DisposeAsync();
			await Host.DisposeAsync();
		}
	}

	/// <summary>Checks writing, reading, describing, listing, moving, creating and deleting through the host.</summary>
	private static async Task CheckFilesAsync( string folder )
	{
		await using var link = Connect( true );
		var workspace = link.Workspace;
		using var timeout = new CancellationTokenSource( Patience );
		var token = timeout.Token;

		var file = Path.Combine( folder, "a.txt" );
		var bytes = Encoding.UTF8.GetBytes( "héllo" );
		await workspace.WriteAsync( file, bytes, token );
		CoreChecks.Check( File.ReadAllBytes( file ).AsSpan().SequenceEqual( bytes ), "fs.write writes the bytes on the host." );
		CoreChecks.Check( (await workspace.ReadAsync( file, token )).AsSpan().SequenceEqual( bytes ), "fs.read returns the file's bytes." );

		var stat = await workspace.StatAsync( file, token );
		CoreChecks.Check( stat.IsFile && stat.Size == bytes.Length, "fs.stat describes a file and its size." );
		CoreChecks.Check( Math.Abs( ( stat.Modified - File.GetLastWriteTimeUtc( file ) ).TotalSeconds ) < 1, "fs.stat carries the write time." );
		CoreChecks.Check( !(await workspace.StatAsync( Path.Combine( folder, "missing" ), token )).Exists, "fs.stat reports a missing path without failing." );

		var sub = Path.Combine( folder, "sub", "deeper" );
		await workspace.CreateDirectoryAsync( sub, token );
		CoreChecks.Check( await workspace.DirectoryExistsAsync( sub, token ), "fs.mkdir creates a folder with its parents." );

		var moved = Path.Combine( folder, "sub", "b.txt" );
		await workspace.MoveAsync( file, moved, token );
		CoreChecks.Check( !File.Exists( file ) && File.Exists( moved ), "fs.move moves a file." );

		var entries = await workspace.ListAsync( Path.Combine( folder, "sub" ), token );
		CoreChecks.Check( entries.Count == 2 && entries.Any( entry => entry is { Name: "b.txt", IsDirectory: false } && entry.Size == bytes.Length ) && entries.Any( entry => entry is { Name: "deeper", IsDirectory: true } ), "fs.list lists files and folders." );

		await workspace.DeleteAsync( Path.Combine( folder, "sub" ), token );
		CoreChecks.Check( !Directory.Exists( Path.Combine( folder, "sub" ) ), "fs.delete deletes a folder with its contents." );

		var failed = false;
		try
		{
			await workspace.ReadAsync( Path.Combine( folder, "missing" ), token );
		}
		catch ( IOException )
		{
			failed = true;
		}
		CoreChecks.Check( failed, "A host error reaches the client as an IOException." );
	}

	/// <summary>Checks proc.run's output, environment, exit code and standard input.</summary>
	private static async Task CheckRunAsync( string folder )
	{
		await using var link = Connect( true );
		var workspace = link.Workspace;
		using var timeout = new CancellationTokenSource( Patience );
		var token = timeout.Token;

		var shell = Path.Combine( Environment.SystemDirectory, "cmd.exe" );
		var result = await workspace.RunAsync( shell, ["/d", "/c", "echo %LOKN_REMOTE%& exit /b 3"], folder, null, new Dictionary<string, string> { ["LOKN_REMOTE"] = "remote-value" }, token );
		CoreChecks.Check( result.Output.Trim() == "remote-value", "proc.run passes the environment and returns the output." );
		CoreChecks.Check( result.Code == 3, "proc.run returns the exit code." );

		var findstr = Path.Combine( Environment.GetEnvironmentVariable( "SystemRoot" ) ?? @"C:\Windows", "System32", "findstr.exe" );
		var echoed = await workspace.RunAsync( findstr, ["/r", "."], folder, "from stdin\n", null, token );
		CoreChecks.Check( echoed.Output.Trim() == "from stdin" && echoed.Code == 0, "proc.run writes the input to standard input." );

		var refused = false;
		try
		{
			await workspace.RunAsync( Path.Combine( folder, "missing.exe" ), [], folder, null, null, token );
		}
		catch ( System.ComponentModel.Win32Exception )
		{
			refused = true;
		}
		CoreChecks.Check( refused, "A program the host cannot start fails as a Win32Exception." );
	}

	/// <summary>Checks that a change in a watched folder arrives as fs.changed, and that unwatching sends fs.unwatch.</summary>
	private static async Task CheckWatchAsync( string folder )
	{
		await using var link = Connect( true );
		var workspace = link.Workspace;

		var watched = Path.Combine( folder, "watched" );
		Directory.CreateDirectory( watched );
		var file = Path.Combine( watched, "new.txt" );
		var seen = new TaskCompletionSource<WorkspaceChange>( TaskCreationOptions.RunContinuationsAsynchronously );
		var watch = workspace.Watch( watched, false, ( path, kind ) =>
		{
			if ( string.Equals( path, file, StringComparison.OrdinalIgnoreCase ) )
				seen.TrySetResult( kind );
		} );

		// The watch starts once the host answers; keep touching the file until a change arrives.
		var deadline = DateTime.UtcNow + Patience;
		while ( !seen.Task.IsCompleted && DateTime.UtcNow < deadline )
		{
			File.WriteAllText( file, DateTime.UtcNow.Ticks.ToString( System.Globalization.CultureInfo.InvariantCulture ) );
			await Task.WhenAny( seen.Task, Task.Delay( 200 ) );
		}
		CoreChecks.Check( seen.Task.IsCompletedSuccessfully && seen.Task.Result is WorkspaceChange.Created or WorkspaceChange.Changed, "A change in a watched folder arrives as fs.changed." );

		watch.Dispose();
		var answer = await link.Client.RequestAsync( new JsonObject { ["t"] = "fs.unwatch", ["watch"] = 999 }, CancellationToken.None );
		CoreChecks.Check( Frame.ReadType( answer ) == Frame.Ok, "fs.unwatch of an unknown watch is not an error." );
	}

	/// <summary>Checks a cmd.exe round trip through a host pseudoterminal and its exit.</summary>
	private static async Task CheckPtyAsync( string folder )
	{
		await using var link = Connect( true );
		var workspace = link.Workspace;

		var output = new StringBuilder();
		var echoed = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
		var exited = new TaskCompletionSource<int>( TaskCreationOptions.RunContinuationsAsynchronously );
		var shell = Path.Combine( Environment.SystemDirectory, "cmd.exe" );
		using var pty = workspace.OpenPty( shell, ["/d", "/q", "/k"], folder, 100, 30, null, text =>
		{
			lock ( output )
			{
				output.Append( text );
				if ( output.ToString().Contains( "remote-pty-ok", StringComparison.Ordinal ) )
					echoed.TrySetResult();
			}
		}, code => exited.TrySetResult( code ) );

		pty.Resize( 120, 40 );
		pty.Write( "echo remote-pty-" + "ok\r" );
		CoreChecks.Check( await Task.WhenAny( echoed.Task, Task.Delay( Patience ) ) == echoed.Task, "pty.in reaches cmd.exe and its echo returns as pty.out." );

		pty.Write( "exit 5\r" );
		CoreChecks.Check( await Task.WhenAny( exited.Task, Task.Delay( Patience ) ) == exited.Task && exited.Task.Result == 5, "pty.exit reports the program's exit code." );
	}

	/// <summary>Checks that a view-only device gets the scope error on every workspace frame, reads and terminals included.</summary>
	private static async Task CheckViewOnlyAsync( string folder )
	{
		await using var link = Connect( false );
		var workspace = link.Workspace;
		using var timeout = new CancellationTokenSource( Patience );

		foreach ( var type in new[] { "fs.stat", "fs.list", "fs.read", "fs.watch" } )
			CoreChecks.Check( await RequestCodeAsync( link, new JsonObject { ["t"] = type, ["path"] = folder }, timeout.Token ) == RemoteErrors.Scope, $"{type} from a view-only device fails with the scope error." );
		var readDenied = false;
		try
		{
			await workspace.DirectoryExistsAsync( folder, timeout.Token );
		}
		catch ( UnauthorizedAccessException )
		{
			readDenied = true;
		}
		CoreChecks.Check( readDenied, "A view-only device may not even read the host's files." );

		string? code = null;
		try
		{
			await link.Client.RequestAsync( new JsonObject { ["t"] = "fs.write", ["path"] = Path.Combine( folder, "denied.txt" ), ["data"] = "" }, timeout.Token );
		}
		catch ( RemoteException error )
		{
			code = error.Code;
		}
		CoreChecks.Check( code == RemoteErrors.Scope, "fs.write from a view-only device fails with the scope error." );

		var denied = false;
		try
		{
			await workspace.WriteAsync( Path.Combine( folder, "denied.txt" ), new byte[] { 1 }, timeout.Token );
		}
		catch ( UnauthorizedAccessException error ) when ( error.InnerException is RemoteException { Code: RemoteErrors.Scope } )
		{
			denied = true;
		}
		CoreChecks.Check( denied && !File.Exists( Path.Combine( folder, "denied.txt" ) ), "The client reports the scope error as UnauthorizedAccessException and nothing is written." );

		var exited = new TaskCompletionSource<int>( TaskCreationOptions.RunContinuationsAsynchronously );
		using var pty = workspace.OpenPty( Path.Combine( Environment.SystemDirectory, "cmd.exe" ), [], folder, 80, 24, null, _ => { }, code => exited.TrySetResult( code ) );
		CoreChecks.Check( await Task.WhenAny( exited.Task, Task.Delay( Patience ) ) == exited.Task && exited.Task.Result == -1, "A terminal a view-only device opens exits with -1." );
	}

	/// <summary>Checks what even a controlling device cannot reach: Loom's secrets, network and stream paths, oversized reads, and more watches than allowed.</summary>
	private static async Task CheckGuardAsync( string folder )
	{
		await using var link = Connect( true );
		using var timeout = new CancellationTokenSource( Patience );
		var secret = Path.Combine( LoknPaths.DataRoot, "devices.json" );

		foreach ( var type in new[] { "fs.read", "fs.stat", "fs.write", "fs.delete" } )
			CoreChecks.Check( await RequestCodeAsync( link, new JsonObject { ["t"] = type, ["path"] = secret, ["data"] = "" }, timeout.Token ) == RemoteErrors.Scope, $"{type} of Loom's devices.json is refused." );
		CoreChecks.Check( await RequestCodeAsync( link, new JsonObject { ["t"] = "fs.read", ["path"] = Path.Combine( LoknPaths.DataRoot, "host-key.pem" ) }, timeout.Token ) == RemoteErrors.Scope, "fs.read of a key file is refused." );
		CoreChecks.Check( await RequestCodeAsync( link, new JsonObject { ["t"] = "fs.delete", ["path"] = Path.GetDirectoryName( LoknPaths.DataRoot ) }, timeout.Token ) == RemoteErrors.Scope, "fs.delete of a folder holding the data folder is refused." );
		CoreChecks.Check( await RequestCodeAsync( link, new JsonObject { ["t"] = "fs.move", ["path"] = Path.Combine( folder, "a" ), ["to"] = secret }, timeout.Token ) == RemoteErrors.Scope, "fs.move onto a secret file is refused." );
		foreach ( var path in new[] { @"\\127.0.0.1\c$\x.txt", @"\\?\C:\x.txt", @"\\.\pipe\x", "relative.txt", Path.Combine( folder, "a.txt:hidden" ) } )
			CoreChecks.Check( await RequestCodeAsync( link, new JsonObject { ["t"] = "fs.stat", ["path"] = path }, timeout.Token ) == RemoteErrors.BadRequest, $"fs.stat of {path} is refused before it is touched." );
		CoreChecks.Check( await RequestCodeAsync( link, new JsonObject { ["t"] = "proc.run", ["file"] = "cmd.exe", ["args"] = new JsonArray( "/c", "type", "devices.json" ), ["cwd"] = folder }, timeout.Token ) == RemoteErrors.Scope, "proc.run naming a secret file is refused." );
		CoreChecks.Check( await RequestCodeAsync( link, new JsonObject { ["t"] = "proc.run", ["file"] = "cmd.exe", ["args"] = new JsonArray( "/c", "dir" ), ["cwd"] = LoknPaths.DataRoot }, timeout.Token ) == RemoteErrors.Scope, "proc.run in the data folder is refused." );
		CoreChecks.Check( WorkspaceGuard.IsSecretName( "VAPID-KEY.PEM" ) && WorkspaceGuard.IsSecretName( "push-subscriptions.json" ) && !WorkspaceGuard.IsSecretName( "settings.json" ), "Secret names match without case." );

		var large = Path.Combine( folder, "large.bin" );
		await using ( var stream = File.Create( large ) )
			stream.SetLength( WorkspaceService.MaxReadBytes + 1 );
		CoreChecks.Check( await RequestCodeAsync( link, new JsonObject { ["t"] = "fs.read", ["path"] = large }, timeout.Token ) == RemoteErrors.Failed, "fs.read of a file over the limit is refused." );
		File.Delete( large );

		for ( var i = 0; i < WorkspaceService.MaxWatches; i++ )
			await link.Client.RequestAsync( new JsonObject { ["t"] = "fs.watch", ["path"] = folder, ["recursive"] = false }, timeout.Token );
		CoreChecks.Check( await RequestCodeAsync( link, new JsonObject { ["t"] = "fs.watch", ["path"] = folder, ["recursive"] = false }, timeout.Token ) == RemoteErrors.Failed, "A connection may not hold more watches than allowed." );
	}

	/// <summary>Sends a request and returns the error code it was refused with, or null when it succeeded.</summary>
	private static async Task<string?> RequestCodeAsync( Link link, JsonObject frame, CancellationToken cancellationToken )
	{
		try
		{
			await link.Client.RequestAsync( frame, cancellationToken );
			return null;
		}
		catch ( RemoteException error )
		{
			return error.Code;
		}
	}

	/// <summary>Checks that closing the connection exits open terminals with -1 and reports watches lost.</summary>
	private static async Task CheckCloseAsync( string folder )
	{
		await using var link = Connect( true );
		var workspace = link.Workspace;

		var output = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
		var exited = new TaskCompletionSource<int>( TaskCreationOptions.RunContinuationsAsynchronously );
		var lost = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
		var shell = Path.Combine( Environment.SystemDirectory, "cmd.exe" );
		using var pty = workspace.OpenPty( shell, ["/d", "/q", "/k"], folder, 80, 24, null, _ => output.TrySetResult(), code => exited.TrySetResult( code ) );
		using var watch = workspace.Watch( folder, false, ( _, kind ) =>
		{
			if ( kind == WorkspaceChange.Lost )
				lost.TrySetResult();
		} );
		await Task.WhenAny( output.Task, Task.Delay( Patience ) );

		await link.Client.DisposeAsync();
		CoreChecks.Check( await Task.WhenAny( exited.Task, Task.Delay( Patience ) ) == exited.Task && exited.Task.Result == -1, "Closing the connection exits an open terminal with -1." );
		CoreChecks.Check( await Task.WhenAny( lost.Task, Task.Delay( Patience ) ) == lost.Task, "Closing the connection reports a watch lost." );

		var failed = false;
		try
		{
			await workspace.StatAsync( folder, CancellationToken.None );
		}
		catch ( IOException )
		{
			failed = true;
		}
		CoreChecks.Check( failed, "Calls after the close fail with IOException." );
	}
}
