using System.Net.WebSockets;

namespace Lokn.Modules.Remote;

/// <summary>Pairs this PC with a host from a pairing link (moved out of RemoteClient.PairAsync), and dials a host's addresses and relay.</summary>
internal static class HostPairing
{
	/// <summary>Defines how long one address may take to connect and complete the handshake.</summary>
	private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds( 10 );

	/// <summary>Defines how long a private address, such as the host's LAN address, may take to answer: on another network it never answers, and the
	/// public address or relay after it should not wait long.</summary>
	private static readonly TimeSpan PrivateDialTimeout = TimeSpan.FromSeconds( 2 );

	/// <summary>Defines how long a dial waits for hosts on this network to answer a discovery query.</summary>
	private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds( 1.5 );

	/// <summary>Gets the device key file in a data folder, which this PC pairs and connects with.</summary>
	internal static string ComputeDeviceKeyPath( string dataFolder ) => Path.Combine( dataFolder, "device-key.pem" );

	/// <summary>Pairs this PC with the host a pairing link names, under a new device id, dialing each of its addresses and then its relay. The entry is not
	/// saved; the caller adds it to <see cref="PairedHosts"/>.</summary>
	/// <param name="dataFolder">The data folder this PC's device key and own host key live in.</param>
	/// <param name="linkText">The pairing link, web client URL or payload.</param>
	/// <param name="cancellationToken">Cancels pairing.</param>
	/// <returns>The new entry, without a name, and the scopes the host granted.</returns>
	/// <exception cref="ArgumentException">The text holds no pairing link, or the link is this PC's own.</exception>
	/// <exception cref="RemoteException">The host refused, such as for a used or expired link.</exception>
	/// <exception cref="IOException">No address of the host answered.</exception>
	public static async Task<(PairedHost Host, IReadOnlyList<string> Scopes)> PairAsync( string dataFolder, string linkText, CancellationToken cancellationToken )
	{
		var link = PairingLink.TryParse( linkText ) ?? throw new ArgumentException( "That is not a Loom pairing link." );
		var hostKeyPath = Path.Combine( dataFolder, "host-key.pem" );
		if ( File.Exists( hostKeyPath ) )
		{
			using var own = RemoteKeys.LoadOrCreate( hostKeyPath );
			if ( own.HostId == link.HostId )
				throw new ArgumentException( "That pairing link is this PC's." );
		}

		using var keys = RemoteKeys.LoadOrCreate( ComputeDeviceKeyPath( dataFolder ) );
		var deviceId = Guid.NewGuid().ToString( "N" );
		var (scopes, _) = await DialEachAsync( link.Addrs, link.Relay, link.HostId,
			( carrier, token ) => SecureChannel.PairAsync( carrier, keys, deviceId, Environment.MachineName, link, token ), cancellationToken ).ConfigureAwait( false );
		return (new PairedHost( link.HostId, link.HostKey, link.Addrs, link.Relay, deviceId, "", DateTimeOffset.UtcNow ), scopes);
	}

