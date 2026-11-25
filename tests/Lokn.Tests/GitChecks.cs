using Goo;
using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Files;
using Lokn.Modules.Git;
using Lokn.Modules.Projects;

namespace Lokn.Tests;

/// <summary>Exercises git status parsing, reading a real repository, the shared watched state that follows file changes, the repository commands and the commit graph.</summary>
internal static class GitChecks
{
	/// <summary>Runs every git check.</summary>
	internal static async Task RunAsync()
	{
		CheckParsing();
		await CheckRepositoryAsync();
		CheckGraph();
		CheckReviewWorkflow();
		await CheckCommandsAsync();
		await CheckHunksAsync();
		await CheckRemoteCountsAsync();
		await CheckPanelAsync();
		await CheckHostsAsync();
		await CheckSideModesAsync();
		Console.WriteLine( "PASS git status and line-count parsing, reading a repository and a plain folder, a shared watch that follows edits and commits, one watch per host of the same folder, "
			+ "repository commands (log, refs, changes, staging, commits, branches, merges and conflicts, history edits, tags, remotes, stashes), the commit graph, Omp review prompts, findings and commit messages, "
			+ "untracked files of a remote host counted without reading large ones across the connection, "
			+ "single hunks staged, unstaged and discarded, the Git panel's graph and file views, a Git panel opened beside a paired host's panel on that host, and the Files side view's modes: Files first, Git's Changes and History only with Git, each mode's content kept while switching, and the selected mode restored." );
	}

	/// <summary>Counts the untracked files of a repository on a remote host, faked over this PC: small ones are read and counted, larger ones are counted
	/// by git where they live, so a large file never crosses the connection.</summary>
	private static async Task CheckRemoteCountsAsync()
	{
		var repository = Directory.CreateTempSubdirectory( "lokn-git-remote-count-" ).FullName;
		var host = new ReadCountingHost();
		try
		{
			await RunAsync( repository, "init", "--quiet", "--initial-branch=main" );
			File.WriteAllText( Path.Combine( repository, "small.txt" ), "one\ntwo" );
			File.WriteAllText( Path.Combine( repository, "large.txt" ), string.Concat( Enumerable.Repeat( "a line of text\n", 100_000 ) ) );
			File.WriteAllBytes( Path.Combine( repository, "large.bin" ), new byte[1_200_000] );
			var changes = await GitRepository.ReadChangesAsync( host, repository, CancellationToken.None );
			var counted = changes.Unstaged.ToDictionary( file => file.Path );
			CoreChecks.Check( counted["small.txt"] is { Added: 2, IsBinary: false } && counted["large.txt"] is { Added: 100_000, IsBinary: false } && counted["large.bin"] is { Added: 0, IsBinary: true },
				"A remote host's untracked files are counted, large ones by git on the host, and a large binary file is flagged without lines." );
			CoreChecks.Check( host.Reads is [var read] && Path.GetFileName( read ) == "small.txt", "Only small untracked files are read across the connection to count them." );
		}
		finally
		{
			ChatChecks.DeleteRepository( repository );
		}
	}

	/// <summary>Stands for a remote host by passing every call to this PC, recording the files read whole, the folders git ran in and the folders watched;
	/// git runs as the executable this PC found.</summary>
	private sealed class ReadCountingHost : IWorkspaceHost
	{
		/// <summary>Gets the files read whole, in order.</summary>
		internal List<string> Reads { get; } = [];

		/// <summary>Gets the folders programs ran in, in order.</summary>
		internal List<string> Runs { get; } = [];

		/// <summary>Gets the folders watched, in order.</summary>
		internal List<string> Watches { get; } = [];

		/// <summary>Gets whether watches only record their folder and never report a change, as for a host whose files nothing else touches; the checks'
		/// hosts share this PC's disk, so one host's git would otherwise wake the other's reader.</summary>
		internal bool IsDeaf { get; init; }

		/// <summary>Runs a program on this PC, git as the executable this PC found, and records its folder.</summary>
		public Task<WorkspaceProcessResult> RunAsync( string file, IReadOnlyList<string> arguments, string directory, string? input, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken )
		{
			lock ( Runs )
				Runs.Add( directory );
			return LocalWorkspaceHost.Shared.RunAsync( file == "git" ? GitProcess.Executable : file, arguments, directory, input, environment, cancellationToken );
		}

		/// <summary>Describes a path on this PC.</summary>
		public Task<WorkspaceStat> StatAsync( string path, CancellationToken cancellationToken ) => LocalWorkspaceHost.Shared.StatAsync( path, cancellationToken );

		/// <summary>Lists a folder on this PC.</summary>
		public Task<IReadOnlyList<WorkspaceEntry>> ListAsync( string path, CancellationToken cancellationToken ) => LocalWorkspaceHost.Shared.ListAsync( path, cancellationToken );

		/// <summary>Reads a file on this PC and records it.</summary>
		public Task<byte[]> ReadAsync( string path, CancellationToken cancellationToken )
		{
			lock ( Reads )
				Reads.Add( path );
			return LocalWorkspaceHost.Shared.ReadAsync( path, cancellationToken );
		}

		/// <summary>Writes a file on this PC.</summary>
		public Task WriteAsync( string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken ) => LocalWorkspaceHost.Shared.WriteAsync( path, data, cancellationToken );

		/// <summary>Deletes a path on this PC.</summary>
		public Task DeleteAsync( string path, CancellationToken cancellationToken ) => LocalWorkspaceHost.Shared.DeleteAsync( path, cancellationToken );

		/// <summary>Moves a path on this PC.</summary>
		public Task MoveAsync( string path, string to, CancellationToken cancellationToken ) => LocalWorkspaceHost.Shared.MoveAsync( path, to, cancellationToken );

		/// <summary>Creates a folder on this PC.</summary>
		public Task CreateDirectoryAsync( string path, CancellationToken cancellationToken ) => LocalWorkspaceHost.Shared.CreateDirectoryAsync( path, cancellationToken );

		/// <summary>Watches a folder on this PC and records it.</summary>
		public IDisposable Watch( string path, bool recursive, Action<string, WorkspaceChange> changed )
		{
			lock ( Watches )
				Watches.Add( path );
			return IsDeaf ? new CancellationTokenSource() : LocalWorkspaceHost.Shared.Watch( path, recursive, changed );
		}

		/// <summary>Opens a pseudoconsole on this PC.</summary>
		public IWorkspacePty OpenPty( string file, IReadOnlyList<string> arguments, string? directory, int columns, int rows, IReadOnlyDictionary<string, string>? environment, Action<string> output, Action<int> exited ) =>
			LocalWorkspaceHost.Shared.OpenPty( file, arguments, directory, columns, rows, environment, output, exited );
	}

	/// <summary>Opens the Git panel on one changed file of a repository with history, and builds its file view unified and split, the graph beside the
	/// working tree and beside a commit, and a commit's file, once their reads arrive; opening the panel again for another file reuses it.</summary>
	/// <remarks>Headless: it proves the views build from real data without errors; the chat-view window suite shows them on screen.</remarks>
	private static async Task CheckPanelAsync()
	{
		var repository = Directory.CreateTempSubdirectory( "lokn-git-panel-" ).FullName;
		File.WriteAllText( Path.Combine( repository, "readme.txt" ), "one\n" );
		await RunAsync( repository, "init", "--quiet", "--initial-branch=main" );
		await RunAsync( repository, "add", "." );
		await RunAsync( repository, "-c", "user.name=Lokn Checks", "-c", "user.email=checks@example.invalid", "commit", "--quiet", "-m", "Initial" );
		await RunAsync( repository, "checkout", "--quiet", "-b", "feature" );
		File.WriteAllText( Path.Combine( repository, "feature.txt" ), "feature\n" );
		await RunAsync( repository, "add", "." );
		await RunAsync( repository, "-c", "user.name=Lokn Checks", "-c", "user.email=checks@example.invalid", "commit", "--quiet", "-m", "Feature" );
		await RunAsync( repository, "checkout", "--quiet", "main" );
		await RunAsync( repository, "-c", "user.name=Lokn Checks", "-c", "user.email=checks@example.invalid", "merge", "--quiet", "--no-ff", "--no-edit", "feature" );
		File.WriteAllText( Path.Combine( repository, "readme.txt" ), "one\ntwo\n" );
		File.WriteAllText( Path.Combine( repository, "draft.txt" ), "draft\n" );

		var app = new LoknApplication();
		var queued = new List<Action>();
		app.BindDispatcher( action =>
		{
			lock ( queued )
				queued.Add( action );
			return true;
		} );
		var store = new ConversationStore( new ProjectStore() );
		var git = new GitModule();
		CoreChecks.Check( app.Load( git ) && ProjectChecks.LoadProjects( app, store )
			&& app.Load( new ConversationsModule( store, ( _, _, _ ) => throw new InvalidOperationException( "No chats in this check." ) ) ),
			"Git and Conversations load for the Git panel." );
		app.Settings.Set( GitModule.Avatars, SettingsScope.Global, false );

		GitRef Ref( string name, GitRefKind kind, string? remote = null ) =>
			new( name, name, kind, "c0ffee", remote, null, 0, 0, false, false );
		var labels = GitContent.ComputeRefLabels( [Ref( "origin/main", GitRefKind.RemoteBranch, "origin" ), Ref( "main", GitRefKind.LocalBranch ),
			Ref( "upstream/fix/login", GitRefKind.RemoteBranch, "upstream" ), Ref( "v1.0", GitRefKind.Tag )] );
		CoreChecks.Check( labels.Select( label => (label.Label, label.IsOnRemote, label.Ref.Kind) ).SequenceEqual( [("main", true, GitRefKind.LocalBranch),
			("fix/login", false, GitRefKind.RemoteBranch), ("v1.0", false, GitRefKind.Tag)] ),
			"A local branch folds in its remote branch of the same name; a branch only on a remote drops its remote's name, keeping its own slashes." );
		CoreChecks.Check( GitAvatars.ComputeUrl( "4759511+lolleko@users.noreply.github.com" ) == "https://avatars.githubusercontent.com/u/4759511?s=80&v=4"
			&& GitAvatars.ComputeUrl( "octo@users.noreply.github.com" ) == "https://github.com/octo.png?size=80"
			&& GitAvatars.ComputeUrl( "someone@example.com" ).StartsWith( "https://www.gravatar.com/avatar/", StringComparison.Ordinal ),
			"GitHub no-reply addresses use the account's picture; other addresses use Gravatar." );
		var panelId = app.Panels.Open( GitModule.PanelType, input: new GitPanelInput( repository, Focus: Path.Combine( repository, "readme.txt" ) ) );
		var panel = (GitContent)app.Panels.Sessions.Single( session => session.Info.Id == panelId ).Content!;
		const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
		var view = (GitFileView)typeof( GitContent ).GetField( "_file", Private )!.GetValue( panel )!;

		// Each build starts the reads it needs; pumping lets them land, and the next build shows them.
		void Settle( Func<bool> done )
		{
			var clock = System.Diagnostics.Stopwatch.StartNew();
			while ( clock.Elapsed.TotalSeconds < 10 )
			{
				Action[] actions;
				lock ( queued )
				{
					actions = [.. queued];
					queued.Clear();
				}
				foreach ( var action in actions )
					action();
				CoreChecks.Check( panel.Build() is Blob, "The Git panel builds." );
				if ( actions.Length == 0 && done() )
					return;
				Thread.Sleep( 20 );
			}
		}

		bool IsLoaded() => (bool)typeof( GitFileView ).GetField( "_isLoaded", Private )!.GetValue( view )!;
		Settle( () => view.Target is not null && IsLoaded() );
		CoreChecks.Check( view.Target == new GitFileTarget( "readme.txt", false ), "A panel opened on a changed file shows that file's unstaged diff once the changes load." );
		typeof( GitFileView ).GetField( "_split", Private )!.SetValue( view, true );
		Settle( () => true );

		git.OpenPanel( app, panelId, repository, Path.Combine( repository, "draft.txt" ) );
		Settle( () => view.Target?.Path == "draft.txt" && IsLoaded() );
		CoreChecks.Check( app.Panels.Panels.Count == 1 && view.Target == new GitFileTarget( "draft.txt", false ),
			"Opening the panel again for another file of the same repository reuses it on that file." );

		panel.Reveal( null, false );
		Settle( () => view.Target is null );
		var history = (GitHistory?)typeof( GitContent ).GetField( "_history", Private )!.GetValue( panel );
		var feature = history?.Commits.FirstOrDefault( commit => commit.Subject == "Feature" );
		CoreChecks.Check( history?.Commits.Any( commit => commit.Parents.Count == 2 ) == true && feature is not null, "The graph loads the merge and the feature commit." );
		typeof( GitContent ).GetMethod( "SelectCommit", Private )!.Invoke( panel, [feature!.Id, false] );
		Settle( () => typeof( GitContent ).GetField( "_commitFiles", Private )!.GetValue( panel ) is not null );
		typeof( GitContent ).GetMethod( "ShowFile", Private )!.Invoke( panel, [new GitFileTarget( "feature.txt", false, feature.Id )] );
		Settle( IsLoaded );
		var patch = (GitFilePatch?)typeof( GitFileView ).GetField( "_patch", Private )!.GetValue( view );
		CoreChecks.Check( patch is { Operation: "create", Added: 1, Hunks.Count: 1 }, "A commit's file shows the lines that commit added." );

		CoreChecks.Check( app.LastError is null, $"The Git panel's graph, working tree, commit and file views build from a real repository without errors. {app.LastError}" );
		app.Shutdown();
	}

