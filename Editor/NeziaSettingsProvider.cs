using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.GraphToolkit.Editor;
using UnityEditor;
using UnityEngine;
using Nezia.Unity.Editor.Mixer;

namespace Nezia.Unity.Editor
{
    /// <summary>
    /// <c>Project Settings &gt; Nezia</c> ページ。
    ///
    /// <para>
    /// URP の Graphics ページと同じ作りで、上段に <see cref="NeziaSettings"/> アセットの
    /// ObjectField + <c>Create New</c> ボタンを置き、選択中アセットを inline で
    /// Inspector 描画する。アセット参照は <c>EditorBuildSettings.AddConfigObject</c> で
    /// <c>ProjectSettings/EditorBuildSettings.asset</c> に GUID として保持し、ビルド時用に
    /// PlayerSettings の preloaded assets にも自動登録する。
    /// </para>
    /// </summary>
    internal static class NeziaSettingsProvider
    {
        private const string SettingsPath = "Project/Nezia";
        private const string DefaultAssetDir = "Assets/Settings";
        private const string DefaultAssetPath = DefaultAssetDir + "/NeziaSettings.asset";
        private const string DefaultMixerPath = DefaultAssetDir + "/DefaultMixer." + NeziaMixerGraph.AssetExtension;
        private static readonly string[] SettingsKeywords = new[] { "Nezia", "Audio", "Mixer", "Bus" };

        private static UnityEditor.Editor s_inlineEditor;

        /// <summary>
        /// Editor 起動 / アセンブリ再ロード時に Settings アセットの存在を担保する。
        /// 既に登録済みなら何もしない。未登録なら <see cref="DefaultAssetPath"/> に新規作成する。
        /// </summary>
        [InitializeOnLoadMethod]
        private static void EnsureSettingsOnLoad()
        {
            // AssetDatabase は domain reload 直後だと未準備なことがあるので 1 フレーム遅延させる。
            EditorApplication.delayCall += () =>
            {
                var settings = GetOrCreate();
                if (settings != null) EnsureDefaultMixer(settings);
            };
        }

        /// <summary>
        /// 登録済み <see cref="NeziaSettings"/> を返す。未登録なら新規作成して登録する。
        /// </summary>
        internal static NeziaSettings GetOrCreate()
        {
            if (EditorBuildSettings.TryGetConfigObject(NeziaSettings.ConfigName, out NeziaSettings existing)
                && existing != null)
                return existing;

            // 既にプロジェクト内のどこかに NeziaSettings が存在すれば、それを採用する
            // （重複作成を避ける。複数あったら最初の 1 つを採用）。
            var guids = AssetDatabase.FindAssets("t:NeziaSettings");
            if (guids != null && guids.Length > 0)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                var found = AssetDatabase.LoadAssetAtPath<NeziaSettings>(path);
                if (found != null)
                {
                    AssignSettings(found);
                    return found;
                }
            }

            // 新規作成。Assets/Settings/ が無ければ作る。
            if (!AssetDatabase.IsValidFolder(DefaultAssetDir))
            {
                if (!AssetDatabase.IsValidFolder("Assets"))
                {
                    Debug.LogError("[Nezia] Cannot create settings: Assets folder is missing.");
                    return null;
                }
                AssetDatabase.CreateFolder("Assets", "Settings");
            }

            var asset = ScriptableObject.CreateInstance<NeziaSettings>();
            var assetPath = AssetDatabase.GenerateUniqueAssetPath(DefaultAssetPath);
            AssetDatabase.CreateAsset(asset, assetPath);
            AssetDatabase.SaveAssets();
            AssignSettings(asset);
            Debug.Log($"[Nezia] Created default settings at {assetPath}", asset);
            return asset;
        }

