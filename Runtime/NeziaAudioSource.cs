using System;
using System.Runtime.InteropServices;
using AOT;
using Nezia.Native;
using UnityEngine;
using UnityEngine.Audio;

namespace Nezia.Unity
{
    /// <summary>
    /// Unity 標準 <c>AudioSource</c> のドロップイン互換コンポーネント。
    ///
    /// <para>
    /// 主要なメソッド・プロパティのシグネチャを <c>AudioSource</c> に合わせる。
    /// 内部では Nezia の <c>nezia_source_*</c> API を呼び出す。
    /// </para>
    ///
    /// <para>
    /// <b>互換範囲</b>: 80% の使用ケースを標準コードのまま動かすことが目標。
    /// <c>reverbZoneMix</c> 等の高度な項目や、AudioMixer Snapshot は対象外。
    /// </para>
    /// </summary>
    [AddComponentMenu("Nezia/Nezia Audio Source")]
    public sealed class NeziaAudioSource : MonoBehaviour
    {
        // ─── Inspector 公開 ──────────────────────────────────────

        [SerializeField] private NeziaAudioClip _clip;
        [SerializeField, Range(0f, 1f)] private float _volume = 1f;
        [SerializeField, Range(-3f, 3f)] private float _pitch = 1f;
        [SerializeField] private bool _loop;
        [SerializeField] private bool _mute;
        [SerializeField] private bool _playOnAwake = true;
        [SerializeField, Range(0f, 1f)] private float _spatialBlend;
        [SerializeField] private float _minDistance = 1f;
        [SerializeField] private float _maxDistance = 500f;
        [SerializeField] private NeziaRolloffMode _rolloffMode = NeziaRolloffMode.InverseDistance;
        [SerializeField] private AudioMixerGroup _outputAudioMixerGroup;
        [SerializeField] private NeziaBusMap _busMap;

        // ─── ランタイム状態 ──────────────────────────────────────

        // ネイティブ側の INVALID は { index: u32::MAX, generation: 0 }。
        // (0, 0) は最初に確保された有効 ID なので INVALID 判定に使ってはいけない。
        private static readonly NeziaEntityId InvalidEntityId =
            new NeziaEntityId { index = uint.MaxValue, generation = 0 };

        private NeziaEntityId _spawnedSource = InvalidEntityId;
        private bool _isPlaying;
        private bool _isPaused;

        // ネイティブ完了コールバックから自分自身を辿るための GCHandle。
        // 自然終了（コールバック発火）または明示 Stop で必ず Free する。
        private GCHandle _selfHandle;

        private bool HasLiveSource => _spawnedSource.index != uint.MaxValue;

        // ─── AudioSource 互換 API ────────────────────────────────

        /// <summary>再生対象クリップ。<c>AudioSource.clip</c> 互換。</summary>
        public NeziaAudioClip clip
        {
            get => _clip;
            set => _clip = value;
        }

        /// <summary>音量 (0.0〜1.0)。<c>AudioSource.volume</c> 互換。</summary>
        public unsafe float volume
        {
            get => _volume;
            set
            {
                _volume = Mathf.Clamp01(value);
                if (HasLiveSource)
                {
                    var r = LibNezia.nezia_source_set_volume(
                        NeziaEngine.RequireHandle(), _spawnedSource, _mute ? 0f : _volume);
                    NeziaException.ThrowIfError(r, "set source volume");
                }
            }
        }

        /// <summary>ピッチ。<c>AudioSource.pitch</c> 互換。</summary>
        public unsafe float pitch
        {
            get => _pitch;
            set
            {
                _pitch = value;
                if (HasLiveSource)
                {
                    var r = LibNezia.nezia_source_set_pitch(
                        NeziaEngine.RequireHandle(), _spawnedSource, _pitch);
                    NeziaException.ThrowIfError(r, "set source pitch");
                }
            }
        }

        /// <summary>ループ再生。<c>AudioSource.loop</c> 互換。</summary>
        public unsafe bool loop
        {
            get => _loop;
            set
            {
                _loop = value;
                if (HasLiveSource)
                {
                    var r = LibNezia.nezia_source_set_loop(
                        NeziaEngine.RequireHandle(), _spawnedSource, _loop ? (byte)1 : (byte)0);
                    NeziaException.ThrowIfError(r, "set source loop");
                }
            }
        }

        /// <summary>ミュート。<c>AudioSource.mute</c> 互換。</summary>
        public unsafe bool mute
        {
            get => _mute;
            set
            {
                _mute = value;
                if (HasLiveSource)
                {
                    var r = LibNezia.nezia_source_set_volume(
                        NeziaEngine.RequireHandle(), _spawnedSource, _mute ? 0f : _volume);
                    NeziaException.ThrowIfError(r, "set source volume (mute)");
                }
            }
        }

