# Mixer authoring (NeziaMixerInspector)

`NeziaMixerAsset` のバスツリー / Effect chain / Send 配線を Inspector で
編集するためのガイド。Wwise / FMOD / Unity Audio Mixer ユーザーが既視感の
ある UX で Hierarchy と Send を組めるようにすることを目標にしている。

> 設計思想・ロードマップ全体は
> [`docs~/roadmap/integration-experience.md`](roadmap/integration-experience.md)
> の IP-12 節を参照。

---

## 起動方法

`NeziaMixerInspector` は `NeziaMixerAsset` の Custom Inspector として
実装されている。次のいずれかで開ける:

- **Project ビュー**で `.asset` (`NeziaMixerAsset`) を選択 → Inspector に編集 UI
- **`Project Settings > Nezia`** を開く → `Settings Asset` で参照されている
  Mixer がそのまま inline Inspector で編集できる
- 専用ウィンドウは存在しない (Animator / Audio Mixer のような独立 EditorWindow
  は採用していない)。Inspector を 2 枚立てて lock する標準 Unity 作法で
  「ツリー側を固定しつつバスを切替えながら別 Inspector で編集」も可能

---

## Buses タブ

バスツリー全体と、選択したバスの個別プロパティを編集する。

### バスの追加・削除・親変更

| 操作 | 結果 |
|---|---|
| **`+ Add Bus`** | 選択中バスと**同じ階層**に兄弟として追加。未選択なら master 直下。名前は `New Bus`〜`New Bus (N)` で自動的に重複回避 |
| **`− Delete`** | 選択バスを削除。**子バスは削除対象の親に昇格**する (カスケード削除しない) |
| **drag & drop** | 行をドラッグして親変更。自身 / 子孫へのドロップは循環防止で reject |

### 選択バスの Inspector ペイン (右ペイン)

- **Name** — リネームすると、子バスの `parent` 参照も自動追従。空文字 rename
  は拒否されて元の値に戻る
- **Gain** — Slider (0〜4) + 数値フィールド (`isDelayed=true` で入力中の
  二重 commit を回避)
- **Muted** — toggle

### Effects セクション

選択バスの effect chain を編集する。

| 要素 | 役割 |
|---|---|
| **`+ Add` メニュー** | `LowPass` / `HighPass` / `Reverb` / `Compressor` を追加 |
| **`▲` / `▼`** | 順序入れ替え (List 順序 = signal 上の処理順) |
| **kind ラベル** | 種別 (削除・並べ替え時に視認用) |
| **Enabled トグル** | ランタイムで `bypass` 相当 |
| **`×`** | 削除 |
| **Position** | `Pre` (フェーダー前) / `Post` (フェーダー後) |
| **kind 固有** | LowPass/HighPass: cutoff・Q / Reverb: roomSize・damping・wet・dry・width / Compressor: thresholdDb・ratio・attackMs・releaseMs・kneeDb・makeupDb |

`[SerializeReference]` で多態シリアライズしているため、List の順序 = effect
の挿入順がそのまま保存される。編集後は内部キャッシュを破棄して、次回
`Resolve` 時に effect chain を再構築する。

---

## Sends タブ

バス→バスの Send (ducking 用 sidechain 含む) を編集する。

### 通常の Send (バス→バス) を組む

1. `+ Add Send` で空の Send 行を追加
2. **source bus** に送り元を選択 (例: `SE`)
3. **target kind** = `Bus`、**target bus** に送り先を選択 (例: `Reverb_Bus`)
4. **Position** = `Pre` / `Post`、**Gain** = 0〜4 を調整

### Compressor sidechain (ducking) を組む

BGM を SE で duck する典型ケース:

1. **Compressor を立てる** — Buses タブで `BGM` を選択 → Effects で
   `+ Add` → `Compressor`。閾値 / ratio / attack / release などは通常の
   Compressor として調整
2. **Sidechain Send を繋ぐ** — Sends タブで `+ Add Send`:
   - **source bus** = `SE` (ducking のキー入力)
   - **target kind** = `CompressorSidechain`
   - **target bus** = `BGM` (Compressor が乗っているバス)
   - **Sidechain Target** = そのバス上の Compressor (例: `[0] Compressor`)
   - **Position** = ふつう `Post` / **Gain** = 1.0 が出発点
3. これで SE が鳴ると BGM の Compressor が SE を sidechain として参照し、
   BGM 側にだけ duck がかかる

`Sidechain Target` ピッカーには対象バス上の **Compressor のみ**が列挙
される。Compressor が無いバスを target にすると警告文が出るので、
Buses タブで先に Compressor を立てること。

### 値編集中の挙動

- **gain / position / source / target bus** の編集は行を再生成しないので、
  Slider のドラッグ操作で連続編集できる
- **target kind** 切替時のみ行が再構築される (sidechain ピッカーの
  表示/非表示が変わるため)

---

## バリデーション

Inspector 下部に常駐するフッタが `NeziaMixerAsset.Validate` の結果を
表示する。Save 等は不要 (再描画のたびに自動再計算)。

検出される代表例:

- バス名重複・空名
- 未知の `parent` 参照
- 親子の循環参照
- Send の `source` / `targetBus` が未知
- `CompressorSidechain` の target が Compressor でない
- `source == target` の自己ルーティング

エラーが残っていてもアセット保存は通る (ランタイムで該当 Send が
`NeziaSend.Invalid` になるだけ)。フッタは編集中の指針として使う。

---

## Undo / Redo

すべての編集は `Undo.RecordObject(NeziaMixerAsset, ...)` 経由で記録される。
Ctrl+Z / Cmd+Z で一段ずつ戻せる:

- バスの追加 / 削除 / 親変更 / リネーム / gain・muted 編集
- Effect の追加 / 削除 / 並べ替え / 各パラメータ
- Send の追加 / 削除 / 各値編集

Undo / Redo 後は内部キャッシュ (`_resolved*`) を破棄し、TreeView と
右ペインを `OnUndoRedo` で同期再構築する。

---

## 関連ドキュメント

- ロードマップ: [`roadmap/integration-experience.md`](roadmap/integration-experience.md) の IP-12
- 変更履歴: [`../CHANGELOG.md`](../CHANGELOG.md)
