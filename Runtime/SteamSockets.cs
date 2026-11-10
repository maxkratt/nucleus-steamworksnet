// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using CodeBoost.Logging;
using Nucleus.Connections;
using Nucleus.Managers.Core;
using Nucleus.Managers.Messages;
using Nucleus.Managers.NetworkLoop;
using Nucleus.Managers.Transports;
using Nucleus.Packets;
using Nucleus.Transports;
using Steamworks;

namespace Nucleus.Integrations.SteamworksNet
{
    /// <summary>
    /// A <see cref="Transport"/> that carries a session over Steam Networking Sockets through Steamworks.NET: peer to peer by SteamID
    /// through Valve's relay network, by IP address, or by a fake IP address Steam grants a game server.
    /// </summary>
    /// <remarks>
    /// A pair of sockets on <see cref="SocketPairTransport{T0,T1}"/>: <see cref="SteamServerSocket"/> listens and
    /// <see cref="SteamClientSocket"/> dials, and this type holds the settings both read, the Steam callback registrations they share, and
    /// the frame's flush. Steam reports connection changes on whichever thread pumps its callbacks, so they are queued and applied on the
    /// network loop thread when each socket receives. A host's own client is paired in process: its traffic is copied straight across, and
    /// the server adopts the client socket's own Connection as its roster entry. Steam could carry it instead, since a P2P connect to an
    /// identity in the same process becomes a loopback connection to that identity's listen socket, but pairing skips Steam's per-message
    /// work. With <see cref="GameServerEnabled"/> the server half runs on the game server interface, so a dedicated server needs no Steam client; the
    /// client half always runs on the user's. Steam has to be initialized, and its callbacks pumped, by the game; this transport never
    /// initializes or shuts Steam down.
    /// </remarks>
    public class SteamSockets : SocketPairTransport<SteamServerSocket, SteamClientSocket>, INetworkLoopStepCallback
    {
        /// <summary>
        /// Decides whether a client asking to connect may proceed, before the engine sees it.
        /// </summary>
        /// <param name="steamId">Who is asking, as Steam authenticated it, or <see cref="UnsetSteamId"/> when it has no SteamID.</param>
        /// <returns><see langword="true"/> to accept the client.</returns>
        public delegate bool ConnectingClientFilterHandler(ulong steamId);
        /// <summary>
        /// A connected client's SteamID became known to this peer, or the client left.
        /// </summary>
        /// <remarks>
        /// Raised on the network loop thread. A server hears of every client; a client hears of itself, and of the others while the server
        /// has <see cref="ShareSteamIdsEnabled"/> on. It can fire before or after the engine reports the same client by other events, so
        /// read <see cref="TryGetSteamId(uint, out ulong)"/> from both places. It is not raised for the entries dropped when a session ends.
        /// </remarks>
        public event SteamIdChangedHandler SteamIdChanged;
        /// <summary>
        /// Handler for the <see cref="SteamIdChanged"/> event.
        /// </summary>
        /// <param name="connectionId">The <see cref="Connection.Id"/> of the client.</param>
        /// <param name="steamId">The client's SteamID64, or <see cref="UnsetSteamId"/> when the client has left.</param>
        public delegate void SteamIdChangedHandler(uint connectionId, ulong steamId);
        /// <summary>
        /// How the server is reached, and so how a client dials it.
        /// </summary>
        /// <remarks>Read at the next start of either half. Both sides have to agree on it.</remarks>
        public SteamworksNetAddressing Addressing;
        /// <summary>
        /// True to run the server half on Steam's game server interface, initialized by <c>GameServer.Init</c>, rather than the logged-in
        /// user's, initialized by <c>SteamAPI.Init</c>.
        /// </summary>
        /// <remarks>Read at the next server start. A dedicated server turns it on; a listen server may run on either. The client half always runs on the user's interface.</remarks>
        public bool GameServerEnabled;
        /// <summary>
        /// The SteamID64 of the server a client connects to in <see cref="SteamworksNetAddressing.SteamId"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="UnsetSteamId"/>, or this peer's own SteamID, joins the server this same peer is running. Ignored, with a logged
        /// warning, while this peer's own server is running, whose client always joins it. Read at the next client start.
        /// </remarks>
        public ulong ServerSteamId;
        /// <summary>
        /// The address and port a client connects to in <see cref="SteamworksNetAddressing.IpAddress"/> and <see cref="SteamworksNetAddressing.FakeIp"/>,
        /// such as <c>203.0.113.7:27015</c>.
        /// </summary>
        /// <remarks>
        /// <see langword="null"/> or empty joins the server this same peer is running. Without a port, <see cref="Configuration.Port"/> is
        /// used, and <c>localhost</c> is the IPv4 loopback; host names are not looked up. A fake address is the one the server logs, and
        /// reports as <see cref="ServerFakeAddress"/>. Read at the next client start.
        /// </remarks>
        public string ServerAddress;
        /// <summary>
        /// The Steam virtual port the server listens on and the client connects to in <see cref="SteamworksNetAddressing.SteamId"/>. Both
        /// sides have to agree on it.
        /// </summary>
        /// <remarks><see cref="SteamworksNetAddressing.IpAddress"/> listens on <see cref="Configuration.Port"/> instead; <see cref="SetPort"/> sets both.</remarks>
        public int VirtualPort;
        /// <summary>
        /// The send rate Steam never drops below on a connection, in bytes per second.
        /// </summary>
        /// <remarks>
        /// <see cref="UnsetSendSetting"/> keeps Steam's default of 256 KiB a second. A Steam ID link sends at this floor in both directions
        /// rather than climbing towards <see cref="SendRateMaximumBytesPerSecond"/>, so it sets how fast large reliable data leaves there;
        /// an address link climbs on its own. Applied to connections opened after it is set.
        /// </remarks>
        public uint SendRateMinimumBytesPerSecond = UnsetSendSetting;
        /// <summary>
        /// The send rate Steam never rises above on a connection, in bytes per second.
        /// </summary>
        /// <remarks>
        /// <see cref="UnsetSendSetting"/> keeps Steam's default of 256 KiB a second, the same as its minimum, which leaves a large
        /// late-join snapshot taking seconds to arrive. Steam estimates the rate between the minimum and this. Applied to connections
        /// opened after it is set.
        /// </remarks>
        public uint SendRateMaximumBytesPerSecond = UnsetSendSetting;
        /// <summary>
        /// The most bytes Steam holds queued on a connection before it refuses a send, in bytes.
        /// </summary>
        /// <remarks>
        /// <see cref="UnsetSendSetting"/> keeps Steam's default of 512 KiB. Reliable messages that would overfill it wait in this transport
        /// until Steam drains, so a larger buffer only lets a burst leave sooner; unreliable ones that would overfill it are dropped.
        /// Applied to connections opened after it is set.
        /// </remarks>
        public uint SendBufferSizeBytes = UnsetSendSetting;
        /// <summary>
        /// The largest packet the server accepts from a client, in bytes; a client that sends a larger one is disconnected.
        /// </summary>
        /// <remarks>
        /// Bounds the buffer the server joins a client's split reliable packet into, so a client cannot grow the server's memory without
        /// end. <see cref="UnsetMaximumClientPacketBytes"/> sets no limit. Packets the server receives from its own client, and packets
        /// a client receives from its server, are never limited. Read on every receive.
        /// </remarks>
        public uint MaximumClientPacketBytes = UnsetMaximumClientPacketBytes;
        /// <summary>
        /// True to carry <see cref="SteamworksNetAddressing.SteamId"/> links over Valve's relay network only, so no peer learns another's
        /// IP address.
        /// </summary>
        /// <remarks>
        /// Turns Steam's ICE off on the listen socket and on the client's connection, so Steam shares no address and never tries a direct
        /// route. Off leaves the choice to Steam, which follows each player's own Steam setting for direct connections and can then
        /// connect two players directly for a lower ping. The other addressings are unaffected. Applied to connections opened after it is
        /// set.
        /// </remarks>
        public bool RelayOnlyEnabled;
        /// <summary>
        /// True for the server to tell every client the SteamID of every other client, so each client's <see cref="TryGetSteamId(uint, out ulong)"/> answers for all of them.
        /// </summary>
        /// <remarks>
        /// Only the server's setting counts. Off, a client knows only its own SteamID. A SteamID links to the player's public Steam profile,
        /// so leave this off where players should not learn who the others are. Applied to clients as they arrive and leave.
        /// </remarks>
        public bool ShareSteamIdsEnabled;
        /// <summary>
        /// An optional gate every client asking to connect passes before the engine sees it, such as lobby members only or a ban list,
        /// or <see langword="null"/> to accept anyone while the server has room.
        /// </summary>
        /// <remarks>Called on the network loop thread. A refused client is closed with a reason it can log.</remarks>
        public ConnectingClientFilterHandler ConnectingClientFilter;
        /// <inheritdoc/>
        /// <remarks>
        /// Zero, because Steam frames each message itself and reports on receipt whether it rode the reliable channel, so this
        /// transport adds no header of its own.
        /// </remarks>
        public override uint DatagramOverheadBytes => 0;
        /// <summary>
        /// The time the server half spends receiving each frame, in microseconds, smoothed over roughly the last twenty frames.
        /// </summary>
        /// <remarks>Covers draining Steam and copying each message out; it should stay flat as clients join, since one poll group serves them all.</remarks>
        public double ServerReceiveMicroseconds { get; internal set; }
        /// <summary>
        /// How many reliable sends Steam has refused since this transport was added, each of which closed its link.
        /// </summary>
        /// <remarks>Reliable messages wait for room in Steam's queue, so this counts only one larger than <see cref="SendBufferSizeBytes"/>.</remarks>
        public int RefusedReliableSendCount { get; internal set; }
        /// <summary>
        /// How many unreliable sends Steam has refused since this transport was added, each of which was dropped.
        /// </summary>
        /// <remarks>Steam refuses one when the connection's queue is past <see cref="SendBufferSizeBytes"/>, as a large state burst can push it.</remarks>
        public int RefusedUnreliableSendCount { get; internal set; }
        /// <summary>
        /// The largest reliable packet the engine has handed this transport since it was added, in bytes.
        /// </summary>
        /// <remarks>
        /// The engine hands a reliable packet over whole, at any size. Steam carries at most 512 KiB in one message, so a packet past that
        /// is sent in pieces and joined again on receipt.
        /// </remarks>
        public int LargestReliablePacketBytes { get; private set; }
        /// <summary>
        /// The largest unreliable packet the engine has handed this transport since it was added, in bytes.
        /// </summary>
        /// <remarks>The engine splits an unreliable packet to the transmission unit itself, so this stays at or below it.</remarks>
        public int LargestUnreliablePacketBytes { get; private set; }
        /// <summary>
        /// The fake address and port Steam granted this server in <see cref="SteamworksNetAddressing.FakeIp"/>, which clients set as
        /// <see cref="ServerAddress"/>, or <see langword="null"/> until Steam grants one.
        /// </summary>
        /// <remarks>Steam grants it a moment after the server starts, and logs it then. It stays with this process across restarts.</remarks>
        public string ServerFakeAddress { get; internal set; }
        /// <summary>
        /// The value of <see cref="ServerSteamId"/> that means this peer's own server.
        /// </summary>
        public const ulong UnsetSteamId = 0;
        /// <summary>
        /// The value of a send setting that keeps Steam's own default.
        /// </summary>
        public const uint UnsetSendSetting = 0;
        /// <summary>
        /// The <see cref="MaximumClientPacketBytes"/> that sets no limit.
        /// </summary>
        public const uint UnsetMaximumClientPacketBytes = 0;
        /// <summary>
        /// The launch-argument flag which sets <see cref="ServerAddress"/>.
        /// </summary>
        public const string AddressArgument = "address";
        /// <summary>
        /// The launch-argument flag which sets <see cref="ServerSteamId"/>.
        /// </summary>
        public const string SteamIdArgument = "steamid";
        /// <summary>
        /// The interface the running server half was started on.
        /// </summary>
        internal SteamNetworkingInterface ServerNetworkingInterface { get; set; } = SteamNetworkingInterface.Client;
        /// <summary>
        /// Status changes on connections to this peer's server, raised on Steam's callback thread and applied by the server socket's receive.
        /// </summary>
        internal readonly ConcurrentQueue<SteamNetConnectionStatusChangedCallback_t> ServerStatusChanges = new();
        /// <summary>
        /// Status changes on this peer's client link, raised on Steam's callback thread and applied by the client socket's receive.
        /// </summary>
        internal readonly ConcurrentQueue<SteamNetConnectionStatusChangedCallback_t> ClientStatusChanges = new();
        /// <summary>
        /// Fake IP grants for this peer's server, raised on Steam's callback thread and applied by the server socket's receive.
        /// </summary>
        internal readonly ConcurrentQueue<SteamNetworkingFakeIPResult_t> FakeIpResults = new();
        /// <summary>
        /// Relay network status changes on the user's interface, raised on Steam's callback thread and applied by the client socket's receive.
        /// </summary>
        internal readonly ConcurrentQueue<SteamRelayNetworkStatus_t> RelayNetworkStatuses = new();
        /// <summary>
        /// The send buffer Steam gives a connection when <see cref="SendBufferSizeBytes"/> is <see cref="UnsetSendSetting"/>, in bytes.
        /// </summary>
        internal const int DefaultSendBufferBytes = 512 * 1024;
        /// <summary>
        /// The name read as the IPv4 loopback address wherever an address is accepted; no other host name is looked up.
        /// </summary>
        internal const string LocalhostText = "localhost";
        /// <summary>
        /// The registration for status changes on the user's interface, or <see langword="null"/> before either half first needed it.
        /// </summary>
        /// <remarks>
        /// Held here rather than by a socket, because one registration serves both: a server on the user's interface and this peer's own
        /// client link both report through it, told apart by the listen socket each change names.
        /// </remarks>
        private Callback<SteamNetConnectionStatusChangedCallback_t> _clientStatusChangedCallback;
        /// <summary>
        /// The registration for status changes on the game server interface, or <see langword="null"/> before a server first ran on it.
        /// </summary>
        private Callback<SteamNetConnectionStatusChangedCallback_t> _gameServerStatusChangedCallback;
        /// <summary>
        /// The registration for fake IP grants on the user's interface, or <see langword="null"/> before a server first ran on it.
        /// </summary>
        private Callback<SteamNetworkingFakeIPResult_t> _clientFakeIpResultCallback;
        /// <summary>
        /// The registration for fake IP grants on the game server interface, or <see langword="null"/> before a server first ran on it.
        /// </summary>
        private Callback<SteamNetworkingFakeIPResult_t> _gameServerFakeIpResultCallback;
        /// <summary>
        /// The registration for relay network status changes on the user's interface, or <see langword="null"/> before the client
        /// half first needed it.
        /// </summary>
        private Callback<SteamRelayNetworkStatus_t> _relayNetworkStatusCallback;
        /// <summary>
        /// The conditions Steam simulates, or <see langword="null"/> for none.
        /// </summary>
        private NetworkConditions? _networkConditions;
        /// <summary>
        /// True while this transport's step callback is registered with the network loop.
        /// </summary>
        /// <remarks>A shutdown can arrive twice, and the second must not unregister what is no longer registered.</remarks>
        private bool _isStepCallbackRegistered;
        /// <summary>
        /// The SteamID of each client this peer knows of, by <see cref="Connection.Id"/>.
        /// </summary>
        private readonly Dictionary<uint, ulong> _steamIdsByConnectionId = new();
        /// <summary>
        /// The <see cref="Connection.Id"/> of each client this peer knows of, by SteamID.
        /// </summary>
        /// <remarks>One account connected twice, which <see cref="SteamworksNetAddressing.IpAddress"/> allows, is held under its latest connection.</remarks>
        private readonly Dictionary<ulong, uint> _connectionIdsBySteamId = new();
        /// <summary>
        /// The priority of each lane; lower runs first. Both lanes share one, so neither starves the other.
        /// </summary>
        private static readonly int[] LanePriorities = { 0, 0 };
        /// <summary>
        /// The share of the send rate each lane gets while both have data waiting.
        /// </summary>
        /// <remarks>
        /// A fixed three to one in favour of unreliable state, so a reliable burst such as a late-join snapshot still moves while state
        /// keeps flowing. It is not a setting: reliable messages are handed to Steam ahead of unreliable ones and wait in the transport
        /// while the send buffer is full, so the split only decides how the rate is shared while both lanes have data queued in Steam.
        /// </remarks>
        private static readonly ushort[] LaneWeights = { 1, 3 };
        /// <summary>
        /// How long Steam holds back a packet it reorders, in milliseconds, matching Synapse's reorder delay.
        /// </summary>
        private const int ReorderDelayMilliseconds = 100;
        /// <summary>
        /// The IPv4 loopback address, 127.0.0.1, in host byte order.
        /// </summary>
        private const uint LoopbackIPv4 = 0x7F000001;
        /// <summary>
        /// The length of the IPv6 form Steam stores every address in.
        /// </summary>
        private const int IPv6AddressBytes = 16;

