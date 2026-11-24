using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Lokn.Modules.Design;

/// <summary>Serves designs and their studio page over HTTP on this PC's loopback, and tells open studios when a design's files change.</summary>
/// <remarks>
/// The server binds 127.0.0.1 only. A paired PC's browser reaches it through its localhost forwarding (docs/remote.md), so the same address works
/// in every window. Each design lives under <c>/d/&lt;token&gt;/</c>; a token is known only after a tool opened its design in this process.
/// Requests must name a loopback host, and changes must come from the server's own origin, so other web pages cannot use it.
/// <para>
/// Every address names <see cref="PreferredPort"/>, so studio tabs survive restarts. Of the Loom processes on this PC, the one that runs the chats
/// owns it (<see cref="WantsPort"/>): the daemon while it runs, else the window. A server checks every second: it gives the port up when it should not
/// own it, and takes it once it is free when it should, so the window hands it to the daemon it starts and takes it back when the daemon stops. Only when
/// a program other than Loom holds the port does a server listen on a free port and name that one.
/// </para>
/// </remarks>
internal sealed class DesignServer : IDisposable
{
	/// <summary>Defines the port every studio address names, so addresses of open tabs survive a restart.</summary>
	internal const int PreferredPort = 47831;

	/// <summary>Defines the path a design server answers with <see cref="ProbeAnswer"/>, so another Loom's server tells itself apart from a foreign program.</summary>
	private const string ProbePath = "/lokn-design";

	/// <summary>Defines what <see cref="ProbePath"/> answers.</summary>
	private const string ProbeAnswer = "lokn-design";

	/// <summary>Defines how often the server checks whether it should own the port, in milliseconds.</summary>
	private const int OwnershipCheckMs = 1000;

	/// <summary>Defines the largest request head or body read, in bytes.</summary>
	private const int MaxRequestBytes = 64 * 1024;

	/// <summary>Defines how long file changes settle before studios reload, in milliseconds.</summary>
	private const int SettleMs = 150;

	/// <summary>Defines how often an idle event stream sends a comment so proxies keep it, in milliseconds.</summary>
	private const int KeepAliveMs = 20_000;

	/// <summary>Guards the listener while ownership changes.</summary>
	private readonly Lock _listenerLock = new();

	/// <summary>Stores the listener, or null while another Loom owns the port.</summary>
	private TcpListener? _listener;

	/// <summary>Stores the stop of the current listener's accept loop.</summary>
	private CancellationTokenSource? _listening;

	/// <summary>Stores the free port listened on while a foreign program holds <see cref="PreferredPort"/>, or zero.</summary>
	private volatile int _fallbackPort;

	/// <summary>Stores the designs by token.</summary>
	private readonly ConcurrentDictionary<string, ServedDesign> _designs = new( StringComparer.Ordinal );

	/// <summary>Stores the server's lifetime.</summary>
	private readonly CancellationTokenSource _stop = new();

	/// <summary>Stores the studio page.</summary>
	private readonly string _studio;

	/// <summary>Stores the snapshot page.</summary>
	private readonly string _shot;

	/// <summary>Stores the CSS variables the studio uses for Loom's theme.</summary>
	private volatile string _themeCss = "";

	/// <summary>Starts the server, which owns the port at once when it should and it is free, and checks ownership every second from then on.</summary>
	/// <param name="wantsPort">Reports whether this process should own the port: it runs the chats. Called on a background thread.</param>
	/// <param name="port">The port every address names; <see cref="PreferredPort"/> except in checks, which must not meet the user's Loom.</param>
	internal DesignServer( Func<bool> wantsPort, int port = PreferredPort )
	{
		_studio = ReadResource( "Studio.html" );
		_shot = ReadResource( "Shot.html" );
		WantsPort = wantsPort;
		_port = port;
		UpdateOwnership();
		_ = WatchOwnershipAsync();
	}

	/// <summary>Stores the port every address names unless a foreign program holds it.</summary>
	private readonly int _port;

	/// <summary>Gets whether this process should own the port.</summary>
	internal Func<bool> WantsPort { get; }

