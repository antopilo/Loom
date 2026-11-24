# Remote: daemon, clients and protocol

Loom can run its chats in a daemon, `Loom.exe --host`, and show them in any number of
clients: Loom on the same PC, Loom on another PC, and a web client on a phone. This file is
the contract every part follows. The plan and its phases are in
[proposals/remote-access.md](proposals/remote-access.md).

## Roles and paired hosts

One Loom window shows This PC's chats **and** the chats of every host this PC paired with, at once; the sidebar's
host switcher moves between them without a restart. The `remote.role` setting (Global scope) only picks where
This PC's own chats run:

| Role | Who runs This PC's chats | Store | Workspace (git, files, terminal) | Browser tabs |
|---|---|---|---|---|
| `local` (default) | This Loom process, as before | Local file | Local | This process's, saved in `browser-tabs.json` |
| `daemon` | `Loom.exe --host` on this PC, over the named pipe | Mirror of the daemon's | Local (same PC) | Mirror of the daemon's |

Each paired host is linked beside This PC, whatever the role:

| Host | Who runs its chats | Store | Workspace | Browser tabs |
|---|---|---|---|---|
| Paired host | That PC's daemon, over the network | Mirror of the host's, in memory | Remote, over the protocol | Mirror of the host's, in memory; localhost pages forwarded |

Paired hosts and the network modes other than `off` (`lan`, `tailscale`, `relay`, `funnel`) are previews: Remote
access marks the **Hosts** and **Add host** headings and those mode buttons with a small muted **Preview** tag, and the
`remote.network` choices in Settings read `LAN (Preview)` and so on. The defaults stay: `remote.role` is `local`,
`remote.network` is `off` and `remote.login-item` is off.

The former `remote` role (one paired host instead of This PC) is gone: the validator still reads `remote`, and Loom
rewrites it to `local` once at start, before the role is latched, and imports `remote-host.json` into
`remote-hosts.json`, so that user gets This PC's own chats back and the host listed in the switcher, connected at
start. Layouts saved in the `remote` role are not migrated: their sessions and panels name the host's chats
without a host, so they read as This PC's and show Home.

`Loom.exe --host [--bare <folder>]` runs with no window. It loads only the modules
`markdown`, `omp`, `projects`, `workspaces`, `git`, `conversations`, `browser`, `design` and `remote`, and of those
only the ones Settings › Modules turns on: it reads the desktop's module states, `%LOCALAPPDATA%\Lokn\modules.json`
(bare: `modules.json` in its folder, where modules are off unless turned on; a bare folder without the file, such as a
check's, runs all of them), and follows the file live, never writing it. A module turned off there stops in the daemon with
the modules that need it, and one turned on starts after the modules it needs; `remote` and the modules it needs
(`conversations`, and through it `omp`, `workspaces`, `projects`, `markdown`) always run, since the daemon exists for
them. `host.log` records each as `DISABLED <id>` or `ENABLED <id>`. The daemon runs
the role `host` because the process is the daemon (`LoknApplication.IsHost`): the daemon
shares `settings.json` with the desktop, so `host` is never saved as `remote.role`. The Browser module loads there
without WebView2 and creates no web view: the daemon owns the session's browser tabs, and its browser tools run in an
attached window (Host tools). A bare
daemon keeps all of its data in its folder (`LoknPaths.UseDataRoot`). One daemon per user
(mutex `Local\LoomHost`, or `Local\LoomHost.<hash of the bare folder>`). In `daemon` role, Loom
starts the daemon (`Loom.exe --host`, hidden, detached) when the pipe is absent and waits up
to 15 s for it; a process that ends before its pipe appears fails the start at once (**Fallback** below). The setting `remote.login-item` adds `"<Loom.exe>" --host` to the user's Run
key (value `LoomHost`, arguments quoted as the C runtime parses them) while `remote.role` is
`daemon`; in any other role, and on uninstall, the value is removed. Loom and the daemon both keep
the value in step with the two settings. A `--bare` desktop or daemon never touches the Run key.

One process at a time writes a data folder's chats. The daemon and Loom in `local` role each
hold `chats.lock` in the data folder open without sharing for as long as they run. In `local`
role Loom first stops a running daemon (stop event, then up to 10 s for its mutex), reads the
history and browser tabs again (`ProjectStore.Reload`, `ConversationStore.Reload`, `BrowserTabs.Reload`), and takes the lock; when the
daemon does not stop, that Loom links with it as in `daemon` role, sets `remote.role` to `daemon` and says so; choosing
This Loom again tries once more. A
daemon that cannot take the lock (Loom in `local` role runs) does not start: its Remote module
fails, and the daemon logs why and exits. The daemon also exits when its Remote module fails for
any other reason (an unreadable `devices.json`, a pipe already taken), and a shutdown that has
not finished 10 s after the stop request ends the process. It logs every reported error, with
stacks for failures of its own loop, to `%TEMP%\Lokn\host.log` (bare: `host.log` in the folder),
which starts over past 1 MiB (the previous one kept as `host.log.1`); Remote access shows the path.

While it runs, the daemon shows an icon in the notification area, unless `remote.tray` (Global,
default on; **Show the daemon in the notification area** in Settings › Remote) is off; it follows the setting live.
The Remote module publishes the daemon's `HostStatus` (Core) with `LoknApplication.PublishHostStatus`
whenever an agent starts or stops working, a chat's questions change, a connection joins or leaves,
the network's error changes or a setting changes. `HostRunner` reads it on its loop and hands it to
`HostTray`, a hidden top-level window on its own per-monitor DPI aware thread beside the session
watcher's. The icon is Loom's application icon from the executable at the taskbar's small icon
size with a dot drawn over its corner: green running, orange while a chat waits for the user's
answer, red while the network reports an error. The tooltip is `Loom: ` and the error, else the
activity line. A left click or double-click opens Loom (`Loom.exe`, with `--bare <folder>` for a
bare daemon; a running window gets the launch forwarded and comes forward). A right click shows:

- **Open Loom**;
- disabled lines: `<n> agents working · <m> devices connected`, with ` · <k> waiting for you` while
  a chat waits (devices are distinct paired devices; Loom on this PC is not one), whether Loom on
  this PC is connected over the pipe, the error when there is one, and `<n> pairing(s) waiting for you`
  while devices wait for an answer to their pairing (Pairing confirm);
- **Remote access…**: Loom with `--open-link lokn://remote/role`, which the desktop follows on its own
  launch and on a forwarded one, opening Settings › Remote at Remote access;
- **Start at login**: checked when the Run value exists; choosing it sets `remote.login-item`
  through the Remote module's `IHostLoginItem` service, as the setting does, and the module
  writes the Run value. It is disabled in a bare daemon and while `remote.role` is not `daemon`;
- a separator and **Stop daemon**, which sets the stop event as `--host --stop` does.

When the status turns to needs-you, the icon shows the notification "An agent needs you" once
(`NIIF_RESPECT_QUIET_TIME`, so Windows holds it back in quiet hours); a click on it opens Loom. When a
device pairs with view only while no Loom is connected over the pipe, the icon shows "<name> paired with
this PC" / "It can view your chats. Open Loom to allow control." once per device (a device first seen while
Loom is connected, which asks itself, is never announced later); a click opens Loom on Remote access. The
icon is added again when Explorer restarts (`TaskbarCreated`) and removed (`NIM_DELETE`) before the
daemon exits; the log records `TRAY added` and `TRAY removed`.

`settings.json` is shared by the desktop and the daemon. `SettingsService` saves by merging:
under the mutex `Local\Lokn.settings.<hash of the path>` it reads the file, applies only the
values this process changed since it last read or wrote the file, and writes only when the
result differs; setting a value to what it already is writes nothing. Both processes follow the
file (`SettingsService.Watch`) and take the other's changes, so the model, prompt and tool
switches chosen on the desktop reach the daemon's chats, and the daemon's network mode shows on
the desktop.

