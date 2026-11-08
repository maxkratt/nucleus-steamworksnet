// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using UnityEditor;

namespace Nucleus.Integrations.SteamworksNet.Samples.VoiceChat.Editor
{
    /// <summary>
    /// Inspector presentation for <see cref="VoiceChatPanel"/>: the bootstrap, transport manager and transport it drives.
    /// </summary>
    [CustomEditor(typeof(VoiceChatPanel))]
    public class VoiceChatPanelEditor : UnityEditor.Editor
    {
        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            // Drawn here rather than by Nucleus, whose script row adds a second help button that ignores this component's HelpURL.
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_steamBootstrap"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_unityTransportManager"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_steamSocketsTransport"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_guiSkin"));
            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
