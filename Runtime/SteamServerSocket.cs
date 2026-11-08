// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CodeBoost.Logging;
using CodeBoost.Performance;
using Nucleus.Connections;
using Nucleus.Packets;
using Nucleus.Transports;
using Nucleus.Transports.Sockets;
using Steamworks;

namespace Nucleus.Integrations.SteamworksNet
{
    /// <summary>
    /// The server half of <see cref="SteamSockets"/>: listens on Steam in the transport's <see cref="SteamSockets.Addressing"/> and treats
    /// every client Steam admits, and this peer's own client paired in process, as an ordinary remote Connection.
    /// </summary>
    /// <remarks>
    /// Runs on the game server interface when <see cref="SteamSockets.GameServerEnabled"/> is on, otherwise on the user's. Every client is received through one poll group, so one call
    /// drains them all. This peer's own client is keyed by <see cref="HSteamNetConnection.Invalid"/>, and its roster entry is the client
    /// socket's own Connection, which this socket only borrows.
    /// </remarks>
    public class SteamServerSocket : CommonSocket
    {
        /// <inheritdoc/>
        public override bool IsServer { get; protected set; } = true;
        /// <inheritdoc/>
        public override bool IsEmulated { get; protected set; } = false;
        /// <summary>
        /// The socket the server listens on, or <see cref="HSteamListenSocket.Invalid"/> when the server is not running.
        /// </summary>
        internal HSteamListenSocket ListenSocket { get; private set; } = HSteamListenSocket.Invalid;
        /// <summary>
        /// True while the server is listening.
        /// </summary>
        internal bool IsListening => ListenSocket != HSteamListenSocket.Invalid;
        /// <summary>
        /// The transport this socket belongs to, which holds the settings it reads.
        /// </summary>
        private SteamSockets _steamSockets;
        /// <summary>
        /// Remote clients by the handle of the Steam connection carrying them.
        /// </summary>
        /// <remarks>The host's own client paired in process is keyed by <see cref="HSteamNetConnection.Invalid"/>.</remarks>
        private readonly Dictionary<HSteamNetConnection, Connection> _connectionsBySteamConnection = new();
        /// <summary>
        /// The Steam connection carrying each remote client, so an outbound packet can be addressed.
        /// </summary>
        private readonly Dictionary<Connection, HSteamNetConnection> _steamConnectionsByConnection = new();
        /// <summary>
        /// The pieces received so far of each client's reliable packet that arrives split, by the client's Steam connection.
        /// </summary>
        /// <remarks>Each segment's array is rented from <see cref="TrackedArrayPool{T0}"/> and becomes the packet's payload once it is whole.</remarks>
        private readonly Dictionary<HSteamNetConnection, ArraySegment<byte>> _splitPackets = new();
        /// <summary>
        /// Admitted clients whose links Steam has closed, and whose drop is due on the next receive.
        /// </summary>
        /// <remarks>
        /// Steam still hands over what arrived before a close until the connection is closed locally. Each client stays mapped, with its
        /// handle open, for one pass, so the messages read in the pass that saw the close reach the game before it hears the client has
        /// gone. A list rather than a set, because a departure handler can drop another entry while the list is drained.
        /// </remarks>
        private readonly List<HSteamNetConnection> _departedSteamConnections = new();
        /// <summary>
        /// The links a flush found Steam refusing reliable data on, closed once the flush has finished.
        /// </summary>
        private readonly List<HSteamNetConnection> _refusedSteamConnections = new();
        /// <summary>
        /// The messages built this frame, handed to the server's interface at the next flush.
        /// </summary>
        private readonly SteamSendBatch _sendBatch = new();
        /// <summary>
        /// Message pointers Steam fills on each receive.
        /// </summary>
        private readonly IntPtr[] _receivedMessagePointers = new IntPtr[SteamMessages.MaximumMessagesPerReceive];
        /// <summary>
        /// Gives out the per-socket ids remote clients are known by, and takes back a departed client's id after a delay.
        /// </summary>
        /// <remarks>Runs no timer of its own: the TransportManager sweeps it once a frame through <see cref="SweepIdPools"/>, on the loop thread that rents and returns ids.</remarks>
        private readonly DelayedUIntPool _idBySocketPool = new(isOwnTimerEnabled: false);
        /// <summary>
        /// The poll group every admitted client is received through, so one call drains them all.
        /// </summary>
        private HSteamNetPollGroup _pollGroup = HSteamNetPollGroup.Invalid;
        /// <summary>
        /// The socket id this peer's own client was admitted with, or <see cref="Connection.UnsetId"/> while it is not paired.
        /// </summary>
        /// <remarks>Kept apart, because its roster entry is the client socket's Connection, which was not initialized with it.</remarks>
        private uint _loopbackIdBySocket = Connection.UnsetId;
        /// <summary>
        /// The interface the server runs on, as log lines name it.
        /// </summary>
        private string InterfaceName => _steamSockets.GameServerEnabled ? "game server" : "user";
        /// <summary>
        /// How long a departed client's socket id is held before it is given out again, in minutes.
        /// </summary>
        /// <remarks>Long enough that nothing still naming the departed client can be mistaken for the client given its id next.</remarks>
        private const double IdReturnMinutes = 5d;
        /// <summary>
        /// The weight each frame's sample carries in <see cref="SteamSockets.ServerReceiveMicroseconds"/>.
        /// </summary>
        private const double ReceiveSmoothing = 0.05;
        /// <summary>
        /// The fake port a <see cref="SteamworksNetAddressing.FakeIp"/> server listens on: the first and only one it asks for.
        /// </summary>
        private const int FakePortIndex = 0;

