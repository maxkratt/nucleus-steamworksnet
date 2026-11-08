# Steam transport settings

## SteamworksNetTransport component (Unity, `Nucleus.Integrations.SteamworksNet`)

Values are copied into the engine `SteamSockets` when it is added. The `ServerSteamId` and `ServerAddress` script properties also write through to that engine transport once it exists, applied at the next client start; `BindAddress` does the same, applied at the next server start. Zero in a send setting keeps Steam's own default.

| Serialized field | Default | Engine target |
|---|---|---|
| `_addressing` | `SteamworksNetAddressing.SteamId` | `SteamSockets.Addressing` (`SteamId`, `IpAddress`, `FakeIp`) |
| `_gameServerEnabled` | `false` | `SteamSockets.GameServerEnabled`; on runs the server half on the game server interface (`GameServer.Init`), off on the logged-in user's; the client half always uses the user interface |
| `_serverSteamId` | `0` (join own server) | `SteamSockets.ServerSteamId`, a SteamID64 (`76561198012345678`); other SteamID formats are not read |
| `_serverAddress` | empty (join own server) | `SteamSockets.ServerAddress`: `"ip"`, `"ip:port"`, `"[ipv6]:port"` or `localhost`; without a port, `_port` is used; host names are not looked up |
| `_port` | `27015` | `SteamSockets.SetPort`, so both `VirtualPort` (`SteamId`) and `Configuration.Port` (the UDP port an `IpAddress` server listens on); server and clients must agree; `FakeIp` ignores it |
| `_bindAddress` | empty (every interface) | `Transport.SetBindAddress`, the local IP (or `localhost`) an `IpAddress` server listens on |
| `_connectingTimeoutSeconds` | `10` | `Configuration.ConnectingTimeoutSeconds`, enforced by Steam on both ends of a connecting link; `0` keeps Steam's 10 s |
| `_idleTimeoutSeconds` | `15` | `Configuration.ConnectedTimeoutSeconds`, enforced by Steam; `0` keeps Steam's 10 s |
| `_maximumConnections` | `250` (at least 1, no upper limit) | `Transport.SetMaximumConnections`; a full server refuses at accept, and a host's own client takes one place. The engine's `0` (any number) is reachable only from code |
| `_maximumClientPacketBytes` | `8388608` (8 MiB) | `SteamSockets.MaximumClientPacketBytes`; a client whose packet (a split reliable one included) would pass it is disconnected; `0` (`UnsetMaximumClientPacketBytes`) sets no limit. Never applied to what a client receives from its server |
| `_relayOnlyEnabled` | `true` | `SteamSockets.RelayOnlyEnabled`; `SteamId` links turn Steam's ICE off so they ride Valve's relay only and no peer learns another's IP. Off leaves direct connections to each player's Steam setting. Ignored by `IpAddress` and `FakeIp` |
| `_sendRateMaximumBytesPerSecond` | `4194304` | `SteamSockets.SendRateMaximumBytesPerSecond` (Steam's default is 256 KiB/s). The floor, `SteamSockets.SendRateMinimumBytesPerSecond`, is code only and stays at Steam's 256 KiB/s unless set: raising it made no difference to a 3.2 MiB late join |
| `_sendBufferSizeBytes` | `2097152` | `SteamSockets.SendBufferSizeBytes`; reliable messages past it wait in the transport, and only one message larger than it is refused and closes the link |
| `_ticketAuthenticationEnabled` | `false` | Installs a `SteamTicketAuthenticator`; server and clients both need it on. A server with it on drops a client with it off after the authentication timeout (30 s) |
| `_shareSteamIdsEnabled` | `false` | `SteamSockets.ShareSteamIdsEnabled`; the server tells every client each other client's SteamID. Only the server's value counts |

## SteamSockets (engine)

Engine-only members: `ConnectingClientFilter` (`bool (ulong steamId)`, `null` admits anyone while there is room), `ServerFakeAddress` (read only, set once Steam grants a `FakeIp` server its address), `TryGetLinkStatus`, `ServerReceiveMicroseconds`, `RefusedReliableSendCount`, `UnsetSteamId` (`0`), `UnsetSendSetting` (`0`). `DatagramOverheadBytes` is `0`: the channel rides Steam's own reliable flag. `Configuration.MaximumTransmissionUnit` stays at the engine's 1200, which is what one Steam packet carries after Steam's framing, so the component offers no setting for it: above 1200 Steam splits an unreliable packet, and below it only wastes space.

## Launch arguments

| Flag | Constant | Sets |
|---|---|---|
| `port` | `Transport.PortArgument` | `Configuration.Port` and `VirtualPort` |
| `address` | `SteamSockets.AddressArgument` | `ServerAddress` |
| `steamid` | `SteamSockets.SteamIdArgument` | `ServerSteamId` |

Nothing calls `ApplyCommandLineArguments()` for you; see **nucleus-transports-and-connections**. Steam's own `+connect_lobby <id>` names a lobby, not a host, so the game reads it (as the Lobbies sample's `LobbiesPanel` does).