	/// <summary>Gets the port studio addresses name: the shared one, unless a foreign program holds it.</summary>
	internal int Port => _fallbackPort is > 0 and var port ? port : _port;

	/// <summary>Gets whether this server listens, as the port's owner or on a fallback port.</summary>
	internal bool IsListening
	{
		get
		{
			lock ( _listenerLock )
				return _listener is not null;
		}
	}

	/// <summary>Sets the theme the studio shows, as CSS custom property declarations.</summary>
	internal string ThemeCss
	{
		set => _themeCss = value;
	}

	/// <summary>Gets or sets what finds a design the server does not serve yet by its token, such as one a studio restored after a restart asks for; called on a server thread.</summary>
	internal Func<string, DesignStore?>? Resolve { get; set; }

	/// <summary>Serves a design and returns its studio address.</summary>
	internal string Serve( DesignStore store )
	{
		_designs.GetOrAdd( store.Token, _ => new ServedDesign( store ) );
		return GetStudioAddress( store );
	}

	/// <summary>Links a served design to the chat working on it, so the studio shows whether its agent works and sends comments to it; null unlinks it.</summary>
	internal void Link( DesignStore store, DesignChatLink? link )
	{
		var design = _designs.GetOrAdd( store.Token, _ => new ServedDesign( store ) );
		design.Chat = link;
		design.Signal();
	}

	/// <summary>Tells a design's open studios that its chat's state changed, such as its agent starting or finishing a turn.</summary>
	internal void NotifyChat( DesignStore store )
	{
		if ( _designs.TryGetValue( store.Token, out var design ) )
			design.Signal();
	}

	/// <summary>Gets a design's studio address.</summary>
	internal string GetStudioAddress( DesignStore store ) => $"http://localhost:{Port}/d/{store.Token}/";

	/// <summary>Gets the address of a page showing one variant at a width, for snapshots.</summary>
	/// <param name="height">The height of the first screen, or zero for the whole page.</param>
	/// <param name="selector">An element the page marks as <c>#target</c>, or null.</param>
	internal string GetShotAddress( DesignStore store, string variant, int width, int height, string? selector ) =>
		$"http://localhost:{Port}/d/{store.Token}/shot?v={Uri.EscapeDataString( variant )}&w={width}&h={height}{(selector is null ? "" : "&s=" + Uri.EscapeDataString( selector ))}";

	/// <summary>Stops the server and its event streams.</summary>
	public void Dispose()
	{
		_stop.Cancel();
		StopListening();
		foreach ( var design in _designs.Values )
			design.Dispose();
		_designs.Clear();
		_stop.Dispose();
	}

	/// <summary>Checks ownership every second until the server stops.</summary>
	private async Task WatchOwnershipAsync()
	{
		while ( !_stop.IsCancellationRequested )
		{
			try
			{
				await Task.Delay( OwnershipCheckMs, _stop.Token ).ConfigureAwait( false );
			}
			catch ( OperationCanceledException )
			{
				return;
			}
			UpdateOwnership();
		}
	}

	/// <summary>Gives the port up when this process should not own it, and takes it when it should and it is free; listens on a free port instead while a
	/// foreign program holds it.</summary>
	internal void UpdateOwnership()
	{
		bool wants;
		try
		{
			wants = WantsPort();
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			return;
		}

		lock ( _listenerLock )
		{
			if ( _stop.IsCancellationRequested )
				return;

			var isOwner = _listener is not null && _fallbackPort == 0;
			if ( isOwner && !wants )
			{
				StopListeningLocked();
				return;
			}
			if ( isOwner || !wants )
				return;

			if ( TryListen( _port ) )
			{
				// A foreign program let the port go: studios name it again.
				_fallbackPort = 0;
				return;
			}
			if ( _listener is not null || IsLoknListening() )
				return;

			// A program other than Loom holds the port, so this server takes any free one.
			if ( TryListen( 0 ) )
				_fallbackPort = ((IPEndPoint)_listener!.LocalEndpoint).Port;
		}
	}

