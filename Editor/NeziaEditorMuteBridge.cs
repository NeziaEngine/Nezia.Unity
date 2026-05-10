using UnityEditor;

namespace Nezia.Unity.EditorOnly
{
    /// <summary>
    /// Game View ツールバーの「Mute Audio」ボタン (= <see cref="EditorUtility.audioMasterMute"/>)
    /// を Nezia の master volume にブリッジする Editor 専用フック。
    ///
    /// <para>
    /// Nezia は Unity の <c>AudioListener</c> 経由ではなく OS ネイティブ出力に直接書き込むため、
    /// Unity 標準のミュートでは音が止まらない。ここで mute フラグを監視し、
    /// <see cref="NeziaEngine.SetMasterVolumeScale"/> を 0/1 で切り替えて実効音量に反映する。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    internal static class NeziaEditorMuteBridge
    {
        private static bool s_lastMute;

        static NeziaEditorMuteBridge()
        {
            // 起動直後に必ず 1 回 push されるよう、現在の状態と逆をシードする。
            s_lastMute = !EditorUtility.audioMasterMute;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            // Play Mode 突入時はエンジンが Initialize 前後にあるので、確実に再 push する。
            if (change == PlayModeStateChange.EnteredPlayMode)
                Push(force: true);
        }

        private static void Tick()
        {
            Push(force: false);
        }

        private static void Push(bool force)
        {
            var muted = EditorUtility.audioMasterMute;
            if (!force && muted == s_lastMute) return;
            s_lastMute = muted;
            NeziaEngine.SetMasterVolumeScale(muted ? 0f : 1f);
        }
    }
}
