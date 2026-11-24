using System.Text.Json;
using System.Text.RegularExpressions;
using Goo;
using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Conversations;

namespace Lokn.Modules.Design;

/// <summary>Lets agents design user interfaces as HTML and CSS that the user previews, compares and comments on in a studio page in Loom's browser.</summary>
/// <remarks>
/// Designs are files in the chat's project (<c>.lokn/design/&lt;name&gt;/</c>). The module serves them on this PC's loopback (<see cref="DesignServer"/>)
/// and shows them with the Browser module's tools, so the studio works wherever those do: in this window, through the daemon, and on a paired PC,
/// whose browser reaches the host's localhost. The daemon loads this module, so a chat's designs are always served where its files are.
/// </remarks>
public sealed partial class DesignModule : ILoknModule
{
	/// <summary>Defines the screens the tools accept by name: the width and the height of the first screen, in CSS pixels.</summary>
	private static readonly Dictionary<string, (int Width, int Height)> Viewports = new( StringComparer.Ordinal ) { ["desktop"] = (1440, 900), ["tablet"] = (768, 1024), ["mobile"] = (390, 844) };

	/// <summary>Defines the screen arguments the rendering tools take.</summary>
	private const string ViewportArguments = """
		"viewport": { "type": "string", "enum": ["desktop", "tablet", "mobile"], "description": "The screen: desktop 1440×900, tablet 768×1024 or mobile 390×844 pixels; defaults to desktop." },
		"width": { "type": "integer", "minimum": 200, "maximum": 3840, "description": "An exact width in CSS pixels, instead of a viewport." }
		""";

	/// <summary>Defines the design name argument every tool takes.</summary>
	private const string NameArgument = """
		"name": { "type": "string", "description": "The design's name, its folder under .lokn/design: 1 to 64 lowercase letters, digits, - and _, such as login-page." }
		""";

	/// <summary>Defines the variant argument.</summary>
	private const string VariantArgument = """
		"variant": { "type": "string", "description": "The variant's folder name, such as a, b or c; omit for the design's own index.html, or for its first variant when it has none." }
		""";

	/// <summary>Defines the optional folder argument.</summary>
	private const string FolderArgument = """
		"folder": { "type": "string", "description": "The project folder the design belongs to; defaults to your chat's working folder." }
		""";

	/// <summary>Stores the server once a tool started it, or null.</summary>
	private DesignServer? _server;

	/// <summary>Gets the feature's stable module identifier.</summary>
	public string Id => "design";

	/// <summary>Gets the feature's display name.</summary>
	public string Title => "Design";

	/// <summary>Gets the Conversations module, which knows the folder each chat works in, and Browser, whose tools show the studio and take snapshots.</summary>
	public IReadOnlyList<string> Dependencies => ["conversations", "browser"];

