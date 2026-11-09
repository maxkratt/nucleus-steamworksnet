// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using Steamworks;
using UnityEngine;

namespace Nucleus.Integrations.SteamworksNet.Samples.QuickMatch
{
    /// <summary>
    /// Carries the matched lobby from the menu scene into the game scene, and holds the names both scenes agree on.
    /// </summary>
    /// <remarks>
    /// This is static because samples may not use <c>DontDestroyOnLoad</c> and nothing else survives the scene load. It is reset from
    /// <see cref="RuntimeInitializeLoadType.SubsystemRegistration"/>, so it never carries a lobby into a later play session.
    /// </remarks>
    public static class QuickMatchSession
    {
        /// <summary>
        /// The lobby the game scene plays from, or <see cref="CSteamID.Nil"/> outside a match.
        /// </summary>
        public static CSteamID LobbySteamId = CSteamID.Nil;
        /// <summary>
        /// The name of the menu scene.
        /// </summary>
        public const string MenuSceneName = "QuickMatch";
        /// <summary>
        /// The name of the game scene.
        /// </summary>
        public const string GameSceneName = "QuickMatchGame";
        /// <summary>
        /// The lobby data key naming the SteamID that will host the match.
        /// </summary>
        public const string HostKey = "host";

        /// <summary>
        /// Clears the lobby at the head of each play session.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() => LobbySteamId = CSteamID.Nil;
    }
}
#endif
