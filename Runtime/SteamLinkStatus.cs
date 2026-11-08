// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using Steamworks;

namespace Nucleus.Integrations.SteamworksNet
{
    /// <summary>
    /// Steam's live view of one link: its ping, its quality each way, its rates and what is still queued on it.
    /// </summary>
    /// <remarks>Read through <see cref="SteamSockets.TryGetLinkStatus"/>. Steam measures these itself, beside the engine's own round trip time.</remarks>
    public readonly struct SteamLinkStatus
    {
        /// <summary>
        /// Steam's current estimate of the round trip time, in milliseconds.
        /// </summary>
        public readonly int PingMilliseconds;
        /// <summary>
        /// The share of packets this side received intact, from 0 to 1, or a negative value while Steam has no measurement.
        /// </summary>
        public readonly float LocalQuality;
        /// <summary>
        /// The share of packets the other side received intact, from 0 to 1, or a negative value while Steam has no measurement.
        /// </summary>
        public readonly float RemoteQuality;
        /// <summary>
        /// The bytes a second currently sent.
        /// </summary>
        public readonly float OutBytesPerSecond;
        /// <summary>
        /// The bytes a second currently received.
        /// </summary>
        public readonly float InBytesPerSecond;
        /// <summary>
        /// The send rate Steam has estimated the link can carry, in bytes a second.
        /// </summary>
        public readonly uint SendRateBytesPerSecond;
        /// <summary>
        /// The reliable bytes queued and not yet sent.
        /// </summary>
        /// <remarks>A figure that keeps growing means the link cannot keep up, and a reliable send will soon be refused.</remarks>
        public readonly uint PendingReliableBytes;
        /// <summary>
        /// The unreliable bytes queued and not yet sent.
        /// </summary>
        public readonly uint PendingUnreliableBytes;
        /// <summary>
        /// The reliable bytes sent and not yet acknowledged.
        /// </summary>
        public readonly uint SentUnacknowledgedReliableBytes;

        /// <summary>
        /// Creates a new instance from Steam's own status.
        /// </summary>
        /// <param name="realTimeStatus">The status Steam reported.</param>
        internal SteamLinkStatus(in SteamNetConnectionRealTimeStatus_t realTimeStatus)
        {
            PingMilliseconds = realTimeStatus.m_nPing;
            LocalQuality = realTimeStatus.m_flConnectionQualityLocal;
            RemoteQuality = realTimeStatus.m_flConnectionQualityRemote;
            OutBytesPerSecond = realTimeStatus.m_flOutBytesPerSec;
            InBytesPerSecond = realTimeStatus.m_flInBytesPerSec;
            SendRateBytesPerSecond = (uint)realTimeStatus.m_nSendRateBytesPerSecond;
            PendingReliableBytes = (uint)realTimeStatus.m_cbPendingReliable;
            PendingUnreliableBytes = (uint)realTimeStatus.m_cbPendingUnreliable;
            SentUnacknowledgedReliableBytes = (uint)realTimeStatus.m_cbSentUnackedReliable;
        }
    }
}
#endif
