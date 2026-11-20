using System.Text.Json.Nodes;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>Answers the <c>admin.*</c> requests the Remote access panel of Loom on this PC sends over the named pipe: status, pairing, the answer to a
/// pending pairing, devices, scopes, revocation and the network mode (docs/remote.md, Admin frames). Any other device gets <c>scope</c>.</summary>
/// <param name="gateway">The gateway the requests manage.</param>
internal sealed class AdminService( HostGateway gateway ) : IHostService
{
	/// <summary>Registers the admin handlers; each refuses a connection that is not <see cref="HostGateway.LocalDevice"/>.</summary>
	public void Register( RemoteConnection connection )
	{
		connection.Peer
			.On( "admin.status", frame => RunAsync( connection, BuildStatusAsync ) )
			.On( "admin.pair", frame => RunAsync( connection, () => BuildPairingAsync( connection, frame ) ) )
			.On( "admin.confirm", frame => RunAsync( connection, () => UpdateConfirmAsync( frame ) ) )
			.On( "admin.unpair", frame => RunAsync( connection, UpdateUnpair ) )
			.On( "admin.devices", frame => RunAsync( connection, () => Task.FromResult( BuildDevices() ) ) )
			.On( "admin.scopes", frame => RunAsync( connection, () => UpdateScopes( frame ) ) )
			.On( "admin.revoke", frame => RunAsync( connection, () => UpdateRevoke( frame ) ) )
			.On( "admin.network", frame => RunAsync( connection, () => UpdateNetworkAsync( frame ) ) );
	}

	/// <summary>Runs an admin request for Loom on this PC only.</summary>
	private static async Task<JsonObject?> RunAsync( RemoteConnection connection, Func<Task<JsonObject>> request )
	{
		if ( connection.DeviceId != HostGateway.LocalDevice )
			throw new RemoteException( RemoteErrors.Scope, "Only Loom on the host's PC manages remote access." );
		return await request().ConfigureAwait( false );
	}

	/// <summary>Answers <c>admin.status</c>: the role, the network mode and what it reaches, the host id, the versions, the live pairing offer and
	/// whether it grants control, the connected devices, and the devices waiting for an answer to their pairing.</summary>
	private async Task<JsonObject> BuildStatusAsync()
	{
		var settings = await UiInvoke.RunAsync( gateway.Post, () => RemoteSettings.Read( gateway.App.Settings ) ).ConfigureAwait( false );
		var network = gateway.Network;
		var status = new JsonObject
		{
			["role"] = RemoteModule.HostRole,
			["network"] = settings.Network,
			["port"] = settings.Port,
			["relayUrl"] = settings.RelayUrl,
			["addresses"] = Frame.BuildStrings( network.Addresses ),
			["hostId"] = gateway.Keys.HostId,
			["hostName"] = Environment.MachineName,
			["pid"] = Environment.ProcessId,
			["version"] = RemoteProtocol.AppVersion,
			["connected"] = new JsonArray( [.. gateway.Connections.Select( connection => (JsonNode)new JsonObject
			{
				["deviceId"] = connection.DeviceId,
				["name"] = connection.Name,
				["control"] = connection.CanControl,
			} )] ),
			["pending"] = new JsonArray( [.. gateway.Pending.Select( device =>
			{
				var entry = HostGateway.BuildPairingPush( device );
				entry.Remove( "t" );
				return (JsonNode)entry;
			} )] ),
		};
		if ( network.WebUrl is { } webUrl )
			status["webUrl"] = webUrl;
		if ( network.RelayStatus is { } relayStatus )
			status["relayStatus"] = relayStatus;
		if ( network.Error is { } error )
			status["error"] = error;
		if ( gateway.LiveOffer is { } offer )
		{
			status["pairing"] = offer.Expires.ToUnixTimeSeconds();
			status["pairingControl"] = offer.Control;
		}
		return status;
	}

	/// <summary>Answers <c>admin.pair { control? }</c>: a new five-minute pairing offer for the addresses the network mode reaches, as a link and, where
	/// the web client works, its URL; it withdraws any earlier offer. With <c>control</c> the device that pairs gets control at once, which the
	/// answer's <c>control</c> confirms. The offer is withdrawn when the connection that asked closes, since the Loom that shows it is gone.</summary>
	/// <remarks>The <c>lan</c> mode's page is plain <c>http</c> on a LAN address, where browsers give no WebCrypto, so it has no web URL.</remarks>
	private Task<JsonObject> BuildPairingAsync( RemoteConnection connection, JsonObject frame )
	{
		var network = gateway.Network;
		var relay = network.Mode == "relay" && network.RelayUrl.Length > 0 ? network.RelayUrl : null;
		if ( network.Addresses.Count == 0 && relay is null )
			throw new RemoteException( RemoteErrors.Failed, "No device can reach this PC yet. Choose a network mode first." );

		var offer = gateway.CreateOffer( network.Addresses, relay, Frame.ReadBool( frame, "control" ) == true );
		connection.Peer.Closed += _ => gateway.WithdrawOffer( offer );
		var answer = new JsonObject
		{
			["link"] = offer.Link.BuildUri(),
			["expires"] = offer.Expires.ToUnixTimeSeconds(),
			["control"] = offer.Control,
		};
		if ( network.WebUrl is { } webUrl && network.Mode != "lan" )
			answer["webUrl"] = offer.Link.BuildWebUrl( webUrl );
		return Task.FromResult( answer );
	}