	/// <summary>Verifies porcelain v2 status and numstat parsing, and which git-folder changes count.</summary>
	private static void CheckParsing()
	{
		const string status = "# branch.oid 0123456789abcdef0123456789abcdef01234567\0# branch.head feature/login\0# branch.upstream origin/feature/login\0# branch.ab +2 -1\0"
			+ "1 M. N... 100644 100644 100644 aaa bbb staged.cs\0"
			+ "1 .M N... 100644 100644 100644 aaa bbb edited.cs\0"
			+ "1 MM N... 100644 100644 100644 aaa bbb both.cs\0"
			+ "2 R. N... 100644 100644 100644 aaa bbb R100 new.cs\0old.cs\0"
			+ "u UU N... 100644 100644 100644 100644 aaa bbb ccc conflict.cs\0"
			+ "? notes.txt\0";
		var state = GitState.ParseStatus( "C:/repo/src", "C:/repo", status );
		CoreChecks.Check( state is { Branch: "feature/login", Commit: "0123456", Upstream: "origin/feature/login", Ahead: 2, Behind: 1 },
			"Status headers give the branch, short commit, upstream, and ahead and behind counts." );
		CoreChecks.Check( state is { Staged: 3, Unstaged: 2, Conflicted: 1, Untracked: 1, Files: 6 },
			"Status entries count staged, unstaged, conflicted and untracked paths once each; a rename's old path is not a file." );

		var fresh = GitState.ParseStatus( "C:/repo", "C:/repo", "# branch.oid (initial)\0# branch.head main\0" );
		var detached = GitState.ParseStatus( "C:/repo", "C:/repo", "# branch.oid 89abcdef0123456789abcdef0123456789abcdef\0# branch.head (detached)\0" );
		CoreChecks.Check( fresh is { Branch: "main", Commit: null, Files: 0 } && detached is { Branch: null, Commit: "89abcde" },
			"A repository without commits has no commit, and a detached HEAD has no branch." );

		CoreChecks.Check( GitState.ParseNumstat( "3\t1\ta.cs\0-\t-\timage.png\0" + "4\t0\t\0old.cs\0new.cs\0" ) == (7, 1),
			"Line counts add text files and renames, skip binary files, and never read a rename's paths as counts." );

		CoreChecks.Check( GitSource.IsGitChange( "HEAD" ) && GitSource.IsGitChange( "index" ) && GitSource.IsGitChange( Path.Combine( "refs", "heads", "main" ) )
			&& GitSource.IsGitChange( Path.Combine( "worktrees", "login", "HEAD" ) ) && !GitSource.IsGitChange( "index.lock" )
			&& !GitSource.IsGitChange( Path.Combine( "objects", "ab", "cdef" ) ) && !GitSource.IsGitChange( Path.Combine( "logs", "HEAD" ) ),
			"Only HEAD, the index and refs, a linked worktree's own included, wake a read; objects, logs and locks do not." );
	}

	/// <summary>Reads a temporary repository and a plain folder, then watches the repository through the module while it changes.</summary>
	private static async Task CheckRepositoryAsync()
	{
		var root = Directory.CreateTempSubdirectory( "lokn-git-" ).FullName;
		var repository = Path.Combine( root, "app" );
		var plain = Directory.CreateDirectory( Path.Combine( root, "plain" ) ).FullName;
		Directory.CreateDirectory( repository );
		File.WriteAllText( Path.Combine( repository, "readme.txt" ), "one\ntwo\n" );
		var none = CancellationToken.None;
		await RunAsync( repository, "init", "--quiet", "--initial-branch=main" );

		var initial = await GitState.ReadAsync( LocalWorkspaceHost.Shared, repository, none );
		CoreChecks.Check( initial is { Branch: "main", Commit: null, Untracked: 1, Files: 1, Error: null } && IsSameFolder( initial.Root, repository ),
			"A repository without commits reads its branch and its untracked file." );

		await RunAsync( repository, "add", "." );
		await RunAsync( repository, "-c", "user.name=Lokn Checks", "-c", "user.email=checks@example.invalid", "commit", "--quiet", "-m", "Initial" );
		File.WriteAllText( Path.Combine( repository, "readme.txt" ), "one\nchanged\nthree\n" );
		Directory.CreateDirectory( Path.Combine( repository, "src" ) );
		File.WriteAllText( Path.Combine( repository, "src", "new.cs" ), "new" );

		var nested = await GitState.ReadAsync( LocalWorkspaceHost.Shared, Path.Combine( repository, "src" ), none );
		CoreChecks.Check( nested is { Branch: "main", Unstaged: 1, Untracked: 1, Files: 2, Added: 2, Removed: 1 } && nested.Commit is { Length: 7 } && IsSameFolder( nested.Root, repository ),
			"A folder inside a repository reads the whole working tree: the edit's lines and the new folder." );

		var outside = await GitState.ReadAsync( LocalWorkspaceHost.Shared, plain, none );
		CoreChecks.Check( outside is { IsRepository: false, Error: null }, "A folder outside any repository is no repository, and no error." );

		var app = new LoknApplication();
		var queued = new List<Action>();
		app.BindDispatcher( action =>
		{
			lock ( queued )
				queued.Add( action );
			return true;
		} );
		var module = new GitModule();
		CoreChecks.Check( app.Load( module ), "The Git module loads." );
		var first = module.Watch( app.Hosts.Local, repository );
		var second = module.Watch( app.Hosts.Local, repository + Path.DirectorySeparatorChar );
		CoreChecks.Check( PumpUntil( () => first.State is { Files: 2 } ) && ReferenceEquals( first.State, second.State ),
			"Two holders of one folder share one read state." );

		await RunAsync( repository, "add", "." );
		await RunAsync( repository, "-c", "user.name=Lokn Checks", "-c", "user.email=checks@example.invalid", "commit", "--quiet", "-m", "Second" );
		CoreChecks.Check( PumpUntil( () => first.State is { Files: 0, Added: 0 } ), "A commit made outside Lokn reaches the watch once its changes settle." );

		await RunAsync( repository, "checkout", "--quiet", "-b", "feature" );
		CoreChecks.Check( PumpUntil( () => second.State is { Branch: "feature" } ), "Switching branch reaches the watch." );

		first.Dispose();
		second.Dispose();
		app.Shutdown();

		bool PumpUntil( Func<bool> condition )
		{
			var clock = System.Diagnostics.Stopwatch.StartNew();
			while ( !condition() && clock.Elapsed.TotalSeconds < 20 )
			{
				Action[] actions;
				lock ( queued )
				{
					actions = [.. queued];
					queued.Clear();
				}
				foreach ( var action in actions )
					action();
				Thread.Sleep( 20 );
			}
			return condition();
		}
	}

