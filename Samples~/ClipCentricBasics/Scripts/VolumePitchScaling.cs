using UnityEngine;

namespace Nezia.Unity.Samples.ClipCentricBasics
{
    /// <summary>
    /// <see cref="NeziaAudioSource.volume"/> / <see cref="NeziaAudioSource.pitch"/> が
    /// Clip-centric モード下では **Clip 基準値への scale (乗算)** として効くことを
    /// 確認するためのデモ。
    /// </summary>
    /// <remarks>
    /// Clip 側で volume = 0.5 を設定し、本コンポーネントで <see cref="volumeScale"/> = 0.5 にすると
    /// 実効音量は 0.25 になる (0.5 * 0.5)。AudioSource.volume = 0.5 の代入が直接最終値になる旧挙動とは異なる。
    /// </remarks>
    [AddComponentMenu("Nezia/Samples/Volume Pitch Scaling")]
    public sealed class VolumePitchScaling : MonoBehaviour
    {
        [SerializeField] private NeziaAudioClip clip;
        [SerializeField, Range(0f, 1f)] private float volumeScale = 0.5f;
        [SerializeField, Range(0.1f, 4f)] private float pitchScale = 1f;
        [SerializeField] private bool playOnAwake = true;

        private NeziaAudioSource _source;

        private void Awake()
        {
            _source = gameObject.AddComponent<NeziaAudioSource>();
            _source.sound = clip;
            _source.useClipDefaults = true;
            // Clip 基準値への乗算として効く。Clip.volume * volumeScale が最終音量。
            _source.volume = volumeScale;
            _source.pitch = pitchScale;

            if (playOnAwake) _source.Play();
        }
    }
}
