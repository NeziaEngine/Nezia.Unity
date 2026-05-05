using Nezia.Native;
using UnityEngine;

namespace Nezia.Unity
{
    /// <summary>
    /// Nezia 用オーディオアセット。
    ///
    /// <para>
    /// エンコード済みのファイルバイト列（wav/ogg/flac/mp3）と必要最小限のメタデータを保持する。
    /// PCM 実体は再生時に Nezia 側で確保され、Unity の <c>AudioClip</c> 経路は通らない。
    /// </para>
    ///
    /// <para>
    /// 通常は <c>NeziaAudioImporter</c>（Editor）が <c>.wav .ogg .flac .mp3</c> を横取りして
    /// このアセットを生成する。ランタイム生成には <see cref="CreateFromBytes"/> を使う。
    /// </para>
    /// </summary>
    public sealed class NeziaAudioClip : NeziaSoundAsset
    {
        [SerializeField] internal byte[] encodedBytes;
        [SerializeField] internal int sampleRate;
        [SerializeField] internal int channels;
        [SerializeField] internal int totalSamples;

        // ネイティブの BufferId は (0, 0) も有効値 (最初に確保されるスロット) なので、
        // 「バッファをロード済みか」の判定を ID の sentinel 比較に頼らず、明示的な bool で持つ。
        // Domain Reload / Editor 再起動でネイティブエンジンが作り直されるとキャッシュした
        // BufferId は無効になるが、Unity は DR 後に必ず ScriptableObject.OnEnable を呼ぶので、
        // そこで _bufferLoaded を false に倒すだけでキャッシュが自動破棄される。
        private NeziaBuffer _buffer;
        private bool _bufferLoaded;

        /// <summary>このクリップの長さ（秒）。メタデータが揃っていない場合は 0。</summary>
        public override float Length => sampleRate > 0 ? (float)totalSamples / sampleRate : 0f;

        /// <summary>サンプリング周波数 (Hz)。</summary>
        public override int SampleRate => sampleRate;

        /// <summary>チャンネル数。</summary>
        public int Channels => channels;

        /// <summary>全サンプル数（チャンネルあたり）。</summary>
        public int TotalSamples => totalSamples;

        /// <summary>
        /// バイト列から実行時にクリップを生成する。
        /// Addressables / UnityWebRequest 等で取得したエンコード済みデータを渡す経路。
        /// メタデータはネイティブ側のロードまで未確定（0）になる。
        /// </summary>
        public static NeziaAudioClip CreateFromBytes(byte[] encoded)
        {
            var clip = ScriptableObject.CreateInstance<NeziaAudioClip>();
            clip.encodedBytes = encoded;
            return clip;
        }

        /// <summary>
        /// Nezia 側に PCM をロードしてバッファを取得する。多重呼び出しはキャッシュされる。
        /// </summary>
        public unsafe NeziaBuffer GetOrLoadBuffer()
        {
            if (_bufferLoaded) return _buffer;
            if (encodedBytes == null || encodedBytes.Length == 0)
                return NeziaBuffer.Invalid;

            var engine = NeziaEngine.RequireHandle();
            fixed (byte* p = encodedBytes)
            {
                var id = LibNezia.nezia_buffer_load_from_memory(engine, p, (nuint)encodedBytes.Length);
                _buffer = new NeziaBuffer(id);
            }
            _bufferLoaded = _buffer.IsValid;
            return _buffer;
        }

        // Unity は ScriptableObject の managed state を Domain Reload 越しに保存するため、
        // _buffer / _bufferLoaded には作り直された前のエンジンの BufferId が残る。
        // OnEnable は DR 後に必ず呼ばれるので、ここでキャッシュを破棄して次回の
        // GetOrLoadBuffer で新エンジンに対し再ロードさせる。
        private void OnEnable()
        {
            _buffer = NeziaBuffer.Invalid;
            _bufferLoaded = false;
        }

        // ─── NeziaSoundAsset 実装 ────────────────────────────────

        internal override unsafe NeziaEntityId Spawn(
            Nezia.Native.NeziaEngine* engine,
            float volume, float pitch,
            NeziaEntityId bus, bool looping,
            delegate* unmanaged[Cdecl]<void*, void> callback, void* userData)
        {
            var buffer = GetOrLoadBuffer();
            if (!buffer.IsValid)
                return new NeziaEntityId { index = uint.MaxValue, generation = 0 };

            return LibNezia.nezia_source_play_with_handle(
                engine, buffer.Id, volume, pitch, bus,
                looping ? (byte)1 : (byte)0, callback, userData);
        }

