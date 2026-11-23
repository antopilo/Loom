using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Lokn.Modules.Design;

namespace Lokn.Tests;

/// <summary>Exercises design folders, comments, exports and the loopback server the studio uses, without a window.</summary>
internal static class DesignChecks
{
	/// <summary>Runs the design checks.</summary>
	internal static async Task RunAsync()
	{
		var root = Path.Combine( Path.GetTempPath(), "Lokn", "design-checks-" + Guid.NewGuid().ToString( "N" ) );
		try
		{
			CheckStore( root );
			CheckDetection( root );
			await CheckServerAsync( root );
		}
		finally
		{
			Directory.Delete( root, recursive: true );
		}
		Console.WriteLine( "PASS design names, variants, files kept inside the design, comments and their resolution, zip exports of one variant, designs named by tool calls, and the studio server's pages, files, comments, change events, linked chat, comments sent to it, and refusal of other hosts and origins." );
	}

	/// <summary>Verifies names, variants, file resolution, comments and exports.</summary>
	private static void CheckStore( string root )
	{
		CoreChecks.Check( !DesignStore.IsValidName( "Login" ) && !DesignStore.IsValidName( "../x" ) && !DesignStore.IsValidName( "" ) && DesignStore.IsValidName( "login-page_2" ),
			"Design names are lowercase letters, digits, - and _." );
		CoreChecks.Check( Throws<ArgumentException>( () => DesignStore.GetFolder( root, ".." ) ), "A name cannot leave the design folder." );

		var store = new DesignStore( "login", DesignStore.GetFolder( root, "login" ) );
		CoreChecks.Check( store.Folder == Path.Combine( root, ".lokn", "design", "login" ) && store.ListVariants().Count == 0, "A design lives in .lokn/design and starts with no variants." );

		Write( store.Folder, "index.html", "<p>main</p>" );
		Write( store.Folder, "b/index.html", "<p>b</p>" );
		Write( store.Folder, "a/index.html", "<p>a</p>" );
		Write( store.Folder, "a/style.css", "p{}" );
		Write( store.Folder, "notes/readme.txt", "no index" );
		Write( root, "secret.txt", "outside" );
		CoreChecks.Check( store.ListVariants().SequenceEqual( ["", "a", "b"] ), "Variants are the folder's own index.html, then folders with one, in order." );
		CoreChecks.Check( store.ResolveFile( "a/" ) == Path.Combine( store.Folder, "a", "index.html" ) && store.ResolveFile( "a/style.css" ) is not null,
			"A folder address serves its index.html, and assets resolve." );
		CoreChecks.Check( store.ResolveFile( "../../../secret.txt" ) is null && store.ResolveFile( "..%2F..%2F..%2Fsecret.txt" ) is null && store.ResolveFile( "a/missing.css" ) is null,
			"Files outside the design, escaped or not, and missing files are not served." );

		var first = store.AddComment( "a", "main > p", "  Bigger  " );
		var second = store.AddComment( "", "", "Darker" );
		CoreChecks.Check( Throws<ArgumentException>( () => store.AddComment( "a", "p", "   " ) ) && Throws<ArgumentException>( () => store.AddComment( "../x", "p", "x" ) ),
			"Blank comments and invalid variants are refused." );
		CoreChecks.Check( store.ReadComments() is [{ Text: "Bigger", Variant: "a", Resolved: false }, { Text: "Darker", Variant: "" }], "Comments are kept in order, trimmed." );
		CoreChecks.Check( store.SetResolved( [first.Id, "nope"], true ) == 1 && store.SetResolved( [first.Id], true ) == 0 && store.ReadComments()[0].Resolved && !store.ReadComments()[1].Resolved,
			"Resolving changes only the named open comments." );
		_ = second;

		var path = store.Export( "a" );
		using ( var archive = ZipFile.OpenRead( path ) )
			CoreChecks.Check( archive.Entries.Select( entry => entry.FullName ).Order( StringComparer.Ordinal ).SequenceEqual( ["index.html", "style.css"] ), "A variant's export holds its files only." );
		using ( var archive = ZipFile.OpenRead( store.Export( "" ) ) )
		{
			var names = archive.Entries.Select( entry => entry.FullName ).ToArray();
			CoreChecks.Check( names.Contains( "index.html" ) && names.Contains( "notes/readme.txt" ) && !names.Any( name => name.StartsWith( "a/", StringComparison.Ordinal ) || name.StartsWith( "exports/", StringComparison.Ordinal ) || name == "comments.json" ),
				"The main variant's export leaves out the other variants, exports and comments." );
		}
		CoreChecks.Check( Throws<InvalidOperationException>( () => store.Export( "notes" ) ), "A folder without index.html cannot be exported." );
	}

