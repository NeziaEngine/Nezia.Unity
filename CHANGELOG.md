# Changelog

本パッケージのすべての注目すべき変更はこのファイルに記載されます。

形式は [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/) に準拠し、
バージョニングは [Semantic Versioning](https://semver.org/lang/ja/) に従います。

## [Unreleased]

### Added

- **IP-4 PR-D ドキュメント刷新 + サンプル追加** — Clip-centric authoring の
  浸透を完了。`Samples~/ClipCentricBasics/` を新設し、Package Manager の
  Samples から取り込める最小サンプル 3 本 (Simple Playback / Volume Pitch
  Scaling / Source Override) を提供。`package.json` に `samples` 宣言を
  追加し、README にも Clip-centric の最小コード例を掲載。

- **IP-12 PR-E ドキュメント整備** — `NeziaMixerInspector` (Buses / Effects /
  Sends タブ) の使い方・Compressor sidechain (ducking) の組み立て手順・
  バリデーション・Undo / Redo の挙動を [`docs~/mixer-authoring.md`](docs~/mixer-authoring.md)
  にまとめた。README の Quickstart からも導線を張った。

- **IP-12 PR-D Send / sidechain タブ** — `NeziaMixerInspector` 上部に
  `Buses` / `Sends` のタブストリップを追加し、Send 配線を専用 UI で編集できる
  ようにした。
  - **`+ Add Send`** で Send 行を追加。各行は **source bus** / **target kind**
    (`Bus` / `CompressorSidechain`) / **target bus** のドロップダウンに加え、
    sidechain 時は対象バス上の **Compressor インデックス**ピッカーが現れる
  - 各 Send 行で **Position** (Pre / Post) と **Gain** (0〜4) を編集
  - 全編集 `Undo.RecordObject` 経由。値編集 (gain / position 等) では行を
    再生成しないため、Slider のドラッグ操作中もフォーカスが切れない
  - 不正 Send (未知バス・sidechain 先が Compressor でない等) は既存の
    `NeziaMixerAsset.Validate` がフッタに警告として表示

- **IP-12 PR-C Effect chain ペイン** — `NeziaMixerInspector` の右ペインに
  選択中バスの Effect chain 編集 UI を追加。
  - **`+ Add` ドロップダウン** で `LowPass` / `HighPass` / `Reverb` / `Compressor`
    を追加
  - 各 effect 行: ヘッダ (`▲` / `▼` で順序入れ替え / kind ラベル / `enabled`
    トグル / `×` 削除) + `Position` (Pre / Post) + kind 固有パラメータ
  - per-kind フィールド:
    - `LowPass` / `HighPass`: cutoff (20〜20000) / Q (0.1〜10)
    - `Reverb`: roomSize / damping / wet / dry / width (各 0〜1)
    - `Compressor`: thresholdDb / ratio / attackMs / releaseMs / kneeDb / makeupDb
  - 全編集 `Undo.RecordObject` 経由 (Ctrl+Z 対応)、編集後は
    `InvalidateResolvedCache` で次回 Resolve 時に effect chain を再構築させる
  - 右ペインを `ScrollView` でラップし、effect が増えても縦に伸ばせる

- **IP-12 PR-A / PR-B `NeziaMixerInspector` (Custom Inspector)** —
  `NeziaMixerAsset` を Project ビューで選択するか `Project Settings > Nezia` の
  inline Inspector から、専用 UI でバスツリーを編集できるようになった。
  当初 `EditorWindow` (`NeziaMixerWindow`) として実装したが、ツリー + プロパティ
  編集は CustomEditor として Inspector に乗せる方が Unity-idiomatic（Animator や
  Audio Mixer 等の専用ウィンドウは「特殊な可視化」が必要なケース向け）なので、
  PR-B のレビュー過程で **`CustomEditor(typeof(NeziaMixerAsset))` に転換**。
  すべて UI Toolkit 実装。
  - 2 ペイン構成 (`TwoPaneSplitView`): 左 = TreeView / 右 = 選択バスの Inspector
  - **`+ Add Bus` / `− Delete`** ボタン
    - `Add` は選択中バスと同じ階層に兄弟として追加 (Wwise / FMOD 流)。名前は
      `New Bus` 〜 `New Bus (N)` で自動的に重複回避
    - `Delete` は選択バスを削除。子バスは削除対象の親に昇格 (カスケード削除しない)
  - 右ペインに **Name** / **Gain (Slider + FloatField)** / **Muted**
    - 入力中の二重 commit を避けるため `Name` / `Gain` 数値は `isDelayed = true`
    - 空文字への rename は拒否し UI を旧名に戻す（親子参照が壊れるため）
    - リネーム時、子バスの `parent` 参照も自動的に新名へ追従
  - **Drag &amp; drop で親変更** — TreeView 上でバスをドラッグ → 別バスや Master 配下に
    ドロップ可。自分自身 / 自分の子孫へのドロップは循環防止のため reject
  - 全編集が `Undo.RecordObject` 経由 (Ctrl+Z 対応)、`Undo` / `Redo` 後は UI 自動リフレッシュ
  - 仮想 Master ルートを単一 root として表示。TreeView の id 衝突回避のため、
    Master = 1 / 実バス `i` = `i + 2` のオフセット採番
  - 空名 bus は `(unnamed)` を橙字で tree に表示し、選択して名前を直せるようにする
  - フッタに `NeziaMixerAsset.Validate()` の結果（重複名 / 未知 parent / 循環など）を
    リアルタイム表示
  - `NeziaMixerAsset` に Editor 専用 internal API を追加: `EditableBuses` /
    `EditableSends` / `InvalidateResolvedCache`
  - GTK ベースのノードグラフ案 (旧 IP-12 PR-1+2) は撤回し、Wwise / FMOD /
    Unity Audio Mixer と同じ `TreeView + UI Toolkit ListView` ハイブリッドに転換

- **IP-12 PR-0a `NeziaSettings` 導入** — URP の `GraphicsSettings` 方式に倣い、
  プロジェクト全体の Nezia 既定設定を `Project Settings > Nezia` から
  アセット参照 1 本で管理できるようにした。
  - `NeziaSettings : ScriptableObject`（Runtime）— `defaultMixer:
    NeziaMixerAsset` を持つ singleton SO。`NeziaSettings.Instance` でランタイム
    取得（Editor は `EditorBuildSettings.TryGetConfigObject` 経由、ビルドでは
    PlayerSettings の preloaded assets として自動ロード）
  - `Project Settings > Nezia` ページ（`NeziaSettingsProvider`）— Settings Asset
    の ObjectField + `Create New...` + 選択中アセットの inline Inspector。
    アセット指定時に `EditorBuildSettings.AddConfigObject` と PlayerSettings
    preloaded assets への登録を自動で行う
  - パッケージ導入時 / Editor 起動時に `Assets/Settings/NeziaSettings.asset`
    を自動生成し、`EditorBuildSettings` と PlayerSettings preloaded assets に
    登録する（`[InitializeOnLoadMethod]`）。プロジェクト内に既に
    `NeziaSettings` が存在する場合はそれを採用するため重複作成されない
  - `NeziaSoundAsset.ResolveOutputBus` / `NeziaAudioSource.Start` の解決順に
    「明示 mixer 指定 → なければ `NeziaSettings.Instance.DefaultMixer`」
    フォールバックを追加。既存挙動は破壊しない（明示 mixer がある場合の動作は
    従来どおり）

- **IP-4 Clip-centric authoring**（PR-A〜C2）— 「鳴り方は Clip が決め、
  Source は『いつ・どこで』だけ」という Wwise / FMOD 流の authoring モデルへ
  転換。詳細は [`docs~/migration/clip-centric.md`](docs~/migration/clip-centric.md)。
  - `NeziaSoundAsset` 基底（IP-4 PR-A、#33）に音響デフォルト 12 フィールドを追加:
    `Volume` / `Pitch` / `Loop` / `OutputMixerAsset`+`OutputBusName` /
    `SpatialBlend` / `MinDistance` / `MaxDistance` / `RolloffMode` /
    `AttenuationCurve` / `DopplerLevel` / `Priority`
  - `NeziaSoundAsset.SourceEffect` 宣言（`LowPass` / `HighPass` / `Reverb` /
    `Compressor`、`[SerializeReference]` 多態シリアライズ）— Clip 起点の
    エフェクトチェーンを Inspector で定義可
  - `NeziaSoundAsset.SourceSend` 宣言 — Clip 起点の Aux Send（Bus / Compressor
    sidechain）。Wwise の per-event aux send 互換で、同じ Reverb Bus を
    共有しつつ音ごとに reverb 量を独立に持たせられる。
    新規 core FFI `nezia_send_add_source_to_bus` /
    `nezia_send_add_source_to_compressor` を経由
  - `NeziaSoundAsset.ApplyAcousticsTo` / `ApplyEffectsAndSendsTo` /
    `ApplyDefaultsTo` / `ResolveOutputBus` — Spawn 直後の source に音響設定を
    一括適用するヘルパ。effect / send は core 側の自動 cleanup 規約に乗るため
    解放管理不要
  - `NeziaSend.AddSourceToBus` / `AddSourceToCompressor`（internal wrapper）—
    新規 core FFI への薄いブリッジ
  - `NeziaAudioSource.useClipDefaults`（IP-4 PR-A、既定 `false` で後方互換）—
    `true` のとき音響設定を Clip に委譲する master toggle
  - `NeziaAudioSource` の per-property override flag 群（IP-4 PR-B、#35）:
    `_overrideOutputBus` / `_overrideSpatial` / `_overrideAttenuation` /
    `_overrideDoppler` / `_overridePriority` / `_overrideLoop`。
    `useClipDefaults=true` のときに「Clip 値を使う / Source 値で override する」
    を per-property に決める
  - 各プロパティ setter の auto-flip — `source.spatialBlend = 1f` 等の代入で
    対応する override flag が暗黙に true になる。既存スクリプトが Clip-centric
    モードでも違和感なく動く
  - `NeziaAudioSource.Play()` の二系統分岐 — `useClipDefaults=true` 時は
    per-property に Source / Clip 値を選択して `ApplyAcousticsTo` に渡し、
    `ApplyEffectsAndSendsTo` で Clip 起点 effect / send を実体化する
  - **Custom Inspector** `NeziaAudioSourceEditor`（IP-4 PR-C1、#36）—
    override-aware UI。各 overridable パラメータに override トグル + 未 override
    時の `Clip default: ...` 補助ラベル。volume / pitch は scale 合成後の最終値
    （例: `× Clip 0.8 = 0.4`）をインライン表示
  - **マイグレーションコマンド**（IP-4 PR-C2、#37）:
    - `Tools/Nezia/Convert Selection to Clip-centric Mode` — 選択 Source の
      `useClipDefaults` を flip し、Source 値が Clip 値と異なる項目だけ
      override ON で残す
    - `Tools/Nezia/Revert Selection to Legacy Mode` — 互換モードへ戻す逆方向

- `NeziaMixerAsset`（`ScriptableObject`、IP-1 PR-A）— バスツリーを Inspector で
  設計するための flat list。`BusNode { name, parent, gain, muted }` を持ち、
  `parent` 空文字なら Master 直下、そうでなければ同アセット内の別バス配下に紐付く。
  - `Resolve(string busName)` — 親→子の順で `NeziaBus` を lazy 構築・キャッシュ
  - `Build()` — 全バスを一括実体化（idempotent）
  - `Validate()`（Editor 専用）— ネイティブを触らずに重複名 / 未知 parent /
    循環を検出。`OnValidate` から自動で呼ばれ Console に warning として出る
- `NeziaAudioSource.mixerAsset` / `outputBusName` — `NeziaMixerAsset` 内のバスを
  名前で指定。`Start()` での解決順は **MixerAsset 優先 → BusMap fallback**
  （既存ユーザーは無影響）
- `Nezia/Mixer Asset` メニューから生成可

- `NeziaMixerAsset.BusNode.effects`（IP-1 PR-B）— バス毎にエフェクトチェーンを
  Inspector で宣言可能に。`[SerializeReference]` ベースの多態シリアライズで
  `LowPass` / `HighPass` / `Reverb` / `Compressor` を kind 別に持つ。
  - 各 spec は `position` (Pre/Post) と `enabled` 初期値、kind 固有のパラメータ
    （例: `LowPass { cutoff, q }`、`Reverb { roomSize, damping, wet, dry, width }`）を持つ
  - `Resolve(busName)` で対応バスを実体化した直後にエフェクトを宣言順で挿入し、
    初期パラメータを反映する
  - `ResolveEffects(busName)` / `ResolveEffect(busName, index)` で実体化後の
    `NeziaEffect` ハンドルを取得（ランタイムでのパラメータ調整用）

- `NeziaSnapshotAsset`（`ScriptableObject`、IP-3 PR-A）— `NeziaMixerAsset` 上の
  バス状態を Inspector で宣言し、ランタイムで `asset.Apply(fadeSeconds)` 一発に
  適用できる Snapshot アセット。Phase 1 はバスゲイン / ミュートのみ対応
  （Send ゲイン / エフェクトパラメータは Phase 2 で追加予定）。
  - `BusOverride { busName, overrideGain, gain, overrideMuted, muted }` —
    `overrideGain` / `overrideMuted` フラグで「ゲインだけ」「ミュートだけ」を
    独立に積むことを表現
  - `Apply(fadeSeconds)` 内部で `NeziaSnapshot.Begin → Commit → Apply → Destroy`
    まで完結（永続ハンドルは保持しない）
  - `Validate()`（Editor 専用）— mixer 未設定 / 未知バス名 / 重複バス名を検出。
    `OnValidate` から自動で呼ばれ Console に warning として出る
  - `Nezia/Snapshot Asset` メニューから生成可

- `NeziaEffect` に kind 別の type-safe ラッパを追加（IP-2）:
  - `effect.AsLowPass()` → `NeziaLowPassEffect` (`Cutoff`, `Q`)
  - `effect.AsHighPass()` → `NeziaHighPassEffect` (`Cutoff`, `Q`)
  - `effect.AsReverb()` → `NeziaReverbEffect` (`RoomSize` / `Damping` /
    `Wet` / `Dry` / `Width`、すべて [0, 1])
  - `effect.AsCompressor()` → `NeziaCompressorEffect` (`ThresholdDb` /
    `Ratio` / `AttackMs` / `ReleaseMs` / `KneeDb` / `MakeupDb`)
  - kind が一致しない場合 `AsXxx()` は `InvalidOperationException` を投げる

### Removed

- `NeziaEffect.SetParam(byte param, float value)` — public API から削除。
  上記 type-safe ラッパ経由でアクセスすること（内部 `SetParamUnchecked` に統合）。

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
- `NeziaWavRecorder` (`IDisposable`) — `NeziaMasterCapture` を background
  thread で drain して Nezia マスター出力を 32-bit float WAV に書き出す
  ユーティリティ。Unity Recorder は Unity の AudioListener 経路しか録音
  できないため、Nezia の独立 DSP 出力をオフライン視聴用に保存する経路を
  提供する。`NeziaWavRecorder.Start(path)` / `Stop()` で開始・終了し、
  ヘッダの RIFF / fact / data 長は停止時にパッチする
- `NeziaEngine.Generation` — `Initialize` ごとに増えるエンジン世代カウンタ。
  ScriptableObject 側のネイティブハンドル・キャッシュが「現在のエンジン世代の
  ものか」を判定するためのフック

### Fixed

- Enter Play Mode Settings の "Reload Domain" を OFF にしている場合、
  2 回目以降の Play で `NeziaAudioClip` / `NeziaRandomContainer` /
  `NeziaBusMap` が無音 / 無効ハンドルになる問題を修正。Domain Reload オフ
  だと SO の `OnEnable` / `OnDisable` がプレイセッションをまたいで呼ばれず、
  `Application.quitting` で破棄された旧エンジンの BufferId / ContainerId /
  Bus ID をキャッシュし続けて新エンジンに対して使ってしまっていた。
  `NeziaEngine.Generation` を併せて記録し、世代不一致時にキャッシュを
  自動破棄して再ロードするように変更（ビルドではエンジンが一度しか
  初期化されないため、SO 側の世代チェックは `#if UNITY_EDITOR` で
  除外しランタイムにオーバーヘッドを与えない）

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
