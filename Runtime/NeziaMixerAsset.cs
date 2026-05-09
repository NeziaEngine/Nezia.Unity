using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nezia.Unity
{
    /// <summary>
    /// バスツリーを Inspector で設計するための ScriptableObject（IP-1 PR-A / PR-B / PR-C スコープ）。
    ///
    /// <para>
    /// Master 直下のバス階層を <see cref="BusNode"/> の flat list として保持する。
    /// <see cref="BusNode.parent"/> が空文字なら master 直下、そうでなければ同アセット内の
    /// 別 <see cref="BusNode.name"/> 配下に紐付く。<see cref="Resolve"/> 初回呼び出し時に
    /// 親→子の順で <see cref="NeziaBus"/> を lazy 構築し、内部 Dictionary にキャッシュする。
    /// </para>
    ///
    /// <para>
    /// PR-B からは各 <see cref="BusNode"/> に <see cref="BusEffect"/> を宣言でき、
    /// バス実体化と同じタイミングでエフェクトチェーンを構築する（<see cref="ResolveEffects"/>）。
    /// </para>
    ///
    /// <para>
    /// PR-C からは <see cref="SendNode"/> によりバス間 Send（bus→bus）と Compressor sidechain Send
    /// （bus→Compressor）も Inspector で記述できる。<see cref="ResolveSends"/> 初回呼び出し時に
    /// 全バス／エフェクトを実体化したうえで Send ハンドルを宣言順に構築する。
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

            [SerializeReference, Tooltip("このバスに挿入するエフェクトチェーン。宣言順に追加される。")]
            public List<BusEffect> effects = new();
        }

        /// <summary>
        /// バスに挿入するエフェクトの宣言ベース型。<see cref="LowPass"/> / <see cref="HighPass"/> /
        /// <see cref="Reverb"/> / <see cref="Compressor"/> を <c>[SerializeReference]</c> で
        /// 多態シリアライズする。
        /// </summary>
        [Serializable]
        public abstract class BusEffect
        {
            [Tooltip("Pre = フェーダー前 / Post = フェーダー後。")]
            public NeziaEffectPosition position = NeziaEffectPosition.Post;

            [Tooltip("初期 enabled。false で挿入後に即 disable する。")]
            public bool enabled = true;

            public abstract NeziaEffectKind Kind { get; }

            internal abstract void ApplyInitial(NeziaEffect effect);
        }

        [Serializable]
        public sealed class LowPass : BusEffect
        {
            [Range(20f, 20000f)] public float cutoff = 1000f;
            [Range(0.1f, 10f)] public float q = 0.7071f;
            public override NeziaEffectKind Kind => NeziaEffectKind.LowPass;
            internal override void ApplyInitial(NeziaEffect effect)
            {
                var v = effect.AsLowPass();
                v.Cutoff = cutoff;
                v.Q = q;
            }
        }

        [Serializable]
        public sealed class HighPass : BusEffect
        {
            [Range(20f, 20000f)] public float cutoff = 200f;
            [Range(0.1f, 10f)] public float q = 0.7071f;
            public override NeziaEffectKind Kind => NeziaEffectKind.HighPass;
            internal override void ApplyInitial(NeziaEffect effect)
            {
                var v = effect.AsHighPass();
                v.Cutoff = cutoff;
                v.Q = q;
            }
        }

        [Serializable]
        public sealed class Reverb : BusEffect
        {
            [Range(0f, 1f)] public float roomSize = 0.5f;
            [Range(0f, 1f)] public float damping = 0.5f;
            [Range(0f, 1f)] public float wet = 0.33f;
            [Range(0f, 1f)] public float dry = 0.7f;
            [Range(0f, 1f)] public float width = 1f;
            public override NeziaEffectKind Kind => NeziaEffectKind.Reverb;
            internal override void ApplyInitial(NeziaEffect effect)
            {
                var v = effect.AsReverb();
                v.RoomSize = roomSize;
                v.Damping = damping;
                v.Wet = wet;
                v.Dry = dry;
                v.Width = width;
            }
        }

        [Serializable]
        public sealed class Compressor : BusEffect
        {
            [Tooltip("圧縮開始 dB (例: -20.0)")]
            public float thresholdDb = -20f;
            [Tooltip("圧縮比。1.0 で無効、∞ で limiter。")]
            public float ratio = 4f;
            public float attackMs = 10f;
            public float releaseMs = 100f;
            public float kneeDb = 6f;
            public float makeupDb = 0f;
            public override NeziaEffectKind Kind => NeziaEffectKind.Compressor;
            internal override void ApplyInitial(NeziaEffect effect)
            {
                var v = effect.AsCompressor();
                v.ThresholdDb = thresholdDb;
                v.Ratio = ratio;
                v.AttackMs = attackMs;
                v.ReleaseMs = releaseMs;
                v.KneeDb = kneeDb;
                v.MakeupDb = makeupDb;
            }
        }

        /// <summary>
        /// Send 1 本の宣言。<see cref="target"/> = <see cref="SendTargetKind.Bus"/> ならバス→バス、
        /// <see cref="SendTargetKind.CompressorSidechain"/> ならバス→Compressor sidechain。
        /// 後者の場合 <see cref="targetBus"/> 上の <see cref="targetEffectIndex"/> 番目のエフェクトを参照する。
        /// </summary>
        [Serializable]
        public sealed class SendNode
        {
            [Tooltip("送り元バス名。")]
            public string source;

            [Tooltip("送り先の種類。Bus は通常の Send、CompressorSidechain は Compressor の sidechain 入力に流す。")]
            public SendTargetKind target = SendTargetKind.Bus;

            [Tooltip("送り先バス名。CompressorSidechain の場合は Compressor が乗っているバスを指す。")]
            public string targetBus;

            [Tooltip("CompressorSidechain 時のみ参照される、targetBus のエフェクトチェーン上のインデックス。")]
            public int targetEffectIndex;

            [Tooltip("Pre = フェーダー前 / Post = フェーダー後。")]
            public NeziaSendPosition position = NeziaSendPosition.Post;

            [Range(0f, 4f), Tooltip("Send ゲイン。1.0 = 0dB。")]
            public float gain = 1f;
        }

        public enum SendTargetKind : byte
        {
            Bus = 0,
            CompressorSidechain = 1,
        }

        [SerializeField] private List<BusNode> buses = new();
        [SerializeField] private List<SendNode> sends = new();

        public IReadOnlyList<BusNode> Buses => buses;
        public IReadOnlyList<SendNode> Sends => sends;

        // 解決済みバス。Generation をまたいで使い回さない（Editor 用）。
        private readonly Dictionary<string, NeziaBus> _resolved = new(StringComparer.Ordinal);
        private readonly Dictionary<string, NeziaEffect[]> _resolvedEffects = new(StringComparer.Ordinal);
        private NeziaSend[] _resolvedSends;
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
            EnsureSendsBuilt();
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
        /// 指定バスに紐付く（解決済み）エフェクトハンドルを宣言順に返す。
        /// バス自体が未解決なら lazy 解決を行い、その時点で <see cref="BusEffect"/> を全て挿入する。
        /// </summary>
        public IReadOnlyList<NeziaEffect> ResolveEffects(string busName)
        {
            if (string.IsNullOrEmpty(busName)) return Array.Empty<NeziaEffect>();
            EnsureFresh();
            BuildLookup();
            if (!_byName.ContainsKey(busName)) return Array.Empty<NeziaEffect>();
            ResolveInternal(busName, new HashSet<string>(StringComparer.Ordinal));
            return _resolvedEffects.TryGetValue(busName, out var arr) ? arr : Array.Empty<NeziaEffect>();
        }

        /// <summary>
        /// 指定バスの index 番目のエフェクトを返す。範囲外なら <see cref="NeziaEffect.Invalid"/>。
        /// </summary>
        public NeziaEffect ResolveEffect(string busName, int index)
        {
            var list = ResolveEffects(busName);
            return (uint)index < (uint)list.Count ? list[index] : NeziaEffect.Invalid;
        }

        /// <summary>
        /// 宣言された Send を全て実体化して宣言順に返す。未解決のバス／エフェクトは内部で lazy 構築する。
        /// 不正な Send（未知バス・index 範囲外・sidechain 先が Compressor でない等）は <see cref="NeziaSend.Invalid"/>。
        /// </summary>
        public IReadOnlyList<NeziaSend> ResolveSends()
        {
            EnsureFresh();
            BuildLookup();
            EnsureSendsBuilt();
            return _resolvedSends ?? Array.Empty<NeziaSend>();
        }

