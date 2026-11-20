using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Lokn.Modules.Remote;

/// <summary>Finds a paired host on the local network when none of its saved addresses answers: the client broadcasts the host's id over UDP, and the
/// host with that id answers the addresses it listens on (docs/remote.md, Discovery).</summary>
/// <remarks>
/// Discovery only says where a host is. A spoofed answer gains nothing: the dial that follows still checks the host's key in the secure channel, and a
/// host answers only with addresses, never with its key or its chats.
/// </remarks>
public sealed class LanDiscovery : IDisposable
{
	/// <summary>Defines the UDP port hosts listen on for discovery queries.</summary>
	public const int Port = 47391;

	/// <summary>Defines the query's prefix, followed by the host id.</summary>
	private const string QueryPrefix = "loom-find:";

	/// <summary>Defines how large a datagram discovery reads or sends.</summary>
	private const int MaxDatagram = 2048;

	/// <summary>Stores the host's id, which queries must name.</summary>
	private readonly string _hostId;

	/// <summary>Reads the addresses the host listens on now, as host:port.</summary>
	private readonly Func<IReadOnlyList<string>> _addresses;

	/// <summary>Stops the answer loop.</summary>
	private readonly CancellationTokenSource _stop = new();

	/// <summary>Stores the socket queries arrive on, or null while not started.</summary>
	private UdpClient? _socket;

	/// <summary>Creates the host side; call <see cref="Start"/> to answer queries.</summary>
	/// <param name="hostId">The host's id.</param>
	/// <param name="addresses">Reads the addresses the host listens on now; an empty list sends no answer.</param>
	public LanDiscovery( string hostId, Func<IReadOnlyList<string>> addresses )
	{
		_hostId = hostId;
		_addresses = addresses;
	}

	/// <summary>Starts answering queries for this host; a port another program holds leaves discovery off, since dialing saved addresses still works.</summary>
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

	/// <summary>Builds a query for a host id.</summary>
	internal static byte[] BuildQuery( string hostId ) => Encoding.UTF8.GetBytes( QueryPrefix + hostId );

	/// <summary>Reads the host id a query names, or null when the datagram is no query.</summary>
	internal static string? ReadQuery( ReadOnlySpan<byte> datagram )
	{
		var text = Encoding.UTF8.GetString( datagram );
		return text.StartsWith( QueryPrefix, StringComparison.Ordinal ) && text.Length > QueryPrefix.Length ? text[QueryPrefix.Length..] : null;
	}

	/// <summary>Builds the answer: the host id and its addresses.</summary>
	internal static byte[] BuildAnswer( string hostId, IReadOnlyList<string> addresses ) =>
		Encoding.UTF8.GetBytes( new JsonObject { ["hostId"] = hostId, ["addrs"] = Frame.BuildStrings( addresses ) }.ToJsonString() );

	/// <summary>Reads the addresses an answer gives for a host id, or none when the datagram answers for another host or is no answer.</summary>
	internal static IReadOnlyList<string> ReadAnswer( ReadOnlySpan<byte> datagram, string hostId )
	{
		try
		{
			return JsonNode.Parse( Encoding.UTF8.GetString( datagram ) ) is JsonObject answer && Frame.ReadString( answer, "hostId" ) == hostId
				? Frame.ReadStrings( answer, "addrs" ) ?? []
				: [];
		}
		catch ( System.Text.Json.JsonException )
		{
			return [];
		}
	}

	/// <summary>Broadcasts a query for a host on every IPv4 network of this PC and gathers the addresses its answers give, until the timeout.</summary>
	/// <param name="hostId">The host to find.</param>
	/// <param name="timeout">How long to wait for answers.</param>
	/// <param name="cancellationToken">Cancels the search.</param>
	/// <returns>The addresses the host gave, in the order they came, or none.</returns>
	public static async Task<IReadOnlyList<string>> FindAsync( string hostId, TimeSpan timeout, CancellationToken cancellationToken )
	{
		using var socket = new UdpClient( AddressFamily.InterNetwork ) { EnableBroadcast = true };
		socket.Client.Bind( new IPEndPoint( IPAddress.Any, 0 ) );
		var query = BuildQuery( hostId );
		foreach ( var target in ListBroadcastAddresses() )
		{
			try
			{
				await socket.SendAsync( query, new IPEndPoint( target, Port ), cancellationToken ).ConfigureAwait( false );
			}
			catch ( SocketException )
			{
				// A network that refuses broadcasts is skipped.
			}
		}

		var found = new List<string>();
		using var wait = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
		wait.CancelAfter( timeout );
		try
		{
			while ( true )
			{
				var received = await socket.ReceiveAsync( wait.Token ).ConfigureAwait( false );
				foreach ( var address in ReadAnswer( received.Buffer, hostId ) )
				{
					if ( !found.Contains( address, StringComparer.Ordinal ) )
						found.Add( address );
				}
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

	/// <summary>Answers each query naming this host with the addresses it listens on now, to the sender.</summary>
	private async Task AnswerAsync( UdpClient socket, CancellationToken stop )
	{
		while ( !stop.IsCancellationRequested )
		{
			try
			{
				var received = await socket.ReceiveAsync( stop ).ConfigureAwait( false );
				if ( received.Buffer.Length > MaxDatagram || ReadQuery( received.Buffer ) != _hostId || _addresses() is not { Count: > 0 } addresses )
					continue;

				await socket.SendAsync( BuildAnswer( _hostId, addresses ), received.RemoteEndPoint, stop ).ConfigureAwait( false );
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

	/// <summary>Lists the directed broadcast address of each up IPv4 network of this PC, and the limited broadcast address.</summary>
	private static IEnumerable<IPAddress> ListBroadcastAddresses()
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
				var address = unicast.Address.GetAddressBytes();
				var mask = unicast.IPv4Mask.GetAddressBytes();
				for ( var index = 0; index < 4; index++ )
					address[index] |= (byte)~mask[index];
				targets.Add( new IPAddress( address ) );
			}
		}
		return targets.Distinct();
	}
}
