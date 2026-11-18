using Goo;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>An open context menu: the item it belongs to, its name and entries, and where a right-click opened it.</summary>
/// <param name="AnchorKey">The key of the item's handle.</param>
/// <param name="Name">The menu's accessible name.</param>
/// <param name="Entries">The menu's entries.</param>
/// <param name="At">The pointer position in the item's coordinates.</param>
internal sealed record GitMenu( string AnchorKey, string Name, IReadOnlyList<MenuEntry> Entries, Point At );

/// <summary>Builds the toolbar, the operation banner and the progress line, and every git command the toolbar and context menus run.</summary>
internal sealed partial class GitContent
{
	/// <summary>Stores the open context menu, or null.</summary>
	private GitMenu? _menu;

	/// <summary>Defines the font weight of the checked-out branch or commit in the toolbar.</summary>
	private const double HeadWeight = 600;

	/// <summary>Builds the toolbar: the checked-out branch or commit, then icons for fetch, pull with its options, push, new branch, stash and pop,
	/// the working-tree actions other modules add, and reload; a side view wraps it.</summary>
	private Row BuildToolbar()
	{
		var theme = Theme.Current;
		var idle = IsIdle;
		var onBranch = _state?.Branch is not null;
		var children = new List<Blob>();
		if ( _state is { } state && (state.Branch ?? state.Commit) is { } head )
			children.Add( new Row
			{
				Key = "git-head",
				FlexShrink = 1,
				MinWidth = 0,
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing / 2,
				Padding = new Edges( 0, theme.Spacing, 0, theme.Spacing / 2 ),
				AccessibilityRole = AccessibilityRole.Text,
				AccessibilityName = state.Branch is null ? $"Detached at {head}" : $"On branch {head}",
				Children =
				[
					GitIcons.Build( GitIcons.Branch, 14, theme.Muted ),
					new Text { Content = head, FontFamily = ViewStyle.CodeFontFamily, FontWeight = HeadWeight, Color = theme.AnsiYellow, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
				],
			} );

		children.Add( BuildIconTool( "fetch", GitIcons.Fetch, "Fetch every remote", idle, RunFetch ) );
		children.Add( new Row
		{
			Key = "pull",
			FlexShrink = 0,
			Children =
			[
				BuildIconTool( "pull", GitIcons.Pull, "Pull the current branch", idle && onBranch, RunPull ),
				BuildIconTool( "pull-options", GitIcons.ChevronDown, "Pull options", idle && onBranch, () => OpenMenu( "pull-options", "Pull options",
				[
					BuildCommand( "Pull with merge", RunPull ),
					BuildCommand( "Pull with rebase", () => RunCommand( "Pulling with rebase…", ( root, token ) => GitRepository.PullAsync( root, true, token ) ) ),
					new MenuSeparator(),
					BuildCommand( "Fetch every remote", RunFetch ),
				], new Point { X = 0, Y = theme.ControlHeight - theme.Spacing / 2 } ), narrow: true ),
			],
		} );
		children.Add( BuildIconTool( "push", GitIcons.Push, "Push the current branch", idle && onBranch, () => RunPush( null, force: false ) ) );
		children.Add( BuildToolGap() );
		children.Add( BuildIconTool( "new-branch", GitIcons.Branch, "New branch at HEAD", idle, () => AskNewBranch( "HEAD", "the current commit" ) ) );
		children.Add( BuildIconTool( "stash", GitIcons.Stash, "Stash every change", idle && _state is { Files: > 0 }, AskStash ) );
		children.Add( BuildIconTool( "pop", GitIcons.Pop, _stashes.Count > 0 ? $"Pop {_stashes[0].Name}: {_stashes[0].Message}" : "Pop the latest stash", idle && _stashes.Count > 0, () => RunStashApply( _stashes[0], pop: true ) ) );
		if ( _layout == GitLayout.Full && BuildChangeActions().ToArray() is { Length: > 0 } actions )
		{
			children.Add( BuildToolGap() );
			children.AddRange( actions );
		}
		children.Add( new Spacer() );
		children.Add( BuildIconTool( "reload", GitIcons.Reload, "Reload", _running is null && !_isLoading, ReloadAll ) );
		return new Row
		{
			Key = "git-toolbar",
			FlexShrink = 0,
			FlexWrap = _layout == GitLayout.Full ? FlexWrap.NoWrap : FlexWrap.Wrap,
			AlignItems = AlignItems.Center,
			Gap = 2,
			Padding = new Edges( theme.Spacing / 2, theme.Spacing ),
			BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
			BorderColor = theme.Border,
			Children = children,
		};
	}

	/// <summary>Reads the refs, log, stashes, operation, changes and live state again.</summary>
	private void ReloadAll()
	{
		Reload();
		_watch.Refresh();
		_changes.Refresh();
	}

	/// <summary>Builds one icon button of the toolbar, with its name as its tooltip; its key also names the handle a menu opens beside.</summary>
	private Blob BuildIconTool( string key, VectorPath icon, string name, bool isEnabled, Action action, int badge = 0, bool narrow = false, bool isOn = false ) =>
		Cell.Mount<GitToolInput, GitToolButton>( "tool:" + key, new( icon, name, isEnabled, () => _context.Run( action ), EnsureHandle( key ), badge, narrow, isOn ) );

	/// <summary>Builds the space between toolbar groups.</summary>
	private static Container BuildToolGap() => new() { Width = Theme.Current.Spacing * 1.5, FlexShrink = 0 };

	/// <summary>Builds one text button, for the banner and a working-tree action without an icon.</summary>
	private Button BuildToolButton( string label, string name, bool isEnabled, Action action )
	{
		var button = Theme.Current.BuildButton( label, () => _context.Run( action ), isEnabled );
		button.AccessibilityName = name;
		button.FlexShrink = 0;
		return button;
	}

	/// <summary>Builds the banner shown while a merge, rebase, cherry-pick or revert waits, with Continue and Abort; null otherwise.</summary>
	private Row? BuildBanner()
	{
		if ( _operation == GitOperation.None || _root is not { } root )
			return null;

		var theme = Theme.Current;
		var operation = _operation;
		var name = operation switch
		{
			GitOperation.Merge => "A merge",
			GitOperation.Rebase => "A rebase",
			GitOperation.CherryPick => "A cherry-pick",
			_ => "A revert",
		};
		var conflicts = _state?.Conflicted ?? 0;
		var text = conflicts > 0
			? $"{name} is in progress with {conflicts:N0} conflicted {(conflicts == 1 ? "file" : "files")}. Resolve and stage them, then continue."
			: $"{name} is in progress.";
		return new Row
		{
			Key = "git-banner",
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Padding = new Edges( theme.Spacing / 2, theme.Spacing ),
			BackgroundColor = theme.AnsiYellow.WithAlpha( 0.14 ),
			BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
			BorderColor = theme.AnsiYellow,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = text,
			Children =
			[
				new Text { Content = text, FlexGrow = 1, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis, },
				BuildToolButton( "Continue", $"Continue the {operation}", IsIdle, () =>
					Run( "Continuing…", token => GitRepository.ContinueAsync( root, operation, token ) ) ),
				BuildToolButton( "Abort", $"Abort the {operation}", IsIdle, () =>
					Run( "Aborting…", token => GitRepository.AbortAsync( root, operation, token ) ) ),
			],
		};
	}

	/// <summary>Builds the progress line while a command runs, with Stop, or the last error with a dismiss button; null when there is neither.</summary>
	private Row? BuildStatusLine()
	{
		var theme = Theme.Current;
		if ( _running is null && _error is null )
			return null;

		var children = new List<Blob>
		{
			new Text
			{
				Content = _running ?? _error ?? "",
				Color = _running is null ? theme.Error : theme.Muted,
				Selectable = _running is null,
				FlexGrow = 1,
				MinWidth = 0,
				TextMaxLines = 4,
				TextTrimming = TextTrimming.Ellipsis,
				FontSize = ViewStyle.SmallSize,
			},
		};
		if ( _running is not null )
			children.Add( BuildToolButton( "Stop", "Stop the running command", true, StopCommand ) );
		else
			children.Add( BuildToolButton( "×", "Dismiss the error", true, () =>
			{
				_error = null;
				MarkChanged();
			} ) );

		return new Row
		{
			Key = "git-status",
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Padding = new Edges( theme.Spacing / 4, theme.Spacing ),
			BackgroundColor = _running is null ? theme.Error.WithAlpha( 0.1 ) : Color.Transparent,
			BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
			BorderColor = theme.Border,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = _running ?? $"Git error: {_error}",
			Children = children,
		};
	}

	/// <summary>Opens a context menu beside an item, at the pointer.</summary>
	private void OpenMenu( string anchorKey, string name, IReadOnlyList<MenuEntry> entries, Point at )
	{
		_menu = new( anchorKey, name, entries, at );
		MarkChanged();
	}

	/// <summary>Mounts the open context menu, or null.</summary>
	private Blob? BuildMenu()
	{
		if ( _menu is not { } menu || !_handles.TryGetValue( menu.AnchorKey, out var anchor ) )
			return null;

		return Cell.Mount<ContextMenuInput, ContextMenu>( "git-menu", new(
			anchor,
			menu.Name,
			menu.Entries,
			() => _context.Run( () =>
			{
				_menu = null;
				MarkChanged();
			} ) )
		{
			AnchorRect = new ElementRect { X = menu.At.X, Y = menu.At.Y },
			Width = 240,
		} );
	}

	/// <summary>Builds a menu action that starts a command, disabled while another runs.</summary>
	private MenuAction BuildCommand( string label, Action run, bool isEnabled = true, bool isDestructive = false ) =>
		new( label, () => _context.Run( run ) )
		{
			IsEnabled = isEnabled && IsIdle,
			IsDestructive = isDestructive,
		};

	/// <summary>Lists a commit's actions: checkout, new branch or tag, cherry-pick, revert, resets, copying its identifier, then the commit actions other modules add.</summary>
	private List<MenuEntry> BuildCommitMenu( GitCommit commit )
	{
		var id = commit.Id;
		var shortId = commit.ShortId;
		var target = _state?.Branch ?? "HEAD";
		return
		[
			BuildCommand( "Checkout commit", () => RunCommand( $"Checking out {shortId}…", ( root, token ) => GitRepository.CheckoutCommitAsync( root, id, token ) ) ),
			BuildCommand( "New branch here…", () => AskNewBranch( id, shortId ) ),
			BuildCommand( "New tag here…", () => AskNewTag( id, shortId ) ),
			new MenuSeparator(),
			BuildCommand( "Cherry-pick", () => RunCommand( $"Cherry-picking {shortId}…", ( root, token ) => GitRepository.CherryPickAsync( root, id, token ) ) ),
			BuildCommand( "Revert", () => RunCommand( $"Reverting {shortId}…", ( root, token ) => GitRepository.RevertAsync( root, id, token ) ) ),
			new MenuSeparator(),
			new MenuNote( $"Reset {target} here" ),
			BuildCommand( "Soft: keep changes staged", () => RunReset( id, shortId, GitResetMode.Soft ) ),
			BuildCommand( "Mixed: keep changes unstaged", () => RunReset( id, shortId, GitResetMode.Mixed ) ),
			BuildCommand( "Hard: discard changes…", () => Confirm( $"Hard reset {target} to {shortId}?",
				"Every uncommitted change is discarded, and commits after this one leave the branch.", "Reset",
				() => RunReset( id, shortId, GitResetMode.Hard ) ), isDestructive: true ),
			new MenuSeparator(),
			new MenuAction( "Copy SHA", () => _context.Run( () => _context.App.Window?.Clipboard.WriteText( id ) ) ) { AccessKey = Key.C },
			.. BuildActionEntries( GitActionScope.Commit, id, null ),
		];
	}

	/// <summary>Lists a local branch's actions, then the branch actions other modules add.</summary>
	private List<MenuEntry> BuildLocalBranchMenu( GitRef branch )
	{
		var name = branch.ShortName;
		var current = _state?.Branch;
		var isOther = !branch.IsCurrent && current is not null;
		return
		[
			BuildCommand( "Checkout", () => Checkout( branch ), !branch.IsCurrent ),
			BuildCommand( $"Merge into {current ?? "current"}", () => RunCommand( $"Merging {name}…", ( root, token ) => GitRepository.MergeAsync( root, name, token ) ), isOther ),
			BuildCommand( $"Rebase {current ?? "current"} onto {name}", () => RunCommand( $"Rebasing onto {name}…", ( root, token ) => GitRepository.RebaseAsync( root, name, token ) ), isOther ),
			new MenuSeparator(),
			BuildCommand( "Pull", RunPull, branch.IsCurrent ),
			BuildCommand( "Push", () => RunPush( branch, force: false ) ),
			BuildCommand( "Force push…", () => Confirm( $"Force push {name}?",
				"The remote branch is overwritten with this one, unless someone else pushed to it since your last fetch.", "Force push",
				() => RunPush( branch, force: true ) ), isDestructive: true ),
			new MenuSeparator(),
			BuildCommand( "New branch from here…", () => AskNewBranch( name, name ) ),
			BuildCommand( "Rename…", () => Ask( $"Rename {name}", "New name", name, "Rename",
				newName => RunCommand( $"Renaming {name}…", ( root, token ) => GitRepository.RenameBranchAsync( root, name, newName, token ) ) ) ),
			BuildCommand( "Delete…", () => Confirm( $"Delete branch {name}?", "The branch is deleted locally; its remote branch stays.", "Delete",
				() => RunDeleteBranch( branch ) ), !branch.IsCurrent, isDestructive: true ),
			.. BuildActionEntries( GitActionScope.Branch, branch.Target, name ),
		];
	}

	/// <summary>Lists a remote branch's actions.</summary>
	private List<MenuEntry> BuildRemoteBranchMenu( GitRef branch )
	{
		var name = branch.ShortName;
		var current = _state?.Branch;
		return
		[
			BuildCommand( "Checkout (tracking)", () => Checkout( branch ) ),
			BuildCommand( $"Merge into {current ?? "current"}", () => RunCommand( $"Merging {name}…", ( root, token ) => GitRepository.MergeAsync( root, name, token ) ), current is not null ),
			new MenuSeparator(),
			BuildCommand( "Delete from remote…", () => Confirm( $"Delete {name} from {branch.Remote ?? "the remote"}?",
				"The branch is deleted on the remote for everyone.", "Delete",
				() => RunCommand( $"Deleting {name}…", ( root, token ) => GitRepository.DeleteBranchAsync( root, branch, false, token ) ) ), isDestructive: true ),
		];
	}

	/// <summary>Lists a tag's actions.</summary>
	private List<MenuEntry> BuildTagMenu( GitRef tag )
	{
		var name = tag.ShortName;
		return
		[
			BuildCommand( "Checkout", () => Checkout( tag ) ),
			BuildCommand( "Push tag", () => RunCommand( $"Pushing {name}…", ( root, token ) => GitRepository.PushTagAsync( root, name, token ) ) ),
			new MenuSeparator(),
			BuildCommand( "Delete…", () => Confirm( $"Delete tag {name}?", "The tag is deleted locally; a pushed tag stays on the remote.", "Delete",
				() => RunCommand( $"Deleting {name}…", ( root, token ) => GitRepository.DeleteTagAsync( root, name, token ) ) ), isDestructive: true ),
		];
	}

	/// <summary>Lists a stash's actions.</summary>
	private List<MenuEntry> BuildStashMenu( GitStash stash ) =>
	[
		BuildCommand( "Apply", () => RunStashApply( stash, pop: false ) ),
		BuildCommand( "Pop", () => RunStashApply( stash, pop: true ) ),
		new MenuSeparator(),
		BuildCommand( "Drop…", () => Confirm( $"Drop {stash.Name}?", "The stashed changes are deleted.", "Drop",
			() => RunCommand( $"Dropping {stash.Name}…", ( root, token ) => GitRepository.StashDropAsync( root, stash.Index, token ) ) ), isDestructive: true ),
	];

	/// <summary>Runs a command against the shown repository, captured now.</summary>
	private void RunCommand( string label, Func<string, CancellationToken, Task> operation )
	{
		if ( _root is { } root )
			Run( label, token => operation( root, token ) );
	}

	/// <summary>Checks out a branch, a remote branch through a tracking branch, or a tag's commit.</summary>
	private void Checkout( GitRef reference )
	{
		if ( reference.IsCurrent )
			return;

		if ( reference.Kind == GitRefKind.Tag )
			RunCommand( $"Checking out {reference.ShortName}…", ( root, token ) => GitRepository.CheckoutCommitAsync( root, reference.Name, token ) );
		else
			RunCommand( $"Checking out {reference.ShortName}…", ( root, token ) => GitRepository.CheckoutAsync( root, reference, token ) );
	}

	/// <summary>Fetches every remote.</summary>
	private void RunFetch() => RunCommand( "Fetching…", ( root, token ) => GitRepository.FetchAsync( root, token ) );

	/// <summary>Pulls the current branch with a merge.</summary>
	private void RunPull() => RunCommand( "Pulling…", ( root, token ) => GitRepository.PullAsync( root, false, token ) );

	/// <summary>Pushes a branch, or the current one when null.</summary>
	private void RunPush( GitRef? branch, bool force ) =>
		RunCommand( force ? "Force pushing…" : "Pushing…", ( root, token ) => GitRepository.PushAsync( root, branch, force, token ) );

	/// <summary>Applies a stash, dropping it when <paramref name="pop"/> is set.</summary>
	private void RunStashApply( GitStash stash, bool pop ) =>
		RunCommand( pop ? $"Popping {stash.Name}…" : $"Applying {stash.Name}…", ( root, token ) => GitRepository.StashApplyAsync( root, stash.Index, pop, token ) );

	/// <summary>Resets the current branch to a commit.</summary>
	private void RunReset( string id, string shortId, GitResetMode mode ) =>
		RunCommand( $"Resetting to {shortId}…", ( root, token ) => GitRepository.ResetAsync( root, id, mode, token ) );

	/// <summary>Deletes a local branch; when git refuses because it is not fully merged, asks before deleting it anyway.</summary>
	private void RunDeleteBranch( GitRef branch ) => RunCommand( $"Deleting {branch.ShortName}…", async ( root, token ) =>
	{
		try
		{
			await GitRepository.DeleteBranchAsync( root, branch, false, token ).ConfigureAwait( false );
		}
		catch ( InvalidOperationException error ) when ( error.Message.Contains( "not fully merged", StringComparison.OrdinalIgnoreCase ) )
		{
			_context.Post( () => Confirm( $"{branch.ShortName} is not fully merged",
				"Its commits that no other branch holds may be lost. Delete it anyway?", "Force delete",
				() => RunCommand( $"Deleting {branch.ShortName}…", ( forceRoot, forceToken ) => GitRepository.DeleteBranchAsync( forceRoot, branch, true, forceToken ) ) ) );
		}
	} );

	/// <summary>Asks for a branch name and creates it at a start point, checking it out.</summary>
	private void AskNewBranch( string startPoint, string description ) =>
		Ask( "New branch", $"Create a branch at {description} and check it out.", "", "Create",
			name => RunCommand( $"Creating {name}…", ( root, token ) => GitRepository.CreateBranchAsync( root, name, startPoint, true, token ) ) );

	/// <summary>Asks for a tag name and creates a lightweight tag at a commit.</summary>
	private void AskNewTag( string commit, string shortId ) =>
		Ask( "New tag", $"Create a tag at {shortId}.", "", "Create",
			name => RunCommand( $"Tagging {name}…", ( root, token ) => GitRepository.CreateTagAsync( root, name, commit, null, token ) ) );

	/// <summary>Asks for an optional message and stashes every change, untracked files included.</summary>
	private void AskStash() =>
		Ask( "Stash changes", "Every change is stashed, untracked files included. Message (optional):", "", "Stash",
			message => RunCommand( "Stashing…", ( root, token ) => GitRepository.StashAsync( root, message.Length > 0 ? message : null, true, token ) ),
			allowEmpty: true );
}
