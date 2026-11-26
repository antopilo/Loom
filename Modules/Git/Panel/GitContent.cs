using System.Text.Json;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Opens the Git panel on a repository, optionally on one working-tree file's diff, and optionally reconnects the review an action linked to it.</summary>
/// <param name="Directory">A folder inside the repository's working tree.</param>
/// <param name="ReviewAction">The key of the <see cref="GitAction"/> that linked the review, or null.</param>
/// <param name="ReviewId">The linked review's identifier, or null.</param>
/// <param name="Focus">The full path of a changed file whose diff shows once the changes load, or null for the commit graph.</param>
/// <param name="Staged">Whether the focused file shows its staged change first.</param>
/// <param name="Commit">A commit, full or short hash, to select once the graph loads, or null.</param>
internal sealed record GitPanelInput( string Directory, string? ReviewAction = null, string? ReviewId = null, string? Focus = null, bool Staged = false, string? Commit = null );

/// <summary>Names how the Git views are laid out: the whole panel, or one narrow side-view mode.</summary>
internal enum GitLayout
{
	/// <summary>The Git panel: the commit graph with the working tree or the selected commit beside it, or one file's diff.</summary>
	Full,

	/// <summary>The side view's Changes mode: the working tree as folder trees with checkboxes, then the commit box.</summary>
	Changes,

	/// <summary>The side view's History mode: branches, tags and stashes above the commit log and the selected commit.</summary>
	History,
}

/// <summary>Shows a repository: the commit graph of every branch with the working tree or the selected commit beside it, or one file's diff with its
/// hunks, and runs git commands from toolbar buttons and context menus.</summary>
/// <remarks>
/// Every command goes through <see cref="Run"/>, which runs it off the UI thread, shows its progress and git's error, and then
/// reloads. The refs and the log reload when the folder's live <see cref="GitState"/> changes branch, commit, files or upstream distance.
/// The Files side view's Changes and History modes are this content in a narrow <see cref="GitLayout"/>.
/// </remarks>
internal sealed partial class GitContent : PanelContent
{
	/// <summary>Defines how many commits the log reads.</summary>
	private const int LogLimit = 5000;

	/// <summary>Defines the font weight of emphasized text, such as the current branch.</summary>
	private const double EmphasisWeight = 600;

	/// <summary>Defines the font weight of ordinary text.</summary>
	private const double NormalWeight = 400;

	/// <summary>Defines the opacity of the accent fill behind a selected row.</summary>
	private const double SelectionOpacity = 0.18;

	/// <summary>Stores the owning panel's dispatch and lifetime.</summary>
	private readonly PanelContext _context;

	/// <summary>Rebuilds the panel after loads and view changes.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Builds the Local Changes view.</summary>
	private readonly GitChangesPane _changes;

	/// <summary>Watches the folder's live git state.</summary>
	private readonly GitWatch _watch;

	/// <summary>Stores the folder the panel was opened on.</summary>
	private readonly string _directory;

	/// <summary>Stores the state last seen by a build, to notice a new one.</summary>
	private GitState? _state;

	/// <summary>Stores the repository root of the latest state, or null outside a repository; read on the UI thread only.</summary>
	private string? _root;

	/// <summary>Stores the branch, commit, files and upstream distance the refs and log were last loaded for.</summary>
	private string? _loadedKey;

	/// <summary>Cancels the running refs and log load when a newer one starts or the panel closes.</summary>
	private CancellationTokenSource? _load;

	/// <summary>Cancels the running command when the user stops it or the panel closes.</summary>
	private CancellationTokenSource? _command;

	/// <summary>Stores the running command's progress text, such as "Pushing…", or null when none runs.</summary>
	private string? _running;

	/// <summary>Stores the last command's or load's error message until dismissed, or null.</summary>
	private string? _error;

	/// <summary>Reports whether a refs and log load is running.</summary>
	private bool _isLoading;

	/// <summary>Reports whether the working tree, rather than a commit, shows beside the graph.</summary>
	private bool _isWorkingTree = true;