	/// <summary>Starts listening on a port, replacing a fallback listener; false when the port is taken.</summary>
	private bool TryListen( int port )
	{
		var listener = new TcpListener( IPAddress.Loopback, port );
		try
		{
			listener.Start();
		}
		catch ( SocketException )
		{
			return false;
		}

		StopListeningLocked();
		_listener = listener;
		_listening = CancellationTokenSource.CreateLinkedTokenSource( _stop.Token );
		_ = AcceptAsync( listener, _listening.Token );
		return true;
	}

	/// <summary>Stops listening, so open studios' event streams end and reconnect to the port's next owner.</summary>
	private void StopListening()
	{
		lock ( _listenerLock )
			StopListeningLocked();
	}

	/// <summary>Stops the current listener and its accept loop; the caller holds <see cref="_listenerLock"/>.</summary>
	private void StopListeningLocked()
	{
		_listening?.Cancel();
		_listening?.Dispose();
		_listening = null;
		_listener?.Stop();
		_listener = null;
		foreach ( var design in _designs.Values )
			design.Signal();
	}

	/// <summary>Reports whether another Loom's design server answers on the preferred port.</summary>
	private bool IsLoknListening()
	{
		try
		{
			using var client = new TcpClient();
			if ( !client.ConnectAsync( IPAddress.Loopback, _port ).Wait( 500 ) )
				return false;
			var stream = client.GetStream();
			stream.ReadTimeout = 500;
			stream.Write( Encoding.ASCII.GetBytes( $"GET {ProbePath} HTTP/1.1\r\nHost: localhost:{_port}\r\nConnection: close\r\n\r\n" ) );
			using var reader = new StreamReader( stream, Encoding.ASCII );
			return reader.ReadToEnd().Contains( ProbeAnswer, StringComparison.Ordinal );
		}
		catch ( Exception error ) when ( error is IOException or SocketException or AggregateException or ObjectDisposedException )
		{
			return false;
		}
	}

	/// <summary>Accepts connections until the listener stops.</summary>
	private async Task AcceptAsync( TcpListener listener, CancellationToken cancellation )
	{
		while ( !cancellation.IsCancellationRequested )
		{
			TcpClient client;
			try
			{
				client = await listener.AcceptTcpClientAsync( cancellation );
			}
			catch ( Exception error ) when ( error is OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException )
			{
				return;
			}
			_ = HandleAsync( client, cancellation );
		}
	}

	/// <summary>Answers one request and closes the connection, except an event stream, which stays open until its listener stops.</summary>
	/// <param name="client">The connection.</param>
	/// <param name="cancellation">Stops when the listener stops, as when this process gives the port up, so open studios reconnect to its next owner.</param>
	private async Task HandleAsync( TcpClient client, CancellationToken cancellation )
	{
		using var _ = client;
		try
		{
			var stream = client.GetStream();
			var request = await ReadRequestAsync( stream, cancellation );
			if ( request is null )
				return;
			await RouteAsync( request, stream, cancellation );
		}
		catch ( Exception error ) when ( error is IOException or SocketException or OperationCanceledException or ObjectDisposedException )
		{
		}
	}

