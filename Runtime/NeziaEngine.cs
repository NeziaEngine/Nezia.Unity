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

            s_handle = LibNezia.nezia_engine_new();
            if (s_handle == null)
                throw new InvalidOperationException("[Nezia] nezia_engine_new returned NULL");

            s_initialized = true;
            s_generation++;

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

        /// <summary>マスター音量（0.0〜1.0、自動クランプ）。</summary>
        public static unsafe float MasterVolume
        {
            set
            {
                var r = LibNezia.nezia_engine_set_volume(RequireHandle(), value);
                NeziaException.ThrowIfError(r, "set master volume");
            }
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
                &d.DroppedPlayCalls);
            NeziaException.ThrowIfError(r, "get dropouts");
            return d;
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
    }
}
