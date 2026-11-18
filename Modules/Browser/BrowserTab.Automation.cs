using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Lokn.Modules.Browser;

/// <summary>Drives a tab's page for agents through the Chrome DevTools Protocol: scripts, real pointer and keyboard input, navigation waits and screenshots.</summary>
/// <remarks>Call on the UI thread while the page runs; continuations return to it.</remarks>
internal sealed partial class BrowserTab
{
	/// <summary>Defines how long a navigation may take to finish before a call returns without waiting for it, in milliseconds.</summary>
	internal const int LoadTimeoutMs = 30_000;

	/// <summary>Defines how long a script may run before its call returns an error, in milliseconds.</summary>
	private const int ScriptTimeoutMs = 30_000;

	/// <summary>Defines the tallest full-page screenshot in CSS pixels, so an endless page stays a readable image.</summary>
	private const double MaxCaptureHeight = 16_000;

	/// <summary>Maps the key names agents press to their DOM key and code, Windows virtual key, and the text the key types.</summary>
	private static readonly Dictionary<string, (string Key, string Code, int VirtualKey, string Text)> Keys = new( StringComparer.OrdinalIgnoreCase )
	{
		["Enter"] = ("Enter", "Enter", 13, "\r"),
		["Tab"] = ("Tab", "Tab", 9, ""),
		["Escape"] = ("Escape", "Escape", 27, ""),
		["Backspace"] = ("Backspace", "Backspace", 8, ""),
		["Delete"] = ("Delete", "Delete", 46, ""),
		["Space"] = (" ", "Space", 32, " "),
		["ArrowUp"] = ("ArrowUp", "ArrowUp", 38, ""),
		["ArrowDown"] = ("ArrowDown", "ArrowDown", 40, ""),
		["ArrowLeft"] = ("ArrowLeft", "ArrowLeft", 37, ""),
		["ArrowRight"] = ("ArrowRight", "ArrowRight", 39, ""),
		["Home"] = ("Home", "Home", 36, ""),
		["End"] = ("End", "End", 35, ""),
		["PageUp"] = ("PageUp", "PageUp", 33, ""),
		["PageDown"] = ("PageDown", "PageDown", 34, ""),
	};

	/// <summary>Gets the names of the keys <see cref="PressAsync"/> accepts.</summary>
	internal static IEnumerable<string> KeyNames => Keys.Keys;

	/// <summary>Gets how the latest navigation ended, such as "loaded" or "failed: HostNameNotResolved", or empty before one finished.</summary>
	internal string LastLoad { get; private set; } = "";

	/// <summary>Gets the running page.</summary>
	private CoreWebView2 Page => Controller?.CoreWebView2 ?? throw new InvalidOperationException( "The tab's page is not running." );

	/// <summary>Evaluates a JavaScript expression in the page, awaiting a promise it returns, and returns its value as JSON, or null for undefined.</summary>
	/// <exception cref="InvalidOperationException">The script threw, ran too long, or the page is gone.</exception>
	internal async Task<JsonElement?> EvaluateAsync( string expression, CancellationToken cancellation )
	{
		var call = CallAsync( "Runtime.evaluate", new { expression, awaitPromise = true, returnByValue = true, userGesture = true } );
		var finished = await Task.WhenAny( call, Task.Delay( ScriptTimeoutMs, cancellation ) );
		cancellation.ThrowIfCancellationRequested();
		if ( finished != call )
			throw new InvalidOperationException( $"The script was still running after {ScriptTimeoutMs / 1000} seconds." );

		var response = await call;
		if ( response.TryGetProperty( "exceptionDetails", out var failure ) )
		{
			var message = failure.TryGetProperty( "exception", out var exception ) && exception.TryGetProperty( "description", out var description )
				? description.GetString()
				: failure.GetProperty( "text" ).GetString();
			throw new InvalidOperationException( "The script threw " + message );
		}

		return response.GetProperty( "result" ).TryGetProperty( "value", out var value ) ? value : null;
	}

	/// <summary>Runs an action that may navigate the page, then waits for that navigation to finish.</summary>
	/// <param name="action">Starts what may navigate, such as a click.</param>
	/// <param name="startMs">How long a navigation may take to start before the action counts as not navigating.</param>
	/// <param name="cancellation">Stops waiting.</param>
	/// <returns>How the navigation ended, "still loading" after <see cref="LoadTimeoutMs"/>, or null when none started.</returns>
	internal async Task<string?> RunNavigationAsync( Func<Task> action, int startMs, CancellationToken cancellation )
	{
		var page = Page;
		var started = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
		var completed = new TaskCompletionSource<string>( TaskCreationOptions.RunContinuationsAsynchronously );
		void Start( object? sender, CoreWebView2NavigationStartingEventArgs e ) => started.TrySetResult();
		void Complete( object? sender, CoreWebView2NavigationCompletedEventArgs e ) => completed.TrySetResult( DescribeLoad( e ) );
		page.NavigationStarting += Start;
		page.NavigationCompleted += Complete;
		try
		{
			await action();
			var first = await Task.WhenAny( started.Task, completed.Task, Task.Delay( startMs, cancellation ) );
			cancellation.ThrowIfCancellationRequested();
			if ( first != started.Task && first != completed.Task )
				return null;

			var end = await Task.WhenAny( completed.Task, Task.Delay( LoadTimeoutMs, cancellation ) );
			cancellation.ThrowIfCancellationRequested();
			return end == completed.Task ? await completed.Task : "still loading";
		}
		finally
		{
			// A tab closed meanwhile has no page left to detach from.
			if ( Controller is not null )
			{
				page.NavigationStarting -= Start;
				page.NavigationCompleted -= Complete;
			}
		}
	}

