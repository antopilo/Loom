using System.Text;

namespace Lokn.Modules.Remote;

/// <summary>One HTTP/1.1 request <see cref="WebSocketHost"/> read: its request line and headers. The host takes no request bodies.</summary>
public sealed class HostRequest
{
	/// <summary>Bounds the request line and headers together, with the blank line that ends them.</summary>
	public const int MaxHeadBytes = 8 * 1024;

	/// <summary>Holds the headers by name, matched without case; repeated headers are joined by commas.</summary>
	private readonly Dictionary<string, string> _headers;

	/// <summary>Creates a parsed request.</summary>
	private HostRequest( string method, string path, string query, string version, Dictionary<string, string> headers, string remoteEndPoint )
	{
		Method = method;
		Path = path;
		Query = query;
		Version = version;
		_headers = headers;
		RemoteEndPoint = remoteEndPoint;
	}

	/// <summary>Gets the method, such as <c>GET</c>.</summary>
	public string Method
	{
		get;
	}

	/// <summary>Gets the path as sent, without the query.</summary>
	public string Path
	{
		get;
	}

	/// <summary>Gets the query with its <c>?</c>, or empty.</summary>
	public string Query
	{
		get;
	}

	/// <summary>Gets the protocol version, <c>HTTP/1.1</c> or <c>HTTP/1.0</c>.</summary>
	public string Version
	{
		get;
	}

	/// <summary>Gets the client's address and port.</summary>
	public string RemoteEndPoint
	{
		get;
	}

	/// <summary>Gets the headers by name, matched without case.</summary>
	public IReadOnlyDictionary<string, string> Headers => _headers;

	/// <summary>Gets whether the request announces a body, with a non-zero <c>Content-Length</c> or any <c>Transfer-Encoding</c>.</summary>
	public bool HasBody => _headers.ContainsKey( "Transfer-Encoding" ) || (_headers.TryGetValue( "Content-Length", out var length ) && length != "0");

	/// <summary>Gets a header's value, or null when it is absent.</summary>
	public string? this[string name] => _headers.TryGetValue( name, out var value ) ? value : null;

	/// <summary>Gets whether a comma-separated header, such as <c>Connection</c>, lists a token, matched without case.</summary>
	public bool HasToken( string name, string token )
	{
		if ( this[name] is not { } value )
			return false;
		foreach ( var part in value.Split( ',' ) )
		{
			if ( part.Trim().Equals( token, StringComparison.OrdinalIgnoreCase ) )
				return true;
		}
		return false;
	}

	/// <summary>Parses a request line and its headers: the bytes before the blank line that ends them.</summary>
	/// <param name="head">The request line and header lines, separated by CRLF, without the final blank line.</param>
	/// <param name="remoteEndPoint">The client's address and port.</param>
	/// <returns>The request, or null when it is not a well-formed HTTP/1.x request in origin form.</returns>
	internal static HostRequest? Parse( ReadOnlySpan<byte> head, string remoteEndPoint )
	{
		var lines = Encoding.Latin1.GetString( head ).Split( "\r\n" );
		var parts = lines[0].Split( ' ' );
		if ( parts.Length != 3 || parts[0].Length == 0 || !parts[0].All( char.IsAsciiLetterUpper )
			|| parts[1] is not ['/', ..] || parts[1].Any( character => character <= ' ' || character >= '\x7f' )
			|| parts[2] is not ("HTTP/1.1" or "HTTP/1.0") )
			return null;

		var headers = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var line in lines.AsSpan( 1 ) )
		{
			// A line folded onto the previous one (obsolete) or a name with spaces is refused, as RFC 9112 asks of servers.
			var colon = line.IndexOf( ':' );
			if ( colon <= 0 || line.AsSpan( 0, colon ).ContainsAny( " \t" ) )
				return null;
			var name = line[..colon];
			var value = line[(colon + 1)..].Trim( [' ', '\t'] );
			headers[name] = headers.TryGetValue( name, out var earlier ) ? earlier + ", " + value : value;
		}

		var query = parts[1].IndexOf( '?' );
		return query < 0
			? new( parts[0], parts[1], "", parts[2], headers, remoteEndPoint )
			: new( parts[0], parts[1][..query], parts[1][query..], parts[2], headers, remoteEndPoint );
	}
}
