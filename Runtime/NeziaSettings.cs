using UnityEngine;

namespace Nezia.Unity
{
    /// <summary>
    /// プロジェクト全体の Nezia 設定を保持する singleton ScriptableObject。
    ///
    /// <para>
    /// URP の <c>GraphicsSettings.defaultRenderPipeline</c> と同じ構造で、
    /// <c>Project Settings &gt; Nezia</c> から本アセットを参照させ、ランタイムは
    /// <see cref="Instance"/> 経由で取得する。実体は通常 <c>Assets/</c> 配下の
    /// <c>.asset</c> ファイルとしてバージョン管理に乗る。
    /// </para>
    ///
    /// <para>
    /// 参照保持は Editor では <c>EditorBuildSettings.AddConfigObject(<see cref="ConfigName"/>, ...)</c>
    /// で <c>ProjectSettings/EditorBuildSettings.asset</c> に GUID を 1 本書く。
    /// ビルド時は PlayerSettings の preloaded assets に登録するため、
    /// ランタイムでは <c>Resources.FindObjectsOfTypeAll</c> で発見できる。
    /// </para>
    /// </summary>
    public sealed class NeziaSettings : ScriptableObject
    {
        /// <summary>
        /// <c>EditorBuildSettings.AddConfigObject</c> で使用する識別子。
        /// 値変更は既存プロジェクトの設定参照を切るため厳禁。
        /// </summary>
        public const string ConfigName = "jp.nezia.unity.settings";

        [SerializeField, Tooltip(
            "プロジェクト全体のデフォルト Mixer。NeziaSoundAsset / NeziaAudioSource が " +
            "明示 mixer を指していないとき、ここに設定された Mixer 内のバス名で解決される。")]
        private NeziaMixerAsset _defaultMixer;

        /// <summary>プロジェクト既定の <see cref="NeziaMixerAsset"/>。未設定なら <c>null</c>。</summary>
        public NeziaMixerAsset DefaultMixer => _defaultMixer;

        // ─── Engine Config ────────────────────────────────────────

        [SerializeField, Tooltip(
            "ON にするとエンジン初期化時のキャパシティ (最大ソース数 / 最大物理ボイス数) を " +
            "ここで上書きする。OFF なら nezia-core ビルドの既定値が使われる。")]
        private bool _overrideEngineConfig = false;

        [SerializeField, Min(1), Tooltip(
            "論理ソース上限。同時に存在しうる Source の総数 (仮想化されたものを含む)。" +
            "max_physical_voices 以上である必要がある。")]
        private uint _maxSources = 1024;

        [SerializeField, Min(1), Tooltip(
            "物理ボイス数上限。実 DSP / ミキシングを行うボイス数。" +
            "max_sources 以下である必要がある。発音数 (同時に音が鳴る数) の上限はこの値で決まる。")]
        private uint _maxPhysicalVoices = 256;

        /// <summary>
        /// エンジン初期化時のキャパシティをこの設定で上書きするかどうか。
        /// <c>false</c> なら nezia-core ビルドの既定値が使われる。
        /// </summary>
        public bool OverrideEngineConfig => _overrideEngineConfig;

        /// <summary>論理ソース上限。<see cref="OverrideEngineConfig"/> が <c>true</c> のときのみ有効。</summary>
        public uint MaxSources => _maxSources;

        /// <summary>物理ボイス上限 (= 同時発音数の上限)。<see cref="OverrideEngineConfig"/> が <c>true</c> のときのみ有効。</summary>
        public uint MaxPhysicalVoices => _maxPhysicalVoices;

        private void OnValidate()
        {
            if (_maxSources < 1) _maxSources = 1;
            if (_maxPhysicalVoices < 1) _maxPhysicalVoices = 1;
            if (_maxPhysicalVoices > _maxSources) _maxPhysicalVoices = _maxSources;
        }

        // ─── Singleton ────────────────────────────────────────────

        private static NeziaSettings s_instance;

        /// <summary>
        /// 現在登録されている <see cref="NeziaSettings"/> アセット。未登録なら <c>null</c>。
        ///
        /// <para>
        /// Editor では <c>EditorBuildSettings</c> から GUID 解決。ランタイムでは
        /// PlayerSettings の preloaded assets としてロード済みの SO を拾う
        /// （<see cref="Resources.FindObjectsOfTypeAll"/>）。
        /// </para>
        /// </summary>
        public static NeziaSettings Instance
        {
            get
            {
                // Unity の overloaded == で破棄済みオブジェクトは null 扱いになるため、これで stale も検出できる。
                if (s_instance != null) return s_instance;

#if UNITY_EDITOR
                UnityEditor.EditorBuildSettings.TryGetConfigObject(ConfigName, out s_instance);
#else
                var all = Resources.FindObjectsOfTypeAll<NeziaSettings>();
                if (all != null && all.Length > 0) s_instance = all[0];
#endif
                return s_instance;
            }
        }

        /// <summary>
        /// <see cref="Instance"/> の内部キャッシュをクリアする。Project Settings 上で
        /// アセットを差し替えた直後など、次回 <see cref="Instance"/> 取得で再解決させたいときに使う。
        /// </summary>
        internal static void InvalidateCache() => s_instance = null;
    }
}
