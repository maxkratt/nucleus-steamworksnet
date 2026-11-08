// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using Nucleus.Components;
using Nucleus.Connections;
using Nucleus.Integrations.Unity.Systems;
using Nucleus.Transports;
using Steamworks;
using UnityEngine;

namespace Nucleus.Integrations.SteamworksNet.Samples
{
    /// <summary>
    /// Draws the Steam persona name of the player who controls this object above it, and a speaking icon over the name while
    /// <see cref="IsSpeaking"/> is set.
    /// </summary>
    /// <remarks>
    /// The server writes the controller's SteamID into <see cref="PlayerNameTagComponent.SteamId"/> when control is assigned, from the
    /// identity Steam authenticated for the link, so a client cannot claim another player's name. Every client then asks its own Steam
    /// for that SteamID's persona name, which Steam may have to fetch first and reports through <see cref="PersonaStateChange_t"/>.
    /// The behaviour declares its own system beside the object's other one, grouped under the same
    /// <see cref="NetworkSystemObject"/>, so the player spawner hands both to the player together.
    /// </remarks>
    [HelpURL("https://github.com/maxkratt/nucleus-steamworksnet/blob/main/Samples~/Shared/README.md")]
    public class PlayerNameTag : NucleusBehaviour<PlayerNameTagComponent>
    {
        /// <summary>
        /// Whether the player is talking, which shows the speaking icon. Set each change by whatever knows, such as a voice chat
        /// script.
        /// </summary>
        public bool IsSpeaking;

        /// <summary>
        /// The skin whose <c>NameTag</c> style draws the tag.
        /// </summary>
        /// <remarks>Left empty, or without that style, the tag draws as a plain box.</remarks>
        [SerializeField]
        [Tooltip("The skin whose NameTag style draws the tag. Leave empty for Unity's default look.")]
        private GUISkin _guiSkin;
        /// <summary>
        /// How far above the object's origin the tag sits, in metres.
        /// </summary>
        [SerializeField]
        [Tooltip("How far above the object's origin the tag sits, in metres.")]
        private float _heightOffset = 1.4f;
        /// <summary>
        /// The icon drawn over the name while <see cref="IsSpeaking"/> is set, or none for players who never speak.
        /// </summary>
        [SerializeField]
        [Tooltip("The icon drawn over the name while the player is talking. Leave empty if the player never talks.")]
        private Texture2D _speakingIcon;
        /// <summary>
        /// The size the speaking icon is drawn at, in pixels.
        /// </summary>
        [SerializeField]
        [Tooltip("The size the speaking icon is drawn at, in pixels.")]
        private float _speakingIconSize = 28f;

        /// <summary>
        /// The camera the tag is placed for, the scene's main camera.
        /// </summary>
        private Camera _camera;
        /// <summary>
        /// Steam's report that a user's persona changed, which carries a name this peer asked for.
        /// </summary>
        private Callback<PersonaStateChange_t> _personaStateChangeCallback;
        /// <summary>
        /// The name drawn on the tag, empty while there is none to show.
        /// </summary>
        private string _displayName = string.Empty;
        /// <summary>
        /// The content drawn on the tag, rebuilt only when <see cref="_displayName"/> changes.
        /// </summary>
        private readonly GUIContent _nameContent = new();

        /// <summary>
        /// The skin style the tag draws with.
        /// </summary>
        private const string NameTagStyleName = "NameTag";
        /// <summary>
        /// The gap between the speaking icon and the top of the tag, in pixels.
        /// </summary>
        private const float SpeakingIconGap = 2f;

        /// <summary>
        /// Finds the camera and listens for persona names, then lets the base adopt the system.
        /// </summary>
        /// <remarks>Both are set first because on a receiving client the system can already be linked when this runs.</remarks>
        protected override void Awake()
        {
            _camera = Camera.main;

            if (_camera == null)
                Debug.LogError($"[{nameof(PlayerNameTag)}] found no main camera, so the tag on [{name}] is not drawn.");

            _personaStateChangeCallback = Callback<PersonaStateChange_t>.Create(OnPersonaStateChanged);

            base.Awake();
        }

        /// <summary>
        /// Stops listening for persona names.
        /// </summary>
        protected override void OnDestroy()
        {
            base.OnDestroy();

            _personaStateChangeCallback?.Dispose();
        }

        /// <summary>
        /// Follows the SteamID and shows whatever it already holds.
        /// </summary>
        protected override void OnSystemLinked()
        {
            Component.MembersChanged += OnNameTagMembersChanged;
            RefreshDisplayName();
        }

