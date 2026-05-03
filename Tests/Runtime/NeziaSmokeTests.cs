using NUnit.Framework;
using Nezia.Unity;

namespace Nezia.Unity.Tests
{
    /// <summary>
    /// 高レベル API の最低限のスモークテスト。
    /// ネイティブライブラリのロードが要らない範囲（型形状・既定値・enum マッピング）に限定する。
    /// </summary>
    public sealed class NeziaSmokeTests
    {
        [Test]
        public void InvalidBuffer_IsNotValid()
        {
            Assert.IsFalse(NeziaBuffer.Invalid.IsValid);
        }

        [Test]
        public void InvalidBus_IsNotValid()
        {
            Assert.IsFalse(NeziaBus.Invalid.IsValid);
        }

        [Test]
        public void RolloffMode_MapsToNativeAttenuationModel()
        {
            // ToNative は internal だが、enum 値が一致していることを int 比較で検証する。
            Assert.AreEqual((int)NeziaRolloffMode.None, 0);
            Assert.AreEqual((int)NeziaRolloffMode.Linear, 1);
            Assert.AreEqual((int)NeziaRolloffMode.InverseDistance, 2);
            Assert.AreEqual((int)NeziaRolloffMode.Exponential, 3);
        }

        [Test]
        public void ErrorCode_OkIsZero()
        {
            Assert.AreEqual(0, (int)NeziaErrorCode.Ok);
        }
    }
}
