using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Hosts the extensible docking workspace and global shell controls.</summary>
internal sealed partial class ShellView : Cell
{
	/// <summary>Stores the application services owned by this shell.</summary>
	private readonly LoknApplication _app;

	/// <summary>Retains the workspace handle for divider coordinates.</summary>
	private readonly ElementHandle _workspaceHandle = new();

	/// <summary>Retains the element handle of every open panel so shell tools can capture its exact visible rectangle.</summary>
	private readonly Dictionary<Guid, ElementHandle> _panelHandles = [];

	/// <summary>Tracks whether the Search palette is open.</summary>
	private bool _isPaletteOpen;

	/// <summary>Stores the native backdrop value last applied to the window.</summary>
	private string? _appliedBackdrop;

	/// <summary>Defines the bottom status row height in logical pixels.</summary>
	private const int StatusHeight = 26;

	/// <summary>Creates a shell for an explicitly composed application.</summary>
	internal ShellView( LoknApplication app ) => _app = app;

	/// <summary>Whether mounting gives the window the executable's icon and the input guard. Only check windows turn it off: both borrow the native
	/// handle through Goo's dialog owner lease, and a lease taken once the window pumps leaves Goo's simulated pointer input dead for the window.</summary>
	internal bool AppliesNativeWindowHooks { get; init; } = true;

	/// <summary>Builds the window chrome, docking surface, and overlays.</summary>
	public override Blob Build()
	{
		var children = new List<Blob>
		{
			BuildTitleBar(),
			BuildBody(),
		};
		if ( _app.LastError is { } error )
			children.Add( BuildError( error ) );

		if ( _app.Settings.Get( ShellSettings.ShowStatusBar ) )
			children.Add( BuildStatus() );
		if ( BuildPaneMenu() is { } menu )
			children.Add( menu );
		children.AddRange( _app.Popups.Windows.Select( BuildPopup ) );
		if ( _isPaletteOpen )
			children.Add( BuildPalette() );

		return new Container
		{
			Width = Length.Percent( 100 ),
			Height = Length.Percent( 100 ),
			BackgroundColor = Theme.Current.Canvas.WithAlpha( CanvasAlpha ),
			Color = Theme.Current.Text,
			FontFamily = Theme.Current.FontFamily,
			FontSize = Theme.Current.FontSize,
			Children = children,
		};
	}

	/// <summary>Connects model refreshes, global shortcuts, background UI dispatch, and the host window.</summary>
	protected override void Mounted()
	{
		_app.Changed += MarkChanged;
		// Agent tools arrive on transport threads, where reading Cell.Host is not allowed; the window itself queues safely.
		var window = Host;

		// Cell.Post throws while this cell builds, and panels size themselves inside that build; the window's queue has no such limit.
		_app.BindDispatcher( window.Post );
		_app.BindWindow( window );
		UpdateWindowBackdrop();
		_app.Commands.BindDispatcher( operation => window.InvokeAsync( operation ) );
		_app.Tools.BindDispatcher( operation => window.InvokeAsync( operation ) );
		Host.PreviewKeyPressed += UpdateGlobalKey;
		_app.Search.OpenRequested += OpenSearch;
		if ( OperatingSystem.IsWindows() && AppliesNativeWindowHooks )
		{
			_ = WindowIcon.ApplyAsync( window, Lifetime );
			_ = InputGuard.ApplyAsync( window, Lifetime );
		}
	}

	/// <summary>Detaches shell callbacks before shutting down feature-owned lifetimes.</summary>
	protected override void Unmounting()
	{
		_app.Changed -= MarkChanged;
		Host.PreviewKeyPressed -= UpdateGlobalKey;
		_app.Search.OpenRequested -= OpenSearch;
		_app.Shutdown();
	}

	/// <summary>Opens the Search palette when a module asks, such as the sidebar's Search button.</summary>
	private void OpenSearch() => UpdatePalette();

	/// <summary>Refreshes the shell after applying a changed backdrop.</summary>
	private void MarkChanged()
	{
		UpdateWindowBackdrop();
		Rebuild();
	}

	/// <summary>Applies a changed shell backdrop to the open native window once.</summary>
	private void UpdateWindowBackdrop()
	{
		var backdrop = _app.Settings.Get( ShellSettings.Backdrop );
		if ( backdrop == _appliedBackdrop )
			return;

		ShellBackdrop.Apply( Host, backdrop );
		_appliedBackdrop = backdrop;
	}

