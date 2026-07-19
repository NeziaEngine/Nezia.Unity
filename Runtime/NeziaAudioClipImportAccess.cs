namespace Nezia.Unity
{
    /// <summary>
    /// <see cref="NeziaAudioClip"/> の internal フィールドへ Editor アセンブリから書き込むための窓口。
    /// 通常コードは触らない。
    /// </summary>
    internal static class NeziaAudioClipImportAccess
    {
        internal static void Populate(NeziaAudioClip clip, byte[] encoded,
            int sampleRate = 0, int channels = 0, int totalSamples = 0)
        {
            clip.encodedBytes = encoded;
            clip.sampleRate = sampleRate;
            clip.channels = channels;
            clip.totalSamples = totalSamples;
        }
    }
}
