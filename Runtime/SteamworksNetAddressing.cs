using UnityEngine;

namespace Nucleus.Integrations.SteamworksNet
{
    /// <summary>
    /// What a <see cref="SteamSockets"/> client dials to reach the server, which also decides how the server listens.
    /// </summary>
    public enum SteamworksNetAddressing : byte
    {
        /// <summary>
        /// The host's SteamID and a virtual port, so no port needs opening. With <see cref="SteamSockets.RelayOnlyEnabled"/> the link rides
        /// Valve's relay network and no peer learns another's IP address; without it Steam may connect two players directly.
        /// </summary>
        [InspectorName("Steam ID")]
        SteamId,
        /// <summary>
        /// A real IP address and UDP port, dialled directly, for a dedicated server with a public port, or two processes on one machine.
        /// </summary>
        [InspectorName("IP Address")]
        IpAddress,
        /// <summary>
        /// A fake IP address and port that Steam grants the server, which clients dial like an address while the traffic still rides the
        /// relay, so a dedicated server gets relay protection without exposing its real address.
        /// </summary>
        [InspectorName("Fake IP")]
        FakeIp
    }
}
