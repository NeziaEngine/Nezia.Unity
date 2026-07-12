using UnityEditor;
using UnityEngine;

namespace Nezia.Unity.Editor.Preview
{
    /// <summary>
    /// IP-6: <see cref="NeziaSoundAsset"/> 系アセットの Inspector に試聴 UI を足す
    /// カスタムエディタ。再生は preview daemon へ委譲し、Editor プロセスでは
    /// 音声バイナリ / PCM を一切扱わない (integration-experience.md IP-6 の方針)。
    ///
    /// 本体のプロパティ描画はデフォルト Inspector に任せ、末尾に
    /// メタデータ表示 + Play / Stop を追加する構成。
    /// </summary>
    [CustomEditor(typeof(NeziaSoundAsset), editorForChildClasses: true)]
    public sealed class NeziaSoundAssetPreviewEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

            var asset = (NeziaSoundAsset)target;
            DrawMetadata(asset);
            DrawTransport(asset);
            DrawStatus();
        }

        private static void DrawMetadata(NeziaSoundAsset asset)
        {
            if (asset is NeziaAudioClip clip)
            {
                EditorGUILayout.LabelField(
                    $"{clip.Length:0.00}s  ·  {clip.SampleRate} Hz  ·  {clip.Channels} ch",
                    EditorStyles.miniLabel);
            }
            else if (asset is NeziaRandomContainer container)
            {
                EditorGUILayout.LabelField(
                    $"Random Container  ·  {container.Children.Count} children (再生ごとにランダム選択)",
                    EditorStyles.miniLabel);
            }
        }

        private static void DrawTransport(NeziaSoundAsset asset)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("▶ Play", GUILayout.Height(24)))
                {
                    NeziaPreviewSession.Play(asset);
                }
                if (GUILayout.Button("■ Stop", GUILayout.Height(24), GUILayout.Width(80)))
                {
                    NeziaPreviewSession.StopLast();
                }
                if (GUILayout.Button("Stop All", GUILayout.Height(24), GUILayout.Width(80)))
                {
                    NeziaPreviewSession.StopAll();
                }
            }
        }

        private static void DrawStatus()
        {
            if (NeziaPreviewSession.LastError == null)
            {
                return;
            }
            EditorGUILayout.HelpBox(NeziaPreviewSession.LastError, MessageType.Warning);

            // バイナリ未解決のときだけ、パス指定の導線を出す。
            if (NeziaCliClient.ResolveCliPath() == null &&
                GUILayout.Button("nezia-cli の場所を指定..."))
            {
                var path = EditorUtility.OpenFilePanel("nezia-cli を選択", "", "");
                if (!string.IsNullOrEmpty(path))
                {
                    NeziaCliClient.SetCliPath(path);
                }
            }
            if (NeziaCliClient.ResolveDaemonPath() == null &&
                GUILayout.Button("nezia-daemon の場所を指定..."))
            {
                var path = EditorUtility.OpenFilePanel("nezia-daemon を選択", "", "");
                if (!string.IsNullOrEmpty(path))
                {
                    NeziaCliClient.SetDaemonPath(path);
                }
            }
        }
    }
}
