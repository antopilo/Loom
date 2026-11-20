using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokn.Core;

namespace Lokn.Modules.Remote;

/// <summary>A browser's web push subscription, as <c>PushSubscription.toJSON()</c> gives it.</summary>
/// <param name="Endpoint">The push service's https address for this browser.</param>
/// <param name="P256dh">The browser's P-256 key, base64url of the uncompressed point.</param>
/// <param name="Auth">The browser's 16-byte authentication secret, base64url.</param>
public sealed record PushSubscription( string Endpoint, string P256dh, string Auth );

/// <summary>Sends web push notifications to paired devices' browsers: answers <c>push.key</c> and <c>push.subscribe</c>, keeps the subscriptions
/// per device next to the VAPID key, and delivers RFC 8291 <c>aes128gcm</c> messages with RFC 8292 VAPID tokens.</summary>
/// <remarks>Safe from any thread; it touches no application state. A revoked device's subscriptions are dropped.</remarks>
public sealed class PushService : IHostService, IDisposable
{
	/// <summary>The contact the VAPID token names.</summary>
	public const string Subject = "mailto:loom@localhost";

	/// <summary>The name of the subscriptions file, in the VAPID key's folder.</summary>
	public const string SubscriptionsFile = "push-subscriptions.json";

	/// <summary>How long a VAPID token is valid.</summary>
	public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours( 12 );

	/// <summary>How long the push service keeps a message for a browser that is offline, in seconds.</summary>
	private const int MessageTtl = 24 * 60 * 60;

	/// <summary>The most subscriptions a device keeps; a new one replaces the oldest.</summary>
	private const int MaxPerDevice = 5;

	/// <summary>The longest notification body sent, in characters, so the message fits one record.</summary>
	private const int MaxBodyLength = 1000;

	/// <summary>Writes and reads the subscriptions file in camelCase.</summary>
	private static readonly JsonSerializerOptions JsonOptions = new( JsonSerializerDefaults.Web ) { WriteIndented = true };

	/// <summary>Posts messages to push services: no proxy, no redirects, and only to public addresses, checked after name resolution.</summary>
	private static readonly HttpClient Http = new( new SocketsHttpHandler
	{
		AllowAutoRedirect = false,
		UseProxy = false,
		ConnectCallback = ConnectPublicAsync,
	} )
	{
		Timeout = TimeSpan.FromSeconds( 30 ),
	};

	/// <summary>Stores the paired devices, whose revocation drops subscriptions.</summary>
	private readonly DeviceStore _devices;

	/// <summary>Stores the VAPID signing key.</summary>
	private readonly ECDsa _key;

	/// <summary>Stores the subscriptions file's path.</summary>
	private readonly string _path;

	/// <summary>Holds each device's subscriptions, oldest first; lock it to read or change them.</summary>
	private readonly Dictionary<string, List<PushSubscription>> _subscriptions = new( StringComparer.Ordinal );

	/// <summary>Loads the VAPID key at <paramref name="vapidKeyPath"/>, creating it when missing, and the subscriptions saved next to it.</summary>
	/// <param name="devices">The paired devices.</param>
	/// <param name="vapidKeyPath">The VAPID key's PEM file, such as <see cref="DefaultKeyPath"/>; a bare host keeps its own in its folder.</param>
	/// <exception cref="CryptographicException">The key file holds no P-256 private key.</exception>
	public PushService( DeviceStore devices, string vapidKeyPath )
	{
		_devices = devices;
		_key = LoadOrCreateKey( vapidKeyPath );
		_path = Path.Combine( Path.GetDirectoryName( Path.GetFullPath( vapidKeyPath ) )!, SubscriptionsFile );
		PublicKey = Base64Url.EncodeToString( WebPushCrypto.ExportPoint( _key.ExportParameters( false ) ) );
		ReadSubscriptions();
		devices.Revoked += RemoveDevice;
	}

	/// <summary>Gets the default VAPID key file in the data folder.</summary>
	public static string DefaultKeyPath => LoknPaths.Combine( "vapid-key.pem" );

	/// <summary>Raised with a message when a push could not be delivered, on a thread-pool thread.</summary>
	public event Action<string>? Failed;

	/// <summary>Gets the VAPID public key as browsers take it for <c>applicationServerKey</c>: base64url of the uncompressed point.</summary>
	public string PublicKey
	{
		get;
	}

	/// <summary>Gets the client that posts messages; checks replace it to reach a push service on loopback, which the default client refuses.</summary>
	internal HttpClient Client { get; init; } = Http;

	/// <summary>Gets a device's subscriptions, oldest first.</summary>
	public IReadOnlyList<PushSubscription> FindSubscriptions( string deviceId )
	{
		lock ( _subscriptions )
			return _subscriptions.TryGetValue( deviceId, out var list ) ? [.. list] : [];
	}

