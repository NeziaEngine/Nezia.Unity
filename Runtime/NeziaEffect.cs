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
        /// </summary>
        public unsafe void SetParam(byte param, float value)
        {
            var r = LibNezia.nezia_effect_set_param(NeziaEngine.RequireHandle(), Id, param, value);
            NeziaException.ThrowIfError(r, "effect set param");
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
}
