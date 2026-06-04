using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Unity.Mathematics;

namespace UI3D
{
    public static class UIBurstCharacterEditor
    {
        [MenuItem("Assets/Create/Optimize/UI Burst/Bake Character Data", false, 1)]
        private static void BakeCharacterMenu()
        {
            var obj = Selection.activeObject;
            GameObject prefab = obj as GameObject;
            if (prefab == null && AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(obj)) as ModelImporter != null)
            {
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(obj));
            }

            if (prefab == null)
            {
                Debug.LogError("[UIBurstCharacterEditor] Please select an FBX model or prefab.");
                return;
            }

            var instantiated = GameObject.Instantiate(prefab);
            var skinnedRenderers = instantiated.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var meshFilters = instantiated.GetComponentsInChildren<MeshFilter>(true);

            if (skinnedRenderers.Length == 0 && meshFilters.Length == 0)
            {
                Debug.LogError("[UIBurstCharacterEditor] No meshes found to bake.");
                Object.DestroyImmediate(instantiated);
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(obj);
            string folder = Path.GetDirectoryName(assetPath);
            string savePath = EditorUtility.SaveFilePanelInProject(
                "Save UI Burst Character Data",
                $"{prefab.name}_BurstData",
                "asset",
                "Select save location",
                folder
            );

            if (string.IsNullOrEmpty(savePath))
            {
                Object.DestroyImmediate(instantiated);
                return;
            }

            var data = ScriptableObject.CreateInstance<UIBurstCharacterData>();
            var root = instantiated.transform;

            // Calculate model bounds
            Matrix4x4 rootW2L = root.worldToLocalMatrix;
            bool hasBounds = false;
            Vector3 min = Vector3.zero;
            Vector3 max = Vector3.zero;

            // Skinned meshes bounds
            foreach (var smr in skinnedRenderers)
            {
                if (smr.sharedMesh == null) continue;
                Matrix4x4 m = rootW2L * smr.transform.localToWorldMatrix;
                Bounds mb = smr.sharedMesh.bounds;
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

            // Static meshes bounds
            foreach (var filter in meshFilters)
            {
                if (filter.sharedMesh == null) continue;
                if (filter.GetComponent<SkinnedMeshRenderer>() != null) continue;
                Matrix4x4 m = rootW2L * filter.transform.localToWorldMatrix;
                Bounds mb = filter.sharedMesh.bounds;
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
                data.modelBoundsCenter = (min + max) * 0.5f;
                data.modelBoundsSize = new Vector3(
                    Mathf.Max(size.x, 1e-4f),
                    Mathf.Max(size.y, 1e-4f),
                    Mathf.Max(size.z, 1e-4f));
            }

            // Save parent first so we can attach sub-meshes to it
            AssetDatabase.CreateAsset(data, savePath);

            // Process SkinnedMeshRenderers
            foreach (var smr in skinnedRenderers)
            {
                if (smr.sharedMesh == null) continue;

                // Clone the mesh so it is saved independently and remains readable
                Mesh bakedMesh = Object.Instantiate(smr.sharedMesh);
                bakedMesh.name = $"{smr.sharedMesh.name}_Baked";
                AssetDatabase.AddObjectToAsset(bakedMesh, data);

                int subMeshCount = smr.sharedMesh.subMeshCount;

                for (int s = 0; s < subMeshCount; s++)
                {
                    Texture mainTex = null;
                    Color col = Color.white;
                    Material[] originalMaterials = smr.sharedMaterials;
                    if (s < originalMaterials.Length && originalMaterials[s] != null)
                    {
                        var origMat = originalMaterials[s];
                        if (origMat.HasProperty("_MainTex")) mainTex = origMat.GetTexture("_MainTex");
                        else if (origMat.HasProperty("_BaseMap")) mainTex = origMat.GetTexture("_BaseMap");
                        
                        if (origMat.HasProperty("_Color")) col = origMat.GetColor("_Color");
                        else if (origMat.HasProperty("_BaseColor")) col = origMat.GetColor("_BaseColor");
                    }

                    var part = new UIBurstPartData
                    {
                        partName = smr.name + (subMeshCount > 1 ? $"_Sub{s}" : ""),
                        subMeshIndex = s,
                        mesh = bakedMesh,
                        mainTexture = mainTex,
                        color = col,
                        isStatic = smr.bones == null || smr.bones.Length == 0,
                        bones = new List<UIBurstPartBoneData>()
                    };

                    // Map bone relative paths
                    if (!part.isStatic)
                    {
                        for (int b = 0; b < smr.bones.Length; b++)
                        {
                            part.bones.Add(new UIBurstPartBoneData
                            {
                                relativePath = GetRelativePath(smr.bones[b], root),
                                bindPose = smr.sharedMesh.bindposes[b]
                            });
                        }
                    }
                    else
                    {
                        part.bones.Add(new UIBurstPartBoneData
                        {
                            relativePath = GetRelativePath(smr.transform, root),
                            bindPose = Matrix4x4.identity
                        });
                    }

                    data.parts.Add(part);
                }
            }

            // Process MeshFilters (Static parts)
            foreach (var filter in meshFilters)
            {
                var meshRenderer = filter.GetComponent<MeshRenderer>();
                if (meshRenderer == null || filter.sharedMesh == null) continue;
                if (filter.GetComponent<SkinnedMeshRenderer>() != null) continue; // already handled

                // Clone the mesh so it is saved independently and remains readable
                Mesh bakedMesh = Object.Instantiate(filter.sharedMesh);
                bakedMesh.name = $"{filter.sharedMesh.name}_Baked";
                AssetDatabase.AddObjectToAsset(bakedMesh, data);

                int subMeshCount = filter.sharedMesh.subMeshCount;

                for (int s = 0; s < subMeshCount; s++)
                {
                    Texture mainTex = null;
                    Color col = Color.white;
                    Material[] originalMaterials = meshRenderer.sharedMaterials;
                    if (s < originalMaterials.Length && originalMaterials[s] != null)
                    {
                        var origMat = originalMaterials[s];
                        if (origMat.HasProperty("_MainTex")) mainTex = origMat.GetTexture("_MainTex");
                        else if (origMat.HasProperty("_BaseMap")) mainTex = origMat.GetTexture("_BaseMap");
                        
                        if (origMat.HasProperty("_Color")) col = origMat.GetColor("_Color");
                        else if (origMat.HasProperty("_BaseColor")) col = origMat.GetColor("_BaseColor");
                    }

                    var part = new UIBurstPartData
                    {
                        partName = filter.name + (subMeshCount > 1 ? $"_Sub{s}" : ""),
                        subMeshIndex = s,
                        mesh = bakedMesh,
                        mainTexture = mainTex,
                        color = col,
                        isStatic = true,
                        bones = new List<UIBurstPartBoneData>()
                    };

                    part.bones.Add(new UIBurstPartBoneData
                    {
                        relativePath = GetRelativePath(filter.transform, root),
                        bindPose = Matrix4x4.identity
                    });

                    data.parts.Add(part);
                }
            }

            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Object.DestroyImmediate(instantiated);
            Debug.Log($"[UIBurstCharacterEditor] Baked successfully to: {savePath}");
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
