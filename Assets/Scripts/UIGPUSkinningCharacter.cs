using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Optimize.GPUSkinning;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UI3D
{
    /// <summary>
    /// Character-level driver for UI GPU skinning.
    ///
    /// Assign a single <see cref="GPUSkinningCharacterData"/> asset (baked via
    /// "Assets > Create > Optimize > GPU Skinning > Bake Character (All Parts)") and
    /// a reference to the animated skeleton root — the component does the rest.
    ///
    /// One <see cref="UIGPUSkinningRenderer"/> child is created per part automatically.
    /// The Animator on the character root keeps driving the bones; the UI renderers
    /// read those bone transforms every frame.
    ///
    /// Must be placed inside a Canvas (Screen Space – Camera or World Space recommended).
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    [AddComponentMenu("UI/3D/UI GPU Skinning Character")]
    [ExecuteAlways]
    public class UIGPUSkinningCharacter : MonoBehaviour
    {
        // ──────────────────────────────────────────────────────────────────────
        // Inspector
        // ──────────────────────────────────────────────────────────────────────

        [Header("Character")]
        [Tooltip("Animated skeleton root — the GameObject that owns the Animator and bone hierarchy.")]
        [SerializeField] public Transform m_CharacterRoot;

        [Tooltip("Single baked asset for the whole character. " +
                 "Bake via: Assets > Create > Optimize > GPU Skinning > Bake Character (All Parts).")]
        [SerializeField] public GPUSkinningCharacterData m_CharacterData;

        [Header("Material")]
        [Tooltip("Shader used to create per-part UI materials. Defaults to 'UI/3DModel'. " +
                 "Must be present in Always Included Shaders or assigned directly for builds.")]
        [SerializeField] public Shader m_UIShader;

        [Header("Depth Clearing")]
        [Tooltip("Insert a full-screen depth-clear quad after the character so standard UI panels " +
                 "drawn on top are not occluded by the 3D depth values.")]
        [SerializeField] public bool m_ClearDepthAfterDraw = true;
        [SerializeField] public Shader m_DepthClearShader;

        // ──────────────────────────────────────────────────────────────────────
        // Private state
        // ──────────────────────────────────────────────────────────────────────

        private readonly List<UIGPUSkinningRenderer> m_Renderers = new List<UIGPUSkinningRenderer>();
        private readonly List<Material>              m_Instances  = new List<Material>();
        private GameObject                           m_DepthWiper;
        private bool                                 m_Built;

        // ──────────────────────────────────────────────────────────────────────
        // Lifecycle
        // ──────────────────────────────────────────────────────────────────────

        private void Awake()
        {
            if (Application.isPlaying) Build();
        }

        private void OnEnable()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) Build();
#endif
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) Teardown();
#endif
        }

        private void OnDestroy() => Teardown();

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Creating GameObjects with UI components fires SendMessage internally,
            // which Unity forbids inside OnValidate. Defer to the next editor tick.
            EditorApplication.delayCall += () =>
            {
                if (this == null) return;
                Teardown();
                Build();
            };
        }
