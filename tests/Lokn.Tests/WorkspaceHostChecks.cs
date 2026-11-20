using System.Text;
using Lokn.Core;

namespace Lokn.Tests;

/// <summary>Exercises the local workspace host (processes, file operations, watching, finished-task continuations), the offline host, and the
/// application's hosts: This PC and paired hosts, each with its own workspace.</summary>
internal static class WorkspaceHostChecks
{
	/// <summary>Defines how long a check waits for a process or a file change, in seconds.</summary>
	private const int TimeoutSeconds = 20;

	/// <summary>Runs the workspace host checks in a temporary folder.</summary>
	internal static async Task RunAsync()
	{
		var folder = Path.Combine( Path.GetTempPath(), "lokn-workspace-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( folder );
		try
		{
			await CheckFilesAsync( folder );
			await CheckWatchAsync( folder );
			await CheckProcessesAsync( folder );
			CheckApplication();
			await CheckOfflineAsync();
		}
		finally
		{
			Directory.Delete( folder, recursive: true );
		}

		Console.WriteLine( "PASS local workspace host processes, files, watching, the offline host, and the hosts registry with per-host workspaces." );
	}

	/// <summary>Verifies stat, list, read, write, move, create and delete, and that file operations finish before they return.</summary>
	private static async Task CheckFilesAsync( string folder )
	{
		var host = LocalWorkspaceHost.Shared;
		var none = CancellationToken.None;
		var file = Path.Combine( folder, "a.txt" );
		var write = host.WriteAsync( file, Encoding.UTF8.GetBytes( "hello" ), none );
		CoreChecks.Check( write.IsCompletedSuccessfully, "A local write finishes before it returns." );

		var stat = await host.StatAsync( file, none );
		CoreChecks.Check( stat is { Exists: true, IsDirectory: false, Size: 5, IsFile: true } && stat.Modified > DateTime.UtcNow.AddMinutes( -5 ), "Stat describes a file." );
		CoreChecks.Check( await host.StatAsync( Path.Combine( folder, "missing" ), none ) == WorkspaceStat.Missing, "Stat of a missing path is not an error." );
		CoreChecks.Check( await host.DirectoryExistsAsync( folder, none ) && !await host.FileExistsAsync( folder, none ) && await host.FileExistsAsync( file, none ), "Folders and files are told apart." );
		CoreChecks.Check( Encoding.UTF8.GetString( await host.ReadAsync( file, none ) ) == "hello", "Read returns the bytes written." );

		var sub = Path.Combine( folder, "sub", "deeper" );
		await host.CreateDirectoryAsync( sub, none );
		var moved = Path.Combine( sub, "b.txt" );
		await host.MoveAsync( file, moved, none );
		await host.WriteAsync( file, Encoding.UTF8.GetBytes( "again" ), none );
		await host.MoveAsync( file, moved, none );
		CoreChecks.Check( !File.Exists( file ) && File.ReadAllText( moved ) == "again", "Move renames a file over an existing one." );

		var entries = await host.ListAsync( folder, none );
		CoreChecks.Check( entries is [{ Name: "sub", IsDirectory: true, Size: 0 }], "List names a folder's entries." );
		CoreChecks.Check( (await host.ListAsync( sub, none )) is [{ Name: "b.txt", IsDirectory: false, Size: 5 }], "List gives each file's size." );

		File.SetAttributes( moved, FileAttributes.ReadOnly );
		await host.DeleteAsync( moved, none );
		CoreChecks.Check( !File.Exists( moved ), "Delete removes a read-only file." );
		await host.DeleteAsync( Path.Combine( folder, "sub" ), none );
		await host.DeleteAsync( Path.Combine( folder, "sub" ), none );
		CoreChecks.Check( !Directory.Exists( Path.Combine( folder, "sub" ) ), "Delete removes a folder with its content, and a missing path is not an error." );

		var failed = host.ReadAsync( Path.Combine( folder, "missing" ), none );
		CoreChecks.Check( failed.IsFaulted && failed.Exception?.InnerException is FileNotFoundException, "A failed read comes back as a faulted task." );

		var ran = false;
		host.StatAsync( folder, none ).WhenDone( _ => throw new InvalidOperationException( "A finished task must not be posted." ), _ => ran = true );
		CoreChecks.Check( ran, "WhenDone runs at once for a finished task." );
	}

	/// <summary>Verifies that a watch reports a new file and stops when disposed, and that a missing folder cannot be watched.</summary>
	private static async Task CheckWatchAsync( string folder )
	{
		var host = LocalWorkspaceHost.Shared;
		var created = new TaskCompletionSource<string>( TaskCreationOptions.RunContinuationsAsynchronously );
		using ( host.Watch( folder, recursive: true, ( path, kind ) =>
		{
			if ( kind == WorkspaceChange.Created )
				created.TrySetResult( path );
		} ) )
		{
			var file = Path.Combine( folder, "watched.txt" );
			File.WriteAllText( file, "x" );
			var path = await created.Task.WaitAsync( TimeSpan.FromSeconds( TimeoutSeconds ) );
			CoreChecks.Check( path.Equals( file, StringComparison.OrdinalIgnoreCase ), "A watch reports a created file's full path." );
		}

		var missing = false;
		try
		{
			host.Watch( Path.Combine( folder, "missing" ), recursive: false, ( _, _ ) => { } ).Dispose();
		}
		catch ( DirectoryNotFoundException )
		{
			missing = true;
		}
		CoreChecks.Check( missing, "Watching a missing folder fails with DirectoryNotFoundException." );
	}

	/// <summary>Verifies arguments, standard input, environment, exit codes, a missing program and cancellation.</summary>
	private static async Task CheckProcessesAsync( string folder )
	{
		var host = LocalWorkspaceHost.Shared;
		using var timeout = new CancellationTokenSource( TimeSpan.FromSeconds( TimeoutSeconds ) );
		var echo = await host.RunAsync( "cmd.exe", ["/d", "/c", "echo %LOKN_CHECK% & cd & exit 3"], folder, null, new Dictionary<string, string> { ["LOKN_CHECK"] = "lokn-env" }, timeout.Token );
		CoreChecks.Check( echo.Code == 3 && echo.Output.Contains( "lokn-env" ) && echo.Output.Contains( folder, StringComparison.OrdinalIgnoreCase ), "A process gets its arguments, folder and variables, and reports its exit code." );

		// The system findstr echoes every line it reads; a sort found first on PATH, such as Git's, would answer differently.
		var echoed = await host.RunAsync( Path.Combine( Environment.SystemDirectory, "findstr.exe" ), ["/r", "."], folder, "b\na\n", null, timeout.Token );
		CoreChecks.Check( echoed.Code == 0 && echoed.Output.Replace( "\r\n", "\n", StringComparison.Ordinal ) == "b\na\n", "A process reads the given standard input." );

		var failed = false;
		try
		{
			await host.RunAsync( "lokn-no-such-program.exe", [], folder, null, null, timeout.Token );
		}
		catch ( System.ComponentModel.Win32Exception )
		{
			failed = true;
		}
		CoreChecks.Check( failed, "A missing program fails to start with a Windows error." );

		using var cancel = new CancellationTokenSource( TimeSpan.FromMilliseconds( 300 ) );
		var cancelled = false;
		try
		{
			await host.RunAsync( "cmd.exe", ["/d", "/c", "ping -n 30 127.0.0.1 > nul"], folder, null, null, cancel.Token );
		}
		catch ( OperationCanceledException )
		{
			cancelled = true;
		}
		CoreChecks.Check( cancelled, "Cancelling a process ends the run." );
	}

	/// <summary>Verifies the hosts registry: This PC first, idempotent adds, removed placeholders, removal closing bound panels and resetting the current host, the
	/// current host behind <see cref="LoknApplication.Workspace"/>, and panels following their own host's workspace only.</summary>
	private static void CheckApplication()
	{
		var app = new LoknApplication();
		var hosts = app.Hosts;
		CoreChecks.Check( hosts.All is [{ IsLocal: true, Id: LoknHost.LocalId, Name: "This PC", State: LoknHostState.Local, CanControl: true, IsOnline: true }]
			&& ReferenceEquals( hosts.Current, hosts.Local ) && ReferenceEquals( hosts.Local.Workspace, LocalWorkspaceHost.Shared ),
			"The application starts with This PC alone, current, on the local workspace host." );
		CoreChecks.Check( ReferenceEquals( app.Workspace, LocalWorkspaceHost.Shared ) && ReferenceEquals( app.BrowserTabs, hosts.Local.BrowserTabs ),
			"The application's workspace and browser tabs are This PC's while it is current." );

		var changes = 0;
		var workspaceChanges = 0;
		hosts.Changed += () => changes++;
		app.WorkspaceChanged += () => workspaceChanges++;
		var desk = hosts.Add( "desk-host-id", "Desk" );
		CoreChecks.Check( desk is { IsLocal: false, Name: "Desk", State: LoknHostState.Connecting, IsOnline: false, CanControl: false } && desk.Workspace is OfflineWorkspaceHost
			&& !ReferenceEquals( desk.BrowserTabs, hosts.Local.BrowserTabs ) && changes == 1,
			"A paired host starts connecting, offline, with its own browser tabs, and adding it reports a change." );
		CoreChecks.Check( ReferenceEquals( hosts.Add( "desk-host-id", "Other name" ), desk ) && hosts.All.Count == 2 && desk.Name == "Desk" && changes == 1,
			"Adding a host again returns the one in the list." );
		CoreChecks.Check( ReferenceEquals( hosts.All[0], hosts.Local ) && ReferenceEquals( hosts.All[1], desk ), "This PC comes first, then paired hosts in the order added." );
		CoreChecks.Check( ReferenceEquals( hosts.Find( null ), hosts.Local ) && ReferenceEquals( hosts.Find( LoknHost.LocalId ), hosts.Local ) && hosts.Find( "gone" ) is null,
			"Find gives This PC for null and the local id, and nothing for an unknown id." );
		var gone = hosts.Get( "gone" );
		CoreChecks.Check( gone is { State: LoknHostState.Removed, IsOnline: false } && ReferenceEquals( hosts.Get( "gone" ), gone ) && !hosts.All.Contains( gone ),
			"Get gives the same removed placeholder for an unknown id, outside the list." );

		desk.Update( LoknHostState.Connected, "Connected via fixture.", canControl: true );
		CoreChecks.Check( desk is { IsOnline: true, StatusText: "Connected via fixture.", CanControl: true } && changes == 2, "A host's state change is reported through the list." );
		desk.Update( LoknHostState.Connected, "Connected via fixture.", canControl: true );
		CoreChecks.Check( changes == 2, "Updating a host to the state it has reports nothing." );

		hosts.SetCurrent( desk.Id );
		CoreChecks.Check( ReferenceEquals( hosts.Current, desk ) && ReferenceEquals( app.BrowserTabs, desk.BrowserTabs ) && app.Workspace is OfflineWorkspaceHost && workspaceChanges == 1,
			"Switching hosts makes the application's workspace and browser tabs the new current host's and reports the workspace change." );
		var connected = new OfflineWorkspaceHost( "Desk fixture" );
		var deskChanges = 0;
		desk.WorkspaceChanged += () => deskChanges++;
		desk.SetWorkspace( connected );
		desk.SetWorkspace( connected );
		CoreChecks.Check( ReferenceEquals( app.Workspace, connected ) && deskChanges == 1 && workspaceChanges == 2,
			"Replacing the current host's workspace reaches the application's workspace once; setting it again raises nothing." );
		hosts.SetCurrent( "unknown" );
		CoreChecks.Check( ReferenceEquals( hosts.Current, hosts.Local ) && ReferenceEquals( app.Workspace, LocalWorkspaceHost.Shared ) && workspaceChanges == 3,
			"An unknown current host falls back to This PC, whose other workspace is reported as the application's change." );

		app.Load( new FixtureModule() );
		var local = app.Panels.Open( "hosts.bound" );
		var remote = app.Panels.Open( "hosts.bound", hostId: desk.Id );
		var plain = app.Panels.Open( "hosts.plain", hostId: desk.Id );
		var localContext = Context( app, local );
		var remoteContext = Context( app, remote );
		CoreChecks.Check( localContext.HostId == LoknHost.LocalId && ReferenceEquals( localContext.Workspace, LocalWorkspaceHost.Shared )
			&& remoteContext.HostId == desk.Id && ReferenceEquals( remoteContext.Host, desk ) && ReferenceEquals( remoteContext.Workspace, connected )
			&& Context( app, plain ).HostId is null && ReferenceEquals( Context( app, plain ).Host, hosts.Local ),
			"Each bound panel reads its own host's workspace; a panel of an unbound type has no host and stands on This PC." );
		var replaced = new OfflineWorkspaceHost( "Desk again" );
		desk.SetWorkspace( replaced );
		CoreChecks.Check( ReferenceEquals( remoteContext.Workspace, replaced ) && ReferenceEquals( localContext.Workspace, LocalWorkspaceHost.Shared ) && workspaceChanges == 3,
			"A paired host's new workspace reaches its panels only, and not the application's while This PC is current." );
		var child = remoteContext.CreateChild();
		CoreChecks.Check( child.Context.HostId == desk.Id, "A child context works on its panel's host." );
		child.Lifetime.Cancel();
		child.Lifetime.Dispose();

		app.Panels.SplitActive( SplitAxis.Horizontal );
		var hidden = app.Panels.Open( "hosts.bound", hostId: desk.Id );
		app.Panels.HideAll();
		app.Panels.Show( local, app.Panels.ActivePaneId );
		var removed = new List<LoknHost>();
		hosts.Removed += removed.Add;
		hosts.SetCurrent( desk.Id );
		CoreChecks.Check( hosts.Remove( desk.Id ) && !hosts.Remove( desk.Id ) && !hosts.Remove( LoknHost.LocalId ), "A paired host is removed once; This PC never is." );
		var open = app.Panels.Panels.Select( panel => panel.Id ).ToHashSet();
		CoreChecks.Check( open.SetEquals( [local, plain] ) && !open.Contains( hidden ),
			"Removing a host closes its bound panels, shown or hidden, and leaves This PC's and unbound panels open." );
		CoreChecks.Check( removed is [var item] && ReferenceEquals( item, desk ) && desk.State == LoknHostState.Removed && hosts.All.Count == 1
			&& ReferenceEquals( hosts.Current, hosts.Local ) && ReferenceEquals( hosts.Get( desk.Id ), desk ),
			"A removed host leaves the list as a removed placeholder, is reported, and This PC becomes current." );

		// Pairing again with a removed host, or pairing a host a restored layout named, revives the one object the open panels hold.
		var restored = Context( app, app.Panels.Open( "hosts.bound", hostId: desk.Id ) );
		var revived = hosts.Add( desk.Id, "Desk again" );
		CoreChecks.Check( ReferenceEquals( revived, desk ) && ReferenceEquals( restored.Host, desk ) && hosts.All.Contains( desk ) && ReferenceEquals( hosts.Get( desk.Id ), desk )
			&& desk is { State: LoknHostState.Connecting, Name: "Desk again", CanControl: false, Workspace: OfflineWorkspaceHost },
			"Adding a removed host again revives the same object, connecting, so a panel built on it while it was gone follows it again." );
		var placeholder = hosts.Get( "later" );
		CoreChecks.Check( ReferenceEquals( hosts.Add( "later", "Later" ), placeholder ) && placeholder is { State: LoknHostState.Connecting, Name: "Later" } && hosts.All.Contains( placeholder ),
			"A host a restored layout named before it was paired becomes that same placeholder once added." );
		hosts.Remove( desk.Id );
		hosts.Remove( "later" );
		app.Shutdown();
	}

	/// <summary>Verifies that the workspace of a host that is not connected fails every call and ends watches and terminals at once.</summary>
	private static async Task CheckOfflineAsync()
	{
		var host = new OfflineWorkspaceHost( "Desk" );
		var none = CancellationToken.None;
		var read = host.ReadAsync( @"C:\any.txt", none );
		var run = host.RunAsync( "git", ["status"], @"C:\", null, null, none );
		CoreChecks.Check( read.IsFaulted && read.Exception?.InnerException is IOException { Message: "Desk is not connected." } && run.IsFaulted
			&& host.StatAsync( @"C:\", none ).IsFaulted && host.ListAsync( @"C:\", none ).IsFaulted && host.WriteAsync( @"C:\a", Array.Empty<byte>(), none ).IsFaulted
			&& host.DeleteAsync( @"C:\a", none ).IsFaulted && host.MoveAsync( @"C:\a", @"C:\b", none ).IsFaulted && host.CreateDirectoryAsync( @"C:\a", none ).IsFaulted,
			"Every file and process call of an offline host fails with an IOException naming the host." );

		var lost = new TaskCompletionSource<WorkspaceChange>( TaskCreationOptions.RunContinuationsAsynchronously );
		using ( host.Watch( @"C:\", recursive: true, ( _, kind ) => lost.TrySetResult( kind ) ) )
			CoreChecks.Check( await lost.Task.WaitAsync( TimeSpan.FromSeconds( TimeoutSeconds ) ) == WorkspaceChange.Lost, "A watch on an offline host reports the folder lost." );

		var output = new StringBuilder();
		var exited = new TaskCompletionSource<int>( TaskCreationOptions.RunContinuationsAsynchronously );
		using ( host.OpenPty( "cmd.exe", [], null, 80, 24, null, text => output.Append( text ), code => exited.TrySetResult( code ) ) )
			CoreChecks.Check( await exited.Task.WaitAsync( TimeSpan.FromSeconds( TimeoutSeconds ) ) == -1 && output.ToString().Contains( "Desk is not connected.", StringComparison.Ordinal ),
				"A terminal on an offline host says why and exits with -1." );
	}

	/// <summary>Gets an open panel's context through its fixture content.</summary>
	private static PanelContext Context( LoknApplication app, Guid panelId ) =>
		((HostContent)app.Panels.Sessions.Single( session => session.Info.Id == panelId ).Content!).Context;

	/// <summary>Registers one host-bound and one unbound panel type.</summary>
	private sealed class FixtureModule : ILoknModule
	{
		/// <summary>Gets the fixture module identifier.</summary>
		public string Id => "hosts";

		/// <summary>Gets the fixture module name.</summary>
		public string Title => "Hosts fixture";

		/// <summary>Registers the panel types.</summary>
		public void Register( ModuleBuilder builder ) => builder
			.Panel( "bound", "Bound", context => new HostContent( context ), onHost: true )
			.Panel( "plain", "Plain", context => new HostContent( context ) );
	}

	/// <summary>Keeps its context so checks can read the host it works on.</summary>
	/// <param name="context">The panel's context.</param>
	private sealed class HostContent( PanelContext context ) : PanelContent
	{
		/// <summary>Gets the panel's context.</summary>
		internal PanelContext Context => context;

		/// <summary>Builds a minimal declaration.</summary>
		public override Goo.Blob Build() => new Goo.Text( "host" );
	}
}
