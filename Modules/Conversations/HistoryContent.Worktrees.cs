using System.Diagnostics;
using Goo;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Git;
using Lokn.Modules.Projects;

namespace Lokn.Modules.Conversations;

/// <summary>Lists a project's chats under its git worktrees, and creates, opens and removes worktrees through the <see cref="WorktreeActions"/> the Worktrees module contributes.</summary>
/// <remarks>
/// With those actions loaded, a project's chats list under a label per worktree they work in, in git's order; a worktree without chats is
/// left out. Without them, chats group by the folders they work in the same way. The listing is read again after Worktrees creates or removes
/// a worktree from anywhere (sidebar, panel or tool) and when the main checkout's git state changes while its label shows; nothing polls.
/// A label whose folder stops being a worktree, as when git removed it from a shell, moves its chats back to the main checkout.
/// </remarks>
internal sealed partial class HistoryContent
{
	/// <summary>Holds the git watches of the folders worktree headings name, by folder, for their branch.</summary>
	private readonly Dictionary<string, GitWatch> _worktreeWatches = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Collects the folders the current build's worktree headings name, so watches of folders no longer shown stop.</summary>
	private readonly HashSet<string> _shownWorktrees = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Holds the latest worktree listing of each project's repository, by project directory.</summary>
	private readonly Dictionary<string, WorktreeListing> _listings = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Counts finished listings, so the sidebar rebuilds when one arrives.</summary>
	private readonly State<int> _listingVersion = new( 0 );

	/// <summary>Tracks the project whose New worktree form is open, or <see cref="Guid.Empty"/>.</summary>
	private readonly State<Guid> _worktreeForm = new( Guid.Empty );

	/// <summary>Retains the branch typed for a new worktree.</summary>
	private readonly State<string> _worktreeBranch = new( "" );

	/// <summary>Retains where a new worktree's branch starts, or empty for the main checkout's commit.</summary>
	private readonly State<string> _worktreeStart = new( "" );

	/// <summary>Tracks the worktree whose removal waits for the user's confirmation, or empty.</summary>
	private readonly State<string> _removingWorktree = new( "" );

	/// <summary>Holds why the last worktree creation or removal failed, shown in its form or confirmation; empty when it did not.</summary>
	private readonly State<string> _worktreeError = new( "" );

	/// <summary>Tracks whether a worktree is being created or removed, which disables the buttons that would start another.</summary>
	private readonly State<bool> _isWorktreeBusy = new( false );

	/// <summary>Stops watching every worktree heading's folder when the sidebar closes.</summary>
	public override void Closed()
	{
		foreach ( var watch in _worktreeWatches.Values )
			watch.Dispose();
		_worktreeWatches.Clear();
	}

	/// <summary>Finds the worktree operations the Worktrees module contributes, or null while it is not loaded.</summary>
	private WorktreeActions? FindWorktreeActions() => context.App.Extensions.Get<WorktreeActions>( WorktreeActions.Point ).FirstOrDefault();

	/// <summary>Groups a project's chats by the folder they work in: the linked worktrees that hold chats, in git's order when the listing is known, then
	/// the other worktrees its chats work in, in the order their first chat is listed, then the project's own directory when chats work there.</summary>
	/// <param name="project">The project.</param>
	/// <param name="conversations">The project's chats in the order the sidebar lists them, which each group keeps.</param>
	/// <param name="worktrees">The working trees of the project's repository, or null when they are not known.</param>
	internal static IReadOnlyList<WorktreeGroup> ComputeWorktreeGroups( ProjectInfo project, IReadOnlyList<ConversationInfo> conversations, IReadOnlyList<WorktreeEntry>? worktrees = null )
	{
		// The checkout the project's directory is in (the main one, or the project lies in a linked one) is the project's own group.
		WorktreeEntry[] listed = worktrees is not null && worktrees.Any( worktree => !worktree.IsMain )
			? [.. worktrees.Where( worktree => !IsInside( project.Directory, worktree.Path ) )]
			: [];
		var groups = listed.Select( worktree => new WorktreeGroup( worktree.Path, IsMain: false, [] ) ).ToList();
		var main = new List<ConversationInfo>();
		foreach ( var conversation in conversations )
		{
			if ( conversation.Worktree is not { } worktree || ConversationStore.IsSameFolder( worktree, project.Directory ) )
			{
				main.Add( conversation );
				continue;
			}

			var group = groups.FirstOrDefault( candidate => ConversationStore.IsSameFolder( candidate.Directory, worktree ) );
			if ( group is null )
				groups.Add( group = new( worktree, IsMain: false, [] ) );
			group.Conversations.Add( conversation );
		}

		// A worktree without chats is not listed; the Worktrees panel still shows it.
		groups.RemoveAll( group => group.Conversations.Count == 0 );
		if ( main.Count > 0 )
			groups.Add( new( project.Directory, IsMain: true, main ) );
		return groups;
	}

