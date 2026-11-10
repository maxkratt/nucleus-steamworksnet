# Changelog

## Nucleus Steamworks.NET 0.48.0
- Minimum Send Rate in the component's Advanced section, default 256 KiB/s. Steam ID links send at this rate rather than climbing towards the maximum, so raise it to send large reliable data faster over Steam IDs.

## Nucleus Steamworks.NET 0.47.0
- `SteamworksNetTransport.ClientAdmissionFilter` refuses a SteamID before its ticket is checked, and can tell the client why.
- With Ticket Authentication on, a client now sends its ticket only once Steam reports it ready.
- The Lobbies and Lobby Browser samples name the host with `SetLobbyGameServer` and connect to the game server rather than the lobby owner, so a late joiner of a lobby whose host left is told so at once.
- New samples: Ban List and Quick Match.

## Nucleus Steamworks.NET 0.46.0
First public release. Earlier versions, 0.1.0 to 0.45.0, were developed privately and not published. Requires Nucleus 0.5.0 or later, Steamworks.NET and Unity 6.5 or later.
- Steam Networking Sockets transport for Nucleus, addressing by Steam ID, IP address or Fake IP, on the user or game server interface.
- Relay-only Steam ID links, on by default, so players do not learn each other's IP addresses.
- Reliable messages past Steam's 512 KiB limit are split and rejoined, and a send queue keeps a full Steam buffer from closing the link.
- Optional Steam auth ticket validation, and a per-client packet size limit on the server.
- SteamID lookups by connection, with optional sharing of every client's SteamID to the other clients.
- Link status, simulated network conditions and launch arguments.
- `SteamworksNetTransport` component, with an inspector that shows status and offers to install Steamworks.NET.
- Samples: Quick Start, Lobbies, Lobby Browser, Dedicated Server and Voice Chat, plus the Shared sample they depend on.
- An agent skill for the transport.
