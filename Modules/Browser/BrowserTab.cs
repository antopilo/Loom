using Goo;
using Lokn.Core;
using Microsoft.Web.WebView2.Core;

namespace Lokn.Modules.Browser;

/// <summary>Holds one tab of a browser panel: its address bar text, title, history state, and its own WebView2 page.</summary>
/// <remarks>The page is created the first time the tab is shown and stays alive, hidden, while another tab is selected.</remarks>
internal sealed partial class BrowserTab
{
	/// <summary>Stores the panel the tab belongs to.</summary>
	private readonly BrowserContent _owner;

	/// <summary>Tracks the address bar's text for this tab, which follows the page until the user edits it.</summary>
	private readonly State<string> _address = new( "" );

	/// <summary>Tracks the page title.</summary>
	private readonly State<string> _title = new( "" );

	/// <summary>Tracks whether history allows going back and forward, and whether a page is loading.</summary>
	private readonly State<(bool Back, bool Forward, bool Loading)> _navigation = new( default );

	/// <summary>Tracks a failure to start the page, shown in its place.</summary>
	private readonly State<string> _failure = new( "" );

	/// <summary>Tracks whether creating the page has started.</summary>
	private bool _isStarting;

	/// <summary>Tracks whether the page reported holding the keyboard, between its focus events.</summary>
	private bool _hasFocus;

	/// <summary>Completes once the page exists, failed to start, or the tab closed.</summary>
	private readonly TaskCompletionSource _started = new( TaskCreationOptions.RunContinuationsAsynchronously );

	/// <summary>Completes once the page finished its first load, failed to start, or the tab closed.</summary>
	private readonly TaskCompletionSource _firstLoad = new( TaskCreationOptions.RunContinuationsAsynchronously );

	/// <summary>Creates a tab that loads an address once shown, with its address bar empty for typing when <paramref name="isEditing"/> is set.</summary>
	internal BrowserTab( BrowserContent owner, string address, bool isEditing )
	{
		_owner = owner;
		Pending = address;
		IsEditing = isEditing;
		_address.Value = isEditing ? "" : address;
	}

	/// <summary>Gets the tab's identity in the strip.</summary>
	internal Guid Id { get; } = Guid.NewGuid();

	/// <summary>Gets the panel the tab belongs to.</summary>
	internal BrowserContent Owner => _owner;

	/// <summary>Gets the address bar text.</summary>
	internal string Address => _address.Value;

	/// <summary>Gets the page title, or an empty string before the page names itself.</summary>
	internal string Title => _title.Value;

	/// <summary>Gets whether history allows going back and forward, and whether a page is loading.</summary>
	internal (bool Back, bool Forward, bool Loading) Navigation => _navigation.Value;

	/// <summary>Gets a failure to start the page, or an empty string.</summary>
	internal string Failure => _failure.Value;

	/// <summary>Gets a task that completes once the page exists, failed to start, or the tab closed.</summary>
	internal Task Started => _started.Task;

	/// <summary>Gets a task that completes once the page finished its first load, failed to start, or the tab closed.</summary>
	internal Task FirstLoad => _firstLoad.Task;

	/// <summary>Gets the page's controller once created.</summary>
	internal CoreWebView2Controller? Controller { get; private set; }

	/// <summary>Gets the address the page loads once it exists, or last loaded from outside.</summary>
	internal string Pending { get; private set; }

	/// <summary>Gets or sets whether the user is editing the address bar for this tab, which stops the page from overwriting it.</summary>
	internal bool IsEditing { get; set; }

	/// <summary>Gets whether the tab was closed.</summary>
	internal bool IsClosed { get; private set; }

	/// <summary>Gets the address the tab shows: the page's own once it loaded, otherwise the pending one.</summary>
	internal string Source => Controller?.CoreWebView2.Source ?? Pending;

	/// <summary>Sets the address bar text while the user types.</summary>
	internal void SetAddress( string text ) => _address.Value = text;

	/// <summary>Shows the page at a placement in Lokn's window pixels, or hides it with null, creating the page the first time it is shown.</summary>
	/// <remarks>Hiding a page that holds the keyboard gives it back to Lokn's window: Windows leaves it on the hidden page, where typing goes nowhere.</remarks>
	internal void Place( nint parent, System.Drawing.Rectangle? placement )
	{
		if ( Controller is { } controller )
		{
			if ( placement is { } bounds )
			{
				controller.Bounds = bounds;
				controller.IsVisible = true;
			}
			else
			{
				controller.IsVisible = false;
				BrowserKeys.Release( parent, _hasFocus );
			}

			return;
		}

		if ( placement is not null && !_isStarting && !IsClosed )
		{
			_isStarting = true;
			_ = StartAsync( parent );
		}
	}

