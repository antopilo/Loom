using Microsoft.Win32;

namespace Lokn;

/// <summary>Cleans up after Loom when the installer removes it.</summary>
/// <remarks>
/// Velopack starts Loom.exe with a hook argument when it installs, updates or uninstalls Loom, and <c>Main</c> answers it
/// first. Velopack itself removes the install folder and every shortcut that points into it, which includes the Start menu
/// shortcut the Notifications module writes. User data in <c>%LOCALAPPDATA%\Lokn</c> is kept.
/// </remarks>
internal static class Installation
{
	/// <summary>Defines the notification protocol key, written by the Notifications module's <c>NotificationIdentity</c>.</summary>
	/// <remarks>The shell cannot reference a feature module, so the name is repeated here; keep the two equal.</remarks>
	private const string NotificationProtocolKey = @"Software\Classes\lokn-notification";

	/// <summary>Removes what this install registered outside its folder, which Velopack does not know about.</summary>
	/// <remarks>The protocol is left alone when it opens another Loom, such as a development build the user ran last.</remarks>
	internal static void RemoveRegistration()
	{
		if ( !OperatingSystem.IsWindows() || Environment.ProcessPath is not { } executable )
			return;

		using ( var command = Registry.CurrentUser.OpenSubKey( NotificationProtocolKey + @"\shell\open\command" ) )
		{
			if ( command?.GetValue( "" ) is not string line || !line.Contains( executable, StringComparison.OrdinalIgnoreCase ) )
				return;
		}

		Registry.CurrentUser.DeleteSubKeyTree( NotificationProtocolKey, throwOnMissingSubKey: false );
	}
}