	/// <summary>Verifies the server's pages, files, comments, events and its refusal of other hosts and origins.</summary>
	private static async Task CheckServerAsync( string root )
	{
		var store = new DesignStore( "server", DesignStore.GetFolder( root, "server" ) );
		Write( store.Folder, "a/index.html", "<p>a</p>" );
		using var server = new DesignServer { ThemeCss = "--accent: rgba(1, 2, 3, 1);" };
		var address = server.Serve( store );
		CoreChecks.Check( address == $"http://localhost:{server.Port}/d/{store.Token}/", "The studio address names the design's token on localhost." );

		using var client = new HttpClient();
		var studio = await client.GetStringAsync( address );
		CoreChecks.Check( studio.Contains( "--accent: rgba(1, 2, 3, 1);", StringComparison.Ordinal ) && studio.Contains( "const name = \"server\";", StringComparison.Ordinal ),
			"The studio carries Loom's theme and the design's name." );
		CoreChecks.Check( await client.GetStringAsync( address + "f/a/" ) == "<p>a</p>", "Variant files are served." );
		CoreChecks.Check( (await client.GetAsync( $"http://localhost:{server.Port}/d/0000000000000000/" )).StatusCode == HttpStatusCode.NotFound, "An unknown design is not found." );
		var restored = new DesignStore( "restored", DesignStore.GetFolder( root, "restored" ) );
		Write( restored.Folder, "index.html", "<p>back</p>" );
		server.Resolve = token => token == restored.Token ? restored : null;
		CoreChecks.Check( await client.GetStringAsync( $"http://localhost:{server.Port}/d/{restored.Token}/f/index.html" ) == "<p>back</p>",
			"A design the server does not serve yet, such as a studio restored after a restart asks for, is served once the module knows it." );

		using ( var request = new HttpRequestMessage( HttpMethod.Get, address ) )
		{
			request.Headers.Host = "evil.example";
			CoreChecks.Check( (await client.SendAsync( request )).StatusCode == HttpStatusCode.MisdirectedRequest, "Requests for another host name are refused." );
		}

		var body = """{ "variant": "a", "selector": "p", "text": "Hello", "context": "<p> \"a\" 12×18px; font 400 16px" }""";
		using ( var request = new HttpRequestMessage( HttpMethod.Post, address + "api/comments" ) { Content = new StringContent( body, Encoding.UTF8, "application/json" ) } )
		{
			request.Headers.Add( "Origin", "https://evil.example" );
			CoreChecks.Check( (await client.SendAsync( request )).StatusCode == HttpStatusCode.Forbidden && store.ReadComments().Count == 0, "Other origins cannot add comments." );
		}

		using var events = await client.GetStreamAsync( address + "api/events" );
		using var reader = new StreamReader( events );
		CoreChecks.Check( await reader.ReadLineAsync() == ": open", "The event stream opens." );

		using ( var request = new HttpRequestMessage( HttpMethod.Post, address + "api/comments" ) { Content = new StringContent( body, Encoding.UTF8, "application/json" ) } )
		{
			request.Headers.Add( "Origin", $"http://localhost:{server.Port}" );
			var response = await client.SendAsync( request );
			CoreChecks.Check( response.IsSuccessStatusCode && store.ReadComments() is [{ Text: "Hello", Selector: "p" }], "The studio adds comments." );
		}
		CoreChecks.Check( await ReadEventAsync( reader ) == "{\"files\":false}", "A comment change tells studios to reload comments, not the page." );

		File.WriteAllText( Path.Combine( store.Folder, "a", "index.html" ), "<p>a2</p>" );
		CoreChecks.Check( await ReadEventAsync( reader ) == "{\"files\":true}", "A saved file tells studios to reload the page." );

		using var state = JsonDocument.Parse( await client.GetStringAsync( address + "api/state" ) );
		CoreChecks.Check( state.RootElement.GetProperty( "variants" ).EnumerateArray().Select( item => item.GetString() ).SequenceEqual( ["a"] )
			&& state.RootElement.GetProperty( "comments" ).GetArrayLength() == 1, "The state lists variants and comments." );

		var zip = await client.GetAsync( address + "api/export?v=a" );
		using ( var archive = new ZipArchive( await zip.Content.ReadAsStreamAsync() ) )
			CoreChecks.Check( zip.Content.Headers.ContentType?.MediaType == "application/zip" && archive.Entries.Single().FullName == "index.html", "The studio downloads a variant as a zip archive." );

		CoreChecks.Check( (await PostAsync( client, server, address + "api/send", "{}" )).StatusCode == HttpStatusCode.Conflict, "Comments cannot be sent while no chat works on the design." );
		var sent = new List<string>();
		var link = new DesignChatLink( "Login chat", message =>
		{
			lock ( sent )
				sent.Add( message );
			return true;
		} ) { IsBusy = true };
		server.Link( store, link );
		CoreChecks.Check( await ReadEventAsync( reader ) == "{\"files\":false}", "Linking a chat tells studios to reload their state." );
		using ( var linked = JsonDocument.Parse( await client.GetStringAsync( address + "api/state" ) ) )
			CoreChecks.Check( linked.RootElement.GetProperty( "chat" ) is var chat && chat.GetProperty( "title" ).GetString() == "Login chat" && chat.GetProperty( "busy" ).GetBoolean(),
				"The state names the linked chat and whether its agent works." );
		var id = store.ReadComments()[0].Id;
		var delivered = await PostAsync( client, server, address + "api/send", $$"""{ "ids": ["{{id}}"] }""" );
		CoreChecks.Check( delivered.IsSuccessStatusCode && sent is [var message] && message.Contains( $"[{id}]", StringComparison.Ordinal ) && message.Contains( "`p`: Hello", StringComparison.Ordinal )
			&& message.Contains( Path.Combine( store.Folder, "a", "index.html" ), StringComparison.Ordinal ) && message.Contains( "design_resolve", StringComparison.Ordinal )
			&& message.Contains( "Element: <p> \"a\" 12×18px; font 400 16px", StringComparison.Ordinal ),
			"Sending comments gives the chat each one's id, file, element and text, and how to resolve them." );
		store.SetResolved( [id], true );
		CoreChecks.Check( (await PostAsync( client, server, address + "api/send", "{}" )).StatusCode == HttpStatusCode.BadRequest && sent.Count == 1, "Resolved comments are not sent." );
	}

