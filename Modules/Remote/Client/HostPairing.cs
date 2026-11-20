using System.Net.WebSockets;

namespace Lokn.Modules.Remote;

/// <summary>Pairs this PC with a host from a pairing link (moved out of RemoteClient.PairAsync), and dials a host's addresses and relay.</summary>
internal static class HostPairing
{
	/// <summary>Defines how long one address may take to connect and complete the handshake.</summary>
	private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds( 10 );

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

	/// <summary>Opens a WebSocket to each address and then the relay until <paramref name="run"/> completes on one; a refusal from the host ends the attempts.</summary>
	/// <returns>What <paramref name="run"/> answered, and the address that answered, or null for the relay.</returns>
	/// <exception cref="IOException">No address answered.</exception>
	public static async Task<(T Result, string? Address)> DialEachAsync<T>( IReadOnlyList<string> addrs, string? relay, string hostId, Func<MessageConnection, CancellationToken, Task<T>> run, CancellationToken cancellationToken )
	{
		var failures = new List<string>();
		foreach ( var addr in addrs )
		{
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
			timeout.CancelAfter( DialTimeout );
			try
			{
				var carrier = await WebSocketDialer.ConnectAsync( WebSocketDialer.BuildHostUri( addr, RemoteModule.Port.DefaultValue ), timeout.Token ).ConfigureAwait( false );
				return (await run( carrier, timeout.Token ).ConfigureAwait( false ), addr);
			}
			catch ( Exception exception ) when ( exception is WebSocketException or IOException or OperationCanceledException or UriFormatException && !cancellationToken.IsCancellationRequested )
			{
				failures.Add( $"{addr}: {exception.Message}" );
			}
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
}
