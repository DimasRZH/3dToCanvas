using System;
using System.Collections.Generic;
using UnityEngine;

namespace Optimize.GPUSkinning
{
    /// <summary>
    /// One body part inside a <see cref="GPUSkinningCharacterData"/>.
    /// Stores the baked static mesh (with UV1/2/3 bone data), the bone hierarchy
    /// for that part, and the texture/colour to render with.
    /// </summary>
    [Serializable]
    public class GPUSkinningPartEntry
    {
        public string partName;

        /// <summary>
        /// Baked static mesh. Bone indices + weights are packed into UV1/UV2/UV3
        /// using the same 3-bone layout as GPUSkinningData:
        ///   uv1 = (boneIndex0, boneIndex1)
        ///   uv2 = (boneIndex2, weight0)
        ///   uv3 = (weight1,    weight2)
        /// </summary>
        public Mesh mesh;

        /// <summary>Bone paths + bind poses + local bounding spheres, relative to the character root.</summary>
        public List<GPUSkinningBoneData> bones;

        /// <summary>Main texture copied from the original material at bake-time.</summary>
        public Texture mainTexture;

        /// <summary>Tint colour copied from the original material at bake-time (default white).</summary>
        public Color color = Color.white;

        public bool Valid() => mesh != null && bones != null;
    }

    /// <summary>
    /// Single ScriptableObject that represents an entire GPU-skinned character —
    /// all body parts, meshes, bone hierarchies and textures baked into one asset.
    ///
    /// Bake via: Assets > Create > Optimize > GPU Skinning > Bake Character (All Parts).
    /// Assign to <see cref="UI3D.UIGPUSkinningCharacter"/> or use directly with
    /// <see cref="GPUSkinningBehaviour"/> in world-space scenes.
    /// </summary>
    [CreateAssetMenu(
        fileName = "Character_GPUSkinning",
        menuName  = "Optimize/GPU Skinning/Character Data")]
    public class GPUSkinningCharacterData : ScriptableObject
    {
        public List<GPUSkinningPartEntry> m_Parts;

        public bool Valid() => m_Parts != null && m_Parts.Count > 0;
    }
}
