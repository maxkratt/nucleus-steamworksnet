// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using Steamworks;
using UnityEngine;

namespace Nucleus.Integrations.SteamworksNet.Samples.DedicatedServer
{
    /// <summary>
    /// Lists the dedicated server in Steam's server browser: its name, map and player limit, a tag that marks it as this sample's, and
    /// the fake address clients join it by, once Steam grants one.
    /// </summary>
    /// <remarks>
    /// Needs a <see cref="SteamBootstrap"/> with its game server option on, and a <see cref="SteamworksNetTransport"/> on
    /// <see cref="SteamworksNetAddressing.FakeIp"/> with <see cref="SteamSockets.GameServerEnabled"/> on; the transport manager's automatic start
    /// mode starts the server. The fake address goes into the game tags because those reach every browsing client with the server's
    /// entry, so a client needs no second query to learn it.
    /// </remarks>
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/maxkratt/nucleus-steamworksnet/blob/master/Samples~/DedicatedServer/README.md")]
    public class DedicatedServerListing : MonoBehaviour
    {
        /// <summary>
        /// The game tag that marks a server as this sample's, which the browser filters on.
        /// </summary>
        public const string SampleTag = "nucleus_dedicated_server";
        /// <summary>
        /// The prefix of the game tag that carries the server's fake address and port.
        /// </summary>
        public const string FakeAddressTagPrefix = "fake=";

        /// <summary>
        /// The bootstrap that owns the Steam game server.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Steam Bootstrap, with its game server option on.")]
        private SteamBootstrap _steamBootstrap;
        /// <summary>
        /// The name the server is listed under.
        /// </summary>
        [SerializeField]
        [Tooltip("The name the server is listed under.")]
        private string _serverName = "Nucleus dedicated server";
        /// <summary>
        /// The map the server lists.
        /// </summary>
        [SerializeField]
        [Tooltip("The map the server lists.")]
        private string _mapName = "Arena";
        /// <summary>
        /// The most players the server lists.
        /// </summary>
        [SerializeField]
        [Tooltip("The most players the server lists.")]
        private int _maximumPlayers = 16;
        /// <summary>
        /// The skin the panel draws with.
        /// </summary>
        /// <remarks>Left empty, the panel draws with Unity's default look.</remarks>
        [SerializeField]
        [Tooltip("The skin the panel draws with. Leave empty for Unity's default look.")]
        private GUISkin _guiSkin;

        /// <summary>
        /// The fake address and port Steam granted the server, or <see langword="null"/> before the grant.
        /// </summary>
        private string _fakeAddress;
        /// <summary>
        /// The callback that reports the fake address grant, held so it is not collected.
        /// </summary>
        private Callback<SteamNetworkingFakeIPResult_t> _fakeIpResultCallback;

        /// <summary>
        /// The fake port index the transport asks for, its first and only one.
        /// </summary>
        private const int FakePortIndex = 0;

        /// <summary>
        /// Publishes the listing and waits for the fake address grant.
        /// </summary>
        private void Awake()
        {
            if (_steamBootstrap == null || !_steamBootstrap.GameServerEnabled)
            {
                Debug.LogError($"[{nameof(DedicatedServerListing)}] needs its [{nameof(SteamBootstrap)}] set, with its game server option on.");
                enabled = false;

                return;
            }

            // The bootstrap wakes first, so the game server is up here or never will be.
            if (!_steamBootstrap.IsSteamInitialized)
            {
                enabled = false;

                return;
            }

            SteamGameServer.SetServerName(_serverName);
            SteamGameServer.SetMapName(_mapName);
            SteamGameServer.SetMaxPlayerCount(_maximumPlayers);
            SteamGameServer.SetGameTags(SampleTag);
            SteamGameServer.SetAdvertiseServerActive(true);

            _fakeIpResultCallback = Callback<SteamNetworkingFakeIPResult_t>.CreateGameServer(OnFakeIpResult);

            // A grant made before this scene loaded raises nothing more, so adopt it.
            SteamGameServerNetworkingSockets.GetFakeIP(FakePortIndex, out SteamNetworkingFakeIPResult_t fakeIpResult);

            if (fakeIpResult.m_eResult is EResult.k_EResultOK)
                OnFakeIpResult(fakeIpResult);
        }

        /// <summary>
        /// Shows the listing while the server runs in a window; a headless build draws nothing.
        /// </summary>
        private void OnGUI()
        {
            SampleGUI.BeginPanel(_guiSkin, 464f, "Dedicated Server");
            GUILayout.Label($"Dedicated server: {_serverName} on {_mapName}");
            GUILayout.Label($"Game server SteamID: {_steamBootstrap.OwnSteamId}");
            GUILayout.Label(_fakeAddress is null ? "Waiting for Steam to grant a fake address." : $"Fake address: {_fakeAddress}");
            SampleGUI.EndPanel();
        }

        /// <summary>
        /// Records the fake address Steam granted and adds it to the game tags.
        /// </summary>
        /// <param name="fakeIpResult">Steam's answer.</param>
        private void OnFakeIpResult(SteamNetworkingFakeIPResult_t fakeIpResult)
        {
            if (fakeIpResult.m_eResult is not EResult.k_EResultOK)
                return;

            uint ip = fakeIpResult.m_unIP;
            _fakeAddress = $"{ip >> 24}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}:{fakeIpResult.m_unPorts[FakePortIndex]}";

            SteamGameServer.SetGameTags($"{SampleTag},{FakeAddressTagPrefix}{_fakeAddress}");

            Debug.Log($"[{nameof(DedicatedServerListing)}] lists [{_serverName}] at the fake address [{_fakeAddress}].");
        }

        /// <summary>
        /// Drops the callback.
        /// </summary>
        /// <remarks>The listing ends with the game server, which the bootstrap logs off and shuts down as the application exits.</remarks>
        private void OnDestroy() => _fakeIpResultCallback?.Dispose();
    }
}
#endif
