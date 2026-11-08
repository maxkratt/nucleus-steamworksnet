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
    /// The reason a <see cref="SteamTicketAuthenticator"/> server gives a client it kicks because Steam withdrew its approval mid-session.
    /// </summary>
    /// <remarks>
    /// Steam withdraws an approved ticket when, for example, the account is VAC banned, its licence is revoked, or a Family Sharing owner
    /// starts playing. The client's authenticator logs the reason; a game that wants to show it registers its own handler for this message.
    /// </remarks>
    [NetworkType]
    public struct SteamAuthRevokedNotice : IMessage
    {
        /// <inheritdoc/>
        public bool IsAuthenticationRequired => true;

        /// <summary>
        /// Steam's verdict, the name of its <c>EAuthSessionResponse</c> value.
        /// </summary>
        public string Response;
    }
}
#endif
