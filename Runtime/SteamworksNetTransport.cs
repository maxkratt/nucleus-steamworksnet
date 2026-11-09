// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
using System;
using System.Threading.Tasks;
using Nucleus.Integrations.Unity.Managers.Transports;
using Nucleus.Integrations.Unity.Transports;
using Nucleus.Managers.Core;
using Nucleus.Transports;
using UnityEngine;

namespace Nucleus.Integrations.SteamworksNet
{
    /// <summary>
    /// Inspector-configurable component for the <see cref="SteamSockets"/> transport. Place it on the UnityCoreManager's GameObject;
    /// the <see cref="UnityTransportManager"/> adds it to the CoreManager with these settings applied.
    /// </summary>
    /// <remarks>
    /// A server needs nothing but Steam initialized: the user's interface for a listen server, <c>GameServer.Init</c> for a dedicated
    /// one. A peer-to-peer client needs <see cref="ServerSteamId"/>, usually the owner of a Steam lobby or a friend's rich presence; an
    /// address or fake IP client needs <see cref="ServerAddress"/>. Left unset on a peer that is also the server, the client joins its
    /// own server in process. Steam itself is initialized and pumped by the game, never by this component.
    /// </remarks>
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/maxkratt/nucleus-steamworksnet#steamworksnettransport")]
    public class SteamworksNetTransport : NetworkTransport
    {
        /// <summary>
        /// The SteamID of the peer hosting the session, which a client connects to.
        /// </summary>
        /// <remarks>Accepted at any time and applied at the next client start, including to the transport an earlier start built.</remarks>
        public ulong ServerSteamId
        {
            get => _serverSteamId;
            set
            {
                _serverSteamId = value;
#if NUCLEUS_STEAMWORKS_ENABLED

                if (_steamSockets is not null)
                    _steamSockets.ServerSteamId = value;
#endif
            }
        }
        /// <summary>
        /// The address and port a client connects to in <see cref="SteamworksNetAddressing.IpAddress"/> and <see cref="SteamworksNetAddressing.FakeIp"/>.
        /// </summary>
        /// <remarks>Accepted at any time and applied at the next client start, including to the transport an earlier start built.</remarks>
        public string ServerAddress
        {
            get => _serverAddress;
            set
            {
                _serverAddress = value;
#if NUCLEUS_STEAMWORKS_ENABLED

                if (_steamSockets is not null)
                    _steamSockets.ServerAddress = value;
#endif
            }
        }
        /// <summary>
        /// The local address an <see cref="SteamworksNetAddressing.IpAddress"/> server listens on: an IP address, <c>localhost</c>, or empty to listen on every interface.
        /// </summary>
        /// <remarks>
        /// Accepted at any time and applied at the next server start, including to the transport an earlier start built. A value that is
        /// not an IP address or <c>localhost</c> is logged and not applied.
        /// </remarks>
        public string BindAddress
        {
            get => _bindAddress;
            set
            {
                _bindAddress = value;
#if NUCLEUS_STEAMWORKS_ENABLED

                if (_steamSockets is not null)
                    _steamSockets.SetBindAddress(value);
#endif
            }
        }
#if NUCLEUS_STEAMWORKS_ENABLED
        /// <summary>
        /// Consulted for each connecting client before it waits for a ticket, or <see langword="null"/> to admit every client to the ticket check.
        /// </summary>
        /// <remarks>Used only while Ticket Authentication is on. Accepted at any time and applied to the authenticator this component registers.</remarks>
        public SteamTicketAuthenticator.ClientAdmissionFilterHandler ClientAdmissionFilter
        {
            get => _clientAdmissionFilter;
            set
            {
                _clientAdmissionFilter = value;

                if (_steamTicketAuthenticator is not null)
                    _steamTicketAuthenticator.ClientAdmissionFilter = value;
            }
        }
#endif
        /// <summary>
        /// The message logged, and thrown from a start, while Steamworks.NET is not installed.
        /// </summary>
        public const string SteamworksMissingText = "Steamworks.NET is not installed, so the Steam transport is compiled out and cannot start. Select the SteamworksNetTransport component to install it.";
        /// <summary>
        /// The message logged, and thrown from a start, while Steamworks.NET is installed but compiled out, for the build target or by <c>DISABLESTEAMWORKS</c>.
        /// </summary>
        public const string SteamworksDisabledText = "Steamworks.NET is compiled out for the current build target, or by DISABLESTEAMWORKS, so the Steam transport is too and cannot start. Switch to a Windows, macOS or Linux build target, or remove DISABLESTEAMWORKS.";
        /// <summary>
        /// The fewest connections the inspector lets a server accept: one, the host's own client or a single remote one.
        /// </summary>
        /// <remarks>
        /// Zero is left out because the engine reads it as <see cref="ServerConfiguration.UnsetMaximumConnections"/>, any number, on
        /// every transport. There is no upper bound: Steam sets none on a listen socket, so a dedicated server is limited only by its
        /// bandwidth and processor.
        /// </remarks>
        public const uint MinimumMaximumConnections = 1;
        /// <summary>
        /// How the server is reached.
        /// </summary>
        /// <remarks>Carried to <see cref="SteamSockets.Addressing"/>.</remarks>
        [SerializeField]
        [Tooltip("How the server is reached: by SteamID through Steam's relay, by IP address, or by a fake IP Steam grants a game server.")]
        private SteamworksNetAddressing _addressing;
        /// <summary>
        /// True to run the server on Steam's game server interface rather than the logged-in user's.
        /// </summary>
        /// <remarks>Carried to <see cref="SteamSockets.GameServerEnabled"/>. A dedicated server turns it on.</remarks>
        [SerializeField]
        [Tooltip("Run the server as a Steam game server, for a dedicated server, which needs GameServer.Init. Off runs it as the logged-in Steam user. A client always runs as the user.")]
        private bool _gameServerEnabled;
        /// <summary>
        /// The SteamID64 of the server a client connects to.
        /// </summary>
        /// <remarks>
        /// Carried to <see cref="SteamSockets.ServerSteamId"/>. <see cref="SteamSockets.UnsetSteamId"/> joins this peer's own server. Ignored
        /// while this peer runs a server, whose client always joins it.
        /// </remarks>
        [SerializeField]
        [Tooltip("SteamID64 of the server a client connects to, such as 76561198012345678. Zero joins this peer's own server. Ignored while this peer runs a server; its client joins that server.")]
        private ulong _serverSteamId;
        /// <summary>
        /// The address and port a client connects to in <see cref="SteamworksNetAddressing.IpAddress"/> and <see cref="SteamworksNetAddressing.FakeIp"/>.
        /// </summary>
        /// <remarks>
        /// Carried to <see cref="SteamSockets.ServerAddress"/>. A fake address is the one the server logs and reports as 
        /// <see cref="SteamSockets.ServerFakeAddress"/>. Ignored while this peer runs a server, whose client always joins it.
        /// </remarks>
        [SerializeField]
        [Tooltip("IP address and port a client connects to, such as 203.0.113.7:27015; for a fake IP, the address the server logs. Host names are not looked up. Without a port, Port is used. Empty joins this peer's own server. Ignored while this peer runs a server; its client joins that server.")]
        private string _serverAddress = string.Empty;
        /// <summary>
        /// The port server and clients agree on: the Steam virtual port in <see cref="SteamworksNetAddressing.SteamId"/>, the UDP port in
        /// <see cref="SteamworksNetAddressing.IpAddress"/>.
        /// </summary>
        /// <remarks>
        /// Carried through <see cref="SteamSockets.SetPort"/>, which sets both <see cref="SteamSockets.VirtualPort"/> and
        /// <see cref="Configuration.Port"/>. An <see cref="SteamworksNetAddressing.IpAddress"/> client uses it when <see cref="ServerAddress"/> names
        /// no port. <see cref="SteamworksNetAddressing.FakeIp"/> ignores it, since Steam grants the server its port.
        /// </remarks>
        [SerializeField]
        [Tooltip("Port server and clients agree on: Steam's virtual port by SteamID, the UDP port the server listens on by IP address. A client uses it when the server's address has no port.")]
        private ushort _port = 27015;
        /// <summary>
        /// The local IP address an <see cref="SteamworksNetAddressing.IpAddress"/> server listens on.
        /// </summary>
        /// <remarks>Carried through <see cref="Transport.SetBindAddress"/>. Empty listens on every interface; <c>localhost</c> is the IPv4 loopback.</remarks>
        [SerializeField]
        [Tooltip("Local IP address, or localhost, an IP address server listens on. Empty listens on every interface.")]
        private string _bindAddress = string.Empty;
        /// <summary>
        /// Seconds a link may spend connecting before Steam gives up on it, on the client and on the server.
        /// </summary>
        /// <remarks>Carried to <see cref="Configuration.ConnectingTimeoutSeconds"/>, which Steam enforces. Zero keeps Steam's default of 10 seconds.</remarks>
        [SerializeField]
        [Tooltip("Seconds a link may spend connecting before Steam gives up, on both client and server. Zero keeps Steam's default, 10 s.")]
        private ushort _connectingTimeoutSeconds = 10;
        /// <summary>
        /// Seconds a connected peer waits without hearing from the other side before Steam drops the link.
        /// </summary>
        /// <remarks>Carried to <see cref="Configuration.ConnectedTimeoutSeconds"/>, which Steam enforces. Zero keeps Steam's default of 10 seconds.</remarks>
        [SerializeField]
        [Tooltip("Seconds a peer waits without hearing from the other side before the link drops. Zero keeps Steam's default, 10 s.")]
        private ushort _idleTimeoutSeconds = 15;
        /// <summary>
        /// The most clients the server accepts at once, the host's own client included.
        /// </summary>
        /// <remarks>
        /// Carried to <see cref="Transport.SetMaximumConnections"/>, and kept at or above <see cref="MinimumMaximumConnections"/> by the
        /// inspector, so the component never passes the engine's <see cref="ServerConfiguration.UnsetMaximumConnections"/>, which would
        /// accept any number. The host's own client takes one
        /// place, as it does on Synapse: a host set to 4 seats itself and three remote players.
        /// </remarks>
        [SerializeField]
        [Tooltip("Most clients the server accepts at once. A host's own client takes one place, so 4 seats the host and three others.")]
        private uint _maximumConnections = 250;
        /// <summary>
        /// The largest packet a client may send the server, in bytes, before the server disconnects it.
        /// </summary>
        /// <remarks>
        /// Carried to <see cref="SteamSockets.MaximumClientPacketBytes"/>. Defaults to 8 MiB, room for any message a game sends from a
        /// client while keeping a client from growing the server's memory without end. <see cref="SteamSockets.UnsetMaximumClientPacketBytes"/>
        /// sets no limit.
        /// </remarks>
        [SerializeField]
        [Tooltip("Largest packet a client may send the server; a client that sends a larger one is disconnected. Zero sets no limit.")]
        private uint _maximumClientPacketBytes = 8 * 1024 * 1024;
        /// <summary>
        /// True to carry every Steam ID link over Valve's relay network only, so no peer learns another's IP address.
        /// </summary>
        /// <remarks>
        /// Carried to <see cref="SteamSockets.RelayOnlyEnabled"/>. Used only by <see cref="SteamworksNetAddressing.SteamId"/>; the other
        /// addressings either dial an address or always ride the relay.
        /// </remarks>
        [SerializeField]
        [Tooltip("Carry Steam ID links over Valve's relay only, so no player learns another's IP address. Off lets Steam connect players directly when each player's Steam settings allow it, which can lower ping.")]
        private bool _relayOnlyEnabled = true;
        /// <summary>
        /// The send rate Steam never rises above on a connection, in bytes per second.
        /// </summary>
        /// <remarks>
        /// Carried to <see cref="SteamSockets.SendRateMaximumBytesPerSecond"/>. Defaults to 4 MiB a second rather than Steam's 256 KiB,
        /// so a late join or a large burst can use a good link while Steam's estimate still backs off on a poor one; at 1 MiB a second
        /// a large late join sat at the ceiling. The minimum is left at Steam's 256 KiB: raising it made no difference to a 3.2 MiB late
        /// join, and a higher floor per connection would oversubscribe a listen server's home upload when several clients join at once.
        /// <see cref="SteamSockets.UnsetSendSetting"/> keeps Steam's default.
        /// </remarks>
        [SerializeField]
        [Tooltip("Rate Steam never sends faster than on a connection. Zero keeps Steam's default, 256 KiB/s.")]
        private uint _sendRateMaximumBytesPerSecond = 4 * 1024 * 1024;
        /// <summary>
        /// The most bytes Steam holds queued on a connection before it refuses a send.
        /// </summary>
        /// <remarks>
        /// Carried to <see cref="SteamSockets.SendBufferSizeBytes"/>. Reliable messages past it wait in the transport until Steam has
        /// room, so only a single message larger than the whole buffer is refused, and that closes the link. Defaults to 2 MiB rather
        /// than Steam's 512 KiB so a large late join or scene load fits. <see cref="SteamSockets.UnsetSendSetting"/> keeps Steam's default.
        /// </remarks>
        [SerializeField]
        [Tooltip("Data Steam may hold queued per connection. Reliable messages past it wait; only one message larger than it closes the link. Zero keeps Steam's default, 512 KiB.")]
        private uint _sendBufferSizeBytes = 2 * 1024 * 1024;
        /// <summary>
        /// True to admit a client only once Steam has confirmed its auth session ticket, through a <see cref="SteamTicketAuthenticator"/>.
        /// </summary>
        /// <remarks>Server and clients both need it on, since the client is the side that sends the ticket. A server with it on drops a client with it off once the engine's authentication timeout passes; a server with it off admits a client with it on unchecked.</remarks>
        [SerializeField]
        [Tooltip("Admit a client only once Steam confirms its ticket: it owns the game and is not banned. Server and clients both need it on; a client with it off is dropped after the authentication timeout.")]
        private bool _ticketAuthenticationEnabled;
        /// <summary>
        /// True for the server to tell every client the SteamID of every other client.
        /// </summary>
        /// <remarks>Carried to <see cref="SteamSockets.ShareSteamIdsEnabled"/>. Only the server's value counts.</remarks>
        [SerializeField]
        [Tooltip("Tell every client the SteamID of every other client, for names, avatars and friend checks. SteamIDs link to public Steam profiles, so leave it off where players should not learn who the others are. Only the server's value counts.")]
        private bool _shareSteamIdsEnabled;
#if NUCLEUS_STEAMWORKS_ENABLED
        /// <summary>
        /// The transport this component configured, or <see langword="null"/> before it has.
        /// </summary>
        private SteamSockets _steamSockets;
        /// <summary>
        /// The authenticator this component registered, or <see langword="null"/> when ticket authentication is off.
        /// </summary>
        private SteamTicketAuthenticator _steamTicketAuthenticator;
        /// <summary>
        /// The filter given to the ticket authenticator, or <see langword="null"/> for none.
        /// </summary>
        private SteamTicketAuthenticator.ClientAdmissionFilterHandler _clientAdmissionFilter;
#endif

#if NUCLEUS_STEAMWORKS_ENABLED
        /// <inheritdoc/>
        public override async Task<Transport> AddToAsync(CoreManager coreManager)
        {
            _steamSockets = (SteamSockets)await coreManager.TransportManager.AddTransportAsync<SteamSockets>();

            _steamSockets.Addressing = _addressing;
            _steamSockets.GameServerEnabled = _gameServerEnabled;
            _steamSockets.ServerSteamId = _serverSteamId;
            _steamSockets.ServerAddress = _serverAddress;
            _steamSockets.SetPort(_port);
            _steamSockets.SetBindAddress(_bindAddress);
            _steamSockets.Configuration.ConnectingTimeoutSeconds = _connectingTimeoutSeconds;
            _steamSockets.Configuration.ConnectedTimeoutSeconds = _idleTimeoutSeconds;
            _steamSockets.SetMaximumConnections(_maximumConnections);
            _steamSockets.MaximumClientPacketBytes = _maximumClientPacketBytes;
            _steamSockets.RelayOnlyEnabled = _relayOnlyEnabled;
            _steamSockets.ShareSteamIdsEnabled = _shareSteamIdsEnabled;
            _steamSockets.SendRateMaximumBytesPerSecond = _sendRateMaximumBytesPerSecond;
            _steamSockets.SendBufferSizeBytes = _sendBufferSizeBytes;

            if (_ticketAuthenticationEnabled)
            {
                _steamTicketAuthenticator = new() { ClientAdmissionFilter = _clientAdmissionFilter };
                _steamTicketAuthenticator.Initialize(coreManager, _steamSockets);
            }

            return _steamSockets;
        }

        /// <summary>
        /// Unregisters the ticket authenticator, if this component registered one.
        /// </summary>
        private void OnDestroy()
        {
            _steamTicketAuthenticator?.Deinitialize();
            _steamTicketAuthenticator = null;
        }
#elif NUCLEUS_STEAMWORKS
        /// <inheritdoc/>
        /// <remarks>Steamworks.NET is compiled out here, so there is no transport to add; the transport manager logs the refusal and skips it.</remarks>
        public override Task<Transport> AddToAsync(CoreManager coreManager) => throw new InvalidOperationException(SteamworksDisabledText);
#else
        /// <inheritdoc/>
        /// <remarks>Steamworks.NET is not installed, so there is no transport to add; the transport manager logs the refusal and skips it.</remarks>
        public override Task<Transport> AddToAsync(CoreManager coreManager) => throw new InvalidOperationException(SteamworksMissingText);
#endif
    }
}