	/// <summary>Builds one popup window: its panel filling a <see cref="ModalWindow"/> with a close button, or why the panel failed.</summary>
	private Blob BuildPopup( ShellPopup popup )
	{
		Blob content = popup.Content is { } panel
			? new Container { Key = "popup-panel", FlexGrow = 1, MinHeight = 0, Children = [panel.Build()] }
			: new Text { Key = "popup-failure", Content = popup.Failure ?? "", Color = Theme.Current.Error, Margin = Theme.Current.Spacing * 2 };
		return Mount<ModalWindowInput, ModalWindow>( "popup:" + popup.TypeId, new( _app, popup.Title, [content], () => _app.Popups.Close( popup ) )
		{
			Width = popup.Options.Width,
			Height = popup.Options.Height,
			ShowsClose = true,
		} );
	}

	/// <summary>Defines the essential shell command that opens the Settings panel.</summary>
	private const string SettingsCommand = "shell.open.settings";

	/// <summary>Supplies shell actions to the titlebar, and the tabs for its middle in tabs mode.</summary>
	private Blob BuildTitleBar()
	{
		return Mount<TitleBarInput, TitleBar>( "titlebar", new(
			() => _ = _app.Commands.ExecuteAsync( "shell.toggle-sidebar" ),
			() => _ = _app.Commands.ExecuteAsync( SettingsCommand ),
			_app.Slots.Get( ShellSlots.TitleLeft ),
			_app.Slots.Get( ShellSlots.TitleRight ),
			IsTabsShown ? BuildTabStrip() : null ) );
	}

	/// <summary>Gets the window canvas alpha from the backdrop and window opacity settings.</summary>
	private double CanvasAlpha => ShellBackdrop.Alpha( _app.Settings.Get( ShellSettings.Backdrop ), _app.Settings.Get( ShellSettings.CanvasOpacity ) );

	/// <summary>Gets the panel background alpha from the backdrop and panel opacity settings.</summary>
	private double PanelAlpha => ShellBackdrop.Alpha( _app.Settings.Get( ShellSettings.Backdrop ), _app.Settings.Get( ShellSettings.PanelOpacity ) );

	/// <summary>Builds the status row: the shell's counts, the modules' contributions on both sides, then the shell's hint.</summary>
	private Blob BuildStatus()
	{
		return new Row
		{
			Key = "status",
			Height = StatusHeight,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Gap = Theme.Current.Spacing,
			Padding = new Edges( 0, Theme.Current.Spacing ),
			Color = Theme.Current.Muted,
			BorderWidth = new Edges( Theme.Current.BorderWidth, 0, 0, 0 ),
			BorderColor = Theme.Current.Border,
			Children =
			[
				new Text
				{
					Content = $"{_app.Panels.Panels.Count} panels  ·  {_app.Modules.Count} modules",
				},
				.. _app.Slots.Get( ShellSlots.StatusLeft ).Select( BuildStatusSlot ),
				new Spacer(),
				.. _app.Slots.Get( ShellSlots.StatusRight ).Select( BuildStatusSlot ),
				new Text
				{
					Content = "Drag a pane's top grip to dock  ·  Ctrl+K search",
				},
			],
		};
	}

	/// <summary>Builds one module's status bar contribution.</summary>
	private Blob BuildStatusSlot( SlotItem item ) => new Row
	{
		Key = $"status-slot:{item.Id}",
		FlexShrink = 0,
		AlignItems = AlignItems.Center,
		Children = [Mount<ContentSurface>( $"status-surface:{item.Id}", () => new( item ) )],
	};

	/// <summary>Builds a dismissible notice for a contained module failure.</summary>
	private Blob BuildError( string error )
	{
		return new Row
		{
			Key = "error",
			Padding = Theme.Current.Spacing,
			AlignItems = AlignItems.Center,
			BackgroundColor = Theme.Current.Raised,
			Children =
			[
				new Text
				{
					Content = error,
					FlexGrow = 1,
					MinWidth = 0,
				},
				Theme.Current.BuildButton( "Dismiss", _app.ClearError ),
			],
		};
	}

	/// <summary>Routes application shortcuts before focused controls, except keys a focused panel claims, while respecting modal windows.</summary>
	private bool UpdateGlobalKey( Key key, KeyModifiers modifiers )
	{
		var isPaletteKey = key == Key.P && modifiers == KeyModifiers.PrimaryShift;
		if ( !isPaletteKey && !_isPaletteOpen && (_app.Popups.Top is null ? _app.Panels.ClaimsKey( key, modifiers ) : _app.Popups.ClaimsKey( key, modifiers )) )
			return false;

		if ( isPaletteKey || (key == Key.K && modifiers == KeyModifiers.Primary) )
		{
			UpdatePalette();
			return true;
		}

		if ( _isPaletteOpen )
			return UpdatePaletteKey( key, modifiers );

		// A popup is modal: its keys go to its controls, not to commands acting on panels behind it.
		if ( _app.Popups.Top is not null )
			return false;

		if ( _app.Commands.FindShortcut( key, modifiers ) is not { } id )
			return false;

		_ = _app.Commands.ExecuteAsync( id );
		return true;
	}
}
