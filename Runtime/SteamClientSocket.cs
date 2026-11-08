// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CodeBoost.Logging;
using Nucleus.Connections;
using Nucleus.Packets;
using Nucleus.Transports;
using Nucleus.Transports.Sockets;
using Steamworks;

namespace Nucleus.Integrations.SteamworksNet
{
    /// <summary>
    /// The client half of <see cref="SteamSockets"/>: dials the server the transport's <see cref="SteamSockets.Addressing"/> points at over
    /// the user's Steam interface, or pairs in process with this peer's own running server.
    /// </summary>
    /// <remarks>Inbound packets are attributed to the server socket's Connection, which is what the engine expects of a client.</remarks>
    public class SteamClientSocket : CommonSocket
    {
        /// <inheritdoc/>
        public override bool IsServer { get; protected set; } = false;
        /// <inheritdoc/>
        public override bool IsEmulated { get; protected set; } = false;
        /// <summary>
        /// The transport this socket belongs to, which holds the settings it reads.
        /// </summary>
        private SteamSockets _steamSockets;
        /// <summary>
        /// The pieces received so far of the server's reliable packet that arrives split, by the client's Steam link.
        /// </summary>
        /// <remarks>A dictionary rather than one field, so the client receive shares <see cref="SteamMessages.TryCreatePacket"/> with the server's.</remarks>
        private readonly Dictionary<HSteamNetConnection, ArraySegment<byte>> _splitPackets = new();
        /// <summary>
        /// The messages built this frame, handed to the user's interface at the next flush.
        /// </summary>
        private readonly SteamSendBatch _sendBatch = new();
        /// <summary>
        /// Message pointers Steam fills on each receive.
        /// </summary>
        private readonly IntPtr[] _receivedMessagePointers = new IntPtr[SteamMessages.MaximumMessagesPerReceive];
        /// <summary>
        /// The client's link to a server, or <see cref="HSteamNetConnection.Invalid"/> when the client is not linked over Steam.
        /// </summary>
        private HSteamNetConnection _steamConnection = HSteamNetConnection.Invalid;
        /// <summary>
        /// True while the client is paired in process with this peer's own server.
        /// </summary>
        private bool _isLoopback;
        /// <summary>
        /// True while the client is connecting but holds its dial until Steam's network config has settled.
        /// </summary>
        /// <remarks>
        /// A process that dials straight after Steam starts knows only Steam's built-in certificate authorities, so it refuses a server
        /// whose certificate a newer one signed (<c>k_ESteamNetConnectionEnd_Remote_BadCert</c>, "CA key ... is not known to us").
        /// </remarks>
        private bool _isDialHeld;
        /// <summary>
        /// True when Steam has closed the client's link and the drop is due on the next receive.
        /// </summary>
        /// <remarks>
        /// Steam still hands over what arrived before a close until the connection is closed locally. The drop waits a pass so the
        /// messages read in the pass that saw the close, a kick's reason among them, reach the game before it hears the link has gone.
        /// </remarks>
        private bool _isLinkDropDue;

        /// <inheritdoc/>
        public override void Initialize(Transport transport)
        {
            base.Initialize(transport);

            InitializeConnection();

            _steamSockets = (SteamSockets)transport;
        }

        /// <summary>
        /// Hands the frame's staged messages to Steam, and closes the link if Steam refused reliable data on it.
        /// </summary>
        /// <remarks>A reliable message Steam refuses, being larger than the whole send buffer, closes the link, because the peers can no longer agree.</remarks>
        internal void FlushStagedMessages()
        {
            _sendBatch.Flush(_steamSockets);

            List<SteamSendBatch.RefusedSend> refusedSends = _sendBatch.RefusedReliableSends;

            if (refusedSends.Count == 0)
                return;

            bool isLinkRefused = false;

            foreach (SteamSendBatch.RefusedSend refusedSend in refusedSends)
            {
                // A link this socket closed after staging, or Steam closed and is due to drop, refuses its leftovers; that is not news.
                if (isLinkRefused || refusedSend.SteamConnection != _steamConnection || _isLinkDropDue)
                    continue;

                isLinkRefused = true;
                _steamSockets.RefusedReliableSendCount++;

                Logger<SteamClientSocket>.LogError($"Steam refused a reliable message of [{refusedSend.PayloadBytes}] bytes: [{refusedSend.Result}]. The link is closed, because the peers can no longer agree. Raise [{nameof(SteamSockets.SendBufferSizeBytes)}] above [{_sendBatch.SendBufferBytes}] bytes if it was larger than that.");
            }

            refusedSends.Clear();

            // Closed only now, since a close flushes again.
            if (isLinkRefused)
                Stop();
        }

