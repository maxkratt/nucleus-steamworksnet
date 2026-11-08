// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using UnityEditor;

namespace Nucleus.Integrations.SteamworksNet.Samples.Editor
{
    /// <summary>
    /// Inspector presentation for <see cref="PlayerNameTag"/>: the skin, the height above the object, and the speaking icon with its
    /// size, which is drawn only once an icon is set.
    /// </summary>
    [CustomEditor(typeof(PlayerNameTag))]
    public class PlayerNameTagEditor : UnityEditor.Editor
    {
        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            // Drawn here rather than by Nucleus, whose script row adds a second help button that ignores this component's HelpURL.
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_guiSkin"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_heightOffset"));
            SerializedProperty speakingIconSerializedProperty = serializedObject.FindProperty("_speakingIcon");
            EditorGUILayout.PropertyField(speakingIconSerializedProperty);

            using (new EditorGUI.IndentLevelScope())
            {
                if (speakingIconSerializedProperty.objectReferenceValue != null)
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_speakingIconSize"));
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
