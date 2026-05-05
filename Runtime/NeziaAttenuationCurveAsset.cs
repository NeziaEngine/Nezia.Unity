using UnityEngine;

namespace Nezia.Unity
{
    /// <summary>
    /// 距離減衰カーブをアセット化した <see cref="ScriptableObject"/>。
    ///
    /// <para>
    /// Inspector で Unity 標準の <see cref="AnimationCurve"/> エディタとして編集でき、
    /// 実行時は <see cref="ToNative"/> でサンプリングして
    /// <see cref="NeziaAttenuationCurve"/>（ネイティブ 64 サンプル LUT）に変換する。
    /// 同一カーブを複数 <see cref="NeziaAudioSource"/> で共有したい用途
    ///（足音用 / SE 用 / BGM 用などのプリセット運用）に向く。
    /// </para>
    /// </summary>
    [CreateAssetMenu(menuName = "Nezia/Attenuation Curve", fileName = "NeziaAttenuationCurve")]
    public sealed class NeziaAttenuationCurveAsset : ScriptableObject
    {
        /// <summary>
        /// 0..1 の正規化距離に対する利得カーブ。
        /// 0 = minDistance、1 = maxDistance。
        /// 既定は線形減衰（1 → 0）。
        /// </summary>
        [SerializeField]
        private AnimationCurve _curve = AnimationCurve.Linear(0f, 1f, 1f, 0f);

        /// <summary>
        /// ネイティブ LUT のサイズと一致する 64 を既定にする。
        /// より細かいカーブを描きたい場合のみ増やす意味があるが、ネイティブ側で
        /// 64 サンプルに再サンプリングされるため通常は変更不要。
        /// </summary>
        [SerializeField, Range(2, 256)]
        private int _samples = 64;

        /// <summary>編集中のカーブ（読み取り専用アクセス）。</summary>
        public AnimationCurve Curve => _curve;

        /// <summary>サンプル数。</summary>
        public int Samples => _samples;

        /// <summary>
        /// 現在のカーブをサンプリングしてネイティブカーブを生成する。
        /// 戻り値の <see cref="NeziaAttenuationCurve.Destroy"/> 責任は呼出側にある。
        /// </summary>
        public NeziaAttenuationCurve ToNative()
        {
            int n = Mathf.Max(2, _samples);
            var pts = new float[n];
            for (int i = 0; i < n; i++)
                pts[i] = _curve.Evaluate(i / (float)(n - 1));
            return NeziaAttenuationCurve.Create(pts);
        }
    }
}
