// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CodeBoost.Logging;
using CodeBoost.Performance;
using Nucleus.Connections;
using Nucleus.Managers.Core;
using Nucleus.Managers.Messages;
using Nucleus.Managers.NetworkLoop;
using Nucleus.Transports;
using Steamworks;

namespace Nucleus.Integrations.SteamworksNet
{
    /// <summary>
    /// An <see cref="Managers.Server.IClientAuthenticator"/> that admits a client only once Steam has confirmed its auth session
    /// ticket belongs to the SteamID its <see cref="SteamSockets"/> link was authenticated as, and that it owns the game.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Steam already authenticates the SteamID behind every link by certificate, which the default authenticator trusts. A ticket adds
    /// what a certificate does not: Steam checks the account owns the app, is not VAC banned, and is not borrowing the game it is
    /// barred from. A dedicated server on the game server interface wants this; a listen server may.
    /// </para>
    /// <para>
    /// Both sides run it. The client sends its ticket as soon as its link is up; the server begins an auth session bound to the link's
    /// SteamID, so a forged ticket, or someone else's, is refused, and approves the client with that SteamID as its identity once Steam
    /// answers. The host's own client, paired in process, is approved at once. Steam's answers arrive on the thread pumping its
    /// callbacks and are applied at <see cref="NetworkLoopSteps.LateVariableUpdate"/>.
    /// </para>
    /// </remarks>
    public class SteamTicketAuthenticator : Managers.Server.IClientAuthenticator, INetworkLoopStepCallback
    {
        /// <summary>
        /// The engine this authenticator is registered with, or <see langword="null"/> while it is not.
        /// </summary>
        private CoreManager _coreManager;
        /// <summary>
        /// The transport whose links this authenticator vouches for.
        /// </summary>
        private SteamSockets _steamSockets;
        /// <summary>
        /// The registration for ticket verdicts on the user's interface.
        /// </summary>
        private Callback<ValidateAuthTicketResponse_t> _clientTicketResponseCallback;
        /// <summary>
        /// The registration for ticket verdicts on the game server interface.
        /// </summary>
        private Callback<ValidateAuthTicketResponse_t> _gameServerTicketResponseCallback;
        /// <summary>
        /// The interface the open auth sessions were begun on.
        /// </summary>
        private SteamNetworkingInterface _sessionNetworkingInterface = SteamNetworkingInterface.Client;
        /// <summary>
        /// Connecting clients whose ticket has not arrived yet, with the SteamID their link was authenticated as.
        /// </summary>
        private readonly Dictionary<Connection, ulong> _awaitingTicketSteamIdsByConnection = new();
        /// <summary>
        /// Clients with an open auth session, awaiting Steam's verdict or already approved, by Connection.
        /// </summary>
        private readonly Dictionary<Connection, ulong> _sessionSteamIdsByConnection = new();
        /// <summary>
        /// Clients with an open auth session, by SteamID, which is all a verdict names.
        /// </summary>
        private readonly Dictionary<ulong, Connection> _sessionConnectionsBySteamId = new();
        /// <summary>
        /// Steam's verdicts, raised on its callback thread and applied on the loop thread.
        /// </summary>
        private readonly ConcurrentQueue<ValidateAuthTicketResponse_t> _ticketResponses = new();
        /// <summary>
        /// The buffer this peer's own ticket is written into.
        /// </summary>
        private readonly byte[] _ticketBuffer = new byte[MaximumTicketBytes];
        /// <summary>
        /// The ticket this peer's client last sent, or <see cref="HAuthTicket.Invalid"/> while it has none outstanding.
        /// </summary>
        private HAuthTicket _ownTicket = HAuthTicket.Invalid;
        /// <summary>
        /// The most bytes a Steam auth session ticket takes; Valve's samples size the buffer at 1024.
        /// </summary>
        private const int MaximumTicketBytes = 1024;

