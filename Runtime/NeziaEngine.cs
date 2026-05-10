using System;
using Nezia.Native;
using UnityEngine;

namespace Nezia.Unity
{
    /// <summary>
    /// Nezia ネイティブエンジンのライフサイクルを管理する静的ファサード。
    ///
    /// <para>
    /// Unity プロセスにつき 1 インスタンス。<c>BeforeSceneLoad</c> で自動初期化され、
    /// <c>Application.quitting</c> で破棄される。<see cref="Initialize"/> を明示的に呼ぶ
    /// 必要は通常ない。
    /// </para>
    /// </summary>
    public static class NeziaEngine
    {
        private static unsafe global::Nezia.Native.NeziaEngine* s_handle;
        private static bool s_initialized;
        private static GameObject s_pumpObject;
        private static int s_generation;

        // ユーザーが <see cref="MasterVolume"/> 経由で設定した値。
        private static float s_userMasterVolume = 1.0f;
        // 外部 (Editor の Game View ミュート等) からのスケール。実効値 = user × scale。
        private static float s_masterVolumeScale = 1.0f;

        /// <summary>エンジンが初期化済みか。</summary>
        public static bool IsInitialized => s_initialized;

        /// <summary>
        /// ネイティブエンジンの世代番号。<see cref="Initialize"/> ごとにインクリメントされ、
        /// 0 はまだ一度も初期化されていない状態を示す。
        ///
        /// <para>
        /// ScriptableObject にキャッシュしたネイティブハンドル（BufferId / ContainerId / Bus 等）が
        /// 「現在のエンジン世代のものか」を判定するために使う。Enter Play Mode Settings の
        /// "Reload Domain" を OFF にしていると <c>OnEnable</c> / <c>OnDisable</c> が
        /// プレイセッションをまたいで呼ばれず、SO 側のキャッシュが旧エンジンの無効な ID を
        /// 持ち越してしまう。世代不一致を検知して再ロードさせるためのフックがこの値。
        /// </para>
        /// </summary>
        public static int Generation => s_generation;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoInitialize()
        {
            try { Initialize(); }
            catch (Exception e) { Debug.LogException(e); }
        }

        /// <summary>
        /// エンジンを初期化する。多重呼び出しは安全（2 回目以降は no-op）。
        /// </summary>
        public static unsafe void Initialize()
        {
            if (s_initialized) return;

            var settings = NeziaSettings.Instance;
            if (settings != null && settings.OverrideEngineConfig)
            {
                // ビルド既定値をテンプレートとして取得し、ユーザー設定で上書きしてから渡す。
                // 将来 EngineConfig にフィールドが増えても既定値が拾われる安全策。
                NeziaEngineConfig cfg;
                var rd = LibNezia.nezia_engine_config_default(&cfg);
                NeziaException.ThrowIfError(rd, "engine config default");
                cfg.max_sources = settings.MaxSources;
                cfg.max_physical_voices = settings.MaxPhysicalVoices;
                s_handle = LibNezia.nezia_engine_new_with_config(&cfg);
                if (s_handle == null)
                    throw new InvalidOperationException(
                        $"[Nezia] nezia_engine_new_with_config returned NULL " +
                        $"(max_sources={cfg.max_sources}, max_physical_voices={cfg.max_physical_voices}). " +
                        "max_physical_voices <= max_sources かつ両者 >= 1 を満たす必要があります。");
            }
            else
            {
                s_handle = LibNezia.nezia_engine_new();
                if (s_handle == null)
                    throw new InvalidOperationException("[Nezia] nezia_engine_new returned NULL");
            }

            s_initialized = true;
            s_generation++;

            // 直近のユーザー設定値 × スケールを再適用 (ドメインリロード後の復帰や、
            // Editor 側ブリッジが Initialize 前に scale を書き込んでいた場合に効く)。
            ApplyEffectiveMasterVolume();

            s_pumpObject = new GameObject("[Nezia Engine Pump]") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(s_pumpObject);
            s_pumpObject.AddComponent<NeziaEnginePump>();

            Application.quitting += Shutdown;
        }

