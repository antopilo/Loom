using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Markdown;

namespace Lokn.Modules.Chat;

/// <summary>Shows a call to an MCP server's tool as a connector line, as Claude does: the service's icon, its name, then the action in words.</summary>
/// <remarks>Omp names an MCP tool <c>mcp__server_tool</c>; the request and the response open under the line.</remarks>
public sealed partial class ChatContent
{
	/// <summary>Defines the prefix Omp gives every MCP tool's name.</summary>
	private const string McpPrefix = "mcp__";

	/// <summary>Defines a connector icon's side in logical pixels.</summary>
	private const int ConnectorIconSize = 16;

	/// <summary>Maps server names whose website is not <c>name.com</c> to the domain their icon comes from.</summary>
	private static readonly Dictionary<string, string> _connectorDomains = new( StringComparer.OrdinalIgnoreCase )
	{
		["notion"] = "notion.so",
		["linear"] = "linear.app",
		["gmail"] = "mail.google.com",
		["google"] = "google.com",
		["gdrive"] = "drive.google.com",
		["calendar"] = "calendar.google.com",
		["atlassian"] = "atlassian.com",
		["jira"] = "atlassian.com",
		["confluence"] = "atlassian.com",
		["context7"] = "context7.com",
		["playwright"] = "playwright.dev",
	};

	/// <summary>Splits an MCP tool's name into its server and its tool, or returns null for any other tool.</summary>
	/// <remarks>Omp joins them with one underscore, so a server whose own name has one splits at its first; the tool keeps the rest.</remarks>
	internal static (string Server, string Tool)? ParseConnector( string name )
	{
		if ( !name.StartsWith( McpPrefix, StringComparison.Ordinal ) )
			return null;
		var rest = name[McpPrefix.Length..];
		var split = rest.IndexOf( '_' );
		return split <= 0 || split == rest.Length - 1 ? (rest, "") : (rest[..split], rest[(split + 1)..]);
	}

	/// <summary>Turns a snake-case name into words: <c>get_recent_activities</c> reads "Get recent activities", <c>strava</c> reads "Strava".</summary>
	internal static string ComputeConnectorWords( string name )
	{
		var words = name.Replace( '_', ' ' ).Replace( '-', ' ' ).Trim();
		return words.Length == 0 ? words : char.ToUpperInvariant( words[0] ) + words[1..];
	}

	/// <summary>Gets the domain a server's icon comes from: a known service's, else <c>server.com</c>.</summary>
	internal static string ComputeConnectorDomain( string server ) =>
		_connectorDomains.TryGetValue( server, out var domain ) ? domain : server.ToLowerInvariant() + ".com";

	/// <summary>Builds a connector call: its icon, the server's name in bold and the action, a status mark, and its request and response when opened.</summary>
	private Container BuildConnector( string key, string server, string tool, JsonElement arguments, string status, JsonElement? result )
	{
		var theme = Theme.Current;
		var expanded = _expanded.Contains( key );
		Blob icon = status switch
		{
			"Running" => ChatIcons.BuildSpinner( "connector-spinner" ),
			_ when ConnectorIcons.Get( ComputeConnectorDomain( server ), update => context.Post( update ), MarkChanged ) is { } source => new Image
			{
				Source = source,
				Width = ConnectorIconSize,
				Height = ConnectorIconSize,
				FlexShrink = 0,
				BorderRadius = theme.Radius,
				Fit = ImageFit.Contain,
			},
			_ => ChatIcons.BuildIcon( ChatIcons.ComputeToolGlyph( "mcp" ) ),
		};
		var title = new Text( [
			new( ComputeConnectorWords( server ), new Style { FontWeight = 600, Color = theme.Text } ),
			new( tool.Length > 0 ? "  " + ComputeConnectorWords( tool ) : "", new Style { Color = theme.Muted } ),
		] )
		{
			FlexShrink = 1,
			MinWidth = 0,
			TextWrap = TextWrap.NoWrap,
			TextTrimming = TextTrimming.Ellipsis,
		};
		var head = new List<Blob> { icon, title, BuildChevron( expanded ) };
		if ( status == "Failed" )
			head.Add( new Text { Content = "Failed", Color = theme.Error, FontSize = ViewStyle.SmallSize, FlexShrink = 0 } );
		var toggle = new Button
		{
			Key = "connector-head",
			AccessibilityName = $"{ComputeConnectorWords( server )} · {ComputeConnectorWords( tool )} · {status}",
			AlignSelf = AlignSelf.FlexStart,
			MaxWidth = Length.Percent( 100 ),
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Padding = new Edges( 2, theme.Spacing / 2 ),
			Margin = new Edges( 0, -theme.Spacing / 2 ),
			BorderRadius = theme.Radius,
			Cursor = Cursor.Pointer,
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			OnClick = () => context.Run( () => UpdateExpanded( key ) ),
			Children = head,
		};
		var children = new List<Blob> { toggle };
		if ( expanded )
		{
			var details = new List<Blob>();
			if ( arguments.ValueKind == JsonValueKind.Object && arguments.EnumerateObject().Any( property => property.Name != "i" ) )
			{
				details.Add( BuildText( "Request", muted: true, size: ViewStyle.SmallSize ) );
				details.Add( MarkdownView.BuildCode( BuildJson( arguments ), "json", showLanguage: false ) );
			}
			if ( result is { } body && TerminalText.BuildPlain( ComputeResultText( body ) ).Trim() is { Length: > 0 } text )
			{
				details.Add( BuildText( "Response", muted: true, size: ViewStyle.SmallSize ) );
				details.Add( BuildCodePreview( key + ":output", text, text.StartsWith( '{' ) || text.StartsWith( '[' ) ? "json" : "text" ) );
			}
			children.Add( BuildIndent( details ) );
		}
		return new Container
		{
			Key = key,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = $"{server} connector {status}",
			Gap = theme.Spacing,
			Children = children,
		};
	}

