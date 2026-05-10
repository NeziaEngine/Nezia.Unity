using System;
using System.Collections.Generic;
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
        // - loop / outputBus / spatial / attenuation / doppler / priority は per-property の
        //   `_overrideXxx` フラグで Clip 値か Source 値かを選択する
        // 既存プレハブでの破壊的変更を避けるため既定は false。新規 NeziaAudioSource では
        // 推奨値 true。マイグレーションコマンドは PR-C で提供予定。
        [SerializeField,
         Tooltip("ON: 音響設定を Clip (NeziaSoundAsset) に委譲し、Source.volume/pitch は scale として効く。" +
                 "OFF (互換モード): Source 側の値が直接最終値になる従来挙動。")]
        private bool _useClipDefaults;

        // ─── Per-property override（IP-4 PR-B） ──────────────────
        //
        // useClipDefaults=true のとき Source プロパティを Clip 値の上から override するか決めるフラグ群。
        // 既定はすべて false = Clip 値を採用。Source の対応 setter (e.g. `source.spatialBlend = 1f`) を
        // 呼ぶと暗黙に true に切り替わるため、従来のスクリプト記述で違和感なく override できる。
        // useClipDefaults=false (legacy) のときは互換挙動が支配するためフラグは無視される。
        [SerializeField, Tooltip("Source.outputBus を Clip より優先する。")]
        private bool _overrideOutputBus;
        [SerializeField, Tooltip("Source の spatialBlend / 距離 / rolloff を Clip より優先する。")]
        private bool _overrideSpatial;
        [SerializeField, Tooltip("Source.attenuationCurve を Clip より優先する。")]
        private bool _overrideAttenuation;
        [SerializeField, Tooltip("Source.dopplerLevel を Clip より優先する。")]
        private bool _overrideDoppler;
        [SerializeField, Tooltip("Source.priority を Clip より優先する。")]
        private bool _overridePriority;
        [SerializeField, Tooltip("Source.loop を Clip より優先する。")]
        private bool _overrideLoop;

        // ─── ランタイム状態 ──────────────────────────────────────

        // ネイティブ側の INVALID は { index: u32::MAX, generation: 0 }。
        // (0, 0) は最初に確保された有効 ID なので INVALID 判定に使ってはいけない。
        private static readonly NeziaEntityId InvalidEntityId =
            new NeziaEntityId { index = uint.MaxValue, generation = 0 };

        private NeziaEntityId _spawnedSource = InvalidEntityId;
        private bool _isPlaying;
        private bool _isPaused;

        // ネイティブ完了コールバックから自分自身を辿るためのトークン。
        // GCHandle を直接 userData に渡すと、Free 後に slot が再利用された際に
        // 過去 enqueue 済みイベントが新しい "different domain" の Target を指してしまう
        // (ManagedThreadId が変わって発火元の domain と一致しない) 問題があるため、
        // ここでは long トークンを userData に渡し、辞書経由で GCHandle を引く。
        // Stop 時は辞書から token を消すだけ。stale event は Remove が false で silent return。
        private long _finishToken;

        private static readonly object s_finishTokensLock = new object();
        private static long s_nextFinishToken = 1;
        private static readonly Dictionary<long, GCHandle> s_finishTokens =
            new Dictionary<long, GCHandle>();

        // Inspector で AnimationCurve として編集された減衰カーブを再生中だけネイティブ確保しておく。
        // Play で生成 → Stop / 自然終了 / Disable で Destroy する。
        private NeziaAttenuationCurve _liveAttenuationCurve = NeziaAttenuationCurve.Invalid;

        // NeziaSpatialUpdater 上での自分の index。-1 = 未登録。
        // spatial > 0 で Play 中のときだけ登録され、Stop / 自然終了 / Disable で解除される。
        // updater 側 swap-back で index が変わると NotifySpatialIndexChanged が呼ばれる。
        private int _spatialIndex = -1;

        private bool HasLiveSource => _spawnedSource.index != uint.MaxValue;

        /// <summary>カスタム距離減衰カーブのアセット。未設定なら <see cref="rolloffMode"/> が使われる。</summary>
        public NeziaAttenuationCurveAsset attenuationCurve
        {
            get => _attenuationCurve;
            set { _attenuationCurve = value; _overrideAttenuation = true; }
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
                _overrideLoop = true;
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
        public float spatialBlend
        {
            get => _spatialBlend;
            set { _spatialBlend = Mathf.Clamp01(value); _overrideSpatial = true; }
        }

        /// <summary>距離減衰の最小距離。</summary>
        public float minDistance
        {
            get => _minDistance;
            set { _minDistance = value; _overrideSpatial = true; }
        }

        /// <summary>距離減衰の最大距離。</summary>
        public float maxDistance
        {
            get => _maxDistance;
            set { _maxDistance = value; _overrideSpatial = true; }
        }

        /// <summary>距離減衰モデル。</summary>
        public NeziaRolloffMode rolloffMode
        {
            get => _rolloffMode;
            set { _rolloffMode = value; _overrideSpatial = true; }
        }

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
                _overrideDoppler = true;
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
                _overridePriority = true;
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
        /// 代入時に override flag が立つので、useClipDefaults=true でも Source 側が優先される。
        /// </summary>
        private NeziaBus _outputBusValue = NeziaBus.Invalid;
        public NeziaBus outputBus
        {
            get => _outputBusValue;
            set { _outputBusValue = value; _overrideOutputBus = true; }
        }

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
            // useClipDefaults=true: Source の volume/pitch は常に Clip 基準値への scale (override 無し)。
            //   loop / bus は per-property override flag が true なら Source 値、false なら Clip 値を採用。
            // useClipDefaults=false (legacy): Source の値がそのまま最終値。
            float clipV = _useClipDefaults ? asset.Volume : 1f;
            float clipP = _useClipDefaults ? asset.Pitch : 1f;
            float effectiveVolume = (_mute ? 0f : _volume) * clipV;
            float effectivePitch = _pitch * clipP;

            bool effectiveLoop;
            if (!_useClipDefaults) effectiveLoop = _loop;
            else effectiveLoop = _overrideLoop ? _loop : asset.Loop;

            NeziaEntityId busId;
            if (!_useClipDefaults)
            {
                busId = outputBus.IsValid
                    ? outputBus.Id
                    : LibNezia.nezia_engine_master_bus(engine);
            }
            else if (_overrideOutputBus && outputBus.IsValid)
            {
                busId = outputBus.Id;
            }
            else
            {
                var clipBus = asset.ResolveOutputBus();
                busId = clipBus.IsValid ? clipBus.Id : LibNezia.nezia_engine_master_bus(engine);
            }

            // 自然終了をネイティブから受け取るためのコールバック登録。
            // looping のときは終了通知が発火しないので、コールバック登録自体を省略してよい。
            // Container 経路は FFI が callback 未対応なので登録しない。
            delegate* unmanaged[Cdecl]<void*, void> cb = null;
            void* userData = null;
            if (!effectiveLoop && asset.SupportsFinishCallback)
            {
                var handle = GCHandle.Alloc(this, GCHandleType.Weak);
                long token;
                lock (s_finishTokensLock)
                {
                    token = s_nextFinishToken++;
                    s_finishTokens[token] = handle;
                }
                _finishToken = token;
                userData = (void*)(IntPtr)token;
                cb = (delegate* unmanaged[Cdecl]<void*, void>)s_finishCallbackPtr;
            }

            // ── spawn 同梱 priority / spatial を組み立て ────────────
            //
            // useClipDefaults: per-property override で Source 値 / Clip 値を選ぶ。
            // legacy: Source 側の値をそのまま使う。
            int effPriority;
            float effSpatialBlend;
            float effMinDistance;
            float effMaxDistance;
            NeziaRolloffMode effRolloff;
            float effDoppler;
            NeziaAttenuationCurveAsset effCurve;
            if (_useClipDefaults)
            {
                effPriority = _overridePriority ? _priority : asset.Priority;
                effSpatialBlend = _overrideSpatial ? _spatialBlend : asset.SpatialBlend;
                effMinDistance = _overrideSpatial ? _minDistance : asset.MinDistance;
                effMaxDistance = _overrideSpatial ? _maxDistance : asset.MaxDistance;
                effRolloff = _overrideSpatial ? _rolloffMode : asset.RolloffMode;
                effDoppler = _overrideDoppler ? _dopplerLevel : asset.DopplerLevel;
                effCurve = _overrideAttenuation ? _attenuationCurve : asset.AttenuationCurve;
            }
            else
            {
                effPriority = _priority;
                effSpatialBlend = _spatialBlend;
                effMinDistance = _minDistance;
                effMaxDistance = _maxDistance;
                effRolloff = _rolloffMode;
                effDoppler = _dopplerLevel;
                effCurve = _attenuationCurve;
            }

            byte nativePriority = NeziaSoundAsset.ToNativePriority(effPriority);
            NeziaAttenuationCurve liveCurve;
            var spatialInit = NeziaSoundAsset.BuildSpawnSpatialInit(
                effSpatialBlend, effMinDistance, effMaxDistance,
                effRolloff, effCurve, effDoppler, out liveCurve);

            var src = asset.Spawn(
                engine, effectiveVolume, effectivePitch, busId, effectiveLoop,
                nativePriority, spatialInit, cb, userData);
            if (src.index == uint.MaxValue)
            {
                // SPSC リング満杯または MAX_SOURCES 到達。確保済み curve を解放してから return。
                if (liveCurve.IsValid) liveCurve.Destroy();
                FreeFinishToken();
                return; // INVALID
            }

            _spawnedSource = src;
            _isPlaying = true;
            _isPaused = false;
            _liveAttenuationCurve = liveCurve;

            if (asset.SpawnAcousticsBundled)
            {
                // priority / spatial / doppler / curve は spawn コマンドに同梱済み。
                // effects / sends と 3D 位置のみここで push する。
                if (_useClipDefaults)
                    asset.ApplyEffectsAndSendsTo(engine, src);
                if (effSpatialBlend > 0f) PushPosition();
            }
            else
            {
                // Container 経路: FFI が同梱未対応のため従来通り個別 push。
                var prResult = LibNezia.nezia_source_set_priority(engine, src, nativePriority);
                NeziaException.ThrowIfError(prResult, "set source priority");

                if (effSpatialBlend > 0f)
                {
                    var r = LibNezia.nezia_source_set_spatial_params(
                        engine, src, effRolloff.ToNative(), effMinDistance, effMaxDistance, 1f);
                    NeziaException.ThrowIfError(r, "set spatial params");

                    r = LibNezia.nezia_source_set_spatial_enabled(engine, src, 1);
                    NeziaException.ThrowIfError(r, "set spatial enabled");

                    r = LibNezia.nezia_source_set_doppler_level(engine, src, effDoppler);
                    NeziaException.ThrowIfError(r, "set source doppler level");

                    if (liveCurve.IsValid)
                    {
                        var cr = LibNezia.nezia_source_set_attenuation_curve(
                            engine, src, liveCurve.Id);
                        NeziaException.ThrowIfError(cr, "set source attenuation curve");
                    }

                    if (_useClipDefaults) asset.ApplyEffectsAndSendsTo(engine, src);
                    PushPosition();
                }
                else
                {
                    var r = LibNezia.nezia_source_set_spatial_enabled(engine, src, 0);
                    NeziaException.ThrowIfError(r, "set spatial disabled");
                    if (_useClipDefaults) asset.ApplyEffectsAndSendsTo(engine, src);
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

        /// <summary>
        /// 複数のソースを 1 回の呼び出しで停止する（バルク停止）。
        /// 個別 <see cref="Stop"/> を N 回呼ぶと SPSC コマンドリングが詰まりやすいため、
        /// ステージ終端などで多数のボイスを止めるときに使う。内部では
        /// <c>nezia_source_stop_many</c> 経由で最大 32 ID／コマンドに束ねて enqueue される。
        ///
        /// <para>
        /// 全ボイスを止める用途なら <see cref="NeziaEngine.StopAll"/> の方が安価。
        /// コマンドリング満杯で一部しか enqueue できなかった場合、enqueue 成功分だけ
        /// ローカル状態を畳み、残りは <see cref="HasLiveSource"/> のまま戻る。呼び出し側は
        /// 戻り値で件数を確認し、未処理ぶんを次フレーム以降に再送する想定。
        /// </para>
        /// </summary>
        /// <returns>実際に停止リクエストを enqueue できたソース数。</returns>
        public static unsafe int StopMany(IReadOnlyList<NeziaAudioSource> sources)
        {
            if (sources == null || sources.Count == 0) return 0;

            // エンジン破棄後（Domain Reload / アプリ終了）はネイティブを叩かず、
            // 単発 Stop と同じくローカル状態だけ畳む。
            if (!NeziaEngine.IsInitialized)
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    var s = sources[i];
                    if (s != null && s.HasLiveSource) s.ClearLocalAfterStop();
                }
                return 0;
            }

            int n = sources.Count;
            var live = new NeziaAudioSource[n];
            var ids = new NeziaEntityId[n];
            int liveCount = 0;
            for (int i = 0; i < n; i++)
            {
                var s = sources[i];
                if (s == null || !s.HasLiveSource) continue;
                live[liveCount] = s;
                ids[liveCount] = s._spawnedSource;
                liveCount++;
            }
            if (liveCount == 0) return 0;

            nuint processed = 0;
            fixed (NeziaEntityId* ptr = ids)
            {
                var r = LibNezia.nezia_source_stop_many(
                    NeziaEngine.RequireHandle(), ptr, (nuint)liveCount, &processed);
                // QueueFull は部分成功（processed < liveCount）。それ以外の失敗は例外化。
                if (r != NeziaResult.Ok && r != NeziaResult.QueueFull)
                    NeziaException.ThrowIfError(r, "stop many sources");
            }

            int processedInt = (int)processed;
            for (int i = 0; i < processedInt; i++)
                live[i].ClearLocalAfterStop();
            return processedInt;
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
            // _mixerAsset が未指定でも _outputBusName が入っていれば Project Settings の
            // default mixer を試す（NeziaSettings 経由）。
            if (!outputBus.IsValid && !string.IsNullOrEmpty(_outputBusName))
            {
                var asset = _mixerAsset != null ? _mixerAsset : NeziaSettings.Instance?.DefaultMixer;
                if (asset != null) outputBus = asset.Resolve(_outputBusName);
            }
            if (_busMap != null && _outputAudioMixerGroup != null && !outputBus.IsValid)
                outputBus = _busMap.Resolve(_outputAudioMixerGroup);

            if (_playOnAwake && ResolvedSound != null) Play();
        }

        // 位置 / 速度更新は NeziaSpatialUpdater が TransformAccessArray + Burst Job で
        // 一括処理するため、各 Source 側で LateUpdate を持たない（per-MB ディスパッチを排除）。

        private void OnEnable()
        {
            // Domain Reload 後など、非シリアライズフィールドが default(0,0) に戻るケースでも
            // 「ライブソース無し」を正しく表すよう、明示的に INVALID へリセット。
            _spawnedSource = InvalidEntityId;
            _isPlaying = false;
            _isPaused = false;
            _spatialIndex = -1;
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
            ClearLocalAfterStop();
        }

        // 明示 Stop の事後処理。ネイティブ側は呼び出し済み前提で、
        // ローカル状態を畳んで finish token / live curve / spatial 登録を解放する。
        // 明示 Stop の場合は SourceFinished が発火しないため finish token はここで Free。
        private void ClearLocalAfterStop()
        {
            _spawnedSource = InvalidEntityId;
            _isPlaying = false;
            _isPaused = false;
            UnregisterSpatial();
            FreeFinishToken();
            DestroyLiveAttenuationCurve();
        }

        private void UnregisterSpatial()
        {
            if (_spatialIndex < 0) return;
            NeziaSpatialUpdater.Unregister(_spatialIndex);
            _spatialIndex = -1;
        }

        // NeziaSpatialUpdater の swap-back で自分の index が変わった際の通知。
        internal void NotifySpatialIndexChanged(int newIndex) => _spatialIndex = newIndex;

        private void DestroyLiveAttenuationCurve()
        {
            if (!_liveAttenuationCurve.IsValid) return;
            if (NeziaEngine.IsInitialized) _liveAttenuationCurve.Destroy();
            _liveAttenuationCurve = NeziaAttenuationCurve.Invalid;
        }

        private void FreeFinishToken()
        {
            if (_finishToken == 0) return;
            GCHandle h = default;
            bool found;
            lock (s_finishTokensLock)
            {
                found = s_finishTokens.Remove(_finishToken, out h);
            }
            if (found && h.IsAllocated) h.Free();
            _finishToken = 0;
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
            long token = userData.ToInt64();
            GCHandle handle;
            bool found;
            lock (s_finishTokensLock)
            {
                found = s_finishTokens.Remove(token, out handle);
            }
            // 既に Stop 済み or stale event (GCHandle slot 再利用後の遅延発火) は silent return。
            if (!found) return;
            try
            {
                if (handle.Target is NeziaAudioSource src) src.OnNaturallyFinished();
            }
            finally
            {
                if (handle.IsAllocated) handle.Free();
            }
        }

        private void OnNaturallyFinished()
        {
            // 既に Stop 済みのときは何もしない（Stop 経由で token は辞書から remove 済み）。
            if (!HasLiveSource) return;
            _spawnedSource = InvalidEntityId;
            _isPlaying = false;
            _isPaused = false;
            // token は呼び出し元 (OnNativeFinishedStatic) が辞書から remove 済み。
            _finishToken = 0;
            UnregisterSpatial();
            DestroyLiveAttenuationCurve();

            if (_destroyOnFinish && this != null) Destroy(gameObject);
        }

        // 位置 / 速度は NeziaSpatialUpdater 側の Job で transform.position から直接読む。
        // Spawn 直後の初回登録のみここで行い、以降は updater が毎フレーム並列に処理する。
        private void PushPosition()
        {
            if (!HasLiveSource) return;
            if (_spatialIndex >= 0) return; // 二重登録防止
            _spatialIndex = NeziaSpatialUpdater.Register(this, transform, _spawnedSource);
        }
    }
}
