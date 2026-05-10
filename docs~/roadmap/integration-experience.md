# Roadmap — Integration Experience

`jp.nezia.unity` (Unity 統合層) を **「Inspector とプレハブだけで音設計が成立する状態」** に
仕上げるためのロードマップ。core 側のロードマップ
[`better-than-unity-audio.md`](../../../../nezia-core/docs/roadmap/better-than-unity-audio.md) と
コンセプト [`CONCEPT.md`](../../../../nezia-core/docs/design/integration/CONCEPT.md) を前提に、
**Integration 固有の authoring / runtime 体験**にフォーカスする。

> 領域横断の優先順序と判断基準のみを扱う。個別機能の詳細設計は `docs/design/*.md`
> （未作成・必要に応じて追加）に切り出す。

---

## 立ち位置と定義

| 層 | 責務 | リポジトリ |
|---|---|---|
| **core** | DSP / ECS / FFI / 補間アルゴリズム | `nezia-core` |
| **Integration** | Unity 上の authoring 体験・MonoBehaviour ブリッジ・アセット型 | `Nezia.Unity` (本リポジトリ) |

core が「**鳴らすことができる**」を満たした後、Integration が「**鳴らしやすい**」を満たすことで
プロダクション採用に到達する。**core の機能漏れを Integration で補うのは原則禁止**
（順序が逆転して保守不能になる）。

「Inspector とプレハブだけで音設計が成立」とは具体的に:

1. **ミキサーグラフを Inspector で設計** — バス階層・エフェクト・Send 配線
2. **Sound Asset を Inspector で配線** — `NeziaAudioSource` への D&D だけで再生可
3. **Snapshot / Cue / Streaming もアセット型** — コードの初期化スクリプト不要
4. **Inspector でプレビュー** — 試聴・パラメータ調整がエディタ内で完結
5. **コード側に残るのは「いつ鳴らすか」のトリガだけ** — 設計はデータ駆動

---

## 設計思想 — Clip-centric authoring

サウンドミドルウェア（Wwise / FMOD / CRI ADX2）の発想に倣い、
**「音の鳴り方は Clip (SoundAsset) が決める。Source は『いつ・どこで』だけ」** を中核に据える。

Bus / Send / Snapshot がアセット化された今、AudioSource 側に音響パラメータが散在すると:

- 同じ Clip でも鳴らす場所で挙動が変わり、Bus ルーティングや Snapshot との整合が取れない
- 「足音は 3D で BGM Bus から SFX Bus へ Send する」といった音側の本質的な性質が
  プレハブ側に書き散らされる
- 大規模プロジェクトで「全足音の最大距離だけ調整したい」が困難

そこで責務を以下のように二層化する:

| レイヤ | 持つもの | 例 |
|---|---|---|
| **Clip / SoundAsset** (鳴り方) | volume / pitch (基準値・ランダム範囲) / loop / outputBus / spatial 設定 / attenuationCurve / doppler / 基準 priority / effect chain / send routing | 「足音は 3D・SFX Bus・priority 192」 |
| **Source** (トリガとインスタンス) | 再生対象アセット参照 / playOnAwake / mute / volumeScale / pitchScale / priority override / position (transform 由来) | 「このオブジェクトは playOnAwake、音量を 0.5 倍」 |

### Unity ユーザーへの配慮（Override モデル）

Wwise/FMOD 流の Clip-centric を貫きつつ、`NeziaAudioSource` を Unity の `AudioSource` 互換のまま
扱える設計を維持する:

- `source.volume = 0.5f` は **Clip 基準値への乗算 (scale)** として動く（破壊的変更を最小化）
- `source.pitch = 1.2f` も同様。Clip 側の pitchRandom と独立に乗算される
- Inspector では Clip 由来のデフォルトをグレー表示し、`Override` チェックでインスタンス上書き可能
  （Cinemachine / HDRP Volume 風の UX）