	/// <summary>Watches one folder path on two fake paired hosts and opens Git panels beside panels of different hosts: each host gets its own reader, git and
	/// watches run on the host they belong to, a new workspace of one host restarts only its readers, and a Git panel is reused only on its own host.</summary>
	private static async Task CheckHostsAsync()
	{
		var repository = Directory.CreateTempSubdirectory( "lokn-git-hosts-" ).FullName;
		var plain = Directory.CreateTempSubdirectory( "lokn-git-hosts-plain-" ).FullName;
		File.WriteAllText( Path.Combine( repository, "readme.txt" ), "one\n" );
		await RunAsync( repository, "init", "--quiet", "--initial-branch=main" );
		var app = new LoknApplication();
		var queued = new List<Action>();
		app.BindDispatcher( action =>
		{
			lock ( queued )
				queued.Add( action );
			return true;
		} );
		try
		{
			var git = new GitModule();
			CoreChecks.Check( app.Load( git ), "The Git module loads for the host checks." );
			app.Settings.Set( GitModule.Avatars, SettingsScope.Global, false );
			var desk = app.Hosts.Add( "desk-host-id-000000000", "Desk" );
			var laptop = app.Hosts.Add( "laptop-host-id-0000000", "Laptop" );
			var deskDisk = new ReadCountingHost();
			var laptopDisk = new ReadCountingHost { IsDeaf = true };
			desk.SetWorkspace( deskDisk );
			laptop.SetWorkspace( laptopDisk );

			var onDesk = git.Watch( desk, repository );
			var onLaptop = git.Watch( laptop, repository );
			CoreChecks.Check( PumpUntil( () => onDesk.State is { IsRepository: true } && onLaptop.State is { IsRepository: true } ) && !ReferenceEquals( onDesk.State, onLaptop.State )
				&& onDesk.Host == desk && onLaptop.Host == laptop,
				"The same folder on two hosts has two readers, each with its own state." );
			CoreChecks.Check( Count( deskDisk.Runs ) > 0 && Count( laptopDisk.Runs ) > 0 && Count( deskDisk.Watches ) > 0 && Count( laptopDisk.Watches ) > 0,
				"Each host's reader runs git and watches files on that host." );

			var deskAgain = new ReadCountingHost();
			// The laptop's first reads may still run; its count is taken once it has not moved for half a second.
			var laptopRuns = -1;
			var still = System.Diagnostics.Stopwatch.StartNew();
			PumpUntil( () =>
			{
				var now = Count( laptopDisk.Runs );
				if ( now != laptopRuns )
				{
					laptopRuns = now;
					still.Restart();
				}
				return still.ElapsedMilliseconds >= 500;
			} );
			var laptopWatches = Count( laptopDisk.Watches );
			desk.SetWorkspace( deskAgain );
			CoreChecks.Check( PumpUntil( () => Count( deskAgain.Runs ) > 0 && Count( deskAgain.Watches ) > 0 ) && Count( laptopDisk.Runs ) == laptopRuns && Count( laptopDisk.Watches ) == laptopWatches,
				"A new workspace of one host reads and watches that host's folders again, and leaves the other host's readers alone." );
			onDesk.Dispose();
			onLaptop.Dispose();

			var deskPanel = app.Panels.Open( GitModule.PanelType, input: new GitPanelInput( repository ), hostId: desk.Id );
			var laptopPlain = app.Panels.Open( GitModule.PanelType, input: new GitPanelInput( plain ), hostId: laptop.Id );
			git.OpenPanel( app, deskPanel, repository );
			CoreChecks.Check( app.Panels.Panels.Count == 2 && app.Panels.GetHostId( deskPanel ) == desk.Id, "Opening the repository's Git panel beside a panel on its host reuses that host's panel." );
			git.OpenPanel( app, laptopPlain, repository );
			var opened = app.Panels.Panels.Select( panel => panel.Id ).Except( [deskPanel, laptopPlain] ).ToArray();
			CoreChecks.Check( opened is [var laptopPanel] && app.Panels.GetHostId( laptopPanel ) == laptop.Id,
				"Beside a panel on another host, the same path opens a new Git panel on that host instead of reusing the first host's." );
			PumpUntil( () => false, seconds: 1 );
			CoreChecks.Check( Count( deskAgain.Runs ) > 0 && Count( laptopDisk.Runs ) > laptopRuns, "Each Git panel reads its repository through its own host." );
			CoreChecks.Check( app.LastError is null, $"Git panels on paired hosts build without errors. {app.LastError}" );
		}
		finally
		{
			app.Shutdown();
			ChatChecks.DeleteRepository( repository );
			Directory.Delete( plain, recursive: true );
		}

		static int Count( List<string> recorded )
		{
			lock ( recorded )
				return recorded.Count;
		}

		bool PumpUntil( Func<bool> condition, double seconds = 20 )
		{
			var clock = System.Diagnostics.Stopwatch.StartNew();
			while ( !condition() && clock.Elapsed.TotalSeconds < seconds )
			{
				Action[] actions;
				lock ( queued )
				{
					actions = [.. queued];
					queued.Clear();
				}
				foreach ( var action in actions )
					action();
				Thread.Sleep( 20 );
			}
			return condition();
		}
	}

	/// <summary>Verifies graph lanes and lines for a linear history, a branch and merge, and two roots with a cut-off parent.</summary>
	private static void CheckGraph()
	{
		var linear = GitGraph.Layout( [Commit( "c", "b" ), Commit( "b", "a" ), Commit( "a" )] );
		CoreChecks.Check( Describe( linear ) == "0/0/1 [] [0>0:0] | 0/0/1 [0>0:0] [0>0:0] | 0/0/1 [0>0:0] []",
			"A linear history stays in one lane and one color, its line ending at the root." );

		var merge = GitGraph.Layout( [Commit( "m", "a", "b" ), Commit( "a", "f" ), Commit( "b", "f" ), Commit( "f" )] );
		CoreChecks.Check( Describe( merge ) == "0/0/2 [] [0>0:0,0>1:1] | 0/0/2 [0>0:0,1>1:1] [0>0:0,1>1:1] | 1/1/2 [0>0:0,1>1:1] [0>0:0,1>1:1] | 0/0/2 [0>0:0,1>0:1] []",
			"A merge's second parent opens a lane with a new color, which joins the first lane again at the fork point." );

		var roots = GitGraph.Layout( [Commit( "n", "p", "q" ), Commit( "p" ), Commit( "z" ), Commit( "q" ), Commit( "x", "missing" ), Commit( "y" )] );
		CoreChecks.Check( Describe( roots ) == "0/0/2 [] [0>0:0,0>1:1] | 0/0/2 [0>0:0,1>1:1] [1>1:1] | 0/2/2 [1>1:1] [1>1:1] | 1/1/2 [1>1:1] [] | 0/3/1 [] [0>0:3] | 0/4/1 [] []",
			"Two roots end their lanes, a new tip reuses the freed first lane, and a parent cut off from the history ends at the bottom of its child's row." );

		static GitCommit Commit( string id, params string[] parents ) => new( id, id, parents, "", "", default, "", "", default, id, "" );

		static string Describe( IReadOnlyList<GitGraphRow> rows ) =>
			string.Join( " | ", rows.Select( row => $"{row.Lane}/{row.Color}/{row.Width} [{Lines( row.Up )}] [{Lines( row.Down )}]" ) );

		static string Lines( IReadOnlyList<GitGraphLine> lines ) =>
			string.Join( ",", lines.OrderBy( line => line.From ).ThenBy( line => line.To ).Select( line => $"{line.From}>{line.To}:{line.Color}" ) );
	}

	/// <summary>Checks that line notes enter Omp's review command and its structured tail becomes inline findings.</summary>
	private static void CheckReviewWorkflow()
	{
		var prompt = GitReviewChat.BuildPrompt(
			@"C:\repo with spaces",
			[new GitReviewAnnotation( new( "src/Auth.cs", 42, DiffLineKind.Added, "return token;" ), "Check token lifetime." )] );
		CoreChecks.Check( prompt.StartsWith( """/review "C:\repo with spaces" Review the uncommitted changes.""", StringComparison.Ordinal )
			&& prompt.Contains( "`src/Auth.cs:42`: Check token lifetime.", StringComparison.Ordinal )
			&& prompt.Contains( "`+return token;`", StringComparison.Ordinal ), "A Git review prompt keeps its repository and operator line annotation." );

		var messages = new[]
		{
			new ConversationMessage( Guid.NewGuid(), "assistant", """
				Review complete.
				<lokn-review>{"overall_correctness":"incorrect","explanation":"Token escape.","confidence":0.95,"findings":[{"title":"Token survives logout","body":"Clear it.","priority":1,"confidence":0.9,"file_path":"src/Auth.cs","line_start":42,"line_end":43}]}</lokn-review>
				""", DateTimeOffset.UtcNow ),
		};
		var result = GitReviewChat.ParseResult( messages );
		CoreChecks.Check( result is { OverallCorrectness: "incorrect", Confidence: 0.95, Findings.Count: 1 }
			&& result.Findings[0] is { Title: "Token survives logout", Priority: 1, FilePath: "src/Auth.cs", LineStart: 42, LineEnd: 43 },
			"The Git panel reads the review verdict and P0-P3 line findings from Omp's final machine-readable block." );

		CoreChecks.Check( GitReviewChat.ParseMessage( "```\nAdd token expiry\n\nTokens outlived logout.\n```", "" ) == new GitMessage( "Add token expiry", "Tokens outlived logout." )
			&& GitReviewChat.ParseMessage( "Other summary\n\nWhy it changed.", "Typed summary" ) == new GitMessage( "Typed summary", "Why it changed." ),
			"A written commit message splits into summary and why, keeping a summary the user typed." );
		CoreChecks.Check( GitReviewChat.BuildAction( new ConversationsModule( new ConversationStore( new ProjectStore() ), ( _, _, _ ) => throw new InvalidOperationException() ) )
			is { Key: GitReviewChat.ActionKey, Scope: GitActionScope.Changes, ReadReview: not null, OpenReview: not null },
			"Conversations adds review in chat to the Git views' working-tree actions." );
	}

	/// <summary>Runs every repository command family against a temporary repository, a second clone and a local bare remote.</summary>
	private static async Task CheckCommandsAsync()
	{
		var none = CancellationToken.None;
		var root = Directory.CreateTempSubdirectory( "lokn-git-commands-" ).FullName;
		var origin = Path.Combine( root, "origin.git" );
		var app = Path.Combine( root, "app" );
		Directory.CreateDirectory( app );
		await RunAsync( root, "init", "--quiet", "--bare", "--initial-branch=main", origin );
		await RunAsync( app, "init", "--quiet", "--initial-branch=main" );
		await ConfigureAsync( app );

		CoreChecks.Check( await GitRepository.ReadLogAsync( LocalWorkspaceHost.Shared, app, 100, none ) is [] && await GitRepository.ReadHeadMessageAsync( LocalWorkspaceHost.Shared, app, none ) == ""
			&& await GitRepository.ReadRefsAsync( LocalWorkspaceHost.Shared, app, none ) is [] && await GitRepository.ReadStashesAsync( LocalWorkspaceHost.Shared, app, none ) is []
			&& await GitRepository.ReadOperationAsync( LocalWorkspaceHost.Shared, app, none ) == GitOperation.None,
			"A repository without commits has no history, references, stashes, head message or operation." );

		await CheckWorkingTreeAsync( app );
		await CheckHistoryAsync( app );
		await CheckRemotesAsync( root, app, origin );
		await CheckStashesAsync( app );
	}

