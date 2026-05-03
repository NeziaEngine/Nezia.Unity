using Nezia.Native;
using UnityEngine;

namespace Nezia.Unity
{
    /// <summary>
    /// Unity の <c>AudioListener</c> をそのまま使えるようにするブリッジコンポーネント。
    ///
    /// <para>
    /// シーン内でアクティブな <see cref="NeziaAudioListener"/> のトランスフォームを
    /// 毎フレーム <c>nezia_listener_set</c> に転送する。<c>AudioListener</c> と同じ
    /// 場所に併設すれば、ユーザーは Nezia 固有のリスナー設定を意識せずに済む。
    /// </para>
    /// </summary>
    [AddComponentMenu("Nezia/Nezia Audio Listener")]
    public sealed class NeziaAudioListener : MonoBehaviour
    {
        private static NeziaAudioListener s_active;

        private void OnEnable()
        {
            if (s_active != null && s_active != this)
                Debug.LogWarning("[Nezia] Multiple NeziaAudioListener active. The latest one wins.", this);
            s_active = this;
        }

        private void OnDisable()
        {
            if (s_active == this) s_active = null;
        }

        internal static unsafe void PushActiveListener()
        {
            if (s_active == null) return;
            var t = s_active.transform;
            var p = t.position;
            var f = t.forward;
            var u = t.up;
            LibNezia.nezia_listener_set(
                NeziaEngine.RequireHandle(),
                new NeziaVec3 { x = p.x, y = p.y, z = p.z },
                new NeziaVec3 { x = f.x, y = f.y, z = f.z },
                new NeziaVec3 { x = u.x, y = u.y, z = u.z });
        }
    }
}
