// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
using System;
using System.Diagnostics;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using Debug = UnityEngine.Debug;
#if NUCLEUS_STEAMWORKS_ENABLED
using Nucleus.Managers.Transports;
using Steamworks;
#endif

namespace Nucleus.Integrations.SteamworksNet.Editor
{
    /// <summary>
    /// Inspector presentation for <see cref="SteamworksNetTransport"/>. A status box heads it, tinted by state: whether the transport is
    /// compiled in, the Steamworks.NET version, and while playing the role and App ID. Settings follow under two headers, Connection
    /// (how peers find each other) and Session (what holds once they are linked), with Steam's send settings in a collapsed Advanced
    /// section. While playing the settings are greyed out, since they were read at start, and a running server shows how clients reach
    /// it under the status box, with a button that copies just its SteamID or fake address. Setup mistakes are flagged where they are
    /// made: Steam or the game server not running while playing, a fake IP on a listen server, and a send buffer too small for one piece.
    /// While the transport is compiled out it lists which check failed, and offers to install Steamworks.NET when Git is on this
    /// system's path. All presentation lives here, the MonoBehaviour carrying no layout attributes; only the script row is drawn by the
    /// shared Nucleus inspector helpers.
    /// </summary>
    [CustomEditor(typeof(SteamworksNetTransport))]
    [CanEditMultipleObjects]
    public class SteamworksNetTransportEditor : UnityEditor.Editor
    {
        /// <summary>
        /// The package request in flight, or <see langword="null"/> when none is.
        /// </summary>
        private AddRequest _addRequest;
        /// <summary>
        /// Why the last package request failed, or <see langword="null"/> when none has.
        /// </summary>
        private string _installErrorText;
        /// <summary>
        /// The status line's text, rebuilt when play mode changes and, while playing, at each runtime refresh.
        /// </summary>
        private string _statusText = string.Empty;
        /// <summary>
        /// The colour of the status line's dot.
        /// </summary>
        private Color _statusColor;
        /// <summary>
        /// The installed Steamworks.NET version, or empty when it is not installed or cannot be read.
        /// </summary>
        private string _steamworksVersionText = string.Empty;
        /// <summary>
        /// The editor time, in seconds, at which the runtime readout is next refreshed.
        /// </summary>
        private double _nextRuntimeRefreshTime;
#if NUCLEUS_STEAMWORKS_ENABLED
        /// <summary>
        /// The label of the row naming how clients reach this peer's server, or empty while no server runs.
        /// </summary>
        private string _serverRowLabel = string.Empty;
        /// <summary>
        /// The value of that row: the server's SteamID, fake address or port.
        /// </summary>
        private string _serverRowText = string.Empty;
        /// <summary>
        /// What the row's Copy button copies, or <see langword="null"/> when there is nothing a client could paste.
        /// </summary>
        private string _serverRowCopyText;
        /// <summary>
        /// The warning drawn under the status box while playing when Steam or the game server is not running, or empty when none is due.
        /// </summary>
        private string _steamWarningText = string.Empty;
#endif

