namespace Lokn.Core;

/// <summary>Names how the daemon is doing, which its icon in the notification area shows as a coloured dot.</summary>
public enum HostState
{
	/// <summary>The daemon runs and no chat waits for the user: a green dot.</summary>
	Running,

	/// <summary>At least one chat waits for the user's answer: an orange dot.</summary>
	NeedsYou,

	/// <summary>The daemon reports an error, such as a network mode that did not start: a red dot.</summary>
	Error,
}

/// <summary>What the daemon, <c>Loom.exe --host</c>, shows in the notification area: its agents, the chats waiting for the user, the connected
/// devices and its error (docs/remote.md, Roles).</summary>
/// <remarks>The Remote module publishes it in the daemon with <see cref="LoknApplication.PublishHostStatus"/>; the daemon's loop reads
/// <see cref="LoknApplication.HostStatus"/>. The texts are computed here, so the tooltip, the menu and the checks agree.</remarks>
/// <param name="Working">How many chats have an agent working.</param>
/// <param name="Waiting">How many chats wait for the user's answer.</param>
/// <param name="Devices">How many other devices are connected, not counting Loom on this PC.</param>
/// <param name="IsLocalConnected">Whether Loom on this PC is connected over the pipe.</param>
/// <param name="Error">The error the daemon reports, or null.</param>
public sealed record HostStatus( int Working, int Waiting, int Devices, bool IsLocalConnected, string? Error )
{
	/// <summary>Gets whether the daemon shows its icon in the notification area (the <c>remote.tray</c> setting).</summary>
	public bool IsTrayShown { get; init; } = true;

	/// <summary>Gets whether the daemon's sign-in entry can be changed: the user's own daemon while chats run in it, never a bare one.</summary>
	public bool CanStartAtLogin
	{
		get; init;
	}

	/// <summary>Gets the name of the newest device that paired with view only and waits for the user to allow control or keep it so, or null.</summary>
	public string? PendingPairing
	{
		get; init;
	}

	/// <summary>Gets the id of the device <see cref="PendingPairing"/> names, so its notification shows once per device; null with it.</summary>
	public string? PendingPairingId
	{
		get; init;
	}

	/// <summary>Gets how many paired devices wait for the user's answer.</summary>
	public int PendingPairings
	{
		get; init;
	}

	/// <summary>Gets the state the icon's dot shows: an error first, then a chat waiting for the user.</summary>
	public HostState State => Error is not null ? HostState.Error : Waiting > 0 ? HostState.NeedsYou : HostState.Running;

	/// <summary>Gets the activity line: <c>2 agents working · 1 device connected</c>, with <c> · 1 waiting for you</c> while a chat waits.</summary>
	public string Activity
	{
		get
		{
			var line = $"{Count( Working, "agent", "agents" )} working · {Count( Devices, "device", "devices" )} connected";
			return Waiting > 0 ? $"{line} · {Waiting} waiting for you" : line;
		}
	}

	/// <summary>Gets whether Loom on this PC shows the daemon's chats now.</summary>
	public string Connection => IsLocalConnected ? "Loom on this PC is connected" : "Loom on this PC is not open";

	/// <summary>Gets what the tooltip shows after <c>Loom: </c>: the error, else the activity line.</summary>
	public string Summary => Error ?? Activity;

	/// <summary>Gets the pairing notification's title: <c>Phone paired with this PC</c>.</summary>
	public string PairingBalloonTitle => $"{PendingPairing ?? "A device"} paired with this PC";

	/// <summary>Gets the pairing notification's text, which sends the user to Loom, where the answer is given.</summary>
	public string PairingBalloon => "It can view your chats. Open Loom to allow control.";

	/// <summary>Gets the menu's pairing line, <c>1 pairing waiting for you</c>, or null while no device waits.</summary>
	public string? PairingLine => PendingPairings switch
	{
		0 => null,
		1 => "1 pairing waiting for you",
		_ => $"{PendingPairings} pairings waiting for you",
	};

	/// <summary>Writes a count with its noun in the singular or plural.</summary>
	private static string Count( int count, string one, string many ) => $"{count} {(count == 1 ? one : many)}";
}

/// <summary>Turns the daemon's sign-in entry on or off the way the Remote access panel does: through the <c>remote.login-item</c> setting,
/// which the Remote module keeps in the user's Run key.</summary>
/// <remarks>The Remote module publishes it with <see cref="ModuleBuilder.Service{T}"/>; the daemon's notification-area menu resolves it with
/// <c>app.Services.Get&lt;IHostLoginItem&gt;()</c> on each use.</remarks>
public interface IHostLoginItem
{
	/// <summary>Sets whether the daemon starts at sign-in; on the UI thread.</summary>
	/// <param name="isOn">Whether it should.</param>
	void Set( bool isOn );
}