- AudioSource からの自動移行 (`ReplaceAudioSourcesMenu`) は、現状の Source 上の設定を
  **同名 Clip の Variant** に焼き直して、Source 側は scale=1 で残す

### 1 Clip = 1 設計、Variant で派生

「Clip 編集が全使用箇所に波及する」のは Wwise/FMOD と同じく**思想として正解**。
シーンごとの揺らぎが必要なら Project ビューで右クリック → **Create Variant** で
派生 Clip を作る運用を推奨し、Editor 拡張で Variant 作成を 1 操作に収める。

---

## 現状のギャップ分析

各機能を「コードでは可能」「Inspector で可能」「プレビュー可能」「Clip-centric 的に正しい場所にある」の 4 軸で評価。

### A. ミキサーグラフ

| 機能 | コード | Inspector | プレビュー | 区分 |
|---|:-:|:-:|:-:|---|
| バス階層構築 | ○ | ○ | ✕ | 完了 (IP-1) |
| エフェクト挿入 | ○ | ○ | ✕ | 完了 (IP-1) |
| Send 配線 | ○ | ○ | ✕ | 完了 (IP-1) |
| Compressor sidechain | ○ | ○ | ✕ | 完了 (IP-1) |
| `AudioMixerGroup` ↔ Bus 解決 | ○ | △ (`NeziaBusMap`) | ✕ | 改善余地 |

### B. Sound Asset（責務の正しさ含む）

| 機能 | コード | Inspector | プレビュー | 配置 | 区分 |
|---|:-:|:-:|:-:|:-:|---|
| 単発クリップ (`NeziaAudioClip`) | ○ | ○ | ✕ | △ (鳴り方は Source 側にある) | プレビュー欠落 + **責務再設計** |
| Random Container | ○ | ○ | ✕ | △ | 同上 |
| Clip 基準 volume/pitch ランダマイズ | ✕ | ✕ | ✕ | — | **未実装** |
| Clip 既定 outputBus / 空間設定 | ✕ | ✕ | ✕ | — | **未実装** |
| Clip 既定 effect chain / send | ✕ | ✕ | ✕ | — | **未実装** |
| ストリーミング再生 | ○ | ✕ | ✕ | — | アセット型未整備 |
| カスタム減衰カーブ | ○ | ○ | ✕ | △ (Source 持ち) | 責務再設計 |
| Cue / Event 層 (文字列キー) | ✕ | ✕ | ✕ | — | 未実装 |

### C. Source 制御

| 機能 | コード | Inspector | プレビュー | 配置 | 区分 |
|---|:-:|:-:|:-:|:-:|---|
| 基本再生 (Play/Stop/Pause) | ○ | ○ | ✕ | ○ | OK |
| volume / pitch (scale) | ○ | ○ | ✕ | ○ (scale 化後) | **責務再設計対象** |
| loop / spatialBlend / 距離 / Doppler / priority | ○ | ○ | ✕ | ✕ (Clip へ移動) | **責務再設計対象** |
| outputBus / mixerAsset+busName | ○ | ○ | ✕ | ✕ (Clip へ移動) | **責務再設計対象** |
| エフェクト挿入 | ○ | ✕ | ✕ | ○ (一時的 effect) | 致命傷 |
| `PlayScheduled` (sample 精度同期) | ✕ | ✕ | ✕ | ○ | 未実装 |

### D. Snapshot

| 機能 | コード | Inspector | プレビュー | 区分 |
|---|:-:|:-:|:-:|---|
| 構築 (`Begin().Set...Commit()`) | ○ | ○ | ✕ | 完了 (IP-3) |
| 適用 (`Apply(fade)`) | ○ | ○ | ✕ | 完了 (IP-3) |

### E. Effect API ergonomics

| 観点 | 現状 | 区分 |
|---|---|---|
| パラメータ参照 | type-safe (`AsLowPass().Cutoff`) | 完了 (IP-2) |
| プリセット | ✕ | 未実装 |

### F. デバッグ / 可視化