	/// <summary>Gets the working trees of a project's repository from the latest listing, and lists them again when Worktrees created or removed one
	/// or the main checkout's watched git state changed; null without Worktrees or before the first listing, and empty outside a repository.</summary>
	private IReadOnlyList<WorktreeEntry>? ReadWorktrees( ProjectInfo project, WorktreeActions? actions )
	{
		if ( actions is null )
			return null;

		_ = _listingVersion.Value;
		var key = NormalizeFolder( project.Directory );
		var version = actions.ReadVersion();
		var state = _worktreeWatches.GetValueOrDefault( key )?.State;
		var listing = _listings.GetValueOrDefault( key );
		if ( listing is null || !listing.IsLoading && (listing.Version != version || !ReferenceEquals( listing.State, state )) )
		{
			_listings[key] = new( version, state, listing?.Worktrees, IsLoading: true );
			_ = UpdateListingAsync( key, actions, version, state );
		}

		return listing?.Worktrees;
	}

	/// <summary>Lists a repository's working trees off the UI thread and keeps the result; a folder outside any repository lists none.</summary>
	private async Task UpdateListingAsync( string key, WorktreeActions actions, int version, GitState? state )
	{
		IReadOnlyList<WorktreeEntry> worktrees;
		try
		{
			worktrees = await Task.Run( () => actions.ListAsync( key, context.Lifetime ) ).ConfigureAwait( false );
		}
		catch ( OperationCanceledException )
		{
			return;
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			worktrees = [];
		}

		context.Post( () =>
		{
			_listings[key] = new( version, state, worktrees, IsLoading: false );
			_listingVersion.Value++;
		} );
	}

	/// <summary>Adds a project's chats below its heading: flat while they all work in the project's own directory, otherwise under a label
	/// per worktree, the main checkout last, each with its menu, removal dialog and + grid.</summary>
	private void AddProjectChats( ProjectInfo project, IReadOnlyList<ConversationInfo> conversations, IReadOnlyList<WorktreeEntry>? worktrees, WorktreeActions? actions, List<Blob> children )
	{
		// Worktrees are This PC's alone: a paired host's project lists its chats flat.
		IReadOnlyList<WorktreeGroup> groups = Host.IsLocal ? ComputeWorktreeGroups( project, conversations, worktrees ) : [];
		if ( groups.All( group => group.IsMain ) )
		{
			for ( var index = 0; index < conversations.Count; index++ )
				children.Add( BuildConversation( conversations[index], index ) );
			return;
		}

		var mainPath = worktrees?.FirstOrDefault( worktree => worktree.IsMain )?.Path ?? project.Directory;
		foreach ( var group in groups )
		{
			var id = ComputeWorktreeId( group.Directory );
			var name = ComputeWorktreeName( group.Directory );
			if ( !group.IsMain && IsWorktreeGone( group.Directory ) )
				context.Post( () => Local.Store.ReleaseWorktree( group.Directory, mainPath ) );
			children.Add( BuildWorktreeHeading( project, group, id, name ) );
			if ( _sidebar.IsMenuOpen( id ) )
			{
				var isCheckout = group.IsMain || ConversationStore.IsSameFolder( group.Directory, mainPath );
				children.Add( BuildWorktreeMenu( project, group, id, name, isCheckout ? null : actions ) );
			}
			if ( actions is not null && _removingWorktree.Value.Length > 0 && ConversationStore.IsSameFolder( _removingWorktree.Value, group.Directory ) )
				children.Add( BuildRemoveWorktree( project, group, name, actions, mainPath ) );
			if ( _sidebar.BuildStarters( id, name, starter => module.NewTask( context.App, Local, project.Id, starter, group.Directory ) ) is { } starters )
				children.Add( starters );
			for ( var index = 0; index < group.Conversations.Count; index++ )
				children.Add( BuildConversation( group.Conversations[index], index, group.Directory ) );
		}
	}