The daemon follows rebuilt and updated module DLLs as the desktop does (the `modules` folder of
the build it runs from, and in the user's daemon the module updates folder), but holds every
reload back while a chat's agent works, since a reload would stop its turn: it logs
`DEFERRED <assemblies>: <n> chats are working` to `host.log` and reloads as soon as the
`HostStatus` the Remote module publishes counts no working chat, logging `RELOADED` or `FAILED`
as the desktop does in `modules.log`. The desktop owns the updates folder: it applies staged
updates (at its start or with **Apply now**) and deletes outdated or refused ones; the daemon
never does, and takes applied updates through the same held-back reload. `sync` carries the host's
build (`version`, `<app>+<Remote module>`); when Loom
in `daemon` role runs another build, Remote access offers to restart the daemon, and Loom
restarts it by itself once when no chat works. An app update waits while the daemon's agents work (**Update when agents
finish** or **Update now**, [architecture](architecture.md#updates)), stops the daemon gracefully before the files are
replaced, and starts it again afterwards when it ran before the update or its Run value exists. Loom and the daemon both
back the data folder up before a newer version first runs on it, whichever starts first ([architecture](architecture.md#data-and-upgrades)).

Choosing another role in **Remote access** or Settings moves This PC's chats while Loom runs, with no restart
(`RemoteModule.SwitchRole`). The Remote module follows `remote.role` (Global) and starts a switch when it names
another role than the one Loom runs in (`RunningRole`). A switch the user chose first waits until no agent works on
This PC (`ConversationStore.WorkingCount` of `ConversationsModule.Hosts.Local`: this Loom's own chats, or the
daemon's mirrored while the daemon runs); meanwhile Remote access shows **Waiting for <n> agents to finish** with
**Cancel**, which sets the role back and ends the wait, as choosing the running role again does. Then:

- **To the daemon**: this Loom writes its pending history (`ConversationStore.Flush`) and tab titles
  (`BrowserTabs.Stop`; projects are written as they change), releases `chats.lock`, and links with the daemon as it
  does at start in `daemon` role, which starts the daemon. The stores and tabs become mirrors and
  `ChatSessionService.Source` creates `RemoteChatSession`s, so the Conversations module reopens every chat panel on
  the same chat (`SourceChanged`); no panel closes.
- **To this Loom**: the daemon link pauses dialing (as **Stop daemon** does), the daemon is asked to stop and waited
  for off the UI thread (up to 10 s for its mutex), then this Loom takes `chats.lock`, stops the link, ends the
  mirrors (`ProjectStore.EndMirror`, `ConversationStore.EndMirror`, `BrowserTabs.EndMirror`: they forward nothing
  more, no chat works, and they read the files the daemon wrote as it stopped), sets `Sessions.Source` back to local
  `ChatSession`s, which reopens the chat panels, and shows This PC as running its chats here. When the daemon does
  not stop or the lock stays held, the chats stay in the daemon: its link dials again, `remote.role` goes back to
  `daemon`, and Remote access and the shell say why.

The running role is latched in a process slot after every switch, so a reload of the Remote module keeps it; a
choice made while the module was off applies once it starts. A check's fixed role (`RemoteHooks.Role`) does not
follow the setting. `lokn://remote/role/daemon` and `lokn://remote/role/local` set `remote.role`, so other modules,
such as Home's What's new card, turn the daemon on without referencing Remote; `lokn://remote/role` opens Remote
access.

**Fallback.** In `daemon` role Loom counts the daemon's failures: the process it started ends before its pipe
appears (the dial fails at once with its exit code, unless another daemon took the mutex meanwhile), the pipe does
not appear within 15 s, or a connection drops while dialing was not paused and the daemon's mutex is gone within
2 s. Three failures within 10 minutes (`DaemonFailures`) move This PC's chats back into this Loom through the same
switch, without waiting for agents, save `remote.role` as `local`, post a notification (source `remote`) with the
reason and the daemon's log path (`RemoteClient.DaemonLogPath`), and show the same note in Remote access until it is
dismissed or the next switch starts. Checks make a bare daemon fail this way: when the environment variable
`LOKN_TEST_HOST_EXIT` holds an integer, `Loom.exe --host --bare <folder>` logs `EXIT test hook <code>` to its
`host.log` and exits with that code before it takes its mutex (`--stop` is unaffected). The checks
(`HandoverChecks`) give the Remote module the built `Loom.exe` (`RemoteHooks.DaemonExecutable`) and point
`LoknPaths.DataRoot` at a temporary bare folder, so it starts, stops and watches only that folder's daemon.

The Remote module keeps one `RemoteClient` per linked host: `Daemon` for
This PC in `daemon` role (it dials the pipe and binds `ConversationsModule.Hosts.Local`), and one in
`Links` per paired host (`HostLinkKind.Paired`), each bound to its own Core `LoknHost` (`app.Hosts`)
and Conversations `ChatHost` (`ConversationsModule.Hosts`). Paired hosts are kept in `remote-hosts.json`
in the data folder (`{ version: 1, hosts: [{ hostId, hostKey, addrs, relay?, deviceId, name, added }] }`,
written owner-only through a temporary file, with the one device key `device-key.pem`; each pairing uses
a new `deviceId`; `name` is empty to show the name the host reports). A list that does not parse is
reported, left untouched and never overwritten; no paired host connects until it is fixed. A link dials
each address in turn over WebSocket, the one that answered last first, then the relay. The host must
answer `sync` within 15 s. It reconnects with a pause from 1 s doubling to 30 s, and on each connect
syncs again, re-attaches the chats a panel shows, and marks its `ChatHost` synced, which loads the chat
panels that waited for it. Its stores become mirrors, its `ChatSessionService.Source` creates
`RemoteChatSession`s, and a paired host's `LoknHost.Workspace` is an `OfflineWorkspaceHost` (Core; every
call fails with `IOException`, watches report `lost`, terminals exit with -1) until a connection swaps in
the host's (`RemoteWorkspaceHost`) and again after it closes, so nothing ever acts on this PC's disk in
its place; a paired host's projects show their letter icon, since project icons are read only for This PC's
projects (`ProjectsModule.GetIcon`). Panels of host-bound types (Git, Files, Editor, Terminal, Browser, Window stream) read
`PanelContext.Workspace` and `PanelContext.Host.BrowserTabs` of the host they were opened on. The
window stream panel's source on a paired host is that link's `ClientStreamSource`, which follows its
current connection. A view-only link refuses store changes itself. Prompt attachments on a paired host
are uploaded first and sent as host paths. The status bar shows the daemon link as a dot
(`shell.status.right`) in `daemon` role; paired hosts show in the host switcher instead.

**Add host** in Remote access pastes a pairing link: `HostPairing.PairAsync` refuses this PC's own link
(its `host-key.pem`), pairs over each address then the relay, and `RemoteModule.AddHostAsync` saves the
entry (pairing again with a host replaces its entry and keeps its name), registers the host with Core and
Conversations, starts its link and makes it the current host, with no restart; the sidebar shows its chats while the
layout stays, so Remote access shows the result. **Remove** stops the link, closes the
host's chats and every panel on it, and drops its entry; the host keeps this device until its user
revokes it there. A rename is saved in the entry. When the Remote module stops (reload, or turned off) every
link stops: its `ChatHost` is detached (mirrors refuse changes, sessions are offline proxies), its browser
tabs wait (`BrowserTabs.Detach`), its workspace goes offline and its host shows as stopped; the hosts stay
registered, so their panels and layouts survive, and the next start links them again by id.

The host (`HostGateway`) always listens on the pipe and, by `remote.network`: `lan` on
127.0.0.1 and each up, non-loopback IPv4 address of physical Ethernet and Wi-Fi interfaces,
bound one by one (never `0.0.0.0`); `tailscale` on 127.0.0.1 and the tailnet addresses, plus
`tailscale serve` of the port for the web client; `funnel` on 127.0.0.1 with Tailscale Funnel;
`relay` on 127.0.0.1 and through the relay link; `off` on the pipe only. The listener
(`WebSocketHost`) is a minimal HTTP/1.1 server on `TcpListener`s, so it needs no URL
reservation or administrator rights, and it does not match the `Host` header, so `tailscale
serve` and Funnel proxying to 127.0.0.1 reach it. When an address cannot be bound (port taken,
address gone), the host keeps 127.0.0.1, reports the error, and leaves the addresses out of
pairing links. Keys, devices, the VAPID key and uploads live in the data folder
(`host-key.pem`, `devices.json`, `vapid-key.pem`, `attachments\remote\<deviceId>\`).

The listeners follow the three network settings, also when the desktop saves them in Settings.
A change of mode, port or relay, and `off`, closes every device's connection (the pipe stays),
so turning access off cuts devices off at once. A mode that failed to start (Tailscale not up
yet, no LAN address yet) reports `error` and starts again after 5 s, doubling to 5 minutes, and
at once 2 s after the PC's addresses change; a `lan` mode also restarts when its addresses
change. Leaving `tailscale` or `funnel`, and stopping the daemon, removes the Tailscale HTTPS
proxy (`tailscale serve --https=443 off`); a removal that fails is reported in `error` and tried
again at the next restart. A connection's device record is read again once it joins: a device
revoked, given other scopes or paired again with another key during the handshake is closed
before any of its requests runs.

`WebSocketHost` takes `IPEndPoint`s (port 0 picks a free port; `Endpoints` gives the bound
ones once started). Each connection carries one request: the request line and headers may take
8 KiB, bytes after them (a body or a pipelined request), a `Content-Length` other than 0 or a
`Transfer-Encoding` are refused, and a malformed or oversized request is answered `400` and
closed. Answers carry `Content-Length` and `Connection: close`; after writing, the host closes
its sending side and reads for up to 1 s until the client closes. The client has 10 s to send
its headers. `GET /loom` with `Upgrade: websocket`, `Connection: Upgrade`, a
`Sec-WebSocket-Key` of 16 bytes and `Sec-WebSocket-Version: 13` gets `101` with
`Sec-WebSocket-Accept` (RFC 6455), then `WebSocket.CreateFromStream` (server, keep-alive
pings every 20 s); another version gets `426` with `Sec-WebSocket-Version: 13`, anything else
on `/loom` gets `400`. `MapGet` handlers take a `HostRequest` (method, path, query, headers)
and return a `HostResponse` (status, headers, body) for `GET` and `HEAD`; other methods get
`405`, unmapped paths `404`.

## Module layout

- `Modules/Conversations` owns `IChatSession`, `ChatSession` (local engine), `RemoteChatSession`
  (proxy over `IChatChannel`), `ChatSessionState` and `ChatSessionEvent` (wire DTOs),
  `ChatSessionService` with a replaceable session source, and store operations
  (`StoreOp`, `ConversationStore.Mutated`, `ConversationStore.Apply`, mirror mode).
  `ProjectStore` gets the same operations.
- `Core` owns `IWorkspaceHost` and the hosts, `app.Hosts` (`LoknHosts`): This PC (`LocalWorkspaceHost`) and each
  paired host, each a `LoknHost` with its own `Workspace` and `BrowserTabs`; `app.Workspace` and `app.BrowserTabs`
  are the current host's. Git, Files, Editor and Terminal reach the disk, processes and PTYs only through their
  panel's host. It also owns `HostStatus`
  and `IHostLoginItem`, through which the shell's `HostRunner` shows the daemon's icon without
  referencing the Remote module. It owns the session's browser tabs (`BrowserTabs`,
  `BrowserTabInfo`, `BrowserOp`), with `IBrowserWindows` and `IBrowserTunnel`, and the proxy a paired PC's pages connect
  through (`BrowserProxy`): page state with no web view, so the daemon and the Remote module hold it without the Browser
  module. Core is the place because the Remote module must not depend on Browser: turning Browser off in Settings ›
  Modules would otherwise stop Remote, and with it the daemon role; the web client lists the tabs without Browser too.
  The proxy lives in Core as well because a WebView2 environment keeps the proxy it started with, across reloads of
  either module.
- `Modules/Browser` owns the views: a browser panel shows session tabs, each with this window's own WebView2 page,
  and saves their ids; `BrowserTabSync` decides, apart from WebView2, which navigations of a page move its tab. In the
  daemon it registers its tools only; they run in a window through `IBrowserWindows`.
- `Modules/Remote` (`Lokn.Modules.Remote`, depends on `conversations`) owns the protocol codec,
  secure channel, transports, devices, pairing and its confirmation (`PairingPrompts`), `HostGateway`
  (role `host`), one `RemoteClient` per linked host (the daemon link and each paired host), the paired
  hosts list (`PairedHosts`, `HostPairing`), the remote `IWorkspaceHost`, the stream panel, the web client
  assets, web push, relay and funnel modes, the browser frames (`BrowserService`), the localhost tunnel
  (`TunnelService` on the host, one `TunnelClient` per paired host), and **Remote access**, the editor of the `remote.access`
  entry in Settings › Remote (`lokn://remote/role` opens it there through the shell's `lokn://shell/settings/<id>` link).
- `Relay/` (`Lokn.Relay`, ASP.NET Core, not a module, not referenced by the app) copies
  ciphertext between a host and its devices and serves the web client under `/h/{hostId}/`.

## Frames

A frame is one UTF-8 JSON object, camelCase. Fields:

| Field | Meaning |
|---|---|
| `t` | Frame type (required). |
| `id` | Request number, set by the sender of a request; unique per connection and direction. |
| `re` | The `id` this frame answers. Answers are `{ "t": "ok", "re": n, ... }` or `{ "t": "err", "re": n, "code": "...", "message": "..." }`. |
| `ch` | Conversation id (`N` format GUID) for chat frames. |
| `seq` | Per-chat sequence number on host pushes, for `attach.since`. One counter per chat for the life of the host process, shared by every connection; it rises with each push of the chat and with each change to its messages, also while no connection is attached. It starts at 0, so `since` 0 or less always asks for the messages. |
| `rev` | Revision of the host's lists on `store` and `working` pushes and in `sync`: one counter for both, raised by each such push. A push whose `rev` is at most the `sync` snapshot's is already in the snapshot. |

Carriers:

- **Named pipe** `\\.\pipe\loom-host-<user SID>` (bare: `-<hash>` suffix), pipe ACL = current
  user only. Frame = 4-byte little-endian length + UTF-8 bytes. No encryption. The client is
  device `local` with scope `control`. The client connects with `CurrentUserOnly` and then
  refuses a server process whose integrity level is below its own, so a sandboxed or
  low-integrity process of the same user cannot pose as the daemon.
- **WebSocket** (`ws://<addr>:<port>/loom`, default port 47390). Handshake frames are text
  messages; after the handshake every frame is one binary message, encrypted.
- **Relay**: the same WebSocket bytes, copied by the relay.

Errors use codes: `scope`, `unknown-chat`, `version`, `bad-request`, `failed`, `revoked`, `busy`.

Limits, so that neither strangers nor paired devices can exhaust the host:

- The WebSocket host keeps at most 512 connections, 32 from one remote address (loopback, where
  `tailscale serve` connects from, counts only toward the total). Server sockets ping every 20 s
  and drop a peer that leaves a ping unanswered for 30 s.
- A handshake message may take 4 KiB; at most 64 handshakes run at once, 8 from one address
  (behind `tailscale serve`/Funnel the address is the last `X-Forwarded-For` entry), each for at
  most 15 s. After the handshake a frame may take 16 MiB.
- Each connection runs at most 64 incoming requests at once; more are answered `busy`.
- Outgoing messages wait in a queue of at most 64 MiB; a message not sent within 60 s, or a full
  queue, means the peer stopped reading, and the connection is dropped.

## Secure channel (WebSocket and relay)

P-256 keys, SPKI DER, base64url without padding. All in the .NET BCL and in WebCrypto.

Pairing (once). The host shows a pairing link and its QR:
`loom://pair#<base64url of {"hostId","hostKey","secret","addrs":[...],"relay"?}>`, where
`secret` is 32 random bytes, valid 5 minutes and one use. The web client accepts the same
data after `#pair=` in its URL. The device makes a static key pair and sends, as plaintext
text frame:

```
{ "t": "pair", "deviceId": <guid N>, "name": "<device name>", "deviceKey": <spki>,
  "mac": base64url( HMAC-SHA256( secret, utf8( hostId + "." + deviceKey ) ) ) }
```

The host refuses a `deviceId` that is already paired (`bad-request`; remove the device on the
host first), checks the MAC, stores `{ deviceId, name, deviceKey, scopes: ["view"], paired }` in
`%LOCALAPPDATA%\Lokn\devices.json` (bare: in its folder), answers
`{ "t": "paired", "hostId", "scopes" }` and closes. The host keeps one pairing offer at a time:
showing a new link withdraws the previous one, and **Done** withdraws it. Revoke deletes the entry and
closes its connections.

Pairing confirm. A device that paired with `["view"]` waits for an answer: the daemon keeps it in memory,
pushes `admin.pairing { deviceId, name, paired }` to Loom on this PC and lists it in `admin.status`'s
`pending`, and the daemon's `HostStatus` counts it (`PendingPairing`, `PendingPairings`). Loom on this PC (in
`daemon` role) posts a notification "<name> paired with this PC" / "It can view your chats. Allow it to
control this PC?" with **Allow control** and **View only** (`lokn://remote/pairing/allow/<deviceId>`,
`…/view/<deviceId>`), and the status bar shows the dialog "Allow <name> to control this PC?" with the
Control text. Either answer sends `admin.confirm`; the daemon then pushes `admin.pairing.done`, which closes
the dialog and marks the notification read in every Loom window. **Allow control** in Remote access's
device list (`admin.scopes`) and revoking also end the wait. Without a Loom attached, the daemon's tray icon
announces the device instead (Roles). A restarted daemon forgets the waits; the devices stay view-only.

Pre-granted links. **Pair to control…** in Remote access sends `admin.pair { control: true }` and warns that the device gets control of this
PC, so the code is for that device only; **Pair to watch…** pairs with view only. The device that pairs through a control offer is stored with
`["view","control"]`, its `paired` frame says so, and Loom on this PC hears `admin.paired { deviceId, name,
control: true }` instead of a question. An answer without `control` comes from an older daemon: the panel
withdraws the offer (`admin.unpair`) and asks to restart the daemon.

| Scope | Allows |
|---|---|
| `view` | Read chats and the store (`sync`, `attach`, `detach`), the browser tabs (`browser.sync` and its pushes), web push. No files, terminals, processes or window streaming. |
| `control` | Also prompts and dialogs (`call`), `store`, `upload`, naming chats with the host's model (`tidy`), browser tab changes (`browser.open`, `.navigate`, `.report`, `.close`), lending its browser to the host's agents (`tools.offer` of browser tools), tunnels to the host's loopback (`tcp.*`), and the host's workspace (`fs.*` including reads and watches, `proc.*`, `pty.*`) and windows (`cap.*`). Even then Loom's own keys and device list stay off-limits (see Workspace frames). |

UI text: **View** — "View: read chats and the store. No files, terminals, processes or window
streaming." **Control** — "Control: also send prompts, change the store, and use this PC's
files, terminals, processes and windows (stream and input) as you. Loom's own keys and device
list stay off-limits, but a controlling device can run any program, so only allow devices you
trust like this PC."

Key and state files (`host-key.pem`, `device-key.pem`, `vapid-key.pem`, `devices.json`,
`push-subscriptions.json`, `remote-hosts.json`, and a legacy `remote-host.json` until it is imported) are
written through a temporary file whose ACL grants only the current user (no inherited entries), then moved
into place, so a bare folder on a shared or synced location does not expose them.

Connect (every time), plaintext text frames:

```
C → H  { "t": "hello", "v": 2, "deviceId", "eph": <spki> }
H → C  { "t": "hello", "v": 2, "hostId", "eph": <spki>, "scopes": [...] }
```

Keys, 3DH:
`ikm = ECDH(cEph, hEph) ‖ ECDH(cStatic, hEph) ‖ ECDH(cEph, hStatic)` (raw X coordinates,
32 bytes each), `salt = SHA-256( cEph ‖ hEph ‖ SHA-256( C hello ) ‖ SHA-256( H hello ) )` (SPKI
bytes, and each `hello` message's UTF-8 bytes exactly as sent, so a changed plaintext hello
breaks the keys), HKDF-SHA256 → 32 bytes with
info `"loom c2h v1"` and 32 bytes with info `"loom h2c v1"`. Each encrypted message is
`counter (8 bytes, little-endian) ‖ AES-256-GCM ciphertext ‖ tag (16)`, nonce = 4 zero bytes ‖
counter; counters start at 0 per direction and must increase by one, else close. The first
encrypted frame C → H is `{ "t": "ready" }`; the host answers `{ "t": "ready" }`. A failed
decrypt closes the connection. An unknown or revoked `deviceId` gets plaintext
`{ "t": "err", "code": "revoked" }` and close. That answer is not authenticated, so a client
shows it but keeps its pairing; pairing again replaces it.

## Chat and store frames

Client → host requests (`control` scope needed where marked ●):

| Frame | Answer | Meaning |
|---|---|---|
| `sync {}` | `{ projects: [ProjectInfo], conversations: [ConversationInfo], hostName, home, role, version, working: [ch], boot, rev, addrs? }` | Full store snapshot (no messages) of every profile, the chats whose agent works, the host's home folder (`home`, where a chat outside projects works until a folder is chosen; a paired PC's mirror uses it, never its own), the host's build (`version`), `boot`, a random id of the host process, `rev`, and the addresses devices dial now (`addrs`, as in pairing links), which a paired PC saves with the reported `hostName` in its entry of `remote-hosts.json`, so it follows the host's address changes without pairing again and names the host while it is offline. Host then pushes `store` and `working` to every connection. A client holds the `store` and `working` pushes that arrive before it applied the snapshot, then applies those with a higher `rev`. |
| `attach { ch, since, panel? }` | `{ seq, messages: [ConversationMessage]?, pages?, state: ChatSessionState, tools: {callId: frame}, intents: {callId: text} }` | Starts pushes for the chat and attaches a panel id to the host's session, so it keeps running: `panel` (the client's panel showing the chat, `N` format), or else one of the connection's own. The last attached panel is the caller of the agent's tool calls (Host tools). `messages` is omitted when `since` is above 0 and equals the current `seq`. A client sends `since: -1` on its first attach and after `boot` changed. A transcript over 4 MiB is cut into pages of whole messages, taken at the attach: `messages` is the first and `pages` their count. |
| `attach.page { ch, page }` | `{ messages }` | Page `page` (1 to `pages` - 1) of the transcript the connection's last `attach` of the chat took; the client asks for them in order before it applies the answer, holding the chat's pushes meanwhile. Another `attach` or a `detach` drops the pages. |
| `detach { ch }` | `{}` | Stops pushes; detaches the client's panel. A closed connection detaches from every chat. |
| ● `call { ch, m, a: [...] }` | `{ r? }` | Invokes a whitelisted `IChatSession` method by name (see `ChatSessionCall`) on the UI thread; every method needs `control`, `CheckNewTaskAsync` too, as it runs a paid model request on the host's account. `Attach` and `Detach` are refused: panels use `attach` and `detach`. |
| ● `store { op: StoreOp }` | `{}` | Applies a store operation on the host; the host broadcasts it back as a push. |
| ● `upload { name, offset, data, final }` | `{ path? }` | Chunked attachment upload, at most 256 KiB per base64 chunk, `offset` = bytes already sent (0 starts the file); written to `attachments\remote\<deviceId>\<name>` in the host's data folder; `final` answers that path. The name keeps its last segment with valid characters only, and a Windows device name (`NUL`, `COM1`, …) gets a `_` in front. Uploads stay, since chats refer to them; a device may keep at most 1 GiB, after which uploads fail with `failed`. |
| ● `tidy { project, chats: [ch] }` | `{}` | Names the listed chats of a host project afresh, as Tidy chats on a client asks: the host reads their prompts from its own history files, asks its own Omp, and gives each chat it still has in the project, with a chat the user did not name, its new title (`SetAutomaticTitle`), which reaches every client as `store` pushes; it answers once the titles are applied, or with the failure, which the client shows as its Tidy chats error. A host too old to know the request answers `bad-request` (`Unknown request`), which the client shows as "Update Loom on <host> to tidy its chats". |

Host → client pushes: `store { op, rev }` and `working { ch, busy, rev }` to every connection,
and to the connections attached to the chat `state { ch, seq, s: ChatSessionState }`,
`msg { ch, seq, m: ConversationMessage, persist }`, `msgs { ch, seq, m: [ConversationMessage] }`
(replace all), `tool { ch, seq, id, f }` (`f: null` removes the tool) and
`evt { ch, seq, e: ChatSessionEvent }` (`{ name, args: [...] }`). Pushes for a chat can arrive
before its `attach` answer; a client holds them and, after applying the answer, applies those
whose `seq` is above the answer's. Each connection's pushes leave through one queue in order; a
`msg` replaces the same message's `msg` still waiting there (streaming sends the whole message
each time), keeping `persist` once true. A connection with 4096 pushes waiting, or a push too
large to send, is closed, and the client syncs and attaches again when it reconnects. When a
session asks for the user (a question or a finished turn), the host sends a web push to every
device without a connection attached to its chat; the web client detaches while its page is
hidden, so a phone in a pocket is notified. A deleted chat's feed and web push handler are
dropped (`ConversationStore.Deleted`). In a window
(role `local`), a session that asks while no panel shows its chat posts a notification whose
link (`lokn://conversations/open/<id>`) opens the chat, marks it waiting in the sidebar, and its
question shows in the notification feed as that link; a panel showing the chat answers it in
place. The daemon has no window and leaves this to the web push. A client's proxy hears its
host only while a panel shows the chat, so a closed chat's question is left out of its feed: a push
the host sent before it took the last panel's `detach` is dropped, and the next `attach` asks from
the last push applied. An `attach` answer replaces what the proxy showed, telling the tool rows and
questions that changed meanwhile, and a prompt sent through a proxy shows the chat working at once,
until the host's next state or its refusal.