        /// <summary>2D/3D ブレンド (0=2D, 1=3D)。<c>AudioSource.spatialBlend</c> 互換。</summary>
        public float spatialBlend { get => _spatialBlend; set => _spatialBlend = Mathf.Clamp01(value); }

        /// <summary>距離減衰の最小距離。</summary>
        public float minDistance { get => _minDistance; set => _minDistance = value; }

        /// <summary>距離減衰の最大距離。</summary>
        public float maxDistance { get => _maxDistance; set => _maxDistance = value; }

        /// <summary>距離減衰モデル。</summary>
        public NeziaRolloffMode rolloffMode { get => _rolloffMode; set => _rolloffMode = value; }

        /// <summary>起動時に自動再生するか。<c>AudioSource.playOnAwake</c> 互換。</summary>
        public bool playOnAwake { get => _playOnAwake; set => _playOnAwake = value; }

        /// <summary>
        /// 出力先 <see cref="NeziaBus"/>。<c>AudioSource.outputAudioMixerGroup</c> の代替。
        /// 未設定（<see cref="NeziaBus.Invalid"/>）の場合はマスターバスへ送られる。
        /// </summary>
        public NeziaBus outputBus { get; set; } = NeziaBus.Invalid;

        /// <summary>
        /// <c>AudioSource.outputAudioMixerGroup</c> 互換。<see cref="NeziaBusMap"/> が
        /// 設定されていればそれ経由で対応する <see cref="NeziaBus"/> に解決される。
        /// </summary>
        public AudioMixerGroup outputAudioMixerGroup
        {
            get => _outputAudioMixerGroup;
            set
            {
                _outputAudioMixerGroup = value;
                outputBus = (_busMap != null && value != null)
                    ? _busMap.Resolve(value)
                    : NeziaBus.Invalid;
            }
        }

        /// <summary>MixerGroup → Bus 解決に使うマップ。シーン内で共通のものを参照させる。</summary>
        public NeziaBusMap busMap
        {
            get => _busMap;
            set { _busMap = value; outputAudioMixerGroup = _outputAudioMixerGroup; }
        }

        /// <summary>
        /// 現在再生中か。<c>AudioSource.isPlaying</c> 互換。
        ///
        /// <para>
        /// ローカル状態 (<c>_isPlaying</c>) を返す。Stop / 自然終了コールバック /
        /// Pause で正しく追従するので、これで信頼できる。
        /// <c>nezia_source_is_alive</c> はスナップショット参照のため直近 spawn を
        /// 反映できないレースがあり、<c>isPlaying</c> の根拠には使わない。
        /// </para>
        /// </summary>
        public bool isPlaying => _isPlaying && !_isPaused;

        /// <summary>
        /// 再生位置 (秒)。<c>AudioSource.time</c> 互換。
        /// 内部では <c>nezia_source_seek / get_position</c> をフレーム単位で呼ぶ。
        /// クリップの <see cref="NeziaAudioClip.SampleRate"/> が 0 の場合は 44100 を仮定する。
        /// </summary>
        public unsafe float time
        {
            get
            {
                if (!HasLiveSource) return 0f;
                float frames;
                var r = LibNezia.nezia_source_get_position(
                    NeziaEngine.RequireHandle(), _spawnedSource, &frames);
                if (r != NeziaResult.Ok) return 0f;
                int sr = (_clip != null && _clip.SampleRate > 0) ? _clip.SampleRate : 44100;
                return frames / sr;
            }
            set
            {
                if (!HasLiveSource) return;
                int sr = (_clip != null && _clip.SampleRate > 0) ? _clip.SampleRate : 44100;
                var r = LibNezia.nezia_source_seek(
                    NeziaEngine.RequireHandle(), _spawnedSource, value * sr);
                NeziaException.ThrowIfError(r, "seek source");
            }
        }