	/// <summary>Shows one file's diff in place of the graph.</summary>
	private readonly GitFileView _file;

	/// <summary>Stores the full path of a file whose diff shows once the changes load, or null.</summary>
	private string? _focus;

	/// <summary>Stores a commit, full or short hash, to select once the graph that holds it loads, or null.</summary>
	private string? _focusCommit;

	/// <summary>Stores whether the focused file shows its staged change first.</summary>
	private bool _focusStaged;

	/// <summary>Stores the loaded commits with their graph, or null before the first load.</summary>
	private GitHistory? _history;

	/// <summary>Stores the loaded branches, remote branches and tags.</summary>
	private IReadOnlyList<GitRef> _refs = [];

	/// <summary>Stores the loaded refs by the commit they point at.</summary>
	private Dictionary<string, IReadOnlyList<GitRef>> _refsByCommit = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Stores the loaded stashes, newest first.</summary>
	private IReadOnlyList<GitStash> _stashes = [];

	/// <summary>Stores the merge, rebase, cherry-pick or revert in progress.</summary>
	private GitOperation _operation;

	/// <summary>Stores how the views are laid out: the whole panel or one side-view mode.</summary>
	private readonly GitLayout _layout;

	/// <summary>Finds authors' pictures for the avatars; null when the Git module is not running.</summary>
	private readonly GitAvatars? _pictures;

	/// <summary>Creates the panel, or a side-view mode, on a folder and starts watching it; the first state read loads the refs and log.</summary>
	internal GitContent( PanelContext context, GitPanelInput input, GitLayout layout = GitLayout.Full )
	{
		_context = context;
		_directory = input.Directory;
		_layout = layout;
		_review = input is { ReviewAction: { } action, ReviewId: { } id } ? (action, id) : null;
		var git = context.App.GetModule<GitModule>();
		_watch = git.Watch( context.Host, _directory );
		_pictures = git.Pictures;
		_changes = new GitChangesPane( context, () => _root ?? "", Run, () => ReviewFindings, OpenChange, OnChangesLoaded );
		_file = new GitFileView( context, _changes, () => _root, Run, CloseFile );
		_focus = input.Focus;
		_focusCommit = input.Commit;
		_focusStaged = input.Staged;
		_buildLogRow = layout == GitLayout.Full ? BuildTableRow : BuildLogRow;
		_buildDetailRow = BuildDetailRow;
		_buildFileItem = BuildFileItem;
		_buildInspectorRow = BuildInspectorRow;
		_buildSidebarRow = BuildSidebarRow;
	}

	/// <summary>Builds the layout's toolbar, banners and views, with any open menu or dialog.</summary>
	public override Blob Build()
	{
		_ = _version.Value;
		ObserveState( _watch.State );
		UpdateReviewResult();
		var theme = Theme.Current;
		var children = _layout switch
		{
			GitLayout.Changes => BuildChangesMode(),
			GitLayout.History => BuildHistoryMode(),
			_ => BuildPanel(),
		};
		if ( BuildMenu() is { } menu )
			children.Add( menu );
		if ( BuildDialog() is { } dialog )
			children.Add( dialog );

		return new Container
		{
			Key = "git",
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			FontFamily = theme.FontFamily,
			FontSize = theme.FontSize,
			Color = theme.Text,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = _layout switch
			{
				GitLayout.Changes => $"Changes: {RepositoryName}",
				GitLayout.History => $"History: {RepositoryName}",
				_ => $"Git: {RepositoryName}",
			},
			Children = children,
		};
	}