        /// <summary>
        /// Drops the subscription and the name, so a pooled object starts its next life without a tag.
        /// </summary>
        protected override void OnSystemUnlinked()
        {
            Component.MembersChanged -= OnNameTagMembersChanged;
            SetDisplayName(string.Empty);
            IsSpeaking = false;
        }

        /// <summary>
        /// Writes the new controller's SteamID on the server.
        /// </summary>
        /// <param name="previousControllerConnection">The previous controlling Connection, or null.</param>
        /// <param name="currentControllerConnection">The current controlling Connection, or null.</param>
        /// <remarks>The server knows every client's SteamID, the host's own included, so this works whether or not the transport shares them with clients.</remarks>
        protected override void OnControllerChanged(Connection previousControllerConnection, Connection currentControllerConnection)
        {
            if (!IsStarted(Invoker.Server))
                return;

            if (currentControllerConnection?.Transport is SteamSockets steamSockets && steamSockets.TryGetSteamId(currentControllerConnection, out ulong steamId))
                Component.SteamId.Value = steamId;
            else
                Component.SteamId.Value = SteamSockets.UnsetSteamId;
        }

        /// <summary>
        /// Shows the name of whichever SteamID arrived or was written.
        /// </summary>
        /// <param name="memberFlags">One bit per member carried.</param>
        /// <param name="memberChangeDirection">Whether this peer wrote the members or received them.</param>
        private void OnNameTagMembersChanged(ulong memberFlags, MemberChangeDirection memberChangeDirection) => RefreshDisplayName();

        /// <summary>
        /// Shows a persona name Steam has just fetched, when it belongs to this tag's player.
        /// </summary>
        /// <param name="personaStateChange">Whose persona changed and what changed.</param>
        private void OnPersonaStateChanged(PersonaStateChange_t personaStateChange)
        {
            if (Component is not null && personaStateChange.m_ulSteamID == Component.SteamId.Value)
                RefreshDisplayName();
        }

        /// <summary>
        /// Draws the tag above the object, and the speaking icon over it while the player talks.
        /// </summary>
        private void OnGUI()
        {
            if (_displayName.Length == 0 || _camera == null)
                return;

            Vector3 screenPoint = _camera.WorldToScreenPoint(transform.position + Vector3.up * _heightOffset);

            // Behind the camera.
            if (screenPoint.z < 0f)
                return;

            GUI.skin = _guiSkin;
            GUIStyle nameTagStyle = GUI.skin.FindStyle(NameTagStyleName) ?? GUI.skin.box;
            Vector2 tagSize = nameTagStyle.CalcSize(_nameContent);
            // GUI space grows downwards from the top, where screen space grows upwards from the bottom.
            Rect tagRect = new(screenPoint.x - tagSize.x * 0.5f, Screen.height - screenPoint.y - tagSize.y, tagSize.x, tagSize.y);
            GUI.Label(tagRect, _nameContent, nameTagStyle);

            if (IsSpeaking && _speakingIcon != null)
                GUI.DrawTexture(new(screenPoint.x - _speakingIconSize * 0.5f, tagRect.y - _speakingIconSize - SpeakingIconGap, _speakingIconSize, _speakingIconSize), _speakingIcon);
        }

        /// <summary>
        /// Shows the persona name of the SteamID the component holds, asking Steam to fetch it when it is not known yet.
        /// </summary>
        /// <remarks>
        /// A peer that runs no client draws nothing, and a dedicated server's Steam is a game server, which keeps no friends list.
        /// </remarks>
        private void RefreshDisplayName()
        {
            ulong steamId = Component.SteamId.Value;

            if (steamId == SteamSockets.UnsetSteamId || !IsStarted(Invoker.Client))
            {
                SetDisplayName(string.Empty);

                return;
            }

            CSteamID personaSteamId = new(steamId);

            if (personaSteamId == SteamUser.GetSteamID())
            {
                SetDisplayName(SteamFriends.GetPersonaName());

                return;
            }

            // True means Steam is fetching the name and reports it through OnPersonaStateChanged.
            bool isFetching = SteamFriends.RequestUserInformation(personaSteamId, bRequireNameOnly: true);
            SetDisplayName(isFetching ? steamId.ToString() : SteamFriends.GetFriendPersonaName(personaSteamId));
        }

        /// <summary>
        /// Sets the name the tag draws.
        /// </summary>
        /// <param name="displayName">The name, or empty to hide the tag.</param>
        private void SetDisplayName(string displayName)
        {
            _displayName = displayName;
            _nameContent.text = displayName;
        }
    }
}
#endif