| 機能 | 状況 | 区分 |
|---|---|---|
| 現在のバスツリー閲覧 | ✕ | 未実装 |
| アクティブソース一覧 | ✕ | 未実装 |
| マスター出力波形 / dB メーター | ✕ (Capture API は出ている) | 未実装 |
| Snapshot 進行可視化 | ✕ | 未実装 |

---

## フェーズ分け

優先順は **「致命傷を埋める → 責務再設計 → 差別化体験」** を厳守する。
core 側の Phase と混同しないため `IPx` (Integration Phase) と表記する。

完了済みフェーズ (IP-1 / IP-2 / IP-3) は履歴として残す。

### IP-1. ミキサーアセット化 — `NeziaMixerAsset` 【完了】

ScriptableObject でバス階層・エフェクト・Send 配線を Inspector 設計可能にした。

PR-A (バスツリー) #26 / PR-B (バスごとのエフェクト挿入) #27 / PR-C (Send 配線・sidechain) #29

### IP-2. Effect type-safe API 【完了】

`byte index` API を kind ごとの type-safe ラッパで覆った。 #25

### IP-3. Snapshot アセット化 — `NeziaSnapshotAsset` 【完了】

PR-A (Phase 1: バスゲイン / ミュート) #31 / PR-B (Phase 2: Send ゲイン・エフェクトパラメータ) #32

---

### IP-4. Clip-centric 責務再設計【最優先・思想転換】

「設計思想」節で示した Clip / Source の二層化を実装する。
ここが入らないと以降のフェーズ（Source Effect Slot, Sound Dictionary 等）の
依存関係が破綻するため、**他の未着手フェーズより必ず先に着手する。**

破壊的変更を含むため、PR を分割してマイグレーションパスを段階的に提供する。

#### IP-4 PR-A: `NeziaSoundAsset` への音響パラメータ拡張

`NeziaSoundAsset` (および派生 `NeziaAudioClip` / `NeziaRandomContainer`) に
鳴り方を司るフィールドを追加する。Source 側はまだ変更しない（既存値が常に勝つ）。

**含む:**
- 基準 volume / pitch（および pitchRandom / volumeRandom 範囲）
- loop （Clip 属性として）
- outputBus 参照（`NeziaMixerAsset` + バス名）
- spatial 設定一式（spatialBlend / minDistance / maxDistance / rolloffMode / attenuationCurve / dopplerLevel）
- 基準 priority
- effect chain（永続エフェクト）
- send routing（永続 send）

`Spawn` 経路で Clip 由来の値をネイティブに反映する。Source からの引数は
「Clip 値に対する scale / override」として渡せる FFI シグネチャに調整する。

**完了条件:**
- 新規 Clip を作成して Inspector で 3D / SFX Bus / 距離 1〜30m を設定し、
  どの GameObject に貼り付けても同じ鳴り方をする

#### IP-4 PR-B: `NeziaAudioSource` の Override モデル化

Source 側のプロパティを「Clip 基準値への scale / 任意 override」に再定義する。

**含む:**
- `volume` / `pitch` を **Clip 値への乗算** として再解釈（setter 互換維持）
- `loop` / `spatialBlend` / 距離 / `rolloffMode` / `attenuationCurve` / `dopplerLevel` /
  `priority` / `outputBus` 等は **Override フラグ + 値** モデルに変更
- Override 未指定なら Clip 値が使われる
- Inspector カスタムエディタで「Clip 値（グレー表示） / Override チェック / 上書き値」
  の三段表示
- `playOnAwake` / `mute` / `volumeScale` 相当はそのまま Source 側

**互換性メモ:**
- 既存スクリプト (`source.volume = 0.5f`) は破綻させない
- `source.spatialBlend = 1f` 等は Override フラグを暗黙 ON にして従来通り効かせる
- 旧フィールドのシリアライズデータは ISerializationCallbackReceiver 等で
  PR-C のマイグレーションが拾える形に保持

