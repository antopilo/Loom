using System.Text;
using System.Text.Json;
using Lokn.Core;

namespace Lokn.Modules.Browser;

/// <summary>Gives agents the browser panel: list its tabs, load addresses, read pages, click and type, run scripts and take screenshots.</summary>
/// <remarks>Calls without a tab id act on the selected tab of the browser panel on screen, the one the user sees.</remarks>
public sealed partial class BrowserModule
{
	/// <summary>Defines how long a page may take to start after its tab is shown, in milliseconds.</summary>
	private const int StartTimeoutMs = 15_000;

	/// <summary>Defines how long an explicit navigation may take to start, in milliseconds.</summary>
	private const int NavigationStartMs = 5_000;

	/// <summary>Defines how long a click, key or submit may take to start a navigation before it counts as staying on the page, in milliseconds.</summary>
	private const int ActionNavigationStartMs = 700;

	/// <summary>Defines how many characters of page text one read returns unless asked otherwise.</summary>
	private const int DefaultTextLength = 12_000;

	/// <summary>Defines the most characters of page text one read returns.</summary>
	private const int MaxTextLength = 50_000;

	/// <summary>Defines how many interactive elements one read numbers unless asked otherwise.</summary>
	private const int DefaultElementCount = 200;

	/// <summary>Defines the most characters of a script result returned to the agent.</summary>
	private const int MaxScriptResult = 50_000;

	/// <summary>Defines how many hex digits of an identifier the tools show.</summary>
	private const int ShortIdLength = 8;

	/// <summary>Defines the shortest identifier prefix the tools accept.</summary>
	private const int MinIdLength = 4;

	/// <summary>Defines the optional tab argument every page tool takes.</summary>
	private const string TabArgument = """
		"tab": { "type": "string", "description": "A tab id from browser_tabs, or a unique prefix of one. Defaults to the selected tab of the browser panel on screen." }
		""";