        /// <summary>
        /// Leaves the server, or unpairs from this peer's own, and walks the client's state down.
        /// </summary>
        /// <returns>The result of the stop.</returns>
        /// <remarks>A link the engine has yet to walk down, such as one whose server dropped it in process, is walked down here too.</remarks>
        internal ConnectionStateChangeResult Stop()
        {
            if (!_isLoopback && !_isDialHeld && _steamConnection == HSteamNetConnection.Invalid && Connection.LocalState is LocalConnectionState.Disconnected)
                return ConnectionStateChangeResult.AlreadyInState;

            _isDialHeld = false;

            if (_isLoopback)
                ReleaseLoopback();
            else
                CloseSteamConnection();

            SteamSockets.ChangeLocalStateToDisconnected(TransportManager, Invoker.Client, Connection);

            return ConnectionStateChangeResult.Success;
        }

        /// <summary>
        /// Ends the in-process pairing from the server's side, which has dropped this peer's own client or heard its link drop.
        /// </summary>
        /// <remarks>
        /// The link is dropped once the packets the server already handed over, a kick's reason among them, have dispatched; they stay
        /// queued for the next receive.
        /// </remarks>
        internal void OnLoopbackDropped()
        {
            if (!_isLoopback)
                return;

            _isLoopback = false;

            if (State is LocalConnectionState.Connected)
                DeferLocalClientLinkDrop();
        }

        /// <summary>
        /// Takes a packet this peer's own server sent its paired client, for the next receive.
        /// </summary>
        /// <param name="incomingPacket">The packet, owning its payload.</param>
        internal void EnqueueLoopbackPacket(IncomingPacket incomingPacket) => IncomingPackets.Enqueue(incomingPacket);

        /// <summary>
        /// Reads Steam's live status of the client's link to the server.
        /// </summary>
        /// <param name="linkStatus">The status, when there is a Steam link to read.</param>
        /// <returns><see langword="true"/> when the client rides a Steam link; a client paired in process has none.</returns>
        internal bool TryGetLinkStatus(out SteamLinkStatus linkStatus) => SteamSockets.TryReadLinkStatus(SteamNetworkingInterface.Client, _steamConnection, out linkStatus);

        /// <summary>
        /// Gets the SteamID-bearing identity of the server this client is linked to over Steam.
        /// </summary>
        /// <param name="serverIdentity">The server's identity, as Steam authenticated it.</param>
        /// <returns><see langword="true"/> when the client has a Steam link to read it from.</returns>
        internal bool TryGetServerIdentity(out SteamNetworkingIdentity serverIdentity)
        {
            serverIdentity = default;

            if (_steamConnection == HSteamNetConnection.Invalid || !SteamNetworkingSockets.GetConnectionInfo(_steamConnection, out SteamNetConnectionInfo_t connectionInfo))
                return false;

            serverIdentity = connectionInfo.m_identityRemote;

            return true;
        }

        /// <inheritdoc/>
        protected override Task<ConnectionStateChangeResult> ConnectAsync() => GetResultTask(Start());

        /// <inheritdoc/>
        protected override Task<ConnectionStateChangeResult> DisconnectAsync() => GetResultTask(Stop());

