using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nezia.Unity.Editor.Mixer
{
    /// <summary>
    /// <see cref="NeziaMixerAsset"/> 専用の編集ウィンドウ（IP-12 PR-A）。
    ///
    /// <para>
    /// Wwise / FMOD / Unity Audio Mixer と同じ「Hierarchy ペイン + プロパティパネル
    /// + Send タブ」UX を採る。GTK ベースのノードグラフ案は撤回し、UI Toolkit の
    /// <see cref="TreeView"/> + <see cref="ReorderableList"/> で組む方針に変更
    /// （詳細: <c>docs~/roadmap/integration-experience.md</c> の IP-12）。
    /// </para>
    ///
    /// <para>
    /// PR-A では <b>表示のみ</b>: アセット選択フィールド + バスツリーの読み取り表示。
    /// 編集（追加・削除・属性編集・親変更）は PR-B、Effect chain は PR-C、
    /// Send タブは PR-D で順次拡張する。
    /// </para>
    /// </summary>
    public sealed class NeziaMixerWindow : EditorWindow
    {
        // ─── 起動導線 ────────────────────────────────────────────

        [MenuItem("Tools/Nezia/Mixer Editor")]
        public static NeziaMixerWindow Open()
        {
            var window = GetWindow<NeziaMixerWindow>();
            window.titleContent = new GUIContent("Nezia Mixer");
            window.minSize = new Vector2(420f, 280f);
            return window;
        }

        /// <summary>指定アセットを target にしてウィンドウを開く。Inspector や OnOpenAsset から利用。</summary>
        public static NeziaMixerWindow Open(NeziaMixerAsset asset)
        {
            var window = Open();
            if (asset != null) window.SetAsset(asset);
            return window;
        }

        // ─── 状態 ────────────────────────────────────────────────

        [SerializeField] private NeziaMixerAsset _asset;

        private ObjectField _assetField;
        private TreeView _treeView;
        private Label _emptyLabel;

        // 「Master 仮想ルート」を含めた木の表現。BusNode を直接ツリー化すると
        // master 直下の bus が複数あるとき root が複数になり TreeView 的に扱いにくいので、
        // 仮想 master を 1 つ root に置く形にする。
        private const int MasterItemId = -1;

        // ─── lifecycle ───────────────────────────────────────────

        private void OnEnable()
        {
            // serialized field にロード済みのアセットがあればそれを採用、無ければ Project Default。
            if (_asset == null) _asset = NeziaSettings.Instance?.DefaultMixer;
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.flexDirection = FlexDirection.Column;

            // ── Toolbar ──────────────────────────────────────────
            var toolbar = new Toolbar();
            toolbar.style.flexShrink = 0f;

            _assetField = new ObjectField("Mixer Asset")
            {
                objectType = typeof(NeziaMixerAsset),
                allowSceneObjects = false,
                value = _asset,
            };
            _assetField.style.flexGrow = 1f;
            _assetField.RegisterValueChangedCallback(evt => SetAsset(evt.newValue as NeziaMixerAsset));
            toolbar.Add(_assetField);

            root.Add(toolbar);

            // ── 本体 (バスツリー)──────────────────────────────────
            _emptyLabel = new Label("Mixer Asset を選択してください。Project Default を作成済みの場合は\n" +
                                   "Project Settings > Nezia から自動的にロードされます。");
            _emptyLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _emptyLabel.style.flexGrow = 1f;
            _emptyLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
            _emptyLabel.style.whiteSpace = WhiteSpace.Normal;
            root.Add(_emptyLabel);

            _treeView = new TreeView
            {
                fixedItemHeight = 22f,
                selectionType = SelectionType.Single,
                makeItem = MakeBusRow,
                bindItem = BindBusRow,
            };
            _treeView.style.flexGrow = 1f;
            root.Add(_treeView);

            RefreshTree();
        }

        // ─── public API ──────────────────────────────────────────

        /// <summary>編集対象アセットを差し替える。</summary>
        public void SetAsset(NeziaMixerAsset asset)
        {
            _asset = asset;
            if (_assetField != null) _assetField.SetValueWithoutNotify(_asset);
            RefreshTree();
        }

        // ─── ツリー構築 ──────────────────────────────────────────

        private void RefreshTree()
        {
            if (_treeView == null || _emptyLabel == null) return;

            if (_asset == null)
            {
                _treeView.style.display = DisplayStyle.None;
                _emptyLabel.style.display = DisplayStyle.Flex;
                _treeView.SetRootItems(new List<TreeViewItemData<BusEntry>>());
                _treeView.Rebuild();
                return;
            }

            _emptyLabel.style.display = DisplayStyle.None;
            _treeView.style.display = DisplayStyle.Flex;

            var rootItems = BuildTreeItems(_asset);
            _treeView.SetRootItems(rootItems);
            _treeView.Rebuild();
            _treeView.ExpandAll();
        }

        /// <summary>
        /// flat な <see cref="NeziaMixerAsset.Buses"/> から TreeView 用階層を組む。
        /// 仮想 Master ノード (id = <see cref="MasterItemId"/>) を root に置き、
        /// <c>parent</c> が空 / 未知のバスはその直下に並べる。
        /// </summary>
        private static List<TreeViewItemData<BusEntry>> BuildTreeItems(NeziaMixerAsset asset)
        {
            var buses = asset.Buses;
            // index → children id list。Master は MasterItemId をキーとする。
            var childrenByParentId = new Dictionary<int, List<int>>();
            // BusNode index → entry
            var entryByIndex = new Dictionary<int, BusEntry>(buses.Count);
            // バス名 → index ルックアップ。重複名は最初の 1 つを採用（Validate 側で警告）
            var indexByName = new Dictionary<string, int>(buses.Count);

            for (int i = 0; i < buses.Count; i++)
            {
                var node = buses[i];
                if (node == null || string.IsNullOrEmpty(node.name)) continue;
                if (indexByName.ContainsKey(node.name)) continue;
                indexByName[node.name] = i;
                entryByIndex[i] = new BusEntry { Index = i, Name = node.name, Gain = node.gain, Muted = node.muted };
            }

            foreach (var (i, _) in entryByIndex)
            {
                var node = buses[i];
                int parentId = MasterItemId;
                if (!string.IsNullOrEmpty(node.parent) && indexByName.TryGetValue(node.parent, out var pIdx))
                    parentId = pIdx;
                if (!childrenByParentId.TryGetValue(parentId, out var list))
                {
                    list = new List<int>();
                    childrenByParentId[parentId] = list;
                }
                list.Add(i);
            }

            // 再帰で TreeViewItemData を組み立てる。
            TreeViewItemData<BusEntry> Build(int id)
            {
                var entry = id == MasterItemId ? BusEntry.Master() : entryByIndex[id];
                List<TreeViewItemData<BusEntry>> children = null;
                if (childrenByParentId.TryGetValue(id, out var childIds))
                {
                    children = new List<TreeViewItemData<BusEntry>>(childIds.Count);
                    foreach (var c in childIds) children.Add(Build(c));
                }
                return new TreeViewItemData<BusEntry>(id, entry, children);
            }

            return new List<TreeViewItemData<BusEntry>> { Build(MasterItemId) };
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

            if (entry.Index == MasterItemId)
            {
                nameLabel.text = "Master";
                nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                detailLabel.text = string.Empty;
            }
            else
            {
                nameLabel.text = entry.Name;
                nameLabel.style.unityFontStyleAndWeight = FontStyle.Normal;
                var muted = entry.Muted ? " · muted" : string.Empty;
                detailLabel.text = $"gain {entry.Gain:0.##}{muted}";
            }
        }

        // ─── DTO ─────────────────────────────────────────────────

        private struct BusEntry
        {
            public int Index;
            public string Name;
            public float Gain;
            public bool Muted;

            public static BusEntry Master() => new BusEntry { Index = MasterItemId, Name = "Master" };
        }
    }
}
