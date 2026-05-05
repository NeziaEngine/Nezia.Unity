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
    ///
    /// <para>
    /// 速度（Doppler 用）は前フレーム位置との差分を <c>Time.deltaTime</c> で割って
    /// 自動算出し、<c>nezia_listener_set_velocity</c> で publish する。
    /// </para>
    /// </summary>
    [AddComponentMenu("Nezia/Nezia Audio Listener")]
    public sealed class NeziaAudioListener : MonoBehaviour
    {
        private static NeziaAudioListener s_active;

        private Vector3 _prevPosition;
        private bool _hasPrevPosition;

        private void OnEnable()
        {
            if (s_active != null && s_active != this)
                Debug.LogWarning("[Nezia] Multiple NeziaAudioListener active. The latest one wins.", this);
            s_active = this;
            _hasPrevPosition = false;
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
            var engine = NeziaEngine.RequireHandle();
            LibNezia.nezia_listener_set(
                engine,
                new NeziaVec3 { x = p.x, y = p.y, z = p.z },
                new NeziaVec3 { x = f.x, y = f.y, z = f.z },
                new NeziaVec3 { x = u.x, y = u.y, z = u.z });

            var dt = Time.deltaTime;
            var v = (s_active._hasPrevPosition && dt > 0f)
                ? (p - s_active._prevPosition) / dt
                : Vector3.zero;
            s_active._prevPosition = p;
            s_active._hasPrevPosition = true;
            LibNezia.nezia_listener_set_velocity(
                engine, new NeziaVec3 { x = v.x, y = v.y, z = v.z });
        }
    }
}
