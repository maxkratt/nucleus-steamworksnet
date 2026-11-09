// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using Nucleus.Managers.Messages;
using Nucleus.Serializers;

namespace Nucleus.Integrations.SteamworksNet.Samples.BanList
{
    /// <summary>
    /// The reason the server gives a client it kicks or refuses.
    /// </summary>
    [NetworkType]
    public struct BanNotice : IMessage
    {
        /// <inheritdoc/>
        /// <remarks>A refusal reaches a client that was never authenticated, so this takes part in the handshake.</remarks>
        public bool IsAuthenticationRequired => false;

        /// <summary>
        /// The reason shown to the player.
        /// </summary>
        public string Reason;
    }
}
#endif
