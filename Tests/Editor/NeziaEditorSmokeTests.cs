using NUnit.Framework;
using Nezia.Unity.Editor;

namespace Nezia.Unity.Editor.Tests
{
    /// <summary>
    /// Editor 拡張の最低限のスモークテスト（型解決のみ）。
    /// </summary>
    public sealed class NeziaEditorSmokeTests
    {
        [Test]
        public void NeziaAudioImporter_TypeIsResolvable()
        {
            Assert.IsNotNull(typeof(NeziaAudioImporter));
        }

        [Test]
        public void ReplaceAudioSourcesMenu_TypeIsResolvable()
        {
            Assert.IsNotNull(typeof(ReplaceAudioSourcesMenu));
        }
    }
}