	/// <summary>Builds the whole panel: the toolbar, the operation banner, the progress or error line, the review, then the graph beside the working tree
	/// or the selected commit, or one file's diff.</summary>
	private List<Blob> BuildPanel()
	{
		var children = new List<Blob> { BuildToolbar() };
		AddNotices( children );
		// The graph and each file's diff enter with a short fade and rise, so switching between them reads as one motion.
		var main = BuildRepositoryMessage() ?? Cell.Mount<GitRevealInput, GitReveal>(
			_file.Target is { } target ? $"file:{target.Commit}:{target.Path}" : "graph",
			new( _file.Target is not null ? _file.Build() : BuildGraphView() ) );
		children.Add( new Container
		{
			Key = "git-main",
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			KeyBindings = _file.Target is null ? [] : [new( Key.Escape, KeyModifiers.None, () => _context.Run( CloseFile ) )],
			Children = [main],
		} );
		return children;
	}

	/// <summary>Shows a clicked working-tree file: in the panel, as its diff; in the side view, as its diff in the Git panel, while the side list keeps the focus.</summary>
	private void OpenChange( GitFileChange file, bool staged )
	{
		if ( _layout == GitLayout.Full )
			ShowFile( new GitFileTarget( file.Path, staged ) );
		else if ( _root is { } root )
			_context.App.GetModule<GitModule>().OpenPanel( _context.App, _context.Id, root, Path.GetFullPath( Path.Combine( root, file.Path ) ), staged, keepFocus: true );
	}

	/// <summary>Shows one file's diff in place of the graph.</summary>
	private void ShowFile( GitFileTarget target )
	{
		_file.Show( target );
		MarkChanged();
	}

	/// <summary>Returns from a file's diff to the graph.</summary>
	private void CloseFile()
	{
		_file.Hide();
		MarkChanged();
	}

	/// <summary>Reads the shown file again and shows a file asked for before the changes loaded, once they have.</summary>
	private void OnChangesLoaded()
	{
		_file.Refresh();
		ResolveFocus();
	}

	/// <summary>Shows a commit beside the graph, selected and scrolled to, once the graph holds it.</summary>
	/// <param name="commit">The commit's full or short hash.</param>
	internal void RevealCommit( string commit )
	{
		_focusCommit = commit;
		ResolveCommit();
	}

	/// <summary>Selects the asked-for commit when the loaded graph holds it; a commit made since the last load waits for the next one.</summary>
	private void ResolveCommit()
	{
		if ( _focusCommit is not { } wanted || _history?.Commits.FirstOrDefault( commit => commit.Id.StartsWith( wanted, StringComparison.OrdinalIgnoreCase ) ) is not { } found )
			return;
		_focusCommit = null;
		_file.Hide();
		SelectCommit( found.Id, reveal: true );
	}

	/// <summary>Shows the working tree beside the graph, or one changed file's diff; called when another view asks for this panel.</summary>
	/// <param name="focus">The full path of a changed file, or null for the graph and the working tree.</param>
	/// <param name="staged">Whether the file shows its staged change first.</param>
	internal void Reveal( string? focus, bool staged )
	{
		if ( focus is null )
		{
			_file.Hide();
			SelectWorkingTree();
			return;
		}

		_focus = focus;
		_focusStaged = staged;
		ResolveFocus();
	}

	/// <summary>Shows the asked-for file's diff once the changes are loaded: its asked-for side, else the other; the graph stays when the working tree no longer changes it.</summary>
	private void ResolveFocus()
	{
		if ( _focus is not { } focus || _root is not { } root || _changes.Changes is not { } changes )
			return;

		_focus = null;
		var path = Path.GetRelativePath( root, focus ).Replace( Path.DirectorySeparatorChar, '/' );
		var preferred = _focusStaged ? changes.Staged : changes.Unstaged;
		var other = _focusStaged ? changes.Unstaged : changes.Staged;
		if ( preferred.Any( file => file.Path == path ) )
			ShowFile( new GitFileTarget( path, _focusStaged ) );
		else if ( other.Any( file => file.Path == path ) )
			ShowFile( new GitFileTarget( path, !_focusStaged ) );
		else
			SelectWorkingTree();
	}

	/// <summary>Adds the operation banner, the progress or error line and the linked review, each when it shows.</summary>
	private void AddNotices( List<Blob> children )
	{
		if ( BuildBanner() is { } banner )
			children.Add( banner );
		if ( BuildStatusLine() is { } status )
			children.Add( status );
		if ( BuildReviewBanner() is { } review )
			children.Add( review );
	}

