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
    /// Inspector として提供する。すべて UI Toolkit (`TreeView` / `TwoPaneSplitView` /
    /// `Slider` / `Toggle` 等) で実装。Project ビューで <c>NeziaMixerAsset</c> を選択すると
    /// 通常の Inspector パネル内にこの編集 UI が出る。<c>Project Settings &gt; Nezia</c> の
    /// inline Inspector / 2 つ目の Inspector を立てた工程内編集 / lock 機能 etc. すべて
    /// 標準 Unity の作法に乗る。
    /// </para>
    ///
    /// <para>
    /// <b>PR-B 範囲</b>: バス追加・削除・リネーム・属性編集 (gain / muted)・親変更
    /// (drag &amp; drop)・<see cref="Undo"/> 対応・<see cref="NeziaMixerAsset.Validate"/>
    /// 結果のフッタ表示。Effect chain は IP-12 PR-C、Send / sidechain は PR-D で
    /// それぞれ専用ペイン / タブとして拡張する。
    /// </para>
    /// </summary>
    [CustomEditor(typeof(NeziaMixerAsset))]
    public sealed class NeziaMixerInspector : UnityEditor.Editor
    {
        // ─── 状態 ────────────────────────────────────────────────

        [SerializeField] private int _selectedBusIndex = -1;

        // ── UI 参照 ──
        private TreeView _treeView;
        private VisualElement _inspectorRoot;
        private Label _inspectorPlaceholder;
        private TextField _nameField;
        private Slider _gainSlider;
        private FloatField _gainNumeric;
        private Toggle _mutedToggle;
        private Button _deleteBusButton;
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
            UpdateValidationFooter();
        }

        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();
            root.style.flexDirection = FlexDirection.Column;
            // Inspector pane は高さ可変なので minHeight を入れて TwoPaneSplitView が潰れないようにする。
            root.style.minHeight = 360f;

            BuildToolbar(root);
            BuildBody(root);
            BuildValidationFooter(root);

            // BuildBody で _treeView 等は構築済み。即時で初期描画する（delayCall は
            // Inspector のライフサイクルと相性が悪く、再構築のたびに古い callback が
            // 残ってバグの温床になる）。
            RefreshTree();
            UpdateRightPane();
            UpdateValidationFooter();

            return root;
        }

        // ─── UI 構築 ─────────────────────────────────────────────

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

        private void BuildBody(VisualElement root)
        {
            // 縦割り (上 = TreeView / 下 = Inspector) — Unity Inspector は横幅が狭いので、
            // 横割りだと右ペインのフィールドが切れて見えない。上下に積む方が読み易い。
            var split = new TwoPaneSplitView(0, 220f, TwoPaneSplitViewOrientation.Vertical);
            split.style.flexGrow = 1f;
            split.style.minHeight = 360f;
            root.Add(split);

            // ── 左 (TreeView を VisualElement でラップして split に渡す)──
            //
            // TwoPaneSplitView は子要素を絶対位置レイアウトするため、TreeView を直接
            // 子にすると TreeView が flex 計算されず描画されない。VisualElement で
            // 包むことで、ラッパーがペイン高さに合わせて広がり、その内側で TreeView が
            // 正しく flexGrow できる。
            var leftPane = new VisualElement();
            leftPane.style.flexGrow = 1f;
            split.Add(leftPane);

            _treeView = new TreeView
            {
                fixedItemHeight = 22f,
                selectionType = SelectionType.Single,
                makeItem = MakeBusRow,
                bindItem = BindBusRow,
            };
            _treeView.style.flexGrow = 1f;
            _treeView.selectionChanged += _ => OnTreeSelectionChanged();
            ConfigureTreeDragAndDrop(_treeView);
            leftPane.Add(_treeView);

            // ── 右 (Inspector) ──
            var rightPane = new VisualElement();
            rightPane.style.paddingTop = 6f;
            rightPane.style.paddingLeft = 10f;
            rightPane.style.paddingRight = 10f;
            rightPane.style.paddingBottom = 6f;
            split.Add(rightPane);

            _inspectorPlaceholder = new Label("バスを選択するとプロパティを編集できます。");
            _inspectorPlaceholder.style.color = new Color(0.6f, 0.6f, 0.6f);
            _inspectorPlaceholder.style.whiteSpace = WhiteSpace.Normal;
            rightPane.Add(_inspectorPlaceholder);

            _inspectorRoot = new VisualElement();
            rightPane.Add(_inspectorRoot);

            var inspectorTitle = new Label("Selected Bus");
            inspectorTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            inspectorTitle.style.marginBottom = 6f;
            _inspectorRoot.Add(inspectorTitle);

            _nameField = new TextField("Name") { isDelayed = true };
            _nameField.RegisterValueChangedCallback(evt =>
            {
                if (_suspendBindCallbacks) return;
                CommitNameChange(evt.newValue);
            });
            _inspectorRoot.Add(_nameField);

            var gainRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
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
            _mutedToggle.RegisterValueChangedCallback(evt =>
            {
                if (_suspendBindCallbacks) return;
                CommitMutedChange(evt.newValue);
            });
            _inspectorRoot.Add(_mutedToggle);
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
