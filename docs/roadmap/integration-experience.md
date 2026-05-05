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

## 現状のギャップ分析

各機能を「コードでは可能」「Inspector で可能」「プレビュー可能」の 3 軸で評価。

### A. ミキサーグラフ

| 機能 | コード | Inspector | プレビュー | 区分 |
|---|:-:|:-:|:-:|---|
| バス階層構築 | ○ | ✕ | ✕ | **致命傷** |
| エフェクト挿入 | ○ | ✕ | ✕ | **致命傷** |
| Send 配線 | ○ | ✕ | ✕ | **致命傷** |
| Compressor sidechain | ○ | ✕ | ✕ | 致命傷 |
| `AudioMixerGroup` ↔ Bus 解決 | ○ | △ (`NeziaBusMap`) | ✕ | 改善余地 |

### B. Sound Asset

| 機能 | コード | Inspector | プレビュー | 区分 |
|---|:-:|:-:|:-:|---|
| 単発クリップ (`NeziaAudioClip`) | ○ | ○ | ✕ | プレビュー欠落 |
| Random Container | ○ | ○ | ✕ | プレビュー欠落 |
| ストリーミング再生 | ○ | ✕ | ✕ | アセット型未整備 |
| カスタム減衰カーブ | ○ | ○ | ✕ | プレビュー欠落 |
| Cue / Event 層 (文字列キー) | ✕ | ✕ | ✕ | 未実装 |

### C. Source 制御

| 機能 | コード | Inspector | プレビュー | 区分 |
|---|:-:|:-:|:-:|---|
| 基本再生 (vol/pitch/loop/...) | ○ | ○ | ✕ | OK |
| 空間オプション (3D blend / Doppler / priority) | ○ | ○ | ✕ | OK |
| エフェクト挿入 | ○ | ✕ | ✕ | 致命傷 |
| `PlayScheduled` (sample 精度同期) | ✕ | ✕ | ✕ | 未実装 |

### D. Snapshot

| 機能 | コード | Inspector | プレビュー | 区分 |
|---|:-:|:-:|:-:|---|
| 構築 (`Begin().Set...Commit()`) | ○ | ✕ | ✕ | **致命傷** |
| 適用 (`Apply(fade)`) | ○ | ✕ | ✕ | **致命傷** |

### E. Effect API ergonomics

| 観点 | 現状 | 区分 |
|---|---|---|
| パラメータ参照 | `effect.SetParam(byte index, float)` — kind ごとに意味が異なる | **使いにくい** |
| Type-safe API | ✕ | 未実装 |
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

優先順は **「致命傷を埋める → 差別化体験」** を厳守する。core 側の Phase と
混同しないため `IPx` (Integration Phase) と表記する。

### IP-1. ミキサーアセット化 — `NeziaMixerAsset` 【致命傷】

ScriptableObject でバス階層・エフェクト・Send 配線を Inspector 設計可能にする。
core 側の `NeziaBus` / `NeziaEffect` / `NeziaSend` への薄い宣言型レイヤ。

**含む:**
- `NeziaMixerAsset` — Master/BGM/SFX/Voice/UI 等のバスツリーを保持
- `NeziaMixerAsset.Build(engine)` — ランタイムでバスを実体化、internal Dictionary で参照
- バス毎のエフェクト挿入を Inspector で記述（Kind + Position + 初期パラメータ）
- バス間 Send 配線（Pre/Post + gain）
- Compressor sidechain も Inspector で完結
- `NeziaAudioSource.outputBus` がアセット内バス参照を直接解決可
- 既存 `NeziaBusMap` は段階的に置換 or 互換維持で吸収

**完了条件:**
- 「Master + BGM + SFX」の 3 バスツリーを Inspector で組み、`NeziaAudioSource` から
  ドロップイン参照して鳴らせる
- コード側の初期化スクリプトが**ゼロ**になる

### IP-2. Effect type-safe API 【致命傷】

`byte index` API を kind ごとの type-safe ラッパで覆う。

**含む:**
- `effect.AsLowPass()` / `AsHighPass()` / `AsReverb()` / `AsCompressor()` 拡張
- 各々に `Cutoff` / `Q` / `RoomSize` / `Wet` / `Threshold` 等の名前付きプロパティ
- 既存 `SetParam(byte, float)` は internal 化 or `[Obsolete]` で温存
- IP-1 と組み合わせると Inspector 側のパラメータ UI が kind 別に明確化される

**完了条件:**
- ドキュメントを引かずに `lpf.AsLowPass().Cutoff = 500f` が書ける
- IDE 補完で全パラメータが見える

### IP-3. Snapshot アセット化 — `NeziaSnapshotAsset` 【致命傷】

IP-1 が入って Bus/Send/Effect がアセット参照可能になった上で実装する。

**含む:**
- Phase 1: バスゲイン / バスミュートのみ
- Phase 2: Send ゲイン / エフェクトパラメータも対応（IP-1 のアセット参照を再利用）
- `asset.Apply(mixer, fadeSeconds)` — ToNative + Apply + Destroy のショートカット