	/// <summary>Answers <c>admin.confirm { deviceId, control, deny? }</c>: the user's answer to a pending pairing; control grants it and reconnects the
	/// device with it, deny refuses or removes it, and either way the device stops waiting. Answers the device list.</summary>
	private async Task<JsonObject> UpdateConfirmAsync( JsonObject frame )
	{
		var deviceId = Frame.ReadString( frame, "deviceId" ) ?? throw new RemoteException( RemoteErrors.BadRequest, "admin.confirm needs deviceId." );
		var control = Frame.ReadBool( frame, "control" ) ?? throw new RemoteException( RemoteErrors.BadRequest, "admin.confirm needs control." );
		var deny = Frame.ReadBool( frame, "deny" ) == true;
		await UiInvoke.RunAsync( gateway.Post, () => gateway.Confirm( deviceId, control, deny ) ).ConfigureAwait( false );
		return BuildDevices();
	}

	/// <summary>Answers <c>admin.unpair</c>: withdraws the pairing offers, as the panel does when it hides the code.</summary>
	private Task<JsonObject> UpdateUnpair()
	{
		gateway.CancelOffers();
		return Task.FromResult( new JsonObject() );
	}

	/// <summary>Answers <c>admin.devices</c>: every paired device with its scopes, pairing time and whether it is connected.</summary>
	private JsonObject BuildDevices()
	{
		var connected = gateway.Connections.Select( connection => connection.DeviceId ).ToHashSet( StringComparer.Ordinal );
		return new JsonObject
		{
			["devices"] = new JsonArray( [.. gateway.Devices.Devices.Select( device => (JsonNode)new JsonObject
			{
				["deviceId"] = device.DeviceId,
				["name"] = device.Name,
				["scopes"] = Frame.BuildStrings( device.Scopes ),
				["paired"] = device.Paired.ToUnixTimeSeconds(),
				["connected"] = connected.Contains( device.DeviceId ),
			} )] ),
		};
	}

	/// <summary>Answers <c>admin.scopes { deviceId, control }</c>: grants or withdraws control, and reconnects the device's connections with it; a device
	/// waiting for an answer to its pairing stops waiting.</summary>
	private async Task<JsonObject> UpdateScopes( JsonObject frame )
	{
		var deviceId = Frame.ReadString( frame, "deviceId" ) ?? throw new RemoteException( RemoteErrors.BadRequest, "admin.scopes needs deviceId." );
		var control = Frame.ReadBool( frame, "control" ) ?? throw new RemoteException( RemoteErrors.BadRequest, "admin.scopes needs control." );
		if ( !gateway.Devices.SetScopes( deviceId, control ? DeviceScopes.Controlling : DeviceScopes.Paired ) )
			throw new RemoteException( RemoteErrors.BadRequest, "No device with that id is paired." );

		gateway.CloseDevice( deviceId );
		await UiInvoke.RunAsync( gateway.Post, () => gateway.ClearPending( deviceId ) ).ConfigureAwait( false );
		return BuildDevices();
	}

	/// <summary>Answers <c>admin.revoke { deviceId }</c>: forgets the device, which closes its connections.</summary>
	private Task<JsonObject> UpdateRevoke( JsonObject frame )
	{
		var deviceId = Frame.ReadString( frame, "deviceId" ) ?? throw new RemoteException( RemoteErrors.BadRequest, "admin.revoke needs deviceId." );
		if ( !gateway.Devices.Revoke( deviceId ) )
			throw new RemoteException( RemoteErrors.BadRequest, "No device with that id is paired." );
		return Task.FromResult( BuildDevices() );
	}

	/// <summary>Answers <c>admin.network { network, port, relayUrl }</c>: saves the settings and restarts the listeners, answering the new status.</summary>
	private async Task<JsonObject> UpdateNetworkAsync( JsonObject frame )
	{
		var network = Frame.ReadString( frame, "network" ) ?? throw new RemoteException( RemoteErrors.BadRequest, "admin.network needs network." );
		var port = Frame.ReadLong( frame, "port" ) is { } value and > 0 and < 65536 ? (int)value
			: throw new RemoteException( RemoteErrors.BadRequest, "admin.network needs a port from 1 to 65535." );
		var relayUrl = Frame.ReadString( frame, "relayUrl" ) ?? "";
		// The settings change on the UI thread; the restart then runs to its end on the thread pool.
		await UiInvoke.RunAsync( gateway.Post, () => gateway.ApplyNetworkSettingsAsync( network, port, relayUrl ) ).ConfigureAwait( false );
		return await BuildStatusAsync().ConfigureAwait( false );
	}
}
