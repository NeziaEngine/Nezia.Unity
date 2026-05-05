# Changelog

本パッケージのすべての注目すべき変更はこのファイルに記載されます。

形式は [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/) に準拠し、
バージョニングは [Semantic Versioning](https://semver.org/lang/ja/) に従います。

## [Unreleased]

### Added

- nezia-core の新規 FFI に追従:
  - `NeziaEngine.SoundSpeed` — `nezia_set_sound_speed`（媒質中の音速、Unity の
    `AudioSettings.speedOfSound` 互換）
  - `NeziaEngine.SetListenerFocus` — SP-06 リスナーフォーカス
    （`nezia_listener_set_focus`）
  - `NeziaAudioSource.dopplerLevel` — `AudioSource.dopplerLevel` 互換
    （`nezia_source_set_doppler_level`、SP-10）
  - `NeziaAudioSource.priority` — `AudioSource.priority` 互換
    （`nezia_source_set_priority`、Phase 2-2 Voice Virtualization 用）。
    ネイティブ層は Wwise / CRI ADX2 互換 (高い値=高優先) に切り替わったため、
    統合層で `255 - unity_priority` を写像して FFI に渡す。
    公開 API は Unity 標準 (0=最高) のまま維持
  - `NeziaAudioListener` がリスナー速度を自動算出して
    `nezia_listener_set_velocity` に publish
  - `NeziaAudioSource` がソース速度を自動算出して
    `nezia_source_batch_set_velocities` に毎フレームバッチ送信
- 再生成された `NeziaNative.g.cs`（上記 FFI 追加分・`NeziaSourceVelocityUpdate`
  構造体を含む）
- nezia-core の追加 FFI（Effects / Sends / Snapshots / Containers / Streaming /
  Master Capture / DSP Time）に追従:
  - `NeziaEffect` / `NeziaEffectKind` (LowPass / HighPass / Reverb / Compressor) /
    `NeziaEffectPosition` — `NeziaBus.AddEffect` および
    `NeziaAudioSource.AddEffect` で挿入、`SetParam` / `Enabled` / `Remove`
  - `NeziaSend` / `NeziaSendPosition` — `AddBusToBus` /
    `AddBusToCompressor`（sidechain 駆動を自動 on）、`Gain` / `Position` / `Remove`
  - `NeziaBus.BindCompressorSidechain` — Compressor の sidechain 駆動を後から制御
  - `NeziaSnapshot` + `NeziaSnapshot.Builder` — バスゲイン / ミュート / Send ゲイン /
    エフェクトパラメータをまとめて fade 付きで適用（AudioMixer Snapshot 相当）
  - `NeziaSoundAsset`（抽象 `ScriptableObject` 基底）と
    `NeziaRandomContainer`（`NeziaSoundAsset` 派生・`[CreateAssetMenu]`）—
    `docs/design/core/container.md` の Unity 統合設計に従い、`NeziaAudioClip`
    （単発再生）と `NeziaRandomContainer`（ランダム選択）を統一基底で扱う。
    `NeziaAudioSource.sound` フィールドが基底型を受け、Inspector D&D で
    どちらでも同じインターフェースで鳴らせる。子は `NeziaSoundAsset[]`
    基底型配列で、将来の Container ネスト（`ContainerChild::Container`）に
    Inspector 構造を変えずに対応できる。`NeziaAudioClip` は
    `NeziaSoundAsset` を継承するように変更（既存利用は無影響）
  - `NeziaAttenuationCurve` + `NeziaAudioSource.SetAttenuationCurve` —
    カスタム距離減衰カーブ（`AnimationCurve` 相当）
  - `NeziaAttenuationCurveAsset` (`ScriptableObject` / `CreateAssetMenu`) —
    Inspector の `AnimationCurve` エディタで編集できるカーブアセット。
    `NeziaAudioSource._attenuationCurve` で割り当てると `Play()` 時に
    自動でサンプリングしてネイティブ確保、`Stop` / 自然終了 / `OnDisable`
    で破棄する
  - `NeziaBuffer.LoadStreaming` / `SeekStreaming` / `SetStreamingLoop` —
    巨大 BGM をフルデコードせずに再生
  - `NeziaBuffer.LoadFromFile` — ファイルパスからのフルロード
    （`streamingAssetsPath` 等用）
  - `NeziaBuffer.OpenReader` + `NeziaBufferReader` (`IDisposable`) —
    任意スレッド可・lock-free の PCM 読み出しリーダー
  - `NeziaAudioClip.AsAudioClip` を `NeziaBufferReader` ベースに差し替え。
    Unity の `pcmReadCallback` 経路で実音が出るようになり、Timeline /
    AudioTrack / Animation Event 等 `AudioClip` を要求するサードパーティ
    アセットとの連携が機能する（旧実装は無音返しの暫定版）
  - `NeziaRandomContainer.PlayFireAndForget` — 制御ハンドル不要な軽量再生
  - `NeziaMasterCapture` (`IDisposable`) と
    `NeziaEngine.EnableMasterCapture` / `DisableMasterCapture` — マスター出力 tap
  - `NeziaEngine.OutputSampleRate` / `OutputChannels` /
    `DspTime` / `DspTimeSamples` — `AudioSettings.dspTime` 相当

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
