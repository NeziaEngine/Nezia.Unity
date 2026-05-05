using System;
using Nezia.Native;

namespace Nezia.Unity
{
    /// <summary>
    /// <see cref="NeziaBuffer"/> のデコード済み PCM をフレーム単位で読み出すリーダー。
    ///
    /// <para>
    /// <see cref="Read"/> は **任意スレッドから呼んでよい** (lock-free)。Unity の
    /// <c>AudioClip.pcmReadCallback</c>（オーディオスレッド）や、波形描画用の
    /// バックグラウンドタスクからもそのまま叩ける。
    /// </para>
    ///
    /// <para>
    /// <see cref="Dispose"/> されるまでバッファのアンロードを遅延させる効果はないので、
    /// バッファ自体のライフタイムは呼出側で管理すること。
    /// </para>
    /// </summary>
    public sealed unsafe class NeziaBufferReader : IDisposable
    {
        private global::Nezia.Native.NeziaBufferReader* _reader;

        internal NeziaBufferReader(global::Nezia.Native.NeziaBufferReader* reader) { _reader = reader; }

        private void RequireReader()
        {
            if (_reader == null)
                throw new ObjectDisposedException(nameof(NeziaBufferReader));
        }

        /// <summary>チャンネル数。</summary>
        public ushort Channels { get { RequireReader(); return LibNezia.nezia_buffer_reader_channels(_reader); } }

        /// <summary>サンプルレート (Hz)。</summary>
        public uint SampleRate { get { RequireReader(); return LibNezia.nezia_buffer_reader_sample_rate(_reader); } }

        /// <summary>総フレーム数（チャンネルあたり）。</summary>
        public ulong TotalFrames { get { RequireReader(); return LibNezia.nezia_buffer_reader_total_frames(_reader); } }

        /// <summary>
        /// <paramref name="frameOffset"/> から <paramref name="dst"/> をインターリーブ PCM で埋める。
        /// 戻り値は **実際に書き込んだフレーム数**（要求より少ないことがある = EOF 到達）。
        /// </summary>
        /// <param name="dst">書き込み先。長さは <see cref="Channels"/> の倍数を期待する。</param>
        /// <param name="frameOffset">読み出し開始フレーム位置。</param>
        /// <param name="count">書き込みたいサンプル数。<paramref name="dst"/>.Length 以下。0 で全長。</param>
        public ulong Read(float[] dst, ulong frameOffset, int count = 0)
        {
            RequireReader();
            if (dst == null) throw new ArgumentNullException(nameof(dst));
            if (count == 0) count = dst.Length;
            if ((uint)count > (uint)dst.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            fixed (float* p = dst)
            {
                return LibNezia.nezia_buffer_reader_read(_reader, frameOffset, p, (nuint)count);
            }
        }

        public void Dispose()
        {
            if (_reader == null) return;
            LibNezia.nezia_buffer_reader_close(_reader);
            _reader = null;
        }

        ~NeziaBufferReader() { Dispose(); }
    }
}
