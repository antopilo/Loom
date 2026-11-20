using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Lokn.Modules.Remote;

/// <summary>A Loom host on this network, as its discovery answer describes it.</summary>
/// <param name="HostId">The host's id.</param>
/// <param name="HostKey">The host's static public key, base64url SPKI; the host id is its hash, which <see cref="LanDiscovery"/> checks.</param>
/// <param name="Name">The host's PC name.</param>
/// <param name="Addrs">The LAN addresses it listens on, as host:port.</param>
public sealed record NearbyHost( string HostId, string HostKey, string Name, IReadOnlyList<string> Addrs );

/// <summary>Finds Loom hosts on the local network over UDP: a client broadcasts <c>loom-find:&lt;hostId&gt;</c> to find one paired host whose saved
/// addresses fail, or <c>loom-who</c> to list every host nearby; a host answers with its id, key, name and LAN addresses (docs/remote.md, Discovery).</summary>
/// <remarks>
/// A host answers only while it listens on the LAN, so nothing answers while Remote access is off. Discovery only says where a host is: an answer
/// whose key does not hash to its id is dropped, and every dial that follows checks the host's key in the secure channel.
/// </remarks>
public sealed class LanDiscovery : IDisposable
{
	/// <summary>Defines the UDP port hosts listen on for discovery queries.</summary>
	public const int Port = 47391;

	/// <summary>Defines the query for one host, followed by its id.</summary>
	private const string FindPrefix = "loom-find:";

	/// <summary>Defines the query every host answers.</summary>
	private const string WhoQuery = "loom-who";

	/// <summary>Defines how large a datagram discovery reads.</summary>
	private const int MaxDatagram = 2048;

	/// <summary>Stores what this host answers, but its addresses.</summary>
	private readonly (string HostId, string HostKey, string Name) _host;

	/// <summary>Reads the addresses the host listens on now, as host:port.</summary>
	private readonly Func<IReadOnlyList<string>> _addresses;

	/// <summary>Stops the answer loop.</summary>
	private readonly CancellationTokenSource _stop = new();

	/// <summary>Stores the socket queries arrive on, or null while not started.</summary>
	private UdpClient? _socket;

	/// <summary>Creates the host side; call <see cref="Start"/> to answer queries.</summary>
	/// <param name="hostId">The host's id.</param>
	/// <param name="hostKey">The host's static public key, base64url SPKI.</param>
	/// <param name="name">The host's PC name.</param>
	/// <param name="addresses">Reads the LAN addresses the host listens on now; an empty list sends no answer.</param>
	public LanDiscovery( string hostId, string hostKey, string name, Func<IReadOnlyList<string>> addresses )
	{
		_host = (hostId, hostKey, name);
		_addresses = addresses;
	}

	/// <summary>Starts answering queries; a port another program holds leaves discovery off, since dialing saved addresses still works.</summary>
	/// <returns>Whether discovery listens.</returns>
	public bool Start()
	{
		try
		{
			var socket = new UdpClient( AddressFamily.InterNetwork ) { EnableBroadcast = true };
			socket.Client.SetSocketOption( SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true );
			socket.Client.Bind( new IPEndPoint( IPAddress.Any, Port ) );
			_socket = socket;
		}
		catch ( SocketException )
		{
			return false;
		}

		_ = AnswerAsync( _socket, _stop.Token );
		return true;
	}

	/// <summary>Stops answering.</summary>
	public void Dispose()
	{
		_stop.Cancel();
		_socket?.Dispose();
		_stop.Dispose();
	}

	/// <summary>Reports whether a query asks this host: <c>loom-who</c>, or <c>loom-find:</c> with its id.</summary>
	internal static bool IsAsked( ReadOnlySpan<byte> datagram, string hostId )
	{
		var text = Encoding.UTF8.GetString( datagram );
		return text == WhoQuery || text == FindPrefix + hostId;
	}

	/// <summary>Builds a host's answer.</summary>
	internal static byte[] BuildAnswer( NearbyHost host ) => Encoding.UTF8.GetBytes( new JsonObject
	{
		["hostId"] = host.HostId,
		["hostKey"] = host.HostKey,
		["name"] = host.Name,
		["addrs"] = Frame.BuildStrings( host.Addrs ),
	}.ToJsonString() );

	/// <summary>Reads an answer, or null for a datagram that is no answer or whose key does not hash to its host id.</summary>
	internal static NearbyHost? ReadAnswer( ReadOnlySpan<byte> datagram )
	{
		try
		{
			if ( JsonNode.Parse( Encoding.UTF8.GetString( datagram ) ) is not JsonObject answer
				|| Frame.ReadString( answer, "hostId" ) is not { Length: > 0 } hostId
				|| Frame.ReadString( answer, "hostKey" ) is not { Length: > 0 } hostKey
				|| RemoteKeys.ComputeHostId( System.Buffers.Text.Base64Url.DecodeFromChars( hostKey ) ) != hostId )
				return null;

			return new( hostId, hostKey, Frame.ReadString( answer, "name" ) ?? "", Frame.ReadStrings( answer, "addrs" ) ?? [] );
		}
		catch ( Exception exception ) when ( exception is System.Text.Json.JsonException or FormatException )
		{
			return null;
		}
	}