	/// <summary>Registers the design tools.</summary>
	public void Register( ModuleBuilder builder ) => builder
		.ToolAsync( "open",
			"Opens a user interface design in Loom's browser beside your conversation, in a studio where the user watches it update live, switches variants and screen widths, and pins comments to elements. "
			+ "Call it first, then write the design as plain HTML and CSS: <folder>/index.html for one design, or one folder per variant such as <folder>/a/index.html and <folder>/b/index.html to offer choices. "
			+ "Relative links to CSS, scripts, images and fonts in the folder work. The studio reloads each time you save, so save early and often. "
			+ "The user's comments arrive as chat messages that name each comment's file, element, the element's markup and styles, and its id. "
			+ "The user sees each save live, so after a change call design_check rather than design_snapshot.",
			$$"""
			{
				"type": "object",
				"properties": {
					{{NameArgument}},
					{{VariantArgument}},
					{{FolderArgument}}
				},
				"required": ["name"],
				"additionalProperties": false
			}
			""",
			OpenAsync )
		.ToolAsync( "check",
			"Checks a design variant at a screen width and answers in text: elements that overflow the width, clipped text, text with too little contrast, broken images, missing alt text, small tap targets on phones, "
			+ "and how many font sizes and text colors the page uses, each with its element's CSS selector. It costs a few hundred tokens, far less than a snapshot: call it after a change instead of design_snapshot, "
			+ "and take a snapshot only to judge how something looks.",
			$$"""
			{
				"type": "object",
				"properties": {
					{{NameArgument}},
					{{VariantArgument}},
					{{ViewportArguments}},
					{{FolderArgument}}
				},
				"required": ["name"],
				"additionalProperties": false
			}
			""",
			CheckAsync )
		.ToolAsync( "snapshot",
			"Saves a PNG of a design variant rendered at a screen width and returns its path; read that image file to see it. Images cost many tokens: the user already watches the design live, "
			+ "so use design_check for layout problems, pass selector to see one element, and take the first screen (the default) rather than fullPage.",
			$$"""
			{
				"type": "object",
				"properties": {
					{{NameArgument}},
					{{VariantArgument}},
					{{ViewportArguments}},
					"selector": { "type": "string", "description": "A CSS selector in the design: captures only that element, with a small margin. The cheapest way to look at one part." },
					"fullPage": { "type": "boolean", "description": "Captures the whole page instead of the first screen. Costs the most tokens." },
					{{FolderArgument}}
				},
				"required": ["name"],
				"additionalProperties": false
			}
			""",
			SnapshotAsync )
		.Tool( "comments",
			"Lists the comments the user pinned to a design in the studio: each one's id, variant, the CSS selector of the element and the text. Read them when the user asks you to apply their comments, then call design_resolve for those you did.",
			$$"""
			{
				"type": "object",
				"properties": {
					{{NameArgument}},
					"resolved": { "type": "boolean", "description": "Also lists resolved comments." },
					{{FolderArgument}}
				},
				"required": ["name"],
				"additionalProperties": false
			}
			""",
			ListComments )
		.Tool( "resolve",
			"Marks comments on a design as done, so the studio crosses them out. Call it for each comment you applied.",
			$$"""
			{
				"type": "object",
				"properties": {
					{{NameArgument}},
					"ids": { "type": "array", "items": { "type": "string" }, "description": "The comment ids from design_comments." },
					{{FolderArgument}}
				},
				"required": ["name", "ids"],
				"additionalProperties": false
			}
			""",
			Resolve )
		.Tool( "export",
			"Exports a design variant's files as a zip archive in the design's exports folder and returns its path. For an image, use design_snapshot.",
			$$"""
			{
				"type": "object",
				"properties": {
					{{NameArgument}},
					{{VariantArgument}},
					{{FolderArgument}}
				},
				"required": ["name"],
				"additionalProperties": false
			}
			""",
			Export );

	/// <summary>Follows the chats on this PC, so a design opens as soon as an agent works on it.</summary>
	public void Start( ModuleContext context )
	{
		LoadKnown();
		StartFollowing( context );
		// Listening from the start lets studio tabs restored with the window find their design at once.
		StartServer().Resolve = ResolveKnown;
	}

	/// <summary>Stops following chats and stops the server, which closes open studios' event streams.</summary>
	public void Stop()
	{
		StopFollowing();
		_server?.Dispose();
		_server = null;
	}

	/// <summary>Serves a design and shows its studio beside the calling chat.</summary>
	private async Task<AgentToolResult> OpenAsync( AgentToolContext context, JsonElement arguments )
	{
		var store = FindStore( context, arguments );
		Directory.CreateDirectory( store.Folder );
		var server = StartServer();
		var address = server.Serve( store );
		Remember( store, context.Conversation );
		if ( FindSession( context ) is { } session )
		{
			// The chat may have started since the module last looked at the sessions.
			UpdateFollowed();
			// The tool shows the studio itself; the chat's own writes to the design must not open a second one.
			LinkChat( session, store );
			if ( !MarkShown( session, store ) )
				return Describe( store, address, null );
		}
		var shown = await ShowStudioAsync( context.App.Tools, store, address, context.Caller, context.Cancellation, context.Conversation, context.Host );
		return Describe( store, address, shown );
	}

	/// <summary>Describes an open design for the agent: where to write it and whether the studio could be shown.</summary>
	private static AgentToolResult Describe( DesignStore store, string address, AgentToolResult? shown )
	{
		var variants = store.ListVariants();
		var written = variants.Count == 0 ? "It has no variant yet." : $"Its variants: {string.Join( ", ", variants.Select( Describe ) )}.";
		var view = shown is { IsError: true } ? $"The studio could not be shown ({shown.Text}); the user can open {address} in Loom's browser." : $"The studio is open at {address}.";
		return new( $"Design {store.Name} is in {store.Folder}. {written} {view} Write {Path.Combine( store.Folder, "index.html" )}, or a folder per variant such as {Path.Combine( store.Folder, "a", "index.html" )}." );
	}