#if UNITY_EDITOR
        /// <summary>
        /// (Editor 専用) ネイティブを触らずに構成を検証する。重複名 / 未知 parent /
        /// 循環参照を文字列リストで返す。<see cref="OnValidate"/> から自動で呼ばれ、
        /// 検出された問題は Console に warning として吐かれる。
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

            // Send の検証。
            for (int i = 0; i < sends.Count; i++)
            {
                var s = sends[i];
                if (s == null) continue;
                if (string.IsNullOrEmpty(s.source))
                {
                    errors.Add($"Send[{i}] に source が未設定です。");
                    continue;
                }
                if (string.IsNullOrEmpty(s.targetBus))
                {
                    errors.Add($"Send[{i}] (source='{s.source}') に targetBus が未設定です。");
                    continue;
                }
                if (!byName.ContainsKey(s.source))
                    errors.Add($"Send[{i}] の source '{s.source}' が見つかりません。");
                if (!byName.TryGetValue(s.targetBus, out var tnode))
                    errors.Add($"Send[{i}] の targetBus '{s.targetBus}' が見つかりません。");
                else if (s.target == SendTargetKind.CompressorSidechain)
                {
                    var fxList = tnode.effects;
                    if (fxList == null || s.targetEffectIndex < 0 || s.targetEffectIndex >= fxList.Count)
                        errors.Add($"Send[{i}] の targetEffectIndex {s.targetEffectIndex} が範囲外です。");
                    else if (fxList[s.targetEffectIndex] == null
                             || fxList[s.targetEffectIndex].Kind != NeziaEffectKind.Compressor)
                        errors.Add($"Send[{i}] sidechain 先 '{s.targetBus}'[{s.targetEffectIndex}] が Compressor ではありません。");
                }
                else if (string.Equals(s.source, s.targetBus, StringComparison.Ordinal))
                {
                    errors.Add($"Send[{i}] は同一バス '{s.source}' を source / target に指定しています。");
                }
            }
            return errors;
        }

        private void OnValidate()
        {
            foreach (var err in Validate())
                Debug.LogWarning($"[NeziaMixerAsset:{name}] {err}", this);
        }
