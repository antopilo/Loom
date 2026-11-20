namespace Lokn.Modules.Remote;

/// <summary>Serves the web client's embedded files (<see cref="WebClientFiles"/>) from a <see cref="WebSocketHost"/>: the page, its script, style,
/// service worker, manifest and icon.</summary>
public static class WebClientAssets
{
	/// <summary>Maps every file's GET path on the host.</summary>
	/// <exception cref="InvalidOperationException">A file is missing from the assembly's resources.</exception>
	public static void Map( WebSocketHost host )
	{
		foreach ( var file in WebClientFiles.Load( typeof( WebClientAssets ).Assembly ) )
			host.MapGet( file.Path, request => Serve( request, file ) );
	}

	/// <summary>Answers one request with a file, or 304 when the browser already has this version.</summary>
	private static HostResponse Serve( HostRequest request, WebClientFile file )
	{
		var current = request["If-None-Match"] == file.Tag;
		var response = new HostResponse( current ? 304 : 200 ) { Body = current ? default : file.Data };
		foreach ( var (name, value) in file.Headers )
			response.Headers[name] = value;
		if ( !current )
			response.Headers["Content-Type"] = file.ContentType;
		return response;
	}
}
