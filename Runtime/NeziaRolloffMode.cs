using Nezia.Native;

namespace Nezia.Unity
{
    /// <summary>
    /// 距離減衰モデル。Unity の <c>AudioRolloffMode</c> と概念的に対応する。
    /// </summary>
    public enum NeziaRolloffMode
    {
        /// <summary>減衰なし（常に最大音量）。</summary>
        None = 0,
        /// <summary>線形減衰。</summary>
        Linear = 1,
        /// <summary>逆距離（Unity の Logarithmic 相当）。</summary>
        InverseDistance = 2,
        /// <summary>指数減衰。</summary>
        Exponential = 3,
    }

    internal static class NeziaRolloffModeExtensions
    {
        internal static NeziaAttenuationModel ToNative(this NeziaRolloffMode mode)
            => (NeziaAttenuationModel)(uint)mode;
    }
}