	/// <summary>Renders a variant at a width and saves a screenshot of its first screen, its whole page, or one element.</summary>
	private Task<AgentToolResult> SnapshotAsync( AgentToolContext context, JsonElement arguments )
	{
		var store = FindStore( context, arguments );
		var variant = FindVariant( store, arguments );
		var (width, height) = ReadViewport( arguments );
		var selector = ReadString( arguments, "selector" );
		var isFullPage = arguments.TryGetProperty( "fullPage", out var full ) && full.ValueKind == JsonValueKind.True;
		return RunOnShotAsync( context, store, variant, width, isFullPage ? 0 : height, selector, async ( tools, tab ) =>
		{
			var shot = await tools.InvokeAsync( "browser_screenshot", JsonSerializer.SerializeToElement( new { tab, selector = selector is null ? "#frame" : "#target" } ),
				context.Caller, context.Cancellation, conversation: context.Conversation, host: context.Host );
			var part = selector is not null ? $"element {selector}" : isFullPage ? "whole page" : $"first {height} px";
			return shot.IsError ? shot : new( $"Variant {Describe( variant )} of design {store.Name}, {part}, at {width} px wide. {shot.Text}" );
		} );
	}

	/// <summary>Renders a variant at a width and reports its layout problems as text.</summary>
	private Task<AgentToolResult> CheckAsync( AgentToolContext context, JsonElement arguments )
	{
		var store = FindStore( context, arguments );
		var variant = FindVariant( store, arguments );
		var (width, height) = ReadViewport( arguments );
		return RunOnShotAsync( context, store, variant, width, height, null, async ( tools, tab ) =>
		{
			var found = await tools.InvokeAsync( "browser_eval", JsonSerializer.SerializeToElement( new { tab, script = "designCheck()" } ),
				context.Caller, context.Cancellation, conversation: context.Conversation, host: context.Host );
			if ( found.IsError )
				return found;
			var text = JsonSerializer.Deserialize<string>( found.Text ) ?? found.Text;
			return new( $"Variant {Describe( variant )} of design {store.Name}: {text}" );
		} );
	}

	/// <summary>Loads a variant at a width in the studio's tab, or a new tab beside the chat without one, runs browser tools on it, and shows the studio again.</summary>
	/// <param name="height">The height of the first screen, or zero for the whole page.</param>
	/// <param name="selector">The element to mark as the page's <c>#target</c>, or null.</param>
	/// <param name="run">Runs the browser tools on the tab, by its short id.</param>
	private async Task<AgentToolResult> RunOnShotAsync( AgentToolContext context, DesignStore store, string variant, int width, int height, string? selector,
		Func<AgentToolRegistry, string, Task<AgentToolResult>> run )
	{
		var server = StartServer();
		var studio = server.Serve( store );
		var tools = context.App.Tools;
		// The studio's own tab renders the page, so no other tab the user has open changes.
		var studioTab = await FindStudioTabAsync( tools, store, context.Caller, context.Cancellation, context.Conversation, context.Host );
		var shotAddress = server.GetShotAddress( store, variant, width, height, selector );
		var shown = await tools.InvokeAsync( "browser_navigate",
			studioTab is null ? JsonSerializer.SerializeToElement( new { url = shotAddress, newTab = true } ) : JsonSerializer.SerializeToElement( new { tab = studioTab, url = shotAddress } ),
			context.Caller, context.Cancellation, conversation: context.Conversation, host: context.Host );
		if ( shown.IsError )
			return AgentToolResult.Error( $"The browser could not show the design: {shown.Text} Call design_open first." );
		if ( TabPattern().Match( shown.Text ) is not { Success: true } match )
			return AgentToolResult.Error( $"The browser did not name the tab it used: {shown.Text}" );
		if ( shown.Text.Contains( "\"missing\"", StringComparison.Ordinal ) )
			return AgentToolResult.Error( $"No element of variant {Describe( variant )} matches {selector}." );

		var tab = match.Groups[1].Value;
		try
		{
			return await run( tools, tab );
		}
		finally
		{
			await tools.InvokeAsync( "browser_navigate", JsonSerializer.SerializeToElement( new { tab, url = studio } ),
				context.Caller, CancellationToken.None, conversation: context.Conversation, host: context.Host );
		}
	}

	/// <summary>Shows a design's studio: in a tab a panel shows on screen, else in a new tab beside the chat, since a tab without a visible panel stays out of sight.</summary>
	private static async Task<AgentToolResult> ShowStudioAsync( AgentToolRegistry tools, DesignStore store, string address, Guid? caller, CancellationToken cancellation, Guid? conversation, string? host )
	{
		var tab = await FindStudioTabAsync( tools, store, caller, cancellation, conversation, host, isOnScreen: true );
		var arguments = tab is null ? JsonSerializer.SerializeToElement( new { url = address, newTab = true } ) : JsonSerializer.SerializeToElement( new { tab, url = address } );
		return await tools.InvokeAsync( "browser_navigate", arguments, caller, cancellation, conversation: conversation, host: host );
	}