`StoreOp = { s: "conversations" | "projects", op: "<method name>", a: { ... } }`, one per
public mutator of `ConversationStore` and `ProjectStore`, with named arguments. A mirror
store applies each local mutation at once, so its caller reads it back, and sends it as a
`store` request; the host's `store` push of it finds it applied already, since every mutator
leaves a value it already holds alone, and pushes come in the host's order, so every mirror ends
as the host. `MoveConversation` is the exception: moving twice is not a no-op, so a mirror only
sends it and moves when the push comes back. `AppendMessage`, `UpdateMessage`,
`ReplaceMessages` and `SetWorking` are host-only and never sent by a client. A conversation
deleted by an op, or missing from a `sync` snapshot, raises `ConversationStore.Deleted`: the
Conversations module unloads the panels showing it and stops its session, on the host as on
every client.

## Admin frames (device `local` only)

Loom on the host's PC manages the daemon over the pipe; any other device gets `scope`.

| Frame | Answer |
|---|---|
| `admin.status {}` | `{ role, network, port, relayUrl, addresses: [...], webUrl?, relayStatus?, error?, pairing?, pairingControl?, hostId, hostName, pid, version, connected: [{ deviceId, name, control }], pending: [{ deviceId, name, paired }] }`; `pairing` is the live offer's expiry (unix seconds), absent once a device used it or it was withdrawn, and `pairingControl` whether it grants control; `pending` lists the devices waiting for an answer to their pairing |
| `admin.pair { control? }` | `{ link, webUrl?, expires, control }` (unix seconds): a new pairing offer for the addresses the network mode reaches (`failed` when none), which withdraws the earlier one; with `control: true` the device that pairs through it gets `["view","control"]` at once. `lan` has no `webUrl`: a plain `http` page on a LAN address gets no WebCrypto. The offer is withdrawn when the connection that asked closes, and Remote access sends `admin.unpair` when its panel closes, since a link works only while it is shown. |
| `admin.unpair {}` | `{}`; withdraws the pairing offer, as Remote access does when its code is closed |
| `admin.confirm { deviceId, control }` | `{ devices }`; the user's answer to a pending pairing: `control` grants control and closes the device's connections; both end the wait. `bad-request` for an unknown device or one that does not wait |
| `admin.devices {}` | `{ devices: [{ deviceId, name, scopes, paired, connected }] }` |
| `admin.scopes { deviceId, control }` | `{ devices }`; closes the device's connections so they reconnect with the new scopes, and ends its wait for an answer |
| `admin.revoke { deviceId }` | `{ devices }`; closes the device's connections and ends its wait for an answer |
| `admin.network { network, port, relayUrl }` | the new `admin.status`; saves the settings and restarts the listeners, even when nothing changed, which retries a failed mode; `error` says what failed |

