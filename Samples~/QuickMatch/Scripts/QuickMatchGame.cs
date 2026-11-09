// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using Nucleus.Connections;
using Nucleus.Integrations.Unity;
using Nucleus.Integrations.Unity.Managers.Transports;
using Nucleus.Managers.Client;
using Nucleus.Managers.Transports;
using Nucleus.Transports;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nucleus.Integrations.SteamworksNet.Samples.QuickMatch
{
    /// <summary>
    /// Starts the match in the game scene: the chosen host serves and names itself the lobby's game server, and every other member
    /// connects to that server, then leaves the lobby once it is in.
    /// </summary>
    /// <remarks>
    /// The lobby's <see cref="QuickMatchSession.HostKey"/> says who hosts, because the owner can change while members are loading. A
    /// lobby whose owner is no longer that host has lost its server, so a member that sees this returns to the menu.
    /// </remarks>
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/maxkratt/nucleus-steamworksnet/blob/main/Samples~/QuickMatch/README.md")]
    public class QuickMatchGame : MonoBehaviour
    {
        /// <summary>
        /// The bootstrap that owns Steam.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Steam Bootstrap.")]
        private SteamBootstrap _steamBootstrap;
        /// <summary>
        /// The transport manager the match starts and stops.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Unity Transport Manager.")]
        private UnityTransportManager _unityTransportManager;
        /// <summary>
        /// The Steam transport, which needs Ticket Authentication on for the member check to run.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Steam Sockets Transport, set to SteamID addressing with Ticket Authentication on.")]
        private SteamworksNetTransport _steamSocketsTransport;
        /// <summary>
        /// The skin the panel draws with.
        /// </summary>
        /// <remarks>Left empty, the panel draws with Unity's default look.</remarks>
        [SerializeField]
        [Tooltip("The skin the panel draws with. Leave empty for Unity's default look.")]
        private GUISkin _guiSkin;

        /// <summary>
        /// The lobby this match came from, or <see cref="CSteamID.Nil"/> once this peer has left it.
        /// </summary>
        private CSteamID _lobbySteamId = CSteamID.Nil;
        /// <summary>
        /// The SteamID chosen to host this match.
        /// </summary>
        private CSteamID _hostSteamId = CSteamID.Nil;
        /// <summary>
        /// Whether this peer hosts the match.
        /// </summary>
        private bool _isHost;
        /// <summary>
        /// Whether this host has named itself the lobby's game server.
        /// </summary>
        private bool _isGameServerNamed;
        /// <summary>
        /// Whether this member has started its client.
        /// </summary>
        private bool _hasStartedClient;
        /// <summary>
        /// Whether this peer is already on its way back to the menu, since leaving stops the client that also reports it.
        /// </summary>
        private bool _isReturning;
        /// <summary>
        /// The last thing that happened, shown on the panel.
        /// </summary>
        private string _statusText = "Starting the match.";
        /// <summary>
        /// The transport manager whose state changes tell a member that its client stopped.
        /// </summary>
        private TransportManager _transportManager;
        /// <summary>
        /// The client manager that reports this peer being admitted.
        /// </summary>
        private ClientManager _clientManager;
        /// <summary>
        /// The callback that reports the lobby's game server being set, held so it is not collected.
        /// </summary>
        private Callback<LobbyGameCreated_t> _lobbyGameCreatedCallback;
        /// <summary>
        /// The callback that reports a member entering or leaving the lobby, held so it is not collected.
        /// </summary>
        private Callback<LobbyChatUpdate_t> _lobbyChatUpdatedCallback;

        /// <summary>
        /// Sets the member check before the server can listen, then registers the subscriptions.
        /// </summary>
        private void Awake()
        {
            if (_steamBootstrap == null || _unityTransportManager == null || _steamSocketsTransport == null || NucleusUnity.BoundCoreManager is null || !_steamBootstrap.IsSteamInitialized)
            {
                Debug.LogError($"[{nameof(QuickMatchGame)}] needs its [{nameof(SteamBootstrap)}], [{nameof(UnityTransportManager)}] and [{nameof(SteamworksNetTransport)}] set, a bound CoreManager and Steam running.");
                enabled = false;

                return;
            }

            _lobbySteamId = QuickMatchSession.LobbySteamId;
            _steamSocketsTransport.ClientAdmissionFilter = OnClientAdmission;

            _transportManager = NucleusUnity.BoundCoreManager.TransportManager;
            _clientManager = NucleusUnity.BoundCoreManager.ClientManager;
            _transportManager.ConnectionLocalStateChanged += OnConnectionLocalStateChanged;
            _clientManager.LocalClientAuthenticated += OnLocalClientAuthenticated;
            _steamBootstrap.SteamShutdownStarted += OnSteamShutdownStarted;
            _lobbyGameCreatedCallback = Callback<LobbyGameCreated_t>.Create(OnLobbyGameCreated);
            _lobbyChatUpdatedCallback = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdated);
        }

        /// <summary>
        /// Hosts or joins, once every component in the scene has woken.
        /// </summary>
        private void Start()
        {
            if (_lobbySteamId == CSteamID.Nil)
            {
                ReturnToMenu("The game scene was opened without a match.");

                return;
            }

            if (!ulong.TryParse(SteamMatchmaking.GetLobbyData(_lobbySteamId, QuickMatchSession.HostKey), out ulong hostSteamId))
            {
                ReturnToMenu("The lobby names no host.");

                return;
            }

            _hostSteamId = new(hostSteamId);
            _isHost = _hostSteamId == SteamUser.GetSteamID();

            if (_isHost)
                StartHost();
            else
                TryStartClientFromLobby();
        }

        /// <summary>
        /// Draws the panel.
        /// </summary>
        private void OnGUI()
        {
            SampleGUI.BeginPanel(_guiSkin, 360f, "Quick Match");
            GUILayout.Label(_statusText);

            if (GUILayout.Button("Leave match"))
                ReturnToMenu("Left the match.");

            SampleGUI.EndPanel();
        }

        /// <summary>
        /// Starts the server, then names this peer the lobby's game server, so nobody dials a server that is not listening.
        /// </summary>
        private async void StartHost()
        {
            _statusText = "Starting the server.";

            await _unityTransportManager.StartHostAsync();

            if (_isReturning)
                return;

            SteamMatchmaking.SetLobbyGameServer(_lobbySteamId, unGameServerIP: 0, unGameServerPort: 0, _hostSteamId);
            _isGameServerNamed = true;
            _statusText = "Hosting the match.";

            LeaveLobbyIfAlone();
        }

        /// <summary>
        /// Reads the lobby's game server and owner, connects when the server is ready, and returns to the menu when the host has left.
        /// </summary>
        /// <remarks>A member that loads after the host named the server has already missed <see cref="OnLobbyGameCreated"/>, so this reads it.</remarks>
        private void TryStartClientFromLobby()
        {
            if (SteamMatchmaking.GetLobbyOwner(_lobbySteamId) != _hostSteamId)
            {
                ReturnToMenu("The host left before the match started.");

                return;
            }

            if (SteamMatchmaking.GetLobbyGameServer(_lobbySteamId, out uint _, out ushort _, out CSteamID serverSteamId))
                StartClient(serverSteamId.m_SteamID);
            else
                _statusText = "Waiting for the host's server.";
        }

        /// <summary>
        /// Starts this member's client toward the game server, unless it already started.
        /// </summary>
        /// <param name="serverSteamId">The game server's SteamID.</param>
        private async void StartClient(ulong serverSteamId)
        {
            if (_hasStartedClient)
                return;

            _hasStartedClient = true;
            _steamSocketsTransport.ServerSteamId = serverSteamId;
            _statusText = "Connecting to the host.";

            await _unityTransportManager.StartClientAsync();
        }

        /// <summary>
        /// Leaves the lobby, if this peer is still in it.
        /// </summary>
        private void LeaveLobby()
        {
            if (_lobbySteamId != CSteamID.Nil)
                SteamMatchmaking.LeaveLobby(_lobbySteamId);

            _lobbySteamId = CSteamID.Nil;
        }

        /// <summary>
        /// Ends the lobby once this host is its last member, which is the only way a Steam lobby closes.
        /// </summary>
        private void LeaveLobbyIfAlone()
        {
            if (_isGameServerNamed && _lobbySteamId != CSteamID.Nil && SteamMatchmaking.GetNumLobbyMembers(_lobbySteamId) == 1)
                LeaveLobby();
        }

        /// <summary>
        /// Leaves the lobby, stops every role and loads the menu.
        /// </summary>
        /// <param name="reason">Why, for the log.</param>
        private void ReturnToMenu(string reason)
        {
            if (_isReturning)
                return;

            _isReturning = true;
            Debug.Log($"[{nameof(QuickMatchGame)}] is returning to the menu: {reason}");

            LeaveLobby();
            _unityTransportManager.ShutdownAll();
            SceneManager.LoadScene(QuickMatchSession.MenuSceneName);
        }

        /// <summary>
        /// Admits only a member of the lobby.
        /// </summary>
        /// <param name="connection">The connecting client.</param>
        /// <param name="steamId">The SteamID its link was authenticated as.</param>
        /// <returns><see langword="true"/> when the client is in the lobby.</returns>
        /// <remarks>A member stays in the lobby until it is authenticated, so a real member is always found here.</remarks>
        private bool OnClientAdmission(Connection connection, ulong steamId)
        {
            if (_lobbySteamId == CSteamID.Nil)
                return false;

            int memberCount = SteamMatchmaking.GetNumLobbyMembers(_lobbySteamId);

            for (int i = 0; i < memberCount; i++)
            {
                if (SteamMatchmaking.GetLobbyMemberByIndex(_lobbySteamId, i).m_SteamID == steamId)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Connects a waiting member once the lobby names its game server.
        /// </summary>
        /// <param name="lobbyGameCreated">Steam's notice.</param>
        private void OnLobbyGameCreated(LobbyGameCreated_t lobbyGameCreated)
        {
            if (!_isHost && lobbyGameCreated.m_ulSteamIDLobby == _lobbySteamId.m_SteamID)
                StartClient(lobbyGameCreated.m_ulSteamIDGameServer);
        }

        /// <summary>
        /// Lets a host close the lobby when it is alone, and sends a member still waiting back to the menu if the host left.
        /// </summary>
        /// <param name="lobbyChatUpdate">Steam's notice.</param>
        private void OnLobbyChatUpdated(LobbyChatUpdate_t lobbyChatUpdate)
        {
            if (lobbyChatUpdate.m_ulSteamIDLobby != _lobbySteamId.m_SteamID)
                return;

            if (_isHost)
                LeaveLobbyIfAlone();
            else if (!_hasStartedClient && SteamMatchmaking.GetLobbyOwner(_lobbySteamId) != _hostSteamId)
                ReturnToMenu("The host left before the match started.");
        }

        /// <summary>
        /// Leaves the lobby once this member is in the match.
        /// </summary>
        /// <param name="localClientConnection">This peer's connection.</param>
        private void OnLocalClientAuthenticated(Connection localClientConnection)
        {
            if (_isHost)
                return;

            _statusText = "In the match.";
            LeaveLobby();
        }

        /// <summary>
        /// Returns a member to the menu once its client stops or fails to connect.
        /// </summary>
        /// <param name="connectionStateChange">The state change that was raised.</param>
        private void OnConnectionLocalStateChanged(ConnectionStateChange connectionStateChange)
        {
            if (_isHost || connectionStateChange.Invoker is not Invoker.Client)
                return;

            if (connectionStateChange.Connection.LocalState is LocalConnectionState.Disconnected or LocalConnectionState.Error or LocalConnectionState.TimedOut)
                ReturnToMenu("The connection to the host ended.");
        }

        /// <summary>
        /// Leaves the lobby as the application exits, before the bootstrap shuts Steam down.
        /// </summary>
        private void OnSteamShutdownStarted() => LeaveLobby();

        /// <summary>
        /// Drops the subscriptions.
        /// </summary>
        private void OnDestroy()
        {
            if (_transportManager is null)
                return;

            _transportManager.ConnectionLocalStateChanged -= OnConnectionLocalStateChanged;
            _clientManager.LocalClientAuthenticated -= OnLocalClientAuthenticated;
            _steamBootstrap.SteamShutdownStarted -= OnSteamShutdownStarted;
            _lobbyGameCreatedCallback.Dispose();
            _lobbyChatUpdatedCallback.Dispose();
        }
    }
}
#endif