        /// <summary>
        /// The git URL the Package Manager adds Steamworks.NET from, pinned to the version the transport was tested with.
        /// </summary>
        private const string SteamworksPackageUrl = "https://github.com/rlabrecque/Steamworks.NET.git?path=/com.rlabrecque.steamworks.net#2025.164.1";
        /// <summary>
        /// The Package Manager name of Steamworks.NET.
        /// </summary>
        private const string SteamworksPackageName = "com.rlabrecque.steamworks.net";
        /// <summary>
        /// The Steamworks.NET project page, which also offers a .unitypackage for projects that cannot add git packages.
        /// </summary>
        private const string SteamworksRepositoryUrl = "https://github.com/rlabrecque/Steamworks.NET";
        /// <summary>
        /// The page Git is downloaded from.
        /// </summary>
        private const string GitDownloadUrl = "https://git-scm.com/downloads";
        /// <summary>
        /// The session key recording that Git was looked for this editor session.
        /// </summary>
        private const string GitCheckedSessionKey = "Nucleus.Steamworks.GitChecked";
        /// <summary>
        /// The session key recording whether Git was found.
        /// </summary>
        private const string GitFoundSessionKey = "Nucleus.Steamworks.GitFound";
        /// <summary>
        /// The session key recording that the missing package warning was logged this editor session.
        /// </summary>
        private const string MissingWarningLoggedSessionKey = "Nucleus.Steamworks.MissingWarningLogged";
        /// <summary>
        /// How long the Git check waits for <c>git --version</c> to answer.
        /// </summary>
        private const int GitCheckTimeoutMilliseconds = 3000;
        /// <summary>
        /// The SteamID that joins this peer's own server.
        /// </summary>
        /// <remarks>Mirrors <c>SteamSockets.UnsetSteamId</c>, which compiles out with Steamworks.NET while this inspector does not.</remarks>
        private const ulong UnsetSteamId = 0UL;
        /// <summary>
        /// The send setting that keeps Steam's default.
        /// </summary>
        /// <remarks>Mirrors <c>SteamSockets.UnsetSendSetting</c>, which compiles out with Steamworks.NET while this inspector does not.</remarks>
        private const uint UnsetSendSetting = 0U;
        /// <summary>
        /// Steam's default send rate, both minimum and maximum, in bytes per second.
        /// </summary>
        private const uint SteamDefaultSendRateBytes = 256U * 1024U;
        /// <summary>
        /// Steam's default send buffer, in bytes.
        /// </summary>
        private const uint SteamDefaultSendBufferBytes = 512U * 1024U;
        /// <summary>
        /// Steam's default connecting and idle timeout, in seconds.
        /// </summary>
        private const int SteamDefaultTimeoutSeconds = 10;
        /// <summary>
        /// The bytes in one kibibyte, the unit the send settings are entered in.
        /// </summary>
        private const uint KibibyteBytes = 1024U;
        /// <summary>
        /// The longest piece a reliable message is sent in, in bytes.
        /// </summary>
        /// <remarks>Mirrors <c>SteamSockets.MaximumMessageBytes</c>, which compiles out with Steamworks.NET while this inspector does not.</remarks>
        private const uint SteamMessagePieceBytes = 512U * 1024U;
        /// <summary>
        /// The session key prefix that records whether each section is expanded, followed by the section's title.
        /// </summary>
        private const string SectionExpandedSessionKeyPrefix = "Nucleus.Steamworks.TransportSection.";
        /// <summary>
        /// The gap between a unit and the right edge of its field.
        /// </summary>
        private const float UnitPadding = 4f;
        /// <summary>
        /// The width of the foldout arrow, where a section's title starts.
        /// </summary>
        private const float FoldoutArrowWidth = 15f;
        /// <summary>
        /// The space above each group's header, which separates it from the group before.
        /// </summary>
        private const float GroupSpacing = 6f;
        /// <summary>
        /// The diameter of the status line's dot.
        /// </summary>
        private const float StatusDotSize = 8f;
        /// <summary>
        /// The width of a compile check's OK or Blocked column.
        /// </summary>
        private const float CheckResultWidth = 64f;
        /// <summary>
        /// The width of the server row's Copy button.
        /// </summary>
        private const float CopyButtonWidth = 48f;
        /// <summary>
        /// The height of the status box.
        /// </summary>
        private const float StatusBoxHeight = 26f;
        /// <summary>
        /// The height of the version pill inside the status box.
        /// </summary>
        private const float StatusPillHeight = 16f;
        /// <summary>
        /// The corner radius of the status box.
        /// </summary>
        private const float StatusCornerRadius = 4f;
        /// <summary>
        /// The space inside the status box around its dot, text and pill.
        /// </summary>
        private const float StatusPadding = 8f;
        /// <summary>
        /// How strongly the state's colour tints the status box.
        /// </summary>
        private const float StatusFillAlpha = 0.12f;
        /// <summary>
        /// How strongly the state's colour draws the status box's border.
        /// </summary>
        private const float StatusBorderAlpha = 0.35f;
        /// <summary>
        /// The fill of the version pill: a dark wash that reads on any tint.
        /// </summary>
        private static readonly Color StatusPillColor = new(0f, 0f, 0f, 0.25f);
        /// <summary>
        /// How often, in seconds, the status box and the server row are refreshed while playing.
        /// </summary>
        /// <remarks>Reading Steam allocates, so it is read at a display's pace, not every frame.</remarks>
        private const double RuntimeRefreshIntervalSeconds = 0.25d;
        /// <summary>
        /// The status dot while the transport is running.
        /// </summary>
        private static readonly Color RunningColor = new(0.35f, 0.75f, 0.4f);
        /// <summary>
        /// The status dot while the transport is compiled in and idle.
        /// </summary>
        private static readonly Color IdleColor = new(0.55f, 0.55f, 0.55f);
        /// <summary>
        /// The status dot while the transport is compiled out.
        /// </summary>
        private static readonly Color CompiledOutColor = new(0.9f, 0.65f, 0.2f);

        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            // Drawn here rather than by Nucleus, whose script row adds a second help button that ignores this component's HelpURL.
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));

            DrawStatusBox();
#if NUCLEUS_STEAMWORKS_ENABLED
            DrawServerRow();

            if (_steamWarningText.Length > 0)
                EditorGUILayout.HelpBox(_steamWarningText, MessageType.Warning);
#elif !NUCLEUS_STEAMWORKS
            DrawSteamworksInstall();
