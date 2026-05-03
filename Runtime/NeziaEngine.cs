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
