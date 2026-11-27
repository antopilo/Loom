using System.Runtime.InteropServices;
using System.Text.Json;
using Goo;
using Lokn.Core;
using Microsoft.Web.WebView2.Core;

namespace Lokn.Modules.Browser;

/// <summary>Shows one session tab in a browser panel with this window's own WebView2 page: loads the tab's address, follows navigations other windows
/// report, and reports its own address, title, progress, failure and history back to the session (<see cref="BrowserTabs"/>).</summary>
/// <remarks>
/// The page is created the first time the tab is shown and stays alive, hidden, while another tab is selected. Its address, title and loading state come
/// from the session, so every window shows the same. Which navigations move the tab and when the page follows another view is <see cref="BrowserTabSync"/>'s
/// decision: a load that only follows another window, with its redirects, never moves the tab, and a window still catching up never overwrites the
/// others with an older page. A paired host's session loads only http and https pages here (<see cref="BrowserTabs.CanShow"/>).
/// </remarks>
internal sealed partial class BrowserTab
{
	/// <summary>Stores the panel the tab belongs to.</summary>
	private readonly BrowserContent _owner;

	/// <summary>Decides what the page loads to follow the session and which of its navigations it reports.</summary>
	private readonly BrowserTabSync _sync;

	/// <summary>Tracks the address bar's text while the user edits it.</summary>
	private readonly State<string> _address = new( "" );

	/// <summary>Tracks whether this page's history allows going back and forward, and whether it loads.</summary>
	private readonly State<(bool Back, bool Forward, bool Loading)> _navigation = new( default );

	/// <summary>Tracks a failure to start the page, shown in its place.</summary>
	private readonly State<string> _failure = new( "" );

	/// <summary>Tracks whether the page asks to show without the panel's address bar, with <c>&lt;meta name="loom-chrome" content="none"&gt;</c>.</summary>
	private readonly State<bool> _isImmersive = new( false );

	/// <summary>Tracks the color the page wants around it, as <c>#rrggbb</c>: its <c>theme-color</c>, else its background; empty for none.</summary>
	private readonly State<string> _chromeColor = new( "" );

	/// <summary>Tracks whether creating the page has started.</summary>
	private bool _isStarting;

	/// <summary>Tracks whether the page reported holding the keyboard, between its focus events.</summary>
	private bool _hasFocus;

	/// <summary>Stores the loading progress this page reported last, from 0 to 100.</summary>
	private int _progress;

	/// <summary>Stores how this page's latest navigation failed, or null.</summary>
	private string? _loadFailure;

	/// <summary>Stores the address this page's latest navigation went to, after its redirects.</summary>
	private string _loadAddress = "";

	/// <summary>Stores the id of the navigation the page started last, so the end of one it replaced reports no address.</summary>
	private ulong _navigationId;

	/// <summary>Completes once the page exists, failed to start, or the tab closed.</summary>
	private readonly TaskCompletionSource _started = new( TaskCreationOptions.RunContinuationsAsynchronously );

	/// <summary>Completes once the page finished its first load, failed to start, or the tab closed.</summary>
	private readonly TaskCompletionSource _firstLoad = new( TaskCreationOptions.RunContinuationsAsynchronously );

	/// <summary>Creates the view of a session tab, with its address bar empty for typing when <paramref name="isEditing"/> is set.</summary>
	internal BrowserTab( BrowserContent owner, Guid id, bool isEditing )
	{
		_owner = owner;
		Id = id;
		IsEditing = isEditing;
		_sync = new( owner.Session, id );
	}

	/// <summary>Gets the session tab's identifier.</summary>
	internal Guid Id { get; }

	/// <summary>Gets this view's identity in the session, so the session tells its own address changes from other windows'.</summary>
	internal string ViewId => _sync.ViewId;

	/// <summary>Gets the panel the tab belongs to.</summary>
	internal BrowserContent Owner => _owner;

	/// <summary>Gets the session tab, or null while a mirror has not received it yet.</summary>
	internal BrowserTabInfo? Info => _sync.Info;

