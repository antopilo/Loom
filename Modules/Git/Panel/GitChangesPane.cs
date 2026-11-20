using Goo;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Holds a repository's local changes for the Git views: the unstaged and staged files as folder trees with a commit box, and the review
/// notes and findings the Git panel's file view marks on lines.</summary>
/// <remarks>
/// Reads run in the background and post back to the UI thread, dropping results a newer read replaced. Every git command that changes
/// the repository goes through the owner's run callback, which shows progress and errors and calls <see cref="Refresh"/> afterwards.
/// </remarks>
internal sealed partial class GitChangesPane
{
	/// <summary>Stores the owning panel's dispatch and lifetime.</summary>
	private readonly PanelContext _context;

	/// <summary>Gives the current repository root, or empty when the panel shows none.</summary>
	private readonly Func<string> _root;

	/// <summary>Runs a labelled mutating git command through the owner, which reports progress and errors and refreshes afterwards.</summary>
	private readonly Action<string, Func<CancellationToken, Task>> _run;

	/// <summary>Gives the latest findings returned by the linked Omp review chat.</summary>
	private readonly Func<IReadOnlyList<GitReviewFinding>> _reviewFindings;

	/// <summary>Shows a clicked file's diff: its change, and whether it is the staged one.</summary>
	private readonly Action<GitFileChange, bool> _open;

	/// <summary>Tells the owner that new changes arrived.</summary>
	private readonly Action _loaded;

	/// <summary>Rebuilds the pane after loads and view changes.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Stores the root the latest load was started for, so a changed root starts a new one; null before the first.</summary>
	private string? _requestedRoot;

	/// <summary>Stores the root the shown changes belong to, or null while none are loaded.</summary>
	private string? _loadedRoot;

	/// <summary>Stores the latest loaded changes, kept on screen while a reload runs.</summary>
	private GitWorkingChanges? _changes;

	/// <summary>Stores the latest load failure, or null.</summary>
	private string? _error;

	/// <summary>Reports whether a load is running.</summary>
	private bool _isLoading;

	/// <summary>Cancels the running load when a newer one starts or the panel closes.</summary>
	private CancellationTokenSource? _load;

	/// <summary>Stores the owner's callbacks and prepares the commit description editor; the first build starts loading.</summary>
	/// <param name="context">The owning panel's dispatch and lifetime.</param>
	/// <param name="root">Gives the current repository root; it may change, and the pane reloads when it does.</param>
	/// <param name="run">Runs a labelled mutating git command, showing progress and errors, and refreshes the pane afterwards.</param>
	/// <param name="reviewFindings">Gives findings returned by the linked Omp review chat.</param>
	/// <param name="open">Shows a clicked file's diff, given its change and whether it is staged.</param>
	/// <param name="loaded">Runs after new changes arrive.</param>
	internal GitChangesPane(
		PanelContext context,
		Func<string> root,
		Action<string, Func<CancellationToken, Task>> run,
		Func<IReadOnlyList<GitReviewFinding>> reviewFindings,
		Action<GitFileChange, bool> open,
		Action loaded )
	{
		_context = context;
		_root = root;
		_run = run;
		_reviewFindings = reviewFindings;
		_open = open;
		_loaded = loaded;
		_description = new TextEditorController( new TextDocument( "" ) );
	}

	/// <summary>Gets the latest loaded changes, or null before the first load.</summary>
	internal GitWorkingChanges? Changes => _changes;

	/// <summary>Gets the findings of the linked review, which the file view marks on lines.</summary>
	internal IReadOnlyList<GitReviewFinding> ReviewFindings => _reviewFindings();

	/// <summary>Starts loading after this build when the repository root changed since the last load was requested.</summary>
	private void RequestLoad()
	{
		var root = _root();
		if ( string.Equals( root, _requestedRoot, StringComparison.Ordinal ) )
			return;

		// Loading changes state, so it starts after this build rather than inside it.
		_requestedRoot = root;
		_context.Post( Refresh );
	}

	/// <summary>Stops the running reads and message writing when the owner closes.</summary>
	internal void Close()
	{
		_load?.Cancel();
		_amendLoad?.Cancel();
		_writing?.Cancel();
	}

