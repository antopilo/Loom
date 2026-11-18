using System.Text.Json;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Files;

/// <summary>Switches the side view between the file tree and the modes other modules contribute, keeping each mode's content while others show.</summary>
internal sealed partial class FileTreeContent
{
	/// <summary>Defines the built-in mode that shows the file tree; the tree is this panel's own content, so it creates nothing.</summary>
	internal static readonly FileTreeMode FilesMode = new( FileTreeMode.FilesKey, FolderGlyph, "Files", 0, _ => throw new InvalidOperationException( "The Files mode is the tree itself." ) );

	/// <summary>Stores the content of each mode picked so far, with the contribution that created it, by mode key.</summary>
	private readonly Dictionary<string, (FileTreeMode Mode, PanelContent Content)> _modeContents = new( StringComparer.Ordinal );

	/// <summary>Stores captured mode states not handed to a content yet, by mode key: restored ones until their mode is first picked, and those of contents a module reload replaced.</summary>
	private readonly Dictionary<string, JsonElement> _modeStates = new( StringComparer.Ordinal );

	/// <summary>Stores the key of the selected mode; while its module is not loaded, the tree shows in its place.</summary>
	private string _mode = FileTreeMode.FilesKey;

	/// <summary>Gets the key of the selected mode, which may belong to a module that is not loaded.</summary>
	internal string SelectedModeKey => _mode;

	/// <summary>Lists the modes: Files first, then the contributed ones by order and key; a key already listed is left out.</summary>
	internal IReadOnlyList<FileTreeMode> ListModes()
	{
		var modes = new List<FileTreeMode> { FilesMode };
		var keys = new HashSet<string>( StringComparer.Ordinal ) { FileTreeMode.FilesKey };
		foreach ( var mode in _context.App.Extensions.Get<FileTreeMode>( FileTreeMode.Point ).OrderBy( mode => mode.Order ).ThenBy( mode => mode.Key, StringComparer.Ordinal ) )
		{
			if ( keys.Add( mode.Key ) )
				modes.Add( mode );
		}
		return modes;
	}

	/// <summary>Selects a mode by key; its content is created when it first builds.</summary>
	internal void SelectMode( string key )
	{
		if ( _mode == key )
			return;

		_mode = key;
		_version.Value++;
	}

	/// <summary>Gets a mode's content, creating it the first time and again when its module was reloaded, which hands the old content's state to the new one.</summary>
	internal PanelContent GetModeContent( FileTreeMode mode )
	{
		if ( _modeContents.TryGetValue( mode.Key, out var cached ) )
		{
			if ( ReferenceEquals( cached.Mode, mode ) )
				return cached.Content;

			CloseMode( mode.Key );
		}

		JsonElement? state = _modeStates.Remove( mode.Key, out var saved ) ? saved : null;
		var content = mode.Create( new FileTreeModeContext( _context, this, state ) );
		_modeContents[mode.Key] = (mode, content);
		return content;
	}

	/// <summary>Captures the selected mode and every mode's state, so a restored panel shows the same mode and each mode resumes.</summary>
	public override JsonElement? CaptureState()
	{
		var states = new Dictionary<string, JsonElement>( _modeStates, StringComparer.Ordinal );
		foreach ( var (key, entry) in _modeContents )
		{
			try
			{
				if ( entry.Content.CaptureState() is { } state )
					states[key] = state;
			}
			catch ( Exception error )
			{
				_context.App.ReportError( "Files", error );
			}
		}
		return JsonSerializer.SerializeToElement( new SavedModes( _mode, states ) );
	}

	/// <summary>Lets the selected mode's content handle keys before the global shortcuts.</summary>
	public override bool ClaimsKey( Key key, KeyModifiers modifiers ) =>
		_mode != FileTreeMode.FilesKey && _modeContents.TryGetValue( _mode, out var entry ) && entry.Content.ClaimsKey( key, modifiers );

	/// <summary>Reads the selected mode and the modes' states a previous panel captured.</summary>
	private void RestoreModes( JsonElement? restored )
	{
		if ( restored is not { ValueKind: JsonValueKind.Object } state )
			return;

		try
		{
			if ( state.Deserialize<SavedModes>() is not { } saved )
				return;
			if ( saved.Mode is { Length: > 0 } mode )
				_mode = mode;
			foreach ( var (key, value) in saved.States ?? [] )
				_modeStates[key] = value.Clone();
		}
		catch ( JsonException )
		{
			// A state from before the side view had modes, or a broken one, opens on the tree.
		}
	}