	/// <summary>Builds the small chevron after a fold's title: right while closed, down while open.</summary>
	private static Text BuildChevron( bool expanded ) => new()
	{
		Content = expanded ? "\uE70D" : "\uE76C",
		FontFamily = ViewStyle.IconFontFamily,
		FontSize = 9,
		Color = Theme.Current.Muted,
		FlexShrink = 0,
	};
}

/// <summary>Loads services' icons by domain once each, keeping them on disk between runs; a domain without one is remembered as missing.</summary>
internal static class ConnectorIcons
{
	/// <summary>Defines the icon size asked for, in pixels: large enough to stay sharp at twice the shown size.</summary>
	private const int FetchSize = 64;

	/// <summary>Holds loaded icons by domain; null marks a domain without one.</summary>
	private static readonly Dictionary<string, ImageSource?> _icons = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Holds the domains whose icon loads, with what to run once each arrives.</summary>
	private static readonly Dictionary<string, List<Action>> _loading = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Fetches icons.</summary>
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds( 15 ) };

	/// <summary>Gets a domain's icon on the UI thread, or null while it loads or when it has none; a first ask starts the load, which comes back
	/// through <paramref name="post"/> and runs <paramref name="loaded"/> for every view that asked.</summary>
	internal static ImageSource? Get( string domain, Action<Action> post, Action loaded )
	{
		if ( _icons.TryGetValue( domain, out var icon ) )
			return icon;
		if ( _loading.TryGetValue( domain, out var waiting ) )
		{
			waiting.Add( loaded );
			return null;
		}
		_loading[domain] = [loaded];
		_ = Task.Run( async () =>
		{
			var bytes = await ReadAsync( domain );
			ImageSource? source = null;
			try
			{
				source = bytes is null ? null : ImageSource.Decode( bytes );
			}
			catch ( Exception error ) when ( error is FormatException or InvalidOperationException or ArgumentException or NotSupportedException )
			{
			}
			// The maps are touched on the UI thread only.
			post( () =>
			{
				_icons[domain] = source;
				if ( _loading.Remove( domain, out var callbacks ) )
				{
					foreach ( var callback in callbacks )
						callback();
				}
			} );
		} );
		return null;
	}

	/// <summary>Reads a domain's icon from the disk cache, or fetches and caches it; null when there is none.</summary>
	private static async Task<byte[]?> ReadAsync( string domain )
	{
		var path = LoknPaths.Combine( "cache", "connectors", domain + ".png" );
		try
		{
			if ( File.Exists( path ) )
				return await File.ReadAllBytesAsync( path );
			using var response = await Http.GetAsync( $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString( domain )}&sz={FetchSize}" );
			// The service answers 404 with a generic globe for a domain it does not know; that globe would only stand for nothing.
			if ( !response.IsSuccessStatusCode )
				return null;
			var bytes = await response.Content.ReadAsByteArrayAsync();
			Directory.CreateDirectory( Path.GetDirectoryName( path )! );
			await File.WriteAllBytesAsync( path, bytes );
			return bytes;
		}
		catch ( Exception error ) when ( error is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException )
		{
			return null;
		}
	}
}
