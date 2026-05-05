using System;
using Nezia.Native;
using UnityEngine;

namespace Nezia.Unity
{
    /// <summary>
    /// デコード済みオーディオデータへのハンドル。
    ///
    /// <para>
    /// 値型として安価にコピー可能だが、解放責任は <see cref="Unload"/> を呼ぶ呼出側にある。
    /// 通常は <see cref="NeziaAudioClip"/> がライフタイムを管理する。
    /// </para>
    /// </summary>
    public readonly struct NeziaBuffer : IEquatable<NeziaBuffer>
    {
        internal readonly NeziaBufferId Id;

        internal NeziaBuffer(NeziaBufferId id) { Id = id; }

        /// <summary>
        /// ハンドルが有効か（ロードに失敗していないか）。
        /// ネイティブ側の INVALID は <c>{ index: u32::MAX, generation: 0 }</c>。
        /// (0, 0) は最初に確保された有効スロット ID なので INVALID 判定に使ってはいけない。
        /// </summary>
        public bool IsValid => Id.index != uint.MaxValue;

        /// <summary>無効ハンドル。</summary>
        public static NeziaBuffer Invalid => new NeziaBuffer(new NeziaBufferId { index = uint.MaxValue, generation = 0 });

        // ─── ロード API（CONCEPT.md「アセットワークフローの推奨順位」対応） ─────

        /// <summary>
        /// エンコード済みバイト列（wav/ogg/flac/mp3）からバッファをロードする。
        /// DLC・<c>UnityWebRequest</c>・Addressables 取得バイト列の正規ルート。
        /// </summary>
        public static unsafe NeziaBuffer LoadFromBytes(byte[] encoded)
        {
            if (encoded == null || encoded.Length == 0)
                throw new ArgumentException("encoded must be non-empty", nameof(encoded));

            var engine = NeziaEngine.RequireHandle();
            fixed (byte* p = encoded)
            {
                var id = LibNezia.nezia_buffer_load_from_memory(engine, p, (nuint)encoded.Length);
                return new NeziaBuffer(id);
            }
        }

        /// <summary>
        /// 既存 Unity <c>AudioClip</c> から PCM を取り出してバッファ化する。
        /// 移行期間専用の暫定 API。新規プロジェクトでは <see cref="LoadFromBytes"/> または
        /// <see cref="NeziaAudioClip"/> を使うこと。
        /// </summary>
        /// <exception cref="NotSupportedException">
        /// <c>loadType == Streaming</c> の場合。元ファイルから <see cref="NeziaAudioClip"/>
        /// に再 import すること。
        /// </exception>
        public static unsafe NeziaBuffer LoadFromAudioClip(AudioClip clip)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            if (clip.loadType == AudioClipLoadType.Streaming)
                throw new NotSupportedException(
                    "[Nezia] Streaming AudioClip cannot be converted. Re-import the source file as a NeziaAudioClip.");

            if (clip.loadState != AudioDataLoadState.Loaded)
            {
                if (!clip.LoadAudioData())
                    throw new InvalidOperationException("[Nezia] AudioClip.LoadAudioData() failed");
            }

            var pcm = new float[clip.samples * clip.channels];
            if (!clip.GetData(pcm, 0))
                throw new InvalidOperationException("[Nezia] AudioClip.GetData() failed");

            var engine = NeziaEngine.RequireHandle();
            NeziaBuffer buf;
            fixed (float* p = pcm)
            {
                var id = LibNezia.nezia_buffer_load_from_pcm(
                    engine, p, (nuint)pcm.Length,
                    (ushort)clip.channels, (uint)clip.frequency);
                buf = new NeziaBuffer(id);
            }

            clip.UnloadAudioData(); // Unity 側 PCM を即時解放してメモリ二重持ち回避
            return buf;
        }

        /// <summary>
        /// ファイルパスからストリーミング再生用バッファをロードする。
        /// 巨大な BGM 等、メモリにフルデコードしたくないアセットで使う。
        /// </summary>
        /// <param name="path">UTF-8 ファイルパス（絶対パス推奨）。</param>
        /// <param name="bufferSeconds">リング容量の目安（秒）。0 以下なら 1.0 を使う。</param>
        public static unsafe NeziaBuffer LoadStreaming(string path, float bufferSeconds = 1.0f)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path must be non-empty", nameof(path));

            var bytes = System.Text.Encoding.UTF8.GetBytes(path);
            var opts = new NeziaStreamingOpts { buffer_seconds = bufferSeconds > 0f ? bufferSeconds : 1.0f };
            fixed (byte* p = bytes)
            {
                var id = LibNezia.nezia_buffer_load_streaming(
                    NeziaEngine.RequireHandle(), p, (nuint)bytes.Length, opts);
                return new NeziaBuffer(id);
            }
        }

        /// <summary>ストリーミングバッファをシークする。静的バッファでは no-op。</summary>
        public unsafe void SeekStreaming(ulong frameOffset)
        {
            if (!IsValid) return;
            LibNezia.nezia_buffer_seek_streaming(NeziaEngine.RequireHandle(), Id, frameOffset);
        }

        /// <summary>ストリーミングバッファのループフラグを設定する。静的バッファでは no-op。</summary>
        public unsafe void SetStreamingLoop(bool looping)
        {
            if (!IsValid) return;
            LibNezia.nezia_buffer_set_streaming_loop(
                NeziaEngine.RequireHandle(), Id, looping ? (byte)1 : (byte)0);
        }

        /// <summary>
        /// このバッファをアンロードする。アンロード後は再生に使用してはならない。
        /// </summary>
        public unsafe void Unload()
        {
            if (!IsValid) return;
            var engine = NeziaEngine.RequireHandle();
            var r = LibNezia.nezia_buffer_unload(engine, Id);
            NeziaException.ThrowIfError(r, "buffer unload");
        }

        public bool Equals(NeziaBuffer other) => Id.index == other.Id.index && Id.generation == other.Id.generation;
        public override bool Equals(object obj) => obj is NeziaBuffer b && Equals(b);
        public override int GetHashCode() => unchecked((int)(Id.index * 397 ^ Id.generation));
        public static bool operator ==(NeziaBuffer a, NeziaBuffer b) => a.Equals(b);
        public static bool operator !=(NeziaBuffer a, NeziaBuffer b) => !a.Equals(b);
    }
}
