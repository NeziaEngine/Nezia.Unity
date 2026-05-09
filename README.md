# Nezia Sound Engine (jp.nezia.unity)

Unity 統合層パッケージ。`AudioSource` 互換 API を保ちつつ、Wwise / FMOD のような
Clip-centric authoring（鳴り方は Clip が決め、Source は『いつ・どこで』だけ）を
Inspector とプレハブだけで成立させることを目指す。

## インストール

`Packages/manifest.json` に以下を追加:

```json
{
  "dependencies": {
    "jp.nezia.unity": "file:../Packages/jp.nezia.unity"
  }
}
```

または Unity Package Manager の "Add package from disk..." から `package.json` を指定。

## 構成

- `Runtime/` — ランタイムコード (`Nezia.Unity` 名前空間)
- `Editor/` — エディタ拡張 (`Nezia.Unity.Editor` 名前空間)
- `Tests/Runtime/`, `Tests/Editor/` — テスト
- `docs~/` — 設計ドキュメント・ロードマップ・移行ガイド

## 思想 — Clip-centric authoring

| レイヤ | 持つもの | 例 |
|---|---|---|
| **Clip / SoundAsset** (鳴り方) | volume / pitch / loop / outputBus / spatial / attenuationCurve / doppler / priority / effect chain / send routing | 「足音は 3D・SFX Bus・priority 192」 |
| **Source** (トリガとインスタンス) | 再生対象アセット参照 / playOnAwake / mute / volumeScale / pitchScale / position (transform 由来) | 「このオブジェクトは playOnAwake、音量を 0.5 倍」 |

`NeziaAudioSource` は `AudioSource` のドロップイン互換のまま使えますが、
**`useClipDefaults=true`** に切り替えると上記モデルが有効になります:

- `source.volume = 0.5f` は **Clip 基準音量への乗算 (scale)** として効く
- 個別パラメータは override トグルで Source 値を強制可能（`source.spatialBlend = 1f` のような直接代入は暗黙に override flag が立つ）
- 何も override しなければすべて Clip 値が支配する

### クイックスタート

1. **Mixer Asset を作る**: `Assets/Create/Nezia/Mixer Asset` でバスツリーを Inspector で設計
2. **Clip を取り込む**: `.wav` / `.ogg` / `.flac` / `.mp3` を Project に D&D（自動で `NeziaAudioClip` になる）
3. **Clip の音響設定を編集**: Inspector で volume / pitch / outputBus / spatial / effect chain / aux send を設定
4. **GameObject に `NeziaAudioSource` を追加**して Clip を D&D
5. **`Use Clip Defaults` を ON** に → Source は再生トリガに徹し、鳴り方は Clip が決定

### 既存プロジェクトの移行

旧来の `AudioSource` ベース、または PR-A 以前の `NeziaAudioSource`
（`useClipDefaults=false` の互換モード）から Clip-centric へ:

- 標準 `AudioSource` → `NeziaAudioSource`:
  `Tools > Nezia > Replace AudioSources With NeziaAudioSource (in Selection)`
- 互換モード → Clip-centric モード:
  `Tools > Nezia > Convert Selection to Clip-centric Mode`
  - Source 値が Clip 値と異なるパラメータは override flag が ON で残る（挙動完全保存）
  - 一致するパラメータは override OFF で Clip に委譲

詳細は [`docs~/migration/clip-centric.md`](docs~/migration/clip-centric.md)。

## 関連ドキュメント

- ロードマップ: [`docs~/roadmap/integration-experience.md`](docs~/roadmap/integration-experience.md)
- 移行ガイド: [`docs~/migration/clip-centric.md`](docs~/migration/clip-centric.md)
- 変更履歴: [`CHANGELOG.md`](CHANGELOG.md)