	/// <summary>Verifies reading, staging, unstaging, discarding and committing changes, and reading commits back.</summary>
	private static async Task CheckWorkingTreeAsync( string app )
	{
		var none = CancellationToken.None;
		Write( app, "readme.txt", "one\ntwo\n" );
		var changes = await GitRepository.ReadChangesAsync( LocalWorkspaceHost.Shared, app, none );
		CoreChecks.Check( changes is { Staged: [], Unstaged: [{ Path: "readme.txt", OldPath: null, Status: '?', Added: 2, Removed: 0, IsBinary: false }] },
			"An untracked file is an unstaged change counted from its lines." );
		var patch = await GitRepository.ReadWorkingPatchAsync( LocalWorkspaceHost.Shared, app, changes.Unstaged[0], false, none );
		CoreChecks.Check( patch.Contains( "+++ b/readme.txt", StringComparison.Ordinal ) && patch.Contains( "\n+two", StringComparison.Ordinal ),
			"An untracked file's patch adds every line." );

		await GitRepository.StageAsync( LocalWorkspaceHost.Shared, app, [], none );
		CoreChecks.Check( await GitRepository.ReadChangesAsync( LocalWorkspaceHost.Shared, app, none ) is { Unstaged: [], Staged: [{ Path: "readme.txt", Status: 'A', Added: 2, Removed: 0 }] },
			"Staging everything before the first commit adds the file with its lines." );
		await GitRepository.UnstageAsync( LocalWorkspaceHost.Shared, app, [], none );
		CoreChecks.Check( await GitRepository.ReadChangesAsync( LocalWorkspaceHost.Shared, app, none ) is { Staged: [], Unstaged: [{ Path: "readme.txt", Status: '?' }] },
			"Unstaging everything works before the first commit." );

		await GitRepository.StageAsync( LocalWorkspaceHost.Shared, app, ["readme.txt"], none );
		await GitRepository.CommitAsync( LocalWorkspaceHost.Shared, app, "Initial", false, none );
		CoreChecks.Check( await GitRepository.ReadHeadMessageAsync( LocalWorkspaceHost.Shared, app, none ) == "Initial", "A commit's message reads back for an amend." );
		await GitRepository.CommitAsync( LocalWorkspaceHost.Shared, app, "First commit\n\nWith a body.\n", true, none );
		var log = await GitRepository.ReadLogAsync( LocalWorkspaceHost.Shared, app, 100, none );
		CoreChecks.Check( log is [{ Subject: "First commit", Body: "With a body.", Parents: [], AuthorName: "Lokn Checks", AuthorEmail: "checks@example.invalid", CommitterName: "Lokn Checks" }]
			&& log[0].Id.Length == 40 && log[0].Id.StartsWith( log[0].ShortId, StringComparison.Ordinal ) && Math.Abs( (DateTimeOffset.Now - log[0].CommitDate).TotalHours ) < 1
			&& Math.Abs( (DateTimeOffset.Now - log[0].AuthorDate).TotalHours ) < 1,
			"Amending replaces the only commit's message; the log reads its identifiers, subject, body, people and dates." );

		Write( app, "readme.txt", "one\nchanged\nthree\n" );
		File.WriteAllBytes( Path.Combine( app, "image.bin" ), [0, 1, 2, 0, 3] );
		CoreChecks.Check( await GitRepository.ReadChangesAsync( LocalWorkspaceHost.Shared, app, none ) is
			{ Staged: [], Unstaged: [{ Path: "readme.txt", Status: 'M', Added: 2, Removed: 1 }, { Path: "image.bin", Status: '?', Added: 0, IsBinary: true }] },
			"An edit counts its lines, and an untracked binary file is flagged without lines." );
		await GitRepository.StageAsync( LocalWorkspaceHost.Shared, app, ["readme.txt"], none );
		changes = await GitRepository.ReadChangesAsync( LocalWorkspaceHost.Shared, app, none );
		CoreChecks.Check( changes is { Staged: [{ Path: "readme.txt", Status: 'M', Added: 2, Removed: 1 }], Unstaged: [{ Path: "image.bin" }] },
			"Staging one path moves only its change." );
		patch = await GitRepository.ReadWorkingPatchAsync( LocalWorkspaceHost.Shared, app, changes.Staged[0], true, none );
		CoreChecks.Check( patch.Contains( "\n+changed", StringComparison.Ordinal ) && patch.Contains( "\n-two", StringComparison.Ordinal ),
			"A staged change's patch compares the index with HEAD." );
		await GitRepository.UnstageAsync( LocalWorkspaceHost.Shared, app, ["readme.txt"], none );
		changes = await GitRepository.ReadChangesAsync( LocalWorkspaceHost.Shared, app, none );
		CoreChecks.Check( changes is { Staged: [], Unstaged: [{ Path: "readme.txt", Status: 'M' }, { Path: "image.bin" }] },
			"Unstaging one path keeps its edit in the working tree." );
		await GitRepository.DiscardAsync( LocalWorkspaceHost.Shared, app, changes.Unstaged, none );
		CoreChecks.Check( await GitRepository.ReadChangesAsync( LocalWorkspaceHost.Shared, app, none ) is { Staged: [], Unstaged: [] }
			&& File.ReadAllText( Path.Combine( app, "readme.txt" ) ) == "one\ntwo\n" && !File.Exists( Path.Combine( app, "image.bin" ) ),
			"Discarding restores an edit from HEAD and deletes an untracked file." );

		await RunAsync( app, "mv", "readme.txt", "notes.txt" );
		CoreChecks.Check( await GitRepository.ReadChangesAsync( LocalWorkspaceHost.Shared, app, none ) is { Staged: [{ Path: "notes.txt", OldPath: "readme.txt", Status: 'R' }], Unstaged: [] },
			"A staged rename names its old path." );
		await GitRepository.CommitAsync( LocalWorkspaceHost.Shared, app, "Rename the notes", false, none );
		log = await GitRepository.ReadLogAsync( LocalWorkspaceHost.Shared, app, 100, none );
		var renamed = await GitRepository.ReadCommitFilesAsync( LocalWorkspaceHost.Shared, app, log[0].Id, none );
		var initial = await GitRepository.ReadCommitFilesAsync( LocalWorkspaceHost.Shared, app, log[1].Id, none );
		var renamePatch = await GitRepository.ReadCommitPatchAsync( LocalWorkspaceHost.Shared, app, log[0].Id, "notes.txt", none );
		var initialPatch = await GitRepository.ReadCommitPatchAsync( LocalWorkspaceHost.Shared, app, log[1].Id, null, none );
		CoreChecks.Check( renamed is [{ Path: "notes.txt", OldPath: "readme.txt", Status: 'R', Added: 0, Removed: 0 }]
			&& initial is [{ Path: "readme.txt", OldPath: null, Status: 'A', Added: 2, Removed: 0, IsBinary: false }]
			&& renamePatch.Contains( "rename from readme.txt", StringComparison.Ordinal ) && initialPatch.Contains( "+++ b/readme.txt", StringComparison.Ordinal ),
			"Commit files compare with the first parent or, for a root commit, the empty tree; a one-file patch keeps its rename." );

		const string odd = "Odd: ä ✓ \"q\" %H %x00 \\ <b> tab\there";
		Write( app, "odd.txt", "odd\n" );
		await GitRepository.StageAsync( LocalWorkspaceHost.Shared, app, ["odd.txt"], none );
		await GitRepository.CommitAsync( LocalWorkspaceHost.Shared, app, odd + "\n\nBody line 1\nBody line 2\n", false, none );
		log = await GitRepository.ReadLogAsync( LocalWorkspaceHost.Shared, app, 100, none );
		CoreChecks.Check( log is [{ Subject: odd, Body: "Body line 1\nBody line 2", Parents: [var parent] }, _, _] && parent == log[1].Id,
			"A subject with quotes, placeholders, a tab and non-ASCII text reads back exactly, with its body and parent." );
		CoreChecks.Check( await GitRepository.ReadLogAsync( LocalWorkspaceHost.Shared, app, 2, none ) is [_, _], "The log stops at its limit." );
	}

