using System.Reflection;
using System.Security.Cryptography;

namespace Lokn.Modules.Remote;

/// <summary>The web client's files as the host and the relay both serve them: paths, content types, caching, validators and the page's content policy.</summary>
/// <remarks>Compiled into the Remote module and, by link, into the relay; both embed the files under the same names (<c>web/&lt;file&gt;</c>).</remarks>
internal static class WebClientFiles
{
	/// <summary>The prefix of the files' embedded resource names.</summary>
	private const string ResourcePrefix = "web/";

	/// <summary>The page's content policy: only its own files, and WebSockets to its own origin only (the host's own <c>/loom</c>, or the relay's
	/// <c>/device/{hostId}</c>), which CSP Level 3 counts as <c>'self'</c>.</summary>
	private const string ContentPolicy = "default-src 'self'; connect-src 'self'; img-src 'self' data:; script-src 'self'; style-src 'self'; "
		+ "worker-src 'self'; manifest-src 'self'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

	/// <summary>Lists each served path, relative to the client's folder, with its resource, content type and caching.</summary>
	private static readonly (string Path, string Resource, string ContentType, string Caching)[] Table =
	[
		("/", "index.html", "text/html; charset=utf-8", "no-cache"),
		("/index.html", "index.html", "text/html; charset=utf-8", "no-cache"),
		("/app.js", "app.js", "text/javascript; charset=utf-8", "no-cache"),
		("/app.css", "app.css", "text/css; charset=utf-8", "no-cache"),
		("/sw.js", "sw.js", "text/javascript; charset=utf-8", "no-cache"),
		("/manifest.webmanifest", "manifest.webmanifest", "application/manifest+json; charset=utf-8", "no-cache"),
		("/icon.png", "icon.png", "image/png", "max-age=86400"),
	];

	/// <summary>Reads every file from an assembly's resources with its headers and validator.</summary>
	/// <exception cref="InvalidOperationException">A file is missing from the assembly's resources.</exception>
	internal static IReadOnlyList<WebClientFile> Load( Assembly assembly ) => [.. Table.Select( entry =>
	{
		var data = ReadResource( assembly, entry.Resource );
		var tag = "\"" + Convert.ToHexStringLower( SHA256.HashData( data ).AsSpan( 0, 12 ) ) + "\"";
		var headers = new List<KeyValuePair<string, string>>
		{
			new( "Cache-Control", entry.Caching ),
			new( "ETag", tag ),
			new( "X-Content-Type-Options", "nosniff" ),
			new( "Referrer-Policy", "no-referrer" ),
		};
		if ( entry.Resource == "index.html" )
			headers.Add( new( "Content-Security-Policy", ContentPolicy ) );
		return new WebClientFile( entry.Path, entry.ContentType, data, tag, headers );
	} )];

	/// <summary>Reads one embedded file.</summary>
	/// <exception cref="InvalidOperationException">The file is missing.</exception>
	private static byte[] ReadResource( Assembly assembly, string name )
	{
		using var stream = assembly.GetManifestResourceStream( ResourcePrefix + name )
			?? throw new InvalidOperationException( $"The web client file {name} is missing from {assembly.GetName().Name}." );
		using var copy = new MemoryStream();
		stream.CopyTo( copy );
		return copy.ToArray();
	}
}

/// <summary>One served file of the web client.</summary>
/// <param name="Path">The request path relative to the client's folder, such as <c>/</c> or <c>/app.js</c>.</param>
/// <param name="ContentType">The content type of a full answer.</param>
/// <param name="Data">The file's bytes.</param>
/// <param name="Tag">The file's quoted <c>ETag</c>; an <c>If-None-Match</c> equal to it is answered 304.</param>
/// <param name="Headers">The headers of every answer, full or 304: caching, the validator, and the page's content policy.</param>
internal sealed record WebClientFile( string Path, string ContentType, byte[] Data, string Tag, IReadOnlyList<KeyValuePair<string, string>> Headers );
