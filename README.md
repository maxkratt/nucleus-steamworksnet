# Nucleus Steamworks.NET

A Steam Networking Sockets transport for the Nucleus netcode engine, built on [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET). Players host and join each other by SteamID through Valve's relay, dedicated servers run as Steam game servers behind a fake IP, and two processes can also dial each other by IP address.

## Requirements

- Unity 6.5 (6000.5) or later.
- Nucleus 0.5.0 or later, in `Assets/Nucleus`.
- Steamworks.NET (`com.rlabrecque.steamworks.net`), added by git URL.
- Steam running and logged in. App ID 480 (Spacewar) works for testing: put `steam_appid.txt` holding `480` in the project folder, and beside the executable in a build.

## Install

1. Import [Nucleus](https://nucleus-networking.com/).
2. In the Package Manager, **+ > Install package from git URL**, add this package:
   ```
   https://github.com/maxkratt/nucleus-steamworksnet.git
   ```
   Optionally append `#v0.x.y` to the URL to choose a specific version.
3. Add Steamworks.NET the same way, or press **Install Steamworks.NET** on the transport's inspector (it needs Git installed):
   ```
   https://github.com/rlabrecque/Steamworks.NET.git?path=/com.rlabrecque.steamworks.net
   ```

Until Steamworks.NET is installed, the package compiles to nothing but the component and its settings, and the inspector says what is missing.

## Quick start

1. Put `SteamworksNetTransport` on the `UnityCoreManager` GameObject. `UnityTransportManager` uses it in place of the default transport.
2. Initialize Steam in your game: `SteamAPI.Init()` before the first start, `SteamAPI.RunCallbacks()` every frame, and `SteamAPI.Shutdown()` only after `UnityTransportManager.ShutdownAll()`. The transport never does this itself. The Shared sample's `SteamBootstrap` does it for you.
3. The host calls `StartHostAsync()`.
4. A client sets `SteamworksNetTransport.ServerSteamId` to the host's SteamID64 and calls `StartClientAsync()`.

| Shape | Addressing | Game Server | The client sets |
|---|---|---|---|
| Player hosts, friends join | Steam ID (default) | off | `ServerSteamId` |
| Dedicated server behind Valve's relay | Fake IP | on | `ServerAddress` (the server's fake address) |
| Public port, or two processes on one account | IP Address | on, or off for local tests | `ServerAddress` (`ip:port`) |

A server with Game Server on runs on `GameServer.Init(...)`, `SteamGameServer.LogOnAnonymous()` (or a login token) and `GameServer.RunCallbacks()` every frame, with no Steam client on the machine. A client always uses the user's Steam. Steam ID addressing between two machines needs two Steam accounts; one account across two processes needs IP Address.

## SteamworksNetTransport

The Unity component. Its settings are copied into the engine transport, `SteamSockets`, when the transport manager adds it, so changes made while playing are discarded. `ServerSteamId`, `ServerAddress` and `BindAddress` are also script properties that write through at any time and apply at the next start. The status box at the top shows whether Steamworks.NET is installed and compiled in, with the fix for anything blocked, and while a server runs it shows its SteamID, fake address or port with a Copy button.

### Connection

| Setting | Default | What it does |
|---|---|---|
| Addressing | Steam ID | How the server is reached: Steam ID through Valve's relay, IP Address dialled directly, or Fake IP, an address Steam grants a game server so its real IP stays hidden. |
| Server's SteamID | `0` | Steam ID only. The SteamID64 a client connects to, such as `76561198012345678`. Zero, or any value while this peer runs a server, joins this peer's own server. |
| Server's Address, Server's Fake Address | empty | IP Address and Fake IP. The `ip`, `ip:port`, `[ipv6]:port` or `localhost` a client connects to; for a fake IP, the address the server logs. Host names are not looked up. Empty joins this peer's own server. |
| Virtual Port, Port | `27015` | Steam's virtual port by SteamID, or the UDP port the server listens on by IP address. Server and clients must agree. A fake IP ignores it. |
| Bind Address | empty | IP Address servers. The local IP, or `localhost`, to listen on. Empty listens on every interface. |
| Relay Only | on | Steam ID links. Carries them over Valve's relay only, so no player learns another's IP address. Off lets Steam connect players directly where each player's Steam settings allow it, which can lower ping. |
| Game Server | off | On for a dedicated server, which runs as a Steam game server. Off for a listen server, which runs as the logged-in Steam user. A client always runs as the user. |

### Session

