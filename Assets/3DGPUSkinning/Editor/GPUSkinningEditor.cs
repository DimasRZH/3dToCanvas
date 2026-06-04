using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Optimize.GPUSkinning
{
    public static class GPUSkinningEditor
    {
        [MenuItem("Assets/Create/Optimize/GPU Skinning/Bake Skinned Mesh", false, 1)]
        private static void BakeSkinnedMeshMenu()
        {
            var obj = Selection.activeObject;
            GameObject prefab = obj as GameObject;
            if (prefab == null && AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(obj)) as ModelImporter != null)
            {
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(obj));
            }

            if (prefab == null)
            {
                Debug.LogError("[GPUSkinningEditor] Please select an FBX model or prefab with a SkinnedMeshRenderer.");
                return;
            }

            var instantiated = GameObject.Instantiate(prefab);
            var smr = instantiated.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr == null)
            {
                Debug.LogError("[GPUSkinningEditor] No SkinnedMeshRenderer found in the selected object.");
                Object.DestroyImmediate(instantiated);
                return;
            }

            if (smr.sharedMesh == null)
            {
                Debug.LogError("[GPUSkinningEditor] SkinnedMeshRenderer has no mesh.");
                Object.DestroyImmediate(instantiated);
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(obj);
            string folder = Path.GetDirectoryName(assetPath);
            string savePath = EditorUtility.SaveFilePanelInProject(
                "Save GPU Skinning Data",
                $"{smr.name}_GPUSkinning",
                "asset",
                "Select save location",
                folder
            );

            if (string.IsNullOrEmpty(savePath))
            {
                Object.DestroyImmediate(instantiated);
                return;
            }

            // Create GPUSkinningData asset
            var data = ScriptableObject.CreateInstance<GPUSkinningData>();
            
            // Process mesh and compute bounds
            data.m_Mesh = Generate3BoneMesh(smr, out var boundsCenterList, out var boundsRadiusList);
            data.m_Mesh.name = $"{smr.sharedMesh.name}_Baked";
            
            // Build bone structures
            var root = instantiated.transform;
            int boneCount = smr.bones.Length;
            data.m_Bones = new List<GPUSkinningBoneData>(boneCount);

            for (int i = 0; i < boneCount; i++)
            {
                var bone = smr.bones[i];
                string relPath = GetRelativePath(bone, root);
                data.m_Bones.Add(new GPUSkinningBoneData
                {
                    relativePath = relPath,
                    bindPose = smr.sharedMesh.bindposes[i],
                    boundsCenter = boundsCenterList[i],
                    boundsRadius = boundsRadiusList[i]
                });
            }

            // Save combination
            AssetDatabase.CreateAsset(data, savePath);
            AssetDatabase.AddObjectToAsset(data.m_Mesh, data);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Object.DestroyImmediate(instantiated);
            Debug.Log($"[GPUSkinningEditor] Successfully baked GPU Skinning Data to: {savePath}", data);
        }

        private static Mesh Generate3BoneMesh(SkinnedMeshRenderer smr, out List<Vector3> boundsCenterList, out List<float> boundsRadiusList)
        {
            Mesh sourceMesh = smr.sharedMesh;
            Mesh targetMesh = Object.Instantiate(sourceMesh);

            int vc = sourceMesh.vertexCount;
            var boneWeights = sourceMesh.boneWeights;
            bool hasWeights = boneWeights != null && boneWeights.Length == vc;

            var boneIndices1 = new List<Vector2>(vc); // UV1: id0, id1
            var boneIndices2 = new List<Vector2>(vc); // UV2: id2, w0
            var boneWeightsList = new List<Vector2>(vc); // UV3: w1, w2

            int boneCount = smr.bones.Length;
            boundsCenterList = new List<Vector3>(boneCount);
            boundsRadiusList = new List<float>(boneCount);

            // Temporary list of points in world space per bone to compute bounding spheres
            var bonePoints = new List<Vector3>[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                bonePoints[i] = new List<Vector3>();
            }

            var verticesOS = sourceMesh.vertices;

            for (int i = 0; i < vc; i++)
            {
                if (hasWeights)
                {
                    var w = boneWeights[i];
                    float sum = w.weight0 + w.weight1 + w.weight2;
                    float r = sum > 1e-5f ? 1f / sum : 1f;

                    float w0 = w.weight0 * r;
                    float w1 = w.weight1 * r;
                    float w2 = w.weight2 * r;

                    boneIndices1.Add(new Vector2(w.boneIndex0, w.boneIndex1));
                    boneIndices2.Add(new Vector2(w.boneIndex2, w0));
                    boneWeightsList.Add(new Vector2(w1, w2));

                    // Collect vertex position in rest-pose world space relative to the bones for bounds calculation
                    Vector3 posOS = verticesOS[i];
                    if (w0 > 0.05f) AddVertexToBonePoints(smr, i, w.boneIndex0, posOS, bonePoints);
                    if (w1 > 0.05f) AddVertexToBonePoints(smr, i, w.boneIndex1, posOS, bonePoints);
                    if (w2 > 0.05f) AddVertexToBonePoints(smr, i, w.boneIndex2, posOS, bonePoints);
                }
                else
                {
                    boneIndices1.Add(Vector2.zero);
                    boneIndices2.Add(new Vector2(0f, 1f));
                    boneWeightsList.Add(Vector2.zero);
                }
            }

            // Calculate bounding spheres in bone local space
            for (int i = 0; i < boneCount; i++)
            {
                var points = bonePoints[i];
                if (points.Count > 0)
                {
                    // Compute bounding sphere (center is average, radius is max distance)
                    Vector3 sumVec = Vector3.zero;
                    foreach (var p in points) sumVec += p;
                    Vector3 centerWS = sumVec / points.Count;

                    float maxDist = 0f;
                    foreach (var p in points)
                    {
                        maxDist = Mathf.Max(maxDist, Vector3.Distance(centerWS, p));
                    }

                    // Convert center to bone local space
                    Vector3 centerLS = smr.bones[i].worldToLocalMatrix.MultiplyPoint(centerWS);
                    
                    boundsCenterList.Add(centerLS);
                    boundsRadiusList.Add(maxDist);
                }
                else
                {
                    boundsCenterList.Add(Vector3.zero);
                    boundsRadiusList.Add(0.1f); // default fallback
                }
            }

            targetMesh.SetUVs(1, boneIndices1);
            targetMesh.SetUVs(2, boneIndices2);
            targetMesh.SetUVs(3, boneWeightsList);

            // Clear original skinning info to prevent Unity from trying to do CPU skinning
            targetMesh.boneWeights = null;
            targetMesh.bindposes = null;

            return targetMesh;
        }

        private static void AddVertexToBonePoints(SkinnedMeshRenderer smr, int vertIndex, int boneIndex, Vector3 vertexOS, List<Vector3>[] bonePoints)
        {
            if (boneIndex < 0 || boneIndex >= smr.bones.Length) return;
            var bone = smr.bones[boneIndex];
            
            // Rest-pose local-to-world of the bone: bone.localToWorldMatrix * bindPose
            Matrix4x4 restWorldMatrix = bone.localToWorldMatrix * smr.sharedMesh.bindposes[boneIndex];
            Vector3 restWorldPos = restWorldMatrix.MultiplyPoint3x4(vertexOS);
            
            bonePoints[boneIndex].Add(restWorldPos);
        }

        private static string GetRelativePath(Transform child, Transform root)
        {
            if (child == root) return "";
            string path = child.name;
            Transform parent = child.parent;
            while (parent != null && parent != root)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }
    }
}