	/// <summary>Answers a request.</summary>
	private async Task RouteAsync( HttpRequest request, NetworkStream stream, CancellationToken cancellation )
	{
		if ( request.Path == ProbePath )
		{
			await WriteTextAsync( stream, 200, ProbeAnswer );
			return;
		}
		if ( !IsLoopbackHost( request.Headers.GetValueOrDefault( "host" ) ) )
		{
			await WriteTextAsync( stream, 421, "This server answers only localhost addresses." );
			return;
		}

		var path = request.Path;
		var query = "";
		if ( path.IndexOf( '?' ) is var mark and >= 0 )
		{
			query = path[(mark + 1)..];
			path = path[..mark];
		}

		var parts = path.Split( '/', 4 );
		if ( parts.Length >= 3 && parts[1] == "d" && !_designs.ContainsKey( parts[2] ) && Resolve?.Invoke( parts[2] ) is { } known && known.Token == parts[2] )
			Serve( known );
		if ( parts.Length < 3 || parts[1] != "d" || !_designs.TryGetValue( parts[2], out var design ) )
		{
			// A studio restored before its design is served again checks back by itself, so it shows the design once an agent opens or writes it.
			var page = parts.Length == 4 && parts[3].Length == 0
				? $"<!DOCTYPE html><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"3\"><title>Design</title><body style=\"font: 13px system-ui; padding: 40px; {_themeCss} background: var(--canvas, #262624); color: var(--muted, #a09f96)\">This design is not open in Loom yet. Ask the agent to open it with design_open; this page shows it then.</body>"
				: null;
			if ( page is not null )
				await WriteAsync( stream, 404, "text/html; charset=utf-8", Encoding.UTF8.GetBytes( page ) );
			else
				await WriteTextAsync( stream, 404, "This design is not open. Ask the agent to open it again with design_open." );
			return;
		}
		if ( parts.Length == 3 )
		{
			await WriteAsync( stream, 301, "text/plain", [], [("Location", path + "/")] );
			return;
		}

		var store = design.Store;
		var rest = parts[3];
		var arguments = ParseQuery( query );
		if ( request.Method == "POST" )
		{
			if ( !IsOwnOrigin( request.Headers.GetValueOrDefault( "origin" ) ) )
			{
				await WriteTextAsync( stream, 403, "Changes come only from the studio." );
				return;
			}
			await RoutePostAsync( stream, design, rest, request.Body );
			return;
		}
		if ( request.Method != "GET" )
		{
			await WriteTextAsync( stream, 405, "Use GET or POST." );
			return;
		}

		switch ( rest )
		{
			case "":
				await WriteAsync( stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes( Fill( _studio, store ) ) );
				return;
			case "shot":
				await WriteAsync( stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes( Fill( _shot, store ) ) );
				return;
			case "api/state":
				var chat = design.Chat;
				await WriteJsonAsync( stream, new { name = store.Name, folder = store.Folder, variants = store.ListVariants(), comments = store.ReadComments(),
					chat = chat is null ? null : new { title = chat.Title, busy = chat.IsBusy } } );
				return;
			case "api/events":
				await StreamEventsAsync( stream, design, cancellation );
				return;
			case "api/export":
				var variant = arguments.GetValueOrDefault( "v" ) ?? "";
				using ( var archive = new MemoryStream() )
				{
					try
					{
						store.WriteArchive( variant, archive );
					}
					catch ( Exception error ) when ( error is ArgumentException or InvalidOperationException )
					{
						await WriteTextAsync( stream, 404, error.Message );
						return;
					}
					var file = $"{store.Name}{(variant.Length > 0 ? "-" + variant : "")}.zip";
					await WriteAsync( stream, 200, "application/zip", archive.ToArray(), [("Content-Disposition", $"attachment; filename=\"{file}\"")] );
				}
				return;
		}

		if ( rest.StartsWith( "f/", StringComparison.Ordinal ) && store.ResolveFile( rest[2..] ) is { } found )
		{
			await WriteAsync( stream, 200, GetContentType( found ), await File.ReadAllBytesAsync( found, cancellation ), [("Cache-Control", "no-store")] );
			return;
		}
		await WriteTextAsync( stream, 404, "Not found." );
	}