	/// <summary>Verifies which designs a tool call's arguments name.</summary>
	private static void CheckDetection( string root )
	{
		var project = Path.Combine( root, "app" );
		string[] Names( string json ) => [.. DesignModule.FindDesigns( JsonDocument.Parse( json ).RootElement, project ).Select( store => store.Folder )];
		var login = Path.Combine( project, ".lokn", "design", "login" );
		CoreChecks.Check( Names( """{ "type": "tool_execution_start", "args": { "path": ".lokn/design/login/a/index.html" } }""" ).SequenceEqual( [login] ),
			"A relative path names a design in the chat's folder." );
		var other = Path.Combine( root, "other" );
		var absolute = JsonSerializer.Serialize( new { args = new { input = $"[{other.Replace( '\\', '/' )}/.lokn/design/home/index.html#1A2B]\nPUT 1.=1:\n+x" } } );
		CoreChecks.Check( Names( absolute ).SequenceEqual( [Path.Combine( other, ".lokn", "design", "home" )] ), "An absolute path in an edit names a design in its own project." );
		CoreChecks.Check( Names( """{ "args": { "command": "cat > .lokn\\design\\login\\style.css && cp x .lokn/design/login/b/" } }""" ).SequenceEqual( [login] ), "Several paths to one design name it once." );
		CoreChecks.Check( Names( """{ "args": { "command": "ls .lokn/design; cat .lokn/design/Login/x" } }""" ).Length == 0, "Listing designs or a name that is not valid names none." );
	}

	/// <summary>Posts JSON as the studio does, from the server's own origin.</summary>
	private static async Task<HttpResponseMessage> PostAsync( HttpClient client, DesignServer server, string address, string json )
	{
		using var request = new HttpRequestMessage( HttpMethod.Post, address ) { Content = new StringContent( json, Encoding.UTF8, "application/json" ) };
		request.Headers.Add( "Origin", $"http://localhost:{server.Port}" );
		return await client.SendAsync( request );
	}

	/// <summary>Reads the data of the next change event, or null when none comes within five seconds.</summary>
	private static async Task<string?> ReadEventAsync( StreamReader reader )
	{
		using var timeout = new CancellationTokenSource( 5000 );
		try
		{
			while ( await reader.ReadLineAsync( timeout.Token ) is { } line )
			{
				if ( line.StartsWith( "data: ", StringComparison.Ordinal ) )
					return line["data: ".Length..];
			}
		}
		catch ( OperationCanceledException )
		{
		}
		return null;
	}

	/// <summary>Writes a file, creating its folder.</summary>
	private static void Write( string folder, string relative, string text )
	{
		var path = Path.Combine( folder, relative );
		Directory.CreateDirectory( Path.GetDirectoryName( path )! );
		File.WriteAllText( path, text );
	}

	/// <summary>Reports whether an action throws an exception of a type.</summary>
	private static bool Throws<T>( Action action ) where T : Exception
	{
		try
		{
			action();
			return false;
		}
		catch ( T )
		{
			return true;
		}
	}
}
