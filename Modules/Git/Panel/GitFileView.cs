using Goo;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Names the file the Git panel's file view shows: a working-tree change, unstaged or staged, or a file a commit changed.</summary>
/// <param name="Path">The repository-relative path with forward slashes, the new path of a rename.</param>
/// <param name="Staged">Whether a working-tree change shows its staged side, HEAD to index, rather than index to working tree.</param>
/// <param name="Commit">The commit whose change shows, or null for the working tree.</param>
internal sealed record GitFileTarget( string Path, bool Staged, string? Commit = null );

/// <summary>Names the kinds of rows in the file view's diff.</summary>
internal enum GitFileRowKind
{
	/// <summary>A hunk's header with its range and actions.</summary>
	Hunk,

	/// <summary>A run of a hunk's lines, one under another.</summary>
	Lines,

	/// <summary>A run of a hunk's lines side by side, old on the left and new on the right.</summary>
	Pairs,
}

/// <summary>One row of the file view's diff.</summary>
/// <param name="Key">The stable row key.</param>
/// <param name="Kind">What the row shows.</param>
/// <param name="Hunk">The hunk's index.</param>
/// <param name="Start">The index of a run's first line, or first pair side by side.</param>
/// <param name="Count">The number of lines or pairs in a run.</param>
/// <param name="Revision">Changes whenever anything the row draws changes, so an equal row is reused.</param>
internal sealed record GitFileRow( string Key, GitFileRowKind Kind, int Hunk, int Start, int Count, int Revision ) : IMeasuredRow;

/// <summary>Shows one file's diff in the Git panel: its path and counts, the unstaged or staged side, hunks with their own Stage, Unstage and Discard,
/// hunk navigation, wrapping, and the lines one under another or side by side.</summary>
/// <remarks>
/// A working-tree file reloads whenever the working changes do, so staging a hunk shows the rest at once, and the view moves to the other side
/// when the shown one has nothing left. A commit's file is read-only. Commands go through the panel's run callback, which shows progress and
/// errors and reloads afterwards.
/// </remarks>
internal sealed partial class GitFileView
{
	/// <summary>Defines the font weight of the file name.</summary>
	private const int NameWeight = 600;

	/// <summary>Stores the owning panel's dispatch and lifetime.</summary>
	private readonly PanelContext _context;

	/// <summary>Gives the working changes, the review notes and the findings marked on lines.</summary>
	private readonly GitChangesPane _pane;

	/// <summary>Gives the current repository root, or null outside a repository.</summary>
	private readonly Func<string?> _root;

	/// <summary>Runs a labelled git command through the panel, which shows progress and errors and reloads afterwards.</summary>
	private readonly Action<string, Func<CancellationToken, Task>> _run;

	/// <summary>Returns the panel to its commit graph.</summary>
	private readonly Action _close;

	/// <summary>Rebuilds the panel after loads and view changes.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Targets the diff list, to scroll to a hunk.</summary>
	private readonly ElementHandle _list = new();

	/// <summary>Builds one diff row; kept as one delegate so the list reuses unchanged rows.</summary>
	private readonly Func<GitFileRow, Blob> _buildRow;

	/// <summary>Stores the shown file, or null while the panel shows its graph.</summary>
	private GitFileTarget? _target;

	/// <summary>Stores the working-tree change the shown patch was read for, or null for a commit's file.</summary>
	private GitFileChange? _change;

	/// <summary>Stores the loaded patch of <see cref="_target"/>, kept on screen while it reloads; null before it loads or when it has no content.</summary>
	private GitFilePatch? _patch;

	/// <summary>Stores each loaded hunk's line pairing, in hunk order.</summary>
	private GitHunkLayout[] _layouts = [];

	/// <summary>Reports whether a patch for the shown target has arrived since it was chosen.</summary>
	private bool _isLoaded;

	/// <summary>Reports whether the working tree no longer changes the shown file on either side.</summary>
	private bool _isGone;

	/// <summary>Stores why the patch could not load, or null.</summary>
	private string? _error;