	/// <summary>Asks a host on this network to pair without a link, under a new device id, and waits until its user answers. The entry is not saved; the
	/// caller adds it to <see cref="PairedHosts"/>.</summary>
	/// <param name="dataFolder">The data folder this PC's device key lives in.</param>
	/// <param name="host">The host, as discovery found it.</param>
	/// <param name="cancellationToken">Cancels the request.</param>
	/// <returns>The new entry and the scopes the host's user granted.</returns>
	/// <exception cref="RemoteException">The host's user declined, or did not answer in time.</exception>
	/// <exception cref="IOException">No address of the host answered.</exception>
	public static async Task<(PairedHost Host, IReadOnlyList<string> Scopes)> KnockAsync( string dataFolder, NearbyHost host, CancellationToken cancellationToken )
	{
		using var keys = RemoteKeys.LoadOrCreate( ComputeDeviceKeyPath( dataFolder ) );
		var deviceId = Guid.NewGuid().ToString( "N" );
		var failures = new List<string>();
		foreach ( var addr in host.Addrs )
		{
			MessageConnection carrier;
			using ( var connect = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken ) )
			{
				connect.CancelAfter( PrivateDialTimeout );
				try
				{
					carrier = await WebSocketDialer.ConnectAsync( WebSocketDialer.BuildHostUri( addr, RemoteModule.Port.DefaultValue ), connect.Token ).ConfigureAwait( false );
				}
				catch ( Exception exception ) when ( exception is WebSocketException or IOException or OperationCanceledException or UriFormatException && !cancellationToken.IsCancellationRequested )
				{
					failures.Add( $"{addr}: {exception.Message}" );
					continue;
				}
			}
			// The host's user answers on the host, so the request waits as long as the host lets it.
			using var answer = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
			answer.CancelAfter( SecureChannel.KnockTimeout + TimeSpan.FromSeconds( 10 ) );
			var scopes = await SecureChannel.KnockAsync( carrier, keys, deviceId, Environment.MachineName, host.HostId, answer.Token ).ConfigureAwait( false );
			return (new PairedHost( host.HostId, host.HostKey, host.Addrs, null, deviceId, "", DateTimeOffset.UtcNow ), scopes);
		}
		throw new IOException( "The PC did not answer. " + string.Join( "; ", failures ) );
	}

	/// <summary>Opens a WebSocket to each address and then the relay until <paramref name="run"/> completes on one; a refusal from the host ends the attempts.</summary>
	/// <returns>What <paramref name="run"/> answered, and the address that answered, or null for the relay.</returns>
	/// <exception cref="IOException">No address answered.</exception>
	public static async Task<(T Result, string? Address)> DialEachAsync<T>( IReadOnlyList<string> addrs, string? relay, string hostId, Func<MessageConnection, CancellationToken, Task<T>> run, CancellationToken cancellationToken )
	{
		var failures = new List<string>();
		async Task<(bool IsDone, T Result)> TryAsync( string addr )
		{
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
			timeout.CancelAfter( IsPrivate( addr ) ? PrivateDialTimeout : DialTimeout );
			try
			{
				var carrier = await WebSocketDialer.ConnectAsync( WebSocketDialer.BuildHostUri( addr, RemoteModule.Port.DefaultValue ), timeout.Token ).ConfigureAwait( false );
				return (true, await run( carrier, timeout.Token ).ConfigureAwait( false ));
			}
			catch ( Exception exception ) when ( exception is WebSocketException or IOException or OperationCanceledException or UriFormatException && !cancellationToken.IsCancellationRequested )
			{
				failures.Add( $"{addr}: {exception.Message}" );
				return (false, default!);
			}
		}

		foreach ( var addr in addrs )
		{
			if ( await TryAsync( addr ).ConfigureAwait( false ) is (true, var result) )
				return (result, addr);
		}

		// None of the saved addresses answered: the host may be on this network at a new address, which it gives when asked by its id.
		foreach ( var addr in (await LanDiscovery.FindAsync( hostId, DiscoveryTimeout, cancellationToken ).ConfigureAwait( false )).Except( addrs, StringComparer.Ordinal ) )
		{
			if ( await TryAsync( addr ).ConfigureAwait( false ) is (true, var result) )
				return (result, addr);
		}

		if ( relay is { Length: > 0 } )
		{
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
			timeout.CancelAfter( DialTimeout );
			try
			{
				var carrier = await RelayDialer.ConnectAsync( relay, hostId, timeout.Token ).ConfigureAwait( false );
				return (await run( carrier, timeout.Token ).ConfigureAwait( false ), null);
			}
			catch ( Exception exception ) when ( exception is WebSocketException or IOException or OperationCanceledException or UriFormatException && !cancellationToken.IsCancellationRequested )
			{
				failures.Add( $"relay: {exception.Message}" );
			}
		}

		throw new IOException( failures.Count == 0 ? "The pairing names no address of the host." : "The host did not answer. " + string.Join( "; ", failures ) );
	}

	/// <summary>Reports whether an address is a bare private IP with its port, such as a host's LAN address, rather than a name or a ws(s) address.</summary>
	private static bool IsPrivate( string address )
	{
		var host = address.StartsWith( '[' ) ? address[1..Math.Max( 1, address.IndexOf( ']' ) )] : address.Split( ':' )[0];
		return System.Net.IPAddress.TryParse( host, out var ip ) && NetworkAddresses.IsPrivate( ip );
	}
}