The daemon pushes to every connection of device `local`:

| Push | Meaning |
|---|---|
| `admin.pairing { deviceId, name, paired }` | A device paired with view only and waits for the user's answer (Pairing confirm). |
| `admin.pairing.done { deviceId }` | It was answered, revoked or rescoped; prompts for it close. |
| `admin.paired { deviceId, name, control: true }` | A device paired through a link that grants control; Loom posts a notification. |

Remote access shows what each scope allows next to pairing and every device: **View** reads
chats and the store, with no files, terminals, processes or window streaming; **Control** also
sends prompts, changes the store, and uses the host's files, terminals, processes and windows as
the user. Stopping the daemon from the panel asks first, since it stops running turns and
disconnects every device.

## Workspace frames (paired hosts; `control` scope for every frame)

| Frame | Answer |
|---|---|
| `proc.run { file, args: [], cwd, input?, env? }` | `{ code, out, err }` (UTF-8 text) |
| `fs.stat { path }` | `{ exists, dir, size, mtime }` |
| `fs.list { path }` | `{ entries: [{ name, dir, size, mtime }] }` |
| `fs.read { path }` | `{ data }` (base64) |
| `fs.write { path, data }` | `{}` |
| `fs.delete`, `fs.move { path, to }`, `fs.mkdir { path }` | `{}` |
| `fs.watch { path, recursive }` | `{ watch }`; host pushes `fs.changed { watch, path, kind }` |
| `fs.unwatch { watch }` | `{}` |
| `pty.open { file, args, cwd, cols, rows, env? }` | `{ pty }`; host pushes `pty.out { pty, data }` (base64), `pty.exit { pty, code }` |
| `pty.in { pty, data }`, `pty.resize { pty, cols, rows }`, `pty.close { pty }` | `{}` |

Details (`WorkspaceService` on the host, `RemoteWorkspaceHost` on the client):

