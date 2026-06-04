using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace UI3D
{
    [RequireComponent(typeof(CanvasRenderer))]
    [AddComponentMenu("UI/3D/UI Burst Skinned Renderer")]
    public class UIBurstSkinnedRenderer : MaskableGraphic
    {
        [Header("Source Settings")]
        [SerializeField] private SkinnedMeshRenderer m_SourceRenderer;
        [SerializeField] private MeshFilter m_SourceMeshFilter;
        [SerializeField] private MeshRenderer m_SourceMeshRenderer;
        [SerializeField] private int m_SubMeshIndex = 0;
        [SerializeField] private Texture m_BakedTexture;

        [Header("Rendering Settings")]
        [SerializeField] private Material m_CustomMaterial;

        // Dynamic mesh updated by CPU Burst Skinning every frame.
        private Mesh m_DynamicMesh;
        private bool m_IsInitialized;

#if UNITY_EDITOR
        private Mesh m_EditModeBakedMesh;
#endif

        private int m_BoneStart;
        private int m_BoneCount;
        private Transform[] m_Bones;

        private int m_VertexStart;
        private int m_VertexCount;

        // The exact material instance handed to the CanvasRenderer
        private Material m_MaterialInstance;

        public int BoneStart => m_BoneStart;
        public Transform[] Bones => m_Bones;
        public int VertexStart => m_VertexStart;
        public int VertexCount => m_VertexCount;

        public override Texture mainTexture
        {
            get
            {
                if (m_CustomMaterial != null && m_CustomMaterial.mainTexture != null)
                    return m_CustomMaterial.mainTexture;
                if (m_SourceRenderer != null && m_SourceRenderer.sharedMaterial != null)
                    return m_SourceRenderer.sharedMaterial.mainTexture;
                if (m_SourceMeshRenderer != null && m_SourceMeshRenderer.sharedMaterial != null)
                    return m_SourceMeshRenderer.sharedMaterial.mainTexture;
                if (m_BakedTexture != null)
                    return m_BakedTexture;
                return base.mainTexture;
            }
        }

        public override Material material
        {
            get
            {
                if (m_CustomMaterial != null)
                    return m_CustomMaterial;
                return defaultMaterial;
            }
            set
            {
                if (m_CustomMaterial != value)
                {
                    m_CustomMaterial = value;
                    SetMaterialDirty();
                }
            }
        }

        protected override void OnDestroy()
        {
            Cleanup();
            base.OnDestroy();
        }

        private void Cleanup()
        {
            m_IsInitialized = false;
            if (m_DynamicMesh != null)
            {
                if (Application.isPlaying) Destroy(m_DynamicMesh);
                else DestroyImmediate(m_DynamicMesh);
                m_DynamicMesh = null;
            }
#if UNITY_EDITOR
            if (m_EditModeBakedMesh != null)
            {
                DestroyImmediate(m_EditModeBakedMesh);
                m_EditModeBakedMesh = null;
            }
#endif
        }

        protected override void UpdateMaterial()
        {
            base.UpdateMaterial();
            if (canvasRenderer == null) return;

            m_MaterialInstance = materialForRendering;

            canvasRenderer.materialCount = 1;
            canvasRenderer.SetMaterial(m_MaterialInstance, 0);
            canvasRenderer.SetTexture(mainTexture);
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
        }

        protected override void UpdateGeometry()
        {
            if (m_DynamicMesh != null)
            {
                canvasRenderer.SetMesh(m_DynamicMesh);
            }
        }

        // Character footprint in this part's local space (canvas units), pushed each frame by
        // UIBurstSkinnedCharacter. Defaults huge so nothing is culled before it's set.
        private Rect m_ContentLocalRect = new Rect(-1e5f, -1e5f, 2e5f, 2e5f);

        public void SetContentLocalRect(Rect localRect) => m_ContentLocalRect = localRect;

        // RectMask2D/Mask cull from this graphic's RectTransform rect, but our part's rect is a
        // zero-size point at the character origin (near the spine) while the mesh covers the whole
        // body — so the default cull hides the entire part as soon as that point leaves the mask
        // (e.g. mask over the legs => character vanishes). We instead cull against the actual
        // character footprint (coarse, cheap, preserves the perf win of hiding fully-offscreen parts)
        // and let the shader's _ClipRect handle exact per-pixel edges.
        public override void Cull(Rect clipRect, bool validRect)
        {
            if (canvasRenderer == null) return;

            // Replicate MaskableGraphic's space: footprint corners -> world -> root-canvas local.
            var rootCanvas = canvas != null ? canvas.rootCanvas : null;
            Matrix4x4 toCanvas = rootCanvas != null ? rootCanvas.transform.worldToLocalMatrix : Matrix4x4.identity;
            Matrix4x4 m = toCanvas * transform.localToWorldMatrix;

            Vector3 p0 = m.MultiplyPoint3x4(new Vector3(m_ContentLocalRect.xMin, m_ContentLocalRect.yMin, 0f));
            Vector3 p1 = m.MultiplyPoint3x4(new Vector3(m_ContentLocalRect.xMax, m_ContentLocalRect.yMin, 0f));
            Vector3 p2 = m.MultiplyPoint3x4(new Vector3(m_ContentLocalRect.xMax, m_ContentLocalRect.yMax, 0f));
            Vector3 p3 = m.MultiplyPoint3x4(new Vector3(m_ContentLocalRect.xMin, m_ContentLocalRect.yMax, 0f));

            float minX = Mathf.Min(Mathf.Min(p0.x, p1.x), Mathf.Min(p2.x, p3.x));
            float maxX = Mathf.Max(Mathf.Max(p0.x, p1.x), Mathf.Max(p2.x, p3.x));
            float minY = Mathf.Min(Mathf.Min(p0.y, p1.y), Mathf.Min(p2.y, p3.y));
            float maxY = Mathf.Max(Mathf.Max(p0.y, p1.y), Mathf.Max(p2.y, p3.y));
            var footprint = new Rect(minX, minY, maxX - minX, maxY - minY);

            bool cull = !validRect || !clipRect.Overlaps(footprint, true);
            if (canvasRenderer.cull != cull) canvasRenderer.cull = cull;
        }

        public void InitializeFromMaster(
            UIBurstSkinnedCharacter character,
            SkinnedMeshRenderer sourceRenderer,
            MeshFilter sourceMeshFilter,
            MeshRenderer sourceMeshRenderer,
            Mesh sourceMesh,
            int subMeshIndex,
            bool isStatic,
            int boneStart,
            int boneCount,
            Transform[] bones,
            int vertexStart,
            int vertexCount,
            Texture bakedTexture = null)
        {
            m_SourceRenderer = sourceRenderer;
            m_SourceMeshFilter = sourceMeshFilter;
            m_SourceMeshRenderer = sourceMeshRenderer;
            m_SubMeshIndex = subMeshIndex;
            m_BoneStart = boneStart;
            m_BoneCount = boneCount;
            m_Bones = bones;
            m_VertexStart = vertexStart;
            m_VertexCount = vertexCount;
            m_BakedTexture = bakedTexture;

            if (sourceMesh == null) return;

            // Original 3D renderers are hidden by UIBurstSkinnedCharacter (non-serialized
            // forceRenderingOff), so it can restore them on teardown — nothing to do here.
            // Read/Write on the source mesh is NOT required: ingest goes through MeshData APIs.

            BuildDynamicMesh(sourceMesh);

            m_IsInitialized = true;
            UpdateMaterial();
        }

        private void BuildDynamicMesh(Mesh sourceMesh)
        {
            m_DynamicMesh = new Mesh();
            m_DynamicMesh.name = "UI Skinned (CPU) - " + sourceMesh.name;
            m_DynamicMesh.MarkDynamic();
            m_DynamicMesh.hideFlags = HideFlags.DontSave; // never serialize the generated mesh

            int subMesh = m_SubMeshIndex;
            if (subMesh < 0 || subMesh >= sourceMesh.subMeshCount)
            {
                subMesh = m_SubMeshIndex = 0;
            }

            // Build the per-part dynamic mesh via writable MeshData. We mirror the source mesh's
            // vertex layout 1:1 and copy each stream byte-for-byte, so SetVertices/Normals/Tangents
            // per-frame keep working without ever touching the source's CPU copy (no Read/Write).
            // AcquireMeshDataWithoutCheck skips R/W check in editor.
            var srcArray = AcquireMeshDataWithoutCheck(sourceMesh);
            var writable = Mesh.AllocateWritableMeshData(1);
            try
            {
                var src = srcArray[0];
                var dst = writable[0];
                int vc = sourceMesh.vertexCount;

                // Mirror source vertex layout (preserves UI batching expectations)
                var attrDescs = sourceMesh.GetVertexAttributes();
                var attrs = new NativeArray<VertexAttributeDescriptor>(attrDescs, Allocator.Temp);
                dst.SetVertexBufferParams(vc, attrs);
                attrs.Dispose();

                // Copy each vertex stream raw
                for (int s = 0; s < src.vertexBufferCount; s++)
                {
                    var srcStream = src.GetVertexData<byte>(s);
                    var dstStream = dst.GetVertexData<byte>(s);
                    dstStream.CopyFrom(srcStream);
                }

                // Copy this submesh's indices (baseVertex pre-applied so we can use baseVertex=0).
                // CanvasRenderer requires 16-bit indices; bail out if the source needs 32-bit.
                int triCount = (int)sourceMesh.GetIndexCount(subMesh);
                if (sourceMesh.indexFormat != IndexFormat.UInt16)
                {
                    Debug.LogError($"Mesh '{sourceMesh.name}' must use 16-bit indices for CanvasRenderer.", this);
                    return;
                }
                dst.SetIndexBufferParams(triCount, IndexFormat.UInt16);
                var srcIdx = src.GetIndexData<ushort>();
                var sub = src.GetSubMesh(subMesh);
                var dstIdx = dst.GetIndexData<ushort>();
                // Indices reference the full source vertex buffer (which we copied verbatim),
                // with baseVertex added so the dynamic mesh can use a submesh at baseVertex=0.
                for (int i = 0; i < triCount; i++)
                {
                    dstIdx[i] = (ushort)(srcIdx[sub.indexStart + i] + sub.baseVertex);
                }

                dst.subMeshCount = 1;
                dst.SetSubMesh(0, new SubMeshDescriptor(0, triCount, MeshTopology.Triangles)
                {
                    firstVertex = 0,
                    vertexCount = vc,
                    bounds = new Bounds(Vector3.zero, Vector3.one * 1e4f)
                }, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers);

                Mesh.ApplyAndDisposeWritableMeshData(writable, m_DynamicMesh,
                    MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers);
                // writable is consumed by Apply; don't dispose again
                writable = default;
            }
            finally
            {
                srcArray.Dispose();
                if (writable.Length > 0) writable.Dispose();
            }

            m_DynamicMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e4f);
            canvasRenderer.SetMesh(m_DynamicMesh);
        }

        public void UpdateMeshGeometry(
            NativeArray<float3> masterPositions,
            NativeArray<float3> masterNormals,
            NativeArray<float4> masterTangents,
            bool skinNormals)
        {
            if (!m_IsInitialized || m_DynamicMesh == null) return;

            // Direct NativeArray upload — Unity walks the source's vertex layout to write only the
            // Position/Normal/Tangent attributes (handles interleave). No managed marshalling, no GC.
            UnityEngine.Profiling.Profiler.BeginSample("UI3D.SetBuffers");
            m_DynamicMesh.SetVertices(masterPositions, m_VertexStart, m_VertexCount);
            // Normals/tangents only when the lit path skinned them; otherwise the mesh keeps its
            // rest-pose normals/tangents (the unlit shader ignores them).
            if (skinNormals)
            {
                m_DynamicMesh.SetNormals(masterNormals, m_VertexStart, m_VertexCount);
                m_DynamicMesh.SetTangents(masterTangents, m_VertexStart, m_VertexCount);
            }
            UnityEngine.Profiling.Profiler.EndSample();

            UnityEngine.Profiling.Profiler.BeginSample("UI3D.CanvasRenderer.SetMesh");
            canvasRenderer.SetMesh(m_DynamicMesh);
            UnityEngine.Profiling.Profiler.EndSample();
        }

#if UNITY_EDITOR
        public void SetBakedMesh(SkinnedMeshRenderer smr)
        {
            if (m_EditModeBakedMesh == null)
            {
                m_EditModeBakedMesh = new Mesh();
                m_EditModeBakedMesh.name = "UI Baked Preview - " + name;
                m_EditModeBakedMesh.hideFlags = HideFlags.DontSave;
            }
            
            if (smr != null)
            {
                smr.BakeMesh(m_EditModeBakedMesh);
                canvasRenderer.SetMesh(m_EditModeBakedMesh);
            }
        }

        public void SetStaticMesh(Mesh mesh)
        {
            if (mesh != null)
            {
                canvasRenderer.SetMesh(mesh);
            }
        }
#endif

        // TEST-ONLY: world-space AABB of the current skinned content (vertices referenced by this
        // part's triangles). The dynamic mesh's own bounds are a 1e4 placeholder (see BuildDynamicMesh),
        // so we derive bounds from the live vertices. Allocates — intended for the stress-test verifier.
        public bool TryGetContentWorldBounds(out Bounds worldBounds)
        {
            worldBounds = default;
            if (m_DynamicMesh == null) return false;

            var verts = m_DynamicMesh.vertices;
            var tris = m_DynamicMesh.triangles;
            if (verts == null || verts.Length == 0 || tris == null || tris.Length == 0) return false;

            var l2w = transform.localToWorldMatrix;
            bool has = false;
            Vector3 min = Vector3.zero, max = Vector3.zero;
            for (int i = 0; i < tris.Length; i++)
            {
                int vi = tris[i];
                if (vi < 0 || vi >= verts.Length) continue;
                Vector3 w = l2w.MultiplyPoint3x4(verts[vi]);
                if (!has) { min = max = w; has = true; }
                else { min = Vector3.Min(min, w); max = Vector3.Max(max, w); }
            }
            if (!has) return false;

            worldBounds = new Bounds((min + max) * 0.5f, max - min);
            return true;
        }

        private static Mesh.MeshDataArray AcquireMeshDataWithoutCheck(Mesh mesh)
        {
            var ctor = typeof(Mesh.MeshDataArray).GetConstructor(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null,
                new System.Type[] { typeof(Mesh), typeof(bool), typeof(bool) },
                null
            );
            if (ctor != null)
            {
                return (Mesh.MeshDataArray)ctor.Invoke(new object[] { mesh, false, false });
            }
            return Mesh.AcquireReadOnlyMeshData(mesh);
        }
    }
}