        /// <inheritdoc/>
        /// <remarks>Registers the frame's flush once both sockets are rented and initialized.</remarks>
        public override async Task InitializeAsync(CoreManager coreManager, uint transportId)
        {
            await base.InitializeAsync(coreManager, transportId);

            coreManager.NetworkLoopManager.RegisterNetworkLoopStepCallbacks(this);
            _isStepCallbackRegistered = true;

            coreManager.ServerManager.ClientAuthenticated += OnClientAuthenticated;
            coreManager.ServerManager.ClientDisconnecting += OnClientDisconnecting;
            coreManager.ClientManager.LocalClientAuthenticated += OnLocalClientAuthenticated;
            coreManager.TransportManager.ConnectionLocalStateChanged += OnConnectionLocalStateChanged;
            coreManager.MessageManager.RegisterMessageHandler<SteamIdNotice>(OnSteamIdNoticeReceived);
        }

        /// <inheritdoc/>
        /// <remarks>Steam carries every link through a relay or a direct route it chooses itself, so the security checks stay on.</remarks>
        public override bool IsLocalTransport() => false;

        /// <inheritdoc/>
        /// <remarks>Sets both <see cref="VirtualPort"/> and <see cref="Configuration.Port"/>, so whichever <see cref="Addressing"/> runs uses it.</remarks>
        public override void SetPort(ushort port)
        {
            VirtualPort = port;
            Configuration.Port = port;
        }

