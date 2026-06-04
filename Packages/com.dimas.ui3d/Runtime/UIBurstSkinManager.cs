using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace UI3D
{
    /// <summary>
    /// Central per-frame driver for all <see cref="UIBurstSkinnedCharacter"/> instances in play mode.
    ///
    /// Why this exists: reading <c>bone.localToWorldMatrix</c> on the main thread (one loop per
    /// character) dominated the frame — profiled at ~7.2ms across 13 characters. This manager moves
    /// those reads onto worker threads with a single <see cref="UIBurstReadBoneWorldMatricesJob"/>
    /// over one global <see cref="TransformAccessArray"/> of every bone, so the transform-system sync
    /// fence is paid ONCE per frame instead of once per character. It then schedules each character's
    /// combine + skinning jobs off that one transform handle and completes them next frame (pipelined).
    /// </summary>
    [DefaultExecutionOrder(10000)] // run after Animators and any user LateUpdate that moves bones
    public class UIBurstSkinManager : MonoBehaviour
    {
        private static UIBurstSkinManager s_Instance;

        public static UIBurstSkinManager Instance
        {
            get
            {
                if (s_Instance == null)
                {
                    var go = new GameObject("[UI3D] Skin Manager") { hideFlags = HideFlags.HideAndDontSave };
                    s_Instance = go.AddComponent<UIBurstSkinManager>();
                }
                return s_Instance;
            }
        }

        private readonly List<UIBurstSkinnedCharacter> m_Characters = new List<UIBurstSkinnedCharacter>();
        private readonly List<int> m_BoneStarts = new List<int>(); // global bone offset per character

        private TransformAccessArray m_BonesTAA;
        private NativeArray<float4x4> m_WorldMatrices; // global: bone localToWorld for every bone
        private bool m_Dirty;

        private JobHandle m_PendingHandle;
        private bool m_HasPending;

        public void Register(UIBurstSkinnedCharacter character)
        {
            if (character == null || m_Characters.Contains(character)) return;
            m_Characters.Add(character);
            m_Dirty = true;
        }

        public void Unregister(UIBurstSkinnedCharacter character)
        {
            if (!m_Characters.Contains(character)) return;
            // The batched handle reads this character's arrays — finish it before the character is
            // allowed to dispose them.
            CompletePending();
            m_Characters.Remove(character);
            m_Dirty = true;
        }

        public static void UnregisterIfExists(UIBurstSkinnedCharacter character)
        {
            if (s_Instance != null) s_Instance.Unregister(character);
        }

        private void CompletePending()
        {
            if (!m_HasPending) return;
            m_PendingHandle.Complete();
            m_HasPending = false;
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying) return;

            // 1. Complete last frame's batched jobs and upload each character's slice. Near-free: the
            //    jobs ran on worker threads through the previous frame.
            if (m_HasPending)
            {
                UnityEngine.Profiling.Profiler.BeginSample("UI3D.CompletePrevJob");
                m_PendingHandle.Complete();
                m_HasPending = false;
                UnityEngine.Profiling.Profiler.EndSample();

                UnityEngine.Profiling.Profiler.BeginSample("UI3D.UploadLoop");
                for (int i = 0; i < m_Characters.Count; i++)
                {
                    var ch = m_Characters[i];
                    if (ch != null && ch.ReadyForSkin) ch.UploadSkinnedResults();
                }
                UnityEngine.Profiling.Profiler.EndSample();
            }

            // 2. Drop destroyed characters, rebuild the global bone array if the set changed.
            if (PruneDestroyed()) m_Dirty = true;
            if (m_Dirty) Rebuild();

            int count = m_Characters.Count;
            if (count == 0 || !m_WorldMatrices.IsCreated || m_BonesTAA.length == 0) return;

            // 3. One transform job reads every bone's world matrix on worker threads.
            UnityEngine.Profiling.Profiler.BeginSample("UI3D.Schedule");
            var readJob = new UIBurstReadBoneWorldMatricesJob { worldMatrices = m_WorldMatrices };
            JobHandle readHandle = readJob.Schedule(m_BonesTAA);

            // 4. Each character's combine+skin chains off the shared transform handle. Entries left as
            //    default (not-ready / skipped characters) are no-op handles and combine harmlessly.
            var handles = new NativeArray<JobHandle>(count, Allocator.Temp);
            for (int i = 0; i < count; i++)
            {
                int start = m_BoneStarts[i];
                var ch = m_Characters[i];
                if (start < 0 || ch == null || !ch.ReadyForSkin) continue;
                handles[i] = ch.ScheduleSkinning(m_WorldMatrices, start, readHandle);
            }

            m_PendingHandle = JobHandle.CombineDependencies(handles);
            handles.Dispose();
            m_HasPending = true;

            UnityEngine.Profiling.Profiler.BeginSample("UI3D.ScheduleBatchedJobs");
            JobHandle.ScheduleBatchedJobs();
            UnityEngine.Profiling.Profiler.EndSample();
            UnityEngine.Profiling.Profiler.EndSample();
        }

        private bool PruneDestroyed()
        {
            bool removed = false;
            for (int i = m_Characters.Count - 1; i >= 0; i--)
            {
                if (m_Characters[i] == null) { m_Characters.RemoveAt(i); removed = true; }
            }
            return removed;
        }

        // Rebuild the global TransformAccessArray + world-matrix array from the current character set.
        private void Rebuild()
        {
            CompletePending();

            if (m_BonesTAA.isCreated) m_BonesTAA.Dispose();
            if (m_WorldMatrices.IsCreated) m_WorldMatrices.Dispose();
            m_BoneStarts.Clear();

            // Per-character global bone offset; -1 marks a not-ready character (skipped when scheduling).
            int totalBones = 0;
            for (int i = 0; i < m_Characters.Count; i++)
            {
                var ch = m_Characters[i];
                if (ch != null && ch.ReadyForSkin)
                {
                    m_BoneStarts.Add(totalBones);
                    totalBones += ch.BoneCount;
                }
                else
                {
                    m_BoneStarts.Add(-1);
                }
            }

            m_Dirty = false;
            if (totalBones == 0) return;

            m_BonesTAA = new TransformAccessArray(totalBones);
            m_WorldMatrices = new NativeArray<float4x4>(totalBones, Allocator.Persistent);

            for (int i = 0; i < m_Characters.Count; i++)
            {
                var ch = m_Characters[i];
                if (ch == null || !ch.ReadyForSkin) continue;
                int bones = ch.BoneCount;
                for (int b = 0; b < bones; b++)
                    m_BonesTAA.Add(ch.BoneOrRoot(b));
            }
        }

        private void OnDestroy()
        {
            CompletePending();
            if (m_BonesTAA.isCreated) m_BonesTAA.Dispose();
            if (m_WorldMatrices.IsCreated) m_WorldMatrices.Dispose();
            if (s_Instance == this) s_Instance = null;
        }
    }
}
