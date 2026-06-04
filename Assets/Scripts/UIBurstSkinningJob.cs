using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Jobs;

namespace UI3D
{
    public struct UIBurstSubMeshInfo
    {
        public int boneStart;       // Offset into the master bone/combined matrices array
        public int bindPoseStart;   // Offset into the master bind poses array
        public float4x4 localToUiMatrix;
    }

    // Reads each bone's world matrix (localToWorldMatrix) on worker threads via the transform job
    // system, so the main thread no longer pays the per-bone transform-hierarchy flush. One instance
    // is scheduled globally (UIBurstSkinManager) over all bones of all characters, paying the
    // transform-system sync fence once per frame instead of once per character.
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public struct UIBurstReadBoneWorldMatricesJob : IJobParallelForTransform
    {
        [WriteOnly] public NativeArray<float4x4> worldMatrices;

        public void Execute(int index, TransformAccess transform)
        {
            worldMatrices[index] = transform.localToWorldMatrix;
        }
    }

    // Per-bone pre-pass: collapse localToUi * (rootWorldToLocal * boneWorld) * bindPose into one
    // matrix per bone. boneWorld comes from UIBurstReadBoneWorldMatricesJob (or a main-thread fill in
    // edit mode). When computeNormalMatrices is set, also store the inverse-transpose of the combined
    // 3x3 so the skinning job can transform normals/tangents correctly under non-uniform (Stretch) scale.
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public struct UIBurstCombineMatricesJob : IJobParallelFor
    {
        // Bone localToWorld matrices (a per-character slice of the global world-matrix array).
        [ReadOnly] public NativeArray<float4x4> worldMatrices;
        // This character's root worldToLocal — folds the bones from world space into root-local space.
        public float4x4 rootWorldToLocal;
        [ReadOnly] public NativeArray<float4x4> bindPoses;
        [ReadOnly] public NativeArray<UIBurstSubMeshInfo> subMeshInfos;
        [ReadOnly] public NativeArray<int> boneToSubMeshIndex;

        [WriteOnly] public NativeArray<float4x4> combinedMatrices;

        // Always allocated (per-bone, tiny), but only filled when skinning normals/tangents (lit path).
        [WriteOnly] public NativeArray<float3x3> normalMatrices;
        public bool computeNormalMatrices;

        public void Execute(int boneIndex)
        {
            int subMeshIdx = boneToSubMeshIndex[boneIndex];
            UIBurstSubMeshInfo info = subMeshInfos[subMeshIdx];

            // bones and bind poses are parallel per submesh, offset by boneStart/bindPoseStart
            int bp = info.bindPoseStart + (boneIndex - info.boneStart);

            float4x4 boneMatrix = math.mul(rootWorldToLocal, worldMatrices[boneIndex]);
            float4x4 boneBind = math.mul(boneMatrix, bindPoses[bp]);
            float4x4 combined = math.mul(info.localToUiMatrix, boneBind);
            combinedMatrices[boneIndex] = combined;

            if (computeNormalMatrices)
            {
                float3x3 m3 = new float3x3(combined.c0.xyz, combined.c1.xyz, combined.c2.xyz);
                // Inverse-transpose handles non-uniform scale correctly.
                normalMatrices[boneIndex] = math.transpose(math.inverse(m3));
            }
            else
            {
                normalMatrices[boneIndex] = float3x3.identity;
            }
        }
    }

    // Position-only CPU skinning (unlit path). Normals/tangents are not skinned because the unlit
    // UI shader ignores them; the dynamic mesh keeps its rest-pose normals/tangents.
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public struct UIBurstSkinningJobPositionOnly : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> basePositions;
        [ReadOnly] public NativeArray<int4> boneIndices;
        [ReadOnly] public NativeArray<float4> boneWeights;
        [ReadOnly] public NativeArray<float4x4> combinedMatrices;

        [WriteOnly] public NativeArray<float3> outputPositions;

        public void Execute(int index)
        {
            float3 p = basePositions[index];
            int4 idx = boneIndices[index];
            float4 w = boneWeights[index];

            outputPositions[index] = w.x * math.transform(combinedMatrices[idx.x], p)
                                   + w.y * math.transform(combinedMatrices[idx.y], p)
                                   + w.z * math.transform(combinedMatrices[idx.z], p)
                                   + w.w * math.transform(combinedMatrices[idx.w], p);
        }
    }

    // Full CPU skinning: positions + normals + tangents (lit path).
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public struct UIBurstSkinningJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> basePositions;
        [ReadOnly] public NativeArray<float3> baseNormals;
        [ReadOnly] public NativeArray<float4> baseTangents;
        [ReadOnly] public NativeArray<int4> boneIndices;
        [ReadOnly] public NativeArray<float4> boneWeights;
        [ReadOnly] public NativeArray<float4x4> combinedMatrices;
        [ReadOnly] public NativeArray<float3x3> normalMatrices;

        [WriteOnly] public NativeArray<float3> outputPositions;
        [WriteOnly] public NativeArray<float3> outputNormals;
        [WriteOnly] public NativeArray<float4> outputTangents;

        public void Execute(int index)
        {
            float3 p = basePositions[index];
            float3 n = baseNormals[index];
            float4 t = baseTangents[index];

            int4 idx = boneIndices[index];
            float4 w = boneWeights[index];

            float4x4 m0 = combinedMatrices[idx.x];
            float4x4 m1 = combinedMatrices[idx.y];
            float4x4 m2 = combinedMatrices[idx.z];
            float4x4 m3 = combinedMatrices[idx.w];

            // Skinned Position
            float3 pos = w.x * math.transform(m0, p)
                       + w.y * math.transform(m1, p)
                       + w.z * math.transform(m2, p)
                       + w.w * math.transform(m3, p);

            // Skinned Normal (inverse-transpose 3x3 precomputed per bone — correct under non-uniform scale)
            float3x3 r0 = normalMatrices[idx.x];
            float3x3 r1 = normalMatrices[idx.y];
            float3x3 r2 = normalMatrices[idx.z];
            float3x3 r3 = normalMatrices[idx.w];

            float3 norm = w.x * math.mul(r0, n)
                        + w.y * math.mul(r1, n)
                        + w.z * math.mul(r2, n)
                        + w.w * math.mul(r3, n);
            norm = math.normalize(norm);

            // Skinned Tangent
            float3 tang = w.x * math.mul(r0, t.xyz)
                        + w.y * math.mul(r1, t.xyz)
                        + w.z * math.mul(r2, t.xyz)
                        + w.w * math.mul(r3, t.xyz);
            tang = math.normalize(tang);

            outputPositions[index] = pos;
            outputNormals[index] = norm;
            outputTangents[index] = new float4(tang, t.w);
        }
    }
}
