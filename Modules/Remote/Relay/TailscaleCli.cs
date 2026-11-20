using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>Runs the Tailscale command line for the <c>tailscale</c> and <c>funnel</c> network modes: the PC's tailnet addresses and name, and HTTPS proxies of the host's port.</summary>
/// <remarks>
/// Every call checks first that Tailscale is installed, running and signed in, and throws <see cref="TailscaleException"/> with a text for the
/// user otherwise. Serve and Funnel use the CLI of Tailscale 1.52 and later (<c>serve --bg --https=443 &lt;target&gt;</c>) when <c>serve --help</c>
/// lists <c>--bg</c>, else the earlier form (<c>serve https:443 / &lt;target&gt;</c>, <c>funnel 443 on|off</c>).
/// </remarks>
public static partial class TailscaleCli
{
	/// <summary>The HTTPS port Serve and Funnel listen on; Funnel allows only 443, 8443 and 10000.</summary>
	private const int HttpsPort = 443;

	/// <summary>How long one command may run; Serve and Funnel wait without end when the tailnet has not enabled HTTPS certificates or Funnel.</summary>
	private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds( 60 );

	/// <summary>Finds <c>tailscale.exe</c> in Tailscale's install folder or on PATH, or null when Tailscale is not installed.</summary>
	public static string? FindExecutable()
	{
		string[] installed = OperatingSystem.IsWindows() ? [Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.ProgramFiles ), "Tailscale", "tailscale.exe" )] : [];
		return ToolLocator.Find( "tailscale", installed );
	}

	/// <summary>Reads Tailscale's state, name and addresses with <c>tailscale status --json</c>; it may be signed out or stopped.</summary>
	/// <exception cref="TailscaleException">Tailscale is not installed or its service is not running.</exception>
	public static async Task<TailscaleStatus> GetStatusAsync( CancellationToken cancellationToken )
	{
		var result = await RunAsync( ["status", "--json"], cancellationToken ).ConfigureAwait( false );
		if ( result.Code != 0 )
			throw new TailscaleException( "Tailscale is not running: " + ReadFirstLine( result ) );
		try
		{
			return ParseStatus( result.Output );
		}
		catch ( JsonException exception )
		{
			throw new TailscaleException( "Tailscale answered 'status --json' with something other than its status: " + exception.Message );
		}
	}

	/// <summary>Gets the PC's tailnet IPv4 addresses with <c>tailscale ip -4</c>.</summary>
	/// <exception cref="TailscaleException">Tailscale is not installed, not running or not signed in.</exception>
	public static async Task<IReadOnlyList<string>> GetAddressesAsync( CancellationToken cancellationToken )
	{
		await ReadRunningStatusAsync( cancellationToken ).ConfigureAwait( false );
		var result = await RunAsync( ["ip", "-4"], cancellationToken ).ConfigureAwait( false );
		if ( result.Code != 0 )
			throw new TailscaleException( "Tailscale has no address for this PC: " + ReadFirstLine( result ) );
		return ParseAddresses( result.Output );
	}

	/// <summary>Gets the PC's MagicDNS name, such as <c>desk.tail1234.ts.net</c>, without the trailing dot; null when the tailnet gives none.</summary>
	/// <exception cref="TailscaleException">Tailscale is not installed, not running or not signed in.</exception>
	public static async Task<string?> GetDnsNameAsync( CancellationToken cancellationToken ) =>
		( await ReadRunningStatusAsync( cancellationToken ).ConfigureAwait( false ) ).DnsName;

	/// <summary>Serves <c>https://&lt;dns name&gt;/</c> to the tailnet only, as a persistent proxy of <c>http://127.0.0.1:<paramref name="port"/></c>; turns Funnel off.</summary>
	/// <exception cref="TailscaleException">Tailscale is not installed, not running or not signed in, or refused, such as when the tailnet has no HTTPS certificates.</exception>
	public static Task ServeAsync( int port, CancellationToken cancellationToken ) => ApplyAsync( port, false, cancellationToken );

	/// <summary>Turns Funnel on, serving <c>https://&lt;dns name&gt;/</c> to the internet as a persistent proxy of <c>http://127.0.0.1:<paramref name="port"/></c>, or off, which keeps the tailnet-only proxy of <see cref="ServeAsync"/>.</summary>
	/// <exception cref="TailscaleException">Tailscale is not installed, not running or not signed in, or refused, such as when the tailnet does not allow Funnel.</exception>
	public static Task FunnelAsync( int port, bool on, CancellationToken cancellationToken ) => ApplyAsync( port, on, cancellationToken );

	/// <summary>Removes Loom's HTTPS proxy on port 443, with its Funnel, so the tailnet no longer reaches <c>http://127.0.0.1:<paramref name="port"/></c>.</summary>
	/// <param name="port">The local port the proxy forwarded to.</param>
	/// <param name="cancellationToken">Cancels the commands.</param>
	/// <exception cref="TailscaleException">Tailscale is not installed, or refused, such as when it is not running.</exception>
	public static async Task ServeOffAsync( int port, CancellationToken cancellationToken )
	{
		var help = await RunAsync( ["serve", "--help"], cancellationToken ).ConfigureAwait( false );
		foreach ( var arguments in BuildOffCommands( SupportsBackground( help.Output + "\n" + help.Error ) ) )
		{
			var result = await RunAsync( arguments, cancellationToken ).ConfigureAwait( false );
			if ( result.Code != 0 )
				throw new TailscaleException( $"Removing the Tailscale proxy of port {port.ToString( CultureInfo.InvariantCulture )} with 'tailscale {string.Join( ' ', arguments )}' failed: " + ReadFirstLine( result ) );
		}
	}

	/// <summary>Builds the commands that remove the HTTPS proxy on port 443 and its Funnel.</summary>
	/// <param name="background">Whether the CLI takes <c>--bg</c> (1.52 and later), as <see cref="SupportsBackground"/> tells.</param>
	/// <returns>The argument lists, run in order.</returns>
	internal static IReadOnlyList<IReadOnlyList<string>> BuildOffCommands( bool background )
	{
		var https = HttpsPort.ToString( CultureInfo.InvariantCulture );
		if ( background )
			return [["serve", "--https=" + https, "off"]];
		return [["funnel", https, "off"], ["serve", "https:" + https, "/", "off"]];
	}

	/// <summary>Parses <c>tailscale ip</c> output: one address per line.</summary>
	internal static IReadOnlyList<string> ParseAddresses( string output ) =>
		[.. output.Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ).Where( line => System.Net.IPAddress.TryParse( line, out _ ) )];

	/// <summary>Parses <c>tailscale status --json</c>: <c>BackendState</c>, <c>Self.DNSName</c>, <c>Self.ID</c>, the keys of <c>Self.CapMap</c>,
	/// <c>TailscaleIPs</c>, <c>AuthURL</c> and <c>CertDomains</c>.</summary>
	/// <exception cref="JsonException">The text is not a status object.</exception>
	internal static TailscaleStatus ParseStatus( string json )
	{
		using var document = JsonDocument.Parse( json );
		var root = document.RootElement;
		if ( root.ValueKind != JsonValueKind.Object )
			throw new JsonException( "The status is not an object." );
		var state = ReadString( root, "BackendState" ) ?? "";
		var hasSelf = root.TryGetProperty( "Self", out var self ) && self.ValueKind == JsonValueKind.Object;
		var dnsName = hasSelf ? ReadString( self, "DNSName" )?.TrimEnd( '.' ) : null;
		var nodeId = hasSelf ? ReadString( self, "ID" ) : null;
		string[] capabilities = hasSelf && self.TryGetProperty( "CapMap", out var caps ) && caps.ValueKind == JsonValueKind.Object
			? [.. caps.EnumerateObject().Select( cap => cap.Name )]
			: [];
		var addresses = root.TryGetProperty( "TailscaleIPs", out var ips ) && ips.ValueKind == JsonValueKind.Array
			? ips.EnumerateArray().Where( ip => ip.ValueKind == JsonValueKind.String ).Select( ip => ip.GetString()! ).ToArray()
			: [];
		var authUrl = ReadString( root, "AuthURL" );
		// Tailscale lists the names it can get certificates for only once HTTPS certificates are on for the tailnet.
		var hasCertificates = root.TryGetProperty( "CertDomains", out var domains ) && domains.ValueKind == JsonValueKind.Array && domains.GetArrayLength() > 0;
		return new( state, string.IsNullOrEmpty( dnsName ) ? null : dnsName, addresses, string.IsNullOrEmpty( authUrl ) ? null : authUrl, hasCertificates )
		{
			NodeId = string.IsNullOrEmpty( nodeId ) ? null : nodeId,
			Capabilities = capabilities,
		};
	}

	/// <summary>Defines the tailnet's DNS settings, where HTTPS certificates are turned on.</summary>
	public const string DnsSettingsUrl = "https://login.tailscale.com/admin/dns";

	/// <summary>Builds the page that allows Funnel for one node, which Tailscale itself prints when Funnel is refused.</summary>
	public static string BuildFunnelUrl( string? nodeId ) =>
		nodeId is null ? "https://login.tailscale.com/admin/acls" : "https://login.tailscale.com/f/funnel?node=" + Uri.EscapeDataString( nodeId );

	/// <summary>Determines from <c>tailscale serve --help</c> whether the CLI is 1.52 or later, whose Serve and Funnel take <c>--bg</c>.</summary>
	internal static bool SupportsBackground( string help ) => BackgroundFlag().IsMatch( help );

	/// <summary>Builds the commands that proxy <c>http://127.0.0.1:<paramref name="port"/></c> on HTTPS port 443, to the tailnet only or with Funnel to the internet.</summary>
	/// <param name="port">The local port.</param>
	/// <param name="funnel">Whether the internet reaches it too.</param>
	/// <param name="background">Whether the CLI takes <c>--bg</c> (1.52 and later), as <see cref="SupportsBackground"/> tells.</param>
	/// <returns>The argument lists, run in order.</returns>
	internal static IReadOnlyList<IReadOnlyList<string>> BuildCommands( int port, bool funnel, bool background )
	{
		var target = "http://127.0.0.1:" + port.ToString( CultureInfo.InvariantCulture );
		var https = HttpsPort.ToString( CultureInfo.InvariantCulture );
		// A 1.52 serve command sets the port's Funnel flag to its own kind, so serving again is how Funnel turns off and the tailnet proxy stays.
		if ( background )
			return [[funnel ? "funnel" : "serve", "--bg", "--https=" + https, target]];
		return [["serve", "https:" + https, "/", target], ["funnel", https, funnel ? "on" : "off"]];
	}

	/// <summary>Builds the user's text for a Tailscale that is not ready to serve, or null when it is running.</summary>
	internal static string? DescribeProblem( TailscaleStatus status ) => status.BackendState switch
	{
		"Running" => null,
		"NeedsLogin" => "Tailscale is not signed in. Sign in from the Tailscale icon in the taskbar" + ( status.AuthUrl is { } url ? " or at " + url : "" ) + ", then try again.",
		"NeedsMachineAuth" => "This PC waits for a tailnet admin to approve it in the Tailscale admin console.",
		"Stopped" => "Tailscale is disconnected. Connect it from the Tailscale icon in the taskbar, then try again.",
		"Starting" or "NoState" or "" => "Tailscale is still starting; try again in a moment.",
		_ => $"Tailscale is not connected (state {status.BackendState}).",
	};

	/// <summary>Turns the HTTPS proxy of <paramref name="port"/> on, with or without Funnel.</summary>
	private static async Task ApplyAsync( int port, bool funnel, CancellationToken cancellationToken )
	{
		var status = await ReadRunningStatusAsync( cancellationToken ).ConfigureAwait( false );
		// Without these node capabilities Serve and Funnel wait for the user without end instead of failing, so they are checked first.
		if ( !status.HasHttps )
			throw new TailscaleException( "HTTPS certificates are off for the tailnet. Turn them on at " + DnsSettingsUrl + " then try again." );
		if ( funnel && !status.HasFunnel )
			throw new TailscaleException( "Funnel is not allowed for this PC. Allow it at " + BuildFunnelUrl( status.NodeId ) + " then try again." );
		var help = await RunAsync( ["serve", "--help"], cancellationToken ).ConfigureAwait( false );
		foreach ( var arguments in BuildCommands( port, funnel, SupportsBackground( help.Output + "\n" + help.Error ) ) )
		{
			var result = await RunApplyAsync( arguments, cancellationToken ).ConfigureAwait( false );
			if ( result.Code != 0 )
			{
				// Tailscale prints the page that enables Serve or Funnel for this PC on a later line; it is kept, so Remote access can open it.
				var link = HttpsLink().Match( result.Error + "\n" + result.Output );
				throw new TailscaleException( $"'tailscale {string.Join( ' ', arguments )}' failed: " + ReadFirstLine( result ) + (link.Success ? " " + link.Value : "") );
			}
		}
	}

	/// <summary>Reads Tailscale's status and checks that it is running and signed in.</summary>
	/// <exception cref="TailscaleException">It is not installed, not running or not signed in.</exception>
	private static async Task<TailscaleStatus> ReadRunningStatusAsync( CancellationToken cancellationToken )
	{
		var status = await GetStatusAsync( cancellationToken ).ConfigureAwait( false );
		return DescribeProblem( status ) is { } problem ? throw new TailscaleException( problem ) : status;
	}

	/// <summary>Runs <c>tailscale</c> with <paramref name="arguments"/> within <see cref="CommandTimeout"/>.</summary>
	/// <exception cref="TailscaleException">Tailscale is not installed, or the command did not finish in time.</exception>
	private static async Task<WorkspaceProcessResult> RunAsync( IReadOnlyList<string> arguments, CancellationToken cancellationToken )
	{
		var executable = FindExecutable() ?? throw new TailscaleException( "Tailscale is not installed. Install it from https://tailscale.com/download, sign in, then try again." );
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
		timeout.CancelAfter( CommandTimeout );
		try
		{
			return await LocalWorkspaceHost.Shared.RunAsync( executable, arguments, AppContext.BaseDirectory, null, null, timeout.Token ).ConfigureAwait( false );
		}
		catch ( OperationCanceledException ) when ( !cancellationToken.IsCancellationRequested )
		{
			throw new TailscaleException( $"'tailscale {string.Join( ' ', arguments )}' did not finish within {CommandTimeout.TotalSeconds:0} s. Serve needs HTTPS certificates, and Funnel its node attribute, enabled in the Tailscale admin console." );
		}
	}

	/// <summary>Runs a Serve or Funnel command, reading its output as it comes: when Tailscale prints the page that enables Serve or Funnel for
	/// this tailnet and then waits for it, the command is stopped at once and the page is reported.</summary>
	/// <remarks>
	/// Tailscale does not exit while that page is unvisited, so waiting for the command lost the page and only a timeout was left to report.
	/// Serve and Funnel always run on this PC, so the process is started here rather than through a workspace host.
	/// </remarks>
	/// <exception cref="TailscaleException">Tailscale is not installed, waits for a page to enable Serve or Funnel, or did not finish in time.</exception>
	private static async Task<WorkspaceProcessResult> RunApplyAsync( IReadOnlyList<string> arguments, CancellationToken cancellationToken )
	{
		var executable = FindExecutable() ?? throw new TailscaleException( "Tailscale is not installed. Install it from https://tailscale.com/download, sign in, then try again." );
		var start = new System.Diagnostics.ProcessStartInfo( executable )
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		foreach ( var argument in arguments )
			start.ArgumentList.Add( argument );

		using var process = System.Diagnostics.Process.Start( start ) ?? throw new TailscaleException( "Tailscale could not be started." );
		var printed = new System.Text.StringBuilder();
		var page = new TaskCompletionSource<string>( TaskCreationOptions.RunContinuationsAsynchronously );
		void Read( string? line )
		{
			if ( line is null )
				return;
			lock ( printed )
				printed.AppendLine( line );
			if ( EnablePage().Match( line ) is { Success: true } match )
				page.TrySetResult( match.Value );
		}
		process.OutputDataReceived += ( _, e ) => Read( e.Data );
		process.ErrorDataReceived += ( _, e ) => Read( e.Data );
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
		timeout.CancelAfter( CommandTimeout );
		var exited = process.WaitForExitAsync( timeout.Token );
		try
		{
			if ( await Task.WhenAny( exited, page.Task ).ConfigureAwait( false ) == page.Task )
			{
				process.Kill( entireProcessTree: true );
				var url = await page.Task.ConfigureAwait( false );
				throw new TailscaleException( url.Contains( "/f/funnel", StringComparison.Ordinal )
					? $"Funnel is not enabled for this tailnet. Allow it at {url}, then choose Check again."
					: $"HTTPS is not enabled for this tailnet. Allow it at {url}, then choose Check again." );
			}

			await exited.ConfigureAwait( false );
			// The exit can come before the last output lines; this waits for both streams to end.
			process.WaitForExit();
		}
		catch ( OperationCanceledException ) when ( !cancellationToken.IsCancellationRequested )
		{
			process.Kill( entireProcessTree: true );
			throw new TailscaleException( $"'tailscale {string.Join( ' ', arguments )}' did not finish within {CommandTimeout.TotalSeconds:0} s." );
		}
		catch ( OperationCanceledException )
		{
			process.Kill( entireProcessTree: true );
			throw;
		}

		lock ( printed )
			return new( process.ExitCode, printed.ToString(), "" );
	}

	/// <summary>Reads the first non-empty line a failed command printed, preferring its error output.</summary>
	private static string ReadFirstLine( WorkspaceProcessResult result ) =>
		( result.Error + "\n" + result.Output ).Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ).FirstOrDefault() ?? $"exit code {result.Code}";

	/// <summary>Reads a string property, or null when it is missing or not a string.</summary>
	private static string? ReadString( JsonElement element, string name ) =>
		element.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	/// <summary>Matches the <c>--bg</c> flag in Serve's help.</summary>
	[GeneratedRegex( @"(^|[\s,])--?bg\b", RegexOptions.Multiline )]
	private static partial Regex BackgroundFlag();

	/// <summary>Matches the first https address in a command's output.</summary>
	[GeneratedRegex( @"https://\S+" )]
	private static partial Regex HttpsLink();

	/// <summary>Matches the admin page Tailscale prints, then waits on, when Serve (HTTPS) or Funnel is not enabled for the tailnet.</summary>
	[GeneratedRegex( @"https://login\.tailscale\.com/f/\S+" )]
	private static partial Regex EnablePage();
}