	/// <summary>Finds the selected mode among the listed ones, or the Files mode while its module is not loaded.</summary>
	private FileTreeMode FindSelectedMode( IReadOnlyList<FileTreeMode> modes )
	{
		// A mode whose module unloaded closes, keeping its state for when it returns.
		foreach ( var key in _modeContents.Keys.Where( key => !modes.Any( mode => ReferenceEquals( mode, _modeContents[key].Mode ) ) ).ToArray() )
			CloseMode( key );

		return modes.FirstOrDefault( mode => mode.Key == _mode ) ?? modes[0];
	}

	/// <summary>Builds the row of mode buttons, the selected one raised.</summary>
	private Row BuildModeButtons( IReadOnlyList<FileTreeMode> modes, FileTreeMode selected )
	{
		var theme = Theme.Current;
		return new Row
		{
			Key = "modes",
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Padding = new Edges( 0, theme.Spacing, theme.Spacing / 2, theme.Spacing ),
			BorderColor = theme.Border,
			BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Side view modes",
			Children = [.. modes.Select( mode => BuildModeButton( mode, ReferenceEquals( mode, selected ) ) )],
		};
	}

	/// <summary>Builds one mode's button, raised and named selected while its mode shows.</summary>
	private Button BuildModeButton( FileTreeMode mode, bool isSelected )
	{
		var key = mode.Key;
		var button = BuildIconButton( mode.Glyph, mode.Name, () => SelectMode( key ) );
		button.Key = "mode:" + key;
		button.AccessibilityName = isSelected ? $"{mode.Name}, selected" : mode.Name;
		if ( isSelected )
		{
			button.BackgroundColor = Theme.Current.Hover;
			button.Children = [new Text { Content = mode.Glyph, FontFamily = IconFont, FontSize = 13, Color = Theme.Current.Text }];
		}
		return button;
	}

	/// <summary>Builds a mode's content in its own cell under the header, so its changes rebuild only it; a failing mode shows its error in place.</summary>
	private Container BuildModeBody( FileTreeMode mode, string name )
	{
		PanelContent? content = null;
		string? failure = null;
		try
		{
			content = GetModeContent( mode );
		}
		catch ( Exception error )
		{
			failure = error.Message;
			_context.App.ReportError( "Files", error );
		}

		return new Container
		{
			Key = "mode-body:" + mode.Key,
			FlexGrow = 1,
			MinHeight = 0,
			MinWidth = 0,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = $"{name} {mode.Name}",
			Children = [content is null
				? new Text { Content = $"{mode.Name} could not open: {failure}", Color = Theme.Current.Muted, Padding = Theme.Current.Spacing * 2 }
				: Cell.Mount<ModeSurfaceInput, ModeSurface>( "mode:" + mode.Key, new( content, Root, mode.Name ) )],
		};
	}

	/// <summary>Closes one mode's content, keeping its captured state for the content that replaces it.</summary>
	private void CloseMode( string key )
	{
		if ( !_modeContents.Remove( key, out var entry ) )
			return;

		try
		{
			if ( entry.Content.CaptureState() is { } state )
				_modeStates[key] = state;
			entry.Content.Closed();
		}
		catch ( Exception error )
		{
			_context.App.ReportError( "Files", error );
		}
	}

	/// <summary>Closes every mode's content with the panel.</summary>
	private void CloseModes()
	{
		foreach ( var key in _modeContents.Keys.ToArray() )
			CloseMode( key );
	}

	/// <summary>The side view's captured state: the selected mode and each mode's own state.</summary>
	/// <param name="Mode">The selected mode's key.</param>
	/// <param name="States">Each mode's captured state, by key.</param>
	private sealed record SavedModes( string Mode, Dictionary<string, JsonElement>? States );
}

/// <summary>What a mode's cell builds: the mode's content, and the folder and name it shows, so a new folder rebuilds it.</summary>
/// <param name="Content">The mode's content.</param>
/// <param name="Root">The folder the side view shows.</param>
/// <param name="Name">The mode's name, for a failure.</param>
internal sealed record ModeSurfaceInput( PanelContent Content, string Root, string Name );

/// <summary>Builds a side-view mode's content in a cell of its own, containing its build failures.</summary>
internal sealed class ModeSurface : GuardedCell<ModeSurfaceInput>
{
	/// <summary>Builds the content, or its failure in its place.</summary>
	protected override Blob BuildView( ModeSurfaceInput input )
	{
		try
		{
			return input.Content.Build();
		}
		catch ( Exception error )
		{
			return new Text { Content = $"{input.Name} could not render: {error.Message}", Color = Theme.Current.Muted, Padding = Theme.Current.Spacing * 2 };
		}
	}
}