	/// <summary>Cancels the running patch load when a newer one starts or the panel closes.</summary>
	private CancellationTokenSource? _load;

	/// <summary>Stores the index of the hunk the navigation is on.</summary>
	private int _hunk;

	/// <summary>Stores what waits for a second click to discard: <c>file</c>, or <c>hunk:</c> and its index; null when nothing does.</summary>
	private string? _confirm;

	/// <summary>Reports whether long lines wrap rather than run past the edge.</summary>
	private bool _wrap;

	/// <summary>Reports whether the old and new lines show side by side.</summary>
	private bool _split;

	/// <summary>Counts changes to what the rows draw.</summary>
	private int _revision;

	/// <summary>Stores the shared collaborators; nothing shows until <see cref="Show"/>.</summary>
	/// <param name="context">The owning panel's dispatch and lifetime.</param>
	/// <param name="pane">The panel's working changes, with their review notes and findings.</param>
	/// <param name="root">Gives the current repository root, or null outside a repository.</param>
	/// <param name="run">Runs a labelled git command, showing progress and errors, then reloads.</param>
	/// <param name="close">Returns the panel to its commit graph.</param>
	internal GitFileView( PanelContext context, GitChangesPane pane, Func<string?> root, Action<string, Func<CancellationToken, Task>> run, Action close )
	{
		_context = context;
		_pane = pane;
		_root = root;
		_run = run;
		_close = close;
		_buildRow = BuildRow;
	}

	/// <summary>Gets the shown file, or null while the panel shows its graph.</summary>
	internal GitFileTarget? Target => _target;

	/// <summary>Shows a file, starting at its first hunk; the same file keeps its place.</summary>
	internal void Show( GitFileTarget target )
	{
		if ( target == _target )
			return;

		_target = target;
		_change = null;
		_patch = null;
		_layouts = [];
		_isLoaded = false;
		_isGone = false;
		_error = null;
		_hunk = 0;
		_confirm = null;
		Load();
	}

	/// <summary>Stops showing a file.</summary>
	internal void Hide()
	{
		_load?.Cancel();
		_load = null;
		_target = null;
		_change = null;
		_patch = null;
		_layouts = [];
		_confirm = null;
		MarkChanged();
	}

	/// <summary>Reads a working-tree file's patch again after the working changes reloaded; a commit's file never changes.</summary>
	internal void Refresh()
	{
		if ( _target is { Commit: null } )
			Load();
	}

	/// <summary>Stops the running load when the panel closes.</summary>
	internal void Close() => _load?.Cancel();

	/// <summary>Finds a working-tree change of the shown path on one side, or null.</summary>
	private GitFileChange? FindChange( string path, bool staged ) =>
		_pane.Changes is { } changes ? (staged ? changes.Staged : changes.Unstaged).FirstOrDefault( file => file.Path == path ) : null;

	/// <summary>Reads the shown file's patch in the background, keeping the shown one until it arrives; a working-tree file moves to the side that still changes it.</summary>
	private void Load()
	{
		_load?.Cancel();
		_load = null;
		if ( _target is not { } target || _root() is not { } root )
			return;

		GitFileChange? change = null;
		if ( target.Commit is null )
		{
			if ( _pane.Changes is null )
				return;

			change = FindChange( target.Path, target.Staged );
			if ( change is null && FindChange( target.Path, !target.Staged ) is { } other )
			{
				target = _target = target with { Staged = !target.Staged };
				change = other;
				_hunk = 0;
			}
			if ( change is null )
			{
				_isGone = true;
				_isLoaded = true;
				_patch = null;
				_layouts = [];
				_confirm = null;
				MarkChanged();
				return;
			}
		}

		_isGone = false;
		var load = CancellationTokenSource.CreateLinkedTokenSource( _context.Lifetime );
		_load = load;
		_ = Task.Run( async () =>
		{
			try
			{
				var text = target.Commit is { } commit
					? await GitRepository.ReadCommitPatchAsync( root, commit, target.Path, load.Token ).ConfigureAwait( false )
					: await GitRepository.ReadWorkingPatchAsync( root, change!, target.Staged, load.Token ).ConfigureAwait( false );
				var patch = GitPatch.Parse( text ).FirstOrDefault();
				GitHunkLayout[] layouts = patch is null ? [] : [.. patch.Hunks.Select( GitHunkLayout.Build )];
				_context.Post( () => Apply( load, target, change, patch, layouts, null ) );
			}
			catch ( OperationCanceledException ) when ( load.IsCancellationRequested )
			{
			}
			catch ( Exception error ) when ( error is not OutOfMemoryException )
			{
				_context.Post( () => Apply( load, target, change, null, [], error.Message ) );
			}
		} );
	}

