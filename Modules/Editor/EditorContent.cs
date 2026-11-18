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
/// Ctrl+Shift+S saves as, Alt+Left and Alt+Right go back and forward. Closing the panel loses unsaved changes.
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

		var editor = new TextEditor
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

		return new Container
		{
			Width = Length.Percent( 100 ),
			Height = Length.Percent( 100 ),
			Children = [header, editor],
		};
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
		var root = hasPath ? FindRepository( Current ) : null;
		var wraps = _context.App.Settings.Get( EditorModule.WrapLines );
		MenuEntry[] entries =
		[
			new MenuAction( "Save", () => Choose( () => _ = SaveAsync( saveAs: false ) ) ) { Shortcut = "Ctrl+S" },
			new MenuAction( "Save as…", () => Choose( () => _ = SaveAsync( saveAs: true ) ) ) { Shortcut = "Ctrl+Shift+S" },
			new MenuSeparator(),
			new MenuAction( wraps ? "✓ Wrap lines" : "Wrap lines", () => Choose( () => _context.App.Settings.Set( EditorModule.WrapLines, SettingsScope.Global, !wraps ) ) ),
			new MenuSeparator(),
			new MenuAction( "Show in Explorer", () => Choose( () => ShowInExplorer( Current ) ) ) { IsEnabled = hasPath },
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
	private static string? FindRepository( string path )
	{
		for ( var directory = Path.GetDirectoryName( path ); directory is not null; directory = Path.GetDirectoryName( directory ) )
		{
			if ( Directory.Exists( Path.Combine( directory, ".git" ) ) || System.IO.File.Exists( Path.Combine( directory, ".git" ) ) )
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

	/// <summary>Saves the shown file, asking for a path for a new file or Save as.</summary>
	private async Task SaveAsync( bool saveAs )
	{
		var path = Current;
		if ( saveAs || path.Length == 0 )
		{
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
		try
		{
			var text = file.Editor.Document.GetText();
			if ( file.NewLine != "\n" )
				text = text.Replace( "\n", file.NewLine, StringComparison.Ordinal );
			var temporary = path + ".lokn-save";
			System.IO.File.WriteAllText( temporary, text, file.Encoding );
			System.IO.File.Move( temporary, path, overwrite: true );
			file.SavedVersion = file.Editor.Document.Version;
			file.ShownDirty = false;
			RequestMarks( file, path );
			if ( !path.Equals( Current, StringComparison.OrdinalIgnoreCase ) )
			{
				// Save as keeps the edits under the new path and goes to it.
				_files.Remove( Current );
				if ( _files.Remove( path, out var replaced ) )
					replaced.Dispose();
				_files[path] = file;
				Navigate( path );
			}
			_error = null;
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException )
		{
			_error = $"Could not save: {error.Message}";
		}

		_version.Value++;
	}

	/// <summary>Reads a file, keeping its encoding and line endings for the next save; a missing path or a new file starts empty.</summary>
	private OpenFile Read( string path )
	{
		var encoding = (Encoding)new UTF8Encoding( encoderShouldEmitUTF8Identifier: false );
		var text = "";
		try
		{
			if ( path.Length > 0 && System.IO.File.Exists( path ) )
			{
				if ( new FileInfo( path ).Length > MaxBytes )
					throw new IOException( $"The file is larger than {MaxBytes / 1024 / 1024} MB." );

				using var reader = new StreamReader( path, encoding, detectEncodingFromByteOrderMarks: true );
				text = reader.ReadToEnd();
				encoding = reader.CurrentEncoding;
			}
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException )
		{
			_error = $"Could not open {Path.GetFileName( path )}: {error.Message}";
		}

		var newLine = text.Contains( "\r\n", StringComparison.Ordinal ) ? "\r\n" : text.Contains( '\n' ) ? "\n" : Environment.NewLine;
		var editor = new TextEditorController( new TextDocument( text.Replace( "\r\n", "\n", StringComparison.Ordinal ) ) );
		var file = new OpenFile( editor, encoding, newLine, path.Length == 0 ? null : CodeHighlighter.FindForPath( path ) ) { SavedVersion = editor.Document.Version };
		if ( path.Length > 0 )
			RequestMarks( file, path );
		return file;
	}

	/// <summary>Asks the modules that mark lines, such as Git, for the saved file's marks, and shows the first that has any.</summary>
	private void RequestMarks( OpenFile file, string path )
	{
		var sources = _context.App.Extensions.Get<LineMarkSource>( LineMarkSource.Point );
		if ( sources.Count == 0 )
			return;

		_ = Task.Run( async () =>
		{
			foreach ( var source in sources )
			{
				IReadOnlyList<LineMark> marks;
				try
				{
					marks = await source.Compute( path, _context.Lifetime );
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

		/// <summary>Releases the editor and its layer.</summary>
		public void Dispose()
		{
			_layer.Dispose();
			Editor.Dispose();
		}
	}
}
