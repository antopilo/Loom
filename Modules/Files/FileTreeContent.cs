using Goo;
using Lokn.Core;

namespace Lokn.Modules.Files;

/// <summary>The side view at the left of the workspace: a header with the project's name, the buttons of its modes, then the project's folders and
/// files as a tree, or the content of another mode, such as Git's changes.</summary>
/// <remarks>
/// Folders list first, then files, each by name; <c>.git</c> is left out. A folder reads its children the first time it opens, and the tree
/// follows file changes under the project. Clicking a file opens it in the editor, which swaps its last file when that has no unsaved
/// changes and otherwise opens a second editor beside it. Other modules add modes through <see cref="FileTreeMode.Point"/>.
/// </remarks>
internal sealed partial class FileTreeContent : PanelContent, IFileTreeView
{
	/// <summary>Holds the installed icon font, registered once; Goo draws only fonts registered with it.</summary>
	private static readonly FontSource Icons = SystemFonts.Register( "Lokn File Icons", 400, italic: false, "SegoeIcons.ttf" );

	/// <summary>Gets the icon font's family.</summary>
	internal static string IconFont => Icons.Family;

	/// <summary>Defines the closed folder glyph.</summary>
	internal const string FolderGlyph = "\uE8B7";

	/// <summary>Defines the open folder glyph.</summary>
	private const string OpenFolderGlyph = "\uE838";

	/// <summary>Defines the file glyph.</summary>
	private const string FileGlyph = "\uE7C3";

	/// <summary>Defines the glyph of the button that hides the tree.</summary>
	private const string CollapseGlyph = "\uE90C";

	/// <summary>Defines the right-pointing chevron of a closed folder.</summary>
	private const string ClosedChevron = "\uE76C";

	/// <summary>Defines the down-pointing chevron of an open folder.</summary>
	private const string OpenChevron = "\uE70D";

	/// <summary>Defines how far each level indents, in logical pixels.</summary>
	private const int Indent = 14;

	/// <summary>Defines a row's height in logical pixels.</summary>
	private const int RowHeight = 26;

	/// <summary>Defines the most entries a folder lists; the rest are summed up in one row.</summary>
	private const int MaxEntries = 2000;

	/// <summary>Stores the panel's context.</summary>
	private readonly PanelContext _context;

	/// <summary>Stores the open folders by full path.</summary>
	private readonly HashSet<string> _open = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Stores folder listings read so far, by full path; a changed folder keeps its listing until it is read again.</summary>
	private readonly Dictionary<string, Entry[]> _listings = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Stores the folders whose listing changed on disk and must be read again.</summary>
	private readonly HashSet<string> _stale = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Stores the folders whose listing is being read.</summary>
	private readonly HashSet<string> _reading = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Bumps when the tree must rebuild.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Stores the file last opened from the tree, which is highlighted.</summary>
	private string? _selected;

	/// <summary>Stores the folder shown, to notice when the project changes.</summary>
	private string? _root;

	/// <summary>Follows changes under the shown folder.</summary>
	private IDisposable? _watcher;

	/// <summary>Stores the file states the status sources reported, by full path.</summary>
	private Dictionary<string, FileState> _states = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Stores the folders that hold a file with a state, with the state shown on the folder.</summary>
	private Dictionary<string, FileState> _folderStates = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Waits for file changes to settle before asking the status sources again.</summary>
	private readonly Timer _statusDelay;

	/// <summary>Stores the filter a header button set, or null.</summary>
	private (string Key, Func<string, bool> Keep)? _filter;

	/// <summary>Stores the folders that hold a file with a state the filter keeps.</summary>
	private HashSet<string> _kept = [];

	/// <summary>Stores the visible rows, flattened again only after the tree or its folder changed.</summary>
	private TreeRow[] _rows = [];

	/// <summary>Stores the tree version and the folder <see cref="_rows"/> was flattened for.</summary>
	private (int Version, string? Root) _rowsFor = (-1, null);

