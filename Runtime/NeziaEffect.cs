using System;
using Nezia.Native;

namespace Nezia.Unity
{
    /// <summary>
    /// バスまたはソースのエフェクトチェーンに挿入する DSP エフェクト種別。
    /// </summary>
    public enum NeziaEffectKind : byte
    {
        /// <summary>低域通過フィルタ。param 0=Cutoff(Hz), 1=Q。</summary>
        LowPass = 0,
        /// <summary>高域通過フィルタ。param 0=Cutoff(Hz), 1=Q。</summary>
        HighPass = 1,
        /// <summary>リバーブ。param 0=RoomSize, 1=Damping, 2=Wet, 3=Dry, 4=Width。</summary>
        Reverb = 2,
        /// <summary>コンプレッサ。sidechain 入力は <see cref="NeziaSend.AddBusToCompressor"/> で駆動可能。</summary>
        Compressor = 3,
    }

    /// <summary>
    /// エフェクトの挿入位置。Bus では Pre/Post-Fader、Source では Pre/Post-Spatial を意味する。
    /// </summary>
    public enum NeziaEffectPosition : byte
    {
        Pre = 0,
        Post = 1,
    }

    /// <summary>
    /// バスまたはソースに挿入された DSP エフェクトへのハンドル。
    ///
    /// <para>
    /// <see cref="NeziaBus.AddEffect"/> または <see cref="NeziaAudioSource.AddEffect"/> で生成し、
    /// <see cref="Remove"/> または対象（Bus / Source）の破棄で解放される。
    /// </para>
    /// </summary>
    public readonly struct NeziaEffect : IEquatable<NeziaEffect>
    {
        internal readonly NeziaEntityId Id;
        public readonly NeziaEffectKind Kind;

        internal NeziaEffect(NeziaEntityId id, NeziaEffectKind kind) { Id = id; Kind = kind; }

        public bool IsValid => Id.index != uint.MaxValue;
        public static NeziaEffect Invalid => new NeziaEffect(new NeziaEntityId { index = uint.MaxValue, generation = 0 }, default);

        /// <summary>このエフェクトの enabled をトグルする。</summary>
        public unsafe bool Enabled
        {
            set
            {
                var r = LibNezia.nezia_effect_set_enabled(NeziaEngine.RequireHandle(), Id, value ? (byte)1 : (byte)0);
                NeziaException.ThrowIfError(r, "effect set enabled");
            }
        }

        /// <summary>
        /// パラメータを設定する。<paramref name="param"/> の意味は <see cref="NeziaEffectKind"/> 参照。
        ///
        /// <para>
        /// 通常は <see cref="AsLowPass"/> / <see cref="AsHighPass"/> / <see cref="AsReverb"/> /
        /// <see cref="AsCompressor"/> 経由の名前付きプロパティを使うこと。
        /// この生 API は internal/互換用に残されている。
        /// </para>
        /// </summary>
        [Obsolete("Prefer typed wrappers via AsLowPass()/AsHighPass()/AsReverb()/AsCompressor(). This raw API will become internal in a future release.")]
        public unsafe void SetParam(byte param, float value)
        {
            SetParamUnchecked(param, value);
        }

        internal unsafe void SetParamUnchecked(byte param, float value)
        {
            var r = LibNezia.nezia_effect_set_param(NeziaEngine.RequireHandle(), Id, param, value);
            NeziaException.ThrowIfError(r, "effect set param");
        }

        /// <summary>
        /// LowPass 用の型安全ラッパ。<see cref="Kind"/> が <see cref="NeziaEffectKind.LowPass"/>
        /// でない場合は <see cref="InvalidOperationException"/> を投げる。
        /// </summary>
        public NeziaLowPassEffect AsLowPass()
        {
            EnsureKind(NeziaEffectKind.LowPass);
            return new NeziaLowPassEffect(this);
        }

        /// <summary>
        /// HighPass 用の型安全ラッパ。
        /// </summary>
        public NeziaHighPassEffect AsHighPass()
        {
            EnsureKind(NeziaEffectKind.HighPass);
            return new NeziaHighPassEffect(this);
        }

        /// <summary>
        /// Reverb 用の型安全ラッパ。
        /// </summary>
        public NeziaReverbEffect AsReverb()
        {
            EnsureKind(NeziaEffectKind.Reverb);
            return new NeziaReverbEffect(this);
        }

        /// <summary>
        /// Compressor 用の型安全ラッパ。
        /// </summary>
        public NeziaCompressorEffect AsCompressor()
        {
            EnsureKind(NeziaEffectKind.Compressor);
            return new NeziaCompressorEffect(this);
        }

        private void EnsureKind(NeziaEffectKind expected)
        {
            if (Kind != expected)
                throw new InvalidOperationException($"[Nezia] Effect kind mismatch: expected {expected}, but is {Kind}.");
        }

        /// <summary>このエフェクトをチェーンから取り外して破棄する。</summary>
        public unsafe void Remove()
        {
            if (!IsValid) return;
            var r = LibNezia.nezia_effect_remove(NeziaEngine.RequireHandle(), Id);
            NeziaException.ThrowIfError(r, "effect remove");
        }

        public bool Equals(NeziaEffect other) => Id.index == other.Id.index && Id.generation == other.Id.generation;
        public override bool Equals(object obj) => obj is NeziaEffect e && Equals(e);
        public override int GetHashCode() => unchecked((int)(Id.index * 397 ^ Id.generation));
        public static bool operator ==(NeziaEffect a, NeziaEffect b) => a.Equals(b);
        public static bool operator !=(NeziaEffect a, NeziaEffect b) => !a.Equals(b);
    }