        /// <inheritdoc/>
        protected override void SendPacket(OutgoingPacket outgoingPacket)
        {
            if (_isLoopback)
            {
                if (State is LocalConnectionState.Connected && _steamSockets.ServerSocket.TryEnqueueLoopbackPacket(outgoingPacket))
                    Nucleus.Managers.Transports.TransportManager.PacketOffloaded(outgoingPacket);
                else
                    Nucleus.Managers.Transports.TransportManager.PacketFailed(outgoingPacket);

                return;
            }

            if (_steamConnection == HSteamNetConnection.Invalid || _isLinkDropDue || Connection.LocalState is not LocalConnectionState.Connected)
            {
                Nucleus.Managers.Transports.TransportManager.PacketFailed(outgoingPacket);

                return;
            }

            _sendBatch.Stage(_steamConnection, outgoingPacket);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Drops a link Steam closed during the previous pass, makes a held dial once Steam's network config has settled, applies the
        /// client's queued Steam status changes, then drains what the server sent.
        /// </remarks>
        protected override void ReceivePackets(ref List<IncomingPacket> receivedPackets)
        {
            if (_isLinkDropDue)
            {
                CloseSteamConnection();
                SteamSockets.ChangeLocalState(TransportManager, Invoker.Client, LocalConnectionState.Disconnected, Connection);

                return;
            }

            SteamSockets steamSockets = _steamSockets;

            while (steamSockets.RelayNetworkStatuses.TryDequeue(out SteamRelayNetworkStatus_t relayNetworkStatus))
            {
                if (_isDialHeld && IsNetworkConfigSettled(relayNetworkStatus.m_eAvailNetworkConfig))
                {
                    _isDialHeld = false;
                    Dial();
                }
            }

            while (steamSockets.ClientStatusChanges.TryDequeue(out SteamNetConnectionStatusChangedCallback_t statusChange))
                ApplyStatusChange(statusChange);

            // Packets this peer's own server handed over in process, still delivered after it dropped the pairing.
            while (IncomingPackets.TryDequeue(out IncomingPacket incomingPacket))
                receivedPackets.Add(incomingPacket);

            if (_steamConnection == HSteamNetConnection.Invalid)
                return;

            // As on the server, a Steam shut down under a live link has dropped it.
            if (SteamNetworkingInterface.Client.IsShutDown())
            {
                Logger<SteamClientSocket>.LogWarning("Steam was shut down while the client was connected, so the client stopped.");
                Stop();

                return;
            }

            Connection serverConnection = steamSockets.ServerSocket.Connection;
            int messageCount;

            do
            {
                messageCount = SteamNetworkingSockets.ReceiveMessagesOnConnection(_steamConnection, _receivedMessagePointers, SteamMessages.MaximumMessagesPerReceive);

                /* Steam delivers messages only on a connected link, but its Connected status change is raised from the game's callback
                 * pump, which can run after this receive. Left Connecting, the client would drop the server's authentication. */
                if (messageCount > 0 && Connection.LocalState is LocalConnectionState.Connecting)
                    SteamSockets.ChangeLocalState(TransportManager, Invoker.Client, LocalConnectionState.Connected, Connection);

                for (int i = 0; i < messageCount; i++)
                {
                    // The server is trusted with what it sends, so its packets are never limited.
                    if (SteamMessages.TryCreatePacket(_receivedMessagePointers[i], serverConnection, _steamConnection, _splitPackets, SteamSockets.UnsetMaximumClientPacketBytes, out IncomingPacket incomingPacket, out _))
                        receivedPackets.Add(incomingPacket);
                }
            }
            while (messageCount == SteamMessages.MaximumMessagesPerReceive);
        }

        /// <summary>
        /// Starts connecting to the server <see cref="SteamSockets.Addressing"/> points at, or pairs in process with this peer's own server.
        /// </summary>
        /// <returns>The result of the start. A Steam link is still connecting when this returns; <see cref="ReceivePackets"/> finishes it.</returns>
        /// <remarks>Until Steam's network config has settled the dial is held, and <see cref="ReceivePackets"/> makes it once it has.</remarks>
        private ConnectionStateChangeResult Start()
        {
            if (_isLoopback || _isDialHeld || _steamConnection != HSteamNetConnection.Invalid)
                return ConnectionStateChangeResult.AlreadyInState;

            if (!_steamSockets.TryPrepareSteam(SteamNetworkingInterface.Client))
                return ConnectionStateChangeResult.UnspecifiedError;

            /* Fresh Connections, so a new link carries none of the last one's ticks or authentication. A pure client still needs the server
             * socket's Connection to attribute inbound packets to, and it has to start clean too; a running server keeps its own. */
            RecycleConnection();

            SteamServerSocket serverSocket = _steamSockets.ServerSocket;
            serverSocket.RecycleConnectionUnlessListening();

            ClearCollection(IncomingPackets);

            /* A peer running its own server only ever joins that server, so a target left over from an earlier join is ignored rather
             * than dialled; otherwise a peer that joined someone and then hosted would send its own client to the old host. */
            if (serverSocket.IsListening)
            {
                if (!IsTargetingSelf())
                    Logger<SteamClientSocket>.LogWarning($"Ignoring the target [{DescribeTarget()}] because this peer is running a server, whose client joins it. Stop the server first to join another.");

                return StartLoopback(serverSocket);
            }

            if (IsTargetingSelf())
                return StartLoopback(serverSocket);

            if (!SteamSockets.ChangeLocalState(TransportManager, Invoker.Client, LocalConnectionState.Connecting, Connection))
                return ConnectionStateChangeResult.UnspecifiedError;

            // Cleared before the status is read, so a change raised after the read is still queued and not dropped.
            _steamSockets.RelayNetworkStatuses.Clear();
            SteamNetworkingUtils.GetRelayNetworkStatus(out SteamRelayNetworkStatus_t relayNetworkStatus);

            if (IsNetworkConfigSettled(relayNetworkStatus.m_eAvailNetworkConfig))
                return Dial();

            _isDialHeld = true;
            Logger<SteamClientSocket>.LogInformation($"Waiting for Steam's network config before connecting to [{DescribeTarget()}].");

            return ConnectionStateChangeResult.Success;
        }

        /// <summary>
        /// Opens the client's Steam link, or walks the client back down when Steam refuses.
        /// </summary>
        /// <returns>The result of the dial. The link is still connecting when this returns.</returns>
        private ConnectionStateChangeResult Dial()
        {
            _sendBatch.SendBufferBytes = _steamSockets.GetSendBufferBytes();
            _steamConnection = Connect();

            if (_steamConnection != HSteamNetConnection.Invalid)
            {
                SteamSockets.ConfigureLanes(SteamNetworkingInterface.Client, _steamConnection);

                return ConnectionStateChangeResult.Success;
            }

            Logger<SteamClientSocket>.LogError($"Steam refused to connect to [{DescribeTarget()}].");

            SteamSockets.ChangeLocalState(TransportManager, Invoker.Client, LocalConnectionState.Disconnected, Connection);

            return ConnectionStateChangeResult.UnspecifiedError;
        }

        /// <summary>
        /// Checks whether the client's target names this peer itself.
        /// </summary>
        /// <returns><see langword="true"/> when the client should join this peer's own server.</returns>
        private bool IsTargetingSelf()
        {
            if (_steamSockets.Addressing is SteamworksNetAddressing.SteamId)
                return _steamSockets.ServerSteamId is SteamSockets.UnsetSteamId || _steamSockets.ServerSteamId == SteamUser.GetSteamID().m_SteamID;

            return string.IsNullOrWhiteSpace(_steamSockets.ServerAddress);
        }

        /// <summary>
        /// Opens the client's Steam link to the target <see cref="SteamSockets.Addressing"/> names.
        /// </summary>
        /// <returns>The link, or <see cref="HSteamNetConnection.Invalid"/> when Steam refused or the address could not be read.</returns>
        private HSteamNetConnection Connect()
        {
            SteamNetworkingConfigValue_t[] options = _steamSockets.CreateOptions();

            if (_steamSockets.Addressing is SteamworksNetAddressing.SteamId)
            {
                SteamNetworkingIdentity serverIdentity = new();
                serverIdentity.SetSteamID64(_steamSockets.ServerSteamId);

                return SteamNetworkingSockets.ConnectP2P(ref serverIdentity, _steamSockets.VirtualPort, options.Length, options);
            }

            string serverAddress = _steamSockets.ServerAddress;

            if (!SteamSockets.TryParseIpAddress(serverAddress.Trim(), _steamSockets.Configuration.Port, out SteamNetworkingIPAddr serverIpAddress))
            {
                Logger<SteamClientSocket>.LogError($"[{serverAddress}] is not an IP address, such as 203.0.113.7, 203.0.113.7:27015 or [::1]:27015. Host names are not looked up.");

                return HSteamNetConnection.Invalid;
            }

            return SteamNetworkingSockets.ConnectByIPAddress(ref serverIpAddress, options.Length, options);
        }

        /// <summary>
        /// Describes where the client is connecting, for a log.
        /// </summary>
        /// <returns>The target.</returns>
        private string DescribeTarget() => _steamSockets.Addressing is SteamworksNetAddressing.SteamId ? $"SteamID {_steamSockets.ServerSteamId} on virtual port {_steamSockets.VirtualPort}" : $"{_steamSockets.Addressing} {_steamSockets.ServerAddress}";

        /// <summary>
        /// Pairs the client with this peer's own running server.
        /// </summary>
        /// <param name="serverSocket">This peer's server socket.</param>
        /// <returns>The result of the start.</returns>
        /// <remarks>
        /// The pairing is recorded before the server admits the client, because admitting it runs its authentication synchronously, and
        /// anything sent from there must find this socket already paired.
        /// </remarks>
        private ConnectionStateChangeResult StartLoopback(SteamServerSocket serverSocket)
        {
            if (!serverSocket.IsListening)
            {
                Logger<SteamClientSocket>.LogError($"No server to join. Set [{nameof(SteamSockets)}.{nameof(SteamSockets.ServerSteamId)}] or [{nameof(SteamSockets)}.{nameof(SteamSockets.ServerAddress)}] to the server, or start this peer's server first.");

                return ConnectionStateChangeResult.UnspecifiedError;
            }

            if (!TryDriveLocalStateTo(Invoker.Client, LocalConnectionState.Connected))
            {
                SteamSockets.ChangeLocalState(TransportManager, Invoker.Client, LocalConnectionState.Disconnected, Connection);

                return ConnectionStateChangeResult.UnspecifiedError;
            }

            _isLoopback = true;

            if (serverSocket.TryPairLoopbackClient(SteamUser.GetSteamID().m_SteamID))
                return ConnectionStateChangeResult.Success;

            Stop();

            return ConnectionStateChangeResult.UnspecifiedError;
        }

        /// <summary>
        /// Walks the client up or down as Steam reported its link.
        /// </summary>
        /// <param name="statusChange">The change Steam reported.</param>
        private void ApplyStatusChange(SteamNetConnectionStatusChangedCallback_t statusChange)
        {
            // A change for a link this client has since closed or replaced.
            if (statusChange.m_hConn != _steamConnection)
                return;

            switch (statusChange.m_info.m_eState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    SteamSockets.ChangeLocalState(TransportManager, Invoker.Client, LocalConnectionState.Connected, Connection);
                    break;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    SteamSockets.LogClosed(statusChange.m_info, "The link to the server");
                    _isLinkDropDue = true;

                    break;
            }
        }

        /// <summary>
        /// Ends the in-process pairing, telling the server its own client left while the client's Connection still names its roster entry.
        /// </summary>
        private void ReleaseLoopback()
        {
            _isLoopback = false;
            ClearCollection(IncomingPackets);

            _steamSockets.ServerSocket.UnpairLoopbackClient();
        }

        /// <summary>
        /// Closes the client's Steam link, if it has one, after handing Steam whatever was staged for it.
        /// </summary>
        private void CloseSteamConnection()
        {
            if (_steamConnection != HSteamNetConnection.Invalid && !SteamNetworkingInterface.Client.IsShutDown())
            {
                FlushStagedMessages();
                SteamNetworkingSockets.CloseConnection(_steamConnection, SteamMessages.ApplicationCloseReason, "Disconnected by the client.", bEnableLinger: false);
            }

            _sendBatch.Discard(_steamConnection);
            SteamMessages.DiscardSplitPacket(_splitPackets, _steamConnection);

            _steamConnection = HSteamNetConnection.Invalid;
            _isLinkDropDue = false;
        }

        /// <summary>
        /// Checks whether Steam has finished trying to fetch its network config, whether or not it succeeded.
        /// </summary>
        /// <param name="networkConfigAvailability">The network config's availability.</param>
        /// <returns><see langword="true"/> when a dial no longer gains anything by waiting.</returns>
        /// <remarks>A fetch that failed dials anyway, so Steam reports why the link could not open rather than the client waiting forever.</remarks>
        private static bool IsNetworkConfigSettled(ESteamNetworkingAvailability networkConfigAvailability)
        {
            return networkConfigAvailability is ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current or ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Failed or ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_CannotTry or ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Previously;
        }

        /// <inheritdoc/>
        /// <remarks>The socket is returned only after it has stopped, so its link is already closed.</remarks>
        public override void OnReturn()
        {
            SteamMessages.DiscardSplitPackets(_splitPackets);
            _sendBatch.Clear();
            ClearCollection(IncomingPackets);

            _steamConnection = HSteamNetConnection.Invalid;
            _isLoopback = false;
            _isDialHeld = false;
            _isLinkDropDue = false;
            _steamSockets = null;

            base.OnReturn();
        }
    }
}
#endif