**完了条件:**
- 既存サンプルシーンが PR-A 後に Clip を整備しただけでそのまま動作する
- Inspector で「Clip 値を尊重 / 一時的に上書き」が一目でわかる

#### IP-4 PR-C: マイグレーション & `ReplaceAudioSourcesMenu` 更新

旧 Source 設定を Clip Variant に焼き直す移行ツールを提供する。

**含む:**
- 既存 `NeziaAudioSource` の serialized 値を読み取り、対応 Clip の Variant を
  Project に生成して差し替えるエディタコマンド（プロジェクト一括 / 個別）
- `ReplaceAudioSourcesMenu`（標準 `AudioSource` → `NeziaAudioSource` 自動化）を
  新モデル対応に更新。AudioSource 上の設定は Clip Variant に焼き、Source は scale=1 で残す
- Project ビュー右クリック → **Create Variant** で Clip 派生を 1 操作で作る Editor 拡張

**完了条件:**
- 既存ユーザーが「メニュー 1 つ」で旧プロジェクトを新モデルに移行できる
- ドキュメントに移行手順が記載されている

#### IP-4 PR-D: ドキュメント刷新

- README — 設計思想・Quickstart・Mixer authoring・サンプル節・最小コード例を整備
- 移行ガイドを [`docs~/migration/clip-centric.md`](../migration/clip-centric.md) に切り出し
- `integration-experience.md` 冒頭の「設計思想 — Clip-centric authoring」節で
  「Clip は鳴り方を持つ／Source は鳴らすだけ」を明示
- `Samples~/ClipCentricBasics/` を新設し Package Manager の Samples から
  取り込める最小サンプル 3 本を提供:
  - `SimpleClipPlayback` — Clip 1 つを `useClipDefaults=true` で鳴らす最小例
  - `VolumePitchScaling` — `source.volume` / `source.pitch` が Clip 値への scale として効く確認
  - `SourceOverrideExample` — `outputBus` / `spatialBlend` を per-instance で override

---

### IP-5. `NeziaAudioSource` Effect Slot

IP-4 完了が前提（Clip の永続 effect chain と区別するため）。
Source 側のエフェクトは「このインスタンスにだけ効く一時的なフィルタ」として位置づける。

**含む:**
- `[SerializeField] EffectSlot[] sourceEffects`（Kind + Position + 初期パラメータ）
- `Play()` 時に自動挿入、`Stop()` で自動 Remove
- Clip 側 effect chain と直列に並ぶ動作を明文化

### IP-6. Sound Asset プレビュー 【authoring 体験の即効薬／別途デーモン依存】

Inspector に再生ボタン・波形を出す。authoring 中の試聴は他のどんな自動化より先に効く。

**方針:**
- プレビュー再生は **別途構築する Nezia プレビューデーモン** に委譲する
  （Editor プロセスから IPC で再生要求を送り、デーモン側で core エンジンを駆動）。
- **Editor 側でバイナリ（音声ファイルのデコード結果や生 PCM）を直接扱う実装はしない。**
  Unity 標準の `AudioClip` / `AudioSource` を使った代替再生も採用しない（実機との挙動乖離が出るため）。
- したがってこのフェーズは **デーモン仕様が固まるまで本実装に入らない**。Editor 側に
  プレビュー API を露出する場合も、デーモン IPC への薄いラッパに留める。

**含む（デーモン稼働後）:**
- `NeziaAudioClip` Inspector: Play / Stop ボタン + メタデータ表示（波形は import 時にメタとして
  生成済みのものを表示。Editor で生 PCM をデコードしない）。**Clip 側の音響パラメータ込みで** 試聴できること
- `NeziaRandomContainer` Inspector: Play (ランダム選択) ボタン + 子のリスト編集体験向上
- `NeziaAttenuationCurveAsset` Inspector: 距離スライダで gain 値プレビュー（任意）
- Editor-only、ランタイムには影響なし

**完了条件:**
- アーティストが Project ビューでクリック → ▶ ボタンでデーモン経由で音が出る
- Editor アセンブリが音声バイナリを直接処理するコードを持たない

