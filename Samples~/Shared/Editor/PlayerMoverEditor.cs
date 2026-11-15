// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED && NUCLEUS_SAMPLES_INPUT_SYSTEM
using UnityEditor;

namespace Nucleus.Integrations.SteamworksNet.Samples.Editor
{
    /// <summary>
    /// Inspector presentation for <see cref="PlayerMover"/>: the move speed, the height above the ground, and where and when a fallen
    /// object is put back.
    /// </summary>
    [CustomEditor(typeof(PlayerMover))]
    public class PlayerMoverEditor : UnityEditor.Editor
    {
        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            // Drawn here rather than by Nucleus, whose script row adds a second help button that ignores this component's HelpURL.
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_moveSpeed"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_groundOffset"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_respawnHeight"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_respawnPosition"));
            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
