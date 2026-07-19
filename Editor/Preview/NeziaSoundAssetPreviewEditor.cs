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
            // FillDefaultInspector は使わない: encodedBytes (mp3 生バイト列、BGM で
            // 数百万要素) の foldout を開いた瞬間に全要素の PropertyField を作ろうと
            // して Editor が 100% CPU で恒久フリーズするため、巨大配列だけ
            // サマリ行に置き換えて描画する。
            FillInspectorSkippingRawBytes(root.Q<VisualElement>("default-inspector"));

            BindPreview(root);

            // 表示された時点で daemon を暖めておく → Play クリック時は起動済み。
            NeziaPreviewSession.PrewarmAsync();

            return root;
        }

        /// <summary>
        /// デフォルト Inspector 相当を組み立てる。ただし <c>encodedBytes</c>
        /// (インポート済み音声の生バイト列) は要素数が数百万に達し、UI Toolkit の
        /// 配列 PropertyField 展開で Editor がフリーズするため、読み取り専用の
        /// サイズ表示 1 行に置き換える。
        /// </summary>
        private void FillInspectorSkippingRawBytes(VisualElement container)
        {
            var prop = serializedObject.GetIterator();
            if (!prop.NextVisible(enterChildren: true))
            {
                return;
            }
            do
            {
                if (prop.propertyPath == "encodedBytes")
                {
                    var bytes = prop.arraySize;
                    container.Add(new Label($"Encoded Bytes    {FormatSize(bytes)}")
                    {
                        style = { opacity = 0.6f, marginLeft = 3, marginTop = 1, marginBottom = 1 },
                    });
                    continue;
                }

                var field = new PropertyField(prop);
                if (prop.propertyPath == "m_Script")
                {
                    field.SetEnabled(false);
                }
                container.Add(field);
            } while (prop.NextVisible(enterChildren: false));
        }

        private static string FormatSize(int bytes) => bytes switch
        {
            >= 1024 * 1024 => $"{bytes / (1024f * 1024f):0.0} MB",
            >= 1024 => $"{bytes / 1024f:0.0} KB",
            _ => $"{bytes} B",
        };

        private void BindPreview(VisualElement root)
        {
            root.Q<Label>("metadata").text = MetadataText();
            BindWaveform(root.Q<VisualElement>("waveform-slot"));

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

        /// <summary>
        /// 波形スロットへ描画要素を差し込む。ピークは cli `peaks` (daemon 経由) で
        /// オンデマンド計算し、セッション内キャッシュする — 波形は Editor 機能
        /// なので front door (柱3) に従い FFI ではなく cli 経路を使う。
        /// Editor プロセスは PCM を扱わない (IP-6 方針)。
        /// Container 等の非クリップは波形自体を出さない。
        /// </summary>
        private void BindWaveform(VisualElement slot)
        {
            if (target is not NeziaAudioClip clip)
            {
                return;
            }
            var assetPath = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(assetPath))
            {
                return;
            }

            var placeholder = new Label("波形を読み込み中…");
            placeholder.AddToClassList("preview__waveform-placeholder");
            slot.Add(placeholder);

            NeziaPreviewSession.GetPeaksAsync(assetPath, peaks =>
            {
                // Inspector が閉じられた後のコールバックは無視する
                // (detached な VisualElement を触っても実害はないが無駄)。
                if (slot.panel == null)
                {
                    return;
                }
                slot.Clear();
                if (peaks is { Length: > 0 })
                {
                    slot.Add(new NeziaWaveformElement(peaks));
                }
                else
                {
                    var failed = new Label("波形なし (daemon 未到達)");
                    failed.AddToClassList("preview__waveform-placeholder");
                    slot.Add(failed);
                }
            });
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
