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
    /// PR-1 (本 PR) ではグラフが空なので生成される <see cref="NeziaMixerAsset"/> も
    /// 空 (<c>buses</c> / <c>sends</c> なし)。後続 PR で Bus / Effect / Send ノードを
    /// 追加し、ここで <see cref="NeziaMixerGraph"/> から読み出して
    /// <see cref="NeziaMixerAsset"/> へ compile する。
    /// </para>
    /// </summary>
    [ScriptedImporter(version: 1, ext: NeziaMixerGraph.AssetExtension)]
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
                CompileGraph(graph, asset);

            ctx.AddObjectToAsset("Mixer", asset);
            ctx.SetMainObject(asset);
        }

        /// <summary>
        /// <see cref="NeziaMixerGraph"/> の宣言内容を <see cref="NeziaMixerAsset"/> に書き出す。
        /// PR-1 では空グラフ前提のため no-op。後続 PR で Bus / Effect / Send ノードを
        /// 走査して <c>buses</c> / <c>sends</c> リストを構築する。
        /// </summary>
        private static void CompileGraph(NeziaMixerGraph graph, NeziaMixerAsset asset)
        {
            // intentionally empty until PR-2.
        }
    }
}
