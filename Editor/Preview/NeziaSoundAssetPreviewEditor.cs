using System;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Nezia.Unity.Editor.Preview
{
    /// <summary>
    /// IP-6: <see cref="NeziaSoundAsset"/> 系アセットの Inspector に試聴 UI を足す
    /// カスタムエディタ (UI Toolkit / UXML)。再生は preview daemon へ委譲し、
    /// Editor プロセスでは音声バイナリ / PCM を一切扱わない (integration-experience.md IP-6)。
    ///
    /// <para>
    /// レイアウトは <c>NeziaSoundAssetPreview.uxml</c> / <c>.uss</c> に分離し、この
    /// クラスは読み込みと配線・状態更新だけを担う。再生は
    /// <see cref="NeziaPreviewSession.PlayAsync"/> で非同期に行い UI をブロックしない。
    /// Inspector 表示時に daemon をプリウォームして初回クリックのコールドスタートを消す。
    /// </para>
    /// </summary>
    [CustomEditor(typeof(NeziaSoundAsset), editorForChildClasses: true)]
    public sealed class NeziaSoundAssetPreviewEditor : UnityEditor.Editor
    {
        private const string UxmlPath =
            "Packages/jp.nezia.unity/Editor/Preview/NeziaSoundAssetPreview.uxml";
        private const string UssPath =
            "Packages/jp.nezia.unity/Editor/Preview/NeziaSoundAssetPreview.uss";

        private Button _playButton;
        private Label _statusLabel;
        private HelpBox _errorBox;
        private Button _cliPathButton;
        private Button _daemonPathButton;

        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();

            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (uxml == null)
            {
                root.Add(new HelpBox($"UXML が見つかりません: {UxmlPath}", HelpBoxMessageType.Error));
                return root;
            }
            uxml.CloneTree(root);

            var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (uss != null)
            {
                root.styleSheets.Add(uss);
            }

            // 既定のプロパティ描画 (import 済みアセットは read-only になる)。
            InspectorElement.FillDefaultInspector(
                root.Q<VisualElement>("default-inspector"), serializedObject, this);

            BindPreview(root);

            // 表示された時点で daemon を暖めておく → Play クリック時は起動済み。
            NeziaPreviewSession.PrewarmAsync();

            return root;
        }

        private void BindPreview(VisualElement root)
        {
            root.Q<Label>("metadata").text = MetadataText();

            _playButton = root.Q<Button>("play");
            _playButton.clicked += OnPlayClicked;

            root.Q<Button>("stop").clicked += () =>
            {
                NeziaPreviewSession.StopLast();
                RefreshState();
            };
            root.Q<Button>("stop-all").clicked += () =>
            {
                NeziaPreviewSession.StopAll();
                RefreshState();
            };

            _statusLabel = root.Q<Label>("status");

            // HelpBox はこの Unity バージョンで UXML から生成できないため C# 側で作り、
            // UXML に用意したスロットへ差し込む (表示順は UXML が決める)。
            _errorBox = new HelpBox(string.Empty, HelpBoxMessageType.Warning);
            _errorBox.AddToClassList("preview__error");
            root.Q<VisualElement>("error-slot").Add(_errorBox);

            _cliPathButton = root.Q<Button>("pick-cli");
            _cliPathButton.clicked += () => PickBinary("nezia-cli", NeziaCliClient.SetCliPath);
            _daemonPathButton = root.Q<Button>("pick-daemon");
            _daemonPathButton.clicked += () => PickBinary("nezia-daemon", NeziaCliClient.SetDaemonPath);

            // ScriptedImporter は import 済みオブジェクトを read-only で描画するため、
            // 何もしないと試聴ボタンまで disabled になる。試聴は import 済みバイナリを
            // 書き換えないので、この操作行だけは常に有効化しておく。
            root.Q<VisualElement>("transport").SetEnabled(true);

            RefreshState();
        }

        private string MetadataText() => target switch
        {
            NeziaAudioClip clip =>
                $"{clip.Length:0.00}s  ·  {clip.SampleRate} Hz  ·  {clip.Channels} ch",
            NeziaRandomContainer container =>
                $"Random Container  ·  {container.Children.Count} children (再生ごとにランダム選択)",
            _ => string.Empty,
        };

        private void OnPlayClicked()
        {
            NeziaPreviewSession.PlayAsync((NeziaSoundAsset)target, RefreshState);
            RefreshState();
        }

        /// <summary>セッション状態に合わせてボタン活性・ステータス・エラー表示を更新する。</summary>
        private void RefreshState()
        {
            if (_playButton == null)
            {
                return;
            }

            var busy = NeziaPreviewSession.IsBusy;
            _playButton.SetEnabled(!busy);
            _playButton.text = busy ? "… 準備中" : "▶ Play";

            var status = NeziaPreviewSession.StatusText;
            SetVisible(_statusLabel, !string.IsNullOrEmpty(status));
            _statusLabel.text = status ?? string.Empty;

            var error = NeziaPreviewSession.LastError;
            SetVisible(_errorBox, !string.IsNullOrEmpty(error));
            _errorBox.text = error ?? string.Empty;

            // バイナリ未解決のときだけ、パス指定の導線を出す。
            SetVisible(_cliPathButton,
                !string.IsNullOrEmpty(error) && NeziaCliClient.ResolveCliPath() == null);
            SetVisible(_daemonPathButton,
                !string.IsNullOrEmpty(error) && NeziaCliClient.ResolveDaemonPath() == null);
        }

        private void PickBinary(string label, Action<string> setter)
        {
            var path = EditorUtility.OpenFilePanel($"{label} を選択", "", "");
            if (!string.IsNullOrEmpty(path))
            {
                setter(path);
                RefreshState();
            }
        }

        private static void SetVisible(VisualElement element, bool visible) =>
            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
    }
}
