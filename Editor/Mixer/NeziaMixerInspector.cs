using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nezia.Unity.Editor.Mixer
{
    /// <summary>
    /// <see cref="NeziaMixerAsset"/> 専用の Custom Inspector（IP-12 PR-A / PR-B）。
    ///
    /// <para>
    /// バスツリーを編集するための「Hierarchy ペイン + プロパティパネル + Send タブ」UX を
    /// Inspector として提供する。すべて UI Toolkit (`TreeView` / `Slider` / `Toggle` 等) で
    /// 実装。Project ビューで <c>NeziaMixerAsset</c> を選択すると
    /// 通常の Inspector パネル内にこの編集 UI が出る。<c>Project Settings &gt; Nezia</c> の
    /// inline Inspector / 2 つ目の Inspector を立てた工程内編集 / lock 機能 etc. すべて
    /// 標準 Unity の作法に乗る。
    /// </para>
    ///
    /// <para>
    /// <b>PR-B 範囲</b>: バス追加・削除・リネーム・属性編集 (gain / muted)・親変更
    /// (drag &amp; drop)・<see cref="Undo"/> 対応・<see cref="NeziaMixerAsset.Validate"/>
    /// 結果のフッタ表示。Effect chain は IP-12 PR-C で、Send / sidechain は PR-D で
    /// 専用タブとして追加済み。
    /// </para>
    /// </summary>
    [CustomEditor(typeof(NeziaMixerAsset))]
    public sealed class NeziaMixerInspector : UnityEditor.Editor
    {
        private enum Tab { Buses, Sends }

        // ─── 状態 ────────────────────────────────────────────────

        [SerializeField] private int _selectedBusIndex = -1;
        [SerializeField] private Tab _activeTab = Tab.Buses;

        // ── UI 参照 ──
        private VisualElement _busesView;
        private VisualElement _sendsView;
        private Button _busesTabBtn;
        private Button _sendsTabBtn;
        private TreeView _treeView;
        private VisualElement _inspectorRoot;
        private Label _inspectorPlaceholder;
        private TextField _nameField;
        private Slider _gainSlider;
        private FloatField _gainNumeric;
        private Toggle _mutedToggle;
        private Button _deleteBusButton;
        private Label _inspectorTitle;
        private VisualElement _effectsRoot;
        private VisualElement _sendsListRoot;
        private VisualElement _validationFooter;

        private bool _suspendBindCallbacks;

        // TreeView の id 衝突回避: Master 仮想ルート = 1, 実バス i = i + BusIdOffset(2)。
        // 0 / 負数は TreeView 内部 sentinel と被ることがあるため避ける。
        private const int MasterItemId = 1;
        private const int BusIdOffset = 2;

        private static int IdForBus(int busIndex) => busIndex + BusIdOffset;
        private static int BusIndexFromId(int id) => id - BusIdOffset;

        private NeziaMixerAsset Asset => target as NeziaMixerAsset;

        // ─── lifecycle ───────────────────────────────────────────

        private void OnEnable()
        {
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
        }

        private void OnUndoRedo()
        {
            RefreshTree();
            UpdateRightPane();
            RefreshSendsList();
            UpdateValidationFooter();
        }

        public override VisualElement CreateInspectorGUI()
        {
            // レイアウト方針:
            //   root (column, height 固定 = Inspector ウィンドウ側の縦スクロールを止める)
            //     ├─ Toolbar          : 自然高さ (flexShrink=0)
            //     ├─ TreeView ペイン  : 固定高さ
            //     ├─ Separator        : 1px ライン
            //     ├─ Inspector ペイン : flexGrow=1 で残りの縦スペースを全部取る ScrollView
            //     └─ Validation footer: 自然高さ (flexShrink=0)
            //
            // root に height を明示する理由:
            //   Custom Inspector の親 (InspectorElement) は子の自然サイズに合わせて
            //   伸びる。root を flexGrow にすると Inspector ウィンドウ側 (外側) の
            //   ScrollView がスクロールしてしまい、「下ペインだけスクロール」にならない。
            //   ただし固定値だとウィンドウサイズに追随せず、余白や切れが発生する。
            //   そこで AttachToPanelEvent で panel ルートに購読し、Inspector ウィンドウの
            //   可視高さへ動的に同期する (BindRootHeightToInspectorWindow を参照)。
            //
            // TwoPaneSplitView は採用しない: 絶対配置による flex 連鎖の切断と drag-line
            // の被りで下ペインが正しく伸びないケースがあり、Inspector では利得が薄い。
            var root = new VisualElement();
            root.style.flexDirection = FlexDirection.Column;
            root.style.flexShrink = 0f;
            BindRootHeightToInspectorWindow(root);

            BuildTabStrip(root);

            // Buses タブ: Toolbar + TreeView + 区切り + Inspector ペイン。
            _busesView = new VisualElement();
            _busesView.style.flexDirection = FlexDirection.Column;
            _busesView.style.flexGrow = 1f;
            _busesView.style.flexShrink = 1f;
            root.Add(_busesView);
            BuildToolbar(_busesView);
            BuildTreePane(_busesView);
            BuildPaneSeparator(_busesView);
            BuildInspectorPane(_busesView);

            // Sends タブ: 配線リストの ScrollView。
            _sendsView = new VisualElement();
            _sendsView.style.flexDirection = FlexDirection.Column;
            _sendsView.style.flexGrow = 1f;
            _sendsView.style.flexShrink = 1f;
            root.Add(_sendsView);
            BuildSendsPane(_sendsView);

            BuildValidationFooter(root);

            ApplyActiveTab();
            // delayCall は Inspector のライフサイクルと相性が悪い (再構築毎に古い
            // callback が残る) ため、即時で初期描画する。
            RefreshTree();
            UpdateRightPane();
            RefreshSendsList();
            UpdateValidationFooter();

            return root;
        }

        // ─── UI 構築 ─────────────────────────────────────────────

        /// <summary>
        /// Inspector ウィンドウの可視領域に対し、root の <c>style.height</c> を
        /// 「下チローム (Asset Labels 等) のすぐ上まで」自動追随させる。
        /// </summary>
        /// <remarks>
        /// Custom Inspector の親はコンテンツ自然サイズで配置するため、root が flexGrow
        /// だけ持っていてもウィンドウ高さを取れない。一方で固定 (panel高さ − 定数) では
        /// アセットヘッダ・Asset Labels・AssetBundle 行などの実サイズと合わず、外側
        /// スクロールが出るか余白が残る。
        ///
        /// そこで root の panel 上の位置 (<see cref="VisualElement.worldBound"/>) を毎フレーム
        /// 取り、panel 高さから「root より上のチローム」を差し引いた残りを高さに設定する。
        /// 下側チローム分は <paramref name="BottomReservePx"/> で別途確保。
        /// </remarks>
        private static void BindRootHeightToInspectorWindow(VisualElement root)
        {
            // Asset Labels + AssetBundle 行 + Addressables (任意) のおおよその合計。
            const float BottomReservePx = 90f;
            const float MinHeightPx = 240f;

            void Sync()
            {
                var panelRoot = root.panel?.visualTree;
                if (panelRoot == null) return;
                var panelH = panelRoot.resolvedStyle.height;
                if (panelH <= 0f) return;

                // root の上端 (panel 座標) より上にあるアセットヘッダ等の高さ。
                var topOffset = root.worldBound.yMin - panelRoot.worldBound.yMin;
                if (float.IsNaN(topOffset) || topOffset < 0f) topOffset = 0f;

                var available = panelH - topOffset - BottomReservePx;
                root.style.height = Mathf.Max(MinHeightPx, available);
            }

            EventCallback<GeometryChangedEvent> onGeo = null;
            VisualElement boundPanelRoot = null;

            root.RegisterCallback<AttachToPanelEvent>(_ =>
            {
                boundPanelRoot = root.panel?.visualTree;
                if (boundPanelRoot == null) return;
                onGeo = __ => Sync();
                boundPanelRoot.RegisterCallback(onGeo);
                // root 自身の geometry でも再計算 (上のチロームが伸縮した時の追随)。
                root.RegisterCallback<GeometryChangedEvent>(onGeo);
                root.schedule.Execute(Sync).StartingIn(0);
            });
            root.RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                if (boundPanelRoot != null && onGeo != null)
                {
                    boundPanelRoot.UnregisterCallback(onGeo);
                    root.UnregisterCallback(onGeo);
                }
                boundPanelRoot = null;
                onGeo = null;
            });
        }

        // ─── タブストリップ ────────────────────────────────────────

        private void BuildTabStrip(VisualElement root)
        {
            var strip = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexShrink = 0f,
                    borderBottomWidth = 1f,
                    borderBottomColor = new Color(0f, 0f, 0f, 0.3f),
                    marginBottom = 2f,
                },
            };

            _busesTabBtn = new Button(() => SetActiveTab(Tab.Buses)) { text = "Buses" };
            _sendsTabBtn = new Button(() => SetActiveTab(Tab.Sends)) { text = "Sends" };
            foreach (var btn in new[] { _busesTabBtn, _sendsTabBtn })
            {
                btn.style.flexGrow = 1f;
                btn.style.marginLeft = 0f;
                btn.style.marginRight = 0f;
                btn.style.marginTop = 0f;
                btn.style.marginBottom = 0f;
                btn.style.borderTopLeftRadius = 0f;
                btn.style.borderTopRightRadius = 0f;
                btn.style.borderBottomLeftRadius = 0f;
                btn.style.borderBottomRightRadius = 0f;
                strip.Add(btn);
            }
            root.Add(strip);
        }

        private void SetActiveTab(Tab tab)
        {
            if (_activeTab == tab) return;
            _activeTab = tab;
            ApplyActiveTab();
        }

        private void ApplyActiveTab()
        {
            if (_busesView != null)
                _busesView.style.display = _activeTab == Tab.Buses ? DisplayStyle.Flex : DisplayStyle.None;
            if (_sendsView != null)
                _sendsView.style.display = _activeTab == Tab.Sends ? DisplayStyle.Flex : DisplayStyle.None;

            // タブのアクティブ強調 (UI Toolkit の Button にデフォルト active 表現が
            // 無いため、フォントウェイトで区別する)。
            if (_busesTabBtn != null)
                _busesTabBtn.style.unityFontStyleAndWeight =
                    _activeTab == Tab.Buses ? FontStyle.Bold : FontStyle.Normal;
            if (_sendsTabBtn != null)
                _sendsTabBtn.style.unityFontStyleAndWeight =
                    _activeTab == Tab.Sends ? FontStyle.Bold : FontStyle.Normal;

            if (_activeTab == Tab.Sends) RefreshSendsList();
        }

        private void BuildToolbar(VisualElement root)
        {
            var toolbar = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexShrink = 0f,
                    paddingTop = 4f,
                    paddingBottom = 4f,
                },
            };

            var addBtn = new Button(AddBus) { text = "+ Add Bus" };
            addBtn.tooltip = "選択中のバスと同じ階層に新しい Bus を追加します（未選択なら master 直下）。";
            toolbar.Add(addBtn);

            _deleteBusButton = new Button(DeleteSelectedBus) { text = "− Delete" };
            _deleteBusButton.tooltip = "選択中の Bus を削除します。子バスは削除対象の親に再 parent されます。";
            toolbar.Add(_deleteBusButton);

            root.Add(toolbar);
        }

        private void BuildTreePane(VisualElement root)
        {
            var pane = new VisualElement();
            pane.style.height = 180f;
            pane.style.flexShrink = 0f;
            root.Add(pane);

            _treeView = new TreeView
            {
                fixedItemHeight = 22f,
                selectionType = SelectionType.Single,
                makeItem = MakeBusRow,
                bindItem = BindBusRow,
            };
            _treeView.style.flexGrow = 1f;
            _treeView.style.flexShrink = 1f;
            _treeView.selectionChanged += _ => OnTreeSelectionChanged();
            ConfigureTreeDragAndDrop(_treeView);
            pane.Add(_treeView);
        }

        private static void BuildPaneSeparator(VisualElement root)
        {
            var sep = new VisualElement();
            sep.style.height = 1f;
            sep.style.flexShrink = 0f;
            sep.style.backgroundColor = new Color(0f, 0f, 0f, 0.3f);
            sep.style.marginTop = 2f;
            sep.style.marginBottom = 2f;
            root.Add(sep);
        }

        private void BuildInspectorPane(VisualElement root)
        {
            // ペイン全体 (Name/Gain/Muted/Effects まで含めて) を ScrollView で包み、
            // 縦が溢れたらここだけスクロールする。Toolbar / TreeView / Footer は
            // 外側に残るので、画面に常駐する。
            var pane = new ScrollView(ScrollViewMode.Vertical);
            pane.style.flexGrow = 1f;
            pane.style.flexShrink = 1f;
            // ScrollView 内部の content は既定で natural size。padding はここに乗せる。
            var content = pane.contentContainer;
            content.style.flexGrow = 1f;
            content.style.paddingTop = 6f;
            content.style.paddingLeft = 10f;
            content.style.paddingRight = 10f;
            content.style.paddingBottom = 6f;
            root.Add(pane);

            _inspectorPlaceholder = new Label("バスを選択するとプロパティを編集できます。");
            _inspectorPlaceholder.style.color = new Color(0.6f, 0.6f, 0.6f);
            _inspectorPlaceholder.style.whiteSpace = WhiteSpace.Normal;
            pane.Add(_inspectorPlaceholder);

            _inspectorRoot = new VisualElement();
            _inspectorRoot.style.flexGrow = 1f;
            _inspectorRoot.style.flexShrink = 1f;
            _inspectorRoot.style.flexDirection = FlexDirection.Column;
            pane.Add(_inspectorRoot);

            _inspectorTitle = new Label("Bus");
            _inspectorTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            _inspectorTitle.style.fontSize = 13f;
            _inspectorTitle.style.marginBottom = 6f;
            _inspectorTitle.style.flexShrink = 0f;
            _inspectorRoot.Add(_inspectorTitle);

            _nameField = new TextField("Name") { isDelayed = true };
            _nameField.style.flexShrink = 0f;
            _nameField.RegisterValueChangedCallback(evt =>
            {
                if (_suspendBindCallbacks) return;
                CommitNameChange(evt.newValue);
            });
            _inspectorRoot.Add(_nameField);

            var gainRow = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexShrink = 0f },
            };
            _gainSlider = new Slider("Gain", 0f, 4f) { value = 1f };
            _gainSlider.style.flexGrow = 1f;
            _gainSlider.RegisterValueChangedCallback(evt =>
            {
                if (_suspendBindCallbacks) return;
                CommitGainChange(evt.newValue);
            });
            gainRow.Add(_gainSlider);

            _gainNumeric = new FloatField { value = 1f, isDelayed = true };
            _gainNumeric.style.width = 60f;
            _gainNumeric.style.marginLeft = 4f;
            _gainNumeric.RegisterValueChangedCallback(evt =>
            {
                if (_suspendBindCallbacks) return;
                CommitGainChange(Mathf.Clamp(evt.newValue, 0f, 4f));
            });
            gainRow.Add(_gainNumeric);
            _inspectorRoot.Add(gainRow);

            _mutedToggle = new Toggle("Muted");
            _mutedToggle.style.flexShrink = 0f;
            _mutedToggle.RegisterValueChangedCallback(evt =>
            {
                if (_suspendBindCallbacks) return;
                CommitMutedChange(evt.newValue);
            });
            _inspectorRoot.Add(_mutedToggle);

            BuildEffectsSection(_inspectorRoot);
        }

        // ─── Sends タブ (IP-12 PR-D) ─────────────────────────────

        private void BuildSendsPane(VisualElement parent)
        {
            // ヘッダ (＋ Add Send) は固定、リストは ScrollView で内側スクロール。
            var header = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    flexShrink = 0f,
                    paddingTop = 6f,
                    paddingLeft = 10f,
                    paddingRight = 10f,
                    paddingBottom = 4f,
                },
            };
            var title = new Label("Sends");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 13f;
            title.style.flexGrow = 1f;
            header.Add(title);

            var addBtn = new Button(AddSend) { text = "+ Add Send" };
            addBtn.tooltip = "Send 配線を追加します。source / target は追加後に編集してください。";
            header.Add(addBtn);
            parent.Add(header);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1f;
            scroll.style.flexShrink = 1f;
            var content = scroll.contentContainer;
            content.style.flexGrow = 1f;
            content.style.paddingLeft = 10f;
            content.style.paddingRight = 10f;
            content.style.paddingBottom = 6f;
            parent.Add(scroll);

            _sendsListRoot = new VisualElement();
            _sendsListRoot.style.flexShrink = 0f;
            scroll.Add(_sendsListRoot);
        }

        private void RefreshSendsList()
        {
            if (_sendsListRoot == null) return;
            _sendsListRoot.Clear();

            var asset = Asset;
            if (asset == null) return;
            var sends = asset.Sends;
            if (sends == null || sends.Count == 0)
            {
                var empty = new Label("No sends. Click + Add Send to wire one.");
                empty.style.color = new Color(0.6f, 0.6f, 0.6f);
                empty.style.marginTop = 4f;
                _sendsListRoot.Add(empty);
                return;
            }

            var busNames = asset.Buses
                .Where(b => b != null && !string.IsNullOrEmpty(b.name))
                .Select(b => b.name)
                .ToList();

            for (int i = 0; i < sends.Count; i++)
            {
                _sendsListRoot.Add(BuildSendRow(asset, sends[i], i, busNames));
            }
        }

        private VisualElement BuildSendRow(NeziaMixerAsset asset, NeziaMixerAsset.SendNode send, int index, List<string> busNames)
        {
            var row = new VisualElement();
            row.style.marginTop = 4f;
            row.style.borderTopWidth = 1f;
            row.style.borderBottomWidth = 1f;
            row.style.borderLeftWidth = 1f;
            row.style.borderRightWidth = 1f;
            var border = new Color(0f, 0f, 0f, 0.25f);
            row.style.borderTopColor = row.style.borderBottomColor = border;
            row.style.borderLeftColor = row.style.borderRightColor = border;
            row.style.paddingTop = 4f;
            row.style.paddingBottom = 4f;
            row.style.paddingLeft = 6f;
            row.style.paddingRight = 6f;

            // ── 行 1: source → target kind / target bus  [×]
            var line1 = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 4f },
            };

            var sourceField = MakeBusDropdown(busNames, send.source, v =>
            {
                Undo.RecordObject(asset, "Edit Send Source");
                send.source = v;
                ApplySendValueChange();
            });
            sourceField.style.flexGrow = 1f;
            line1.Add(sourceField);

            var arrow = new Label("→") { style = { marginLeft = 4f, marginRight = 4f } };
            line1.Add(arrow);

            var targetKindField = new EnumField(send.target);
            targetKindField.style.width = 130f;
            targetKindField.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(asset, "Edit Send Target Kind");
                send.target = (NeziaMixerAsset.SendTargetKind)evt.newValue;
                // sidechain picker の表示/非表示が変わるため行を再構築する。
                ApplySendStructuralChange();
            });
            line1.Add(targetKindField);

            var targetBusField = MakeBusDropdown(busNames, send.targetBus, v =>
            {
                Undo.RecordObject(asset, "Edit Send Target Bus");
                send.targetBus = v;
                // sidechain 時は Compressor 一覧が変わるので再構築。Bus 時は不要だが
                // 区別を付けると分岐が増えるので一律で再構築 (リスト規模が小さく問題ない)。
                ApplySendStructuralChange();
            });
            targetBusField.style.flexGrow = 1f;
            targetBusField.style.marginLeft = 4f;
            line1.Add(targetBusField);

            var removeBtn = new Button(() => RemoveSend(index)) { text = "×" };
            removeBtn.style.width = 24f;
            removeBtn.style.marginLeft = 4f;
            line1.Add(removeBtn);

            row.Add(line1);

            // ── 行 2: (sidechain 時のみ) target effect index ──
            if (send.target == NeziaMixerAsset.SendTargetKind.CompressorSidechain)
            {
                var effectChoices = BuildCompressorChoices(asset, send.targetBus);
                if (effectChoices.Count == 0)
                {
                    var note = new Label("対象バスに Compressor がありません。Buses タブで追加してください。");
                    note.style.color = new Color(0.95f, 0.7f, 0.2f);
                    note.style.whiteSpace = WhiteSpace.Normal;
                    note.style.marginBottom = 4f;
                    row.Add(note);
                }
                else
                {
                    var current = effectChoices.FirstOrDefault(c => c.index == send.targetEffectIndex);
                    var defaultLabel = current.label ?? effectChoices[0].label;
                    var labels = effectChoices.Select(c => c.label).ToList();
                    var picker = new DropdownField("Sidechain Target", labels, defaultLabel);
                    picker.RegisterValueChangedCallback(evt =>
                    {
                        var hit = effectChoices.FirstOrDefault(c => c.label == evt.newValue);
                        Undo.RecordObject(asset, "Edit Sidechain Target");
                        send.targetEffectIndex = hit.index;
                        ApplySendValueChange();
                    });
                    row.Add(picker);
                }
            }

            // ── 行 3: Position / Gain ──
            var posField = new EnumField("Position", send.position);
            posField.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(asset, "Edit Send Position");
                send.position = (NeziaSendPosition)evt.newValue;
                ApplySendValueChange();
            });
            row.Add(posField);

            row.Add(MakeBoundFloatSlider("Gain", 0f, 4f, send.gain,
                v => { Undo.RecordObject(asset, "Edit Send Gain"); send.gain = v; ApplySendValueChange(); }));

            return row;
        }

        /// <summary>
        /// バス名 (空文字を含む) のドロップダウンを作成する。空白行は「(未設定)」として
        /// 表示し、選択されると空文字を commit する。
        /// </summary>
        private static DropdownField MakeBusDropdown(List<string> busNames, string current, System.Action<string> commit)
        {
            const string Unset = "(未設定)";
            var labels = new List<string> { Unset };
            labels.AddRange(busNames);
            var initial = string.IsNullOrEmpty(current) || !busNames.Contains(current) ? Unset : current;
            var dd = new DropdownField(labels, initial);
            dd.RegisterValueChangedCallback(evt =>
            {
                commit(evt.newValue == Unset ? string.Empty : evt.newValue);
            });
            return dd;
        }

        private static List<(int index, string label)> BuildCompressorChoices(NeziaMixerAsset asset, string busName)
        {
            var result = new List<(int, string)>();
            if (asset == null || string.IsNullOrEmpty(busName)) return result;
            var bus = asset.Buses.FirstOrDefault(b => b != null && b.name == busName);
            if (bus?.effects == null) return result;
            for (int i = 0; i < bus.effects.Count; i++)
            {
                if (bus.effects[i] is NeziaMixerAsset.Compressor)
                    result.Add((i, $"[{i}] Compressor"));
            }
            return result;
        }

        private void AddSend()
        {
            var asset = Asset;
            if (asset == null) return;
            Undo.RecordObject(asset, "Add Send");
            var sends = GetSendListAccessor();
            sends.Add(new NeziaMixerAsset.SendNode
            {
                source = string.Empty,
                target = NeziaMixerAsset.SendTargetKind.Bus,
                targetBus = string.Empty,
                targetEffectIndex = 0,
                position = NeziaSendPosition.Post,
                gain = 1f,
            });
            ApplySendStructuralChange();
        }

        private void RemoveSend(int index)
        {
            var asset = Asset;
            if (asset == null) return;
            var sends = GetSendListAccessor();
            if (index < 0 || index >= sends.Count) return;
            Undo.RecordObject(asset, "Remove Send");
            sends.RemoveAt(index);
            ApplySendStructuralChange();
        }

        /// <summary>
        /// Send リストの構造 (件数 / 行レイアウト) が変わる編集の仕上げ。
        /// 行を再構築するため、ドラッグ中の Slider 等にはこちらを使わない。
        /// </summary>
        private void ApplySendStructuralChange()
        {
            var asset = Asset;
            if (asset == null) return;
            asset.InvalidateResolvedCache();
            EditorUtility.SetDirty(asset);
            RefreshSendsList();
            UpdateValidationFooter();
        }

        /// <summary>
        /// 値編集 (gain / position / source / target) のみで行レイアウトを保つ仕上げ。
        /// Slider を再生成しないので、ドラッグ操作の連続性が維持される。
        /// </summary>
        private void ApplySendValueChange()
        {
            var asset = Asset;
            if (asset == null) return;
            asset.InvalidateResolvedCache();
            EditorUtility.SetDirty(asset);
            UpdateValidationFooter();
        }

        private List<NeziaMixerAsset.SendNode> GetSendListAccessor() => Asset?.EditableSends;

        // ─── Effects (IP-12 PR-C) ────────────────────────────────

        private void BuildEffectsSection(VisualElement parent)
        {
            // スクロールは Inspector ペイン外側 (BuildInspectorPane) が担うので、
            // ここではセクションを自然高さで積むだけ。
            var section = new VisualElement();
            section.style.marginTop = 12f;
            section.style.flexShrink = 0f;
            section.style.flexDirection = FlexDirection.Column;
            parent.Add(section);

            var header = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    marginBottom = 4f,
                    flexShrink = 0f,
                },
            };
            var title = new Label("Effects");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.flexGrow = 1f;
            header.Add(title);

            var addBtn = new Button(ShowAddEffectMenu) { text = "+ Add" };
            header.Add(addBtn);
            section.Add(header);

            _effectsRoot = new VisualElement();
            _effectsRoot.style.flexShrink = 0f;
            section.Add(_effectsRoot);
        }

        private void ShowAddEffectMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("LowPass"),    false, () => AddEffect(NeziaEffectKind.LowPass));
            menu.AddItem(new GUIContent("HighPass"),   false, () => AddEffect(NeziaEffectKind.HighPass));
            menu.AddItem(new GUIContent("Reverb"),     false, () => AddEffect(NeziaEffectKind.Reverb));
            menu.AddItem(new GUIContent("Compressor"), false, () => AddEffect(NeziaEffectKind.Compressor));
            menu.ShowAsContext();
        }

        private void AddEffect(NeziaEffectKind kind)
        {
            var asset = Asset;
            if (asset == null || _selectedBusIndex < 0 || _selectedBusIndex >= asset.Buses.Count) return;

            Undo.RecordObject(asset, "Add Effect");
            var bus = GetBusListAccessor()[_selectedBusIndex];
            if (bus.effects == null) bus.effects = new List<NeziaMixerAsset.BusEffect>();
            NeziaMixerAsset.BusEffect spec = kind switch
            {
                NeziaEffectKind.LowPass    => new NeziaMixerAsset.LowPass(),
                NeziaEffectKind.HighPass   => new NeziaMixerAsset.HighPass(),
                NeziaEffectKind.Reverb     => new NeziaMixerAsset.Reverb(),
                NeziaEffectKind.Compressor => new NeziaMixerAsset.Compressor(),
                _ => null,
            };
            if (spec == null) return;
            bus.effects.Add(spec);
            ApplyEffectChange();
        }

        private void RemoveEffect(int index)
        {
            var asset = Asset;
            if (asset == null || _selectedBusIndex < 0 || _selectedBusIndex >= asset.Buses.Count) return;
            var bus = GetBusListAccessor()[_selectedBusIndex];
            if (bus.effects == null || index < 0 || index >= bus.effects.Count) return;

            Undo.RecordObject(asset, "Remove Effect");
            bus.effects.RemoveAt(index);
            ApplyEffectChange();
        }

        private void MoveEffect(int from, int to)
        {
            var asset = Asset;
            if (asset == null || _selectedBusIndex < 0 || _selectedBusIndex >= asset.Buses.Count) return;
            var bus = GetBusListAccessor()[_selectedBusIndex];
            if (bus.effects == null) return;
            if (from < 0 || from >= bus.effects.Count) return;
            if (to < 0 || to >= bus.effects.Count) return;
            if (from == to) return;

            Undo.RecordObject(asset, "Reorder Effect");
            var item = bus.effects[from];
            bus.effects.RemoveAt(from);
            bus.effects.Insert(to, item);
            ApplyEffectChange();
        }

        /// <summary>Effect chain 操作後の共通仕上げ。再描画 + dirty マーク + cache 破棄。</summary>
        private void ApplyEffectChange()
        {
            var asset = Asset;
            if (asset == null) return;
            asset.InvalidateResolvedCache();
            EditorUtility.SetDirty(asset);
            RefreshEffectRows();
        }

        /// <summary>選択中バスの effect chain 行を再構築する。</summary>
        private void RefreshEffectRows()
        {
            if (_effectsRoot == null) return;
            _effectsRoot.Clear();

            var asset = Asset;
            if (asset == null || _selectedBusIndex < 0 || _selectedBusIndex >= asset.Buses.Count) return;
            var bus = asset.Buses[_selectedBusIndex];
            var effects = bus?.effects;
            if (effects == null || effects.Count == 0)
            {
                var empty = new Label("No effects. Click + Add to insert one.");
                empty.style.color = new Color(0.6f, 0.6f, 0.6f);
                empty.style.marginTop = 2f;
                _effectsRoot.Add(empty);
                return;
            }

            for (int i = 0; i < effects.Count; i++)
            {
                _effectsRoot.Add(BuildEffectRow(effects[i], i, effects.Count));
            }
        }

        private VisualElement BuildEffectRow(NeziaMixerAsset.BusEffect effect, int index, int total)
        {
            var row = new VisualElement();
            row.style.marginTop = 4f;
            row.style.borderTopWidth = 1f;
            row.style.borderBottomWidth = 1f;
            row.style.borderLeftWidth = 1f;
            row.style.borderRightWidth = 1f;
            row.style.borderTopColor = row.style.borderBottomColor = row.style.borderLeftColor = row.style.borderRightColor = new Color(0f, 0f, 0f, 0.25f);
            row.style.paddingTop = 4f;
            row.style.paddingBottom = 4f;
            row.style.paddingLeft = 6f;
            row.style.paddingRight = 6f;

            // ── ヘッダ: ↑ ↓ | kind 名 + enabled toggle | ×
            var header = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 4f },
            };

            var upBtn = new Button(() => MoveEffect(index, index - 1)) { text = "▲" };
            upBtn.style.width = 24f;
            upBtn.SetEnabled(index > 0);
            header.Add(upBtn);

            var downBtn = new Button(() => MoveEffect(index, index + 1)) { text = "▼" };
            downBtn.style.width = 24f;
            downBtn.SetEnabled(index < total - 1);
            header.Add(downBtn);

            var kindLabel = new Label(effect.Kind.ToString());
            kindLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            kindLabel.style.flexGrow = 1f;
            kindLabel.style.marginLeft = 6f;
            header.Add(kindLabel);

            var enabledToggle = new Toggle { value = effect.enabled, tooltip = "Enabled" };
            enabledToggle.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(Asset, "Toggle Effect Enabled");
                effect.enabled = evt.newValue;
                EditorUtility.SetDirty(Asset);
            });
            header.Add(enabledToggle);

            var removeBtn = new Button(() => RemoveEffect(index)) { text = "×" };
            removeBtn.style.width = 24f;
            removeBtn.style.marginLeft = 4f;
            header.Add(removeBtn);

            row.Add(header);

            // ── Position (Pre / Post) — 全 effect 共通 ──
            var positionField = new EnumField("Position", effect.position);
            positionField.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(Asset, "Change Effect Position");
                effect.position = (NeziaEffectPosition)evt.newValue;
                EditorUtility.SetDirty(Asset);
            });
            row.Add(positionField);

            // ── Per-kind フィールド ──
            switch (effect)
            {
                case NeziaMixerAsset.LowPass lp:    BuildLowPassFields(row, lp); break;
                case NeziaMixerAsset.HighPass hp:   BuildHighPassFields(row, hp); break;
                case NeziaMixerAsset.Reverb rv:     BuildReverbFields(row, rv); break;
                case NeziaMixerAsset.Compressor cp: BuildCompressorFields(row, cp); break;
            }
            return row;
        }

        private void BuildLowPassFields(VisualElement row, NeziaMixerAsset.LowPass spec)
        {
            row.Add(MakeBoundFloatSlider("Cutoff", 20f, 20000f, spec.cutoff,
                v => { Undo.RecordObject(Asset, "Edit LowPass Cutoff"); spec.cutoff = v; EditorUtility.SetDirty(Asset); }));
            row.Add(MakeBoundFloatSlider("Q", 0.1f, 10f, spec.q,
                v => { Undo.RecordObject(Asset, "Edit LowPass Q"); spec.q = v; EditorUtility.SetDirty(Asset); }));
        }

        private void BuildHighPassFields(VisualElement row, NeziaMixerAsset.HighPass spec)
        {
            row.Add(MakeBoundFloatSlider("Cutoff", 20f, 20000f, spec.cutoff,
                v => { Undo.RecordObject(Asset, "Edit HighPass Cutoff"); spec.cutoff = v; EditorUtility.SetDirty(Asset); }));
            row.Add(MakeBoundFloatSlider("Q", 0.1f, 10f, spec.q,
                v => { Undo.RecordObject(Asset, "Edit HighPass Q"); spec.q = v; EditorUtility.SetDirty(Asset); }));
        }

        private void BuildReverbFields(VisualElement row, NeziaMixerAsset.Reverb spec)
        {
            row.Add(MakeBoundFloatSlider("Room Size", 0f, 1f, spec.roomSize,
                v => { Undo.RecordObject(Asset, "Edit Reverb"); spec.roomSize = v; EditorUtility.SetDirty(Asset); }));
            row.Add(MakeBoundFloatSlider("Damping", 0f, 1f, spec.damping,
                v => { Undo.RecordObject(Asset, "Edit Reverb"); spec.damping = v; EditorUtility.SetDirty(Asset); }));
            row.Add(MakeBoundFloatSlider("Wet", 0f, 1f, spec.wet,
                v => { Undo.RecordObject(Asset, "Edit Reverb"); spec.wet = v; EditorUtility.SetDirty(Asset); }));
            row.Add(MakeBoundFloatSlider("Dry", 0f, 1f, spec.dry,
                v => { Undo.RecordObject(Asset, "Edit Reverb"); spec.dry = v; EditorUtility.SetDirty(Asset); }));
            row.Add(MakeBoundFloatSlider("Width", 0f, 1f, spec.width,
                v => { Undo.RecordObject(Asset, "Edit Reverb"); spec.width = v; EditorUtility.SetDirty(Asset); }));
        }

        private void BuildCompressorFields(VisualElement row, NeziaMixerAsset.Compressor spec)
        {
            row.Add(MakeBoundFloat("Threshold dB", spec.thresholdDb,
                v => { Undo.RecordObject(Asset, "Edit Compressor"); spec.thresholdDb = v; EditorUtility.SetDirty(Asset); }));
            row.Add(MakeBoundFloat("Ratio", spec.ratio,
                v => { Undo.RecordObject(Asset, "Edit Compressor"); spec.ratio = v; EditorUtility.SetDirty(Asset); }));
            row.Add(MakeBoundFloat("Attack ms", spec.attackMs,
                v => { Undo.RecordObject(Asset, "Edit Compressor"); spec.attackMs = v; EditorUtility.SetDirty(Asset); }));
            row.Add(MakeBoundFloat("Release ms", spec.releaseMs,
                v => { Undo.RecordObject(Asset, "Edit Compressor"); spec.releaseMs = v; EditorUtility.SetDirty(Asset); }));
            row.Add(MakeBoundFloat("Knee dB", spec.kneeDb,
                v => { Undo.RecordObject(Asset, "Edit Compressor"); spec.kneeDb = v; EditorUtility.SetDirty(Asset); }));
            row.Add(MakeBoundFloat("Makeup dB", spec.makeupDb,
                v => { Undo.RecordObject(Asset, "Edit Compressor"); spec.makeupDb = v; EditorUtility.SetDirty(Asset); }));
        }

        /// <summary>Slider + 数値フィールド合成の編集行。</summary>
        private static VisualElement MakeBoundFloatSlider(string label, float min, float max, float initial, System.Action<float> commit)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            var slider = new Slider(label, min, max) { value = initial };
            slider.style.flexGrow = 1f;
            var num = new FloatField { value = initial, isDelayed = true };
            num.style.width = 60f;
            num.style.marginLeft = 4f;

            slider.RegisterValueChangedCallback(evt =>
            {
                num.SetValueWithoutNotify(evt.newValue);
                commit(evt.newValue);
            });
            num.RegisterValueChangedCallback(evt =>
            {
                var clamped = Mathf.Clamp(evt.newValue, min, max);
                slider.SetValueWithoutNotify(clamped);
                if (!Mathf.Approximately(evt.newValue, clamped))
                    num.SetValueWithoutNotify(clamped);
                commit(clamped);
            });
            row.Add(slider);
            row.Add(num);
            return row;
        }

        /// <summary>範囲制約のない数値編集行（Compressor 用）。</summary>
        private static VisualElement MakeBoundFloat(string label, float initial, System.Action<float> commit)
        {
            var f = new FloatField(label) { value = initial, isDelayed = true };
            f.RegisterValueChangedCallback(evt => commit(evt.newValue));
            return f;
        }

        private void BuildValidationFooter(VisualElement root)
        {
            _validationFooter = new VisualElement();
            _validationFooter.style.flexShrink = 0f;
            _validationFooter.style.paddingLeft = 8f;
            _validationFooter.style.paddingRight = 8f;
            _validationFooter.style.paddingTop = 4f;
            _validationFooter.style.paddingBottom = 4f;
            _validationFooter.style.borderTopWidth = 1f;
            _validationFooter.style.borderTopColor = new Color(0f, 0f, 0f, 0.3f);
            _validationFooter.style.backgroundColor = new Color(0.7f, 0.5f, 0.1f, 0.18f);
            root.Add(_validationFooter);
        }

        // ─── ツリー構築 ──────────────────────────────────────────

        private void RefreshTree()
        {
            if (_treeView == null) return;
            var asset = Asset;
            if (asset == null)
            {
                _treeView.SetRootItems(new List<TreeViewItemData<BusEntry>>());
                _treeView.Rebuild();
                return;
            }

            var rootItems = BuildTreeItems(asset);
            _treeView.SetRootItems(rootItems);
            _treeView.Rebuild();
            _treeView.ExpandAll();

            if (_selectedBusIndex >= 0 && _selectedBusIndex < asset.Buses.Count)
                _treeView.SetSelectionById(IdForBus(_selectedBusIndex));
            else
                _treeView.SetSelectionById(MasterItemId);
        }

        /// <summary>
        /// flat な <see cref="NeziaMixerAsset.Buses"/> から TreeView 用の単一 root ツリーを組む。
        /// Master 仮想ルートを root に置き、<c>parent</c> が空 / 未知 / 循環で行き先が無い
        /// バスはすべて Master の直接の子として表示する。
        /// </summary>
        private static List<TreeViewItemData<BusEntry>> BuildTreeItems(NeziaMixerAsset asset)
        {
            var buses = asset.Buses;
            var entryByIndex = new Dictionary<int, BusEntry>(buses.Count);
            var indexByName = new Dictionary<string, int>(buses.Count);

            for (int i = 0; i < buses.Count; i++)
            {
                var node = buses[i];
                if (node == null) continue;
                if (!string.IsNullOrEmpty(node.name) && !indexByName.ContainsKey(node.name))
                    indexByName[node.name] = i;
                entryByIndex[i] = new BusEntry { Index = i, Name = node.name, Gain = node.gain, Muted = node.muted };
            }

            var rootSafe = new HashSet<int>();
            foreach (var i in entryByIndex.Keys)
            {
                if (DescendsToRoot(i, buses, indexByName))
                    rootSafe.Add(i);
            }

            var childrenByParentId = new Dictionary<int, List<int>>();
            foreach (var i in entryByIndex.Keys)
            {
                var node = buses[i];
                int parentId = MasterItemId;
                if (rootSafe.Contains(i) && !string.IsNullOrEmpty(node.parent)
                    && indexByName.TryGetValue(node.parent, out var pIdx))
                    parentId = IdForBus(pIdx);
                if (!childrenByParentId.TryGetValue(parentId, out var list))
                {
                    list = new List<int>();
                    childrenByParentId[parentId] = list;
                }
                list.Add(i);
            }

            TreeViewItemData<BusEntry> BuildBus(int busIndex)
            {
                var entry = entryByIndex[busIndex];
                List<TreeViewItemData<BusEntry>> children = null;
                if (childrenByParentId.TryGetValue(IdForBus(busIndex), out var childIds))
                {
                    children = new List<TreeViewItemData<BusEntry>>(childIds.Count);
                    foreach (var c in childIds) children.Add(BuildBus(c));
                }
                return new TreeViewItemData<BusEntry>(IdForBus(busIndex), entry, children);
            }

            var masterChildren = new List<TreeViewItemData<BusEntry>>();
            if (childrenByParentId.TryGetValue(MasterItemId, out var rootBusIds))
                foreach (var busIndex in rootBusIds) masterChildren.Add(BuildBus(busIndex));

            var masterEntry = new BusEntry { Index = -1, Name = "Master", Gain = 1f, Muted = false };
            return new List<TreeViewItemData<BusEntry>>
            {
                new TreeViewItemData<BusEntry>(MasterItemId, masterEntry, masterChildren),
            };
        }

        private static bool DescendsToRoot(int i, IReadOnlyList<NeziaMixerAsset.BusNode> buses,
            Dictionary<string, int> indexByName)
        {
            var visited = new HashSet<int>();
            var cur = i;
            while (true)
            {
                if (!visited.Add(cur)) return false;
                var pname = buses[cur]?.parent;
                if (string.IsNullOrEmpty(pname)) return true;
                if (!indexByName.TryGetValue(pname, out var next)) return true;
                cur = next;
            }
        }

        // ─── 行 UI ───────────────────────────────────────────────

        private VisualElement MakeBusRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.flexGrow = 1f;

            var nameLabel = new Label { name = "name" };
            nameLabel.style.flexGrow = 1f;
            nameLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            row.Add(nameLabel);

            var detailLabel = new Label { name = "detail" };
            detailLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
            detailLabel.style.marginRight = 4f;
            detailLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            row.Add(detailLabel);

            return row;
        }

        private void BindBusRow(VisualElement element, int index)
        {
            var entry = _treeView.GetItemDataForIndex<BusEntry>(index);
            var nameLabel = element.Q<Label>("name");
            var detailLabel = element.Q<Label>("detail");

            var isMaster = entry.Index < 0;
            var hasName = !string.IsNullOrEmpty(entry.Name);
            nameLabel.text = isMaster ? entry.Name : (hasName ? entry.Name : "(unnamed)");
            nameLabel.style.unityFontStyleAndWeight = isMaster ? FontStyle.Bold : FontStyle.Normal;
            nameLabel.style.color = (!isMaster && !hasName)
                ? new Color(0.95f, 0.5f, 0.2f)
                : StyleKeyword.Null;
            detailLabel.text = isMaster
                ? string.Empty
                : $"gain {entry.Gain:0.##}{(entry.Muted ? " · muted" : "")}";
        }

        // ─── 選択 / 右ペイン更新 ─────────────────────────────────

        private void OnTreeSelectionChanged()
        {
            var selectedIds = _treeView.selectedIds.ToList();
            if (selectedIds.Count == 0)
            {
                _selectedBusIndex = -1;
            }
            else
            {
                var id = selectedIds[0];
                _selectedBusIndex = id == MasterItemId ? -1 : BusIndexFromId(id);
            }
            UpdateRightPane();
        }

        private void UpdateRightPane()
        {
            if (_inspectorRoot == null) return;
            var asset = Asset;
            var hasSelection = asset != null
                && _selectedBusIndex >= 0
                && _selectedBusIndex < asset.Buses.Count;

            _inspectorRoot.style.display = hasSelection ? DisplayStyle.Flex : DisplayStyle.None;
            _inspectorPlaceholder.style.display = hasSelection ? DisplayStyle.None : DisplayStyle.Flex;
            _deleteBusButton?.SetEnabled(hasSelection);

            if (!hasSelection) return;

            var node = asset.Buses[_selectedBusIndex];
            _suspendBindCallbacks = true;
            try
            {
                _nameField.SetValueWithoutNotify(node.name ?? string.Empty);
                _gainSlider.SetValueWithoutNotify(node.gain);
                _gainNumeric.SetValueWithoutNotify(node.gain);
                _mutedToggle.SetValueWithoutNotify(node.muted);
            }
            finally
            {
                _suspendBindCallbacks = false;
            }

            // 見出しに選択中バス名を反映する（空名 bus は (unnamed) と表示）。
            if (_inspectorTitle != null)
            {
                _inspectorTitle.text = string.IsNullOrEmpty(node.name) ? "(unnamed)" : node.name;
            }

            // Effect chain は per-effect の値が異なるので、毎回完全再構築する。
            RefreshEffectRows();
        }

        // ─── 編集操作 ────────────────────────────────────────────

        private void AddBus()
        {
            var asset = Asset;
            if (asset == null) return;

            string parentName = string.Empty;
            if (_selectedBusIndex >= 0 && _selectedBusIndex < asset.Buses.Count)
                parentName = asset.Buses[_selectedBusIndex].parent ?? string.Empty;

            var existingNames = new HashSet<string>(asset.Buses.Select(b => b?.name ?? ""));
            var newName = "New Bus";
            int suffix = 1;
            while (existingNames.Contains(newName)) newName = $"New Bus ({suffix++})";

            Undo.RecordObject(asset, "Add Bus");
            var listAccessor = GetBusListAccessor();
            listAccessor.Add(new NeziaMixerAsset.BusNode
            {
                name = newName,
                parent = parentName,
                gain = 1f,
                muted = false,
            });
            ApplyAndRefresh(asset.Buses.Count - 1);
        }

        private void DeleteSelectedBus()
        {
            var asset = Asset;
            if (asset == null) return;
            if (_selectedBusIndex < 0 || _selectedBusIndex >= asset.Buses.Count) return;

            var listAccessor = GetBusListAccessor();
            var deletedName = listAccessor[_selectedBusIndex]?.name;
            var inheritedParent = listAccessor[_selectedBusIndex]?.parent ?? string.Empty;

            Undo.RecordObject(asset, "Delete Bus");

            if (!string.IsNullOrEmpty(deletedName))
            {
                for (int i = 0; i < listAccessor.Count; i++)
                {
                    if (i == _selectedBusIndex) continue;
                    if (listAccessor[i] != null && listAccessor[i].parent == deletedName)
                        listAccessor[i].parent = inheritedParent;
                }
            }

            listAccessor.RemoveAt(_selectedBusIndex);
            _selectedBusIndex = -1;
            ApplyAndRefresh(_selectedBusIndex);
        }

        private void CommitNameChange(string newName)
        {
            var asset = Asset;
            if (asset == null || _selectedBusIndex < 0 || _selectedBusIndex >= asset.Buses.Count) return;
            var listAccessor = GetBusListAccessor();
            var oldName = listAccessor[_selectedBusIndex]?.name;
            if (oldName == newName) return;

            // 空文字への rename は拒否し、UI を元の名前に戻す。
            if (string.IsNullOrEmpty(newName))
            {
                _suspendBindCallbacks = true;
                try { _nameField.SetValueWithoutNotify(oldName ?? string.Empty); }
                finally { _suspendBindCallbacks = false; }
                return;
            }

            Undo.RecordObject(asset, "Rename Bus");
            listAccessor[_selectedBusIndex].name = newName;

            if (!string.IsNullOrEmpty(oldName))
            {
                for (int i = 0; i < listAccessor.Count; i++)
                {
                    if (i == _selectedBusIndex) continue;
                    if (listAccessor[i] != null && listAccessor[i].parent == oldName)
                        listAccessor[i].parent = newName;
                }
            }

            ApplyAndRefresh(_selectedBusIndex);
        }

        private void CommitGainChange(float newGain)
        {
            var asset = Asset;
            if (asset == null || _selectedBusIndex < 0 || _selectedBusIndex >= asset.Buses.Count) return;
            var listAccessor = GetBusListAccessor();
            if (Mathf.Approximately(listAccessor[_selectedBusIndex].gain, newGain)) return;

            Undo.RecordObject(asset, "Change Bus Gain");
            listAccessor[_selectedBusIndex].gain = newGain;
            ApplyAndRefresh(_selectedBusIndex);
        }

        private void CommitMutedChange(bool newMuted)
        {
            var asset = Asset;
            if (asset == null || _selectedBusIndex < 0 || _selectedBusIndex >= asset.Buses.Count) return;
            var listAccessor = GetBusListAccessor();
            if (listAccessor[_selectedBusIndex].muted == newMuted) return;

            Undo.RecordObject(asset, "Toggle Bus Muted");
            listAccessor[_selectedBusIndex].muted = newMuted;
            ApplyAndRefresh(_selectedBusIndex);
        }

        // ─── Drag & Drop（親変更）────────────────────────────────

        private void ConfigureTreeDragAndDrop(TreeView treeView)
        {
            treeView.canStartDrag += args => args.selectedIds.All(id => id != MasterItemId);

            treeView.setupDragAndDrop += args =>
            {
                var ids = treeView.selectedIds.ToList();
                return new StartDragArgs(ids.Count == 1 ? "Bus" : "Buses", DragVisualMode.Move);
            };

            treeView.dragAndDropUpdate += args =>
            {
                var draggedIds = treeView.selectedIds.ToList();
                return IsValidDropTarget(draggedIds, args.parentId)
                    ? DragVisualMode.Move
                    : DragVisualMode.Rejected;
            };

            treeView.handleDrop += args =>
            {
                var asset = Asset;
                if (asset == null) return DragVisualMode.Rejected;

                var draggedIds = treeView.selectedIds.ToList();
                if (!IsValidDropTarget(draggedIds, args.parentId)) return DragVisualMode.Rejected;

                Undo.RecordObject(asset, "Reparent Bus");
                var listAccessor = GetBusListAccessor();
                string newParentName;
                if (args.parentId == MasterItemId)
                {
                    newParentName = string.Empty;
                }
                else
                {
                    var pIdx = BusIndexFromId(args.parentId);
                    newParentName = (pIdx >= 0 && pIdx < listAccessor.Count)
                        ? listAccessor[pIdx]?.name ?? string.Empty
                        : string.Empty;
                }

                foreach (var draggedId in draggedIds)
                {
                    var idx = BusIndexFromId(draggedId);
                    if (idx < 0 || idx >= listAccessor.Count) continue;
                    listAccessor[idx].parent = newParentName;
                }

                ApplyAndRefresh(_selectedBusIndex);
                return DragVisualMode.Move;
            };
        }

        private bool IsValidDropTarget(IReadOnlyList<int> draggedIds, int parentId)
        {
            var asset = Asset;
            if (draggedIds == null || draggedIds.Count == 0) return false;
            if (asset == null) return false;

            if (parentId == MasterItemId) return true;
            var parentIdx = BusIndexFromId(parentId);
            if (parentIdx < 0 || parentIdx >= asset.Buses.Count) return false;

            foreach (var draggedId in draggedIds)
            {
                if (draggedId == MasterItemId) return false;
                var draggedIdx = BusIndexFromId(draggedId);
                if (draggedIdx == parentIdx) return false;
                if (IsDescendant(parentIdx, draggedIdx)) return false;
            }
            return true;
        }

        private bool IsDescendant(int candidate, int ancestor)
        {
            var asset = Asset;
            if (asset == null) return false;
            var buses = asset.Buses;
            var indexByName = new Dictionary<string, int>(buses.Count);
            for (int i = 0; i < buses.Count; i++)
            {
                if (!string.IsNullOrEmpty(buses[i]?.name) && !indexByName.ContainsKey(buses[i].name))
                    indexByName[buses[i].name] = i;
            }
            int cur = candidate;
            var visited = new HashSet<int>();
            while (cur >= 0 && cur != ancestor)
            {
                if (!visited.Add(cur)) return false;
                var pname = buses[cur]?.parent;
                if (string.IsNullOrEmpty(pname)) return false;
                if (!indexByName.TryGetValue(pname, out cur)) return false;
            }
            return cur == ancestor;
        }

        // ─── 仕上げ（保存・再描画・Validation） ──────────────────

        private void ApplyAndRefresh(int newSelectedIndex)
        {
            var asset = Asset;
            if (asset == null) return;
            asset.InvalidateResolvedCache();
            EditorUtility.SetDirty(asset);
            _selectedBusIndex = newSelectedIndex;
            RefreshTree();
            UpdateRightPane();
            UpdateValidationFooter();
        }

        private void UpdateValidationFooter()
        {
            if (_validationFooter == null) return;
            _validationFooter.Clear();

            var asset = Asset;
            if (asset == null)
            {
                _validationFooter.style.display = DisplayStyle.None;
                return;
            }

            var errors = asset.Validate();
            if (errors == null || errors.Count == 0)
            {
                _validationFooter.style.display = DisplayStyle.None;
                return;
            }

            _validationFooter.style.display = DisplayStyle.Flex;
            foreach (var err in errors)
            {
                var label = new Label("⚠ " + err);
                label.style.color = new Color(0.95f, 0.7f, 0.2f);
                label.style.whiteSpace = WhiteSpace.Normal;
                _validationFooter.Add(label);
            }
        }

        private List<NeziaMixerAsset.BusNode> GetBusListAccessor() => Asset?.EditableBuses;

        // ─── DTO ─────────────────────────────────────────────────

        private struct BusEntry
        {
            public int Index;
            public string Name;
            public float Gain;
            public bool Muted;
        }
    }
}