**完了条件:**
- BGM の Normal / Battle / Boss スナップショットを Inspector で組み、
  ゲームコードから `boss.Apply(2.0f)` だけで遷移できる

### IP-4. Sound Asset プレビュー 【authoring 体験の即効薬】

Inspector に再生ボタン・波形を出す。authoring 中の試聴は他のどんな自動化より先に効く。

**含む:**
- `NeziaAudioClip` Inspector: Play / Stop ボタン + 波形 + メタデータ表示
- `NeziaRandomContainer` Inspector: Play (ランダム選択) ボタン + 子のリスト編集体験向上
- `NeziaAttenuationCurveAsset` Inspector: 距離スライダで gain 値プレビュー（任意）
- Editor-only、ランタイムには影響なし

**完了条件:**
- アーティストが Project ビューでクリック → ▶ ボタンで音が出る

### IP-5. `NeziaAudioSource` Effect Slot

IP-1 / IP-2 が前提。Inspector で Source にエフェクトを並べる。

**含む:**
- `[SerializeField] EffectSlot[] effects`（Kind + Position + 初期パラメータ）
- `Play()` 時に自動挿入、`Stop()` で自動 Remove
- AudioMixerGroup の "Inspector でフィルタを並べる" 体験の代替

### IP-6. `PlayScheduled`（sample 精度同期）

リズムゲーム / ループ音源クロスフェードで必須。`AudioSource.PlayScheduled` 互換。

**含む:**
- `NeziaAudioSource.PlayScheduled(double dspTime)`
- core 側 FFI に schedule API 追加が必要かを先に調査
  （現状 `nezia_source_play_with_handle` には開始時刻指定がない）
- 調査結果次第で **core 側 PR が前提**になる可能性 → このフェーズは依存待ち

### IP-7. Cue / Event 層 — `NeziaSoundDictionary`

文字列キーから SoundAsset 解決、Wwise/FMOD 的データ駆動。

**含む:**
- `NeziaSoundDictionary` (`ScriptableObject`) — `Dictionary<string, NeziaSoundAsset>`
- `NeziaEngine.Play(eventName, ...)` — 解決ショートカット
- 階層キー (`"sfx/footstep/grass"` 等) の慣習を README で示す程度
- イベント単位の volume / pitch ランダマイズ等は core の Container 拡張待ちなのでこの PR では未対応

### IP-8. ストリーミングアセット化 — `NeziaStreamingAudioClip`

`NeziaBuffer.LoadStreaming("path")` を Inspector に乗せる。

**含む:**
- `NeziaStreamingAudioClip : NeziaSoundAsset`
- StreamingAssets 相対パスフィールド + `bufferSeconds`
- `NeziaAudioSource` に D&D で BGM ストリーミング再生

### IP-9. デバッグ / 可視化ウィンドウ — `Tools > Nezia > Mixer Inspector`

EditorWindow で IP-1 のアセットや実行中の状態を可視化。

**含む:**
- 現在のバスツリー（IP-1 の `NeziaMixerAsset` がランタイムでどう実体化されたか）
- アクティブソース一覧（id / clip / volume / position）
- マスター出力 dB メーター（既存 `NeziaMasterCapture` を tap）
- Snapshot 進行バー

優先度は低いがデバッグ効率に直結する。

### IP-10. その他軽微

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
| IP-4 Asset Preview |   |   | ◎ |   | ★ |
| IP-5 Source Effect Slot |   | ◎ |   |   | ★ |
| IP-6 PlayScheduled | ○ |   |   |   | ★ |
| IP-7 Sound Dictionary | ◎ | ◎ |   |   | ★ |
| IP-8 Streaming Asset |   | ◎ |   |   |   |
| IP-9 Mixer Inspector |   |   | ◎ |   | ★ |

---

## 設計上のガードレール

1. **core の機能漏れを Integration で補わない** — core 側 PR で対応する
2. **既存ユーザーを壊さない** — `NeziaAudioSource` などの公開 API は破壊的変更を最小化
3. **Editor-only コードは `Editor/` ディレクトリに分離** — Runtime asmdef を肥らせない
4. **AudioSource / AudioMixer 互換性を保つ** — 既存 Unity プロジェクトの移行コストを下げる
5. **アセット参照は基底型で受ける** — Container ネスト等の将来拡張で Inspector 構造を変えない
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
| IP-1 Mixer Asset | 未着手 | — |
| IP-2 Effect type-safe | 未着手 | — |
| IP-3 Snapshot Asset | 未着手 | — |
| IP-4 Asset Preview | 未着手 | — |
| IP-5 Source Effect Slot | 未着手 | — |
| IP-6 PlayScheduled | 調査前 | — |
| IP-7 Sound Dictionary | 未着手 | — |
| IP-8 Streaming Asset | 未着手 | — |
| IP-9 Mixer Inspector | 未着手 | — |
| IP-10 その他軽微 | 未着手 | — |

各フェーズ着手時に PR 番号を埋める。
