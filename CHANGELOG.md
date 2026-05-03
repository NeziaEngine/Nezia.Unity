# Changelog

本パッケージのすべての注目すべき変更はこのファイルに記載されます。

形式は [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/) に準拠し、
バージョニングは [Semantic Versioning](https://semver.org/lang/ja/) に従います。

## [0.1.0] - 2026-05-02

### Added

- UPM パッケージ初期構成 (Runtime / Editor / Tests asmdef)
- 高レベル Runtime API (CONCEPT.md フェーズ 1):
  - `NeziaEngine` — エンジンライフサイクル静的ファサード（自動初期化・ポンプ）
  - `NeziaBuffer` / `NeziaBus` — 安全なハンドルラッパ
  - `NeziaBuffer.LoadFromBytes` / `LoadFromAudioClip` — 推奨ワークフロー入口
  - `NeziaAudioClip` — `ScriptableObject` アセット型（レベル 2）
  - `NeziaAudioSource` — `AudioSource` 互換 `MonoBehaviour`（レベル 1）
  - `NeziaAudioListener` — `AudioListener` 自動追従ブリッジ（レベル 4）
  - `NeziaException` / `NeziaErrorCode` — `NeziaResult` の例外マッピング
  - `NeziaAudioClip.AsAudioClip()` — Timeline / 既存 API への `AudioClip` プロキシ橋渡し
    （PCM 供給は FFI 拡張までスタブ）
  - `NeziaBusMap` — `AudioMixerGroup` → `NeziaBus` の `ScriptableObject` マッピング
  - `NeziaAudioSource.outputAudioMixerGroup` / `busMap` — 互換プロパティ
- 高レベル Editor API:
  - `NeziaAudioImporter` — `.wav .ogg .flac .mp3` を `NeziaAudioClip` として
    取り込む `ScriptedImporter`（CONCEPT.md レベル 2）
  - `Tools > Nezia` メニュー — 選択配下の `AudioSource` ↔ `NeziaAudioSource` 一括変換
    と `NeziaAudioListener` 自動付与（CONCEPT.md レベル 3 / 4）

### Changed

- `NeziaAudioSource.Play()` を常に `nezia_source_play_with_handle` 経由に変更し、`Stop` / `Pause` /
  `UnPause` / `time` シーク・volume / pitch / mute の動的反映に対応。
  fire-and-forget パスは <c>PlayOneShot</c> / <c>PlayClipAtPoint</c> 専用とした。
- `PlayDelayed` は API オミット（利用頻度低・呼び出し側の <c>Invoke</c> で代替可能）。
