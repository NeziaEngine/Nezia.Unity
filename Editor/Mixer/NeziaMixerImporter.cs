using System.Collections.Generic;
using System.Linq;
using Unity.GraphToolkit.Editor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Nezia.Unity.Editor.Mixer
{
    /// <summary>
    /// <c>.neziamixer</c> ファイル（<see cref="NeziaMixerGraph"/>）から
    /// ランタイム参照用の <see cref="NeziaMixerAsset"/> を生成する ScriptedImporter。
    ///
    /// <para>
    /// Shader Graph の <c>.shadergraph → .shader</c> パターンを踏襲する。
    /// 1 つの <c>.neziamixer</c> ファイルに対して main asset として
    /// <see cref="NeziaMixerAsset"/> を出力するため、Project ビューでは
    /// 単一ファイルに見え、ランタイムは従来どおり <see cref="NeziaMixerAsset"/>
    /// 型でこのファイルを参照できる。
    /// </para>
    ///
    /// <para>
    /// PR-2 から <see cref="NeziaMixerBusNode"/> を走査してバスツリーを compile する。
    /// Effect / Send は IP-12 PR-3 / PR-4 で順次追加。
    /// </para>
    /// </summary>
    [ScriptedImporter(version: 2, ext: NeziaMixerGraph.AssetExtension)]
    public sealed class NeziaMixerImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx)
        {
            // GraphDatabase は import 専用に「クリーンなインスタンス」を返す。
            // 戻り値が null になる経路 (GTK 内部状態の都合) もありうるので保険的に扱う。
            var graph = GraphDatabase.LoadGraphForImporter<NeziaMixerGraph>(ctx.assetPath);

            var asset = ScriptableObject.CreateInstance<NeziaMixerAsset>();
            asset.name = System.IO.Path.GetFileNameWithoutExtension(ctx.assetPath);

            if (graph != null)
                CompileGraph(graph, asset, ctx);

            ctx.AddObjectToAsset("Mixer", asset);
            ctx.SetMainObject(asset);
        }

        /// <summary>
        /// <see cref="NeziaMixerGraph"/> 内の <see cref="NeziaMixerBusNode"/> を走査し、
        /// <see cref="NeziaMixerAsset"/> の <c>buses</c> リストを構築する。
        ///
        /// <para>
        /// 不正なグラフ（重複名 / 名前空 / 循環）も <see cref="NeziaMixerAsset"/> としては
        /// 生成し続ける。エラーは <see cref="AssetImportContext.LogImportError"/>
        /// で Console に通知し、グラフ側でも <see cref="NeziaMixerGraph.OnGraphChanged"/>
        /// が同様の警告を出す。
        /// </para>
        /// </summary>
        private static void CompileGraph(NeziaMixerGraph graph, NeziaMixerAsset asset, AssetImportContext ctx)
        {
            var busNodes = graph.GetNodes().OfType<NeziaMixerBusNode>().ToList();
            if (busNodes.Count == 0)
            {
                asset.SetBusesForImporter(new List<NeziaMixerAsset.BusNode>());
                return;
            }

            // 重複名検出は最初に O(N) で済ませて log し、以後は最初に出現したノードを採用する。
            var seenNames = new HashSet<string>();
            var compiled = new List<NeziaMixerAsset.BusNode>(busNodes.Count);
            foreach (var node in busNodes)
            {
                var name = node.BusName;
                if (string.IsNullOrEmpty(name))
                {
                    ctx.LogImportError($"[NeziaMixerImporter] Bus node has empty name; skipping.");
                    continue;
                }
                if (!seenNames.Add(name))
                {
                    ctx.LogImportError($"[NeziaMixerImporter] Duplicate bus name '{name}'; later occurrences are ignored.");
                    continue;
                }

                compiled.Add(new NeziaMixerAsset.BusNode
                {
                    name = name,
                    parent = ResolveParentName(node),
                    gain = ResolveFloatPort(node, NeziaMixerBusNode.GainPortName, 1f),
                    muted = ResolveBoolPort(node, NeziaMixerBusNode.MutedPortName, false),
                });
            }

            asset.SetBusesForImporter(compiled);
        }

        /// <summary>
        /// <see cref="NeziaMixerBusNode.ParentPortName"/> 入力ポートに接続されている親バスの
        /// 名前を返す。未接続なら空文字（master 直下）。
        /// </summary>
        private static string ResolveParentName(NeziaMixerBusNode node)
        {
            var parentPort = node.GetInputPortByName(NeziaMixerBusNode.ParentPortName);
            var source = parentPort?.firstConnectedPort;
            if (source?.GetNode() is NeziaMixerBusNode parentBus)
                return parentBus.BusName;
            return string.Empty;
        }

        private static float ResolveFloatPort(NeziaMixerBusNode node, string portName, float fallback)
        {
            var port = node.GetInputPortByName(portName);
            if (port == null) return fallback;
            return port.TryGetValue<float>(out var value) ? value : fallback;
        }

        private static bool ResolveBoolPort(NeziaMixerBusNode node, string portName, bool fallback)
        {
            var port = node.GetInputPortByName(portName);
            if (port == null) return fallback;
            return port.TryGetValue<bool>(out var value) ? value : fallback;
        }
    }
}