    /// <summary>
    /// LowPass フィルタの型安全ビュー。<see cref="NeziaEffect.AsLowPass"/> から取得する。
    /// 値域: <see cref="Cutoff"/> Hz, <see cref="Q"/> 共振係数。
    /// </summary>
    public readonly struct NeziaLowPassEffect
    {
        public readonly NeziaEffect Effect;
        internal NeziaLowPassEffect(NeziaEffect effect) { Effect = effect; }

        /// <summary>カットオフ周波数 (Hz)。</summary>
        public float Cutoff { set => Effect.SetParamUnchecked(0, value); }
        /// <summary>共振係数 Q。</summary>
        public float Q { set => Effect.SetParamUnchecked(1, value); }
    }

    /// <summary>
    /// HighPass フィルタの型安全ビュー。<see cref="NeziaEffect.AsHighPass"/> から取得する。
    /// </summary>
    public readonly struct NeziaHighPassEffect
    {
        public readonly NeziaEffect Effect;
        internal NeziaHighPassEffect(NeziaEffect effect) { Effect = effect; }

        /// <summary>カットオフ周波数 (Hz)。</summary>
        public float Cutoff { set => Effect.SetParamUnchecked(0, value); }
        /// <summary>共振係数 Q。</summary>
        public float Q { set => Effect.SetParamUnchecked(1, value); }
    }

    /// <summary>
    /// Reverb の型安全ビュー。<see cref="NeziaEffect.AsReverb"/> から取得する。
    /// すべて正規化値 [0.0, 1.0]。
    /// </summary>
    public readonly struct NeziaReverbEffect
    {
        public readonly NeziaEffect Effect;
        internal NeziaReverbEffect(NeziaEffect effect) { Effect = effect; }

        public float RoomSize { set => Effect.SetParamUnchecked(0, value); }
        public float Damping { set => Effect.SetParamUnchecked(1, value); }
        public float Wet { set => Effect.SetParamUnchecked(2, value); }
        public float Dry { set => Effect.SetParamUnchecked(3, value); }
        public float Width { set => Effect.SetParamUnchecked(4, value); }
    }

    /// <summary>
    /// Compressor の型安全ビュー。<see cref="NeziaEffect.AsCompressor"/> から取得する。
    /// sidechain 駆動は <see cref="NeziaSend.AddBusToCompressor"/> もしくは
    /// <see cref="NeziaBus.BindCompressorSidechain"/> で別途制御する。
    /// </summary>
    public readonly struct NeziaCompressorEffect
    {
        public readonly NeziaEffect Effect;
        internal NeziaCompressorEffect(NeziaEffect effect) { Effect = effect; }

        /// <summary>圧縮開始 dB (例: -20.0)。</summary>
        public float ThresholdDb { set => Effect.SetParamUnchecked(0, value); }
        /// <summary>圧縮比。1.0 で無効、∞ で limiter。</summary>
        public float Ratio { set => Effect.SetParamUnchecked(1, value); }
        /// <summary>アタック ms (反応速度)。</summary>
        public float AttackMs { set => Effect.SetParamUnchecked(2, value); }
        /// <summary>リリース ms (回復速度)。</summary>
        public float ReleaseMs { set => Effect.SetParamUnchecked(3, value); }
        /// <summary>ソフトニー幅 dB (0 でハードニー)。</summary>
        public float KneeDb { set => Effect.SetParamUnchecked(4, value); }
        /// <summary>メイクアップゲイン dB。</summary>
        public float MakeupDb { set => Effect.SetParamUnchecked(5, value); }
    }
}
