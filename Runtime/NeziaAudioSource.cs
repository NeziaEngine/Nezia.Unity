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

        // 鳴らす対象。NeziaAudioClip / NeziaRandomContainer など NeziaSoundAsset 派生を受ける。
        // 旧フィールド `_clip` (NeziaAudioClip 型) は AudioSource 互換性維持のため残し、
        // 値が入っている場合は `_sound` 側にコピーされて優先される (`Reset` / Play 時)。
        [SerializeField] private NeziaSoundAsset _sound;
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
        [SerializeField] private NeziaAttenuationCurveAsset _attenuationCurve;
        [SerializeField, Range(0f, 5f)] private float _dopplerLevel = 1f;
        [SerializeField, Range(0, 255)] private int _priority = 128;
        [SerializeField] private AudioMixerGroup _outputAudioMixerGroup;
        [SerializeField] private NeziaBusMap _busMap;
        [SerializeField] private NeziaMixerAsset _mixerAsset;
        [SerializeField] private string _outputBusName;

        // IP-4 (Clip-centric authoring): true のとき音響設定を sound asset 側に委譲する。
        // - volume / pitch は Clip 値への乗算 (scale) として動く
        // - loop は Source または Clip のいずれかが true なら有効
        // - outputBus は Source 側未設定なら Clip の `OutputMixerAsset`/`OutputBusName` を解決
        // - spatial / attenuation / doppler / priority は Clip の `ApplyDefaultsTo` が一括適用
        // 既存プレハブでの破壊的変更を避けるため既定は false。新規 NeziaAudioSource では
        // 推奨値 true。マイグレーションコマンドは PR-C で提供予定。
        [SerializeField,
         Tooltip("ON: 音響設定を Clip (NeziaSoundAsset) に委譲し、Source.volume/pitch は scale として効く。" +
                 "OFF (互換モード): Source 側の値が直接最終値になる従来挙動。")]
        private bool _useClipDefaults;

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

        // Inspector で AnimationCurve として編集された減衰カーブを再生中だけネイティブ確保しておく。
        // Play で生成 → Stop / 自然終了 / Disable で Destroy する。
        private NeziaAttenuationCurve _liveAttenuationCurve = NeziaAttenuationCurve.Invalid;

        private bool HasLiveSource => _spawnedSource.index != uint.MaxValue;

        /// <summary>カスタム距離減衰カーブのアセット。未設定なら <see cref="rolloffMode"/> が使われる。</summary>
        public NeziaAttenuationCurveAsset attenuationCurve
        {
            get => _attenuationCurve;
            set => _attenuationCurve = value;
        }

        // ─── AudioSource 互換 API ────────────────────────────────

        /// <summary>
        /// 再生対象。<see cref="NeziaAudioClip"/>（単発）でも
        /// <see cref="NeziaRandomContainer"/>（ランダム選択）でも受け取れる。
        /// </summary>
        public NeziaSoundAsset sound
        {
            get => _sound != null ? _sound : _clip;
            set
            {
                _sound = value;
                _clip = value as NeziaAudioClip; // 互換 getter のために sync
            }
        }

        /// <summary>再生対象クリップ。<c>AudioSource.clip</c> 互換（旧シグネチャ）。</summary>
        public NeziaAudioClip clip
        {
            get => _sound as NeziaAudioClip ?? _clip;
            set { _clip = value; if (value != null) _sound = value; }
        }

        // 内部用: 現在 dispatch 対象のアセットを返す。`_sound` が優先で、未設定なら旧 `_clip`。
        private NeziaSoundAsset ResolvedSound => _sound != null ? _sound : _clip;

        /// <summary>
        /// Clip-centric モード切替。<c>true</c> なら音響設定を sound asset に委譲する。
        /// 既定 <c>false</c>（後方互換）。新規セットアップでは <c>true</c> 推奨。
        /// </summary>
        public bool useClipDefaults { get => _useClipDefaults; set => _useClipDefaults = value; }

        // Clip 由来の volume / pitch を取得するヘルパ。useClipDefaults=false や asset 未設定なら 1。
        private float ClipVolume()
        {
            if (!_useClipDefaults) return 1f;
            var s = ResolvedSound;
            return s != null ? s.Volume : 1f;
        }

        private float ClipPitch()
        {
            if (!_useClipDefaults) return 1f;
            var s = ResolvedSound;
            return s != null ? s.Pitch : 1f;
        }

        /// <summary>
        /// 音量 (0.0〜1.0)。<c>AudioSource.volume</c> 互換。
        /// <see cref="useClipDefaults"/> が ON のときは Clip 基準音量への乗算 (scale) として
        /// ネイティブへ渡す（最終ゲイン = Clip.Volume × this.volume）。
        /// </summary>
        public unsafe float volume
        {
            get => _volume;
            set
            {
                _volume = Mathf.Clamp01(value);
                if (HasLiveSource)
                {
                    var final = (_mute ? 0f : _volume) * ClipVolume();
                    var r = LibNezia.nezia_source_set_volume(
                        NeziaEngine.RequireHandle(), _spawnedSource, final);
                    NeziaException.ThrowIfError(r, "set source volume");
                }
            }
        }

        /// <summary>
        /// ピッチ。<c>AudioSource.pitch</c> 互換。
        /// <see cref="useClipDefaults"/> が ON のときは Clip 基準ピッチへの乗算 (scale)。
        /// </summary>
        public unsafe float pitch
        {
            get => _pitch;
            set
            {
                _pitch = value;
                if (HasLiveSource)
                {
                    var r = LibNezia.nezia_source_set_pitch(
                        NeziaEngine.RequireHandle(), _spawnedSource, _pitch * ClipPitch());
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
                    var final = (_mute ? 0f : _volume) * ClipVolume();
                    var r = LibNezia.nezia_source_set_volume(
                        NeziaEngine.RequireHandle(), _spawnedSource, final);
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

        /// <summary>
        /// Doppler 効果の強度。<c>AudioSource.dopplerLevel</c> 互換。
        /// Unity 同様 0〜5 の範囲。ネイティブ側は [0, 1] にクランプされる
        /// （1.0 で物理計算を完全適用、0.0 で無効）。
        /// </summary>
        public unsafe float dopplerLevel
        {
            get => _dopplerLevel;
            set
            {
                _dopplerLevel = Mathf.Clamp(value, 0f, 5f);
                if (HasLiveSource)
                {
                    var r = LibNezia.nezia_source_set_doppler_level(
                        NeziaEngine.RequireHandle(), _spawnedSource, _dopplerLevel);
                    NeziaException.ThrowIfError(r, "set source doppler level");
                }
            }
        }

        /// <summary>
        /// 再生優先度。<c>AudioSource.priority</c> 互換で 0=最高、255=最低 を維持する。
        /// Unity 標準は名目上 0..256 だが、Nezia ネイティブは <c>u8</c> なので
        /// 0..255 を 1 段階ずつ使い切る範囲に統一している。
        /// 範囲外の値は <see cref="Mathf.Clamp(int, int, int)"/> で 0..255 に切り詰められる。
        ///
        /// <para>
        /// ネイティブ層は Wwise / CRI ADX2 互換 (高い値=高優先) に切り替わったため、
        /// 統合層で <c>255 - unity_priority</c> で写像してから FFI に渡す。
        /// </para>
        /// </summary>
        public unsafe int priority
        {
            get => _priority;
            set
            {
                _priority = Mathf.Clamp(value, 0, 255);
                if (HasLiveSource)
                {
                    var r = LibNezia.nezia_source_set_priority(
                        NeziaEngine.RequireHandle(), _spawnedSource, ToNativePriority(_priority));
                    NeziaException.ThrowIfError(r, "set source priority");
                }
            }
        }

        // Unity (低い値=高優先) → ネイティブ Wwise/ADX2 (高い値=高優先) への写像。
        private static byte ToNativePriority(int unityPriority) => (byte)(255 - unityPriority);

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
        /// バスツリー設計用 ScriptableObject（IP-1）。設定されている場合は <see cref="outputBusName"/>
        /// で指定されたバスを優先し、未設定なら <see cref="busMap"/> 経路にフォールバックする。
        /// </summary>
        public NeziaMixerAsset mixerAsset
        {
            get => _mixerAsset;
            set { _mixerAsset = value; ResolveOutputBusFromAsset(); }
        }

        /// <summary>
        /// <see cref="mixerAsset"/> 内で参照するバスの論理名。空文字なら master 直下扱い。
        /// </summary>
        public string outputBusName
        {
            get => _outputBusName;
            set { _outputBusName = value; ResolveOutputBusFromAsset(); }
        }

        private void ResolveOutputBusFromAsset()
        {
            if (_mixerAsset != null && !string.IsNullOrEmpty(_outputBusName))
                outputBus = _mixerAsset.Resolve(_outputBusName);
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
                var asset = ResolvedSound;
                int sr = (asset != null && asset.SampleRate > 0) ? asset.SampleRate : 44100;
                return frames / sr;
            }
            set
            {
                if (!HasLiveSource) return;
                var asset = ResolvedSound;
                int sr = (asset != null && asset.SampleRate > 0) ? asset.SampleRate : 44100;
                var r = LibNezia.nezia_source_seek(
                    NeziaEngine.RequireHandle(), _spawnedSource, value * sr);
                NeziaException.ThrowIfError(r, "seek source");
            }
        }

        /// <summary>クリップ／コンテナを再生する。<c>AudioSource.Play()</c> 互換。</summary>
        public unsafe void Play()
        {
            var asset = ResolvedSound;
            if (asset == null) return;

            // 既存ソースがあれば停止してから新規 spawn する（AudioSource.Play の再起動セマンティクス）
            if (HasLiveSource) StopInternal();

            var engine = NeziaEngine.RequireHandle();

            // ── volume / pitch / loop / bus を Clip-side 既定と合成 ──────────
            //
            // useClipDefaults=true: Source の volume/pitch は Clip 基準値への scale。
            //   loop は Source または Clip の論理和（Source.loop=true なら強制ループ可）。
            //   bus は Source 側設定が優先、未設定なら Clip の outputBus、最後に Master。
            // useClipDefaults=false (legacy): Source の値がそのまま最終値。
            float clipV = _useClipDefaults ? asset.Volume : 1f;
            float clipP = _useClipDefaults ? asset.Pitch : 1f;
            bool effectiveLoop = _useClipDefaults ? (_loop || asset.Loop) : _loop;
            float effectiveVolume = (_mute ? 0f : _volume) * clipV;
            float effectivePitch = _pitch * clipP;

            NeziaEntityId busId;
            if (outputBus.IsValid)
            {
                busId = outputBus.Id;
            }
            else if (_useClipDefaults)
            {
                var clipBus = asset.ResolveOutputBus();
                busId = clipBus.IsValid ? clipBus.Id : LibNezia.nezia_engine_master_bus(engine);
            }
            else
            {
                busId = LibNezia.nezia_engine_master_bus(engine);
            }

            // 自然終了をネイティブから受け取るためのコールバック登録。
            // looping のときは終了通知が発火しないので、コールバック登録自体を省略してよい。
            // Container 経路は FFI が callback 未対応なので登録しない。
            delegate* unmanaged[Cdecl]<void*, void> cb = null;
            void* userData = null;
            if (!effectiveLoop && asset.SupportsFinishCallback)
            {
                _selfHandle = GCHandle.Alloc(this, GCHandleType.Weak);
                userData = (void*)GCHandle.ToIntPtr(_selfHandle);
                cb = (delegate* unmanaged[Cdecl]<void*, void>)s_finishCallbackPtr;
            }

            var src = asset.Spawn(engine, effectiveVolume, effectivePitch, busId, effectiveLoop, cb, userData);
            if (src.index == uint.MaxValue)
            {
                FreeSelfHandle();
                return; // INVALID
            }

            _spawnedSource = src;
            _isPlaying = true;
            _isPaused = false;
            _hasPrevPosition = false;

            if (_useClipDefaults)
            {
                // Clip 側に spatial / doppler / priority / attenuation を委譲する。
                // 戻り値の AttenuationCurve は despawn 時に Destroy する責務をここで引き取る。
                _liveAttenuationCurve = asset.ApplyDefaultsTo(engine, src);
                if (asset.SpatialBlend > 0f) PushPosition();
            }
            else
            {
                // 互換モード: Source 側の値で priority / spatial を設定する従来挙動。
                var prResult = LibNezia.nezia_source_set_priority(
                    engine, src, ToNativePriority(_priority));
                NeziaException.ThrowIfError(prResult, "set source priority");

                if (_spatialBlend > 0f)
                {
                    var r = LibNezia.nezia_source_set_spatial_params(
                        engine, src, _rolloffMode.ToNative(), _minDistance, _maxDistance, 1f);
                    NeziaException.ThrowIfError(r, "set spatial params");

                    r = LibNezia.nezia_source_set_spatial_enabled(engine, src, 1);
                    NeziaException.ThrowIfError(r, "set spatial enabled");

                    r = LibNezia.nezia_source_set_doppler_level(engine, src, _dopplerLevel);
                    NeziaException.ThrowIfError(r, "set source doppler level");

                    if (_attenuationCurve != null)
                    {
                        _liveAttenuationCurve = _attenuationCurve.ToNative();
                        if (_liveAttenuationCurve.IsValid)
                        {
                            var cr = LibNezia.nezia_source_set_attenuation_curve(
                                engine, src, _liveAttenuationCurve.Id);
                            NeziaException.ThrowIfError(cr, "set source attenuation curve");
                        }
                    }

                    PushPosition();
                }
                else
                {
                    var r = LibNezia.nezia_source_set_spatial_enabled(engine, src, 0);
                    NeziaException.ThrowIfError(r, "set spatial disabled");
                }
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

        /// <summary>
        /// 現在再生中のソースにエフェクトを追加する。再生中でない場合は <see cref="InvalidOperationException"/>。
        /// ソース despawn 時にエフェクトもまとめて解放されるため、明示 <see cref="NeziaEffect.Remove"/>
        /// は通常不要。
        /// </summary>
        public unsafe NeziaEffect AddEffect(NeziaEffectKind kind, NeziaEffectPosition position = NeziaEffectPosition.Post)
        {
            if (!HasLiveSource)
                throw new InvalidOperationException("[Nezia] AddEffect requires the source to be playing.");
            var id = LibNezia.nezia_effect_add(
                NeziaEngine.RequireHandle(),
                Native.NeziaEffectTargetKind.Source, _spawnedSource,
                (Native.NeziaEffectKind)(byte)kind,
                (Native.NeziaEffectPosition)(byte)position);
            return new NeziaEffect(id, kind);
        }

        /// <summary>
        /// 現在再生中のソースにカスタム距離減衰カーブを割り当てる。
        /// <c>NeziaAttenuationCurve.Invalid</c> を渡すとカーブを外す（silent fallback）。
        /// </summary>
        public unsafe void SetAttenuationCurve(NeziaAttenuationCurve curve)
        {
            if (!HasLiveSource)
                throw new InvalidOperationException("[Nezia] SetAttenuationCurve requires the source to be playing.");
            var r = LibNezia.nezia_source_set_attenuation_curve(
                NeziaEngine.RequireHandle(), _spawnedSource, curve.Id);
            NeziaException.ThrowIfError(r, "source set attenuation curve");
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
            // MixerAsset 優先、未設定 / 未解決なら BusMap に fallback。
            if (!outputBus.IsValid && _mixerAsset != null && !string.IsNullOrEmpty(_outputBusName))
                outputBus = _mixerAsset.Resolve(_outputBusName);
            if (_busMap != null && _outputAudioMixerGroup != null && !outputBus.IsValid)
                outputBus = _busMap.Resolve(_outputAudioMixerGroup);

            if (_playOnAwake && ResolvedSound != null) Play();
        }

        private void LateUpdate()
        {
            if (!_isPlaying || _isPaused) return;
            // useClipDefaults=true のときは Clip 側 spatial 設定をトリガに使う。
            // false のときは従来どおり Source 側 _spatialBlend を見る。
            bool spatial = _useClipDefaults
                ? (ResolvedSound?.SpatialBlend ?? 0f) > 0f
                : _spatialBlend > 0f;
            if (spatial) PushPositionAndVelocity();
        }

        private void OnEnable()
        {
            // Domain Reload 後など、非シリアライズフィールドが default(0,0) に戻るケースでも
            // 「ライブソース無し」を正しく表すよう、明示的に INVALID へリセット。
            _spawnedSource = InvalidEntityId;
            _isPlaying = false;
            _isPaused = false;
            _hasPrevPosition = false;
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
            DestroyLiveAttenuationCurve();
        }

        private void DestroyLiveAttenuationCurve()
        {
            if (!_liveAttenuationCurve.IsValid) return;
            if (NeziaEngine.IsInitialized) _liveAttenuationCurve.Destroy();
            _liveAttenuationCurve = NeziaAttenuationCurve.Invalid;
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
            DestroyLiveAttenuationCurve();

            if (_destroyOnFinish && this != null) Destroy(gameObject);
        }

        // 速度（Doppler 用）は前フレーム位置との差分を Time.deltaTime で割って自動算出する。
        // ユーザーが明示的に Rigidbody.velocity 等を渡すフックは現状用意していない。
        private Vector3 _prevPosition;
        private bool _hasPrevPosition;

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

        private void PushPositionAndVelocity()
        {
            if (!HasLiveSource) return;
            var p = transform.position;
            EnqueuePendingPosition(new NeziaSourcePositionUpdate
            {
                source = _spawnedSource,
                position = new NeziaVec3 { x = p.x, y = p.y, z = p.z },
            });

            var dt = Time.deltaTime;
            var v = (_hasPrevPosition && dt > 0f) ? (p - _prevPosition) / dt : Vector3.zero;
            _prevPosition = p;
            _hasPrevPosition = true;
            EnqueuePendingVelocity(new NeziaSourceVelocityUpdate
            {
                source = _spawnedSource,
                velocity = new NeziaVec3 { x = v.x, y = v.y, z = v.z },
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
        private static NeziaSourceVelocityUpdate[] s_pendingVelocities = new NeziaSourceVelocityUpdate[64];
        private static int s_pendingVelocityCount;

        private static void EnqueuePendingPosition(NeziaSourcePositionUpdate update)
        {
            if (s_pendingPositionCount == s_pendingPositions.Length)
                Array.Resize(ref s_pendingPositions, s_pendingPositions.Length * 2);
            s_pendingPositions[s_pendingPositionCount++] = update;
        }

        private static void EnqueuePendingVelocity(NeziaSourceVelocityUpdate update)
        {
            if (s_pendingVelocityCount == s_pendingVelocities.Length)
                Array.Resize(ref s_pendingVelocities, s_pendingVelocities.Length * 2);
            s_pendingVelocities[s_pendingVelocityCount++] = update;
        }

        internal static unsafe void FlushPendingPositions()
        {
            if (!NeziaEngine.IsInitialized)
            {
                s_pendingPositionCount = 0;
                s_pendingVelocityCount = 0;
                return;
            }

            if (s_pendingPositionCount > 0)
            {
                fixed (NeziaSourcePositionUpdate* ptr = s_pendingPositions)
                {
                    var r = LibNezia.nezia_source_batch_set_positions(
                        NeziaEngine.RequireHandle(), ptr, (nuint)s_pendingPositionCount);
                    NeziaException.ThrowIfError(r, "batch set source positions");
                }
                s_pendingPositionCount = 0;
            }

            if (s_pendingVelocityCount > 0)
            {
                fixed (NeziaSourceVelocityUpdate* ptr = s_pendingVelocities)
                {
                    var r = LibNezia.nezia_source_batch_set_velocities(
                        NeziaEngine.RequireHandle(), ptr, (nuint)s_pendingVelocityCount);
                    NeziaException.ThrowIfError(r, "batch set source velocities");
                }
                s_pendingVelocityCount = 0;
            }
        }
    }
}
