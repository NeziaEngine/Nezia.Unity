using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace Nezia.Unity
{
    /// <summary>
    /// Unity <see cref="AudioMixerGroup"/> から <see cref="NeziaBus"/> への変換層。
    ///
    /// <para>
    /// CONCEPT.md「非目標」の通り、AudioMixer Snapshot や DSP プラグインの完全互換は
    /// 行わない。本クラスはあくまで「ゲームコードが <c>outputAudioMixerGroup</c> として
    /// 指す MixerGroup を Nezia の Bus に向け直す」ためのマッピング。
    /// </para>
    ///
    /// <para>
    /// バスの実体は <see cref="NeziaBusFactory"/> が生成する。Inspector では論理名
    /// （"BGM" / "SE" / "Voice" 等）と <see cref="AudioMixerGroup"/> を結びつけ、
    /// ランタイム初回参照時にバスが lazy に作成される。
    /// </para>
    /// </summary>
    [CreateAssetMenu(fileName = "NeziaBusMap", menuName = "Nezia/Bus Map")]
    public sealed class NeziaBusMap : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            public string busName;
            public AudioMixerGroup mixerGroup;
            [Range(0f, 4f)] public float gain = 1f;
        }

        [SerializeField] private List<Entry> entries = new();

        private readonly Dictionary<AudioMixerGroup, NeziaBus> _resolved = new();
#if UNITY_EDITOR
        private int _resolvedGeneration;
#endif

        /// <summary>
        /// 指定 MixerGroup に対応する <see cref="NeziaBus"/> を返す。
        /// 初回呼び出し時にマスターバス直下に新規バスを生成し、以後キャッシュする。
        /// マッピング未定義 / null の場合は <see cref="NeziaBus.Invalid"/>。
        /// </summary>
        public NeziaBus Resolve(AudioMixerGroup group)
        {
            if (group == null) return NeziaBus.Invalid;

#if UNITY_EDITOR
            // 世代不一致 = 旧エンジンが発行した Bus ID。旧エンジンは free 済みなので捨てるだけ。
            // ビルドではエンジンが一度しか初期化されないので #if UNITY_EDITOR で除外する。
            if (_resolved.Count > 0 && _resolvedGeneration != NeziaEngine.Generation)
                _resolved.Clear();
#endif

            if (_resolved.TryGetValue(group, out var cached)) return cached;

            foreach (var e in entries)
            {
                if (e.mixerGroup != group) continue;
                var bus = NeziaBus.Create(e.gain);
                _resolved[group] = bus;
#if UNITY_EDITOR
                _resolvedGeneration = NeziaEngine.Generation;
#endif
                return bus;
            }
            return NeziaBus.Invalid;
        }

        private void OnDisable() => _resolved.Clear();
    }
}