	/// <summary>Finds the short id of a browser tab that shows a design's studio or one of its snapshot pages, or null.</summary>
	/// <param name="isOnScreen">Whether only a tab that a panel shows on screen counts.</param>
	private static async Task<string?> FindStudioTabAsync( AgentToolRegistry tools, DesignStore store, Guid? caller, CancellationToken cancellation, Guid? conversation, string? host,
		bool isOnScreen = false )
	{
		var listed = await tools.InvokeAsync( "browser_tabs", JsonSerializer.SerializeToElement( new { } ), caller, cancellation, conversation: conversation, host: host );
		return listed.IsError ? null
			: StudioTabPattern().Matches( listed.Text ).FirstOrDefault( found => found.Groups["url"].Value.Contains( $"/d/{store.Token}/", StringComparison.Ordinal )
				&& ( !isOnScreen || found.Groups["rest"].Value.Contains( "(on screen", StringComparison.Ordinal ) ) )?.Groups[1].Value;
	}

	/// <summary>Reads the screen a call names: a viewport's width and height, or an exact width with a desktop height.</summary>
	private static (int Width, int Height) ReadViewport( JsonElement arguments )
	{
		if ( ReadInteger( arguments, "width" ) is { } width )
			return width is < 200 or > 3840 ? throw new ArgumentException( "The width must be 200 to 3840 pixels." ) : (width, Viewports["desktop"].Height);
		var name = ReadString( arguments, "viewport" ) ?? "desktop";
		return Viewports.TryGetValue( name, out var screen ) ? screen : throw new ArgumentException( $"Unknown viewport {name}; use desktop, tablet or mobile." );
	}

	/// <summary>Lists a design's comments.</summary>
	private static AgentToolResult ListComments( AgentToolContext context, JsonElement arguments )
	{
		var store = FindStore( context, arguments );
		var all = arguments.TryGetProperty( "resolved", out var flag ) && flag.ValueKind == JsonValueKind.True;
		var comments = store.ReadComments().Where( comment => all || !comment.Resolved ).ToArray();
		if ( comments.Length == 0 )
			return new( all ? $"Design {store.Name} has no comments." : $"Design {store.Name} has no open comments." );
		return AgentToolResult.Json( comments.Select( comment => new
		{
			comment.Id,
			Variant = Describe( comment.Variant ),
			File = Path.Combine( store.GetVariantFolder( comment.Variant ), DesignStore.IndexFile ),
			Element = comment.Selector.Length > 0 ? comment.Selector : "the whole page",
			comment.Text,
			Seen = comment.Context,
			Resolved = comment.Resolved ? true : (bool?)null,
		} ) );
	}

	/// <summary>Marks comments as done.</summary>
	private static AgentToolResult Resolve( AgentToolContext context, JsonElement arguments )
	{
		var store = FindStore( context, arguments );
		var ids = arguments.TryGetProperty( "ids", out var list ) && list.ValueKind == JsonValueKind.Array
			? list.EnumerateArray().Where( item => item.ValueKind == JsonValueKind.String ).Select( item => item.GetString()!.Trim() ).ToHashSet( StringComparer.Ordinal )
			: throw new ArgumentException( "Pass ids, an array of comment ids." );
		var known = store.ReadComments().Select( comment => comment.Id ).ToHashSet( StringComparer.Ordinal );
		var unknown = ids.Where( id => !known.Contains( id ) ).ToArray();
		var changed = store.SetResolved( ids, true );
		return new( $"Resolved {changed} comment{(changed == 1 ? "" : "s")}.{(unknown.Length > 0 ? $" Unknown ids: {string.Join( ", ", unknown )}." : "")}" );
	}

	/// <summary>Exports a variant as a zip archive.</summary>
	private static AgentToolResult Export( AgentToolContext context, JsonElement arguments )
	{
		var store = FindStore( context, arguments );
		var path = store.Export( FindVariant( store, arguments ) );
		return new( $"Exported to {path}." );
	}

	/// <summary>Starts the server when no tool did yet, and gives it the current theme.</summary>
	/// <remarks>
	/// The process that runs this PC's chats owns the studios' port, since their comments and the agent's state go through its chat sessions: the daemon,
	/// or the window while no daemon holds its chats (its store is not a mirror). The server rechecks this every second.
	/// </remarks>
	private DesignServer StartServer()
	{
		_server ??= new DesignServer( () => _context is { } context && (context.App.IsHost || _host is { Store.IsMirror: false }) );
		_server.ThemeCss = FormatTheme( Theme.Current );
		return _server;
	}

