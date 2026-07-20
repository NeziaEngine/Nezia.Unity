using Nezia.Native;

namespace Nezia.Unity
{
    /// <summary>
    /// ランタイムプロファイラ (M3 可視化基盤) の読み取り API。
    ///
    /// <para>
    /// 有効化するとサウンドスレッドが毎コールバック末尾に状態フレーム
    /// (バス gain/muted / アクティブソース詳細 / master peak / Snapshot 進行) を
    /// triple buffer へ publish する。読み側はどの頻度でポーリングしても
    /// サウンドスレッドを妨げない (newest-wins)。
    /// 無効時のサウンドスレッド追加コストは atomic load 1 回のみなので、
    /// 可視化ウィンドウを開いている間だけ有効にする運用を想定する。
    /// </para>
    /// </summary>
    public static class NeziaProfiler
    {
        /// <summary>ソースの再生状態 (core `SourceState` の写像)。</summary>
        public enum SourceState : byte
        {
            Stopped = 0,
            Playing = 1,
            Scheduled = 2,
            Pausing = 3,
        }

        /// <summary>バス 1 本のプロファイル。</summary>
        public struct BusInfo
        {
            public uint Index;
            public uint Generation;
            /// <summary>現在の線形ゲイン (Snapshot 補間中はその瞬間値)。</summary>
            public float Gain;
            public bool Muted;
        }

        /// <summary>アクティブソース 1 本のプロファイル。</summary>
        public struct SourceInfo
        {
            public uint Index;
            public uint Generation;
            /// <summary>出力先バスの EntityId。無効時は Index = uint.MaxValue。</summary>
            public uint BusIndex;
            public uint BusGeneration;
            public float Volume;
            public float Pitch;
            /// <summary>再生位置 (ソースフレーム、ピッチ換算前)。</summary>
            public float SampleOffset;
            public SourceState State;
            /// <summary>virtualizer によって mix スキップされているか。</summary>
            public bool IsVirtual;
        }

        private static bool s_enabled;

        /// <summary>プロファイラ publish の有効/無効。</summary>
        public static unsafe bool Enabled
        {
            get => s_enabled;
            set
            {
                var r = LibNezia.nezia_profiler_set_enabled(NeziaEngine.RequireHandle(), value);
                NeziaException.ThrowIfError(r, "profiler set enabled");
                s_enabled = value;
            }
        }

        /// <summary>
        /// 最新フレームを取り込む。以降の読み取りはこのフレームの値を返す。
        /// ポーリングごとに 1 回呼ぶこと。
        /// </summary>
        public static unsafe void Update()
        {
            var r = LibNezia.nezia_profiler_update(NeziaEngine.RequireHandle());
            NeziaException.ThrowIfError(r, "profiler update");
        }

        /// <summary>直近フレームのマスター出力 peak (soft limiter 後、callback 内 max |sample|)。</summary>
        public static unsafe void GetMasterPeak(out float left, out float right)
        {
            float l, rr;
            var r = LibNezia.nezia_profiler_master_peak(NeziaEngine.RequireHandle(), &l, &rr);
            NeziaException.ThrowIfError(r, "profiler master peak");
            left = l;
            right = rr;
        }

        /// <summary>
        /// 直近フレームの Mixer Snapshot フェード進行 (サンプル)。
        /// 戻り値 false = フェード非進行 (total 0)。
        /// </summary>
        public static unsafe bool GetSnapshotProgress(out ulong total, out ulong remaining)
        {
            ulong t, rem;
            var r = LibNezia.nezia_profiler_snapshot_progress(NeziaEngine.RequireHandle(), &t, &rem);
            NeziaException.ThrowIfError(r, "profiler snapshot progress");
            total = t;
            remaining = rem;
            return t > 0;
        }

        /// <summary>
        /// 直近フレームの生存バスを <paramref name="destination"/> にコピーする。
        /// 戻り値は書き込んだ個数 (配列長超過分は切り捨て)。
        /// </summary>
        public static unsafe int ReadBuses(BusInfo[] destination)
        {
            if (destination == null || destination.Length == 0)
            {
                return 0;
            }
            var native = stackalloc NeziaProfilerBus[destination.Length];
            var count = (int)LibNezia.nezia_profiler_copy_buses(
                NeziaEngine.RequireHandle(), native, (nuint)destination.Length);
            for (var i = 0; i < count; i++)
            {
                destination[i] = new BusInfo
                {
                    Index = native[i].index,
                    Generation = native[i].generation,
                    Gain = native[i].gain,
                    Muted = native[i].muted != 0,
                };
            }
            return count;
        }

        /// <summary>
        /// 直近フレームの生存ソースを <paramref name="destination"/> にコピーする。
        /// 戻り値は書き込んだ個数 (配列長超過分は切り捨て)。
        /// </summary>
        public static unsafe int ReadSources(SourceInfo[] destination)
        {
            if (destination == null || destination.Length == 0)
            {
                return 0;
            }
            // stackalloc は大きい配列で危険 (ソースは数千になり得る) なので
            // 一時 native 配列を使う。ポーリング用途なので GC 圧は許容範囲。
            var native = new NeziaProfilerSource[destination.Length];
            int count;
            fixed (NeziaProfilerSource* p = native)
            {
                count = (int)LibNezia.nezia_profiler_copy_sources(
                    NeziaEngine.RequireHandle(), p, (nuint)destination.Length);
            }
            for (var i = 0; i < count; i++)
            {
                destination[i] = new SourceInfo
                {
                    Index = native[i].index,
                    Generation = native[i].generation,
                    BusIndex = native[i].bus_index,
                    BusGeneration = native[i].bus_generation,
                    Volume = native[i].volume,
                    Pitch = native[i].pitch,
                    SampleOffset = native[i].sample_offset,
                    State = (SourceState)native[i].state,
                    IsVirtual = native[i].is_virtual != 0,
                };
            }
            return count;
        }
    }
}