	/// <summary>Builds the label of a worktree, its branch or folder name, with a + and a … revealed on hover; resting on the + offers other starters.</summary>
	private Row BuildWorktreeHeading( ProjectInfo project, WorktreeGroup group, Guid id, string name )
	{
		var isRevealed = _sidebar.IsRevealed( id );
		return _sidebar.BuildHoverRow( id, isSelected: false,
		[
			new Text
			{
				Key = $"worktree:{group.Directory}",
				Content = name,
				Color = Theme.Current.Muted,
				FontSize = Theme.Current.FontSize - 1,
				FlexGrow = 1,
				MinWidth = 0,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
				Padding = new Edges( Theme.Current.Spacing / 2, Theme.Current.Spacing, 0, Theme.Current.Spacing ),
				AccessibilityName = group.IsMain ? $"Main checkout, {name}" : $"Worktree {name}",
			},
			_sidebar.BuildAddButton( id, $"New chat in {name}", () => module.NewConversation( context.App, Local, project.Id, group.Directory ), isRevealed ),
			_sidebar.BuildMoreButton( id, $"{name} options", isRevealed ),
		] );
	}

	/// <summary>Builds a worktree label's menu: New chat, New task and Open folder, then Remove worktree while Worktrees is loaded, greyed out for the main checkout.</summary>
	/// <param name="project">The project.</param>
	/// <param name="group">The worktree.</param>
	/// <param name="id">The label's row.</param>
	/// <param name="name">The worktree's name.</param>
	/// <param name="removal">The operations that remove it, or null when it cannot be removed here.</param>
	private Blob BuildWorktreeMenu( ProjectInfo project, WorktreeGroup group, Guid id, string name, WorktreeActions? removal )
	{
		var entries = new List<MenuEntry>
		{
			new MenuAction( "New chat", () => context.Run( () => module.NewConversation( context.App, Local, project.Id, group.Directory ) ) ) { AccessKey = Key.N },
			new MenuAction( "New task", () => context.Run( () => module.NewTask( context.App, Local, project.Id, worktree: group.Directory ) ) ) { AccessKey = Key.T },
			new MenuAction( "Open folder", () => context.Run( () => OpenFolder( group.Directory ) ) ) { AccessKey = Key.O },
		};
		if ( FindWorktreeActions() is not null )
		{
			entries.Add( new MenuSeparator() );
			entries.Add( new MenuAction( "Remove worktree", () => context.Run( () => BeginRemoveWorktree( group.Directory ) ) )
			{
				AccessKey = Key.D,
				IsDestructive = true,
				IsEnabled = removal is not null,
			} );
		}

		return _sidebar.BuildMenu( id, $"{name} options", entries );
	}

	/// <summary>Asks the user, in a dialog, to confirm removing a worktree.</summary>
	private void BeginRemoveWorktree( string directory )
	{
		_sidebar.CloseMenu();
		_worktreeError.Value = "";
		_removingWorktree.Value = directory;
	}

	/// <summary>Builds the removal dialog: what removing deletes and keeps, git's refusal when it refused, and Cancel and Remove; it stays open while git works.</summary>
	private Blob BuildRemoveWorktree( ProjectInfo project, WorktreeGroup group, string name, WorktreeActions actions, string mainPath )
	{
		var moved = group.Conversations.Count switch
		{
			0 => "",
			1 => " Its chat moves back to the main checkout.",
			var count => $" Its {count} chats move back to the main checkout.",
		};
		var isBusy = _isWorktreeBusy.Value;
		return Cell.Mount<ConfirmDialogInput, ConfirmDialog>( "remove-worktree", new( context.App, $"Remove the worktree {name}?",
			$"Its folder is deleted and its branch is kept.{moved}", isBusy ? "Removing…" : "Remove",
			() => context.Run( () => ConfirmRemoveWorktree( project, group.Directory, actions, mainPath ) ),
			() => context.Run( CancelRemoveWorktree ) )
		{
			IsBusy = isBusy,
			Error = _worktreeError.Value.Length > 0 ? _worktreeError.Value : null,
		} );
	}

	/// <summary>Closes the removal dialog, unless git is removing the worktree now.</summary>
	private void CancelRemoveWorktree()
	{
		if ( _isWorktreeBusy.Value )
			return;

		_removingWorktree.Value = "";
		_worktreeError.Value = "";
	}

	/// <summary>Removes a worktree as confirmed; once git removed it, its chats move back to the main checkout, and when git refuses, as with uncommitted changes, nothing changes and its reason shows.</summary>
	private void ConfirmRemoveWorktree( ProjectInfo project, string path, WorktreeActions actions, string mainPath )
	{
		if ( _isWorktreeBusy.Value )
			return;

		_ = UpdateWorktreeAsync( async () =>
		{
			await actions.RemoveAsync( project.Directory, path, context.Lifetime ).ConfigureAwait( false );
			return path;
		}, removed =>
		{
			Local.Store.ReleaseWorktree( removed, mainPath );
			_removingWorktree.Value = "";
		} );
	}