        // ─── AudioClip 必須箇所への橋渡し ────────────────────────

        private AudioClip _proxyClip;
        private NeziaBufferReader _proxyReader;
        // pcmReadCallback / pcmSetPositionCallback はオーディオスレッドから呼ばれるので、
        // メインスレッドのフィールドアクセスは Volatile な long で frame offset を共有する。
        private long _proxyReadCursorFrames;
        private int _proxyChannels;

        /// <summary>
        /// Unity <c>AudioClip</c> が要求される箇所（Timeline AudioTrack・Animation Event・
        /// サードパーティアセット等）への橋渡し。
        ///
        /// <para>
        /// 内部的には <c>AudioClip.Create(stream: true, ...)</c> で PCM 実体を持たない
        /// <c>AudioClip</c> façade を遅延生成し、<c>pcmReadCallback</c> でネイティブの
        /// <see cref="NeziaBufferReader"/> から都度供給する。
        /// 補助手段であり、推奨は <see cref="NeziaAudioSource"/> 経由の直接利用。
        /// </para>
        /// </summary>
        public AudioClip AsAudioClip()
        {
            if (_proxyClip != null) return _proxyClip;

            // バッファをロードしてリーダーを開く。これでネイティブ側のメタデータが確定する。
            var buffer = GetOrLoadBuffer();
            if (buffer.IsValid) _proxyReader = buffer.OpenReader();

            // メタデータはリーダー優先、なければ Importer が書いたシリアライズ値、最後に既定値。
            int sr = (int)(_proxyReader?.SampleRate ?? 0);
            if (sr == 0) sr = sampleRate > 0 ? sampleRate : 44100;
            int ch = _proxyReader?.Channels ?? 0;
            if (ch == 0) ch = channels > 0 ? channels : 2;
            int total = (int)(_proxyReader?.TotalFrames ?? 0);
            if (total == 0) total = totalSamples > 0 ? totalSamples : sr;

            _proxyChannels = ch;
            _proxyReadCursorFrames = 0;

            _proxyClip = AudioClip.Create(
                name: name,
                lengthSamples: total,
                channels: ch,
                frequency: sr,
                stream: true,
                pcmreadercallback: ProxyPcmRead,
                pcmsetpositioncallback: ProxyPcmSetPosition);
            return _proxyClip;
        }

        // pcmReadCallback はオーディオスレッドから呼ばれる。NeziaBufferReader.Read は
        // lock-free なのでそのまま叩いてよい。EOF 到達時は dst 末尾を 0 埋め。
        private void ProxyPcmRead(float[] data)
        {
            var reader = _proxyReader;
            int ch = _proxyChannels;
            if (reader == null || ch <= 0)
            {
                for (int i = 0; i < data.Length; i++) data[i] = 0f;
                return;
            }

            ulong frameOffset = (ulong)System.Threading.Interlocked.Read(ref _proxyReadCursorFrames);
            int requested = (data.Length / ch) * ch; // チャンネル境界で切り捨て
            ulong written = reader.Read(data, frameOffset, requested);
            int writtenSamples = (int)written * ch;
            for (int i = writtenSamples; i < data.Length; i++) data[i] = 0f;

            System.Threading.Interlocked.Exchange(
                ref _proxyReadCursorFrames, (long)(frameOffset + written));
        }

        private void ProxyPcmSetPosition(int newPosition)
        {
            // newPosition は frame 単位で渡される (Unity 仕様)。
            System.Threading.Interlocked.Exchange(ref _proxyReadCursorFrames, newPosition);
        }

        // OnDisable は SO の破棄 / Domain Reload 直前 / Editor 終了時に呼ばれる。
        // DR 直前のときは「現在のエンジンの BufferId」が依然として有効なので、ここで unload しても
        // 次の OnEnable で _bufferLoaded がクリアされ、次回 GetOrLoadBuffer で再ロードされる。
        private void OnDisable()
        {
            if (_proxyReader != null) { _proxyReader.Dispose(); _proxyReader = null; }
            _proxyClip = null;

            if (_bufferLoaded && NeziaEngine.IsInitialized)
            {
                try { _buffer.Unload(); } catch { /* shutdown 順序によっては無視 */ }
            }
            _buffer = NeziaBuffer.Invalid;
            _bufferLoaded = false;
        }
    }
}
