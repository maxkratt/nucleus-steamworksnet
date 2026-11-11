// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using System;
using System.Collections.Generic;
using Nucleus.Connections;
using Nucleus.Integrations.Unity;
using Nucleus.Integrations.Unity.Managers.Transports;
using Nucleus.Managers.Transports;
using Nucleus.Transports;
using Steamworks;
using UnityEngine;

namespace Nucleus.Integrations.SteamworksNet.Samples.Lobbies
{
    /// <summary>
    /// The Lobbies panel: host a public, friends-only or private Steam lobby, invite through the overlay, join a friend's lobby, a
    /// typed lobby ID or the lobby Steam names with <c>+connect_lobby</c>, see the members with their names and avatars, and read the
    /// game mode and map the host sets as lobby data.
    /// </summary>
    /// <remarks>
    /// The lobby only finds the host; the session itself runs over <see cref="SteamworksNetTransport"/>, whose
    /// <see cref="SteamworksNetTransport.ServerSteamId"/> a member sets to the lobby's game server before starting its client. The host
    /// starts its server, creates the lobby, then names itself the lobby's game server, so no member can dial a server that is not yet
    /// listening. When the host leaves, Steam hands the lobby to another member, but the server went with the host, so each member leaves
    /// the lobby once its client stops, and a player who enters later sees that the game server is no longer the owner.
    /// </remarks>
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/maxkratt/nucleus-steamworksnet/blob/master/Samples~/Lobbies/README.md")]
    public class LobbiesPanel : MonoBehaviour
    {
        /// <summary>
        /// The bootstrap that owns Steam.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Steam Bootstrap.")]
        private SteamBootstrap _steamBootstrap;
        /// <summary>
        /// The transport manager the panel starts and stops.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Unity Transport Manager.")]
        private UnityTransportManager _unityTransportManager;
        /// <summary>
        /// The Steam transport, which a member points at the lobby's game server.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Steam Sockets Transport, set to SteamID addressing.")]
        private SteamworksNetTransport _steamSocketsTransport;
        /// <summary>
        /// The most players a lobby holds, the host included.
        /// </summary>
        [SerializeField]
        [Tooltip("Most players a lobby holds, the host included.")]
        private int _maximumLobbyMembers = 8;
        /// <summary>
        /// The skin the panel draws with.
        /// </summary>
        /// <remarks>Left empty, the panel draws with Unity's default look.</remarks>
        [SerializeField]
        [Tooltip("The skin the panel draws with. Leave empty for Unity's default look.")]
        private GUISkin _guiSkin;

