using System.Globalization;
using System.Text;

namespace Lokn.Modules.Remote;

/// <summary>An answer to a <see cref="HostRequest"/>: a status, headers and a body. <see cref="WebSocketHost"/> adds <c>Content-Length</c> and, unless
/// the connection upgrades, <c>Connection: close</c>, and leaves the body out of answers to <c>HEAD</c>.</summary>
/// <param name="statusCode">The status code.</param>
public sealed class HostResponse( int statusCode )
{
	/// <summary>Gets the status code.</summary>
	public int StatusCode => statusCode;

	/// <summary>Gets the headers by name, matched without case; set <c>Content-Type</c> here.</summary>
	public Dictionary<string, string> Headers { get; } = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Gets or sets the body.</summary>
	public ReadOnlyMemory<byte> Body
	{
		get; init;
	}

	/// <summary>Gets whether the status may carry a body and its length: not 1xx, 204 or 304.</summary>
	internal bool HasContent => statusCode >= 200 && statusCode is not (204 or 304);

	/// <summary>Formats the status line and headers with the blank line that ends them.</summary>
	/// <param name="closes">Whether the connection closes after this answer, so it says <c>Connection: close</c> in place of any <c>Connection</c> header.</param>
	internal byte[] FormatHead( bool closes )
	{
		var text = new StringBuilder();
		text.Append( CultureInfo.InvariantCulture, $"HTTP/1.1 {statusCode} {DescribeStatus( statusCode )}\r\n" );
		foreach ( var (name, value) in Headers )
		{
			if ( !name.Equals( "Content-Length", StringComparison.OrdinalIgnoreCase ) && !(closes && name.Equals( "Connection", StringComparison.OrdinalIgnoreCase )) )
				text.Append( name ).Append( ": " ).Append( value ).Append( "\r\n" );
		}
		if ( HasContent )
			text.Append( CultureInfo.InvariantCulture, $"Content-Length: {Body.Length}\r\n" );
		if ( closes )
			text.Append( "Connection: close\r\n" );
		text.Append( "\r\n" );
		return Encoding.Latin1.GetBytes( text.ToString() );
	}

	/// <summary>Gives the reason phrase of the status codes the host sends.</summary>
	private static string DescribeStatus( int code ) => code switch
	{
		101 => "Switching Protocols",
		200 => "OK",
		204 => "No Content",
		304 => "Not Modified",
		400 => "Bad Request",
		404 => "Not Found",
		405 => "Method Not Allowed",
		426 => "Upgrade Required",
		500 => "Internal Server Error",
		_ => "Status",
	};
}