        /// <inheritdoc/>
        /// <remarks>Applied to clients arriving after the call; clients already admitted stay.</remarks>
        public override void SetMaximumConnections(uint maximumConnections) => ServerConfiguration.MaximumConnections = maximumConnections;

        /// <inheritdoc/>
        /// <remarks>
        /// Used only by a <see cref="SteamworksNetAddressing.IpAddress"/> server, which listens on it at <see cref="Configuration.Port"/>. Every other
        /// mode, and the client, lets Steam choose its local address.
        /// </remarks>
        public override bool SetBindAddress(string bindAddress)
        {
            if (string.IsNullOrEmpty(bindAddress))
            {
                Configuration.BindAddress = null;

                return true;
            }

            if (!string.Equals(bindAddress, LocalhostText, StringComparison.OrdinalIgnoreCase) && !IPAddress.TryParse(bindAddress, out _))
            {
                Logger<SteamSockets>.LogError($"The bind address [{bindAddress}] is not an IP address or [{LocalhostText}].");

                return false;
            }

            Configuration.BindAddress = bindAddress;

            return true;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// <para>
        /// Supports every condition through Steam's own simulation, which can change mid-session. Jitter follows Steam's exponential
        /// spread, averaging half of <see cref="NetworkConditions.JitterMilliseconds"/> and never above it, and reordered packets are held
        /// back 100 ms. <see cref="NetworkConditions.Seed"/> is ignored, since Steam draws its own random choices.
        /// </para>
        /// <para>
        /// Steam keeps these settings for the whole process, so they also apply to any other Steam connection it has open. The host's own
        /// client, paired in process, never crosses Steam and is unaffected; test from a remote client.
        /// </para>
        /// </remarks>
        public override void SetNetworkConditions(NetworkConditions? networkConditions)
        {
            _networkConditions = networkConditions;

            // Steam may not be up yet; the conditions are applied again at every start.
            ApplyNetworkConditions();
        }

        /// <inheritdoc/>
        public override NetworkConditions? GetNetworkConditions() => _networkConditions;

        /// <summary>
        /// Reads Steam's live status of the link carrying a Connection.
        /// </summary>
        /// <param name="connection">A remote client on this peer's server, or this peer's own client Connection for its link to the server.</param>
        /// <param name="linkStatus">The status, when there is a Steam link to read.</param>
        /// <returns><see langword="true"/> when the Connection rides a Steam link; the host's own client, paired in process, has none.</returns>
        /// <remarks>
        /// Steamworks.NET allocates about 80 bytes marshalling each call, so read it at a display's pace, such as a few times a second,
        /// rather than every frame.
        /// </remarks>
        public bool TryGetLinkStatus(Connection connection, out SteamLinkStatus linkStatus)
        {
            linkStatus = default;

            if (connection is null)
                return false;

            // The host's own client is both of these Connections at once, and has no Steam link on either side.
            if (ClientSocket is not null && connection == ClientSocket.Connection)
                return ClientSocket.TryGetLinkStatus(out linkStatus);

            return ServerSocket is not null && ServerSocket.TryGetLinkStatus(connection, out linkStatus);
        }

        /// <summary>
        /// Gets the SteamID of a connected client.
        /// </summary>
        /// <param name="connectionId">The client's <see cref="Connection.Id"/>, such as a <c>NetworkSystem.ControllerConnectionId</c>.</param>
        /// <param name="steamId">The client's SteamID64, as Steam authenticated its link.</param>
        /// <returns>
        /// <see langword="true"/> when this peer knows it: on a server for any authenticated client, and on a client for itself, or for
        /// any client while the server has <see cref="ShareSteamIdsEnabled"/> on and the notice has arrived.
        /// </returns>
        public bool TryGetSteamId(uint connectionId, out ulong steamId) => _steamIdsByConnectionId.TryGetValue(connectionId, out steamId);

        /// <summary>
        /// Gets the SteamID of a connected client.
        /// </summary>
        /// <param name="connection">The client's Connection, on any peer.</param>
        /// <param name="steamId">The client's SteamID64, as Steam authenticated its link.</param>
        /// <returns><see langword="true"/> when this peer knows it, as for <see cref="TryGetSteamId(uint, out ulong)"/>.</returns>
        public bool TryGetSteamId(Connection connection, out ulong steamId)
        {
            steamId = UnsetSteamId;

            return connection is not null && TryGetSteamId(connection.Id, out steamId);
        }

        /// <summary>
        /// Gets the Connection of the client playing as a SteamID.
        /// </summary>
        /// <param name="steamId">The SteamID64 to find.</param>
        /// <param name="connection">This peer's Connection for that client.</param>
        /// <returns><see langword="true"/> when this peer knows the SteamID and holds a Connection for its client.</returns>
        public bool TryGetConnection(ulong steamId, out Connection connection)
        {
            connection = null;

            return _connectionIdsBySteamId.TryGetValue(steamId, out uint connectionId) && CoreManager.TransportManager.TryGetConnection(connectionId, out connection);
        }

        /// <inheritdoc/>
        public override async Task ShutdownAsync()
        {
            await base.ShutdownAsync();

            if (_isStepCallbackRegistered)
            {
                CoreManager.NetworkLoopManager.UnregisterNetworkLoopStepCallbacks(this);
                _isStepCallbackRegistered = false;

                CoreManager.ServerManager.ClientAuthenticated -= OnClientAuthenticated;
                CoreManager.ServerManager.ClientDisconnecting -= OnClientDisconnecting;
                CoreManager.ClientManager.LocalClientAuthenticated -= OnLocalClientAuthenticated;
                CoreManager.TransportManager.ConnectionLocalStateChanged -= OnConnectionLocalStateChanged;
                CoreManager.MessageManager.UnregisterMessageHandler<SteamIdNotice>(OnSteamIdNoticeReceived);
                ForgetAllSteamIds();
            }

            _clientStatusChangedCallback?.Dispose();
            _gameServerStatusChangedCallback?.Dispose();
            _clientFakeIpResultCallback?.Dispose();
            _gameServerFakeIpResultCallback?.Dispose();
            _relayNetworkStatusCallback?.Dispose();
            _clientStatusChangedCallback = null;
            _gameServerStatusChangedCallback = null;
            _clientFakeIpResultCallback = null;
            _gameServerFakeIpResultCallback = null;
            _relayNetworkStatusCallback = null;

            ServerStatusChanges.Clear();
            ClientStatusChanges.Clear();
            FakeIpResults.Clear();
            RelayNetworkStatuses.Clear();
        }

        /// <inheritdoc/>
        NetworkLoopSteps INetworkLoopStepCallback.GetNetworkLoopSteps() => NetworkLoopSteps.LateVariableUpdate;

        /// <summary>
        /// Hands the frame's staged messages of both sockets to Steam, the engine having sent everything it will this frame.
        /// </summary>
        /// <param name="networkLoopStep">The step being performed.</param>
        /// <param name="stepDelta">Delta information about the step.</param>
        void INetworkLoopStepCallback.OnNetworkLoopStep(NetworkLoopSteps networkLoopStep, StepDelta stepDelta)
        {
            ServerSocket?.FlushStagedMessages();
            ClientSocket?.FlushStagedMessages();
        }

        /// <summary>
        /// Gets the SteamID-bearing identity of the server this peer's client is linked to over Steam.
        /// </summary>
        /// <param name="serverIdentity">The server's identity, as Steam authenticated it.</param>
        /// <returns><see langword="true"/> when the client half has a Steam link to read it from.</returns>
        internal bool TryGetServerIdentity(out SteamNetworkingIdentity serverIdentity)
        {
            serverIdentity = default;

            return ClientSocket is not null && ClientSocket.TryGetServerIdentity(out serverIdentity);
        }

        /// <summary>
        /// Records the size of a packet the engine handed either socket.
        /// </summary>
        /// <param name="channel">The channel the packet rides.</param>
        /// <param name="payloadBytes">The packet's length, in bytes.</param>
        internal void RecordPacketSize(Channel channel, int payloadBytes)
        {
            if (channel is Channel.Reliable)
            {
                if (payloadBytes > LargestReliablePacketBytes)
                    LargestReliablePacketBytes = payloadBytes;
            }
            else if (payloadBytes > LargestUnreliablePacketBytes)
            {
                LargestUnreliablePacketBytes = payloadBytes;
            }
        }

        /// <summary>
        /// Checks an interface is initialized, registers for its callbacks, and starts relay discovery.
        /// </summary>
        /// <param name="steamNetworkingInterface">The interface the socket starting runs on.</param>
        /// <returns><see langword="true"/> when Steam is ready to connect.</returns>
        internal bool TryPrepareSteam(SteamNetworkingInterface steamNetworkingInterface)
        {
            try
            {
                // Throws when the game has not initialized Steam; the transport never does that itself.
                steamNetworkingInterface.InitRelayNetworkAccess();
            }
            catch (InvalidOperationException invalidOperationException)
            {
                Logger<SteamSockets>.LogError($"Steam is not initialized: [{invalidOperationException.Message}]. Initialize Steamworks.NET, or GameServer for a game server interface, before connecting.");

                return false;
            }

            if (steamNetworkingInterface == SteamNetworkingInterface.GameServer)
            {
                _gameServerStatusChangedCallback ??= steamNetworkingInterface.CreateStatusChangedCallback(OnGameServerStatusChanged);
                _gameServerFakeIpResultCallback ??= steamNetworkingInterface.CreateFakeIpResultCallback(OnGameServerFakeIpResult);
            }
            else
            {
                _clientStatusChangedCallback ??= steamNetworkingInterface.CreateStatusChangedCallback(OnClientStatusChanged);
                _clientFakeIpResultCallback ??= steamNetworkingInterface.CreateFakeIpResultCallback(OnClientFakeIpResult);
                _relayNetworkStatusCallback ??= Callback<SteamRelayNetworkStatus_t>.Create(OnRelayNetworkStatusChanged);
            }

            ApplyNetworkConditions();

            return true;
        }

        /// <summary>
        /// Builds the per-connection Steam options from <see cref="Transport.Configuration"/> and the send settings.
        /// </summary>
        /// <returns>The options to pass when listening or connecting.</returns>
        /// <remarks>
        /// A listen socket passes its options on to every connection it accepts. Unauthenticated IP connections and symmetric connects
        /// are turned off explicitly, so a global setting elsewhere in the game cannot let an unidentified peer in.
        /// </remarks>
        internal SteamNetworkingConfigValue_t[] CreateOptions()
        {
            List<SteamNetworkingConfigValue_t> options = new()
            {
                CreateInt32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_IP_AllowWithoutAuth, value: 0),
                CreateInt32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SymmetricConnect, value: 0)
            };

