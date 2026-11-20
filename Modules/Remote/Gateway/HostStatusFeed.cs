using Lokn.Core;
using Lokn.Modules.Chat;
using Lokn.Modules.Conversations;

namespace Lokn.Modules.Remote;

/// <summary>Publishes the daemon's <see cref="HostStatus"/>, which its icon in the notification area shows, on the daemon's UI thread whenever an
/// agent starts or stops working, a chat's questions change, a device connects, leaves or waits for a pairing answer, the network's error changes or
/// a setting changes.</summary>
internal sealed class HostStatusFeed : IDisposable
{
	/// <summary>Stores the daemon's application, which carries the status.</summary>
	private readonly LoknApplication _app;

	/// <summary>Stores the daemon's conversation store, whose working set counts the agents.</summary>
	private readonly ConversationStore _store;

	/// <summary>Stores the daemon's chat sessions, whose pending questions count the chats waiting for the user.</summary>
	private readonly ChatSessionService _sessions;

	/// <summary>Stores the gateway whose connections count the devices and whose network reports the error.</summary>
	private readonly HostGateway _gateway;

	/// <summary>Stores whether this is the user's own daemon, whose sign-in entry the menu can change; a bare daemon's never.</summary>
	private readonly bool _isUserDaemon;

	/// <summary>Starts publishing at once and on every change.</summary>
	/// <param name="app">The daemon's application.</param>
	/// <param name="local">This PC's chats, whose working set counts the agents and whose pending questions count the chats waiting for the user.</param>
	/// <param name="gateway">The started gateway.</param>
	/// <param name="isUserDaemon">Whether this is the user's own daemon rather than a bare one or a check's.</param>
	internal HostStatusFeed( LoknApplication app, ChatHost local, HostGateway gateway, bool isUserDaemon )
	{
		_app = app;
		_store = local.Store;
		_sessions = local.Sessions;
		_gateway = gateway;
		_isUserDaemon = isUserDaemon;
		_store.WorkingChanged += UpdateWorking;
		_sessions.WaitingChanged += Publish;
		_gateway.Changed += Publish;
		_app.Settings.Changed += Publish;
		Publish();
	}

	/// <summary>Stops following the daemon and withdraws the status.</summary>
	public void Dispose()
	{
		_store.WorkingChanged -= UpdateWorking;
		_sessions.WaitingChanged -= Publish;
		_gateway.Changed -= Publish;
		_app.Settings.Changed -= Publish;
		_app.PublishHostStatus( null );
	}

	/// <summary>Computes the status the icon shows from what the daemon holds now.</summary>
	/// <param name="working">How many chats have an agent working.</param>
	/// <param name="waiting">How many chats wait for the user's answer.</param>
	/// <param name="deviceIds">The device of each open connection, <see cref="HostGateway.LocalDevice"/> for Loom on this PC; a device with several
	/// connections counts once.</param>
	/// <param name="error">The network's error, or null.</param>
	/// <param name="isTrayShown">Whether the icon shows (the <see cref="RemoteModule.Tray"/> setting).</param>
	/// <param name="canStartAtLogin">Whether the menu can change the sign-in entry.</param>
	/// <param name="pending">The devices waiting for the user's answer to their pairing, or none.</param>
	internal static HostStatus Compute( int working, int waiting, IEnumerable<string> deviceIds, string? error, bool isTrayShown, bool canStartAtLogin,
		IReadOnlyList<PendingDevice>? pending = null )
	{
		var devices = deviceIds.ToHashSet( StringComparer.Ordinal );
		var isLocalConnected = devices.Remove( HostGateway.LocalDevice );
		var newest = pending?.MaxBy( device => device.Paired );
		return new( working, waiting, devices.Count, isLocalConnected, string.IsNullOrWhiteSpace( error ) ? null : error )
		{
			IsTrayShown = isTrayShown,
			CanStartAtLogin = canStartAtLogin,
			PendingPairing = newest?.Name,
			PendingPairingId = newest?.DeviceId,
			PendingPairings = pending?.Count ?? 0,
		};
	}

	/// <summary>Publishes again after an agent started or stopped.</summary>
	private void UpdateWorking( Guid conversationId, bool isWorking ) => Publish();

	/// <summary>Publishes the current status; the application raises nothing when it did not change.</summary>
	private void Publish()
	{
		var settings = _app.Settings;
		_app.PublishHostStatus( Compute( _store.WorkingCount, _sessions.Waiting.Count, _gateway.Connections.Select( connection => connection.DeviceId ),
			_gateway.Network.Error, settings.Get( RemoteModule.Tray, SettingsScope.Global ),
			_isUserDaemon && settings.Get( RemoteModule.Role, SettingsScope.Global ) == RemoteModule.DaemonRole, _gateway.Pending ) );
	}
}