	/// <summary>Finds one host by its id and answers the addresses it gave, or none.</summary>
	/// <param name="hostId">The host to find.</param>
	/// <param name="timeout">How long to wait for its answer.</param>
	/// <param name="cancellationToken">Cancels the search.</param>
	public static async Task<IReadOnlyList<string>> FindAsync( string hostId, TimeSpan timeout, CancellationToken cancellationToken ) =>
		(await SearchAsync( FindPrefix + hostId, timeout, cancellationToken ).ConfigureAwait( false ))
			.Where( host => host.HostId == hostId ).SelectMany( host => host.Addrs ).Distinct( StringComparer.Ordinal ).ToArray();

	/// <summary>Lists every host on this network that answers within the timeout, once each.</summary>
	/// <param name="timeout">How long to wait for answers.</param>
	/// <param name="cancellationToken">Cancels the search.</param>
	public static Task<IReadOnlyList<NearbyHost>> ListAsync( TimeSpan timeout, CancellationToken cancellationToken ) =>
		SearchAsync( WhoQuery, timeout, cancellationToken );

	/// <summary>Broadcasts a query on every IPv4 network of this PC and gathers the valid answers until the timeout, one per host.</summary>
	private static async Task<IReadOnlyList<NearbyHost>> SearchAsync( string query, TimeSpan timeout, CancellationToken cancellationToken )
	{
		using var socket = new UdpClient( AddressFamily.InterNetwork ) { EnableBroadcast = true };
		socket.Client.Bind( new IPEndPoint( IPAddress.Any, 0 ) );
		var bytes = Encoding.UTF8.GetBytes( query );
		foreach ( var target in ListTargets() )
		{
			try
			{
				await socket.SendAsync( bytes, new IPEndPoint( target, Port ), cancellationToken ).ConfigureAwait( false );
			}
			catch ( SocketException )
			{
				// A network that refuses broadcasts, or an address that cannot be reached, is skipped.
			}
		}

		var found = new List<NearbyHost>();
		using var wait = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
		wait.CancelAfter( timeout );
		try
		{
			while ( true )
			{
				var received = await socket.ReceiveAsync( wait.Token ).ConfigureAwait( false );
				if ( received.Buffer.Length <= MaxDatagram && ReadAnswer( received.Buffer ) is { } host && found.All( known => known.HostId != host.HostId ) )
					found.Add( host );
			}
		}
		catch ( OperationCanceledException ) when ( !cancellationToken.IsCancellationRequested )
		{
			// The wait for answers ended.
		}
		catch ( SocketException )
		{
			// The socket closed or the network went away; what came so far stands.
		}
		return found;
	}

	/// <summary>Answers each query that asks this host with its id, key, name and the LAN addresses it listens on now, to the sender.</summary>
	private async Task AnswerAsync( UdpClient socket, CancellationToken stop )
	{
		while ( !stop.IsCancellationRequested )
		{
			try
			{
				var received = await socket.ReceiveAsync( stop ).ConfigureAwait( false );
				if ( received.Buffer.Length > MaxDatagram || !IsAsked( received.Buffer, _host.HostId ) || _addresses() is not { Count: > 0 } addresses )
					continue;

				var answer = BuildAnswer( new( _host.HostId, _host.HostKey, _host.Name, addresses ) );
				await socket.SendAsync( answer, received.RemoteEndPoint, stop ).ConfigureAwait( false );
			}
			catch ( OperationCanceledException )
			{
				return;
			}
			catch ( ObjectDisposedException )
			{
				return;
			}
			catch ( SocketException )
			{
				// A sender that left before the answer; keep listening.
			}
		}
	}

	/// <summary>Lists where a query goes: the limited broadcast address, the directed broadcast address of each up IPv4 network of this PC, and, for a
	/// network of at most <see cref="MaxSweep"/> addresses, each of its other addresses, since some Wi-Fi extenders and access points pass unicast but
	/// drop broadcasts in one direction.</summary>
	private static IEnumerable<IPAddress> ListTargets()
	{
		var targets = new List<IPAddress> { IPAddress.Broadcast };
		foreach ( var network in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces() )
		{
			if ( network.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up )
				continue;
			foreach ( var unicast in network.GetIPProperties().UnicastAddresses )
			{
				if ( unicast.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback( unicast.Address ) || unicast.IPv4Mask is null )
					continue;
				var own = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian( unicast.Address.GetAddressBytes() );
				var mask = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian( unicast.IPv4Mask.GetAddressBytes() );
				var first = own & mask;
				var last = first | ~mask;
				targets.Add( ToAddress( last ) );
				if ( last - first > MaxSweep )
					continue;
				for ( var address = first + 1; address < last; address++ )
				{
					if ( address != own )
						targets.Add( ToAddress( address ) );
				}
			}
		}
		return targets.Distinct();
	}

	/// <summary>Defines the largest network whose addresses a query is also sent to one by one: a /22.</summary>
	private const uint MaxSweep = 1024;

	/// <summary>Builds an IPv4 address from its number.</summary>
	private static IPAddress ToAddress( uint address )
	{
		var bytes = new byte[4];
		System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian( bytes, address );
		return new IPAddress( bytes );
	}
}
