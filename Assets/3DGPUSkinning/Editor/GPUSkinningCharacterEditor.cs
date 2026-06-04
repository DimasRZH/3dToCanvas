using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Optimize.GPUSkinning
{
    /// <summary>
    /// Editor baking tool that processes an entire FBX model and outputs a single
    /// <see cref="GPUSkinningCharacterData"/> asset containing every body part.
    ///
    /// Usage: select an FBX in the Project window, then
    ///   Assets > Create > Optimize > GPU Skinning > Bake Character (All Parts)
    /// </summary>
    public static class GPUSkinningCharacterEditor
    {
        [MenuItem("Assets/Create/Optimize/GPU Skinning/Bake Character (All Parts)", false, 2)]
        private static void BakeCharacterMenu()
        {
            var obj = Selection.activeObject;
            if (obj == null)
            {
                Debug.LogError("[GPUSkinningCharacterEditor] Nothing selected. Select an FBX model.");
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(obj);
            if (AssetImporter.GetAtPath(assetPath) as ModelImporter == null)
            {
                Debug.LogError("[GPUSkinningCharacterEditor] Selected asset is not an FBX model.");
                return;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                Debug.LogError("[GPUSkinningCharacterEditor] Could not load prefab from selected asset.");
                return;
            }

            string folder     = Path.GetDirectoryName(assetPath);
            string defaultName = Path.GetFileNameWithoutExtension(assetPath) + "_GPUSkinning";
            string savePath   = EditorUtility.SaveFilePanelInProject(
                "Save GPU Skinning Character Data",
                defaultName,
                "asset",
                "Choose where to save the character data asset.",
                folder);

            if (string.IsNullOrEmpty(savePath)) return;

            // ── Instantiate to get runtime bone transforms ───────────────────
            var instance = Object.Instantiate(prefab);
            instance.hideFlags = HideFlags.HideAndDontSave;

            var smrs = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (smrs.Length == 0)
            {
                Object.DestroyImmediate(instance);
                Debug.LogError("[GPUSkinningCharacterEditor] No SkinnedMeshRenderers found in selected FBX.");
                return;
            }

            // ── Create the character asset ────────────────────────────────────
            var characterData = ScriptableObject.CreateInstance<GPUSkinningCharacterData>();
            characterData.m_Parts = new List<GPUSkinningPartEntry>(smrs.Length);

            var rootTransform = instance.transform;

            foreach (var smr in smrs)
            {
                if (smr.sharedMesh == null) continue;

                // ── Bake mesh with UV1/2/3 bone data ─────────────────────────
                var bakedMesh = BakeMeshWithBoneUVs(smr, rootTransform,
                    out List<Vector3> boundsCenters,
                    out List<float>   boundsRadii);
                bakedMesh.name = smr.sharedMesh.name + "_Baked";

                // ── Build bone list ───────────────────────────────────────────
                var boneList   = BuildBoneList(smr, rootTransform, boundsCenters, boundsRadii);
                var origMat    = smr.sharedMaterial;
                var tex        = ExtractTexture(origMat);
                var col        = ExtractColor(origMat);

                var entry = new GPUSkinningPartEntry
                {
                    partName    = smr.name,
                    mesh        = bakedMesh,
                    bones       = boneList,
                    mainTexture = tex,
                    color       = col
                };

                characterData.m_Parts.Add(entry);
            }

            Object.DestroyImmediate(instance);

            // ── Save asset + embed all meshes as sub-assets ───────────────────
            AssetDatabase.CreateAsset(characterData, savePath);
            foreach (var part in characterData.m_Parts)
            {
                if (part.mesh != null)
                    AssetDatabase.AddObjectToAsset(part.mesh, characterData);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[GPUSkinningCharacterEditor] Saved {characterData.m_Parts.Count} parts → {savePath}", characterData);
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = characterData;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Mesh baking
        // ─────────────────────────────────────────────────────────────────────

        private static Mesh BakeMeshWithBoneUVs(
            SkinnedMeshRenderer smr,
            Transform           root,
            out List<Vector3>   boundsCenters,
            out List<float>     boundsRadii)
        {
            var source     = smr.sharedMesh;
            var targetMesh = Object.Instantiate(source);
            targetMesh.hideFlags = HideFlags.None;

            int   vc          = source.vertexCount;
            var   boneWeights = source.boneWeights;
            bool  hasWeights  = boneWeights != null && boneWeights.Length == vc;
            int   boneCount   = smr.bones.Length;

            // Per-bone vertex lists for bounding-sphere calculation
            var bonePoints = new List<Vector3>[Mathf.Max(1, boneCount)];
            for (int i = 0; i < bonePoints.Length; i++)
                bonePoints[i] = new List<Vector3>();

            var uv1 = new List<Vector2>(vc); // (boneIndex0, boneIndex1)
            var uv2 = new List<Vector2>(vc); // (boneIndex2, weight0)
            var uv3 = new List<Vector2>(vc); // (weight1, weight2)

            var verticesOS = source.vertices;

            for (int i = 0; i < vc; i++)
            {
                if (hasWeights)
                {
                    var w   = boneWeights[i];
                    float s = w.weight0 + w.weight1 + w.weight2;
                    float r = s > 1e-5f ? 1f / s : 1f;

                    float w0 = w.weight0 * r;
                    float w1 = w.weight1 * r;
                    float w2 = w.weight2 * r;

                    uv1.Add(new Vector2(w.boneIndex0, w.boneIndex1));
                    uv2.Add(new Vector2(w.boneIndex2, w0));
                    uv3.Add(new Vector2(w1, w2));

                    Vector3 p = verticesOS[i];
                    if (w0 > 0.05f) AddToBonePoints(smr, w.boneIndex0, p, bonePoints);
                    if (w1 > 0.05f) AddToBonePoints(smr, w.boneIndex1, p, bonePoints);
                    if (w2 > 0.05f) AddToBonePoints(smr, w.boneIndex2, p, bonePoints);
                }
                else
                {
                    // 0-bone (static) — bind to identity bone 0, full weight
                    uv1.Add(Vector2.zero);
                    uv2.Add(new Vector2(0f, 1f));
                    uv3.Add(Vector2.zero);
                }
            }

            // ── Bounding spheres in bone-local space ──────────────────────────
            boundsCenters = new List<Vector3>(boneCount);
            boundsRadii   = new List<float>(boneCount);

            for (int i = 0; i < boneCount; i++)
            {
                var pts = bonePoints[i];
                if (pts.Count > 0)
                {
                    Vector3 sum = Vector3.zero;
                    foreach (var p in pts) sum += p;
                    Vector3 center = sum / pts.Count;

                    float maxDist = 0f;
                    foreach (var p in pts)
                        maxDist = Mathf.Max(maxDist, Vector3.Distance(center, p));

                    // Convert centre to bone-local space
                    Vector3 centerLS = smr.bones[i].worldToLocalMatrix.MultiplyPoint(center);
                    boundsCenters.Add(centerLS);
                    boundsRadii.Add(maxDist);
                }
                else
                {
                    boundsCenters.Add(Vector3.zero);
                    boundsRadii.Add(0.1f);
                }
            }

            targetMesh.SetUVs(1, uv1);
            targetMesh.SetUVs(2, uv2);
            targetMesh.SetUVs(3, uv3);
            targetMesh.boneWeights = null;
            targetMesh.bindposes   = null;

            return targetMesh;
        }

        private static void AddToBonePoints(
            SkinnedMeshRenderer smr,
            int                 boneIndex,
            Vector3             vertexOS,
            List<Vector3>[]     bonePoints)
        {
            if (boneIndex < 0 || boneIndex >= smr.bones.Length) return;
            var bone        = smr.bones[boneIndex];
            var restWorld   = bone.localToWorldMatrix * smr.sharedMesh.bindposes[boneIndex];
            bonePoints[boneIndex].Add(restWorld.MultiplyPoint3x4(vertexOS));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Bone list
        // ─────────────────────────────────────────────────────────────────────

        private static List<GPUSkinningBoneData> BuildBoneList(
            SkinnedMeshRenderer smr,
            Transform           root,
            List<Vector3>       centers,
            List<float>         radii)
        {
            int count = smr.bones.Length;
            var list  = new List<GPUSkinningBoneData>(count);

            for (int i = 0; i < count; i++)
            {
                list.Add(new GPUSkinningBoneData
                {
                    relativePath  = GetRelativePath(smr.bones[i], root),
                    bindPose      = smr.sharedMesh.bindposes[i],
                    boundsCenter  = centers[i],
                    boundsRadius  = radii[i]
                });
            }

            return list;
        }

        private static string GetRelativePath(Transform child, Transform root)
        {
            if (child == root) return "";
            string path   = child.name;
            var    parent = child.parent;
            while (parent != null && parent != root)
            {
                path   = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Material helpers
        // ─────────────────────────────────────────────────────────────────────

        private static Texture ExtractTexture(Material mat)
        {
            if (mat == null) return null;
            if (mat.HasProperty("_MainTex") && mat.GetTexture("_MainTex") != null)
                return mat.GetTexture("_MainTex");
            if (mat.HasProperty("_BaseMap") && mat.GetTexture("_BaseMap") != null)
                return mat.GetTexture("_BaseMap");
            return null;
        }

        private static Color ExtractColor(Material mat)
        {
            if (mat == null) return Color.white;
            if (mat.HasProperty("_Color"))    return mat.GetColor("_Color");
            if (mat.HasProperty("_BaseColor")) return mat.GetColor("_BaseColor");
            return Color.white;
        }
    }
}
