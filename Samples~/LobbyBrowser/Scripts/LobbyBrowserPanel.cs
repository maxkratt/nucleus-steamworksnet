// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using System.Collections.Generic;
using Nucleus.Connections;
using Nucleus.Integrations.Unity;
using Nucleus.Integrations.Unity.Managers.Transports;
using Nucleus.Managers.Transports;
using Nucleus.Transports;
using Steamworks;
using UnityEngine;

namespace Nucleus.Integrations.SteamworksNet.Samples.LobbyBrowser
{
    /// <summary>
    /// The Lobby Browser panel: a host opens a public lobby that publishes its name and its Steam ping location, and a player lists the
    /// public lobbies of this sample within a chosen distance, sees each one's estimated ping, and joins one.
    /// </summary>
    /// <remarks>
    /// The ping is estimated without sending anything to the host: Steam's relay network measures each peer's ping to its relays, the
    /// host publishes that as a ping location string in its lobby data, and <see cref="SteamNetworkingUtils.EstimatePingTimeFromLocalHost"/>
    /// combines it with this peer's own. Both sides need relay network access, which <see cref="SteamBootstrap"/> starts. A joined
    /// player connects to the lobby's game server through <see cref="SteamworksNetTransport.ServerSteamId"/>, and leaves the lobby once
    /// its client stops.
    /// </remarks>
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/maxkratt/nucleus-steamworksnet/blob/master/Samples~/LobbyBrowser/README.md")]
    public class LobbyBrowserPanel : MonoBehaviour
    {
        /// <summary>
        /// The bootstrap that owns Steam.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Steam Bootstrap.")]
        private SteamBootstrap _steamBootstrap;
        /// <summary>
        /// The transport manager the panel starts and stops.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Unity Transport Manager.")]
        private UnityTransportManager _unityTransportManager;
        /// <summary>
        /// The Steam transport, which a joining player points at the lobby's game server.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Steam Sockets Transport, set to SteamID addressing.")]
        private SteamworksNetTransport _steamSocketsTransport;
        /// <summary>
        /// The most players a hosted lobby holds, the host included.
        /// </summary>
        [SerializeField]
        [Tooltip("Most players a hosted lobby holds, the host included.")]
        private int _maximumLobbyMembers = 8;
        /// <summary>
        /// The most lobbies one search returns.
        /// </summary>
        [SerializeField]
        [Tooltip("Most lobbies one search returns.")]
        private int _maximumSearchResults = 20;
        /// <summary>
        /// The skin the panel draws with.
        /// </summary>
        /// <remarks>Left empty, the panel draws with Unity's default look.</remarks>
        [SerializeField]
        [Tooltip("The skin the panel draws with. Leave empty for Unity's default look.")]
        private GUISkin _guiSkin;

