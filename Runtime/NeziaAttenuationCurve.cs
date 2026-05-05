using System;
using Nezia.Native;

namespace Nezia.Unity
{
    /// <summary>
    /// カスタム距離減衰カーブのハンドル。`[0.0, 1.0]` 正規化距離に対する利得を 64 サンプル LUT にする。
    ///
    /// <para>
    /// 適用するソース側で <see cref="NeziaRolloffMode"/> 相当の <c>model = Custom</c> を使う必要があるが、
    /// 現状の Unity ラッパでは個別 API が未公開のため、ネイティブ層を直接呼ぶ拡張用途。
    /// </para>
    /// </summary>
    public readonly struct NeziaAttenuationCurve : IEquatable<NeziaAttenuationCurve>
    {
        internal readonly NeziaAttenuationCurveId Id;

        internal NeziaAttenuationCurve(NeziaAttenuationCurveId id) { Id = id; }

        public bool IsValid => Id.index != uint.MaxValue;
        public static NeziaAttenuationCurve Invalid => new NeziaAttenuationCurve(new NeziaAttenuationCurveId { index = uint.MaxValue, generation = 0 });

        /// <summary>
        /// `[0.0, 1.0]` の正規化距離に対応する利得サンプルからカーブを生成する。
        /// 内部で 64 サンプル LUT に再サンプリングされる。
        /// </summary>
        public static unsafe NeziaAttenuationCurve Create(float[] points)
        {
            if (points == null || points.Length == 0)
                throw new ArgumentException("points must be non-empty", nameof(points));

            fixed (float* p = points)
            {
                var id = LibNezia.nezia_attenuation_curve_create(
                    NeziaEngine.RequireHandle(), p, (nuint)points.Length);
                return new NeziaAttenuationCurve(id);
            }
        }

        /// <summary>このカーブを破棄する。参照中のソースは silent fallback する。</summary>
        public unsafe void Destroy()
        {
            if (!IsValid) return;
            var r = LibNezia.nezia_attenuation_curve_destroy(NeziaEngine.RequireHandle(), Id);
            NeziaException.ThrowIfError(r, "attenuation curve destroy");
        }

        public bool Equals(NeziaAttenuationCurve other) => Id.index == other.Id.index && Id.generation == other.Id.generation;
        public override bool Equals(object obj) => obj is NeziaAttenuationCurve c && Equals(c);
        public override int GetHashCode() => unchecked((int)(Id.index * 397 ^ Id.generation));
        public static bool operator ==(NeziaAttenuationCurve a, NeziaAttenuationCurve b) => a.Equals(b);
        public static bool operator !=(NeziaAttenuationCurve a, NeziaAttenuationCurve b) => !a.Equals(b);
    }
}