	/// <summary>Verifies branches, a conflicting merge aborted and then continued, detached checkout, history edits and tags.</summary>
	private static async Task CheckHistoryAsync( string app )
	{
		var none = CancellationToken.None;
		await GitRepository.CreateBranchAsync( LocalWorkspaceHost.Shared, app, "feature", "HEAD", true, none );
		var refs = await GitRepository.ReadRefsAsync( LocalWorkspaceHost.Shared, app, none );
		CoreChecks.Check( refs.Single( item => item.IsCurrent ) is { ShortName: "feature", Name: "refs/heads/feature", Kind: GitRefKind.LocalBranch, Remote: null, Upstream: null }
			&& refs.Any( item => item is { ShortName: "main", IsCurrent: false } ),
			"Creating a branch with checkout makes it the current branch." );
		await CommitFileAsync( app, "notes.txt", "one\nfeature\n", "Feature edit" );
		await GitRepository.RenameBranchAsync( LocalWorkspaceHost.Shared, app, "feature", "topic", none );
		refs = await GitRepository.ReadRefsAsync( LocalWorkspaceHost.Shared, app, none );
		CoreChecks.Check( refs.Single( item => item.IsCurrent ).ShortName == "topic" && !refs.Any( item => item.ShortName == "feature" ),
			"Renaming the current branch keeps it current." );
		var topic = refs.Single( item => item.ShortName == "topic" );

		await GitRepository.CheckoutAsync( LocalWorkspaceHost.Shared, app, refs.Single( item => item.ShortName == "main" ), none );
		await CommitFileAsync( app, "notes.txt", "one\nmain\n", "Main edit" );
		CoreChecks.Check( await ThrowsAsync( () => GitRepository.MergeAsync( LocalWorkspaceHost.Shared, app, "topic", none ) ), "A conflicting merge fails with git's message." );
		var changes = await GitRepository.ReadChangesAsync( LocalWorkspaceHost.Shared, app, none );
		CoreChecks.Check( await GitRepository.ReadOperationAsync( LocalWorkspaceHost.Shared, app, none ) == GitOperation.Merge && changes is { Unstaged: [{ Path: "notes.txt", Status: 'U' }] },
			"A stopped merge reads as a merge, its conflicted file unstaged." );
		var conflict = await GitRepository.ReadWorkingPatchAsync( LocalWorkspaceHost.Shared, app, changes.Unstaged[0], false, none );
		CoreChecks.Check( conflict.Contains( "\n+<<<<<<< ", StringComparison.Ordinal ), "A conflicted file's patch shows its markers as added lines." );
		await GitRepository.AbortAsync( LocalWorkspaceHost.Shared, app, GitOperation.Merge, none );
		CoreChecks.Check( await GitRepository.ReadOperationAsync( LocalWorkspaceHost.Shared, app, none ) == GitOperation.None && await GitRepository.ReadChangesAsync( LocalWorkspaceHost.Shared, app, none ) is { Staged: [], Unstaged: [] },
			"Aborting a merge returns to the branch as it was." );

		CoreChecks.Check( await ThrowsAsync( () => GitRepository.MergeAsync( LocalWorkspaceHost.Shared, app, "topic", none ) ), "The merge conflicts again." );
		Write( app, "notes.txt", "one\nboth\n" );
		await GitRepository.StageAsync( LocalWorkspaceHost.Shared, app, ["notes.txt"], none );
		await GitRepository.ContinueAsync( LocalWorkspaceHost.Shared, app, GitOperation.Merge, none );
		var log = await GitRepository.ReadLogAsync( LocalWorkspaceHost.Shared, app, 100, none );
		CoreChecks.Check( await GitRepository.ReadOperationAsync( LocalWorkspaceHost.Shared, app, none ) == GitOperation.None && log[0] is { Parents: [_, var second] } && second == topic.Target
			&& log[0].Subject.StartsWith( "Merge branch 'topic'", StringComparison.Ordinal ),
			"Continuing a resolved merge commits it with both parents and git's message, without an editor." );
		var rows = GitGraph.Layout( log );
		CoreChecks.Check( rows.Count == log.Count && rows[0] is { Lane: 0, Down.Count: 2 } && rows.Max( row => row.Width ) == 2,
			"The real history lays out with the merge's second parent in a second lane." );

		await GitRepository.DeleteBranchAsync( LocalWorkspaceHost.Shared, app, topic, false, none );
		CoreChecks.Check( !(await GitRepository.ReadRefsAsync( LocalWorkspaceHost.Shared, app, none )).Any( item => item.ShortName == "topic" ), "A merged branch deletes." );
		await GitRepository.CheckoutCommitAsync( LocalWorkspaceHost.Shared, app, log[0].Parents[0], none );
		CoreChecks.Check( (await GitRepository.ReadRefsAsync( LocalWorkspaceHost.Shared, app, none )).All( item => !item.IsCurrent ) && await HeadAsync( app ) == log[0].Parents[0],
			"Checking out a commit detaches HEAD." );
		await GitRepository.CheckoutAsync( LocalWorkspaceHost.Shared, app, await RefAsync( app, "main" ), none );

		var extra = await CommitFileAsync( app, "extra.txt", "extra\n", "Add extra" );
		await GitRepository.RevertAsync( LocalWorkspaceHost.Shared, app, extra, none );
		CoreChecks.Check( !File.Exists( Path.Combine( app, "extra.txt" ) ) && (await GitRepository.ReadLogAsync( LocalWorkspaceHost.Shared, app, 1, none )) is [{ Subject: "Revert \"Add extra\"" }],
			"Reverting adds a commit that undoes the change, with git's message." );
		await GitRepository.ResetAsync( LocalWorkspaceHost.Shared, app, extra, GitResetMode.Hard, none );
		CoreChecks.Check( File.Exists( Path.Combine( app, "extra.txt" ) ) && await HeadAsync( app ) == extra && await GitRepository.ReadChangesAsync( LocalWorkspaceHost.Shared, app, none ) is { Staged: [], Unstaged: [] },
			"A hard reset moves the branch and the working tree." );
		await GitRepository.ResetAsync( LocalWorkspaceHost.Shared, app, extra + "~1", GitResetMode.Soft, none );
		CoreChecks.Check( await GitRepository.ReadChangesAsync( LocalWorkspaceHost.Shared, app, none ) is { Staged: [{ Path: "extra.txt", Status: 'A' }], Unstaged: [] }, "A soft reset keeps the change staged." );
		await GitRepository.ResetAsync( LocalWorkspaceHost.Shared, app, "HEAD", GitResetMode.Mixed, none );
		CoreChecks.Check( await GitRepository.ReadChangesAsync( LocalWorkspaceHost.Shared, app, none ) is { Staged: [], Unstaged: [{ Path: "extra.txt", Status: '?' }] }, "A mixed reset unstages the change." );
		await GitRepository.ResetAsync( LocalWorkspaceHost.Shared, app, extra, GitResetMode.Hard, none );

		await GitRepository.CreateBranchAsync( LocalWorkspaceHost.Shared, app, "pick", "HEAD", true, none );
		var pick = await CommitFileAsync( app, "pick.txt", "pick\n", "Pick me" );
		await GitRepository.CheckoutAsync( LocalWorkspaceHost.Shared, app, await RefAsync( app, "main" ), none );
		// Main moves first, so the copy gets another parent and can never be byte-identical to the original.
		await CommitFileAsync( app, "before-pick.txt", "main\n", "Main before the pick" );
		await GitRepository.CherryPickAsync( LocalWorkspaceHost.Shared, app, pick, none );
		var current = (await GitRepository.ReadRefsAsync( LocalWorkspaceHost.Shared, app, none )).Single( reference => reference.IsCurrent );
		CoreChecks.Check( File.Exists( Path.Combine( app, "pick.txt" ) ) && current.ShortName == "main" && current.Target != pick
			&& (await GitRepository.ReadLogAsync( LocalWorkspaceHost.Shared, app, 0, none )).Single( commit => commit.Id == current.Target ).Subject == "Pick me",
			"Cherry-picking copies a commit onto the current branch." );
		var pickBranch = await RefAsync( app, "pick" );
		CoreChecks.Check( await ThrowsAsync( () => GitRepository.DeleteBranchAsync( LocalWorkspaceHost.Shared, app, pickBranch, false, none ) ), "An unmerged branch does not delete without force." );
		await GitRepository.DeleteBranchAsync( LocalWorkspaceHost.Shared, app, pickBranch, true, none );

		await GitRepository.CreateBranchAsync( LocalWorkspaceHost.Shared, app, "later", "HEAD", false, none );
		var mainTip = await CommitFileAsync( app, "main2.txt", "main\n", "Main moves on" );
		await GitRepository.CheckoutAsync( LocalWorkspaceHost.Shared, app, await RefAsync( app, "later" ), none );
		await CommitFileAsync( app, "later.txt", "later\n", "Later work" );
		await GitRepository.RebaseAsync( LocalWorkspaceHost.Shared, app, "main", none );
		CoreChecks.Check( (await GitRepository.ReadLogAsync( LocalWorkspaceHost.Shared, app, 1, none )) is [{ Subject: "Later work", Parents: [var onto] }] && onto == mainTip
			&& (await RefAsync( app, "later" )).IsCurrent,
			"Rebasing replays the branch onto the new base." );
		await GitRepository.CheckoutAsync( LocalWorkspaceHost.Shared, app, await RefAsync( app, "main" ), none );
		await GitRepository.DeleteBranchAsync( LocalWorkspaceHost.Shared, app, await RefAsync( app, "later" ), true, none );

		await GitRepository.CreateTagAsync( LocalWorkspaceHost.Shared, app, "v1.0", "HEAD", "Release one", none );
		await GitRepository.CreateTagAsync( LocalWorkspaceHost.Shared, app, "light", mainTip + "~1", null, none );
		var parentOfTip = (await GitProcess.RunAsync( LocalWorkspaceHost.Shared, app, none, "rev-parse", mainTip + "~1" )).Trim();
		refs = await GitRepository.ReadRefsAsync( LocalWorkspaceHost.Shared, app, none );
		CoreChecks.Check( refs.Single( item => item.ShortName == "v1.0" ) is { Kind: GitRefKind.Tag, Name: "refs/tags/v1.0" } release && release.Target == mainTip
			&& refs.Single( item => item.ShortName == "light" ).Target == parentOfTip,
			"Annotated and lightweight tags both point at their commit." );
		await GitRepository.DeleteTagAsync( LocalWorkspaceHost.Shared, app, "light", none );
		CoreChecks.Check( !(await GitRepository.ReadRefsAsync( LocalWorkspaceHost.Shared, app, none )).Any( item => item.ShortName == "light" ), "A tag deletes." );
	}