        /// <summary>
        /// The lobby this peer is in, or <see cref="CSteamID.Nil"/> when it is in none.
        /// </summary>
        private CSteamID _lobbySteamId = CSteamID.Nil;
        /// <summary>
        /// The index into <see cref="DistanceFilters"/> of the distance the next search covers.
        /// </summary>
        private int _distanceFilterIndex = 1;
        /// <summary>
        /// Whether this peer created the lobby it is in.
        /// </summary>
        private bool _isLobbyOwner;
        /// <summary>
        /// Whether this peer has hosted or begun joining since it last left, which swaps the browser for the lobby view.
        /// </summary>
        private bool _hasStarted;
        /// <summary>
        /// Whether this peer's client has connected since it last joined, so a dropped session reads differently from a failed connect.
        /// </summary>
        private bool _hasConnected;
        /// <summary>
        /// Whether this player has started its client since it last left, since entering a lobby and Steam's game server notice can both name the server.
        /// </summary>
        private bool _hasStartedClient;
        /// <summary>
        /// Whether the hosted lobby still waits for this peer's ping location, which <see cref="OnRelayNetworkStatusChanged"/> publishes
        /// once the relay network has measured it.
        /// </summary>
        private bool _isPingLocationPending;
        /// <summary>
        /// How many times this peer has hosted or left, so a host whose server finishes starting after a later Leave or Host knows it was
        /// overtaken.
        /// </summary>
        private uint _sessionCount;
        /// <summary>
        /// The lobbies this peer asked Steam to create that Steam has not answered yet.
        /// </summary>
        private uint _pendingLobbyRequestCount;
        /// <summary>
        /// How many of <see cref="_pendingLobbyRequestCount"/> were asked for before this peer last left, whose lobbies are left as soon as
        /// Steam creates them.
        /// </summary>
        private uint _staleLobbyRequestCount;
        /// <summary>
        /// The last thing that happened, shown on the panel.
        /// </summary>
        private string _statusText = "Search for lobbies, or host one.";
        /// <summary>
        /// The name a hosted lobby is listed under.
        /// </summary>
        private string _lobbyNameText = string.Empty;
        /// <summary>
        /// The lobbies the last search found.
        /// </summary>
        private readonly List<LobbyListing> _lobbyListings = new();
        /// <summary>
        /// The transport manager whose state changes tell a joined player that its client stopped.
        /// </summary>
        private TransportManager _transportManager;
        /// <summary>
        /// The call result that delivers a search's lobbies, held so it is not collected.
        /// </summary>
        private CallResult<LobbyMatchList_t> _lobbyMatchListCallResult;
        /// <summary>
        /// The callback that reports a lobby this peer asked for being created, held so it is not collected.
        /// </summary>
        private Callback<LobbyCreated_t> _lobbyCreatedCallback;
        /// <summary>
        /// The callback that reports this peer entering a lobby, held so it is not collected.
        /// </summary>
        private Callback<LobbyEnter_t> _lobbyEnteredCallback;
        /// <summary>
        /// The callback that reports the lobby's game server being set, held so it is not collected.
        /// </summary>
        private Callback<LobbyGameCreated_t> _lobbyGameCreatedCallback;
        /// <summary>
        /// The callback that reports the relay network's availability changing, held so it is not collected.
        /// </summary>
        private Callback<SteamRelayNetworkStatus_t> _relayNetworkStatusChangedCallback;

        /// <summary>
        /// The distances a search can cover, in the order the panel shows them.
        /// </summary>
        private static readonly ELobbyDistanceFilter[] DistanceFilters = { ELobbyDistanceFilter.k_ELobbyDistanceFilterClose, ELobbyDistanceFilter.k_ELobbyDistanceFilterDefault, ELobbyDistanceFilter.k_ELobbyDistanceFilterFar, ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide };
        /// <summary>
        /// The names of <see cref="DistanceFilters"/>, as the panel shows them.
        /// </summary>
        private static readonly string[] DistanceFilterNames = { "Close", "Default", "Far", "Worldwide" };

        /// <summary>
        /// The lobby data key that marks a lobby as this sample's, so a search on a shared App ID such as 480 skips every other game's.
        /// </summary>
        private const string SampleKey = "nucleus_sample";
        /// <summary>
        /// The value stored under <see cref="SampleKey"/>.
        /// </summary>
        private const string SampleValue = "lobby_browser";
        /// <summary>
        /// The lobby data key the host's lobby name is stored under.
        /// </summary>
        private const string NameKey = "name";
        /// <summary>
        /// The lobby data key the host's ping location string is stored under.
        /// </summary>
        private const string PingLocationKey = "ping_location";
        /// <summary>
        /// The longest ping location string Steam writes, its <c>k_cchMaxSteamNetworkingPingLocationString</c>.
        /// </summary>
        private const int PingLocationStringBytes = 1024;
        /// <summary>
        /// The ping shown for a lobby whose ping could not be estimated.
        /// </summary>
        private const int UnsetPingMilliseconds = -1;

