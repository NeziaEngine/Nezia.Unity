using System.Globalization;
using System.Text;
using Nezia.Unity;

namespace Nezia.Unity.Editor.Preview
{
    /// <summary>
    /// IP-6: アセットを nezia-cli の protojson 入力 (MixerDef / ClipParams) へ変換する。
    ///
    /// JSON スキーマは nezia-core proto/nezia/v1/daemon.proto の定義そのもの
    /// (cli は protojson でそのままパースする)。フィールド名は lowerCamelCase。
    /// </summary>
    internal static class NeziaPreviewJson
    {
        // ─── MixerDef ─────────────────────────────────────────────

        /// <summary><see cref="NeziaMixerAsset"/> を MixerDef JSON へ変換する。</summary>
        internal static string BuildMixerJson(NeziaMixerAsset mixer)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"buses\":[");
            for (var i = 0; i < mixer.Buses.Count; i++)
            {
                var bus = mixer.Buses[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(Quote(bus.name));
                if (!string.IsNullOrEmpty(bus.parent))
                {
                    sb.Append(",\"parent\":").Append(Quote(bus.parent));
                }
                sb.Append(",\"gain\":").Append(F(bus.gain));
                if (bus.muted) sb.Append(",\"muted\":true");
                if (bus.effects != null && bus.effects.Count > 0)
                {
                    sb.Append(",\"effects\":[");
                    for (var e = 0; e < bus.effects.Count; e++)
                    {
                        if (e > 0) sb.Append(',');
                        AppendBusEffect(sb, bus.effects[e]);
                    }
                    sb.Append(']');
                }
                sb.Append('}');
            }
            sb.Append(']');

            if (mixer.Sends != null && mixer.Sends.Count > 0)
            {
                sb.Append(",\"sends\":[");
                for (var i = 0; i < mixer.Sends.Count; i++)
                {
                    var send = mixer.Sends[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"sourceBus\":").Append(Quote(send.source));
                    if (send.target == NeziaMixerAsset.SendTargetKind.CompressorSidechain)
                    {
                        sb.Append(",\"compressor\":{\"bus\":").Append(Quote(send.targetBus))
                            .Append(",\"effectIndex\":").Append(send.targetEffectIndex).Append('}');
                    }
                    else
                    {
                        sb.Append(",\"targetBus\":").Append(Quote(send.targetBus));
                    }
                    sb.Append(",\"position\":").Append(ChainPosition(send.position == NeziaSendPosition.Pre));
                    sb.Append(",\"gain\":").Append(F(send.gain)).Append('}');
                }
                sb.Append(']');
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendBusEffect(StringBuilder sb, NeziaMixerAsset.BusEffect effect)
        {
            sb.Append("{\"position\":")
                .Append(ChainPosition(effect.position == NeziaEffectPosition.Pre))
                .Append(",\"enabled\":").Append(effect.enabled ? "true" : "false");
            switch (effect)
            {
                case NeziaMixerAsset.LowPass lp:
                    sb.Append(",\"lowPass\":{\"cutoff\":").Append(F(lp.cutoff))
                        .Append(",\"q\":").Append(F(lp.q)).Append('}');
                    break;
                case NeziaMixerAsset.HighPass hp:
                    sb.Append(",\"highPass\":{\"cutoff\":").Append(F(hp.cutoff))
                        .Append(",\"q\":").Append(F(hp.q)).Append('}');
                    break;
                case NeziaMixerAsset.Reverb rv:
                    sb.Append(",\"reverb\":{\"roomSize\":").Append(F(rv.roomSize))
                        .Append(",\"damping\":").Append(F(rv.damping))
                        .Append(",\"wet\":").Append(F(rv.wet))
                        .Append(",\"dry\":").Append(F(rv.dry))
                        .Append(",\"width\":").Append(F(rv.width)).Append('}');
                    break;
                case NeziaMixerAsset.Compressor cp:
                    sb.Append(",\"compressor\":{\"thresholdDb\":").Append(F(cp.thresholdDb))
                        .Append(",\"ratio\":").Append(F(cp.ratio))
                        .Append(",\"attackMs\":").Append(F(cp.attackMs))
                        .Append(",\"releaseMs\":").Append(F(cp.releaseMs))
                        .Append(",\"kneeDb\":").Append(F(cp.kneeDb))
                        .Append(",\"makeupDb\":").Append(F(cp.makeupDb)).Append('}');
                    break;
            }
            sb.Append('}');
        }

        // ─── ClipParams ───────────────────────────────────────────

        /// <summary>
        /// <see cref="NeziaSoundAsset"/> の音響デフォルトを ClipParams JSON へ変換する。
        /// 適用対象: priority / spatial (SpatialBlend &gt; 0 のとき)。
        /// Effects / Sends の preview 反映は後続 PR (bus 専用種別の検証と合わせて対応)。
        /// カスタム減衰カーブは daemon 側未対応のため InverseDistance にフォールバックする。
        /// </summary>
        internal static string BuildClipJson(NeziaSoundAsset asset)
        {
            var sb = new StringBuilder(128);
            sb.Append('{');
            // proto 側の規約: priority 0 は「デフォルト 128」なので、最高優先度 0 は 1 に丸める。
            var priority = asset.Priority <= 0 ? 1 : asset.Priority;
            sb.Append("\"priority\":").Append(priority);

            if (asset.SpatialBlend > 0f)
            {
                var model = asset.RolloffMode switch
                {
                    NeziaRolloffMode.None => "ATTENUATION_MODEL_NONE",
                    NeziaRolloffMode.Linear => "ATTENUATION_MODEL_LINEAR",
                    NeziaRolloffMode.Exponential => "ATTENUATION_MODEL_EXPONENTIAL",
                    _ => "ATTENUATION_MODEL_INVERSE_DISTANCE",
                };
                sb.Append(",\"spatial\":{\"model\":\"").Append(model).Append('"')
                    .Append(",\"minDistance\":").Append(F(asset.MinDistance))
                    .Append(",\"maxDistance\":").Append(F(asset.MaxDistance))
                    .Append(",\"rolloff\":1.0")
                    .Append(",\"dopplerLevel\":")
                    .Append(F(UnityEngine.Mathf.Clamp01(asset.DopplerLevel)))
                    .Append('}');
            }
            sb.Append('}');
            return sb.ToString();
        }

        // ─── ヘルパ ───────────────────────────────────────────────

        private static string ChainPosition(bool pre) =>
            pre ? "\"CHAIN_POSITION_PRE\"" : "\"CHAIN_POSITION_POST\"";

        /// <summary>float を JSON 数値として安定表記する (カルチャ非依存)。</summary>
        private static string F(float value) =>
            value.ToString("0.0###", CultureInfo.InvariantCulture);

        /// <summary>JSON 文字列リテラルへの最小エスケープ。</summary>
        private static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