        /// <inheritdoc/>
        public override void Initialize(Transport transport)
        {
            base.Initialize(transport);

            InitializeConnection();

            _steamSockets = (SteamSockets)transport;
            LocalConnectionStateChanged += OnLocalConnectionStateChanged;

            _idBySocketPool.OnRent();
            _idBySocketPool.Initialize(IdReturnMinutes, firstRentedValue: Connection.UnsetId + 1);
        }

        /// <summary>
        /// Hands the frame's staged messages to Steam, and closes any link Steam refused reliable data on.
        /// </summary>
        /// <remarks>A reliable message Steam refuses, being larger than the whole send buffer, closes its link, because the peers can no longer agree.</remarks>
        internal void FlushStagedMessages()
        {
            _sendBatch.Flush(_steamSockets);

            List<SteamSendBatch.RefusedSend> refusedSends = _sendBatch.RefusedReliableSends;

            if (refusedSends.Count == 0)
                return;

            foreach (SteamSendBatch.RefusedSend refusedSend in refusedSends)
            {
                HSteamNetConnection steamConnection = refusedSend.SteamConnection;

                // A link this socket closed after staging, or Steam closed and is due to drop, refuses its leftovers; that is not news.
                bool isLinkOpen = _connectionsBySteamConnection.ContainsKey(steamConnection) && !_departedSteamConnections.Contains(steamConnection);

                if (!isLinkOpen || _refusedSteamConnections.Contains(steamConnection))
                    continue;

                _steamSockets.RefusedReliableSendCount++;
                _refusedSteamConnections.Add(steamConnection);

                Logger<SteamServerSocket>.LogError($"Steam refused a reliable message of [{refusedSend.PayloadBytes}] bytes: [{refusedSend.Result}]. The link is closed, because the peers can no longer agree. Raise [{nameof(SteamSockets.SendBufferSizeBytes)}] above [{_sendBatch.SendBufferBytes}] bytes if it was larger than that.");
            }

            refusedSends.Clear();

            // Closed only now, since a close flushes again; taken off the end, since that flush can add to the list.
            while (_refusedSteamConnections.Count > 0)
            {
                int lastIndex = _refusedSteamConnections.Count - 1;
                HSteamNetConnection refusedSteamConnection = _refusedSteamConnections[lastIndex];
                _refusedSteamConnections.RemoveAt(lastIndex);

                if (_connectionsBySteamConnection.TryGetValue(refusedSteamConnection, out Connection connection))
                    DisconnectRemoteClient(connection, isLingering: false);
            }
        }

        /// <summary>
        /// Pairs this peer's own client with the running server, adopting the client socket's Connection as its roster entry.
        /// </summary>
        /// <param name="steamId">This peer's SteamID, which is the paired client's address.</param>
        /// <returns><see langword="true"/> when the engine admitted the client.</returns>
        /// <remarks>The server sees its own client arrive as any other, so authentication and spawning treat it alike.</remarks>
        internal bool TryPairLoopbackClient(ulong steamId) => TryAdmitClient(HSteamNetConnection.Invalid, steamId);

        /// <summary>
        /// Tells the engine this peer's own client has left the server, if it was paired.
        /// </summary>
        /// <remarks>Called while the client link's Connection still names its roster entry, before the client socket walks its link down.</remarks>
        internal void UnpairLoopbackClient()
        {
            if (!_connectionsBySteamConnection.Remove(HSteamNetConnection.Invalid, out Connection loopbackConnection))
                return;

            _steamConnectionsByConnection.Remove(loopbackConnection);
            ClearCollection(IncomingPackets);

            NotifyRemoteDisconnected(loopbackConnection, TakeLoopbackIdBySocket());
        }