| Setting | Default | What it does |
|---|---|---|
| Maximum Connections | `250` | The most clients the server accepts at once; a full server refuses at accept. A host's own client takes one place. At least 1 here; the engine's 0 (any number) is reachable from code. |
| Maximum Client Packet | 8 MiB | The largest packet, a split reliable one included, a client may send the server. A client past it is logged and disconnected. Zero sets no limit. It never limits what a client receives. |
| Ticket Authentication | off | Admits a client only once Steam confirms its ticket: it owns the game and is not banned. Server and clients both need it on. A server with it on drops a client with it off after the engine's authentication timeout (30 s); a client with it on joins a server with it off unchecked. |
| Share Steam IDs | off | Tells every client the SteamID of every other client, for names, avatars and friend checks. Only the server's value counts. See [Getting a player's SteamID](#getting-a-players-steamid). |

### Advanced

Collapsed by default, with a summary of its values.

| Setting | Default | What it does |
|---|---|---|
| Connecting Timeout | 10 s | How long a link may spend connecting before Steam gives up. Zero keeps Steam's 10 s. |
| Idle Timeout | 15 s | How long a peer waits without hearing from the other side before the link drops. Steam keeps links alive on its own thread, so a game hitch does not trip it. Zero keeps Steam's 10 s. |
| Maximum Send Rate | 4 MiB/s | The rate Steam never sends faster than on one connection. Zero keeps Steam's 256 KiB/s. |
| Send Buffer Size | 2 MiB | How much Steam may hold queued per connection. Reliable messages past it wait in the transport. Reliable messages go out in pieces of up to 512 KiB, so keep it at 512 KiB or more. Zero keeps Steam's 512 KiB. |

### From code

These live on `SteamSockets`, which `AddToAsync` returns and `TransportManager` holds:

- `ConnectingClientFilter`, a `bool (ulong steamId)` called on the network loop thread before the engine sees a client. Return `false` to refuse it, for a ban list or lobby members only.
- `ServerFakeAddress`, the address Steam grants a Fake IP server once it is ready.
- `TryGetLinkStatus(connection, out SteamLinkStatus)`, Steam's ping, connection quality and pending bytes for one link.
- `SendRateMinimumBytesPerSecond`, the send rate floor. It stays at Steam's 256 KiB/s; raising it made no difference in testing.
- `SetNetworkConditions` simulates latency, loss and jitter through Steam's own settings, mid-session too. They are process wide, so they also affect any other Steam connection in the process, and the host's own client is never delayed: judge lag from a separate client.

### Launch arguments

Read only when the game calls `ApplyCommandLineArguments()` on the transport.

| Flag | Sets |
|---|---|
| `-port <n>` | Port and virtual port |
| `-address <ip:port>` | `ServerAddress` |
| `-steamid <id>` | `ServerSteamId` |

Steam's own `+connect_lobby <id>` names a lobby, not a host; the Lobbies sample reads it.

### Identity

On every addressing, a client's `Connection.RemoteAddress` on the server is its SteamID64, authenticated by Steam, so the default authenticator already sets `Identity` to it. Ticket Authentication adds the ownership and ban check on top, and a ticket is bound to the link's SteamID, so a borrowed or forged one is refused.

### Getting a player's SteamID

`SteamSockets.TryGetSteamId(connection, out ulong steamId)` returns a client's Steam-authenticated SteamID, and also accepts a connection id such as `NetworkSystem.ControllerConnectionId`. `TryGetConnection(steamId, out connection)` is the reverse. Get the `SteamSockets` from `connection.Transport`.

A server knows every client, and a client knows itself. To let clients look up each other, for names, avatars or friend checks, tick **Share Steam IDs** on the server. It is off by default because a SteamID links to the player's public Steam profile. The IDs arrive shortly after a client joins, so subscribe to `SteamIdChanged` as well as reading them; its `steamId` is `SteamSockets.UnsetSteamId` when a client leaves.

### Behaviour worth knowing

- Every drop logs Steam's close reason once.
- A reliable send that Steam refuses closes the link and logs a line naming the send buffer. Raise Send Buffer Size for large bursts, such as big late-join snapshots.
- Shut Steam down only after `UnityTransportManager.ShutdownAll()`, so closes reach the other side; otherwise it waits out its idle timeout.

## Samples

Import them from the package's **Samples** tab in the Package Manager. Import **Shared** first: every other sample needs it. Each sample has a README with its scene and a two account test.

| Sample | What it shows |
|---|---|
| [Shared](Samples~/Shared/README.md) | `SteamBootstrap` (initializing, pumping and shutting down Steam, or a game server), player name tags and the sample skin. |
| [Quick Start](Samples~/QuickStart/README.md) | Host or join by SteamID and move a capsule. Needs the Input System package. |
| [Lobbies](Samples~/Lobbies/README.md) | Create, join and leave Steam lobbies, invites, members, friends and lobby data. |
| [Lobby Browser](Samples~/LobbyBrowser/README.md) | Search public lobbies by distance and estimated ping. |
| [Dedicated Server](Samples~/DedicatedServer/README.md) | A Steam game server with a LAN and internet server browser. |
| [Voice Chat](Samples~/VoiceChat/README.md) | Push to talk Steam voice relayed through the server. Needs the Input System package. |

The Quick Start and Voice Chat samples spawn networked prefabs: after importing them, open **Tools > Nucleus** and press **Rebuild** beside Rebuild Network Prefab Collection on its Build tab.

## Agent skill

`Skills~/nucleus-steamworksnet` is a skill for AI coding agents covering setup, addressing, lobbies, identity and every setting. Copy the folder into your project's `.claude/skills/` (or your agent's equivalent) beside the Nucleus skills.

## Tests

The PlayMode tests (`Nucleus.Integrations.SteamworksNet.Tests`) run in the Test Runner when the package is listed under `testables` in `Packages/manifest.json`. They skip unless Steam is running.

## Licence

MIT, see [LICENSE.md](LICENSE.md).