        /// <summary>
        /// エンジンを破棄する。以降の API 呼び出しは <see cref="InvalidOperationException"/> を投げる。
        /// </summary>
        public static unsafe void Shutdown()
        {
            if (!s_initialized) return;
            s_initialized = false;

            if (s_pumpObject != null)
            {
                UnityEngine.Object.Destroy(s_pumpObject);
                s_pumpObject = null;
            }

            // spatial updater の NativeArray / TransformAccessArray を解放する。
            NeziaSpatialUpdater.Shutdown();

            LibNezia.nezia_engine_free(s_handle);
            s_handle = null;
        }

        /// <summary>
        /// マスター音量（0.0〜1.0、自動クランプ）。
        /// <para>
        /// 内部では Editor の Game View ミュート等による外部スケールと積算した実効値を
        /// ネイティブへ送る。ユーザーがここに設定した値はそのまま保持される。
        /// </para>
        /// </summary>
        public static unsafe float MasterVolume
        {
            set
            {
                s_userMasterVolume = value;
                ApplyEffectiveMasterVolume();
            }
        }

        /// <summary>
        /// 外部 (Editor の Game View ミュート等) から master volume にかける乗数を設定する。
        /// <see cref="MasterVolume"/> のユーザー設定値は保持したまま実効値だけを変える。
        /// エンジン未初期化でも呼べる (値だけ覚えて Initialize 時に適用)。
        /// </summary>
        internal static void SetMasterVolumeScale(float scale)
        {
            s_masterVolumeScale = scale;
            if (s_initialized) ApplyEffectiveMasterVolume();
        }

        private static unsafe void ApplyEffectiveMasterVolume()
        {
            if (!s_initialized) return;
            var effective = s_userMasterVolume * s_masterVolumeScale;
            var r = LibNezia.nezia_engine_set_volume(RequireHandle(), effective);
            NeziaException.ThrowIfError(r, "set master volume");
        }

        /// <summary>すべてのボイスを停止する。</summary>
        public static unsafe void StopAll()
        {
            var r = LibNezia.nezia_engine_stop_all(RequireHandle());
            NeziaException.ThrowIfError(r, "stop all");
        }

        /// <summary>マスターバス。</summary>
        public static unsafe NeziaBus MasterBus
            => new NeziaBus(LibNezia.nezia_engine_master_bus(RequireHandle()));

        /// <summary>
        /// 媒質中の音速 (m/s)。Unity の <c>AudioSettings.speedOfSound</c> 互換。
        /// 既定 343.0。0 以下は無視。水中シーン等で 1480.0 等に変更する。
        /// </summary>
        public static unsafe float SoundSpeed
        {
            set
            {
                var r = LibNezia.nezia_set_sound_speed(RequireHandle(), value);
                NeziaException.ThrowIfError(r, "set sound speed");
            }
        }

        /// <summary>
        /// SP-06 リスナーフォーカスを設定する。距離減衰用とパンニング用で
        /// 独立した補間係数を取り、空間演算では
        /// <c>lerp(listener_position, focus_point, level)</c> で導出した
        /// 仮想リスナー位置を使う。<c>level = 0</c> でフォーカス無効。
        /// </summary>
        public static unsafe void SetListenerFocus(
            Vector3 focusPoint, float distanceFocusLevel, float directionFocusLevel)
        {
            var r = LibNezia.nezia_listener_set_focus(
                RequireHandle(),
                new NeziaVec3 { x = focusPoint.x, y = focusPoint.y, z = focusPoint.z },
                distanceFocusLevel,
                directionFocusLevel);
            NeziaException.ThrowIfError(r, "set listener focus");
        }

        /// <summary>出力サンプルレート (Hz)。</summary>
        public static unsafe uint OutputSampleRate
        {
            get
            {
                uint sr = 0;
                LibNezia.nezia_engine_output_format(RequireHandle(), &sr, null);
                return sr;
            }
        }

        /// <summary>出力チャンネル数。</summary>
        public static unsafe ushort OutputChannels
        {
            get
            {
                ushort ch = 0;
                LibNezia.nezia_engine_output_format(RequireHandle(), null, &ch);
                return ch;
            }
        }

        /// <summary>エンジン起動以降の累積処理フレーム数 (per-channel sample count)。</summary>
        public static unsafe ulong DspTimeSamples
            => LibNezia.nezia_engine_dsp_time_samples(RequireHandle());

        /// <summary><see cref="DspTimeSamples"/> を秒に換算した値。Unity の <c>AudioSettings.dspTime</c> 相当。</summary>
        public static unsafe double DspTime
            => LibNezia.nezia_engine_dsp_time_seconds(RequireHandle());

