using System;
using System.Collections.Generic;
using Nezia.Native;
using UnityEngine;

namespace Nezia.Unity
{
    /// <summary>
    /// 「鳴らす対象」を表す抽象アセット基底。
    ///
    /// <para>
    /// <see cref="NeziaAudioClip"/>（単一バッファ再生）と
    /// <see cref="NeziaRandomContainer"/>（ランダム選択再生）を統一的に扱うための基底型。
    /// <see cref="NeziaAudioSource"/> は具体型を意識せずこの型のフィールドだけを持つ。
    /// </para>
    ///
    /// <para>
    /// Wwise / FMOD / CRI ADX における「Sound 抽象 (Single + Container)」と同じ責務を持つ。
    /// 将来 Switch / Sequence Container を足す際もこの基底に差し込む。
    /// </para>
    ///
    /// <para>
    /// IP-4 (Clip-centric authoring) からは「鳴り方を Asset が所有する」モデルに移行し、
    /// 音響デフォルト（volume/pitch/loop/出力バス/spatial 一式/attenuation/doppler/priority）を
    /// この基底に持たせる。<see cref="NeziaAudioSource"/> 側は <c>UseClipDefaults=true</c> のとき
    /// これらの値に委譲し、Source 自身の volume/pitch は Clip 値への scale として扱う。
    /// 旧来挙動が必要な場合は Source 側で <c>UseClipDefaults=false</c> に切り替える。
    /// </para>
    /// </summary>
    public abstract class NeziaSoundAsset : ScriptableObject
    {
        // ─── 音響デフォルト（IP-4 Clip-centric） ─────────────────

        [Header("Acoustic Defaults")]
        [SerializeField, Range(0f, 1f),
         Tooltip("基準音量。Source.volume はこれへの乗算 (scale) として効く (UseClipDefaults=true 時)。")]
        private float _volume = 1f;

        [SerializeField, Range(-3f, 3f),
         Tooltip("基準ピッチ。Source.pitch はこれへの乗算 (scale) として効く (UseClipDefaults=true 時)。")]
        private float _pitch = 1f;

        [SerializeField,
         Tooltip("ループ再生。UseClipDefaults=true の Source からは Clip 値が採用される。")]
        private bool _loop;

        [Header("Routing")]
        [SerializeField,
         Tooltip("出力先 Mixer Asset。Bus は名前で参照する。")]
        private NeziaMixerAsset _outputMixerAsset;

        [SerializeField,
         Tooltip("出力先バス論理名。空文字なら Source 側 / Master へフォールバック。")]
        private string _outputBusName;

        [Header("Spatial")]
        [SerializeField, Range(0f, 1f),
         Tooltip("0=2D / 1=3D。0 のとき以下の距離・Doppler 設定は無効。")]
        private float _spatialBlend;

        [SerializeField, Tooltip("距離減衰の最小距離。")]
        private float _minDistance = 1f;

        [SerializeField, Tooltip("距離減衰の最大距離。")]
        private float _maxDistance = 500f;

        [SerializeField, Tooltip("距離減衰モデル。AttenuationCurve 設定時はそちらが優先。")]
        private NeziaRolloffMode _rolloffMode = NeziaRolloffMode.InverseDistance;

        [SerializeField, Tooltip("カスタム距離減衰カーブ。未設定なら RolloffMode が使われる。")]
        private NeziaAttenuationCurveAsset _attenuationCurve;

        [SerializeField, Range(0f, 5f), Tooltip("Doppler 効果の強度。")]
        private float _dopplerLevel = 1f;

        [Header("Voice")]
        [SerializeField, Range(0, 255),
         Tooltip("再生優先度。0=最高、255=最低 (Unity AudioSource 互換)。")]
        private int _priority = 128;

        // ─── Effect chain（Clip 起点エフェクト） ─────────────────
        //
        // 再生時にこの sound から spawn された source 自身のエフェクトチェーンに挿入される。
        // ソース despawn 時にネイティブ側で自動解放されるため、Apply 経路では追加するだけで良い。

