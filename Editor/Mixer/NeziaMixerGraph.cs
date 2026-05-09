using System;
using System.Collections.Generic;
using System.Linq;
using Unity.GraphToolkit.Editor;

namespace Nezia.Unity.Editor.Mixer
{
    /// <summary>
    /// Graph Toolkit ベースのバスツリー編集グラフ（IP-12）。
    ///
    /// <para>
    /// このクラス自体は <c>Unity.GraphToolkit.Editor</c> 名前空間に閉じた authoring 用の
    /// 表現で、ランタイムから直接参照しない。Project ビューで
    /// <see cref="AssetExtension"/> を持つファイルとしてシリアライズされ、
    /// インポート時に <see cref="NeziaMixerImporter"/> が走って中身を
    /// <see cref="NeziaMixerAsset"/>（Runtime SO）として main asset に書き出す。
    /// </para>
    ///
    /// <para>
    /// PR-2 からは <see cref="NeziaMixerBusNode"/> を扱う。<see cref="OnGraphChanged"/>
    /// で重複名 / 名前空 / 親子循環を validate し、対象ノード上にエラーマーカーを表示する。
    /// </para>
    /// </summary>
    [Graph(AssetExtension), Serializable]
    public sealed class NeziaMixerGraph : Graph
    {
        /// <summary>
        /// アセット拡張子。<c>.neziamixer</c> ファイルとしてプロジェクト内に保存される。
        /// </summary>
        public const string AssetExtension = "neziamixer";

        public override void OnGraphChanged(GraphLogger logger)
        {
            ValidateBusNodes(logger);
        }

        private void ValidateBusNodes(GraphLogger logger)
        {
            var busNodes = GetNodes().OfType<NeziaMixerBusNode>().ToList();
            if (busNodes.Count == 0) return;

            // ── 名前バリデーション (空名 / 重複名)──────────────────
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in busNodes)
            {
                if (string.IsNullOrEmpty(node.BusName))
                {
                    logger.LogError("Bus node が空の名前を持っています。名前を設定してください。", node);
                    continue;
                }
                if (!seen.Add(node.BusName))
                    logger.LogError($"バス名 '{node.BusName}' が重複しています。名前は一意である必要があります。", node);
            }

            // ── 親子循環検出 ──────────────────────────────────────
            //
            // 各ノードから Parent ポート経由で親をたどり、自分自身に戻るパスがあれば循環。
            foreach (var node in busNodes)
            {
                var visiting = new HashSet<NeziaMixerBusNode>();
                var current = node;
                while (current != null)
                {
                    if (!visiting.Add(current))
                    {
                        logger.LogError($"バス '{node.BusName}' から循環参照を検出しました。", node);
                        break;
                    }
                    var parentPort = current.GetInputPortByName(NeziaMixerBusNode.ParentPortName);
                    var source = parentPort?.firstConnectedPort;
                    current = source?.GetNode() as NeziaMixerBusNode;
                }
            }
        }
    }
}
