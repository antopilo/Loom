using System.Diagnostics;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>Builds the status badges, the file context menu and the discard confirmation that the side view's file rows use.</summary>
internal sealed partial class GitChangesPane
{
	/// <summary>Defines a file row's height in logical pixels.</summary>
	private const int RowHeight = 24;

	/// <summary>Defines the status badge's size in logical pixels.</summary>
	private const int BadgeSize = 16;

	/// <summary>Defines how far a badge's letter moves down so its ink, not its line box, sits in the middle.</summary>
	private const int BadgeInkDrop = 2;

	/// <summary>Defines the status badge's fill opacity behind its letter.</summary>
	private const double BadgeOpacity = 0.18;

	/// <summary>Defines the list heading's emphasis weight.</summary>
	private const int HeadingWeight = 600;

	/// <summary>Defines the discard dialog's widest size in logical pixels.</summary>
	private const int DialogWidth = 400;

	/// <summary>Defines the opacity of the backdrop behind the discard dialog.</summary>
	private const double BackdropOpacity = 0.6;

	/// <summary>Targets each file row by key, so a right-click menu opens beside it and arrow keys move focus to it.</summary>
	private readonly Dictionary<string, ElementHandle> _rowHandles = new( StringComparer.Ordinal );

	/// <summary>Stores the key of the file row whose context menu is open, or null.</summary>
	private string? _menu;

	/// <summary>Stores where on its row a right-click opened the menu, in the row's coordinates, or null to open beside the whole row.</summary>
	private ElementRect? _menuAt;

	/// <summary>Stores the files the open menu acts on: the selection, or the one file right-clicked outside it.</summary>
	private IReadOnlyList<GitFileChange> _menuFiles = [];

	/// <summary>Reports whether the open menu's files are staged.</summary>
	private bool _menuStaged;

	/// <summary>Stores the files waiting for the user to confirm discarding their changes, or null when the dialog is closed.</summary>
	private IReadOnlyList<GitFileChange>? _discard;

	/// <summary>Builds a file's status letter on a tint of its color.</summary>
	private static Container BuildBadge( char status )
	{
		var color = ComputeStatusColor( status );
		return new Container
		{
			Width = BadgeSize,
			Height = BadgeSize,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = Theme.Current.Radius - 2,
			BackgroundColor = color.WithAlpha( BadgeOpacity ),
			Children =
			[
				new Text
				{
					Content = status.ToString(),
					Color = color,
					FontFamily = ViewStyle.CodeFontFamily,
					FontSize = ViewStyle.SmallSize - 1,
					FontWeight = HeadingWeight,
					// Consolas draws capitals and ? high in their line box, 2 logical pixels above the badge's center at this size;
					// a centered box moves by half its margin.
					Margin = new Edges( BadgeInkDrop * 2, 0, 0, 0 ),
				},
			],
		};
	}

	/// <summary>Gets a status letter's color: modified in the accent, added and untracked in success, deleted in error, renamed in blue, conflicted in yellow.</summary>
	private static Color ComputeStatusColor( char status ) => status switch
	{
		'M' or 'T' => Theme.Current.Accent,
		'A' or '?' or 'C' => Theme.Current.Success,
		'D' => Theme.Current.Error,
		'R' => Theme.Current.AnsiBlue,
		'U' => Theme.Current.AnsiYellow,
		_ => Theme.Current.Muted,
	};

	/// <summary>Names a status letter for assistive technology.</summary>
	private static string DescribeStatus( char status ) => status switch
	{
		'M' => "modified",
		'A' => "added",
		'D' => "deleted",
		'R' => "renamed",
		'C' => "copied",
		'T' => "type changed",
		'U' => "conflicted",
		'?' => "untracked",
		_ => "changed",
	};

	/// <summary>Splits a repository path into its file name and its folder, which is empty at the root.</summary>
	private static (string Name, string Folder) SplitPath( string path )
	{
		var slash = path.LastIndexOfAny( ['/', '\\'] );
		return slash < 0 ? (path, "") : (path[(slash + 1)..], path[..slash]);
	}

	/// <summary>Gets a file row's key, unique across both lists.</summary>
	private static string ComputeRowKey( string path, bool staged ) => (staged ? "s:" : "u:") + path;

	/// <summary>Gets the handle that targets a file row, creating it on first use.</summary>
	private ElementHandle UpdateRowHandle( string key )
	{
		if ( !_rowHandles.TryGetValue( key, out var handle ) )
			_rowHandles.Add( key, handle = new ElementHandle() );
		return handle;
	}