**先行調査としてやってよいこと:**
- Inspector UI のレイアウト・メタデータ表示・波形プレースホルダ等、再生バックエンドに依存しない部分
- デーモン IPC のインターフェース設計

### IP-7. `PlayScheduled`（sample 精度同期）

リズムゲーム / ループ音源クロスフェードで必須。`AudioSource.PlayScheduled` 互換。

**含む:**
- `NeziaAudioSource.PlayScheduled(double dspTime)`
- core 側 FFI に schedule API 追加が必要かを先に調査
  （現状 `nezia_source_play_with_handle` には開始時刻指定がない）
- 調査結果次第で **core 側 PR が前提**になる可能性 → このフェーズは依存待ち

### IP-8. Cue / Event 層 — `NeziaSoundDictionary`

文字列キーから SoundAsset 解決、Wwise/FMOD 的データ駆動。
IP-4 完了で Clip 自体が完結した発音単位になっているため、Dictionary は単純な解決層で済む。

**含む:**
- `NeziaSoundDictionary` (`ScriptableObject`) — `Dictionary<string, NeziaSoundAsset>`
- `NeziaEngine.Play(eventName, ...)` — 解決ショートカット
- 階層キー (`"sfx/footstep/grass"` 等) の慣習を README で示す程度

### IP-9. ストリーミングアセット化 — `NeziaStreamingAudioClip`

`NeziaBuffer.LoadStreaming("path")` を Inspector に乗せる。IP-4 と同じ
SoundAsset 拡張ルールに従う（Clip 側に音響パラメータを持つ）。

**含む:**
- `NeziaStreamingAudioClip : NeziaSoundAsset`
- StreamingAssets 相対パスフィールド + `bufferSeconds`
- `NeziaAudioSource` に D&D で BGM ストリーミング再生

### IP-10. デバッグ / 可視化ウィンドウ — `Tools > Nezia > Mixer Inspector`

EditorWindow で IP-1 のアセットや実行中の状態を可視化。

**含む:**
- 現在のバスツリー（IP-1 の `NeziaMixerAsset` がランタイムでどう実体化されたか）
- アクティブソース一覧（id / clip / 適用 volume / position / どの Override が効いているか）
- マスター出力 dB メーター（既存 `NeziaMasterCapture` を tap）
- Snapshot 進行バー

> Editor 単体で音を鳴らす系（プレビュー）は IP-6 と同じくプレビューデーモン側で扱う。
> 本フェーズの可視化はランタイム実行中の状態を覗くもので、Editor 側で音声バイナリを
> 触る方向には広げない。

優先度は低いがデバッグ効率に直結する。

### IP-12. Project-level Mixer + Mixer Editor Window

URP の `GraphicsSettings.defaultRenderPipeline` 方式に倣い、プロジェクト全体の
グローバル Bus 構成を `Project Settings > Nezia` から **アセット参照1本** で
管理する。あわせてバスツリー / Effect chain / Send 配線を直感的に編集できる
専用 EditorWindow を導入し、Inspector の flat list を超えた authoring 体験に
する。

**位置付け:**
- 既存 `NeziaMixerAsset`（IP-1）はそのまま実体として再利用。シリアライズ
  形式は変えない（破壊的変更なし）。
- 「アクティブなミキサー構成は同時に 1 つ」という Wwise / FMOD 慣習を
  仕様レベルで明文化する。複数 Asset は override / プラットフォーム差し替え
  用の派生として残す。

**UI 基盤の選定:**

最初は Unity 6.2 公式 **Graph Toolkit (`com.unity.graphtoolkit`)** で実装
（IP-12 PR-1 + PR-2 として `.neziamixer` ScriptedImporter + `NeziaMixerBusNode`
を一度マージ）したが、以下の理由で **GTK 採用を撤回**:

- GTK は **純データフロー DAG** を前提としており（Unity 自身が CHANGELOG で
  "We no longer impose semantics like 'execution flow' on ports" と明言）、
  バスツリーの構造的 parent-child 関係を表現するのに不向き