	/// <summary>Adds the project menu's worktree entries while Worktrees is loaded and the project is in a repository: New worktree… and Manage worktrees.</summary>
	private void AddWorktreeEntries( ProjectInfo project, IReadOnlyList<WorktreeEntry>? worktrees, WorktreeActions? actions, List<MenuEntry> entries )
	{
		if ( actions is null || worktrees is not { Count: > 0 } )
			return;

		entries.Add( new MenuAction( "New worktree…", () => context.Run( () => BeginNewWorktree( project.Id ) ) ) { AccessKey = Key.W } );
		entries.Add( new MenuAction( "Manage worktrees", () => context.Run( () => actions.OpenManager( context.App, project.Directory ) ) ) { AccessKey = Key.M } );
	}

	/// <summary>Opens the New worktree form under a project's heading, empty.</summary>
	private void BeginNewWorktree( Guid projectId )
	{
		_sidebar.CloseMenu();
		_worktreeBranch.Value = "";
		_worktreeStart.Value = "";
		_worktreeError.Value = "";
		_worktreeForm.Value = projectId;
	}

	/// <summary>Closes the New worktree form.</summary>
	private void CancelWorktreeAction()
	{
		_worktreeForm.Value = Guid.Empty;
		_worktreeError.Value = "";
	}

	/// <summary>Reports whether a project's New worktree form is open, subscribing the building view.</summary>
	private bool IsCreatingWorktree( Guid projectId ) => _worktreeForm.Value == projectId;

	/// <summary>Builds the New worktree form under a project's heading: the branch, where a new branch starts, git's refusal when it refused, and Cancel and Create; Escape cancels.</summary>
	private Container BuildNewWorktree( ProjectInfo project, WorktreeActions actions )
	{
		var theme = Theme.Current;
		var isBusy = _isWorktreeBusy.Value;
		var children = new List<Blob>
		{
			new TextEntry
			{
				Key = "worktree-branch",
				AutoFocus = true,
				Value = _worktreeBranch.Value,
				Controlled = true,
				Placeholder = "Branch",
				AccessibilityName = "New worktree branch",
				Height = theme.ControlHeight,
				OnChange = value => context.Run( () => _worktreeBranch.Value = value ),
				OnSubmit = _ => context.Run( () => CreateWorktree( project, actions ) ),
				KeyBindings = [new( Key.Escape, KeyModifiers.None, () => context.Run( CancelWorktreeAction ) )],
			},
			new TextEntry
			{
				Key = "worktree-start",
				Value = _worktreeStart.Value,
				Controlled = true,
				Placeholder = "Start from (optional)",
				AccessibilityName = "New worktree start point",
				Height = theme.ControlHeight,
				OnChange = value => context.Run( () => _worktreeStart.Value = value ),
				OnSubmit = _ => context.Run( () => CreateWorktree( project, actions ) ),
				KeyBindings = [new( Key.Escape, KeyModifiers.None, () => context.Run( CancelWorktreeAction ) )],
			},
		};
		if ( _worktreeError.Value.Length > 0 )
			children.Add( new Text { Content = _worktreeError.Value, Selectable = true } );
		var create = _sidebar.BuildAction( isBusy ? "Creating…" : "Create", () => CreateWorktree( project, actions ), $"Create the worktree in {project.Name}" );
		create.Disabled = isBusy || _worktreeBranch.Value.Trim().Length == 0;
		children.Add( new Row
		{
			Gap = theme.Spacing,
			JustifyContent = JustifyContent.FlexEnd,
			Children = [_sidebar.BuildAction( "Cancel", CancelWorktreeAction ), create],
		} );
		return new Container
		{
			Key = "new-worktree",
			Gap = theme.Spacing,
			Padding = theme.Spacing,
			BackgroundColor = theme.Raised,
			BorderRadius = theme.Radius,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = $"New worktree in {project.Name}",
			Children = children,
		};
	}

	/// <summary>Creates the typed branch's worktree beside the repository with its saved setup, then opens a new chat working in it.</summary>
	private void CreateWorktree( ProjectInfo project, WorktreeActions actions )
	{
		var branch = _worktreeBranch.Value.Trim();
		if ( _isWorktreeBusy.Value || branch.Length == 0 )
			return;

		var start = _worktreeStart.Value.Trim();
		_ = UpdateWorktreeAsync( () => actions.CreateAsync( project.Directory, branch, start, context.Lifetime ), path =>
		{
			_worktreeForm.Value = Guid.Empty;
			if ( Local.Projects.Find( project.Id ) is not null )
				module.NewConversation( context.App, Local, project.Id, path );
		} );
	}

