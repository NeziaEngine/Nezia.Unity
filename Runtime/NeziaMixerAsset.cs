using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nezia.Unity
{
    /// <summary>
    /// バスツリーを Inspector で設計するための ScriptableObject（IP-1 PR-A スコープ）。
    ///
    /// <para>
    /// Master 直下のバス階層を <see cref="BusNode"/> の flat list として保持する。
    /// <see cref="BusNode.parent"/> が空文字なら master 直下、そうでなければ同アセット内の
    /// 別 <see cref="BusNode.name"/> 配下に紐付く。<see cref="Resolve"/> 初回呼び出し時に
    /// 親→子の順で <see cref="NeziaBus"/> を lazy 構築し、内部 Dictionary にキャッシュする。
    /// </para>
    ///
    /// <para>
    /// 既存 <see cref="NeziaBusMap"/>（<c>AudioMixerGroup</c> → <see cref="NeziaBus"/>）と並存可。
    /// <see cref="NeziaAudioSource"/> 側の解決順は MixerAsset 優先、未設定なら BusMap にフォールバック。
    /// </para>
    /// </summary>
    [CreateAssetMenu(fileName = "NeziaMixerAsset", menuName = "Nezia/Mixer Asset")]
    public sealed class NeziaMixerAsset : ScriptableObject
    {
        /// <summary>
        /// バスツリーの 1 ノード。<see cref="parent"/> が空文字なら Master 直下。
        /// </summary>
        [Serializable]
        public sealed class BusNode
        {
            [Tooltip("アセット内で一意の論理名。NeziaAudioSource からこの名前で参照する。")]
            public string name;

            [Tooltip("親バス名。空文字なら Master 直下。")]
            public string parent;

            [Range(0f, 4f), Tooltip("バスゲイン。1.0 = 0dB。")]
            public float gain = 1f;

            [Tooltip("ミュート初期値。")]
            public bool muted;
        }

        [SerializeField] private List<BusNode> buses = new();

        public IReadOnlyList<BusNode> Buses => buses;

        // 解決済みバス。Generation をまたいで使い回さない（Editor 用）。
        private readonly Dictionary<string, NeziaBus> _resolved = new(StringComparer.Ordinal);
#if UNITY_EDITOR
        private int _resolvedGeneration;
#endif

        // 名前 → 設定 Lookup（Build 中だけ使う）。
        private Dictionary<string, BusNode> _byName;

        /// <summary>
        /// バスツリー全体を実体化する。<see cref="Resolve"/> でも lazy に同じことを行うため
        /// 通常は明示呼び出し不要。重複名・未知 parent・循環参照は <see cref="ArgumentException"/>。
        /// 既に Build 済みなら no-op（Generation 一致時）。
        /// </summary>
        public void Build()
        {
            EnsureFresh();
            BuildLookup();
            foreach (var node in buses)
            {
                if (node == null || string.IsNullOrEmpty(node.name)) continue;
                ResolveInternal(node.name, new HashSet<string>(StringComparer.Ordinal));
            }
        }

        /// <summary>
        /// 指定名のバスを返す。未存在 / 名前空なら <see cref="NeziaBus.Invalid"/>。
        /// 必要なら親バスを再帰的に lazy 生成する。
        /// </summary>
        public NeziaBus Resolve(string busName)
        {
            if (string.IsNullOrEmpty(busName)) return NeziaBus.Invalid;
            EnsureFresh();
            BuildLookup();
            return _byName.ContainsKey(busName)
                ? ResolveInternal(busName, new HashSet<string>(StringComparer.Ordinal))
                : NeziaBus.Invalid;
        }

        /// <summary>
        /// ネイティブを触らずに構成を検証する。重複名・未知 parent・循環参照を文字列リストで返す。
        /// 戻り値が空なら設定 OK。Inspector / テスト用。
        /// </summary>
        public List<string> Validate()
        {
            var errors = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in buses)
            {
                if (node == null) continue;
                if (string.IsNullOrEmpty(node.name)) { errors.Add("BusNode に name が未設定です。"); continue; }
                if (!seen.Add(node.name)) errors.Add($"バス名 '{node.name}' が重複しています。");
            }
            foreach (var node in buses)
            {
                if (node == null || string.IsNullOrEmpty(node.name)) continue;
                if (string.IsNullOrEmpty(node.parent)) continue;
                if (!seen.Contains(node.parent))
                    errors.Add($"バス '{node.name}' の parent '{node.parent}' が見つかりません。");
            }
            // 循環検出
            var byName = new Dictionary<string, BusNode>(StringComparer.Ordinal);
            foreach (var n in buses)
                if (n != null && !string.IsNullOrEmpty(n.name) && !byName.ContainsKey(n.name)) byName[n.name] = n;

            foreach (var node in buses)
            {
                if (node == null || string.IsNullOrEmpty(node.name)) continue;
                var visiting = new HashSet<string>(StringComparer.Ordinal);
                var cur = node;
                while (cur != null && !string.IsNullOrEmpty(cur.parent))
                {
                    if (!visiting.Add(cur.name))
                    {
                        errors.Add($"バス '{node.name}' から循環参照を検出しました。");
                        break;
                    }
                    if (!byName.TryGetValue(cur.parent, out cur)) break;
                }
            }
            return errors;
        }

        // ─── 内部 ────────────────────────────────────────────────

        private void EnsureFresh()
        {
#if UNITY_EDITOR
            if (_resolved.Count > 0 && _resolvedGeneration != NeziaEngine.Generation)
            {
                _resolved.Clear();
                _resolvedGeneration = NeziaEngine.Generation;
            }
            else if (_resolved.Count == 0)
            {
                _resolvedGeneration = NeziaEngine.Generation;
            }
#endif
        }

        private void BuildLookup()
        {
            if (_byName != null && _byName.Count == buses.Count) return;
            _byName = new Dictionary<string, BusNode>(StringComparer.Ordinal);
            foreach (var n in buses)
            {
                if (n == null || string.IsNullOrEmpty(n.name)) continue;
                if (_byName.ContainsKey(n.name))
                    throw new ArgumentException($"[NeziaMixerAsset:{name}] バス名 '{n.name}' が重複しています。");
                _byName[n.name] = n;
            }
        }

        private NeziaBus ResolveInternal(string busName, HashSet<string> visiting)
        {
            if (_resolved.TryGetValue(busName, out var cached)) return cached;

            if (!_byName.TryGetValue(busName, out var node))
                throw new ArgumentException($"[NeziaMixerAsset:{name}] バス '{busName}' が見つかりません。");

            if (!visiting.Add(busName))
                throw new ArgumentException($"[NeziaMixerAsset:{name}] バス '{busName}' で循環参照。");

            NeziaBus bus;
            if (string.IsNullOrEmpty(node.parent))
            {
                bus = NeziaBus.Create(node.gain);
            }
            else
            {
                var parentBus = ResolveInternal(node.parent, visiting);
                bus = NeziaBus.CreateRouted(parentBus, node.gain);
            }
            if (node.muted) bus.Muted = true;

            _resolved[busName] = bus;
            visiting.Remove(busName);
            return bus;
        }

        private void OnDisable()
        {
            _resolved.Clear();
            _byName = null;
        }
    }
}
