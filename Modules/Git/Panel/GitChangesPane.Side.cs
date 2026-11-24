using Goo;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Builds the Files side view's Changes mode: the working tree's unstaged and staged files as folder trees or lists of selectable rows, and a narrow commit box.</summary>
/// <remarks>
/// Clicking a file selects it and shows its diff in the Git panel; Ctrl+click adds or removes a file and Shift+click selects a range, within one
/// section. A right-click opens the menu for the selection, and Enter, Delete and Ctrl+C act on it. The commit box writes its message with a
/// <see cref="GitMessageWriter"/> when a module contributes one, such as Conversations with the chat's model.
/// </remarks>
internal sealed partial class GitChangesPane
{
	/// <summary>Defines a section heading's height in logical pixels.</summary>
	private const int SectionHeight = 30;

	/// <summary>Defines a section summary's height in logical pixels.</summary>
	private const int SummaryHeight = 22;

	/// <summary>Defines the indent per tree level in logical pixels.</summary>
	private const int TreeIndent = 14;

	/// <summary>Defines the most characters of the changes handed to a message writer.</summary>
	private const int MessagePatchLimit = 60_000;

	/// <summary>Defines the description editor's height in the side view, about three lines.</summary>
	private const int SideDescriptionHeight = 58;

	/// <summary>Defines the accent tint's opacity behind selected file rows.</summary>
	private const double SelectionOpacity = 0.2;

	/// <summary>Defines the width of a folder row's fold chevron in logical pixels.</summary>
	private const int ChevronWidth = 16;

	/// <summary>Defines the folder glyph.</summary>
	internal const string FolderGlyph = "\uE8B7";

	/// <summary>Defines the list glyph of the flat-list toggle.</summary>
	private const string ListGlyph = "\uE8FD";

	/// <summary>Defines the lightning glyph drawn on the button that writes the message.</summary>
	private const string WriteGlyph = "\uE945";

	/// <summary>Defines the pencil glyph before the Working tree title, the Changes mode's icon.</summary>
	private const string ChangesGlyph = "\uE70F";

	/// <summary>Defines the Return key glyph of the commit shortcut.</summary>
	private const string ReturnGlyph = "\uE751";

	/// <summary>Defines the size of the icons drawn beside text, in logical pixels.</summary>
	internal const int InlineIconSize = 12;

	/// <summary>Builds an icon from the system icon font, sized to sit beside text.</summary>
	internal static Text BuildInlineIcon( string glyph, Color color ) => new()
	{
		Content = glyph,
		FontFamily = ViewStyle.IconFontFamily,
		FontSize = InlineIconSize,
		Color = color,
		FlexShrink = 0,
	};

	/// <summary>Stores the folded folders of the side trees, keyed by list and tree path.</summary>
	private readonly HashSet<string> _foldedSide = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Targets the side view's commit options button, which its menu opens beside.</summary>
	private readonly ElementHandle _commitOptions = new();

	/// <summary>Reports whether the side view lists files flat rather than as folder trees.</summary>
	private bool _asList;

	/// <summary>Reports whether the commit options menu is open.</summary>
	private bool _isCommitMenuOpen;

	/// <summary>Cancels writing the commit message, or null while none is written.</summary>
	private CancellationTokenSource? _writing;

	/// <summary>Stores why the message could not be written, or null.</summary>
	private string? _writeError;

	/// <summary>Builds one side row; kept as one delegate so the list reuses unchanged rows.</summary>
	private Func<SideRow, Blob>? _buildSideRow;

	/// <summary>Stores the row keys of the selected files, all in one section.</summary>
	private readonly HashSet<string> _selection = new( StringComparer.Ordinal );

	/// <summary>Stores the row key a Shift+click range starts from, or null.</summary>
	private string? _selectionAnchor;

