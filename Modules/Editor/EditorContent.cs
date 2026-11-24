using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Editor;

/// <summary>Edits text files in one panel: a header with back and forward, the file's path and a menu, then the text.</summary>
/// <remarks>
/// Each file the panel opened stays open behind back and forward with its unsaved changes, its encoding and its line endings,
/// until the panel closes. The panel remembers its history across restarts; unsaved changes are not kept. Ctrl+S saves,
/// Ctrl+Shift+S saves as, Alt+Left and Alt+Right go back and forward. Closing the panel loses unsaved changes. On a paired host, whose disk no native
/// file chooser shows, Save as asks for the path in a bar under the header and checks it on the host.
/// </remarks>
internal sealed class EditorContent : PanelContent
{
	/// <summary>Defines the largest file the editor opens, in bytes.</summary>
	private const long MaxBytes = 16 * 1024 * 1024;

	/// <summary>Defines the monospace fonts; the first installed one wins.</summary>
	private const string CodeFont = "Cascadia Mono, Consolas, Courier New";

	/// <summary>Holds the installed Windows icon font, registered once; Goo draws only fonts registered with it.</summary>
	private static readonly FontSource Icons = BuildIconFont();

	/// <summary>Registers Segoe Fluent Icons for the header buttons.</summary>
	private static FontSource BuildIconFont()
	{
		var font = FontSource.FromPath( "Lokn Editor Icons", 400, italic: false, Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.Fonts ), "SegoeIcons.ttf" ) );
		font.Register();
		return font;
	}

	/// <summary>Defines the header's icon button size in logical pixels.</summary>
	private const int IconSize = 28;

	/// <summary>Stores the panel's context.</summary>
	private readonly PanelContext _context;

	/// <summary>Stores the files this panel opened, by full path, or by an empty key for a new file not saved yet.</summary>
	private readonly Dictionary<string, OpenFile> _files = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Stores the paths visited, oldest first; back and forward move through it.</summary>
	private readonly List<string> _history = [];

	/// <summary>Bumps when the panel must rebuild outside the editor: a load, a save, a move, the menu, or the first edit after a save.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Anchors the menu to its button.</summary>
	private readonly ElementHandle _menuHandle = new();

	/// <summary>Stores the shown entry of <see cref="_history"/>.</summary>
	private int _index = -1;

	/// <summary>Tracks whether the menu is open.</summary>
	private bool _isMenuOpen;

	/// <summary>Stores the last error, shown in the header until the next change, or null.</summary>
	private string? _error;

	/// <summary>Stores the path typed in the Save as bar of a file on a paired host, or null while the bar is closed.</summary>
	private string? _saveAsPath;

	/// <summary>Stores why the host refused the Save as bar's path, or null.</summary>
	private string? _saveAsError;

	/// <summary>Stores whether the host is checking the Save as bar's path.</summary>
	private bool _isSaveAsChecking;

	/// <summary>Stores the module, which picks the editor a link opens in.</summary>
	private readonly EditorModule _module;

	/// <summary>Opens the file given as input, or the history restored from the last session; without either starts a new file.</summary>
	internal EditorContent( PanelContext context, EditorModule module )
	{
		_context = context;
		_module = module;
		module.MarkUsed( this );
		if ( context.RestoredState is { ValueKind: JsonValueKind.Object } state && state.TryGetProperty( "history", out var history ) && history.ValueKind == JsonValueKind.Array )
		{
			_history.AddRange( history.EnumerateArray().Select( item => item.GetString() ).OfType<string>() );
			_index = state.TryGetProperty( "index", out var index ) && index.TryGetInt32( out var value ) ? Math.Clamp( value, 0, _history.Count - 1 ) : _history.Count - 1;
		}
		else if ( context.Input is string path )
			Navigate( Path.GetFullPath( path ) );

		if ( _history.Count == 0 )
		{
			_history.Add( "" );
			_index = 0;
		}
	}

	/// <summary>Gets the panel's identifier.</summary>
	internal Guid Id => _context.Id;

	/// <summary>Gets whether the shown file has unsaved changes.</summary>
	internal bool IsDirty => File.IsDirty;

	/// <summary>Reports whether the panel shows a file.</summary>
	internal bool Shows( string path ) => Current.Equals( path, StringComparison.OrdinalIgnoreCase );

	/// <summary>Gets the path shown, or empty for a new file.</summary>
	private string Current => _history[_index];

	/// <summary>Gets the file shown, reading it the first time it shows.</summary>
	private OpenFile File => _files.TryGetValue( Current, out var file ) ? file : _files[Current] = Read( Current );

	/// <summary>Builds the header and the editor.</summary>
	public override Blob Build()
	{
		_ = _version.Value;
		var theme = Theme.Current;
		var settings = _context.App.Settings;
		var file = File;
		file.Editor.TabWidth = settings.Get( EditorModule.TabWidth );
		file.Editor.IndentWidth = settings.Get( EditorModule.TabWidth );
		var name = Current.Length == 0 ? "Untitled" : Path.GetFileName( Current );

		var header = new Row
		{
			Key = "header",
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Padding = new Edges( theme.Spacing / 2, theme.Spacing ),
			BorderColor = theme.Border,
			BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
			Children =
			[
				BuildIcon( "\uE72B", "Back", () => Move( -1 ), _index > 0 ),
				BuildIcon( "\uE72A", "Forward", () => Move( 1 ), _index < _history.Count - 1 ),
				new Text
				{
					Content = (file.IsDirty ? "● " : "") + (_error ?? (Current.Length == 0 ? "Untitled" : Current)),
					Color = _error is not null ? theme.Error : file.IsDirty ? theme.Text : theme.Muted,
					Margin = new Edges( 0, theme.Spacing / 2 ),
					FlexGrow = 1,
					FlexShrink = 1,
					MinWidth = 0,
					TextWrap = TextWrap.NoWrap,
					TextTrimming = TextTrimming.Ellipsis,
				},
				BuildMenuButton(),
			],
		};

		// An image shows as itself; another file that is not text shows why instead of its bytes as characters.
		Blob body = file.Image is { } image ? BuildImage( image ) : file.IsBinary ? BuildNotText( name ) : BuildEditor( file, name );
		return new Container
		{
			Width = Length.Percent( 100 ),
			Height = Length.Percent( 100 ),
			Children = _saveAsPath is { } typed ? [header, BuildSaveAs( typed ), body] : [header, body],
		};
	}

	/// <summary>Builds an image file centered in the panel, never larger than its own pixels.</summary>
	private static Container BuildImage( ImageSource image ) => new()
	{
		Key = "image",
		FlexGrow = 1,
		MinHeight = 0,
		Padding = Theme.Current.Spacing,
		AlignItems = AlignItems.Center,
		JustifyContent = JustifyContent.Center,
		Children =
		[
			new Image
			{
				Source = image,
				Fit = ImageFit.Contain,
				Width = Length.Percent( 100 ),
				Height = Length.Percent( 100 ),
				MaxWidth = image.Width,
				MaxHeight = image.Height,
				AccessibilityName = "Image",
			},
		],
	};

	/// <summary>Builds the note shown in place of a file that is not text and not an image Loom can show.</summary>
	private static Text BuildNotText( string name ) => new()
	{
		Key = "not-text",
		Content = $"{name} is not a text file, so the editor does not open it.",
		Color = Theme.Current.Muted,
		Padding = Theme.Current.Spacing * 2,
	};

	/// <summary>Builds the text editor of a text file.</summary>
	private TextEditor BuildEditor( OpenFile file, string name )
	{
		var settings = _context.App.Settings;
		var theme = Theme.Current;
		return new TextEditor
		{
			Key = "editor:" + Current,
			Controller = file.Editor,
			Layers = [file.Refresh()],
			ScrollbarY = BuildScrollbar(),
			ScrollbarX = BuildScrollbar(),
			AccessibilityName = name,
			AccessibilityRole = AccessibilityRole.TextEditor,
			FontFamily = CodeFont,
			FontSize = settings.Get( EditorModule.FontSize ),
			ShowLineNumbers = settings.Get( EditorModule.LineNumbers ),
			TextWrap = settings.Get( EditorModule.WrapLines ) ? TextWrap.Wrap : TextWrap.NoWrap,
			LineNumberColor = theme.Muted,
			CaretColor = theme.Accent,
			CurrentLineColor = theme.Hover,
			Padding = theme.Spacing,
			FlexGrow = 1,
			MinHeight = 0,
			MinWidth = 0,
			OnChange = _ => UpdateChanged(),
			KeyBindings =
			[
				new( Key.S, KeyModifiers.Primary, () => _context.Run( () => _ = SaveAsync( saveAs: false ) ) ),
				new( Key.S, KeyModifiers.PrimaryShift, () => _context.Run( () => _ = SaveAsync( saveAs: true ) ) ),
				new( Key.Left, new KeyModifiers { Alt = true }, () => _context.Run( () => Move( -1 ) ) ),
				new( Key.Right, new KeyModifiers { Alt = true }, () => _context.Run( () => Move( 1 ) ) ),
			],
		};
	}

	/// <summary>Builds the Save as bar of a file on a paired host: the path on the host, why it was refused, Save and Cancel; Enter saves, Escape cancels.</summary>
	private Container BuildSaveAs( string typed )
	{
		var theme = Theme.Current;
		var host = _context.Host.Name;
		var save = theme.BuildButton( _isSaveAsChecking ? "Checking…" : "Save", () => _context.Run( ConfirmSaveAs ), !_isSaveAsChecking && typed.Trim().Length > 0 );
		save.Key = "save-as-save";
		var cancel = theme.BuildButton( "Cancel", () => _context.Run( CloseSaveAs ), !_isSaveAsChecking );
		cancel.Key = "save-as-cancel";
		var children = new List<Blob>
		{
			new Row
			{
				Key = "save-as-row",
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing,
				Children =
				[
					new Text { Content = $"Save as on {host}", FlexShrink = 0 },
					new TextEntry
					{
						Key = "save-as-path",
						Value = typed,
						Controlled = true,
						AutoFocus = true,
						Placeholder = @"C:\path\to\file.txt",
						AccessibilityName = $"Path on {host}",
						FlexGrow = 1,
						MinWidth = 0,
						Height = theme.ControlHeight,
						Padding = new Edges( 0, theme.Spacing ),
						BackgroundColor = theme.Raised,
						BorderColor = theme.Border,
						BorderWidth = theme.BorderWidth,
						BorderRadius = theme.Radius,
						FocusStyle = new Style { BorderColor = theme.Accent },
						OnChange = text => _context.Run( () =>
						{
							_saveAsPath = text;
							_saveAsError = null;
							_version.Value++;
						} ),
						OnSubmit = _ => _context.Run( ConfirmSaveAs ),
						KeyBindings = [new( Key.Escape, KeyModifiers.None, () => _context.Run( CloseSaveAs ) )],
					},
					save,
					cancel,
				],
			},
		};
		if ( _saveAsError is { } refusal )
			children.Add( new Text { Key = "save-as-error", Content = refusal, Color = theme.Error, Selectable = true, TextWrap = TextWrap.Wrap } );

		return new Container
		{
			Key = "save-as",
			Gap = theme.Spacing / 2,
			Padding = new Edges( theme.Spacing / 2, theme.Spacing ),
			BorderColor = theme.Border,
			BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
			Children = children,
		};
	}

	/// <summary>Checks the Save as bar's path on the paired host, off the UI thread, then saves the file there or says why not.</summary>
	private void ConfirmSaveAs()
	{
		if ( _isSaveAsChecking || _saveAsPath?.Trim() is not { Length: > 0 } typed )
			return;

		var host = _context.Host;
		if ( !Path.IsPathFullyQualified( typed ) )
		{
			_saveAsError = $"Type the full path of a file on {host.Name}, such as C:\\folder\\file.txt.";
			_version.Value++;
			return;
		}

		var path = Path.GetFullPath( typed );
		_isSaveAsChecking = true;
		_saveAsError = null;
		_version.Value++;
		CheckSaveAsAsync( host.Workspace, host.Name, path, _context.Lifetime ).WhenDone( _context.Post, done =>
		{
			_isSaveAsChecking = false;
			var refusal = done.IsCompletedSuccessfully ? done.Result : done.Exception?.GetBaseException().Message ?? "The check was cancelled.";
			if ( refusal is not null )
			{
				_saveAsError = refusal;
				_version.Value++;
				return;
			}

			CloseSaveAs();
			Save( path );
		} );
	}

	/// <summary>Closes the Save as bar without saving.</summary>
	private void CloseSaveAs()
	{
		if ( _isSaveAsChecking )
			return;

		_saveAsPath = null;
		_saveAsError = null;
		_version.Value++;
	}

	/// <summary>Checks on a host that a path can take a saved file: its folder exists there and the path is not a folder.</summary>
	/// <returns>Why the path cannot take the file, or null when it can.</returns>
	private static async Task<string?> CheckSaveAsAsync( IWorkspaceHost workspace, string hostName, string path, CancellationToken cancellationToken )
	{
		if ( Path.GetDirectoryName( path ) is not { } folder || !await workspace.DirectoryExistsAsync( folder, cancellationToken ).ConfigureAwait( false ) )
			return $"{hostName} has no folder at {Path.GetDirectoryName( path ) ?? path}.";

		return (await workspace.StatAsync( path, cancellationToken ).ConfigureAwait( false )).IsDirectory ? $"{path} is a folder on {hostName}." : null;
	}

	/// <summary>Saves the history so the panel reopens its files after a restart; a new file not saved yet is left out.</summary>
	public override JsonElement? CaptureState()
	{
		var saved = _history.Where( path => path.Length > 0 ).ToArray();
		if ( saved.Length == 0 )
			return null;

		var index = Array.IndexOf( saved, Current );
		return JsonSerializer.SerializeToElement( new { history = saved, index = index < 0 ? saved.Length - 1 : index } );
	}

	/// <summary>Releases every open file's editor.</summary>
	public override void Closed()
	{
		_module.Forget( this );
		foreach ( var file in _files.Values )
			file.Dispose();
	}

	/// <summary>Opens a file in this panel after the shown one, dropping anything forward of it, like a browser.</summary>
	internal void Navigate( string path )
	{
		if ( _index >= 0 && Current.Equals( path, StringComparison.OrdinalIgnoreCase ) )
			return;

		if ( _index < _history.Count - 1 )
			_history.RemoveRange( _index + 1, _history.Count - _index - 1 );
		_history.Add( path );
		_index = _history.Count - 1;
		_error = null;
		_module.MarkUsed( this );
		_version.Value++;
	}

	/// <summary>Goes back or forward one file; the file keeps its unsaved changes.</summary>
	private void Move( int step )
	{
		var index = _index + step;
		if ( index < 0 || index >= _history.Count )
			return;

		_index = index;
		_error = null;
		_version.Value++;
	}

	/// <summary>Builds one header icon button.</summary>
	private Button BuildIcon( string glyph, string name, Action action, bool isEnabled = true, ElementHandle? handle = null )
	{
		var theme = Theme.Current;
		void Run() => _context.Run( action );
		return new Button
		{
			Key = name,
			Handle = handle,
			Width = IconSize,
			Height = IconSize,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = theme.Radius,
			Focusable = isEnabled,
			Cursor = isEnabled ? Cursor.Pointer : Cursor.Default,
			AccessibilityName = name,
			Color = isEnabled ? theme.Text : theme.Muted,
			Opacity = isEnabled ? 1 : 0.4,
			OnClick = isEnabled ? Run : null,
			KeyBindings = isEnabled ? [new( Key.Enter, KeyModifiers.None, Run ), new( Key.Space, KeyModifiers.None, Run )] : [],
			HoverStyle = isEnabled ? new Style { BackgroundColor = theme.Hover } : null,
			FocusStyle = new Style { BackgroundColor = theme.Hover },
			Children = [new Text { Content = glyph, FontFamily = Icons.Family, FontSize = 12 }],
		};
	}

	/// <summary>Builds the "…" button and, while open, its menu.</summary>
	private Blob BuildMenuButton()
	{
		var button = BuildIcon( "\uE712", "More actions", () =>
		{
			_isMenuOpen = !_isMenuOpen;
			_version.Value++;
		}, handle: _menuHandle );
		if ( !_isMenuOpen )
			return button;

		var hasPath = Current.Length > 0;
		var root = hasPath ? File.Repository : null;
		var wraps = _context.App.Settings.Get( EditorModule.WrapLines );
		MenuEntry[] entries =
		[
			new MenuAction( "Save", () => Choose( () => _ = SaveAsync( saveAs: false ) ) ) { Shortcut = "Ctrl+S" },
			new MenuAction( "Save as…", () => Choose( () => _ = SaveAsync( saveAs: true ) ) ) { Shortcut = "Ctrl+Shift+S" },
			new MenuSeparator(),
			new MenuAction( wraps ? "✓ Wrap lines" : "Wrap lines", () => Choose( () => _context.App.Settings.Set( EditorModule.WrapLines, SettingsScope.Global, !wraps ) ) ),
			new MenuSeparator(),
			new MenuAction( "Show in Explorer", () => Choose( () => ShowInExplorer( Current ) ) ) { IsEnabled = hasPath && _context.Host.IsLocal },
			new MenuAction( "Copy path", () => Choose( () => Copy( Current ) ) ) { IsEnabled = hasPath },
			new MenuAction( "Copy relative path", () => Choose( () => Copy( Path.GetRelativePath( root!, Current ) ) ) ) { IsEnabled = root is not null },
		];
		button.Children =
		[
			.. button.Children,
			Cell.Mount<ContextMenuInput, ContextMenu>( "editor-menu", new( _menuHandle, "Editor actions", entries, () => _context.Run( CloseMenu ) )
			{
				Placement = PortalPlacement.BottomEnd,
				Width = 240,
			} ),
		];
		return button;
	}

	/// <summary>Closes the menu, then runs the picked action.</summary>
	private void Choose( Action action ) => _context.Run( () =>
	{
		CloseMenu();
		action();
	} );

	/// <summary>Closes the menu.</summary>
	private void CloseMenu()
	{
		if ( !_isMenuOpen )
			return;

		_isMenuOpen = false;
		_version.Value++;
	}

	/// <summary>Copies text to the clipboard.</summary>
	private void Copy( string text )
	{
		if ( _context.App.Window?.Clipboard is not { } clipboard )
			return;

		clipboard.WriteText( text );
	}

	/// <summary>Opens Explorer with the file selected.</summary>
	private void ShowInExplorer( string path )
	{
		try
		{
			Process.Start( new ProcessStartInfo( "explorer.exe" ) { ArgumentList = { "/select,", path }, UseShellExecute = false } );
		}
		catch ( Exception error ) when ( error is System.ComponentModel.Win32Exception or InvalidOperationException )
		{
			Fail( $"Could not open Explorer: {error.Message}" );
		}
	}

	/// <summary>Finds the nearest folder above a file that holds a git repository, which relative paths start from; null when there is none.</summary>
	private static async Task<string?> FindRepositoryAsync( IWorkspaceHost workspace, string path, CancellationToken cancellationToken )
	{
		for ( var directory = Path.GetDirectoryName( path ); directory is not null; directory = Path.GetDirectoryName( directory ) )
		{
			if ( (await workspace.StatAsync( Path.Combine( directory, ".git" ), cancellationToken ).ConfigureAwait( false )).Exists )
				return directory;
		}
		return null;
	}

	/// <summary>Recolors the text when it really changed, and shows the unsaved mark once it differs from the file.</summary>
	/// <remarks>The editor also reports changes that leave the text as it was; rebuilding for those would rebuild without end.</remarks>
	private void UpdateChanged()
	{
		var file = File;
		if ( !file.NeedsRefresh && file.ShownDirty == file.IsDirty && _error is null )
			return;

		file.ShownDirty = file.IsDirty;
		_error = null;
		_version.Value++;
	}

	/// <summary>Saves the shown file, asking for a path for a new file or Save as: in the native file chooser on This PC, in the Save as bar on a paired host.</summary>
	private async Task SaveAsync( bool saveAs )
	{
		var path = Current;
		if ( saveAs || path.Length == 0 )
		{
			// A paired host's disk shows in no native file chooser, which would pick a path on This PC's.
			if ( !_context.Host.IsLocal )
			{
				_saveAsPath = path;
				_saveAsError = null;
				_version.Value++;
				return;
			}

			if ( _context.App.Window is not { } window )
				return;

			var result = await window.ShowFileDialogAsync( FileDialogKind.SaveFile, new FileDialogOptions { Title = "Save as", InitialPath = path } );
			if ( result.Status != FileDialogStatus.Success || result.Paths is not [var picked, ..] )
			{
				if ( result.Status != FileDialogStatus.Cancelled )
					_context.Post( () => Fail( $"Could not open the file chooser: {result.Error}" ) );
				return;
			}
			path = Path.GetFullPath( picked );
		}

		_context.Post( () => Save( path ) );
	}

	/// <summary>Writes the shown file through a temporary file, so a failed write keeps the old one; a new path moves the file there.</summary>
	private void Save( string path )
	{
		var file = File;
		if ( file.IsLoading || file.IsBinary )
		{
			Fail( file.IsBinary ? "Could not save: the file is not text." : "Could not save: the file is still loading." );
			return;
		}

		var text = file.Editor.Document.GetText();
		if ( file.NewLine != "\n" )
			text = text.Replace( "\n", file.NewLine, StringComparison.Ordinal );
		byte[] bytes = [.. file.Encoding.GetPreamble(), .. file.Encoding.GetBytes( text )];
		var version = file.Editor.Document.Version;
		// A remote host answers later, when the editor may show another file; the save belongs to the file and path it started from.
		var origin = Current;
		WriteAsync( _context.Workspace, path, bytes, _context.Lifetime ).WhenDone( _context.Post, done => UpdateSaved( file, origin, path, version, done ) );
	}

	/// <summary>Writes the bytes to a temporary file beside the path, then moves it over the path.</summary>
	private static async Task WriteAsync( IWorkspaceHost workspace, string path, byte[] bytes, CancellationToken cancellationToken )
	{
		var temporary = path + ".lokn-save";
		await workspace.WriteAsync( temporary, bytes, cancellationToken ).ConfigureAwait( false );
		await workspace.MoveAsync( temporary, path, cancellationToken ).ConfigureAwait( false );
	}

	/// <summary>Marks a finished save, moving the file to its new path after Save as, or shows why it failed.</summary>
	/// <param name="file">The file saved.</param>
	/// <param name="origin">The path the file was shown under when the save started.</param>
	/// <param name="path">The path it was saved to.</param>
	/// <param name="version">The document version written.</param>
	/// <param name="write">The finished write.</param>
	private void UpdateSaved( OpenFile file, string origin, string path, long version, Task write )
	{
		try
		{
			write.GetAwaiter().GetResult();
			file.SavedVersion = version;
			file.ShownDirty = file.IsDirty;
			RequestMarks( file, path );
			if ( !path.Equals( origin, StringComparison.OrdinalIgnoreCase ) )
			{
				// Save as keeps the edits under the new path, and goes to it unless the editor moved on to another file meanwhile.
				if ( _files.GetValueOrDefault( origin ) == file )
					_files.Remove( origin );
				if ( _files.Remove( path, out var replaced ) && replaced != file )
					replaced.Dispose();
				_files[path] = file;
				if ( origin.Equals( Current, StringComparison.OrdinalIgnoreCase ) )
					Navigate( path );
				FindRepositoryAsync( _context.Workspace, path, _context.Lifetime ).WhenDone( _context.Post, found =>
				{
					file.Repository = found.IsCompletedSuccessfully ? found.Result : null;
					_version.Value++;
				} );
			}
			_error = null;
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException )
		{
			_error = $"Could not save: {error.Message}";
		}

		_version.Value++;
	}

	/// <summary>Reads a file through the workspace, keeping its encoding and line endings for the next save; a missing path or a new file starts empty.</summary>
	/// <remarks>A new file starts empty at once. Another file is read off the UI thread: it shows empty and unsavable until it is read, then the read file swaps in.</remarks>
	private OpenFile Read( string path )
	{
		if ( path.Length == 0 )
			return BuildFile( path, LoadAsync( _context.Workspace, path, _context.Lifetime ) );

		var workspace = _context.Workspace;
		var load = Task.Run( () => LoadAsync( workspace, path, _context.Lifetime ) );
		var loading = BuildFile( path, null );
		loading.IsLoading = true;
		_ = load.ContinueWith( _ => _context.Post( () =>
		{
			if ( !_files.TryGetValue( path, out var shown ) || shown != loading )
				return;

			_files[path] = BuildFile( path, load );
			loading.Dispose();
			_version.Value++;
		} ), TaskScheduler.Default );
		return loading;
	}

	/// <summary>Reads a file's text, its encoding and its repository; a missing path or a new file is empty.</summary>
	private static async Task<LoadedFile> LoadAsync( IWorkspaceHost workspace, string path, CancellationToken cancellationToken )
	{
		var encoding = (Encoding)new UTF8Encoding( encoderShouldEmitUTF8Identifier: false );
		if ( path.Length == 0 )
			return new( "", encoding, null, null );

		var text = "";
		string? error = null;
		ImageSource? image = null;
		var isBinary = false;
		try
		{
			var stat = await workspace.StatAsync( path, cancellationToken ).ConfigureAwait( false );
			if ( stat.IsFile )
			{
				if ( stat.Size > MaxBytes )
					throw new IOException( $"The file is larger than {MaxBytes / 1024 / 1024} MB." );

				var bytes = await workspace.ReadAsync( path, cancellationToken ).ConfigureAwait( false );
				if ( IsBinary( bytes ) )
				{
					// Shown as characters, an image's bytes made a document the editor could not draw, and Loom closed.
					isBinary = true;
					image = TryDecodeImage( bytes );
				}
				else
				{
					using var reader = new StreamReader( new MemoryStream( bytes ), encoding, detectEncodingFromByteOrderMarks: true );
					text = reader.ReadToEnd();
					encoding = reader.CurrentEncoding;
				}
			}
		}
		catch ( Exception failure ) when ( failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException )
		{
			error = $"Could not open {Path.GetFileName( path )}: {failure.Message}";
		}

		string? repository;
		try
		{
			repository = await FindRepositoryAsync( workspace, path, cancellationToken ).ConfigureAwait( false );
		}
		catch ( Exception failure ) when ( failure is IOException or UnauthorizedAccessException or ArgumentException )
		{
			repository = null;
		}
		return new( text, encoding, repository, error ) { Image = image, IsBinary = isBinary };
	}

	/// <summary>Determines whether a file is not text: it has a NUL byte among its first 8000, as git decides, and no UTF-16 or UTF-32 byte order mark.</summary>
	private static bool IsBinary( byte[] bytes )
	{
		if ( bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..] or [0, 0, 0xFE, 0xFF, ..] )
			return false;

		return bytes.AsSpan( 0, Math.Min( bytes.Length, 8000 ) ).Contains( (byte)0 );
	}

	/// <summary>Decodes a file as an image, or returns null when it is not one Loom can show.</summary>
	private static ImageSource? TryDecodeImage( byte[] bytes )
	{
		try
		{
			return ImageSource.Decode( bytes );
		}
		catch ( Exception error ) when ( error is FormatException or InvalidOperationException or ArgumentException or NotSupportedException or IOException )
		{
			return null;
		}
	}

	/// <summary>Builds the open file from a finished read, or an empty one while it is still loading, and asks for its line marks.</summary>
	private OpenFile BuildFile( string path, Task<LoadedFile>? load )
	{
		// Expected read failures come back as the error; anything else surfaces as it did when the read ran here.
		if ( load is { IsFaulted: true } )
			load.GetAwaiter().GetResult();

		var loaded = load is { IsCompletedSuccessfully: true } ? load.Result : new LoadedFile( "", new UTF8Encoding( encoderShouldEmitUTF8Identifier: false ), null, null );
		if ( loaded.Error is { } error )
			_error = error;

		var text = loaded.Text;
		var newLine = text.Contains( "\r\n", StringComparison.Ordinal ) ? "\r\n" : text.Contains( '\n' ) ? "\n" : Environment.NewLine;
		var editor = new TextEditorController( new TextDocument( text.Replace( "\r\n", "\n", StringComparison.Ordinal ) ) );
		var file = new OpenFile( editor, loaded.Encoding, newLine, path.Length == 0 ? null : CodeHighlighter.FindForPath( path ) )
		{
			SavedVersion = editor.Document.Version,
			Repository = loaded.Repository,
			Image = loaded.Image,
			IsBinary = loaded.IsBinary,
		};
		if ( path.Length > 0 && load is not null && !loaded.IsBinary )
			RequestMarks( file, path );
		return file;
	}

	/// <summary>Asks the modules that mark lines, such as Git, for the saved file's marks, and shows the first that has any.</summary>
	private void RequestMarks( OpenFile file, string path )
	{
		var sources = _context.App.Extensions.Get<LineMarkSource>( LineMarkSource.Point );
		if ( sources.Count == 0 )
			return;

		var workspace = _context.Workspace;
		_ = Task.Run( async () =>
		{
			foreach ( var source in sources )
			{
				IReadOnlyList<LineMark> marks;
				try
				{
					marks = await source.Compute( workspace, path, _context.Lifetime );
				}
				catch ( Exception error ) when ( error is not OperationCanceledException )
				{
					continue;
				}

				if ( marks.Count > 0 || source == sources[^1] )
				{
					_context.Post( () =>
					{
						file.Marks = marks;
						_version.Value++;
					} );
					return;
				}
			}
		} );
	}

	/// <summary>Builds a slim scrollbar that widens under the pointer, like the chat's.</summary>
	private static Scrollbar BuildScrollbar()
	{
		var theme = Theme.Current;
		return new Scrollbar
		{
			Thickness = 10,
			HitThickness = 14,
			Thumb = new Container
			{
				Width = 6,
				Height = 6,
				BorderRadius = 3,
				BackgroundColor = theme.Muted,
				Opacity = 0.5,
				HoverStyle = new Style { Width = 10, Height = 10, BorderRadius = 5, Opacity = 0.8 },
			},
		};
	}

	/// <summary>Shows an error in the header.</summary>
	private void Fail( string message )
	{
		_error = message;
		_version.Value++;
	}

	/// <summary>Holds what reading a file found.</summary>
	/// <param name="Text">The text, with its own line endings.</param>
	/// <param name="Encoding">The encoding to write it back with.</param>
	/// <param name="Repository">The folder of the nearest repository above it, or null.</param>
	/// <param name="Error">Why it could not be read, or null.</param>
	private sealed record LoadedFile( string Text, Encoding Encoding, string? Repository, string? Error )
	{
		/// <summary>Gets the decoded image of an image file, or null.</summary>
		public ImageSource? Image { get; init; }

		/// <summary>Gets whether the file is not text, an image or not.</summary>
		public bool IsBinary { get; init; }
	}

	/// <summary>Holds one open file: its text, how to write it back, the version last saved, and its colors and line marks.</summary>
	private sealed class OpenFile( TextEditorController editor, Encoding encoding, string newLine, CodeHighlighter.Grammar? grammar ) : IDisposable
	{
		/// <summary>Defines the longest file colored as it is typed, in lines; longer files show plain.</summary>
		private const int MaxColoredLines = 20000;

		/// <summary>Gets the text being edited.</summary>
		internal TextEditorController Editor { get; } = editor;

		/// <summary>Gets the encoding the file was read with.</summary>
		internal Encoding Encoding { get; } = encoding;

		/// <summary>Gets the line ending the file used.</summary>
		internal string NewLine { get; } = newLine;

		/// <summary>Gets or sets the document version last loaded or saved.</summary>
		internal long SavedVersion { get; set; }

		/// <summary>Gets or sets whether the header shows the unsaved mark.</summary>
		internal bool ShownDirty { get; set; }

		/// <summary>Gets or sets whether the file is still being read, which keeps it from being saved.</summary>
		internal bool IsLoading { get; set; }

		/// <summary>Gets the decoded image shown in place of the text, or null.</summary>
		internal ImageSource? Image { get; init; }

		/// <summary>Gets whether the file is not text, which keeps it from being shown as text or saved.</summary>
		internal bool IsBinary { get; init; }

		/// <summary>Gets or sets the nearest folder above the file that holds a git repository, which relative paths start from; null when there is none.</summary>
		internal string? Repository { get; set; }

		/// <summary>Gets whether the text differs from the file.</summary>
		internal bool IsDirty => Editor.Document.Version != SavedVersion;

		/// <summary>Gets whether the text or the marks changed since the colors were last built.</summary>
		internal bool NeedsRefresh => _layerVersion != Editor.Document.Version || _layerTheme != Theme.Current;

		/// <summary>Gets or sets the marks of the saved file, such as git changes; they stay on their lines while the text is edited until the next save.</summary>
		internal IReadOnlyList<LineMark> Marks
		{
			get;
			set
			{
				field = value;
				_layerVersion = -1;
			}
		} = [];

		/// <summary>Holds the colors and marks drawn over the text.</summary>
		private readonly TextPresentationLayer _layer = new( editor.Document );

		/// <summary>Stores the document version the layer was built for, or -1 to rebuild it.</summary>
		private long _layerVersion = -1;

		/// <summary>Stores the theme the layer was built with, so a theme change recolors it.</summary>
		private Theme? _layerTheme;

		/// <summary>Rebuilds the colors and marks when the text, the marks or the theme changed, and gets the layer.</summary>
		internal TextPresentationLayer Refresh()
		{
			var document = Editor.Document;
			if ( _layerVersion == document.Version && _layerTheme == Theme.Current )
				return _layer;

			_layerVersion = document.Version;
			_layerTheme = Theme.Current;
			var lines = document.GetText().Split( '\n' );
			var starts = new int[lines.Length];
			for ( int line = 1, offset = 0; line < lines.Length; line++ )
				starts[line] = offset += lines[line - 1].Length + 1;

			var spans = new List<TextStyleSpan>();
			// Marks first, so a line's tint sits under its token colors.
			var theme = Theme.Current;
			foreach ( var mark in Marks )
			{
				if ( mark.Change == LineChange.Deleted || mark.Line >= lines.Length )
					continue;

				var tint = (mark.Change == LineChange.Added ? theme.Success : theme.Accent).WithAlpha( 0.14 );
				spans.Add( new( $"mark:{mark.Line}", new( starts[mark.Line], lines[mark.Line].Length + 1 ), new Style { BackgroundColor = tint } ) );
			}

			if ( grammar is not null && lines.Length <= MaxColoredLines )
			{
				var isInComment = false;
				for ( var line = 0; line < lines.Length; line++ )
				{
					foreach ( var (start, length, token) in CodeHighlighter.Scan( lines[line], grammar, ref isInComment ) )
					{
						if ( CodeHighlighter.BuildStyle( token ) is { } style )
							spans.Add( new( $"code:{line}:{start}", new( starts[line] + start, length ), style ) );
					}
				}
			}

			_layer.Clear();
			_layer.ReplaceStyles( spans );
			// Removed lines show as a thin bar between the lines around them.
			foreach ( var mark in Marks.Where( mark => mark.Change == LineChange.Deleted && mark.Line <= lines.Length ) )
			{
				var offset = mark.Line < lines.Length ? starts[mark.Line] : document.Length;
				_layer.SetBlockSlot( $"deleted:{mark.Line}", new( offset, 0 ), new Container
				{
					Height = 2,
					Margin = new Edges( 1, 0 ),
					BackgroundColor = theme.Error,
					AccessibilityName = $"{mark.Removed.Count} lines removed",
				} );
			}

			return _layer;
		}

		/// <summary>Releases the editor, its layer and its image.</summary>
		public void Dispose()
		{
			_layer.Dispose();
			Editor.Dispose();
			Image?.Dispose();
		}
	}
}
