---
name: nucleus-steamworksnet
description: Covers the Nucleus Steam transport built on Steamworks.NET (Steam Networking Sockets) for games on Steam. Explains SteamSockets and the Unity SteamworksNetTransport component, addressing (SteamId through Valve's relay, IpAddress dialled directly, FakeIp for game servers), GameServerEnabled (the user's or the game server interface), ServerSteamId and ServerAddress, who initializes and pumps Steam, finding the host through a Steam lobby (CreateLobby, LobbyEnter_t, GetLobbyOwner, GameLobbyJoinRequested_t, +connect_lobby) and the Lobbies sample, SteamID identity, SteamTicketAuthenticator, ConnectingClientFilter, kick linger, SetNetworkConditions on Steam, TryGetLinkStatus and SteamLinkStatus, send rate and send buffer settings, and testing on App ID 480. Load it when a project uses Steam for multiplayer, when code touches any of these names, when a Steam client cannot connect, or when someone asks how to join a friend, invite through Steam or run a Steam dedicated server with Nucleus.
---

# Nucleus over Steam

The general connection model (transports, Connection lifetime, events, authentication, kicks) belongs to **nucleus-transports-and-connections**. This skill covers only what is specific to Steam.

## Setup

- Install Steamworks.NET (`com.rlabrecque.steamworks.net`): the `SteamworksNetTransport` inspector has an Install Steamworks.NET button (it needs Git on the path), or add the package's git URL in the Package Manager. Until it is installed, and on any build target where Steamworks.NET compiles itself out (anything but Windows, macOS, Linux and Android, or with `DISABLESTEAMWORKS`), everything but the component and its settings is compiled out, and a start fails with a logged error. The inspector lists each check as OK or Blocked, with the fix for a blocked one: the package (and Git to install it), `DISABLESTEAMWORKS`, or the build target.
- Put `SteamworksNetTransport` on the `UnityCoreManager` GameObject. `UnityTransportManager` adds it in place of the default `SynapseTransport`.
- The transport never initializes, pumps or shuts down Steam. The game's own Steam owner does that: `SteamAPI.Init()` before the first start, `SteamAPI.RunCallbacks()` every frame (Steam's connection changes arrive through it), and `SteamAPI.Shutdown()` only after `UnityTransportManager.ShutdownAll()`, so closes reach the peer. Keep Steam up across scene reloads; shutting it down right after a leave makes the peer wait out its idle timeout.
- For testing, App ID 480 (Spacewar) with `steam_appid.txt` in the project folder (and beside the executable in a build).

Confirm any API against the source in the package (`Packages/com.maxkratt.nucleus.steamworksnet/Runtime/`, or `Library/PackageCache/` when installed by git URL) before using it.

## Pick a shape

| Shape | Addressing | Game Server (`GameServerEnabled`) | The client sets |
|---|---|---|---|
| Player hosts, friends join | `SteamId` (default) | off | `ServerSteamId` = the host's SteamID64 |
| Dedicated server, IP hidden behind Valve's relay | `FakeIp` | on | `ServerAddress` = the server's `ServerFakeAddress` |
| Dedicated server on a public port, or two processes on one Steam account | `IpAddress` | on (or off for local tests) | `ServerAddress` = `"ip:port"` |

- A host calls `StartHostAsync()`; its own client pairs in process whatever `ServerSteamId` holds, and a target naming another peer is ignored with a warning (stop the server first to join someone else). A pure client sets `ServerSteamId` or `ServerAddress` on the component (both write through and apply at the next start), then calls `StartClientAsync()`.
- A server with `GameServerEnabled` on needs `GameServer.Init(...)`, `SteamGameServer.LogOnAnonymous()` and `GameServer.RunCallbacks()` every frame instead of the user's Steam; no Steam client runs on that machine. A client always uses the user's interface.
- `SteamId` between two machines needs two Steam accounts. One account across two processes needs `IpAddress`.

## To find the host through a lobby

Nucleus ships no lobby code; the lobby only tells a joiner which SteamID to dial.