	/// <summary>Builds the Working tree header: its title, the tree and list toggles, then the owner's tools, such as stashing and the actions other modules add.</summary>
	internal Row BuildSideHeader( IEnumerable<Blob> tools )
	{
		var theme = Theme.Current;
		var tree = BuildIconButton( FolderGlyph, "Show changes as folders", !_asList, () => UpdateAsList( false ) );
		var list = BuildIconButton( ListGlyph, "Show changes as a list", _asList, () => UpdateAsList( true ) );
		return new Row
		{
			Key = "git-side-header",
			FlexShrink = 0,
			FlexWrap = FlexWrap.Wrap,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Padding = new Edges( theme.Spacing / 2, theme.Spacing / 2, theme.Spacing / 2, theme.Spacing ),
			Children =
			[
				BuildInlineIcon( ChangesGlyph, theme.Muted ),
				new Text { Content = "Working tree", FontWeight = HeadingWeight, FlexShrink = 1, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
				new Spacer(),
				list,
				tree,
				.. tools,
			],
		};
	}

	/// <summary>Builds the side view's changes: both sections in one virtual list, then the commit box, with any open menu or dialog.</summary>
	internal Blob BuildSide()
	{
		_ = _version.Value;
		RequestLoad();
		_buildSideRow ??= BuildSideRow;
		var theme = Theme.Current;
		var children = new List<Blob>();
		if ( _changes is not { } changes )
		{
			var text = _error ?? (_requestedRoot is { Length: > 0 } || _isLoading ? "Reading changes…" : "No repository");
			var message = BuildMessage( "git-side-status", text, _error is null ? null : theme.Error );
			message.FlexGrow = 1;
			children.Add( message );
		}
		else
		{
			if ( _error is { } error )
				children.Add( new Text { Key = "git-side-error", Content = error, FlexShrink = 0, Padding = new Edges( theme.Spacing / 2, theme.Spacing ), FontSize = ViewStyle.SmallSize, Color = theme.Error } );
			if ( changes.Unstaged.Count == 0 && changes.Staged.Count == 0 )
			{
				var empty = BuildMessage( "git-side-empty", "No local changes" );
				empty.FlexGrow = 1;
				children.Add( empty );
			}
			else
				children.Add( new Virtual
				{
					Key = "git-side-files",
					Source = new MeasuredRowsSource<SideRow>( ComputeSideRows( changes ), _buildSideRow, ComputeSideEstimate, FollowEnd: false ),
					AccessibilityRole = AccessibilityRole.List,
					AccessibilityName = "Changed files",
					FlexGrow = 1,
					MinHeight = 0,
					Padding = new Edges( 0, theme.Spacing / 2 ),
					OverflowX = Overflow.Hidden,
					ScrollbarY = ViewStyle.BuildScrollbar(),
				} );
			children.Add( BuildSideCommitBox() );
		}
		if ( BuildMenu() is { } menu )
			children.Add( menu );
		if ( BuildCommitMenu() is { } commitMenu )
			children.Add( commitMenu );
		if ( _discard is { } files )
			children.Add( BuildDiscardDialog( files ) );

		return new Container
		{
			Key = "git-side-changes",
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			FontFamily = ViewStyle.FontFamily,
			FontSize = theme.FontSize,
			Color = theme.Text,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Working tree",
			Children = children,
		};
	}

	/// <summary>Lists both sections' rows: each heading and summary, then its files as folder trees or a flat list.</summary>
	private List<SideRow> ComputeSideRows( GitWorkingChanges changes )
	{
		var rows = new List<SideRow>();
		AddSideSection( rows, changes.Unstaged, staged: false );
		AddSideSection( rows, changes.Staged, staged: true );
		return rows;
	}

	/// <summary>Adds one section: its heading, a summary of its changes, and its files.</summary>
	private void AddSideSection( List<SideRow> rows, IReadOnlyList<GitFileChange> files, bool staged )
	{
		var prefix = staged ? "s:" : "u:";
		rows.Add( new( prefix + "section", SideRowKind.Section, staged, staged ? "Staged" : "Unstaged", 0, null, files.Count, 0, 0, true, false ) );
		if ( files.Count == 0 )
		{
			rows.Add( new( prefix + "empty", SideRowKind.Empty, staged, staged ? "Nothing staged" : "No unstaged changes", 0, null, 0, 0, 0, true, false ) );
			return;
		}

		rows.Add( new( prefix + "summary", SideRowKind.Summary, staged, DescribeChanges( files ), 0, null, files.Count,
			files.Sum( file => file.Added ), files.Sum( file => file.Removed ), true, false ) );
		if ( _asList )
		{
			foreach ( var file in files )
				rows.Add( BuildFileSideRow( file, staged, SplitPath( file.Path ).Name, 0 ) );
			return;
		}

		foreach ( var entry in PathTree.Build( files, file => file.Path, path => _foldedSide.Contains( prefix + path ) ) )
			rows.Add( entry.Item is { } file
				? BuildFileSideRow( file, staged, entry.Label, entry.Depth )
				: new( prefix + "d:" + entry.Path, SideRowKind.Folder, staged, entry.Label, entry.Depth, null, entry.Count, 0, 0, entry.IsOpen, _selection.Contains( prefix + "d:" + entry.Path ) ) );
	}

	/// <summary>Builds a file's row value.</summary>
	private SideRow BuildFileSideRow( GitFileChange file, bool staged, string label, int depth )
	{
		var key = ComputeRowKey( file.Path, staged );
		return new( key, SideRowKind.File, staged, label, depth, file, 1, file.Added, file.Removed, true, _selection.Contains( key ) );
	}

	/// <summary>Counts a section's files by kind of change, such as "3 modified, 1 added".</summary>
	private static string DescribeChanges( IReadOnlyList<GitFileChange> files )
	{
		var parts = files
			.GroupBy( file => file.Status switch
			{
				'A' or 'C' => "added",
				'?' => "untracked",
				'D' => "deleted",
				'R' => "renamed",
				'U' => "conflicted",
				_ => "modified",
			} )
			.OrderByDescending( group => group.Count() )
			.Select( group => $"{group.Count():N0} {group.Key}" );
		return string.Join( ", ", parts );
	}

	/// <summary>Estimates a side row's height until layout measures it.</summary>
	private static double ComputeSideEstimate( SideRow row ) => row.Kind switch
	{
		SideRowKind.Section => SectionHeight,
		SideRowKind.Summary or SideRowKind.Empty => SummaryHeight,
		_ => RowHeight,
	};

	/// <summary>Builds one side row by its kind.</summary>
	private Blob BuildSideRow( SideRow row ) => row.Kind switch
	{
		SideRowKind.Section => BuildSideSection( row ),
		SideRowKind.Summary => BuildSideSummary( row ),
		SideRowKind.Folder => BuildSideFolder( row ),
		SideRowKind.File => BuildSideFile( row ),
		_ => new Text { Content = row.Label, MinHeight = SummaryHeight, Padding = new Edges( 0, Theme.Current.Spacing ), FontSize = ViewStyle.SmallSize, Color = Theme.Current.Muted },
	};

	/// <summary>Builds a section heading: its title and count, and the button that stages or unstages every file.</summary>
	private Row BuildSideSection( SideRow row )
	{
		var theme = Theme.Current;
		var staged = row.IsStaged;
		var hasFiles = row.Count > 0;
		void ToggleAll() => _context.Run( () => UpdateStageAll( staged ) );
		var all = staged
			? BuildSmallButton( "Unstage all", "Unstage all files", ToggleAll, hasFiles )
			: BuildSmallButton( "Stage all", "Stage all files", ToggleAll, hasFiles );
		return new Row
		{
			MinHeight = SectionHeight,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Padding = new Edges( 0, theme.Spacing / 2 ),
			BorderWidth = new Edges( staged ? theme.BorderWidth : 0, 0, 0, 0 ),
			BorderColor = theme.Border,
			Children =
			[
				new Text { Content = row.Label, FontWeight = HeadingWeight, FlexShrink = 0 },
				new Text { Content = row.Count.ToString( "N0" ), Color = theme.Muted, FontSize = ViewStyle.SmallSize, FlexShrink = 0 },
				new Spacer(),
				all,
			],
		};
	}

	/// <summary>Builds a section's summary: how many files changed by kind, and the lines added and removed.</summary>
	private static Row BuildSideSummary( SideRow row )
	{
		var theme = Theme.Current;
		return new Row
		{
			MinHeight = SummaryHeight,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Padding = new Edges( 0, theme.Spacing, 0, theme.Spacing ),
			Children =
			[
				new Text { Content = row.Label, Color = theme.Muted, FontSize = ViewStyle.SmallSize, FlexShrink = 1, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
				new Spacer(),
				.. BuildCounts( row.Added, row.Removed ),
			],
		};
	}

	/// <summary>Builds a folder row: its chevron, which folds or unfolds it, then its glyph, joined name and changed-file count. Clicking selects it like a file,
	/// so the menu, Enter and Delete act on every file under it.</summary>
	private Row BuildSideFolder( SideRow row )
	{
		var theme = Theme.Current;
		var key = row.Key;
		var staged = row.IsStaged;
		var folded = (staged ? "s:" : "u:") + key[(key.IndexOf( "d:", StringComparison.Ordinal ) + 2)..];
		void Run( Action action ) => _context.Run( action );
		var chevron = new Button
		{
			Width = ChevronWidth,
			Height = RowHeight,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			Cursor = Cursor.Pointer,
			AccessibilityName = $"{(row.IsOpen ? "Fold" : "Unfold")} {row.Label}",
			OnClick = () => Run( () => UpdateSideFolded( folded ) ),
			Children = [theme.BuildChevron( row.IsOpen )],
		};
		var item = new Button
		{
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = RowHeight,
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Cursor = Cursor.Pointer,
			Focusable = true,
			BorderRadius = theme.Radius,
			FocusStyle = new Style { BorderColor = theme.Accent, BorderWidth = theme.BorderWidth },
			AccessibilityRole = AccessibilityRole.ListItem,
			AccessibilityName = $"Folder {row.Label}, {row.Count:N0} changed{(row.IsSelected ? ", selected" : "")}",
			KeyBindings = [.. BuildSelectionKeys( key, staged ), new( Key.Right, KeyModifiers.None, () => Run( () => { if ( !row.IsOpen ) UpdateSideFolded( folded ); } ) ),
				new( Key.Left, KeyModifiers.None, () => Run( () => { if ( row.IsOpen ) UpdateSideFolded( folded ); } ) )],
			Children =
			[
				new Text { Content = FolderGlyph, FontFamily = ViewStyle.IconFontFamily, FontSize = 13, Color = theme.Muted, FlexShrink = 0 },
				new Text { Content = row.Label, FlexShrink = 1, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
				new Spacer(),
				new Text { Content = row.Count.ToString( "N0" ), Color = theme.Muted, FontSize = ViewStyle.SmallSize, FlexShrink = 0 },
			],
		};
		return BuildSelectableRow( row, null, [chevron, item] );
	}

	/// <summary>Builds a file row: its status letter, name and line counts. Clicking selects it and shows its diff; Ctrl and Shift extend the selection; right-clicking opens the selection's menu.</summary>
	private Row BuildSideFile( SideRow row )
	{
		var theme = Theme.Current;
		var file = row.File!;
		var staged = row.IsStaged;
		var key = row.Key;
		var children = new List<Blob>
		{
			BuildBadge( file.Status ),
			new Text { Content = row.Label, FlexShrink = 1, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
		};
		if ( _asList && SplitPath( file.Path ).Folder is { Length: > 0 } folder )
			children.Add( new Text { Content = folder, Color = theme.Muted, FontSize = ViewStyle.SmallSize, FlexShrink = 4, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis } );
		children.Add( new Spacer() );
		if ( file.IsBinary )
			children.Add( new Text { Content = "binary", Color = theme.Muted, FontSize = ViewStyle.SmallSize, FlexShrink = 0 } );
		else
			children.AddRange( BuildCounts( file.Added, file.Removed ) );

		var item = new Button
		{
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = RowHeight,
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Cursor = Cursor.Pointer,
			Focusable = true,
			BorderRadius = theme.Radius,
			FocusStyle = new Style { BorderColor = theme.Accent, BorderWidth = theme.BorderWidth },
			AccessibilityRole = AccessibilityRole.ListItem,
			AccessibilityName = $"{file.Path}, {DescribeStatus( file.Status )}, {(staged ? "staged" : "unstaged")}{(row.IsSelected ? ", selected" : "")}",
			KeyBindings = [.. BuildSelectionKeys( key, staged )],
			Children = children,
		};
		return BuildSelectableRow( row, file, [item] );
	}

	/// <summary>Builds the keys a file or folder row handles: Enter stages, Space selects, Delete discards, Ctrl+A selects all, Ctrl+C copies paths, and the menu keys.</summary>
	private List<KeyBinding> BuildSelectionKeys( string key, bool staged )
	{
		void Run( Action action ) => _context.Run( action );
		return
		[
			new( Key.Enter, KeyModifiers.None, () => Run( () => UpdateStage( ComputeKeyTargets( key, staged ), staged ) ) ),
			new( Key.Space, KeyModifiers.None, () => Run( () => SelectRow( key, null, staged, KeyModifiers.None ) ) ),
			new( Key.Delete, KeyModifiers.None, () => Run( () => BeginDiscard( ComputeKeyTargets( key, staged ) ) ) ),
			new( Key.A, KeyModifiers.Primary, () => Run( () => SelectAll( staged ) ) ),
			new( Key.C, KeyModifiers.Primary, () => Run( () => CopyPaths( ComputeKeyTargets( key, staged ), full: false ) ) ),
			new( Key.C, KeyModifiers.PrimaryShift, () => Run( () => CopyPaths( ComputeKeyTargets( key, staged ), full: true ) ) ),
			new( Key.F10, new KeyModifiers { Shift = true }, () => Run( () => OpenRowMenu( key, staged, null ) ) ),
			new( Key.Menu, KeyModifiers.None, () => Run( () => OpenRowMenu( key, staged, null ) ) ),
		];
	}

	/// <summary>Wraps a file or folder row's content in its selectable row: tinted while selected, selecting on click and opening the menu on right-click.</summary>
	private Row BuildSelectableRow( SideRow row, GitFileChange? file, List<Blob> children )
	{
		var theme = Theme.Current;
		var key = row.Key;
		var staged = row.IsStaged;
		var tint = row.IsSelected ? theme.Accent.WithAlpha( SelectionOpacity ) : Color.Transparent;
		return new Row
		{
			Handle = UpdateRowHandle( key ),
			MinHeight = RowHeight,
			AlignItems = AlignItems.Center,
			Padding = new Edges( 0, theme.Spacing / 2, 0, theme.Spacing / 2 + row.Depth * TreeIndent ),
			BorderRadius = theme.Radius,
			BackgroundColor = tint,
			HoverStyle = new Style { BackgroundColor = row.IsSelected ? tint : theme.Hover },
			OnPointerDown = pointer => _context.Run( () =>
			{
				if ( pointer.Button == PointerButton.Secondary )
					OpenRowMenu( key, staged, new ElementRect { X = pointer.Position.X, Y = pointer.Position.Y } );
				else if ( pointer.Button == PointerButton.Primary )
					SelectRow( key, file, staged, pointer.Modifiers );
			} ),
			Children = children,
		};
	}

	/// <summary>Selects a file or folder row: alone, which shows a file's diff; added or removed with Ctrl; or the shown range from the anchor with Shift, within its section.</summary>
	private void SelectRow( string key, GitFileChange? file, bool staged, KeyModifiers modifiers )
	{
		var prefix = staged ? "s:" : "u:";
		// A selection never spans both sections, since staging and unstaging go opposite ways.
		if ( _selection.Any( selected => !selected.StartsWith( prefix, StringComparison.Ordinal ) ) )
			_selection.Clear();

		if ( modifiers.Shift && _selectionAnchor is { } anchor && anchor.StartsWith( prefix, StringComparison.Ordinal ) && _changes is { } changes )
		{
			List<string> keys = [.. ComputeSideRows( changes ).Where( row => row.IsStaged == staged && row.Kind is SideRowKind.File or SideRowKind.Folder ).Select( row => row.Key )];
			var from = keys.IndexOf( anchor );
			var to = keys.IndexOf( key );
			if ( from >= 0 && to >= 0 )
			{
				if ( !modifiers.HasPrimary )
					_selection.Clear();
				_selection.UnionWith( keys[Math.Min( from, to )..(Math.Max( from, to ) + 1)] );
				MarkChanged();
				return;
			}
		}

		_selectionAnchor = key;
		if ( modifiers.HasPrimary )
		{
			if ( !_selection.Remove( key ) )
				_selection.Add( key );
			MarkChanged();
			return;
		}

		_selection.Clear();
		_selection.Add( key );
		if ( file is not null )
			OpenSideDiff( file, key );
		else
			MarkChanged();
	}

	/// <summary>Selects every file of a section.</summary>
	private void SelectAll( bool staged )
	{
		_selection.Clear();
		_selection.UnionWith( ListOf( staged ).Select( file => ComputeRowKey( file.Path, staged ) ) );
		MarkChanged();
	}

	/// <summary>Opens the menu from a row: a row outside the selection becomes the selection first, as in Explorer.</summary>
	private void OpenRowMenu( string key, bool staged, ElementRect? at )
	{
		if ( !_selection.Contains( key ) )
		{
			_selection.Clear();
			_selection.Add( key );
			_selectionAnchor = key;
		}
		OpenMenu( key, staged, at );
	}

	/// <summary>Gets the files a key on a row acts on: the selection when the row is in it, otherwise the row's file or folder alone.</summary>
	private IReadOnlyList<GitFileChange> ComputeKeyTargets( string key, bool staged ) =>
		ComputeFiles( _selection.Contains( key ) ? _selection : [key], staged );

	/// <summary>Gets the selected files in list order, every file under a selected folder included.</summary>
	private IReadOnlyList<GitFileChange> ComputeSelectedFiles() =>
		ComputeFiles( _selection, _selection.FirstOrDefault()?.StartsWith( "s:", StringComparison.Ordinal ) ?? false );

	/// <summary>Gets a section's files that file row keys name or that lie under folder row keys, in list order.</summary>
	private IReadOnlyList<GitFileChange> ComputeFiles( IReadOnlyCollection<string> keys, bool staged )
	{
		string[] folders = [.. keys.Select( ComputeFolderPrefix ).OfType<string>()];
		return [.. ListOf( staged ).Where( file => keys.Contains( ComputeRowKey( file.Path, staged ) )
			|| folders.Any( folder => file.Path.StartsWith( folder, StringComparison.OrdinalIgnoreCase ) ) )];
	}

	/// <summary>Gets the repository path prefix a folder row key covers, such as <c>src/Core/</c>, or null for a file row key.</summary>
	private static string? ComputeFolderPrefix( string key ) =>
		key.Length > 4 && key.AsSpan( 2 ).StartsWith( "d:" ) ? key[5..] + "/" : null;

	/// <summary>Gets the row keys of a section's folders as the tree lays them out with nothing folded.</summary>
	private IEnumerable<string> ComputeFolderKeys( bool staged ) =>
		PathTree.Build( ListOf( staged ), file => file.Path, _ => false ).Where( entry => entry.Item is null ).Select( entry => (staged ? "s:d:" : "u:d:") + entry.Path );

	/// <summary>Builds the green added and red removed line counts, leaving out a zero.</summary>
	private static IEnumerable<Blob> BuildCounts( int added, int removed )
	{
		if ( added > 0 )
			yield return new Text { Content = $"+{added:N0}", Color = Theme.Current.Success, FontSize = ViewStyle.SmallSize, FlexShrink = 0 };
		if ( removed > 0 )
			yield return new Text { Content = $"−{removed:N0}", Color = Theme.Current.Error, FontSize = ViewStyle.SmallSize, FlexShrink = 0 };
	}

	/// <summary>Builds a square icon button, raised while it is on.</summary>
	internal Button BuildIconButton( string glyph, string name, bool isOn, Action action )
	{
		var theme = Theme.Current;
		void Run() => _context.Run( action );
		return new Button
		{
			Key = name,
			Width = theme.ControlHeight - theme.Spacing,
			MinHeight = theme.ControlHeight - theme.Spacing,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = theme.Radius,
			BackgroundColor = isOn ? theme.Hover : Color.Transparent,
			Focusable = true,
			Cursor = Cursor.Pointer,
			AccessibilityName = isOn ? $"{name}, on" : name,
			OnClick = Run,
			KeyBindings = [new( Key.Enter, KeyModifiers.None, Run ), new( Key.Space, KeyModifiers.None, Run )],
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			FocusStyle = new Style { BackgroundColor = theme.Hover },
			Children = [new Text { Content = glyph, FontFamily = ViewStyle.IconFontFamily, FontSize = 13, Color = isOn ? theme.Text : theme.Muted }],
		};
	}

	/// <summary>Builds the side view's commit box: summary, why, Write it and Amend, then the main button and its options.</summary>
	/// <remarks>With a message writer and no summary typed, the main button and Ctrl+Enter write the message; otherwise they commit.</remarks>
	private Container BuildSideCommitBox()
	{
		var theme = Theme.Current;
		var writer = FindMessageWriter();
		var writes = writer is not null && !_amend && _subject.Trim().Length == 0;
		var stagedCount = ListOf( true ).Count;
		var label = _writing is not null ? "Writing…"
			: writes ? "Generate message"
			: _amend ? "Amend last commit"
			: stagedCount == 1 ? "Commit 1 file" : $"Commit {stagedCount:N0} files";
		var canRun = _writing is null && (writes ? HasChanges : CanCommit());
		var commitKey = new KeyBinding( Key.Enter, KeyModifiers.Primary, () => _context.Run( RunCommitBox ) );

		var main = Theme.Current.BuildButton( label, () => _context.Run( RunCommitBox ), canRun );
		main.AccessibilityName = $"{label} (Ctrl+Enter)";
		main.FlexGrow = 1;
		main.FlexShrink = 1;
		main.MinWidth = 0;
		main.FlexDirection = FlexDirection.Row;
		main.Gap = theme.Spacing;
		main.Children =
		[
			new Text { Content = label, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis, FlexShrink = 1, MinWidth = 0 },
			new Row
			{
				AlignItems = AlignItems.Center,
				Gap = 2,
				FlexShrink = 0,
				Children = [new Text { Content = "Ctrl+", Color = theme.Muted, FontSize = ViewStyle.SmallSize }, BuildInlineIcon( ReturnGlyph, theme.Muted )],
			},
		];
		if ( canRun )
		{
			main.BorderColor = theme.Accent;
			main.BorderWidth = theme.BorderWidth;
		}

		var options = Theme.Current.BuildButton( "Commit options", () => _context.Run( () => UpdateCommitMenu( !_isCommitMenuOpen ) ) );
		options.Handle = _commitOptions;
		options.AccessibilityName = "Commit options";
		options.FlexShrink = 0;
		options.Width = theme.ControlHeight;
		options.Padding = 0;
		options.Children = [theme.BuildChevron( true )];

		var tools = new List<Blob>();
		if ( writer is not null )
		{
			var write = BuildSmallButton( "Write it", $"Write the commit message with {writer.Name}", () => _context.Run( () => WriteMessage( writer ) ), _writing is null && HasChanges );
			write.FlexDirection = FlexDirection.Row;
			write.AlignItems = AlignItems.Center;
			write.Gap = Theme.Current.Spacing / 2;
			write.Children = [BuildInlineIcon( WriteGlyph, Theme.Current.Muted ), new Text { Content = "Write it" }];
			tools.Add( write );
		}
		tools.Add( BuildAmendBox() );

		var children = new List<Blob>
		{
			new TextEntry
			{
				Key = "summary",
				Value = _subject,
				Controlled = true,
				Placeholder = "Summary",
				PlaceholderColor = theme.Muted,
				AccessibilityName = "Commit summary",
				MinHeight = theme.ControlHeight,
				MinWidth = 0,
				Padding = new Edges( 0, theme.Spacing ),
				FontSize = theme.FontSize,
				Color = theme.Text,
				BackgroundColor = theme.Raised,
				BorderColor = theme.Border,
				BorderWidth = theme.BorderWidth,
				BorderRadius = theme.Radius,
				OnChange = value => _context.Run( () => UpdateSubject( value ) ),
				KeyBindings = [commitKey],
			},
			new TextEditor
			{
				Key = "why",
				Controller = _description,
				Placeholder = "Why this change…",
				PlaceholderColor = theme.Muted,
				CaretColor = theme.Accent,
				AccessibilityName = "Why this change",
				AccessibilityRole = AccessibilityRole.TextEditor,
				Height = SideDescriptionHeight,
				MinWidth = 0,
				Padding = new Edges( theme.Spacing / 2, theme.Spacing ),
				FontFamily = ViewStyle.FontFamily,
				FontSize = theme.FontSize,
				Color = theme.Text,
				BackgroundColor = theme.Raised,
				BorderColor = theme.Border,
				BorderWidth = theme.BorderWidth,
				BorderRadius = theme.Radius,
				KeyBindings = [commitKey],
			},
		};
		if ( (_amendError ?? _writeError) is { } error )
			children.Add( new Text { Key = "commit-error", Content = error, FontSize = ViewStyle.SmallSize, Color = theme.Error, TextMaxLines = 3, TextTrimming = TextTrimming.Ellipsis } );
		children.Add( new Row { Key = "tools", AlignItems = AlignItems.Center, Gap = theme.Spacing / 2, FlexWrap = FlexWrap.Wrap, Children = tools } );
		children.Add( new Row { Key = "commit", AlignItems = AlignItems.Center, Gap = theme.Spacing / 2, Children = [main, options] } );

		return new Container
		{
			Key = "git-side-commit",
			FlexShrink = 0,
			Gap = theme.Spacing / 2,
			Padding = theme.Spacing,
			BorderWidth = new Edges( theme.BorderWidth, 0, 0, 0 ),
			BorderColor = theme.Border,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Commit",
			Children = children,
		};
	}

	/// <summary>Builds the commit options menu: commit, commit and push, and writing the message; null while it is closed.</summary>
	private Blob? BuildCommitMenu()
	{
		if ( !_isCommitMenuOpen )
			return null;

		var canCommit = CanCommit() && _writing is null;
		var entries = new List<MenuEntry>
		{
			new MenuAction( _amend ? "Amend last commit" : "Commit", () => _context.Run( Commit ) ) { IsEnabled = canCommit, Shortcut = _subject.Trim().Length > 0 ? "Ctrl+Enter" : null },
			new MenuAction( _amend ? "Amend and push" : "Commit and push", () => _context.Run( CommitAndPush ) ) { IsEnabled = canCommit },
		};
		if ( FindMessageWriter() is { } writer )
		{
			entries.Add( new MenuSeparator() );
			entries.Add( new MenuAction( "Generate message", () => _context.Run( () => WriteMessage( writer ) ) ) { IsEnabled = _writing is null && HasChanges } );
		}

		return Cell.Mount<ContextMenuInput, ContextMenu>( "git-commit-menu", new( _commitOptions, "Commit options", entries, () => _context.Run( () => UpdateCommitMenu( false ) ) )
		{
			Width = 220,
		} );
	}

	/// <summary>Opens or closes the commit options menu.</summary>
	private void UpdateCommitMenu( bool isOpen )
	{
		_isCommitMenuOpen = isOpen;
		MarkChanged();
	}

	/// <summary>Runs the commit box's main button: writes the message while no summary is typed and a writer is loaded, otherwise commits.</summary>
	private void RunCommitBox()
	{
		if ( _writing is not null )
			return;
		if ( FindMessageWriter() is { } writer && !_amend && _subject.Trim().Length == 0 )
			WriteMessage( writer );
		else
			Commit();
	}

	/// <summary>Commits the staged files, or amends the last commit, then pushes the current branch.</summary>
	private void CommitAndPush()
	{
		if ( !CanCommit() )
			return;

		var message = ComposeMessage();
		var amend = _amend;
		RunGit( amend ? "Amending and pushing" : "Committing and pushing", async ( root, cancellationToken ) =>
		{
			await GitRepository.CommitAsync( _context.Workspace, root, message, amend, cancellationToken );
			_context.Post( ClearMessage );
			await GitRepository.PushAsync( _context.Workspace, root, null, false, cancellationToken );
		} );
	}

	/// <summary>Gets the first loaded message writer, or null.</summary>
	private GitMessageWriter? FindMessageWriter() => _context.App.Extensions.Get<GitMessageWriter>( GitMessageWriter.Point ).FirstOrDefault();

	/// <summary>Writes the commit message from the staged changes, or every change when none is staged, keeping a typed summary.</summary>
	private void WriteMessage( GitMessageWriter writer )
	{
		if ( _loadedRoot is not { } root || !HasChanges )
			return;

		_writing?.Cancel();
		var writing = CancellationTokenSource.CreateLinkedTokenSource( _context.Lifetime );
		_writing = writing;
		_writeError = null;
		_isCommitMenuOpen = false;
		MarkChanged();
		var app = _context.App;
		var request = (Profile: app.Panels.GetProfile( _context.Id ) ?? app.Settings.ActiveProfile, Summary: _subject.Trim(), HasStaged: ListOf( true ).Count > 0);
		_ = Task.Run( async () =>
		{
			try
			{
				var patch = await GitProcess.RunAsync( _context.Workspace, root, writing.Token, "-c", "core.quotepath=false", "diff", request.HasStaged ? "--cached" : "HEAD", "--no-color", "--no-ext-diff" );
				if ( patch.Length > MessagePatchLimit )
					patch = patch[..MessagePatchLimit] + "\n… the rest of the changes is cut.";
				_context.Post( () => StartWriter( writing, writer, new GitMessageRequest( app, request.Profile, root, patch, request.Summary ) ) );
			}
			catch ( OperationCanceledException ) when ( writing.IsCancellationRequested )
			{
			}
			catch ( Exception error ) when ( error is not OutOfMemoryException )
			{
				_context.Post( () => ApplyMessage( writing, null, error.Message ) );
			}
		} );
	}

	/// <summary>Starts the writer on the UI thread, unless a newer write replaced this one, and shows its message when it arrives.</summary>
	private void StartWriter( CancellationTokenSource writing, GitMessageWriter writer, GitMessageRequest request )
	{
		if ( _writing != writing )
			return;

		Task<GitMessage> written;
		try
		{
			written = writer.Write( request, writing.Token );
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			ApplyMessage( writing, null, error.Message );
			return;
		}

		_ = written.ContinueWith( task => _context.Post( () =>
		{
			if ( writing.IsCancellationRequested )
				return;
			ApplyMessage( writing, task.IsCompletedSuccessfully ? task.Result : null,
				task.IsCompletedSuccessfully ? null : task.Exception?.GetBaseException().Message ?? "The message was not written." );
		} ), TaskScheduler.Default );
	}

	/// <summary>Fills the summary, unless the user typed one, and the description with a written message, unless a newer write replaced it.</summary>
	private void ApplyMessage( CancellationTokenSource writing, GitMessage? message, string? error )
	{
		if ( _writing != writing )
			return;

		_writing = null;
		writing.Dispose();
		_writeError = error;
		if ( message is not null )
		{
			if ( _subject.Trim().Length == 0 )
				_subject = message.Summary.Trim();
			UpdateDescription( message.Description.Trim() );
		}
		MarkChanged();
	}

	/// <summary>Stages every unstaged file, or unstages every staged one.</summary>
	private void UpdateStageAll( bool staged ) => RunGit( staged ? "Unstaging all files" : "Staging all files", ( root, cancellationToken ) => staged
		? GitRepository.UnstageAsync( _context.Workspace, root, [], cancellationToken )
		: GitRepository.StageAsync( _context.Workspace, root, [], cancellationToken ) );

	/// <summary>Shows the side lists flat or as folder trees.</summary>
	private void UpdateAsList( bool asList )
	{
		_asList = asList;
		MarkChanged();
	}

	/// <summary>Folds or unfolds a folder of a side tree.</summary>
	private void UpdateSideFolded( string key )
	{
		if ( !_foldedSide.Remove( key ) )
			_foldedSide.Add( key );
		MarkChanged();
	}

	/// <summary>Shows a file's diff through the owner; its row is already the selection.</summary>
	private void OpenSideDiff( GitFileChange file, string key )
	{
		MarkChanged();
		_open( file, key.StartsWith( "s:", StringComparison.Ordinal ) );
	}

	/// <summary>Forgets the side view's state that belonged to the previous repository.</summary>
	private void ResetSide()
	{
		_foldedSide.Clear();
		_selection.Clear();
		_selectionAnchor = null;
		_isCommitMenuOpen = false;
		_writing?.Cancel();
		_writing = null;
		_writeError = null;
	}

	/// <summary>Names the kinds of side rows.</summary>
	private enum SideRowKind
	{
		/// <summary>A section heading: Unstaged or Staged.</summary>
		Section,

		/// <summary>A section's summary of its changes.</summary>
		Summary,

		/// <summary>A note that a section is empty.</summary>
		Empty,

		/// <summary>A folder of a tree.</summary>
		Folder,

		/// <summary>A changed file.</summary>
		File,
	}

	/// <summary>One row of the side view's changes.</summary>
	/// <param name="Key">The stable row key; a file's is its list row key.</param>
	/// <param name="Kind">What the row shows.</param>
	/// <param name="IsStaged">Whether the row belongs to the staged section.</param>
	/// <param name="Label">The section title, summary text, folder name or file name.</param>
	/// <param name="Depth">The tree level.</param>
	/// <param name="File">The changed file of a file row.</param>
	/// <param name="Count">How many files a section or folder holds.</param>
	/// <param name="Added">The lines added.</param>
	/// <param name="Removed">The lines removed.</param>
	/// <param name="IsOpen">Whether a folder shows its contents.</param>
	/// <param name="IsSelected">Whether a file's diff was opened last.</param>
	private sealed record SideRow( string Key, SideRowKind Kind, bool IsStaged, string Label, int Depth, GitFileChange? File, int Count, int Added, int Removed, bool IsOpen, bool IsSelected ) : IMeasuredRow;
}