        /// <summary>
        /// Registers the Steam callbacks and the transport subscription.
        /// </summary>
        private void Awake()
        {
            if (_steamBootstrap == null || _unityTransportManager == null || _steamSocketsTransport == null || NucleusUnity.BoundCoreManager is null)
            {
                Debug.LogError($"[{nameof(LobbyBrowserPanel)}] needs its [{nameof(SteamBootstrap)}], [{nameof(UnityTransportManager)}] and [{nameof(SteamworksNetTransport)}] set, and a bound CoreManager.");
                enabled = false;

                return;
            }

            // The bootstrap wakes first, so Steam is up here or never will be.
            if (!_steamBootstrap.IsSteamInitialized)
            {
                enabled = false;

                return;
            }

            _lobbyNameText = $"{SteamFriends.GetPersonaName()}'s lobby";

            _transportManager = NucleusUnity.BoundCoreManager.TransportManager;
            _transportManager.ConnectionLocalStateChanged += OnConnectionLocalStateChanged;
            _steamBootstrap.SteamShutdownStarted += OnSteamShutdownStarted;

            _lobbyMatchListCallResult = CallResult<LobbyMatchList_t>.Create(OnLobbyMatchListReceived);
            _lobbyCreatedCallback = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
            _lobbyEnteredCallback = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
            _lobbyGameCreatedCallback = Callback<LobbyGameCreated_t>.Create(OnLobbyGameCreated);
            _relayNetworkStatusChangedCallback = Callback<SteamRelayNetworkStatus_t>.Create(OnRelayNetworkStatusChanged);
        }

        /// <summary>
        /// Draws the panel.
        /// </summary>
        private void OnGUI()
        {
            SampleGUI.BeginPanel(_guiSkin, 464f, "Lobby Browser");

            if (!_steamBootstrap.IsSteamInitialized)
            {
                GUILayout.Label("Steam is not running, so this peer cannot host or join.");
                SampleGUI.EndPanel();

                return;
            }

            GUILayout.Label(_statusText);

            if (_hasStarted)
            {
                if (_lobbySteamId != CSteamID.Nil)
                    GUILayout.Label(_isLobbyOwner ? $"Hosting lobby {_lobbySteamId.m_SteamID}." : $"In lobby {_lobbySteamId.m_SteamID}.");

                if (GUILayout.Button("Leave"))
                    Leave("Left the lobby.");
            }
            else
            {
                DrawBrowser();
            }

            SampleGUI.EndPanel();
        }

        /// <summary>
        /// Draws the host controls, the distance filter, Search and the lobbies found.
        /// </summary>
        private void DrawBrowser()
        {
            GUILayout.Label("Lobby name");
            _lobbyNameText = GUILayout.TextField(_lobbyNameText);

            if (GUILayout.Button("Host public lobby"))
                Host();

            GUILayout.Space(8f);
            _distanceFilterIndex = GUILayout.Toolbar(_distanceFilterIndex, DistanceFilterNames);

            if (GUILayout.Button("Search"))
                Search();

            foreach (LobbyListing lobbyListing in _lobbyListings)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(lobbyListing.Name, GUILayout.Width(190f));
                GUILayout.Label($"{lobbyListing.MemberCount}/{lobbyListing.MemberLimit}", GUILayout.Width(50f));
                GUILayout.Label(lobbyListing.PingMilliseconds == UnsetPingMilliseconds ? "? ms" : $"{lobbyListing.PingMilliseconds} ms", GUILayout.Width(60f));

                if (GUILayout.Button("Join"))
                    JoinLobby(lobbyListing.LobbySteamId);

                GUILayout.EndHorizontal();
            }
        }

