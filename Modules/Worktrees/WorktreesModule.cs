using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Conversations;

namespace Lokn.Modules.Worktrees;

/// <summary>Contributes a panel that manages a project's git working trees and branches, and what the working trees share, and the chat sidebar's worktree operations.</summary>
public sealed class WorktreesModule : ILoknModule
{
	/// <summary>Stores an explicitly supplied project store, or null to use the loaded Conversations module's.</summary>
	private readonly ConversationStore? _store;

	/// <summary>Stores repository setups and native build records.</summary>
	private readonly WorktreeSetupStore _setup;

	/// <summary>Counts the working trees created or removed here, so the chat sidebar lists a repository again after each.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Stores the running module context for posting to the UI thread, or null while stopped.</summary>
	private ModuleContext? _context;

	/// <summary>Gets the feature's stable module identifier.</summary>
	public string Id => "worktrees";

	/// <summary>Gets the feature's display name.</summary>
	public string Title => "Worktrees";

	/// <summary>Gets the Conversations module as a requirement only when its project store is not supplied, and Git, whose runner it uses.</summary>
	public IReadOnlyList<string> Dependencies => _store is null ? ["conversations", "git"] : [];

	/// <summary>Creates the module over the loaded Conversations module's projects; used when the application loads the module by itself.</summary>
	public WorktreesModule() => _setup = new( WorktreeSetupStore.DefaultPath );

	/// <summary>Creates the module over an explicitly supplied project store and setup file.</summary>
	/// <param name="store">The projects offered as repositories.</param>
	/// <param name="setupPath">The worktree setup file.</param>
	internal WorktreesModule( ConversationStore store, string setupPath )
	{
		_store = store;
		_setup = new( setupPath );
	}

	/// <summary>Reads the setup file and registers the worktrees panel, its automatic palette command, the tool that moves an agent's chat into a worktree,
	/// and the worktree operations the chat sidebar offers.</summary>
	public void Register( ModuleBuilder builder )
	{
		_setup.Load();
		builder
			.Panel( "list", "Worktrees", context => new WorktreesContent( context, _store ?? context.App.GetModule<ConversationsModule>().Store, _setup, MarkChanged ) )
			.Contribute( WorktreeActions.Point, new WorktreeActions( ListAsync, CreateAsync, RemoveAsync, OpenManager, () => _version.Value ) )
			.ToolAsync( "enter",
				"Moves your chat into a git worktree of its repository, so you work on a branch in parallel with other chats without touching the main checkout. Call it before changing any file whenever the user asks you to work in a worktree, on a separate branch in parallel, or alongside other agents. It creates the worktree beside the repository (<repository>.worktrees/<branch>) with the repository's Lokn worktree setup, or reuses the worktree that already has the branch, and Lokn's sidebar lists the chat under it. Pass main: true instead to move back to the main checkout.",
				"""
				{
					"type": "object",
					"properties": {
						"branch": { "type": "string", "description": "The branch to work on. An existing local or remote branch is checked out; any other name creates a new branch." },
						"start": { "type": "string", "description": "Where a new branch starts, such as main or a commit; defaults to the main checkout's commit." },
						"main": { "type": "boolean", "description": "True to move back to the repository's main checkout instead of a worktree." }
					},
					"additionalProperties": false
				}
				""",
				EnterAsync );
	}

	/// <summary>Keeps the module context, for posting changes to the UI thread.</summary>
	public void Start( ModuleContext context ) => _context = context;

	/// <summary>Drops the module context.</summary>
	public void Stop() => _context = null;

	/// <summary>Reports, from any thread, that a working tree was created or removed, so the chat sidebar lists its repository again.</summary>
	private void MarkChanged() => _context?.Post( () => _version.Value++ );

	/// <summary>Lists the working trees of the repository holding a folder for the chat sidebar, main checkout first, leaving out those whose directory is gone.</summary>
	private static async Task<IReadOnlyList<WorktreeEntry>> ListAsync( string repository, CancellationToken cancellationToken )
	{
		var worktrees = await GitWorktrees.ListAsync( repository, cancellationToken ).ConfigureAwait( false );
		return [.. worktrees.Where( worktree => !worktree.IsPrunable ).Select( worktree => new WorktreeEntry( worktree.Path, worktree.Branch, worktree.IsMain ) )];
	}

