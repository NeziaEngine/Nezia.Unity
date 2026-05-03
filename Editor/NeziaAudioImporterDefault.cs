using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Nezia.Unity.Editor
{
    /// <summary>
    /// <c>.wav .ogg .flac .mp3</c> のデフォルト importer を <see cref="NeziaAudioImporter"/> に
    /// 切り替える <c>AssetPostprocessor</c>。<c>ScriptedImporter.overrideExts</c> は
    /// 「Reimport with」候補としての登録に留まり、Unity ネイティブ <c>AudioImporter</c> が
    /// デフォルトのままになるため、<see cref="AssetDatabase.SetImporterOverride{T}"/> で
    /// per-asset に強制上書きする。
    ///
    /// <para>
    /// 採用プロジェクトで自動上書きを止めたい場合は Scripting Define Symbols に
    /// <c>NEZIA_DISABLE_DEFAULT_IMPORTER_OVERRIDE</c> を追加する。
    /// 既に override 済みのアセットは <c>Tools/Nezia/Clear …</c> メニューで戻せる。
    /// </para>
    /// </summary>
    public sealed class NeziaAudioImporterDefault : AssetPostprocessor
    {
        private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".wav", ".ogg", ".flac", ".mp3",
        };

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
#if NEZIA_DISABLE_DEFAULT_IMPORTER_OVERRIDE
            return;
#else
            ApplyOverride(importedAssets);
            ApplyOverride(movedAssets);
#endif
        }

        private static void ApplyOverride(string[] paths)
        {
            if (paths == null) return;
            foreach (var path in paths)
            {
                if (!IsAudioAsset(path)) continue;
                var current = AssetImporter.GetAtPath(path);
                if (current is NeziaAudioImporter) continue;
                AssetDatabase.SetImporterOverride<NeziaAudioImporter>(path);
            }
        }

        private static bool IsAudioAsset(string path)
            => !string.IsNullOrEmpty(path) && AudioExtensions.Contains(Path.GetExtension(path));

        // ─── 既存アセットへの一括適用 ─────────────────────────────

        [MenuItem("Tools/Nezia/Apply NeziaAudioImporter To All Audio Assets")]
        public static void ApplyToAll()
        {
            var guids = AssetDatabase.FindAssets("t:AudioClip");
            int converted = 0, skipped = 0;
            try
            {
                AssetDatabase.StartAssetEditing();
                for (int i = 0; i < guids.Length; i++)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    if (!IsAudioAsset(path)) { skipped++; continue; }
                    if (AssetImporter.GetAtPath(path) is NeziaAudioImporter) { skipped++; continue; }

                    if (EditorUtility.DisplayCancelableProgressBar(
                            "Nezia", $"Overriding importer: {path}", (float)i / Mathf.Max(1, guids.Length)))
                        break;

                    AssetDatabase.SetImporterOverride<NeziaAudioImporter>(path);
                    converted++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
            }

            Debug.Log($"[Nezia] Importer override applied. converted={converted}, skipped={skipped}");
        }

        [MenuItem("Tools/Nezia/Clear NeziaAudioImporter Override (All Audio Assets)")]
        public static void ClearAll()
        {
            // Override 済みの音声は AudioClip ではなく NeziaAudioClip (ScriptableObject) としてヒットする。
            var guids = AssetDatabase.FindAssets("t:" + nameof(NeziaAudioClip));
            int cleared = 0;
            try
            {
                AssetDatabase.StartAssetEditing();
                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!IsAudioAsset(path)) continue;
                    if (AssetImporter.GetAtPath(path) is not NeziaAudioImporter) continue;
                    AssetDatabase.ClearImporterOverride(path);
                    cleared++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            Debug.Log($"[Nezia] Cleared NeziaAudioImporter override. cleared={cleared}");
        }
    }
}
