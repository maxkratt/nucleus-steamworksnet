// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using System;
using Nucleus.Integrations.Unity.Managers.Transports;
using Steamworks;
using UnityEngine;

namespace Nucleus.Integrations.SteamworksNet.Samples
{
    /// <summary>
    /// Owns Steam for a sample scene: it initializes the user's Steam, or a Steam game server, pumps its callbacks, and shuts it down
    /// after the transports as the application exits. <see cref="SteamworksNetTransport"/> never initializes Steam itself, so a game
    /// does this, with this component or its own code.
    /// </summary>
    /// <remarks>
    /// Its <c>Awake</c> runs before every ordinary component's, so Steam is up before any other sample component calls it and before
    /// a transport starts. It stays scene-scoped: Steam itself outlives a scene load, so the next scene's bootstrap adopts the running
    /// Steam rather than initializing it again, and leaving a scene never shuts Steam down. Steam needs <c>steam_appid.txt</c> in the
    /// project folder in the editor, and beside the executable in a build. A game server needs no Steam client running, so a headless
    /// build can serve; the transport's Game Server option must then be on too.
    /// </remarks>
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/maxkratt/nucleus-steamworksnet/blob/master/Samples~/Shared/README.md")]
    public class SteamBootstrap : MonoBehaviour
    {
        /// <summary>
        /// The application is exiting and Steam is about to shut down, so this is the last moment Steam calls can be made.
        /// </summary>
        public event SteamShutdownStartedHandler SteamShutdownStarted;
        /// <summary>
        /// Handler for the <see cref="SteamShutdownStarted"/> event.
        /// </summary>
        public delegate void SteamShutdownStartedHandler();
        /// <summary>
        /// Whether Steam is up for this scene, initialized here or adopted from an earlier scene.
        /// </summary>
        /// <remarks>For a game server this is true from initialization; its logon completes a moment later.</remarks>
        public bool IsSteamInitialized { get; private set; }
        /// <summary>
        /// The local user's SteamID, or the game server's once its logon completed, or <see cref="SteamSockets.UnsetSteamId"/> before.
        /// </summary>
        public ulong OwnSteamId { get; private set; }
        /// <summary>
        /// Whether this bootstrap initializes a Steam game server rather than the user's Steam.
        /// </summary>
        public bool GameServerEnabled => _gameServerEnabled;
        /// <summary>
        /// The App ID value that skips the relaunch check.
        /// </summary>
        public const uint UnsetAppId = 0U;

        /// <summary>
        /// The game's Steam App ID, checked with <see cref="SteamAPI.RestartAppIfNecessary"/> so a build started outside Steam is
        /// relaunched through it, or <see cref="UnsetAppId"/> to skip the check.
        /// </summary>
        /// <remarks>
        /// Steam answers <see langword="false"/> while <c>steam_appid.txt</c> sits beside the executable, so the check matters only in a
        /// shipped build, which leaves that file out. A game server never runs it.
        /// </remarks>
        [SerializeField]
        [Tooltip("Steam App ID; a build started outside Steam quits and is relaunched through Steam. 0 skips the check.")]
        private uint _appId = UnsetAppId;
        /// <summary>
        /// True to initialize a Steam game server instead of the user's Steam, as a dedicated server does. Such a peer can only serve.
        /// </summary>
        [SerializeField]
        [Tooltip("Initialize a Steam game server instead of the user's Steam, as a dedicated server does. Such a peer can only serve.")]
        private bool _gameServerEnabled;
        /// <summary>
        /// The UDP port the game server reports to Steam as its game port.
        /// </summary>
        [SerializeField]
        [Tooltip("Game port the game server reports to Steam.")]
        private ushort _gameServerPort = 27015;
        /// <summary>
        /// The UDP port Steam's server browser queries the game server on.
        /// </summary>
        [SerializeField]
        [Tooltip("Port Steam's server browser queries the game server on.")]
        private ushort _gameServerQueryPort = 27016;
        /// <summary>
        /// The game server login token the server logs on with, or empty to log on anonymously.
        /// </summary>
        /// <remarks>
        /// A token, made for the App ID on Steam's game server account page, gives the server a persistent SteamID; some games list
        /// only servers that logged on with one.
        /// </remarks>
        [SerializeField]
        [Tooltip("Game server login token from Steam's game server account page. Empty logs on anonymously.")]
        private string _gameServerLoginToken = string.Empty;

        /// <summary>
        /// The scene's transport manager, stopped before Steam shuts down, or <see langword="null"/> when the scene has none.
        /// </summary>
        private UnityTransportManager _unityTransportManager;
        /// <summary>
        /// The callback that reports the game server's logon succeeding, held so it is not collected.
        /// </summary>
        private Callback<SteamServersConnected_t> _gameServerConnectedCallback;
        /// <summary>
        /// The callback that reports the game server's logon failing, held so it is not collected.
        /// </summary>
        private Callback<SteamServerConnectFailure_t> _gameServerConnectFailureCallback;

        /// <summary>
        /// Initializes Steam or a game server, or adopts the one an earlier scene initialized, and starts relay discovery.
        /// </summary>
        private void Awake()
        {
            _unityTransportManager = FindAnyObjectByType<UnityTransportManager>();

            if (_gameServerEnabled ? TryInitializeGameServer() : TryInitializeClient())
                IsSteamInitialized = true;
            else
                enabled = false;
        }

