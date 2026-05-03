using System.Collections.Generic;
using Nezia.Unity;
using UnityEditor;
using UnityEngine;

namespace Nezia.Unity.Editor
{
    /// <summary>
    /// CONCEPT.md レベル 3「シーン透過変換」。
    /// 選択 GameObject 配下の <c>AudioSource</c> ↔ <see cref="NeziaAudioSource"/> を再帰的に
    /// 置き換える Editor メニューを提供する。
    /// </summary>
    public static class ReplaceAudioSourcesMenu
    {
        private const string MenuRoot = "Tools/Nezia/";

        [MenuItem(MenuRoot + "Replace AudioSources With NeziaAudioSource (in Selection)")]
        public static void ReplaceUnityToNezia()
        {
            int converted = 0;
            foreach (var go in IterateSelectedRoots())
            foreach (var src in go.GetComponentsInChildren<AudioSource>(includeInactive: true))
            {
                ConvertUnityToNezia(src);
                converted++;
            }
            Debug.Log($"[Nezia] Replaced {converted} AudioSource(s) with NeziaAudioSource.");
        }

        [MenuItem(MenuRoot + "Replace NeziaAudioSources With AudioSource (in Selection)")]
        public static void ReplaceNeziaToUnity()
        {
            int converted = 0;
            foreach (var go in IterateSelectedRoots())
            foreach (var src in go.GetComponentsInChildren<NeziaAudioSource>(includeInactive: true))
            {
                ConvertNeziaToUnity(src);
                converted++;
            }
            Debug.Log($"[Nezia] Reverted {converted} NeziaAudioSource(s) to AudioSource.");
        }

        [MenuItem(MenuRoot + "Add NeziaAudioListener To AudioListeners (in Selection)")]
        public static void AttachListenerBridge()
        {
            int attached = 0;
            foreach (var go in IterateSelectedRoots())
            foreach (var listener in go.GetComponentsInChildren<AudioListener>(includeInactive: true))
            {
                if (listener.GetComponent<NeziaAudioListener>() != null) continue;
                Undo.AddComponent<NeziaAudioListener>(listener.gameObject);
                attached++;
            }
            Debug.Log($"[Nezia] Attached NeziaAudioListener to {attached} AudioListener(s).");
        }

        // ─── 変換本体 ────────────────────────────────────────────

        private static void ConvertUnityToNezia(AudioSource src)
        {
            var go = src.gameObject;
            Undo.RecordObject(go, "Replace AudioSource with NeziaAudioSource");

            var snap = new Snapshot
            {
                Volume = src.volume,
                Pitch = src.pitch,
                Loop = src.loop,
                Mute = src.mute,
                PlayOnAwake = src.playOnAwake,
                SpatialBlend = src.spatialBlend,
                MinDistance = src.minDistance,
                MaxDistance = src.maxDistance,
                Rolloff = ToNeziaRolloff(src.rolloffMode),
            };

            Undo.DestroyObjectImmediate(src);
            var nezia = Undo.AddComponent<NeziaAudioSource>(go);
            snap.ApplyTo(nezia);
        }

        private static void ConvertNeziaToUnity(NeziaAudioSource src)
        {
            var go = src.gameObject;
            Undo.RecordObject(go, "Replace NeziaAudioSource with AudioSource");

            var snap = new Snapshot
            {
                Volume = src.volume,
                Pitch = src.pitch,
                Loop = src.loop,
                Mute = src.mute,
                SpatialBlend = src.spatialBlend,
                MinDistance = src.minDistance,
                MaxDistance = src.maxDistance,
                Rolloff = src.rolloffMode,
            };

            Undo.DestroyObjectImmediate(src);
            var unity = Undo.AddComponent<AudioSource>(go);
            unity.volume = snap.Volume;
            unity.pitch = snap.Pitch;
            unity.loop = snap.Loop;
            unity.mute = snap.Mute;
            unity.spatialBlend = snap.SpatialBlend;
            unity.minDistance = snap.MinDistance;
            unity.maxDistance = snap.MaxDistance;
            unity.rolloffMode = ToUnityRolloff(snap.Rolloff);
            // clip / outputAudioMixerGroup の自動マッピングは別途設計（情報不足のため未対応）
        }

        // ─── helpers ────────────────────────────────────────────

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

        private static NeziaRolloffMode ToNeziaRolloff(AudioRolloffMode mode) => mode switch
        {
            AudioRolloffMode.Linear => NeziaRolloffMode.Linear,
            AudioRolloffMode.Logarithmic => NeziaRolloffMode.InverseDistance,
            AudioRolloffMode.Custom => NeziaRolloffMode.InverseDistance, // 近似
            _ => NeziaRolloffMode.InverseDistance,
        };

        private static AudioRolloffMode ToUnityRolloff(NeziaRolloffMode mode) => mode switch
        {
            NeziaRolloffMode.Linear => AudioRolloffMode.Linear,
            NeziaRolloffMode.InverseDistance => AudioRolloffMode.Logarithmic,
            NeziaRolloffMode.Exponential => AudioRolloffMode.Custom,
            _ => AudioRolloffMode.Logarithmic,
        };

        private struct Snapshot
        {
            public float Volume;
            public float Pitch;
            public bool Loop;
            public bool Mute;
            public bool PlayOnAwake;
            public float SpatialBlend;
            public float MinDistance;
            public float MaxDistance;
            public NeziaRolloffMode Rolloff;

            public void ApplyTo(NeziaAudioSource s)
            {
                s.volume = Volume;
                s.pitch = Pitch;
                s.loop = Loop;
                s.mute = Mute;
                s.playOnAwake = PlayOnAwake;
                s.spatialBlend = SpatialBlend;
                s.minDistance = MinDistance;
                s.maxDistance = MaxDistance;
                s.rolloffMode = Rolloff;
            }
        }
    }
}
