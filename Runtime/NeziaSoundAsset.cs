using Nezia.Native;
using UnityEngine;

namespace Nezia.Unity
{
    /// <summary>
    /// 「鳴らす対象」を表す抽象アセット基底。
    ///
    /// <para>
    /// <see cref="NeziaAudioClip"/>（単一バッファ再生）と
    /// <see cref="NeziaRandomContainer"/>（ランダム選択再生）を統一的に扱うための基底型。
    /// <see cref="NeziaAudioSource"/> は具体型を意識せずこの型のフィールドだけを持つ。
    /// </para>
    ///
    /// <para>
    /// Wwise / FMOD / CRI ADX における「Sound 抽象 (Single + Container)」と同じ責務を持つ。
    /// 将来 Switch / Sequence Container を足す際もこの基底に差し込む。
    /// </para>
    /// </summary>
    public abstract class NeziaSoundAsset : ScriptableObject
    {
        /// <summary>このアセットの長さ（秒）。Container 等は 0 を返してよい。</summary>
        public virtual float Length => 0f;

        /// <summary>サンプルレート (Hz)。0 のときは <c>NeziaAudioSource.time</c> 計算で 44100 が代用される。</summary>
        public virtual int SampleRate => 0;

        /// <summary>
        /// この sound 経路でハンドル付きソースを spawn する。失敗時は INVALID。
        ///
        /// <para>
        /// <see cref="NeziaAudioSource"/> 内部用。具象側で
        /// <c>nezia_source_play_with_handle</c> または
        /// <c>nezia_container_play_with_handle</c> に分岐する。
        /// Container 経路では現状 <paramref name="callback"/> は未対応で無視される
        /// （natural finish の通知は受け取れず、<see cref="NeziaAudioSource"/> 側は
        /// alive ポーリングまたは明示 Stop に依存する）。
        /// </para>
        /// </summary>
        internal abstract unsafe NeziaEntityId Spawn(
            Nezia.Native.NeziaEngine* engine,
            float volume, float pitch,
            NeziaEntityId bus, bool looping,
            delegate* unmanaged[Cdecl]<void*, void> callback, void* userData);

        /// <summary>
        /// Container 経路かどうか。<see cref="NeziaAudioSource"/> 側で
        /// natural-finish callback の登録可否判定に使う。
        /// </summary>
        internal virtual bool SupportsFinishCallback => true;
    }
}