#endif

            serializedObject.Update();

            if (Application.isPlaying)
                EditorGUILayout.HelpBox("Settings were read when the transport started, and Unity discards changes made while playing. Edit them in edit mode.", MessageType.None);

            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                DrawConnectionGroup();
                DrawSessionGroup();
                DrawAdvancedSection();
            }

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// Reads the Steamworks.NET version, builds the status line, starts the runtime refresh, and looks for Git once per editor
        /// session, so the install button is drawn only where the Package Manager can use it.
        /// </summary>
        private void OnEnable()
        {
#if NUCLEUS_STEAMWORKS
            /* Read only while the compiler saw the package, since the Package Manager's list can still name it around a removal or an
             * install. Found by name, not by a Steamworks.NET type, since DISABLESTEAMWORKS compiles those types out while the package
             * stays. */
            foreach (UnityEditor.PackageManager.PackageInfo packageInfo in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                if (packageInfo.name != SteamworksPackageName)
                    continue;

                _steamworksVersionText = $"Steamworks.NET {packageInfo.version}";

                break;
            }
#endif

            RefreshStatus();

            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += OnRuntimeRefreshDue;
#if !NUCLEUS_STEAMWORKS
            if (SessionState.GetBool(GitCheckedSessionKey, defaultValue: false))
                return;

            SessionState.SetBool(GitFoundSessionKey, IsGitFound());
            SessionState.SetBool(GitCheckedSessionKey, true);
#endif
        }

        /// <summary>
        /// Stops the runtime refresh and stops watching a package request when the inspector closes.
        /// </summary>
        /// <remarks>The request itself carries on, and a success recompiles the editor whether or not this inspector is open.</remarks>
        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdated;
            EditorApplication.update -= OnRuntimeRefreshDue;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        }

        /// <summary>
        /// Rebuilds the status box and the server row as play mode starts or ends.
        /// </summary>
        /// <param name="playModeStateChange">The change.</param>
        private void OnPlayModeStateChanged(PlayModeStateChange playModeStateChange)
        {
            RefreshStatus();
#if NUCLEUS_STEAMWORKS_ENABLED
            RefreshRuntime();
#endif
            Repaint();
        }

        /// <summary>
        /// Refreshes the status box and the server row a few times a second while playing.
        /// </summary>
        /// <remarks>
        /// The role and the fake address change with no event this inspector can hear, so they are read on a clock, as any live
        /// readout is. Outside play mode nothing is read.
        /// </remarks>
        private void OnRuntimeRefreshDue()
        {
            if (!Application.isPlaying || EditorApplication.timeSinceStartup < _nextRuntimeRefreshTime)
                return;

            _nextRuntimeRefreshTime = EditorApplication.timeSinceStartup + RuntimeRefreshIntervalSeconds;

            RefreshStatus();
#if NUCLEUS_STEAMWORKS_ENABLED
            RefreshRuntime();
#endif
            Repaint();
        }

        /// <summary>
        /// Draws the status box: a rounded box tinted by state, holding a dot, the state, and the Steamworks.NET version in a pill at
        /// the right. While the transport is compiled out the box grows to hold the checklist under a divider, so the state and its
        /// causes read as one block.
        /// </summary>
        private void DrawStatusBox()
        {
            Color borderColor = new(_statusColor.r, _statusColor.g, _statusColor.b, StatusBorderAlpha);

            EditorGUILayout.Space(2f);

            // The group's rect is only known once layout has run, so the box is painted on Repaint, behind the rows drawn into it.
            Rect boxRect = EditorGUILayout.BeginVertical();

            if (Event.current.type is EventType.Repaint)
            {
                Color fillColor = new(_statusColor.r, _statusColor.g, _statusColor.b, StatusFillAlpha);

                GUI.DrawTexture(boxRect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, fillColor, 0f, StatusCornerRadius);
                GUI.DrawTexture(boxRect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, borderColor, 1f, StatusCornerRadius);
            }

            DrawStatusHeader(EditorGUILayout.GetControlRect(false, StatusBoxHeight));
#if !NUCLEUS_STEAMWORKS_ENABLED

            Rect dividerRect = EditorGUILayout.GetControlRect(false, 1f);

            EditorGUI.DrawRect(new Rect(dividerRect.x + StatusPadding, dividerRect.y, dividerRect.width - StatusPadding * 2f, 1f), borderColor);
            DrawCompiledOutChecks();
            EditorGUILayout.Space(StatusPadding / 2f);
#endif

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2f);
        }

        /// <summary>
        /// Draws the status box's top row: the dot, the state, and the version pill.
        /// </summary>
        /// <param name="boxRect">The row, spanning the box's width.</param>
        private void DrawStatusHeader(Rect boxRect)
        {
            Rect dotRect = new(boxRect.x + StatusPadding, boxRect.y + (boxRect.height - StatusDotSize) / 2f, StatusDotSize, StatusDotSize);

            GUI.DrawTexture(dotRect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, _statusColor, 0f, StatusDotSize / 2f);

            float pillWidth = 0f;

            if (_steamworksVersionText.Length > 0)
            {
                GUIContent versionGuiContent = new(_steamworksVersionText);
                Vector2 versionSize = EditorStyles.miniLabel.CalcSize(versionGuiContent);
                pillWidth = versionSize.x + StatusPadding * 2f;
                Rect pillRect = new(boxRect.xMax - pillWidth - StatusPadding / 2f, boxRect.y + (boxRect.height - StatusPillHeight) / 2f, pillWidth, StatusPillHeight);

                GUI.DrawTexture(pillRect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, StatusPillColor, 0f, StatusPillHeight / 2f);
                GUI.Label(new Rect(pillRect.x + StatusPadding, pillRect.y, versionSize.x, pillRect.height), versionGuiContent, EditorStyles.miniLabel);
            }

            Rect textRect = new(dotRect.xMax + StatusPadding, boxRect.y, boxRect.width - (dotRect.xMax - boxRect.x) - StatusPadding * 2f - pillWidth, boxRect.height);

            EditorGUI.LabelField(textRect, _statusText);
        }

        /// <summary>
        /// Builds the status line's text and colour from whether the transport is compiled in and, while playing, what it runs.
        /// </summary>
        private void RefreshStatus()
        {
#if NUCLEUS_STEAMWORKS_ENABLED
            _steamWarningText = Application.isPlaying ? GetSteamWarningText() : string.Empty;

            if (!Application.isPlaying || target is not SteamworksNetTransport { Transport: SteamSockets steamSockets })
            {
                _statusText = Application.isPlaying ? "Not added to a running CoreManager" : "Ready to start";
                _statusColor = IdleColor;

                return;
            }

            TransportManager transportManager = steamSockets.CoreManager.TransportManager;
            bool isServerStarted = transportManager.IsServerStarted;
            bool isClientStarted = transportManager.IsClientStarted;

            if (!isServerStarted && !isClientStarted)
            {
                _statusText = "Steam transport idle";
                _statusColor = IdleColor;

                return;
            }

            string roleText = isServerStarted && isClientStarted ? "hosting" : isServerStarted ? "server" : "client";
            // A dedicated server inits only the game server API, so its App ID is read there; every other peer has the user's.
            bool isGameServerOnly = isServerStarted && !isClientStarted && steamSockets.GameServerEnabled;
            AppId_t appId = isGameServerOnly ? SteamGameServerUtils.GetAppID() : SteamUtils.GetAppID();

            _statusText = $"Steam running · App {appId.m_AppId} · {roleText}";
            _statusColor = RunningColor;
#else
            _statusText = "Transport compiled out";
            _statusColor = CompiledOutColor;
#endif
        }

        /// <summary>
        /// Draws the Connection group: the addressing with a note on what it means for players, the fields it decides, and the server
        /// interface.
        /// </summary>
        private void DrawConnectionGroup()
        {
            SerializedProperty addressingSerializedProperty = serializedObject.FindProperty("_addressing");
            SteamworksNetAddressing addressing = (SteamworksNetAddressing)addressingSerializedProperty.enumValueIndex;

            DrawHeader("Connection");
            EditorGUILayout.PropertyField(addressingSerializedProperty);

            // Every field here means what the addressing says it means, so all are drawn one level in under it.
            using (new EditorGUI.IndentLevelScope())
            {
                SerializedProperty targetSerializedProperty = serializedObject.FindProperty(addressing is SteamworksNetAddressing.SteamId ? "_serverSteamId" : "_serverAddress");

                EditorGUILayout.PropertyField(targetSerializedProperty, new GUIContent(GetTargetLabel(addressing), targetSerializedProperty.tooltip));

                // A fake address carries the port Steam granted, so only the other two addressings have a port to agree on.
                if (addressing is not SteamworksNetAddressing.FakeIp)
                {
                    SerializedProperty portSerializedProperty = serializedObject.FindProperty("_port");

                    EditorGUILayout.PropertyField(portSerializedProperty, new GUIContent(GetPortLabel(addressing), portSerializedProperty.tooltip));
                }

                // Only an IP address server binds a local address; the other addressings listen on Steam's relay.
                if (addressing is SteamworksNetAddressing.IpAddress)
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_bindAddress"));

                // Only Steam ID links can go direct; a fake IP always rides the relay, and an address is dialled as given.
                if (addressing is SteamworksNetAddressing.SteamId)
                {
                    SerializedProperty relayOnlyEnabledSerializedProperty = serializedObject.FindProperty("_relayOnlyEnabled");

                    EditorGUILayout.PropertyField(relayOnlyEnabledSerializedProperty, new GUIContent("Relay Only", relayOnlyEnabledSerializedProperty.tooltip));
                }
            }

            SerializedProperty gameServerEnabledSerializedProperty = serializedObject.FindProperty("_gameServerEnabled");

            EditorGUILayout.PropertyField(gameServerEnabledSerializedProperty, new GUIContent("Game Server", gameServerEnabledSerializedProperty.tooltip));

            // Judged only while the selection agrees, so the note never describes one object by another's settings.
            if (addressing is SteamworksNetAddressing.FakeIp && !addressingSerializedProperty.hasMultipleDifferentValues && !gameServerEnabledSerializedProperty.hasMultipleDifferentValues && !gameServerEnabledSerializedProperty.boolValue)
                EditorGUILayout.HelpBox("A fake IP without Game Server works, but a listen server rarely wants one: players reach it by SteamID with no address to share. A fake IP suits a dedicated server with Game Server on.", MessageType.Info);
        }

        /// <summary>
        /// Draws the Session group: who may join and what a client may send.
        /// </summary>
        private void DrawSessionGroup()
        {
            SerializedProperty maximumConnectionsSerializedProperty = serializedObject.FindProperty("_maximumConnections");
            SerializedProperty ticketAuthenticationEnabledSerializedProperty = serializedObject.FindProperty("_ticketAuthenticationEnabled");

            DrawHeader("Session");
            EditorGUILayout.PropertyField(maximumConnectionsSerializedProperty);

            // Corrected only while the selection agrees, so one object's value is never written over the others.
            if (!maximumConnectionsSerializedProperty.hasMultipleDifferentValues && maximumConnectionsSerializedProperty.uintValue < SteamworksNetTransport.MinimumMaximumConnections)
                maximumConnectionsSerializedProperty.uintValue = SteamworksNetTransport.MinimumMaximumConnections;

            DrawKibibyteField(serializedObject.FindProperty("_maximumClientPacketBytes"), "Maximum Client Packet", "KiB");
            EditorGUILayout.PropertyField(ticketAuthenticationEnabledSerializedProperty, new GUIContent("Ticket Authentication", ticketAuthenticationEnabledSerializedProperty.tooltip));

            SerializedProperty shareSteamIdsEnabledSerializedProperty = serializedObject.FindProperty("_shareSteamIdsEnabled");

            EditorGUILayout.PropertyField(shareSteamIdsEnabledSerializedProperty, new GUIContent("Share Steam IDs", shareSteamIdsEnabledSerializedProperty.tooltip));
        }

        /// <summary>
        /// Draws the collapsed Advanced section with Steam's timeouts, send rate and buffer, summarised while it is collapsed.
        /// </summary>
        private void DrawAdvancedSection()
        {
            SerializedProperty connectingTimeoutSecondsSerializedProperty = serializedObject.FindProperty("_connectingTimeoutSeconds");
            SerializedProperty idleTimeoutSecondsSerializedProperty = serializedObject.FindProperty("_idleTimeoutSeconds");
            SerializedProperty sendRateMaximumSerializedProperty = serializedObject.FindProperty("_sendRateMaximumBytesPerSecond");
            SerializedProperty sendBufferSizeSerializedProperty = serializedObject.FindProperty("_sendBufferSizeBytes");
            string advancedSummary = $"{FormatSeconds(connectingTimeoutSecondsSerializedProperty.intValue)} / {FormatSeconds(idleTimeoutSecondsSerializedProperty.intValue)} timeouts · {FormatBytes(sendRateMaximumSerializedProperty.uintValue, SteamDefaultSendRateBytes)}/s · {FormatBytes(sendBufferSizeSerializedProperty.uintValue, SteamDefaultSendBufferBytes)} buffer";

            EditorGUILayout.Space(GroupSpacing);

            if (!DrawSection("Advanced", advancedSummary, isExpandedByDefault: false))
                return;

            using (new EditorGUI.IndentLevelScope())
            {
                DrawPropertyFieldWithUnit(connectingTimeoutSecondsSerializedProperty, "Connecting Timeout", "s");
                DrawPropertyFieldWithUnit(idleTimeoutSecondsSerializedProperty, "Idle Timeout", "s");
                DrawKibibyteField(sendRateMaximumSerializedProperty, "Maximum Send Rate", "KiB/s");
                DrawKibibyteField(sendBufferSizeSerializedProperty, "Send Buffer Size", "KiB");
            }

            uint sendBufferSizeBytes = sendBufferSizeSerializedProperty.uintValue;

            if (!sendBufferSizeSerializedProperty.hasMultipleDifferentValues && sendBufferSizeBytes is not UnsetSendSetting && sendBufferSizeBytes < SteamMessagePieceBytes)
                EditorGUILayout.HelpBox("Reliable messages are sent in pieces of up to 512 KiB, and one piece larger than the send buffer closes the link. Keep it at 512 KiB or more, or zero for Steam's default.", MessageType.Warning);
        }

        /// <summary>
        /// Draws why the transport is compiled out, inside the status box, as one line per check saying what blocks it and how to fix
        /// that.
        /// </summary>
        /// <remarks>The checklist stands in for a warning box: the amber status box above it already says the transport is compiled out.</remarks>
        private void DrawCompiledOutChecks()
        {
#if NUCLEUS_STEAMWORKS
            DrawCheck(isPassed: true, _steamworksVersionText.Length > 0 ? $"{_steamworksVersionText} installed" : "Steamworks.NET installed");
#else
            bool isGitFound = SessionState.GetBool(GitFoundSessionKey, defaultValue: false);

            DrawCheck(isPassed: false, "Steamworks.NET is not installed: install it below");
            DrawCheck(isGitFound, isGitFound ? "Git on the editor's path, for the install" : "Git is not on the editor's path: install it and restart Unity, or import Steamworks.NET's .unitypackage");
#endif
#if DISABLESTEAMWORKS
            DrawCheck(isPassed: false, "DISABLESTEAMWORKS is defined: remove it from Scripting Define Symbols");
#else
            DrawCheck(isPassed: true, "DISABLESTEAMWORKS not defined");
#endif
#if UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX
            DrawCheck(isPassed: true, $"Build target supported ({EditorUserBuildSettings.activeBuildTarget})");
#else
            DrawCheck(isPassed: false, $"Build target {EditorUserBuildSettings.activeBuildTarget} is not supported: switch to Windows, macOS or Linux");
#endif
        }

        /// <summary>
        /// Draws one compile check as OK or Blocked beside what it found.
        /// </summary>
        /// <param name="isPassed">Whether the check passed.</param>
        /// <param name="description">What it found, and how to fix it when it failed.</param>
        private static void DrawCheck(bool isPassed, string description)
        {
            Rect rowRect = EditorGUILayout.GetControlRect();

            // Inset to line up with the status text above, inside the box's border.
            rowRect.xMin += StatusPadding;
            rowRect.xMax -= StatusPadding;

            Rect resultRect = new(rowRect.x, rowRect.y, CheckResultWidth, rowRect.height);
            Color previousColor = GUI.contentColor;

            GUI.contentColor = isPassed ? RunningColor : CompiledOutColor;
            EditorGUI.LabelField(resultRect, isPassed ? "OK" : "Blocked", EditorStyles.boldLabel);
            GUI.contentColor = previousColor;

            EditorGUI.LabelField(new Rect(resultRect.xMax, rowRect.y, rowRect.width - resultRect.width, rowRect.height), description);
        }

        /// <summary>
        /// Draws, under the status box while Steamworks.NET is missing, the install button or the Git download link, and the project link.
        /// </summary>
        private void DrawSteamworksInstall()
        {
            if (_addRequest is not null)
            {
                EditorGUILayout.LabelField("Adding Steamworks.NET. Unity recompiles when it is done.");
            }
            else if (SessionState.GetBool(GitFoundSessionKey, defaultValue: false))
            {
                if (_installErrorText is not null)
                    EditorGUILayout.HelpBox(_installErrorText, MessageType.Error);

                if (GUILayout.Button("Install Steamworks.NET", GUILayout.Height(26f)))
                    InstallSteamworks();
            }
            else if (EditorGUILayout.LinkButton("Download Git"))
            {
                Application.OpenURL(GitDownloadUrl);
            }

            if (EditorGUILayout.LinkButton("Steamworks.NET on GitHub"))
                Application.OpenURL(SteamworksRepositoryUrl);
        }