	/// <summary>Drops the handles and selection of file and folder rows that are no longer listed, and the menu of a row that is gone.</summary>
	private void UpdateRowHandles()
	{
		var keys = ListOf( false ).Select( file => ComputeRowKey( file.Path, false ) )
			.Concat( ListOf( true ).Select( file => ComputeRowKey( file.Path, true ) ) )
			.Concat( ComputeFolderKeys( false ) )
			.Concat( ComputeFolderKeys( true ) )
			.ToHashSet( StringComparer.Ordinal );
		foreach ( var key in _rowHandles.Keys.Where( key => !keys.Contains( key ) ).ToArray() )
			_rowHandles.Remove( key );
		if ( _menu is { } menu && !keys.Contains( menu ) )
			_menu = null;
		_selection.IntersectWith( keys );
		if ( _selectionAnchor is { } anchor && !keys.Contains( anchor ) )
			_selectionAnchor = null;
	}

	/// <summary>Stages unstaged files or unstages staged ones, including a rename's old path, through the owner.</summary>
	private void UpdateStage( IReadOnlyList<GitFileChange> files, bool staged )
	{
		string[] paths = [.. files.SelectMany( file => file.OldPath is { } old ? new[] { file.Path, old } : new[] { file.Path } ).Distinct( StringComparer.Ordinal )];
		// An empty list means every file to the data layer, so nothing selected must not reach it.
		if ( paths.Length == 0 )
			return;

		var what = files.Count == 1 ? SplitPath( files[0].Path ).Name : $"{files.Count:N0} files";
		RunGit( $"{(staged ? "Unstaging" : "Staging")} {what}", ( root, cancellationToken ) => staged
			? GitRepository.UnstageAsync( root, paths, cancellationToken )
			: GitRepository.StageAsync( root, paths, cancellationToken ) );
	}

	/// <summary>Opens the context menu for the selected files, at the pointer or beside the row it was opened on.</summary>
	private void OpenMenu( string key, bool staged, ElementRect? at )
	{
		if ( ComputeSelectedFiles() is not { Count: > 0 } files )
			return;

		_menu = key;
		_menuAt = at;
		_menuFiles = files;
		_menuStaged = staged;
		MarkChanged();
	}

	/// <summary>Builds the open context menu for its files: open, stage or unstage, discard, ignore, stash, save as a patch and copy paths; null when no menu is open.</summary>
	private Blob? BuildMenu()
	{
		if ( _menu is not { } key || _menuFiles is not { Count: > 0 } files || !_rowHandles.TryGetValue( key, out var anchor ) )
			return null;

		var staged = _menuStaged;
		var first = files[0];
		var what = files.Count == 1 ? "1 file" : $"{files.Count:N0} files";
		var entries = new List<MenuEntry>
		{
			new MenuAction( "Open", () => _context.Run( () => OpenFiles( files ) ) ) { AccessKey = Key.O, IsEnabled = files.Any( file => file.Status != 'D' ) },
			new MenuAction( "Show in File Explorer", () => _context.Run( () => ShowInExplorer( first ) ) ) { AccessKey = Key.E },
			new MenuSeparator(),
			new MenuAction( staged ? "Unstage" : "Stage", () => _context.Run( () => UpdateStage( files, staged ) ) ) { Shortcut = "Enter" },
			new MenuAction( "Discard changes…", () => _context.Run( () => BeginDiscard( files ) ) ) { Shortcut = "Delete", IsDestructive = true },
			new MenuSeparator(),
		};
		entries.AddRange( BuildIgnoreEntries( files ) );
		entries.Add( new MenuSeparator() );
		entries.Add( new MenuAction( $"Stash {what}", () => _context.Run( () => StashFiles( files ) ) ) { AccessKey = Key.T } );
		entries.Add( new MenuAction( "Save as Patch…", () => _context.Run( () => _ = SavePatchAsync( files, staged ) ) ) { AccessKey = Key.P } );
		entries.Add( new MenuSeparator() );
		entries.Add( new MenuAction( "Copy Path", () => _context.Run( () => CopyPaths( files, full: false ) ) ) { Shortcut = "Ctrl+C" } );
		entries.Add( new MenuAction( "Copy Full Path", () => _context.Run( () => CopyPaths( files, full: true ) ) ) { Shortcut = "Ctrl+Shift+C" } );
		if ( files.Any( file => file.Status == 'U' ) )
		{
			entries.Add( new MenuSeparator() );
			entries.Add( new MenuNote( "Staging a resolved file marks it resolved" ) );
		}

		return Cell.Mount<ContextMenuInput, ContextMenu>( "git-changes-menu", new( anchor, files.Count == 1 ? $"{first.Path} options" : $"{what} options", entries, () => _context.Run( CloseMenu ) )
		{
			AnchorRect = _menuAt,
			Width = 260,
		} );
	}