        /// <summary>クリップを再生する。<c>AudioSource.Play()</c> 互換。</summary>
        public unsafe void Play()
        {
            if (_clip == null) return;
            var buffer = _clip.GetOrLoadBuffer();
            if (!buffer.IsValid) return;

            // 既存ソースがあれば停止してから新規 spawn する（AudioSource.Play の再起動セマンティクス）
            if (HasLiveSource) StopInternal();

            var engine = NeziaEngine.RequireHandle();
            var effectiveVolume = _mute ? 0f : _volume;
            var busId = outputBus.IsValid ? outputBus.Id : LibNezia.nezia_engine_master_bus(engine);

            // 自然終了をネイティブから受け取るためのコールバック登録。
            // looping のときは終了通知が発火しないので、コールバック登録自体を省略してよい。
            delegate* unmanaged[Cdecl]<void*, void> cb = null;
            void* userData = null;
            if (!_loop)
            {
                _selfHandle = GCHandle.Alloc(this, GCHandleType.Weak);
                userData = (void*)GCHandle.ToIntPtr(_selfHandle);
                cb = (delegate* unmanaged[Cdecl]<void*, void>)s_finishCallbackPtr;
            }

            var src = LibNezia.nezia_source_play_with_handle(
                engine, buffer.Id, effectiveVolume, _pitch, busId,
                _loop ? (byte)1 : (byte)0, cb, userData);
            if (src.index == uint.MaxValue)
            {
                FreeSelfHandle();
                return; // INVALID
            }

            _spawnedSource = src;
            _isPlaying = true;
            _isPaused = false;

            if (_spatialBlend > 0f)
            {
                var r = LibNezia.nezia_source_set_spatial_params(
                    engine, src, _rolloffMode.ToNative(), _minDistance, _maxDistance, 1f);
                NeziaException.ThrowIfError(r, "set spatial params");

                r = LibNezia.nezia_source_set_spatial_enabled(engine, src, 1);
                NeziaException.ThrowIfError(r, "set spatial enabled");

                PushPosition();
            }
            else
            {
                var r = LibNezia.nezia_source_set_spatial_enabled(engine, src, 0);
                NeziaException.ThrowIfError(r, "set spatial disabled");
            }
        }

        /// <summary>
        /// 指定クリップをワンショット再生する（現在の再生は維持）。
        /// fire-and-forget なため Stop / Pause の対象外。
        /// </summary>
        public unsafe void PlayOneShot(NeziaAudioClip oneShot, float volumeScale = 1f)
        {
            if (oneShot == null) return;
            var buffer = oneShot.GetOrLoadBuffer();
            if (!buffer.IsValid) return;
            var v = (_mute ? 0f : _volume) * volumeScale;
            var engine = NeziaEngine.RequireHandle();
            if (outputBus.IsValid)
                LibNezia.nezia_source_play_to_bus(engine, buffer.Id, v, _pitch, outputBus.Id, 0);
            else
                LibNezia.nezia_source_play(engine, buffer.Id, v, _pitch, 0);
        }

        /// <summary>停止。<c>AudioSource.Stop()</c> 互換。</summary>
        public unsafe void Stop()
        {
            if (HasLiveSource) StopInternal();
        }

        /// <summary>一時停止。<c>AudioSource.Pause()</c> 互換。</summary>
        public unsafe void Pause()
        {
            if (!HasLiveSource || _isPaused) return;
            var r = LibNezia.nezia_source_pause(NeziaEngine.RequireHandle(), _spawnedSource);
            NeziaException.ThrowIfError(r, "pause source");
            _isPaused = true;
        }

        /// <summary>再開。<c>AudioSource.UnPause()</c> 互換。</summary>
        public unsafe void UnPause()
        {
            if (!HasLiveSource || !_isPaused) return;
            var r = LibNezia.nezia_source_resume(NeziaEngine.RequireHandle(), _spawnedSource);
            NeziaException.ThrowIfError(r, "resume source");
            _isPaused = false;
        }

        /// <summary>位置指定でクリップを 1 回再生する。<c>AudioSource.PlayClipAtPoint</c> 互換。</summary>
        public static void PlayClipAtPoint(NeziaAudioClip clip, Vector3 position, float volume = 1f)
        {
            if (clip == null) return;
            var go = new GameObject("[Nezia OneShot]");
            go.transform.position = position;
            var src = go.AddComponent<NeziaAudioSource>();
            src._clip = clip;
            src._volume = volume;
            src._spatialBlend = 1f;
            src._playOnAwake = false;
            src._destroyOnFinish = true;
            src.Play();
        }

        // ─── Unity ライフサイクル ────────────────────────────────

        private bool _destroyOnFinish;

        private void Start()
        {
            // 起動時に AudioMixerGroup → Bus を解決（busMap が後から差された場合の救済）
            if (_busMap != null && _outputAudioMixerGroup != null && !outputBus.IsValid)
                outputBus = _busMap.Resolve(_outputAudioMixerGroup);

            if (_playOnAwake && _clip != null) Play();
        }

        private void LateUpdate()
        {
            if (_isPlaying && !_isPaused && _spatialBlend > 0f) PushPosition();
        }