#if NUCLEUS_STEAMWORKS_ENABLED

        /// <summary>
        /// Reads how clients reach this peer's server, for the row under the status box.
        /// </summary>
        private void RefreshRuntime()
        {
            _serverRowLabel = string.Empty;
            _serverRowText = string.Empty;
            _serverRowCopyText = null;

            if (target is not SteamworksNetTransport { Transport: SteamSockets steamSockets } || !steamSockets.CoreManager.TransportManager.IsServerStarted)
                return;

            switch (steamSockets.Addressing)
            {
                case SteamworksNetAddressing.IpAddress:
                    // Clients need this machine's public IP address, which only the network knows, so only the port is shown.
                    _serverRowLabel = "Server's Port";
                    _serverRowText = $"UDP {steamSockets.Configuration.Port}";

                    break;
                case SteamworksNetAddressing.FakeIp:
                    _serverRowLabel = "Server's Fake Address";
                    _serverRowText = steamSockets.ServerFakeAddress ?? "Waiting for Steam to grant one";
                    _serverRowCopyText = steamSockets.ServerFakeAddress;

                    break;
                default:
                {
                    CSteamID serverSteamId = steamSockets.GameServerEnabled ? SteamGameServer.GetSteamID() : SteamUser.GetSteamID();

                    _serverRowLabel = "Server's SteamID";
                    _serverRowText = serverSteamId.m_SteamID.ToString();
                    _serverRowCopyText = _serverRowText;

                    break;
                }
            }
        }

        /// <summary>
        /// Gets the warning due while playing when Steam, or the game server a Game Server interface needs, is not running.
        /// </summary>
        /// <returns>The warning, or empty when none is due.</returns>
        /// <remarks>
        /// A dedicated server initializes only the game server, so the user's Steam is not asked for once one runs. The game server is
        /// not asked for while this peer runs only a client, which always uses the user's interface.
        /// </remarks>
        private string GetSteamWarningText()
        {
            bool isGameServerEnabled = serializedObject.FindProperty("_gameServerEnabled").boolValue;
            bool isGameServerRunning = !SteamNetworkingInterface.GameServer.IsShutDown();

            if (SteamSockets.IsSteamShutDown() && !(isGameServerEnabled && isGameServerRunning))
                return "Steam is not initialized, so the transport cannot start. The game calls SteamAPI.Init, or GameServer.Init for a dedicated server, before starting; this component never does.";

            bool isClientOnly = target is SteamworksNetTransport { Transport: SteamSockets steamSockets } && steamSockets.CoreManager.TransportManager is { IsClientStarted: true, IsServerStarted: false };

            if (isGameServerEnabled && !isGameServerRunning && !isClientOnly)
                return "Game Server is on, but no game server is running, so this peer cannot run a server. Call GameServer.Init before starting it, or turn Game Server off for a listen server.";

            return string.Empty;
        }

        /// <summary>
        /// Draws, while a server runs, the row naming how clients reach it, with a button that copies just the SteamID or fake address.
        /// </summary>
        private void DrawServerRow()
        {
            if (_serverRowLabel.Length == 0)
                return;

            Rect rowRect = EditorGUILayout.GetControlRect();
            float buttonWidth = _serverRowCopyText is null ? 0f : CopyButtonWidth + 4f;

            EditorGUI.LabelField(new Rect(rowRect.x, rowRect.y, rowRect.width - buttonWidth, rowRect.height), _serverRowLabel, _serverRowText);

            if (_serverRowCopyText is not null && GUI.Button(new Rect(rowRect.xMax - CopyButtonWidth, rowRect.y, CopyButtonWidth, rowRect.height), "Copy", EditorStyles.miniButton))
                EditorGUIUtility.systemCopyBuffer = _serverRowCopyText;
        }