        /// <summary>
        /// The lobby this peer is in, or <see cref="CSteamID.Nil"/> when it is in none.
        /// </summary>
        private CSteamID _lobbySteamId = CSteamID.Nil;
        /// <summary>
        /// The index into <see cref="LobbyTypes"/> of the type the next hosted lobby is created as.
        /// </summary>
        private int _lobbyTypeIndex = 1;
        /// <summary>
        /// Whether this peer created the lobby it is in.
        /// </summary>
        private bool _isLobbyOwner;
        /// <summary>
        /// Whether this peer has hosted or begun joining since it last left, which swaps the start buttons for the lobby view.
        /// </summary>
        private bool _hasStarted;
        /// <summary>
        /// Whether this peer's client has connected since it last joined, so a dropped session reads differently from a failed connect.
        /// </summary>
        private bool _hasConnected;
        /// <summary>
        /// Whether this member has started its client since it last left, since entering a lobby and Steam's game server notice can both name the server.
        /// </summary>
        private bool _hasStartedClient;
        /// <summary>
        /// The last thing that happened, shown on the panel.
        /// </summary>
        private string _statusText = "Not in a lobby.";
        /// <summary>
        /// The lobby ID typed into the panel.
        /// </summary>
        private string _lobbyIdText = string.Empty;
        /// <summary>
        /// How far the member list is scrolled, once the lobby holds more members than it shows at once.
        /// </summary>
        private Vector2 _memberScrollPosition;
        /// <summary>
        /// The game mode the host is editing, set as lobby data on Apply.
        /// </summary>
        private string _gameModeText = "Deathmatch";
        /// <summary>
        /// The map the host is editing, set as lobby data on Apply.
        /// </summary>
        private string _mapText = "Arena";
        /// <summary>
        /// The lobby's game mode, as last read from its lobby data.
        /// </summary>
        private string _lobbyGameMode = string.Empty;
        /// <summary>
        /// The lobby's map, as last read from its lobby data.
        /// </summary>
        private string _lobbyMap = string.Empty;
        /// <summary>
        /// The most members the lobby holds, as last read from Steam.
        /// </summary>
        private int _lobbyMemberLimit;
        /// <summary>
        /// The lobby's members, as last read from Steam.
        /// </summary>
        private readonly List<LobbyMember> _lobbyMembers = new();
        /// <summary>
        /// Friends who are in a lobby of this game, as last read when the user pressed Refresh.
        /// </summary>
        private readonly List<LobbyMember> _friendsInLobbies = new();
        /// <summary>
        /// Each user's small avatar, by SteamID, created once Steam has the image.
        /// </summary>
        private readonly Dictionary<ulong, Texture2D> _avatarsBySteamId = new();
        /// <summary>
        /// The transport manager whose state changes tell a member that its client stopped.
        /// </summary>
        private TransportManager _transportManager;
        /// <summary>
        /// The callback that reports a lobby this peer asked for being created, held so it is not collected.
        /// </summary>
        private Callback<LobbyCreated_t> _lobbyCreatedCallback;
        /// <summary>
        /// The callback that reports this peer entering a lobby, held so it is not collected.
        /// </summary>
        private Callback<LobbyEnter_t> _lobbyEnteredCallback;
        /// <summary>
        /// The callback that reports a member entering or leaving this peer's lobby, held so it is not collected.
        /// </summary>
        private Callback<LobbyChatUpdate_t> _lobbyChatUpdatedCallback;
        /// <summary>
        /// The callback that reports a change to the lobby's data, held so it is not collected.
        /// </summary>
        private Callback<LobbyDataUpdate_t> _lobbyDataUpdatedCallback;
        /// <summary>
        /// The callback that reports the lobby's game server being set, held so it is not collected.
        /// </summary>
        private Callback<LobbyGameCreated_t> _lobbyGameCreatedCallback;
        /// <summary>
        /// The callback that reports the user accepting an invite or joining a friend from Steam while the game runs, held so it is not
        /// collected.
        /// </summary>
        private Callback<GameLobbyJoinRequested_t> _gameLobbyJoinRequestedCallback;
        /// <summary>
        /// The callback that reports an avatar Steam had to download, held so it is not collected.
        /// </summary>
        private Callback<AvatarImageLoaded_t> _avatarImageLoadedCallback;
        /// <summary>
        /// The callback that reports a user's name or avatar changing, held so it is not collected.
        /// </summary>
        private Callback<PersonaStateChange_t> _personaStateChangedCallback;

        /// <summary>
        /// The lobby types the host chooses between, in the order the panel shows them.
        /// </summary>
        private static readonly ELobbyType[] LobbyTypes = { ELobbyType.k_ELobbyTypePublic, ELobbyType.k_ELobbyTypeFriendsOnly, ELobbyType.k_ELobbyTypePrivate };
        /// <summary>
        /// The names of <see cref="LobbyTypes"/>, as the panel shows them.
        /// </summary>
        private static readonly string[] LobbyTypeNames = { "Public", "Friends only", "Private" };