	/// <summary>Registers <c>push.key</c>, answering <c>{ key }</c>, and <c>push.subscribe { endpoint, p256dh, auth }</c>, which saves the subscription for the connection's device.</summary>
	public void Register( RemoteConnection connection )
	{
		connection.Peer.On( "push.key", _ => Task.FromResult<JsonObject?>( new JsonObject { ["key"] = PublicKey } ) );
		connection.Peer.On( "push.subscribe", frame =>
		{
			var subscription = new PushSubscription(
				Frame.ReadString( frame, "endpoint" ) ?? "",
				Frame.ReadString( frame, "p256dh" ) ?? "",
				Frame.ReadString( frame, "auth" ) ?? "" );
			if ( !IsValid( subscription ) )
				throw new RemoteException( RemoteErrors.BadRequest, "The push subscription is not an https endpoint with a P-256 key and a 16-byte secret." );
			Save( connection.DeviceId, subscription );
			return Task.FromResult<JsonObject?>( null );
		} );
	}

	/// <summary>Sends a notification <c>{ title, body, ch }</c> to every subscription of every paired device, dropping those the push service answers 404 or 410.</summary>
	/// <param name="chatId">The chat it is about, which the web client opens when it is clicked.</param>
	/// <param name="title">The notification's title.</param>
	/// <param name="body">The notification's text; long text is cut.</param>
	public Task NotifyAsync( Guid chatId, string title, string body ) => NotifyAsync( chatId, title, body, new HashSet<string>( StringComparer.Ordinal ) );

	/// <summary>Sends a notification <c>{ title, body, ch }</c> to every subscription of every paired device but <paramref name="skipDevices"/>, dropping those the push service answers 404 or 410.</summary>
	/// <param name="chatId">The chat it is about, which the web client opens when it is clicked.</param>
	/// <param name="title">The notification's title.</param>
	/// <param name="body">The notification's text; long text is cut.</param>
	/// <param name="skipDevices">The devices that get no notification, such as those that already show the chat.</param>
	public async Task NotifyAsync( Guid chatId, string title, string body, IReadOnlySet<string> skipDevices )
	{
		List<(string DeviceId, PushSubscription Subscription)> targets;
		lock ( _subscriptions )
		{
			targets = [.. _subscriptions
				.Where( pair => !skipDevices.Contains( pair.Key ) && _devices.Find( pair.Key ) is not null )
				.SelectMany( pair => pair.Value.Select( subscription => (pair.Key, subscription) ) )];
		}
		if ( targets.Count == 0 )
			return;

		var message = new JsonObject
		{
			["title"] = title,
			["body"] = body.Length > MaxBodyLength ? body[..MaxBodyLength] + "…" : body,
			["ch"] = chatId.ToString( "N" ),
		};
		var plaintext = Encoding.UTF8.GetBytes( message.ToJsonString() );
		await Task.WhenAll( targets.Select( target => SendAsync( target.DeviceId, target.Subscription, plaintext ) ) ).ConfigureAwait( false );
	}

	/// <summary>Stops following revocations and releases the key.</summary>
	public void Dispose()
	{
		_devices.Revoked -= RemoveDevice;
		_key.Dispose();
	}

	/// <summary>Encrypts and posts one message, dropping the subscription when it is gone and reporting other failures.</summary>
	private async Task SendAsync( string deviceId, PushSubscription subscription, byte[] plaintext )
	{
		try
		{
			var endpoint = new Uri( subscription.Endpoint );
			var body = WebPushCrypto.Encrypt( plaintext, Base64Url.DecodeFromChars( subscription.P256dh ), Base64Url.DecodeFromChars( subscription.Auth ) );
			var token = WebPushCrypto.BuildVapidToken( _key, WebPushCrypto.ComputeAudience( endpoint ), DateTimeOffset.UtcNow + TokenLifetime, Subject );
			var content = new ByteArrayContent( body );
			content.Headers.ContentType = new MediaTypeHeaderValue( "application/octet-stream" );
			content.Headers.ContentEncoding.Add( "aes128gcm" );
			using var request = new HttpRequestMessage( HttpMethod.Post, endpoint ) { Content = content };
			request.Headers.Authorization = new AuthenticationHeaderValue( "vapid", $"t={token}, k={PublicKey}" );
			request.Headers.TryAddWithoutValidation( "TTL", MessageTtl.ToString( System.Globalization.CultureInfo.InvariantCulture ) );
			using var response = await Client.SendAsync( request ).ConfigureAwait( false );
			if ( response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone )
				Remove( deviceId, subscription.Endpoint );
			else if ( !response.IsSuccessStatusCode )
				Failed?.Invoke( $"The push service at {endpoint.Host} answered {(int)response.StatusCode} {response.ReasonPhrase}." );
		}
		catch ( Exception exception ) when ( exception is HttpRequestException or TaskCanceledException or FormatException or ArgumentException or CryptographicException )
		{
			Failed?.Invoke( $"A push notification failed: {exception.Message}" );
		}
	}

