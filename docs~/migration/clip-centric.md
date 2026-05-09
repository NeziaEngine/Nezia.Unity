# Clip-centric authoring への移行ガイド

IP-4 で導入された Clip-centric authoring モデル（鳴り方は Clip が決め、Source は
『いつ・どこで』だけ）への移行手順をまとめる。既存プロジェクトの挙動を壊さずに
段階的に Clip-centric へ倒す運用を想定している。

> 設計思想・責務分担表は
> [`docs~/roadmap/integration-experience.md`](../roadmap/integration-experience.md)
> の「設計思想」節を参照。

---

## 用語

| 用語 | 説明 |
|---|---|
| **Clip-centric モード** | `NeziaAudioSource.useClipDefaults = true`。鳴り方は `NeziaSoundAsset` が決定 |
| **互換モード (Legacy)** | `useClipDefaults = false`。Source の値が直接最終値になる従来挙動 |
| **Override flag** | Clip-centric モード中に Source 値を Clip 値より優先するための per-property bool |
| **Auto-flip** | `source.spatialBlend = 1f` のような直接代入で対応する override flag が暗黙に true になる挙動 |

---

## 移行戦略

### 戦略 A: 既存プロジェクトを段階的に Clip-centric へ

1. **アップグレード直後** — 全 `NeziaAudioSource` は `useClipDefaults = false`
   のまま。挙動は完全に従来通り。
2. **新規追加するアセットから Clip-centric** — 新しい `NeziaAudioClip` には
   音響デフォルトを設定。新しい `NeziaAudioSource` は Inspector で
   `Use Clip Defaults` を ON。
3. **既存 Source を一括変換** — `Tools > Nezia > Convert Selection to
   Clip-centric Mode` で選択した Source を flip。挙動保存のため必要な箇所だけ
   override ON で残る。
4. **Clip 値を整える** — override ON のままになっている項目について、Source 値を
   Clip に焼き直して override OFF（または Clip Variant を作って差し替え）

### 戦略 B: 互換モードのまま据え置き

`useClipDefaults = false` のままなら従来挙動が完全保存される。Clip-centric の
恩恵（Bus 配線・Aux Send・effect chain の Clip 単位設計、Snapshot との整合）は
得られないが、移行リスクはゼロ。

---

## 移行コマンド一覧

### `Tools > Nezia > Replace AudioSources With NeziaAudioSource (in Selection)`

標準 `AudioSource` → `NeziaAudioSource` への置換（Phase 1 から提供）。
変換結果は `useClipDefaults = false` の互換モード。Clip-centric にしたい場合は
下記 Convert を続けて実行する。

### `Tools > Nezia > Convert Selection to Clip-centric Mode`

選択 GameObject 配下の `NeziaAudioSource` を Clip-centric モードへ flip
（PR-C2 で追加）。各パラメータについて Source 値と Clip 値を比較し、

- **異なる**場合 → 対応する `_overrideXxx` flag を ON にして Source 値の挙動を完全保存
- **一致する**場合 → flag を OFF にして以後 Clip が支配する状態へ倒す

| パラメータ | Match 判定 |
|---|---|
| loop | `src.loop == asset.Loop` |
| spatial 群 | spatialBlend / minDistance / maxDistance / rolloffMode が全一致 |
| attenuationCurve | リファレンス一致 |
| dopplerLevel | `Mathf.Approximately` |
| priority | int 一致 |
| outputBus | `_outputAudioMixerGroup` / `_busMap` / `_mixerAsset` / `_outputBusName` がすべて空なら Match |

Asset 未参照（`_sound` / `_clip` が両方 null）の場合は「Clip 値が無い = Source が
支配し続けるべき」と解釈し、すべての override を ON で flip する。

### `Tools > Nezia > Revert Selection to Legacy Mode`

`useClipDefaults` を `false` に戻す逆方向コマンド。override flag は保持されるので、
再 flip 時には以前の override 状態が復元される。

---

## Inspector の見え方

### 互換モード (`useClipDefaults = false`)

従来の `AudioSource` 互換レイアウト。Clip 値の参照表示は無い。

### Clip-centric モード (`useClipDefaults = true`)

各 overridable プロパティに **override トグル** が付く:

```
Spatial
  □ Override
    Clip default: 3D (1m–500m, InverseDistance)
```

- override OFF: フィールドは disabled、`Clip default: ...` 補助ラベルが表示される
- override ON: フィールドが有効化、Source 値が Clip 値より優先される

`Volume` / `Pitch` は常に Clip 値への scale として扱われ、合成後の最終値が
インライン表示される（例: `× Clip 0.8 = 0.4`）。

---

## コードからの利用

### 既存スクリプトの互換性

`source.volume = 0.5f` / `source.pitch = 1.2f` 等の代入は Clip-centric モードでも
動作する:

- `volume` / `pitch` は Clip 値への scale
- それ以外（`spatialBlend` / `loop` / `outputBus` / etc.）は **代入時に対応する
  override flag が auto-flip で ON になる**

つまり既存のランタイム制御コードは Clip-centric モードでも違和感なく動く。

### 明示的に Clip 値へ戻す

override flag を OFF に戻して Clip 値を採用させたい場合は SerializedProperty
経由（Editor）または将来の専用 API で操作する（ランタイム API は今後の PR で
検討）。

---

## トラブルシューティング

### Q: Convert したら音が変わってしまった

A: Asset 未参照（`_sound`/`_clip` が空）の Source は全 override ON で flip される
ため挙動は変わらないはず。挙動が変わった場合は以下を確認:

1. Clip 側の値と Source 側の値の差が `Mathf.Approximately` 範囲外で「一致と判定
   された」ケースがある（小さな浮動小数差はマッチ扱い）
2. Bus 配線が `_outputAudioMixerGroup` 等の他のフィールドに依存していたが Source
   に何も設定されていなかったため override OFF になった

`Revert Selection to Legacy Mode` で戻して、Source 側を整えてから再 Convert する
のが安全。

### Q: `useClipDefaults = true` にしたが Clip Inspector に音響デフォルトが見えない

A: PR-A 以前の `NeziaAudioClip` ではフィールドが無かった。Unity を再起動するか
Reimport すると最新フィールドが Inspector に出る。

### Q: Random Container の child Clip 設定はどうなる？

A: Wwise / FMOD と同じで、Random Container 自身の音響設定が支配し、選ばれた
child Clip の音響設定は無視される。「どの音が鳴るか」だけが child のランダム選択
に委ねられる。

---

## 関連 PR

- [#33] IP-4 PR-A — `NeziaSoundAsset` Clip-centric acoustic defaults
- [#35] IP-4 PR-B — Per-property override flags
- [#36] IP-4 PR-C1 — Override-aware Custom Inspector
- [#37] IP-4 PR-C2 — Migration command
