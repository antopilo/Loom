using Lokn.Core;

namespace Lokn;

/// <summary>Names what an item of the daemon's notification-area menu does; the number is the native menu item's identifier.</summary>
internal enum HostTrayCommand
{
	/// <summary>An information line or a separator, which does nothing.</summary>
	None,

	/// <summary>Opens Loom.</summary>
	Open,

	/// <summary>Opens Loom on the Remote access panel.</summary>
	RemoteAccess,

	/// <summary>Turns the daemon's sign-in entry on or off.</summary>
	StartAtLogin,

	/// <summary>Stops the daemon.</summary>
	Stop,
}

/// <summary>One item of the daemon's notification-area menu.</summary>
/// <param name="Command">What it does.</param>
/// <param name="Text">What it says; empty for a separator.</param>
/// <param name="IsEnabled">Whether it can be chosen.</param>
/// <param name="IsChecked">Whether it shows a check mark.</param>
internal sealed record HostTrayItem( HostTrayCommand Command, string Text, bool IsEnabled = true, bool IsChecked = false )
{
	/// <summary>Gets a separator line.</summary>
	public static HostTrayItem Separator { get; } = new( HostTrayCommand.None, "", IsEnabled: false );

	/// <summary>Gets whether this is a separator line.</summary>
	public bool IsSeparator => Text.Length == 0;
}

/// <summary>Builds what the daemon's icon in the notification area says: its menu, its tooltip and its notification (docs/remote.md, Roles).</summary>
/// <remarks>Pure functions of a <see cref="HostStatus"/>, so the checks cover them without a desktop; <c>HostTray</c> shows them.</remarks>
internal static class HostTrayMenu
{
	/// <summary>Defines the link <see cref="HostTrayCommand.RemoteAccess"/> opens Loom on: Remote access in Settings.</summary>
	internal const string RemoteAccessLink = "lokn://remote/role";

	/// <summary>Defines the notification's title when a chat starts waiting for the user.</summary>
	internal const string BalloonTitle = "An agent needs you";

	/// <summary>Defines the longest tooltip Windows shows, without its terminating null.</summary>
	internal const int TipLength = 127;

	/// <summary>Defines the longest error line the menu shows.</summary>
	private const int ErrorLength = 80;

	/// <summary>Builds the menu: Open Loom, the activity and connection lines (and the error, and the pairings waiting for an answer), Remote access,
	/// Start at login, then Stop daemon.</summary>
	/// <param name="status">The daemon's status.</param>
	/// <param name="isStartAtLogin">Whether the daemon's sign-in entry exists in the user's Run key.</param>
	internal static IReadOnlyList<HostTrayItem> Build( HostStatus status, bool isStartAtLogin )
	{
		List<HostTrayItem> items =
		[
			new( HostTrayCommand.Open, "Open Loom" ),
			new( HostTrayCommand.None, status.Activity, IsEnabled: false ),
			new( HostTrayCommand.None, status.Connection, IsEnabled: false ),
		];
		if ( status.Error is { } error )
			items.Add( new( HostTrayCommand.None, Shorten( error, ErrorLength ), IsEnabled: false ) );
		if ( status.PairingLine is { } pairing )
			items.Add( new( HostTrayCommand.None, pairing, IsEnabled: false ) );
		items.Add( new( HostTrayCommand.RemoteAccess, "Remote access\u2026" ) );
		items.Add( new( HostTrayCommand.StartAtLogin, "Start at login", status.CanStartAtLogin, status.CanStartAtLogin && isStartAtLogin ) );
		items.Add( HostTrayItem.Separator );
		items.Add( new( HostTrayCommand.Stop, "Stop daemon" ) );
		return items;
	}

	/// <summary>Builds the tooltip, <c>Loom: </c> and the status's summary, shortened to what Windows shows.</summary>
	internal static string BuildTip( HostStatus status ) => Shorten( "Loom: " + status.Summary, TipLength );

	/// <summary>Builds the notification's text when a chat starts waiting for the user.</summary>
	internal static string BuildBalloon( HostStatus status ) =>
		(status.Waiting == 1 ? "A chat is waiting for your answer." : $"{status.Waiting} chats are waiting for your answer.") + " Click to open Loom.";

	/// <summary>Decides whether the icon announces the newest device waiting for a pairing answer: once per device, and only while Loom on this PC is
	/// not connected, since Loom asks itself then. A device first seen while Loom is connected is never announced later.</summary>
	/// <param name="status">The daemon's status, or null.</param>
	/// <param name="shown">The devices already announced or seen; the newest pending device is added.</param>
	internal static bool ShouldShowPairing( HostStatus? status, HashSet<string> shown ) =>
		status?.PendingPairingId is { } deviceId && shown.Add( deviceId ) && !status.IsLocalConnected;

	/// <summary>Shortens a text to a length, ending it with an ellipsis when cut.</summary>
	private static string Shorten( string text, int length ) => text.Length <= length ? text : text[..(length - 1)] + "\u2026";
}
