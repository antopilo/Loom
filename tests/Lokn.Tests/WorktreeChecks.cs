using System.Diagnostics;
using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Conversations;
using Lokn.Modules.Git;
using Lokn.Modules.Omp;
using Lokn.Modules.Projects;
using Lokn.Modules.Worktrees;

namespace Lokn.Tests;

/// <summary>Exercises working tree and branch management, and sharing folders and native builds, against temporary repositories.</summary>
internal static class WorktreeChecks
{
	/// <summary>Defines porcelain output covering a main, a linked, a detached locked, and a prunable working tree.</summary>
	private const string Porcelain = """
		worktree C:/repos/app
		HEAD 1111111111111111111111111111111111111111
		branch refs/heads/main

		worktree C:/repos/app.worktrees/feature-login
		HEAD 2222222222222222222222222222222222222222
		branch refs/heads/feature/login

		worktree C:/repos/app.worktrees/review
		HEAD 3333333333333333333333333333333333333333
		detached
		locked being reviewed

		worktree C:/repos/app.worktrees/gone
		HEAD 4444444444444444444444444444444444444444
		branch refs/heads/gone
		prunable gitdir file points to non-existent location

		""";

	/// <summary>Defines the identity fixture commits are made with.</summary>
	private static readonly string[] Identity = ["-c", "user.name=Lokn Checks", "-c", "user.email=checks@example.invalid"];

	/// <summary>Runs the worktree checks without opening a native window.</summary>
	internal static async Task RunAsync()
	{
		CheckParsing();
		await CheckRepositoryAsync();
		await CheckSharingAsync();
		Console.WriteLine( "PASS worktree and branch parsing, sparse and remote-branch creation, branch listing and deletion, native fingerprints and build records, shared folders and native builds through links, removal and git clean that keep the shared files, and the chat sidebar's worktree labels, New worktree, Remove worktree and Manage worktrees, absent without Worktrees, chats following their shell into a worktree, and worktrees git removed outside Lokn." );
	}

