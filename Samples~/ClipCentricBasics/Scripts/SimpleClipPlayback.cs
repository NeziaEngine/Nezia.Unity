using UnityEngine;

namespace Nezia.Unity.Samples.ClipCentricBasics
{
    /// <summary>
    /// Clip-centric 最小例。
    /// 鳴り方 (volume / pitch / outputBus / 距離 / effect chain / send) は
    /// すべて <see cref="NeziaAudioClip"/> 側で設定し、ここでは「いつ鳴らすか」だけを書く。
    /// </summary>
    /// <remarks>
    /// Inspector で <see cref="clip"/> に <see cref="NeziaAudioClip"/> を割り当ててから
    /// シーンを Play する。Trigger キー (Space) で再生。
    /// </remarks>
    [AddComponentMenu("Nezia/Samples/Simple Clip Playback")]
    public sealed class SimpleClipPlayback : MonoBehaviour
    {
        [SerializeField] private NeziaAudioClip clip;
        [SerializeField] private KeyCode triggerKey = KeyCode.Space;

        private NeziaAudioSource _source;

        private void Awake()
        {
            _source = gameObject.AddComponent<NeziaAudioSource>();
            _source.sound = clip;
            // Clip-centric を有効化 — これを ON にしないと Source 側のフィールドが勝つ互換挙動になる。
            _source.useClipDefaults = true;
        }

        private void Update()
        {
            if (Input.GetKeyDown(triggerKey)) _source.Play();
        }
    }
}
