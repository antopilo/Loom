# Lokn.Relay

A small ASP.NET Core server that lets a Loom host and its devices reach each other when neither
can accept a connection: both connect out to the relay, and the relay copies WebSocket messages
between them. The protocol is in [docs/remote.md](../docs/remote.md#relay). Everything a native
Loom client and its host exchange is end-to-end encrypted (AES-256-GCM); the relay sees ciphertext
only and never parses or logs it. The web client page under `/h/{hostId}/`, however, is served by
the relay, so a hostile relay operator could serve a modified page that acts with the browser's
device key: use the web client only from a relay you trust, or from the host's own origin
(Tailscale Serve or Funnel). It is not a module and the app never references it.

## Endpoints

| Path | Who | What |
|---|---|---|
| `GET /host/{hostId}` (WebSocket) | Host | Control socket. The relay sends a challenge, the host answers with its proof; the relay then sends `{ "t": "relay.open", "sid" }` for each device. |
| `GET /host/{hostId}/{sid}` (WebSocket) | Host | Data socket for one device, with its own challenge and proof first. |
| `GET /device/{hostId}` (WebSocket) | Device | Waits up to 10 s for the host's data socket, then carries the device's bytes. |
| `GET /h/{hostId}/` and its files | Browser | The web client (the Remote module's files, embedded at build): a pairing link's web URL in relay mode is `https://<relay>/h/<hostId>/#pair=…`, and the page connects to `/device/{hostId}` on the same origin. |

On every host socket the relay first sends `{ "t": "relay.challenge", "nonce" }` (32 random bytes,
base64url). The host answers `{ "t": "relay.host", "hostKey", "sig" }`: `hostKey` is the host's
static P-256 key (base64url SPKI DER), `sig` is the base64url IEEE P1363 ECDSA P-256 SHA-256
signature over `utf8( "loom-relay-proof v2\n" + hostId + "\n" + purpose + "\n" + nonce )`, where
`purpose` is `control` on the control socket and `data/<sid>` on a data socket. `hostId` must equal
the first 22 characters of base64url( SHA-256( SPKI ) ). A proof is good for its one socket only,
so a captured one cannot be replayed. A failed proof closes the socket with status 1008; a device
whose host does not answer is closed with status 1001.

The relay keeps no state on disk: no accounts, no device list. Limits keep one client or host from
exhausting it: 64 sockets per client address (`429` beyond), 16 devices waiting for one host and
1024 in total (`503`), 64 devices connected to one host. Behind a reverse proxy on loopback the
client address is taken from `X-Forwarded-For`. To serve only your own hosts, list their ids in
`appsettings.json`: `{ "Relay": { "AllowedHosts": [ "<hostId>" ] } }`; others get `403`. Without
the list any host with a key can use the relay.

## Build and run

```
dotnet publish Relay/Lokn.Relay.csproj -c Release -o out/relay
out/relay/Lokn.Relay --urls http://127.0.0.1:8080
```

Configuration is read from `appsettings.json` beside the binary, environment variables
(`ASPNETCORE_URLS`, `Kestrel__...`) and the command line, as for any ASP.NET Core app. Keep
`appsettings.json` in the publish directory, not in `Relay/` (the check suite references the
project and would copy it).

## TLS

Browsers need HTTPS for WebCrypto, and the web client served from the relay's domain uses
`wss://`. Terminate TLS either in Kestrel or in a reverse proxy.

### Kestrel

`appsettings.json` beside the binary:

```json
{
	"Kestrel": {
		"Endpoints": {
			"Https": {
				"Url": "https://0.0.0.0:443",
				"Certificate": {
					"Path": "/etc/letsencrypt/live/relay.example.com/fullchain.pem",
					"KeyPath": "/etc/letsencrypt/live/relay.example.com/privkey.pem"
				}
			}
		}
	},
	"Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } }
}
```

Kestrel reloads the certificate files when they change. On Linux, binding port 443 needs
`CAP_NET_BIND_SERVICE` (see the systemd unit below).

### Reverse proxy

Listen on loopback (`--urls http://127.0.0.1:8080`) and let the proxy forward WebSocket
upgrades. Caddy:

```
relay.example.com {
	reverse_proxy 127.0.0.1:8080
}
```

nginx:

```nginx
location / {
	proxy_pass http://127.0.0.1:8080;
	proxy_http_version 1.1;
	proxy_set_header Upgrade $http_upgrade;
	proxy_set_header Connection "upgrade";
	proxy_read_timeout 1h;
}
```

The relay pings idle sockets every 30 s, so a proxy idle timeout above that keeps chats open.

## Running as a service

### systemd

`/etc/systemd/system/loom-relay.service`:

```ini
[Unit]
Description=Loom relay
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
ExecStart=/opt/loom-relay/Lokn.Relay
WorkingDirectory=/opt/loom-relay
User=loom-relay
AmbientCapabilities=CAP_NET_BIND_SERVICE
Restart=always
Environment=ASPNETCORE_URLS=http://127.0.0.1:8080

[Install]
WantedBy=multi-user.target
```

```
sudo systemctl daemon-reload
sudo systemctl enable --now loom-relay
journalctl -u loom-relay -f
```

Drop the `Environment=` line when Kestrel serves TLS from `appsettings.json`.

### Windows service

The relay reports to the service manager itself, so `sc.exe` can run it directly (elevated):

```
sc.exe create LoomRelay binPath= "C:\LoomRelay\Lokn.Relay.exe" start= auto
sc.exe start LoomRelay
```

Put the `Kestrel` section (or `"Urls": "http://127.0.0.1:8080"` behind a proxy) in
`C:\LoomRelay\appsettings.json`; a `.pfx` certificate uses `"Path"` and `"Password"`. Logs go to
the Windows event log (Application log, source `Lokn.Relay`).

## Docker (optional)

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Relay/ Relay/
RUN dotnet publish Relay/Lokn.Relay.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["./Lokn.Relay"]
```

Build from the repository root (`docker build -f Relay/Dockerfile .` after saving the above as
`Relay/Dockerfile`) and put a TLS proxy in front of port 8080.