	/// <summary>Verifies porcelain records, branch records, reflog lines, generated directories, typed folders, and size formatting.</summary>
	private static void CheckParsing()
	{
		var worktrees = GitWorktrees.Parse( Porcelain );
		CoreChecks.Check( worktrees.Count == 4 && worktrees[0] is { IsMain: true, Branch: "main" } && worktrees.Skip( 1 ).All( worktree => !worktree.IsMain ), "Only the first record is the main working tree." );
		CoreChecks.Check( worktrees[1].Branch == "feature/login" && worktrees[1].Path == Path.GetFullPath( "C:/repos/app.worktrees/feature-login" ), "Branches lose their reference prefix and paths are normalized." );
		CoreChecks.Check( worktrees[2] is { Branch: null, IsLocked: true, IsPrunable: false } && worktrees[3] is { IsPrunable: true, IsLocked: false }, "Detached, locked, and prunable states are read." );
		CoreChecks.Check( GitWorktrees.BuildPath( @"C:\repos\app\", "feature/login" ) == @"C:\repos\app.worktrees\feature-login", "New working trees go beside the main one with a flattened branch name." );
		CoreChecks.Check( WorktreesContent.FormatSize( 512 ) == "512 B" && WorktreesContent.FormatSize( 1536 ) == "1.5 KB" && WorktreesContent.FormatSize( 3L * 1024 * 1024 * 1024 ) == "3.0 GB", "Sizes use the largest fitting binary unit." );
		CoreChecks.Check( GitWorktrees.ParseDirectories( @" engine\core/, docs;;engine\core " ).SequenceEqual( ["engine/core", "docs"] ), "Typed folders become distinct repository-relative forward-slash paths." );

		var branches = GitWorktrees.ParseBranches( string.Join( "\n",
			"refs/heads/main\0origin/main\0ahead 1\01700000000\0Fix\0C:/repos/app",
			"refs/heads/feature/login\0\0\01600000000\0Login\0",
			"refs/remotes/origin/HEAD\0\0\01700000000\0Fix\0",
			"refs/remotes/origin/main\0\0\01700000000\0Fix\0",
			"refs/remotes/origin/only/remote\0\0\01500000000\0Remote\0" ) );
		CoreChecks.Check( branches.Count == 3 && branches[0] is { Name: "main", Track: "ahead 1", Upstream: "origin/main", IsRemote: false } && branches[0].WorktreePath == Path.GetFullPath( "C:/repos/app" ),
			"Local branches keep their upstream, distance, and working tree." );
		CoreChecks.Check( branches[2] is { Name: "origin/only/remote", LocalName: "only/remote", IsRemote: true } && branches.All( branch => branch.Name != "origin/main" && branch.LocalName != "HEAD" ),
			"Remote branches a local branch names, and remote HEAD aliases, are left out." );

		var reflog = NativeBuilds.ParseReflog( "aaaa HEAD@{1700000100}\nbbbb HEAD@{1700000000}\n" );
		CoreChecks.Check( reflog.Count == 2 && reflog[0].Commit == "aaaa" && reflog[1].Time == DateTimeOffset.FromUnixTimeSeconds( 1700000000 ), "Reflog lines give each commit and when HEAD moved to it." );
	}

	/// <summary>Verifies creating full, sparse, started and remote-branch working trees, listing and deleting branches, and removing with real git.</summary>
	private static async Task CheckRepositoryAsync()
	{
		var root = Path.Combine( Path.GetTempPath(), "lokn-worktrees-" + Guid.NewGuid().ToString( "N" ) );
		var repository = Path.Combine( root, "app" );
		Write( repository, "readme.txt", "worktree fixture" );
		Write( repository, "engine/a.txt", "engine" );
		Write( repository, "game/assets/big.bin", "asset" );
		Run( repository, "init", "--quiet" );
		Run( repository, "add", "." );
		Commit( repository, "Initial" );
		Run( repository, "branch", "-M", "main" );
		Run( repository, "branch", "existing" );
		var none = CancellationToken.None;

		var listed = await GitWorktrees.ListAsync( repository, none );
		CoreChecks.Check( listed.Single().IsMain && GitWorktrees.ComputeSize( repository, null, none ) > 0, "A fresh repository lists and measures its main working tree." );

		var created = await GitWorktrees.AddAsync( repository, "feature/new", "", [], none );
		var existing = await GitWorktrees.AddAsync( repository, "existing", "", [], none );
		listed = await GitWorktrees.ListAsync( repository, none );
		CoreChecks.Check( listed.Count == 3 && listed.Any( worktree => worktree.Path == created && worktree.Branch == "feature/new" ) && listed.Any( worktree => worktree.Path == existing && worktree.Branch == "existing" ), "Working trees are created for new and existing branches." );
		CoreChecks.Check( File.Exists( Path.Combine( created, "readme.txt" ) ) && GitWorktrees.ComputeSize( created, null, none ) > 0, "A created working tree is checked out and measurable." );
		CoreChecks.Check( GitWorktrees.ComputeSize( created, Path.Combine( created, "game" ), none ) < GitWorktrees.ComputeSize( created, null, none ), "A size can leave out a shared folder." );

		var sparse = await GitWorktrees.AddAsync( repository, "sparse", "", ["engine"], none );
		CoreChecks.Check( File.Exists( Path.Combine( sparse, "engine", "a.txt" ) ) && File.Exists( Path.Combine( sparse, "readme.txt" ) ) && !Directory.Exists( Path.Combine( sparse, "game" ) ),
			"A sparse working tree writes only its folders and the top-level files." );
		CoreChecks.Check( (await GitWorktrees.ReadSparseDirectoriesAsync( sparse, none )).SequenceEqual( ["engine"] ) && (await GitWorktrees.ReadSparseDirectoriesAsync( created, none )).Count == 0
			&& (await GitWorktrees.ReadSparseDirectoriesAsync( repository, none )).Count == 0, "Sparse folders belong to their working tree alone." );
		CoreChecks.Check( RunOutput( sparse, "status", "--porcelain" ).Length == 0, "A sparse working tree has no changes." );

		Write( existing, "engine/a.txt", "changed on existing" );
		Run( existing, "add", "." );
		Commit( existing, "Change existing" );
		var based = await GitWorktrees.AddAsync( repository, "based", "existing", [], none );
		CoreChecks.Check( RunOutput( based, "rev-parse", "HEAD" ) == RunOutput( existing, "rev-parse", "HEAD" ), "A new branch starts at the given start point." );
		await ExpectFailureAsync( () => GitWorktrees.AddAsync( repository, "existing", "main", [], none ), "A start point for an existing branch is refused." );

		var clone = Path.Combine( root, "clone" );
		Run( root, "clone", "--quiet", repository, clone );
		var remoteOnly = (await GitWorktrees.ListBranchesAsync( clone, none )).Single( branch => branch.Name == "origin/existing" );
		CoreChecks.Check( remoteOnly is { IsRemote: true, LocalName: "existing" }, "A branch only a remote has is listed under its remote." );
		var tracking = await GitWorktrees.AddAsync( clone, remoteOnly.LocalName, "", [], none );
		var cloneBranches = await GitWorktrees.ListBranchesAsync( clone, none );
		CoreChecks.Check( cloneBranches.Any( branch => branch is { Name: "existing", Upstream: "origin/existing" } && branch.WorktreePath == tracking ) && cloneBranches.All( branch => branch.Name != "origin/existing" ),
			"A remote-only branch becomes a local branch tracking it, checked out in the new working tree." );

		Run( repository, "branch", "merged" );
		await GitWorktrees.DeleteBranchAsync( repository, "merged", none );
		CoreChecks.Check( (await GitWorktrees.ListBranchesAsync( repository, none )).All( branch => branch.Name != "merged" ), "A merged branch is deleted." );
		await ExpectFailureAsync( () => GitWorktrees.DeleteBranchAsync( repository, "existing", none ), "A branch checked out in a working tree is not deleted." );

		Write( created, "readme.txt", "uncommitted" );
		await ExpectFailureAsync( () => WorktreeSharing.RemoveAsync( repository, created, none ), "A working tree with uncommitted changes is not removed." );
		await WorktreeSharing.RemoveAsync( repository, based, none );
		listed = await GitWorktrees.ListAsync( repository, none );
		CoreChecks.Check( !listed.Any( worktree => worktree.Path == based ) && !Directory.Exists( based ) && Directory.Exists( created ), "Removal deletes a clean working tree and refuses a changed one." );
		CoreChecks.Check( GitWorktrees.ComputeSize( based, null, none ) == 0, "A missing directory measures as empty." );

		var app = new LoknApplication();
		var store = new ConversationStore( new ProjectStore() );
		var project = store.ProjectStore.Create( SettingsProfile.Personal, "App", repository );
		CoreChecks.Check( app.Load( new WorktreesModule( store, Path.Combine( root, "worktrees.json" ) ) ), "The worktrees module loads." );
		var panelId = app.Panels.Open( "worktrees.list" );
		var panel = app.Panels.Sessions.Single( session => session.Info.Id == panelId ).Content!;
		CoreChecks.Check( panel.Build() is not null && panel.CaptureState()!.Value.GetString() == repository, "The panel builds and starts on the active profile's first project." );
		var desk = app.Hosts.Add( "worktree-host-id-00000", "Desk" );
		var deskPanelId = app.Panels.Open( "worktrees.list", hostId: desk.Id );
		var deskPanel = app.Panels.Sessions.Single( session => session.Info.Id == deskPanelId ).Content!;
		CoreChecks.Check( app.Panels.GetHostId( deskPanelId ) == desk.Id && Walk( deskPanel.Build() ).OfType<Goo.Text>().Any( text => text.Content == "Worktrees work on this PC only." ),
			"A Worktrees panel on a paired host says worktrees work on this PC only." );
		app.Panels.Close( deskPanelId );
		await CheckEnterAsync( app, store, project, repository, panelId );
		await CheckSidebarAsync( app, store, project, repository, created );
		app.Shutdown();

		await ExpectFailureAsync( () => GitWorktrees.ListAsync( root, none ), "A directory outside any repository reports git's error." );
	}

	/// <summary>Verifies an agent's call moves its chat into a new or existing branch's worktree and back to the main checkout, and that only chats may call it.</summary>
	private static async Task CheckEnterAsync( LoknApplication app, ConversationStore store, ProjectInfo project, string repository, Guid otherPanelId )
	{
		var conversations = new ConversationsModule( store, ( _, _, _ ) => new ChatStandIn() );
		CoreChecks.Check( ProjectChecks.LoadProjects( app, store ) && app.Load( conversations ), "The conversations module loads beside worktrees." );
		var chat = store.CreateConversation( SettingsProfile.Personal, project.Id );
		var chatPanel = conversations.OpenConversation( app, ChatRef.Local( chat.Id ) );
		Task<AgentToolResult> Enter( string json, Guid caller ) => app.Tools.InvokeAsync( "worktrees_enter", System.Text.Json.JsonDocument.Parse( json ).RootElement, caller, CancellationToken.None );

		var result = await Enter( """{ "branch": "parallel" }""", chatPanel );
		var parallel = store.GetDirectory( chat.Id );
		CoreChecks.Check( !result.IsError && ConversationStore.IsSameFolder( parallel, GitWorktrees.BuildPath( repository, "parallel" ) ) && RunOutput( parallel, "branch", "--show-current" ) == "parallel",
			$"An agent's call creates a worktree of a new branch and moves its chat there ({result.Text})." );
		CoreChecks.Check( HistoryContent.ComputeWorktreeGroups( project, [store.Get( chat.Id ), store.CreateConversation( SettingsProfile.Personal, project.Id )] ).Count == 2,
			"The sidebar then lists the project's chats under their worktrees." );

		result = await Enter( """{ "branch": "existing" }""", chatPanel );
		CoreChecks.Check( !result.IsError && RunOutput( store.GetDirectory( chat.Id ), "branch", "--show-current" ) == "existing", $"A branch with a worktree reuses it ({result.Text})." );

		result = await Enter( """{ "main": true }""", chatPanel );
		CoreChecks.Check( !result.IsError && store.GetDirectory( chat.Id ) == project.Directory && store.Get( chat.Id ).Worktree is null, $"main: true moves the chat back to the project's directory ({result.Text})." );

		// During a turn no panel shows, the call names the chat by its conversation instead.
		result = await app.Tools.InvokeAsync( "worktrees_enter", System.Text.Json.JsonDocument.Parse( """{ "branch": "parallel" }""" ).RootElement, null, CancellationToken.None, conversation: chat.Id );
		CoreChecks.Check( !result.IsError && ConversationStore.IsSameFolder( store.GetDirectory( chat.Id ), GitWorktrees.BuildPath( repository, "parallel" ) ),
			$"An agent whose chat no panel shows moves its chat by the conversation its call names ({result.Text})." );
		result = await Enter( """{ "main": true }""", chatPanel );
		CoreChecks.Check( !result.IsError && store.Get( chat.Id ).Worktree is null, $"The chat moves back to the project's directory ({result.Text})." );

		CoreChecks.Check( (await Enter( """{ "branch": "parallel" }""", otherPanelId )).IsError && (await Enter( """{ "branch": "x", "main": true }""", chatPanel )).IsError,
			"Only a chat's agent can move, and a branch with main: true is refused." );

		var desk = conversations.Hosts.Add( app.Hosts.Add( "enter-host-id-00000000", "Desk" ) );
		var remote = desk.Store.CreateConversation( SettingsProfile.Personal );
		var refused = await app.Tools.InvokeAsync( "worktrees_enter", System.Text.Json.JsonDocument.Parse( """{ "branch": "parallel" }""" ).RootElement, null, CancellationToken.None,
			conversation: remote.Id, host: desk.Id );
		CoreChecks.Check( refused.IsError && refused.Text.Contains( "Worktrees work on this PC only.", StringComparison.Ordinal ), $"A paired host's chat cannot move into a worktree ({refused.Text})." );
	}

	/// <summary>Stands in for a chat panel, so a conversation panel has content without an agent.</summary>
	private sealed class ChatStandIn : PanelContent
	{
		/// <summary>Builds an empty view.</summary>
		public override Goo.Blob Build() => new Goo.Container();
	}

	/// <summary>Verifies the chat sidebar labels only the worktrees its chats work in, creates a worktree with the repository's setup and opens a chat in it,
	/// removes one through its dialog moving its chats back, moves a chat to the worktree its shell worked in, drops a worktree git removed outside Lokn,
	/// refuses the main checkout and a changed worktree, opens the manager, offers none of it without Worktrees, and renames a project in place.</summary>
	/// <param name="app">The application with Worktrees and Conversations loaded.</param>
	/// <param name="store">The chats and projects.</param>
	/// <param name="project">The project on the repository.</param>
	/// <param name="repository">The repository's main checkout.</param>
	/// <param name="changed">A linked worktree with uncommitted changes.</param>
	private static async Task CheckSidebarAsync( LoknApplication app, ConversationStore store, ProjectInfo project, string repository, string changed )
	{
		CoreChecks.Check( app.Load( new GitModule() ) && app.Load( new OmpModule() ), "Git and Omp load for the worktree labels and a chat's shell." );
		var queued = new List<Action>();
		app.BindDispatcher( action =>
		{
			lock ( queued )
				queued.Add( action );
			return true;
		} );
		bool PumpUntil( Func<bool> condition )
		{
			var clock = Stopwatch.StartNew();
			while ( !condition() && clock.Elapsed.TotalSeconds < 30 )
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

		var sidebar = app.Slots.Get( ShellSlots.Sidebar ).Single( item => item.ModuleId == "conversations" ).Content;
		List<object> Shown() => [.. Walk( sidebar.Build() )];
		Text? Label( string path ) => Shown().OfType<Text>().FirstOrDefault( text => text.Key == $"worktree:{path}" );
		Button? FindButton( string name ) => Shown().OfType<Button>().FirstOrDefault( button => button.AccessibilityName == name );
		MenuAction? Entry( string label ) => Shown().OfType<ContextMenuInput>().SelectMany( menu => menu.Entries ).OfType<MenuAction>().FirstOrDefault( entry => entry.Label == label );
		void Choose( string label )
		{
			var menu = Shown().OfType<ContextMenuInput>().Single( candidate => candidate.Entries.OfType<MenuAction>().Any( entry => entry.Label == label ) );
			menu.Dismiss();
			menu.Entries.OfType<MenuAction>().Single( entry => entry.Label == label ).Run();
		}
		ConfirmDialogInput? Dialog() => Shown().OfType<ConfirmDialogInput>().SingleOrDefault();
		void Remove( string path )
		{
			var name = Label( path )!.Content;
			FindButton( $"{name} options" )!.OnClick!();
			Choose( "Remove worktree" );
			Dialog()!.Confirm();
		}
		ConversationInfo[] Chats() => [.. store.Conversations( SettingsProfile.Personal, project.Id )];

		var actions = app.Extensions.Get<WorktreeActions>( WorktreeActions.Point ).Single();
		var listing = await actions.ListAsync( repository, CancellationToken.None );
		var linked = listing.Where( worktree => !worktree.IsMain ).Select( worktree => worktree.Path ).ToArray();
		CoreChecks.Check( linked.Length > 0 && Chats().All( chat => chat.Worktree is null ) && HistoryContent.ComputeWorktreeGroups( project, Chats(), listing ) is [{ IsMain: true }]
			&& linked.All( path => Label( path ) is null ),
			"The sidebar leaves out linked worktrees without chats, so a project whose chats all work in its directory stays one flat list." );
		CoreChecks.Check( HistoryContent.ComputeWorktreeGroups( project, Chats(), [new( repository, "main", IsMain: true )] ).Count == 1,
			"A project whose repository has no linked worktree keeps its chats in one flat list." );

		var setup = (WorktreeSetupStore)typeof( WorktreesModule ).GetField( "_setup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance )!.GetValue( app.GetModule<WorktreesModule>() )!;
		setup.SetSetup( repository, new RepositorySetup( ["engine"], [], [], "" ) );
		FindButton( $"{project.Name} options" )!.OnClick!();
		CoreChecks.Check( PumpUntil( () => Entry( "New worktree…" ) is not null ), "The project menu offers New worktree… once the repository is listed." );
		Choose( "New worktree…" );
		Shown().OfType<TextEntry>().Single( entry => entry.AccessibilityName == "New worktree branch" ).OnChange!( "sidebar" );
		FindButton( $"Create the worktree in {project.Name}" )!.OnClick!();
		var created = GitWorktrees.BuildPath( repository, "sidebar" );
		CoreChecks.Check( PumpUntil( () => Chats().Any( chat => chat.Worktree is { } worktree && ConversationStore.IsSameFolder( worktree, created ) ) && Label( created ) is not null )
			&& RunOutput( created, "branch", "--show-current" ) == "sidebar" && File.Exists( Path.Combine( created, "engine", "a.txt" ) ) && !Directory.Exists( Path.Combine( created, "game" ) )
			&& Shown().OfType<TextEntry>().All( entry => entry.AccessibilityName != "New worktree branch" ),
			"New worktree… creates the branch's worktree with the repository's setup, closes its form and opens a chat working there." );

		var parked = store.CreateConversation( SettingsProfile.Personal, project.Id );
		store.SetWorktree( parked.Id, changed );
		CoreChecks.Check( PumpUntil( () => Label( changed ) is not null ), "A linked worktree shows its label once a chat works in it." );
		Remove( changed );
		CoreChecks.Check( PumpUntil( () => Dialog()?.Error?.Contains( "uncommitted", StringComparison.Ordinal ) == true ) && Directory.Exists( changed ),
			"Removing a worktree with uncommitted changes shows why git refused in its dialog and keeps it." );
		Dialog()!.Cancel();
		CoreChecks.Check( Dialog() is null, "Cancel closes the removal dialog." );

		var moved = Chats().Single( chat => chat.Worktree is { } worktree && ConversationStore.IsSameFolder( worktree, created ) ).Id;
		Remove( created );
		CoreChecks.Check( PumpUntil( () => store.Get( moved ).Worktree is null && Label( created ) is null ) && !Directory.Exists( created ) && store.GetDirectory( moved ) == project.Directory,
			"Remove worktree deletes it once confirmed and moves its chats back to the main checkout." );

		var follower = store.CreateConversation( SettingsProfile.Personal, project.Id );
		var shell = new ChatSession( app, store, follower.Id, app.GetModule<OmpModule>().Service, app.Post );
		var outside = Path.GetDirectoryName( repository )!;
		shell.UpdateFrame( JsonDocument.Parse( """{"type":"agent_start"}""" ).RootElement.Clone() );
		foreach ( var folder in new[] { changed, Path.Combine( changed, "engine" ), repository, outside, outside, outside } )
			shell.UpdateFrame( JsonSerializer.SerializeToElement( new
			{
				type = "message_end",
				messageId = Guid.NewGuid().ToString( "N" ),
				message = new { role = "toolResult", toolName = "bash", content = Array.Empty<object>(), details = new { cwd = folder.Replace( '\\', '/' ) } },
			} ) );
		shell.UpdateFrame( JsonDocument.Parse( """{"type":"agent_end"}""" ).RootElement.Clone() );
		CoreChecks.Check( PumpUntil( () => store.Get( follower.Id ).Worktree is { } worktree && ConversationStore.IsSameFolder( worktree, changed ) && Label( changed ) is not null ),
			"A project chat whose turn ran most of its commands in a linked worktree moves there and lists under its label; folders outside the repository do not count." );
		shell.Dispose();

		var vanishing = await GitWorktrees.AddAsync( repository, "vanishing", "", [], CancellationToken.None );
		var stranded = store.CreateConversation( SettingsProfile.Personal, project.Id );
		store.SetWorktree( stranded.Id, vanishing );
		CoreChecks.Check( PumpUntil( () => Label( vanishing ) is not null ), "A chat's linked worktree shows its label." );
		Run( repository, "worktree", "remove", "--force", vanishing );
		Directory.CreateDirectory( vanishing );
		// The label is built first: building the sidebar is what notices the folder is gone.
		CoreChecks.Check( PumpUntil( () => Label( vanishing ) is null && store.Get( stranded.Id ).Worktree is null ) && store.GetDirectory( stranded.Id ) == project.Directory,
			"A worktree git removed outside Lokn, leaving its empty folder, drops its label and moves its chats back to the main checkout." );

		var main = Shown().OfType<Text>().Single( text => text.AccessibilityName?.StartsWith( "Main checkout", StringComparison.Ordinal ) == true ).Content;
		FindButton( $"{main} options" )!.OnClick!();
		CoreChecks.Check( Entry( "Remove worktree" ) is { IsEnabled: false }, "The main checkout's label cannot be removed." );
		Shown().OfType<ContextMenuInput>().Single().Dismiss();
		await ExpectFailureAsync( () => actions.RemoveAsync( repository, repository, CancellationToken.None ), "Removing the main checkout is refused." );

		FindButton( $"{project.Name} options" )!.OnClick!();
		Choose( "Manage worktrees" );
		CoreChecks.Check( app.Panels.FindContent<WorktreesContent>() is { } manager && ConversationStore.IsSameFolder( manager.Content.CaptureState()!.Value.GetString()!, project.Directory ),
			"Manage worktrees shows the Worktrees panel on the project's repository." );

		store.ReleaseWorktree( changed, project.Directory );
		CoreChecks.Check( app.Unload( "worktrees" ), "Worktrees unloads while Conversations stays." );
		FindButton( $"{project.Name} options" )!.OnClick!();
		CoreChecks.Check( Entry( "New chat" ) is not null && Entry( "New worktree…" ) is null && Entry( "Manage worktrees" ) is null
			&& Shown().OfType<Text>().All( text => text.Key?.StartsWith( "worktree:", StringComparison.Ordinal ) != true ),
			"Without Worktrees the sidebar keeps a project's chats flat and offers no worktree actions." );
		Choose( "Rename" );
		Shown().OfType<TextEntry>().Single( entry => entry.AccessibilityName == "Rename project" ).OnSubmit!( "Renamed app" );
		CoreChecks.Check( store.ProjectStore.Get( project.Id ).Name == "Renamed app" && Shown().OfType<TextEntry>().All( entry => entry.AccessibilityName != "Rename project" )
			&& project.Directory == store.ProjectStore.Get( project.Id ).Directory,
			"Rename in a project's menu edits its name in place on the heading and keeps its folder." );
		app.BindDispatcher( null );
	}

	/// <summary>Walks a built view: every blob, and the context menu and confirmation dialog inputs mounted in it.</summary>
	private static IEnumerable<object> Walk( Blob blob )
	{
		yield return blob;
		if ( blob.GetType().GetProperty( "Children" )?.GetValue( blob ) is IEnumerable<Blob> children )
		{
			foreach ( var child in children.SelectMany( Walk ) )
				yield return child;
		}

		foreach ( var field in blob.GetType().GetFields( System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public ) )
		{
			var value = field.GetValue( blob );
			if ( value is ContextMenuInput or ConfirmDialogInput )
				yield return value;
			// The sidebar's projects and chats sit inside the profile slide's mounted input.
			if ( value is ProfileSlideInput slide )
			{
				foreach ( var item in Walk( slide.Content ) )
					yield return item;
			}
		}
	}

	/// <summary>Verifies native fingerprints, build records, and that shared folders and native builds reach a working tree through links that removal, clean and unsharing never follow.</summary>
	private static async Task CheckSharingAsync()
	{
		var root = Path.Combine( Path.GetTempPath(), "lokn-sharing-" + Guid.NewGuid().ToString( "N" ) );
		var repository = Path.Combine( root, "app" );
		Write( repository, "src/a.cpp", "native" );
		Write( repository, "engine/b.cs", "managed" );
		Write( repository, "bin/native/tracked.dll", "tracked" );
		Write( repository, ".gitignore", "cache/\nbin/native/*\n!bin/native/tracked.dll\n" );
		Run( repository, "init", "--quiet" );
		Run( repository, "add", "." );
		Commit( repository, "Initial" );
		Run( repository, "branch", "-M", "main" );
		CommitOnBranch( repository, "managed-only", "engine/b.cs", "managed change" );
		CommitOnBranch( repository, "native-change", "src/a.cpp", "native change" );
		Write( repository, "cache/c.bin", "cache" );
		var none = CancellationToken.None;

		var setup = new RepositorySetup( [], ["cache"], ["src"], "bin/native" );
		var fingerprints = await NativeBuilds.ComputeFingerprintsAsync( repository, ["refs/heads/main", "refs/heads/managed-only", "refs/heads/native-change"], setup.NativeInputs, none );
		CoreChecks.Check( fingerprints["refs/heads/main"] == fingerprints["refs/heads/managed-only"] && fingerprints["refs/heads/main"] != fingerprints["refs/heads/native-change"],
			"Native fingerprints are equal exactly when the native input paths hold the same files." );

		// The build must come after the commit's reflog second, as a real native build does.
		Thread.Sleep( 1100 );
		Write( repository, "bin/native/engine.dll", "built" );
		Write( repository, "bin/native/sub/engine.pdb", "symbols" );

		var setupPath = Path.Combine( root, "worktrees.json" );
		var store = new WorktreeSetupStore( setupPath );
		store.Load();
		store.SetSetup( repository, setup );
		store.SetBuild( repository, await WorktreeSharing.RecordAsync( repository, setup, none ) );

		Write( repository, "src/a.cpp", "uncommitted" );
		await ExpectFailureAsync( () => WorktreeSharing.RecordAsync( repository, setup, none ), "A build is not recorded while native sources have uncommitted changes." );
		Run( repository, "checkout", "--quiet", "--", "src/a.cpp" );

		var shared = await GitWorktrees.AddAsync( repository, "managed-only", "", [], none );
		var (messages, link) = await WorktreeSharing.ShareNewAsync( repository, shared, store.Capture( repository ), none );
		CoreChecks.Check( link is not null && string.Equals( link.Source, WorktreeSetupStore.Normalize( repository ), StringComparison.OrdinalIgnoreCase ),
			$"A working tree with the same native sources links the recorded build ({string.Join( " ", messages )})." );
		store.SetLink( shared, link );
		CoreChecks.Check( FolderLinks.IsLink( Path.Combine( shared, "cache" ) ) && File.ReadAllText( Path.Combine( shared, "cache", "c.bin" ) ) == "cache", "A shared folder is a junction to the main checkout's folder." );
		CoreChecks.Check( File.ReadAllText( Path.Combine( shared, "bin/native/engine.dll" ) ) == "built" && File.Exists( Path.Combine( shared, "bin/native/sub/engine.pdb" ) ) && File.ReadAllText( Path.Combine( shared, "bin/native/tracked.dll" ) ) == "tracked",
			"Native build files are linked beside the tracked files git checked out." );
		CoreChecks.Check( RunOutput( shared, "status", "--porcelain" ).Length == 0, "Shared folders and native files are no change to git." );

		var view = await WorktreeSharing.ReadViewAsync( repository, store, none );
		CoreChecks.Check( view.Details[shared].Native is { Action: NativeAction.StopSharing, IsWarning: false } sharing && sharing.Text.StartsWith( "Shares the native build", StringComparison.Ordinal )
			&& view.Details[shared].SharedNative is not null, "The linked working tree reports the build it shares." );
		CoreChecks.Check( view.Details[repository].Native is { IsWarning: false } recorded && recorded.Text.StartsWith( "Native build recorded", StringComparison.Ordinal ), "The main checkout reports its recorded build." );
		CoreChecks.Check( view.BranchNative.ContainsKey( "refs/heads/managed-only" ) && !view.BranchNative.ContainsKey( "refs/heads/native-change" ), "Branches show which recorded build has their native sources." );

		var other = await GitWorktrees.AddAsync( repository, "native-change", "", [], none );
		var (otherMessages, otherLink) = await WorktreeSharing.ShareNewAsync( repository, other, store.Capture( repository ), none );
		CoreChecks.Check( otherLink is null && !File.Exists( Path.Combine( other, "bin/native/engine.dll" ) ) && otherMessages.Any( message => message.StartsWith( "No recorded native build", StringComparison.Ordinal ) ),
			"A working tree with other native sources gets no native build." );
		CoreChecks.Check( FolderLinks.IsLink( Path.Combine( other, "cache" ) ), "Shared folders do not depend on native sources." );

		Write( shared, "notes.txt", "untracked" );
		await ExpectFailureAsync( () => WorktreeSharing.RemoveAsync( repository, shared, none ), "A working tree with untracked files is not removed." );
		CoreChecks.Check( FolderLinks.IsLink( Path.Combine( shared, "cache" ) ), "A refused removal leaves the links in place." );
		File.Delete( Path.Combine( shared, "notes.txt" ) );

		await WorktreeSharing.RemoveAsync( repository, other, none );
		CoreChecks.Check( !Directory.Exists( other ) && File.ReadAllText( Path.Combine( repository, "cache", "c.bin" ) ) == "cache", "Removing a working tree keeps the folders it shared." );

		// MSVC rewrites outputs in place; a hard link shows that, and the record notices the change.
		File.WriteAllText( Path.Combine( repository, "bin/native/engine.dll" ), "rebuilt" );
		view = await WorktreeSharing.ReadViewAsync( repository, store, none );
		CoreChecks.Check( File.ReadAllText( Path.Combine( shared, "bin/native/engine.dll" ) ) == "rebuilt" && view.Details[shared].Native is { IsWarning: true },
			"A rebuild of the shared build shows in the linked working tree, which warns about it." );
		CoreChecks.Check( view.Details[repository].Native is { Action: NativeAction.Record, IsWarning: true }, "A changed build asks to be recorded again." );

		await WorktreeSharing.StopSharingNativeAsync( shared, setup, none );
		CoreChecks.Check( !File.Exists( Path.Combine( shared, "bin/native/engine.dll" ) ) && File.Exists( Path.Combine( shared, "bin/native/tracked.dll" ) ) && File.ReadAllText( Path.Combine( repository, "bin/native/engine.dll" ) ) == "rebuilt",
			"Stopping sharing deletes the linked names and keeps tracked files and the shared build." );

		Run( shared, "clean", "-fdx", "--quiet" );
		CoreChecks.Check( File.ReadAllText( Path.Combine( repository, "cache", "c.bin" ) ) == "cache" && File.Exists( Path.Combine( repository, "bin/native/sub/engine.pdb" ) ),
			"git clean -fdx in a working tree removes its links, not the main checkout's files." );

		Run( repository, "checkout", "--quiet", "native-change" );
		Run( repository, "checkout", "--quiet", "main" );
		await ExpectFailureAsync( () => WorktreeSharing.RecordAsync( repository, setup, none ), "A build is not recorded when native sources changed after it was built." );

		var reloaded = new WorktreeSetupStore( setupPath );
		reloaded.Load();
		var restored = reloaded.Capture( repository );
		CoreChecks.Check( restored.Setup.SharedFolders.SequenceEqual( setup.SharedFolders ) && restored.Setup.NativeOutput == setup.NativeOutput && restored.Builds.ContainsKey( WorktreeSetupStore.Normalize( repository ) )
			&& restored.Links.ContainsKey( WorktreeSetupStore.Normalize( shared ) ), "The setup, build record and link survive a restart." );
	}

	/// <summary>Commits one file change on a new branch and returns to main.</summary>
	private static void CommitOnBranch( string repository, string branch, string file, string content )
	{
		Run( repository, "checkout", "--quiet", "-b", branch );
		Write( repository, file, content );
		Run( repository, "add", "." );
		Commit( repository, $"Change {file}" );
		Run( repository, "checkout", "--quiet", "main" );
	}

	/// <summary>Commits the staged changes with the fixture identity.</summary>
	private static void Commit( string directory, string message ) => Run( directory, [.. Identity, "commit", "--quiet", "-m", message] );

	/// <summary>Writes a file, creating its directory.</summary>
	private static void Write( string root, string relative, string content )
	{
		var path = Path.Combine( root, relative );
		Directory.CreateDirectory( Path.GetDirectoryName( path )! );
		File.WriteAllText( path, content );
	}

	/// <summary>Fails the check unless an operation throws git's or the module's error.</summary>
	private static async Task ExpectFailureAsync( Func<Task> operation, string description )
	{
		try
		{
			await operation();
		}
		catch ( InvalidOperationException )
		{
			CoreChecks.Check( true, description );
			return;
		}

		CoreChecks.Check( false, description );
	}

	/// <summary>Runs a git fixture command and fails the check when it does not succeed.</summary>
	private static void Run( string directory, params string[] arguments ) => RunOutput( directory, arguments );

	/// <summary>Runs a git fixture command, fails the check when it does not succeed, and returns its trimmed output.</summary>
	private static string RunOutput( string directory, params string[] arguments )
	{
		var start = new ProcessStartInfo( "git" )
		{
			WorkingDirectory = directory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		foreach ( var argument in arguments )
			start.ArgumentList.Add( argument );

		using var process = Process.Start( start )!;
		var output = process.StandardOutput.ReadToEndAsync();
		var error = process.StandardError.ReadToEndAsync();
		process.WaitForExit();
		CoreChecks.Check( process.ExitCode == 0, $"git {string.Join( ' ', arguments )} succeeds. {error.Result}" );
		return output.Result.Trim();
	}
}