        [SerializeReference,
         Tooltip("再生時に source へ挿入するエフェクトチェーン。宣言順に追加される。")]
        private List<SourceEffect> _effects = new();

        // ─── Aux Send 配線（User-Defined Aux Sends） ─────────────
        //
        // Wwise / FMOD の per-event aux send 互換。同じ Reverb Bus を共有しつつ
        // 音ごとに reverb 量を独立に持たせるのに使う（bus→bus send との対比）。
        // 送り先バスは MixerAsset 経由で名前解決する。

        [SerializeField,
         Tooltip("再生時に張られる Aux Send。送り先は MixerAsset 内のバス名で指定する。")]
        private List<SourceSend> _sends = new();

        // ─── 公開アクセサ ────────────────────────────────────────

        /// <summary>このアセットの長さ（秒）。Container 等は 0 を返してよい。</summary>
        public virtual float Length => 0f;

        /// <summary>サンプルレート (Hz)。0 のときは <c>NeziaAudioSource.time</c> 計算で 44100 が代用される。</summary>
        public virtual int SampleRate => 0;

        /// <summary>基準音量 (0.0〜1.0)。</summary>
        public float Volume => _volume;

        /// <summary>基準ピッチ。</summary>
        public float Pitch => _pitch;

        /// <summary>ループ再生（Asset 側既定）。</summary>
        public bool Loop => _loop;

        /// <summary>出力先 <see cref="NeziaMixerAsset"/>。</summary>
        public NeziaMixerAsset OutputMixerAsset => _outputMixerAsset;

        /// <summary>出力先バス論理名。</summary>
        public string OutputBusName => _outputBusName;

        /// <summary>2D/3D ブレンド。0=2D, 1=3D。</summary>
        public float SpatialBlend => _spatialBlend;

        /// <summary>距離減衰の最小距離。</summary>
        public float MinDistance => _minDistance;

        /// <summary>距離減衰の最大距離。</summary>
        public float MaxDistance => _maxDistance;

        /// <summary>距離減衰モデル。</summary>
        public NeziaRolloffMode RolloffMode => _rolloffMode;

        /// <summary>カスタム距離減衰カーブアセット。</summary>
        public NeziaAttenuationCurveAsset AttenuationCurve => _attenuationCurve;

        /// <summary>Doppler 効果の強度。</summary>
        public float DopplerLevel => _dopplerLevel;

        /// <summary>再生優先度（Unity 表現: 0=最高, 255=最低）。</summary>
        public int Priority => _priority;

        /// <summary>Clip 起点エフェクトチェーン宣言の読み取り専用ビュー。</summary>
        public IReadOnlyList<SourceEffect> Effects => _effects;

        /// <summary>Aux Send 宣言の読み取り専用ビュー。</summary>
        public IReadOnlyList<SourceSend> Sends => _sends;

        // ─── Effect / Send 宣言型（nested） ───────────────────────
        //
        // NeziaMixerAsset.BusEffect とほぼ同じ shape を持つが、source 起点 vs bus 起点で
        // 物理的な targetKind が異なるため別系統で保持する。共通化は将来 PR で検討。

        /// <summary>
        /// この sound の再生時に source へ挿入されるエフェクト宣言ベース型。
        /// <c>[SerializeReference]</c> で多態シリアライズされる。
        /// </summary>
        [Serializable]
        public abstract class SourceEffect
        {
            [Tooltip("Source 上の Pre = Pre-Spatial / Post = Post-Spatial。")]
            public NeziaEffectPosition position = NeziaEffectPosition.Post;

            [Tooltip("初期 enabled。false で挿入後に即 disable する。")]
            public bool enabled = true;

            public abstract NeziaEffectKind Kind { get; }
            internal abstract void ApplyInitial(NeziaEffect effect);
        }

        [Serializable]
        public sealed class LowPass : SourceEffect
        {
            [Range(20f, 20000f)] public float cutoff = 1000f;
            [Range(0.1f, 10f)] public float q = 0.7071f;
            public override NeziaEffectKind Kind => NeziaEffectKind.LowPass;
            internal override void ApplyInitial(NeziaEffect effect)
            {
                var v = effect.AsLowPass();
                v.Cutoff = cutoff;
                v.Q = q;
            }
        }

