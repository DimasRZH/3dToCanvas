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
    /// UI-canvas drop-in for <see cref="GPUSkinningBehaviour"/>.
    ///
    /// Place this on a RectTransform that is a descendant of a Canvas.
    /// Assign <see cref="m_Data"/> (baked GPUSkinningData asset) and
    /// <see cref="m_RootBone"/> (the animated skeleton root in the scene).
    /// Set <see cref="m_Material"/> to a "UI/3DModel" material with the right texture.
    ///
    /// The component:
    ///   - Sets the baked static mesh directly on the CanvasRenderer (zero re-upload per frame).
    ///   - Computes bone matrices on the CPU each LateUpdate (trivially cheap — bone count, not vertex count).
    ///   - Uploads the matrix array via Material.SetMatrixArray("_Bones") — no StructuredBuffer needed.
    ///   - Works with the existing UIModelShader ("UI/3DModel") which already declares _Bones[150]
    ///     and reads our uv1/uv2/uv3 bone index + weight layout.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    [AddComponentMenu("UI/3D/UI GPU Skinning Renderer")]
    [ExecuteAlways]
    public class UIGPUSkinningRenderer : MaskableGraphic
    {
        // ──────────────────────────────────────────────────────────────────────
        // Inspector fields
        // ──────────────────────────────────────────────────────────────────────

        [Header("GPU Skinning Data")]
        [Tooltip("Baked GPUSkinningData asset (produced by Assets > Create > Optimize > GPU Skinning > Bake Skinned Mesh).")]
        [SerializeField] public GPUSkinningData m_Data;

        [Tooltip("Root of the animated skeleton hierarchy. Bone paths stored in m_Data are relative to this transform.")]
        [SerializeField] public Transform m_RootBone;

        [Header("Debug")]
        [SerializeField] public bool m_BoundsGizmos = false;

        // ──────────────────────────────────────────────────────────────────────
        // Private state
        // ──────────────────────────────────────────────────────────────────────

        private List<Transform>  m_Bones          = new List<Transform>();
        private Matrix4x4[]      m_SamplingMatrices;
        private Material         m_MaterialInstance;
        private bool             m_MeshSet;

        private static readonly int kBonesID = Shader.PropertyToID("_Bones");

        // ──────────────────────────────────────────────────────────────────────
        // MaskableGraphic overrides
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>We drive the mesh ourselves; clear the auto-generated quad.</summary>
        protected override void OnPopulateMesh(VertexHelper vh) => vh.Clear();

        /// <summary>Keep the baked mesh set on the CanvasRenderer even after canvas rebuilds.</summary>
        protected override void UpdateGeometry()
        {
            // Do NOT call base — that would overwrite our mesh with the VertexHelper output.
            if (m_Data != null && m_Data.m_Mesh != null)
                canvasRenderer.SetMesh(m_Data.m_Mesh);
        }

        public override Texture mainTexture
        {
            get
            {
                if (material != null && material.mainTexture != null)
                    return material.mainTexture;
                return base.mainTexture;
            }
        }

        protected override void UpdateMaterial()
        {
            base.UpdateMaterial();
            EnsureMaterialInstance();
            if (m_MaterialInstance != null)
            {
                canvasRenderer.materialCount = 1;
                canvasRenderer.SetMaterial(m_MaterialInstance, 0);
                if (m_MaterialInstance.mainTexture != null)
                    canvasRenderer.SetTexture(m_MaterialInstance.mainTexture);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Lifecycle
        // ──────────────────────────────────────────────────────────────────────

        protected override void OnEnable()
        {
            base.OnEnable();
            // Ensure the Canvas knows we need the extra UV channels for bone data.
            RequestCanvasChannels();
            Initialize();
            // Force first update so the buffer is populated before any render.
            LateUpdate();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            DestroyMaterialInstance();
        }

        protected override void OnDestroy()
        {
            DestroyMaterialInstance();
            base.OnDestroy();
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();
            // Defer: modifying canvasRenderer inside OnValidate triggers SendMessage which Unity forbids.
            EditorApplication.delayCall += () =>
            {
                if (this == null) return;
                m_Bones.Clear();
                m_MeshSet      = false;
                DestroyMaterialInstance();
                Initialize();
                LateUpdate();
            };
        }
#endif

        // ──────────────────────────────────────────────────────────────────────
        // Initialization
        // ──────────────────────────────────────────────────────────────────────

        private void RequestCanvasChannels()
        {
            var c = GetComponentInParent<Canvas>();
            if (c == null) return;
            c.additionalShaderChannels |=
                AdditionalCanvasShaderChannels.TexCoord1 |
                AdditionalCanvasShaderChannels.TexCoord2 |
                AdditionalCanvasShaderChannels.TexCoord3 |
                AdditionalCanvasShaderChannels.Normal;
        }

        private void EnsureMaterialInstance()
        {
            if (m_MaterialInstance != null) return;
            if (material == null)   return;

            m_MaterialInstance = new Material(material)
            {
                name      = material.name + "_GPUSkinInst",
                hideFlags = HideFlags.DontSave
            };
        }

        private void DestroyMaterialInstance()
        {
            if (m_MaterialInstance == null) return;
            if (Application.isPlaying) Destroy(m_MaterialInstance);
            else                       DestroyImmediate(m_MaterialInstance);
            m_MaterialInstance = null;
        }

        /// <summary>
        /// Resolves bone transforms and allocates the matrix buffer.
        /// Safe to call repeatedly — returns early if already done.
        /// </summary>
        private bool Initialize()
        {
            if (m_Data == null || !m_Data.Valid() || m_RootBone == null)
                return false;

            // ── Mesh ──────────────────────────────────────────────────────────
            if (!m_MeshSet && m_Data.m_Mesh != null)
            {
                canvasRenderer.SetMesh(m_Data.m_Mesh);
                m_MeshSet = true;
            }

            // ── Material instance ─────────────────────────────────────────────
            EnsureMaterialInstance();
            if (m_MaterialInstance != null)
            {
                canvasRenderer.materialCount = 1;
                canvasRenderer.SetMaterial(m_MaterialInstance, 0);
                if (m_MaterialInstance.mainTexture != null)
                    canvasRenderer.SetTexture(m_MaterialInstance.mainTexture);
            }

            // ── Matrix buffer ─────────────────────────────────────────────────
            int boneCount   = m_Data.m_Bones.Count;
            int bufferCount = Mathf.Max(1, boneCount);
            if (m_SamplingMatrices == null || m_SamplingMatrices.Length != bufferCount)
                m_SamplingMatrices = new Matrix4x4[bufferCount];

            // ── Bone resolution ───────────────────────────────────────────────
            if (m_Bones.Count == boneCount) return true; // already resolved

            m_Bones.Clear();
            for (int i = 0; i < boneCount; i++)
            {
                var bd             = m_Data.m_Bones[i];
                var boneTransform  = m_RootBone.Find(bd.relativePath);

                if (boneTransform == null)
                    boneTransform = FindChildRecursive(m_RootBone, LastSegment(bd.relativePath));

                if (boneTransform == null)
                {
                    Debug.LogError($"[UIGPUSkinningRenderer] '{name}': bone not found — '{bd.relativePath}'", this);
                    m_Bones.Clear();
                    return false;
                }

                m_Bones.Add(boneTransform);
            }

            return true;
        }

        // ──────────────────────────────────────────────────────────────────────
        // Per-frame update
        // ──────────────────────────────────────────────────────────────────────

        private void LateUpdate()
        {
            if (!Initialize()) return;
            if (m_MaterialInstance == null) return;

            int       boneCount = m_Bones.Count;
            Matrix4x4 rootW2L   = m_RootBone.worldToLocalMatrix;

            if (boneCount == 0)
            {
                // 0-bone (static) part: identity keeps vertices at rest-pose in object space.
                m_SamplingMatrices[0] = Matrix4x4.identity;
            }
            else
            {
                for (int i = 0; i < boneCount; i++)
                {
                    var bd = m_Data.m_Bones[i];
                    // Bring the bone from bind pose -> world space -> 3D Root's local space.
                    // By outputting into the Root's local space, the CanvasRenderer then naturally 
                    // applies the UI RectTransform's position, rotation, and scale (e.g. 300x) to it!
                    m_SamplingMatrices[i] = rootW2L * m_Bones[i].localToWorldMatrix * bd.bindPose;
                }
            }

            m_MaterialInstance.SetMatrixArray(kBonesID, m_SamplingMatrices);
        }

        // ──────────────────────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────────────────────

        private static string LastSegment(string path)
        {
            int idx = path.LastIndexOf('/');
            return idx >= 0 ? path.Substring(idx + 1) : path;
        }

        private static Transform FindChildRecursive(Transform parent, string name)
        {
            if (parent.name == name) return parent;
            for (int i = 0; i < parent.childCount; i++)
            {
                var found = FindChildRecursive(parent.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!m_BoundsGizmos || m_Data == null || m_Bones.Count == 0) return;

            Gizmos.color = Color.cyan;
            for (int i = 0; i < m_Bones.Count; i++)
            {
                var bd       = m_Data.m_Bones[i];
                var centerWS = m_Bones[i].TransformPoint(bd.boundsCenter);
                float radius = bd.boundsRadius * Mathf.Max(
                    m_Bones[i].lossyScale.x,
                    Mathf.Max(m_Bones[i].lossyScale.y, m_Bones[i].lossyScale.z));
                Gizmos.DrawWireSphere(centerWS, radius);
            }
        }
#endif
    }
}