	/// <summary>Saves a subscription for a device, replacing one with its endpoint, wherever it was, and the device's oldest beyond the limit.</summary>
	private void Save( string deviceId, PushSubscription subscription )
	{
		lock ( _subscriptions )
		{
			foreach ( var list in _subscriptions.Values )
				list.RemoveAll( item => item.Endpoint == subscription.Endpoint );
			if ( !_subscriptions.TryGetValue( deviceId, out var own ) )
				_subscriptions[deviceId] = own = [];
			own.Add( subscription );
			if ( own.Count > MaxPerDevice )
				own.RemoveRange( 0, own.Count - MaxPerDevice );
			WriteSubscriptions();
		}
	}

	/// <summary>Drops one subscription the push service no longer knows.</summary>
	private void Remove( string deviceId, string endpoint )
	{
		lock ( _subscriptions )
		{
			if ( _subscriptions.TryGetValue( deviceId, out var list ) && list.RemoveAll( item => item.Endpoint == endpoint ) > 0 )
				WriteSubscriptions();
		}
	}

	/// <summary>Drops a revoked device's subscriptions.</summary>
	private void RemoveDevice( string deviceId )
	{
		lock ( _subscriptions )
		{
			if ( _subscriptions.Remove( deviceId ) )
				WriteSubscriptions();
		}
	}

	/// <summary>Checks that a subscription has an https endpoint on the default port at a name that is no address of this PC or a private network,
	/// an uncompressed P-256 key and a 16-byte secret. Names are checked again after resolution when a message is sent.</summary>
	internal static bool IsValid( PushSubscription subscription )
	{
		if ( !Uri.TryCreate( subscription.Endpoint, UriKind.Absolute, out var endpoint ) || endpoint.Scheme != Uri.UriSchemeHttps || !endpoint.IsDefaultPort
			|| endpoint.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6) || endpoint.IsLoopback || endpoint.UserInfo.Length > 0
			|| endpoint.HostNameType == UriHostNameType.Dns && (!endpoint.DnsSafeHost.Contains( '.', StringComparison.Ordinal ) || endpoint.DnsSafeHost.EndsWith( ".local", StringComparison.OrdinalIgnoreCase )) )
			return false;
		if ( IPAddress.TryParse( endpoint.DnsSafeHost, out var literal ) && !NetworkAddresses.IsPublic( literal ) )
			return false;
		try
		{
			using var key = WebPushCrypto.ImportPoint( Base64Url.DecodeFromChars( subscription.P256dh ) );
			return key is not null && Base64Url.DecodeFromChars( subscription.Auth ).Length == WebPushCrypto.AuthSecretSize;
		}
		catch ( FormatException )
		{
			return false;
		}
	}

	/// <summary>Connects to a push service only through its public addresses, so a subscription cannot make the host reach this PC or its networks.</summary>
	private static async ValueTask<Stream> ConnectPublicAsync( SocketsHttpConnectionContext context, CancellationToken cancellationToken )
	{
		var host = context.DnsEndPoint.Host;
		IPAddress[] addresses = IPAddress.TryParse( host, out var literal ) ? [literal] : await Dns.GetHostAddressesAsync( host, cancellationToken ).ConfigureAwait( false );
		var allowed = addresses.Where( NetworkAddresses.IsPublic ).ToArray();
		if ( allowed.Length == 0 || allowed.Length != addresses.Length )
			throw new HttpRequestException( $"The push service {host} resolves to an address that is not public." );

		var socket = new Socket( SocketType.Stream, ProtocolType.Tcp ) { NoDelay = true };
		try
		{
			await socket.ConnectAsync( allowed, context.DnsEndPoint.Port, cancellationToken ).ConfigureAwait( false );
			return new NetworkStream( socket, true );
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}

	/// <summary>Loads the subscriptions file; a missing or unreadable file means none.</summary>
	private void ReadSubscriptions()
	{
		try
		{
			if ( !File.Exists( _path ) || JsonSerializer.Deserialize<Dictionary<string, List<PushSubscription>>>( File.ReadAllText( _path ), JsonOptions ) is not { } saved )
				return;
			lock ( _subscriptions )
			{
				foreach ( var (deviceId, list) in saved )
					_subscriptions[deviceId] = list;
			}
		}
		catch ( JsonException )
		{
		}
	}

	/// <summary>Writes every subscription, readable by the current user only, under the lock.</summary>
	private void WriteSubscriptions() =>
		SecretFile.WriteAllText( _path, JsonSerializer.Serialize( _subscriptions.Where( pair => pair.Value.Count > 0 ).ToDictionary(), JsonOptions ) );

	/// <summary>Loads the P-256 key saved at a path, or creates and saves one when the file is missing.</summary>
	/// <exception cref="CryptographicException">The file holds no P-256 private key.</exception>
	private static ECDsa LoadOrCreateKey( string path )
	{
		if ( File.Exists( path ) )
		{
			var key = ECDsa.Create();
			try
			{
				key.ImportFromPem( File.ReadAllText( path ) );
				if ( key.KeySize != 256 )
					throw new CryptographicException( $"{path} does not hold a P-256 key." );
				return key;
			}
			catch
			{
				key.Dispose();
				throw;
			}
		}

		var created = ECDsa.Create( ECCurve.NamedCurves.nistP256 );
		SecretFile.WriteAllText( path, created.ExportPkcs8PrivateKeyPem() );
		return created;
	}
}
