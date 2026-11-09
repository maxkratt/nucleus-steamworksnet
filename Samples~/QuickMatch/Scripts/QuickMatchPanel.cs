// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using System;
using System.Collections.Generic;
using System.Threading;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nucleus.Integrations.SteamworksNet.Samples.QuickMatch
{
    /// <summary>
    /// The matchmaking panel: one Find Match button that searches open lobbies at widening distances, joins the first with room, or
    /// creates an invisible lobby and waits, then loads the game scene for every member once the lobby is full.
    /// </summary>
    /// <remarks>
    /// No server runs while matchmaking, and this scene has no Nucleus objects. Two players who search together both find nothing and both
    /// create a lobby, so a lone owner keeps searching and moves into any waiting lobby with a lower SteamID, which only one side can do.
    /// </remarks>
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/maxkratt/nucleus-steamworksnet/blob/main/Samples~/QuickMatch/README.md")]
    public class QuickMatchPanel : MonoBehaviour
    {
        /// <summary>
        /// The bootstrap that owns Steam.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Steam Bootstrap.")]
        private SteamBootstrap _steamBootstrap;
        /// <summary>
        /// How many players start a match, this peer included.
        /// </summary>
        [SerializeField]
        [Tooltip("How many players start a match, this peer included.")]
        private int _targetPlayerCount = 2;
        /// <summary>
        /// The skin the panel draws with.
        /// </summary>
        /// <remarks>Left empty, the panel draws with Unity's default look.</remarks>
        [SerializeField]
        [Tooltip("The skin the panel draws with. Leave empty for Unity's default look.")]
        private GUISkin _guiSkin;

        /// <summary>
        /// Where this peer is in matchmaking.
        /// </summary>
        private MatchState _matchState = MatchState.Idle;
        /// <summary>
        /// The lobby this peer is in, or <see cref="CSteamID.Nil"/> when it is in none.
        /// </summary>
        private CSteamID _lobbySteamId = CSteamID.Nil;
        /// <summary>
        /// The index into <see cref="SearchDistances"/> of the current search.
        /// </summary>
        private int _distanceIndex;
        /// <summary>
        /// The index into <see cref="_foundLobbySteamIds"/> of the next lobby to try.
        /// </summary>
        private int _nextFoundLobbyIndex;
        /// <summary>
        /// How many members the lobby had when last read.
        /// </summary>
        private int _lobbyMemberCount;
        /// <summary>
        /// Whether the search under way is a lone owner's look for a lobby to merge into.
        /// </summary>
        private bool _isMergeSearch;
        /// <summary>
        /// The last thing that happened, shown on the panel.
        /// </summary>
        private string _statusText = "Press Find Match.";
        /// <summary>
        /// The lobbies the last search returned.
        /// </summary>
        private readonly List<CSteamID> _foundLobbySteamIds = new();
        /// <summary>
        /// Cancels the wait before a lone owner's next merge search, or <see langword="null"/> when none is waiting.
        /// </summary>
        private CancellationTokenSource _mergeTimerCancellationTokenSource;
        /// <summary>
        /// The call result that reports a lobby search, held so it is not collected.
        /// </summary>
        private CallResult<LobbyMatchList_t> _lobbyMatchListCallResult;
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
        /// The distances searched, nearest first.
        /// </summary>
        private static readonly ELobbyDistanceFilter[] SearchDistances =
        {
            ELobbyDistanceFilter.k_ELobbyDistanceFilterClose,
            ELobbyDistanceFilter.k_ELobbyDistanceFilterDefault,
            ELobbyDistanceFilter.k_ELobbyDistanceFilterFar,
            ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide
        };

        /// <summary>
        /// The lobby data key marking a lobby as this sample's, since App ID 480 is shared by every developer testing on it.
        /// </summary>
        private const string SampleKey = "nucleus_quick_match";
        /// <summary>
        /// The value of <see cref="SampleKey"/>.
        /// </summary>
        private const string SampleValue = "1";
        /// <summary>
        /// The lobby data key holding the game version, so only matching builds meet.
        /// </summary>
        private const string VersionKey = "version";
        /// <summary>
        /// The lobby data key holding the lobby's state.
        /// </summary>
        private const string StateKey = "state";
        /// <summary>
        /// The state of a lobby still waiting for players.
        /// </summary>
        private const string OpenState = "open";
        /// <summary>
        /// The state of a full lobby whose members are loading the game.
        /// </summary>
        private const string StartingState = "starting";
        /// <summary>
        /// The shortest wait before a lone owner's next merge search, in seconds.
        /// </summary>
        private const float MinimumMergeDelaySeconds = 2f;
        /// <summary>
        /// The longest wait before a lone owner's next merge search, in seconds.
        /// </summary>
        private const float MaximumMergeDelaySeconds = 5f;

        /// <summary>
        /// Registers the Steam callbacks.
        /// </summary>
        private void Awake()
        {
            if (_steamBootstrap == null)
            {
                Debug.LogError($"[{nameof(QuickMatchPanel)}] needs its [{nameof(SteamBootstrap)}] set.");
                enabled = false;

                return;
            }

            // Reaching the menu means any earlier match is over.
            QuickMatchSession.LobbySteamId = CSteamID.Nil;

            if (!_steamBootstrap.IsSteamInitialized)
                return;

            _steamBootstrap.SteamShutdownStarted += OnSteamShutdownStarted;
            _lobbyMatchListCallResult = CallResult<LobbyMatchList_t>.Create(OnLobbyMatchList);
            _lobbyCreatedCallback = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
            _lobbyEnteredCallback = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
            _lobbyChatUpdatedCallback = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdated);
            _lobbyDataUpdatedCallback = Callback<LobbyDataUpdate_t>.Create(OnLobbyDataUpdated);
        }

        /// <summary>
        /// Draws the panel.
        /// </summary>
        private void OnGUI()
        {
            SampleGUI.BeginPanel(_guiSkin, 360f, "Quick Match");

            if (!_steamBootstrap.IsSteamInitialized)
            {
                GUILayout.Label("Steam is not running, so this peer cannot find a match.");
                SampleGUI.EndPanel();

                return;
            }

            GUILayout.Label(_statusText);

            if (_matchState is MatchState.Waiting)
                GUILayout.Label($"Players {_lobbyMemberCount} / {_targetPlayerCount}");

            if (_matchState is MatchState.Idle)
            {
                if (GUILayout.Button("Find Match"))
                    FindMatch();
            }
            else if (_matchState is not MatchState.Loading && GUILayout.Button("Cancel"))
            {
                Cancel();
            }

            SampleGUI.EndPanel();
        }

        /// <summary>
        /// Starts searching at the nearest distance.
        /// </summary>
        private void FindMatch()
        {
            _matchState = MatchState.Searching;
            _distanceIndex = 0;

            RequestLobbySearch(SearchDistances[_distanceIndex], isMergeSearch: false);
        }

        /// <summary>
        /// Stops matchmaking and leaves any lobby.
        /// </summary>
        private void Cancel()
        {
            _matchState = MatchState.Idle;
            StopMergeTimer();
            _lobbyMatchListCallResult.Cancel();
            LeaveCurrentLobby();

            _statusText = "Cancelled.";
        }

        /// <summary>
        /// Searches for lobbies of this sample and version that are open with a free slot.
        /// </summary>
        /// <param name="distance">How far to search.</param>
        /// <param name="isMergeSearch">Whether this is a lone owner's look for a lobby to merge into.</param>
        private void RequestLobbySearch(ELobbyDistanceFilter distance, bool isMergeSearch)
        {
            _isMergeSearch = isMergeSearch;

            if (!isMergeSearch)
                _statusText = "Searching for a match.";

            SteamMatchmaking.AddRequestLobbyListStringFilter(SampleKey, SampleValue, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListStringFilter(VersionKey, Application.version, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListStringFilter(StateKey, OpenState, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListFilterSlotsAvailable(1);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(distance);

            _lobbyMatchListCallResult.Set(SteamMatchmaking.RequestLobbyList());
        }

        /// <summary>
        /// Tries the next lobby the search found, or widens the search when none is left.
        /// </summary>
        private void JoinNextFoundLobby()
        {
            if (_nextFoundLobbyIndex < _foundLobbySteamIds.Count)
            {
                _statusText = "Joining a match.";
                SteamMatchmaking.JoinLobby(_foundLobbySteamIds[_nextFoundLobbyIndex++]);

                return;
            }

            WidenSearch();
        }

        /// <summary>
        /// Searches one distance further, or creates a lobby once the widest distance found nothing.
        /// </summary>
        private void WidenSearch()
        {
            _distanceIndex++;

            if (_distanceIndex < SearchDistances.Length)
            {
                RequestWiderSearchNextFrame();

                return;
            }

            _statusText = "Creating a lobby.";
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeInvisible, _targetPlayerCount);
        }

        /// <summary>
        /// Starts the next distance's search one frame from now, unless matchmaking was cancelled.
        /// </summary>
        /// <remarks>
        /// Widening usually happens inside <see cref="OnLobbyMatchList"/>, and Steamworks.NET clears a <see cref="CallResult{T}"/> right
        /// after its callback returns, so a search set from inside that callback would never report back.
        /// </remarks>
        private async void RequestWiderSearchNextFrame()
        {
            try
            {
                await Awaitable.NextFrameAsync(destroyCancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_matchState is MatchState.Searching)
                RequestLobbySearch(SearchDistances[_distanceIndex], isMergeSearch: false);
        }

        /// <summary>
        /// Rereads the lobby, loads the game if it is starting, starts it if this peer owns it and it is full, and otherwise keeps a lone owner looking for a merge.
        /// </summary>
        /// <remarks>The owner is read on every update because Steam hands the lobby on when its owner leaves.</remarks>
        private void ReadLobby()
        {
            if (SteamMatchmaking.GetLobbyData(_lobbySteamId, StateKey) == StartingState)
            {
                LoadGame();

                return;
            }

            CSteamID ownSteamId = SteamUser.GetSteamID();
            bool isOwner = SteamMatchmaking.GetLobbyOwner(_lobbySteamId) == ownSteamId;
            _lobbyMemberCount = SteamMatchmaking.GetNumLobbyMembers(_lobbySteamId);

            if (isOwner && _lobbyMemberCount >= _targetPlayerCount)
            {
                StopMergeTimer();
                SteamMatchmaking.SetLobbyJoinable(_lobbySteamId, bLobbyJoinable: false);
                SteamMatchmaking.SetLobbyData(_lobbySteamId, QuickMatchSession.HostKey, ownSteamId.m_SteamID.ToString());

                // Set last: this is the value every member, this one included, loads the game on.
                SteamMatchmaking.SetLobbyData(_lobbySteamId, StateKey, StartingState);

                return;
            }

            if (!isOwner || _lobbyMemberCount > 1)
                StopMergeTimer();
            else if (_mergeTimerCancellationTokenSource is null)
                StartMergeTimer();

            _statusText = "Waiting for players.";
        }

        /// <summary>
        /// Carries the lobby across the scene load and loads the game.
        /// </summary>
        private void LoadGame()
        {
            StopMergeTimer();
            _matchState = MatchState.Loading;
            _statusText = "Starting the match.";

            // A scene load does not take this peer out of the lobby.
            QuickMatchSession.LobbySteamId = _lobbySteamId;
            SceneManager.LoadScene(QuickMatchSession.GameSceneName);
        }

        /// <summary>
        /// Waits a jittered moment, then looks for a lobby to merge into.
        /// </summary>
        private async void StartMergeTimer()
        {
            _mergeTimerCancellationTokenSource = new();
            CancellationToken cancellationToken = _mergeTimerCancellationTokenSource.Token;

            try
            {
                await Awaitable.WaitForSecondsAsync(UnityEngine.Random.Range(MinimumMergeDelaySeconds, MaximumMergeDelaySeconds), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            StopMergeTimer();
            RequestLobbySearch(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide, isMergeSearch: true);
        }

        /// <summary>
        /// Cancels the wait before the next merge search, if there is one.
        /// </summary>
        private void StopMergeTimer()
        {
            if (_mergeTimerCancellationTokenSource is null)
                return;

            _mergeTimerCancellationTokenSource.Cancel();
            _mergeTimerCancellationTokenSource.Dispose();
            _mergeTimerCancellationTokenSource = null;
        }

        /// <summary>
        /// Leaves the lobby this peer is in, if any.
        /// </summary>
        private void LeaveCurrentLobby()
        {
            if (_lobbySteamId != CSteamID.Nil)
                SteamMatchmaking.LeaveLobby(_lobbySteamId);

            _lobbySteamId = CSteamID.Nil;
        }

        /// <summary>
        /// Moves a still lone owner into a found lobby with a lower SteamID, or waits to look again.
        /// </summary>
        private void ApplyMergeResults()
        {
            if (_matchState is not MatchState.Waiting || SteamMatchmaking.GetNumLobbyMembers(_lobbySteamId) != 1)
                return;

            for (int i = _foundLobbySteamIds.Count - 1; i >= 0; i--)
            {
                if (_foundLobbySteamIds[i].m_SteamID >= _lobbySteamId.m_SteamID)
                    _foundLobbySteamIds.RemoveAt(i);
            }

            if (_foundLobbySteamIds.Count == 0)
            {
                // A member who came and went during the search has already restarted the timer.
                if (_mergeTimerCancellationTokenSource is null)
                    StartMergeTimer();

                return;
            }

            LeaveCurrentLobby();
            _matchState = MatchState.Searching;

            // Should every one of these fail, the next widening creates a new lobby.
            _distanceIndex = SearchDistances.Length - 1;
            _nextFoundLobbyIndex = 0;
            JoinNextFoundLobby();
        }

        /// <summary>
        /// Records what a search found and acts on it.
        /// </summary>
        /// <param name="lobbyMatchList">Steam's answer.</param>
        /// <param name="isIoFailure">Whether Steam could not be reached.</param>
        private void OnLobbyMatchList(LobbyMatchList_t lobbyMatchList, bool isIoFailure)
        {
            _foundLobbySteamIds.Clear();

            if (!isIoFailure)
            {
                for (int i = 0; i < (int)lobbyMatchList.m_nLobbiesMatching; i++)
                    _foundLobbySteamIds.Add(SteamMatchmaking.GetLobbyByIndex(i));
            }

            Debug.Log($"[{nameof(QuickMatchPanel)}] search found [{_foundLobbySteamIds.Count}] lobbies. Merge search: [{_isMergeSearch}]. Failed: [{isIoFailure}].");

            if (_isMergeSearch)
            {
                ApplyMergeResults();

                return;
            }

            if (_matchState is not MatchState.Searching)
                return;

            _nextFoundLobbyIndex = 0;
            JoinNextFoundLobby();
        }

        /// <summary>
        /// Marks a created lobby as open for searches, or reports why Steam refused it.
        /// </summary>
        /// <param name="lobbyCreated">Steam's notice.</param>
        private void OnLobbyCreated(LobbyCreated_t lobbyCreated)
        {
            CSteamID lobbySteamId = new(lobbyCreated.m_ulSteamIDLobby);

            if (lobbyCreated.m_eResult is not EResult.k_EResultOK)
            {
                _matchState = MatchState.Idle;
                _statusText = $"Steam refused the lobby: [{lobbyCreated.m_eResult}].";

                return;
            }

            Debug.Log($"[{nameof(QuickMatchPanel)}] created lobby [{lobbySteamId.m_SteamID}] while [{_matchState}].");

            // Cancelled while Steam was creating it. Only Idle means that: Steam may report entering the lobby first, which moves this peer to Waiting.
            if (_matchState is MatchState.Idle)
            {
                SteamMatchmaking.LeaveLobby(lobbySteamId);

                return;
            }

            SteamMatchmaking.SetLobbyData(lobbySteamId, SampleKey, SampleValue);
            SteamMatchmaking.SetLobbyData(lobbySteamId, VersionKey, Application.version);
            SteamMatchmaking.SetLobbyData(lobbySteamId, StateKey, OpenState);
        }

        /// <summary>
        /// Records the lobby this peer entered, or moves to the next found lobby when the join was refused.
        /// </summary>
        /// <param name="lobbyEnter">Steam's notice.</param>
        /// <remarks>Steam raises this for the creator of a lobby too.</remarks>
        private void OnLobbyEntered(LobbyEnter_t lobbyEnter)
        {
            CSteamID lobbySteamId = new(lobbyEnter.m_ulSteamIDLobby);

            Debug.Log($"[{nameof(QuickMatchPanel)}] entered lobby [{lobbySteamId.m_SteamID}] with response [{(EChatRoomEnterResponse)lobbyEnter.m_EChatRoomEnterResponse}] while [{_matchState}].");

            // Cancelled while Steam was joining it.
            if (_matchState is not MatchState.Searching)
            {
                SteamMatchmaking.LeaveLobby(lobbySteamId);

                return;
            }

            if (lobbyEnter.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                JoinNextFoundLobby();

                return;
            }

            _lobbySteamId = lobbySteamId;
            _matchState = MatchState.Waiting;

            ReadLobby();
        }

        /// <summary>
        /// Rereads the lobby when a member enters or leaves.
        /// </summary>
        /// <param name="lobbyChatUpdate">Steam's notice.</param>
        private void OnLobbyChatUpdated(LobbyChatUpdate_t lobbyChatUpdate)
        {
            if (_matchState is MatchState.Waiting && lobbyChatUpdate.m_ulSteamIDLobby == _lobbySteamId.m_SteamID)
                ReadLobby();
        }

        /// <summary>
        /// Rereads the lobby when its data changes, which is how every member learns the match is starting.
        /// </summary>
        /// <param name="lobbyDataUpdate">Steam's notice.</param>
        private void OnLobbyDataUpdated(LobbyDataUpdate_t lobbyDataUpdate)
        {
            if (_matchState is MatchState.Waiting && lobbyDataUpdate.m_bSuccess != 0 && lobbyDataUpdate.m_ulSteamIDLobby == _lobbySteamId.m_SteamID)
                ReadLobby();
        }

        /// <summary>
        /// Leaves the lobby as the application exits, before the bootstrap shuts Steam down.
        /// </summary>
        private void OnSteamShutdownStarted() => LeaveCurrentLobby();

        /// <summary>
        /// Drops the subscriptions and any waiting timer, leaving the lobby joined for the game scene.
        /// </summary>
        private void OnDestroy()
        {
            StopMergeTimer();

            if (_lobbyMatchListCallResult is null)
                return;

            _steamBootstrap.SteamShutdownStarted -= OnSteamShutdownStarted;
            _lobbyMatchListCallResult.Dispose();
            _lobbyCreatedCallback.Dispose();
            _lobbyEnteredCallback.Dispose();
            _lobbyChatUpdatedCallback.Dispose();
            _lobbyDataUpdatedCallback.Dispose();
        }

        /// <summary>
        /// Where a peer is in matchmaking.
        /// </summary>
        private enum MatchState : byte
        {
            /// <summary>
            /// The peer is not matchmaking.
            /// </summary>
            Idle,
            /// <summary>
            /// The peer is searching for, joining or creating a lobby.
            /// </summary>
            Searching,
            /// <summary>
            /// The peer is in a lobby that is waiting for players.
            /// </summary>
            Waiting,
            /// <summary>
            /// The peer is loading the game scene.
            /// </summary>
            Loading
        }
    }
}
#endif