- Every frame needs `control`, reads and watches included. The host serves its local workspace.
- For remote devices (not `local`) paths must be fully qualified local paths: UNC
  (`\\server\share`), device (`\\.\`, `\\?\`) and alternate stream (`file:stream`) paths are
  refused before anything touches them. A path is resolved through links, junctions and short
  names; Loom's secret files (`*-key.pem`, `devices.json`, `push-subscriptions.json`,
  `remote-hosts.json`, `remote-host.json`) inside the data folder in use or `%LOCALAPPDATA%\Lokn` are refused with
  `scope`, and `fs.move`/`fs.delete` may not take such a folder or one holding it. `proc.run` and
  `pty.open` refuse those paths as program or working folder, a data folder as working folder,
  and arguments that name a secret file; a controlling device still runs programs as the user.
- `fs.read` answers files of up to 12 MiB less 48 KiB (so the base64 answer fits a frame) and
  refuses larger ones with `failed` before reading them. A connection holds at most 32 watches
  and 16 terminals, and is closed when more than 4096 of its pushes wait to be sent.
- `mtime` is Unix milliseconds (UTC), `0` for a missing path. `kind` is `created`, `changed`,
  `deleted`, `renamed` or `lost`. `pty.in` `data` is plain text; `pty.out` `data` is base64
  UTF-8 and may split a character, so the client decodes per terminal.
- Watch and terminal ids are per connection; when it closes the host ends its watches,
  terminals and running `proc.run`s. `pty.close` still produces `pty.exit`. A terminal's
  pushes may arrive before the `pty.open` answer; the client holds them until it has the id.
- The client's `Watch` and `OpenPty` return at once. A refused watch reports `lost`; a refused
  terminal writes the reason and exits with -1. On close, watches report `lost`, terminals exit
  with -1 and pending calls fail with `IOException`. Errors map to `IOException`, `scope` to
  `UnauthorizedAccessException`, and a `proc.run` failure to `Win32Exception`.

## Browser frames

The browser works like Tern's browser blocks. A browser tab belongs to the session, not to a window: `BrowserTabInfo
{ id, url, title, profile, loading, progress, failed, canGoBack, canGoForward, created, lastUsed, order, rev, by }`.
The authority (this process in `local`, the daemon in `daemon` and for paired PCs) keeps the tabs and saves them in
`browser-tabs.json` in the data folder (bare: in the bare folder), without `loading`, `progress`, `rev` or `by`; a
file that does not parse is reported and never overwritten. Every Loom window that shows a tab renders it with its
own WebView2 page and reports what the page does; the session holds no web view, so tabs survive windows closing,
and the daemon's survive Loom closing and the daemon restarting. The web client shows no page (Web client).

| Frame | Scope | Answer |
|---|---|---|
| `browser.sync {}` | `view` | `{ tabs: [BrowserTabInfo] }`, every tab with its revision |
| `browser.open { tab, url, profile, by? }` | `control` | `{ rev }`; an id the host has already is left alone |
| `browser.navigate { tab, url, by? }` | `control` | `{ rev }`; sets `url`, `by`, `lastUsed`, `loading`, and clears `failed` |
| `browser.report { tab, by, url, title, loading, progress, failed, canGoBack, canGoForward, navigated? }` | `control` | `{ rev }`; a window's page state, whole: a missing `failed` clears it |
| `browser.close { tab }` | `control` | `{}`; closes the tab in every window |

`tab` is the tab id (a GUID), `by` the reporting view's id, `progress` 0 to 100, `url` at most 8192 characters and
`title` cut at 1024. `rev` is the tab's revision after the change, the same as before when it altered nothing; there is
none once the tab is closed or unknown. A report without `navigated: true` describes the page at the tab's address: when
its `url` differs (a window still catching up), the host drops it, and it never changes `url`, `by` or `lastUsed`. A
report with `navigated: true` is the view's own navigation and moves the tab to its `url`, with its `by`. A malformed or
unknown change is `bad-request`. The host pushes every change, whoever made it, to every connection, the sender's too:
`browser.tab { tab: BrowserTabInfo }` and `browser.closed { tab }`, in either order with the change's answer. Each change
the host applies raises that tab's `rev` (per tab, from 1 for a new tab; not saved, so a restarted host starts again and
clients take its snapshot); a change that alters nothing is not applied, raises nothing and pushes nothing. A
connection's newer push of a tab replaces its older one still waiting to be sent, so a page's progress never piles up.
The session keeps at most 256 tabs; opening more closes the least recently used. A title alone is saved with the next
change of an address or the tab list, or at shutdown, so a page that animates its title never writes the file each time.

Clients mirror the tabs like the conversation store (`BrowserTabs.BeginMirror`): a mirror applies its own change at
once, so the window reads it straight back, and sends it. While a change it sent for a tab is unanswered, the mirror
holds that tab's pushes, which may describe the tab before the change: an echo of a report sent before a click's commit
would otherwise send the page back. The answer raises the mirror's `rev` to the host's; once no change of the tab waits,
the newest held push applies only when its `rev` is at least that, and a later push of the answered `rev` replaces the
mirror's own state with the host's. Any other push applies only when its `rev` is newer than the mirror's. A client asks
`browser.sync` right after `sync` and holds the browser pushes until it loaded the answer (`BrowserTabs.Load`). A change
made while disconnected waits and is applied and sent again after the next snapshot: every open, navigate and close,
but only each tab's latest report, dropped when the snapshot has the tab at another address than the mirror last
received; a close of a tab opened offline drops both. At most 256 wait; past that the oldest goes, an open with its
tab's other changes, so every change sent still applies. A view-only device's change is refused locally, answered as
refused, and the next snapshot undoes it. In the `daemon` role the mirror keeps the tabs read from the file until the
snapshot, since it is the daemon's file; Loom never writes it while it mirrors. A paired host's mirror is its own
`BrowserTabs` (`LoknHost.BrowserTabs`), in memory, and starts empty. A browser
panel restored before the snapshot waits for its tabs and drops those the snapshot lacks.

Views (`Modules/Browser`): a browser panel saves the ids of the session tabs it shows (`CaptureState` holds `Ids`,
their addresses, the selected one and the profile), so a reopened window shows the same tabs from the session; state
saved before tabs were the session's opens its addresses as new tabs. Each window's page loads the tab's `url` when it
starts, and loads it again whenever another view set it (`by` differs and the page shows another address).
`BrowserTabSync` decides which address changes are a view's own: a navigation the page starts is, when the user, an
agent or the page started it (WebView2's `IsUserInitiated`, or the view owning the address: it set it last, or no view
did), unless the view started it to load another view's address. Such a followed load, with its redirects, never moves
the tab, so two windows whose cookies redirect differently (signed in on the host, signed out on a paired PC) never pull
each other back and forth, and opening a host tab elsewhere never pulls the host's page to a sign-in page. Within a
followed page, an address change that keeps the document (a script's history entry) is the view's own only while the
page has a user activation (`navigator.userActivation.isActive`). A page reports its title, progress, failure and
history only while it shows the tab's address, and a new address (`navigated`) only from its own navigation, also when
that navigation ended without replacing the page (a download or 204): the tab then returns to the page still shown.
Closing a tab (its ×, middle click, or an agent) closes it in every window, and a panel left without tabs closes.
Closing a panel or a window leaves its tabs to the other windows of a daemon or paired host; in the `local` role, which
has no other window, closing a panel closes the tabs no other panel shows (not a reload or shutdown). **Session tabs**
in the tab strip lists the session's tabs the panel does not show, such as one another window opened, to show one
there or close it everywhere. `browser_tabs` lists the tabs open in the window and the 20 most recently used others,
and counts the rest.

### Localhost forwarding and what a paired PC shows (each paired host)

A paired PC shows each host's session, whose files and `localhost` are that host's. Each paired host has its own
`BrowserTabs`, `TunnelClient`, proxy and WebView2 profile (`<data>\browser-hosts\<hostId>`, since a WebView2 user data
folder keeps the proxy it started with), so pages of two hosts never share a loopback. Its web views therefore load only
`http`, `https` and `about:blank` (`BrowserTabs.CanShow`): a tab at a file, a drive or UNC path or another scheme shows
why instead of loading this PC's file, the address bar refuses such an address, and a page's navigation or new window
to one is cancelled. Every connection of its pages goes through a SOCKS5 proxy on this PC's `127.0.0.1`
(`BrowserProxy`, Chromium's `--proxy-server` with `--proxy-bypass-list=<-loopback>`, so loopback is proxied too). The
proxy resolves names itself: a destination that is loopback by name (`localhost`, `*.localhost`) or by any address it
resolves to (127.0.0.0/8, `::1`, the unspecified address, IPv4-mapped forms) goes to the same port on the host through
the client's `TunnelClient`; any other address of this PC is refused; anything else connects directly. So a host's dev
server page, its scripts, `fetch` calls, WebSockets and redirects all reach the host's loopback under their own
addresses (https certificates for `localhost` match), and nothing a page or a lent tool loads reaches this PC's own
services. The proxy lives as long as the process: a WebView2 environment keeps the proxy it started with.

| Frame | Direction | Meaning |
|---|---|---|
| `tcp.open { tcp, port, host? }` → `{}` | client → host | Connects stream `tcp` (the client's id, unique per connection) to `port` on the host's loopback: `host` is `localhost` (127.0.0.1, then ::1), a loopback IP address, or by default 127.0.0.1. |
| `tcp.data { tcp, data }` → `{}` | client → host | Writes the bytes (base64, at most 64 KiB); answered once written, and the client sends one chunk at a time. |
| `tcp.eof { tcp }` → `{}` | client → host | The page sent all it will; the host ends its side's sending to the service, whose answer still flows back. |
| `tcp.data { tcp, data }` | host → client push | Bytes from the host's service. |
| `tcp.ack { tcp, bytes }` | client → host push | The client wrote that many bytes to the page's connection. |
| `tcp.close { tcp }` → `{}`, push | both | The stream ends; the other side closes its socket after writing what it has. |

Security: every `tcp.*` frame needs `control`, which already lets a device run any program on the host as the user, so
tunnels add reach only to what the host serves on loopback. The host connects only to its loopback addresses
(`IPAddress.IsLoopback`); any other `host` is refused with `scope`, so a device cannot use the host to reach other
machines. It refuses Loom's own gateway port (the port the network listeners were asked for and bound), so a tunnelled
connection never reaches the gateway as a local one (Tailscale Serve and the per-address limits trust loopback). A
connection holds at most 64 streams, counting those still connecting (`busy` beyond); a stream sends at most 512 KiB
the client has not acknowledged, so neither side buffers without bound, and the usual push queue limits close a
connection that stops reading. Streams end when their connection closes. On the paired PC the proxy binds 127.0.0.1
only and refuses loopback destinations while no host is connected or the host lets the PC only view; while the client
runs, any program on this PC can reach the host's loopback ports through it, as it could with the device key in the
data folder.

## Stream frames (`control` scope)

| Frame | Answer |
|---|---|
| `cap.list {}` | `{ windows: [{ hwnd, title, process, w, h }] }` |
| `cap.preview { hwnd, icon? }` | `{ jpeg?, minimized, icon? }` (base64): one capture at most 400 pixels wide, none while `minimized`; with `icon: true`, the window's icon as 32×32 premultiplied RGBA |
| `cap.start { hwnd, fps, quality, maxWidth?, maxHeight?, codecs?, resolution?: { w, h } }` | `{ cap, codec, resolution?: { w?, h?, message? } }`; host pushes `cap.frame { cap, w, h, codec, jpeg \| data }` (base64) at most `fps` per second, with at most 3 frames waiting for the client's `cap.ack { cap }`; each ack frees one |
| ● `cap.input { cap, kind: "move"|"down"|"up"|"wheel"|"key", x, y, button?, delta?, vk?, down? }` | `{}` (x, y in frame pixels) |
| `cap.stop { cap }` | `{}` |

`cap` ids are per connection; a connection may run up to 4 streams, and closing it stops them all.
`cap.ack { cap }` and `cap.end { cap, message }` are pushes: the client acks after it showed a frame;
the host pushes `cap.end` when a capture fails (for example the window closed) and the stream is gone.
`cap.input` units: `button` as in the DOM (0 left, 1 middle, 2 right, 3 back, 4 forward), `delta` in
Windows wheel units (120 per notch, positive scrolls up), `vk` a Windows virtual-key code, `down`
true on press for `key`. `cap.list`, `cap.preview`, `cap.start` and `cap.input` answer `err scope` to a view-only
device. Input requests are handled in arrival order. Keys and clicks are sent only while the
streamed window, or a window it owns, is in the foreground; otherwise they are dropped.

Screens: since Remote 2.9.0 `cap.list` lists the host's screens before its windows, with a negative
`hwnd` (-1 the main screen, then -2, -3 in the order Windows lists them), `process` empty and the
screen's size in physical pixels (`ScreenCatalog`). A screen streams through Windows.Graphics.Capture with the
pointer drawn in; its `cap.input` maps frame pixels to the screen's bounds and goes to whatever has the
focus, with no foreground check. An older host lists no screens; an older client shows them as windows.

Previews: since Remote 2.13.0 the stream panel shows each listed window as a tile with a `cap.preview`
capture, three requests at a time, and asks for the icon once per process. An older host answers an
error, and the panel shows the tile without a picture and with the process's first letter.

Frame size: since Remote 2.11.0 frames keep the source's full size, scaled down only to fit `maxWidth`
and `maxHeight` (physical pixels; missing or 0 means no cap, and frames are never scaled up). The stream
panel sends its surface's size in physical pixels once it is laid out, and after a resize settles
(300 ms) the push `cap.view { cap, maxWidth, maxHeight }` gives the running stream the new cap (0 for
none); a host before 2.11 ignores it. Before its first layout the panel sends no cap. An older Loom sends
`maxWidth: 1600`, which still caps its frames, and an older host takes a missing `maxWidth` as 1600. H.264
frames are also kept within 4096 pixels a side and 4096×2304 pixels in all (H.264 level 5.1's largest
frame), so a 3440×1440 screen streams at full size; JPEG frames go up to 7680×4320. The H.264 bitrate
follows the frame's pixels: 0.05 to 0.2 bits per pixel per frame by `quality`, between 0.5 and 50 Mbit/s.

Screen resolution: since Remote 2.11.0 a screen stream may ask the host to switch that screen to a size
with `resolution: { w, h }` in physical pixels; window streams ignore it. The stream panel sends the size
of the screen its Loom window is on, while the toolbar's **Use this device's resolution** switch is on
(the default; it shows for screens only, and flipping it restarts the stream). The host picks the screen's
supported mode (current orientation and color depth) closest to the size: the largest that fits within
it, else the smallest; of the same aspect ratio (within 2 %) first; at the current refresh rate when that
size has it, else its highest. It applies the mode with `ChangeDisplaySettingsEx` and `CDS_FULLSCREEN`,
a temporary change never written to the registry, before it opens the capture, and answers the screen's
mode in `resolution { w, h }`. When the screen is not at the size asked for, `resolution.message` says
why, and the panel shows it under the toolbar: `This screen has no 2560×1600 mode; streaming at
1680×1050.`, `This screen could not switch to …; streaming at …` (Windows refused; the screen stays as it
was), `Another viewer chose this screen's resolution; streaming at …`, or that the screen cannot change
its resolution. The first stream that asks wins; later streams of that screen, from any connection, use
its mode. The original mode returns when the last of those streams stops (`cap.stop`, a failed capture,
or its connection closing) and when the gateway stops (`StreamService.Dispose`). Before a switch the
host writes the original modes to `screen-modes.json` in its data folder (the bare folder of a bare
daemon); a daemon that starts and finds the file, because the last one ended with a screen switched,
restores those modes and deletes it. Every mode change goes through `IDisplayModes`
(`Win32DisplayModes` in the daemon), so the checks use a fake and never switch a real screen.

Codecs: a client lists in `codecs` what it decodes, best first; without the field the host sends JPEG.
With `"h264"` listed, the host encodes H.264 with Media Foundation (a hardware encoder when the GPU has
one, else Windows' software one): baseline profile, low-latency mode, Annex B in `data`, one picture per
frame and no B-frames; the first frame, and the first after the window's size changes, is a key frame.
A host that cannot encode H.264, or whose encoder fails, sends JPEG in `jpeg`, so a client decodes each
frame by its `codec` (a frame with no `codec` is JPEG). Frames are never dropped after encoding: the ack
window holds back the capture instead, so an H.264 stream stays decodable. The panel publishes decoded
frames as generations of one image source, so Goo keeps the displayed generation until the replacement's
GPU upload completes instead of showing the background between frames. The host captures with
Windows.Graphics.Capture, falling back to `PrintWindow` where it is unavailable, and writes per-stream
timings to `%TEMP%\Lokn\stream.log` every five seconds.

Two more host pushes serve the agents' window tools (Host tools):

| Push | Meaning |
|---|---|
| `window.show { ch?, hwnd, title, panel? }` | An agent of chat `ch` works with window `hwnd` of the host. The gateway sends it to every `control` connection attached to the chat, with `panel` the client panel it was attached as (`attach.panel`; omitted for the connection's own id), else to device `local` when it is connected. The client opens a `remote.stream` panel on that host beside `panel` (else beside a panel that shows `ch`, else in the active pane) that streams `hwnd` at once, skipping the window list, or brings the one already streaming it forward (one per host and window). The active pane stays where it was, and the stream does not take the keyboard focus. |
| `cap.agent { hwnd, kind, x?, y? }` | An agent acted in window `hwnd`: `kind` is `capture`, `click`, `type`, `key` or `scroll`; `x`, `y` are where, as fractions (0 to 1) of the window's width and height, for `click` and `scroll`. Sent to every connection that streams `hwnd` and always to device `local`, whose panels stream in process; a daemon-role Loom passes it to them. Viewers show **Agent is using this window** in the panel's header until 3 s after the last action, and a short ripple at the point. |

A stream panel's input (`StreamPanelInput { hwnd, title }`) is also its saved state, so a restored layout streams the
same window again, or shows the window list with the reason when it is gone.

### Following the host's agent (`cap.show`)

The Remote module gives agents the host's desktop: `remote_desktop_windows`, `remote_desktop_open` (start a
program and wait for its window), `remote_desktop_screenshot` (JPEG of a window in `%TEMP%\Lokn\desktop`) and
`remote_desktop_act` (click, double or right click, move, scroll, type, key chords; x and y in pixels of the
window's last screenshot). They run in the process whose agent calls them, the daemon for its chats, and
need Windows. Each call that works in a window makes the daemon push
`cap.show { hwnd, title, process, w, h }` to every connected device with `control`, Loom on this PC excepted.
A paired Loom that follows it (setting **Show the host's screen when its agent uses an app**,
`remote.follow-host-screen`, on by default) streams that window as `window.show` does: it brings forward the
stream panel on that host that streams it, or opens one that streams it at once.

## Web client and push

The host's WebSocket listener also serves the web client, embedded in the Remote module
(`Modules/Remote/Web/Assets`), through `WebClientAssets.Map`: `GET /` (and `/index.html`),
`/app.js`, `/app.css`, `/sw.js`, `/manifest.webmanifest` and `/icon.png`. The relay serves the
same files under `/h/{hostId}/` (below). The table of paths, content types and headers is
`WebClientFiles`, compiled into both. Every file has an `ETag` and answers `304` to
`If-None-Match`; all but the icon are `Cache-Control: no-cache`, and the page carries a
`Content-Security-Policy` that allows only its own files and WebSockets to its own origin
(`connect-src 'self'`, which CSP Level 3 browsers apply to same-origin `ws:`/`wss:`). The page refers to its files relatively. WebCrypto needs a
secure context: use `localhost`, `tailscale serve`, the relay's HTTPS domain, or `funnel`. The
client is plain ES2022 with no build step and loads nothing from elsewhere.

The pairing link's web URL (`admin.pair`'s `webUrl`, `PairingLink.BuildWebUrl`) is the page plus
`#pair=<payload>`: `https://<dns name>/` in `tailscale` (when MagicDNS gives a name) and
`funnel`, and `RelayDialer.BuildPageUrl`, `https://<relay>/h/<hostId>/` (`http://` for a plain
relay, the relay's path prefix kept), in `relay`. `lan` gives none, since its page is not a
secure context.

- **Pairing.** The page reads `#pair=<payload>`, also when a pairing link is opened in a tab that already
  shows the page, checks that `hostId` matches `hostKey`, and removes the fragment from the address bar and
  history at once; once paired it drops the host it showed and talks to the new one. It keeps one static P-256 key
  in IndexedDB (database `loom`, store `keys`, key `device`: `{ id, key, publicKey }`, the private
  key non-extractable) and each paired host as `host:<hostId>`: `{ hostId, hostKey, deviceId,
  paired }` (`paired` = when, in ms; `deviceId` = a new id per pairing, since a host refuses to
  pair an id it knows; records without one use the key's `id`). It always connects
  to its own origin (`ws://` on plain `http://`): when its path is `<prefix>/h/<hostId>/…` (served
  by a relay) to `wss://<origin><prefix>/device/<hostId>`, otherwise to `wss://<origin>/loom`; the
  link's `addrs` are for native clients. A `revoked` answer is not authenticated, so the page only
  says the host does not know it and keeps the record; pairing again replaces it. The IndexedDB
  records belong to the origin, so pages of several hosts on one relay share the store: a relay
  page uses the record of the `<hostId>` in its path, and a host's own origin the most recently
  paired record. The account profile the list shows is remembered per host (`localStorage`
  `loom.profile.<hostId>`).
- **Connection.** The handshake is the one above; on a lost connection the page reconnects with
  backoff (1 s doubling to 15 s), at once when it becomes visible or the network returns, then
  sends `sync`, holding `store` and `working` pushes until the snapshot is in (also for the
  re-syncs an unknown store op asks for; a connection runs one sync at a time), and re-attaches the
  open chat with `since` = its last `seq`, or -1 for a chat not attached yet and when `boot` changed.
  While the page is hidden its chat is detached, also across a reconnect, so the host sends web
  pushes; showing the page attaches it again. Overlapping attaches of one chat count only the
  newest; `pages` of a large transcript are fetched with `attach.page` before the answer applies,
  and a stale attach stops asking for them.
- **Chats.** The list groups chats by project (then the chats outside projects) for one account
  profile at a time, pinned first, and shows a working dot from `sync.working` and `working`
  pushes. Opening a chat sends `attach` and buffers its pushes until the answer, then applies
  those with a newer `seq`; leaving it sends `detach`. Tool results show inside their call's
  collapsed line. A new chat is the store operation `CreateConversation` with a GUID the page
  picks. The composer calls `Send`, or `Interject` with the Steer switch while the agent is
  busy, and `Stop`. Pending dialogs answer through `AnswerDialog` with the same
  `extension_ui_response` objects as the desktop (`DismissDialog` for `open_url`;
  questionnaire picks through `UpdateSelection`). A form (`method: "form"`) shows its HTML rebuilt through an allow list of elements
  and attributes, keeps its picks and typed text on the page, and checks fields with Omp's rules before it sends `data`. An `Interject`
  the host answers `false` (its agent is
  still connecting) keeps the message in the composer and says so. Without `control` the page says it
  is view only and disables the actions. Messages render as a Markdown subset with every text escaped
  and raw HTML left as text; links open only `http` and `https` pages, in a new tab with
  `rel="noopener noreferrer"`. The page and its scripts have no inline code, so its content policy holds;
  the checks read them as text (`RemoteWebChecks`), since they run no JavaScript.
- **Browser tabs.** Like Tern's web client, the page has no browser. After `sync` it asks `browser.sync`, holding
  `browser.tab` and `browser.closed` pushes until the answer, and applies a pushed tab only when its `rev` is newer.
  Below the chats it lists the session's tabs read-only (title, host, a dot while loading) with the note "Open on a
  computer: pages show in Loom's Browser panel there."; a host without browser tabs lists none.

Web push:

| Frame | Answer |
|---|---|
| `push.key {}` | `{ key }`: the host's VAPID public key, base64url of the uncompressed P-256 point (`applicationServerKey`) |
| `push.subscribe { endpoint, p256dh, auth }` | `{}`; `view` is enough. `endpoint` must be `https` on the default port with a dotted host name or a public IP address (not `localhost`, `.local`, loopback, private, link-local or shared addresses), `p256dh` an uncompressed P-256 point and `auth` 16 bytes, else `bad-request` |

`PushService` keeps the VAPID key as PEM at the path it is given (`vapid-key.pem` in the data
folder by default) and the subscriptions in `push-subscriptions.json` beside it, by device id,
at most 5 per device; an endpoint lives under one device only, and revoking a device drops its
subscriptions. The page subscribes when the user turns notifications on, and sends its
subscription again on every connect. `NotifyAsync( chatId, title, body, skipDevices? )` posts to every
subscription of every paired device but `skipDevices` an RFC 8291 `aes128gcm` body (one 4096-byte record) holding
`{ title, body, ch }`, with `TTL: 86400` and `Authorization: vapid t=<JWT>, k=<key>`, where the
JWT (RFC 8292, ES256) has `aud` = the endpoint's origin, `exp` = 12 hours ahead and
`sub` = `mailto:loom@localhost`. Posts use no proxy and follow no redirect, and connect only when
every address the endpoint's name resolves to is public, so a subscription cannot reach this PC
or its networks. Answers `404` and `410` drop the subscription; other failures
raise `PushService.Failed`. The service worker shows the notification (tagged by chat) and a
click focuses a page of its own scope (a relay origin serves other hosts' pages beside it) and opens
`#chat=<ch>`, taking only a GUID in `N` form. The host calls `NotifyAsync` when a chat finishes
or asks a question while no client is attached to it.

`QrCode.Encode( text )` draws the pairing link's QR code: byte mode (UTF-8), level M, the smallest
version from 1 to 25, the mask with the lowest penalty; it returns the modules without the quiet
zone, which the caller draws (4 modules).

## Relay

`Lokn.Relay` listens on HTTPS (Kestrel; TLS from its config).
`GET /host/{hostId}` (WebSocket): the host's control socket. The relay first sends
`{ "t": "relay.challenge", "nonce" }` with 32 fresh random bytes (base64url); the host answers
`{ "t": "relay.host", "hostKey", "sig" }`: `hostKey` is the host's static key (SPKI, base64url),
`sig` = ECDSA P-256 SHA-256 over
`utf8( "loom-relay-proof v2\n" + hostId + "\n" + purpose + "\n" + nonce )` with the host's static
key (IEEE P1363 format, base64url), where `purpose` is `control`; `hostId` must equal
base64url( SHA-256( hostKey ) )[..22]. A proof therefore works for one socket only and cannot be
replayed, and needs no clock. The host must answer within 10 s.
The relay sends nothing back on success; a failed proof closes the socket with status 1008
(policy violation) and the reason. A host that reconnects replaces its older control socket.
The relay pushes `{ "t": "relay.open", "sid" }` per device. The host then opens
`GET /host/{hostId}/{sid}` (a fresh challenge and proof, `purpose` = `data/<sid>`) and the relay
copies messages both ways, text and binary kept, between it and `GET /device/{hostId}` (the
device's socket), which waits up to 10 s and is otherwise closed with status 1001. An unknown or
expired `sid` is closed with 1008. A close on either side is passed on to the other. The relay
never parses device bytes. `hostId` is derived the same way everywhere.

Relay limits: 64 sockets per client address (the `X-Forwarded-For` address behind a reverse
proxy on loopback), else `429`; 16 devices waiting for one host and 1024 in total, else `503`; 64
devices connected to one host, else close 1001. `Relay:AllowedHosts` (a list of host ids in its
configuration) restricts the relay to those hosts (`403` for others); empty serves any host.
`RelayHostLink` opens at most 16 device sessions that have not finished their handshake; further
`relay.open`s are ignored and those devices time out.

Trust: the relay sees only ciphertext of native clients, which carry their own code. The web
client page served under `/h/{hostId}/`, however, comes from the relay, so a hostile relay
operator could serve a modified page that uses the browser's stored device key with that
device's scopes. Use the web client from a relay you trust, or open it from the host's own
origin (Tailscale Serve or Funnel), which needs no relay.

The relay also serves the web client (`RelayWebClient`): `GET`/`HEAD` `/h/{hostId}/` and
`/h/{hostId}/<file>` for the files above, with the host's headers, `ETag`s and `304`s; the
files are the Remote module's, embedded by link under the same resource names (`web/<file>`,
the icon from `assets/brand/loom-256.png`). `/h/{hostId}` without the slash is redirected to
`{hostId}/` so the page's relative paths resolve. The page is the same for every `hostId`; the
relay does not check that the host exists.

Relay addresses (`remote.relay-url`, the pairing link's `relay`) are `https://` or `wss://`
(secure), `http://` or `ws://` only for `localhost` or a loopback, private, link-local or shared
(Tailscale) IP address (`ArgumentException` otherwise), or a bare `host[:port]`, which
means `wss://`; a path in the address is a prefix for the endpoints. `RelayHostLink` keeps the
control socket open, reconnecting after 1 s and doubling the wait up to 30 s (back to 1 s once a
socket stayed up 30 s), and hands each data socket to the host's accept path, which runs the
secure channel as for a direct WebSocket. Relay sockets ping every 20 s and count the relay
gone after 20 s without a pong. `RelayDialer.ConnectAsync` opens the device's socket.

## Discovery

Every mode except `off` also listens on the host's LAN addresses and lists them first, so a Loom PC on the same
network connects directly. A paired PC keeps up to 8 host addresses, the newest reported first (`sync.addrs`, and
the `host.addrs { addrs }` push the host sends to connected devices when its addresses change). When none answers,
the client broadcasts `loom-find:<hostId>` over UDP to port 47391 on each IPv4 network, and the host with that id
answers `{ hostId, addrs }` with its LAN addresses; the client dials those, then the relay. A private address gets
2 s before the next one. Discovery reveals only addresses: every dial still checks the host's key.

`loom-who` asks every host: each answers `{ hostId, hostKey, name, addrs }` while it listens on the LAN, and an answer whose
key does not hash to its id is dropped. Remote access lists them under **Nearby on this network**. **Pair** sends a plaintext
`knock { deviceId, name, deviceKey }`; the host keeps the connection open up to 2 minutes and asks its user like a view-only
pairing (`admin.pairing` with `code`), showing a six-digit code from SHA-256 of both public keys, which the device shows too, so
someone between them would show another code. **Allow control** answers `paired { hostId, scopes }` and stores the device;
**Deny** (`admin.confirm { deny: true }`) answers `err failed`.

## Tailscale and Funnel

Network modes `tailscale` and `funnel` run `tailscale.exe` (`%ProgramFiles%\Tailscale` or PATH).
Every call first reads `tailscale status --json` and refuses with a user-facing error when
Tailscale is not installed, its service is not running, or `BackendState` is not `Running`
(signed out, stopped, awaiting approval). The pairing link's addresses come from
`tailscale ip -4`; the web client's HTTPS name is `Self.DNSName` without the trailing dot.
With Tailscale 1.52 and later (`serve --help` lists `--bg`):

- `tailscale` mode: `tailscale serve --bg --https=443 http://127.0.0.1:<port>` (tailnet only).
  Serving again also turns Funnel off for the port, so turning Funnel off is this command.
- `funnel` mode: `tailscale funnel --bg --https=443 http://127.0.0.1:<port>` (internet).

Earlier CLIs get `tailscale serve https:443 / http://127.0.0.1:<port>`, then
`tailscale funnel 443 on|off`. Each command may take 60 s (HTTPS certificates and Funnel must be
enabled for the tailnet in the admin console, else the CLI waits for that).

Leaving the `tailscale` or `funnel` mode, and stopping the daemon, removes the proxy with
`TailscaleCli.ServeOffAsync`: `tailscale serve --https=443 off` (1.52 and later), or
`tailscale funnel 443 off` then `tailscale serve https:443 / off`.

## Host tools

A daemon has no window. Its chats offer the agent only the tools of the modules it loads.
Loom in role `daemon` (device `local`) lends it its own, and Loom linked with a paired host lends that host its browser tools alone:

| Frame | Direction | Meaning |
|---|---|---|
| `tools.offer { tools: [AgentToolInfo] }` → `{}` | client → host request | Sent after `sync` on every connection and again whenever the client's `AgentToolRegistry` or settings change, when the list differs from the last one sent; the full list, which the host compares with the connection's earlier offer module by module. A paired device may offer only tools of module `browser`, and needs `control`; anything else gets `scope`. |
| `tool.call { call, name, args, caller?, conversation? }` | host → client push | The daemon's agent called an offered tool. `caller` is the panel id the call's chat was attached as (`attach.panel`), so a tool's "self" is the client panel showing the chat; `conversation` is the agent's chat, so a tool finds it even when no window shows it. |
| `tool.result { call, result: { text, isError, details? } \| error }` → `{}` | client → host request | Ends the call with the client's `AgentToolResult`, or with `error` as an error result: the client sends `error` when the tool threw or was cancelled, so no call waits for nothing. A result for a call no longer waited for is ignored. |
| `tool.cancel { call }` | host → client push | The agent abandoned the call; the client cancels the tool's token. |

The daemon adds the offer with `AgentToolRegistry.Offer`, one offer per module, under the tools'
own names and module ids, so Omp's per-module tool switches cover them. A module whose offered
tools (names, descriptions, schemas) are the same as before keeps its offer and its running
calls; only a module whose tools changed or went away is offered again, which cancels its own
running calls. Its own tools always win: an offered name
the daemon already has is skipped, and a daemon module that later registers that name takes it
over. The client runs a call with its own `app.Tools.InvokeAsync` on its UI thread, naming the paired host as the call's
`AgentToolContext.Host` (none for the daemon link), so a browser tool acts on that host's tabs. A call has
no timeout of its own, as the registry has none: it ends with the result, the agent's
cancellation (sent as `tool.cancel`) or the connection closing (an error result). When the
connection closes the daemon removes the offer; `AgentToolRegistry.Changed` makes each
`OmpToolBridge` republish.

Browser tools need a window's web view. The daemon loads the Browser module, whose tools (`browser_show`,
`browser_tabs`, `browser_navigate`, `browser_read`, `browser_act`, `browser_eval`, `browser_screenshot`) act on the
session's tabs; in the daemon each call goes to `BrowserTabs.Windows`, which the gateway's `ToolService` is, and runs as
a `tool.call` in a window that lends its browser: one that offered tools of module `browser`. The window that shows the
agent's chat is preferred (the connection that attached it as the call's `caller`, else the latest to attach it), so pages
such as the design studio open where the user reads the chat; then Loom on this PC (device `local`), else the paired PC
that offered most recently. With none, the call returns the error "No Loom
window is open to show the browser; open Loom on this PC or on a paired device." The window runs the tool on its own
views of the session tabs, showing a tab it does not show yet beside the chat (or in view); a `caller` that is no
panel of that window counts as none, since the chat may be attached in another window. Offered browser tools never
enter the daemon's registry, whose own names win, so Omp's switches and descriptions are the daemon's.

Each paired host's link offers only its browser tools, and only while `remote.lend-browser` (Global, default on; **Let
agents on paired hosts use this PC's browser** in Settings › Remote) is on and the host lets it control; turning it off offers an
empty list, which stops the lending, and a `tool.call` for any other tool, or while it is off, is answered with an
error. Its other tools would act on its own PC rather than the host's. Its browser tools never show the host's agents
this PC: they load only `http` and `https` pages, through the proxy that sends loopback to the host (Localhost
forwarding), so `browser_navigate` refuses files, drive and UNC paths and other schemes, every page tool refuses a tab at
such an address, and `browser_show` refuses `html` and `path`, whose files are on the host: the agent serves the page
on the host's localhost instead and passes its `url`. `browser_screenshot` uploads its PNG to the host (`upload`,
`BrowserTabs.ShareFile`) and answers the host's path, which the agent can read.

### Window tools

Agents see and use the windows of the PC their chat runs on through tools the Remote module offers with
`AgentToolRegistry.Offer` (module `remote`), in the daemon and in Loom in role `local`; Loom in role `daemon` leaves them to
its daemon. They are offered only while `remote.agent-windows` (Global, default on; **Let agents see and use this PC's
windows** in Remote access) is on, and only on Windows.

| Tool | Arguments | Does |
|---|---|---|
| `window_list` | `{}` | The visible windows (`WindowCatalog`): `id` (the hwnd), `title`, `process`, `width`, `height` (client pixels). |
| `window_show` | `{ window }` | Shows the window to the user live (below). |
| `window_capture` | `{ window }` | Captures the window at most 1280 pixels wide as JPEG under `screenshots` in the host's data folder and answers the path and size; the agent reads the file to see it, as with `browser_screenshot`. It captures as streams do (Windows.Graphics.Capture, else `PrintWindow`), waiting up to 1 s for the first frame. |
| `window_click` | `{ window, x, y, button?: left\|right\|middle, double? }` | Moves the pointer to the point and clicks. |
| `window_type` | `{ window, text }` | Types the text as Unicode key input where the window's focus is; a line break presses Enter. |
| `window_key` | `{ window, key, modifiers? }` | Presses a key (Enter, Tab, Escape, Backspace, Delete, Insert, Home, End, PageUp, PageDown, arrows, Space, F1–F24, A–Z, 0–9; `Ctrl+S` form allowed) with `Ctrl`, `Shift` or `Alt` held, releasing them after. |
| `window_scroll` | `{ window, x, y, delta }` | Turns the wheel over the point, `delta` notches (positive up). |

`window` is the hwnd, or a case-insensitive part of a title or process name that names one window (a whole title
wins over partial matches); several or no matches are refused with the candidates' ids. Coordinates are pixels of
the window's last capture, the image `window_capture` or the last input tool returned; before any capture, the size a
capture would have now. Input goes through `InputInjector`, which brings the window to the foreground; a window that
closed, runs elevated while Loom does not, or could not be brought to the front is refused with an error. Each input
tool waits 150 ms and answers a fresh capture.

Every tool but `window_list` shows the window: in the daemon by `window.show` to the Loom that shows the agent's chat
(Stream frames), in a `local`-role Loom by opening the stream panel in process (`LocalStreamSource`) beside the calling
panel. Each action also goes to the window's viewers as `cap.agent`.
