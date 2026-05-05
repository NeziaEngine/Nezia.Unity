using System;
using Nezia.Native;

namespace Nezia.Unity
{
    /// <summary>
    /// 子バッファから 1 つを擬似ランダムに選んで再生するコンテナ。
    /// 足音等のバリエーション再生に使う。
    /// </summary>
    public readonly struct NeziaRandomContainer : IEquatable<NeziaRandomContainer>
    {
        internal readonly NeziaContainerId Id;

        internal NeziaRandomContainer(NeziaContainerId id) { Id = id; }

        public bool IsValid => Id.index != uint.MaxValue;
        public static NeziaRandomContainer Invalid => new NeziaRandomContainer(new NeziaContainerId { index = uint.MaxValue, generation = 0 });

        /// <summary>子バッファ群から Random Container を生成する。</summary>
        public static unsafe NeziaRandomContainer Create(NeziaBuffer[] children)
        {
            if (children == null || children.Length == 0)
                throw new ArgumentException("children must be non-empty", nameof(children));

            var ids = new NeziaBufferId[children.Length];
            for (int i = 0; i < children.Length; i++) ids[i] = children[i].Id;

            fixed (NeziaBufferId* p = ids)
            {
                var id = LibNezia.nezia_container_create_random(
                    NeziaEngine.RequireHandle(), p, (nuint)ids.Length);
                return new NeziaRandomContainer(id);
            }
        }

        /// <summary>子を 1 つ選んでマスターバスで再生する（fire-and-forget）。</summary>
        public unsafe void Play(float volume = 1.0f, float pitch = 1.0f, bool looping = false)
        {
            var engine = NeziaEngine.RequireHandle();
            var bus = LibNezia.nezia_engine_master_bus(engine);
            var r = LibNezia.nezia_container_play(engine, Id, volume, pitch, bus, looping ? (byte)1 : (byte)0);
            NeziaException.ThrowIfError(r, "container play");
        }

        /// <summary>子を 1 つ選んで指定バスで再生する（fire-and-forget）。</summary>
        public unsafe void PlayToBus(NeziaBus bus, float volume = 1.0f, float pitch = 1.0f, bool looping = false)
        {
            var r = LibNezia.nezia_container_play(
                NeziaEngine.RequireHandle(), Id, volume, pitch, bus.Id, looping ? (byte)1 : (byte)0);
            NeziaException.ThrowIfError(r, "container play to bus");
        }

        /// <summary>このコンテナを破棄する。</summary>
        public unsafe void Destroy()
        {
            if (!IsValid) return;
            var r = LibNezia.nezia_container_destroy(NeziaEngine.RequireHandle(), Id);
            NeziaException.ThrowIfError(r, "container destroy");
        }

        public bool Equals(NeziaRandomContainer other) => Id.index == other.Id.index && Id.generation == other.Id.generation;
        public override bool Equals(object obj) => obj is NeziaRandomContainer c && Equals(c);
        public override int GetHashCode() => unchecked((int)(Id.index * 397 ^ Id.generation));
        public static bool operator ==(NeziaRandomContainer a, NeziaRandomContainer b) => a.Equals(b);
        public static bool operator !=(NeziaRandomContainer a, NeziaRandomContainer b) => !a.Equals(b);
    }
}
