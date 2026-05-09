using Nezia.Unity;
using UnityEditor;
using UnityEngine;

namespace Nezia.Unity.Editor
{
    /// <summary>
    /// <see cref="NeziaAudioSource"/> 用 Custom Inspector（IP-4 PR-C1）。
    ///
    /// <para>
    /// <c>useClipDefaults=true</c> のとき override-aware UI に切り替わる:
    /// 各 overridable プロパティに override トグルが付き、未 override なら
    /// 「Clip default: ...」の補助ラベルを表示してフィールドは disabled になる。
    /// volume / pitch は常に Clip 値への scale として扱い、clip mode 中は
    /// 合成後の最終値もインラインで表示する。
    /// </para>
    ///
    /// <para>
    /// <c>useClipDefaults=false</c>（互換モード）のときは従来同等のフラットレイアウト。
    /// </para>
    /// </summary>
    [CustomEditor(typeof(NeziaAudioSource))]
    public sealed class NeziaAudioSourceEditor : UnityEditor.Editor
    {
        // Sound + mode
        private SerializedProperty _sound, _clip, _useClipDefaults;
        // Playback
        private SerializedProperty _volume, _pitch, _loop, _mute, _playOnAwake;
        // Spatial group
        private SerializedProperty _spatialBlend, _minDistance, _maxDistance, _rolloffMode;
        private SerializedProperty _attenuationCurve, _dopplerLevel, _priority;
        // Routing
        private SerializedProperty _outputAudioMixerGroup, _busMap, _mixerAsset, _outputBusName;
        // Override flags
        private SerializedProperty _overrideOutputBus, _overrideSpatial, _overrideAttenuation;
        private SerializedProperty _overrideDoppler, _overridePriority, _overrideLoop;

        private void OnEnable()
        {
            _sound = serializedObject.FindProperty("_sound");
            _clip = serializedObject.FindProperty("_clip");
            _useClipDefaults = serializedObject.FindProperty("_useClipDefaults");
            _volume = serializedObject.FindProperty("_volume");
            _pitch = serializedObject.FindProperty("_pitch");
            _loop = serializedObject.FindProperty("_loop");
            _mute = serializedObject.FindProperty("_mute");
            _playOnAwake = serializedObject.FindProperty("_playOnAwake");
            _spatialBlend = serializedObject.FindProperty("_spatialBlend");
            _minDistance = serializedObject.FindProperty("_minDistance");
            _maxDistance = serializedObject.FindProperty("_maxDistance");
            _rolloffMode = serializedObject.FindProperty("_rolloffMode");
            _attenuationCurve = serializedObject.FindProperty("_attenuationCurve");
            _dopplerLevel = serializedObject.FindProperty("_dopplerLevel");
            _priority = serializedObject.FindProperty("_priority");
            _outputAudioMixerGroup = serializedObject.FindProperty("_outputAudioMixerGroup");
            _busMap = serializedObject.FindProperty("_busMap");
            _mixerAsset = serializedObject.FindProperty("_mixerAsset");
            _outputBusName = serializedObject.FindProperty("_outputBusName");
            _overrideOutputBus = serializedObject.FindProperty("_overrideOutputBus");
            _overrideSpatial = serializedObject.FindProperty("_overrideSpatial");
            _overrideAttenuation = serializedObject.FindProperty("_overrideAttenuation");
            _overrideDoppler = serializedObject.FindProperty("_overrideDoppler");
            _overridePriority = serializedObject.FindProperty("_overridePriority");
            _overrideLoop = serializedObject.FindProperty("_overrideLoop");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // ── Sound asset ──────────────────────────────────────
            EditorGUILayout.PropertyField(_sound, new GUIContent("Sound"));
            // 旧 _clip は _sound 未設定時のみ表示（互換性維持）
            if (_sound.objectReferenceValue == null)
                EditorGUILayout.PropertyField(_clip, new GUIContent("Clip (legacy)"));

            EditorGUILayout.Space(4);

            // ── Mode ─────────────────────────────────────────────
            EditorGUILayout.PropertyField(_useClipDefaults, new GUIContent("Use Clip Defaults",
                "ON: 鳴り方は Clip(SoundAsset) が決め、Source.volume/pitch は Clip 値への scale。" +
                "個別パラメータは override トグルで Source 値を強制可能。\n" +
                "OFF (互換モード): Source の値が直接最終値になる従来挙動。"));

            bool clipMode = _useClipDefaults.boolValue;
            var asset = ResolveAsset();

            EditorGUILayout.Space(4);

            // ── Playback ─────────────────────────────────────────
            EditorGUILayout.LabelField("Playback", EditorStyles.boldLabel);
            DrawScale("Volume", _volume, asset?.Volume ?? 1f, clipMode);
            DrawScale("Pitch", _pitch, asset?.Pitch ?? 1f, clipMode);
            EditorGUILayout.PropertyField(_mute);
            EditorGUILayout.PropertyField(_playOnAwake);
            DrawOverridable("Loop", _loop, _overrideLoop, clipMode,
                clipText: asset != null ? asset.Loop.ToString() : "false");

            EditorGUILayout.Space(4);

            // ── Routing ──────────────────────────────────────────
            EditorGUILayout.LabelField("Routing", EditorStyles.boldLabel);
            DrawOverridableGroup(_overrideOutputBus, clipMode,
                clipText: asset != null ? FormatClipBus(asset) : "Master",
                drawFields: () =>
                {
                    EditorGUILayout.PropertyField(_outputAudioMixerGroup);
                    EditorGUILayout.PropertyField(_busMap);
                    EditorGUILayout.PropertyField(_mixerAsset);
                    EditorGUILayout.PropertyField(_outputBusName);
                });

            EditorGUILayout.Space(4);

            // ── Spatial ──────────────────────────────────────────
            EditorGUILayout.LabelField("Spatial", EditorStyles.boldLabel);
            DrawOverridableGroup(_overrideSpatial, clipMode,
                clipText: asset != null ? FormatClipSpatial(asset) : "2D",
                drawFields: () =>
                {
                    EditorGUILayout.PropertyField(_spatialBlend);
                    EditorGUILayout.PropertyField(_minDistance);
                    EditorGUILayout.PropertyField(_maxDistance);
                    EditorGUILayout.PropertyField(_rolloffMode);
                });

            DrawOverridable("Attenuation Curve", _attenuationCurve, _overrideAttenuation, clipMode,
                clipText: asset?.AttenuationCurve != null ? asset.AttenuationCurve.name : "(none)");

            DrawOverridable("Doppler Level", _dopplerLevel, _overrideDoppler, clipMode,
                clipText: asset != null ? asset.DopplerLevel.ToString("0.##") : "1");

            EditorGUILayout.Space(4);

            // ── Voice ────────────────────────────────────────────
            EditorGUILayout.LabelField("Voice", EditorStyles.boldLabel);
            DrawOverridable("Priority", _priority, _overridePriority, clipMode,
                clipText: asset != null ? asset.Priority.ToString() : "128");

            serializedObject.ApplyModifiedProperties();
        }