	/// <summary>Reloads the working changes in the background, keeping the shown ones until the new ones arrive; a newer reload replaces a running one.</summary>
	/// <remarks>Call on the UI thread. A changed repository root clears the selection, diff and amend state first.</remarks>
	internal void Refresh()
	{
		var root = _root();
		_requestedRoot = root;
		if ( !string.Equals( root, _loadedRoot, StringComparison.Ordinal ) )
			ResetRoot();

		_load?.Cancel();
		_load = null;
		if ( root.Length == 0 )
		{
			_isLoading = false;
			MarkChanged();
			return;
		}

		var load = CancellationTokenSource.CreateLinkedTokenSource( _context.Lifetime );
		_load = load;
		_isLoading = true;
		MarkChanged();
		_ = Task.Run( async () =>
		{
			try
			{
				var changes = await GitRepository.ReadChangesAsync( _context.Workspace, root, load.Token );
				_context.Post( () => ApplyChanges( load, root, changes, null ) );
			}
			catch ( OperationCanceledException )
			{
			}
			catch ( Exception error )
			{
				_context.Post( () => ApplyChanges( load, root, null, error.Message ) );
			}
		} );
	}

	/// <summary>Shows a finished load unless a newer one replaced it, then tells the owner.</summary>
	private void ApplyChanges( CancellationTokenSource load, string root, GitWorkingChanges? changes, string? error )
	{
		if ( _load != load )
			return;

		_isLoading = false;
		_error = error;
		if ( changes is not null )
		{
			_changes = changes;
			_loadedRoot = root;
			UpdateRowHandles();
		}
		MarkChanged();
		if ( changes is not null )
			_loaded();
	}

	/// <summary>Forgets everything that belonged to the previous repository: changes, menu, dialog, amend, review notes and side-view state.</summary>
	/// <remarks>The typed commit message stays, so switching repositories does not lose it.</remarks>
	private void ResetRoot()
	{
		_changes = null;
		_loadedRoot = null;
		_error = null;
		_menu = null;
		_discard = null;
		_rowHandles.Clear();
		_amendLoad?.Cancel();
		_amendLoad = null;
		_amendError = null;
		_amend = false;
		_prefill = null;
		_annotations.Clear();
		_editingAnnotation = null;
		_annotationText = "";
		ResetSide();
	}

	/// <summary>Runs a mutating git command on the shown repository through the owner, which shows progress and errors and refreshes afterwards.</summary>
	/// <param name="label">What the command does, such as "Staging 2 files".</param>
	/// <param name="operation">The command, given the repository root and a cancellation token.</param>
	private void RunGit( string label, Func<string, CancellationToken, Task> operation )
	{
		if ( _loadedRoot is not { } root )
			return;

		_run( label, cancellationToken => operation( root, cancellationToken ) );
	}

	/// <summary>Gets the shown unstaged or staged files, or none before the first load.</summary>
	private IReadOnlyList<GitFileChange> ListOf( bool staged ) =>
		_changes is not { } changes ? [] : staged ? changes.Staged : changes.Unstaged;

	/// <summary>Builds a muted line in place of a list or the diff.</summary>
	private static Text BuildMessage( string key, string text, Color? color = null ) => new()
	{
		Key = key,
		Content = text,
		Padding = Theme.Current.Spacing * 2,
		Color = color ?? Theme.Current.Muted,
	};

	/// <summary>Builds a compact text button with small type, for list headers and file rows.</summary>
	private static Button BuildSmallButton( string label, string accessibilityName, Action action, bool isEnabled = true )
	{
		var button = Theme.Current.BuildButton( label, action, isEnabled );
		button.AccessibilityName = accessibilityName;
		button.Height = Theme.Current.ControlHeight - Theme.Current.Spacing;
		button.Padding = new Edges( 0, Theme.Current.Spacing / 2 );
		button.FlexShrink = 0;
		button.Children =
		[
			new Text
			{
				Content = label,
				FontSize = ViewStyle.SmallSize,
				TextWrap = TextWrap.NoWrap,
			},
		];
		return button;
	}

	/// <summary>Rebuilds the pane after a state change.</summary>
	private void MarkChanged() => _version.Value++;
}