	/// <summary>Gets the per-user folder browser screenshots are saved in.</summary>
	internal static string ScreenshotsPath => Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "Lokn", "screenshots" );

	/// <summary>Registers the tools that let agents use browser panels.</summary>
	private void RegisterPageTools( ModuleBuilder builder ) => builder
		.Tool( "tabs",
			"Lists Lokn's browser panels and their tabs: ids, titles, addresses, which tab each panel shows, and which panel is on screen. The other browser tools act on the selected tab of the panel on screen unless given a tab id.",
			"""{ "type": "object", "properties": {}, "additionalProperties": false }""",
			ListTabs )
		.ToolAsync( "navigate",
			"Loads an address in Lokn's browser panel, which the user sees, or goes back, forward or reloads, and waits for the page to load. With no browser panel on screen it opens one beside your conversation. The page keeps the user's cookies and sign-ins for the profile. Use it to open web pages and local dev servers instead of a shell browser.",
			$$"""
			{
				"type": "object",
				"properties": {
					{{TabArgument}},
					"url": { "type": "string", "description": "An address such as https://example.com, localhost:5173 or a local file path; other text is searched for." },
					"action": { "type": "string", "enum": ["back", "forward", "reload"], "description": "Goes through history or reloads instead of loading a url." },
					"newTab": { "type": "boolean", "description": "Opens the url in a new tab instead of replacing the page." }
				},
				"additionalProperties": false
			}
			""",
			NavigateAsync )
		.ToolAsync( "read",
			"Reads the page in a browser tab: its title and address, its visible text, and its links, buttons and fields, each numbered with a ref for browser_act. Read again after the page changes; refs from an earlier read may no longer exist.",
			$$"""
			{
				"type": "object",
				"properties": {
					{{TabArgument}},
					"offset": { "type": "integer", "minimum": 0, "description": "The character of the page text to start at, to read on past an earlier read." },
					"maxChars": { "type": "integer", "minimum": 1, "maximum": {{MaxTextLength}}, "description": "How many characters of text to return; defaults to {{DefaultTextLength}}." },
					"maxElements": { "type": "integer", "minimum": 0, "maximum": 1000, "description": "How many interactive elements to list; defaults to {{DefaultElementCount}}." }
				},
				"additionalProperties": false
			}
			""",
			ReadAsync )
		.ToolAsync( "act",
			"Uses an element of the page in a browser tab as a person would, with real mouse and keyboard input: click, type into a field, press a key, choose an option of a select box, hover, or scroll. Name the element by a ref from browser_read or by a CSS selector. Waits for any page load the action starts.",
			$$"""
			{
				"type": "object",
				"properties": {
					{{TabArgument}},
					"action": { "type": "string", "enum": ["click", "type", "press", "select", "hover", "scroll"] },
					"ref": { "type": "integer", "description": "The element's ref from the latest browser_read." },
					"selector": { "type": "string", "description": "A CSS selector for the element, instead of a ref." },
					"text": { "type": "string", "description": "For type: the text to type. For select: the option's text or value." },
					"clear": { "type": "boolean", "description": "For type: replaces the field's text; defaults to true. False types at the caret." },
					"submit": { "type": "boolean", "description": "For type: presses Enter afterwards." },
					"key": { "type": "string", "enum": [{{string.Join( ", ", BrowserTab.KeyNames.Select( key => $"\"{key}\"" ) )}}], "description": "For press: the key, pressed in the element when one is named, else where the focus is." },
					"direction": { "type": "string", "enum": ["down", "up"], "description": "For scroll without an element: which way to scroll the page by most of a screen. With an element, scroll brings it into view." }
				},
				"required": ["action"],
				"additionalProperties": false
			}
			""",
			ActAsync )
		.ToolAsync( "eval",
			"Runs JavaScript in the page of a browser tab and returns its value as JSON, awaiting a promise. Use it to extract data or check state that browser_read does not show; prefer browser_act for clicking and typing, which sends real input.",
			$$"""
			{
				"type": "object",
				"properties": {
					{{TabArgument}},
					"script": { "type": "string", "description": "A JavaScript expression, such as document.title or [...document.querySelectorAll('h2')].map(h => h.innerText). For statements, use an async arrow function called at once: (async () => { ...; return value; })()" }
				},
				"required": ["script"],
				"additionalProperties": false
			}
			""",
			EvaluateAsync )
		.ToolAsync( "screenshot",
			"Saves a PNG screenshot of the page in a browser tab and returns its path; read that image file to see the page. Use it to check layout and visuals.",
			$$"""
			{
				"type": "object",
				"properties": {
					{{TabArgument}},
					"fullPage": { "type": "boolean", "description": "Captures the whole page rather than the visible part." }
				},
				"additionalProperties": false
			}
			""",
			ScreenshotAsync );

	/// <summary>Lists every open browser panel's tabs, marking the panel on screen and each panel's selected tab.</summary>
	private AgentToolResult ListTabs( AgentToolContext context, JsonElement arguments )
	{
		var panels = _panels.Where( panel => panel.IsOpen ).ToArray();
		if ( panels.Length == 0 )
			return new( "No browser panel is open. browser_navigate with a url opens one beside your conversation." );

		var shown = FindDefault( context );
		var text = new StringBuilder();
		foreach ( var panel in panels )
		{
			var place = panel == shown ? "on screen, used by default" : IsShown( context.App, panel ) ? "on screen" : "hidden";
			text.AppendLine( $"Browser panel {ComputeShortId( panel.Id )} ({place}, {panel.Profile} profile):" );
			foreach ( var tab in panel.Tabs )
			{
				var marker = tab == panel.Selected ? "*" : " ";
				var state = tab.Navigation.Loading ? " [loading]" : tab.Controller is null ? " [not started]" : "";
				text.AppendLine( $"{marker} tab {ComputeShortId( tab.Id )} \"{tab.Title}\" {tab.Source}{state}" );
			}
		}

		text.Append( "* marks the tab each panel shows." );
		return new( text.ToString() );
	}

	/// <summary>Loads an address in a tab, a new tab or a new browser panel, or moves through a tab's history, and waits for the page.</summary>
	private async Task<AgentToolResult> NavigateAsync( AgentToolContext context, JsonElement arguments )
	{
		var url = ReadString( arguments, "url" );
		var action = ReadString( arguments, "action" );
		if ( (url is null) == (action is null) )
			throw new ArgumentException( "Pass either url or action." );

		var app = context.App;
		var isNewTab = arguments.TryGetProperty( "newTab", out var flag ) && flag.ValueKind == JsonValueKind.True;
		BrowserTab tab;
		string outcome;
		if ( url is not null )
		{
			var address = BrowserAddress.Compute( url, app.Settings.Get( Search ) ) ?? throw new ArgumentException( "The url is blank." );
			if ( ReadString( arguments, "tab" ) is null && FindDefault( context ) is null )
			{
				tab = ShowBeside( app, address, context.Caller, replace: null );
			}
			else if ( isNewTab )
			{
				tab = ResolveTab( context, arguments ).Owner.OpenTab( address );
			}
			else
			{
				tab = ResolveTab( context, arguments );
				if ( tab.Controller is not null )
				{
					outcome = await tab.RunNavigationAsync( () =>
					{
						tab.Owner.Navigate( tab, address );
						return Task.CompletedTask;
					}, NavigationStartMs, context.Cancellation ) ?? "did not start loading";
					return Describe( tab, outcome );
				}

				tab.Owner.Navigate( tab, address );
			}

			await RequirePageAsync( app, tab, context.Cancellation );
			return Describe( tab, tab.FirstLoad.IsCompleted ? tab.LastLoad : "still loading" );
		}

		tab = ResolveTab( context, arguments );
		await RequirePageAsync( app, tab, context.Cancellation );
		var web = tab.Controller!.CoreWebView2;
		Action move = action switch
		{
			"back" when web.CanGoBack => web.GoBack,
			"forward" when web.CanGoForward => web.GoForward,
			"reload" => web.Reload,
			"back" or "forward" => throw new InvalidOperationException( $"The tab has no page to go {action} to." ),
			_ => throw new ArgumentException( $"Unknown action {action}; use back, forward or reload." ),
		};
		outcome = await tab.RunNavigationAsync( () =>
		{
			move();
			return Task.CompletedTask;
		}, NavigationStartMs, context.Cancellation ) ?? "did not start loading";
		return Describe( tab, outcome );
	}

	/// <summary>Reads a tab's text and numbered interactive elements.</summary>
	private async Task<AgentToolResult> ReadAsync( AgentToolContext context, JsonElement arguments )
	{
		var tab = ResolveTab( context, arguments );
		await RequirePageAsync( context.App, tab, context.Cancellation );
		var offset = Math.Max( 0, ReadInteger( arguments, "offset" ) ?? 0 );
		var length = Math.Clamp( ReadInteger( arguments, "maxChars" ) ?? DefaultTextLength, 1, MaxTextLength );
		var limit = Math.Clamp( ReadInteger( arguments, "maxElements" ) ?? DefaultElementCount, 0, 1000 );
		var page = await RunScriptAsync( tab, BrowserScripts.Read, new { offset, max = length, limit }, context.Cancellation );

		var text = new StringBuilder();
		text.AppendLine( $"\"{page.GetProperty( "title" ).GetString()}\" {page.GetProperty( "url" ).GetString()} (tab {ComputeShortId( tab.Id )})" );
		text.AppendLine( $"Scrolled to {page.GetProperty( "scrollY" ).GetInt32()} of {page.GetProperty( "scrollHeight" ).GetInt32()} px; the viewport is {page.GetProperty( "viewportHeight" ).GetInt32()} px tall." );
		var elements = page.GetProperty( "elements" );
		var count = page.GetProperty( "elementCount" ).GetInt32();
		text.AppendLine();
		text.AppendLine( elements.GetArrayLength() < count
			? $"Interactive elements, with refs for browser_act ({elements.GetArrayLength()} of {count}; pass maxElements for more):"
			: $"Interactive elements, with refs for browser_act ({count}):" );
		foreach ( var element in elements.EnumerateArray() )
		{
			text.Append( $"[{element.GetProperty( "ref" ).GetInt32()}] {element.GetProperty( "role" ).GetString()} \"{element.GetProperty( "name" ).GetString()}\"" );
			if ( element.TryGetProperty( "href", out var href ) )
				text.Append( $" -> {href.GetString()}" );
			if ( element.TryGetProperty( "value", out var value ) )
				text.Append( $" = \"{value.GetString()}\"" );
			if ( element.TryGetProperty( "checked", out _ ) )
				text.Append( " (checked)" );
			if ( element.TryGetProperty( "disabled", out _ ) )
				text.Append( " (disabled)" );
			if ( element.TryGetProperty( "offscreen", out _ ) )
				text.Append( " (off screen)" );
			text.AppendLine();
		}

		var total = page.GetProperty( "textLength" ).GetInt32();
		var slice = page.GetProperty( "text" ).GetString() ?? "";
		var end = offset + slice.Length;
		text.AppendLine();
		text.AppendLine( end < total
			? $"Text (characters {offset} to {end} of {total}; pass offset {end} for more):"
			: $"Text (characters {offset} to {end} of {total}):" );
		text.Append( slice );
		return new( text.ToString() );
	}

	/// <summary>Clicks, types, presses a key, chooses an option, hovers or scrolls in a tab, waiting for any page load it starts.</summary>
	private async Task<AgentToolResult> ActAsync( AgentToolContext context, JsonElement arguments )
	{
		var tab = ResolveTab( context, arguments );
		await RequirePageAsync( context.App, tab, context.Cancellation );
		var action = ReadString( arguments, "action" ) ?? throw new ArgumentException( "Pass an action." );
		var reference = ReadInteger( arguments, "ref" );
		var selector = ReadString( arguments, "selector" );
		var hasTarget = reference is not null || selector is not null;
		if ( reference is not null && selector is not null )
			throw new ArgumentException( "Pass either ref or selector, not both." );

		var text = ReadString( arguments, "text" );
		var cancellation = context.Cancellation;
		if ( action == "scroll" && !hasTarget )
		{
			var direction = ReadString( arguments, "direction" ) == "up" ? -1 : 1;
			var position = await RunScriptAsync( tab, BrowserScripts.Scroll, new { direction }, cancellation );
			return new( $"Scrolled {(direction < 0 ? "up" : "down")} to {position.GetProperty( "scrollY" ).GetInt32()} of {position.GetProperty( "scrollHeight" ).GetInt32()} px." );
		}

		if ( action == "press" && !hasTarget )
		{
			var key = ReadString( arguments, "key" ) ?? throw new ArgumentException( "Pass the key to press." );
			var pressed = await tab.RunNavigationAsync( () => tab.PressAsync( key ), ActionNavigationStartMs, cancellation );
			return DescribeAction( tab, $"Pressed {key}.", pressed );
		}

		if ( !hasTarget )
			throw new ArgumentException( $"Pass the ref or selector of the element to {action}." );

		var isTyping = action == "type";
		if ( isTyping && text is null )
			throw new ArgumentException( "Pass the text to type." );
		if ( action == "select" && text is null )
			throw new ArgumentException( "Pass the option's text or value as text." );

		var isClearing = !arguments.TryGetProperty( "clear", out var clear ) || clear.ValueKind != JsonValueKind.False;
		var target = await RunScriptAsync( tab, BrowserScripts.Locate, new
		{
			@ref = reference,
			selector,
			focus = isTyping || action == "press",
			clear = isTyping && isClearing,
			option = action == "select" ? text : null,
		}, cancellation );
		if ( target.TryGetProperty( "error", out var error ) )
			throw new InvalidOperationException( error.GetString() );

		var name = target.GetProperty( "name" ).GetString();
		if ( action == "select" )
			return DescribeAction( tab, $"Chose \"{target.GetProperty( "chosen" ).GetString()}\" in {name}.", null );

		var note = target.TryGetProperty( "covered", out var covered ) && covered.ValueKind == JsonValueKind.String
			? $" Note: {covered.GetString()} covers its center, so the input may have reached that instead."
			: "";
		var x = target.GetProperty( "x" ).GetDouble();
		var y = target.GetProperty( "y" ).GetDouble();
		switch ( action )
		{
			case "click":
				return DescribeAction( tab, $"Clicked {name}.{note}", await tab.RunNavigationAsync( () => tab.ClickAsync( x, y ), ActionNavigationStartMs, cancellation ) );
			case "hover":
				await tab.HoverAsync( x, y );
				return DescribeAction( tab, $"Hovered over {name}.{note}", null );
			case "scroll":
				return DescribeAction( tab, $"Scrolled {name} into view.", null );
			case "press":
				var key = ReadString( arguments, "key" ) ?? throw new ArgumentException( "Pass the key to press." );
				return DescribeAction( tab, $"Pressed {key} in {name}.", await tab.RunNavigationAsync( () => tab.PressAsync( key ), ActionNavigationStartMs, cancellation ) );
			case "type":
				await tab.InsertTextAsync( text! );
				if ( arguments.TryGetProperty( "submit", out var submit ) && submit.ValueKind == JsonValueKind.True )
					return DescribeAction( tab, $"Typed into {name} and pressed Enter.", await tab.RunNavigationAsync( () => tab.PressAsync( "Enter" ), ActionNavigationStartMs, cancellation ) );
				return DescribeAction( tab, $"Typed into {name}.", null );
			default:
				throw new ArgumentException( $"Unknown action {action}; use click, type, press, select, hover or scroll." );
		}
	}

	/// <summary>Runs an agent's script in a tab and returns its value as JSON.</summary>
	private async Task<AgentToolResult> EvaluateAsync( AgentToolContext context, JsonElement arguments )
	{
		var script = ReadString( arguments, "script" ) ?? throw new ArgumentException( "Pass the script to run." );
		var tab = ResolveTab( context, arguments );
		await RequirePageAsync( context.App, tab, context.Cancellation );
		var value = await tab.EvaluateAsync( script, context.Cancellation );
		if ( value is not { } result )
			return new( "undefined" );

		var json = JsonSerializer.Serialize( result, Indented );
		return new( json.Length > MaxScriptResult ? json[..MaxScriptResult] + $"\n… cut at {MaxScriptResult} of {json.Length} characters." : json );
	}

	/// <summary>Saves a screenshot of a tab's page and returns its path.</summary>
	private async Task<AgentToolResult> ScreenshotAsync( AgentToolContext context, JsonElement arguments )
	{
		var tab = ResolveTab( context, arguments );
		await RequirePageAsync( context.App, tab, context.Cancellation );
		var isFullPage = arguments.TryGetProperty( "fullPage", out var full ) && full.ValueKind == JsonValueKind.True;
		var png = await tab.CaptureAsync( isFullPage );
		Directory.CreateDirectory( ScreenshotsPath );
		var path = Path.Combine( ScreenshotsPath, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{ComputeShortId( tab.Id )}.png" );
		await File.WriteAllBytesAsync( path, png, context.Cancellation );
		return new( $"Saved a screenshot of {tab.Source} to {path}. Read that file to see it." );
	}

	/// <summary>Finds the tab an agent named, or the selected tab of its default browser panel.</summary>
	private BrowserTab ResolveTab( AgentToolContext context, JsonElement arguments )
	{
		if ( ReadString( arguments, "tab" ) is not { } text )
			return FindDefault( context )?.Selected ?? throw new InvalidOperationException( "You have no browser panel beside your conversation. Call browser_navigate with a url to open one, or pass a tab id from browser_tabs." );

		var key = text.Trim().Replace( "-", "", StringComparison.Ordinal ).ToLowerInvariant();
		if ( key.Length < MinIdLength )
			throw new ArgumentException( $"Pass at least {MinIdLength} characters of a tab id from browser_tabs." );

		BrowserTab[] matches = [.. _panels.Where( panel => panel.IsOpen ).SelectMany( panel => panel.Tabs ).Where( tab => tab.Id.ToString( "N" ).StartsWith( key, StringComparison.Ordinal ) )];
		return matches switch
		{
			[var tab] => tab,
			[] => throw new KeyNotFoundException( $"No open tab has id {text}; call browser_tabs." ),
			_ => throw new ArgumentException( $"{text} matches several tabs; pass more of its id." ),
		};
	}

	/// <summary>Gets the browser an agent's tools use without a tab id: the one beside its conversation, otherwise, while the conversation is on screen, the browser on screen in its profile.</summary>
	/// <remarks>A conversation working in the background never defaults to the browser the user is looking at, which may belong to another session or profile.</remarks>
	private BrowserContent? FindDefault( AgentToolContext context )
	{
		var app = context.App;
		if ( context.Caller is not { } caller )
			return FindShown( app );

		var source = app.Panels.ResolvePanelId( caller );
		if ( FindOwn( app, source ) is { } own )
			return own;

		return app.Panels.IsShown( source ) ? FindShown( app, app.Panels.GetProfile( source ) ) : null;
	}

	/// <summary>Shows a tab when its panel is on screen and waits until its page runs and finished its first load, or for a while.</summary>
	/// <exception cref="InvalidOperationException">The page cannot run because its panel is hidden, or it failed to start.</exception>
	private async Task RequirePageAsync( LoknApplication app, BrowserTab tab, CancellationToken cancellation )
	{
		var isShown = IsShown( app, tab.Owner );
		if ( isShown )
		{
			// The user sees what the agent works on.
			tab.Owner.UpdateSelect( tab );
			_linkPanel = tab.Owner;
		}

		if ( tab.Controller is null )
		{
			if ( !isShown )
				throw new InvalidOperationException( $"Browser panel {ComputeShortId( tab.Owner.Id )} is not on screen, so its pages do not run. A browser beside a conversation the user is not looking at starts when they open that conversation; do not move it into their view." );

			await Task.WhenAny( tab.Started, Task.Delay( StartTimeoutMs, cancellation ) );
			cancellation.ThrowIfCancellationRequested();
			if ( tab.Failure.Length > 0 )
				throw new InvalidOperationException( tab.Failure );
			if ( tab.Controller is null )
				throw new InvalidOperationException( tab.IsClosed ? "The tab was closed." : "The page did not start in time." );
		}

		// A page that is still loading can be read as it is.
		await Task.WhenAny( tab.FirstLoad, Task.Delay( BrowserTab.LoadTimeoutMs, cancellation ) );
		cancellation.ThrowIfCancellationRequested();
	}

	/// <summary>Runs one of <see cref="BrowserScripts"/> with its argument and returns its value.</summary>
	private static async Task<JsonElement> RunScriptAsync( BrowserTab tab, string script, object argument, CancellationToken cancellation ) =>
		await tab.EvaluateAsync( $"({script})({JsonSerializer.Serialize( argument )})", cancellation )
			?? throw new InvalidOperationException( "The page returned nothing." );

	/// <summary>Describes a tab after a navigation.</summary>
	private static AgentToolResult Describe( BrowserTab tab, string outcome )
	{
		var web = tab.Controller?.CoreWebView2;
		return new( $"{Capitalize( outcome )}: \"{web?.DocumentTitle ?? tab.Title}\" {web?.Source ?? tab.Source} (tab {ComputeShortId( tab.Id )}). Call browser_read to see the page." );
	}

	/// <summary>Describes what an action did, and the page it led to when it navigated.</summary>
	private static AgentToolResult DescribeAction( BrowserTab tab, string done, string? navigation )
	{
		if ( navigation is null )
			return new( done );

		var web = tab.Controller?.CoreWebView2;
		return new( $"{done} The page navigated and {navigation}: \"{web?.DocumentTitle ?? tab.Title}\" {web?.Source ?? tab.Source}. Refs from the earlier browser_read are gone; read the page again." );
	}

	/// <summary>Capitalizes a sentence's first letter.</summary>
	private static string Capitalize( string text ) => text.Length == 0 ? text : char.ToUpperInvariant( text[0] ) + text[1..];

	/// <summary>Shortens an identifier to the hex digits the tools show.</summary>
	private static string ComputeShortId( Guid id ) => id.ToString( "N" )[..ShortIdLength];

	/// <summary>Reads a non-empty string argument, or null.</summary>
	private static string? ReadString( JsonElement arguments, string name ) =>
		arguments.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

	/// <summary>Reads an integer argument, or null.</summary>
	private static int? ReadInteger( JsonElement arguments, string name ) =>
		arguments.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32( out var number ) ? number : null;

	/// <summary>Defines indented JSON for script results.</summary>
	private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