	/// <summary>Clicks with the left mouse button at a point in the page's viewport, in CSS pixels, as a person would.</summary>
	internal async Task ClickAsync( double x, double y )
	{
		await CallAsync( "Input.dispatchMouseEvent", new { type = "mouseMoved", x, y } );
		await CallAsync( "Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", buttons = 1, clickCount = 1 } );
		await CallAsync( "Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", buttons = 0, clickCount = 1 } );
	}

	/// <summary>Moves the mouse to a point in the page's viewport, in CSS pixels.</summary>
	internal Task HoverAsync( double x, double y ) => CallAsync( "Input.dispatchMouseEvent", new { type = "mouseMoved", x, y } );

	/// <summary>Types text into the focused element, replacing its selection, as an input method would.</summary>
	internal Task InsertTextAsync( string text ) => CallAsync( "Input.insertText", new { text } );

	/// <summary>Presses and releases one named key in the focused element.</summary>
	/// <exception cref="ArgumentException">The key is not one of <see cref="KeyNames"/>.</exception>
	internal async Task PressAsync( string name )
	{
		if ( !Keys.TryGetValue( name, out var key ) )
			throw new ArgumentException( $"Unknown key {name}; use one of {string.Join( ", ", Keys.Keys )}." );

		object down = key.Text.Length > 0
			? new { type = "keyDown", key = key.Key, code = key.Code, windowsVirtualKeyCode = key.VirtualKey, nativeVirtualKeyCode = key.VirtualKey, text = key.Text }
			: new { type = "rawKeyDown", key = key.Key, code = key.Code, windowsVirtualKeyCode = key.VirtualKey, nativeVirtualKeyCode = key.VirtualKey };
		await CallAsync( "Input.dispatchKeyEvent", down );
		await CallAsync( "Input.dispatchKeyEvent", new { type = "keyUp", key = key.Key, code = key.Code, windowsVirtualKeyCode = key.VirtualKey, nativeVirtualKeyCode = key.VirtualKey } );
	}

	/// <summary>Captures the page as PNG: the viewport, or the whole page up to <see cref="MaxCaptureHeight"/> CSS pixels tall.</summary>
	internal async Task<byte[]> CaptureAsync( bool isFullPage )
	{
		object parameters = new { format = "png" };
		if ( isFullPage )
		{
			var size = (await CallAsync( "Page.getLayoutMetrics", new { } )).GetProperty( "cssContentSize" );
			var clip = new { x = 0, y = 0, width = size.GetProperty( "width" ).GetDouble(), height = Math.Min( size.GetProperty( "height" ).GetDouble(), MaxCaptureHeight ), scale = 1 };
			parameters = new { format = "png", captureBeyondViewport = true, clip };
		}

		var shot = await CallAsync( "Page.captureScreenshot", parameters );
		return Convert.FromBase64String( shot.GetProperty( "data" ).GetString() ?? "" );
	}

	/// <summary>Describes how a navigation ended for an agent.</summary>
	private static string DescribeLoad( CoreWebView2NavigationCompletedEventArgs e ) => !e.IsSuccess
		? $"failed: {e.WebErrorStatus}"
		: e.HttpStatusCode is 0 or (>= 200 and < 400) ? "loaded" : $"loaded with HTTP {e.HttpStatusCode}";

	/// <summary>Records how a navigation ended.</summary>
	private void UpdateLastLoad( CoreWebView2NavigationCompletedEventArgs e ) => LastLoad = DescribeLoad( e );

	/// <summary>Calls a DevTools Protocol method and returns its result.</summary>
	/// <exception cref="InvalidOperationException">The page refused the call or is gone.</exception>
	private async Task<JsonElement> CallAsync( string method, object parameters )
	{
		string response;
		try
		{
			response = await Page.CallDevToolsProtocolMethodAsync( method, JsonSerializer.Serialize( parameters ) );
		}
		catch ( Exception error ) when ( error is COMException or ArgumentException )
		{
			throw new InvalidOperationException( $"The page refused {method}: {error.Message}" );
		}

		using var document = JsonDocument.Parse( response );
		return document.RootElement.Clone();
	}
}
