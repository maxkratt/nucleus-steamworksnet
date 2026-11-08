// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using UnityEditor;
using UnityEngine;

namespace Nucleus.Integrations.SteamworksNet.Samples.Editor
{
    /// <summary>
    /// Inspector presentation for <see cref="SteamBootstrap"/>: the App ID, or the game server option with its ports and login token, and
    /// whether Steam is up and the local SteamID while playing.
    /// </summary>
    [CustomEditor(typeof(SteamBootstrap))]
    public class SteamBootstrapEditor : UnityEditor.Editor
    {
        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            // Drawn here rather than by Nucleus, whose script row adds a second help button that ignores this component's HelpURL.
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));

            serializedObject.Update();
            SerializedProperty gameServerEnabledSerializedProperty = serializedObject.FindProperty("_gameServerEnabled");

            // The relaunch check is the user's Steam only, so a game server hides it.
            if (!gameServerEnabledSerializedProperty.boolValue)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_appId"));

            EditorGUILayout.PropertyField(gameServerEnabledSerializedProperty, new GUIContent("Game Server", gameServerEnabledSerializedProperty.tooltip));

            using (new EditorGUI.IndentLevelScope())
            {
                if (gameServerEnabledSerializedProperty.boolValue)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_gameServerPort"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_gameServerQueryPort"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_gameServerLoginToken"));
                }
            }
            serializedObject.ApplyModifiedProperties();

            if (!EditorApplication.isPlaying)
                return;

            SteamBootstrap steamBootstrap = (SteamBootstrap)target;

            EditorGUILayout.LabelField("Steam Initialized", steamBootstrap.IsSteamInitialized.ToString());
            EditorGUILayout.LabelField("SteamID", steamBootstrap.OwnSteamId.ToString());
        }
    }
}
#endif
