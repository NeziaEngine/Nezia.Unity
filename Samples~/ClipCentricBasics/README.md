# Clip-centric Basics

Nezia の Clip-centric authoring を最小コードで体験するためのサンプル。

> 設計思想は
> [`docs~/roadmap/integration-experience.md`](../../docs~/roadmap/integration-experience.md)
> の「設計思想 — Clip-centric authoring」、Mixer まわりは
> [`docs~/mixer-authoring.md`](../../docs~/mixer-authoring.md) を参照。

## 含まれるもの

| ファイル | 役割 |
|---|---|
| `Scripts/SimpleClipPlayback.cs` | Clip 1 つを `NeziaAudioSource.useClipDefaults=true` で鳴らす最小例 |
| `Scripts/VolumePitchScaling.cs` | `source.volume` / `source.pitch` が **Clip 値への scale** として効くデモ |
| `Scripts/SourceOverrideExample.cs` | `outputBus` / `spatialBlend` を Source 側で一時 override する例 |

## 使い方

1. Unity Package Manager で `Nezia Sound Engine` を開き、`Samples` から
   `Clip-centric Basics` を `Import`
2. 取り込まれたフォルダの `Scripts/` 内のスクリプトを GameObject に
   貼り付け、Inspector で `NeziaAudioClip` を割り当て
3. Play モードで再生

## 重要なポイント

- **鳴り方は Clip 側**で決める (volume / pitch / loop / outputBus / 距離 /
  effect chain / send) — Source 側で同じ項目を再定義しない
- **Source 側は「いつ・どこで・どのインスタンスで」だけ**を扱う
  (playOnAwake / `Play()` 呼び出し / volumeScale 相当)
- 既定値の上から「このインスタンスだけ outputBus を変えたい」のような一時的な
  上書きは override flag で表現する (`SourceOverrideExample.cs` 参照)