        /// <summary>
        /// Takes a packet this peer's own client sent, for the next receive, as a packet from its roster entry.
        /// </summary>
        /// <param name="outgoingPacket">The client's packet. Its payload is copied, so the caller hands it back to the engine.</param>
        /// <returns><see langword="true"/> when the client is paired and the packet was taken.</returns>
        internal bool TryEnqueueLoopbackPacket(OutgoingPacket outgoingPacket)
        {
            if (!_connectionsBySteamConnection.TryGetValue(HSteamNetConnection.Invalid, out Connection loopbackConnection))
                return false;

            IncomingPackets.Enqueue(new(CopyIntoOwnedPayload(outgoingPacket.Payload, out _), outgoingPacket.Channel, loopbackConnection));

            return true;
        }

        /// <summary>
        /// Rents a fresh Connection for this socket unless the server is running.
        /// </summary>
        /// <remarks>
        /// A pure client attributes inbound packets to this socket's Connection, so a new client link needs it clean too; a running server
        /// keeps its own, which still describes the server.
        /// </remarks>
        internal void RecycleConnectionUnlessListening()
        {
            if (!IsListening)
                RecycleConnection();
        }

        /// <summary>
        /// Reads Steam's live status of the link carrying a remote client.
        /// </summary>
        /// <param name="connection">The remote client.</param>
        /// <param name="linkStatus">The status, when there is a Steam link to read.</param>
        /// <returns><see langword="true"/> when the client rides a Steam link; this peer's own client has none.</returns>
        internal bool TryGetLinkStatus(Connection connection, out SteamLinkStatus linkStatus)
        {
            linkStatus = default;

            return _steamConnectionsByConnection.TryGetValue(connection, out HSteamNetConnection steamConnection) && SteamSockets.TryReadLinkStatus(_steamSockets.ServerNetworkingInterface, steamConnection, out linkStatus);
        }

        /// <inheritdoc/>
        protected override void SweepIdPools() => _idBySocketPool.Sweep();

        /// <inheritdoc/>
        protected override Task<ConnectionStateChangeResult> ConnectAsync() => GetResultTask(Start());

        /// <inheritdoc/>
        protected override Task<ConnectionStateChangeResult> DisconnectAsync() => GetResultTask(Stop());

        /// <inheritdoc/>
        protected override Task<ConnectionStateChangeResult> DisconnectRemoteClientAsync(Connection connection) => GetResultTask(DisconnectRemoteClient(connection, isLingering: false));

        /// <inheritdoc/>
        /// <remarks>
        /// Steam's linger keeps the connection open after it is closed until the reliable data already queued on it has been
        /// acknowledged, which is exactly what a kick's reason needs. The engine is told the client is gone at once.
        /// </remarks>
        protected override Task<ConnectionStateChangeResult> DisconnectRemoteClientAfterDeliveryAsync(Connection connection) => GetResultTask(DisconnectRemoteClient(connection, isLingering: true));

        /// <inheritdoc/>
        protected override void SendPacket(OutgoingPacket outgoingPacket)
        {
            if (outgoingPacket.Receiver is null || !_steamConnectionsByConnection.TryGetValue(outgoingPacket.Receiver, out HSteamNetConnection steamConnection) || _departedSteamConnections.Contains(steamConnection))
            {
                Nucleus.Managers.Transports.TransportManager.PacketFailed(outgoingPacket);

                return;
            }

            if (steamConnection == HSteamNetConnection.Invalid)
            {
                _steamSockets.ClientSocket.EnqueueLoopbackPacket(new(CopyIntoOwnedPayload(outgoingPacket.Payload, out _), outgoingPacket.Channel, Connection));
                Nucleus.Managers.Transports.TransportManager.PacketOffloaded(outgoingPacket);

                return;
            }

            _sendBatch.Stage(steamConnection, outgoingPacket);
        }

