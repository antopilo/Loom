using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>Turns the daemon's sign-in entry on or off for the daemon's notification-area menu the way the Remote access panel's switch does: it
/// sets <see cref="RemoteModule.LoginItem"/>, and the module writes the Run value from the setting.</summary>
/// <param name="module">The module whose running context holds the settings.</param>
internal sealed class HostLoginItem( RemoteModule module ) : IHostLoginItem
{
	/// <summary>Sets whether the daemon starts at sign-in; on the UI thread while the module runs.</summary>
	public void Set( bool isOn ) => module.Context.App.Settings.Set( RemoteModule.LoginItem, SettingsScope.Global, isOn );
}
