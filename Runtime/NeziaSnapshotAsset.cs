using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nezia.Unity
{
    /// <summary>
    /// <see cref="NeziaMixerAsset"/> 上のバス状態を Inspector で宣言し、
    /// ランタイムで <see cref="Apply"/> 一発に適用する Snapshot アセット
    /// （IP-3 PR-A スコープ: バスゲイン / ミュートのみ）。
    ///
    /// <para>
    /// AudioMixer の Snapshot に相当するデータ駆動レイヤ。<see cref="NeziaSnapshot"/>
    /// (`Begin().Set...Commit()`) を毎フレーム呼ぶ代わりに ScriptableObject に固定し、
    /// ゲームコードからは <c>asset.Apply(fadeSeconds)</c> だけで遷移できる。
    /// </para>
    ///
    /// <para>
    /// Phase 2 で Send ゲイン / エフェクトパラメータも受け取る予定。<see cref="BusOverride"/>
    /// は <c>overrideGain</c> / <c>overrideMuted</c> のフラグ別管理にして、
    /// 「ゲインだけ動かす」「ミュートだけ動かす」を独立に表現できる。
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

        [SerializeField, Tooltip("対象のミキサーアセット。Snapshot は同じ MixerAsset に解決されたバスに対して適用される。")]
        private NeziaMixerAsset mixer;

        [SerializeField] private List<BusOverride> busOverrides = new();

        public NeziaMixerAsset Mixer => mixer;
        public IReadOnlyList<BusOverride> BusOverrides => busOverrides;

        /// <summary>
        /// この Snapshot を <paramref name="fadeSeconds"/> かけて適用する（0 で即時）。
        ///
        /// <para>
        /// 内部的には <see cref="NeziaSnapshot.Begin"/> で Builder を生成し、
        /// <see cref="busOverrides"/> を <see cref="Mixer"/> 経由で解決して積み、
        /// Commit → Apply → Destroy までを 1 ステップで行う（永続ハンドルは保持しない）。
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
        /// 重複バス名指定を文字列リストで返す。<see cref="OnValidate"/> から自動で呼ばれる。
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