#endif

        // ─── 内部 ────────────────────────────────────────────────

        private void EnsureFresh()
        {
#if UNITY_EDITOR
            if (_resolved.Count > 0 && _resolvedGeneration != NeziaEngine.Generation)
            {
                _resolved.Clear();
                _resolvedEffects.Clear();
                _resolvedSends = null;
                _resolvedGeneration = NeziaEngine.Generation;
            }
            else if (_resolved.Count == 0)
            {
                _resolvedEffects.Clear();
                _resolvedSends = null;
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

            // エフェクトチェーンを宣言順に挿入する。
            if (node.effects != null && node.effects.Count > 0)
            {
                var fxArr = new NeziaEffect[node.effects.Count];
                for (int i = 0; i < node.effects.Count; i++)
                {
                    var spec = node.effects[i];
                    if (spec == null) { fxArr[i] = NeziaEffect.Invalid; continue; }
                    var fx = bus.AddEffect(spec.Kind, spec.position);
                    spec.ApplyInitial(fx);
                    if (!spec.enabled) fx.Enabled = false;
                    fxArr[i] = fx;
                }
                _resolvedEffects[busName] = fxArr;
            }

            visiting.Remove(busName);
            return bus;
        }

        private void EnsureSendsBuilt()
        {
            if (_resolvedSends != null) return;
            if (sends == null || sends.Count == 0)
            {
                _resolvedSends = Array.Empty<NeziaSend>();
                return;
            }

            var arr = new NeziaSend[sends.Count];
            for (int i = 0; i < sends.Count; i++)
            {
                var spec = sends[i];
                if (spec == null
                    || string.IsNullOrEmpty(spec.source)
                    || string.IsNullOrEmpty(spec.targetBus)
                    || !_byName.ContainsKey(spec.source)
                    || !_byName.TryGetValue(spec.targetBus, out var tnode))
                {
                    arr[i] = NeziaSend.Invalid;
                    continue;
                }

                var src = ResolveInternal(spec.source, new HashSet<string>(StringComparer.Ordinal));
                // Resolve target side so its effects exist before referencing them.
                ResolveInternal(spec.targetBus, new HashSet<string>(StringComparer.Ordinal));

                if (spec.target == SendTargetKind.CompressorSidechain)
                {
                    if (!_resolvedEffects.TryGetValue(spec.targetBus, out var fxArr)
                        || (uint)spec.targetEffectIndex >= (uint)fxArr.Length)
                    {
                        arr[i] = NeziaSend.Invalid;
                        continue;
                    }
                    var fx = fxArr[spec.targetEffectIndex];
                    if (!fx.IsValid || fx.Kind != NeziaEffectKind.Compressor)
                    {
                        arr[i] = NeziaSend.Invalid;
                        continue;
                    }
                    arr[i] = NeziaSend.AddBusToCompressor(src, fx, spec.position, spec.gain);
                }
                else
                {
                    var dst = _resolved[spec.targetBus];
                    arr[i] = NeziaSend.AddBusToBus(src, dst, spec.position, spec.gain);
                }
            }
            _resolvedSends = arr;
        }

        private void OnDisable()
        {
            _resolved.Clear();
            _resolvedEffects.Clear();
            _resolvedSends = null;
            _byName = null;
        }
    }
}