	/// <summary>Gets the address bar text: what the user types while editing, else the session's address.</summary>
	internal string Address => IsEditing ? _address.Value : Source;

	/// <summary>Gets the page title, or an empty string before the page names itself.</summary>
	internal string Title => Info?.Title ?? "";

	/// <summary>Gets whether history allows going back and forward, and whether a page is loading: this page's own once it runs, else the session's.</summary>
	internal (bool Back, bool Forward, bool Loading) Navigation => Controller is null && Info is { } info ? (info.CanGoBack, info.CanGoForward, info.Loading) : _navigation.Value;

	/// <summary>Gets a failure to start the page, or an empty string.</summary>
	internal string Failure => _failure.Value;

	/// <summary>Gets whether the page asks to show without the panel's address bar, such as a tool's own page that brings its own toolbar.</summary>
	internal bool IsImmersive => _isImmersive.Value;

	/// <summary>Gets the color the page wants around it, as <c>#rrggbb</c>, or an empty string for the panel's own.</summary>
	internal string ChromeColor => _chromeColor.Value;

	/// <summary>Gets the name a page made by or kept for Loom shows in the address bar instead of its address, or null for any other page: a file in
	/// Loom's data or temporary folder, such as a report an agent showed, a file in a project's <c>.lokn</c> folder, or a page served on this PC
	/// (<c>localhost</c>). The name is the page's title, else its file name without the date a report's file starts with, else the server's address.</summary>
	internal string? LoomName
	{
		get
		{
			if ( !Uri.TryCreate( Source, UriKind.Absolute, out var uri ) )
				return null;

			if ( uri.IsFile )
			{
				var path = uri.LocalPath;
				if ( !IsInside( path, LoknPaths.DataRoot ) && !IsInside( path, LoknPaths.TempRoot ) && !path.Contains( $"{Path.DirectorySeparatorChar}.lokn{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase ) )
					return null;

				if ( Title.Length > 0 && !Title.Equals( Path.GetFileName( path ), StringComparison.OrdinalIgnoreCase ) )
					return Title;

				var name = Path.GetFileNameWithoutExtension( path );
				// Reports are saved as yyyyMMdd-HHmmss-name.html.
				return name.Length > 16 && name[8] == '-' && name[15] == '-' && long.TryParse( name.AsSpan( 0, 8 ), out _ ) ? name[16..] : name;
			}

			if ( uri.Scheme is "http" or "https" && (uri.IsLoopback || uri.Host.Equals( "localhost", StringComparison.OrdinalIgnoreCase )) )
				return Title.Length > 0 ? Title : uri.Authority;

			return null;
		}
	}

	/// <summary>Returns whether a path is inside a folder.</summary>
	private static bool IsInside( string path, string folder ) =>
		path.StartsWith( Path.TrimEndingDirectorySeparator( folder ) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase );

	/// <summary>Gets why this PC does not show the tab's address, such as a file on the paired host, or an empty string when it does.</summary>
	internal string Blocked => Info is { } info && !Tabs.CanShow( info.Url )
		? $"This tab shows {info.Url}, which is on the host's PC. This PC shows only the web pages (http and https) of the host's browser tabs."
		: "";

	/// <summary>Gets a task that completes once the page exists, failed to start, or the tab closed.</summary>
	internal Task Started => _started.Task;

	/// <summary>Gets a task that completes once the page finished its first load, failed to start, or the tab closed.</summary>
	internal Task FirstLoad => _firstLoad.Task;

	/// <summary>Gets the page's controller once created.</summary>
	internal CoreWebView2Controller? Controller { get; private set; }

	/// <summary>Gets or sets whether the user is editing the address bar for this tab, which stops the session from overwriting it.</summary>
	internal bool IsEditing { get; set; }

	/// <summary>Gets whether the tab was closed in this panel.</summary>
	internal bool IsClosed { get; private set; }

	/// <summary>Gets the session's address of the tab, or an empty string while it is not known yet.</summary>
	internal string Source => Info?.Url ?? "";

	/// <summary>Gets the address this page shows, or the session's address before the page runs.</summary>
	internal string PageSource => Controller?.CoreWebView2 is { } web ? web.Source : Source;

	/// <summary>Gets the session's tabs: the panel's host's.</summary>
	private BrowserTabs Tabs => _owner.Session;

	/// <summary>Sets the address bar text while the user types.</summary>
	internal void SetAddress( string text ) => _address.Value = text;

	/// <summary>Starts editing the address bar with the current address.</summary>
	internal void BeginEditing()
	{
		if ( IsEditing )
			return;

		_address.Value = Source;
		IsEditing = true;
	}

	/// <summary>Shows the page at a placement in Lokn's window pixels, or hides it with null, creating the page the first time it is shown once the session has the tab.</summary>
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

		if ( placement is not null && !_isStarting && !IsClosed && Info is not null )
		{
			_isStarting = true;
			_ = StartAsync( parent );
		}
	}

	/// <summary>Sends the tab to an address, such as one typed or an agent's, which this page loads and every other window follows.</summary>
	/// <exception cref="InvalidOperationException">This PC shows a paired host's tabs and may not load the address, such as a file.</exception>
	internal void Navigate( string address )
	{
		if ( !Tabs.CanShow( address ) )
			throw new InvalidOperationException( $"This PC shows the host's browser tabs, which open only http and https addresses here, not {address}." );

		IsEditing = false;
		_sync.Navigate( address );
		Controller?.CoreWebView2.Navigate( address );
	}

	/// <summary>Loads what was typed and gives the page the keyboard.</summary>
	internal void Submit( string address )
	{
		Navigate( address );
		Controller?.MoveFocus( CoreWebView2MoveFocusReason.Programmatic );
	}

	/// <summary>Stops editing the address bar, which shows the session's address, or a Loom page's name, again; clearing the typed text redraws the bar.</summary>
	internal void EndEditing()
	{
		IsEditing = false;
		_address.Value = "";
	}

	/// <summary>Loads the session's address when another window set it and this page shows another; called after the session tab changed.</summary>
	internal void Follow()
	{
		if ( Controller?.CoreWebView2 is { } web && _sync.ComputeLoad( web.Source, isStarting: false ) is { } address )
			web.Navigate( address );
	}

	/// <summary>Closes the tab's page in this panel, giving the keyboard back to Lokn's window when the page held it; the session tab stays.</summary>
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

	/// <summary>Creates the page in the tab's profile, wires its events, and loads the session's address.</summary>
	private async Task StartAsync( nint parent )
	{
		var context = _owner.Context;
		try
		{
			var environment = await _owner.Module.GetEnvironmentAsync( _owner.Host );
			var options = environment.CreateCoreWebView2ControllerOptions();
			options.ProfileName = (Info?.Profile ?? _owner.Profile).ToString();
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
			web.SourceChanged += ( _, e ) => context.Run( () => UpdateSource( web, e.IsNewDocument ) );
			web.DocumentTitleChanged += ( _, _ ) => context.Run( () => Report( BrowserPageEvent.State ) );
			web.HistoryChanged += ( _, _ ) => context.Run( () => UpdateNavigation( BrowserPageEvent.State ) );
			web.NavigationStarting += ( _, e ) => context.Run( () => UpdateStarting( e ) );
			web.ContentLoading += ( _, _ ) => context.Run( () => UpdateProgress( 50 ) );
			web.DOMContentLoaded += ( _, _ ) => context.Run( () =>
			{
				UpdateProgress( 80 );
				_ = UpdateImmersiveAsync( web );
			} );
			web.NavigationCompleted += ( _, e ) => context.Run( () => UpdateCompleted( web, e ) );
			web.WebMessageReceived += ( _, e ) => context.Run( () => UpdateMessage( web, e.WebMessageAsJson ) );
			await web.AddScriptToExecuteOnDocumentCreatedAsync( BrowserScripts.ChromeColor );
			if ( context.Lifetime.IsCancellationRequested || IsClosed )
			{
				_started.TrySetResult();
				_firstLoad.TrySetResult();
				return;
			}

			web.NewWindowRequested += ( _, e ) => context.Run( () =>
			{
				// Links that open a new window open a new tab in this panel instead.
				e.Handled = true;
				if ( Tabs.CanShow( e.Uri ) )
					_owner.OpenTab( e.Uri );
			} );
			_owner.ApplyPlacement( this );
			web.Navigate( _sync.ComputeLoad( "", isStarting: true ) ?? "about:blank" );
			if ( !IsEditing && _owner.Selected == this )
				controller.MoveFocus( CoreWebView2MoveFocusReason.Programmatic );
			_started.TrySetResult();
		}
		catch ( Exception error ) when ( error is WebView2RuntimeNotFoundException or COMException or UnauthorizedAccessException or ArgumentException or InvalidOperationException )
		{
			_failure.Value = error is WebView2RuntimeNotFoundException
				? "The Microsoft Edge WebView2 runtime is not installed. Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and reopen this panel."
				: $"The browser could not start: {error.Message}";
			_started.TrySetResult();
			_firstLoad.TrySetResult();
		}
	}

	/// <summary>Marks the page loading and records whose navigation it is; an address this PC may not load is refused.</summary>
	private void UpdateStarting( CoreWebView2NavigationStartingEventArgs e )
	{
		if ( !Tabs.CanShow( e.Uri ) )
		{
			e.Cancel = true;
			return;
		}

		_sync.BeginNavigation( e.Uri, e.IsRedirected, e.IsUserInitiated );
		_navigationId = e.NavigationId;
		_loadAddress = e.Uri;
		_loadFailure = null;
		_progress = 10;
		_navigation.Value = _navigation.Value with { Loading = true };
		Report( BrowserPageEvent.State );
	}

	/// <summary>Records loading progress and reports it.</summary>
	private void UpdateProgress( int progress )
	{
		_progress = progress;
		Report( BrowserPageEvent.State );
	}

	/// <summary>Records how the page's current navigation ended and reports the page, with its address: a navigation of the view's own that never replaced
	/// the page, such as a download or a 204, leaves the tab on the page it still shows rather than on the address that did not load. The end of a
	/// navigation another replaced reports nothing.</summary>
	private void UpdateCompleted( CoreWebView2 web, CoreWebView2NavigationCompletedEventArgs e )
	{
		_firstLoad.TrySetResult();
		if ( e.NavigationId != _navigationId )
			return;

		UpdateLastLoad( e );
		_loadFailure = e.IsSuccess || web.Source != _loadAddress ? null : LastLoad;
		_progress = 100;
		UpdateNavigation( BrowserPageEvent.Complete );
	}

	/// <summary>Reports an address change: at once for a new document or within the view's own page, otherwise once the page says whether the user
	/// activated it, which makes a script's history entry the user's doing.</summary>
	private void UpdateSource( CoreWebView2 web, bool isNewDocument )
	{
		if ( isNewDocument || _sync.IsOwnLoad )
		{
			Report( BrowserPageEvent.Commit );
			return;
		}

		_ = UpdateActivatedSourceAsync( web );
	}

	/// <summary>Asks a followed page whether the user activated it, taking its address changes over when so, then reports it.</summary>
	private async Task UpdateActivatedSourceAsync( CoreWebView2 web )
	{
		string activated;
		try
		{
			activated = await web.ExecuteScriptAsync( "navigator.userActivation.isActive" );
		}
		catch ( Exception error ) when ( error is COMException or InvalidOperationException )
		{
			// The page closed or navigated away meanwhile.
			return;
		}

		_owner.Context.Run( () =>
		{
			if ( Controller?.CoreWebView2 != web )
				return;

			if ( activated == "true" )
				_sync.TakeOver();
			Report( BrowserPageEvent.Commit );
		} );
	}

	/// <summary>Reads whether the loaded page asks to show without the address bar; a page that cannot be asked keeps the bar.</summary>
	private async Task UpdateImmersiveAsync( CoreWebView2 web )
	{
		string asked;
		try
		{
			asked = await web.ExecuteScriptAsync( "document.querySelector( 'meta[name=\"loom-chrome\"]' )?.content === 'none'" );
		}
		catch ( Exception error ) when ( error is COMException or InvalidOperationException )
		{
			// The page closed or navigated away meanwhile.
			return;
		}

		_owner.Context.Run( () =>
		{
			if ( Controller?.CoreWebView2 == web )
				_isImmersive.Value = asked == "true";
		} );
	}

	/// <summary>Takes a message a page sent Loom: the color it asks for around it (<see cref="BrowserScripts.ChromeColor"/>), or the comments a user wrote
	/// on a report, which go to the chat that made it, with the outcome sent back to the page as <c>{ "loomCommentsSent": true }</c> or
	/// <c>{ "loomCommentsSent": false, "error": "…" }</c>. Any other message is ignored.</summary>
	private void UpdateMessage( CoreWebView2 web, string json )
	{
		if ( Controller?.CoreWebView2 != web )
			return;

		try
		{
			using var message = JsonDocument.Parse( json );
			var root = message.RootElement;
			if ( root.ValueKind != JsonValueKind.Object )
				return;

			if ( root.TryGetProperty( "loomChrome", out var color ) && color.ValueKind == JsonValueKind.String && color.GetString() is { } value
				&& (value.Length == 0 || IsHexColor( value )) )
				_chromeColor.Value = value;

			if ( root.TryGetProperty( "loomComments", out var list ) && list.ValueKind == JsonValueKind.Array )
				UpdateComments( web, list );
		}
		catch ( JsonException )
		{
			// Not a message of Loom's.
		}
	}

	/// <summary>Sends the comments a report page sent, each <c>{ "about": "…", "comment": "…" }</c>, to the chat that made the report, and tells the page how it went.</summary>
	private void UpdateComments( CoreWebView2 web, JsonElement list )
	{
		const int MaxComments = 50, MaxLength = 2000;
		static string Clip( string text ) => text.Length > MaxLength ? text[..MaxLength] + "\u2026" : text;
		var comments = list.EnumerateArray()
			.Where( item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty( "comment", out var comment ) && comment.ValueKind == JsonValueKind.String )
			.Select( item => (About: Clip( item.TryGetProperty( "about", out var about ) && about.ValueKind == JsonValueKind.String ? about.GetString()!.Trim() : "the report" ),
				Comment: Clip( item.GetProperty( "comment" ).GetString()!.Trim() )) )
			.Where( item => item.Comment.Length > 0 )
			.Take( MaxComments )
			.ToArray();
		var error = comments.Length == 0 ? "Write a comment first."
			: !Uri.TryCreate( web.Source, UriKind.Absolute, out var uri ) || !uri.IsFile ? "Only a report a chat made can take comments."
			: _owner.Module.SendComments( _owner.Context.App, uri.LocalPath, comments );
		web.PostWebMessageAsJson( JsonSerializer.Serialize( new { loomCommentsSent = error is null, error } ) );
	}

	/// <summary>Returns whether a value is a color as <c>#rrggbb</c>.</summary>
	private static bool IsHexColor( string value ) => value.Length == 7 && value[0] == '#' && !value.AsSpan( 1 ).ContainsAnyExcept( "0123456789abcdefABCDEF" );

	/// <summary>Reads history and loading state from the page and reports it.</summary>
	private void UpdateNavigation( BrowserPageEvent happened )
	{
		if ( Controller?.CoreWebView2 is not { } web )
			return;

		_navigation.Value = (web.CanGoBack, web.CanGoForward, _progress < 100 && _navigation.Value.Loading);
		Report( happened );
	}

	/// <summary>Reports the page's state to the session (<see cref="BrowserTabSync.Report"/>).</summary>
	private void Report( BrowserPageEvent happened )
	{
		if ( Controller?.CoreWebView2 is not { } web )
			return;

		var navigation = _navigation.Value;
		_sync.Report( new( web.Source, web.DocumentTitle, navigation.Loading, _progress, _loadFailure, web.CanGoBack, web.CanGoForward ), happened );
	}

	/// <summary>Records that the page holds the keyboard and clears Lokn's own focus, which Goo would otherwise keep showing.</summary>
	private void UpdateGotFocus()
	{
		_hasFocus = true;
		_owner.UpdatePageFocus();
	}
}
