using System;
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
    /// PR-1 (本 PR) ではグラフは空のスケルトン。Bus / Effect / Send ノードは
    /// 後続 PR (IP-12 PR-2 以降) で追加していく。
    /// </para>
    /// </summary>
    [Graph(AssetExtension), Serializable]
    public sealed class NeziaMixerGraph : Graph
    {
        /// <summary>
        /// アセット拡張子。<c>.neziamixer</c> ファイルとしてプロジェクト内に保存される。
        /// </summary>
        public const string AssetExtension = "neziamixer";
    }
}