	/// <summary>Verifies push with and without an upstream, tags, fetch, pull both ways, remote branch checkout and deletion, and a forced push.</summary>
	private static async Task CheckRemotesAsync( string root, string app, string origin )
	{
		var none = CancellationToken.None;
		var other = Path.Combine( root, "other" );
		await RunAsync( app, "remote", "add", "origin", origin );
		await GitRepository.PushAsync( LocalWorkspaceHost.Shared, app, null, false, none );
		var refs = await GitRepository.ReadRefsAsync( LocalWorkspaceHost.Shared, app, none );
		CoreChecks.Check( refs.Single( item => item.ShortName == "main" ) is { Upstream: "origin/main", Ahead: 0, Behind: 0, IsUpstreamGone: false, IsCurrent: true }
			&& refs.Any( item => item is { ShortName: "origin/main", Name: "refs/remotes/origin/main", Kind: GitRefKind.RemoteBranch, Remote: "origin" } ),
			"Pushing a branch without an upstream pushes it to origin and tracks it there." );
		await GitRepository.PushTagAsync( LocalWorkspaceHost.Shared, app, "v1.0", none );
		CoreChecks.Check( (await GitProcess.RunAsync( LocalWorkspaceHost.Shared, origin, none, "for-each-ref", "--format=%(refname)", "refs/tags" )).Contains( "refs/tags/v1.0", StringComparison.Ordinal ),
			"Pushing a tag sends it to the remote." );

		await RunAsync( root, "clone", "--quiet", "--config", "core.autocrlf=false", origin, other );
		await ConfigureAsync( other );
		var cloned = await GitRepository.ReadRefsAsync( LocalWorkspaceHost.Shared, other, none );
		CoreChecks.Check( cloned.Any( item => item.ShortName == "origin/main" ) && !cloned.Any( item => item.ShortName.EndsWith( "/HEAD", StringComparison.Ordinal ) )
			&& cloned.Any( item => item is { ShortName: "v1.0", Kind: GitRefKind.Tag } ),
			"A clone lists its remote branches without the remote's HEAD alias, and the pushed tag." );

		var remoteWork = await CommitFileAsync( other, "remote.txt", "from the other clone\n", "Remote work" );
		await GitRepository.PushAsync( LocalWorkspaceHost.Shared, other, null, false, none );
		await CommitFileAsync( app, "local.txt", "local\n", "Local work" );
		CoreChecks.Check( await RefAsync( app, "main" ) is { Ahead: 1, Behind: 0 }, "A local commit puts the branch ahead of its upstream." );
		await GitRepository.FetchAsync( LocalWorkspaceHost.Shared, app, none );
		CoreChecks.Check( await RefAsync( app, "main" ) is { Ahead: 1, Behind: 1 }, "Fetching finds the remote commit the branch lacks." );
		await GitRepository.PullAsync( LocalWorkspaceHost.Shared, app, true, none );
		CoreChecks.Check( await RefAsync( app, "main" ) is { Ahead: 1, Behind: 0 } && File.Exists( Path.Combine( app, "remote.txt" ) )
			&& (await GitRepository.ReadLogAsync( LocalWorkspaceHost.Shared, app, 1, none )) is [{ Subject: "Local work", Parents: [var based] }] && based == remoteWork,
			"Pulling with rebase replays the local commit on the remote one." );
		await GitRepository.PushAsync( LocalWorkspaceHost.Shared, app, await RefAsync( app, "main" ), false, none );
		CoreChecks.Check( await RefAsync( app, "main" ) is { Ahead: 0, Behind: 0 }, "Pushing a named branch to its upstream brings it level." );
		await GitRepository.PullAsync( LocalWorkspaceHost.Shared, other, false, none );
		CoreChecks.Check( await HeadAsync( other ) == await HeadAsync( app ) && File.Exists( Path.Combine( other, "local.txt" ) ), "Pulling with merge fast-forwards the other clone." );

		await GitRepository.CreateBranchAsync( LocalWorkspaceHost.Shared, app, "shared", "HEAD", false, none );
		await GitRepository.PushAsync( LocalWorkspaceHost.Shared, app, await RefAsync( app, "shared" ), false, none );
		await GitRepository.FetchAsync( LocalWorkspaceHost.Shared, other, none );
		var remoteShared = await RefAsync( other, "origin/shared" );
		await GitRepository.CheckoutAsync( LocalWorkspaceHost.Shared, other, remoteShared, none );
		CoreChecks.Check( (await GitRepository.ReadRefsAsync( LocalWorkspaceHost.Shared, other, none )).Single( item => item.IsCurrent ) is { ShortName: "shared", Upstream: "origin/shared", Kind: GitRefKind.LocalBranch },
			"Checking out a remote branch creates a local branch tracking it." );
		await GitRepository.CheckoutAsync( LocalWorkspaceHost.Shared, other, await RefAsync( other, "main" ), none );
		await GitRepository.CheckoutAsync( LocalWorkspaceHost.Shared, other, remoteShared, none );
		CoreChecks.Check( (await RefAsync( other, "shared" )).IsCurrent, "Checking out a remote branch again uses its local branch." );

		await GitRepository.DeleteBranchAsync( LocalWorkspaceHost.Shared, app, await RefAsync( app, "origin/shared" ), false, none );
		await GitRepository.FetchAsync( LocalWorkspaceHost.Shared, other, none );
		var pruned = await GitRepository.ReadRefsAsync( LocalWorkspaceHost.Shared, other, none );
		CoreChecks.Check( pruned.Single( item => item.ShortName == "shared" ) is { IsUpstreamGone: true, Upstream: "origin/shared" } && !pruned.Any( item => item.ShortName == "origin/shared" ),
			"Deleting a remote branch pushes the deletion; a fetch prunes it and marks the tracking branch gone." );

		await GitRepository.ResetAsync( LocalWorkspaceHost.Shared, app, "HEAD~1", GitResetMode.Hard, none );
		CoreChecks.Check( await ThrowsAsync( () => GitRepository.PushAsync( LocalWorkspaceHost.Shared, app, null, false, none ) ), "A push that would drop remote commits fails without force." );
		await GitRepository.PushAsync( LocalWorkspaceHost.Shared, app, null, true, none );
		CoreChecks.Check( (await GitProcess.RunAsync( LocalWorkspaceHost.Shared, origin, none, "rev-parse", "refs/heads/main" )).Trim() == await HeadAsync( app ), "A forced push replaces the remote branch." );
	}

	/// <summary>Verifies stashing with untracked files, applying, popping and dropping.</summary>
	private static async Task CheckStashesAsync( string app )
	{
		var none = CancellationToken.None;
		Write( app, "notes.txt", "stashed edit\n" );
		Write( app, "draft.txt", "draft\n" );
		await GitRepository.StashAsync( LocalWorkspaceHost.Shared, app, "Work in progress", true, none );
		var stashes = await GitRepository.ReadStashesAsync( LocalWorkspaceHost.Shared, app, none );
		CoreChecks.Check( stashes is [{ Index: 0, Name: "stash@{0}" } stash] && stash.Message.EndsWith( "Work in progress", StringComparison.Ordinal ) && stash.Commit.Length == 40
			&& await GitRepository.ReadChangesAsync( LocalWorkspaceHost.Shared, app, none ) is { Staged: [], Unstaged: [] } && !File.Exists( Path.Combine( app, "draft.txt" ) ),
			"Stashing with untracked files saves and clears every change." );
		CoreChecks.Check( !(await GitRepository.ReadLogAsync( LocalWorkspaceHost.Shared, app, 0, none )).Any( commit => commit.Id == stashes[0].Commit ), "The history leaves stash commits out." );

		await GitRepository.StashApplyAsync( LocalWorkspaceHost.Shared, app, 0, false, none );
		var changes = await GitRepository.ReadChangesAsync( LocalWorkspaceHost.Shared, app, none );
		CoreChecks.Check( changes is { Unstaged: [{ Path: "notes.txt", Status: 'M' }, { Path: "draft.txt", Status: '?' }] } && await GitRepository.ReadStashesAsync( LocalWorkspaceHost.Shared, app, none ) is [_],
			"Applying a stash restores its changes and keeps it." );
		await GitRepository.DiscardAsync( LocalWorkspaceHost.Shared, app, changes.Unstaged, none );
		await GitRepository.StashApplyAsync( LocalWorkspaceHost.Shared, app, 0, true, none );
		CoreChecks.Check( await GitRepository.ReadChangesAsync( LocalWorkspaceHost.Shared, app, none ) is { Unstaged: [_, _] } && await GitRepository.ReadStashesAsync( LocalWorkspaceHost.Shared, app, none ) is [],
			"Popping a stash restores its changes and drops it." );

		await GitRepository.StashAsync( LocalWorkspaceHost.Shared, app, null, true, none );
		await GitRepository.StashDropAsync( LocalWorkspaceHost.Shared, app, 0, none );
		CoreChecks.Check( await GitRepository.ReadStashesAsync( LocalWorkspaceHost.Shared, app, none ) is [] && await GitRepository.ReadChangesAsync( LocalWorkspaceHost.Shared, app, none ) is { Staged: [], Unstaged: [] },
			"Dropping a stash deletes it." );
	}

	/// <summary>Configures a fixture repository's identity, signing and line endings, so its commits do not depend on the user's settings.</summary>
	private static async Task ConfigureAsync( string repository )
	{
		await RunAsync( repository, "config", "user.name", "Lokn Checks" );
		await RunAsync( repository, "config", "user.email", "checks@example.invalid" );
		await RunAsync( repository, "config", "commit.gpgSign", "false" );
		await RunAsync( repository, "config", "tag.gpgSign", "false" );
		await RunAsync( repository, "config", "core.autocrlf", "false" );
	}

	/// <summary>Writes a file, stages it and commits it through the repository commands, returning the new commit's identifier.</summary>
	private static async Task<string> CommitFileAsync( string repository, string path, string text, string message )
	{
		Write( repository, path, text );
		await GitRepository.StageAsync( LocalWorkspaceHost.Shared, repository, [path], CancellationToken.None );
		await GitRepository.CommitAsync( LocalWorkspaceHost.Shared, repository, message, false, CancellationToken.None );
		return await HeadAsync( repository );
	}

	/// <summary>Reads the full identifier of a repository's HEAD commit.</summary>
	private static async Task<string> HeadAsync( string repository ) =>
		(await GitProcess.RunAsync( LocalWorkspaceHost.Shared, repository, CancellationToken.None, "rev-parse", "HEAD" )).Trim();

	/// <summary>Reads one reference by its short name.</summary>
	private static async Task<GitRef> RefAsync( string repository, string shortName ) =>
		(await GitRepository.ReadRefsAsync( LocalWorkspaceHost.Shared, repository, CancellationToken.None )).Single( item => item.ShortName == shortName );

	/// <summary>Writes a fixture file's text into a repository.</summary>
	private static void Write( string repository, string path, string text ) => File.WriteAllText( Path.Combine( repository, path ), text );

	/// <summary>Reports whether a command fails with git's message.</summary>
	private static async Task<bool> ThrowsAsync( Func<Task> action )
	{
		try
		{
			await action();
			return false;
		}
		catch ( InvalidOperationException )
		{
			return true;
		}
	}

	/// <summary>Runs a git fixture command through the shared runner, failing the check with git's message.</summary>
	private static async Task RunAsync( string directory, params string[] arguments )
	{
		try
		{
			await GitProcess.RunAsync( LocalWorkspaceHost.Shared, directory, CancellationToken.None, arguments );
		}
		catch ( InvalidOperationException error )
		{
			CoreChecks.Check( false, $"git {string.Join( ' ', arguments )} succeeds. {error.Message}" );
		}
	}

	/// <summary>Reports whether git's path for a folder names the same folder as a fixture path.</summary>
	private static bool IsSameFolder( string? reported, string expected ) =>
		reported is not null && string.Equals( new DirectoryInfo( reported ).FullName, new DirectoryInfo( expected ).FullName, StringComparison.OrdinalIgnoreCase );

