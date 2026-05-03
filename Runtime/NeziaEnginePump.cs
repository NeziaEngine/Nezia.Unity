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
            NeziaEngine.PollEvents();
            NeziaAudioListener.PushActiveListener();
        }
    }
}
