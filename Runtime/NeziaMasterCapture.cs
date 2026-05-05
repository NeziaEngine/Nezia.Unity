using System;
using Nezia.Native;

namespace Nezia.Unity
{
    /// <summary>
    /// マスター出力を tap するキャプチャリーダー。
    ///
    /// <para>
    /// <see cref="NeziaEngine.EnableMasterCapture"/> で生成、<see cref="Dispose"/> で解放。
    /// 任意スレッドから <see cref="Read(float[], int)"/> を呼んでよい（lock-free SPSC）。
    /// </para>
    /// </summary>
    public sealed unsafe class NeziaMasterCapture : IDisposable
    {
        private NeziaCaptureReader* _reader;

        internal NeziaMasterCapture(NeziaCaptureReader* reader) { _reader = reader; }

        private void RequireReader()
        {
            if (_reader == null)
                throw new ObjectDisposedException(nameof(NeziaMasterCapture));
        }

        /// <summary>サンプルレート (Hz)。</summary>
        public uint SampleRate { get { RequireReader(); return LibNezia.nezia_capture_reader_sample_rate(_reader); } }

        /// <summary>チャンネル数。</summary>
        public ushort Channels { get { RequireReader(); return LibNezia.nezia_capture_reader_channels(_reader); } }

        /// <summary>起動以降の累積ドロップサンプル数（リング溢れの目安）。</summary>
        public ulong DroppedSamples { get { RequireReader(); return LibNezia.nezia_capture_reader_dropped_samples(_reader); } }

        /// <summary>
        /// インターリーブ PCM を最大 <paramref name="count"/> サンプル <paramref name="dst"/> に書き込む。
        /// 戻り値: 実書き込みサンプル数。
        /// </summary>
        public ulong Read(float[] dst, int count)
        {
            RequireReader();
            if (dst == null) throw new ArgumentNullException(nameof(dst));
            if ((uint)count > (uint)dst.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            fixed (float* p = dst)
            {
                return LibNezia.nezia_capture_reader_read(_reader, p, (nuint)count);
            }
        }

        public void Dispose()
        {
            if (_reader == null) return;
            LibNezia.nezia_capture_reader_close(_reader);
            _reader = null;
        }

        ~NeziaMasterCapture() { Dispose(); }
    }
}