        /// <summary>
        /// Registers this as the server's authenticator, and starts sending this peer's own ticket whenever its client links up.
        /// </summary>
        /// <param name="coreManager">The engine to register with.</param>
        /// <param name="steamSockets">The transport whose links are vouched for.</param>
        public void Initialize(CoreManager coreManager, SteamSockets steamSockets)
        {
            _coreManager = coreManager;
            _steamSockets = steamSockets;

            coreManager.ServerManager.SetAuthenticator(this);
            coreManager.ServerManager.ClientDisconnecting += OnClientDisconnecting;
            coreManager.TransportManager.ConnectionLocalStateChanged += OnConnectionLocalStateChanged;
            coreManager.MessageManager.RegisterMessageHandler<SteamAuthTicketMessage>(OnTicketReceived);
            coreManager.MessageManager.RegisterMessageHandler<SteamAuthRevokedNotice>(OnRevokedNoticeReceived);
            coreManager.NetworkLoopManager.RegisterNetworkLoopStepCallbacks(this);

            // Registering touches no native code, so it works before the game has initialized Steam.
            _clientTicketResponseCallback = SteamNetworkingInterface.Client.CreateValidateAuthTicketCallback(OnTicketResponse);
            _gameServerTicketResponseCallback = SteamNetworkingInterface.GameServer.CreateValidateAuthTicketCallback(OnTicketResponse);
        }

        /// <inheritdoc/>
        public void OnClientConnecting(Connection connection)
        {
            if (_steamSockets.IsOwnClientConnection(connection))
            {
                _coreManager.ServerManager.ApproveClient(connection, connection.RemoteAddress);

                return;
            }

            if (!ulong.TryParse(connection.RemoteAddress, out ulong steamId) || steamId == SteamSockets.UnsetSteamId)
            {
                Logger<SteamTicketAuthenticator>.LogWarning($"A client with no SteamID [{connection.RemoteAddress}] was refused.");
                _coreManager.ServerManager.DenyClient(connection);

                return;
            }

            _awaitingTicketSteamIdsByConnection[connection] = steamId;
        }

        /// <summary>
        /// Ends every auth session, cancels this peer's ticket, and restores the engine's default authenticator.
        /// </summary>
        public void Deinitialize()
        {
            if (_coreManager is null)
                return;

            List<Connection> sessionConnections = new(_sessionSteamIdsByConnection.Keys);

            foreach (Connection sessionConnection in sessionConnections)
                EndSession(sessionConnection);

            _awaitingTicketSteamIdsByConnection.Clear();
            CancelOwnTicket();

            _coreManager.ServerManager.SetAuthenticator(clientAuthenticator: null);
            _coreManager.ServerManager.ClientDisconnecting -= OnClientDisconnecting;
            _coreManager.TransportManager.ConnectionLocalStateChanged -= OnConnectionLocalStateChanged;
            _coreManager.MessageManager.UnregisterMessageHandler<SteamAuthTicketMessage>(OnTicketReceived);
            _coreManager.MessageManager.UnregisterMessageHandler<SteamAuthRevokedNotice>(OnRevokedNoticeReceived);
            _coreManager.NetworkLoopManager.UnregisterNetworkLoopStepCallbacks(this);

            _clientTicketResponseCallback.Dispose();
            _gameServerTicketResponseCallback.Dispose();

            _coreManager = null;
            _steamSockets = null;
        }

        /// <inheritdoc/>
        NetworkLoopSteps INetworkLoopStepCallback.GetNetworkLoopSteps() => NetworkLoopSteps.LateVariableUpdate;

        /// <summary>
        /// Applies Steam's queued verdicts.
        /// </summary>
        /// <param name="networkLoopStep">The step being performed.</param>
        /// <param name="stepDelta">Delta information about the step.</param>
        void INetworkLoopStepCallback.OnNetworkLoopStep(NetworkLoopSteps networkLoopStep, StepDelta stepDelta)
        {
            while (_ticketResponses.TryDequeue(out ValidateAuthTicketResponse_t ticketResponse))
                ApplyTicketResponse(ticketResponse);
        }

