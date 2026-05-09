using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nezia.Unity
{
    /// <summary>
    /// <see cref="NeziaMixerAsset"/> 上のバス状態を Inspector で宣言し、
    /// ランタイムで <see cref="Apply"/> 一発に適用する Snapshot アセット。
    ///
    /// <para>
    /// AudioMixer の Snapshot に相当するデータ駆動レイヤ。<see cref="NeziaSnapshot"/>
    /// (`Begin().Set...Commit()`) を毎フレーム呼ぶ代わりに ScriptableObject に固定し、
    /// ゲームコードからは <c>asset.Apply(fadeSeconds)</c> だけで遷移できる。
    /// </para>
    ///
    /// <para>
    /// PR-A: バスゲイン / バスミュート。
    /// PR-B: <see cref="SendOverride"/> による Send ゲイン、
    /// <see cref="EffectOverride"/> による（kind 別の）エフェクトパラメータ。
    /// 各 override はフラグ別管理にして「特定パラメータだけを動かす」を独立に表現できる。
    /// </para>
    /// </summary>
    [CreateAssetMenu(fileName = "NeziaSnapshotAsset", menuName = "Nezia/Snapshot Asset")]
    public sealed class NeziaSnapshotAsset : ScriptableObject
    {
        /// <summary>
        /// バス 1 つに対する上書き宣言。<see cref="overrideGain"/> / <see cref="overrideMuted"/>
        /// が false のフィールドは Snapshot に積まれない（既存値を保持）。
        /// </summary>
        [Serializable]
        public sealed class BusOverride
        {
            [Tooltip("Mixer 内のバス名。")]
            public string busName;

            [Tooltip("true でこのバスのゲインを Snapshot に積む。")]
            public bool overrideGain = true;

            [Range(0f, 4f), Tooltip("バスゲイン。1.0 = 0dB。overrideGain が true のときのみ適用。")]
            public float gain = 1f;

            [Tooltip("true でこのバスのミュート状態を Snapshot に積む。")]
            public bool overrideMuted;

            [Tooltip("ミュート値。overrideMuted が true のときのみ適用。")]
            public bool muted;
        }

        /// <summary>
        /// Send 1 本に対する上書き宣言。<see cref="sendIndex"/> は <see cref="NeziaMixerAsset.Sends"/>
        /// 内の宣言順インデックス。Inspector でリストを並べ替えると壊れるため、明示的な
        /// 並び替え禁止 or 後続 PR で <see cref="NeziaMixerAsset.SendNode"/> 側に名前を持たせる
        /// 計画。
        /// </summary>
        [Serializable]
        public sealed class SendOverride
        {
            [Tooltip("Mixer の sends 配列におけるインデックス。")]
            public int sendIndex;

            [Tooltip("true でこの Send のゲインを Snapshot に積む。")]
            public bool overrideGain = true;

            [Range(0f, 4f), Tooltip("Send ゲイン。overrideGain が true のときのみ適用。")]
            public float gain = 1f;
        }

        /// <summary>
        /// エフェクトパラメータ上書きの基底。<see cref="busName"/> + <see cref="effectIndex"/>
        /// で <see cref="NeziaMixerAsset"/> 内のエフェクトを特定する。実際のパラメータ群は
        /// <see cref="LowPassOverride"/> 等の派生クラスに持たせ、<c>[SerializeReference]</c>
        /// で多態シリアライズする。
        /// </summary>
        [Serializable]
        public abstract class EffectOverride
        {
            [Tooltip("対象エフェクトが乗っているバス名。")]
            public string busName;

            [Tooltip("対象バスの effects 配列におけるインデックス。")]
            public int effectIndex;

            public abstract NeziaEffectKind Kind { get; }

            internal abstract void ApplyTo(NeziaSnapshot.Builder builder, NeziaEffect effect);

            /// <summary>有効な override が 1 つもない（全 override フラグが false）なら true。</summary>
            internal abstract bool IsEmpty { get; }
        }

        [Serializable]
        public sealed class LowPassOverride : EffectOverride
        {
            public bool overrideCutoff = true;
            [Range(20f, 20000f)] public float cutoff = 1000f;
            public bool overrideQ;
            [Range(0.1f, 10f)] public float q = 0.7071f;

            public override NeziaEffectKind Kind => NeziaEffectKind.LowPass;
            internal override bool IsEmpty => !overrideCutoff && !overrideQ;
            internal override void ApplyTo(NeziaSnapshot.Builder builder, NeziaEffect effect)
            {
                if (overrideCutoff) builder.SetEffectParam(effect, 0, cutoff);
                if (overrideQ) builder.SetEffectParam(effect, 1, q);
            }
        }

        [Serializable]
        public sealed class HighPassOverride : EffectOverride
        {
            public bool overrideCutoff = true;
            [Range(20f, 20000f)] public float cutoff = 200f;
            public bool overrideQ;
            [Range(0.1f, 10f)] public float q = 0.7071f;

            public override NeziaEffectKind Kind => NeziaEffectKind.HighPass;
            internal override bool IsEmpty => !overrideCutoff && !overrideQ;
            internal override void ApplyTo(NeziaSnapshot.Builder builder, NeziaEffect effect)
            {
                if (overrideCutoff) builder.SetEffectParam(effect, 0, cutoff);
                if (overrideQ) builder.SetEffectParam(effect, 1, q);
            }
        }

        [Serializable]
        public sealed class ReverbOverride : EffectOverride
        {
            public bool overrideRoomSize;
            [Range(0f, 1f)] public float roomSize = 0.5f;
            public bool overrideDamping;
            [Range(0f, 1f)] public float damping = 0.5f;
            public bool overrideWet = true;
            [Range(0f, 1f)] public float wet = 0.33f;
            public bool overrideDry;
            [Range(0f, 1f)] public float dry = 0.7f;
            public bool overrideWidth;
            [Range(0f, 1f)] public float width = 1f;

            public override NeziaEffectKind Kind => NeziaEffectKind.Reverb;
            internal override bool IsEmpty =>
                !overrideRoomSize && !overrideDamping && !overrideWet && !overrideDry && !overrideWidth;
            internal override void ApplyTo(NeziaSnapshot.Builder builder, NeziaEffect effect)
            {
                if (overrideRoomSize) builder.SetEffectParam(effect, 0, roomSize);
                if (overrideDamping) builder.SetEffectParam(effect, 1, damping);
                if (overrideWet) builder.SetEffectParam(effect, 2, wet);
                if (overrideDry) builder.SetEffectParam(effect, 3, dry);
                if (overrideWidth) builder.SetEffectParam(effect, 4, width);
            }
        }

        [Serializable]
        public sealed class CompressorOverride : EffectOverride
        {
            public bool overrideThresholdDb = true;
            public float thresholdDb = -20f;
            public bool overrideRatio;
            public float ratio = 4f;
            public bool overrideAttackMs;
            public float attackMs = 10f;
            public bool overrideReleaseMs;
            public float releaseMs = 100f;
            public bool overrideKneeDb;
            public float kneeDb = 6f;
            public bool overrideMakeupDb;
            public float makeupDb = 0f;

            public override NeziaEffectKind Kind => NeziaEffectKind.Compressor;
            internal override bool IsEmpty =>
                !overrideThresholdDb && !overrideRatio && !overrideAttackMs
                && !overrideReleaseMs && !overrideKneeDb && !overrideMakeupDb;
            internal override void ApplyTo(NeziaSnapshot.Builder builder, NeziaEffect effect)
            {
                if (overrideThresholdDb) builder.SetEffectParam(effect, 0, thresholdDb);
                if (overrideRatio) builder.SetEffectParam(effect, 1, ratio);
                if (overrideAttackMs) builder.SetEffectParam(effect, 2, attackMs);
                if (overrideReleaseMs) builder.SetEffectParam(effect, 3, releaseMs);
                if (overrideKneeDb) builder.SetEffectParam(effect, 4, kneeDb);
                if (overrideMakeupDb) builder.SetEffectParam(effect, 5, makeupDb);
            }
        }

        [SerializeField, Tooltip("対象のミキサーアセット。Snapshot は同じ MixerAsset に解決されたバスに対して適用される。")]
        private NeziaMixerAsset mixer;

        [SerializeField] private List<BusOverride> busOverrides = new();
        [SerializeField] private List<SendOverride> sendOverrides = new();
        [SerializeReference] private List<EffectOverride> effectOverrides = new();

        public NeziaMixerAsset Mixer => mixer;
        public IReadOnlyList<BusOverride> BusOverrides => busOverrides;
        public IReadOnlyList<SendOverride> SendOverrides => sendOverrides;
        public IReadOnlyList<EffectOverride> EffectOverrides => effectOverrides;

        /// <summary>
        /// この Snapshot を <paramref name="fadeSeconds"/> かけて適用する（0 で即時）。
        ///
        /// <para>
        /// 内部的には <see cref="NeziaSnapshot.Begin"/> で Builder を生成し、各 override を
        /// <see cref="Mixer"/> 経由で解決して積み、Commit → Apply → Destroy までを 1 ステップ
        /// で行う（永続ハンドルは保持しない）。
        /// </para>
        /// </summary>
        public void Apply(float fadeSeconds = 0f)
        {
            if (mixer == null)
                throw new InvalidOperationException($"[NeziaSnapshotAsset:{name}] mixer が未設定です。");

            var builder = NeziaSnapshot.Begin();
            try
            {
                if (busOverrides != null)
                {
                    foreach (var ov in busOverrides)
                    {
                        if (ov == null || string.IsNullOrEmpty(ov.busName)) continue;
                        if (!ov.overrideGain && !ov.overrideMuted) continue;

                        var bus = mixer.Resolve(ov.busName);
                        if (!bus.IsValid)
                        {
                            Debug.LogWarning(
                                $"[NeziaSnapshotAsset:{name}] バス '{ov.busName}' が Mixer に存在しません。スキップします。",
                                this);
                            continue;
                        }

                        if (ov.overrideGain) builder.SetBusGain(bus, ov.gain);
                        if (ov.overrideMuted) builder.SetBusMuted(bus, ov.muted);
                    }
                }

                if (sendOverrides != null && sendOverrides.Count > 0)
                {
                    var sends = mixer.ResolveSends();
                    foreach (var ov in sendOverrides)
                    {
                        if (ov == null || !ov.overrideGain) continue;
                        if ((uint)ov.sendIndex >= (uint)sends.Count)
                        {
                            Debug.LogWarning(
                                $"[NeziaSnapshotAsset:{name}] sendIndex {ov.sendIndex} が Mixer の Send 数 ({sends.Count}) を超えています。スキップします。",
                                this);
                            continue;
                        }
                        var send = sends[ov.sendIndex];
                        if (!send.IsValid)
                        {
                            Debug.LogWarning(
                                $"[NeziaSnapshotAsset:{name}] sendIndex {ov.sendIndex} の Send が無効です（宣言エラー）。スキップします。",
                                this);
                            continue;
                        }
                        builder.SetSendGain(send, ov.gain);
                    }
                }

                if (effectOverrides != null)
                {
                    foreach (var ov in effectOverrides)
                    {
                        if (ov == null || string.IsNullOrEmpty(ov.busName) || ov.IsEmpty) continue;
                        var effect = mixer.ResolveEffect(ov.busName, ov.effectIndex);
                        if (!effect.IsValid)
                        {
                            Debug.LogWarning(
                                $"[NeziaSnapshotAsset:{name}] バス '{ov.busName}' のエフェクト[{ov.effectIndex}] が見つかりません。スキップします。",
                                this);
                            continue;
                        }
                        if (effect.Kind != ov.Kind)
                        {
                            Debug.LogWarning(
                                $"[NeziaSnapshotAsset:{name}] バス '{ov.busName}'[{ov.effectIndex}] の kind ({effect.Kind}) が override の {ov.Kind} と一致しません。スキップします。",
                                this);
                            continue;
                        }
                        ov.ApplyTo(builder, effect);
                    }
                }

                var snapshot = builder.Commit(); // builder._ptr は null 化される
                try { snapshot.Apply(fadeSeconds); }
                finally { snapshot.Destroy(); }
            }
            finally
            {
                builder.Cancel(); // Commit 済みなら no-op、途中例外時のみ実際に解放
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// (Editor 専用) ネイティブを触らずに構成を検証する。mixer 未設定 / 未知バス名 /
        /// 重複バス名指定 / 範囲外 sendIndex / kind 不一致を文字列リストで返す。
        /// <see cref="OnValidate"/> から自動で呼ばれる。
        /// </summary>
        public List<string> Validate()
        {
            var errors = new List<string>();
            if (mixer == null)
            {
                errors.Add("mixer が未設定です。");
                return errors;
            }

            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in mixer.Buses)
                if (n != null && !string.IsNullOrEmpty(n.name)) known.Add(n.name);

            // BusOverride
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < busOverrides.Count; i++)
            {
                var ov = busOverrides[i];
                if (ov == null) continue;
                if (string.IsNullOrEmpty(ov.busName))
                {
                    errors.Add($"BusOverride[{i}] に busName が未設定です。");
                    continue;
                }
                if (!known.Contains(ov.busName))
                    errors.Add($"BusOverride[{i}] の busName '{ov.busName}' が Mixer 内に見つかりません。");
                if (!seen.Add(ov.busName))
                    errors.Add($"BusOverride[{i}] の busName '{ov.busName}' が重複しています。");
            }

            // SendOverride
            var sendCount = mixer.Sends.Count;
            var seenSend = new HashSet<int>();
            for (int i = 0; i < sendOverrides.Count; i++)
            {
                var ov = sendOverrides[i];
                if (ov == null) continue;
                if (ov.sendIndex < 0 || ov.sendIndex >= sendCount)
                    errors.Add($"SendOverride[{i}] の sendIndex {ov.sendIndex} が Mixer の Send 数 ({sendCount}) の範囲外です。");
                else if (!seenSend.Add(ov.sendIndex))
                    errors.Add($"SendOverride[{i}] の sendIndex {ov.sendIndex} が重複しています。");
            }

            // EffectOverride
            var seenEffect = new HashSet<(string, int)>();
            for (int i = 0; i < effectOverrides.Count; i++)
            {
                var ov = effectOverrides[i];
                if (ov == null) continue;
                if (string.IsNullOrEmpty(ov.busName))
                {
                    errors.Add($"EffectOverride[{i}] に busName が未設定です。");
                    continue;
                }
                if (!known.Contains(ov.busName))
                {
                    errors.Add($"EffectOverride[{i}] の busName '{ov.busName}' が Mixer 内に見つかりません。");
                    continue;
                }
                NeziaMixerAsset.BusNode node = null;
                foreach (var n in mixer.Buses)
                    if (n != null && n.name == ov.busName) { node = n; break; }
                if (node == null) continue;
                if (node.effects == null || ov.effectIndex < 0 || ov.effectIndex >= node.effects.Count)
                {
                    errors.Add($"EffectOverride[{i}] の effectIndex {ov.effectIndex} が バス '{ov.busName}' のエフェクト数の範囲外です。");
                    continue;
                }
                var spec = node.effects[ov.effectIndex];
                if (spec == null) continue;
                if (spec.Kind != ov.Kind)
                    errors.Add($"EffectOverride[{i}] の kind ({ov.Kind}) が バス '{ov.busName}'[{ov.effectIndex}] の {spec.Kind} と一致しません。");
                if (!seenEffect.Add((ov.busName, ov.effectIndex)))
                    errors.Add($"EffectOverride[{i}] の (busName, effectIndex) = ('{ov.busName}', {ov.effectIndex}) が重複しています。");
            }
            return errors;
        }

        private void OnValidate()
        {
            foreach (var err in Validate())
                Debug.LogWarning($"[NeziaSnapshotAsset:{name}] {err}", this);
        }
#endif
    }
}
