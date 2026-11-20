using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>The remote settings one start of the gateway or client runs with, read once from the Global scope.</summary>
/// <param name="Role">The role of This PC's chats: <c>local</c> or <c>daemon</c>, or <c>host</c> in the daemon; a former <c>remote</c> is rewritten to <c>local</c> at start.</param>
/// <param name="Port">The host's WebSocket port.</param>
/// <param name="Network">The network mode: <c>off</c>, <c>lan</c>, <c>tailscale</c>, <c>relay</c> or <c>funnel</c>.</param>
/// <param name="RelayUrl">The relay's address, or empty.</param>
/// <param name="LoginItem">Whether the daemon starts at sign-in.</param>
internal sealed record RemoteSettings( string Role, int Port, string Network, string RelayUrl, bool LoginItem )
{
	/// <summary>Reads every remote setting from the Global scope, which is the only scope they apply in.</summary>
	internal static RemoteSettings Read( SettingsService settings ) => new(
		settings.Get( RemoteModule.Role, SettingsScope.Global ),
		settings.Get( RemoteModule.Port, SettingsScope.Global ),
		settings.Get( RemoteModule.Network, SettingsScope.Global ),
		settings.Get( RemoteModule.RelayUrl, SettingsScope.Global ),
		settings.Get( RemoteModule.LoginItem, SettingsScope.Global ) );
}