#endif

        // ──────────────────────────────────────────────────────────────────────
        // Build / Teardown
        // ──────────────────────────────────────────────────────────────────────

        private void Build()
        {
            if (m_Built) return;
            if (m_CharacterRoot == null || m_CharacterData == null || !m_CharacterData.Valid()) return;

            m_Built = true;

            // ── Canvas channel requirements ───────────────────────────────────
            var canvas = GetComponentInParent<Canvas>();
            if (canvas != null)
            {
                canvas.additionalShaderChannels |=
                    AdditionalCanvasShaderChannels.TexCoord1 |
                    AdditionalCanvasShaderChannels.TexCoord2 |
                    AdditionalCanvasShaderChannels.TexCoord3 |
                    AdditionalCanvasShaderChannels.Normal;
            }
            else
            {
                Debug.LogWarning($"[UIGPUSkinningCharacter] '{name}' is not under a Canvas — parts will not render.", this);
            }

            // ── Resolve shader ────────────────────────────────────────────────
            Shader shader = m_UIShader != null ? m_UIShader : Shader.Find("UI/3DModel");
            if (shader == null)
            {
                Debug.LogError("[UIGPUSkinningCharacter] UI/3DModel shader not found. " +
                               "Assign it to 'UI Shader' or add it to Always Included Shaders.", this);
                return;
            }

            var   rootRT    = (RectTransform)transform;
            var   rootPivot = rootRT.pivot;
            var   parts     = m_CharacterData.m_Parts;

            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (!part.Valid()) continue;

                // ── Child RectTransform ───────────────────────────────────────
                var partGo  = new GameObject($"[UI-GPU] {part.partName}");
                partGo.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;
#if UNITY_EDITOR
                if (!Application.isPlaying) partGo.hideFlags |= HideFlags.HideInHierarchy;
#endif
                partGo.transform.SetParent(transform, false);

                var rt              = partGo.AddComponent<RectTransform>();
                rt.anchorMin        = rootPivot;
                rt.anchorMax        = rootPivot;
                rt.pivot            = rootPivot;
                rt.sizeDelta        = Vector2.zero;
                rt.anchoredPosition = Vector2.zero;

                // ── Per-part material instance ────────────────────────────────
                var mat        = new Material(shader) { hideFlags = HideFlags.DontSave };
                mat.name       = $"{part.partName}_UIMat";
                if (part.mainTexture != null) mat.mainTexture = part.mainTexture;
                mat.color      = part.color;
                m_Instances.Add(mat);

                // ── UIGPUSkinningRenderer ─────────────────────────────────────
                var renderer              = partGo.AddComponent<UIGPUSkinningRenderer>();
                renderer.m_Data           = CreateRuntimePartData(part);
                renderer.m_RootBone       = m_CharacterRoot;
                renderer.material         = mat;

                m_Renderers.Add(renderer);
            }

            // ── Depth wiper ───────────────────────────────────────────────────
            if (m_ClearDepthAfterDraw)
            {
                m_DepthWiper           = new GameObject("[UI-GPU] Depth Wiper",
                    typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
                m_DepthWiper.hideFlags = HideFlags.DontSave;

                var wiperRT       = m_DepthWiper.GetComponent<RectTransform>();
                wiperRT.SetParent(transform, false);
                wiperRT.anchorMin = Vector2.zero;
                wiperRT.anchorMax = Vector2.one;
                wiperRT.offsetMin = Vector2.zero;
                wiperRT.offsetMax = Vector2.zero;

                var raw = m_DepthWiper.GetComponent<RawImage>();
                raw.raycastTarget = false;

                Shader clearShader = m_DepthClearShader != null
                    ? m_DepthClearShader
                    : Shader.Find("UI/DepthClear");
                if (clearShader != null)
                    raw.material = new Material(clearShader) { hideFlags = HideFlags.DontSave };
                else
                    Debug.LogWarning("[UIGPUSkinningCharacter] UI/DepthClear shader not found.", this);
            }
        }

        /// <summary>
        /// Converts a <see cref="GPUSkinningPartEntry"/> into the <see cref="GPUSkinningData"/>
        /// that <see cref="UIGPUSkinningRenderer"/> expects — without allocating a new asset on disk.
        /// </summary>
        private static GPUSkinningData CreateRuntimePartData(GPUSkinningPartEntry part)
        {
            var data    = ScriptableObject.CreateInstance<GPUSkinningData>();
            data.m_Mesh  = part.mesh;
            data.m_Bones = part.bones;
            data.hideFlags = HideFlags.DontSave;
            return data;
        }

        private void Teardown()
        {
            m_Built = false;

            foreach (var r in m_Renderers)
                if (r != null) DestroyObj(r.gameObject);
            m_Renderers.Clear();

            foreach (var mat in m_Instances)
                DestroyObj(mat);
            m_Instances.Clear();

            if (m_DepthWiper != null) { DestroyObj(m_DepthWiper); m_DepthWiper = null; }
        }

        private static void DestroyObj(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else                       DestroyImmediate(o);
        }
    }
}