        /// <summary>
        /// The lobby data key the host sets the game mode under.
        /// </summary>
        private const string GameModeKey = "game_mode";
        /// <summary>
        /// The lobby data key the host sets the map under.
        /// </summary>
        private const string MapKey = "map";
        /// <summary>
        /// The launch argument Steam passes a game it starts to accept a lobby invite, followed by the lobby's ID.
        /// </summary>
        private const string ConnectLobbyArgument = "+connect_lobby";
        /// <summary>
        /// The avatar handle Steam returns while it is still downloading the image; <see cref="AvatarImageLoaded_t"/> follows.
        /// </summary>
        private const int LoadingAvatarHandle = -1;
        /// <summary>
        /// The avatar handle Steam returns when the user has no avatar.
        /// </summary>
        private const int UnsetAvatarHandle = 0;
        /// <summary>
        /// How many members the list shows before it scrolls.
        /// </summary>
        private const int VisibleMemberRowCount = 4;
        /// <summary>
        /// The member list's height once it scrolls, in pixels: four rows of thirty.
        /// </summary>
        private const float MemberListHeight = 120f;

        /// <summary>
        /// Registers the Steam callbacks and the transport subscription, then enters the lobby named on the command line, if any.
        /// </summary>
        private void Awake()
        {
            if (_steamBootstrap == null || _unityTransportManager == null || _steamSocketsTransport == null || NucleusUnity.BoundCoreManager is null)
            {
                Debug.LogError($"[{nameof(LobbiesPanel)}] needs its [{nameof(SteamBootstrap)}], [{nameof(UnityTransportManager)}] and [{nameof(SteamworksNetTransport)}] set, and a bound CoreManager.");
                enabled = false;

                return;
            }

            // The bootstrap wakes first, so Steam is up here or never will be.
            if (!_steamBootstrap.IsSteamInitialized)
            {
                enabled = false;

                return;
            }

            _transportManager = NucleusUnity.BoundCoreManager.TransportManager;
            _transportManager.ConnectionLocalStateChanged += OnConnectionLocalStateChanged;
            _steamBootstrap.SteamShutdownStarted += OnSteamShutdownStarted;

            _lobbyCreatedCallback = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
            _lobbyEnteredCallback = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
            _lobbyChatUpdatedCallback = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdated);
            _lobbyDataUpdatedCallback = Callback<LobbyDataUpdate_t>.Create(OnLobbyDataUpdated);
            _lobbyGameCreatedCallback = Callback<LobbyGameCreated_t>.Create(OnLobbyGameCreated);
            _gameLobbyJoinRequestedCallback = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
            _avatarImageLoadedCallback = Callback<AvatarImageLoaded_t>.Create(OnAvatarImageLoaded);
            _personaStateChangedCallback = Callback<PersonaStateChange_t>.Create(OnPersonaStateChanged);

            if (TryGetLaunchLobby(out CSteamID launchLobbySteamId))
                JoinLobby(launchLobbySteamId);
        }

        /// <summary>
        /// Draws the panel.
        /// </summary>
        private void OnGUI()
        {
            SampleGUI.BeginPanel(_guiSkin, 404f, "Lobbies");

            if (!_steamBootstrap.IsSteamInitialized)
            {
                GUILayout.Label("Steam is not running, so this peer cannot host or join.");
                SampleGUI.EndPanel();

                return;
            }

            GUILayout.Label(_statusText);

            if (_hasStarted)
                DrawLobby();
            else
                DrawStart();

            SampleGUI.EndPanel();
        }