	/// <summary>Answers a change the studio asks for.</summary>
	private static async Task RoutePostAsync( NetworkStream stream, ServedDesign design, string rest, byte[] body )
	{
		var store = design.Store;
		try
		{
			using var document = JsonDocument.Parse( body );
			var root = document.RootElement;
			switch ( rest )
			{
				case "api/send":
					if ( design.Chat is not { } link )
					{
						await WriteTextAsync( stream, 409, "No chat works on this design. Ask an agent to open it with design_open." );
						return;
					}
					var wanted = ReadIds( root );
					var open = store.ReadComments().Where( item => !item.Resolved && (wanted.Count == 0 || wanted.Contains( item.Id )) ).ToArray();
					if ( open.Length == 0 )
					{
						await WriteTextAsync( stream, 400, "There are no open comments to send." );
						return;
					}
					if ( !link.Send( store.ComposeRequest( open ) ) )
					{
						await WriteTextAsync( stream, 409, "The chat is gone." );
						return;
					}
					await WriteJsonAsync( stream, new { sent = open.Length } );
					return;
				case "api/comments":
					var comment = store.AddComment( ReadString( root, "variant" ), ReadString( root, "selector" ), ReadString( root, "text" ), ReadString( root, "context" ) );
					await WriteJsonAsync( stream, comment );
					return;
				case "api/resolve":
					var ids = ReadIds( root );
					var resolved = !root.TryGetProperty( "resolved", out var flag ) || flag.ValueKind != JsonValueKind.False;
					await WriteJsonAsync( stream, new { changed = store.SetResolved( ids, resolved ) } );
					return;
			}
			await WriteTextAsync( stream, 404, "Not found." );
		}
		catch ( Exception error ) when ( error is JsonException or ArgumentException )
		{
			await WriteTextAsync( stream, 400, error.Message );
		}
	}

	/// <summary>Reads the comment identifiers a request names, or none.</summary>
	private static HashSet<string> ReadIds( JsonElement root ) => root.TryGetProperty( "ids", out var list ) && list.ValueKind == JsonValueKind.Array
		? list.EnumerateArray().Where( item => item.ValueKind == JsonValueKind.String ).Select( item => item.GetString()! ).ToHashSet( StringComparer.Ordinal )
		: [];

	/// <summary>Sends <c>change</c> events while the design's files change, until the studio leaves or the server stops.</summary>
	private async Task StreamEventsAsync( NetworkStream stream, ServedDesign design, CancellationToken cancellation )
	{
		await stream.WriteAsync( Encoding.ASCII.GetBytes( "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n: open\n\n" ), cancellation );
		var version = design.Version;
		while ( !cancellation.IsCancellationRequested )
		{
			var changed = design.WaitAsync( version );
			var finished = await Task.WhenAny( changed, Task.Delay( KeepAliveMs, cancellation ) );
			if ( finished == changed )
			{
				version = design.Version;
				await stream.WriteAsync( Encoding.ASCII.GetBytes( $"event: change\ndata: {{\"files\":{(design.FilesChanged ? "true" : "false")}}}\n\n" ), cancellation );
			}
			else
			{
				await stream.WriteAsync( ": alive\n\n"u8.ToArray(), cancellation );
			}
		}
	}

	/// <summary>Puts a design's values and the theme into a page.</summary>
	private string Fill( string page, DesignStore store ) => page
		.Replace( "/*THEME*/", _themeCss, StringComparison.Ordinal )
		.Replace( "\"__NAME__\"", JsonSerializer.Serialize( store.Name ), StringComparison.Ordinal );

	/// <summary>Reports whether a Host header names this PC's loopback, as a page this server served sends.</summary>
	private bool IsLoopbackHost( string? host ) =>
		host is not null && (host == $"localhost:{Port}" || host == $"127.0.0.1:{Port}");

	/// <summary>Reports whether an Origin header is the server's own.</summary>
	private bool IsOwnOrigin( string? origin ) =>
		origin is not null && (origin == $"http://localhost:{Port}" || origin == $"http://127.0.0.1:{Port}");

	/// <summary>Reads a request's line, headers and body, or null when it is malformed or too large.</summary>
	private static async Task<HttpRequest?> ReadRequestAsync( NetworkStream stream, CancellationToken cancellation )
	{
		var buffer = new byte[MaxRequestBytes];
		var length = 0;
		var end = -1;
		while ( end < 0 )
		{
			if ( length == buffer.Length )
				return null;
			var read = await stream.ReadAsync( buffer.AsMemory( length ), cancellation );
			if ( read == 0 )
				return null;
			length += read;
			end = buffer.AsSpan( 0, length ).IndexOf( "\r\n\r\n"u8 );
		}

		var lines = Encoding.ASCII.GetString( buffer, 0, end ).Split( "\r\n" );
		var first = lines[0].Split( ' ' );
		if ( first.Length != 3 )
			return null;

		var headers = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var line in lines.Skip( 1 ) )
		{
			if ( line.IndexOf( ':' ) is var colon and > 0 )
				headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
		}

