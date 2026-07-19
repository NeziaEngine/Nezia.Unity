using System.IO;
using Nezia.Native;
using Nezia.Unity;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Nezia.Unity.Editor
{
    /// <summary>
    /// <c>.wav .ogg .flac .mp3</c> を横取りして <see cref="NeziaAudioClip"/> として import する
    /// ScriptedImporter。Unity 標準 <c>AudioImporter</c> 経路を完全に置き換える。
    ///
    /// <para>
    /// CONCEPT.md レベル 2 の正規ルート: Inspector ドラッグ・Addressables・AssetBundle の
    /// すべてに乗り、Unity の再エンコードを通らない。
    /// </para>
    ///
    /// <para>
    /// <c>.wav .ogg .flac .mp3</c> は Unity ネイティブ <c>AudioImporter</c> の所有なので、
    /// <c>overrideExts</c> 自体は per-asset 上書き（右クリック → Reimport with → NeziaAudioImporter）
    /// 候補としての登録に留まる。デフォルト経路への昇格は <see cref="NeziaAudioImporterDefault"/>
    /// (<c>AssetPostprocessor</c>) が <c>AssetDatabase.SetImporterOverride&lt;NeziaAudioImporter&gt;</c>
    /// を自動適用することで実現する。
    /// </para>
    ///
    /// <para>
    /// メタデータ（sample rate / channels / total frames）は <c>nezia_audio_peek_metadata</c>
    /// で先読みしてアセットに焼き込む。peek が失敗した場合は 0 のままにする
    /// （再生時に <c>nezia_buffer_load_from_memory</c> が改めて判別するので動作影響は無い）。
    /// </para>
    /// </summary>
    [ScriptedImporter(
        version: 3, // v3: 波形ピーク (waveformPeaks) の焼き込みを追加
        exts: new string[0],
        overrideExts: new[] { "wav", "ogg", "flac", "mp3" })]
    public sealed class NeziaAudioImporter : ScriptedImporter
    {
        /// <summary>
        /// 波形ピークのビン数。Inspector の波形表示の横解像度に相当する。
        /// 256 ビン × 4 byte = 1KB/アセットで、表示には十分な解像度。
        /// </summary>
        private const int WaveformBins = 256;

        public override unsafe void OnImportAsset(AssetImportContext ctx)
        {
            var bytes = File.ReadAllBytes(ctx.assetPath);

            int sampleRate = 0, channels = 0, totalSamples = 0;
            float[] peaks = null;
            if (bytes.Length > 0)
            {
                var meta = default(NeziaAudioMetadata);
                fixed (byte* p = bytes)
                {
                    var r = LibNezia.nezia_audio_peek_metadata(p, (nuint)bytes.Length, &meta);
                    if (r == NeziaResult.Ok)
                    {
                        sampleRate = (int)meta.sample_rate;
                        channels = meta.channels;
                        // int に収まらない長さは現実的に無いがクランプしておく。
                        totalSamples = meta.total_frames > int.MaxValue
                            ? int.MaxValue
                            : (int)meta.total_frames;
                    }
                }

                // 波形ピークを import 時に焼き込む (Inspector 表示用)。
                // フルデコード 1 回分のコストがかかるが、import worker 上で走るため
                // Editor の体感への影響は小さい。失敗時は null のまま
                // (表示側がプレースホルダを出す。再生には影響しない)。
                var candidate = new float[WaveformBins];
                fixed (byte* p = bytes)
                fixed (float* o = candidate)
                {
                    var r = LibNezia.nezia_audio_compute_peaks(
                        p, (nuint)bytes.Length, o, (nuint)WaveformBins);
                    if (r == NeziaResult.Ok)
                    {
                        peaks = candidate;
                    }
                }
            }

            var clip = ScriptableObject.CreateInstance<NeziaAudioClip>();
            NeziaAudioClipImportAccess.Populate(clip, bytes, sampleRate, channels, totalSamples, peaks);

            ctx.AddObjectToAsset("main", clip);
            ctx.SetMainObject(clip);
        }
    }
}
