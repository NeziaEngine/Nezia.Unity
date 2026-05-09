using System;
using NUnit.Framework;
using UnityEngine;
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

        // ─── NeziaMixerAsset (IP-1 PR-A) ───────────────────────────

#if UNITY_EDITOR
        [Test]
        public void MixerAsset_EmptyValidates()
        {
            var asset = ScriptableObject.CreateInstance<NeziaMixerAsset>();
            try
            {
                Assert.IsEmpty(asset.Validate());
                Assert.IsEmpty(asset.Buses);
            }
            finally { ScriptableObject.DestroyImmediate(asset); }
        }

        [Test]
        public void MixerAsset_DuplicateNames_AreReported()
        {
            var asset = MakeMixer(new[]
            {
                ("Master", ""),
                ("Master", ""),
            });
            try
            {
                var errs = asset.Validate();
                Assert.IsTrue(errs.Exists(e => e.Contains("重複")));
            }
            finally { ScriptableObject.DestroyImmediate(asset); }
        }

        [Test]
        public void MixerAsset_UnknownParent_IsReported()
        {
            var asset = MakeMixer(new[] { ("BGM", "Nope") });
            try
            {
                var errs = asset.Validate();
                Assert.IsTrue(errs.Exists(e => e.Contains("見つかりません")));
            }
            finally { ScriptableObject.DestroyImmediate(asset); }
        }

        [Test]
        public void MixerAsset_Cycle_IsReported()
        {
            var asset = MakeMixer(new[] { ("A", "B"), ("B", "A") });
            try
            {
                var errs = asset.Validate();
                Assert.IsTrue(errs.Exists(e => e.Contains("循環")));
            }
            finally { ScriptableObject.DestroyImmediate(asset); }
        }
#endif

        [Test]
        public void MixerAsset_ResolveEmptyName_ReturnsInvalid()
        {
            var asset = ScriptableObject.CreateInstance<NeziaMixerAsset>();
            try
            {
                Assert.IsFalse(asset.Resolve(null).IsValid);
                Assert.IsFalse(asset.Resolve("").IsValid);
            }
            finally { ScriptableObject.DestroyImmediate(asset); }
        }

        [Test]
        public void MixerAsset_ResolveEffects_EmptyOnUnknownBus()
        {
            var asset = ScriptableObject.CreateInstance<NeziaMixerAsset>();
            try
            {
                Assert.IsEmpty(asset.ResolveEffects("Nope"));
                Assert.IsFalse(asset.ResolveEffect("Nope", 0).IsValid);
            }
            finally { ScriptableObject.DestroyImmediate(asset); }
        }

        // ─── BusEffect specs (IP-1 PR-B) ───────────────────────────

        [Test]
        public void BusEffect_LowPass_KindMatches()
        {
            var spec = new NeziaMixerAsset.LowPass { cutoff = 800f, q = 1.2f };
            Assert.AreEqual(NeziaEffectKind.LowPass, spec.Kind);
            Assert.IsTrue(spec.enabled);
            Assert.AreEqual(NeziaEffectPosition.Post, spec.position);
        }

        [Test]
        public void BusEffect_HighPass_KindMatches()
        {
            var spec = new NeziaMixerAsset.HighPass();
            Assert.AreEqual(NeziaEffectKind.HighPass, spec.Kind);
        }

        [Test]
        public void BusEffect_Reverb_KindMatches()
        {
            var spec = new NeziaMixerAsset.Reverb();
            Assert.AreEqual(NeziaEffectKind.Reverb, spec.Kind);
        }

        [Test]
        public void BusEffect_Compressor_KindMatches()
        {
            var spec = new NeziaMixerAsset.Compressor();
            Assert.AreEqual(NeziaEffectKind.Compressor, spec.Kind);
        }

        // ─── SendNode (IP-1 PR-C) ──────────────────────────────────

        [Test]
        public void SendTargetKind_OrdinalsArePinned()
        {
            Assert.AreEqual(0, (byte)NeziaMixerAsset.SendTargetKind.Bus);
            Assert.AreEqual(1, (byte)NeziaMixerAsset.SendTargetKind.CompressorSidechain);
        }

        [Test]
        public void SendNode_Defaults()
        {
            var s = new NeziaMixerAsset.SendNode();
            Assert.AreEqual(NeziaMixerAsset.SendTargetKind.Bus, s.target);
            Assert.AreEqual(NeziaSendPosition.Post, s.position);
            Assert.AreEqual(1f, s.gain);
        }

