// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using System;
using System.Runtime.InteropServices;
using CodeBoost.Logging;
using Steamworks;

namespace Nucleus.Integrations.SteamworksNet
{
    /// <summary>
    /// The Steam networking calls a <see cref="SteamSockets"/> server makes, routed to either the user's interface or the game server's.
    /// </summary>
    /// <remarks>
    /// Steamworks.NET exposes the two as separate static classes with the same members, and a handle opened on one is unknown to the
    /// other. Picking one of these at start, rather than testing a flag on every call, keeps the choice in one place. Both instances
    /// hold no state, so sharing them statically carries nothing between runs.
    /// </remarks>
    internal abstract class SteamNetworkingInterface
    {
        /// <summary>
        /// The logged-in user's interface.
        /// </summary>
        public static readonly SteamNetworkingInterface Client = new ClientInterface();
        /// <summary>
        /// The game server interface.
        /// </summary>
        public static readonly SteamNetworkingInterface GameServer = new GameServerInterface();

        /// <summary>
        /// Checks whether this interface is unavailable, never initialized or already shut down.
        /// </summary>
        /// <returns><see langword="true"/> when no call may be made on it.</returns>
        public bool IsShutDown()
        {
            try
            {
                TestIfAvailable();

                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        /// <summary>
        /// Sets one global Steam networking option to an integer.
        /// </summary>
        /// <param name="configValue">The option.</param>
        /// <param name="value">Its value.</param>
        public void SetGlobalInt32(ESteamNetworkingConfigValue configValue, int value) => SetGlobal(configValue, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, value);

        /// <summary>
        /// Sets one global Steam networking option to a float.
        /// </summary>
        /// <param name="configValue">The option.</param>
        /// <param name="value">Its value.</param>
        public void SetGlobalFloat(ESteamNetworkingConfigValue configValue, float value) => SetGlobal(configValue, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Float, BitConverter.SingleToInt32Bits(value));

        /// <summary>
        /// Throws when this interface is not initialized.
        /// </summary>
        public abstract void TestIfAvailable();
        /// <summary>
        /// Starts relay discovery, so the first connect does not wait on it.
        /// </summary>
        public abstract void InitRelayNetworkAccess();
        /// <summary>
        /// Allocates a Steam-owned message with room for a payload.
        /// </summary>
        /// <param name="size">The payload size, in bytes.</param>
        /// <returns>The message.</returns>
        public abstract IntPtr AllocateMessage(int size);
        /// <summary>
        /// Opens a listen socket reached by identity and virtual port.
        /// </summary>
        /// <param name="virtualPort">The virtual port.</param>
        /// <param name="options">The per-connection options.</param>
        /// <returns>The socket, or <see cref="HSteamListenSocket.Invalid"/>.</returns>
        public abstract HSteamListenSocket CreateListenSocketP2P(int virtualPort, SteamNetworkingConfigValue_t[] options);
        /// <summary>
        /// Opens a listen socket reached by IP address.
        /// </summary>
        /// <param name="localAddress">The address and port to bind.</param>
        /// <param name="options">The per-connection options.</param>
        /// <returns>The socket, or <see cref="HSteamListenSocket.Invalid"/>.</returns>
        public abstract HSteamListenSocket CreateListenSocketIP(ref SteamNetworkingIPAddr localAddress, SteamNetworkingConfigValue_t[] options);
        /// <summary>
        /// Opens a listen socket reached by one of the fake ports Steam grants this interface.
        /// </summary>
        /// <param name="fakePortIndex">The index of the fake port, not its number.</param>
        /// <param name="options">The per-connection options.</param>
        /// <returns>The socket, or <see cref="HSteamListenSocket.Invalid"/>.</returns>
        public abstract HSteamListenSocket CreateListenSocketP2PFakeIP(int fakePortIndex, SteamNetworkingConfigValue_t[] options);
        /// <summary>
        /// Asks Steam for a fake IP address and ports.
        /// </summary>
        /// <param name="portCount">How many ports to ask for.</param>
        /// <returns><see langword="true"/> when a new request started; <see langword="false"/> when one is already running or granted.</returns>
        public abstract bool BeginAsyncRequestFakeIP(int portCount);
        /// <summary>
        /// Reads the fake address Steam granted.
        /// </summary>
        /// <param name="fakePortIndex">The index of the fake port.</param>
        /// <param name="fakeIpResult">The grant, or the reason there is none.</param>
        public abstract void GetFakeIP(int fakePortIndex, out SteamNetworkingFakeIPResult_t fakeIpResult);
        /// <summary>
        /// Accepts a connection asking to join.
        /// </summary>
        /// <param name="steamConnection">The connection.</param>
        /// <returns>Steam's result.</returns>
        public abstract EResult AcceptConnection(HSteamNetConnection steamConnection);
        /// <summary>
        /// Closes a connection.
        /// </summary>
        /// <param name="steamConnection">The connection.</param>
        /// <param name="reason">The reason code.</param>
        /// <param name="debug">The reason text, or <see langword="null"/>.</param>
        /// <param name="isLingering">True to keep it open until queued reliable data is acknowledged.</param>
        public abstract void CloseConnection(HSteamNetConnection steamConnection, int reason, string debug, bool isLingering);
        /// <summary>
        /// Closes a listen socket.
        /// </summary>
        /// <param name="listenSocket">The socket.</param>
        public abstract void CloseListenSocket(HSteamListenSocket listenSocket);
        /// <summary>
        /// Creates a poll group.
        /// </summary>
        /// <returns>The group, or <see cref="HSteamNetPollGroup.Invalid"/>.</returns>
        public abstract HSteamNetPollGroup CreatePollGroup();
        /// <summary>
        /// Destroys a poll group.
        /// </summary>
        /// <param name="pollGroup">The group.</param>
        public abstract void DestroyPollGroup(HSteamNetPollGroup pollGroup);
        /// <summary>
        /// Moves a connection into a poll group.
        /// </summary>
        /// <param name="steamConnection">The connection.</param>
        /// <param name="pollGroup">The group.</param>
        public abstract void SetConnectionPollGroup(HSteamNetConnection steamConnection, HSteamNetPollGroup pollGroup);
        /// <summary>
        /// Takes waiting messages from every connection in a poll group.
        /// </summary>
        /// <param name="pollGroup">The group.</param>
        /// <param name="messagePointers">The array Steam fills.</param>
        /// <param name="maximumMessages">The most messages to take.</param>
        /// <returns>How many messages were taken.</returns>
        public abstract int ReceiveMessagesOnPollGroup(HSteamNetPollGroup pollGroup, IntPtr[] messagePointers, int maximumMessages);
        /// <summary>
        /// Takes waiting messages from one connection.
        /// </summary>
        /// <param name="steamConnection">The connection.</param>
        /// <param name="messagePointers">The array Steam fills.</param>
        /// <param name="maximumMessages">The most messages to take.</param>
        /// <returns>How many messages were taken.</returns>
        public abstract int ReceiveMessagesOnConnection(HSteamNetConnection steamConnection, IntPtr[] messagePointers, int maximumMessages);
        /// <summary>
        /// Hands Steam a batch of messages.
        /// </summary>
        /// <param name="messageCount">How many messages are in the batch.</param>
        /// <param name="messagePointers">The messages.</param>
        /// <param name="results">Steam's result for each one.</param>
        public abstract void SendMessages(int messageCount, IntPtr[] messagePointers, long[] results);
        /// <summary>
        /// Configures a connection's lanes.
        /// </summary>
        /// <param name="steamConnection">The connection.</param>
        /// <param name="lanePriorities">The priority of each lane.</param>
        /// <param name="laneWeights">The weight of each lane.</param>
        /// <returns>Steam's result.</returns>
        public abstract EResult ConfigureConnectionLanes(HSteamNetConnection steamConnection, int[] lanePriorities, ushort[] laneWeights);
        /// <summary>
        /// Reads a connection's live status.
        /// </summary>
        /// <param name="steamConnection">The connection.</param>
        /// <param name="realTimeStatus">The status.</param>
        /// <returns>Steam's result.</returns>
        public abstract EResult GetConnectionRealTimeStatus(HSteamNetConnection steamConnection, out SteamNetConnectionRealTimeStatus_t realTimeStatus);
        /// <summary>
        /// Starts checking a client's auth session ticket against its SteamID.
        /// </summary>
        /// <param name="ticket">The ticket.</param>
        /// <param name="ticketSize">The ticket's length, in bytes.</param>
        /// <param name="steamId">The SteamID the ticket has to belong to.</param>
        /// <returns>Steam's first answer; the final one arrives as <see cref="ValidateAuthTicketResponse_t"/>.</returns>
        public abstract EBeginAuthSessionResult BeginAuthSession(byte[] ticket, int ticketSize, CSteamID steamId);
        /// <summary>
        /// Ends an auth session begun with <see cref="BeginAuthSession"/>.
        /// </summary>
        /// <param name="steamId">The client's SteamID.</param>
        public abstract void EndAuthSession(CSteamID steamId);
        /// <summary>
        /// Registers for connection status changes on this interface.
        /// </summary>
        /// <param name="dispatchDelegate">The handler.</param>
        /// <returns>The registration.</returns>
        public abstract Callback<SteamNetConnectionStatusChangedCallback_t> CreateStatusChangedCallback(Callback<SteamNetConnectionStatusChangedCallback_t>.DispatchDelegate dispatchDelegate);
        /// <summary>
        /// Registers for fake IP grants on this interface.
        /// </summary>
        /// <param name="dispatchDelegate">The handler.</param>
        /// <returns>The registration.</returns>
        public abstract Callback<SteamNetworkingFakeIPResult_t> CreateFakeIpResultCallback(Callback<SteamNetworkingFakeIPResult_t>.DispatchDelegate dispatchDelegate);
        /// <summary>
        /// Registers for auth ticket verdicts on this interface.
        /// </summary>
        /// <param name="dispatchDelegate">The handler.</param>
        /// <returns>The registration.</returns>
        public abstract Callback<ValidateAuthTicketResponse_t> CreateValidateAuthTicketCallback(Callback<ValidateAuthTicketResponse_t>.DispatchDelegate dispatchDelegate);
        /// <summary>
        /// Gets the instance a server runs on.
        /// </summary>
        /// <param name="isGameServer">True for the game server interface, false for the user's.</param>
        /// <returns>The matching instance.</returns>
        public static SteamNetworkingInterface Get(bool isGameServer) => isGameServer ? GameServer : Client;

        /// <summary>
        /// Sets one global option from a pointer to its value.
        /// </summary>
        /// <param name="configValue">The option.</param>
        /// <param name="dataType">The value's type.</param>
        /// <param name="valuePointer">Native memory holding the value.</param>
        /// <returns><see langword="true"/> when Steam accepted the value.</returns>
        protected abstract bool SetConfigValue(ESteamNetworkingConfigValue configValue, ESteamNetworkingConfigDataType dataType, IntPtr valuePointer);

        /// <summary>
        /// Sets one global option, handing Steam a pointer to its four bytes.
        /// </summary>
        /// <param name="configValue">The option.</param>
        /// <param name="dataType">The value's type.</param>
        /// <param name="valueBits">The value's bits.</param>
        /// <remarks>Allocates native memory per call, which is fine for a setting changed by hand.</remarks>
        private void SetGlobal(ESteamNetworkingConfigValue configValue, ESteamNetworkingConfigDataType dataType, int valueBits)
        {
            IntPtr valuePointer = Marshal.AllocHGlobal(sizeof(int));

            try
            {
                Marshal.WriteInt32(valuePointer, valueBits);

                if (!SetConfigValue(configValue, dataType, valuePointer))
                    Logger<SteamSockets>.LogWarning($"Steam refused the global option [{configValue}].");
            }
            finally
            {
                Marshal.FreeHGlobal(valuePointer);
            }
        }

        /// <summary>
        /// The calls routed to <see cref="SteamNetworkingSockets"/>, <see cref="SteamNetworkingUtils"/> and <see cref="SteamUser"/>.
        /// </summary>
        private sealed class ClientInterface : SteamNetworkingInterface
        {
            /// <inheritdoc/>
            public override void TestIfAvailable() => InteropHelp.TestIfAvailableClient();
            /// <inheritdoc/>
            public override void InitRelayNetworkAccess() => SteamNetworkingUtils.InitRelayNetworkAccess();
            /// <inheritdoc/>
            public override IntPtr AllocateMessage(int size) => SteamNetworkingUtils.AllocateMessage(size);
            /// <inheritdoc/>
            public override HSteamListenSocket CreateListenSocketP2P(int virtualPort, SteamNetworkingConfigValue_t[] options) => SteamNetworkingSockets.CreateListenSocketP2P(virtualPort, options.Length, options);
            /// <inheritdoc/>
            public override HSteamListenSocket CreateListenSocketIP(ref SteamNetworkingIPAddr localAddress, SteamNetworkingConfigValue_t[] options) => SteamNetworkingSockets.CreateListenSocketIP(ref localAddress, options.Length, options);
            /// <inheritdoc/>
            public override HSteamListenSocket CreateListenSocketP2PFakeIP(int fakePortIndex, SteamNetworkingConfigValue_t[] options) => SteamNetworkingSockets.CreateListenSocketP2PFakeIP(fakePortIndex, options.Length, options);
            /// <inheritdoc/>
            public override bool BeginAsyncRequestFakeIP(int portCount) => SteamNetworkingSockets.BeginAsyncRequestFakeIP(portCount);
            /// <inheritdoc/>
            public override void GetFakeIP(int fakePortIndex, out SteamNetworkingFakeIPResult_t fakeIpResult) => SteamNetworkingSockets.GetFakeIP(fakePortIndex, out fakeIpResult);
            /// <inheritdoc/>
            public override EResult AcceptConnection(HSteamNetConnection steamConnection) => SteamNetworkingSockets.AcceptConnection(steamConnection);
            /// <inheritdoc/>
            public override void CloseConnection(HSteamNetConnection steamConnection, int reason, string debug, bool isLingering) => SteamNetworkingSockets.CloseConnection(steamConnection, reason, debug, isLingering);
            /// <inheritdoc/>
            public override void CloseListenSocket(HSteamListenSocket listenSocket) => SteamNetworkingSockets.CloseListenSocket(listenSocket);
            /// <inheritdoc/>
            public override HSteamNetPollGroup CreatePollGroup() => SteamNetworkingSockets.CreatePollGroup();
            /// <inheritdoc/>
            public override void DestroyPollGroup(HSteamNetPollGroup pollGroup) => SteamNetworkingSockets.DestroyPollGroup(pollGroup);
            /// <inheritdoc/>
            public override void SetConnectionPollGroup(HSteamNetConnection steamConnection, HSteamNetPollGroup pollGroup) => SteamNetworkingSockets.SetConnectionPollGroup(steamConnection, pollGroup);
            /// <inheritdoc/>
            public override int ReceiveMessagesOnPollGroup(HSteamNetPollGroup pollGroup, IntPtr[] messagePointers, int maximumMessages) => SteamNetworkingSockets.ReceiveMessagesOnPollGroup(pollGroup, messagePointers, maximumMessages);
            /// <inheritdoc/>
            public override int ReceiveMessagesOnConnection(HSteamNetConnection steamConnection, IntPtr[] messagePointers, int maximumMessages) => SteamNetworkingSockets.ReceiveMessagesOnConnection(steamConnection, messagePointers, maximumMessages);
            /// <inheritdoc/>
            public override void SendMessages(int messageCount, IntPtr[] messagePointers, long[] results) => SteamNetworkingSockets.SendMessages(messageCount, messagePointers, results);
            /// <inheritdoc/>
            public override EResult ConfigureConnectionLanes(HSteamNetConnection steamConnection, int[] lanePriorities, ushort[] laneWeights) => SteamNetworkingSockets.ConfigureConnectionLanes(steamConnection, lanePriorities.Length, lanePriorities, laneWeights);
            /// <inheritdoc/>
            public override EResult GetConnectionRealTimeStatus(HSteamNetConnection steamConnection, out SteamNetConnectionRealTimeStatus_t realTimeStatus)
            {
                realTimeStatus = default;
                SteamNetConnectionRealTimeLaneStatus_t laneStatus = default;

                return SteamNetworkingSockets.GetConnectionRealTimeStatus(steamConnection, ref realTimeStatus, nLanes: 0, ref laneStatus);
            }
            /// <inheritdoc/>
            public override EBeginAuthSessionResult BeginAuthSession(byte[] ticket, int ticketSize, CSteamID steamId) => SteamUser.BeginAuthSession(ticket, ticketSize, steamId);
            /// <inheritdoc/>
            public override void EndAuthSession(CSteamID steamId) => SteamUser.EndAuthSession(steamId);
            /// <inheritdoc/>
            public override Callback<SteamNetConnectionStatusChangedCallback_t> CreateStatusChangedCallback(Callback<SteamNetConnectionStatusChangedCallback_t>.DispatchDelegate dispatchDelegate) => Callback<SteamNetConnectionStatusChangedCallback_t>.Create(dispatchDelegate);
            /// <inheritdoc/>
            public override Callback<SteamNetworkingFakeIPResult_t> CreateFakeIpResultCallback(Callback<SteamNetworkingFakeIPResult_t>.DispatchDelegate dispatchDelegate) => Callback<SteamNetworkingFakeIPResult_t>.Create(dispatchDelegate);
            /// <inheritdoc/>
            public override Callback<ValidateAuthTicketResponse_t> CreateValidateAuthTicketCallback(Callback<ValidateAuthTicketResponse_t>.DispatchDelegate dispatchDelegate) => Callback<ValidateAuthTicketResponse_t>.Create(dispatchDelegate);
            /// <inheritdoc/>
            protected override bool SetConfigValue(ESteamNetworkingConfigValue configValue, ESteamNetworkingConfigDataType dataType, IntPtr valuePointer) => SteamNetworkingUtils.SetConfigValue(configValue, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, dataType, valuePointer);
        }

        /// <summary>
        /// The calls routed to <see cref="SteamGameServerNetworkingSockets"/>, <see cref="SteamGameServerNetworkingUtils"/> and <see cref="SteamGameServer"/>.
        /// </summary>
        private sealed class GameServerInterface : SteamNetworkingInterface
        {
            /// <inheritdoc/>
            public override void TestIfAvailable() => InteropHelp.TestIfAvailableGameServer();
            /// <inheritdoc/>
            public override void InitRelayNetworkAccess() => SteamGameServerNetworkingUtils.InitRelayNetworkAccess();
            /// <inheritdoc/>
            public override IntPtr AllocateMessage(int size) => SteamGameServerNetworkingUtils.AllocateMessage(size);
            /// <inheritdoc/>
            public override HSteamListenSocket CreateListenSocketP2P(int virtualPort, SteamNetworkingConfigValue_t[] options) => SteamGameServerNetworkingSockets.CreateListenSocketP2P(virtualPort, options.Length, options);
            /// <inheritdoc/>
            public override HSteamListenSocket CreateListenSocketIP(ref SteamNetworkingIPAddr localAddress, SteamNetworkingConfigValue_t[] options) => SteamGameServerNetworkingSockets.CreateListenSocketIP(ref localAddress, options.Length, options);
            /// <inheritdoc/>
            public override HSteamListenSocket CreateListenSocketP2PFakeIP(int fakePortIndex, SteamNetworkingConfigValue_t[] options) => SteamGameServerNetworkingSockets.CreateListenSocketP2PFakeIP(fakePortIndex, options.Length, options);
            /// <inheritdoc/>
            public override bool BeginAsyncRequestFakeIP(int portCount) => SteamGameServerNetworkingSockets.BeginAsyncRequestFakeIP(portCount);
            /// <inheritdoc/>
            public override void GetFakeIP(int fakePortIndex, out SteamNetworkingFakeIPResult_t fakeIpResult) => SteamGameServerNetworkingSockets.GetFakeIP(fakePortIndex, out fakeIpResult);
            /// <inheritdoc/>
            public override EResult AcceptConnection(HSteamNetConnection steamConnection) => SteamGameServerNetworkingSockets.AcceptConnection(steamConnection);
            /// <inheritdoc/>
            public override void CloseConnection(HSteamNetConnection steamConnection, int reason, string debug, bool isLingering) => SteamGameServerNetworkingSockets.CloseConnection(steamConnection, reason, debug, isLingering);
            /// <inheritdoc/>
            public override void CloseListenSocket(HSteamListenSocket listenSocket) => SteamGameServerNetworkingSockets.CloseListenSocket(listenSocket);
            /// <inheritdoc/>
            public override HSteamNetPollGroup CreatePollGroup() => SteamGameServerNetworkingSockets.CreatePollGroup();
            /// <inheritdoc/>
            public override void DestroyPollGroup(HSteamNetPollGroup pollGroup) => SteamGameServerNetworkingSockets.DestroyPollGroup(pollGroup);
            /// <inheritdoc/>
            public override void SetConnectionPollGroup(HSteamNetConnection steamConnection, HSteamNetPollGroup pollGroup) => SteamGameServerNetworkingSockets.SetConnectionPollGroup(steamConnection, pollGroup);
            /// <inheritdoc/>
            public override int ReceiveMessagesOnPollGroup(HSteamNetPollGroup pollGroup, IntPtr[] messagePointers, int maximumMessages) => SteamGameServerNetworkingSockets.ReceiveMessagesOnPollGroup(pollGroup, messagePointers, maximumMessages);
            /// <inheritdoc/>
            public override int ReceiveMessagesOnConnection(HSteamNetConnection steamConnection, IntPtr[] messagePointers, int maximumMessages) => SteamGameServerNetworkingSockets.ReceiveMessagesOnConnection(steamConnection, messagePointers, maximumMessages);
            /// <inheritdoc/>
            public override void SendMessages(int messageCount, IntPtr[] messagePointers, long[] results) => SteamGameServerNetworkingSockets.SendMessages(messageCount, messagePointers, results);
            /// <inheritdoc/>
            public override EResult ConfigureConnectionLanes(HSteamNetConnection steamConnection, int[] lanePriorities, ushort[] laneWeights) => SteamGameServerNetworkingSockets.ConfigureConnectionLanes(steamConnection, lanePriorities.Length, lanePriorities, laneWeights);
            /// <inheritdoc/>
            public override EResult GetConnectionRealTimeStatus(HSteamNetConnection steamConnection, out SteamNetConnectionRealTimeStatus_t realTimeStatus)
            {
                realTimeStatus = default;
                SteamNetConnectionRealTimeLaneStatus_t laneStatus = default;

                return SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(steamConnection, ref realTimeStatus, nLanes: 0, ref laneStatus);
            }
            /// <inheritdoc/>
            public override EBeginAuthSessionResult BeginAuthSession(byte[] ticket, int ticketSize, CSteamID steamId) => SteamGameServer.BeginAuthSession(ticket, ticketSize, steamId);
            /// <inheritdoc/>
            public override void EndAuthSession(CSteamID steamId) => SteamGameServer.EndAuthSession(steamId);
            /// <inheritdoc/>
            public override Callback<SteamNetConnectionStatusChangedCallback_t> CreateStatusChangedCallback(Callback<SteamNetConnectionStatusChangedCallback_t>.DispatchDelegate dispatchDelegate) => Callback<SteamNetConnectionStatusChangedCallback_t>.CreateGameServer(dispatchDelegate);
            /// <inheritdoc/>
            public override Callback<SteamNetworkingFakeIPResult_t> CreateFakeIpResultCallback(Callback<SteamNetworkingFakeIPResult_t>.DispatchDelegate dispatchDelegate) => Callback<SteamNetworkingFakeIPResult_t>.CreateGameServer(dispatchDelegate);
            /// <inheritdoc/>
            public override Callback<ValidateAuthTicketResponse_t> CreateValidateAuthTicketCallback(Callback<ValidateAuthTicketResponse_t>.DispatchDelegate dispatchDelegate) => Callback<ValidateAuthTicketResponse_t>.CreateGameServer(dispatchDelegate);
            /// <inheritdoc/>
            protected override bool SetConfigValue(ESteamNetworkingConfigValue configValue, ESteamNetworkingConfigDataType dataType, IntPtr valuePointer) => SteamGameServerNetworkingUtils.SetConfigValue(configValue, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, dataType, valuePointer);
        }
    }
}
#endif
