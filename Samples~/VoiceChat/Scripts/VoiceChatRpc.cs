// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using System;
using Nucleus.Managers.Rpc;
using Nucleus.Serializers;

namespace Nucleus.Integrations.SteamworksNet.Samples.VoiceChat
{
    /// <summary>
    /// One chunk of a player's voice, compressed by Steam, sent on that player's own object to everyone who observes it.
    /// </summary>
    [NetworkType]
    public struct VoiceChatRpc : IRpc
    {
        /// <summary>
        /// The voice as <c>SteamUser.GetVoice</c> wrote it, which <c>SteamUser.DecompressVoice</c> turns back into PCM.
        /// </summary>
        /// <remarks>
        /// A segment rather than a <c>byte[]</c>: the sender points it into its recording buffer, and a receiver reads it into an array
        /// rented from <c>TrackedArrayPool</c> that its handler returns, so a chunk allocates nothing on either side.
        /// </remarks>
        public ArraySegment<byte> CompressedVoice;
    }
}
#endif
