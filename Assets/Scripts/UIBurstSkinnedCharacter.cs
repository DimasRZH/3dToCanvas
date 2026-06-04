using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace UI3D
{
    public enum UIBurstScaleMode
    {
        None,
        MatchWidth,
        MatchHeight,
        Fit,
        Fill,
        Stretch
    }

    [ExecuteAlways] // also build/skin a live preview in Edit Mode (editor-only paths are #if UNITY_EDITOR)
    [RequireComponent(typeof(Animator))]
    [RequireComponent(typeof(RectTransform))] // Forces root transform to be a RectTransform
    [AddComponentMenu("UI/3D/UI Burst Skinned Character")]
    public class UIBurstSkinnedCharacter : MonoBehaviour
    {
        [Header("Character Data")]
        [Tooltip("Optional: Baked character data containing readable cloned meshes to avoid Read/Write enabled requirement in builds.")]
        [SerializeField] private UIBurstCharacterData m_CharacterData;

        [Header("UI Scaling")]
        [Tooltip("Scale mode to adjust the 3D model relative to the RectTransform size.")]
        [SerializeField] private UIBurstScaleMode m_ScaleMode = UIBurstScaleMode.Fit;

        [Tooltip("Auto-fit the model's bounding box into the RectTransform (behaves like a UI graphic). Disable to use the manual Reference Rect Size instead.")]
        [SerializeField] private bool m_UseModelBounds = true;

        [Tooltip("Manual override: the design-time RectTransform size that corresponds to a scale factor of 1.0. Used only when Use Model Bounds is off.")]
        [SerializeField] private Vector2 m_ReferenceRectSize = new Vector2(200f, 200f);

        [Header("Material Settings")]
        [Tooltip("Shader to use for dynamic UI materials. Defaults to 'UI/3DModel'.")]
        [SerializeField] private Shader m_UiShader;

        [Tooltip("If set, this material overrides the default. A unique instance is created per part.")]
        [SerializeField] private Material m_CustomUiMaterial;

        [Header("Rendering")]
        [Tooltip("Skin normals & tangents each frame. Leave off for unlit materials (default) to save " +
                 "skinning math and upload bandwidth; enable only when a lit material needs them.")]
        [SerializeField] private bool m_SkinNormals = false;

        [Header("Depth Clearing")]
        [Tooltip("Wipes the Z-buffer after drawing the character so UI panels drawn in front don't intersect with the 3D model.")]
        [SerializeField] private bool m_ClearDepthAfterDraw = true;

        [Tooltip("Shader used to clear depth. Defaults to 'UI/DepthClear'. Assign to guarantee it survives the build.")]
        [SerializeField] private Shader m_DepthClearShader;

#if UNITY_EDITOR
        [Header("Editor Preview (editor-only, stripped from builds)")]
        [Tooltip("Pose the rig with a clip in Edit Mode instead of showing the default rest pose.")]
        [SerializeField] private bool m_PreviewAnimation = false;

        [Tooltip("Clip sampled for the edit-mode preview pose.")]
        [SerializeField] private AnimationClip m_PreviewClip;

        [Tooltip("Normalized time (0..1) along the preview clip.")]
        [SerializeField, Range(0f, 1f)] private float m_PreviewTime = 0f;

        private bool m_PreviewDirty;
        private bool m_EditorHooksInstalled;
        private List<SubMeshSource> m_EditModeSources;
#endif

        private List<UIBurstSkinnedRenderer> m_UiRenderers = new List<UIBurstSkinnedRenderer>();
        private GameObject m_DepthWiperGo;

        // Originals we hid (via non-serialized forceRenderingOff) so we can restore them on teardown.
        private readonly List<Renderer> m_DisabledOriginals = new List<Renderer>();
        // Per-part materials we created, so we can destroy them (avoids editor material leaks).
        private readonly List<Material> m_GeneratedMaterials = new List<Material>();

        // Rest-pose model bounds in root-local space, used to fit/center the model in the rect.
        private float3 m_ModelBoundsCenter;
        private float3 m_ModelBoundsSize = new float3(1f, 1f, 1f);
        private Vector2 m_LastSyncedPivot = new Vector2(float.NaN, float.NaN);
        private bool m_Initialized;

        // Master bone/matrix arrays
        private NativeArray<float4x4> m_MasterBindPoses;
        private NativeArray<float4x4> m_MasterBoneMatrices;
        private NativeArray<float4x4> m_MasterCombinedMatrices;
        private NativeArray<float3x3> m_MasterNormalMatrices;
        private NativeArray<int> m_MasterBoneToSubMeshIndex;
        private NativeArray<UIBurstSubMeshInfo> m_MasterSubMeshInfos;

        // Flat bone list in master order. In play mode these are read on worker threads by the central
        // UIBurstSkinManager's transform job; in edit mode they're read on the main thread (synchronous).
        private Transform[] m_AllBones;

        // Master CPU Skinning input arrays
        private NativeArray<float3> m_MasterBasePositions;
        private NativeArray<float3> m_MasterBaseNormals;
        private NativeArray<float4> m_MasterBaseTangents;
        private NativeArray<int4> m_MasterBoneIndices;
        private NativeArray<float4> m_MasterBoneWeights;

        // Master CPU Skinning output arrays
        private NativeArray<float3> m_MasterOutputPositions;
        private NativeArray<float3> m_MasterOutputNormals;
        private NativeArray<float4> m_MasterOutputTangents;

        private struct SubMeshSource
        {
            public string partName;
            public SkinnedMeshRenderer skinnedRenderer;
            public MeshFilter meshFilter;
            public MeshRenderer meshRenderer;
            public int subMeshIndex;
            public Mesh mesh;
            public Transform[] bones;
            public Matrix4x4[] bindPoses;
            public bool isStatic;
            public Texture mainTexture;
            public Color color;
        }

        private void Awake()
        {
            if (!Application.isPlaying) return; // edit-mode build is driven from OnEnable
#if UNITY_EDITOR
            // Clear any edit-mode preview that may persist when entering Play with domain reload disabled.
            TeardownPreview();
#endif
            InitializeCharacter();
        }

        private void OnEnable()
        {
            if (Application.isPlaying)
            {
                // Awake already built the parts; register with the central skin manager so it drives
                // bone reads + skinning for all characters in one batched pass.
                if (m_Initialized) UIBurstSkinManager.Instance.Register(this);
                return;
            }
#if UNITY_EDITOR
            InstallEditorHooks();
            RebuildPreview();
#endif
        }

        private void OnDisable()
        {
            if (Application.isPlaying)
            {
                UIBurstSkinManager.UnregisterIfExists(this);
                return;
            }
#if UNITY_EDITOR
            UninstallEditorHooks();
            TeardownPreview();
#endif
        }

#if UNITY_EDITOR
        private void OnValidate() => m_PreviewDirty = true;

        // Fires on resize / anchor / pivot edits (and parent rect changes) — refresh the preview.
        private void OnRectTransformDimensionsChange()
        {
            if (!Application.isPlaying) m_PreviewDirty = true;
        }

        private void InstallEditorHooks()
        {
            if (m_EditorHooksInstalled) return;
            m_EditorHooksInstalled = true;
            UnityEditor.EditorApplication.update += EditorUpdate;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        private void UninstallEditorHooks()
        {
            if (!m_EditorHooksInstalled) return;
            m_EditorHooksInstalled = false;
            UnityEditor.EditorApplication.update -= EditorUpdate;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
        }

        private void EditorUpdate()
        {
            if (this == null || Application.isPlaying || !isActiveAndEnabled) return;

            bool needsUpdate = m_PreviewDirty || transform.hasChanged;
            if (!needsUpdate && m_AllBones != null)
            {
                for (int i = 0; i < m_AllBones.Length; i++)
                {
                    if (m_AllBones[i] != null && m_AllBones[i].hasChanged)
                    {
                        needsUpdate = true;
                        break;
                    }
                }
            }

            if (needsUpdate)
            {
                m_PreviewDirty = false;
                transform.hasChanged = false;
                if (m_AllBones != null)
                {
                    for (int i = 0; i < m_AllBones.Length; i++)
                    {
                        if (m_AllBones[i] != null) m_AllBones[i].hasChanged = false;
                    }
                }
                SkinOnce();
                UnityEditor.SceneView.RepaintAll();
            }
        }

        private void OnBeforeAssemblyReload() => TeardownPreview();

        // Full edit-mode (re)build: tear down anything stale, rebuild parts, do one synchronous skin.
        public void RebuildPreview()
        {
            TeardownPreview();
            InitializeCharacter();
            SkinOnce();
        }

        // One synchronous skin (schedule + Complete immediately). Edit mode has no reliable per-frame
        // tick, so we never use the pipelined path here.
        private void SkinOnce()
        {
            if (m_UiRenderers.Count == 0 || !m_MasterBoneMatrices.IsCreated || m_AllBones == null) return;
            if (m_PreviewAnimation && m_PreviewClip != null)
                m_PreviewClip.SampleAnimation(gameObject, m_PreviewTime * m_PreviewClip.length);
            ComputeAndSkinSync();
        }

        private void TeardownPreview()
        {
            CleanupMasterBuffers();
            CleanupGenerated();
            m_Initialized = false;
        }
#endif

        // Configure the serialized settings before the object is activated (Awake/InitializeCharacter
        // run on activation). Used by the stress-test harness to spawn instances at runtime.
        public void Configure(UIBurstScaleMode scaleMode, bool useModelBounds, Vector2 referenceRectSize,
                              Shader uiShader, Material customUiMaterial, bool skinNormals = false,
                              Shader depthClearShader = null)
        {
            m_ScaleMode = scaleMode;
            m_UseModelBounds = useModelBounds;
            m_ReferenceRectSize = referenceRectSize;
            m_UiShader = uiShader;
            m_CustomUiMaterial = customUiMaterial;
            m_SkinNormals = skinNormals;
            if (depthClearShader != null) m_DepthClearShader = depthClearShader;
        }

        public UIBurstScaleMode ScaleMode => m_ScaleMode;

        /// <summary>
        /// Builds the UI render parts from the model's skinned/static meshes. Called automatically in
        /// Awake; safe to call again (idempotent — a second call is ignored). Requires the GameObject
        /// to be under a Canvas whose render mode provides a depth buffer (Screen Space - Camera or
        /// World Space) for correct 3D depth sorting.
        /// </summary>
        public void InitializeCharacter()
        {
            if (m_Initialized) return; // idempotent: avoid duplicating UI parts on repeated calls
            m_Initialized = true;

            // Auto-configure Canvas shader channels to avoid stripping vertex attributes (UV3, UV4, Normals) if needed.
            var canvas = GetComponentInParent<Canvas>();
            if (canvas == null)
            {
                Debug.LogWarning($"[UIBurstSkinnedCharacter] '{name}' is not under a Canvas; it will not render.", this);
            }
            else
            {
                canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1
                                                | AdditionalCanvasShaderChannels.TexCoord2
                                                | AdditionalCanvasShaderChannels.TexCoord3
                                                | AdditionalCanvasShaderChannels.Normal;

                // The 3D shader relies on ZWrite/ZTest for intra-model depth sorting, which needs a
                // depth buffer. Screen Space - Overlay has none, so parts/triangles will mis-sort
                // (e.g. hair drawing over the face). Use Screen Space - Camera or World Space.
                if (canvas.rootCanvas != null && canvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    Debug.LogWarning($"[UIBurstSkinnedCharacter] '{name}' is under a Screen Space - Overlay canvas, " +
                        "which has no depth buffer. The 3D model may sort incorrectly (back faces/parts over front). " +
                        "Use Screen Space - Camera or World Space for correct depth sorting.", this);
                }
            }

            // Force the Animator to always update bone transforms since we disable original 3D renderers.
            // Play-only: cullingMode is serialized, and a static edit preview doesn't need it.
            if (Application.isPlaying)
            {
                var animator = GetComponent<Animator>();
                if (animator != null)
                {
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                }
            }

            List<SubMeshSource> sources = new List<SubMeshSource>();

            if (m_CharacterData != null && m_CharacterData.Valid())
            {
                foreach (var part in m_CharacterData.parts)
                {
                    if (part.mesh == null) continue;

                    Transform[] bones = new Transform[part.bones.Count];
                    Matrix4x4[] bindposes = new Matrix4x4[part.bones.Count];
                    for (int i = 0; i < part.bones.Count; i++)
                    {
                        var boneData = part.bones[i];
                        Transform boneTransform = transform.Find(boneData.relativePath);
                        if (boneTransform == null)
                        {
                            boneTransform = FindChildRecursive(transform, LastSegment(boneData.relativePath));
                        }
                        if (boneTransform == null)
                        {
                            Debug.LogError($"[UIBurstSkinnedCharacter] '{name}': bone not found - '{boneData.relativePath}'", this);
                            boneTransform = transform;
                        }
                        bones[i] = boneTransform;
                        bindposes[i] = boneData.bindPose;
                    }

                    sources.Add(new SubMeshSource
                    {
                        partName = part.partName,
                        skinnedRenderer = null,
                        meshFilter = null,
                        meshRenderer = null,
                        subMeshIndex = part.subMeshIndex,
                        mesh = part.mesh,
                        bones = bones,
                        bindPoses = bindposes,
                        isStatic = part.isStatic,
                        mainTexture = part.mainTexture,
                        color = part.color
                    });
                }

                // Hide originals if they exist
                var skinnedRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
                foreach (var smr in skinnedRenderers) HideOriginal(smr);

                var meshFilters = GetComponentsInChildren<MeshFilter>(true);
                foreach (var filter in meshFilters)
                {
                    if (filter.GetComponent<SkinnedMeshRenderer>() != null) continue;
                    var meshRenderer = filter.GetComponent<MeshRenderer>();
                    HideOriginal(meshRenderer);
                }
            }
            else
            {
                var skinnedRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
                var meshFilters = GetComponentsInChildren<MeshFilter>(true);

                foreach (var smr in skinnedRenderers)
                {
                    if (smr.sharedMesh == null) continue;
                    int subMeshCount = smr.sharedMesh.subMeshCount;

                    Transform[] bones;
                    Matrix4x4[] bindposes;
                    bool isStatic;

                    if (smr.bones != null && smr.bones.Length > 0)
                    {
                        bones = smr.bones;
                        bindposes = smr.sharedMesh.bindposes;
                        isStatic = false;
                    }
                    else
                    {
                        bones = new Transform[] { smr.transform };
                        bindposes = new Matrix4x4[] { Matrix4x4.identity };
                        isStatic = true;
                    }

                    for (int i = 0; i < subMeshCount; i++)
                    {
                        Texture mainTexture = null;
                        Color color = Color.white;
                        if (smr.sharedMaterials != null && i < smr.sharedMaterials.Length && smr.sharedMaterials[i] != null)
                        {
                            var origMat = smr.sharedMaterials[i];
                            if (origMat.HasProperty("_MainTex")) mainTexture = origMat.GetTexture("_MainTex");
                            else if (origMat.HasProperty("_BaseMap")) mainTexture = origMat.GetTexture("_BaseMap");

                            if (origMat.HasProperty("_Color")) color = origMat.GetColor("_Color");
                            else if (origMat.HasProperty("_BaseColor")) color = origMat.GetColor("_BaseColor");
                        }

                        sources.Add(new SubMeshSource
                        {
                            partName = smr.name + (subMeshCount > 1 ? $"_Sub{i}" : ""),
                            skinnedRenderer = smr,
                            meshFilter = null,
                            meshRenderer = null,
                            subMeshIndex = i,
                            mesh = smr.sharedMesh,
                            bones = bones,
                            bindPoses = bindposes,
                            isStatic = isStatic,
                            mainTexture = mainTexture,
                            color = color
                        });
                    }

                    HideOriginal(smr);
                }

                foreach (var filter in meshFilters)
                {
                    var meshRenderer = filter.GetComponent<MeshRenderer>();
                    if (meshRenderer == null || filter.sharedMesh == null) continue;
                    if (filter.GetComponent<SkinnedMeshRenderer>() != null) continue;

                    int subMeshCount = filter.sharedMesh.subMeshCount;
                    var bones = new Transform[] { filter.transform };
                    var bindposes = new Matrix4x4[] { Matrix4x4.identity };

                    for (int i = 0; i < subMeshCount; i++)
                    {
                        Texture mainTexture = null;
                        Color color = Color.white;
                        if (meshRenderer.sharedMaterials != null && i < meshRenderer.sharedMaterials.Length && meshRenderer.sharedMaterials[i] != null)
                        {
                            var origMat = meshRenderer.sharedMaterials[i];
                            if (origMat.HasProperty("_MainTex")) mainTexture = origMat.GetTexture("_MainTex");
                            else if (origMat.HasProperty("_BaseMap")) mainTexture = origMat.GetTexture("_BaseMap");

                            if (origMat.HasProperty("_Color")) color = origMat.GetColor("_Color");
                            else if (origMat.HasProperty("_BaseColor")) color = origMat.GetColor("_BaseColor");
                        }

                        sources.Add(new SubMeshSource
                        {
                            partName = filter.name + (subMeshCount > 1 ? $"_Sub{i}" : ""),
                            skinnedRenderer = null,
                            meshFilter = filter,
                            meshRenderer = meshRenderer,
                            subMeshIndex = i,
                            mesh = filter.sharedMesh,
                            bones = bones,
                            bindPoses = bindposes,
                            isStatic = true,
                            mainTexture = mainTexture,
                            color = color
                        });
                    }

                    HideOriginal(meshRenderer);
                }
            }

            if (sources.Count == 0) return;

            if (m_CharacterData != null && m_CharacterData.Valid())
            {
                m_ModelBoundsCenter = m_CharacterData.modelBoundsCenter;
                m_ModelBoundsSize = m_CharacterData.modelBoundsSize;
            }
            else
            {
                ComputeModelBounds(sources);
            }

            int totalBones = 0;
            int totalBindPoses = 0;
            int totalVertices = 0;

            for (int i = 0; i < sources.Count; i++)
            {
                totalBones += sources[i].bones.Length;
                totalBindPoses += sources[i].bindPoses.Length;
                totalVertices += sources[i].mesh.vertexCount;
            }

            // Bone matrices allocations
            m_MasterBindPoses = new NativeArray<float4x4>(totalBindPoses, Allocator.Persistent);
            m_MasterBoneMatrices = new NativeArray<float4x4>(totalBones, Allocator.Persistent);
            m_MasterCombinedMatrices = new NativeArray<float4x4>(totalBones, Allocator.Persistent);
            m_MasterNormalMatrices = new NativeArray<float3x3>(totalBones, Allocator.Persistent);
            m_MasterBoneToSubMeshIndex = new NativeArray<int>(totalBones, Allocator.Persistent);
            m_MasterSubMeshInfos = new NativeArray<UIBurstSubMeshInfo>(sources.Count, Allocator.Persistent);

            // CPU skinning allocations
            m_MasterBasePositions = new NativeArray<float3>(totalVertices, Allocator.Persistent);
            m_MasterBoneIndices = new NativeArray<int4>(totalVertices, Allocator.Persistent);
            m_MasterBoneWeights = new NativeArray<float4>(totalVertices, Allocator.Persistent);
            m_MasterOutputPositions = new NativeArray<float3>(totalVertices, Allocator.Persistent);

            // Normal/tangent buffers only exist on the lit path; the unlit shader ignores them.
            if (m_SkinNormals)
            {
                m_MasterBaseNormals = new NativeArray<float3>(totalVertices, Allocator.Persistent);
                m_MasterBaseTangents = new NativeArray<float4>(totalVertices, Allocator.Persistent);
                m_MasterOutputNormals = new NativeArray<float3>(totalVertices, Allocator.Persistent);
                m_MasterOutputTangents = new NativeArray<float4>(totalVertices, Allocator.Persistent);
            }

            // Populate all bones for TransformAccessArray
            Transform[] allBones = new Transform[totalBones];
            int currentBoneIdx = 0;

            int currentBoneOffset = 0;
            int currentBindPoseOffset = 0;
            int currentVertexOffset = 0;

            // Resolve the UI shader. Prefer the serialized reference: it guarantees the shader is
            // included in the build. Shader.Find only works if the shader is referenced elsewhere or
            // listed under Project Settings > Graphics > Always Included Shaders — otherwise it is
            // stripped and returns null in a player build.
            Shader shaderToUse = m_UiShader;
            if (shaderToUse == null)
            {
                shaderToUse = Shader.Find("UI/3DModel");
            }
            if (shaderToUse == null && m_CustomUiMaterial == null)
            {
                Debug.LogError($"[UIBurstSkinnedCharacter] '{name}': shader 'UI/3DModel' not found and no " +
                    "custom material set. Assign the shader to the 'UI Shader' field or add it to " +
                    "Always Included Shaders so it survives the build.", this);
            }

            Vector2 rootPivot = ((RectTransform)transform).pivot;

            for (int j = 0; j < sources.Count; j++)
            {
                var src = sources[j];
                int bCount = src.bones.Length;
                int bpCount = src.bindPoses.Length;
                var mesh = src.mesh;
                int vc = mesh.vertexCount;

                string partName = src.partName;
                string suffix = src.mesh.subMeshCount > 1 ? $"_Sub{src.subMeshIndex}" : "";

                // Collect bones for multi-threaded Transform access
                for (int i = 0; i < bCount; i++)
                {
                    allBones[currentBoneIdx++] = src.bones[i];
                }

                // Copy bind poses
                for (int i = 0; i < bpCount; i++)
                {
                    m_MasterBindPoses[currentBindPoseOffset + i] = src.bindPoses[i];
                }

                // Initialize bone matrices and mapping
                for (int i = 0; i < bCount; i++)
                {
                    m_MasterBoneMatrices[currentBoneOffset + i] = float4x4.identity;
                    m_MasterBoneToSubMeshIndex[currentBoneOffset + i] = j;
                }

                m_MasterSubMeshInfos[j] = new UIBurstSubMeshInfo
                {
                    boneStart = currentBoneOffset,
                    bindPoseStart = currentBindPoseOffset
                };

                // Copy vertex attributes and setup bone data via MeshData — no Read/Write required
                // on the source mesh. AcquireMeshDataWithoutCheck skips R/W check in editor.
                var meshDataArray = AcquireMeshDataWithoutCheck(mesh);
                try
                {
                    var data = meshDataArray[0];

                    var verts = new NativeArray<Vector3>(vc, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                    data.GetVertices(verts);

                    bool hasNormals = m_SkinNormals && data.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Normal);
                    bool hasTangents = m_SkinNormals && data.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Tangent);
                    NativeArray<Vector3> normals = default;
                    NativeArray<Vector4> tangents = default;
                    if (hasNormals)
                    {
                        normals = new NativeArray<Vector3>(vc, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                        data.GetNormals(normals);
                    }
                    if (hasTangents)
                    {
                        tangents = new NativeArray<Vector4>(vc, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                        data.GetTangents(tangents);
                    }

                    // BoneWeight1 / bonesPerVertex come from Mesh (not MeshData) and work on
                    // non-readable meshes. The returned NativeArrays are views — do NOT dispose them.
                    bool hasWeights = !src.isStatic
                        && data.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.BlendWeight)
                        && data.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.BlendIndices);
                    NativeArray<byte> bonesPerVertex = default;
                    NativeArray<BoneWeight1> allWeights = default;
                    if (hasWeights)
                    {
                        bonesPerVertex = mesh.GetBonesPerVertex();
                        allWeights = mesh.GetAllBoneWeights();
                        if (bonesPerVertex.Length != vc) hasWeights = false;
                    }

                    int weightCursor = 0;
                    for (int i = 0; i < vc; i++)
                    {
                        int globalIdx = currentVertexOffset + i;
                        m_MasterBasePositions[globalIdx] = verts[i];
                        if (m_SkinNormals)
                        {
                            m_MasterBaseNormals[globalIdx] = hasNormals ? new float3(normals[i].x, normals[i].y, normals[i].z) : new float3(0f, 0f, 1f);
                            m_MasterBaseTangents[globalIdx] = hasTangents ? new float4(tangents[i].x, tangents[i].y, tangents[i].z, tangents[i].w) : new float4(1f, 0f, 0f, 1f);
                        }

                        if (hasWeights)
                        {
                            int count = bonesPerVertex[i];
                            int b0 = 0, b1 = 0, b2 = 0, b3 = 0;
                            float w0 = 0f, w1 = 0f, w2 = 0f, w3 = 0f;
                            if (count > 0) { var w = allWeights[weightCursor + 0]; b0 = w.boneIndex; w0 = w.weight; }
                            if (count > 1) { var w = allWeights[weightCursor + 1]; b1 = w.boneIndex; w1 = w.weight; }
                            if (count > 2) { var w = allWeights[weightCursor + 2]; b2 = w.boneIndex; w2 = w.weight; }
                            if (count > 3) { var w = allWeights[weightCursor + 3]; b3 = w.boneIndex; w3 = w.weight; }
                            weightCursor += count;

                            m_MasterBoneIndices[globalIdx] = new int4(
                                currentBoneOffset + b0,
                                currentBoneOffset + b1,
                                currentBoneOffset + b2,
                                currentBoneOffset + b3
                            );

                            float sum = w0 + w1 + w2 + w3;
                            if (sum > 1e-5f)
                            {
                                float r = 1f / sum;
                                m_MasterBoneWeights[globalIdx] = new float4(w0 * r, w1 * r, w2 * r, w3 * r);
                            }
                            else
                            {
                                m_MasterBoneWeights[globalIdx] = new float4(1f, 0f, 0f, 0f);
                            }
                        }
                        else
                        {
                            m_MasterBoneIndices[globalIdx] = new int4(currentBoneOffset, currentBoneOffset, currentBoneOffset, currentBoneOffset);
                            m_MasterBoneWeights[globalIdx] = new float4(1f, 0f, 0f, 0f);
                        }
                    }

                    verts.Dispose();
                    if (normals.IsCreated) normals.Dispose();
                    if (tangents.IsCreated) tangents.Dispose();
                }
                finally
                {
                    meshDataArray.Dispose();
                }

                // Instantiate UI GameObject. DontSave keeps these generated parts out of the saved
                // scene/prefab (critical for edit-mode preview); hidden + not-editable in the editor.
                GameObject uiPartGo = new GameObject($"[UI] {partName}{suffix}");
                uiPartGo.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;
#if UNITY_EDITOR
                if (!Application.isPlaying) uiPartGo.hideFlags |= HideFlags.HideInHierarchy;
#endif
                uiPartGo.transform.SetParent(transform, false);

                // Overlay each part on the root origin so its local space matches the root's.
                var rectTransform = uiPartGo.AddComponent<RectTransform>();
                rectTransform.anchorMin = rootPivot;
                rectTransform.anchorMax = rootPivot;
                rectTransform.pivot = rootPivot;
                rectTransform.sizeDelta = Vector2.zero;
                rectTransform.anchoredPosition = Vector2.zero;

                var uiRenderer = uiPartGo.AddComponent<UIBurstSkinnedRenderer>();

                // Configure Material
                if (m_CustomUiMaterial != null)
                {
                    var customInstance = new Material(m_CustomUiMaterial) { hideFlags = HideFlags.DontSave };
                    customInstance.name = $"{partName}_UIMat{suffix}";
                    m_GeneratedMaterials.Add(customInstance);
                    uiRenderer.material = customInstance;
                }
                else if (shaderToUse != null)
                {
                    Material uiMat = new Material(shaderToUse) { hideFlags = HideFlags.DontSave };
                    uiMat.name = $"{partName}_UIMat{suffix}";
                    m_GeneratedMaterials.Add(uiMat);

                    if (src.mainTexture != null)
                    {
                        uiMat.SetTexture("_MainTex", src.mainTexture);
                    }
                    uiMat.SetColor("_Color", src.color);
                    uiRenderer.material = uiMat;
                }

                uiRenderer.InitializeFromMaster(
                    this,
                    src.skinnedRenderer,
                    src.meshFilter,
                    src.meshRenderer,
                    src.mesh,
                    src.subMeshIndex,
                    src.isStatic,
                    currentBoneOffset,
                    bCount,
                    src.bones,
                    currentVertexOffset,
                    vc,
                    src.mainTexture
                );

                m_UiRenderers.Add(uiRenderer);

                currentBoneOffset += bCount;
                currentBindPoseOffset += bpCount;
                currentVertexOffset += vc;
            }

            m_AllBones = allBones;

            // Generate Depth Wiper quad to clear Z-buffer and prevent intersection with standard UI panels
            if (m_ClearDepthAfterDraw)
            {
                m_DepthWiperGo = new GameObject("[UI] Depth Wiper", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.RawImage));
                m_DepthWiperGo.hideFlags = HideFlags.DontSave;
                var wiperRt = m_DepthWiperGo.GetComponent<RectTransform>();
                wiperRt.SetParent(transform, false);
                
                // Stretch to cover the entire RectTransform
                wiperRt.anchorMin = Vector2.zero;
                wiperRt.anchorMax = Vector2.one;
                wiperRt.offsetMin = Vector2.zero;
                wiperRt.offsetMax = Vector2.zero;
                
                var rawImage = m_DepthWiperGo.GetComponent<UnityEngine.UI.RawImage>();
                rawImage.raycastTarget = false;
                
                Shader clearShader = m_DepthClearShader != null ? m_DepthClearShader : Shader.Find("UI/DepthClear");
                if (clearShader != null)
                {
                    rawImage.material = new Material(clearShader);
                }
                else
                {
                    Debug.LogWarning("UI/DepthClear shader not found. Depth wiping will not work.");
                }
            }
        }

        // Hide an original 3D renderer without serializing the change (forceRenderingOff is runtime-only),
        // and remember it so we can restore it on teardown.
        private void HideOriginal(Renderer r)
        {
            if (r == null) return;
            r.forceRenderingOff = true;
            m_DisabledOriginals.Add(r);
        }

        private static void DestroyObj(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }

        // Destroy the generated parts/materials and restore the original renderers. Leaves the master
        // NativeArrays alone (disposed separately in CleanupMasterBuffers).
        private void CleanupGenerated()
        {
            for (int i = 0; i < m_DisabledOriginals.Count; i++)
                if (m_DisabledOriginals[i] != null) m_DisabledOriginals[i].forceRenderingOff = false;
            m_DisabledOriginals.Clear();

            for (int i = 0; i < m_UiRenderers.Count; i++)
                if (m_UiRenderers[i] != null) DestroyObj(m_UiRenderers[i].gameObject);
            m_UiRenderers.Clear();

            if (m_DepthWiperGo != null)
            {
                DestroyObj(m_DepthWiperGo);
                m_DepthWiperGo = null;
            }

            for (int i = 0; i < m_GeneratedMaterials.Count; i++)
                DestroyObj(m_GeneratedMaterials[i]);
            m_GeneratedMaterials.Clear();

            m_LastSyncedPivot = new Vector2(float.NaN, float.NaN);
        }

        // Combined rest-pose AABB of all source meshes, expressed in the root's local space.
        private void ComputeModelBounds(List<SubMeshSource> sources)
        {
            Matrix4x4 rootW2L = transform.worldToLocalMatrix;
            bool hasBounds = false;
            Vector3 min = Vector3.zero;
            Vector3 max = Vector3.zero;

            for (int s = 0; s < sources.Count; s++)
            {
                var src = sources[s];
                Transform t = src.skinnedRenderer != null ? src.skinnedRenderer.transform
                                                          : src.meshFilter.transform;
                Matrix4x4 m = rootW2L * t.localToWorldMatrix;
                Bounds mb = src.mesh.bounds;
                Vector3 c = mb.center;
                Vector3 e = mb.extents;

                for (int cx = -1; cx <= 1; cx += 2)
                for (int cy = -1; cy <= 1; cy += 2)
                for (int cz = -1; cz <= 1; cz += 2)
                {
                    Vector3 corner = m.MultiplyPoint3x4(c + new Vector3(cx * e.x, cy * e.y, cz * e.z));
                    if (!hasBounds) { min = max = corner; hasBounds = true; }
                    else { min = Vector3.Min(min, corner); max = Vector3.Max(max, corner); }
                }
            }

            if (hasBounds)
            {
                Vector3 size = max - min;
                m_ModelBoundsCenter = (min + max) * 0.5f;
                m_ModelBoundsSize = new float3(
                    Mathf.Max(size.x, 1e-4f),
                    Mathf.Max(size.y, 1e-4f),
                    Mathf.Max(size.z, 1e-4f));
            }
        }

        // Keeps child parts overlaid on the root origin so their local space stays in sync with the root pivot.
        private void SyncChildPivots(Vector2 pivot)
        {
            if (pivot == m_LastSyncedPivot) return;
            m_LastSyncedPivot = pivot;

            for (int i = 0; i < m_UiRenderers.Count; i++)
            {
                var r = m_UiRenderers[i];
                if (r == null) continue;

                var rt = r.rectTransform;
                rt.anchorMin = pivot;
                rt.anchorMax = pivot;
                rt.pivot = pivot;
                rt.sizeDelta = Vector2.zero;
                rt.anchoredPosition = Vector2.zero;
            }
        }

        /// <summary>
        /// Dynamically pops a specific 3D part (like a Head or Weapon) in front of the UI.
        /// Used by UIBurstSortOverrideBehaviour via Animator State Machine.
        /// </summary>
        public void SetPartSortOrder(string partName, int sortOrder)
        {
            if (string.IsNullOrEmpty(partName)) return;
            
            for (int i = 0; i < m_UiRenderers.Count; i++)
            {
                var renderer = m_UiRenderers[i];
                if (renderer != null && (renderer.name == $"[UI] {partName}" || renderer.name.StartsWith($"[UI] {partName}_Sub")))
                {
                    var canvas = renderer.gameObject.GetComponent<Canvas>();
                    if (canvas == null)
                    {
                        // Visual sort override only — no GraphicRaycaster needed (would add input overhead).
                        canvas = renderer.gameObject.AddComponent<Canvas>();
                    }
                    canvas.overrideSorting = true;
                    canvas.sortingOrder = sortOrder;
                }
            }
        }

        /// <summary>
        /// Reverts a part back to standard sorting, placing it back within the UI hierarchy naturally.
        /// </summary>
        public void ResetPartSortOrder(string partName)
        {
            if (string.IsNullOrEmpty(partName)) return;

            for (int i = 0; i < m_UiRenderers.Count; i++)
            {
                var renderer = m_UiRenderers[i];
                if (renderer != null && (renderer.name == $"[UI] {partName}" || renderer.name.StartsWith($"[UI] {partName}_Sub")))
                {
                    var canvas = renderer.gameObject.GetComponent<Canvas>();
                    if (canvas != null)
                    {
                        canvas.overrideSorting = false;
                    }
                }
            }
        }

        // True once the buffers are built and there is something to skin (used by the manager).
        public bool ReadyForSkin =>
            m_AllBones != null && m_UiRenderers.Count > 0 && m_MasterCombinedMatrices.IsCreated;

        // Flat bone list in master order; the manager concatenates these into its global TransformAccessArray.
        public Transform[] AllBones => m_AllBones;
        public int BoneCount => m_AllBones != null ? m_AllBones.Length : 0;
        // Substitute for a null/missing bone: the root transform yields rootW2L*world == identity (matches
        // the old main-thread path's identity fallback) and keeps the TransformAccessArray dense.
        public Transform BoneOrRoot(int i) => m_AllBones[i] != null ? m_AllBones[i] : transform;

        private void UploadAll()
        {
            int rendererCount = m_UiRenderers.Count;
            for (int i = 0; i < rendererCount; i++)
            {
                var r = m_UiRenderers[i];
                if (r == null || !r.isActiveAndEnabled) continue;
                r.UpdateMeshGeometry(m_MasterOutputPositions, m_MasterOutputNormals, m_MasterOutputTangents, m_SkinNormals);
            }
        }

        // Manager calls this next frame after the batched skinning handle has completed.
        public void UploadSkinnedResults() => UploadAll();

        // Play-mode entry point (called by UIBurstSkinManager): main-thread prep, then schedule
        // combine+skin reading this character's bone world matrices from a slice of the manager's
        // global world-matrix array (filled by the shared transform job, on which `dep` depends).
        public JobHandle ScheduleSkinning(NativeArray<float4x4> globalWorldMatrices, int boneGlobalStart, JobHandle dep)
        {
            float4x4 rootWorldToLocal = PrepareSubMeshInfos();
            var worldSlice = globalWorldMatrices.GetSubArray(boneGlobalStart, m_AllBones.Length);
            return ScheduleCombineSkin(worldSlice, rootWorldToLocal, dep);
        }

#if UNITY_EDITOR
        // Edit-mode synchronous skin (no manager / no per-frame tick): fill bone world matrices on the
        // main thread, schedule the chain, complete immediately, and upload.
        private void ComputeAndSkinSync()
        {
            if (m_UiRenderers.Count == 0 || !m_MasterBoneMatrices.IsCreated || m_AllBones == null) return;

            float4x4 rootWorldToLocal = PrepareSubMeshInfos();
            for (int b = 0; b < m_AllBones.Length; b++)
                m_MasterBoneMatrices[b] = (float4x4)BoneOrRoot(b).localToWorldMatrix;

            var handle = ScheduleCombineSkin(m_MasterBoneMatrices, rootWorldToLocal, default);
            handle.Complete();
            UploadAll();
        }
#endif

        // Main-thread per-frame prep: pivot sync, scale-mode fit, localToUi matrix, and content-rect
        // culling footprint written into m_MasterSubMeshInfos. Returns this character's root worldToLocal.
        private float4x4 PrepareSubMeshInfos()
        {
            int rendererCount = m_UiRenderers.Count;
            var rootWorldToLocal = (float4x4)transform.worldToLocalMatrix;

            var rootRectTransform = (RectTransform)transform;
            var rectSize = rootRectTransform.rect.size;
            var pivot = rootRectTransform.pivot;

            // Keep child parts overlaid on the root origin if the pivot changed.
            UnityEngine.Profiling.Profiler.BeginSample("UI3D.SyncChildPivots");
            SyncChildPivots(pivot);
            UnityEngine.Profiling.Profiler.EndSample();

            // Reference footprint: model bounds (auto-fit) or the manual reference rect size.
            float2 refSize = m_UseModelBounds
                ? new float2(m_ModelBoundsSize.x, m_ModelBoundsSize.y)
                : new float2(m_ReferenceRectSize.x, m_ReferenceRectSize.y);
            refSize = math.max(refSize, new float2(1e-4f, 1e-4f));

            // Compute scaling based on current RectTransform size compared to the reference footprint.
            float scaleX = 1f;
            float scaleY = 1f;
            float scaleZ = 1f;

            if (m_ScaleMode != UIBurstScaleMode.None && rectSize.x > 1e-4f && rectSize.y > 1e-4f)
            {
                float rx = rectSize.x / refSize.x;
                float ry = rectSize.y / refSize.y;

                switch (m_ScaleMode)
                {
                    case UIBurstScaleMode.MatchWidth:
                        scaleX = scaleY = scaleZ = rx;
                        break;
                    case UIBurstScaleMode.MatchHeight:
                        scaleX = scaleY = scaleZ = ry;
                        break;
                    case UIBurstScaleMode.Fit:
                        scaleX = scaleY = scaleZ = math.min(rx, ry);
                        break;
                    case UIBurstScaleMode.Fill:
                        scaleX = scaleY = scaleZ = math.max(rx, ry);
                        break;
                    case UIBurstScaleMode.Stretch:
                        scaleX = rx;
                        scaleY = ry;
                        scaleZ = math.min(rx, ry);
                        break;
                }
            }

            // Center the model's bounds at the rect center (pivot-aware), then scale.
            // rectCenter encodes the pivot, so moving the pivot moves the content like any UI graphic.
            float3 rectCenter = new float3((0.5f - pivot.x) * rectSize.x, (0.5f - pivot.y) * rectSize.y, 0f);
            float4x4 localToUi = math.mul(
                float4x4.Translate(rectCenter),
                math.mul(
                    float4x4.Scale(new float3(scaleX, scaleY, scaleZ)),
                    float4x4.Translate(-m_ModelBoundsCenter)));

            // Character footprint in local (canvas) space, for correct RectMask2D/Mask culling.
            // Model bounds projected through the current scale, centered at rectCenter, padded a little
            // because skinned poses can exceed the rest-pose bounds.
            const float kCullPad = 1.2f;
            float halfW = 0.5f * scaleX * m_ModelBoundsSize.x * kCullPad;
            float halfH = 0.5f * scaleY * m_ModelBoundsSize.y * kCullPad;
            var contentLocalRect = new Rect(rectCenter.x - halfW, rectCenter.y - halfH, halfW * 2f, halfH * 2f);

            for (int i = 0; i < rendererCount; i++)
            {
                var info = m_MasterSubMeshInfos[i];
                info.localToUiMatrix = localToUi;
                m_MasterSubMeshInfos[i] = info;

                var r = m_UiRenderers[i];
                if (r != null) r.SetContentLocalRect(contentLocalRect);
            }

            return rootWorldToLocal;
        }

        // Schedule combine (folds rootW2L*world*bindPose*localToUi per bone) then the CPU skinning job.
        // worldMatrices holds this character's bone localToWorld matrices (length == m_AllBones.Length).
        private JobHandle ScheduleCombineSkin(NativeArray<float4x4> worldMatrices, float4x4 rootWorldToLocal, JobHandle dep)
        {
            var combineJob = new UIBurstCombineMatricesJob
            {
                worldMatrices = worldMatrices,
                rootWorldToLocal = rootWorldToLocal,
                bindPoses = m_MasterBindPoses,
                subMeshInfos = m_MasterSubMeshInfos,
                boneToSubMeshIndex = m_MasterBoneToSubMeshIndex,
                combinedMatrices = m_MasterCombinedMatrices,
                normalMatrices = m_MasterNormalMatrices,
                computeNormalMatrices = m_SkinNormals
            };
            var combineHandle = combineJob.Schedule(worldMatrices.Length, 16, dep);

            if (m_SkinNormals)
            {
                var skinningJob = new UIBurstSkinningJob
                {
                    basePositions = m_MasterBasePositions,
                    baseNormals = m_MasterBaseNormals,
                    baseTangents = m_MasterBaseTangents,
                    boneIndices = m_MasterBoneIndices,
                    boneWeights = m_MasterBoneWeights,
                    combinedMatrices = m_MasterCombinedMatrices,
                    normalMatrices = m_MasterNormalMatrices,
                    outputPositions = m_MasterOutputPositions,
                    outputNormals = m_MasterOutputNormals,
                    outputTangents = m_MasterOutputTangents
                };
                return skinningJob.Schedule(m_MasterBasePositions.Length, 64, combineHandle);
            }
            else
            {
                var skinningJob = new UIBurstSkinningJobPositionOnly
                {
                    basePositions = m_MasterBasePositions,
                    boneIndices = m_MasterBoneIndices,
                    boneWeights = m_MasterBoneWeights,
                    combinedMatrices = m_MasterCombinedMatrices,
                    outputPositions = m_MasterOutputPositions
                };
                return skinningJob.Schedule(m_MasterBasePositions.Length, 64, combineHandle);
            }
        }

        private void OnDestroy()
        {
            CleanupMasterBuffers();
            CleanupGenerated();
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

        private void CleanupMasterBuffers()
        {
            // In play mode the manager completes its batched handle before unregistering this character
            // (see UIBurstSkinManager.Unregister); edit mode skins synchronously. So no in-flight job
            // reads these arrays here.
            m_AllBones = null;

            if (m_MasterBindPoses.IsCreated) m_MasterBindPoses.Dispose();
            if (m_MasterBoneMatrices.IsCreated) m_MasterBoneMatrices.Dispose();
            if (m_MasterCombinedMatrices.IsCreated) m_MasterCombinedMatrices.Dispose();
            if (m_MasterNormalMatrices.IsCreated) m_MasterNormalMatrices.Dispose();
            if (m_MasterBoneToSubMeshIndex.IsCreated) m_MasterBoneToSubMeshIndex.Dispose();
            if (m_MasterSubMeshInfos.IsCreated) m_MasterSubMeshInfos.Dispose();

            if (m_MasterBasePositions.IsCreated) m_MasterBasePositions.Dispose();
            if (m_MasterBaseNormals.IsCreated) m_MasterBaseNormals.Dispose();
            if (m_MasterBaseTangents.IsCreated) m_MasterBaseTangents.Dispose();
            if (m_MasterBoneIndices.IsCreated) m_MasterBoneIndices.Dispose();
            if (m_MasterBoneWeights.IsCreated) m_MasterBoneWeights.Dispose();

            if (m_MasterOutputPositions.IsCreated) m_MasterOutputPositions.Dispose();
            if (m_MasterOutputNormals.IsCreated) m_MasterOutputNormals.Dispose();
            if (m_MasterOutputTangents.IsCreated) m_MasterOutputTangents.Dispose();
        }
    }
}
