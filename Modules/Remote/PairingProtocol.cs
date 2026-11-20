using System.Runtime.Versioning;
using Lokn.Core;
using Microsoft.Win32;

namespace Lokn.Modules.Remote;

/// <summary>Lets a browser hand <c>loom://pair#…</c> links to Loom: the per-user <c>loom</c> protocol launches this executable with
/// <c>--open-link</c>, which the running window follows, and <see cref="Open"/> pairs with the host the link names in Remote access.</summary>
internal static class PairingProtocol
{
	/// <summary>Defines the scheme of native pairing links.</summary>
	internal const string Scheme = "loom";

	/// <summary>Gets whether this process may write the <c>loom</c> protocol: never in a bare instance, whose data folder is custom, since the protocol
	/// belongs to the user's own Loom.</summary>
	internal static bool CanInstall => !LoknPaths.IsCustom;

	/// <summary>Writes the <c>loom</c> protocol for the running executable unless it is current or <see cref="CanInstall"/> is false; a failure is
	/// reported, since pasting the link still works.</summary>
	[SupportedOSPlatform( "windows" )]
	internal static void Install( LoknApplication app )
	{
		if ( !CanInstall || Environment.ProcessPath is not { } executable )
			return;

		var command = $"\"{executable}\" --open-link \"%1\"";
		try
		{
			using var protocol = Registry.CurrentUser.CreateSubKey( $@"Software\Classes\{Scheme}" );
			using var open = protocol.CreateSubKey( @"shell\open\command" );
			if ( open.GetValue( "" ) as string == command )
				return;

			protocol.SetValue( "", "URL:Loom pairing link" );
			protocol.SetValue( "URL Protocol", "" );
			open.SetValue( "", command );
		}
		catch ( Exception exception ) when ( exception is UnauthorizedAccessException or IOException or System.Security.SecurityException )
		{
			app.ReportError( "Remote", exception );
		}
	}

	/// <summary>Opens Remote access to pair with the host a followed <c>loom://pair#…</c> link names; any other link is left to the next handler.</summary>
	internal static bool Open( LinkRequest request )
	{
		var text = request.Uri.OriginalString;
		if ( !string.Equals( request.Uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase ) || PairingLink.TryParse( text ) is null )
			return false;

		request.App.GetModule<RemoteModule>().OpenAccess( request.App, text );
		return true;
	}
}