	/// <summary>Shows a loaded patch unless a newer load or another file replaced it.</summary>
	private void Apply( CancellationTokenSource load, GitFileTarget target, GitFileChange? change, GitFilePatch? patch, GitHunkLayout[] layouts, string? error )
	{
		if ( _load != load || _target != target )
			return;

		_load = null;
		load.Dispose();
		_isLoaded = true;
		_error = error;
		if ( error is null )
		{
			_change = change;
			_patch = patch;
			_layouts = layouts;
			_hunk = Math.Clamp( _hunk, 0, Math.Max( 0, layouts.Length - 1 ) );
			if ( _confirm is { } confirm && confirm.StartsWith( "hunk:", StringComparison.Ordinal ) )
				_confirm = null;
		}
		MarkChanged();
	}

	/// <summary>Builds the header, the options, the sides compared, and the diff or a message in its place.</summary>
	internal Blob Build()
	{
		_ = _version.Value;
		var theme = Theme.Current;
		var children = new List<Blob> { BuildHeader(), BuildOptions(), BuildSides() };
		if ( _target is { Commit: null } && _pane.BuildAnnotationEditor( ReviewPath ) is { } editor )
			children.Add( editor );
		if ( _change is { Status: 'U' } )
			children.Add( new Text
			{
				Key = "git-file-conflict",
				Content = "This file has conflicts. Resolve them in the file, then stage it to mark it resolved.",
				FlexShrink = 0,
				Padding = new Edges( theme.Spacing / 2, theme.Spacing * 2 ),
				Color = theme.AnsiYellow,
			} );
		children.Add( BuildBody() );
		return new Container
		{
			Key = "git-file",
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = $"Diff of {_target?.Path}",
			Children = children,
		};
	}

	/// <summary>Gets the shown path in the form review notes and findings use.</summary>
	private string ReviewPath => (_target?.Path ?? "").Replace( '/', Path.DirectorySeparatorChar );

