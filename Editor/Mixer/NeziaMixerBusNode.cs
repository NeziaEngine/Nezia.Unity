using System;
using Unity.GraphToolkit.Editor;

namespace Nezia.Unity.Editor.Mixer
{
    /// <summary>
    /// バスツリー上の 1 バスを表すノード（IP-12 PR-2）。
    ///
    /// <para>
    /// <b>ポート</b>:
    /// <list type="bullet">
    ///   <item><c>Parent</c>（input, single）— 親バスの <c>Output</c> ポートと接続する。
    ///         未接続なら master 直下扱いになる。</item>
    ///   <item><c>Output</c>（output, multi-out）— 子バスの <c>Parent</c> へ流す。
    ///         複数の子から同一バスを親として参照できる。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Node Options</b>（ポートではない設定値）: <c>BusName</c> / <c>Gain</c> / <c>Muted</c>。
    /// 他ノードからの dynamic 駆動を想定しないバスのプロパティは GTK の Node Option として
    /// 宣言する。ノードヘッダ下と Inspector に表示され、エッジ接続の対象にはならない。
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
        // ポート / オプション名定数。Importer / Validator から型安全に参照する。
        internal const string ParentPortName = "Parent";
        internal const string OutputPortName = "Output";
        internal const string BusNameOptionName = "BusName";
        internal const string GainOptionName = "Gain";
        internal const string MutedOptionName = "Muted";

        internal const string DefaultBusName = "Bus";

        protected override void OnDefinePorts(IPortDefinitionContext context)
        {
            // 構造ポート: Parent (input / single) ↔ Output (output / multi-out)。
            context.AddInputPort<BusFlow>(ParentPortName).Build();
            context.AddOutputPort<BusFlow>(OutputPortName).Build();
        }

        protected override void OnDefineOptions(IOptionDefinitionContext context)
        {
            // バスのプロパティはエッジ接続不可な Node Option として宣言する。
            // ノードヘッダ下と Inspector の両方にインライン編集 UI が出る。
            context.AddOption<string>(BusNameOptionName)
                .WithDefaultValue(DefaultBusName)
                .WithTooltip("バスの論理名。NeziaSoundAsset.OutputBusName 等から参照されるキー。" +
                             "アセット内で一意。")
                .Build();

            context.AddOption<float>(GainOptionName)
                .WithDefaultValue(1f)
                .WithTooltip("バスゲイン (倍率)。1.0 = 0dB。")
                .Build();

            context.AddOption<bool>(MutedOptionName)
                .WithDefaultValue(false)
                .WithTooltip("ミュート初期値。")
                .Build();
        }

        /// <summary>このノードが表すバスの論理名。Node Option 値。</summary>
        public string BusName
        {
            get
            {
                var opt = GetNodeOptionByName(BusNameOptionName);
                return opt != null && opt.TryGetValue<string>(out var v) && v != null
                    ? v : string.Empty;
            }
        }
    }
}
