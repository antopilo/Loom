using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Lokn.Core;

namespace Lokn;

/// <summary>Applies the theme picked in Settings with the global theme file's tokens over it, and reapplies either on change.</summary>
public sealed class ThemeModule : ILoknModule
{
	/// <summary>Defines the quiet period in milliseconds after a file event before the theme is reread.</summary>
	private const int DebounceMilliseconds = 200;

	/// <summary>Defines the picked built-in theme; profiles can pick their own.</summary>
	internal static readonly Setting<string> Selected = new(
		"theme.name",
		"Theme",
		"Hover to preview, click to use. theme.json can override colors.",
		defaultValue: ThemeCatalog.DefaultName,
		category: "Theme",
		isValid: name => ThemeCatalog.Find( name ) is not null )
	{
		Editor = ThemePicker.Build,
	};

	/// <summary>Stores the watched theme file path.</summary>
	private readonly string _path;

	/// <summary>Coalesces the several file events one editor save raises into a single read.</summary>
	private readonly Timer _debounce;

	/// <summary>Stores the running module context, or null before the first start.</summary>
	private ModuleContext? _context;

	/// <summary>Stores the theme file watcher while the module runs.</summary>
	private FileSystemWatcher? _watcher;

	/// <summary>Stores the theme file's JSON, or null when there is no file.</summary>
	private string? _overrides;

	/// <summary>Stores the theme in effect, which a preview temporarily replaces.</summary>
	private Theme _applied = Theme.Default;

	/// <summary>Stores the theme being previewed, or null.</summary>
	private Theme? _preview;

	/// <summary>Defines how long the pointer rests on a theme card before the theme is previewed, in milliseconds.</summary>
	private const int HoverMilliseconds = 150;

	/// <summary>Holds the pending start or end of a hover preview, or null.</summary>
	private Goo.WindowTimer? _hover;

	/// <summary>Gets the global theme file path, in the data folder.</summary>
	public static string DefaultPath => LoknPaths.Combine( "theme.json" );

	/// <summary>Gets the stable theme module identifier.</summary>
	public string Id => "theme";

	/// <summary>Gets the feature's display name.</summary>
	public string Title => "Theme";

	/// <summary>Keeps the theme loaded: it is part of the shell, which every view draws with.</summary>
	public bool CanUnload => false;

	/// <summary>Creates a theme loader for the per-user theme file; used when the application loads the module by itself.</summary>
	public ThemeModule() : this( DefaultPath )
	{
	}

	/// <summary>Creates a theme loader for one theme file.</summary>
	public ThemeModule( string path )
	{
		_path = Path.GetFullPath( path );
		_debounce = new( _ => PostOverrides(), null, Timeout.Infinite, Timeout.Infinite );
	}

	/// <summary>Registers the theme setting and the command that opens the theme file for editing.</summary>
	public void Register( ModuleBuilder builder )
	{
		builder
			.Setting( Selected )
			.Command( "edit", "Theme: edit theme file", _ => OpenFile() );
	}

	/// <summary>Applies the picked theme and the theme file, then follows the setting and file saves.</summary>
	public void Start( ModuleContext context )
	{
		_context = context;
		var directory = Path.GetDirectoryName( _path )!;
		Directory.CreateDirectory( directory );

		// Apply before returning so the first frame already uses the saved theme.
		if ( ReadOverrides() is var (isRead, json) && isRead )
			_overrides = json;

		UpdateApplied();
		context.App.Settings.Changed += UpdateApplied;
		_watcher = new( directory, Path.GetFileName( _path ) )
		{
			NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
		};
		_watcher.Changed += MarkDirty;
		_watcher.Created += MarkDirty;
		_watcher.Deleted += MarkDirty;
		_watcher.Renamed += MarkDirty;
		_watcher.EnableRaisingEvents = true;
	}