        [Serializable]
        public sealed class HighPass : SourceEffect
        {
            [Range(20f, 20000f)] public float cutoff = 200f;
            [Range(0.1f, 10f)] public float q = 0.7071f;
            public override NeziaEffectKind Kind => NeziaEffectKind.HighPass;
            internal override void ApplyInitial(NeziaEffect effect)
            {
                var v = effect.AsHighPass();
                v.Cutoff = cutoff;
                v.Q = q;
            }
        }

        [Serializable]
        public sealed class Reverb : SourceEffect
        {
            [Range(0f, 1f)] public float roomSize = 0.5f;
            [Range(0f, 1f)] public float damping = 0.5f;
            [Range(0f, 1f)] public float wet = 0.33f;
            [Range(0f, 1f)] public float dry = 0.7f;
            [Range(0f, 1f)] public float width = 1f;
            public override NeziaEffectKind Kind => NeziaEffectKind.Reverb;
            internal override void ApplyInitial(NeziaEffect effect)
            {
                var v = effect.AsReverb();
                v.RoomSize = roomSize;
                v.Damping = damping;
                v.Wet = wet;
                v.Dry = dry;
                v.Width = width;
            }
        }

        [Serializable]
        public sealed class Compressor : SourceEffect
        {
            public float thresholdDb = -20f;
            public float ratio = 4f;
            public float attackMs = 10f;
            public float releaseMs = 100f;
            public float kneeDb = 6f;
            public float makeupDb = 0f;
            public override NeziaEffectKind Kind => NeziaEffectKind.Compressor;
            internal override void ApplyInitial(NeziaEffect effect)
            {
                var v = effect.AsCompressor();
                v.ThresholdDb = thresholdDb;
                v.Ratio = ratio;
                v.AttackMs = attackMs;
                v.ReleaseMs = releaseMs;
                v.KneeDb = kneeDb;
                v.MakeupDb = makeupDb;
            }
        }

        /// <summary>
        /// Aux Send 1 本の宣言。<see cref="target"/> = <see cref="NeziaMixerAsset.SendTargetKind.Bus"/>
        /// なら通常の source→bus、<see cref="NeziaMixerAsset.SendTargetKind.CompressorSidechain"/> なら
        /// <see cref="targetBus"/> に挿入された <see cref="targetEffectIndex"/> 番目の Compressor の
        /// sidechain 入力へ流す。
        /// </summary>
        [Serializable]
        public sealed class SourceSend
        {
            [Tooltip("送り先 Mixer Asset。空なら SoundAsset の OutputMixerAsset が使われる。")]
            public NeziaMixerAsset mixerAsset;

            [Tooltip("送り先バス名。Mixer Asset 内で解決される。")]
            public string targetBus;

            [Tooltip("Bus = 通常の source→bus / CompressorSidechain = 同バスの Compressor へ送る。")]
            public NeziaMixerAsset.SendTargetKind target = NeziaMixerAsset.SendTargetKind.Bus;

            [Tooltip("CompressorSidechain 時に参照される、targetBus のエフェクトチェーン上のインデックス。")]
            public int targetEffectIndex;

            [Tooltip("Pre = フェーダー前 / Post = フェーダー後。")]
            public NeziaSendPosition position = NeziaSendPosition.Post;

            [Range(0f, 4f), Tooltip("Send ゲイン。1.0 = 0dB。")]
            public float gain = 1f;
        }

        // ─── ランタイム経路 ──────────────────────────────────────

