using System.Diagnostics;
using System.Text.Json;
using Goo;
using Lokn.Core;
using Microsoft.Web.WebView2.Core;

namespace Lokn.Modules.Browser;

/// <summary>Shows session browser tabs: a tab strip and an address bar with navigation buttons over the selected tab's WebView2 page placed on the panel.</summary>
/// <remarks>
/// The tabs are the session's (<see cref="BrowserTabs"/>), which outlive the panel and the window; the panel shows some of them, each with this
/// window's own page, and saves their ids. Closing a tab closes it in every window. Closing the panel leaves its tabs to the other windows of a daemon
/// or paired host; in the <c>local</c> role, which has no other window, it closes those no other panel shows.
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

	/// <summary>Stores the settings profile whose cookies and history this panel's new tabs use, fixed when it opens.</summary>
	private readonly SettingsProfile _profile;

	/// <summary>Retains the address bar for focus.</summary>
	private readonly ElementHandle _addressBar = new();

	/// <summary>Retains the session tabs button the menu opens under.</summary>
	private readonly ElementHandle _sessionButton = new();

	/// <summary>Tracks the tabs the panel shows, in strip order; empty while none is open or the host has not sent them yet.</summary>
	private readonly State<IReadOnlyList<BrowserTab>> _tabs;

	/// <summary>Tracks the tab whose page is shown, or null when the panel shows none.</summary>
	private readonly SelectedTab _selected = new();

	/// <summary>Tracks whether the session tabs menu is open.</summary>
	private readonly State<bool> _isMenuOpen = new( false );

	/// <summary>Stores the latest placement in Lokn window pixels, or null while hidden.</summary>
	private System.Drawing.Rectangle? _placement;

	/// <summary>Stores Lokn's window and its native handle once the view is mounted.</summary>
	private (Window Window, nint Handle)? _host;

	/// <summary>Creates the panel for a session tab, a requested address, the restored tabs, or the home page.</summary>
	internal BrowserContent( PanelContext context, BrowserModule module )
	{
		_context = context;
		_module = module;
		// The panel's host never changes, so the panel follows the same session from its first tab to its last.
		Host = context.Host;
		Session = Host.BrowserTabs;
		var request = context.Input as BrowserRequest;
		var restored = ReadRestored( context.RestoredState );
		_profile = restored?.Profile ?? request?.Profile ?? context.App.Settings.ActiveProfile;
		var search = context.App.Settings.Get( BrowserModule.Search );
		var isEditing = request?.FocusAddress == true;
		List<BrowserTab> views = [];
		var selected = 0;
		_tabs = new( views );
		if ( request?.TabId is { } shown )
		{
			views.Add( new( this, shown, isEditing: false ) );
		}
		else if ( request?.Address is { } typed && BrowserAddress.Compute( typed, search ) is { } address && Session.CanShow( address ) )
		{
			views.Add( CreateSessionTab( address, isEditing ) );
		}
		else if ( restored is not null )
		{
			views.AddRange( Restore( restored ) );
			selected = Math.Clamp( restored.Selected, 0, Math.Max( 0, views.Count - 1 ) );
		}

		// A panel with nothing to show opens the home page, unless it waits for the host's tabs it was showing.
		if ( views.Count == 0 && (restored is null || Session.IsSynced) )
			views.Add( CreateSessionTab( ComputeHome(), isEditing ) );

		_tabs.Value = views.ToArray();
		_selected.Value = views.Count > 0 ? views[selected] : null;
		Session.TabChanged += UpdateTabChanged;
		Session.Reset += UpdateReset;
	}

	/// <summary>Gets the panel's dispatch and lifetime.</summary>
	internal PanelContext Context => _context;

	/// <summary>Gets the module that shares the WebView2 environment.</summary>
	internal BrowserModule Module => _module;

	/// <summary>Gets the settings profile whose cookies and history the panel's new tabs use; never null for a browser.</summary>
	public override SettingsProfile? Profile => _profile;

	/// <summary>Gets the panel's identifier.</summary>
	internal Guid Id => _context.Id;

	/// <summary>Gets whether the panel is still open.</summary>
	internal bool IsOpen => !_context.Lifetime.IsCancellationRequested;

	/// <summary>Gets the tabs the panel shows, in strip order.</summary>
	internal IReadOnlyList<BrowserTab> Tabs => _tabs.Value;

	/// <summary>Gets the tab whose page is shown, or null when the panel shows none.</summary>
	internal BrowserTab? Selected => _selected.Value;

	/// <summary>Gets the host whose browser tabs the panel shows: this PC's, or a paired host's mirror.</summary>
	internal LoknHost Host
	{
		get;
	}

	/// <summary>Gets the session's tabs: the panel's host's.</summary>
	internal BrowserTabs Session
	{
		get;
	}

	/// <summary>Builds the tab strip and the address bar over the page area.</summary>
	public override Blob Build()
	{
		var theme = Theme.Current;
		var tab = _selected.Value;
		var notice = tab is null ? (Session.IsSynced ? "No tab is open here. Open one with New tab or Session tabs." : "Waiting for the host's browser tabs\u2026")
			: tab.Failure.Length > 0 ? tab.Failure
			: tab.Info is null ? "Waiting for the host's browser tabs\u2026"
			: tab.Blocked.Length > 0 ? tab.Blocked
			: null;
		var page = notice is not null
			? new Container
			{
				FlexGrow = 1,
				Padding = theme.Spacing * 3,
				AlignItems = AlignItems.Center,
				JustifyContent = JustifyContent.Center,
				Children = [new Text { Content = notice, Color = theme.Muted, Selectable = true }],
			}
			: Cell.Mount<BrowserViewInput, BrowserView>( "page", new( this, _context.Id ) );
		page.FlexGrow = 1;
		page.MinHeight = 0;
		return new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = tab?.Title is { Length: > 0 } title ? $"Browser: {title}" : "Browser",
			FlexGrow = 1,
			MinHeight = 0,
			MinWidth = 0,
			// A page that brings its own toolbar, such as the design studio, shows without the address bar; the tab strip stays while there is a tab to switch to.
			Children = tab?.IsImmersive == true && notice is null
				? _tabs.Value.Count > 1 ? [BuildTabs( theme ), page] : [page]
				: [BuildTabs( theme ), BuildBar( theme, tab ), page],
		};
	}

	/// <summary>Builds the navigation buttons and the address bar of the selected tab.</summary>
	private Row BuildBar( Theme theme, BrowserTab? tab )
	{
		var navigation = tab?.Navigation ?? default;
		return new Row
		{
			Key = "bar",
			Height = BarHeight + theme.Spacing,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Padding = new Edges( theme.Spacing / 2, theme.Spacing ),
			Children =
			[
				BuildButton( "\uE72B", "Back", navigation.Back, () => tab?.Controller?.CoreWebView2.GoBack() ),
				BuildButton( "\uE72A", "Forward", navigation.Forward, () => tab?.Controller?.CoreWebView2.GoForward() ),
				navigation.Loading
					? BuildButton( "\uE711", "Stop", true, () => tab?.Controller?.CoreWebView2.Stop() )
					: BuildButton( "\uE72C", "Reload", tab?.Controller is not null, () => tab?.Controller?.CoreWebView2.Reload() ),
				new TextEntry
				{
					Key = "address",
					Handle = _addressBar,
					Value = tab?.Address ?? "",
					Controlled = true,
					AutoFocus = tab?.IsEditing == true,
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
					OnFocus = _ => _context.Run( () => _selected.Value?.BeginEditing() ),
					OnBlur = _ => _context.Run( () => _selected.Value?.EndEditing() ),
					OnChange = text => _context.Run( () => _selected.Value?.SetAddress( text ) ),
					OnSubmit = text => _context.Run( () => UpdateNavigate( text ) ),
				},
				BuildButton( "\uE8A7", "Open in default browser", tab is not null, () => _context.Run( UpdateOpenExternal ) ),
			],
		};
	}

	/// <summary>Captures the ids of the session tabs the panel shows, their addresses, the selected tab, and the profile the panel opened in, so a
	/// reopened window shows the same tabs from the session.</summary>
	public override JsonElement? CaptureState()
	{
		var tabs = _tabs.Value;
		var selected = _selected.Value;
		return JsonSerializer.SerializeToElement( new SavedPage( selected?.Source ?? "", _profile, [.. tabs.Select( tab => tab.Source )], selected is null ? 0 : Math.Max( 0, IndexOf( selected ) ), [.. tabs.Select( tab => tab.Id )] ) );
	}

	/// <summary>Closes every page of the panel. In the <c>local</c> role this window is the only one that shows the session, so tabs no other panel here
	/// shows close with the panel rather than stay where nobody sees them; a mirror's stay for the other windows, and a reload, a restored layout or
	/// shutdown keeps every tab.</summary>
	public override void Closed()
	{
		Session.TabChanged -= UpdateTabChanged;
		Session.Reset -= UpdateReset;
		CoverPages( false );
		foreach ( var tab in _tabs.Value )
			tab.Close();

		_module.Forget( this );
		if ( Session.IsMirror || _module.IsStopping || _context.App.Panels.IsSuspending )
			return;

		foreach ( var tab in _tabs.Value.Where( tab => !_module.IsShown( Host.Id, tab.Id ) ) )
			Session.Close( tab.Id );
	}

	/// <summary>Opens an address in a new session tab after the others and shows it.</summary>
	internal BrowserTab OpenTab( string address ) => OpenTab( address, isEditing: false );

	/// <summary>Shows a session tab in this panel, adding it after the others when the panel does not show it yet, and selects it.</summary>
	internal BrowserTab ShowTab( Guid id )
	{
		if ( _tabs.Value.FirstOrDefault( tab => tab.Id == id ) is not { } view )
		{
			view = new BrowserTab( this, id, isEditing: false );
			_tabs.Value = [.. _tabs.Value, view];
		}

		UpdateSelect( view );
		return view;
	}

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
			tab.Place( handle, tab == _selected.Value && !_isMenuOpen.Value ? _placement : null );
	}

	/// <summary>Hands Lokn's shortcuts, such as Ctrl+K and Ctrl+W, back to Lokn while a page has the keyboard; Ctrl+L or F6 leaves the page for the address bar.</summary>
	/// <remarks>Every other key reaches the page, Escape included, so a page can close its own dialogs with it.</remarks>
	internal void UpdateAccelerator( CoreWebView2AcceleratorKeyPressedEventArgs e )
	{
		if ( e.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown) || _host is not var (window, handle) || BrowserKeys.Map( e.VirtualKey ) is not { } key )
			return;

		var modifiers = BrowserKeys.ReadModifiers();
		if ( ((key == Key.L && modifiers == KeyModifiers.Primary) || (key == Key.F6 && modifiers == KeyModifiers.None)) && _selected.Value?.IsImmersive != true )
		{
			e.Handled = true;
			BrowserKeys.Focus( handle );
			_addressBar.Focus();
			_addressBar.SelectText( 0, _selected.Value?.Address.Length ?? 0 );
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

	/// <summary>Shows a tab's page, hiding the one shown before and leaving its address bar edit.</summary>
	internal void UpdateSelect( BrowserTab tab )
	{
		var previous = _selected.Value;
		if ( previous == tab )
			return;

		previous?.EndEditing();
		_selected.Value = tab;
		ApplyPlacements();
	}

	/// <summary>Creates the view of a new session tab at an address, in this panel's profile; the view loads it itself.</summary>
	private BrowserTab CreateSessionTab( string address, bool isEditing )
	{
		var view = new BrowserTab( this, Guid.NewGuid(), isEditing );
		Session.Open( address, _profile, view.ViewId, view.Id );
		return view;
	}

	/// <summary>Creates the views of restored tabs: by session id, opening a tab again from its saved address when this process owns the session and lost
	/// it; a mirror that has not synced yet keeps every id until the host's tabs arrive. State saved before tabs were the session's opens its addresses.</summary>
	private List<BrowserTab> Restore( SavedPage restored )
	{
		var addresses = restored.Tabs?.Where( saved => saved.Length > 0 && Session.CanShow( saved ) ).ToArray() is { Length: > 0 } saved ? saved
			: restored.Address is { Length: > 0 } address && Session.CanShow( address ) ? [address] : [];
		List<BrowserTab> views = [];
		if ( restored.Ids is not { Length: > 0 } ids )
		{
			views.AddRange( addresses.Select( address => CreateSessionTab( address, isEditing: false ) ) );
			return views;
		}

		for ( var index = 0; index < ids.Length; index++ )
		{
			var id = ids[index];
			if ( Session.Find( id ) is not null || !Session.IsSynced )
			{
				views.Add( new BrowserTab( this, id, isEditing: false ) );
			}
			else if ( !Session.IsMirror && restored.Tabs is { } addressesSaved && index < addressesSaved.Length && addressesSaved[index].Length > 0 )
			{
				var view = new BrowserTab( this, id, isEditing: false );
				Session.Open( addressesSaved[index], _profile, by: null, id );
				views.Add( view );
			}
		}

		return views;
	}

	/// <summary>Opens an address in a new session tab after the others and shows it, with the address bar empty for typing when <paramref name="isEditing"/> is set.</summary>
	/// <exception cref="InvalidOperationException">This PC shows a paired host's tabs and may not load the address, such as a file.</exception>
	private BrowserTab OpenTab( string address, bool isEditing )
	{
		if ( !Session.CanShow( address ) )
			throw new InvalidOperationException( $"This PC shows the host's browser tabs, which open only http and https addresses here, not {address}." );

		var tab = CreateSessionTab( address, isEditing );
		_tabs.Value = [.. _tabs.Value, tab];
		UpdateSelect( tab );
		return tab;
	}

	/// <summary>Follows a session tab this panel shows: its page loads an address another window set, and a tab closed anywhere leaves the panel.</summary>
	private void UpdateTabChanged( Guid id, BrowserTabInfo? info )
	{
		if ( _tabs.Value.FirstOrDefault( tab => tab.Id == id ) is not { } view )
			return;

		if ( info is null )
		{
			RemoveView( view );
			return;
		}

		view.Follow();
		ApplyPlacement( view );
	}

	/// <summary>Follows a snapshot of the host's tabs: tabs it no longer has leave the panel, and every page loads the address the session holds.</summary>
	private void UpdateReset()
	{
		foreach ( var view in _tabs.Value.ToArray() )
		{
			if ( Session.Find( view.Id ) is null )
			{
				if ( Session.IsSynced )
					RemoveView( view );
				continue;
			}

			view.Follow();
			ApplyPlacement( view );
		}

		if ( _tabs.Value.Count > 0 && _selected.Value is null )
			UpdateSelect( _tabs.Value[0] );
	}

	/// <summary>Removes a closed session tab's view, showing its right neighbor, or the left one at the end; the panel closes with its last tab.</summary>
	private void RemoveView( BrowserTab tab )
	{
		var tabs = _tabs.Value;
		var index = IndexOf( tab );
		if ( index < 0 )
			return;

		tab.Close();
		BrowserTab[] rest = [.. tabs.Where( candidate => candidate != tab )];
		_tabs.Value = rest;
		if ( rest.Length == 0 )
		{
			_selected.Value = null;
			_context.Post( () => _context.App.Panels.Close( _context.Id ) );
			return;
		}

		if ( _selected.Value == tab )
		{
			_selected.Value = rest[Math.Min( index, rest.Length - 1 )];
			ApplyPlacements();
		}
	}

	/// <summary>Opens the home page in a new tab with the address bar focused for typing.</summary>
	private void UpdateNewTab()
	{
		OpenTab( ComputeHome(), isEditing: true );
		_addressBar.Focus();
	}

	/// <summary>Closes a tab in the session, and so in every window; closing the panel's last tab closes the panel.</summary>
	private void UpdateCloseTab( BrowserTab tab )
	{
		if ( Session.Find( tab.Id ) is null )
			RemoveView( tab );
		else
			Session.Close( tab.Id );
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

		if ( selected is not null )
			ApplyPlacement( selected );
	}

	/// <summary>Loads what was typed in the selected tab and gives its page the keyboard.</summary>
	private void UpdateNavigate( string text )
	{
		if ( BrowserAddress.Compute( text, _context.App.Settings.Get( BrowserModule.Search ) ) is not { } address )
			return;

		if ( _selected.Value is { } tab )
			tab.Submit( address );
		else
			OpenTab( address );
	}

	/// <summary>Opens the selected tab's page in the user's default browser.</summary>
	private void UpdateOpenExternal()
	{
		if ( Uri.TryCreate( _selected.Value?.Source, UriKind.Absolute, out var uri ) && uri.Scheme is "http" or "https" )
			Process.Start( new ProcessStartInfo( uri.AbsoluteUri ) { UseShellExecute = true } );
	}

	/// <summary>Opens or closes the session tabs menu, hiding the pages meanwhile, since a native page would cover it.</summary>
	private void UpdateMenu( bool isOpen )
	{
		if ( _isMenuOpen.Value == isOpen )
			return;

		_isMenuOpen.Value = isOpen;
		CoverPages( isOpen );
		ApplyPlacements();
	}

	/// <summary>Hides or shows native pages while this panel draws over them.</summary>
	private void CoverPages( bool isCovered ) => _context.App.CoverNativeSurfaces( this, isCovered );

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
		var home = BrowserAddress.Compute( settings.Get( BrowserModule.Home ), settings.Get( BrowserModule.Search ) );
		return home is not null && Session.CanShow( home ) ? home : "about:blank";
	}

	/// <summary>Builds the strip of tabs with the buttons that open a new tab and list the session's other tabs.</summary>
	private Row BuildTabs( Theme theme )
	{
		var tabs = _tabs.Value;
		var selected = _selected.Value;
		var children = new Blob[tabs.Count + 2];
		for ( var index = 0; index < tabs.Count; index++ )
			children[index] = BuildTab( theme, tabs[index], tabs[index] == selected );
		children[tabs.Count] = BuildButton( "\uE710", "New tab", true, UpdateNewTab );
		children[tabs.Count + 1] = BuildSessionTabs();
		return new Row
		{
			Key = "tabs",
			Height = BarHeight + theme.Spacing / 2,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Padding = new Edges( theme.Spacing / 2, theme.Spacing, 0 ),
			OverflowX = Overflow.Scroll,
			AccessibilityRole = AccessibilityRole.Toolbar,
			AccessibilityName = "Tabs",
			Children = children,
		};
	}

	/// <summary>Builds the button whose menu lists the session's tabs this panel does not show, such as those another window opened or a closed panel
	/// left, to show one here or close it in every window.</summary>
	private Button BuildSessionTabs()
	{
		var button = BuildButton( "\uE8FD", "Session tabs", true, () => UpdateMenu( !_isMenuOpen.Value ) );
		button.Handle = _sessionButton;
		if ( !_isMenuOpen.Value )
			return button;

		var shown = _tabs.Value.Select( tab => tab.Id ).ToHashSet();
		var others = Session.Tabs.Where( tab => !shown.Contains( tab.Id ) ).ToArray();
		List<MenuEntry> entries = others.Length == 0
			? [new MenuNote( "Every tab of the session is open here." )]
			:
			[
				.. others.Select( tab => (MenuEntry)new MenuAction( ComputeName( tab.Title, tab.Url ), () => _context.Run( () => ShowTab( tab.Id ) ) ) ),
				new MenuSeparator(),
				.. others.Select( tab => (MenuEntry)new MenuAction( $"Close {ComputeName( tab.Title, tab.Url )}", () => _context.Run( () => Session.Close( tab.Id ) ) )
				{
					IsDestructive = true,
				} ),
			];
		button.Children =
		[
			.. button.Children,
			Cell.Mount<ContextMenuInput, ContextMenu>( "session-tabs", new( _sessionButton, "Session tabs", entries, () => _context.Run( () => UpdateMenu( false ) ) )
			{
				Width = 280,
			} ),
		];
		return button;
	}

	/// <summary>Builds one tab: its name, which shows it, and a button that closes it; a middle click closes it too.</summary>
	private Row BuildTab( Theme theme, BrowserTab tab, bool isSelected )
	{
		var name = ComputeName( tab.Title, tab.Source );
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
	private static string ComputeName( string title, string source )
	{
		if ( title.Length > 0 )
			return title;

		if ( source.Length == 0 )
			return "New tab";

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

	/// <summary>Retains the registered icon font for the toolbar.</summary>
	private static readonly FontSource IconFont = SystemFonts.Register( "Lokn Browser Icons", 400, italic: false, "SegoeIcons.ttf" );

	/// <summary>Reads the tabs saved by <see cref="CaptureState"/>.</summary>
	private static SavedPage? ReadRestored( JsonElement? state )
	{
		if ( state is not { ValueKind: JsonValueKind.Object } element )
			return null;

		try
		{
			return element.Deserialize<SavedPage>() is { } saved && (saved.Address is { Length: > 0 } || saved.Ids is { Length: > 0 }) ? saved : null;
		}
		catch ( JsonException )
		{
			return null;
		}
	}

	/// <summary>Holds the tabs saved across restarts.</summary>
	/// <param name="Address">The selected tab's address.</param>
	/// <param name="Profile">The settings profile whose cookies the panel's new tabs use.</param>
	/// <param name="Tabs">Every tab's address in strip order; null in state saved before the panel had tabs.</param>
	/// <param name="Selected">The selected tab's position in <paramref name="Tabs"/>.</param>
	/// <param name="Ids">Every tab's session id in strip order; null in state saved before tabs were the session's.</param>
	private sealed record SavedPage( string Address, SettingsProfile Profile, string[]? Tabs = null, int Selected = 0, Guid[]? Ids = null );

	/// <summary>Holds the selected tab, which views read to rebuild when it changes; none while the panel shows no tab.</summary>
	private sealed class SelectedTab
	{
		/// <summary>Counts changes, so views reading the tab rebuild.</summary>
		private readonly State<int> _version = new( 0 );

		/// <summary>Stores the tab, or null.</summary>
		private BrowserTab? _tab;

		/// <summary>Gets or sets the selected tab, or null.</summary>
		public BrowserTab? Value
		{
			get
			{
				_ = _version.Value;
				return _tab;
			}
			set
			{
				if ( _tab == value )
					return;

				_tab = value;
				_version.Value++;
			}
		}
	}
}