	/// <summary>Builds the line shown in place of the views while the repository is read or when the folder is in none; null once a repository shows.</summary>
	private Text? BuildRepositoryMessage() =>
		_state is null ? BuildMessage( "Reading the repository…" )
		: _root is null ? BuildMessage( _state.Error ?? $"{_directory} is not inside a git repository." )
		: null;

	/// <summary>Keeps the folder, the linked review and a shown working-tree file, so a restored layout resumes the same surface.</summary>
	public override JsonElement? CaptureState()
	{
		var file = _file.Target is { Commit: null } target && _root is { } root ? Path.GetFullPath( Path.Combine( root, target.Path ) ) : null;
		return JsonSerializer.SerializeToElement( new GitPanelInput( _directory, _review?.Action, _review?.Id, file, _file.Target?.Staged ?? false ) );
	}

	/// <summary>Stops the running loads and command and the git watch.</summary>
	public override void Closed()
	{
		_load?.Cancel();
		_command?.Cancel();
		_details?.Cancel();
		_changes.Close();
		_file.Close();
		_watch.Dispose();
	}

	/// <summary>Gets the repository's folder name, shown as the panel's heading.</summary>
	internal string RepositoryName => FolderName.Compute( _root ?? _directory );

	/// <summary>Reports whether this panel shows the repository that holds <paramref name="directory"/>.</summary>
	internal bool Shows( string directory )
	{
		var folder = Path.TrimEndingDirectorySeparator( Path.GetFullPath( directory ) );
		if ( _root is { } root )
		{
			var top = Path.TrimEndingDirectorySeparator( Path.GetFullPath( root ) );
			return string.Equals( folder, top, StringComparison.OrdinalIgnoreCase )
				|| folder.StartsWith( top + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase );
		}

		return string.Equals( folder, Path.TrimEndingDirectorySeparator( Path.GetFullPath( _directory ) ), StringComparison.OrdinalIgnoreCase );
	}

	/// <summary>Runs one git command off the UI thread: shows <paramref name="label"/> while it runs, disables the other commands, shows git's error when it fails, then reloads everything.</summary>
	/// <param name="label">The progress text, such as "Pushing…".</param>
	/// <param name="operation">The command; it receives a token cancelled when the user stops it or the panel closes.</param>
	/// <remarks>Ignored while another command runs or outside a repository; call it on the UI thread.</remarks>
	internal void Run( string label, Func<CancellationToken, Task> operation )
	{
		if ( _running is not null || _root is null )
			return;

		var command = CancellationTokenSource.CreateLinkedTokenSource( _context.Lifetime );
		_command = command;
		_running = label;
		_error = null;
		MarkChanged();
		_ = Task.Run( async () =>
		{
			string? error = null;
			try
			{
				await operation( command.Token ).ConfigureAwait( false );
			}
			catch ( OperationCanceledException ) when ( command.IsCancellationRequested )
			{
				error = "Stopped.";
			}
			catch ( Exception failure ) when ( failure is not OutOfMemoryException )
			{
				error = failure.Message;
			}
			_context.Post( () => FinishCommand( command, error ) );
		} );
	}

	/// <summary>Gets whether no command runs and a repository is shown, so commands can start.</summary>
	private bool IsIdle => _running is null && _root is not null;

	/// <summary>Ends a command: shows its error, then reloads the refs, log, stashes, operation, changes and live state.</summary>
	private void FinishCommand( CancellationTokenSource command, string? error )
	{
		if ( _command != command )
			return;

		_command = null;
		command.Dispose();
		_running = null;
		_error = error;
		Reload();
		_watch.Refresh();
		_changes.Refresh();
		MarkChanged();
	}

	/// <summary>Stops the running command.</summary>
	private void StopCommand() => _command?.Cancel();