        /// <summary>
        /// この sound 経路でハンドル付きソースを spawn する。失敗時は INVALID。
        ///
        /// <para>
        /// <see cref="NeziaAudioSource"/> 内部用。具象側で
        /// <c>nezia_source_play_with_handle</c> または
        /// <c>nezia_container_play_with_handle</c> に分岐する。
        /// Container 経路では現状 <paramref name="callback"/> は未対応で無視される
        /// （natural finish の通知は受け取れず、<see cref="NeziaAudioSource"/> 側は
        /// alive ポーリングまたは明示 Stop に依存する）。
        /// </para>
        ///
        /// <para>
        /// ここで渡す <paramref name="volume"/> / <paramref name="pitch"/> /
        /// <paramref name="bus"/> / <paramref name="looping"/> は <see cref="NeziaAudioSource"/>
        /// 側で Clip-side デフォルトと合成済みの「最終値」。spatial / doppler / priority /
        /// attenuation 系は spawn 後に <see cref="ApplyDefaultsTo"/> 経由で push される。
        /// </para>
        /// </summary>
        internal abstract unsafe NeziaEntityId Spawn(
            Nezia.Native.NeziaEngine* engine,
            float volume, float pitch,
            NeziaEntityId bus, bool looping,
            delegate* unmanaged[Cdecl]<void*, void> callback, void* userData);

        /// <summary>
        /// Container 経路かどうか。<see cref="NeziaAudioSource"/> 側で
        /// natural-finish callback の登録可否判定に使う。
        /// </summary>
        internal virtual bool SupportsFinishCallback => true;

        /// <summary>
        /// この Asset の出力バスを <see cref="OutputMixerAsset"/> から解決する。
        /// 未設定または解決失敗時は <see cref="NeziaBus.Invalid"/>。
        /// </summary>
        public NeziaBus ResolveOutputBus()
        {
            if (string.IsNullOrEmpty(_outputBusName)) return NeziaBus.Invalid;
            // 明示指定 mixer が無ければ Project Settings の default mixer にフォールバックする。
            var asset = _outputMixerAsset != null ? _outputMixerAsset : NeziaSettings.Instance?.DefaultMixer;
            return asset != null ? asset.Resolve(_outputBusName) : NeziaBus.Invalid;
        }

        /// <summary>
        /// Spawn 直後の source に音響パラメータ（priority / spatial / doppler / attenuation）を適用する。
        ///
        /// <para>
        /// 各引数は呼び出し側で「Clip 値か Source override 値か」を per-property に選択した
        /// <em>effective 値</em>。これにより同じ FFI 列を Source-side override の混在パターンでも再利用できる。
        /// volume / pitch / bus / loop は <see cref="Spawn"/> の引数経路で渡し済みなのでここでは扱わない。
        /// </para>
        ///
        /// <para>
        /// 戻り値は spatial=ON 時に確保された <see cref="NeziaAttenuationCurve"/>。Source の despawn 時に
        /// <c>Destroy()</c> する責務は呼び出し側。spatial=OFF や curve 未設定の場合は
        /// <see cref="NeziaAttenuationCurve.Invalid"/> を返す。
        /// </para>
        /// </summary>
        internal static unsafe NeziaAttenuationCurve ApplyAcousticsTo(
            Nezia.Native.NeziaEngine* engine,
            NeziaEntityId source,
            int priority,
            float spatialBlend,
            float minDistance,
            float maxDistance,
            NeziaRolloffMode rolloffMode,
            NeziaAttenuationCurveAsset attenuationCurve,
            float dopplerLevel)
        {
            // Priority は Unity 表現 (0=最高) → ネイティブ表現 (高い値=高優先) で反転。
            var pr = LibNezia.nezia_source_set_priority(
                engine, source, (byte)(255 - Mathf.Clamp(priority, 0, 255)));
            NeziaException.ThrowIfError(pr, "set source priority");

            var liveCurve = NeziaAttenuationCurve.Invalid;
            if (spatialBlend > 0f)
            {
                var r = LibNezia.nezia_source_set_spatial_params(
                    engine, source, rolloffMode.ToNative(), minDistance, maxDistance, 1f);
                NeziaException.ThrowIfError(r, "set spatial params");

                r = LibNezia.nezia_source_set_spatial_enabled(engine, source, 1);
                NeziaException.ThrowIfError(r, "set spatial enabled");

                r = LibNezia.nezia_source_set_doppler_level(engine, source, dopplerLevel);
                NeziaException.ThrowIfError(r, "set source doppler level");

                if (attenuationCurve != null)
                {
                    liveCurve = attenuationCurve.ToNative();
                    if (liveCurve.IsValid)
                    {
                        var cr = LibNezia.nezia_source_set_attenuation_curve(
                            engine, source, liveCurve.Id);
                        NeziaException.ThrowIfError(cr, "set source attenuation curve");
                    }
                }
            }
            else
            {
                var r = LibNezia.nezia_source_set_spatial_enabled(engine, source, 0);
                NeziaException.ThrowIfError(r, "set spatial disabled");
            }

            return liveCurve;
        }