            if (Configuration.ConnectingTimeoutSeconds > 0)
                options.Add(CreateInt32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutInitial, Configuration.ConnectingTimeoutSeconds * 1000));

            if (Configuration.ConnectedTimeoutSeconds > 0)
                options.Add(CreateInt32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected, Configuration.ConnectedTimeoutSeconds * 1000));

            if (SendRateMinimumBytesPerSecond is not UnsetSendSetting)
                options.Add(CreateInt32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, (int)SendRateMinimumBytesPerSecond));

            if (SendRateMaximumBytesPerSecond is not UnsetSendSetting)
                options.Add(CreateInt32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, (int)SendRateMaximumBytesPerSecond));

            if (SendBufferSizeBytes is not UnsetSendSetting)
                options.Add(CreateInt32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, (int)SendBufferSizeBytes));

            // Only Steam ID links use ICE; an address is dialled as given, and a fake IP always rides the relay.
            if (RelayOnlyEnabled && Addressing is SteamworksNetAddressing.SteamId)
                options.Add(CreateInt32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable, Constants.k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_Disable));

            return options.ToArray();
        }

        /// <summary>
        /// Gets the send buffer the connections opened next will have, as <see cref="CreateOptions"/> sets it.
        /// </summary>
        /// <returns>The buffer, in bytes.</returns>
        internal int GetSendBufferBytes() => SendBufferSizeBytes is UnsetSendSetting ? DefaultSendBufferBytes : (int)SendBufferSizeBytes;

        /// <summary>
        /// Checks whether the user's interface has shut down under this transport.
        /// </summary>
        /// <returns><see langword="true"/> when Steam is no longer initialized, so no Steam call may be made.</returns>
        /// <remarks>
        /// The game owns Steam and Unity does not order one object's teardown against another's, so Steam can shut down before this
        /// transport stops. Its handles died with it, so the teardown only forgets them rather than throwing partway and leaving the
        /// engine's state half walked down. The check is a field read while Steam is up.
        /// </remarks>
        internal static bool IsSteamShutDown() => SteamNetworkingInterface.Client.IsShutDown();

        /// <summary>
        /// Changes a socket's local Connection state, logging when the engine refuses.
        /// </summary>
        /// <param name="transportManager">The socket's TransportManager.</param>
        /// <param name="invoker">The role to move.</param>
        /// <param name="localConnectionState">The state to move to.</param>
        /// <param name="connection">The role's Connection.</param>
        /// <returns><see langword="true"/> when the role is in that state afterwards.</returns>
        internal static bool ChangeLocalState(TransportManager transportManager, Invoker invoker, LocalConnectionState localConnectionState, Connection connection)
        {
            ConnectionStateChangeResult connectionStateChangeResult = transportManager.ChangeLocalConnectionState(invoker, localConnectionState, connection);

            if (connectionStateChangeResult is ConnectionStateChangeResult.Success or ConnectionStateChangeResult.AlreadyInState)
                return true;

            Logger<SteamSockets>.LogError($"The {invoker} could not be moved to [{localConnectionState}]: [{connectionStateChangeResult}].");

            return false;
        }

        /// <summary>
        /// Walks a socket's local Connection down through disconnecting to disconnected, unless it is already there.
        /// </summary>
        /// <param name="transportManager">The socket's TransportManager.</param>
        /// <param name="invoker">The role to move.</param>
        /// <param name="connection">The role's Connection.</param>
        internal static void ChangeLocalStateToDisconnected(TransportManager transportManager, Invoker invoker, Connection connection)
        {
            if (connection.LocalState is LocalConnectionState.Disconnected)
                return;

            // Connecting goes straight down: passing through Disconnecting is for a link that was up.
            if (connection.LocalState is LocalConnectionState.Connected)
                ChangeLocalState(transportManager, invoker, LocalConnectionState.Disconnecting, connection);

            ChangeLocalState(transportManager, invoker, LocalConnectionState.Disconnected, connection);
        }

        /// <summary>
        /// Reads Steam's live status of one link.
        /// </summary>
        /// <param name="steamNetworkingInterface">The interface the link was opened on.</param>
        /// <param name="steamConnection">The link, or <see cref="HSteamNetConnection.Invalid"/> for none.</param>
        /// <param name="linkStatus">The status, when Steam reported one.</param>
        /// <returns><see langword="true"/> when Steam reported the link's status.</returns>
        internal static bool TryReadLinkStatus(SteamNetworkingInterface steamNetworkingInterface, HSteamNetConnection steamConnection, out SteamLinkStatus linkStatus)
        {
            linkStatus = default;

            if (steamConnection == HSteamNetConnection.Invalid || steamNetworkingInterface.IsShutDown())
                return false;

            if (steamNetworkingInterface.GetConnectionRealTimeStatus(steamConnection, out SteamNetConnectionRealTimeStatus_t realTimeStatus) is not EResult.k_EResultOK)
                return false;

            linkStatus = new(in realTimeStatus);

            return true;
        }

        /// <summary>
        /// Gives a connection its reliable and unreliable lanes, so unreliable state never waits behind a reliable burst.
        /// </summary>
        /// <param name="steamNetworkingInterface">The interface the connection was opened on.</param>
        /// <param name="steamConnection">The connection to configure.</param>
        internal static void ConfigureLanes(SteamNetworkingInterface steamNetworkingInterface, HSteamNetConnection steamConnection)
        {
            EResult result = steamNetworkingInterface.ConfigureConnectionLanes(steamConnection, LanePriorities, LaneWeights);

            if (result is not EResult.k_EResultOK)
                Logger<SteamSockets>.LogError($"Steam could not configure lanes: [{result}]. Unreliable packets will share the reliable lane.");
        }

        /// <summary>
        /// Reads an IP address, with or without a port, into Steam's form.
        /// </summary>
        /// <param name="text">The address, such as <c>203.0.113.7</c>, <c>203.0.113.7:27015</c> or <c>[::1]:27015</c>. <c>localhost</c> is the IPv4 loopback, and empty or <see langword="null"/> is every interface.</param>
        /// <param name="defaultPort">The port used when <paramref name="text"/> names none.</param>
        /// <param name="ipAddress">The address read.</param>
        /// <returns><see langword="true"/> when <paramref name="text"/> was an address. Host names are not looked up.</returns>
        internal static bool TryParseIpAddress(string text, ushort defaultPort, out SteamNetworkingIPAddr ipAddress)
        {
            // The embedded array has to exist at its full length for Steam to write the address into it.
            ipAddress = new() { m_ipv6 = new byte[IPv6AddressBytes] };

            if (string.Equals(text, LocalhostText, StringComparison.OrdinalIgnoreCase))
                ipAddress.SetIPv4(LoopbackIPv4, defaultPort);
            else if (!string.IsNullOrEmpty(text) && !ipAddress.ParseString(text))
                return false;

            if (ipAddress.m_port == 0)
                ipAddress.m_port = defaultPort;

            return true;
        }

        /// <summary>
        /// Logs why Steam closed a link, which a disconnect state change does not carry.
        /// </summary>
        /// <param name="connectionInfo">Steam's description of the closed link.</param>
        /// <param name="subject">What the link was, for the message.</param>
        internal static void LogClosed(SteamNetConnectionInfo_t connectionInfo, string subject)
        {
            Logger<SteamSockets>.LogInformation($"{subject} closed: [{(ESteamNetConnectionEnd)connectionInfo.m_eEndReason}] [{connectionInfo.m_szEndDebug}].");
        }

        /// <inheritdoc/>
        /// <remarks>Records the packet's size, then routes it to the sending role's socket.</remarks>
        protected override void OnSendPacket(OutgoingPacket outgoingPacket)
        {
            RecordPacketSize(outgoingPacket.Channel, outgoingPacket.Payload.Count);

            base.OnSendPacket(outgoingPacket);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Applies <see cref="Transport.PortArgument"/> to <see cref="VirtualPort"/> as well, and adds <see cref="AddressArgument"/> and
        /// <see cref="SteamIdArgument"/>, which set where the client connects.
        /// </remarks>
        protected override void OnApplyCommandLineArguments(CommandLineArguments commandLineArguments)
        {
            base.OnApplyCommandLineArguments(commandLineArguments);

            if (commandLineArguments.TryGetUInt16(PortArgument, out ushort port))
                VirtualPort = port;

            if (commandLineArguments.TryGetValue(AddressArgument, out string address))
                ServerAddress = address;

            if (!commandLineArguments.TryGetValue(SteamIdArgument, out string steamIdText))
                return;

            if (ulong.TryParse(steamIdText, out ulong steamId))
                ServerSteamId = steamId;
            else
                Logger<SteamSockets>.LogError($"The launch argument [{SteamIdArgument}] value [{steamIdText}] is not a SteamID.");
        }

        /// <summary>
        /// Records an authenticated client's SteamID on the server, and shares it when <see cref="ShareSteamIdsEnabled"/> is on.
        /// </summary>
        /// <param name="connection">The client.</param>
        /// <remarks>The client's address on this transport is the SteamID Steam authenticated for its link.</remarks>
        private void OnClientAuthenticated(Connection connection)
        {
            if (connection.Transport != this || !ulong.TryParse(connection.RemoteAddress, out ulong steamId) || steamId == UnsetSteamId)
                return;

            uint connectionId = connection.Id;
            RecordSteamId(connectionId, steamId);

            if (!ShareSteamIdsEnabled)
                return;

            CoreManager.ServerManager.SendMessageExcept(Channel.Reliable, new SteamIdNotice { ConnectionId = connectionId, SteamId = steamId }, connection);

            // The host's own client reads this peer's records directly.
            if (IsOwnClientConnection(connection))
                return;

            foreach (KeyValuePair<uint, ulong> steamIdByConnectionId in _steamIdsByConnectionId)
                connection.SendMessage(Channel.Reliable, new SteamIdNotice { ConnectionId = steamIdByConnectionId.Key, SteamId = steamIdByConnectionId.Value });
        }

        /// <summary>
        /// Forgets a departing client's SteamID on the server and tells the remaining clients.
        /// </summary>
        /// <param name="connection">The departing client.</param>
        private void OnClientDisconnecting(Connection connection)
        {
            if (!TryForgetSteamId(connection.Id))
                return;

            // The server's stop walks its clients too, and by then there is no one left to tell.
            if (ShareSteamIdsEnabled && CoreManager.TransportManager.IsServerStarted)
                CoreManager.ServerManager.SendMessageExcept(Channel.Reliable, new SteamIdNotice { ConnectionId = connection.Id, SteamId = UnsetSteamId }, connection);
        }

        /// <summary>
        /// Records this pure client's own SteamID once the server has assigned its Connection an id.
        /// </summary>
        /// <param name="localClientConnection">This peer's own client Connection.</param>
        /// <remarks>A host skips it, since its server recorded the same client when it authenticated.</remarks>
        private void OnLocalClientAuthenticated(Connection localClientConnection)
        {
            if (localClientConnection.Transport == this && !CoreManager.TransportManager.IsServerStarted)
                RecordSteamId(localClientConnection.Id, SteamUser.GetSteamID().m_SteamID);
        }

        /// <summary>
        /// Applies the server's word about a client's SteamID.
        /// </summary>
        /// <param name="messageContext">The context the notice arrived in.</param>
        /// <param name="steamIdNotice">The notice.</param>
        /// <remarks>Only the server may say who a client is; a notice from a client would let it claim another player's account.</remarks>
        private void OnSteamIdNoticeReceived(in MessageContext messageContext, SteamIdNotice steamIdNotice)
        {
            if (!messageContext.IsSenderServer || CoreManager.TransportManager.IsServerStarted)
                return;

            if (steamIdNotice.SteamId is UnsetSteamId)
                TryForgetSteamId(steamIdNotice.ConnectionId);
            else
                RecordSteamId(steamIdNotice.ConnectionId, steamIdNotice.SteamId);
        }

        /// <summary>
        /// Forgets every SteamID when this peer's session ends.
        /// </summary>
        /// <param name="connectionStateChange">The state change that was raised.</param>
        /// <remarks>A host whose client half drops keeps its server's records.</remarks>
        private void OnConnectionLocalStateChanged(ConnectionStateChange connectionStateChange)
        {
            if (connectionStateChange.IsRemoteStateChange || connectionStateChange.Connection.LocalState is not LocalConnectionState.Disconnected)
                return;

            if (connectionStateChange.Invoker is Invoker.Server || !CoreManager.TransportManager.IsServerStarted)
                ForgetAllSteamIds();
        }

        /// <summary>
        /// Records a client's SteamID and reports it, unless this peer already holds that value.
        /// </summary>
        /// <param name="connectionId">The client's <see cref="Connection.Id"/>.</param>
        /// <param name="steamId">The client's SteamID64.</param>
        private void RecordSteamId(uint connectionId, ulong steamId)
        {
            if (_steamIdsByConnectionId.TryGetValue(connectionId, out ulong recordedSteamId) && recordedSteamId == steamId)
                return;

            _steamIdsByConnectionId[connectionId] = steamId;
            _connectionIdsBySteamId[steamId] = connectionId;

            SteamIdChanged?.Invoke(connectionId, steamId);
        }

        /// <summary>
        /// Forgets a client's SteamID and reports that it left.
        /// </summary>
        /// <param name="connectionId">The client's <see cref="Connection.Id"/>.</param>
        /// <returns><see langword="true"/> when this peer held a SteamID for the client.</returns>
        private bool TryForgetSteamId(uint connectionId)
        {
            if (!_steamIdsByConnectionId.Remove(connectionId, out ulong steamId))
                return false;

            // One account connected twice is held under its latest connection, which an earlier one leaving must not remove.
            if (_connectionIdsBySteamId.TryGetValue(steamId, out uint recordedConnectionId) && recordedConnectionId == connectionId)
                _connectionIdsBySteamId.Remove(steamId);

            SteamIdChanged?.Invoke(connectionId, UnsetSteamId);

            return true;
        }

        /// <summary>
        /// Drops every SteamID this peer holds, without reporting them.
        /// </summary>
        private void ForgetAllSteamIds()
        {
            _steamIdsByConnectionId.Clear();
            _connectionIdsBySteamId.Clear();
        }

        /// <summary>
        /// Writes <see cref="_networkConditions"/> into Steam's simulation on every interface that is up.
        /// </summary>
        /// <remarks>Steam's simulation settings are global, so the two interfaces may each keep their own copy; both are set.</remarks>
        private void ApplyNetworkConditions()
        {
            NetworkConditions networkConditions = _networkConditions ?? default;

            ApplyNetworkConditions(SteamNetworkingInterface.Client, networkConditions);
            ApplyNetworkConditions(SteamNetworkingInterface.GameServer, networkConditions);
        }

        /// <summary>
        /// Queues a status change on the user's interface for the socket it concerns. Runs on the thread pumping Steam's callbacks.
        /// </summary>
        /// <param name="statusChange">The change Steam reported.</param>
        private void OnClientStatusChanged(SteamNetConnectionStatusChangedCallback_t statusChange)
        {
            HSteamListenSocket listenSocket = statusChange.m_info.m_hListenSocket;

            if (listenSocket == HSteamListenSocket.Invalid)
                ClientStatusChanges.Enqueue(statusChange);
            else if (ServerNetworkingInterface == SteamNetworkingInterface.Client && ServerSocket is not null && listenSocket == ServerSocket.ListenSocket)
                ServerStatusChanges.Enqueue(statusChange);
        }

        /// <summary>
        /// Queues a status change on the game server interface for the server. Runs on the thread pumping Steam's game server callbacks.
        /// </summary>
        /// <param name="statusChange">The change Steam reported.</param>
        private void OnGameServerStatusChanged(SteamNetConnectionStatusChangedCallback_t statusChange)
        {
            if (ServerNetworkingInterface == SteamNetworkingInterface.GameServer && ServerSocket is not null && statusChange.m_info.m_hListenSocket == ServerSocket.ListenSocket)
                ServerStatusChanges.Enqueue(statusChange);
        }

        /// <summary>
        /// Queues a fake IP grant on the user's interface for the server. Runs on the thread pumping Steam's callbacks.
        /// </summary>
        /// <param name="fakeIpResult">Steam's answer.</param>
        private void OnClientFakeIpResult(SteamNetworkingFakeIPResult_t fakeIpResult)
        {
            if (ServerNetworkingInterface == SteamNetworkingInterface.Client)
                FakeIpResults.Enqueue(fakeIpResult);
        }

        /// <summary>
        /// Queues a fake IP grant on the game server interface for the server. Runs on the thread pumping Steam's game server callbacks.
        /// </summary>
        /// <param name="fakeIpResult">Steam's answer.</param>
        private void OnGameServerFakeIpResult(SteamNetworkingFakeIPResult_t fakeIpResult)
        {
            if (ServerNetworkingInterface == SteamNetworkingInterface.GameServer)
                FakeIpResults.Enqueue(fakeIpResult);
        }

        /// <summary>
        /// Queues a relay network status change on the user's interface for the client. Runs on the thread pumping Steam's callbacks.
        /// </summary>
        /// <param name="relayNetworkStatus">The status Steam reported.</param>
        private void OnRelayNetworkStatusChanged(SteamRelayNetworkStatus_t relayNetworkStatus) => RelayNetworkStatuses.Enqueue(relayNetworkStatus);

        /// <summary>
        /// Writes conditions into one interface's simulation settings, if that interface is up.
        /// </summary>
        /// <param name="steamNetworkingInterface">The interface.</param>
        /// <param name="networkConditions">The conditions; the default value simulates nothing.</param>
        private static void ApplyNetworkConditions(SteamNetworkingInterface steamNetworkingInterface, NetworkConditions networkConditions)
        {
            if (steamNetworkingInterface.IsShutDown())
                return;

            float jitterMilliseconds = networkConditions.JitterMilliseconds;

            steamNetworkingInterface.SetGlobalInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketLag_Send, (int)networkConditions.LatencyMilliseconds);
            steamNetworkingInterface.SetGlobalFloat(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketJitter_Send_Avg, jitterMilliseconds / 2f);
            steamNetworkingInterface.SetGlobalFloat(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketJitter_Send_Max, jitterMilliseconds);
            steamNetworkingInterface.SetGlobalFloat(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketJitter_Send_Pct, jitterMilliseconds > 0f ? 100f : 0f);
            steamNetworkingInterface.SetGlobalFloat(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketLoss_Send, networkConditions.PacketLossChance * 100f);
            steamNetworkingInterface.SetGlobalFloat(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketReorder_Send, networkConditions.OutOfOrderChance * 100f);
            steamNetworkingInterface.SetGlobalInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketReorder_Time, ReorderDelayMilliseconds);
            steamNetworkingInterface.SetGlobalFloat(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketDup_Send, networkConditions.DuplicateChance * 100f);
        }

        /// <summary>
        /// Builds one integer Steam option.
        /// </summary>
        /// <param name="configValue">The option to set.</param>
        /// <param name="value">Its value.</param>
        /// <returns>The option.</returns>
        private static SteamNetworkingConfigValue_t CreateInt32Option(ESteamNetworkingConfigValue configValue, int value)
        {
            return new()
            {
                m_eValue = configValue,
                m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                m_val = new() { m_int32 = value }
            };
        }
    }
}
#endif