- ポート型を持つ限り `<Type>Constant` ノードが ItemLibrary に自動生成される
  仕様で、これを公開 API で抑制できない（`SupportedTypes` /
  `ItemLibraryHelper` がすべて `[UnityRestricted] internal`）
- typeless port は GTK 内部で `NullReferenceException`
- `0.4.0-exp.2` で API 破壊変更が継続中

**新方針: TreeView + UI Toolkit ListView + Send タブのハイブリッド構成**

Wwise / FMOD / Unity Audio Mixer / CRI ADX2 など業界標準ツールが採用する
「Hierarchy ペイン + プロパティパネル + Send タブ」UX に揃える。Unity 標準
API のみで完結し将来安定。

| 領域 | UI |
|---|---|
| バスツリー（階層・親子） | UI Toolkit `TreeView`（Unity 6 で強化済み） |
| 選択バスのプロパティ / Effect chain | 右ペインに `UI Toolkit ListView` + Inspector |
| Send / Compressor sidechain 配線 | 別タブで「source → target + gain + position」のリスト UI |

#### IP-12 PR-0a: `NeziaSettings` 導入（Project Settings 連携）

- `NeziaSettings : ScriptableObject`（`Runtime/`）— `defaultMixer:
  NeziaMixerAsset` を 1 フィールドだけ持つ singleton SO。将来の global 設定の
  受け皿でもある。
- `Project Settings > Nezia` ページ（`Editor/NeziaSettingsProvider`）— URP の
  Graphics ページと同じ作り。`Settings Asset:` の ObjectField + `Create New` +
  inline Inspector。
- 参照保持は `EditorBuildSettings.AddConfigObject(GUID, asset, overwrite:true)`
  方式で `ProjectSettings/EditorBuildSettings.asset` に GUID 1 本を持つ。
  実体 SO は `Assets/` 配下なので通常のバージョン管理に乗る。
- 登録時に PlayerSettings の preloaded assets に追加 → ランタイムは
  `NeziaSettings.Instance` で取得（`Resources` 不要）。
- **自動生成**: パッケージ導入後の Editor 起動時に `Assets/Settings/NeziaSettings.asset`
  を自動生成し、`EditorBuildSettings` と preloaded assets へ登録する
  （`[InitializeOnLoadMethod]`）。ユーザーは Project Settings ページを開かずとも
  既定状態で動く。プロジェクト内に既存 `NeziaSettings` が見つかれば再利用。
- 解決順統合: `NeziaSoundAsset.ResolveOutputBus()` と `NeziaAudioSource.Start()`
  に「明示 mixer 指定 → なければ `NeziaSettings.Instance.DefaultMixer`」の
  フォールバックを足す。既存挙動は破壊しない。

#### IP-12 PR-A / PR-B: `NeziaMixerInspector` (Custom Inspector)

`NeziaMixerAsset` の CustomEditor として実装。Project ビューでアセットを選択する
（または `Project Settings > Nezia` の inline Inspector）と専用 UI でバスツリーを
編集できる。当初 `EditorWindow` で実装したが、ツリー + プロパティ編集は Inspector
に乗せる方が Unity-idiomatic（Animator / Audio Mixer 等の専用ウィンドウは
「特殊な可視化」用で、本ケースには過剰）と判断し、PR-B のレビュー過程で
CustomEditor に転換した。

- `[CustomEditor(typeof(NeziaMixerAsset))]` + `CreateInspectorGUI()`
- 2 ペイン構成 (`TwoPaneSplitView`): 左 = TreeView / 右 = 選択バスの Inspector
- `+ Add Bus` (兄弟として追加・Wwise/FMOD 流) / `− Delete` (子は親に昇格)
- 右ペインで `Name` / `Gain` / `Muted` 編集
  - `Name` / `Gain` 数値は `isDelayed = true`（入力中の二重 commit 回避）
  - 空文字 rename は拒否
  - リネーム時に子バスの `parent` 参照を自動追従
