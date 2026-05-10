using UnityEngine;

namespace Nezia.Unity.Samples.ClipCentricBasics
{
    /// <summary>
    /// 「Clip の鳴り方をベースにしつつ、このインスタンスだけ outputBus / spatialBlend を上書きしたい」
    /// という典型ケースを示す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="NeziaAudioSource.spatialBlend"/> や <see cref="NeziaAudioSource.outputBusName"/> を
    /// 直接代入すると、対応する override flag (<c>_overrideSpatial</c> / <c>_overrideOutputBus</c>) が
    /// 暗黙に true になり、その項目だけ Source 値が Clip 値より優先される。
    /// それ以外の項目 (loop / 距離・rolloff 以外の spatial / effect chain / send 等) は
    /// 引き続き Clip 値が支配する。
    /// </para>
    /// <para>
    /// Inspector でも同じことが per-property の Override トグルで表現できる。
    /// Cinemachine / HDRP Volume の override UX を意識した設計。
    /// </para>
    /// </remarks>
    [AddComponentMenu("Nezia/Samples/Source Override Example")]
    public sealed class SourceOverrideExample : MonoBehaviour
    {
        [SerializeField] private NeziaAudioClip clip;

        [Header("Source-side overrides (このインスタンスだけ)")]
        [SerializeField] private NeziaMixerAsset mixerAsset;
        [SerializeField] private string overrideOutputBus = "SE";
        [SerializeField, Range(0f, 1f)] private float overrideSpatialBlend = 1f;

        private void Awake()
        {
            var src = gameObject.AddComponent<NeziaAudioSource>();
            src.sound = clip;
            src.useClipDefaults = true;

            // 出力 Bus を override (このインスタンスだけ Clip の outputBus を無視)。
            // setter 経由で代入することで _overrideOutputBus が暗黙に立つ。
            if (mixerAsset != null && !string.IsNullOrEmpty(overrideOutputBus))
            {
                src.mixerAsset = mixerAsset;
                src.outputBusName = overrideOutputBus;
            }

            // 2D/3D ブレンドを override (Clip が 2D 設計でも、このインスタンスは 3D で鳴らす等)。
            src.spatialBlend = overrideSpatialBlend;

            src.Play();
        }
    }
}