	/// <summary>Runs a worktree creation or removal off the UI thread, then applies its result, or shows why it failed.</summary>
	private async Task UpdateWorktreeAsync( Func<Task<string>> operation, Action<string> succeeded )
	{
		_isWorktreeBusy.Value = true;
		_worktreeError.Value = "";
		string result;
		try
		{
			result = await Task.Run( operation ).ConfigureAwait( false );
		}
		catch ( OperationCanceledException )
		{
			return;
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			context.Post( () =>
			{
				_isWorktreeBusy.Value = false;
				_worktreeError.Value = error.Message;
			} );
			return;
		}

		context.Post( () =>
		{
			_isWorktreeBusy.Value = false;
			succeeded( result );
		} );
	}

	/// <summary>Shows a worktree's folder in File Explorer, as the Worktrees panel does; only an existing directory is opened.</summary>
	private static void OpenFolder( string path )
	{
		if ( !Directory.Exists( path ) )
			return;

		var start = new ProcessStartInfo( "explorer.exe" )
		{
			UseShellExecute = false,
		};
		start.ArgumentList.Add( path );
		Process.Start( start )?.Dispose();
	}

	/// <summary>Derives a stable row identifier for a worktree label from its folder, for hover, its menu and the + grid.</summary>
	private static Guid ComputeWorktreeId( string directory ) =>
		new( System.Security.Cryptography.MD5.HashData( System.Text.Encoding.UTF8.GetBytes( NormalizeFolder( directory ).ToUpperInvariant() ) ) );

	/// <summary>Names a worktree by the branch checked out in it, watching its folder while a label shows it; without git, or before it answers, by the folder's name.</summary>
	private string ComputeWorktreeName( string directory )
	{
		var key = NormalizeFolder( directory );
		_shownWorktrees.Add( key );
		if ( !_worktreeWatches.TryGetValue( key, out var watch ) && context.App.Modules.Any( info => info.Id == "git" ) )
			_worktreeWatches[key] = watch = context.App.GetModule<GitModule>().Watch( context.App.Hosts.Local, key );
		return watch?.State?.Branch ?? FolderName.Compute( key );
	}

	/// <summary>Reports whether a worktree label's folder is no longer a worktree, as after git removed it outside Lokn: its watch read the folder
	/// as missing or outside any repository's top level, and no <c>.git</c> is left in it; false before the first read or without Git.</summary>
	private bool IsWorktreeGone( string directory ) =>
		_worktreeWatches.GetValueOrDefault( NormalizeFolder( directory ) )?.State is { } state
		&& !(state.Root is { } root && ConversationStore.IsSameFolder( root, directory ))
		&& !Path.Exists( Path.Combine( directory, ".git" ) );

	/// <summary>Stops watching the folders no worktree label of the finished build names.</summary>
	private void ReleaseWorktreeWatches()
	{
		foreach ( var key in _worktreeWatches.Keys.Where( key => !_shownWorktrees.Contains( key ) ).ToArray() )
		{
			_worktreeWatches[key].Dispose();
			_worktreeWatches.Remove( key );
		}

		_shownWorktrees.Clear();
	}

	/// <summary>Turns a folder into its full path without a trailing separator, the key of its watch, listing and label.</summary>
	private static string NormalizeFolder( string directory ) => Path.TrimEndingDirectorySeparator( Path.GetFullPath( directory ) );

	/// <summary>Reports whether a folder is another one or inside it, whatever the separators or letter case.</summary>
	private static bool IsInside( string folder, string parent )
	{
		var inner = NormalizeFolder( folder );
		var outer = NormalizeFolder( parent );
		return string.Equals( inner, outer, StringComparison.OrdinalIgnoreCase ) || inner.StartsWith( outer + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase );
	}
}

/// <summary>Holds the chats of a project that work in one folder.</summary>
/// <param name="Directory">The worktree, or the project's own directory.</param>
/// <param name="IsMain">Whether the folder is the project's own directory rather than a linked worktree.</param>
/// <param name="Conversations">The chats working there, in sidebar order.</param>
internal sealed record WorktreeGroup( string Directory, bool IsMain, List<ConversationInfo> Conversations );

/// <summary>Holds one listing of a project's repository and what it was read against.</summary>
/// <param name="Version">The Worktrees change count it was read at.</param>
/// <param name="State">The main checkout's watched git state it was read at, or null.</param>
/// <param name="Worktrees">The working trees, or null before the first listing finished.</param>
/// <param name="IsLoading">Whether a listing is running.</param>
internal sealed record WorktreeListing( int Version, GitState? State, IReadOnlyList<WorktreeEntry>? Worktrees, bool IsLoading );