	/// <summary>Builds the path with the name emphasized, the line counts, Edit, the side's file action and Close.</summary>
	private Row BuildHeader()
	{
		var theme = Theme.Current;
		var path = _target?.Path ?? "";
		var slash = path.LastIndexOf( '/' );
		var children = new List<Blob>
		{
			GitIcons.Build( GitIcons.File, 15, theme.Accent ),
			new Text { Content = slash >= 0 ? path[..(slash + 1)] : "", Color = theme.Muted, FlexShrink = 1, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
			new Text { Content = path[(slash + 1)..], FontWeight = NameWeight, FlexShrink = 0, TextWrap = TextWrap.NoWrap },
		};
		if ( _patch is { } patch && (patch.Added > 0 || patch.Removed > 0) )
			children.Add( BuildCounts( patch.Added, patch.Removed ) );
		children.Add( new Spacer() );
		children.Add( BuildAction( "Edit", $"Edit {path}", Edit, icon: GitIcons.Pencil, isEnabled: File.Exists( FullPath ) ) );
		if ( _target is { Commit: null, Staged: false } && _change is { } unstaged )
		{
			children.Add( _confirm == "file"
				? BuildAction( "Discard file?", $"Confirm discarding every change to {path}", () => DiscardFile( unstaged ), theme.Error, strong: true )
				: BuildAction( "Discard", $"Discard every change to {path}", () => Confirm( "file" ), theme.Error ) );
			children.Add( BuildAction( "Stage file", $"Stage {path}", () => RunFile( "Staging", ( root, token ) => GitRepository.StageAsync( root, [unstaged.Path], token ) ), icon: GitIcons.Plus, strong: true ) );
		}
		else if ( _target is { Commit: null, Staged: true } && _change is { } staged )
			children.Add( BuildAction( "Unstage file", $"Unstage {path}", () => RunFile( "Unstaging", ( root, token ) => GitRepository.UnstageAsync( root, [staged.Path], token ) ), icon: GitIcons.Minus, strong: true ) );
		children.Add( BuildIcon( GitIcons.Close, "Close the file and show the commit graph", _close ) );
		return new Row
		{
			Key = "git-file-header",
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Padding = new Edges( theme.Spacing / 2, theme.Spacing ),
			Children = children,
		};
	}

	/// <summary>Builds the side switch or the commit, Wrap, the layout switch and the hunk navigation.</summary>
	private Row BuildOptions()
	{
		var theme = Theme.Current;
		var children = new List<Blob>();
		if ( _target is { Commit: { } commit } )
			children.Add( new Text { Content = $"Commit {Shorten( commit )}", FontFamily = ViewStyle.CodeFontFamily, FontSize = ViewStyle.SmallSize, Color = theme.Muted, FlexShrink = 0 } );
		else if ( _target is { } target )
		{
			var hasUnstaged = FindChange( target.Path, false ) is not null;
			var hasStaged = FindChange( target.Path, true ) is not null;
			children.Add( BuildSegments( "Side",
			[
				("Unstaged", !target.Staged, hasUnstaged, () => ShowSide( false )),
				("Staged", target.Staged, hasStaged, () => ShowSide( true )),
			] ) );
		}
		children.Add( BuildSegments( "Wrap", [("Wrap", _wrap, true, () => UpdateOption( () => _wrap = !_wrap ))] ) );
		children.Add( new Spacer() );
		children.Add( BuildSegments( "Layout",
		[
			("Unified", !_split, true, () => UpdateOption( () => _split = false )),
			("Split", _split, true, () => UpdateOption( () => _split = true )),
		] ) );

		var count = _layouts.Length;
		children.Add( BuildIcon( GitIcons.ChevronUp, "Previous hunk", () => MoveHunk( -1 ), count > 1 ) );
		children.Add( new Text
		{
			Content = count == 0 ? "0/0" : $"{_hunk + 1}/{count}",
			FontSize = ViewStyle.SmallSize,
			Color = theme.Muted,
			MinWidth = 36,
			TextAlign = TextAlign.Center,
			FlexShrink = 0,
		} );
		children.Add( BuildIcon( GitIcons.ChevronDown, "Next hunk", () => MoveHunk( 1 ), count > 1 ) );
		return new Row
		{
			Key = "git-file-options",
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Padding = new Edges( 0, theme.Spacing, theme.Spacing / 2, theme.Spacing ),
			Children = children,
		};
	}

	/// <summary>Builds the line naming the two sides compared, and how many hunks and lines the diff has.</summary>
	private Row BuildSides()
	{
		var theme = Theme.Current;
		var (from, to) = _target switch
		{
			{ Commit: { } commit } => ($"{Shorten( commit )}^", Shorten( commit )),
			{ Staged: true } => ("HEAD", "Index"),
			_ => ("Index", "Working tree"),
		};
		var hunks = _layouts.Length;
		var lines = _patch?.Hunks.Sum( hunk => hunk.Lines.Count ) ?? 0;
		return new Row
		{
			Key = "git-file-sides",
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Padding = new Edges( theme.Spacing / 4, theme.Spacing ),
			BackgroundColor = theme.Canvas,
			BorderWidth = new Edges( theme.BorderWidth, 0, theme.BorderWidth, 0 ),
			BorderColor = theme.Border,
			Children =
			[
				new Text { Content = from, FontSize = ViewStyle.SmallSize, Color = theme.Muted, FlexShrink = 0 },
				new Text { Content = "›", FontSize = ViewStyle.SmallSize, Color = theme.Muted, FlexShrink = 0 },
				new Text { Content = to, FontSize = ViewStyle.SmallSize, FlexShrink = 0 },
				new Spacer(),
				new Text
				{
					Content = $"{hunks:N0} {(hunks == 1 ? "change" : "changes")} · {lines:N0} {(lines == 1 ? "row" : "rows")}",
					FontSize = ViewStyle.SmallSize,
					Color = theme.Muted,
					FlexShrink = 0,
				},
			],
		};
	}

	/// <summary>Builds the diff, or a message while it loads, when it fails, or when it has no lines to show.</summary>
	private Blob BuildBody()
	{
		var message = _error is not null ? _error
			: !_isLoaded ? "Reading diff…"
			: _isGone ? "The working tree no longer changes this file."
			: _patch is null ? "No content changes"
			: _patch.Note is { } note ? note
			: _patch.Hunks.Count == 0 ? "No content changes"
			: null;
		if ( message is not null )
			return new Text
			{
				Key = "git-file-message",
				Content = message,
				Padding = Theme.Current.Spacing * 2,
				Color = _error is null ? Theme.Current.Muted : Theme.Current.Error,
			};

		return new Virtual
		{
			// One key per file and side starts a new file at the top and keeps the place across reloads of the same one.
			Key = $"git-file-diff:{_target}",
			Handle = _list,
			Source = new MeasuredRowsSource<GitFileRow>( ComputeRows(), _buildRow, ComputeEstimate, FollowEnd: false ),
			AccessibilityName = $"Lines of {_target?.Path}",
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			Padding = new Edges( 0, 0, Theme.Current.Spacing * 2, 0 ),
			OverflowX = Overflow.Hidden,
			ScrollbarY = ViewStyle.BuildScrollbar(),
		};
	}

	/// <summary>Gets the shown file's full path.</summary>
	private string FullPath => _root() is { } root && _target is { } target ? Path.GetFullPath( Path.Combine( root, target.Path ) ) : "";

	/// <summary>Opens the shown file in the editor.</summary>
	private void Edit()
	{
		if ( FullPath is { Length: > 0 } path )
			_context.App.OpenLink( new Uri( $"lokn://editor/open?path={Uri.EscapeDataString( path )}" ), _context.Id );
	}

	/// <summary>Shows the shown file's unstaged or staged side.</summary>
	private void ShowSide( bool staged )
	{
		if ( _target is { Commit: null } target && target.Staged != staged )
			Show( target with { Staged = staged } );
	}

	/// <summary>Changes a display option and redraws every row.</summary>
	private void UpdateOption( Action change )
	{
		change();
		MarkChanged();
	}

	/// <summary>Moves the navigation to the previous or next hunk, wrapping around, and scrolls it to the top.</summary>
	private void MoveHunk( int step )
	{
		if ( _layouts.Length == 0 )
			return;

		_hunk = ((_hunk + step) % _layouts.Length + _layouts.Length) % _layouts.Length;
		_list.ScrollToItem( "h:" + _hunk, ScrollAlignment.Start, true );
		MarkChanged();
	}

	/// <summary>Asks for a second click before a discard.</summary>
	private void Confirm( string what )
	{
		_confirm = what;
		MarkChanged();
	}

	/// <summary>Discards every change of the shown unstaged file.</summary>
	private void DiscardFile( GitFileChange change ) => RunFile( "Discarding", ( root, token ) => GitRepository.DiscardAsync( root, [change], token ) );

	/// <summary>Runs a command on the shown file, labelled with its name.</summary>
	private void RunFile( string verb, Func<string, CancellationToken, Task> operation )
	{
		if ( _root() is not { } root || _target is not { } target )
			return;

		_confirm = null;
		_run( $"{verb} {Path.GetFileName( target.Path )}…", token => operation( root, token ) );
		MarkChanged();
	}

	/// <summary>Gets a commit's short identifier.</summary>
	private static string Shorten( string commit ) => commit.Length > 7 ? commit[..7] : commit;

	/// <summary>Builds the green added and red removed line counts.</summary>
	private static Row BuildCounts( int added, int removed )
	{
		var theme = Theme.Current;
		var children = new List<Blob>();
		if ( added > 0 )
			children.Add( new Text { Content = $"+{added:N0}", Color = theme.Success, FontSize = ViewStyle.SmallSize, FlexShrink = 0 } );
		if ( removed > 0 )
			children.Add( new Text { Content = $"−{removed:N0}", Color = theme.Error, FontSize = ViewStyle.SmallSize, FlexShrink = 0 } );
		return new Row
		{
			FlexShrink = 0,
			Gap = theme.Spacing / 2,
			Padding = new Edges( 0, theme.Spacing / 2 ),
			BorderRadius = theme.Radius,
			BackgroundColor = theme.Raised,
			Children = children,
		};
	}

	/// <summary>Builds a compact outlined button with an optional icon, in a color or the text color; a strong one is filled.</summary>
	private Button BuildAction( string label, string name, Action action, Color? color = null, VectorPath? icon = null, bool strong = false, bool isEnabled = true )
	{
		var theme = Theme.Current;
		var ink = color ?? theme.Text;
		var children = new List<Blob>();
		if ( icon is { } shape )
			children.Add( GitIcons.Build( shape, 13, ink ) );
		children.Add( new Text { Content = label, FontSize = ViewStyle.SmallSize, Color = ink, TextWrap = TextWrap.NoWrap } );
		var button = theme.BuildButton( label, () => _context.Run( action ), isEnabled );
		button.AccessibilityName = name;
		button.FlexShrink = 0;
		button.FlexDirection = FlexDirection.Row;
		button.AlignItems = AlignItems.Center;
		button.Gap = theme.Spacing / 2;
		button.Height = theme.ControlHeight - theme.Spacing / 2;
		button.Padding = new Edges( 0, theme.Spacing );
		button.BorderWidth = theme.BorderWidth;
		button.BorderColor = color is { } tint ? tint.WithAlpha( 0.6 ) : theme.Border;
		button.BackgroundColor = strong ? (color ?? theme.Accent).WithAlpha( 0.16 ) : Color.Transparent;
		button.TransitionMs = 120;
		button.TransitionProperties = [TransitionProperty.BackgroundColor, TransitionProperty.BorderColor];
		button.Children = children;
		return button;
	}

	/// <summary>Builds a square icon button with its name as its tooltip.</summary>
	private Blob BuildIcon( VectorPath icon, string name, Action action, bool isEnabled = true ) =>
		Cell.Mount<GitToolInput, GitToolButton>( "file-tool:" + name, new( icon, name, isEnabled, () => _context.Run( action ) ) );

	/// <summary>Builds joined toggle buttons, the chosen one raised; a disabled one is dimmed.</summary>
	private Row BuildSegments( string name, IReadOnlyList<(string Label, bool IsOn, bool IsEnabled, Action Select)> items )
	{
		var theme = Theme.Current;
		var children = new List<Blob>();
		foreach ( var (label, isOn, isEnabled, select) in items )
		{
			var button = theme.BuildButton( label, () => _context.Run( select ), isEnabled );
			button.AccessibilityName = isOn ? $"{label}, on" : label;
			button.FlexShrink = 0;
			button.Height = theme.ControlHeight - theme.Spacing;
			button.Padding = new Edges( 0, theme.Spacing );
			button.BorderRadius = theme.Radius;
			button.BackgroundColor = isOn ? theme.Hover : Color.Transparent;
			button.Children = [new Text { Content = label, FontSize = ViewStyle.SmallSize, FontWeight = isOn ? NameWeight : 400, Color = !isEnabled ? theme.Muted.WithAlpha( 0.6 ) : isOn ? theme.Text : theme.Muted, TextWrap = TextWrap.NoWrap }];
			children.Add( button );
		}

		return new Row
		{
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Gap = 2,
			Padding = 2,
			BorderRadius = theme.Radius,
			BorderWidth = theme.BorderWidth,
			BorderColor = theme.Border,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = name,
			Children = children,
		};
	}

	/// <summary>Rebuilds the view and every row after a change.</summary>
	private void MarkChanged()
	{
		_revision++;
		_version.Value++;
	}
}