	/// <summary>Stops following changes and restores the built-in theme.</summary>
	public void Stop()
	{
		// The watcher owns a native directory handle and would keep raising events for a disabled feature.
		_watcher?.Dispose();
		_watcher = null;
		if ( _context is { } context )
			context.App.Settings.Changed -= UpdateApplied;

		_hover?.Dispose();
		_hover = null;
		_preview = null;
		_applied = Theme.Default;
		Theme.Current = Theme.Default;
	}

	/// <summary>Shows a theme across the app until the preview ends with null, without changing the setting.</summary>
	internal void Preview( Theme? theme )
	{
		_hover?.Dispose();
		_hover = null;
		_preview = theme;
		UpdateCurrent();
	}

	/// <summary>Previews a hovered theme card once the pointer rests on it, and ends the preview once the pointer leaves, from a window timer,
	/// so a scroll that crosses many cards does not rebuild every view per card or change the theme inside input dispatch.</summary>
	internal void Hover( Theme? theme )
	{
		_hover?.Dispose();
		_hover = _context?.App.Window?.After( theme is null ? 0 : HoverMilliseconds, () => Preview( theme ) );
	}

	/// <summary>Composes the picked theme with the file's tokens; an invalid file is reported and the running theme kept.</summary>
	private void UpdateApplied()
	{
		if ( _context is not { Lifetime.IsCancellationRequested: false } context )
			return;

		var picked = ThemeCatalog.Find( context.App.Settings.Get( Selected ) )?.Theme ?? Theme.Default;
		try
		{
			_applied = _overrides is null ? picked : Theme.Parse( _overrides, picked );
		}
		catch ( JsonException error )
		{
			// Keep the running theme so a half-typed edit does not repaint the window.
			context.App.ReportError( "Theme", error );
			return;
		}

		UpdateCurrent();
	}

	/// <summary>Shows the previewed theme, or the applied one; assigning only on change avoids rebuilding every view.</summary>
	private void UpdateCurrent()
	{
		var theme = _preview ?? _applied;
		if ( Theme.Current != theme )
			Theme.Current = theme;
	}

	/// <summary>Restarts the quiet period after any event on the theme file.</summary>
	private void MarkDirty( object sender, FileSystemEventArgs change ) => _debounce.Change( DebounceMilliseconds, Timeout.Infinite );

	/// <summary>Reads the theme file on the timer thread and applies it on the UI thread.</summary>
	private void PostOverrides()
	{
		if ( _context is not { Lifetime.IsCancellationRequested: false } context )
			return;

		if ( ReadOverrides() is var (isRead, json) && isRead )
		{
			context.Post( () =>
			{
				_overrides = json;
				UpdateApplied();
			} );
		}
	}

	/// <summary>Reads the theme file's JSON, null when it is absent, or reports false while an editor still holds it.</summary>
	private (bool IsRead, string? Json) ReadOverrides()
	{
		try
		{
			return (true, File.Exists( _path ) ? File.ReadAllText( _path ) : null);
		}
		catch ( IOException )
		{
			// The editor still holds the file mid-save; its closing write raises another event.
			_debounce.Change( DebounceMilliseconds, Timeout.Infinite );
			return (false, null);
		}
	}

	/// <summary>Opens the theme file, first writing every token of the current theme commented out when it does not exist.</summary>
	private void OpenFile()
	{
		if ( !File.Exists( _path ) )
			File.WriteAllText( _path, FormatTemplate( _applied ) );

		Process.Start( new ProcessStartInfo( _path )
		{
			UseShellExecute = true,
		} );
	}

	/// <summary>Builds a theme file that overrides nothing but lists every token, so a saved file keeps following the picked theme.</summary>
	internal static string FormatTemplate( Theme theme )
	{
		var template = new StringBuilder( "{\n\t// Tokens set here override the theme picked in Settings. Remove // from a line to set it.\n" );
		foreach ( var line in theme.Format().Split( '\n' ).Select( line => line.Trim() ).Where( line => line.StartsWith( '"' ) ) )
			template.Append( "\t// " ).Append( line ).Append( '\n' );

		return template.Append( "}\n" ).ToString();
	}
}