#if UNITY_EDITOR
        [Test]
        public void Sends_UnknownBuses_AreReported()
        {
            var asset = MakeMixer(new[] { ("BGM", ""), ("SFX", "") });
            SetSends(asset, new[]
            {
                new NeziaMixerAsset.SendNode { source = "Nope", targetBus = "SFX" },
                new NeziaMixerAsset.SendNode { source = "BGM",  targetBus = "Nada" },
            });
            try
            {
                var errs = asset.Validate();
                Assert.IsTrue(errs.Exists(e => e.Contains("source 'Nope'")));
                Assert.IsTrue(errs.Exists(e => e.Contains("targetBus 'Nada'")));
            }
            finally { ScriptableObject.DestroyImmediate(asset); }
        }

        [Test]
        public void Sends_SelfLoop_IsReported()
        {
            var asset = MakeMixer(new[] { ("BGM", "") });
            SetSends(asset, new[]
            {
                new NeziaMixerAsset.SendNode { source = "BGM", targetBus = "BGM" },
            });
            try
            {
                var errs = asset.Validate();
                Assert.IsTrue(errs.Exists(e => e.Contains("同一バス")));
            }
            finally { ScriptableObject.DestroyImmediate(asset); }
        }

        [Test]
        public void Sends_SidechainTargetMustBeCompressor()
        {
            var asset = MakeMixer(new[] { ("BGM", ""), ("SFX", "") });
            // SFX に LowPass を 1 本だけ。sidechain 先として LowPass を指すと不正。
            asset.Buses[1].effects.Add(new NeziaMixerAsset.LowPass());
            SetSends(asset, new[]
            {
                new NeziaMixerAsset.SendNode
                {
                    source = "BGM",
                    targetBus = "SFX",
                    target = NeziaMixerAsset.SendTargetKind.CompressorSidechain,
                    targetEffectIndex = 0,
                },
            });
            try
            {
                var errs = asset.Validate();
                Assert.IsTrue(errs.Exists(e => e.Contains("Compressor ではありません")));
            }
            finally { ScriptableObject.DestroyImmediate(asset); }
        }

        [Test]
        public void Sends_SidechainEffectIndex_OutOfRange_IsReported()
        {
            var asset = MakeMixer(new[] { ("BGM", ""), ("SFX", "") });
            SetSends(asset, new[]
            {
                new NeziaMixerAsset.SendNode
                {
                    source = "BGM",
                    targetBus = "SFX",
                    target = NeziaMixerAsset.SendTargetKind.CompressorSidechain,
                    targetEffectIndex = 5,
                },
            });
            try
            {
                var errs = asset.Validate();
                Assert.IsTrue(errs.Exists(e => e.Contains("targetEffectIndex")));
            }
            finally { ScriptableObject.DestroyImmediate(asset); }
        }

        [Test]
        public void Sends_Valid_ProducesNoErrors()
        {
            var asset = MakeMixer(new[] { ("BGM", ""), ("SFX", "") });
            asset.Buses[1].effects.Add(new NeziaMixerAsset.Compressor());
            SetSends(asset, new[]
            {
                new NeziaMixerAsset.SendNode { source = "BGM", targetBus = "SFX" },
                new NeziaMixerAsset.SendNode
                {
                    source = "BGM",
                    targetBus = "SFX",
                    target = NeziaMixerAsset.SendTargetKind.CompressorSidechain,
                    targetEffectIndex = 0,
                },
            });
            try
            {
                Assert.IsEmpty(asset.Validate());
                Assert.AreEqual(2, asset.Sends.Count);
            }
            finally { ScriptableObject.DestroyImmediate(asset); }
        }

        private static void SetSends(NeziaMixerAsset asset, NeziaMixerAsset.SendNode[] entries)
        {
            var field = typeof(NeziaMixerAsset).GetField("sends",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var list = new System.Collections.Generic.List<NeziaMixerAsset.SendNode>(entries);
            field.SetValue(asset, list);
        }
#endif

        private static NeziaMixerAsset MakeMixer((string name, string parent)[] entries)
        {
            var asset = ScriptableObject.CreateInstance<NeziaMixerAsset>();
            // private List<BusNode> へは Reflection でセット（Inspector を介さずに組み立てる）。
            var field = typeof(NeziaMixerAsset).GetField("buses",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var list = new System.Collections.Generic.List<NeziaMixerAsset.BusNode>();
            foreach (var (n, p) in entries)
                list.Add(new NeziaMixerAsset.BusNode { name = n, parent = p, gain = 1f });
            field.SetValue(asset, list);
            return asset;
        }
    }
}