        /// <summary>
        /// Checks a connecting client's ticket against the SteamID its link was authenticated as, then returns its pooled array.
        /// </summary>
        /// <param name="messageContext">The context the ticket arrived in.</param>
        /// <param name="steamAuthTicketMessage">The ticket.</param>
        private void OnTicketReceived(in MessageContext messageContext, SteamAuthTicketMessage steamAuthTicketMessage)
        {
            ArraySegment<byte> ticketSegment = steamAuthTicketMessage.Ticket;
            Connection connection = messageContext.SenderConnection;

            // A ticket from a client already past this step, or from one this authenticator never saw connect, is ignored.
            if (connection is not null && _awaitingTicketSteamIdsByConnection.Remove(connection, out ulong steamId))
                BeginSession(connection, steamId, ticketSegment);

            // Steam copies the ticket when the session begins, so the pooled array can go back now.
            if (messageContext.IsDeserialized && ticketSegment.Array is not null)
                TrackedArrayPool<byte>.Return(ticketSegment.Array);
        }

        /// <summary>
        /// Begins an auth session for a connecting client's ticket, bound to the SteamID its link was authenticated as.
        /// </summary>
        /// <param name="connection">The client.</param>
        /// <param name="steamId">The SteamID its link was authenticated as.</param>
        /// <param name="ticketSegment">The ticket it sent, valid only for this call.</param>
        private void BeginSession(Connection connection, ulong steamId, ArraySegment<byte> ticketSegment)
        {
            if (ticketSegment.Array is null || ticketSegment.Count == 0)
            {
                Deny(connection, steamId, "sent an empty ticket");

                return;
            }

            // Steam reads a ticket from the start of its array, which a pooled one normally is.
            byte[] ticket = ticketSegment.Offset == 0 ? ticketSegment.Array : ticketSegment.ToArray();
            SteamNetworkingInterface steamNetworkingInterface = _steamSockets.ServerNetworkingInterface;
            EBeginAuthSessionResult beginAuthSessionResult = steamNetworkingInterface.BeginAuthSession(ticket, ticketSegment.Count, new(steamId));

            if (beginAuthSessionResult is not EBeginAuthSessionResult.k_EBeginAuthSessionResultOK)
            {
                Deny(connection, steamId, $"sent a ticket Steam refused: [{beginAuthSessionResult}]");

                return;
            }

            _sessionNetworkingInterface = steamNetworkingInterface;
            _sessionSteamIdsByConnection[connection] = steamId;
            _sessionConnectionsBySteamId[steamId] = connection;
        }

        /// <summary>
        /// Logs the reason a server gave for kicking this client after Steam withdrew its approval.
        /// </summary>
        /// <param name="messageContext">The context the notice arrived in.</param>
        /// <param name="steamAuthRevokedNotice">The notice.</param>
        private void OnRevokedNoticeReceived(in MessageContext messageContext, SteamAuthRevokedNotice steamAuthRevokedNotice) =>
            Logger<SteamTicketAuthenticator>.LogWarning($"The server kicked this client because Steam withdrew its approval: [{steamAuthRevokedNotice.Response}].");

        /// <summary>
        /// Approves or refuses a client as Steam judged its ticket.
        /// </summary>
        /// <param name="ticketResponse">Steam's verdict.</param>
        private void ApplyTicketResponse(ValidateAuthTicketResponse_t ticketResponse)
        {
            ulong steamId = ticketResponse.m_SteamID.m_SteamID;

            if (!_sessionConnectionsBySteamId.TryGetValue(steamId, out Connection connection))
                return;

            EAuthSessionResponse authSessionResponse = ticketResponse.m_eAuthSessionResponse;

            if (authSessionResponse is EAuthSessionResponse.k_EAuthSessionResponseOK)
            {
                if (!connection.IsAuthenticated)
                    _coreManager.ServerManager.ApproveClient(connection, steamId.ToString());

                return;
            }

            // A verdict withdrawn after approval, such as a VAC ban mid-session, kicks the client and tells it why.
            if (connection.IsAuthenticated)
            {
                Logger<SteamTicketAuthenticator>.LogWarning($"Steam withdrew its approval of client [{steamId}]: [{authSessionResponse}], so it was kicked.");
                connection.Kick(new SteamAuthRevokedNotice { Response = authSessionResponse.ToString() });

                return;
            }

            EndSession(connection);
            Deny(connection, steamId, $"was refused by Steam: [{authSessionResponse}]");
        }