        /// <summary>
        /// Initializes the user's Steam, unless an earlier scene already did.
        /// </summary>
        /// <returns><see langword="true"/> when Steam is up.</returns>
        private bool TryInitializeClient()
        {
            if (_appId is not UnsetAppId && SteamAPI.RestartAppIfNecessary(new AppId_t(_appId)))
            {
                Debug.Log($"[{nameof(SteamBootstrap)}] was started outside Steam, so it quits while Steam relaunches it.");
                Application.Quit();

                return false;
            }

            // Steam outlives a scene load, so only the first scene of the process initializes it.
            if (IsClientShutDown() && !SteamAPI.Init())
            {
                Debug.LogError($"[{nameof(SteamBootstrap)}] could not initialize Steam. Steam must be running and steam_appid.txt must be in the project folder, or beside the executable in a build.");

                return false;
            }

            OwnSteamId = SteamUser.GetSteamID().m_SteamID;

            // Started now so the first connect does not wait on relay discovery.
            SteamNetworkingUtils.InitRelayNetworkAccess();

            Debug.Log($"[{nameof(SteamBootstrap)}] Steam is up as [{SteamFriends.GetPersonaName()}] with SteamID [{OwnSteamId}].");

            return true;
        }

        /// <summary>
        /// Initializes a Steam game server and logs it on, unless an earlier scene already did.
        /// </summary>
        /// <returns><see langword="true"/> when the game server is up.</returns>
        /// <remarks>The logon completes a moment later, in <see cref="OnGameServerConnected"/>.</remarks>
        private bool TryInitializeGameServer()
        {
            _gameServerConnectedCallback = Callback<SteamServersConnected_t>.CreateGameServer(OnGameServerConnected);
            _gameServerConnectFailureCallback = Callback<SteamServerConnectFailure_t>.CreateGameServer(OnGameServerConnectFailure);

            if (!IsGameServerShutDown())
            {
                if (SteamGameServer.BLoggedOn())
                    OwnSteamId = SteamGameServer.GetSteamID().m_SteamID;

                return true;
            }

            if (!GameServer.Init(0, _gameServerPort, _gameServerQueryPort, EServerMode.eServerModeAuthenticationAndSecure, Application.version))
            {
                Debug.LogError($"[{nameof(SteamBootstrap)}] could not initialize a Steam game server. steam_appid.txt must be in the project folder, or beside the executable in a build.");

                return false;
            }

            SteamGameServer.SetDedicatedServer(true);

            if (string.IsNullOrEmpty(_gameServerLoginToken))
                SteamGameServer.LogOnAnonymous();
            else
                SteamGameServer.LogOn(_gameServerLoginToken);

            SteamGameServerNetworkingUtils.InitRelayNetworkAccess();

            Debug.Log($"[{nameof(SteamBootstrap)}] Steam game server initialized on game port [{_gameServerPort}] and query port [{_gameServerQueryPort}], logging on {(string.IsNullOrEmpty(_gameServerLoginToken) ? "anonymously" : "with its login token")}.");

            return true;
        }

        /// <summary>
        /// Pumps Steam's callbacks, which is where lobby notices and the transport's connection status changes arrive from.
        /// </summary>
        private void Update()
        {
            // Something else may have shut Steam down, and Steamworks.NET throws on every pump after that.
            if (_gameServerEnabled)
            {
                if (!IsGameServerShutDown())
                    GameServer.RunCallbacks();
            }
            else if (!IsClientShutDown())
            {
                SteamAPI.RunCallbacks();
            }
        }

        /// <summary>
        /// Records the game server's SteamID once its logon completed.
        /// </summary>
        /// <param name="steamServersConnected">Steam's notice.</param>
        private void OnGameServerConnected(SteamServersConnected_t steamServersConnected)
        {
            OwnSteamId = SteamGameServer.GetSteamID().m_SteamID;

            Debug.Log($"[{nameof(SteamBootstrap)}] game server logged on with SteamID [{OwnSteamId}].");
        }

        /// <summary>
        /// Logs why the game server's logon failed.
        /// </summary>
        /// <param name="steamServerConnectFailure">Steam's notice.</param>
        private void OnGameServerConnectFailure(SteamServerConnectFailure_t steamServerConnectFailure) => Debug.LogError($"[{nameof(SteamBootstrap)}] game server could not log on: [{steamServerConnectFailure.m_eResult}], still retrying: [{steamServerConnectFailure.m_bStillRetrying}].");

        /// <summary>
        /// Drops the game server callbacks, leaving Steam running for the next scene.
        /// </summary>
        private void OnDestroy()
        {
            _gameServerConnectedCallback?.Dispose();
            _gameServerConnectFailureCallback?.Dispose();
        }

        /// <summary>
        /// Stops the transports, then shuts Steam down as the application exits.
        /// </summary>
        /// <remarks>The transports go first so their connections are closed while Steam can still send the close.</remarks>
        private void OnApplicationQuit()
        {
            if (!IsSteamInitialized)
                return;

            SteamShutdownStarted?.Invoke();

            if (_unityTransportManager != null)
                _unityTransportManager.ShutdownAll();

            if (_gameServerEnabled)
            {
                SteamGameServer.LogOff();
                GameServer.Shutdown();
            }
            else
            {
                SteamAPI.Shutdown();
            }

            IsSteamInitialized = false;
        }

        /// <summary>
        /// Checks whether the user's Steam is unavailable, never initialized or already shut down.
        /// </summary>
        /// <returns><see langword="true"/> when no Steam call may be made.</returns>
        private static bool IsClientShutDown()
        {
            try
            {
                InteropHelp.TestIfAvailableClient();

                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        /// <summary>
        /// Checks whether the Steam game server is unavailable, never initialized or already shut down.
        /// </summary>
        /// <returns><see langword="true"/> when no game server call may be made.</returns>
        private static bool IsGameServerShutDown()
        {
            try
            {
                InteropHelp.TestIfAvailableGameServer();

                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }
}
#endif