		var size = headers.TryGetValue( "content-length", out var text ) && int.TryParse( text, out var parsed ) ? parsed : 0;
		if ( size < 0 || size > MaxRequestBytes )
			return null;

		var body = new byte[size];
		var held = Math.Min( size, length - end - 4 );
		Array.Copy( buffer, end + 4, body, 0, held );
		if ( held < size )
			await stream.ReadExactlyAsync( body.AsMemory( held ), cancellation );
		return new HttpRequest( first[0], first[1], headers, body );
	}

	/// <summary>Writes a response and its body.</summary>
	private static async Task WriteAsync( NetworkStream stream, int status, string type, byte[] body, (string Name, string Value)[]? headers = null )
	{
		var head = new StringBuilder();
		head.Append( $"HTTP/1.1 {status} {GetReason( status )}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\nX-Content-Type-Options: nosniff\r\n" );
		foreach ( var (name, value) in headers ?? [] )
			head.Append( $"{name}: {value}\r\n" );
		head.Append( "\r\n" );
		await stream.WriteAsync( Encoding.ASCII.GetBytes( head.ToString() ) );
		await stream.WriteAsync( body );
	}

	/// <summary>Writes a plain text response.</summary>
	private static Task WriteTextAsync( NetworkStream stream, int status, string text ) =>
		WriteAsync( stream, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes( text ) );

	/// <summary>Writes a value as camel-case JSON.</summary>
	private static Task WriteJsonAsync( NetworkStream stream, object value ) =>
		WriteAsync( stream, 200, "application/json", JsonSerializer.SerializeToUtf8Bytes( value, JsonSerializerOptions.Web ), [("Cache-Control", "no-store")] );

	/// <summary>Parses a query string.</summary>
	private static Dictionary<string, string> ParseQuery( string query ) => query
		.Split( '&', StringSplitOptions.RemoveEmptyEntries )
		.Select( pair => pair.Split( '=', 2 ) )
		.GroupBy( pair => Uri.UnescapeDataString( pair[0] ) )
		.ToDictionary( group => group.Key, group => Uri.UnescapeDataString( group.First().ElementAtOrDefault( 1 ) ?? "" ) );

	/// <summary>Reads a string property, or empty text.</summary>
	private static string ReadString( JsonElement root, string name ) =>
		root.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

	/// <summary>Gets the reason phrase of a status code the server sends.</summary>
	private static string GetReason( int status ) => status switch
	{
		200 => "OK",
		301 => "Moved Permanently",
		400 => "Bad Request",
		403 => "Forbidden",
		404 => "Not Found",
		405 => "Method Not Allowed",
		409 => "Conflict",
		421 => "Misdirected Request",
		_ => "Error",
	};

	/// <summary>Gets a file's content type from its extension.</summary>
	internal static string GetContentType( string path ) => Path.GetExtension( path ).ToLowerInvariant() switch
	{
		".html" or ".htm" => "text/html; charset=utf-8",
		".css" => "text/css; charset=utf-8",
		".js" or ".mjs" => "text/javascript; charset=utf-8",
		".json" => "application/json",
		".svg" => "image/svg+xml",
		".png" => "image/png",
		".jpg" or ".jpeg" => "image/jpeg",
		".gif" => "image/gif",
		".webp" => "image/webp",
		".avif" => "image/avif",
		".ico" => "image/x-icon",
		".woff" => "font/woff",
		".woff2" => "font/woff2",
		".ttf" => "font/ttf",
		".otf" => "font/otf",
		".mp4" => "video/mp4",
		".webm" => "video/webm",
		".txt" or ".md" => "text/plain; charset=utf-8",
		_ => "application/octet-stream",
	};

	/// <summary>Reads an embedded page.</summary>
	private static string ReadResource( string name )
	{
		using var stream = typeof( DesignServer ).Assembly.GetManifestResourceStream( name ) ?? throw new InvalidOperationException( $"The module lacks its {name}." );
		using var reader = new StreamReader( stream );
		return reader.ReadToEnd();
	}

	/// <summary>Holds one parsed request.</summary>
	private sealed record HttpRequest( string Method, string Path, Dictionary<string, string> Headers, byte[] Body );

	/// <summary>Watches one served design's folder and counts its changes.</summary>
	private sealed class ServedDesign : IDisposable
	{
		/// <summary>Stores the folder watcher, created once the folder exists.</summary>
		private readonly FileSystemWatcher? _watcher;

		/// <summary>Stores the task that completes at the next change.</summary>
		private TaskCompletionSource _next = new( TaskCreationOptions.RunContinuationsAsynchronously );

		/// <summary>Stores the timer that lets a burst of changes settle.</summary>
		private readonly Timer _settle;

		/// <summary>Creates the watcher over a design.</summary>
		internal ServedDesign( DesignStore store )
		{
			Store = store;
			_settle = new Timer( _ => Signal() );
			Directory.CreateDirectory( store.Folder );
			_watcher = new FileSystemWatcher( store.Folder ) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
			_watcher.Changed += OnChanged;
			_watcher.Created += OnChanged;
			_watcher.Deleted += OnChanged;
			_watcher.Renamed += OnChanged;
			_watcher.EnableRaisingEvents = true;
		}

		/// <summary>Gets the design.</summary>
		internal DesignStore Store { get; }

		/// <summary>Gets or sets the chat working on the design, or null.</summary>
		internal DesignChatLink? Chat { get; set; }

		/// <summary>Gets how many settled changes there were.</summary>
		internal int Version { get; private set; }

		/// <summary>Gets whether the latest settled change touched files other than the comments, so studios reload the page and not only the comments.</summary>
		internal bool FilesChanged { get; private set; }

		/// <summary>Stores whether a file other than the comments changed since the last settled change.</summary>
		private bool _pendingFiles;

		/// <summary>Waits for the first change after a version.</summary>
		internal Task WaitAsync( int version )
		{
			lock ( this )
				return version != Version ? Task.CompletedTask : _next.Task;
		}

		/// <summary>Stops watching.</summary>
		public void Dispose()
		{
			_watcher?.Dispose();
			_settle.Dispose();
		}

		/// <summary>Restarts the settle timer on a change, ignoring exports and temporary comment files.</summary>
		private void OnChanged( object sender, FileSystemEventArgs e )
		{
			var relative = Path.GetRelativePath( Store.Folder, e.FullPath );
			if ( relative.StartsWith( DesignStore.ExportsFolder, StringComparison.OrdinalIgnoreCase ) || relative.EndsWith( ".tmp", StringComparison.OrdinalIgnoreCase ) )
				return;
			if ( !string.Equals( relative, DesignStore.CommentsFile, StringComparison.OrdinalIgnoreCase ) )
				_pendingFiles = true;
			_settle.Change( SettleMs, Timeout.Infinite );
		}

		/// <summary>Counts a settled change and wakes the waiting streams.</summary>
		internal void Signal()
		{
			TaskCompletionSource done;
			lock ( this )
			{
				Version++;
				FilesChanged = _pendingFiles;
				_pendingFiles = false;
				done = _next;
				_next = new( TaskCreationOptions.RunContinuationsAsynchronously );
			}
			done.SetResult();
		}
	}
}

/// <summary>Connects a served design to the chat whose agent works on it.</summary>
/// <param name="title">The chat's title, which the studio shows.</param>
/// <param name="send">Sends a message to the chat from any thread: a new turn when the agent is idle, else queued after its turn; false when the chat is gone.</param>
internal sealed class DesignChatLink( string title, Func<string, bool> send )
{
	/// <summary>Stores whether the agent works now; written on the UI thread, read by the server.</summary>
	private volatile bool _isBusy;

	/// <summary>Gets the chat's title.</summary>
	internal string Title => title;

	/// <summary>Gets or sets whether the chat's agent works now.</summary>
	internal bool IsBusy
	{
		get => _isBusy;
		set => _isBusy = value;
	}

	/// <summary>Sends a message to the chat; false when the chat is gone.</summary>
	internal bool Send( string message ) => send( message );
}