1. The host calls `StartHostAsync()`, then `SteamMatchmaking.CreateLobby(...)`, and in `LobbyCreated_t` calls `SteamMatchmaking.SetLobbyGameServer(lobby, 0, 0, ownSteamId)`, so nobody dials a server that is not listening yet.
2. A joiner enters the lobby: from an invite or the friends list while the game runs (`GameLobbyJoinRequested_t`, then `SteamMatchmaking.JoinLobby`), or from `+connect_lobby <id>` when Steam launched the game to accept an invite (read it from `Environment.GetCommandLineArgs()`; it is Steam's flag, not a Nucleus one).
3. In its `LobbyEnter_t` callback the joiner skips the rest when `GetLobbyOwner(lobby)` is its own SteamID (the creator gets the callback too), then reads `GetLobbyGameServer(lobby, ...)`. If none is set it waits for `LobbyGameCreated_t`. If the game server differs from the owner, the host has left. Otherwise it sets `ServerSteamId` to the game server and calls `StartClientAsync()`.
4. On leaving, call `SteamMatchmaking.LeaveLobby` and `ShutdownAll()`.

The Lobbies sample (`LobbiesPanel`, `Scenes/SteamLobbies.unity`) shows the whole flow, including the invite overlay and joining by typed lobby ID. The Quick Match sample (`QuickMatchPanel`, `QuickMatchGame`) shows matchmaking through invisible lobbies, with a session open only to the lobby's members.

## Identity and admission

- On every addressing, `Connection.RemoteAddress` on the server is the client's Steam-authenticated SteamID64, so the default authenticator already sets `Identity` to it. Do not parse it: call `SteamSockets.TryGetSteamId(connection or connectionId, out ulong steamId)`, or `TryGetConnection(steamId, out connection)` for the reverse. Get the `SteamSockets` from `connection.Transport`.
- A server knows every client's SteamID and a client knows its own. For a client to know the others (names, avatars, friend checks), tick Share Steam IDs on the server (`SteamSockets.ShareSteamIdsEnabled`, off by default, since a SteamID links to a public profile). The IDs arrive after the client joins, in any order against the engine's other events, so read `TryGetSteamId` where needed and subscribe to `SteamIdChanged` (`uint connectionId, ulong steamId`, `UnsetSteamId` when the client left). Never let a client report its own SteamID; it could claim someone else's.
- To also have Steam confirm the player owns the game and is not banned, tick Ticket Authentication (under Shared) on server and clients (`SteamTicketAuthenticator`). A ticket is bound to the link's SteamID, so a borrowed or forged one is refused. If only the server has it on, a client sends no ticket and is dropped after `ServerManager.AuthenticationTimeoutSeconds` (30 s by default) with a generic authentication line; if only the client has it on, it joins unchecked.
- To refuse by SteamID before the engine sees the client (ban list, lobby members only), set `SteamSockets.ConnectingClientFilter`, a `bool (ulong steamId)` called on the network loop thread.
- To refuse by SteamID and tell the client why, with Ticket Authentication on, set `SteamworksNetTransport.ClientAdmissionFilter`, a `bool (Connection connection, ulong steamId)` that runs before the ticket is waited for. Refuse with `ServerManager.DenyClient(connection, notice)` and return `false`; the notice's `IsAuthenticationRequired` must be `false`, because the client is not authenticated yet. The Ban List sample shows this.
- Maximum Connections (at least 1 in the inspector, with no upper limit) refuses at accept once the server is full. A host's own client takes one place, as on Synapse.

## Behaviour worth knowing

- Kick reasons arrive: a kick closes with Steam's linger, which holds the link open until queued reliable data is acknowledged, and each side drops a link one receive pass after Steam closes it, so messages that arrived with the close are read first.
- A reliable send Steam refuses (send buffer full) closes the link and is logged naming `SendBufferSizeBytes`. Raise it for large bursts such as big late-join snapshots.
- Close reasons from Steam are logged once per drop, so a drop always says why.
- `SetNetworkConditions` supports every field and works mid-session, but it writes Steam's process-wide simulation, so it also affects any other Steam connection in the process. The host's own client is unaffected; judge lag from a separate client.
- `SteamSockets.TryGetLinkStatus(connection, out SteamLinkStatus)` reads Steam's ping, quality and pending bytes for a link. While a server runs, the component's inspector shows its SteamID or fake address under the status box, with a Copy button that copies only that, or its port when addressing by IP. Its settings are greyed out while playing, since they were read at start.

Every field, default and launch argument: [references/settings.md](references/settings.md).