- Drag & drop で親変更（自身 / 子孫へのドロップは循環防止で reject）
- 全編集 `Undo.RecordObject` 経由（Ctrl+Z 対応）
- 仮想 Master ルートを単一 root として表示。TreeView の id 衝突回避のため
  Master = 1 / 実バス `i` = `i + 2` のオフセット採番
- 空名 bus は `(unnamed)` を橙字で tree に表示
- `NeziaMixerAsset.Validate()` をフッタにリアルタイム表示

#### IP-12 PR-C: Effect chain ペイン

- 選択 Bus の右ペインに Effect 行リスト
- `+ Add` ドロップダウンメニューから kind 選択 (LowPass / HighPass / Reverb / Compressor)
- 各 effect 行: ▲▼ 並べ替えボタン / kind ラベル / enabled トグル / × 削除
- per-kind パラメータをインライン編集（Slider + 数値フィールド合成、isDelayed=true）
- `[SerializeReference]` の多態シリアライズを活かしつつ、List 順序が effect 挿入順
- 全編集 `Undo.RecordObject`、編集後 `InvalidateResolvedCache` で次回 Resolve 時に再構築
- 右ペインを `ScrollView` でラップし、effect が増えても縦に伸ばせる

#### IP-12 PR-D: Send / sidechain 編集

`NeziaMixerInspector` 上部に `Buses` / `Sends` のタブストリップを追加し、
Send 配線を専用 UI で編集できる。Buses タブは PR-A〜C のバス編集 UI を
そのまま、Sends タブは Send 行のスクロールリストを表示する。

- `+ Add Send` で行を追加。各行は `source bus` / `target kind`
  (`Bus` / `CompressorSidechain`) / `target bus` のドロップダウンに加え、
  sidechain 時は対象バス上の **Compressor インデックス**ピッカーが現れる
- 各 Send 行で `Position` (Pre / Post) と `Gain` (0〜4) を編集
- 全編集 `Undo.RecordObject`、編集後 `InvalidateResolvedCache`
- 値編集 (gain / position / source / target) では行を再生成せず Slider の
  ドラッグ連続性を保つ。target kind / target bus 変更時のみ再構築
- 不正 Send は既存の `NeziaMixerAsset.Validate` がフッタに警告として表示
  （未知バス / sidechain 先が Compressor でない / source==target 等）
- (将来検討) source × target の matrix view は規模が大きい設定でしか有用に
  ならないため、必要が見えるまで保留

#### IP-12 PR-E: ドキュメント整備

専用ドキュメント [`docs~/mixer-authoring.md`](../mixer-authoring.md) を新設し、
`NeziaMixerInspector` の使い方を集約した。README の Quickstart からも
導線を張る。

- 起動導線: Project ビュー / `Project Settings > Nezia` の inline Inspector
- Buses タブ: バスの追加・削除・親変更・リネーム・Inspector ペイン
- Effects: 種別追加・並べ替え・enabled・per-kind パラメータ
- Sends タブ: bus→bus / Compressor sidechain (ducking) の組み立て手順
- バリデーションフッタが拾うエラー例の一覧
- Undo / Redo 対象操作の一覧
- (旧計画にあった "Open in Mixer Editor" ボタン / `OnOpenAsset` 連携は
  不要になったため、対応する記述は入れない。Inspector が起動導線を兼ねる)

**完了条件:**
- 新規プロジェクトでアセットを 1 つも作らずとも「BGM/SE Bus が鳴る」状態を
  Project Settings 1 ページのセットアップで作れる
- バスツリーの追加・属性編集・Effect 挿入・Send 配線が `NeziaMixerWindow`
  だけで完結する
- Wwise / FMOD ユーザーが既視感のある UX で操作できる

---

### IP-11. その他軽微

- `NeziaAudioListener` の velocity 平滑化係数 / Doppler 上限の Inspector 化
- `NeziaMasterCapture` を Unity Recorder 連携サンプルとして同梱
- Container per-instance voice limit（**core 側の対応が前提**）

