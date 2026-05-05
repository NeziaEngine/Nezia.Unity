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

        /// <summary>エンジンが初期化済みか。</summary>
        public static bool IsInitialized => s_initialized;

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
}
