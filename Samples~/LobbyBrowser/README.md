# Lobby Browser

A public lobby list with distance filtering and an estimated ping per lobby: hosts open public lobbies, and players search, pick one by its ping and join.

## What it shows

- Hosting a public lobby that publishes a name, a sample tag and the host's Steam ping location as lobby data.
- Searching with `RequestLobbyList`, filtered to this sample's tag and to a distance (Close, Default, Far, Worldwide), sorted by ping.
- Estimating each lobby's ping without contacting its host: the host's ping location string (`GetLocalPingLocation`, `ConvertPingLocationToString`) and this peer's own are combined by `EstimatePingTimeFromLocalHost`. A host whose relay measurement is not ready yet publishes its location when `SteamRelayNetworkStatus_t` reports the relay network available.
- Joining a listed lobby, connecting to the game server its host named, and leaving it once the client stops.

## Requirements

- Nucleus and Steamworks.NET.
- The Shared sample (it holds `SteamBootstrap`, which starts relay network access), imported beside this one.
- Steam running and logged in, and `steam_appid.txt` (480 for testing) in the project folder, or beside the executable in a build.

## Testing with two accounts

1. Open `Scenes/SteamLobbyBrowser.unity`.
2. On the first machine, set a lobby name and press **Host public lobby**.
3. On the second machine, logged into another Steam account, pick a distance and press **Search**; the lobby is listed with its member count and estimated ping. Press **Join**.
4. **Leave** on the host stops its server; the player's client stops and it leaves the lobby too.

The estimate runs through Steam's relays, so it is not zero even for a lobby on the same machine. On App ID 480 every developer's public lobbies share one list, which is why searches filter to this sample's tag.

## What to look at

- `LobbyBrowserPanel.Search`: the filters are added again before every `RequestLobbyList`, since Steam clears them after each request.
- `LobbyBrowserPanel.OnLobbyMatchListReceived`: the ping estimate per lobby.
- `LobbyBrowserPanel.TryPublishPingLocation`: the host's side.
