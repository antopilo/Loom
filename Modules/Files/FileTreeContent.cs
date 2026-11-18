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
	/// <summary>Holds the installed Windows icon font, registered once; Goo draws only fonts registered with it.</summary>
	private static readonly FontSource Icons = BuildIconFont();

	/// <summary>Gets the icon font's family.</summary>
	internal static string IconFont => Icons.Family;

	/// <summary>Registers Segoe Fluent Icons for the tree and the sidebar item.</summary>
	private static FontSource BuildIconFont()
	{
		var font = FontSource.FromPath( "Lokn File Icons", 400, italic: false, Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.Fonts ), "SegoeIcons.ttf" ) );
		font.Register();
		return font;
	}

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

	/// <summary>Stores folder listings read so far, by full path; a file change drops the folder's listing.</summary>
	private readonly Dictionary<string, Entry[]> _listings = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Bumps when the tree must rebuild.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Stores the file last opened from the tree, which is highlighted.</summary>
	private string? _selected;

	/// <summary>Stores the folder shown, to notice when the project changes.</summary>
	private string? _root;

	/// <summary>Follows changes under the shown folder.</summary>
	private FileSystemWatcher? _watcher;

	/// <summary>Stores the file states the status sources reported, by full path.</summary>
	private Dictionary<string, FileState> _states = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Stores the folders that hold a file with a state, with the state shown on the folder.</summary>
	private Dictionary<string, FileState> _folderStates = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Waits for file changes to settle before asking the status sources again.</summary>
	private readonly Timer _statusDelay;

	/// <summary>Stores the filter a header button set, or null.</summary>
	private (string Key, Func<string, bool> Keep)? _filter;

	/// <summary>Creates the side view for the current project, on the mode it showed when its state was captured.</summary>
	internal FileTreeContent( PanelContext context )
	{
		_context = context;
		_statusDelay = new( _ => _context.Post( RequestStates ), null, Timeout.Infinite, Timeout.Infinite );
		RestoreModes( context.RestoredState );
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
		_version.Value++;
	}

	/// <summary>Builds the header, the mode buttons, and the tree or the selected mode's content.</summary>
	public override Blob Build()
	{
		_ = _version.Value;
		var theme = Theme.Current;
		// Without a project, the tree shows the folder Lokn runs in, the same one a new terminal starts in.
		var root = _context.App.Services.Get<IWorkingFolder>()?.Current ?? Environment.CurrentDirectory;
		if ( !string.Equals( root, _root, StringComparison.OrdinalIgnoreCase ) )
			UpdateRoot( root );

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
			var rows = new List<Blob>();
			if ( root is null )
				rows.Add( new Text { Content = "Open a project to see its files.", Color = theme.Muted, Padding = theme.Spacing * 2 } );
			else
				AddRows( rows, root, 0 );
			body = new Container
			{
				Key = "rows",
				AccessibilityRole = AccessibilityRole.Group,
				AccessibilityName = $"{name} files",
				FlexGrow = 1,
				MinHeight = 0,
				OverflowY = Overflow.Scroll,
				Padding = new Edges( theme.Spacing / 2, theme.Spacing / 2 ),
				Children = rows,
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
		_watcher?.Dispose();
		_statusDelay.Dispose();
		CloseModes();
	}

	/// <summary>Adds the rows of a folder and of its open subfolders.</summary>
	private void AddRows( List<Blob> rows, string folder, int depth )
	{
		var entries = List( folder );
		foreach ( var entry in entries )
		{
			// A filter keeps the files it accepts and the folders that hold a kept file with a state, and opens those folders.
			if ( _filter is { } filter && !(entry.IsFolder ? HoldsKept( entry.Path, filter.Keep ) : filter.Keep( entry.Path )) )
				continue;

			rows.Add( BuildRow( entry, depth ) );
			if ( entry.IsFolder && (_open.Contains( entry.Path ) || _filter is not null) )
				AddRows( rows, entry.Path, depth + 1 );
		}
	}

	/// <summary>Reports whether a folder holds a file with a state that the filter keeps.</summary>
	private bool HoldsKept( string folder, Func<string, bool> keep )
	{
		var prefix = folder.TrimEnd( Path.DirectorySeparatorChar ) + Path.DirectorySeparatorChar;
		return _states.Keys.Any( path => path.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) && keep( path ) );
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
	private Button BuildRow( Entry entry, int depth )
	{
		var theme = Theme.Current;
		var isOpen = entry.IsFolder && _open.Contains( entry.Path );
		var isSelected = !entry.IsFolder && string.Equals( entry.Path, _selected, StringComparison.OrdinalIgnoreCase );
		FileState? state = entry.IsFolder ? _folderStates.TryGetValue( entry.Path, out var held ) ? held : null : GetState( entry.Path );
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
	private Entry[] List( string folder )
	{
		if ( _listings.TryGetValue( folder, out var cached ) )
			return cached;

		Entry[] entries;
		try
		{
			var info = new DirectoryInfo( folder );
			entries =
			[
				.. info.EnumerateDirectories()
					.Where( item => item.Name != ".git" )
					.OrderBy( item => item.Name, StringComparer.OrdinalIgnoreCase )
					.Select( item => new Entry( item.FullName, item.Name, IsFolder: true ) ),
				.. info.EnumerateFiles()
					.OrderBy( item => item.Name, StringComparer.OrdinalIgnoreCase )
					.Select( item => new Entry( item.FullName, item.Name, IsFolder: false ) ),
			];
			if ( entries.Length > MaxEntries )
				entries = [.. entries.Take( MaxEntries )];
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or System.Security.SecurityException )
		{
			entries = [];
		}

		return _listings[folder] = entries;
	}

	/// <summary>Shows another folder: forgets the old listings and follows changes under the new one.</summary>
	private void UpdateRoot( string? root )
	{
		_root = root;
		_open.Clear();
		_listings.Clear();
		_states = new( StringComparer.OrdinalIgnoreCase );
		_folderStates = new( StringComparer.OrdinalIgnoreCase );
		_filter = null;
		_watcher?.Dispose();
		_watcher = null;
		if ( root is null || !Directory.Exists( root ) )
			return;

		try
		{
			_watcher = new FileSystemWatcher( root )
			{
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
			};
			_watcher.Created += MarkChanged;
			_watcher.Deleted += MarkChanged;
			_watcher.Renamed += MarkChanged;
			_watcher.Changed += MarkChanged;
			_watcher.EnableRaisingEvents = true;
		}
		catch ( Exception error ) when ( error is IOException or ArgumentException or PlatformNotSupportedException )
		{
			_watcher = null;
		}

		RequestStates();
	}

	/// <summary>Asks the status sources, such as Git, for the states under the folder, and colors the tree with the first state each file gets.</summary>
	private void RequestStates()
	{
		var root = _root;
		var sources = _context.App.Extensions.Get<FileStatusSource>( FileStatusSource.Point );
		if ( root is null || sources.Count == 0 )
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

	/// <summary>Drops the listing of the folder that changed, on the UI thread, so it is read again, and asks for states again once changes settle.</summary>
	private void MarkChanged( object sender, FileSystemEventArgs change )
	{
		if ( change.FullPath.Contains( $"{Path.DirectorySeparatorChar}.git", StringComparison.OrdinalIgnoreCase ) && !change.FullPath.EndsWith( "index", StringComparison.OrdinalIgnoreCase ) )
			return;

		_statusDelay.Change( 500, Timeout.Infinite );
		if ( change.ChangeType == WatcherChangeTypes.Changed )
			return;

		var folder = Path.GetDirectoryName( change.FullPath );
		_context.Post( () =>
		{
			if ( folder is not null && _listings.Remove( folder ) )
				_version.Value++;
		} );
	}

	/// <summary>One folder or file in a listing.</summary>
	/// <param name="Path">The full path.</param>
	/// <param name="Name">The name shown.</param>
	/// <param name="IsFolder">Whether it is a folder.</param>
	private sealed record Entry( string Path, string Name, bool IsFolder );
}
