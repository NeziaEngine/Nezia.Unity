using System;
using Nezia.Native;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;

namespace Nezia.Unity
{
    /// <summary>
    /// アクティブな spatial <see cref="NeziaAudioSource"/> の位置 / 速度を
    /// <see cref="TransformAccessArray"/> + <see cref="IJobParallelForTransform"/> で
    /// 並列収集し、フレーム末尾に 1 回の FFI 呼び出しでネイティブへ流し込む。
    ///
    /// <para>
    /// 旧実装は Source ごとに <c>LateUpdate</c> を持ち、各 MB が
    /// <c>transform.position</c> を読んで static バッファに enqueue していた。
    /// ソース数 N に比例して MB ディスパッチと managed transform 読みが積まれ、
    /// Unity 標準 <c>AudioSource</c> と同じ per-MB コストが支配的だった。
    /// </para>
    ///
    /// <para>
    /// 本クラスは Source を 1 度だけ <see cref="Register"/> し、Pump の
    /// <see cref="Flush"/> から Burst Job が並列に position/velocity を計算する。
    /// 静止 / 移動の判定や速度差分も Job 内で完結し、結果は pinned
    /// <see cref="NativeArray{T}"/> から直接 FFI に渡す。
    /// </para>
    /// </summary>
    internal static class NeziaSpatialUpdater
    {
        // 1 エントリ分の job 内状態。`prevPos` / `hasPrev` は Job 内で更新される。
        internal struct EntryData
        {
            public NeziaEntityId source;
            public Vector3 prevPos;
            public int hasPrev; // 0 = 初回未取得 / 1 = prevPos が有効
        }

        // TransformAccessArray と並列に並ぶ owner / entry。すべて同じ index で参照する。
        private static TransformAccessArray s_transforms;
        private static NeziaAudioSource[] s_owners = Array.Empty<NeziaAudioSource>();
        private static NativeArray<EntryData> s_entries;
        private static NativeArray<NeziaSourcePositionUpdate> s_positions;
        private static NativeArray<NeziaSourceVelocityUpdate> s_velocities;
        private static int s_count;
        private static int s_capacity;

        private const int InitialCapacity = 64;

        private static void EnsureCreated()
        {
            if (s_transforms.isCreated) return;
            s_transforms = new TransformAccessArray(InitialCapacity);
            s_owners = new NeziaAudioSource[InitialCapacity];
            s_entries = new NativeArray<EntryData>(InitialCapacity, Allocator.Persistent);
            s_positions = new NativeArray<NeziaSourcePositionUpdate>(InitialCapacity, Allocator.Persistent);
            s_velocities = new NativeArray<NeziaSourceVelocityUpdate>(InitialCapacity, Allocator.Persistent);
            s_capacity = InitialCapacity;
        }

        /// <summary>
        /// spatial source を登録する。返り値の index を呼び出し側が保持し、
        /// <see cref="Unregister"/> 時に渡す。
        /// </summary>
        internal static int Register(NeziaAudioSource src, Transform t, NeziaEntityId id)
        {
            EnsureCreated();
            if (s_count == s_capacity) Grow(s_capacity * 2);
            int idx = s_count++;
            s_transforms.Add(t);
            s_owners[idx] = src;
            s_entries[idx] = new EntryData
            {
                source = id,
                prevPos = t.position,
                hasPrev = 0,
            };
            return idx;
        }

        /// <summary>
        /// 登録解除。swap-back で穴を詰めるため、末尾でなければ移動された owner に
        /// 新しい index を通知する。
        /// </summary>
        internal static void Unregister(int idx)
        {
            if (idx < 0 || idx >= s_count) return;
            int last = s_count - 1;
            s_transforms.RemoveAtSwapBack(idx);
            if (idx != last)
            {
                var moved = s_owners[last];
                s_owners[idx] = moved;
                s_entries[idx] = s_entries[last];
                if (moved != null) moved.NotifySpatialIndexChanged(idx);
            }
            s_owners[last] = null;
            s_count = last;
        }

        private static void Grow(int newCap)
        {
            Array.Resize(ref s_owners, newCap);

            var newEntries = new NativeArray<EntryData>(newCap, Allocator.Persistent);
            NativeArray<EntryData>.Copy(s_entries, newEntries, s_count);
            s_entries.Dispose();
            s_entries = newEntries;

            // positions / velocities は毎フレーム上書きなのでコピー不要、Dispose して張り替えるだけ。
            s_positions.Dispose();
            s_positions = new NativeArray<NeziaSourcePositionUpdate>(newCap, Allocator.Persistent);
            s_velocities.Dispose();
            s_velocities = new NativeArray<NeziaSourceVelocityUpdate>(newCap, Allocator.Persistent);

            s_capacity = newCap;
        }

        /// <summary>
        /// Job を実行し、結果を 1 回の FFI 呼び出しでネイティブへ送る。
        /// <see cref="NeziaEnginePump.LateUpdate"/> から呼ばれる想定。
        /// </summary>
        internal static unsafe void Flush()
        {
            if (s_count == 0) return;
            if (!NeziaEngine.IsInitialized) return;

            var job = new GatherJob
            {
                entries = s_entries,
                positions = s_positions,
                velocities = s_velocities,
                dt = Time.deltaTime,
            };
            job.Schedule(s_transforms).Complete();

            var engine = NeziaEngine.RequireHandle();
            var posPtr = (NeziaSourcePositionUpdate*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(s_positions);
            var velPtr = (NeziaSourceVelocityUpdate*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(s_velocities);

            var rp = LibNezia.nezia_source_batch_set_positions(engine, posPtr, (nuint)s_count);
            NeziaException.ThrowIfError(rp, "batch set source positions");
            var rv = LibNezia.nezia_source_batch_set_velocities(engine, velPtr, (nuint)s_count);
            NeziaException.ThrowIfError(rv, "batch set source velocities");
        }

        /// <summary>
        /// 全ネイティブリソースを解放する。<see cref="NeziaEngine.Shutdown"/> から呼ばれる。
        /// 二重呼び出しは安全。
        /// </summary>
        internal static void Shutdown()
        {
            if (s_transforms.isCreated) s_transforms.Dispose();
            if (s_entries.IsCreated) s_entries.Dispose();
            if (s_positions.IsCreated) s_positions.Dispose();
            if (s_velocities.IsCreated) s_velocities.Dispose();
            if (s_owners.Length > 0) Array.Clear(s_owners, 0, s_owners.Length);
            s_count = 0;
            s_capacity = 0;
        }

        [BurstCompile]
        private struct GatherJob : IJobParallelForTransform
        {
            public NativeArray<EntryData> entries;
            [WriteOnly] public NativeArray<NeziaSourcePositionUpdate> positions;
            [WriteOnly] public NativeArray<NeziaSourceVelocityUpdate> velocities;
            public float dt;

            public void Execute(int i, TransformAccess t)
            {
                var p = t.position;
                var entry = entries[i];

                positions[i] = new NeziaSourcePositionUpdate
                {
                    source = entry.source,
                    position = new NeziaVec3 { x = p.x, y = p.y, z = p.z },
                };

                Vector3 v = Vector3.zero;
                if (entry.hasPrev != 0 && dt > 0f)
                    v = (p - entry.prevPos) / dt;
                velocities[i] = new NeziaSourceVelocityUpdate
                {
                    source = entry.source,
                    velocity = new NeziaVec3 { x = v.x, y = v.y, z = v.z },
                };

                entry.prevPos = p;
                entry.hasPrev = 1;
                entries[i] = entry;
            }
        }
    }
}