	/// <summary>Creates the worktree of a branch for the chat sidebar with the repository's saved setup, as the panel and the tool do, and returns its directory.</summary>
	private async Task<string> CreateAsync( string repository, string branch, string startPoint, CancellationToken cancellationToken )
	{
		var worktrees = await GitWorktrees.ListAsync( repository, cancellationToken ).ConfigureAwait( false );
		var (path, _) = await WorktreeSharing.CreateAsync( _setup, worktrees[0].Path, branch.Trim(), startPoint.Trim(), null, cancellationToken ).ConfigureAwait( false );
		MarkChanged();
		return path;
	}

	/// <summary>Removes a linked worktree for the chat sidebar after its folder links, as the panel does, and forgets its native records; the main checkout and a worktree with changes are refused.</summary>
	private async Task RemoveAsync( string repository, string path, CancellationToken cancellationToken )
	{
		var worktrees = await GitWorktrees.ListAsync( repository, cancellationToken ).ConfigureAwait( false );
		if ( ConversationStore.IsSameFolder( worktrees[0].Path, path ) )
			throw new InvalidOperationException( $"{path} is the repository's main checkout, which cannot be removed." );

		await WorktreeSharing.RemoveAsync( worktrees[0].Path, path, cancellationToken ).ConfigureAwait( false );
		_setup.Forget( path );
		MarkChanged();
	}

	/// <summary>Shows the Worktrees panel on the repository holding a folder, reusing an open one.</summary>
	private static void OpenManager( LoknApplication app, string directory )
	{
		if ( app.Panels.FindContent<WorktreesContent>() is { } open )
		{
			open.Content.ShowRepository( directory );
			app.Panels.Activate( open.Id );
			return;
		}

		app.Panels.Open( "worktrees.list", input: directory );
	}

	/// <summary>Moves the calling agent's chat into the worktree of a branch, creating it with the repository's setup when no worktree has the branch, or back to the main checkout.</summary>
	private async Task<AgentToolResult> EnterAsync( AgentToolContext context, JsonElement arguments )
	{
		var branch = arguments.TryGetProperty( "branch", out var named ) && named.ValueKind == JsonValueKind.String ? named.GetString()!.Trim() : "";
		var start = arguments.TryGetProperty( "start", out var started ) && started.ValueKind == JsonValueKind.String ? started.GetString()!.Trim() : "";
		var isMain = arguments.TryGetProperty( "main", out var main ) && main.ValueKind == JsonValueKind.True;
		if ( isMain == (branch.Length > 0) )
			throw new ArgumentException( "Pass either a branch or main: true." );

		var conversations = context.App.GetModule<ConversationsModule>();
		if ( context.Caller is not { } caller || conversations.FindConversation( caller ) is not { } conversation )
			throw new InvalidOperationException( "Only the agent of a chat can move its chat into a worktree." );

		var store = _store ?? conversations.Store;
		var previous = store.GetDirectory( conversation.Id );
		var worktrees = await GitWorktrees.ListAsync( previous, context.Cancellation );
		var mainPath = worktrees[0].Path;
		string path;
		string created;
		if ( isMain )
		{
			path = mainPath;
			created = "";
		}
		else if ( worktrees.FirstOrDefault( worktree => worktree.Branch == branch ) is { } existing )
		{
			if ( start.Length > 0 )
				throw new ArgumentException( $"{branch} is already checked out in {existing.Path}, at its own commit. Leave out start to work there." );

			path = existing.Path;
			created = "";
		}
		else
		{
			(path, var messages) = await WorktreeSharing.CreateAsync( _setup, mainPath, branch, start, null, context.Cancellation );
			MarkChanged();
			created = string.Join( " ", [$"Created the worktree of {branch} at {path}.", .. messages] ) + " ";
		}

		if ( store.Find( conversation.Id ) is null )
			throw new InvalidOperationException( "The chat was deleted." );

		store.SetWorktree( conversation.Id, path );
		var current = store.GetDirectory( conversation.Id );
		if ( ConversationStore.IsSameFolder( previous, current ) )
			return new( $"{created}This chat already works in {current}." );

		return new( $"{created}This chat now works in {current}. Your shell and file tools still start in {previous} for the rest of this turn, so use absolute paths under {current} or cd into it for every command and edit. From your next turn on, you start there." );
	}
}
