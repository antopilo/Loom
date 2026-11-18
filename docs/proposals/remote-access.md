# Remote access plan

Status: proposal. Nothing here is built yet.

Goal: other devices (phone, laptop, browser) see and control Loom's chats, with the same
network modes as Tern: LAN, Tailscale, a self-hosted relay, and (later) a public tunnel.
Later, a daemon owns the chats, and the network part does not change.

## What Tern does

From Tern's docs ([CLI](https://tern.build/docs/cli),
[changelog](https://tern.build/changelog) v0.6.0, v0.7.0, v0.7.5):

| Tern | Meaning |
|---|---|
| `tern daemon` | Owns every agent and shell. Windows, browser tabs and phones are clients. |
| `tern remote on --mode lan\|tailscale\|proxy\|relay\|funnel` | One switch, five ways to reach the daemon. |
| `tern remote pair [--control]` | QR code. Without `--control` the device can only look. |
| Settings → Revoke | Each paired device can be removed. |
| `tern relay --domain …` | A relay you host yourself. End-to-end encrypted; the relay sees only ciphertext. |
| `tern web serve` | A token URL for a browser client. |

The lesson: **one protocol and one secure channel for every transport.** A mode only
changes how bytes get from the device to the daemon.

## Target design

```
                 ┌──────────────── Session host ─────────────────┐
                 │  ChatSessionService: one ChatSession per chat │
                 │    OmpSession ─► omp.exe                      │
                 │    state, stream, dialogs, working, tools     │
                 │  RemoteGateway                                │
                 │    devices.json (paired keys, scopes)         │
                 │    secure channel (ECDH P-256, HKDF, AES-GCM) │
                 │    transports ▼                               │
                 └──┬──────────┬──────────────┬────────────────┬─┘
        in-process  │  WS on   │  WS on       │ outbound WS    │
        (phase 1-5) │  LAN     │  Tailscale IP│ to relay       │
                    ▼          ▼              ▼                ▼
               Loom panels   phone/laptop   phone/laptop     relay ◄── phone (any network)
```

- **Session host** is the Loom process in phases 1–5, and `Lokn.Host.exe` (the daemon)
  in phase 6. The gateway moves with it; clients see no difference.
- The desktop panels are clients of `ChatSessionService` from phase 1. In phase 6 they
  connect over a named pipe with the same protocol.

## The protocol (one for all clients)

Frames are JSON, one per WebSocket message (named pipe: length-prefixed). Every frame
after the handshake is encrypted by the secure channel.

| Direction | Frame | Purpose |
|---|---|---|
| C → H | `hello { v, deviceId }` | Start; host answers with its version and the device's scopes. |
| C → H | `list` | Chats: id, title, project, working, needs-attention. |
| C → H | `attach { chatId, since }` | Snapshot of messages after `since`, then live events. |
| C → H | `detach { chatId }` | Stop events for the chat. |
| C → H | `prompt { chatId, text, attachments }` | Needs `control`. |
| C → H | `abort`, `steer`, `answer_dialog`, `set_model` | Needs `control`. |
| H → C | `snapshot`, `message`, `delta`, `tool`, `dialog`, `state`, `working` | Chat events, each with a sequence number per chat. |
| H → C | `error { code, message }` | Refusals (scope, unknown chat, version). |

Rules:

- Sequence numbers per chat make reconnect cheap: `attach { since: n }` replays only the gap.
- The host never forwards raw Omp RPC. Omp frames change with Omp versions; the protocol
  is Lokn's contract. `ChatSession` maps Omp frames to these events once, for every client.
- Attachments from a device upload to the host first (`upload` frame, chunked), and become
  file paths on the host. Omp still reads files itself.

## Secure channel and pairing

All crypto is in the .NET BCL (`ECDiffieHellman` P-256, `HKDF`, `AesGcm`), so no NuGet
package is needed in a module. A browser has the same three in WebCrypto.

```
 Pair (once)                                   Connect (every time)
 ───────────                                   ────────────────────
 Host shows QR:                                Device → host: deviceId, ephemeral pubkey
   hostId, host pubkey, one-time secret,       Host   → device: ephemeral pubkey
   addresses (LAN, Tailscale, relay URL)       Both: ECDH(static+ephemeral) → HKDF → AES-GCM keys
 Device sends its pubkey + HMAC(secret)        Both prove the static key; else close
 Host stores { deviceId, pubkey, scopes }      Counter nonces; replay = close
```

- Scopes: `view` (read only) or `control` (prompt, abort, dialogs). Default `view`, like
  Tern's `pair` without `--control`.
- `devices.json` in `%LOCALAPPDATA%\Lokn\`. **Revoke** removes the entry and closes its
  connections at once.
- The one-time secret is valid for 5 minutes and one use.
- Transport TLS is not needed for secrecy, because the channel is already end-to-end.

## Network modes

| Mode | How the device reaches the host | Listener | Notes |
|---|---|---|---|
| `off` | No access | None | Default. |
| `lan` | `ws://<lan-ip>:<port>` | `HttpListener` on the LAN address | Needs a Windows URL ACL (`netsh http add urlacl`) once; setup asks for elevation. |
| `tailscale` | `ws://<100.x ip>:<port>` | Bound to the Tailscale address only | Check `tailscale ip` at start; fail with a clear message if it is absent. |
| `relay` | Device and host both connect out to the relay | None on the host | Works on any network, no port forward. |
| `funnel` | Public HTTPS via Tailscale Funnel to the local listener | Localhost | Later. Exposes a port to the internet; the channel still guards it. |

Never bind `0.0.0.0` / `+` by default.

### Relay

A small separate project, `Relay/` (ASP.NET Core is allowed there; it is not a module).

```
 host ──WS──► relay ◄──WS── device
   join(hostId, proof)       join(hostId, deviceId)
           relay pairs the two sockets and copies ciphertext
```

- The relay knows only `hostId` and `deviceId`. It cannot read or change frames (AES-GCM).
- It authenticates the host by a signature with the host key, so nobody else can take its
  `hostId`.
- Self-hosted, like `tern relay --domain`. A hosted Loom relay is a separate decision.

### Browser clients

WebCrypto works only in a secure context (HTTPS or localhost). A page from
`http://<lan-ip>` cannot use it. So:

- Serve the web client from the relay's HTTPS domain, or through `tailscale serve` (HTTPS).
- A native phone app has no such limit.

## Host tools when a device drives a chat

`OmpToolBridge` runs Lokn tools on the UI thread with the chat's panel as caller. A device
prompt can make the agent call `shell_*`, `browser_show`, the terminal and file tools.

- Phases 1–5: tools run as now; the caller is the chat's `ChatSession`, not a panel. Tools
  that need a panel open one (as when the user is at the desk).
- Phase 6: the daemon forwards `host_tool_call` to an attached Loom window. With no window,
  the daemon sends `set_host_tools` without Lokn's tools, and the agent keeps Omp's own.
- A `control` device is remote code execution on this PC. Pairing UI says so.

## Phases

Each phase ships alone and keeps every current behavior.

### Phase 1: Chats without panels (the large one)

- New `ChatSessionService` in `Modules/Conversations`, reached with
  `context.App.GetModule<ConversationsModule>()`.
- Move out of `ChatContent.*.cs` into `ChatSession`: connection (`_connection`,
  `_session`, `EnsureConnection`, `BuildConnectionAsync`), frame handling
  (`UpdateFrame`), send/abort/clear, dialogs, working state, tool bridge, title and
  task-check calls.
- `ChatSession` lifetime = module lifetime, not panel lifetime. Closing a panel detaches;
  an idle session stops after a timeout (setting).
- `ChatContent` keeps view state only (scroll, composer draft, open cards) and subscribes
  to `ChatSession` events, posted to the UI thread.
- `ConversationStore` stays the persistence owner.
- Check: `conversations` and `chat` suites; a new headless check that runs a turn with
  no panel open, with the synthetic JSONL child.

### Phase 2: Protocol and in-process loopback

- Define the frames above in `Modules/Remote/Protocol`, with a version.
- `ChatSession` events → protocol events in one mapper.
- A headless test client attaches over an in-memory pipe and runs: list, attach, prompt,
  abort, dialog answer, reconnect with `since`.

### Phase 3: `Remote` module, LAN and Tailscale

- `Modules/Remote/Lokn.Modules.Remote.csproj`, depends on `conversations`.
- `HttpListener` + `System.Net.WebSockets`; secure channel; `devices.json`.
- Settings: mode, port. Panel **Remote access**: mode, status, QR pairing, device list with
  scopes and **Revoke**. Commands in the Search palette.
- Status bar dot while a device is connected.
- Check: headless suite `remote`: pairing, wrong key, revoked device, replay, scope refusal.

### Phase 4: Client

- Web client first (one codebase for phone and laptop): chat list, transcript with
  Markdown, tool and diff cards (read only), prompt, abort, dialogs.
- Served by the relay or `tailscale serve`, never by plain LAN HTTP (WebCrypto).
- Native phone app later, if the web client is not enough.

### Phase 5: Relay

- `Relay/` project, deploy notes, `relay` mode in the Remote module.
- Check: host and device through a local relay; relay log has no plaintext.

### Phase 6: Daemon

- `Lokn.Host.exe`: owns `ChatSessionService`, `OmpService` sessions and `RemoteGateway`.
  Login item, single instance.
- Loom connects over a named pipe (`\\.\pipe\lokn-host-<user>`) with the same protocol and
  a local device identity.
- Host-tool forwarding as above. Handover on update (stop accepting, finish turns or keep
  Omp children and re-adopt), as Tern v0.7.5 does.
- This changes the architecture: `docs/architecture.md` gets a new layer.

### Phase 7: Funnel and push

- `funnel` mode through Tailscale Funnel.
- Push notifications for "agent needs you" need APNs/FCM and a native app: open question.

## Open questions

- Phone client: web only, or a native app (MAUI / Kotlin / Swift)?
- Relay: only self-hosted, or a hosted one too?
- Idle timeout for a chat with no client attached.
- Can a `view` device see tool output that holds secrets (env, files)? Proposal: yes, it
  is the same transcript the desk sees; pairing UI says so.
- Phase 6 order: before or after the client? The protocol makes either order work.