        /// <summary>
        /// Draws the lobby type and Host, the friends in lobbies, and joining by a typed lobby ID.
        /// </summary>
        private void DrawStart()
        {
            SampleGUI.Section("Host");
            _lobbyTypeIndex = GUILayout.Toolbar(_lobbyTypeIndex, LobbyTypeNames);

            if (GUILayout.Button("Host lobby"))
                Host();

            SampleGUI.Section("Friends in a lobby");

            if (GUILayout.Button("Refresh friends"))
                RefreshFriendsInLobbies();

            foreach (LobbyMember friend in _friendsInLobbies)
            {
                _avatarsBySteamId.TryGetValue(friend.SteamId.m_SteamID, out Texture2D avatar);

                if (SampleGUI.PlayerRow(avatar, friend.Name, buttonText: "Join"))
                    JoinLobby(friend.LobbySteamId);
            }

            SampleGUI.Section("Join by lobby ID");
            _lobbyIdText = GUILayout.TextField(_lobbyIdText);

            if (GUILayout.Button("Join lobby"))
            {
                if (ulong.TryParse(_lobbyIdText.Trim(), out ulong lobbyId))
                    JoinLobby(new(lobbyId));
                else
                    _statusText = "That is not a lobby ID.";
            }
        }

        /// <summary>
        /// Draws the lobby: invite, the lobby data, the members and Leave.
        /// </summary>
        /// <remarks>The host edits the lobby data in place; a member sees it as one line, since it cannot change it.</remarks>
        private void DrawLobby()
        {
            if (_lobbySteamId != CSteamID.Nil)
            {
                GUILayout.BeginHorizontal();

                if (GUILayout.Button("Copy lobby ID"))
                    GUIUtility.systemCopyBuffer = _lobbySteamId.m_SteamID.ToString();

                if (GUILayout.Button("Invite friends"))
                    SteamFriends.ActivateGameOverlayInviteDialog(_lobbySteamId);

                GUILayout.EndHorizontal();

                SampleGUI.Section("Lobby data");

                if (_isLobbyOwner)
                {
                    _gameModeText = SampleGUI.Field("Game mode", _gameModeText);
                    _mapText = SampleGUI.Field("Map", _mapText);

                    if (GUILayout.Button("Apply lobby data"))
                        SetLobbyData();
                }
                else
                {
                    GUILayout.Label(_lobbyGameMode.Length == 0 && _lobbyMap.Length == 0 ? "The host has set none." : $"{_lobbyGameMode} on {_lobbyMap}");
                }

                SampleGUI.Section($"Members {_lobbyMembers.Count} / {_lobbyMemberLimit}");

                // A full lobby would push Leave off a small window, so a long member list scrolls.
                bool isMemberListScrolling = _lobbyMembers.Count > VisibleMemberRowCount;

                if (isMemberListScrolling)
                    _memberScrollPosition = GUILayout.BeginScrollView(_memberScrollPosition, GUILayout.Height(MemberListHeight));

                foreach (LobbyMember lobbyMember in _lobbyMembers)
                {
                    _avatarsBySteamId.TryGetValue(lobbyMember.SteamId.m_SteamID, out Texture2D avatar);
                    SampleGUI.PlayerRow(avatar, lobbyMember.Name, badge: lobbyMember.IsOwner ? "HOST" : null);
                }

                if (isMemberListScrolling)
                    GUILayout.EndScrollView();
            }

            GUILayout.Space(8f);

            if (GUILayout.Button("Leave"))
                Leave("Left the lobby.");
        }

        /// <summary>
        /// Starts this peer as a host, then creates the lobby players find it through.
        /// </summary>
        private async void Host()
        {
            _hasStarted = true;
            _statusText = "Starting the server.";

            await _unityTransportManager.StartHostAsync();

            _statusText = "Creating the lobby.";
            SteamMatchmaking.CreateLobby(LobbyTypes[_lobbyTypeIndex], _maximumLobbyMembers);
        }

        /// <summary>
        /// Asks Steam to enter a lobby; the client starts once the lobby names its game server.
        /// </summary>
        /// <param name="lobbySteamId">The lobby to enter.</param>
        private void JoinLobby(CSteamID lobbySteamId)
        {
            _hasStarted = true;
            _statusText = $"Entering lobby [{lobbySteamId.m_SteamID}].";

            SteamMatchmaking.JoinLobby(lobbySteamId);
        }