        /// <summary>
        /// マスター出力キャプチャを有効化し、リーダーを返す。二重呼び出しは <c>null</c> を返す。
        /// </summary>
        public static unsafe NeziaMasterCapture EnableMasterCapture()
        {
            var reader = LibNezia.nezia_engine_enable_master_capture(RequireHandle());
            return reader == null ? null : new NeziaMasterCapture(reader);
        }

        /// <summary>マスター出力キャプチャを無効化する（リーダーは残量 drain 用に残す）。</summary>
        public static unsafe void DisableMasterCapture()
            => LibNezia.nezia_engine_disable_master_capture(RequireHandle());

        /// <summary>
        /// オーディオコールバックの DSP 負荷統計を取得する。
        /// Unity の <c>AudioSettings.GetCPULoad()</c> / Profiler Audio DSP CPU の対応物。
        /// </summary>
        public static unsafe NeziaDspStats GetDspStats()
        {
            NeziaDspStats stats;
            var r = LibNezia.nezia_engine_get_dsp_stats(
                RequireHandle(),
                &stats.LoadPercent,
                &stats.CallbackMicroseconds,
                &stats.PeakMicroseconds,
                &stats.AverageMicroseconds,
                &stats.CallbackCount);
            NeziaException.ThrowIfError(r, "get dsp stats");
            return stats;
        }

        /// <summary>
        /// 現在再生中 (state == Playing) のソース数。Unity の Playing Sources カウンタ相当。
        /// audio thread が毎コールバック末尾に atomic store した最新値を返す。
        /// </summary>
        public static unsafe uint ActiveSourceCount
        {
            get
            {
                uint count = 0;
                var r = LibNezia.nezia_engine_get_active_source_count(RequireHandle(), &count);
                NeziaException.ThrowIfError(r, "get active source count");
                return count;
            }
        }

        /// <summary>
        /// ベンチマーク用のドロップアウト系累積カウンタを取得する。
        /// </summary>
        public static unsafe NeziaDropouts GetDropouts()
        {
            NeziaDropouts d;
            var r = LibNezia.nezia_engine_get_dropouts(
                RequireHandle(),
                &d.VoiceSteal,
                &d.Underrun,
                &d.DroppedPlayCalls,
                &d.CommandQueueFull);
            NeziaException.ThrowIfError(r, "get dropouts");
            return d;
        }

        /// <summary>
        /// エンジンのメモリ使用量スナップショットを取得する。
        ///
        /// <para>
        /// <see cref="NeziaMemoryStats.HeapTracked"/> が <c>true</c> のときに限り
        /// <c>HeapBytesInUse</c> / <c>HeapBytesPeak</c> / <c>AllocCount</c> / <c>FreeCount</c>
        /// が live で更新される (cdylib 配布時のみ)。サブシステム別の実バイト数
        /// (<c>VoicesBytes</c> 等) は常時取得可能。
        /// </para>
        /// </summary>
        public static unsafe NeziaMemoryStats GetMemoryStats()
        {
            NeziaMemoryStatsC raw;
            var r = LibNezia.nezia_engine_get_memory_stats(RequireHandle(), &raw);
            NeziaException.ThrowIfError(r, "get memory stats");
            return new NeziaMemoryStats
            {
                HeapBytesInUse = raw.heap_bytes_in_use,
                HeapBytesPeak = raw.heap_bytes_peak,
                AllocCount = raw.alloc_count,
                FreeCount = raw.free_count,
                HeapTracked = raw.heap_tracked != 0,
                VoicesBytes = raw.voices_bytes,
                BuffersBytes = raw.buffers_bytes,
                EffectsBytes = raw.effects_bytes,
                GraphBytes = raw.graph_bytes,
            };
        }

        // ─── internal ────────────────────────────────────────────

        internal static unsafe global::Nezia.Native.NeziaEngine* RequireHandle()
        {
            if (!s_initialized || s_handle == null)
                throw new InvalidOperationException(
                    "[Nezia] Engine is not initialized. Call NeziaEngine.Initialize() first.");
            return s_handle;
        }

        internal static unsafe void PollEvents()
        {
            if (!s_initialized) return;
            LibNezia.nezia_engine_poll_events(s_handle);
        }
    }