	/// <summary>Notices a new live state during a build and schedules what it changes: the changes always, the refs and log when its key differs.</summary>
	private void ObserveState( GitState? state )
	{
		if ( ReferenceEquals( state, _state ) )
			return;

		_state = state;
		_root = state?.Root;
		_context.Post( UpdateState );
	}

	/// <summary>Reloads the changes for a new state, and the refs and log when its branch, commit, files or upstream distance changed.</summary>
	private void UpdateState()
	{
		_changes.Refresh();
		var key = _state is { Root: { } root } state ? $"{root}|{state.Branch}|{state.Commit}|{state.Files}|{state.Ahead}|{state.Behind}" : null;
		if ( key == _loadedKey )
			return;

		_loadedKey = key;
		Reload();
	}

	/// <summary>Reads the log with its graph, the refs, the stashes and the operation in progress in the background, keeping the shown ones until they arrive.</summary>
	/// <remarks>The Changes mode shows neither the log nor the refs, so it reads only the stashes and the operation.</remarks>
	private void Reload()
	{
		if ( _root is not { } root )
			return;

		_load?.Cancel();
		var load = CancellationTokenSource.CreateLinkedTokenSource( _context.Lifetime );
		_load = load;
		_isLoading = true;
		MarkChanged();
		var withHistory = _layout != GitLayout.Changes;
		_ = Task.Run( async () =>
		{
			try
			{
				var log = withHistory ? GitRepository.ReadLogAsync( _context.Workspace, root, LogLimit, load.Token ) : Task.FromResult<IReadOnlyList<GitCommit>>( [] );
				var refs = withHistory ? GitRepository.ReadRefsAsync( _context.Workspace, root, load.Token ) : Task.FromResult<IReadOnlyList<GitRef>>( [] );
				var stashes = GitRepository.ReadStashesAsync( _context.Workspace, root, load.Token );
				var operation = GitRepository.ReadOperationAsync( _context.Workspace, root, load.Token );
				await Task.WhenAll( log, refs, stashes, operation ).ConfigureAwait( false );
				var history = GitHistory.Build( log.Result );
				_context.Post( () => ApplyLoad( load, root, history, refs.Result, stashes.Result, operation.Result, null ) );
			}
			catch ( OperationCanceledException ) when ( load.IsCancellationRequested )
			{
			}
			catch ( Exception error ) when ( error is not OutOfMemoryException )
			{
				_context.Post( () => ApplyLoad( load, root, null, null, null, GitOperation.None, error.Message ) );
			}
		} );
	}

	/// <summary>Shows a finished load unless a newer one replaced it, keeping the selected commit when it is still there.</summary>
	private void ApplyLoad( CancellationTokenSource load, string root, GitHistory? history, IReadOnlyList<GitRef>? refs, IReadOnlyList<GitStash>? stashes, GitOperation operation, string? error )
	{
		if ( _load != load )
			return;

		_load = null;
		load.Dispose();
		_isLoading = false;
		if ( error is not null || history is null || refs is null || stashes is null || root != _root )
		{
			_error ??= error;
			MarkChanged();
			return;
		}

		_history = history;
		_refs = refs;
		_stashes = stashes;
		_operation = operation;
		_refsByCommit = refs
			.GroupBy( item => item.Target, StringComparer.OrdinalIgnoreCase )
			.ToDictionary( group => group.Key, group => (IReadOnlyList<GitRef>)group.OrderBy( item => item.Kind ).ThenByDescending( item => item.IsCurrent ).ToArray(), StringComparer.OrdinalIgnoreCase );
		UpdateSelectionAfterLoad();
		ResolveCommit();
		MarkChanged();
	}

	/// <summary>Builds a muted line in place of a view.</summary>
	private static Text BuildMessage( string text ) => new()
	{
		Key = "git-message",
		Content = text,
		Padding = Theme.Current.Spacing * 2,
		Color = Theme.Current.Muted,
	};

	/// <summary>Rebuilds the panel after a state change.</summary>
	private void MarkChanged() => _version.Value++;
}