        /// <summary>
        /// Spawn 直後の source に Asset 側のエフェクトチェーンと Aux Send を適用する。
        /// effects / sends は Clip 固有の設定であり、Source-side override の対象ではない。
        /// ソース despawn 時にネイティブ側で自動解放されるため、追加するだけで管理不要。
        /// </summary>
        internal unsafe void ApplyEffectsAndSendsTo(
            Nezia.Native.NeziaEngine* engine, NeziaEntityId source)
        {
            // ── Effect chain（source-target effect_add） ─────────────
            if (_effects != null)
            {
                for (int i = 0; i < _effects.Count; i++)
                {
                    var spec = _effects[i];
                    if (spec == null) continue;
                    var fxId = LibNezia.nezia_effect_add(
                        engine,
                        Native.NeziaEffectTargetKind.Source,
                        source,
                        (Native.NeziaEffectKind)(byte)spec.Kind,
                        (Native.NeziaEffectPosition)(byte)spec.position);
                    var fx = new NeziaEffect(fxId, spec.Kind);
                    if (!fx.IsValid) continue;
                    spec.ApplyInitial(fx);
                    if (!spec.enabled) fx.Enabled = false;
                }
            }

            // ── Aux Sends（source-target send） ─────────────────────
            //
            // 送り先 Mixer Asset は per-send 設定があればそれ、無ければ outputMixerAsset を使う。
            // 解決失敗 (asset null / バス未存在 / 不正な sidechain target) は silent skip。
            if (_sends != null)
            {
                for (int i = 0; i < _sends.Count; i++)
                {
                    var spec = _sends[i];
                    if (spec == null || string.IsNullOrEmpty(spec.targetBus)) continue;
                    var asset = spec.mixerAsset != null ? spec.mixerAsset : _outputMixerAsset;
                    if (asset == null) continue;
                    var dstBus = asset.Resolve(spec.targetBus);
                    if (!dstBus.IsValid) continue;

                    if (spec.target == NeziaMixerAsset.SendTargetKind.CompressorSidechain)
                    {
                        var fx = asset.ResolveEffect(spec.targetBus, spec.targetEffectIndex);
                        if (!fx.IsValid || fx.Kind != NeziaEffectKind.Compressor) continue;
                        NeziaSend.AddSourceToCompressor(source, fx, spec.position, spec.gain);
                    }
                    else
                    {
                        NeziaSend.AddSourceToBus(source, dstBus, spec.position, spec.gain);
                    }
                }
            }
        }

        /// <summary>
        /// すべて Asset 側の値で <see cref="ApplyAcousticsTo"/> + <see cref="ApplyEffectsAndSendsTo"/> を実行する
        /// 便宜メソッド。<see cref="NeziaAudioSource"/> 側で per-property override が無い場合にこれを呼ぶ。
        /// </summary>
        internal unsafe NeziaAttenuationCurve ApplyDefaultsTo(
            Nezia.Native.NeziaEngine* engine, NeziaEntityId source)
        {
            var curve = ApplyAcousticsTo(
                engine, source,
                _priority, _spatialBlend, _minDistance, _maxDistance,
                _rolloffMode, _attenuationCurve, _dopplerLevel);
            ApplyEffectsAndSendsTo(engine, source);
            return curve;
        }
    }
}
