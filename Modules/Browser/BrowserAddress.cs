namespace Lokn.Modules.Browser;

/// <summary>Turns what the user types in the address bar into the address to load.</summary>
internal static class BrowserAddress
{
	/// <summary>Defines the schemes an address may use as typed; others, such as javascript:, are searched for instead.</summary>
	private static readonly string[] Schemes = ["http", "https", "file", "about", "edge"];

	/// <summary>Returns the address to load: an absolute URL as typed, a host or path with https added, a local path, or a search; null for blank text.</summary>
	internal static string? Compute( string text, string searchTemplate )
	{
		text = text.Trim();
		if ( text.Length == 0 )
			return null;

		if ( Uri.TryCreate( text, UriKind.Absolute, out var absolute ) && Schemes.Contains( absolute.Scheme, StringComparer.OrdinalIgnoreCase ) )
			return absolute.AbsoluteUri;

		// A drive or UNC path opens the file.
		if ( text is [_, ':', '\\' or '/', ..] or ['\\', '\\', ..] )
			return new Uri( text ).AbsoluteUri;

		// One word with a dot, a port or "localhost", and no spaces, is a host: example.com, localhost:5173, 192.168.1.1/admin.
		if ( !text.Contains( ' ' ) && Uri.TryCreate( ComputeScheme( text ) + text, UriKind.Absolute, out var host )
			&& (host.Host.Contains( '.' ) || host.Host == "localhost" || !host.IsDefaultPort) && !host.Host.EndsWith( '.' ) )
			return host.AbsoluteUri;

		return string.Format( searchTemplate, Uri.EscapeDataString( text ) );
	}

	/// <summary>Uses plain http for local hosts, which rarely serve https, and https elsewhere.</summary>
	private static string ComputeScheme( string text ) =>
		text.StartsWith( "localhost", StringComparison.OrdinalIgnoreCase ) || text.StartsWith( "127.", StringComparison.Ordinal ) ? "http://" : "https://";
}
