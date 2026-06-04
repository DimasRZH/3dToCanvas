using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace UI3D
{
    [Serializable]
    public struct UIBurstPartBoneData
    {
        public string relativePath;
        public float4x4 bindPose;
    }

    [Serializable]
    public class UIBurstPartData
    {
        public string partName;
        public int subMeshIndex;
        public Mesh mesh; // The cloned readable mesh asset containing rest-pose vertices, UVs, bone weights
        public Texture mainTexture;
        public Color color = Color.white;
        public List<UIBurstPartBoneData> bones;
        public bool isStatic;
    }

    // No [CreateAssetMenu] — empty assets are useless; the baker at
    // Assets/Editor/UIBurstCharacterEditor.cs owns the "Optimize/UI Burst/Bake Character Data"
    // menu path and creates a fully-populated asset from the selected FBX/prefab.
    public class UIBurstCharacterData : ScriptableObject
    {
        public Vector3 modelBoundsCenter;
        public Vector3 modelBoundsSize = Vector3.one;
        public List<UIBurstPartData> parts = new List<UIBurstPartData>();
        
        public bool Valid() => parts != null && parts.Count > 0;
    }
}
