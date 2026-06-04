using UnityEditor;
using UnityEngine;
using UI3D;

namespace UI3DEditor
{
    /// <summary>
    /// Inspector for UIBurstSkinnedCharacter. The component already renders a live Edit-Mode preview
    /// via [ExecuteAlways]; this just adds a manual "Rebuild Preview" button for when the artist
    /// changes the source model/rig and wants to force a clean rebuild.
    /// </summary>
    [CustomEditor(typeof(UIBurstSkinnedCharacter))]
    public class UIBurstSkinnedCharacterEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                if (GUILayout.Button("Rebuild Preview"))
                {
                    foreach (var t in targets)
                    {
                        if (t is UIBurstSkinnedCharacter c) c.RebuildPreview();
                    }
                    SceneView.RepaintAll();
                }
            }

            if (Application.isPlaying)
                EditorGUILayout.HelpBox("Preview controls are Edit-Mode only.", MessageType.None);
        }
    }
}
