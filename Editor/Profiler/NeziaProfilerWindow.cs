using System.Collections.Generic;
using Nezia.Unity;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nezia.Unity.Editor.Profiler
{
    /// <summary>
    /// IP-10: 実行中エンジンの可視化ウィンドウ (M3 デバッグビジュアライザ基盤)。
    ///
    /// <para>
    /// Play Mode 中の in-process エンジンを <see cref="NeziaProfiler"/> (FFI) で
    /// 約 10Hz ポーリングし、マスター dB メーター / Mixer Snapshot 進行 /
    /// バス一覧 (リアルタイム gain) / アクティブソース一覧を表示する。
    /// ウィンドウを開いている間だけプロファイラ publish を有効化し、閉じると
    /// 無効化する (OFF 時のサウンドスレッド追加コストは atomic load 1 回のみ)。
    /// </para>
    ///
    /// <para>
    /// 編集時 (daemon 試聴中) の可視化は将来の共有メモリ side-channel と合わせて
    /// 後続フェーズで扱う (daemon CONCEPT.md「将来拡張」)。
    /// </para>
    /// </summary>
    public sealed class NeziaProfilerWindow : EditorWindow
    {
        private const string MenuRoot = "Tools/Nezia/";

        private const string UxmlPath =
            "Packages/jp.nezia.unity/Editor/Profiler/NeziaProfilerWindow.uxml";
        private const string UssPath =
            "Packages/jp.nezia.unity/Editor/Profiler/NeziaProfilerWindow.uss";

        /// <summary>ポーリング間隔 (ms)。約 10Hz。</summary>
        private const long PollIntervalMs = 100;

        /// <summary>メーターの表示レンジ下限 (dB)。これ以下は -∞ 扱い。</summary>
        private const float MeterFloorDb = -60f;

        /// <summary>読み取りバッファ上限。</summary>
        private const int MaxBuses = 64;
        private const int MaxSourcesShown = 512;

        private VisualElement _placeholder;
        private VisualElement _content;
        private VisualElement _meterL;
        private VisualElement _meterR;
        private Label _meterLDb;
        private Label _meterRDb;
        private VisualElement _snapshotRow;
        private ProgressBar _snapshotProgress;
        private Label _busesHeader;
        private VisualElement _busRows;
        private Label _sourcesHeader;
        private MultiColumnListView _sourcesList;

        private readonly NeziaProfiler.BusInfo[] _buses = new NeziaProfiler.BusInfo[MaxBuses];
        private readonly NeziaProfiler.SourceInfo[] _sources =
            new NeziaProfiler.SourceInfo[MaxSourcesShown];
        private int _busCount;
        private int _sourceCount;
        private readonly List<int> _sourceIndices = new();

        /// <summary>バス EntityId → 論理名 (NeziaSettings の DefaultMixer から解決)。</summary>
        private readonly Dictionary<(uint, uint), string> _busNames = new();

        /// <summary>
        /// バッファプールスロット index → クリップ名。ロード済み
        /// <see cref="NeziaAudioClip"/> の BufferId から逆引きする。未知の index に
        /// 遭遇したときだけ再スキャンする (PlayOneShot 等の後発ロードを拾うため)。
        /// </summary>
        private readonly Dictionary<uint, string> _bufferNames = new();
        /// <summary>このポーリング周期で既に全 Clip スキャンを実行したか (多重スキャン防止)。</summary>
        private bool _bufferScannedThisPoll;

        /// <summary>バス行の UI キャッシュ (毎ポーリングで作り直さない)。</summary>
        private readonly List<BusRow> _busRowCache = new();

        /// <summary>
        /// プロファイラ publish を有効化したエンジン世代 (<see cref="NeziaEngine.Generation"/>)。
        /// 自前の bool フラグだと Enter Play Mode Options (Domain Reload なし) で静的が
        /// セッションをまたいで残り、新エンジンに enable を呼び損ねる (「2 回目以降
        /// 表示されない」)。エンジン世代が変わったら必ず再 enable する。
        /// </summary>
        private int _enabledGeneration = -1;

        private sealed class BusRow
        {
            public VisualElement Root;
            public Label Name;
            public VisualElement GainFill;
            public Label GainValue;
            public Label Muted;
        }

        [MenuItem(MenuRoot + "Profiler")]
        public static void Open()
        {
            var window = GetWindow<NeziaProfilerWindow>();
            window.titleContent = new GUIContent("Nezia Profiler");
            window.minSize = new Vector2(420, 300);
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;

            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (uxml == null)
            {
                root.Add(new HelpBox($"UXML が見つかりません: {UxmlPath}", HelpBoxMessageType.Error));
                return;
            }
            uxml.CloneTree(root);
            var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (uss != null)
            {
                root.styleSheets.Add(uss);
            }

            _placeholder = root.Q<VisualElement>("placeholder");
            _content = root.Q<VisualElement>("content");
            _meterL = root.Q<VisualElement>("meter-l");
            _meterR = root.Q<VisualElement>("meter-r");
            _meterLDb = root.Q<Label>("meter-l-db");
            _meterRDb = root.Q<Label>("meter-r-db");
            _snapshotRow = root.Q<VisualElement>("snapshot-row");
            _snapshotProgress = root.Q<ProgressBar>("snapshot-progress");
            _busesHeader = root.Q<Label>("buses-header");
            _busRows = root.Q<VisualElement>("bus-rows");
            _sourcesHeader = root.Q<Label>("sources-header");
            _sourcesList = root.Q<MultiColumnListView>("sources-list");

            ConfigureSourceColumns();
            _sourcesList.selectionChanged += _ => PingSelectedSource();
            _sourcesList.itemsChosen += _ => PingSelectedSource();

            root.schedule.Execute(Poll).Every(PollIntervalMs);
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            DisableProfiling();
        }

        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode)
            {
                // エンジンは play 終了で破棄されるため、世代を無効化する。
                _enabledGeneration = -1;
                _busNames.Clear();
                _bufferNames.Clear();
            }
        }

        private void DisableProfiling()
        {
            if (_enabledGeneration >= 0 && NeziaEngine.IsInitialized)
            {
                try
                {
                    NeziaProfiler.Enabled = false;
                }
                catch
                {
                    // engine 終了レースは無視 (可視化の後始末に失敗しても実害なし)。
                }
            }
            _enabledGeneration = -1;
        }

        // ─── ポーリング ───────────────────────────────────────────

        private void Poll()
        {
            var alive = EditorApplication.isPlaying && NeziaEngine.IsInitialized;
            SetVisible(_placeholder, !alive);
            SetVisible(_content, alive);
            if (!alive)
            {
                _enabledGeneration = -1;
                return;
            }

            // 新しいエンジン世代 (= 新しい Play セッション or 再初期化) では必ず
            // FFI の enable を呼び直す。cached static ではなくエンジン実体に紐付ける。
            var generation = NeziaEngine.Generation;
            if (_enabledGeneration != generation)
            {
                NeziaProfiler.Enabled = true;
                _enabledGeneration = generation;
                _busNames.Clear();
                _bufferNames.Clear();
                RebuildBusNameMap();
            }

            NeziaProfiler.Update();
            UpdateMeter();
            UpdateSnapshotProgress();
            UpdateBuses();
            UpdateSources();
        }

        private void UpdateMeter()
        {
            NeziaProfiler.GetMasterPeak(out var l, out var r);
            ApplyMeter(_meterL, _meterLDb, l);
            ApplyMeter(_meterR, _meterRDb, r);
        }

        private static void ApplyMeter(VisualElement fill, Label dbLabel, float peak)
        {
            var db = peak > 0f ? 20f * Mathf.Log10(peak) : float.NegativeInfinity;
            var norm = float.IsNegativeInfinity(db)
                ? 0f
                : Mathf.Clamp01((db - MeterFloorDb) / -MeterFloorDb);
            fill.style.width = Length.Percent(norm * 100f);
            fill.EnableInClassList("profiler__meter-fill--hot", db > -3f);
            dbLabel.text = float.IsNegativeInfinity(db) ? "-∞ dB" : $"{db:0.0} dB";
        }

        private void UpdateSnapshotProgress()
        {
            var active = NeziaProfiler.GetSnapshotProgress(out var total, out var remaining);
            SetVisible(_snapshotRow, active);
            if (active && total > 0)
            {
                _snapshotProgress.value = 1f - (float)remaining / total;
                _snapshotProgress.title = $"{(1f - (float)remaining / total) * 100f:0}%";
            }
        }

        private void UpdateBuses()
        {
            _busCount = NeziaProfiler.ReadBuses(_buses);
            _busesHeader.text = $"Buses ({_busCount})";

            // 行数をバス数に合わせる (作り直しは増減時のみ)。
            while (_busRowCache.Count < _busCount)
            {
                var row = MakeBusRow();
                _busRowCache.Add(row);
                _busRows.Add(row.Root);
            }
            while (_busRowCache.Count > _busCount)
            {
                var last = _busRowCache[^1];
                last.Root.RemoveFromHierarchy();
                _busRowCache.RemoveAt(_busRowCache.Count - 1);
            }

            for (var i = 0; i < _busCount; i++)
            {
                var bus = _buses[i];
                var row = _busRowCache[i];
                row.Name.text = ResolveBusName(bus.Index, bus.Generation);
                // gain 0..2 を 0..100% で表示 (1.0 = 中央 50%)。
                row.GainFill.style.width = Length.Percent(Mathf.Clamp01(bus.Gain / 2f) * 100f);
                row.GainValue.text = $"{bus.Gain:0.00}";
                row.Muted.text = bus.Muted ? "M" : string.Empty;
            }
        }

        private BusRow MakeBusRow()
        {
            var root = new VisualElement();
            root.AddToClassList("profiler__bus-row");
            var name = new Label();
            name.AddToClassList("profiler__bus-name");
            root.Add(name);
            var track = new VisualElement();
            track.AddToClassList("profiler__bus-gain-track");
            var fill = new VisualElement();
            fill.AddToClassList("profiler__bus-gain-fill");
            track.Add(fill);
            root.Add(track);
            var value = new Label();
            value.AddToClassList("profiler__bus-gain-value");
            root.Add(value);
            var muted = new Label();
            muted.AddToClassList("profiler__bus-muted");
            root.Add(muted);
            return new BusRow { Root = root, Name = name, GainFill = fill, GainValue = value, Muted = muted };
        }

        private void UpdateSources()
        {
            _sourceCount = NeziaProfiler.ReadSources(_sources);
            _sourcesHeader.text = $"Sources ({_sourceCount})";
            _bufferScannedThisPoll = false;

            _sourceIndices.Clear();
            for (var i = 0; i < _sourceCount; i++)
            {
                _sourceIndices.Add(i);
            }
            _sourcesList.itemsSource = _sourceIndices;
            _sourcesList.RefreshItems();
        }

        /// <summary>
        /// 選択中のソース行に対応する GameObject (NeziaAudioSource) を Hierarchy で
        /// 選択 + ping する。PlayOneShot / container / preview 等、コンポーネントに
        /// 紐付かないソースは対応先が無いため何もしない。
        /// </summary>
        private void PingSelectedSource()
        {
            var row = _sourcesList.selectedIndex;
            if (row < 0 || row >= _sourceCount)
            {
                return;
            }
            var src = _sources[row];
            foreach (var component in FindObjectsByType<NeziaAudioSource>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var id = component.SpawnedSourceId;
                if (id.index == src.Index && id.generation == src.Generation)
                {
                    Selection.activeGameObject = component.gameObject;
                    EditorGUIUtility.PingObject(component.gameObject);
                    return;
                }
            }
        }

        private void ConfigureSourceColumns()
        {
            SetupColumn("id", i => $"{_sources[i].Index}-{_sources[i].Generation}");
            SetupColumn("clip", i => ResolveClipName(_sources[i].BufferIndex));
            SetupColumn("bus", i => ResolveBusName(_sources[i].BusIndex, _sources[i].BusGeneration));
            SetupColumn("state", i =>
            {
                var s = _sources[i];
                var state = s.State.ToString();
                return s.IsVirtual ? state + " (virt)" : state;
            });
            SetupColumn("volume", i => $"{_sources[i].Volume:0.00}");
            SetupColumn("pitch", i => $"{_sources[i].Pitch:0.00}");
            SetupColumn("offset", i =>
            {
                var sr = NeziaEngine.OutputSampleRate;
                return sr > 0 ? $"{_sources[i].SampleOffset / sr:0.00}" : "-";
            });
        }

        private void SetupColumn(string name, System.Func<int, string> format)
        {
            var column = _sourcesList.columns[name];
            column.makeCell = () => new Label { style = { unityTextAlign = TextAnchor.MiddleLeft } };
            column.bindCell = (element, row) =>
            {
                var i = (int)_sourcesList.itemsSource[row];
                ((Label)element).text = format(i);
            };
        }

        // ─── バス名解決 ───────────────────────────────────────────

        /// <summary>
        /// NeziaSettings の DefaultMixer からバス EntityId → 論理名の対応を作る。
        /// Play Mode 開始後 (バス実体化後) に 1 度呼ぶ。未知のバスは "Bus i-g" 表示。
        /// </summary>
        private void RebuildBusNameMap()
        {
            _busNames.Clear();
            var master = NeziaEngine.MasterBus;
            _busNames[(master.Id.index, master.Id.generation)] = "Master";

            var mixer = NeziaSettings.Instance != null ? NeziaSettings.Instance.DefaultMixer : null;
            if (mixer == null)
            {
                return;
            }
            foreach (var node in mixer.Buses)
            {
                if (node == null || string.IsNullOrEmpty(node.name))
                {
                    continue;
                }
                var bus = mixer.Resolve(node.name);
                if (bus.IsValid)
                {
                    _busNames[(bus.Id.index, bus.Id.generation)] = node.name;
                }
            }
        }

        /// <summary>
        /// バッファプールスロット index からクリップ名を解決する。ロード済みの
        /// 全 NeziaAudioClip (アセット) をスキャンして対応表を作る。
        /// 未知の index はスキャンをやり直し、それでも不明なら "Buffer N" 表示
        /// (バイト列直ロードやストリーミング等、アセット外バッファ)。
        /// </summary>
        private string ResolveClipName(uint bufferIndex)
        {
            if (_bufferNames.TryGetValue(bufferIndex, out var name))
            {
                return name;
            }
            // 未知の index は 1 ポーリングにつき最大 1 回だけ全 Clip を再スキャン
            // (後発ロードの取り込み)。それでも見つからなければアセット外バッファ
            // (PlayOneShot / container の一時バッファ等) として "Buffer N" を
            // キャッシュし、以降このセッションでは再スキャンしない (10Hz スキャン地獄回避)。
            if (!_bufferScannedThisPoll)
            {
                _bufferScannedThisPoll = true;
                RebuildBufferNameMap();
                if (_bufferNames.TryGetValue(bufferIndex, out name))
                {
                    return name;
                }
            }
            name = $"Buffer {bufferIndex}";
            _bufferNames[bufferIndex] = name;
            return name;
        }

        private void RebuildBufferNameMap()
        {
            _bufferNames.Clear();
            foreach (var clip in Resources.FindObjectsOfTypeAll<NeziaAudioClip>())
            {
                if (clip.TryGetLoadedBufferIndex(out var index))
                {
                    _bufferNames[index] = clip.name;
                }
            }
        }

        private string ResolveBusName(uint index, uint generation)
        {
            if (index == uint.MaxValue)
            {
                return "-";
            }
            return _busNames.TryGetValue((index, generation), out var name)
                ? name
                : $"Bus {index}-{generation}";
        }

        private static void SetVisible(VisualElement element, bool visible) =>
            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
    }
}
