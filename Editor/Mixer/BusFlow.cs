using System;

namespace Nezia.Unity.Editor.Mixer
{
    /// <summary>
    /// バス間の親子関係（音響 routing）を示すポート型のマーカー。
    ///
    /// <para>
    /// GTK のポートには型パラメータが必須なので、Bus → Bus の構造的な
    /// 接続を表現するための空 struct を定義している。値そのものは保持せず、
    /// 「このポート同士は接続可能」という型システム上の宣言にだけ使う。
    /// </para>
    /// </summary>
    [Serializable]
    public struct BusFlow { }
}