        /// <inheritdoc/>
        protected override void ReceivePackets(ref List<IncomingPacket> receivedPackets)
        {
            // Held, because a departure handler can shut the transport down, which returns this socket and clears its reference.
            SteamSockets steamSockets = _steamSockets;
            long startTimestamp = Stopwatch.GetTimestamp();

            ReceiveServerPackets(receivedPackets);

            double sampleMicroseconds = (Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000d / Stopwatch.Frequency;
            steamSockets.ServerReceiveMicroseconds += (sampleMicroseconds - steamSockets.ServerReceiveMicroseconds) * ReceiveSmoothing;
        }

        /// <summary>
        /// Opens the listen socket in <see cref="SteamSockets.Addressing"/> on the interface <see cref="SteamSockets.GameServerEnabled"/> picks.
        /// </summary>
        /// <returns>The result of the start.</returns>
        private ConnectionStateChangeResult Start()
        {
            if (IsListening)
                return ConnectionStateChangeResult.AlreadyInState;

            SteamNetworkingInterface steamNetworkingInterface = SteamNetworkingInterface.Get(_steamSockets.GameServerEnabled);

            if (!_steamSockets.TryPrepareSteam(steamNetworkingInterface))
                return ConnectionStateChangeResult.UnspecifiedError;

            _steamSockets.ServerNetworkingInterface = steamNetworkingInterface;
            _sendBatch.NetworkingInterface = steamNetworkingInterface;
            _sendBatch.SendBufferBytes = _steamSockets.GetSendBufferBytes();

            // A fresh Connection, so the new server's bookkeeping starts clean rather than carrying the last session's ticks.
            RecycleConnection();

            ListenSocket = CreateListenSocket(steamNetworkingInterface);
            _pollGroup = steamNetworkingInterface.CreatePollGroup();

            if (!IsListening || _pollGroup == HSteamNetPollGroup.Invalid)
            {
                Logger<SteamServerSocket>.LogError($"Steam could not open a [{_steamSockets.Addressing}] listen socket on the [{InterfaceName}] interface. A game server interface needs GameServer.Init first.");

                ReleaseListenSocket();
                SteamSockets.ChangeLocalState(TransportManager, Invoker.Server, LocalConnectionState.Disconnected, Connection);

                return ConnectionStateChangeResult.UnspecifiedError;
            }

            if (TryDriveLocalStateTo(Invoker.Server, LocalConnectionState.Connected))
            {
                Logger<SteamServerSocket>.LogInformation($"Steam server listening by [{_steamSockets.Addressing}] on the [{InterfaceName}] interface.");

                return ConnectionStateChangeResult.Success;
            }

            Stop();

            return ConnectionStateChangeResult.UnspecifiedError;
        }

        /// <summary>
        /// Opens the listen socket for <see cref="SteamSockets.Addressing"/>.
        /// </summary>
        /// <param name="steamNetworkingInterface">The interface to open it on.</param>
        /// <returns>The socket, or <see cref="HSteamListenSocket.Invalid"/> when Steam refused.</returns>
        private HSteamListenSocket CreateListenSocket(SteamNetworkingInterface steamNetworkingInterface)
        {
            SteamNetworkingConfigValue_t[] options = _steamSockets.CreateOptions();

            switch (_steamSockets.Addressing)
            {
                case SteamworksNetAddressing.IpAddress:
                {
                    if (!TryCreateBindAddress(out SteamNetworkingIPAddr localAddress))
                        return HSteamListenSocket.Invalid;

                    return steamNetworkingInterface.CreateListenSocketIP(ref localAddress, options);
                }
                case SteamworksNetAddressing.FakeIp:
                    RequestFakeIp(steamNetworkingInterface);

                    return steamNetworkingInterface.CreateListenSocketP2PFakeIP(FakePortIndex, options);
                case SteamworksNetAddressing.SteamId:
                default:
                    return steamNetworkingInterface.CreateListenSocketP2P(_steamSockets.VirtualPort, options);
            }
        }

        /// <summary>
        /// Asks Steam for a fake address, or adopts the one this process was already granted.
        /// </summary>
        /// <param name="steamNetworkingInterface">The interface the server runs on.</param>
        /// <remarks>
        /// Steam grants one fake address per interface for the life of the process and refuses a second request, so a restarted server
        /// reads the earlier grant instead. A new grant arrives later as a callback.
        /// </remarks>
        private void RequestFakeIp(SteamNetworkingInterface steamNetworkingInterface)
        {
            if (steamNetworkingInterface.BeginAsyncRequestFakeIP(portCount: 1))
            {
                Logger<SteamServerSocket>.LogInformation($"Steam accepted the fake address request on the [{InterfaceName}] interface; the grant follows.");

                return;
            }

            steamNetworkingInterface.GetFakeIP(FakePortIndex, out SteamNetworkingFakeIPResult_t fakeIpResult);

            if (fakeIpResult.m_eResult is EResult.k_EResultOK)
                ApplyFakeIpResult(fakeIpResult);
            else
                Logger<SteamServerSocket>.LogError($"Steam refused the fake address request on the [{InterfaceName}] interface, and holds no earlier grant: [{fakeIpResult.m_eResult}]. Clients cannot reach this server by one.");
        }

        /// <summary>
        /// Builds the address an <see cref="SteamworksNetAddressing.IpAddress"/> server binds, from <see cref="Configuration.BindAddress"/> and <see cref="Configuration.Port"/>.
        /// </summary>
        /// <param name="localAddress">The address to bind.</param>
        /// <returns><see langword="true"/> when the bind address could be read.</returns>
        /// <remarks>The server always listens on <see cref="Configuration.Port"/>, even when the bind address names a port of its own.</remarks>
        private bool TryCreateBindAddress(out SteamNetworkingIPAddr localAddress)
        {
            Configuration configuration = _steamSockets.Configuration;

            if (!SteamSockets.TryParseIpAddress(configuration.BindAddress, configuration.Port, out localAddress))
            {
                Logger<SteamServerSocket>.LogError($"Steam could not read the bind address [{configuration.BindAddress}].");

                return false;
            }

            localAddress.m_port = configuration.Port;

            return true;
        }

        /// <summary>
        /// Drops every client, closes the listen socket, and walks the server's state down.
        /// </summary>
        /// <returns>The result of the stop.</returns>
        private ConnectionStateChangeResult Stop()
        {
            if (!IsListening)
                return ConnectionStateChangeResult.AlreadyInState;

            // A host's own client loses its server with it, as it would over a socket.
            if (_connectionsBySteamConnection.ContainsKey(HSteamNetConnection.Invalid))
                _steamSockets.ClientSocket.Stop();

            List<Connection> departingConnections = new(_steamConnectionsByConnection.Keys);

            foreach (Connection departingConnection in departingConnections)
                DisconnectRemoteClient(departingConnection, isLingering: false);

            ReleaseListenSocket();
            SteamSockets.ChangeLocalStateToDisconnected(TransportManager, Invoker.Server, Connection);

            return ConnectionStateChangeResult.Success;
        }

        /// <summary>
        /// Closes one remote client's link and tells the engine it is gone.
        /// </summary>
        /// <param name="connection">The client to drop.</param>
        /// <param name="isLingering">True to keep the Steam connection open until its queued reliable data is acknowledged.</param>
        /// <returns>The result of the disconnect.</returns>
        private ConnectionStateChangeResult DisconnectRemoteClient(Connection connection, bool isLingering)
        {
            if (!IsListening)
                return ConnectionStateChangeResult.InvalidSocketState;

            if (connection is null || !_steamConnectionsByConnection.Remove(connection, out HSteamNetConnection steamConnection))
                return ConnectionStateChangeResult.InvalidConnection;

            _connectionsBySteamConnection.Remove(steamConnection);
            _departedSteamConnections.Remove(steamConnection);

            if (steamConnection == HSteamNetConnection.Invalid)
            {
                ClearCollection(IncomingPackets);

                // The host's own client is told its server dropped it, as a remote client would be, once what it was already sent has dispatched.
                _steamSockets.ClientSocket.OnLoopbackDropped();

                return NotifyRemoteDisconnected(connection, TakeLoopbackIdBySocket());
            }

            SteamNetworkingInterface steamNetworkingInterface = _steamSockets.ServerNetworkingInterface;

            if (!steamNetworkingInterface.IsShutDown())
            {
                /* A kick's reason was staged this frame; it has to reach Steam before the close, or the linger has nothing to deliver. Messages
                 * waiting for room go too, since a lingering close drains Steam's queue anyway, and any it refuses die with the link. */
                if (isLingering)
                    _sendBatch.ReleaseAllQueuedReliableSends(steamConnection);

                FlushStagedMessages();
                steamNetworkingInterface.CloseConnection(steamConnection, SteamMessages.ApplicationCloseReason, "Disconnected by the server.", isLingering);
            }

            _sendBatch.Discard(steamConnection);
            SteamMessages.DiscardSplitPacket(_splitPackets, steamConnection);

            return NotifyRemoteDisconnected(connection, connection.IdBySocket);
        }

        /// <summary>
        /// Drops the clients Steam closed during the previous pass, applies the server's queued Steam status changes, then drains every
        /// admitted client's messages.
        /// </summary>
        /// <param name="receivedPackets">The list to append received packets to.</param>
        private void ReceiveServerPackets(List<IncomingPacket> receivedPackets)
        {
            SteamSockets steamSockets = _steamSockets;

            // Taken off the end before each drop, so a departure handler that drops another entry leaves the drain intact.
            while (_departedSteamConnections.Count > 0)
            {
                int lastIndex = _departedSteamConnections.Count - 1;
                HSteamNetConnection departedSteamConnection = _departedSteamConnections[lastIndex];
                _departedSteamConnections.RemoveAt(lastIndex);

                DropDepartedClient(departedSteamConnection);
            }

            // A departure handler that shut the transport down has returned this socket.
            if (TransportManager is null)
                return;

            while (steamSockets.FakeIpResults.TryDequeue(out SteamNetworkingFakeIPResult_t fakeIpResult))
                ApplyFakeIpResult(fakeIpResult);

            while (TransportManager is not null && steamSockets.ServerStatusChanges.TryDequeue(out SteamNetConnectionStatusChangedCallback_t statusChange))
                ApplyStatusChange(statusChange);

            if (TransportManager is null)
                return;

            while (IncomingPackets.TryDequeue(out IncomingPacket incomingPacket))
                receivedPackets.Add(incomingPacket);

            if (_pollGroup == HSteamNetPollGroup.Invalid)
                return;

            SteamNetworkingInterface steamNetworkingInterface = steamSockets.ServerNetworkingInterface;

            /* A game that shut Steam down while the server ran has taken every link with it, and Steamworks.NET throws on any further
             * call, so the server stops here, forgetting its handles, rather than receiving and sending into a dead interface. */
            if (steamNetworkingInterface.IsShutDown())
            {
                Logger<SteamServerSocket>.LogWarning("Steam was shut down while the server was running, so the server stopped.");
                Stop();

                return;
            }

            int messageCount;

            do
            {
                messageCount = steamNetworkingInterface.ReceiveMessagesOnPollGroup(_pollGroup, _receivedMessagePointers, SteamMessages.MaximumMessagesPerReceive);

                for (int i = 0; i < messageCount; i++)
                {
                    IntPtr messagePointer = _receivedMessagePointers[i];
                    HSteamNetConnection steamConnection = new((uint)Marshal.ReadInt32(messagePointer, SteamMessages.ConnectionOffset));

                    // A client dropped earlier this frame can still have messages in the group; they belong to nobody now.
                    if (!_connectionsBySteamConnection.TryGetValue(steamConnection, out Connection sender))
                        SteamNetworkingMessage_t.Release(messagePointer);
                    else if (SteamMessages.TryCreatePacket(messagePointer, sender, steamConnection, _splitPackets, steamSockets.MaximumClientPacketBytes, out IncomingPacket incomingPacket, out bool isOverLimit))
                        receivedPackets.Add(incomingPacket);
                    else if (isOverLimit)
                        DisconnectOverLimitClient(sender, steamSockets.MaximumClientPacketBytes);
                }
            }
            // A departure handler raised by a drop above can stop the server, which destroys the poll group.
            while (messageCount == SteamMessages.MaximumMessagesPerReceive && _pollGroup != HSteamNetPollGroup.Invalid);
        }

        /// <summary>
        /// Accepts, admits or drops a client as Steam reported it.
        /// </summary>
        /// <param name="statusChange">The change Steam reported.</param>
        private void ApplyStatusChange(SteamNetConnectionStatusChangedCallback_t statusChange)
        {
            HSteamNetConnection steamConnection = statusChange.m_hConn;
            SteamNetworkingInterface steamNetworkingInterface = _steamSockets.ServerNetworkingInterface;

            switch (statusChange.m_info.m_eState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    AcceptOrRefuse(steamConnection, statusChange.m_info.m_identityRemote.GetSteamID64());
                    break;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                {
                    ulong steamId = statusChange.m_info.m_identityRemote.GetSteamID64();

                    if (!TryAdmitClient(steamConnection, steamId))
                        steamNetworkingInterface.CloseConnection(steamConnection, SteamMessages.ApplicationCloseReason, "Refused by the server.", isLingering: false);

                    break;
                }
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                {
                    SteamSockets.LogClosed(statusChange.m_info, "A client");

                    // Steam keeps a closed connection's handle until it is closed locally too; an admitted client keeps it one pass longer.
                    if (!_connectionsBySteamConnection.ContainsKey(steamConnection))
                        steamNetworkingInterface.CloseConnection(steamConnection, SteamMessages.ApplicationCloseReason, debug: null, isLingering: false);
                    else if (!_departedSteamConnections.Contains(steamConnection))
                        _departedSteamConnections.Add(steamConnection);

                    break;
                }
            }
        }

        /// <summary>
        /// Records and logs the fake address Steam granted this server.
        /// </summary>
        /// <param name="fakeIpResult">Steam's answer.</param>
        private void ApplyFakeIpResult(SteamNetworkingFakeIPResult_t fakeIpResult)
        {
            if (fakeIpResult.m_eResult is not EResult.k_EResultOK)
            {
                Logger<SteamServerSocket>.LogError($"Steam did not grant a fake address: [{fakeIpResult.m_eResult}]. Clients cannot reach this server by one.");

                return;
            }

            uint ip = fakeIpResult.m_unIP;
            _steamSockets.ServerFakeAddress = $"{ip >> 24}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}:{fakeIpResult.m_unPorts[FakePortIndex]}";

            Logger<SteamServerSocket>.LogInformation($"Steam granted this server the fake address [{_steamSockets.ServerFakeAddress}].");
        }

        /// <summary>
        /// Accepts a client asking to connect, unless the server is full or <see cref="SteamSockets.ConnectingClientFilter"/> refuses it.
        /// </summary>
        /// <param name="steamConnection">The Steam connection asking.</param>
        /// <param name="steamId">Who is asking, or <see cref="SteamSockets.UnsetSteamId"/> when it has no SteamID.</param>
        private void AcceptOrRefuse(HSteamNetConnection steamConnection, ulong steamId)
        {
            SteamNetworkingInterface steamNetworkingInterface = _steamSockets.ServerNetworkingInterface;
            uint maximumConnections = _steamSockets.ServerConfiguration.MaximumConnections;

            if (maximumConnections is not ServerConfiguration.UnsetMaximumConnections && _steamConnectionsByConnection.Count >= maximumConnections)
            {
                steamNetworkingInterface.CloseConnection(steamConnection, SteamMessages.ApplicationCloseReason, "The server is full.", isLingering: false);

                return;
            }

            SteamSockets.ConnectingClientFilterHandler connectingClientFilter = _steamSockets.ConnectingClientFilter;

            if (connectingClientFilter is not null && !connectingClientFilter(steamId))
            {
                steamNetworkingInterface.CloseConnection(steamConnection, SteamMessages.ApplicationCloseReason, "Refused by the server.", isLingering: false);

                return;
            }

            EResult result = steamNetworkingInterface.AcceptConnection(steamConnection);

            if (result is EResult.k_EResultOK)
                SteamSockets.ConfigureLanes(steamNetworkingInterface, steamConnection);
            else
                Logger<SteamServerSocket>.LogError($"Steam could not accept a client: [{result}].");
        }

        /// <summary>
        /// Registers a client with the engine and starts receiving its messages.
        /// </summary>
        /// <param name="steamConnection">The Steam connection carrying it, or <see cref="HSteamNetConnection.Invalid"/> for this peer's own client.</param>
        /// <param name="steamId">Who it is, as Steam authenticated it.</param>
        /// <returns><see langword="true"/> when the engine admitted the client.</returns>
        /// <remarks>
        /// This peer's own client is admitted on the client socket's Connection rather than one rented here, so the engine can tell it is
        /// the host's own; the address the server observed is written onto it. Both maps are filled before the engine hears of the client,
        /// because admitting it runs its authentication synchronously, and the reply that admits it is sent from there.
        /// </remarks>
        private bool TryAdmitClient(HSteamNetConnection steamConnection, ulong steamId)
        {
            if (!_idBySocketPool.EnsureRent(out uint idBySocket))
            {
                Logger<SteamServerSocket>.LogWarning($"Steam client [{steamId}] was refused: the server has no socket id to give it.");

                return false;
            }

            bool isLoopback = steamConnection == HSteamNetConnection.Invalid;
            Connection connection;

            if (isLoopback)
            {
                connection = _steamSockets.ClientSocket.Connection;
                connection.SetRemoteAddress(steamId.ToString());
                _loopbackIdBySocket = idBySocket;
            }
            else
            {
                connection = ResettableObjectPool<Connection>.Rent();
                connection.Initialize(idBySocket, isEmulated: false, Transport, remoteAddress: steamId.ToString());
            }

            _connectionsBySteamConnection[steamConnection] = connection;
            _steamConnectionsByConnection[connection] = steamConnection;

            ConnectionStateChangeResult connectionStateChangeResult = TransportManager.SetRemoteConnectionStateAsConnected(idBySocket, connection);

            if (connectionStateChangeResult is ConnectionStateChangeResult.Success)
            {
                // Messages that arrived before admission move into the group with the connection, so none are lost.
                if (!isLoopback)
                    _steamSockets.ServerNetworkingInterface.SetConnectionPollGroup(steamConnection, _pollGroup);

                return true;
            }

            Logger<SteamServerSocket>.LogError($"Steam client [{steamId}] could not be registered: [{connectionStateChangeResult}].");

            _connectionsBySteamConnection.Remove(steamConnection);
            _steamConnectionsByConnection.Remove(connection);
            _idBySocketPool.ReturnUnsafe(idBySocket);

            // Borrowed from the client socket, which alone returns it to the pool, so it is released rather than pooled.
            if (isLoopback)
            {
                _loopbackIdBySocket = Connection.UnsetId;
                connection.ReleaseServerRole();
            }
            else
            {
                ResettableObjectPool<Connection>.Return(connection);
            }

            return false;
        }

        /// <summary>
        /// Takes the socket id this peer's own client was admitted with.
        /// </summary>
        /// <returns>The id, or <see cref="Connection.UnsetId"/> when it was not paired.</returns>
        private uint TakeLoopbackIdBySocket()
        {
            uint idBySocket = _loopbackIdBySocket;
            _loopbackIdBySocket = Connection.UnsetId;

            return idBySocket;
        }

        /// <summary>
        /// Closes the listen socket and poll group.
        /// </summary>
        private void ReleaseListenSocket()
        {
            SteamNetworkingInterface steamNetworkingInterface = _steamSockets.ServerNetworkingInterface;

            if (!steamNetworkingInterface.IsShutDown())
            {
                if (IsListening)
                    steamNetworkingInterface.CloseListenSocket(ListenSocket);

                if (_pollGroup != HSteamNetPollGroup.Invalid)
                    steamNetworkingInterface.DestroyPollGroup(_pollGroup);
            }

            ListenSocket = HSteamListenSocket.Invalid;
            _pollGroup = HSteamNetPollGroup.Invalid;

            ClearCollection(IncomingPackets);
        }

        /// <summary>
        /// Tells the engine a remote client has gone, and returns its socket id once the engine has let it go.
        /// </summary>
        /// <param name="connection">The departed client.</param>
        /// <param name="idBySocket">The id the client was admitted with, read before the engine may recycle the Connection.</param>
        /// <returns>The result of the state change.</returns>
        /// <remarks>
        /// The engine refuses a client that was never registered or has already left, so no id goes back twice. A departure handler can
        /// shut the transport down, which returns this socket and its id pool, so the id goes back only while the socket is still in use.
        /// </remarks>
        private ConnectionStateChangeResult NotifyRemoteDisconnected(Connection connection, uint idBySocket)
        {
            ConnectionStateChangeResult connectionStateChangeResult = SetRemoteConnectionStateAsDisconnected(connection);

            if (connectionStateChangeResult is ConnectionStateChangeResult.Success && TransportManager is not null)
                _idBySocketPool.ReturnUnsafe(idBySocket);

            return connectionStateChangeResult;
        }

        /// <summary>
        /// Closes the handle of a client whose link Steam closed during the previous pass, and tells the engine it is gone.
        /// </summary>
        /// <param name="steamConnection">The departed client's Steam connection.</param>
        /// <remarks>Steam may have shut down in the pass between, which took the handle with it, so only the engine is told then.</remarks>
        private void DropDepartedClient(HSteamNetConnection steamConnection)
        {
            SteamNetworkingInterface steamNetworkingInterface = _steamSockets.ServerNetworkingInterface;

            if (!steamNetworkingInterface.IsShutDown())
                steamNetworkingInterface.CloseConnection(steamConnection, SteamMessages.ApplicationCloseReason, debug: null, isLingering: false);

            _sendBatch.Discard(steamConnection);
            SteamMessages.DiscardSplitPacket(_splitPackets, steamConnection);

            if (!_connectionsBySteamConnection.Remove(steamConnection, out Connection connection))
                return;

            _steamConnectionsByConnection.Remove(connection);
            NotifyRemoteDisconnected(connection, connection.IdBySocket);
        }

        /// <summary>
        /// Disconnects a client that sent a packet larger than <see cref="SteamSockets.MaximumClientPacketBytes"/>.
        /// </summary>
        /// <param name="connection">The client.</param>
        /// <param name="maximumClientPacketBytes">The limit it went past.</param>
        /// <remarks>Its messages still waiting in this receive pass are released unread, since it is no longer admitted.</remarks>
        private void DisconnectOverLimitClient(Connection connection, uint maximumClientPacketBytes)
        {
            Logger<SteamServerSocket>.LogWarning($"Steam client [{connection.RemoteAddress}] sent a packet larger than [{maximumClientPacketBytes}] bytes, so it was disconnected.");
            DisconnectRemoteClient(connection, isLingering: false);
        }

        /// <summary>
        /// Unpairs this peer's own client when its link drops by any path other than this transport's own, such as the engine walking it down.
        /// </summary>
        /// <param name="connectionStateChange">The change.</param>
        /// <param name="localConnectionState">The state the role moved to.</param>
        /// <param name="isServerStarted">True when this peer's server is running.</param>
        /// <remarks>
        /// Heard while the client link's Connection still names its roster entry, so the server can still release it. A drop this transport
        /// makes itself has already unpaired, and finds nothing here.
        /// </remarks>
        private void OnLocalConnectionStateChanged(ConnectionStateChange connectionStateChange, LocalConnectionState localConnectionState, bool isServerStarted)
        {
            if (connectionStateChange.Invoker is not Invoker.Client || localConnectionState is not LocalConnectionState.Disconnected)
                return;

            if (!_connectionsBySteamConnection.TryGetValue(HSteamNetConnection.Invalid, out Connection loopbackConnection) || !ReferenceEquals(connectionStateChange.Connection, loopbackConnection))
                return;

            _steamSockets.ClientSocket.OnLoopbackDropped();
            UnpairLoopbackClient();
        }

        /// <inheritdoc/>
        /// <remarks>
        /// A remote client's Connection goes back to its pool through the TransportManager, and this peer's own client's belongs to the
        /// client socket, so the maps are cleared rather than drained. The socket is returned only after it has stopped.
        /// </remarks>
        public override void OnReturn()
        {
            if (TransportManager is not null)
                LocalConnectionStateChanged -= OnLocalConnectionStateChanged;

            _idBySocketPool.OnReturn();

            _connectionsBySteamConnection.Clear();
            _steamConnectionsByConnection.Clear();
            _departedSteamConnections.Clear();
            _refusedSteamConnections.Clear();
            SteamMessages.DiscardSplitPackets(_splitPackets);
            _sendBatch.Clear();
            ClearCollection(IncomingPackets);

            ListenSocket = HSteamListenSocket.Invalid;
            _pollGroup = HSteamNetPollGroup.Invalid;
            _loopbackIdBySocket = Connection.UnsetId;
            _steamSockets = null;

            base.OnReturn();
        }
    }
}
#endif
