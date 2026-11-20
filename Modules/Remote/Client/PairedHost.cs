namespace Lokn.Modules.Remote;

/// <summary>A host this PC paired with, one entry of <c>remote-hosts.json</c> in the data folder (<see cref="PairedHosts"/>).</summary>
/// <param name="HostId">The host's id.</param>
/// <param name="HostKey">The host's static public key, base64url SPKI.</param>
/// <param name="Addrs">The addresses the host listened on when it paired or last connected, as host:port or full ws/wss addresses.</param>
/// <param name="Relay">The relay's address, or null.</param>
/// <param name="DeviceId">The id this PC paired under, a GUID in <c>N</c> form.</param>
/// <param name="Name">The name the user gave the host in this Loom, or empty to show the name the host reports.</param>
/// <param name="Added">When this PC paired with the host.</param>
/// <param name="HostName">The name the host reported when it last connected, shown while it is offline; empty before it first connected.</param>
public sealed record PairedHost( string HostId, string HostKey, IReadOnlyList<string> Addrs, string? Relay, string DeviceId, string Name = "", DateTimeOffset Added = default,
	string HostName = "" );