	/// <summary>Builds the ignore actions: the files themselves, and for one file its extension and its folder.</summary>
	private IEnumerable<MenuEntry> BuildIgnoreEntries( IReadOnlyList<GitFileChange> files )
	{
		// One selected folder ignores the folder itself, which also covers files added to it later.
		if ( _selection.Count == 1 && ComputeFolderPrefix( _selection.First() ) is { } prefix )
		{
			yield return new MenuAction( $"Ignore folder “{prefix}”", () => _context.Run( () => Ignore( ["/" + prefix] ) ) ) { AccessKey = Key.I };
			yield break;
		}

		if ( files.Count > 1 )
		{
			yield return new MenuAction( $"Ignore {files.Count:N0} files", () => _context.Run( () => Ignore( [.. files.Select( file => "/" + file.Path )] ) ) ) { AccessKey = Key.I };
			yield break;
		}

		var (name, folder) = SplitPath( files[0].Path );
		yield return new MenuAction( $"Ignore “{name}”", () => _context.Run( () => Ignore( ["/" + files[0].Path] ) ) ) { AccessKey = Key.I };
		if ( Path.GetExtension( name ) is { Length: > 1 } extension )
			yield return new MenuAction( $"Ignore all “*{extension}” files", () => _context.Run( () => Ignore( ["*" + extension] ) ) );
		if ( folder.Length > 0 )
			yield return new MenuAction( $"Ignore folder “{folder}/”", () => _context.Run( () => Ignore( ["/" + folder + "/"] ) ) );
	}

	/// <summary>Closes the context menu.</summary>
	private void CloseMenu()
	{
		_menu = null;
		_menuFiles = [];
		MarkChanged();
	}

	/// <summary>Opens each file that still exists with the program Windows associates with it.</summary>
	private void OpenFiles( IReadOnlyList<GitFileChange> files )
	{
		foreach ( var file in files.Where( file => file.Status != 'D' ) )
			OpenFile( file );
	}

	/// <summary>Adds patterns to the repository's .gitignore through the owner, which refreshes the changes afterwards.</summary>
	private void Ignore( IReadOnlyList<string> patterns ) =>
		RunGit( patterns.Count == 1 ? $"Ignoring {patterns[0]}" : $"Ignoring {patterns.Count:N0} files", async ( root, cancellationToken ) =>
		{
			foreach ( var pattern in patterns )
				await GitRepository.IgnoreAsync( root, pattern, cancellationToken );
		} );

	/// <summary>Stashes only these files' changes, a rename's old path included.</summary>
	private void StashFiles( IReadOnlyList<GitFileChange> files )
	{
		string[] paths = [.. files.SelectMany( file => file.OldPath is { } old ? new[] { file.Path, old } : new[] { file.Path } ).Distinct( StringComparer.Ordinal )];
		RunGit( files.Count == 1 ? $"Stashing {SplitPath( files[0].Path ).Name}" : $"Stashing {files.Count:N0} files",
			( root, cancellationToken ) => GitRepository.StashPathsAsync( root, paths, cancellationToken ) );
	}

	/// <summary>Asks where to save, then writes the files' unstaged or staged changes there as one patch.</summary>
	private async Task SavePatchAsync( IReadOnlyList<GitFileChange> files, bool staged )
	{
		if ( _context.App.Window is not { } window || _loadedRoot is not { } repository )
			return;

		var name = (files.Count == 1 ? SplitPath( files[0].Path ).Name : Path.GetFileName( repository )) + ".patch";
		var result = await window.ShowFileDialogAsync( FileDialogKind.SaveFile, new FileDialogOptions { Title = "Save as Patch", InitialPath = Path.Combine( repository, name ) } );
		if ( result.Status != FileDialogStatus.Success || result.Paths is not [var picked, ..] )
			return;

		var path = Path.GetFullPath( picked );
		_context.Post( () => RunGit( $"Saving {Path.GetFileName( path )}", async ( root, cancellationToken ) =>
		{
			var patch = new System.Text.StringBuilder();
			foreach ( var file in files )
				patch.Append( await GitRepository.ReadWorkingPatchAsync( root, file, staged, cancellationToken ) );
			await File.WriteAllTextAsync( path, patch.ToString(), cancellationToken );
		} ) );
	}

	/// <summary>Copies the files' repository-relative or full paths to the clipboard, one per line.</summary>
	private void CopyPaths( IReadOnlyList<GitFileChange> files, bool full )
	{
		if ( _loadedRoot is not { } root || files.Count == 0 )
			return;

		var paths = files.Select( file => full ? Path.GetFullPath( Path.Combine( root, file.Path ) ) : file.Path );
		_context.App.Window?.Clipboard.WriteText( string.Join( Environment.NewLine, paths ) );
	}

