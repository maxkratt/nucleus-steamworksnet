// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using Nucleus.Managers.Messages;
using Nucleus.Serializers;

namespace Nucleus.Integrations.SteamworksNet
{
    /// <summary>
    /// The server's word to a client that a connected client has a SteamID, or has left, sent while <see cref="SteamSockets.ShareSteamIdsEnabled"/> is on.
    /// </summary>
    [NetworkType]
    public struct SteamIdNotice : IMessage
    {
        /// <summary>
        /// The <see cref="Connections.Connection.Id"/> of the client this is about.
        /// </summary>
        public uint ConnectionId;
        /// <summary>
        /// The client's SteamID64 as Steam authenticated its link, or <see cref="SteamSockets.UnsetSteamId"/> when the client has left.
        /// </summary>
        public ulong SteamId;
    }
}
#endif
