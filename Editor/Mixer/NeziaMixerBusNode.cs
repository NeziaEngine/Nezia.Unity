using System;
using Unity.GraphToolkit.Editor;
using UnityEngine;

namespace Nezia.Unity.Editor.Mixer
{
    /// <summary>
    /// バスツリー上の 1 バスを表すノード（IP-12 PR-2）。
    ///
    /// <para>
    /// <b>ポート構成</b>:
    /// <list type="bullet">
    ///   <item><c>Parent</c>（input, single）— 親バスの <c>Output</c> ポートと接続する。
    ///         未接続なら master 直下扱いになる。</item>
    ///   <item><c>Output</c>（output, multi-out）— 子バスの <c>Parent</c> へ流す。
    ///         複数の子から同一バスを親として参照できる。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>パラメータ</b>: バス名 / Gain / Muted。Gain・Muted は GTK のインプットポートに
    /// 既定値として埋め込み、ノード上でインライン編集できるようにする。バス名は
    /// 構造同定キーなので素の <c>[SerializeField]</c> として持つ。
    /// </para>
    ///
    /// <para>
    /// PR-2 ではエフェクトチェーンと Aux Send は対象外。Effect は IP-12 PR-3、
    /// Send は IP-12 PR-4 で別ノード／別ポートとして拡張する。
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class NeziaMixerBusNode : Node
    {
        // ポート名定数。Importer / Validator から型安全に参照する。
        internal const string ParentPortName = "Parent";
        internal const string OutputPortName = "Output";
        internal const string GainPortName = "Gain";
        internal const string MutedPortName = "Muted";

        [SerializeField, Tooltip(
            "バスの論理名。NeziaSoundAsset.OutputBusName 等から参照されるキー。" +
            "アセット内で一意。空の場合は import 時にエラーが報告される。")]
        private string _busName = "Bus";

        /// <summary>このノードが表すバスの論理名。</summary>
        public string BusName => _busName;

        protected override void OnDefinePorts(IPortDefinitionContext context)
        {
            // 構造ポート: Parent (input / single) ↔ Output (output / multi-out)。
            context.AddInputPort<BusFlow>(ParentPortName).Build();
            context.AddOutputPort<BusFlow>(OutputPortName).Build();

            // 値ポート: Gain (float, default 1.0) と Muted (bool, default false)。
            // 入力ポートに既定値を埋め込むことで、ノード上でインライン編集が可能。
            context.AddInputPort<float>(GainPortName).WithDefaultValue(1f).Build();
            context.AddInputPort<bool>(MutedPortName).WithDefaultValue(false).Build();
        }
    }
}