        // ─── Helpers ─────────────────────────────────────────────

        private NeziaSoundAsset ResolveAsset()
        {
            var asset = _sound.objectReferenceValue as NeziaSoundAsset;
            return asset ?? (_clip.objectReferenceValue as NeziaSoundAsset);
        }

        private static string FormatClipBus(NeziaSoundAsset a)
        {
            if (a.OutputMixerAsset == null || string.IsNullOrEmpty(a.OutputBusName))
                return "Master";
            return $"{a.OutputMixerAsset.name} / {a.OutputBusName}";
        }

        private static string FormatClipSpatial(NeziaSoundAsset a)
        {
            if (a.SpatialBlend <= 0f) return "2D";
            return $"3D ({a.MinDistance:0.#}m–{a.MaxDistance:0.#}m, {a.RolloffMode})";
        }

        // volume / pitch 用: Clip 値が 1 でなければ合成後の最終値を補助表示する。
        private void DrawScale(string label, SerializedProperty prop, float clipDefault, bool clipMode)
        {
            EditorGUILayout.PropertyField(prop, new GUIContent(label));
            if (!clipMode || Mathf.Approximately(clipDefault, 1f)) return;
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.LabelField(
                    $"× Clip {clipDefault:0.##} = {prop.floatValue * clipDefault:0.##}",
                    EditorStyles.miniLabel);
            }
        }

        // 単一フィールド用の override-aware drawer。
        private void DrawOverridable(string label, SerializedProperty value, SerializedProperty overrideFlag, bool clipMode, string clipText)
        {
            if (!clipMode)
            {
                EditorGUILayout.PropertyField(value, new GUIContent(label));
                return;
            }

            EditorGUILayout.BeginHorizontal();
            overrideFlag.boolValue = EditorGUILayout.ToggleLeft(
                new GUIContent(" ", "Override で Source 値を Clip 値より優先する"),
                overrideFlag.boolValue, GUILayout.Width(28));
            using (new EditorGUI.DisabledScope(!overrideFlag.boolValue))
                EditorGUILayout.PropertyField(value, new GUIContent(label));
            EditorGUILayout.EndHorizontal();
            if (!overrideFlag.boolValue)
            {
                using (new EditorGUI.IndentLevelScope())
                    EditorGUILayout.LabelField($"Clip default: {clipText}", EditorStyles.miniLabel);
            }
        }

        // 複数フィールドをひとまとめの override で扱うグループ drawer。
        private void DrawOverridableGroup(SerializedProperty overrideFlag, bool clipMode, string clipText, System.Action drawFields)
        {
            if (!clipMode)
            {
                drawFields();
                return;
            }

            overrideFlag.boolValue = EditorGUILayout.ToggleLeft(
                new GUIContent("Override", "Source 値を Clip 値より優先する"),
                overrideFlag.boolValue);

            using (new EditorGUI.IndentLevelScope())
            {
                if (!overrideFlag.boolValue)
                {
                    EditorGUILayout.LabelField($"Clip default: {clipText}", EditorStyles.miniLabel);
                }
                else
                {
                    drawFields();
                }
            }
        }
    }
}