        /// <summary>
        /// Leaves the lobby and stops every role this peer runs.
        /// </summary>
        /// <param name="statusText">What the panel shows afterwards.</param>
        private void Leave(string statusText)
        {
            if (_lobbySteamId != CSteamID.Nil)
                SteamMatchmaking.LeaveLobby(_lobbySteamId);

            // Cleared before the shutdown, whose client stop would otherwise call this again.
            _hasStarted = false;
            _hasConnected = false;
            _hasStartedClient = false;
            _isLobbyOwner = false;
            _lobbySteamId = CSteamID.Nil;
            _lobbyMembers.Clear();
            _lobbyGameMode = string.Empty;
            _lobbyMap = string.Empty;

            _unityTransportManager.ShutdownAll();

            _statusText = statusText;
        }

        /// <summary>
        /// Sets the game mode and map the host typed as lobby data, which reaches every member through <see cref="OnLobbyDataUpdated"/>.
        /// </summary>
        private void SetLobbyData()
        {
            SteamMatchmaking.SetLobbyData(_lobbySteamId, GameModeKey, _gameModeText);
            SteamMatchmaking.SetLobbyData(_lobbySteamId, MapKey, _mapText);
        }

        /// <summary>
        /// Reads the lobby's members and data from Steam, and asks for each member's avatar.
        /// </summary>
        private void RefreshLobby()
        {
            _lobbyMembers.Clear();

            CSteamID ownerSteamId = SteamMatchmaking.GetLobbyOwner(_lobbySteamId);
            int memberCount = SteamMatchmaking.GetNumLobbyMembers(_lobbySteamId);

            for (int i = 0; i < memberCount; i++)
            {
                CSteamID memberSteamId = SteamMatchmaking.GetLobbyMemberByIndex(_lobbySteamId, i);
                _lobbyMembers.Add(new(memberSteamId, SteamFriends.GetFriendPersonaName(memberSteamId), _lobbySteamId, memberSteamId == ownerSteamId));
                RequestAvatar(memberSteamId);
            }

            _lobbyMemberLimit = SteamMatchmaking.GetLobbyMemberLimit(_lobbySteamId);
            _lobbyGameMode = SteamMatchmaking.GetLobbyData(_lobbySteamId, GameModeKey);
            _lobbyMap = SteamMatchmaking.GetLobbyData(_lobbySteamId, MapKey);
        }

        /// <summary>
        /// Lists the friends who are in a lobby of this game, from what Steam knows of each friend's game.
        /// </summary>
        private void RefreshFriendsInLobbies()
        {
            _friendsInLobbies.Clear();

            CGameID ownGameId = new(SteamUtils.GetAppID());
            int friendCount = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);

            for (int i = 0; i < friendCount; i++)
            {
                CSteamID friendSteamId = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);

                if (!SteamFriends.GetFriendGamePlayed(friendSteamId, out FriendGameInfo_t friendGameInfo) || friendGameInfo.m_gameID != ownGameId || !friendGameInfo.m_steamIDLobby.IsValid())
                    continue;

