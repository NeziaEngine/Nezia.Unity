using UnityEngine;

namespace Nezia.Unity
{
    /// <summary>
    /// 毎フレームの末尾でイベントポンプを駆動する非表示コンポーネント。
    /// <see cref="NeziaEngine.Initialize"/> 内部から自動的に追加される。
    /// </summary>
    [AddComponentMenu("")]
    [DefaultExecutionOrder(int.MaxValue)]
    internal sealed class NeziaEnginePump : MonoBehaviour
    {
        private void LateUpdate()
        {
            // NeziaSpatialUpdater が TransformAccessArray + Burst Job で
            // 全 spatial source の position/velocity を並列収集し、1 回の FFI で送る。
            // この pump は DefaultExecutionOrder=int.MaxValue で最後に走るため、
            // 同フレーム内のすべての Register / Unregister がここまでに完了している。
            NeziaSpatialUpdater.Flush();
            NeziaEngine.PollEvents();
            NeziaAudioListener.PushActiveListener();
        }
    }
}
