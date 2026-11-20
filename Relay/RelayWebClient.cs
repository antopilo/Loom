using Lokn.Modules.Remote;

namespace Lokn.Relay;

/// <summary>Serves the web client under <c>/h/{hostId}/</c> with the host's headers, so a browser opens it from the relay's HTTPS origin and its page
/// reaches the host through <c>/device/{hostId}</c> on the same origin.</summary>
internal static class RelayWebClient
{
	/// <summary>Maps every file of the web client under <c>/h/{hostId}</c>.</summary>
	/// <exception cref="InvalidOperationException">A file is missing from the relay's resources.</exception>
	public static void Map( WebApplication app )
	{
		foreach ( var file in WebClientFiles.Load( typeof( RelayWebClient ).Assembly ) )
		{
			app.MapMethods( "/h/{hostId}" + file.Path, [HttpMethods.Get, HttpMethods.Head], ( HttpContext context, string hostId ) =>
			{
				// The page's files are relative to its folder, so /h/{hostId} without the slash is sent to the folder first.
				if ( file.Path == "/" && !(context.Request.Path.Value ?? "").EndsWith( '/' ) )
				{
					context.Response.Redirect( Uri.EscapeDataString( hostId ) + "/" + context.Request.QueryString );
					return Task.CompletedTask;
				}
				return ServeAsync( context, file );
			} );
		}
	}

	/// <summary>Answers one request with a file, or 304 when the browser already has this version.</summary>
	private static Task ServeAsync( HttpContext context, WebClientFile file )
	{
		var response = context.Response;
		foreach ( var (name, value) in file.Headers )
			response.Headers[name] = value;
		if ( context.Request.Headers.IfNoneMatch.ToString() == file.Tag )
		{
			response.StatusCode = StatusCodes.Status304NotModified;
			return Task.CompletedTask;
		}

		response.ContentType = file.ContentType;
		response.ContentLength = file.Data.Length;
		return HttpMethods.IsHead( context.Request.Method ) ? Task.CompletedTask : response.Body.WriteAsync( file.Data ).AsTask();
	}
}