        /// <summary>
        /// <see cref="NeziaSettings"/> の <c>defaultMixer</c> が未設定の場合に
        /// <c>Assets/Settings/DefaultMixer.neziamixer</c> を新規生成し、自動アサインする。
        /// 既に設定済みの場合は何もしない。
        /// </summary>
        private static void EnsureDefaultMixer(NeziaSettings settings)
        {
            if (settings.DefaultMixer != null) return;

            // 既に DefaultMixer.neziamixer がプロジェクトに存在すればそれを採用する。
            var existing = AssetDatabase.LoadAssetAtPath<NeziaMixerAsset>(DefaultMixerPath);
            if (existing == null)
            {
                if (!AssetDatabase.IsValidFolder(DefaultAssetDir))
                    AssetDatabase.CreateFolder("Assets", "Settings");
                // GraphDatabase.CreateGraph が新しい .neziamixer を作成し、
                // Importer (NeziaMixerImporter) が main asset として NeziaMixerAsset を生成する。
                GraphDatabase.CreateGraph<NeziaMixerGraph>(DefaultMixerPath);
                AssetDatabase.ImportAsset(DefaultMixerPath, ImportAssetOptions.ForceSynchronousImport);
                existing = AssetDatabase.LoadAssetAtPath<NeziaMixerAsset>(DefaultMixerPath);
                if (existing != null)
                    Debug.Log($"[Nezia] Created default mixer at {DefaultMixerPath}", existing);
            }

            if (existing == null) return;

            var so = new SerializedObject(settings);
            so.FindProperty("_defaultMixer").objectReferenceValue = existing;
            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssetIfDirty(settings);
            NeziaSettings.InvalidateCache();
        }

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider(SettingsPath, SettingsScope.Project)
            {
                label = "Nezia",
                keywords = SettingsKeywords,
                guiHandler = _ => DrawGUI(),
                deactivateHandler = () =>
                {
                    if (s_inlineEditor != null)
                    {
                        Object.DestroyImmediate(s_inlineEditor);
                        s_inlineEditor = null;
                    }
                },
            };
        }

        private static void DrawGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Nezia Settings", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "プロジェクト全体の Nezia 設定アセットを指定します。" +
                "ここで指定された Mixer は NeziaSoundAsset / NeziaAudioSource が " +
                "明示的な Mixer を持たないときの既定として使われます。",
                MessageType.None);
            EditorGUILayout.Space(4);

            // 通常はここで自動生成済みのアセットが手に入る。ユーザーが削除した場合のリカバリも兼ねる。
            var current = GetOrCreate();

            using (var change = new EditorGUI.ChangeCheckScope())
            {
                var next = (NeziaSettings)EditorGUILayout.ObjectField(
                    new GUIContent("Settings Asset"), current, typeof(NeziaSettings), allowSceneObjects: false);

                if (change.changed) AssignSettings(next);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Create New...", GUILayout.Width(120)))
                    CreateNewSettingsAsset();
            }

            EditorGUILayout.Space(12);

            if (current == null)
            {
                EditorGUILayout.HelpBox(
                    "Settings Asset の自動生成に失敗しました。`Create New...` で手動生成してください。",
                    MessageType.Warning);
                ResetInlineEditor();
                return;
            }

            EditorGUILayout.LabelField("Asset Inspector", EditorStyles.boldLabel);
            EnsureInlineEditor(current);
            using (new EditorGUI.IndentLevelScope())
            {
                s_inlineEditor.OnInspectorGUI();
            }
        }

        // ─── 操作 ────────────────────────────────────────────────

        private static void AssignSettings(NeziaSettings asset)
        {
            if (asset == null)
            {
                EditorBuildSettings.RemoveConfigObject(NeziaSettings.ConfigName);
                RemoveFromPreloadedAssets<NeziaSettings>();
            }
            else
            {
                EditorBuildSettings.AddConfigObject(NeziaSettings.ConfigName, asset, overwrite: true);
                EnsurePreloadedAsset(asset);
            }
            NeziaSettings.InvalidateCache();
            ResetInlineEditor();
        }

        private static void CreateNewSettingsAsset()
        {
            var path = EditorUtility.SaveFilePanelInProject(
                "Create Nezia Settings",
                "NeziaSettings",
                "asset",
                "プロジェクト直下の Settings 用に保存先を選んでください。");
            if (string.IsNullOrEmpty(path)) return;

            var asset = ScriptableObject.CreateInstance<NeziaSettings>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            AssignSettings(asset);
            EditorGUIUtility.PingObject(asset);
        }

        // ─── inline editor ───────────────────────────────────────

        private static void EnsureInlineEditor(NeziaSettings asset)
        {
            if (s_inlineEditor != null && s_inlineEditor.target == asset) return;
            ResetInlineEditor();
            s_inlineEditor = UnityEditor.Editor.CreateEditor(asset);
        }

        private static void ResetInlineEditor()
        {
            if (s_inlineEditor != null)
            {
                Object.DestroyImmediate(s_inlineEditor);
                s_inlineEditor = null;
            }
        }

        // ─── preloaded assets 管理 ────────────────────────────────

        private static void EnsurePreloadedAsset(Object asset)
        {
            var list = PlayerSettings.GetPreloadedAssets()?.ToList() ?? new List<Object>();
            // 同型の古いエントリは入れ替える（複数の NeziaSettings が紛れ込まないように）。
            list.RemoveAll(o => o == null || o is NeziaSettings);
            list.Add(asset);
            PlayerSettings.SetPreloadedAssets(list.ToArray());
        }

        private static void RemoveFromPreloadedAssets<T>() where T : Object
        {
            var list = PlayerSettings.GetPreloadedAssets();
            if (list == null || list.Length == 0) return;
            var filtered = list.Where(o => o != null && !(o is T)).ToArray();
            if (filtered.Length != list.Length)
                PlayerSettings.SetPreloadedAssets(filtered);
        }
    }
}