	/// <summary>Creates the side view for the current project, on the mode it showed when its state was captured.</summary>
	internal FileTreeContent( PanelContext context )
	{
		_context = context;
		_statusDelay = new( _ => _context.Post( RequestStates ), null, Timeout.Infinite, Timeout.Infinite );
		RestoreModes( context.RestoredState );
		context.Host.WorkspaceChanged += ReloadRoot;
	}

	/// <summary>Gets the folder the tree shows.</summary>
	public string Root => _root ?? "";

	/// <summary>Gets the state a status source reported for a file, or null.</summary>
	public FileState? GetState( string path ) => _states.TryGetValue( path, out var state ) ? state : null;

	/// <summary>Gets the key of the filter that applies, or null.</summary>
	public string? FilterKey => _filter?.Key;

	/// <summary>Shows only the files a filter keeps, or every file again.</summary>
	public void SetFilter( string? key, Func<string, bool>? keep )
	{
		_filter = key is not null && keep is not null ? (key, keep) : null;
		_kept = _filter is { } filter ? ComputeKept( filter.Keep ) : [];
		_version.Value++;
	}

	/// <summary>Builds the header, the mode buttons, and the tree or the selected mode's content.</summary>
	public override Blob Build()
	{
		_ = _version.Value;
		var theme = Theme.Current;
		// The tree shows the open session's folder when that session is on the tree's host. Without one, a tree on This PC shows the folder Lokn runs in,
		// the same one a new terminal starts in, and a tree on a paired host shows none, as that folder is not on its disk. A hidden tree keeps its folder,
		// so it never lists or watches the folders of sessions it is not shown in.
		var next = _context.WorkingFolder ?? (_context.Host.IsLocal ? Environment.CurrentDirectory : null);
		if ( !string.Equals( next, _root, StringComparison.OrdinalIgnoreCase ) && (_root is null || _context.App.Panels.IsShown( _context.Id )) )
			UpdateRoot( next );
		var root = _root;

		var modes = ListModes();
		var mode = FindSelectedMode( modes );
		var name = root is null ? "Files" : Path.GetFileName( root.TrimEnd( Path.DirectorySeparatorChar ) ) is { Length: > 0 } leaf ? leaf : root;
		var header = new Row
		{
			Key = "header",
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Height = RowHeight + theme.Spacing * 2,
			Padding = new Edges( 0, theme.Spacing, 0, theme.Spacing * 1.5 ),
			Children =
			[
				new Text { Content = FolderGlyph, FontFamily = IconFont, FontSize = 14, Color = theme.Muted, FlexShrink = 0 },
				new Text { Content = name, FontWeight = 600, FlexGrow = 1, FlexShrink = 1, MinWidth = 0, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
				// Header buttons act on the tree, so they show in the Files mode and in modes that ask for them.
				.. mode.Key == FileTreeMode.FilesKey || mode.ShowsButtons
					? _context.App.Extensions.Get<FileTreeButton>( FileTreeButton.Point ).Select( BuildContributedButton )
					: [],
				BuildIconButton( CollapseGlyph, "Hide files", () => _context.App.Panels.Close( _context.Id ) ),
			],
		};

		Blob body;
		if ( mode.Key != FileTreeMode.FilesKey )
			body = BuildModeBody( mode, name );
		else
		{
			body = root is null
				? new Text { Key = "empty", Content = "Open a project to see its files.", Color = theme.Muted, Padding = theme.Spacing * 2 }
				: new Virtual
				{
					Key = "rows",
					Source = new MeasuredRowsSource<TreeRow>( ComputeRows( root ), BuildRow, static _ => RowHeight, FollowEnd: false ),
					AccessibilityName = $"{name} files",
					FlexGrow = 1,
					MinWidth = 0,
					MinHeight = 0,
					OverflowX = Overflow.Hidden,
					Padding = new Edges( theme.Spacing / 2, theme.Spacing / 2 ),
					ScrollbarY = ViewStyle.GetScrollbar(),
				};
		}

		return new Container
		{
			Width = Length.Percent( 100 ),
			Height = Length.Percent( 100 ),
			Children = [header, BuildModeButtons( modes, mode ), body],
		};
	}

	/// <summary>Stops following file changes and closes the modes' content.</summary>
	public override void Closed()
	{
		_context.Host.WorkspaceChanged -= ReloadRoot;
		_watcher?.Dispose();
		_statusDelay.Dispose();
		CloseModes();
	}

	/// <summary>Gets the visible rows, flattening the open folders again only after the tree or its folder changed.</summary>
	private TreeRow[] ComputeRows( string root )
	{
		if ( _rowsFor == (_version.Value, root) )
			return _rows;

		var rows = new List<TreeRow>();
		AddRows( rows, root, 0 );
		_rows = [.. rows];
		_rowsFor = (_version.Value, root);
		return _rows;
	}

	/// <summary>Adds the rows of a folder and of its open subfolders.</summary>
	private void AddRows( List<TreeRow> rows, string folder, int depth )
	{
		foreach ( var entry in List( folder ) )
		{
			// A filter keeps the files it accepts and the folders that hold a kept file with a state, and opens those folders.
			if ( _filter is { } filter && !(entry.IsFolder ? _kept.Contains( entry.Path ) : filter.Keep( entry.Path )) )
				continue;

			var isOpen = entry.IsFolder && _open.Contains( entry.Path );
			var isSelected = !entry.IsFolder && string.Equals( entry.Path, _selected, StringComparison.OrdinalIgnoreCase );
			FileState? state = entry.IsFolder ? _folderStates.TryGetValue( entry.Path, out var held ) ? held : null : GetState( entry.Path );
			rows.Add( new TreeRow( entry.Path, entry, depth, isOpen, isSelected, state ) );
			if ( entry.IsFolder && (isOpen || _filter is not null) )
				AddRows( rows, entry.Path, depth + 1 );
		}
	}

	/// <summary>Collects the folders that hold a file with a state that the filter keeps.</summary>
	private HashSet<string> ComputeKept( Func<string, bool> keep )
	{
		var kept = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var path in _states.Keys.Where( keep ) )
		{
			var folder = Path.GetDirectoryName( path );
			while ( folder is not null && kept.Add( folder ) )
				folder = Path.GetDirectoryName( folder );
		}
		return kept;
	}