        private void OnEnable()
        {
            // Domain Reload 後など、非シリアライズフィールドが default(0,0) に戻るケースでも
            // 「ライブソース無し」を正しく表すよう、明示的に INVALID へリセット。
            _spawnedSource = InvalidEntityId;
            _isPlaying = false;
            _isPaused = false;
        }

        private void OnDisable() => Stop();

        // ─── 内部 ────────────────────────────────────────────────

        private unsafe void StopInternal()
        {
            // エンジン破棄後（Domain Reload / アプリ終了 / シーン破棄）に OnDisable→Stop が
            // 走るケースがある。ネイティブはもう存在しないので例外化せず、ローカル状態だけ畳む。
            if (NeziaEngine.IsInitialized)
            {
                var r = LibNezia.nezia_source_stop(NeziaEngine.RequireHandle(), _spawnedSource);
                // InvalidHandle は既に自然終了済みのケース。例外化しない。
                if (r != NeziaResult.Ok && r != NeziaResult.InvalidHandle)
                    NeziaException.ThrowIfError(r, "stop source");
            }
            _spawnedSource = InvalidEntityId;
            _isPlaying = false;
            _isPaused = false;
            // 明示 Stop の場合は SourceFinished が発火しないので、ここで Free する。
            FreeSelfHandle();
        }

        private void FreeSelfHandle()
        {
            if (_selfHandle.IsAllocated) _selfHandle.Free();
            _selfHandle = default;
        }

        // ネイティブからのコールバック用デリゲート。Unity (Mono / IL2CPP) では
        // [UnmanagedCallersOnly] が利用不可のため、AOT-safe な MonoPInvokeCallback +
        // GetFunctionPointerForDelegate 経由で関数ポインタを得る。
        private delegate void NativeFinishCallback(IntPtr userData);

        private static readonly NativeFinishCallback s_finishCallback = OnNativeFinishedStatic;
        private static readonly IntPtr s_finishCallbackPtr =
            Marshal.GetFunctionPointerForDelegate(s_finishCallback);

        [MonoPInvokeCallback(typeof(NativeFinishCallback))]
        private static void OnNativeFinishedStatic(IntPtr userData)
        {
            var handle = GCHandle.FromIntPtr(userData);
            try
            {
                if (handle.Target is NeziaAudioSource src) src.OnNaturallyFinished();
            }
            finally
            {
                handle.Free();
            }
        }

        private void OnNaturallyFinished()
        {
            // 既に Stop 済みのときは何もしない（Stop 経由で _selfHandle は free 済み）。
            if (!HasLiveSource) return;
            _spawnedSource = InvalidEntityId;
            _isPlaying = false;
            _isPaused = false;
            _selfHandle = default; // 呼び出し元 (OnNativeFinishedStatic) が Free する。

            if (_destroyOnFinish && this != null) Destroy(gameObject);
        }

        private void PushPosition()
        {
            if (!HasLiveSource) return;
            var p = transform.position;
            EnqueuePendingPosition(new NeziaSourcePositionUpdate
            {
                source = _spawnedSource,
                position = new NeziaVec3 { x = p.x, y = p.y, z = p.z },
            });
        }

        // ─── 位置更新の一括送信 ──────────────────────────────────
        //
        // 各ソースが個別に nezia_source_batch_set_positions を呼ぶと、
        // 1 フレームに多数のソースがあるとき FFI 越えの回数がそのまま線形に増える。
        // 同フレーム内の更新は静的バッファへ積み、フレーム末尾の
        // NeziaEnginePump からまとめて 1 回の FFI 呼び出しで送る。

        private static NeziaSourcePositionUpdate[] s_pendingPositions = new NeziaSourcePositionUpdate[64];
        private static int s_pendingPositionCount;

        private static void EnqueuePendingPosition(NeziaSourcePositionUpdate update)
        {
            if (s_pendingPositionCount == s_pendingPositions.Length)
                Array.Resize(ref s_pendingPositions, s_pendingPositions.Length * 2);
            s_pendingPositions[s_pendingPositionCount++] = update;
        }

        internal static unsafe void FlushPendingPositions()
        {
            if (s_pendingPositionCount == 0) return;
            if (!NeziaEngine.IsInitialized)
            {
                s_pendingPositionCount = 0;
                return;
            }

            fixed (NeziaSourcePositionUpdate* ptr = s_pendingPositions)
            {
                var r = LibNezia.nezia_source_batch_set_positions(
                    NeziaEngine.RequireHandle(), ptr, (nuint)s_pendingPositionCount);
                NeziaException.ThrowIfError(r, "batch set source positions");
            }
            s_pendingPositionCount = 0;
        }
    }
}