    /// <summary>
    /// オーディオコールバックの DSP 負荷統計スナップショット。
    /// </summary>
    public struct NeziaDspStats
    {
        /// <summary>直近 callback の負荷率 (0.0〜1.0+, <c>last_ns / budget_ns</c>)。</summary>
        public float LoadPercent;
        /// <summary>直近 callback の処理時間 (μs)。</summary>
        public float CallbackMicroseconds;
        /// <summary>起動以降の最大 callback 処理時間 (μs)。</summary>
        public float PeakMicroseconds;
        /// <summary>起動以降の平均 callback 処理時間 (μs)。</summary>
        public float AverageMicroseconds;
        /// <summary>起動以降の累積 callback 回数。</summary>
        public ulong CallbackCount;
    }

    /// <summary>
    /// 起動以降のドロップアウト系累積カウンタ (ベンチマーク用)。
    /// </summary>
    public struct NeziaDropouts
    {
        /// <summary>
        /// callback ごとの virtualized voice 数の累積和。
        /// Nezia は <c>MAX_PHYSICAL_VOICES</c> 超過時に優先度下位を一時的に mix スキップする
        /// 設計のため、伝統的な voice steal とは意味が異なり、
        /// 「mix されなかった voice-frame の数」と読む。
        /// </summary>
        public ulong VoiceSteal;
        /// <summary>ストリーミングバッファ underrun の累積発生回数。</summary>
        public ulong Underrun;
        /// <summary><c>MAX_SOURCES</c> 上限到達による Play コマンド失敗の累積回数。</summary>
        public ulong DroppedPlayCalls;
        /// <summary>
        /// SPSC コマンドリングが満杯で <c>try_push</c> が失敗した累積回数。
        /// <see cref="DroppedPlayCalls"/> (= <c>MAX_SOURCES</c> 到達) と原因が異なり、
        /// 1 フレームで API バーストして audio thread が drain する前にリングが詰まったケースを示す。
        /// </summary>
        public ulong CommandQueueFull;
    }

    /// <summary>
    /// エンジンのメモリ使用量スナップショット。
    ///
    /// <para>
    /// 2 つの独立した経路で「Nezia が使っているメモリ」を観測する:
    /// </para>
    /// <list type="number">
    ///   <item>
    ///     <description>
    ///     グローバルアロケータ統計 (<see cref="HeapBytesInUse"/> 等)。
    ///     <c>mem-tracking</c> feature を有効にした cdylib でのみ live で更新され、
    ///     その場合 <see cref="HeapTracked"/> が <c>true</c> になる。staticlib リンク時は
    ///     全カウンタ 0 + <see cref="HeapTracked"/> が <c>false</c>。
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///     各サブシステムの「論理保持バイト」 (<see cref="VoicesBytes"/> など)。
    ///     <c>Vec.capacity * size_of</c> ベースで walker が常時集計するため、リンク形態に
    ///     関わらず取得可能。
    ///     </description>
    ///   </item>
    /// </list>
    /// </summary>
    public struct NeziaMemoryStats
    {
        /// <summary>現在ヒープに確保中のバイト数 (cdylib + <c>mem-tracking</c> 時のみ有効)。</summary>
        public ulong HeapBytesInUse;
        /// <summary>起動以降のヒープ使用量ピーク (cdylib + <c>mem-tracking</c> 時のみ有効)。</summary>
        public ulong HeapBytesPeak;
        /// <summary>累積 alloc 回数 (cdylib + <c>mem-tracking</c> 時のみ有効)。</summary>
        public ulong AllocCount;
        /// <summary>累積 free 回数 (cdylib + <c>mem-tracking</c> 時のみ有効)。</summary>
        public ulong FreeCount;
        /// <summary>
        /// グローバルアロケータ統計が live で更新されているか。<c>false</c> のとき
        /// <see cref="HeapBytesInUse"/> 系は常に 0。
        /// </summary>
        public bool HeapTracked;
        /// <summary>ボイス / ソース系サブシステムの論理保持バイト数。</summary>
        public ulong VoicesBytes;
        /// <summary>オーディオバッファプールの論理保持バイト数 (PCM 実バイトを含む)。</summary>
        public ulong BuffersBytes;
        /// <summary>エフェクト系ワールドの論理保持バイト数。</summary>
        public ulong EffectsBytes;
        /// <summary>バス / ルーティンググラフの論理保持バイト数。</summary>
        public ulong GraphBytes;
    }
}