        /// <summary>
        /// Starts this peer as a host, then creates the public lobby the browser lists it under.
        /// </summary>
        /// <remarks>The server is up before the lobby exists, so no player can dial a server that is not listening.</remarks>
        private async void Host()
        {
            uint sessionCount = ++_sessionCount;
            _hasStarted = true;
            _statusText = "Starting the server.";

            await _unityTransportManager.StartHostAsync();

            // Left, or hosted again, while the server was starting, so this session has ended.
            if (sessionCount != _sessionCount)
                return;

            _statusText = "Creating the lobby.";
            _pendingLobbyRequestCount++;
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, _maximumLobbyMembers);
        }

        /// <summary>
        /// Asks Steam for this sample's public lobbies within the chosen distance; <see cref="OnLobbyMatchListReceived"/> lists them.
        /// </summary>
        /// <remarks>Steam clears the filters after each request, so they are added again before every one.</remarks>
        private void Search()
        {
            SteamMatchmaking.AddRequestLobbyListStringFilter(SampleKey, SampleValue, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(DistanceFilters[_distanceFilterIndex]);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(_maximumSearchResults);
            _lobbyMatchListCallResult.Set(SteamMatchmaking.RequestLobbyList());

            _statusText = "Searching.";
        }

        /// <summary>
        /// Asks Steam to enter a lobby; the client starts once the lobby names its game server.
        /// </summary>
        /// <param name="lobbySteamId">The lobby to enter.</param>
        private void JoinLobby(CSteamID lobbySteamId)
        {
            _hasStarted = true;
            _statusText = $"Entering lobby [{lobbySteamId.m_SteamID}].";

            SteamMatchmaking.JoinLobby(lobbySteamId);
        }

        /// <summary>
        /// Starts this peer as a client of the lobby's game server, unless it already started one.
        /// </summary>
        /// <param name="serverSteamId">The game server's SteamID.</param>
        private async void StartClient(ulong serverSteamId)
        {
            if (_hasStartedClient)
                return;

            _hasStartedClient = true;
            _steamSocketsTransport.ServerSteamId = serverSteamId;
            _statusText = $"Connecting to the host [{serverSteamId}].";

            await _unityTransportManager.StartClientAsync();
        }

        /// <summary>
        /// Leaves the lobby and stops every role this peer runs.
        /// </summary>
        /// <param name="statusText">What the panel shows afterwards.</param>
        private void Leave(string statusText)
        {
            if (_lobbySteamId != CSteamID.Nil)
                SteamMatchmaking.LeaveLobby(_lobbySteamId);

            // A lobby Steam is still creating has no ID to leave yet, so it is left when OnLobbyCreated reports it.
            _staleLobbyRequestCount = _pendingLobbyRequestCount;
            _sessionCount++;

            // Cleared before the shutdown, whose client stop would otherwise call this again.
            _hasStarted = false;
            _hasConnected = false;
            _hasStartedClient = false;
            _isLobbyOwner = false;
            _isPingLocationPending = false;
            _lobbySteamId = CSteamID.Nil;

            _unityTransportManager.ShutdownAll();

            _statusText = statusText;
        }

        /// <summary>
        /// Publishes this peer's ping location in its lobby's data, or leaves it pending while the relay network has not measured it.
        /// </summary>
        private void TryPublishPingLocation()
        {
            // A negative age means the relay network has not measured this peer's location yet.
            if (SteamNetworkingUtils.GetLocalPingLocation(out SteamNetworkPingLocation_t pingLocation) < 0f)
            {
                _isPingLocationPending = true;

                return;
            }

            SteamNetworkingUtils.ConvertPingLocationToString(ref pingLocation, out string pingLocationText, PingLocationStringBytes);
            SteamMatchmaking.SetLobbyData(_lobbySteamId, PingLocationKey, pingLocationText);

            _isPingLocationPending = false;
        }

        /// <summary>
        /// Lists the lobbies a search found, estimating each one's ping from the location its host published.
        /// </summary>
        /// <param name="lobbyMatchList">Steam's result.</param>
        /// <param name="isIoFailure">Whether the request failed before Steam answered.</param>
        private void OnLobbyMatchListReceived(LobbyMatchList_t lobbyMatchList, bool isIoFailure)
        {
            _lobbyListings.Clear();

            if (isIoFailure)
            {
                _statusText = "The search failed; try again.";

                return;
            }

            for (int i = 0; i < (int)lobbyMatchList.m_nLobbiesMatching; i++)
            {
                CSteamID lobbySteamId = SteamMatchmaking.GetLobbyByIndex(i);
                int pingMilliseconds = UnsetPingMilliseconds;

                if (SteamNetworkingUtils.ParsePingLocationString(SteamMatchmaking.GetLobbyData(lobbySteamId, PingLocationKey), out SteamNetworkPingLocation_t pingLocation))
                {
                    int estimatedPingMilliseconds = SteamNetworkingUtils.EstimatePingTimeFromLocalHost(ref pingLocation);

                    if (estimatedPingMilliseconds >= 0)
                        pingMilliseconds = estimatedPingMilliseconds;
                }

                _lobbyListings.Add(new(lobbySteamId, SteamMatchmaking.GetLobbyData(lobbySteamId, NameKey), SteamMatchmaking.GetNumLobbyMembers(lobbySteamId), SteamMatchmaking.GetLobbyMemberLimit(lobbySteamId), pingMilliseconds));
            }

            _lobbyListings.Sort(LobbyListing.CompareByPing);
            _statusText = _lobbyListings.Count == 0 ? "No lobbies found at that distance." : $"Lobbies found: {_lobbyListings.Count}.";
        }

        /// <summary>
        /// Records the lobby this peer created, publishes its listing data and names this peer its game server, or reports why Steam refused it.
        /// </summary>
        /// <param name="lobbyCreated">Steam's notice.</param>
        /// <remarks>
        /// Steam answers each request once, so a lobby asked for before this peer last left is left here rather than taken over.
        /// </remarks>
        private void OnLobbyCreated(LobbyCreated_t lobbyCreated)
        {
            _pendingLobbyRequestCount--;

            if (_staleLobbyRequestCount > 0)
            {
                _staleLobbyRequestCount--;

                if (lobbyCreated.m_eResult is EResult.k_EResultOK)
                    SteamMatchmaking.LeaveLobby(new(lobbyCreated.m_ulSteamIDLobby));

                return;
            }

            if (lobbyCreated.m_eResult is not EResult.k_EResultOK)
            {
                _statusText = $"Steam refused the lobby: [{lobbyCreated.m_eResult}]. The server is still up.";
                Debug.LogError($"[{nameof(LobbyBrowserPanel)}] could not create a lobby: [{lobbyCreated.m_eResult}].");

                return;
            }

            _isLobbyOwner = true;
            _lobbySteamId = new(lobbyCreated.m_ulSteamIDLobby);

            SteamMatchmaking.SetLobbyData(_lobbySteamId, SampleKey, SampleValue);
            SteamMatchmaking.SetLobbyData(_lobbySteamId, NameKey, _lobbyNameText);
            TryPublishPingLocation();

            // The server is already listening, so players may dial it from here on.
            SteamMatchmaking.SetLobbyGameServer(_lobbySteamId, unGameServerIP: 0, unGameServerPort: 0, SteamUser.GetSteamID());

            _statusText = "Hosting. Players can find this lobby with Search.";
        }

        /// <summary>
        /// Records the lobby this peer entered and starts the client toward its game server, unless this peer owns the lobby.
        /// </summary>
        /// <param name="lobbyEnter">Steam's notice.</param>
        /// <remarks>Steam raises this for the creator of a lobby too, which is already serving.</remarks>
        private void OnLobbyEntered(LobbyEnter_t lobbyEnter)
        {
            if (lobbyEnter.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                _hasStarted = false;
                _statusText = $"Could not enter the lobby: [{(EChatRoomEnterResponse)lobbyEnter.m_EChatRoomEnterResponse}].";

                return;
            }

            CSteamID enteredLobbySteamId = new(lobbyEnter.m_ulSteamIDLobby);
            CSteamID ownerSteamId = SteamMatchmaking.GetLobbyOwner(enteredLobbySteamId);

            // OnLobbyCreated records a lobby this peer created, so one asked for before this peer last left is not taken over here.
            if (ownerSteamId == SteamUser.GetSteamID())
                return;

            _lobbySteamId = enteredLobbySteamId;

            // A game server set before this peer entered raised no notice for it, so it is read here; otherwise OnLobbyGameCreated follows.
            if (!SteamMatchmaking.GetLobbyGameServer(_lobbySteamId, out uint _, out ushort _, out CSteamID serverSteamId))
            {
                _statusText = "Waiting for the host's server.";

                return;
            }

            // The host served this lobby, so a different owner means Steam handed the lobby on after the host left.
            if (serverSteamId != ownerSteamId)
            {
                Leave("The host of that lobby has left.");

                return;
            }

            StartClient(serverSteamId.m_SteamID);
        }

        /// <summary>
        /// Starts this player's client when the lobby it is waiting in names its game server.
        /// </summary>
        /// <param name="lobbyGameCreated">Steam's notice.</param>
        private void OnLobbyGameCreated(LobbyGameCreated_t lobbyGameCreated)
        {
            if (!_isLobbyOwner && lobbyGameCreated.m_ulSteamIDLobby == _lobbySteamId.m_SteamID)
                StartClient(lobbyGameCreated.m_ulSteamIDGameServer);
        }

        /// <summary>
        /// Publishes the ping location a hosted lobby is still waiting for, once the relay network is available.
        /// </summary>
        /// <param name="steamRelayNetworkStatus">Steam's notice.</param>
        private void OnRelayNetworkStatusChanged(SteamRelayNetworkStatus_t steamRelayNetworkStatus)
        {
            if (_isPingLocationPending && steamRelayNetworkStatus.m_eAvail is ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current)
                TryPublishPingLocation();
        }

        /// <summary>
        /// Shows the session's progress, and leaves the lobby once a joined player's client stops or fails to connect, since the server it dialled is gone.
        /// </summary>
        /// <param name="connectionStateChange">The state change that was raised.</param>
        private void OnConnectionLocalStateChanged(ConnectionStateChange connectionStateChange)
        {
            if (!_hasStarted || _isLobbyOwner || connectionStateChange.Invoker is not Invoker.Client)
                return;

            switch (connectionStateChange.Connection.LocalState)
            {
                case LocalConnectionState.Connected:
                    _hasConnected = true;
                    _statusText = "Connected to the host.";

                    break;
                case LocalConnectionState.Disconnected:
                case LocalConnectionState.Error:
                case LocalConnectionState.TimedOut:
                    Leave(_hasConnected ? "The host left, or the connection dropped." : "Could not connect to the host.");
                    break;
            }
        }

        /// <summary>
        /// Drops the subscriptions.
        /// </summary>
        private void OnDestroy()
        {
            if (_transportManager is null)
                return;

            _transportManager.ConnectionLocalStateChanged -= OnConnectionLocalStateChanged;
            _steamBootstrap.SteamShutdownStarted -= OnSteamShutdownStarted;
            _lobbyMatchListCallResult.Dispose();
            _lobbyCreatedCallback.Dispose();
            _lobbyEnteredCallback.Dispose();
            _lobbyGameCreatedCallback.Dispose();
            _relayNetworkStatusChangedCallback.Dispose();
        }

        /// <summary>
        /// Leaves the lobby as the application exits, before the bootstrap shuts Steam down.
        /// </summary>
        private void OnSteamShutdownStarted()
        {
            if (_lobbySteamId != CSteamID.Nil)
                SteamMatchmaking.LeaveLobby(_lobbySteamId);
        }

        /// <summary>
        /// One lobby a search found.
        /// </summary>
        private readonly struct LobbyListing
        {
            /// <summary>
            /// The lobby.
            /// </summary>
            public readonly CSteamID LobbySteamId;
            /// <summary>
            /// The name its host listed it under.
            /// </summary>
            public readonly string Name;
            /// <summary>
            /// How many members it holds.
            /// </summary>
            public readonly int MemberCount;
            /// <summary>
            /// The most members it holds.
            /// </summary>
            public readonly int MemberLimit;
            /// <summary>
            /// The estimated ping to its host, in milliseconds, or <see cref="UnsetPingMilliseconds"/> when it could not be estimated.
            /// </summary>
            public readonly int PingMilliseconds;

            /// <summary>
            /// Creates the listing.
            /// </summary>
            /// <param name="lobbySteamId">The lobby.</param>
            /// <param name="name">The name its host listed it under.</param>
            /// <param name="memberCount">How many members it holds.</param>
            /// <param name="memberLimit">The most members it holds.</param>
            /// <param name="pingMilliseconds">The estimated ping to its host, or <see cref="UnsetPingMilliseconds"/>.</param>
            public LobbyListing(CSteamID lobbySteamId, string name, int memberCount, int memberLimit, int pingMilliseconds)
            {
                LobbySteamId = lobbySteamId;
                Name = name;
                MemberCount = memberCount;
                MemberLimit = memberLimit;
                PingMilliseconds = pingMilliseconds;
            }

            /// <summary>
            /// Orders listings by estimated ping, lowest first, with unknown pings last.
            /// </summary>
            /// <param name="leftLobbyListing">The first listing.</param>
            /// <param name="rightLobbyListing">The second listing.</param>
            /// <returns>A negative number when the first sorts earlier, zero when they tie, and a positive number otherwise.</returns>
            public static int CompareByPing(LobbyListing leftLobbyListing, LobbyListing rightLobbyListing)
            {
                uint leftPingMilliseconds = (uint)leftLobbyListing.PingMilliseconds;
                uint rightPingMilliseconds = (uint)rightLobbyListing.PingMilliseconds;

                // The unset ping wraps to the largest unsigned value, so it sorts last.
                return leftPingMilliseconds.CompareTo(rightPingMilliseconds);
            }
        }
    }
}
#endif
