using System;
using System.Collections.Generic;
using UnityEngine;

namespace Optimize.GPUSkinning
{
    [Serializable]
    public struct GPUSkinningBoneData
    {
        public Matrix4x4 bindPose;
        public string relativePath;
        public Vector3 boundsCenter;
        public float boundsRadius;
    }
    
    [CreateAssetMenu(fileName = "GPUSkinningData", menuName = "Optimize/GPU Skinning/Data")]
    public class GPUSkinningData : ScriptableObject
    {
        public Mesh m_Mesh;
        public List<GPUSkinningBoneData> m_Bones;

        public bool Valid() => m_Mesh != null && m_Bones != null;
    }
}