	/// <summary>Opens the Files side view with and without Git: Files comes first and Git's modes only with Git, switching keeps each mode's content,
	/// both Git modes build from a real repository, and a restored panel shows the mode it showed.</summary>
	private static async Task CheckSideModesAsync()
	{
		var tree = PathTree.Build<string>( ["src/core/a.cs", "src/core/b.cs", "readme.txt"], path => path, _ => false );
		CoreChecks.Check( string.Join( " | ", tree.Select( entry => $"{entry.Depth}:{entry.Label}:{entry.Count}" ) ) == "0:src/core:2 | 1:a.cs:1 | 1:b.cs:1 | 0:readme.txt:1",
			"Changed files group into folders first, joining folders that hold one folder, with each folder's count." );

		var repository = Directory.CreateTempSubdirectory( "lokn-git-modes-" ).FullName;
		File.WriteAllText( Path.Combine( repository, "readme.txt" ), "one\n" );
		await RunAsync( repository, "init", "--quiet", "--initial-branch=main" );
		await RunAsync( repository, "add", "." );
		await RunAsync( repository, "-c", "user.name=Lokn Checks", "-c", "user.email=checks@example.invalid", "commit", "--quiet", "-m", "Initial" );
		Directory.CreateDirectory( Path.Combine( repository, "src", "core" ) );
		File.WriteAllText( Path.Combine( repository, "src", "core", "added.cs" ), "added\n" );
		File.WriteAllText( Path.Combine( repository, "readme.txt" ), "one\ntwo\n" );
		await RunAsync( repository, "add", "readme.txt" );

		var plain = new LoknApplication();
		CoreChecks.Check( plain.Load( new FolderFixture( repository ) ) && plain.Load( new FilesModule() ), "Files loads without Git." );
		var aloneId = plain.Panels.Open( "files.tree" );
		var alone = (FileTreeContent)plain.Panels.Sessions.Single( session => session.Info.Id == aloneId ).Content!;
		CoreChecks.Check( alone.ListModes().Select( mode => mode.Key ).SequenceEqual( [FileTreeMode.FilesKey] ) && alone.Build() is Blob,
			"Without Git the side view offers only the Files mode." );
		// Another task's layout hides its tree; Files then opens a tree for the shown task instead of pulling the hidden one, with its old folder, in.
		plain.Panels.HideAll();
		FilesModule.Toggle( plain );
		var opened = FilesModule.Find( plain );
		CoreChecks.Check( opened is not null && opened.Id != aloneId && plain.Panels.Panels.Any( panel => panel.Id == aloneId ) && !plain.Panels.IsShown( aloneId ),
			"Files opens a tree in the shown task and leaves another task's hidden tree in its layout." );
		FilesModule.Toggle( plain );
		CoreChecks.Check( FilesModule.Find( plain ) is null, "Files closes the shown tree." );
		plain.Shutdown();

		var app = new LoknApplication();
		var queued = new List<Action>();
		app.BindDispatcher( action =>
		{
			lock ( queued )
				queued.Add( action );
			return true;
		} );
		CoreChecks.Check( app.Load( new FolderFixture( repository ) ) && app.Load( new FilesModule() ) && app.Load( new GitModule() ), "Files and Git load for the side view." );
		var id = app.Panels.Open( "files.tree" );
		var side = (FileTreeContent)app.Panels.Sessions.Single( session => session.Info.Id == id ).Content!;
		var modes = side.ListModes();
		CoreChecks.Check( modes.Select( mode => mode.Key ).SequenceEqual( [FileTreeMode.FilesKey, "git.changes", "git.history"] ) && side.Build() is Blob && side.SelectedModeKey == FileTreeMode.FilesKey,
			"The side view lists Files first, then Git's Changes and History, and opens on Files." );

		side.SelectMode( "git.changes" );
		CoreChecks.Check( side.Build() is Blob, "The side view builds in the Changes mode." );
		var changes = side.GetModeContent( modes[1] );
		side.SelectMode( "git.history" );
		CoreChecks.Check( side.Build() is Blob, "The side view builds in the History mode." );
		var history = side.GetModeContent( modes[2] );
		side.SelectMode( "git.changes" );
		side.Build();
		CoreChecks.Check( ReferenceEquals( changes, side.GetModeContent( modes[1] ) ) && ReferenceEquals( history, side.GetModeContent( modes[2] ) ) && !ReferenceEquals( changes, history ),
			"Switching modes keeps each mode's content, so its state survives." );

		// Each build starts the reads it needs; pumping lets them land, and the next build shows them.
		var content = typeof( GitSideMode ).GetField( "_content", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance )!;
		var pane = typeof( GitContent ).GetField( "_changes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance )!;
		CoreChecks.Check( changes.Build() is Blob && history.Build() is Blob, "Both Git modes build before the repository is read." );
		var clock = System.Diagnostics.Stopwatch.StartNew();
		while ( clock.Elapsed.TotalSeconds < 10 && content.GetValue( changes ) is GitContent git && pane.GetValue( git ) is GitChangesPane { HasChanges: false } )
		{
			Pump();
			CoreChecks.Check( changes.Build() is Blob && history.Build() is Blob, "Both Git modes build while their reads land." );
			Thread.Sleep( 50 );
		}
		Pump();
		CoreChecks.Check( content.GetValue( changes ) is GitContent shown && pane.GetValue( shown ) is GitChangesPane { HasChanges: true } && changes.Build() is Blob && history.Build() is Blob,
			"The Changes mode reads the side view's repository, staged and unstaged, and both modes build from it." );
		CoreChecks.Check( app.LastError is null, $"The side view's modes build without errors. {app.LastError}" );

		var layout = app.Panels.CaptureLayout();
		app.Panels.Close( id );
		CoreChecks.Check( app.Panels.ApplyLayout( layout ) && app.Panels.Sessions.Single( session => session.Content is FileTreeContent ).Content is FileTreeContent { SelectedModeKey: "git.changes" } restored
			&& restored.Build() is Blob, "A restored side view shows the mode it showed." );

		// A Git panel opened in the project opens the side view in Changes; a file clicked there shows its diff in that panel, the list keeping the focus.
		app.Panels.Close( app.Panels.Sessions.Single( session => session.Content is FileTreeContent ).Info.Id );
		var gitId = app.Panels.Open( GitModule.PanelType );
		Pump();
		var sideTree = FilesModule.Find( app );
		var sideView = sideTree is null ? null : app.Panels.Sessions.Single( session => session.Info.Id == sideTree.Id ).Content as FileTreeContent;
		CoreChecks.Check( sideView is { SelectedModeKey: "git.changes" } && app.Panels.ActivePanelId == gitId,
			"Opening the Git panel in a project opens the side view in its Changes mode, and the Git panel keeps the focus." );
		var sideChanges = (GitSideMode)sideView!.GetModeContent( sideView.ListModes().Single( mode => mode.Key == "git.changes" ) );
		var gitPanel = (GitContent)app.Panels.Sessions.Single( session => session.Info.Id == gitId ).Content!;
		var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
		var fileView = (GitFileView)typeof( GitContent ).GetField( "_file", flags )!.GetValue( gitPanel )!;
		bool Loaded() => content.GetValue( sideChanges ) is GitContent narrow && pane.GetValue( narrow ) is GitChangesPane { HasChanges: true }
			&& pane.GetValue( gitPanel ) is GitChangesPane { HasChanges: true };
		var waited = System.Diagnostics.Stopwatch.StartNew();
		while ( waited.Elapsed.TotalSeconds < 10 && !Loaded() )
		{
			Pump();
			sideView.Build();
			sideChanges.Build();
			gitPanel.Build();
			Thread.Sleep( 50 );
		}
		app.Panels.Activate( sideTree!.Id );
		var narrowView = (GitContent)content.GetValue( sideChanges )!;
		var readme = ((GitChangesPane)pane.GetValue( narrowView )!).Changes!.Staged.Single( file => file.Path == "readme.txt" );
		typeof( GitContent ).GetMethod( "OpenChange", flags )!.Invoke( narrowView, [readme, true] );
		Pump();
		CoreChecks.Check( Loaded() && fileView.Target == new GitFileTarget( "readme.txt", true ) && app.Panels.ActivePanelId == sideTree.Id && app.Panels.Panels.Count( panel => panel.TypeId == GitModule.PanelType ) == 1,
			"Clicking a file in the Changes mode shows its diff in the open Git panel, while the side view keeps the focus." );

		// The Git panel's table gives the message its room first at any width: the other columns shrink, then the SHA and the date hide.
		var tableWidth = typeof( GitContent ).GetField( "_tableWidth", flags )!;
		var computeColumns = typeof( GitContent ).GetMethod( "ComputeTableColumns", flags )!;
		GitTableColumns Columns( double width )
		{
			tableWidth.SetValue( gitPanel, width );
			return (GitTableColumns)computeColumns.Invoke( gitPanel, [] )!;
		}
		var unmeasured = Columns( 0 );
		var wide = Columns( 1600 );
		CoreChecks.Check( unmeasured == wide && wide is { ShowsDate: true, ShowsSha: true, IsRefCompact: false },
			"A wide Git table, or one not laid out yet, shows every column at its full width." );
		foreach ( var width in new[] { 900.0, 525, 400, 300 } )
		{
			var columns = Columns( width );
			var taken = columns.RefWidth + columns.GraphWidth + (columns.ShowsDate ? 72 : 0) + (columns.ShowsSha ? 72 : 0);
			CoreChecks.Check( width - taken >= 120, $"A {width} pixel wide Git table leaves the commit message at least 120 pixels: {columns}." );
		}
		CoreChecks.Check( Columns( 525 ) is { ShowsDate: true, ShowsSha: true } && Columns( 260 ) is { ShowsDate: false, ShowsSha: false, IsRefCompact: true },
			"A narrowing Git table shrinks its Branch / Tag column to icons before it hides the SHA and the date." );
		tableWidth.SetValue( gitPanel, 0.0 );

		// Closing the side view leaves the Git panel a way to bring its working tree back.
		app.Panels.Close( sideTree.Id );
		typeof( GitContent ).GetMethod( "ShowSideChanges", flags )!.Invoke( gitPanel, [] );
		var reopened = FilesModule.Find( app );
		CoreChecks.Check( reopened is not null && app.Panels.Sessions.Single( session => session.Info.Id == reopened.Id ).Content is FileTreeContent { SelectedModeKey: "git.changes" },
			"The Git panel shows the closed side view again, in its Changes mode." );

		// A Git panel opened from a chat sits next to the side view it opens, so the chat is not left between the two Git views.
		CoreChecks.Check( app.Load( new FixtureModule( "chat-fixture", builder => builder.Panel( "page", "Chat", _ => new FixtureContent() ) ) ), "A chat stand-in loads." );
		foreach ( var panel in app.Panels.Panels )
			app.Panels.Close( panel.Id );
		var chatId = app.Panels.Open( "chat-fixture.page" );
		string Order() => string.Join( ", ", app.Panels.Panes.Select( item => app.Panels.Panels.Single( panel => panel.Id == item.PanelId ).TypeId ) );
		app.GetModule<GitModule>().OpenPanel( app, chatId, repository );
		Pump();
		CoreChecks.Check( Order() == "files.tree, git.panel, chat-fixture.page", $"A Git panel opened from a chat opens the side view and sits between it and the chat: {Order()}." );
		app.Panels.Close( app.Panels.Panels.Single( panel => panel.TypeId == GitModule.PanelType ).Id );
		app.GetModule<GitModule>().OpenPanel( app, chatId, repository );
		Pump();
		CoreChecks.Check( Order() == "files.tree, git.panel, chat-fixture.page", $"A Git panel opened from a chat beside an open side view sits between them: {Order()}." );
		app.Shutdown();

		void Pump()
		{
			Action[] actions;
			lock ( queued )
			{
				actions = [.. queued];
				queued.Clear();
			}
			foreach ( var action in actions )
				action();
		}
	}

	/// <summary>Publishes a fixed working folder, as Projects does for the open project.</summary>
	/// <param name="folder">The folder.</param>
	private sealed class FolderFixture( string folder ) : ILoknModule, IWorkingFolder
	{
		/// <summary>Gets the fixture module identifier.</summary>
		public string Id => "folder-fixture";

		/// <summary>Gets the fixture module name.</summary>
		public string Title => "Working folder fixture";

		/// <summary>Gets the fixed folder.</summary>
		public string? Current => folder;

		/// <summary>Publishes the folder.</summary>
		public void Register( ModuleBuilder builder ) => builder.Service<IWorkingFolder>( this );
	}

	/// <summary>Stages, unstages and discards single hunks through their patches, and checks the hunk numbers, CRLF content kept in the index,
	/// a missing final newline, an untracked file's patch, and the gap rows GitDiff builds from the same parser.</summary>
	private static async Task CheckHunksAsync()
	{
		var sample = GitDiff.Parse( "diff --git a/a.txt b/a.txt\nindex 1..2 100644\n--- a/a.txt\n+++ b/a.txt\n@@ -3,3 +3,3 @@ class A\n one\n-two\n+TWO\n three\n@@ -20,2 +20,3 @@\n x\n+y\n z\n" );
		CoreChecks.Check( sample is [{ Path: "a.txt", Operation: "update", Added: 2, Removed: 1, Note: null }]
			&& sample[0].Lines.Select( line => (line.Kind, line.Number) ).SequenceEqual( [(DiffLineKind.Gap, 2), (DiffLineKind.Context, 3), (DiffLineKind.Removed, 4), (DiffLineKind.Added, 4),
				(DiffLineKind.Context, 5), (DiffLineKind.Gap, 14), (DiffLineKind.Context, 20), (DiffLineKind.Added, 21), (DiffLineKind.Context, 22)] ),
			"GitDiff's rows come from the hunk parser, with a gap before each hunk that unchanged lines precede." );

		var none = CancellationToken.None;
		var repository = Directory.CreateTempSubdirectory( "lokn-git-hunks-" ).FullName;
		try
		{
			await RunAsync( repository, "init", "--quiet", "--initial-branch=main" );
			await ConfigureAsync( repository );
			string[] original = [.. Enumerable.Range( 1, 30 ).Select( number => $"line {number}" )];
			Write( repository, "lines.txt", string.Join( '\n', original ) + "\n" );
			Write( repository, "crlf.txt", "one\r\ntwo\r\nthree\r\n" );
			Write( repository, "open.txt", "first\nlast" );
			await RunAsync( repository, "add", "." );
			await RunAsync( repository, "-c", "user.name=Lokn Checks", "-c", "user.email=checks@example.invalid", "commit", "--quiet", "-m", "Initial" );

			string[] edited = [.. original];
			edited[2] = "line 3 changed";
			edited[24] = "line 25 changed";
			Write( repository, "lines.txt", string.Join( '\n', edited ) + "\n" );
			var file = await ReadPatchAsync( "lines.txt", false );
			CoreChecks.Check( file is { Path: "lines.txt", OldPath: null, Operation: "update", Note: null, Added: 2, Removed: 2,
					Hunks: [{ Header: "@@ -1,6 +1,6 @@", OldStart: 1, OldCount: 6, NewStart: 1, NewCount: 6 }, { OldStart: 22, OldCount: 7, NewStart: 22, NewCount: 7 }] }
				&& file.Header.StartsWith( "diff --git a/lines.txt b/lines.txt\n", StringComparison.Ordinal ) && file.Header.EndsWith( "+++ b/lines.txt\n", StringComparison.Ordinal )
				&& file.Hunks[0].Lines.SequenceEqual( [new( DiffLineKind.Context, 1, 1, "line 1" ), new( DiffLineKind.Context, 2, 2, "line 2" ), new( DiffLineKind.Removed, 3, 0, "line 3" ),
					new( DiffLineKind.Added, 0, 3, "line 3 changed" ), new( DiffLineKind.Context, 4, 4, "line 4" ), new( DiffLineKind.Context, 5, 5, "line 5" ), new( DiffLineKind.Context, 6, 6, "line 6" )] )
				&& file.Hunks[1].Lines[3] == new GitPatchLine( DiffLineKind.Removed, 25, 0, "line 25" ) && file.Hunks[1].Lines[4] == new GitPatchLine( DiffLineKind.Added, 0, 25, "line 25 changed" )
				&& file.Hunks[0].Text.StartsWith( "@@ -1,6 +1,6 @@\n", StringComparison.Ordinal ) && file.Hunks[0].Text.EndsWith( " line 6\n", StringComparison.Ordinal ),
				"Two separate edits parse as two hunks with their starts, counts, old and new line numbers, and raw texts." );

			await GitRepository.StageHunkAsync( LocalWorkspaceHost.Shared, repository, GitPatch.BuildHunkPatch( file, file.Hunks[0] ), none );
			CoreChecks.Check( await ReadDiffAsync( true ) is [{ Hunks: [var stagedHunk] }] && Changes( stagedHunk, "line 3" )
				&& await ReadDiffAsync( false ) is [{ Hunks: [var unstagedHunk] }] && Changes( unstagedHunk, "line 25" ),
				"Staging one hunk stages only its change and leaves the other unstaged." );

			var staged = await ReadPatchAsync( "lines.txt", true );
			await GitRepository.UnstageHunkAsync( LocalWorkspaceHost.Shared, repository, GitPatch.BuildHunkPatch( staged, staged.Hunks[0] ), none );
			CoreChecks.Check( (await GitRepository.ReadChangesAsync( LocalWorkspaceHost.Shared, repository, none )).Staged is [] && await ReadDiffAsync( false ) is [{ Hunks.Count: 2 }],
				"Unstaging a staged hunk from the staged patch leaves nothing staged and both changes in the working tree." );

			file = await ReadPatchAsync( "lines.txt", false );
			await GitRepository.DiscardHunkAsync( LocalWorkspaceHost.Shared, repository, GitPatch.BuildHunkPatch( file, file.Hunks[1] ), none );
			var lines = File.ReadAllText( Path.Combine( repository, "lines.txt" ) ).Split( '\n' );
			CoreChecks.Check( lines.Length == 31 && lines[2] == "line 3 changed" && lines[24] == "line 25" && (await GitRepository.ReadChangesAsync( LocalWorkspaceHost.Shared, repository, none )).Staged is [],
				"Discarding one hunk restores its original lines and keeps the other change." );

			Write( repository, "crlf.txt", "one\r\nTWO\r\nthree\r\n" );
			var crlf = await ReadPatchAsync( "crlf.txt", false );
			await GitRepository.StageHunkAsync( LocalWorkspaceHost.Shared, repository, GitPatch.BuildHunkPatch( crlf, crlf.Hunks[0] ), none );
			CoreChecks.Check( crlf.Hunks is [{ Lines: [{ Kind: DiffLineKind.Context, Text: "one" }, { Kind: DiffLineKind.Removed, Text: "two" }, { Kind: DiffLineKind.Added, Text: "TWO" }, { Kind: DiffLineKind.Context, Text: "three" }] }]
				&& crlf.Hunks[0].Text.Contains( "-two\r\n+TWO\r\n", StringComparison.Ordinal )
				&& await GitProcess.RunAsync( LocalWorkspaceHost.Shared, repository, none, "show", ":crlf.txt" ) == "one\r\nTWO\r\nthree\r\n",
				"A CRLF file's rows drop the carriage return while its hunk text keeps it, so the staged content keeps CRLF." );

			Write( repository, "open.txt", "first\nLAST" );
			var open = await ReadPatchAsync( "open.txt", false );
			await GitRepository.StageHunkAsync( LocalWorkspaceHost.Shared, repository, GitPatch.BuildHunkPatch( open, open.Hunks[0] ), none );
			CoreChecks.Check( open.Hunks is [{ OldStart: 1, OldCount: 2, NewStart: 1, NewCount: 2,
					Lines: [{ Kind: DiffLineKind.Context, Text: "first" }, { Kind: DiffLineKind.Removed, Text: "last" }, { Kind: DiffLineKind.Added, Text: "LAST" }] }]
				&& open.Hunks[0].Text.EndsWith( "-last\n\\ No newline at end of file\n+LAST\n\\ No newline at end of file\n", StringComparison.Ordinal )
				&& await GitProcess.RunAsync( LocalWorkspaceHost.Shared, repository, none, "show", ":open.txt" ) == "first\nLAST",
				"A file without a final newline keeps git's marker in its hunk text, not its rows, and its hunk stages." );

			Write( repository, "fresh.txt", "a\nb\n" );
			var fresh = await ReadPatchAsync( "fresh.txt", false );
			await GitRepository.StageHunkAsync( LocalWorkspaceHost.Shared, repository, GitPatch.BuildHunkPatch( fresh, fresh.Hunks[0] ), none );
			CoreChecks.Check( fresh is { Path: "fresh.txt", OldPath: null, Operation: "create", Added: 2, Removed: 0, Hunks: [{ OldStart: 0, OldCount: 0, NewStart: 1, NewCount: 2 }] }
				&& await GitProcess.RunAsync( LocalWorkspaceHost.Shared, repository, none, "show", ":fresh.txt" ) == "a\nb\n",
				"An untracked file's no-index patch parses as a created file, and its hunk stages it." );
		}
		finally
		{
			ChatChecks.DeleteRepository( repository );
		}

		async Task<GitFilePatch> ReadPatchAsync( string path, bool staged )
		{
			var changes = await GitRepository.ReadChangesAsync( LocalWorkspaceHost.Shared, repository, none );
			var change = (staged ? changes.Staged : changes.Unstaged).Single( item => item.Path == path );
			return GitPatch.Parse( await GitRepository.ReadWorkingPatchAsync( LocalWorkspaceHost.Shared, repository, change, staged, none ) ).Single();
		}

		async Task<IReadOnlyList<GitFilePatch>> ReadDiffAsync( bool staged ) =>
			GitPatch.Parse( await GitProcess.RunAsync( LocalWorkspaceHost.Shared, repository, none, "diff", "--no-color", "--no-ext-diff", staged ? "--cached" : "--" ) );

		static bool Changes( GitHunk hunk, string line ) =>
			hunk.Lines.Where( row => row.Kind != DiffLineKind.Context ).Select( row => row.Text ).SequenceEqual( [line, line + " changed"] );
	}
}