                _friendsInLobbies.Add(new(friendSteamId, SteamFriends.GetFriendPersonaName(friendSteamId), friendGameInfo.m_steamIDLobby, isOwner: false));
                RequestAvatar(friendSteamId);
            }

            _statusText = _friendsInLobbies.Count == 0 ? "No friends are in a lobby of this game." : $"Friends in a lobby of this game: {_friendsInLobbies.Count}.";
        }

        /// <summary>
        /// Creates a user's avatar texture when Steam has the image, or leaves it to <see cref="OnAvatarImageLoaded"/> while Steam
        /// downloads it.
        /// </summary>
        /// <param name="steamId">The user.</param>
        private void RequestAvatar(CSteamID steamId)
        {
            if (_avatarsBySteamId.ContainsKey(steamId.m_SteamID))
                return;

            int imageHandle = SteamFriends.GetSmallFriendAvatar(steamId);

            if (imageHandle is LoadingAvatarHandle or UnsetAvatarHandle)
                return;

            StoreAvatar(steamId, imageHandle);
        }

        /// <summary>
        /// Copies a Steam image into a texture and stores it as the user's avatar.
        /// </summary>
        /// <param name="steamId">The user.</param>
        /// <param name="imageHandle">Steam's handle for the image.</param>
        private void StoreAvatar(CSteamID steamId, int imageHandle)
        {
            if (!SteamUtils.GetImageSize(imageHandle, out uint width, out uint height))
                return;

            int rowBytes = (int)width * 4;
            byte[] imageBytes = new byte[rowBytes * (int)height];

            if (!SteamUtils.GetImageRGBA(imageHandle, imageBytes, imageBytes.Length))
                return;

            // Steam's rows run top to bottom and a texture's bottom to top, so the rows are reversed.
            byte[] textureBytes = new byte[imageBytes.Length];

            for (int row = 0; row < (int)height; row++)
                Buffer.BlockCopy(imageBytes, row * rowBytes, textureBytes, ((int)height - 1 - row) * rowBytes, rowBytes);

            Texture2D avatar = new((int)width, (int)height, TextureFormat.RGBA32, mipChain: false);
            avatar.LoadRawTextureData(textureBytes);
            avatar.Apply();

            _avatarsBySteamId[steamId.m_SteamID] = avatar;
        }

        /// <summary>
        /// Records the lobby this peer created, sets its first lobby data and names this peer its game server, or reports why Steam refused it.
        /// </summary>
        /// <param name="lobbyCreated">Steam's notice.</param>
        private void OnLobbyCreated(LobbyCreated_t lobbyCreated)
        {
            if (lobbyCreated.m_eResult is not EResult.k_EResultOK)
            {
                _statusText = $"Steam refused the lobby: [{lobbyCreated.m_eResult}]. The server is still up.";
                Debug.LogError($"[{nameof(LobbiesPanel)}] could not create a lobby: [{lobbyCreated.m_eResult}].");

                return;
            }

            _isLobbyOwner = true;
            _lobbySteamId = new(lobbyCreated.m_ulSteamIDLobby);
            _statusText = $"Hosting a {LobbyTypeNames[_lobbyTypeIndex].ToLowerInvariant()} lobby.";

            SetLobbyData();

            // The server is already listening, so members may dial it from here on.
            SteamMatchmaking.SetLobbyGameServer(_lobbySteamId, unGameServerIP: 0, unGameServerPort: 0, SteamUser.GetSteamID());
        }

        /// <summary>
        /// Records the lobby this peer entered and starts the client toward its game server, unless this peer owns the lobby.
        /// </summary>
        /// <param name="lobbyEnter">Steam's notice.</param>
        /// <remarks>Steam raises this for the creator of a lobby too, which is already serving.</remarks>
        private void OnLobbyEntered(LobbyEnter_t lobbyEnter)
        {
            if (lobbyEnter.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                _hasStarted = false;
                _statusText = $"Could not enter the lobby: [{(EChatRoomEnterResponse)lobbyEnter.m_EChatRoomEnterResponse}].";

                return;
            }

            _lobbySteamId = new(lobbyEnter.m_ulSteamIDLobby);
            RefreshLobby();

            CSteamID ownerSteamId = SteamMatchmaking.GetLobbyOwner(_lobbySteamId);

            if (ownerSteamId == SteamUser.GetSteamID())
                return;

            // A game server set before this peer entered raised no notice for it, so it is read here; otherwise OnLobbyGameCreated follows.
            if (!SteamMatchmaking.GetLobbyGameServer(_lobbySteamId, out uint _, out ushort _, out CSteamID serverSteamId))
            {
                _statusText = "Waiting for the host's server.";

                return;
            }

            // The host served this lobby, so a different owner means Steam handed the lobby on after the host left.
            if (serverSteamId != ownerSteamId)
            {
                Leave("The host of that lobby has left.");

                return;
            }

            StartClient(serverSteamId.m_SteamID);
        }

        /// <summary>
        /// Starts this member's client when the lobby it is waiting in names its game server.
        /// </summary>
        /// <param name="lobbyGameCreated">Steam's notice.</param>
        private void OnLobbyGameCreated(LobbyGameCreated_t lobbyGameCreated)
        {
            if (!_isLobbyOwner && lobbyGameCreated.m_ulSteamIDLobby == _lobbySteamId.m_SteamID)
                StartClient(lobbyGameCreated.m_ulSteamIDGameServer);
        }

        /// <summary>
        /// Starts this peer as a client of the lobby's game server, unless it already started one.
        /// </summary>
        /// <param name="serverSteamId">The game server's SteamID.</param>
        private async void StartClient(ulong serverSteamId)
        {
            if (_hasStartedClient)
                return;

            _hasStartedClient = true;
            _steamSocketsTransport.ServerSteamId = serverSteamId;
            _statusText = $"Connecting to the host [{serverSteamId}].";

            await _unityTransportManager.StartClientAsync();
        }

        /// <summary>
        /// Rereads the members when one enters or leaves this peer's lobby.
        /// </summary>
        /// <param name="lobbyChatUpdate">Steam's notice.</param>
        private void OnLobbyChatUpdated(LobbyChatUpdate_t lobbyChatUpdate)
        {
            if (lobbyChatUpdate.m_ulSteamIDLobby == _lobbySteamId.m_SteamID)
                RefreshLobby();
        }

        /// <summary>
        /// Rereads the lobby when its data or a member's data changes.
        /// </summary>
        /// <param name="lobbyDataUpdate">Steam's notice.</param>
        private void OnLobbyDataUpdated(LobbyDataUpdate_t lobbyDataUpdate)
        {
            if (lobbyDataUpdate.m_bSuccess != 0 && lobbyDataUpdate.m_ulSteamIDLobby == _lobbySteamId.m_SteamID)
                RefreshLobby();
        }

        /// <summary>
        /// Enters the lobby the user accepted an invite to, or chose to join a friend in, from Steam while the game was running.
        /// </summary>
        /// <param name="gameLobbyJoinRequested">Steam's notice.</param>
        private void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t gameLobbyJoinRequested)
        {
            if (_hasStarted)
            {
                _statusText = "Leave this lobby before joining another.";

                return;
            }

            JoinLobby(gameLobbyJoinRequested.m_steamIDLobby);
        }

        /// <summary>
        /// Stores an avatar Steam has finished downloading.
        /// </summary>
        /// <param name="avatarImageLoaded">Steam's notice.</param>
        private void OnAvatarImageLoaded(AvatarImageLoaded_t avatarImageLoaded) => StoreAvatar(avatarImageLoaded.m_steamID, avatarImageLoaded.m_iImage);

        /// <summary>
        /// Rereads the lobby when a member's name or avatar changes.
        /// </summary>
        /// <param name="personaStateChange">Steam's notice.</param>
        private void OnPersonaStateChanged(PersonaStateChange_t personaStateChange)
        {
            if ((personaStateChange.m_nChangeFlags & EPersonaChange.k_EPersonaChangeAvatar) != 0 && _avatarsBySteamId.TryGetValue(personaStateChange.m_ulSteamID, out Texture2D avatar))
            {
                _avatarsBySteamId.Remove(personaStateChange.m_ulSteamID);
                Destroy(avatar);
            }

            if (_lobbySteamId != CSteamID.Nil)
                RefreshLobby();
        }

        /// <summary>
        /// Shows the session's progress, and leaves the lobby once this member's client stops or fails to connect, since the server it dialled is gone.
        /// </summary>
        /// <param name="connectionStateChange">The state change that was raised.</param>
        private void OnConnectionLocalStateChanged(ConnectionStateChange connectionStateChange)
        {
            if (!_hasStarted || _isLobbyOwner || connectionStateChange.Invoker is not Invoker.Client)
                return;

            switch (connectionStateChange.Connection.LocalState)
            {
                case LocalConnectionState.Connected:
                    _hasConnected = true;
                    _statusText = "Connected to the host.";

                    break;
                case LocalConnectionState.Disconnected:
                case LocalConnectionState.Error:
                case LocalConnectionState.TimedOut:
                    Leave(_hasConnected ? "The host left, or the connection dropped." : "Could not connect to the host.");
                    break;
            }
        }

        /// <summary>
        /// Drops the subscriptions and avatar textures.
        /// </summary>
        /// <remarks>Steam keeps running for the next scene; the bootstrap shuts it down when the application exits.</remarks>
        private void OnDestroy()
        {
            if (_transportManager is null)
                return;

            _transportManager.ConnectionLocalStateChanged -= OnConnectionLocalStateChanged;
            _steamBootstrap.SteamShutdownStarted -= OnSteamShutdownStarted;
            _lobbyCreatedCallback.Dispose();
            _lobbyEnteredCallback.Dispose();
            _lobbyChatUpdatedCallback.Dispose();
            _lobbyDataUpdatedCallback.Dispose();
            _lobbyGameCreatedCallback.Dispose();
            _gameLobbyJoinRequestedCallback.Dispose();
            _avatarImageLoadedCallback.Dispose();
            _personaStateChangedCallback.Dispose();

            foreach (Texture2D avatar in _avatarsBySteamId.Values)
                Destroy(avatar);

            _avatarsBySteamId.Clear();
        }

        /// <summary>
        /// Leaves the lobby as the application exits, before the bootstrap shuts Steam down.
        /// </summary>
        private void OnSteamShutdownStarted()
        {
            if (_lobbySteamId != CSteamID.Nil)
                SteamMatchmaking.LeaveLobby(_lobbySteamId);
        }

        /// <summary>
        /// Reads the lobby Steam named on the command line when it started the game to accept an invite.
        /// </summary>
        /// <param name="lobbySteamId">The lobby, when one was named.</param>
        /// <returns><see langword="true"/> when the command line names a lobby.</returns>
        private static bool TryGetLaunchLobby(out CSteamID lobbySteamId)
        {
            string[] arguments = Environment.GetCommandLineArgs();

            for (int i = 0; i < arguments.Length - 1; i++)
            {
                if (string.Equals(arguments[i], ConnectLobbyArgument, StringComparison.OrdinalIgnoreCase) && ulong.TryParse(arguments[i + 1], out ulong lobbyId))
                {
                    lobbySteamId = new(lobbyId);

                    return true;
                }
            }

            lobbySteamId = CSteamID.Nil;

            return false;
        }

        /// <summary>
        /// A user the panel lists: a lobby member, or a friend in a lobby.
        /// </summary>
        private readonly struct LobbyMember
        {
            /// <summary>
            /// The user's SteamID.
            /// </summary>
            public readonly CSteamID SteamId;
            /// <summary>
            /// The user's Steam display name.
            /// </summary>
            public readonly string Name;
            /// <summary>
            /// The lobby the user is in.
            /// </summary>
            public readonly CSteamID LobbySteamId;
            /// <summary>
            /// Whether the user owns that lobby.
            /// </summary>
            public readonly bool IsOwner;

            /// <summary>
            /// Creates the entry.
            /// </summary>
            /// <param name="steamId">The user's SteamID.</param>
            /// <param name="name">The user's Steam display name.</param>
            /// <param name="lobbySteamId">The lobby the user is in.</param>
            /// <param name="isOwner">Whether the user owns that lobby.</param>
            public LobbyMember(CSteamID steamId, string name, CSteamID lobbySteamId, bool isOwner)
            {
                SteamId = steamId;
                Name = name;
                LobbySteamId = lobbySteamId;
                IsOwner = isOwner;
            }
        }
    }
}
#endif