	/// <summary>Opens a file with the program Windows associates with it.</summary>
	private void OpenFile( GitFileChange file )
	{
		if ( _loadedRoot is not { } root )
			return;

		Process.Start( new ProcessStartInfo( Path.GetFullPath( Path.Combine( root, file.Path ) ) ) { UseShellExecute = true } )?.Dispose();
	}

	/// <summary>Opens Explorer with the file selected, or its folder (or the repository) when the file no longer exists.</summary>
	private void ShowInExplorer( GitFileChange file )
	{
		if ( _loadedRoot is not { } root )
			return;

		var path = Path.GetFullPath( Path.Combine( root, file.Path ) );
		if ( File.Exists( path ) && OperatingSystem.IsWindows() )
		{
			Process.Start( "explorer.exe", $"/select,\"{path}\"" )?.Dispose();
			return;
		}

		var folder = Path.GetDirectoryName( path ) is { } parent && Directory.Exists( parent ) ? parent : root;
		Process.Start( new ProcessStartInfo( folder ) { UseShellExecute = true } )?.Dispose();
	}

	/// <summary>Asks the user to confirm discarding the files' changes.</summary>
	private void BeginDiscard( IReadOnlyList<GitFileChange> files )
	{
		if ( files.Count == 0 )
			return;

		_discard = files;
		_menu = null;
		MarkChanged();
	}

	/// <summary>Closes the discard dialog without changing anything.</summary>
	private void CancelDiscard()
	{
		_discard = null;
		MarkChanged();
	}

	/// <summary>Discards the confirmed files' changes through the owner; untracked files are deleted.</summary>
	private void ConfirmDiscard()
	{
		if ( _discard is not { } files )
			return;

		_discard = null;
		MarkChanged();
		var what = files.Count == 1 ? SplitPath( files[0].Path ).Name : $"{files.Count:N0} files";
		RunGit( $"Discarding changes to {what}", ( root, cancellationToken ) => GitRepository.DiscardAsync( root, files, cancellationToken ) );
	}

	/// <summary>Builds the in-pane confirmation for discarding changes over a dimmed backdrop; it warns when untracked files will be deleted.</summary>
	private Container BuildDiscardDialog( IReadOnlyList<GitFileChange> files )
	{
		var theme = Theme.Current;
		var subject = files.Count == 1 ? $"“{SplitPath( files[0].Path ).Name}”" : $"{files.Count:N0} files";
		var untracked = files.Count( file => file.Status == '?' );
		var body = new List<Blob>
		{
			new Text { Content = "Discard changes?", FontWeight = HeadingWeight, },
			new Text { Content = $"All changes to {subject} will be lost. This cannot be undone.", Color = theme.Muted, },
		};
		if ( untracked > 0 )
			body.Add( new Text
			{
				Content = untracked == files.Count
					? untracked == 1 ? "The file is untracked and will be deleted." : "These files are untracked and will be deleted."
					: $"{untracked:N0} untracked {(untracked == 1 ? "file" : "files")} will be deleted.",
				Color = theme.Error,
			} );
		var cancel = Theme.Current.BuildButton( "Cancel", () => _context.Run( CancelDiscard ) );
		cancel.AutoFocus = true;
		var discard = Theme.Current.BuildButton( "Discard", () => _context.Run( ConfirmDiscard ) );
		discard.Color = theme.Error;
		discard.BorderColor = theme.Error;
		discard.BorderWidth = theme.BorderWidth;
		body.Add( new Row
		{
			JustifyContent = JustifyContent.FlexEnd,
			Gap = theme.Spacing,
			Padding = new Edges( theme.Spacing, 0, 0, 0 ),
			Children = [cancel, discard],
		} );

		return new Container
		{
			Key = "git-changes-discard",
			Position = PositionType.Absolute,
			Left = 0,
			Top = 0,
			Right = 0,
			Bottom = 0,
			Padding = theme.Spacing * 2,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			Children =
			[
				// The backdrop sits behind the card as a sibling, so a click on the card never reaches it.
				new Container
				{
					Position = PositionType.Absolute,
					Left = 0,
					Top = 0,
					Right = 0,
					Bottom = 0,
					BackgroundColor = theme.Canvas.WithAlpha( BackdropOpacity ),
					OnPointerDown = _ => _context.Run( CancelDiscard ),
				},
				new Container
				{
					AccessibilityRole = AccessibilityRole.Dialog,
					AccessibilityName = "Discard changes",
					Width = Length.Percent( 100 ),
					MaxWidth = DialogWidth,
					Gap = theme.Spacing,
					Padding = theme.Spacing * 2,
					BackgroundColor = theme.Raised,
					BorderColor = theme.Border,
					BorderWidth = theme.BorderWidth,
					BorderRadius = theme.Radius,
					KeyBindings = [new( Key.Escape, KeyModifiers.None, () => _context.Run( CancelDiscard ) )],
					Children = body,
				},
			],
		};
	}
}