/// <summary>Tailscale's state as <c>tailscale status --json</c> reports it.</summary>
/// <param name="BackendState">The state, such as <c>Running</c>, <c>NeedsLogin</c> or <c>Stopped</c>.</param>
/// <param name="DnsName">The PC's MagicDNS name without the trailing dot, or null.</param>
/// <param name="Addresses">The PC's tailnet addresses, IPv4 and IPv6.</param>
/// <param name="AuthUrl">The sign-in address while signing in, or null.</param>
/// <param name="HasCertificates">Whether HTTPS certificates are on for the tailnet, so Serve and Funnel can use HTTPS.</param>
public sealed record TailscaleStatus( string BackendState, string? DnsName, IReadOnlyList<string> Addresses, string? AuthUrl, bool HasCertificates = false )
{
	/// <summary>Gets whether Tailscale is connected and signed in.</summary>
	public bool IsRunning => BackendState == "Running";

	/// <summary>Gets the PC's stable node id, which the page that allows Funnel names, or null.</summary>
	public string? NodeId { get; init; }

	/// <summary>Gets the node capabilities the tailnet grants this PC, such as <c>https</c> and <c>funnel</c>.</summary>
	public IReadOnlyList<string> Capabilities { get; init; } = [];

	/// <summary>Gets whether the tailnet has HTTPS certificates on, which Serve and Funnel need.</summary>
	public bool HasHttps => HasCertificates || Capabilities.Contains( "https" );

	/// <summary>Gets whether Funnel is allowed for this PC.</summary>
	public bool HasFunnel => Capabilities.Contains( "funnel" ) || Capabilities.Contains( "https://tailscale.com/cap/funnel" );
}

/// <summary>Tailscale is not installed, not running or not signed in, or refused a command; the message is written for the user.</summary>
/// <param name="message">What went wrong and what to do.</param>
public sealed class TailscaleException( string message ) : InvalidOperationException( message );