        /// <summary>
        /// Sends this peer's ticket when its client links up to another peer's server, and cancels it when the link drops.
        /// </summary>
        /// <param name="connectionStateChange">The state change that was raised.</param>
        private void OnConnectionLocalStateChanged(ConnectionStateChange connectionStateChange)
        {
            if (connectionStateChange.IsRemoteStateChange || connectionStateChange.Invoker is not Invoker.Client)
                return;

            LocalConnectionState localConnectionState = connectionStateChange.Connection.LocalState;

            if (localConnectionState is LocalConnectionState.Disconnected)
                CancelOwnTicket();
            else if (localConnectionState is LocalConnectionState.Connected && !_coreManager.TransportManager.IsServerStarted)
                SendOwnTicket();
        }

        /// <summary>
        /// Ends a departing client's auth session, which Steam would otherwise keep open.
        /// </summary>
        /// <param name="connection">The departing client.</param>
        private void OnClientDisconnecting(Connection connection)
        {
            _awaitingTicketSteamIdsByConnection.Remove(connection);
            EndSession(connection);
        }

        /// <summary>
        /// Writes this peer's ticket, bound to the server it is linked to, and sends it.
        /// </summary>
        private void SendOwnTicket()
        {
            CancelOwnTicket();

            if (!_coreManager.TransportManager.TryGetServerConnection(out Connection serverConnection))
                return;

            /* The ticket is bound to the server's identity, so a server that is handed it cannot replay it to another. Without a Steam
             * link to read it from, the identity stays invalid and the ticket is unbound. */
            _steamSockets.TryGetServerIdentity(out SteamNetworkingIdentity serverIdentity);

            _ownTicket = SteamUser.GetAuthSessionTicket(_ticketBuffer, MaximumTicketBytes, out uint ticketSize, ref serverIdentity);

            if (_ownTicket == HAuthTicket.Invalid)
            {
                Logger<SteamTicketAuthenticator>.LogError("Steam did not issue an auth session ticket, so the server will refuse this client.");

                return;
            }

            serverConnection.SendMessage(Channel.Reliable, new SteamAuthTicketMessage { Ticket = new(_ticketBuffer, 0, (int)ticketSize) });
        }

        /// <summary>
        /// Cancels this peer's outstanding ticket, so it can no longer be used.
        /// </summary>
        private void CancelOwnTicket()
        {
            if (_ownTicket == HAuthTicket.Invalid)
                return;

            if (!SteamNetworkingInterface.Client.IsShutDown())
                SteamUser.CancelAuthTicket(_ownTicket);

            _ownTicket = HAuthTicket.Invalid;
        }

        /// <summary>
        /// Ends a client's auth session, if it has one.
        /// </summary>
        /// <param name="connection">The client.</param>
        private void EndSession(Connection connection)
        {
            if (!_sessionSteamIdsByConnection.Remove(connection, out ulong steamId))
                return;

            _sessionConnectionsBySteamId.Remove(steamId);

            if (!_sessionNetworkingInterface.IsShutDown())
                _sessionNetworkingInterface.EndAuthSession(new(steamId));
        }

        /// <summary>
        /// Refuses a connecting client and logs why.
        /// </summary>
        /// <param name="connection">The client.</param>
        /// <param name="steamId">Its SteamID.</param>
        /// <param name="reason">Why, as the end of a sentence.</param>
        private void Deny(Connection connection, ulong steamId, string reason)
        {
            Logger<SteamTicketAuthenticator>.LogWarning($"Client [{steamId}] {reason}, so it was refused.");
            _coreManager.ServerManager.DenyClient(connection);
        }

        /// <summary>
        /// Queues one of Steam's verdicts. Runs on the thread pumping Steam's callbacks.
        /// </summary>
        /// <param name="ticketResponse">Steam's verdict.</param>
        private void OnTicketResponse(ValidateAuthTicketResponse_t ticketResponse) => _ticketResponses.Enqueue(ticketResponse);
    }
}
#endif
