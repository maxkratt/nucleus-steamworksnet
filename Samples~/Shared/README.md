# Shared

`SteamBootstrap`, which every other sample needs imported beside it; `PlayerNameTag`, which draws a player's Steam name over their object; `UI/SteamSampleSkin`, the GUISkin every sample panel draws with; and the ground and player materials in `Materials/`. The transport never initializes Steam itself; in a sample scene this component does.

## What it shows

- Initializing the user's Steam, or adopting the Steam an earlier scene started, before any other sample component wakes.
- Optionally relaunching a build through Steam when an App ID is set (`RestartAppIfNecessary`).
- Starting relay network access, so the first connection and ping estimates do not wait for it.
- Running a Steam game server instead, with game and query ports and an optional login token (anonymous logon without one), as the Dedicated Server sample does.
- Pumping callbacks, and on quit stopping the transports before shutting Steam down.
- Showing who controls an object: the server writes the controller's SteamID from the link Steam authenticated (so a client cannot claim another player's name), and every client turns it into a persona name through its own Steam, fetching it with `RequestUserInformation` when it is not known yet.

## Using it

Put one on an object in each scene that uses Steam, beside the transport. It stays with its scene; Steam itself outlives a scene load, so the next scene's bootstrap adopts it. `steam_appid.txt` (480 for testing) goes in the project folder, or beside the executable in a build.

There is nothing to test on its own: each other sample's two account test runs it.