#endif

        /// <summary>
        /// Draws a group's header in bold, as Unity heads its own inspector groups.
        /// </summary>
        /// <param name="title">The group's name.</param>
        private static void DrawHeader(string title)
        {
            EditorGUILayout.Space(GroupSpacing);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        /// <summary>
        /// Draws a section's title as a bold foldout, with its summary in the value column while it is collapsed.
        /// </summary>
        /// <param name="title">The section's name, which also keys its stored state.</param>
        /// <param name="summary">A one-line reading of the section's values.</param>
        /// <param name="isExpandedByDefault">Whether the section starts expanded before anyone has toggled it.</param>
        /// <returns><see langword="true"/> when the section is expanded and its contents should be drawn.</returns>
        /// <remarks>Expansion is kept for the editor session, shared by every transport, as Unity keeps its own foldouts.</remarks>
        private static bool DrawSection(string title, string summary, bool isExpandedByDefault = true)
        {
            string sessionKey = SectionExpandedSessionKeyPrefix + title;
            bool isExpanded = SessionState.GetBool(sessionKey, isExpandedByDefault);
            Rect rowRect = EditorGUILayout.GetControlRect();

            // The foldout spans the row with no text, so the whole row toggles, and the title is drawn bold over it.
            bool isNowExpanded = EditorGUI.Foldout(rowRect, isExpanded, GUIContent.none, toggleOnLabelClick: true);
            Rect titleRect = new(rowRect.x + FoldoutArrowWidth, rowRect.y, EditorGUIUtility.labelWidth - FoldoutArrowWidth, rowRect.height);

            EditorGUI.LabelField(titleRect, title, EditorStyles.boldLabel);

            if (!isNowExpanded)
            {
                Rect summaryRect = new(rowRect.x + EditorGUIUtility.labelWidth + 2f, rowRect.y, rowRect.width - EditorGUIUtility.labelWidth - 2f, rowRect.height);

                EditorGUI.LabelField(summaryRect, summary, EditorStyles.miniLabel);
            }

            if (isNowExpanded != isExpanded)
                SessionState.SetBool(sessionKey, isNowExpanded);

            return isNowExpanded;
        }

        /// <summary>
        /// Draws a property under its own label, with its unit at the right of the field.
        /// </summary>
        /// <param name="serializedProperty">The property to draw.</param>
        /// <param name="label">The label, used in place of the property's name, which already carries the unit.</param>
        /// <param name="unit">The unit, such as "s".</param>
        private static void DrawPropertyFieldWithUnit(SerializedProperty serializedProperty, string label, string unit)
        {
            Rect rowRect = EditorGUILayout.GetControlRect();

            EditorGUI.PropertyField(rowRect, serializedProperty, new GUIContent(label, serializedProperty.tooltip));
            DrawUnit(rowRect, unit);
        }

        /// <summary>
        /// Draws a byte count entered in kibibytes, with its unit at the right of the field.
        /// </summary>
        /// <param name="bytesSerializedProperty">The <see cref="uint"/> property holding the count in bytes.</param>
        /// <param name="label">The label, used in place of the property's name, which says bytes.</param>
        /// <param name="unit">The unit, such as "KiB/s".</param>
        /// <remarks>
        /// The property stays in bytes, so the API and saved values are unchanged. A count set from code that is not a whole number of
        /// kibibytes reads rounded down here, and is only rewritten once it is edited.
        /// </remarks>
        private static void DrawKibibyteField(SerializedProperty bytesSerializedProperty, string label, string unit)
        {
            Rect rowRect = EditorGUILayout.GetControlRect();

            using (EditorGUI.PropertyScope propertyScope = new(rowRect, new GUIContent(label, bytesSerializedProperty.tooltip), bytesSerializedProperty))
            {
                EditorGUI.BeginChangeCheck();
                EditorGUI.showMixedValue = bytesSerializedProperty.hasMultipleDifferentValues;

                long kibibytes = EditorGUI.LongField(rowRect, propertyScope.content, bytesSerializedProperty.uintValue / KibibyteBytes);

                EditorGUI.showMixedValue = false;

                if (EditorGUI.EndChangeCheck())
                    bytesSerializedProperty.uintValue = (uint)Math.Clamp(kibibytes, 0L, uint.MaxValue / KibibyteBytes) * KibibyteBytes;
            }

            DrawUnit(rowRect, unit);
        }

        /// <summary>
        /// Draws a unit at the right of a field already drawn in a row.
        /// </summary>
        /// <param name="rowRect">The row the field was drawn in.</param>
        /// <param name="unit">The unit.</param>
        /// <remarks>The unit is measured and placed against the field's right edge, since the shared mini label style aligns left.</remarks>
        private static void DrawUnit(Rect rowRect, string unit)
        {
            GUIContent unitGuiContent = new(unit);
            float unitWidth = EditorStyles.miniLabel.CalcSize(unitGuiContent).x;

            GUI.Label(new Rect(rowRect.xMax - unitWidth - UnitPadding, rowRect.y, unitWidth, rowRect.height), unitGuiContent, EditorStyles.miniLabel);
        }

        /// <summary>
        /// Asks the Package Manager to add Steamworks.NET, and watches the request until it finishes.
        /// </summary>
        private void InstallSteamworks()
        {
            _installErrorText = null;
            _addRequest = Client.Add(SteamworksPackageUrl);

            EditorApplication.update += OnEditorUpdated;
        }

        /// <summary>
        /// Shows why a package request failed once it has finished.
        /// </summary>
        /// <remarks>
        /// The Package Manager raises no event when a request fails, so the request is checked each editor update while it is in flight,
        /// and the check is dropped as soon as it finishes. A success needs no handling: the editor recompiles with the package.
        /// </remarks>
        private void OnEditorUpdated()
        {
            if (_addRequest is not { IsCompleted: true })
                return;

            EditorApplication.update -= OnEditorUpdated;

            if (_addRequest.Status is StatusCode.Failure)
                _installErrorText = $"Steamworks.NET could not be added: {_addRequest.Error.message}";

            _addRequest = null;
            Repaint();
        }

        /// <summary>
        /// Logs, once per editor session, that the Steam transport is compiled out, because Steamworks.NET is missing or is compiled out itself.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void OnEditorLoaded()
        {
#if !NUCLEUS_STEAMWORKS_ENABLED
            if (SessionState.GetBool(MissingWarningLoggedSessionKey, defaultValue: false))
                return;

            SessionState.SetBool(MissingWarningLoggedSessionKey, true);
#if NUCLEUS_STEAMWORKS
            Debug.LogWarning(SteamworksNetTransport.SteamworksDisabledText);
#else
            Debug.LogWarning(SteamworksNetTransport.SteamworksMissingText);
#endif
#endif
        }

        /// <summary>
        /// Gets the label of the field a client connects to, named for what the addressing expects in it.
        /// </summary>
        /// <param name="addressing">The addressing being drawn.</param>
        /// <returns>The label.</returns>
        private static string GetTargetLabel(SteamworksNetAddressing addressing)
        {
            return addressing switch
            {
                SteamworksNetAddressing.IpAddress => "Server's Address",
                SteamworksNetAddressing.FakeIp => "Server's Fake Address",
                _ => "Server's SteamID"
            };
        }

        /// <summary>
        /// Gets the label of the port server and clients agree on.
        /// </summary>
        /// <param name="addressing">The addressing being drawn.</param>
        /// <returns>The label.</returns>
        private static string GetPortLabel(SteamworksNetAddressing addressing) => addressing is SteamworksNetAddressing.SteamId ? "Virtual Port" : "Port";

        /// <summary>
        /// Writes a send setting's byte count in the largest whole unit that reads cleanly.
        /// </summary>
        /// <param name="bytes">The count, or <see cref="UnsetSendSetting"/> for Steam's default.</param>
        /// <param name="steamDefaultBytes">The count Steam uses when the setting is unset.</param>
        /// <returns>The count, such as "4 MiB" or "300 KiB".</returns>
        private static string FormatBytes(uint bytes, uint steamDefaultBytes)
        {
            if (bytes is UnsetSendSetting)
                bytes = steamDefaultBytes;

            if (bytes >= 1024U * 1024U && bytes % (1024U * 1024U) == 0U)
                return $"{bytes / (1024U * 1024U)} MiB";

            return bytes >= 1024U ? $"{bytes / 1024U} KiB" : $"{bytes} B";
        }

        /// <summary>
        /// Writes a timeout in seconds, reading zero as Steam's default.
        /// </summary>
        /// <param name="seconds">The timeout, or zero for Steam's default.</param>
        /// <returns>The timeout, such as "15 s".</returns>
        private static string FormatSeconds(int seconds) => $"{(seconds == 0 ? SteamDefaultTimeoutSeconds : seconds)} s";

        /// <summary>
        /// Checks whether <c>git</c> runs from this editor's path, which is the Git the Package Manager uses.
        /// </summary>
        /// <returns><see langword="true"/> when <c>git --version</c> ran and succeeded.</returns>
        private static bool IsGitFound()
        {
            try
            {
                using Process gitProcess = Process.Start(new ProcessStartInfo("git", "--version")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });

                return gitProcess is not null && gitProcess.WaitForExit(GitCheckTimeoutMilliseconds) && gitProcess.ExitCode == 0;
            }
            catch (Exception)
            {
                // Not found on the path; Process.Start throws rather than returning null for a missing executable.
                return false;
            }
        }
    }
}
