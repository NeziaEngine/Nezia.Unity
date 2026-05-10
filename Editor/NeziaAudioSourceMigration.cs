using System.Collections.Generic;
using Nezia.Unity;
using UnityEditor;
using UnityEngine;

namespace Nezia.Unity.Editor
{
    /// <summary>
    /// IP-4 移行ユーティリティ（PR-C2）。
    ///
    /// <para>
    /// 既存の <see cref="NeziaAudioSource"/>（<c>useClipDefaults=false</c> の互換モード）を
    /// Clip-centric モードへ flip し、Source 値が Clip 値と異なるパラメータについて
    /// override flag を立てて挙動を完全保存する。Source 値が Clip 値と一致する場合は
    /// flag を OFF にして以後 Clip が支配する状態へ移行する。
    /// </para>
    ///
    /// <para>
    /// 目安: 既定値しか触っていない Source は完全に Clip-centric に倒れ、
    /// 何かを Source 上でカスタマイズしていた場合はその項目だけ override ON で残る。
    /// </para>
    /// </summary>
    public static class NeziaAudioSourceMigration
    {
        private const string MenuRoot = "Tools/Nezia/";

        [MenuItem(MenuRoot + "Convert Selection to Clip-centric Mode")]
        public static void ConvertToClipCentric()
        {
            int touched = 0, alreadyConverted = 0;
            foreach (var go in IterateSelectedRoots())
            foreach (var src in go.GetComponentsInChildren<NeziaAudioSource>(includeInactive: true))
            {
                if (src.useClipDefaults) { alreadyConverted++; continue; }
                ConvertOne(src);
                touched++;
            }
            Debug.Log($"[Nezia] Converted {touched} NeziaAudioSource(s) to Clip-centric mode " +
                      $"(skipped {alreadyConverted} already-converted).");
        }

        [MenuItem(MenuRoot + "Revert Selection to Legacy Mode")]
        public static void RevertToLegacy()
        {
            int touched = 0;
            foreach (var go in IterateSelectedRoots())
            foreach (var src in go.GetComponentsInChildren<NeziaAudioSource>(includeInactive: true))
            {
                if (!src.useClipDefaults) continue;
                Undo.RecordObject(src, "Revert to legacy mode");
                var so = new SerializedObject(src);
                so.FindProperty("_useClipDefaults").boolValue = false;
                // override flag は保存しておく（再 flip 時に意味を持つ）。クリアはしない。
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(src);
                touched++;
            }
            Debug.Log($"[Nezia] Reverted {touched} NeziaAudioSource(s) to legacy mode.");
        }

        // ─── 変換本体 ────────────────────────────────────────────

        private static void ConvertOne(NeziaAudioSource src)
        {
            Undo.RecordObject(src, "Convert to Clip-centric mode");

            var so = new SerializedObject(src);
            so.FindProperty("_useClipDefaults").boolValue = true;

            // Asset 参照が無いときは Clip 値との比較ができないので「全 override ON」で挙動保存する。
            var asset = ResolveAsset(so);

            so.FindProperty("_overrideLoop").boolValue = !LoopMatches(src, asset);
            so.FindProperty("_overrideOutputBus").boolValue = !OutputBusFieldsAreEmpty(so);
            so.FindProperty("_overrideSpatial").boolValue = !SpatialMatches(src, asset);
            so.FindProperty("_overrideAttenuation").boolValue = !AttenuationMatches(src, asset);
            so.FindProperty("_overrideDoppler").boolValue = !DopplerMatches(src, asset);
            so.FindProperty("_overridePriority").boolValue = !PriorityMatches(src, asset);

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(src);
        }

        // ─── 比較ロジック ────────────────────────────────────────
        //
        // asset == null のときは「Clip 値が無いので Source 値が支配し続けるべき」と解釈し、
        // override ON 相当 (= Match しない) を返す。

        private static bool LoopMatches(NeziaAudioSource src, NeziaSoundAsset asset)
            => asset != null && src.loop == asset.Loop;

        private static bool SpatialMatches(NeziaAudioSource src, NeziaSoundAsset asset)
        {
            if (asset == null) return false;
            return Mathf.Approximately(src.spatialBlend, asset.SpatialBlend)
                && Mathf.Approximately(src.minDistance, asset.MinDistance)
                && Mathf.Approximately(src.maxDistance, asset.MaxDistance)
                && src.rolloffMode == asset.RolloffMode;
        }

        private static bool AttenuationMatches(NeziaAudioSource src, NeziaSoundAsset asset)
            => asset != null && src.attenuationCurve == asset.AttenuationCurve;

        private static bool DopplerMatches(NeziaAudioSource src, NeziaSoundAsset asset)
            => asset != null && Mathf.Approximately(src.dopplerLevel, asset.DopplerLevel);

        private static bool PriorityMatches(NeziaAudioSource src, NeziaSoundAsset asset)
            => asset != null && src.priority == asset.Priority;

        // outputBus は Source 側の _outputAudioMixerGroup / _busMap / _mixerAsset /
        // _outputBusName のいずれかが設定されていれば「ユーザーが Source 側で意図的に
        // 配線した」と判断し override ON。すべて空なら override OFF にして Clip に委譲。
        private static bool OutputBusFieldsAreEmpty(SerializedObject so)
        {
            return so.FindProperty("_outputAudioMixerGroup").objectReferenceValue == null
                && so.FindProperty("_busMap").objectReferenceValue == null
                && so.FindProperty("_mixerAsset").objectReferenceValue == null
                && string.IsNullOrEmpty(so.FindProperty("_outputBusName").stringValue);
        }

        private static NeziaSoundAsset ResolveAsset(SerializedObject so)
        {
            return so.FindProperty("_sound").objectReferenceValue as NeziaSoundAsset;
        }

        private static IEnumerable<GameObject> IterateSelectedRoots()
        {
            var roots = Selection.gameObjects;
            if (roots == null || roots.Length == 0)
            {
                Debug.LogWarning("[Nezia] No GameObject selected.");
                yield break;
            }
            foreach (var go in roots) yield return go;
        }
    }
}
