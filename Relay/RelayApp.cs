using Microsoft.AspNetCore.HttpOverrides;

namespace Lokn.Relay;

/// <summary>Builds the relay's web application: the host control, host data and device WebSocket endpoints, and the web client under <c>/h/{hostId}/</c>.</summary>
public static class RelayApp
{
	/// <summary>Defines how often the relay pings idle sockets so proxies and NATs keep them open.</summary>
	private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds( 30 );

	/// <summary>Builds a startable relay listening on <paramref name="urls"/>, or on its configured endpoints when empty.</summary>
	public static WebApplication Build( string[] urls ) => Build( urls, [] );

	/// <summary>Builds a startable relay listening on <paramref name="urls"/>, or on its configured endpoints when empty, with command-line configuration <paramref name="args"/>.</summary>
	public static WebApplication Build( string[] urls, string[] args )
	{
		// Configuration (appsettings.json, Kestrel endpoints and certificates) lives beside the binary, wherever a service manager starts it.
		var builder = WebApplication.CreateBuilder( new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory } );
		builder.Host.UseWindowsService();
		builder.Host.UseSystemd();
		if ( urls.Length > 0 )
			builder.WebHost.UseUrls( urls );
		builder.Services.AddSingleton<RelayHub>();
		var app = builder.Build();
		var hub = app.Services.GetRequiredService<RelayHub>();
		// Behind a reverse proxy on loopback, per-client limits use the address the proxy forwards; other senders cannot set it.
		app.UseForwardedHeaders( new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto } );
		app.UseWebSockets( new WebSocketOptions { KeepAliveInterval = KeepAlive, KeepAliveTimeout = KeepAlive } );
		app.Map( "/host/{hostId}", ( HttpContext context, string hostId ) => hub.RunHostAsync( context, hostId ) );
		app.Map( "/host/{hostId}/{sid}", ( HttpContext context, string hostId, string sid ) => hub.RunHostDataAsync( context, hostId, sid ) );
		app.Map( "/device/{hostId}", ( HttpContext context, string hostId ) => hub.RunDeviceAsync( context, hostId ) );
		RelayWebClient.Map( app );
		return app;
	}
}