---

## 完了条件マトリクス

| フェーズ | コード初期化ゼロ | Inspector 設計 | プレビュー | 致命傷 | 差別化 |
|---|:-:|:-:|:-:|:-:|:-:|
| IP-1 Mixer Asset | ◎ | ◎ |   | ★ |   |
| IP-2 Effect type-safe | ○ | ○ |   | ★ |   |
| IP-3 Snapshot Asset | ◎ | ◎ |   | ★ |   |
| **IP-4 Clip-centric 再設計** | **◎** | **◎** |   | **★** | **★** |
| IP-5 Source Effect Slot |   | ◎ |   |   | ★ |
| IP-6 Asset Preview |   |   | ◎ |   | ★ |
| IP-7 PlayScheduled | ○ |   |   |   | ★ |
| IP-8 Sound Dictionary | ◎ | ◎ |   |   | ★ |
| IP-9 Streaming Asset |   | ◎ |   |   |   |
| IP-10 Mixer Inspector |   |   | ◎ |   | ★ |

---

## 設計上のガードレール

1. **core の機能漏れを Integration で補わない** — core 側 PR で対応する
2. **Clip-centric 思想を曲げない** — 鳴り方は Clip / 鳴らす場所は Source。
   「Source 側に設定を増やすほうが楽」という誘惑は Variant で解消する
3. **既存ユーザーを壊さない（API 互換性）** — `source.volume` / `source.pitch` 等の
   よく使われる setter は scale 化後も動く。破壊的変更は Override モデルへの
   段階的 PR とマイグレーションコマンドで吸収する
4. **Editor-only コードは `Editor/` ディレクトリに分離** — Runtime asmdef を肥らせない
5. **AudioSource / AudioMixer 互換性を保つ（移行コスト）** — `ReplaceAudioSourcesMenu` で
   既存プロジェクトを 1 操作で移せる状態を維持
6. **アセット参照は基底型で受ける** — Container ネスト等の将来拡張で Inspector 構造を変えない
   （`NeziaSoundAsset[]` の方針を踏襲）

---

## 関連ドキュメント

- core ロードマップ: [`nezia-core/docs/roadmap/better-than-unity-audio.md`](../../../../nezia-core/docs/roadmap/better-than-unity-audio.md)
- 統合戦略: [`nezia-core/docs/design/integration/CONCEPT.md`](../../../../nezia-core/docs/design/integration/CONCEPT.md)
- Container 設計（Unity 露出方針も含む）: [`nezia-core/docs/design/core/container.md`](../../../../nezia-core/docs/design/core/container.md)
- Snapshot 設計: [`nezia-core/docs/design/core/snapshot.md`](../../../../nezia-core/docs/design/core/snapshot.md)

---

## ステータス

| 項目 | 状態 | 担当 PR |
|---|---|---|
| IP-1 Mixer Asset | 完了 | #26 (PR-A), #27 (PR-B), #29 (PR-C) |
| IP-2 Effect type-safe | 完了 | #25 |
| IP-3 Snapshot Asset | 完了 | #31 (PR-A), #32 (PR-B) |
| **IP-4 Clip-centric 再設計** | **PR-A〜D 完了** | #33 (PR-A), #35 (PR-B), #36 (PR-C1), #37 (PR-C2), PR-D (samples + docs) |
| IP-5 Source Effect Slot | IP-4 待ち | — |
| IP-6 Asset Preview | デーモン依存により保留（バイナリ直接操作の実装路線は廃止） | — |
| IP-7 PlayScheduled | 調査前 | — |
| IP-8 Sound Dictionary | IP-4 待ち | — |
| IP-9 Streaming Asset | IP-4 のルールに従って実装予定 | — |
| IP-10 Mixer Inspector | 未着手 | — |
| IP-11 その他軽微 | 未着手 | — |

各フェーズ着手時に PR 番号を埋める。
