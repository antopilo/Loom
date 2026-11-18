using System.Diagnostics;
using System.Text.Json;
using Goo;
using Lokn.Core;
using Microsoft.Web.WebView2.Core;

namespace Lokn.Modules.Browser;

/// <summary>Shows web pages in tabs: a tab strip and an address bar with navigation buttons over the selected tab's WebView2 page placed on the panel.</summary>
/// <remarks>
/// Each page is a native child window over Lokn's window, so it stays hidden while a shell overlay must draw over its area.
/// <see cref="BrowserView"/> keeps the selected tab's page on the panel's page area; the others, and every page while the panel or native surfaces are hidden, stay hidden.
/// </remarks>
internal sealed partial class BrowserContent : PanelContent
{
	/// <summary>Defines the address bar height in logical pixels.</summary>
	private const int BarHeight = 30;

	/// <summary>Defines the widest a tab grows in logical pixels.</summary>
	private const int TabWidth = 180;

	/// <summary>Defines the narrowest a tab shrinks in logical pixels when many are open.</summary>
	private const int TabMinWidth = 56;

	/// <summary>Stores the panel's dispatch and lifetime.</summary>
	private readonly PanelContext _context;

	/// <summary>Stores the module that shares the WebView2 environment.</summary>
	private readonly BrowserModule _module;

	/// <summary>Stores the settings profile whose cookies and history this panel uses, fixed when it opens.</summary>
	private readonly SettingsProfile _profile;

	/// <summary>Retains the address bar for focus.</summary>
	private readonly ElementHandle _addressBar = new();

	/// <summary>Tracks the open tabs in strip order; never empty while the panel is open.</summary>
	private readonly State<IReadOnlyList<BrowserTab>> _tabs;

	/// <summary>Tracks the tab whose page is shown.</summary>
	private readonly State<BrowserTab> _selected;

	/// <summary>Stores the latest placement in Lokn window pixels, or null while hidden.</summary>
	private System.Drawing.Rectangle? _placement;

	/// <summary>Stores Lokn's window and its native handle once the view is mounted.</summary>
	private (Window Window, nint Handle)? _host;

	/// <summary>Creates the panel for a requested address, the restored tabs, or the home page.</summary>
	internal BrowserContent( PanelContext context, BrowserModule module )
	{
		_context = context;
		_module = module;
		var request = context.Input as BrowserRequest;
		var restored = ReadRestored( context.RestoredState );
		_profile = restored?.Profile ?? request?.Profile ?? context.App.Settings.ActiveProfile;
		var search = context.App.Settings.Get( BrowserModule.Search );
		var isEditing = request?.FocusAddress == true;
		BrowserTab[] tabs;
		var selected = 0;
		if ( request?.Address is { } typed && BrowserAddress.Compute( typed, search ) is { } address )
		{
			tabs = [new( this, address, isEditing )];
		}
		else if ( restored is not null )
		{
			tabs = (restored.Tabs?.Where( saved => saved.Length > 0 ).ToArray() is { Length: > 0 } saved ? saved : [restored.Address])
				.Select( saved => new BrowserTab( this, saved, isEditing: false ) )
				.ToArray();
			selected = Math.Clamp( restored.Selected, 0, tabs.Length - 1 );
		}
		else
		{
			tabs = [new( this, ComputeHome(), isEditing )];
		}

		_tabs = new( tabs );
		_selected = new( tabs[selected] );
	}

	/// <summary>Gets the panel's dispatch and lifetime.</summary>
	internal PanelContext Context => _context;

	/// <summary>Gets the module that shares the WebView2 environment.</summary>
	internal BrowserModule Module => _module;

	/// <summary>Gets the settings profile whose cookies and history the panel's pages use; never null for a browser.</summary>
	public override SettingsProfile? Profile => _profile;

	/// <summary>Gets the panel's identifier.</summary>
	internal Guid Id => _context.Id;

	/// <summary>Gets whether the panel is still open.</summary>
	internal bool IsOpen => !_context.Lifetime.IsCancellationRequested;

