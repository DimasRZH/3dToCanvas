using System.Collections.Generic;
using UnityEngine;

namespace Optimize.GPUSkinning
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [ExecuteAlways]
    public class GPUSkinningBehaviour : MonoBehaviour
    {
        public GPUSkinningData m_Data;
        public Transform m_RootBone;
        
        private MeshRenderer m_MeshRenderer;
        private MeshFilter m_MeshFilter;
        private MaterialPropertyBlock m_Block;
        private List<Transform> m_Bones = new List<Transform>();
        private GraphicsBuffer m_Buffer;
        private Matrix4x4[] m_SamplingMatrices;
        
        public Bounds m_Bounds;
        public bool m_BoundsGizmos = true;

        private static readonly int kBoneMatricesID = Shader.PropertyToID("_BoneMatrices");

        private void OnEnable()
        {
            LateUpdate();
        }

        private void OnDisable()
        {
            Cleanup();
        }

        private void OnDestroy()
        {
            Cleanup();
        }

        private void Cleanup()
        {
            if (m_Buffer != null)
            {
                m_Buffer.Dispose();
                m_Buffer = null;
            }
        }

        private void OnValidate()
        {
            LateUpdate();
        }

        private bool Validate()
        {
            if (m_RootBone == null || m_Data == null || !m_Data.Valid())
                return false;

            m_MeshFilter = GetComponent<MeshFilter>();
            m_MeshRenderer = GetComponent<MeshRenderer>();
            
            if (m_MeshFilter == null || m_MeshRenderer == null)
                return false;

            if (m_MeshFilter.sharedMesh != m_Data.m_Mesh)
                m_MeshFilter.sharedMesh = m_Data.m_Mesh;

            m_Block ??= new MaterialPropertyBlock();

            int boneCount = m_Data.m_Bones.Count;
            int bufferCount = Mathf.Max(1, boneCount);
            if (m_Buffer == null || m_Buffer.count != bufferCount)
            {
                Cleanup();
                m_Buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferCount, sizeof(float) * 16);
                m_SamplingMatrices = new Matrix4x4[bufferCount];
            }

            if (m_Bones.Count != boneCount)
            {
                m_Bones.Clear();
                for (int i = 0; i < boneCount; i++)
                {
                    var bone = m_Data.m_Bones[i];
                    var boneTransform = m_RootBone.Find(bone.relativePath);
                    if (boneTransform == null)
                    {
                        // Fallback: search by name in children of root bone
                        boneTransform = FindChildRecursive(m_RootBone, GetBoneName(bone.relativePath));
                    }
                    
                    if (boneTransform == null)
                    {
                        m_Bones.Clear();
                        Debug.LogError($"[GPUSkinningBehaviour] Bone not found: {bone.relativePath}", this);
                        return false;
                    }
                    m_Bones.Add(boneTransform);
                }
            }

            return true;
        }

        private string GetBoneName(string path)
        {
            int idx = path.LastIndexOf('/');
            return idx >= 0 ? path.Substring(idx + 1) : path;
        }

        private Transform FindChildRecursive(Transform parent, string name)
        {
            if (parent.name == name) return parent;
            for (int i = 0; i < parent.childCount; i++)
            {
                var found = FindChildRecursive(parent.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        private void LateUpdate()
        {
            if (!Validate())
                return;

            int transformCount = m_Bones.Count;
            Matrix4x4 w2l = transform.worldToLocalMatrix;
            
            // Set initial bounds to the first bone position or transform position
            Bounds bounds = new Bounds(transformCount > 0 ? m_Bones[0].position : transform.position, Vector3.zero);

            if (transformCount == 0)
            {
                m_SamplingMatrices[0] = Matrix4x4.identity;
                // For static meshes, use the default mesh bounds transformed to world space
                var mesh = m_MeshFilter.sharedMesh;
                if (mesh != null)
                {
                    Bounds localBounds = mesh.bounds;
                    Vector3 centerWS = transform.TransformPoint(localBounds.center);
                    Vector3 sizeWS = Vector3.Scale(localBounds.size, transform.lossyScale);
                    bounds = new Bounds(centerWS, sizeWS);
                }
            }
            else
            {
                for (int i = 0; i < transformCount; i++)
                {
                    var boneData = m_Data.m_Bones[i];
                    
                    // Compute the animation matrix: worldToLocal * boneWorld * bindPose
                    m_SamplingMatrices[i] = w2l * m_Bones[i].localToWorldMatrix * boneData.bindPose;
                    
                    // Calculate dynamic world bounds for frustum culling
                    Vector3 centerWS = m_Bones[i].TransformPoint(boneData.boundsCenter);
                    float radiusWS = boneData.boundsRadius * Mathf.Max(m_Bones[i].lossyScale.x, Mathf.Max(m_Bones[i].lossyScale.y, m_Bones[i].lossyScale.z));
                    
                    // Encapsulate the bone bounding sphere
                    bounds.Encapsulate(new Bounds(centerWS, Vector3.one * radiusWS * 2f));
                }
            }

            m_Bounds = bounds;
            m_MeshRenderer.bounds = m_Bounds;
            
            m_Buffer.SetData(m_SamplingMatrices);
            m_Block.SetBuffer(kBoneMatricesID, m_Buffer);
            m_MeshRenderer.SetPropertyBlock(m_Block);
        }

        private void OnDrawGizmos()
        {
            if (!m_BoundsGizmos || !Validate()) return;

            Gizmos.color = Color.yellow;
            Gizmos.DrawWireCube(m_Bounds.center, m_Bounds.size);

            Gizmos.color = new Color(0f, 1f, 1f, 0.2f);
            for (int i = 0; i < m_Bones.Count; i++)
            {
                var boneData = m_Data.m_Bones[i];
                Vector3 centerWS = m_Bones[i].TransformPoint(boneData.boundsCenter);
                float radiusWS = boneData.boundsRadius * Mathf.Max(m_Bones[i].lossyScale.x, Mathf.Max(m_Bones[i].lossyScale.y, m_Bones[i].lossyScale.z));
                Gizmos.DrawWireSphere(centerWS, radiusWS);
            }
        }
    }
}