	/// <summary>Gets the color of a state: green for new files, yellow for changes, red for removals and conflicts, muted for ignored.</summary>
	private static Color ComputeColor( FileState state ) => state switch
	{
		FileState.Added or FileState.Untracked => Theme.Current.Success,
		FileState.Modified or FileState.Renamed => Theme.Current.AnsiYellow,
		FileState.Deleted or FileState.Conflicted => Theme.Current.Error,
		_ => Theme.Current.Muted,
	};

	/// <summary>Gets the letter shown at the end of a changed file's row.</summary>
	private static string ComputeLetter( FileState state ) => state switch
	{
		FileState.Added => "A",
		FileState.Modified => "M",
		FileState.Deleted => "D",
		FileState.Renamed => "R",
		FileState.Untracked => "U",
		FileState.Conflicted => "!",
		_ => "",
	};

	/// <summary>Builds one row: the chevron and icon of a folder or the icon of a file, then the name.</summary>
	private Button BuildRow( TreeRow row )
	{
		var theme = Theme.Current;
		var (_, entry, depth, isOpen, isSelected, state) = row;
		var nameColor = state is { } shown ? ComputeColor( shown ) : theme.Text;
		void Activate() => _context.Run( () =>
		{
			if ( entry.IsFolder )
				Toggle( entry.Path );
			else
				OpenFile( entry.Path );
		} );
		return new Button
		{
			Key = entry.Path,
			Height = RowHeight,
			FlexShrink = 0,
			FlexDirection = FlexDirection.Row,
			JustifyContent = JustifyContent.FlexStart,
			AlignSelf = AlignSelf.Stretch,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing * 0.75,
			Padding = new Edges( 0, theme.Spacing, 0, theme.Spacing / 2 + depth * Indent ),
			BorderRadius = theme.Radius,
			BackgroundColor = isSelected ? theme.Hover : Color.Transparent,
			Focusable = true,
			Cursor = Cursor.Pointer,
			AccessibilityName = entry.IsFolder ? $"{entry.Name} folder, {(isOpen ? "open" : "closed")}" : entry.Name,
			OnClick = Activate,
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, Activate ),
				new( Key.Space, KeyModifiers.None, Activate ),
			],
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			FocusStyle = new Style { BackgroundColor = theme.Hover },
			Children =
			[
				new Text { Content = entry.IsFolder ? isOpen ? OpenChevron : ClosedChevron : "", FontFamily = IconFont, FontSize = 9, Width = 10, Color = theme.Muted, FlexShrink = 0 },
				new Text { Content = entry.IsFolder ? isOpen ? OpenFolderGlyph : FolderGlyph : FileGlyph, FontFamily = IconFont, FontSize = 13, Color = theme.Muted, FlexShrink = 0 },
				new Text
				{
					Content = entry.Name,
					FontWeight = isSelected ? 600 : 400,
					Color = nameColor,
					FlexShrink = 1,
					MinWidth = 0,
					TextWrap = TextWrap.NoWrap,
					TextTrimming = TextTrimming.Ellipsis,
				},
				new Spacer(),
				// A file shows its state's letter; a folder that holds changes shows a dot.
				.. state is not { } marked || marked == FileState.Ignored ? Array.Empty<Blob>()
					: entry.IsFolder
						? [new Container { Width = 6, Height = 6, BorderRadius = 3, BackgroundColor = nameColor, FlexShrink = 0 }]
						: [new Text { Content = ComputeLetter( marked ), Color = nameColor, FontSize = Theme.Current.FontSize - 2, FlexShrink = 0 }],
			],
		};
	}

	/// <summary>Builds a button a module added to the header; it shows raised while it reports itself on.</summary>
	private Button BuildContributedButton( FileTreeButton contributed )
	{
		var button = BuildIconButton( contributed.Glyph, contributed.Name, () => contributed.Run( this ) );
		button.Key = "button:" + contributed.Key;
		bool isOn;
		try
		{
			isOn = contributed.IsOn?.Invoke( this ) == true;
		}
		catch ( Exception error )
		{
			_context.App.ReportError( "Files", error );
			isOn = false;
		}
		if ( isOn )
		{
			button.BackgroundColor = Theme.Current.Hover;
			button.AccessibilityName = $"{contributed.Name}, on";
		}
		return button;
	}

	/// <summary>Builds a small icon button for the header.</summary>
	private Button BuildIconButton( string glyph, string name, Action action )
	{
		var theme = Theme.Current;
		void Run() => _context.Run( action );
		return new Button
		{
			Key = name,
			Width = RowHeight,
			Height = RowHeight,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = theme.Radius,
			Focusable = true,
			Cursor = Cursor.Pointer,
			AccessibilityName = name,
			OnClick = Run,
			KeyBindings = [new( Key.Enter, KeyModifiers.None, Run ), new( Key.Space, KeyModifiers.None, Run )],
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			FocusStyle = new Style { BackgroundColor = theme.Hover },
			Children = [new Text { Content = glyph, FontFamily = IconFont, FontSize = 13, Color = theme.Muted }],
		};
	}

	/// <summary>Opens or closes a folder.</summary>
	private void Toggle( string folder )
	{
		if ( !_open.Remove( folder ) )
			_open.Add( folder );
		_version.Value++;
	}

	/// <summary>Opens a file in the editor beside the tree, through the Editor module's route.</summary>
	private void OpenFile( string path )
	{
		_selected = path;
		_version.Value++;
		_context.App.OpenLink( new Uri( $"lokn://editor/open?path={Uri.EscapeDataString( path )}" ), _context.Id );
	}

	/// <summary>Lists a folder's folders, then its files, by name; a folder that cannot be read lists nothing.</summary>
	/// <remarks>The listing is read off the UI thread. A folder lists nothing until its first answer arrives, and a changed folder keeps its listing
	/// until the new one arrives, then the tree rebuilds.</remarks>
	private Entry[] List( string folder )
	{
		var cached = _listings.GetValueOrDefault( folder );
		if ( (cached is not null && !_stale.Contains( folder )) || _reading.Contains( folder ) )
			return cached ?? [];

		_stale.Remove( folder );
		_reading.Add( folder );
		var workspace = _context.Workspace;
		_ = Task.Run( async () =>
		{
			Entry[] entries;
			try
			{
				entries = BuildEntries( folder, await workspace.ListAsync( folder, _context.Lifetime ) );
			}
			catch ( Exception )
			{
				// A folder that cannot be read lists nothing.
				entries = [];
			}

			_context.Post( () =>
			{
				if ( workspace != _context.Workspace || !_reading.Remove( folder ) )
					return;

				_listings[folder] = entries;
				_version.Value++;
			} );
		} );
		return cached ?? [];
	}

	/// <summary>Orders a listing's folders, then its files, by name, leaving out <c>.git</c>.</summary>
	private static Entry[] BuildEntries( string folder, IReadOnlyList<WorkspaceEntry> items )
	{
		Entry[] entries =
		[
			.. items
				.Where( item => item.IsDirectory && item.Name != ".git" )
				.OrderBy( item => item.Name, StringComparer.OrdinalIgnoreCase )
				.Select( item => new Entry( Path.Combine( folder, item.Name ), item.Name, IsFolder: true ) ),
			.. items
				.Where( item => !item.IsDirectory )
				.OrderBy( item => item.Name, StringComparer.OrdinalIgnoreCase )
				.Select( item => new Entry( Path.Combine( folder, item.Name ), item.Name, IsFolder: false ) ),
		];
		return entries.Length > MaxEntries ? [.. entries.Take( MaxEntries )] : entries;
	}

	/// <summary>Reads the shown folder again through a replaced workspace host.</summary>
	private void ReloadRoot()
	{
		_root = null;
		_version.Value++;
	}

	/// <summary>Shows another folder: forgets the old listings and follows changes under the new one.</summary>
	private void UpdateRoot( string? root )
	{
		_root = root;
		_open.Clear();
		_listings.Clear();
		_stale.Clear();
		_reading.Clear();
		_states = new( StringComparer.OrdinalIgnoreCase );
		_folderStates = new( StringComparer.OrdinalIgnoreCase );
		_filter = null;
		_kept = [];
		_watcher?.Dispose();
		_watcher = null;
		if ( root is null )
			return;

		try
		{
			_watcher = _context.Workspace.Watch( root, recursive: true, MarkChanged );
		}
		catch ( DirectoryNotFoundException )
		{
			return;
		}
		catch ( Exception error ) when ( error is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException )
		{
			_watcher = null;
		}

		RequestStates();
	}

	/// <summary>Asks the status sources, such as Git, for the states under the folder, and colors the tree with the first state each file gets.</summary>
	/// <remarks>Status sources read this PC's disk, so a tree on a paired host stays uncolored rather than showing another PC's states.</remarks>
	private void RequestStates()
	{
		var root = _root;
		var sources = _context.App.Extensions.Get<FileStatusSource>( FileStatusSource.Point );
		if ( root is null || sources.Count == 0 || !_context.Host.IsLocal )
			return;

		_ = Task.Run( async () =>
		{
			var states = new Dictionary<string, FileState>( StringComparer.OrdinalIgnoreCase );
			foreach ( var source in sources )
			{
				try
				{
					foreach ( var (path, state) in await source.Compute( root, _context.Lifetime ) )
						states.TryAdd( Path.GetFullPath( path ), state );
				}
				catch ( Exception error ) when ( error is not OperationCanceledException )
				{
					// A failing source leaves the tree uncolored rather than stopping it.
				}
			}

			var folders = new Dictionary<string, FileState>( StringComparer.OrdinalIgnoreCase );
			foreach ( var (path, state) in states )
			{
				if ( state == FileState.Ignored )
					continue;
				for ( var folder = Path.GetDirectoryName( path ); folder is not null && folder.Length >= root.Length; folder = Path.GetDirectoryName( folder ) )
				{
					// A conflict or removal outranks a new file, which outranks a change, so a folder shows its most urgent state.
					if ( !folders.TryGetValue( folder, out var held ) || Rank( state ) > Rank( held ) )
						folders[folder] = state;
				}
			}

			_context.Post( () =>
			{
				if ( !string.Equals( root, _root, StringComparison.OrdinalIgnoreCase ) )
					return;
				_states = states;
				_folderStates = folders;
				if ( _filter is { } filter )
					_kept = ComputeKept( filter.Keep );
				_version.Value++;
			} );
		} );
	}

	/// <summary>Ranks states by how much a folder should show them.</summary>
	private static int Rank( FileState state ) => state switch
	{
		FileState.Conflicted => 4,
		FileState.Deleted => 3,
		FileState.Added or FileState.Untracked => 2,
		FileState.Modified or FileState.Renamed => 1,
		_ => 0,
	};

	/// <summary>Marks the listing of the folder that changed as stale, on the UI thread, so it is read again, and asks for states again once changes settle.</summary>
	/// <remarks>Runs on a watcher thread.</remarks>
	private void MarkChanged( string path, WorkspaceChange kind )
	{
		if ( path.Contains( $"{Path.DirectorySeparatorChar}.git", StringComparison.OrdinalIgnoreCase ) && !path.EndsWith( "index", StringComparison.OrdinalIgnoreCase ) )
			return;

		_statusDelay.Change( 500, Timeout.Infinite );
		if ( kind == WorkspaceChange.Changed )
			return;

		// Lost changes mark every listing stale.
		var folder = kind == WorkspaceChange.Lost ? null : Path.GetDirectoryName( path );
		_context.Post( () =>
		{
			if ( folder is null )
			{
				_stale.UnionWith( _listings.Keys );
				_stale.UnionWith( _reading );
			}
			else if ( !(_listings.ContainsKey( folder ) || _reading.Contains( folder )) || !_stale.Add( folder ) )
				return;

			_version.Value++;
		} );
	}

	/// <summary>One folder or file in a listing.</summary>
	/// <param name="Path">The full path.</param>
	/// <param name="Name">The name shown.</param>
	/// <param name="IsFolder">Whether it is a folder.</param>
	private sealed record Entry( string Path, string Name, bool IsFolder );

	/// <summary>One visible row of the tree: an entry at its depth, with what the row shows of it.</summary>
	/// <param name="Key">The entry's full path, which keys the row.</param>
	/// <param name="Entry">The folder or file.</param>
	/// <param name="Depth">How many folders deep it is under the shown folder.</param>
	/// <param name="IsOpen">Whether it is an open folder.</param>
	/// <param name="IsSelected">Whether it is the file last opened from the tree.</param>
	/// <param name="State">The state a status source reported for it, or null.</param>
	private sealed record TreeRow( string Key, Entry Entry, int Depth, bool IsOpen, bool IsSelected, FileState? State ) : IMeasuredRow;
}