	/// <summary>Finds the design a call names, in its folder argument or the calling chat's working folder.</summary>
	/// <exception cref="InvalidOperationException">No folder was passed and no chat's agent made the call, or the chat is on another PC.</exception>
	private static DesignStore FindStore( AgentToolContext context, JsonElement arguments )
	{
		var name = ReadString( arguments, "name" ) ?? throw new ArgumentException( "Pass the design's name." );
		string project;
		if ( ReadString( arguments, "folder" ) is { } folder )
		{
			if ( !Path.IsPathFullyQualified( folder ) )
				throw new ArgumentException( "The folder must be a full path." );
			project = folder;
		}
		else
		{
			var chat = FindChat( context ) ?? throw new InvalidOperationException( "Pass folder: only a chat's agent has a working folder." );
			project = ComputeAgentDirectory( chat.Host.Store, chat.Conversation.Id );
		}
		return new DesignStore( name, DesignStore.GetFolder( project, name ) );
	}

	/// <summary>Finds the chat whose agent made a call, or null when no chat's agent did.</summary>
	/// <exception cref="InvalidOperationException">The chat runs on another PC.</exception>
	private static HostedChat? FindChat( AgentToolContext context )
	{
		var chat = context.App.GetModule<ConversationsModule>().FindConversation( context );
		if ( chat is { Host.IsLocal: false } )
			throw new InvalidOperationException( "This chat runs on another PC, whose own Loom serves its designs." );
		return chat;
	}

	/// <summary>Finds the session of the chat whose agent made a call, when it runs in this process.</summary>
	private static ChatSession? FindSession( AgentToolContext context ) =>
		FindChat( context ) is { } chat ? chat.Host.Sessions.Find( chat.Conversation.Id ) as ChatSession : null;

	/// <summary>Finds the variant a call names, else the design's first.</summary>
	/// <exception cref="ArgumentException">The named variant has no index.html.</exception>
	private static string FindVariant( DesignStore store, JsonElement arguments )
	{
		var variants = store.ListVariants();
		if ( ReadString( arguments, "variant" ) is { } named )
		{
			if ( variants.Contains( named ) )
				return named;
			if ( named == "main" && variants.Contains( "" ) )
				return "";
			throw new ArgumentException( $"Design {store.Name} has no variant {named}; it has {(variants.Count == 0 ? "none" : string.Join( ", ", variants.Select( Describe ) ))}." );
		}
		return variants.Count > 0 ? variants[0] : throw new InvalidOperationException( $"Design {store.Name} has no index.html yet; write {Path.Combine( store.Folder, DesignStore.IndexFile )} first." );
	}

	/// <summary>Formats the theme as CSS custom property declarations for the studio.</summary>
	internal static string FormatTheme( Theme theme ) =>
		$"--canvas: {Css( theme.Canvas )}; --panel: {Css( theme.Panel )}; --raised: {Css( theme.Raised )}; --hover: {Css( theme.Hover )}; --text: {Css( theme.Text )}; "
		+ $"--muted: {Css( theme.Muted )}; --border: {Css( theme.Border )}; --accent: {Css( theme.Accent )}; --font: '{new string( [.. theme.FontFamily.Where( letter => letter is not ('"' or '\'' or '<' or '>' or ';') )] )}';";

	/// <summary>Formats a color as CSS.</summary>
	private static string Css( Color color ) =>
		$"rgba({Math.Round( color.R * 255 )}, {Math.Round( color.G * 255 )}, {Math.Round( color.B * 255 )}, {color.A.ToString( System.Globalization.CultureInfo.InvariantCulture )})";

	/// <summary>Names a variant for the agent.</summary>
	private static string Describe( string variant ) => variant.Length == 0 ? "main (index.html)" : variant;

	/// <summary>Reads a non-empty string argument, or null.</summary>
	private static string? ReadString( JsonElement arguments, string name ) =>
		arguments.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.String && value.GetString()?.Trim() is { Length: > 0 } text ? text : null;

	/// <summary>Reads an integer argument, or null.</summary>
	private static int? ReadInteger( JsonElement arguments, string name ) =>
		arguments.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32( out var number ) ? number : null;

	/// <summary>Matches the tab id in a browser tool's answer.</summary>
	[GeneratedRegex( @"\(tab ([0-9a-f]{8})\)" )]
	private static partial Regex TabPattern();

	/// <summary>Matches a tab in <c>browser_tabs</c>'s answer: its short id, its address, and the rest of its line, which says where it shows.</summary>
	[GeneratedRegex( @"tab ([0-9a-f]{8}) "".*?"" (?<url>https?://\S+?)(?=,| \[|\s|$)(?<rest>[^\r\n]*)" )]
	private static partial Regex StudioTabPattern();
}