	/// <summary>Loads an address from outside the tab, such as a link followed in a chat.</summary>
	internal void Navigate( string address )
	{
		IsEditing = false;
		_address.Value = address;
		Pending = address;
		Controller?.CoreWebView2.Navigate( address );
	}

	/// <summary>Loads what was typed and gives the page the keyboard.</summary>
	internal void Submit( string address )
	{
		Navigate( address );
		Controller?.MoveFocus( CoreWebView2MoveFocusReason.Programmatic );
	}

	/// <summary>Stops editing the address bar, restoring the page's address when nothing was submitted.</summary>
	internal void EndEditing()
	{
		if ( !IsEditing )
			return;

		IsEditing = false;
		if ( Controller is { } controller )
			_address.Value = controller.CoreWebView2.Source;
	}

	/// <summary>Closes the tab's page, giving the keyboard back to Lokn's window when the page held it.</summary>
	internal void Close()
	{
		IsClosed = true;
		if ( Controller is { } controller )
		{
			var parent = controller.ParentWindow;
			controller.Close();
			Controller = null;
			BrowserKeys.Release( parent, _hasFocus );
		}

		_hasFocus = false;
		_started.TrySetResult();
		_firstLoad.TrySetResult();
	}

	/// <summary>Creates the page in the panel's profile, wires its events, and loads the pending address.</summary>
	private async Task StartAsync( nint parent )
	{
		var context = _owner.Context;
		try
		{
			var environment = await _owner.Module.GetEnvironmentAsync();
			var options = environment.CreateCoreWebView2ControllerOptions();
			options.ProfileName = _owner.Profile.ToString();
			var controller = await environment.CreateCoreWebView2ControllerAsync( parent, options );
			if ( context.Lifetime.IsCancellationRequested || IsClosed )
			{
				controller.Close();
				_started.TrySetResult();
				_firstLoad.TrySetResult();
				return;
			}

			Controller = controller;
			var theme = Theme.Current;
			controller.DefaultBackgroundColor = System.Drawing.Color.FromArgb( (int)(theme.Panel.R * 255), (int)(theme.Panel.G * 255), (int)(theme.Panel.B * 255) );
			controller.AcceleratorKeyPressed += ( _, e ) => context.Run( () => _owner.UpdateAccelerator( e ) );
			controller.GotFocus += ( _, _ ) => context.Run( UpdateGotFocus );
			controller.LostFocus += ( _, _ ) => context.Run( () => _hasFocus = false );
			var web = controller.CoreWebView2;
			web.SourceChanged += ( _, _ ) => context.Run( UpdateSource );
			web.DocumentTitleChanged += ( _, _ ) => context.Run( () => _title.Value = web.DocumentTitle );
			web.HistoryChanged += ( _, _ ) => context.Run( UpdateNavigation );
			web.NavigationStarting += ( _, _ ) => context.Run( () => _navigation.Value = _navigation.Value with { Loading = true } );
			web.NavigationCompleted += ( _, e ) => context.Run( () =>
			{
				UpdateLastLoad( e );
				_firstLoad.TrySetResult();
				UpdateNavigation();
			} );
			web.NewWindowRequested += ( _, e ) => context.Run( () =>
			{
				// Links that open a new window open a new tab in this panel instead.
				e.Handled = true;
				_owner.OpenTab( e.Uri );
			} );
			_owner.ApplyPlacement( this );
			web.Navigate( Pending );
			if ( !IsEditing && _owner.Selected == this )
				controller.MoveFocus( CoreWebView2MoveFocusReason.Programmatic );
			_started.TrySetResult();
		}
		catch ( Exception error ) when ( error is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException )
		{
			_failure.Value = error is WebView2RuntimeNotFoundException
				? "The Microsoft Edge WebView2 runtime is not installed. Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and reopen this panel."
				: $"The browser could not start: {error.Message}";
			_started.TrySetResult();
			_firstLoad.TrySetResult();
		}
	}

	/// <summary>Shows the page's address unless the user is typing one.</summary>
	private void UpdateSource()
	{
		if ( Controller is { } controller && !IsEditing )
			_address.Value = controller.CoreWebView2.Source;
	}

	/// <summary>Reads history and loading state from the page.</summary>
	private void UpdateNavigation()
	{
		if ( Controller?.CoreWebView2 is { } web )
			_navigation.Value = (web.CanGoBack, web.CanGoForward, false);
	}

	/// <summary>Records that the page holds the keyboard and clears Lokn's own focus, which Goo would otherwise keep showing.</summary>
	private void UpdateGotFocus()
	{
		_hasFocus = true;
		_owner.UpdatePageFocus();
	}
}
