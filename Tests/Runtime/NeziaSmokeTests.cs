using System;
using NUnit.Framework;
using Nezia.Native;
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

        [Test]
        public void EffectKind_MatchesNativeOrdinals()
        {
            Assert.AreEqual(0, (byte)NeziaEffectKind.LowPass);
            Assert.AreEqual(1, (byte)NeziaEffectKind.HighPass);
            Assert.AreEqual(2, (byte)NeziaEffectKind.Reverb);
            Assert.AreEqual(3, (byte)NeziaEffectKind.Compressor);
        }

        [Test]
        public void EffectAsLowPass_ThrowsOnKindMismatch()
        {
            var reverb = MakeFakeEffect(NeziaEffectKind.Reverb);
            Assert.Throws<InvalidOperationException>(() => reverb.AsLowPass());
            Assert.Throws<InvalidOperationException>(() => reverb.AsHighPass());
            Assert.Throws<InvalidOperationException>(() => reverb.AsCompressor());
        }

        [Test]
        public void EffectAsReverb_ReturnsViewOnMatchingKind()
        {
            var reverb = MakeFakeEffect(NeziaEffectKind.Reverb);
            var view = reverb.AsReverb();
            Assert.AreEqual(reverb, view.Effect);
        }

        // Kind だけを指定したダミー Effect を組み立てる（Id は INVALID）。
        // 実ネイティブ呼び出し前にスローされる検証ロジックだけをテストするため。
        private static NeziaEffect MakeFakeEffect(NeziaEffectKind kind)
        {
            return new NeziaEffect(new NeziaEntityId { index = uint.MaxValue, generation = 0 }, kind);
        }
    }
}
