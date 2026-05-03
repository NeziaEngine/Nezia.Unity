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
            // 各 NeziaAudioSource.LateUpdate で積まれた位置更新を 1 回の FFI 呼び出しで流し込む。
            // この pump は DefaultExecutionOrder=int.MaxValue で最後に走るため、
            // 同フレーム内のすべての PushPosition がここまでに完了している。
            NeziaAudioSource.FlushPendingPositions();
            NeziaEngine.PollEvents();
            NeziaAudioListener.PushActiveListener();
        }
    }
}