	/// <summary>Gets the open tabs in strip order.</summary>
	internal IReadOnlyList<BrowserTab> Tabs => _tabs.Value;

	/// <summary>Gets the tab whose page is shown.</summary>
	internal BrowserTab Selected => _selected.Value;

	/// <summary>Builds the tab strip and the address bar over the page area.</summary>
	public override Blob Build()
	{
		var theme = Theme.Current;
		var tab = _selected.Value;
		var navigation = tab.Navigation;
		var page = tab.Failure.Length > 0
			? new Container
			{
				FlexGrow = 1,
				Padding = theme.Spacing * 3,
				AlignItems = AlignItems.Center,
				JustifyContent = JustifyContent.Center,
				Children = [new Text { Content = tab.Failure, Color = theme.Muted, Selectable = true }],
			}
			: Cell.Mount<BrowserViewInput, BrowserView>( "page", new( this, _context.Id ) );
		page.FlexGrow = 1;
		page.MinHeight = 0;
		return new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = tab.Title.Length > 0 ? $"Browser: {tab.Title}" : "Browser",
			FlexGrow = 1,
			MinHeight = 0,
			MinWidth = 0,
			Children =
			[
				BuildTabs( theme ),
				new Row
				{
					Key = "bar",
					Height = BarHeight + theme.Spacing,
					FlexShrink = 0,
					AlignItems = AlignItems.Center,
					Gap = theme.Spacing / 2,
					Padding = new Edges( theme.Spacing / 2, theme.Spacing ),
					Children =
					[
						BuildButton( "\uE72B", "Back", navigation.Back, () => tab.Controller?.CoreWebView2.GoBack() ),
						BuildButton( "\uE72A", "Forward", navigation.Forward, () => tab.Controller?.CoreWebView2.GoForward() ),
						navigation.Loading
							? BuildButton( "\uE711", "Stop", true, () => tab.Controller?.CoreWebView2.Stop() )
							: BuildButton( "\uE72C", "Reload", tab.Controller is not null, () => tab.Controller?.CoreWebView2.Reload() ),
						new TextEntry
						{
							Key = "address",
							Handle = _addressBar,
							Value = tab.Address,
							Controlled = true,
							AutoFocus = tab.IsEditing,
							Placeholder = "Search or enter an address",
							AccessibilityName = "Address",
							InputOptions = new TextInputOptions { Purpose = InputPurpose.Url },
							Height = BarHeight - 4,
							FlexGrow = 1,
							MinWidth = 0,
							Padding = new Edges( 0, theme.Spacing ),
							BackgroundColor = theme.Raised,
							BorderColor = theme.Border,
							BorderWidth = theme.BorderWidth,
							BorderRadius = theme.Radius,
							FocusStyle = new Style { BorderColor = theme.Accent },
							OnFocus = _ => _context.Run( () => _selected.Value.IsEditing = true ),
							OnBlur = _ => _context.Run( () => _selected.Value.EndEditing() ),
							OnChange = text => _context.Run( () => _selected.Value.SetAddress( text ) ),
							OnSubmit = text => _context.Run( () => UpdateNavigate( text ) ),
						},
						BuildButton( "\uE8A7", "Open in default browser", true, () => _context.Run( UpdateOpenExternal ) ),
					],
				},
				page,
			],
		};
	}

	/// <summary>Captures every tab's address, the selected tab, and the profile the panel opened in, so a restart reopens the pages with the same cookies.</summary>
	public override JsonElement? CaptureState()
	{
		var tabs = _tabs.Value;
		var selected = _selected.Value;
		return JsonSerializer.SerializeToElement( new SavedPage( selected.Source, _profile, [.. tabs.Select( tab => tab.Source )], Math.Max( 0, IndexOf( selected ) ) ) );
	}

	/// <summary>Closes every page with the panel.</summary>
	public override void Closed()
	{
		foreach ( var tab in _tabs.Value )
			tab.Close();

		_module.Forget( this );
	}

	/// <summary>Opens an address in a new tab after the others and shows it.</summary>
	internal BrowserTab OpenTab( string address ) => OpenTab( address, isEditing: false );

	/// <summary>Loads an address in one of this panel's tabs and shows it.</summary>
	internal void Navigate( BrowserTab tab, string address )
	{
		tab.Navigate( address );
		UpdateSelect( tab );
	}

	/// <summary>Reports whether the current layout shows a panel; a hidden panel's page must not cover what is shown.</summary>
	internal bool IsShown( Guid panelId ) => _context.App.Panels.IsShown( panelId ) && !_context.Lifetime.IsCancellationRequested;

	/// <summary>Records where the page should show in Lokn's window pixels, or null to hide it, starting the selected tab's page on first use.</summary>
	internal void Place( Window window, nint handle, System.Drawing.Rectangle? placement )
	{
		_host = (window, handle);
		_placement = placement;
		ApplyPlacements();
	}

	/// <summary>Shows one tab's page at the panel's placement when it is the selected tab, and hides it otherwise.</summary>
	internal void ApplyPlacement( BrowserTab tab )
	{
		if ( _host is var (_, handle) )
			tab.Place( handle, tab == _selected.Value ? _placement : null );
	}

	/// <summary>Hands Lokn's shortcuts, such as Ctrl+K and Ctrl+W, back to Lokn while a page has the keyboard; Ctrl+L or F6 leaves the page for the address bar.</summary>
	/// <remarks>Every other key reaches the page, Escape included, so a page can close its own dialogs with it.</remarks>
	internal void UpdateAccelerator( CoreWebView2AcceleratorKeyPressedEventArgs e )
	{
		if ( e.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown) || _host is not var (window, handle) || BrowserKeys.Map( e.VirtualKey ) is not { } key )
			return;

		var modifiers = BrowserKeys.ReadModifiers();
		if ( (key == Key.L && modifiers == KeyModifiers.Primary) || (key == Key.F6 && modifiers == KeyModifiers.None) )
		{
			e.Handled = true;
			BrowserKeys.Focus( handle );
			_addressBar.Focus();
			_addressBar.SelectText( 0, _selected.Value.Address.Length );
			return;
		}

		if ( !modifiers.Ctrl )
			return;

		var isPalette = (key == Key.K && modifiers == KeyModifiers.Primary) || (key == Key.P && modifiers == KeyModifiers.PrimaryShift);
		if ( !isPalette && _context.App.Commands.FindShortcut( key, modifiers ) is null )
			return;

		// Lokn's window takes the keyboard back first, so the palette or the next panel gets typing. The shortcut is replayed on a later pump:
		// this event blocks the page until it returns, so Windows has not finished moving the keyboard yet.
		e.Handled = true;
		BrowserKeys.Focus( handle );
		_context.Post( () =>
		{
			BrowserKeys.Reclaim( handle );
			window.PlatformInput.KeyPress( key, modifiers );
			window.PlatformInput.KeyRelease( key, modifiers );
		} );
	}

	/// <summary>Clears Lokn's own keyboard focus once a page takes the keyboard, so the address bar or another control stops showing focus it no longer has.</summary>
	/// <remarks>The page is a native window inside Lokn's, which stays the foreground window, so Goo is not told it lost the keyboard.</remarks>
	internal void UpdatePageFocus()
	{
		if ( _host is var (window, _) )
			window.Focus.Clear();
	}

	/// <summary>Opens an address in a new tab after the others and shows it, with the address bar empty for typing when <paramref name="isEditing"/> is set.</summary>
	private BrowserTab OpenTab( string address, bool isEditing )
	{
		var tab = new BrowserTab( this, address, isEditing );
		_tabs.Value = [.. _tabs.Value, tab];
		UpdateSelect( tab );
		return tab;
	}

	/// <summary>Shows a tab's page, hiding the one shown before and leaving its address bar edit.</summary>
	internal void UpdateSelect( BrowserTab tab )
	{
		var previous = _selected.Value;
		if ( previous == tab )
			return;

		if ( previous.IsEditing )
			previous.EndEditing();
		_selected.Value = tab;
		ApplyPlacements();
	}

	/// <summary>Opens the home page in a new tab with the address bar focused for typing.</summary>
	private void UpdateNewTab()
	{
		OpenTab( ComputeHome(), isEditing: true );
		_addressBar.Focus();
	}

	/// <summary>Closes a tab, showing its right neighbor, or the left one at the end; closing the last tab closes the panel.</summary>
	private void UpdateCloseTab( BrowserTab tab )
	{
		var tabs = _tabs.Value;
		var index = IndexOf( tab );
		if ( index < 0 )
			return;

		if ( tabs.Count == 1 )
		{
			_context.App.Panels.Close( _context.Id );
			return;
		}

		tab.Close();
		BrowserTab[] rest = [.. tabs.Where( candidate => candidate != tab )];
		_tabs.Value = rest;
		if ( _selected.Value == tab )
		{
			_selected.Value = rest[Math.Min( index, rest.Length - 1 )];
			ApplyPlacements();
		}
	}

	/// <summary>Hides every page but the selected tab's, then shows that one at the panel's placement.</summary>
	private void ApplyPlacements()
	{
		var selected = _selected.Value;
		foreach ( var tab in _tabs.Value )
		{
			if ( tab != selected )
				ApplyPlacement( tab );
		}

		ApplyPlacement( selected );
	}

	/// <summary>Loads what was typed in the selected tab and gives its page the keyboard.</summary>
	private void UpdateNavigate( string text )
	{
		if ( BrowserAddress.Compute( text, _context.App.Settings.Get( BrowserModule.Search ) ) is { } address )
			_selected.Value.Submit( address );
	}

	/// <summary>Opens the selected tab's page in the user's default browser.</summary>
	private void UpdateOpenExternal()
	{
		if ( Uri.TryCreate( _selected.Value.Source, UriKind.Absolute, out var uri ) && uri.Scheme is "http" or "https" )
			Process.Start( new ProcessStartInfo( uri.AbsoluteUri ) { UseShellExecute = true } );
	}

	/// <summary>Returns a tab's position in the strip, or -1 once it was closed.</summary>
	private int IndexOf( BrowserTab tab )
	{
		var tabs = _tabs.Value;
		for ( var index = 0; index < tabs.Count; index++ )
		{
			if ( tabs[index] == tab )
				return index;
		}

		return -1;
	}

	/// <summary>Returns the home page address from settings, or a blank page when it cannot be read.</summary>
	private string ComputeHome()
	{
		var settings = _context.App.Settings;
		return BrowserAddress.Compute( settings.Get( BrowserModule.Home ), settings.Get( BrowserModule.Search ) ) ?? "about:blank";
	}

	/// <summary>Builds the strip of tabs with the button that opens a new one.</summary>
	private Row BuildTabs( Theme theme )
	{
		var tabs = _tabs.Value;
		var selected = _selected.Value;
		var children = new Blob[tabs.Count + 1];
		for ( var index = 0; index < tabs.Count; index++ )
			children[index] = BuildTab( theme, tabs[index], tabs[index] == selected );
		children[tabs.Count] = BuildButton( "\uE710", "New tab", true, UpdateNewTab );
		return new Row
		{
			Key = "tabs",
			Height = BarHeight + theme.Spacing / 2,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Padding = new Edges( theme.Spacing / 2, theme.Spacing, 0 ),
			Overflow = Overflow.Hidden,
			AccessibilityRole = AccessibilityRole.Toolbar,
			AccessibilityName = "Tabs",
			Children = children,
		};
	}

	/// <summary>Builds one tab: its name, which shows it, and a button that closes it; a middle click closes it too.</summary>
	private Row BuildTab( Theme theme, BrowserTab tab, bool isSelected )
	{
		var name = ComputeTabName( tab );
		var select = theme.BuildButton( name, () => _context.Run( () => UpdateSelect( tab ) ) );
		select.FlexGrow = 1;
		select.FlexShrink = 1;
		select.MinWidth = 0;
		select.Height = BarHeight - 4;
		select.Padding = new Edges( 0, theme.Spacing / 2, 0, theme.Spacing );
		// A button stacks its content in a column: center the title vertically, and stretch it across so a long one ends in an ellipsis.
		select.JustifyContent = JustifyContent.Center;
		select.AlignItems = AlignItems.Stretch;
		select.HoverStyle = null;
		select.AccessibilityName = isSelected ? $"{name} (selected tab)" : name;
		select.Children =
		[
			new Text
			{
				Content = name,
				FontSize = theme.FontSize - 1,
				Color = isSelected ? theme.Text : theme.Muted,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
		];
		var close = BuildButton( "\uE711", $"Close {name}", true, () => UpdateCloseTab( tab ) );
		close.Width = BarHeight - 8;
		close.Height = BarHeight - 8;
		return new Row
		{
			Key = $"tab:{tab.Id}",
			Width = TabWidth,
			MinWidth = TabMinWidth,
			FlexShrink = 1,
			Height = BarHeight - 4,
			AlignItems = AlignItems.Center,
			Padding = new Edges( 0, 2, 0, 0 ),
			BorderRadius = theme.Radius,
			BackgroundColor = isSelected ? theme.Raised : theme.Panel,
			HoverStyle = isSelected ? null : new Style { BackgroundColor = theme.Hover },
			OnPointerDown = pointer =>
			{
				if ( pointer.Button == PointerButton.Middle )
					_context.Run( () => UpdateCloseTab( tab ) );
			},
			Children = [select, close],
		};
	}

	/// <summary>Names a tab by its page title, or its host or address before the page names itself.</summary>
	private static string ComputeTabName( BrowserTab tab )
	{
		if ( tab.Title.Length > 0 )
			return tab.Title;

		var source = tab.Source;
		return Uri.TryCreate( source, UriKind.Absolute, out var uri ) && uri.Host.Length > 0 ? uri.Host : source;
	}

	/// <summary>Builds a compact toolbar button showing a Segoe Fluent Icons glyph.</summary>
	private Button BuildButton( string glyph, string name, bool isEnabled, Action action )
	{
		var button = Theme.Current.BuildButton( name, () => _context.Run( action ), isEnabled );
		button.Width = BarHeight;
		button.Height = BarHeight - 4;
		button.Padding = 0;
		button.FlexShrink = 0;
		button.Children =
		[
			new Text
			{
				Content = glyph,
				FontFamily = IconFont.Family,
				FontSize = Theme.Current.FontSize - 1,
			},
		];
		return button;
	}

	/// <summary>Retains the registered Windows icon font for the toolbar.</summary>
	private static readonly FontSource IconFont = BuildIconFont();

	/// <summary>Registers the installed Segoe Fluent Icons face.</summary>
	private static FontSource BuildIconFont()
	{
		var font = FontSource.FromPath( "Lokn Browser Icons", 400, italic: false, Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.Fonts ), "SegoeIcons.ttf" ) );
		font.Register();
		return font;
	}

	/// <summary>Reads the pages saved by <see cref="CaptureState"/>.</summary>
	private static SavedPage? ReadRestored( JsonElement? state )
	{
		if ( state is not { ValueKind: JsonValueKind.Object } element )
			return null;

		try
		{
			return element.Deserialize<SavedPage>() is { Address.Length: > 0 } saved ? saved : null;
		}
		catch ( JsonException )
		{
			return null;
		}
	}

	/// <summary>Holds the pages saved across restarts.</summary>
	/// <param name="Address">The selected tab's address.</param>
	/// <param name="Profile">The settings profile whose cookies the pages use.</param>
	/// <param name="Tabs">Every tab's address in strip order; null in state saved before the panel had tabs.</param>
	/// <param name="Selected">The selected tab's position in <paramref name="Tabs"/>.</param>
	private sealed record SavedPage( string Address, SettingsProfile Profile, string[]? Tabs = null, int Selected = 0 );
}
