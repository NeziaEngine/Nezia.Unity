using System;
using Nezia.Native;

namespace Nezia.Unity
{
    /// <summary>
    /// Send のタップ位置。
    /// Pre = Fader 適用前で tap（本線が mute / gain 0 でも Send へ流れる）。
    /// Post = Fader 適用後で tap（本線 mute なら Send もゼロ）。
    /// </summary>
    public enum NeziaSendPosition : byte
    {
        Pre = 0,
        Post = 1,
    }

    /// <summary>
    /// バス → バス、またはバス → Compressor sidechain への Send ハンドル。
    /// </summary>
    public readonly struct NeziaSend : IEquatable<NeziaSend>
    {
        internal readonly NeziaSendId Id;

        internal NeziaSend(NeziaSendId id) { Id = id; }

        public bool IsValid => Id.index != uint.MaxValue;
        public static NeziaSend Invalid => new NeziaSend(new NeziaSendId { index = uint.MaxValue, generation = 0 });

        /// <summary>バス → バスの Send を作成する。</summary>
        public static unsafe NeziaSend AddBusToBus(NeziaBus src, NeziaBus dst, NeziaSendPosition position = NeziaSendPosition.Post, float gain = 1.0f)
        {
            var id = LibNezia.nezia_send_add_bus_to_bus(
                NeziaEngine.RequireHandle(), src.Id, dst.Id, (Native.NeziaSendPosition)position, gain);
            return new NeziaSend(id);
        }

        /// <summary>
        /// バス → Compressor sidechain 入力の Send を作成する。Compressor の sidechain 駆動は自動で有効化される。
        /// </summary>
        public static unsafe NeziaSend AddBusToCompressor(NeziaBus src, NeziaEffect compressor, NeziaSendPosition position = NeziaSendPosition.Post, float gain = 1.0f)
        {
            if (compressor.Kind != NeziaEffectKind.Compressor)
                throw new ArgumentException("compressor must be NeziaEffectKind.Compressor", nameof(compressor));

            var id = LibNezia.nezia_send_add_bus_to_compressor(
                NeziaEngine.RequireHandle(), src.Id, compressor.Id, (Native.NeziaSendPosition)position, gain);
            return new NeziaSend(id);
        }

        /// <summary>
        /// ソース → バスの Send を作成する (User-Defined Aux Send)。Wwise / FMOD の per-event aux send 互換。
        /// 同じ Reverb Bus を共有しつつ、音ごとに reverb 量を独立に持たせるのに使う。
        /// ソースが despawn されると Send も自動で解放される。
        /// </summary>
        internal static unsafe NeziaSend AddSourceToBus(NeziaEntityId src, NeziaBus dst, NeziaSendPosition position = NeziaSendPosition.Post, float gain = 1.0f)
        {
            var id = LibNezia.nezia_send_add_source_to_bus(
                NeziaEngine.RequireHandle(), src, dst.Id, (Native.NeziaSendPosition)position, gain);
            return new NeziaSend(id);
        }

        /// <summary>
        /// ソース → Compressor sidechain 入力の Send を作成する。
        /// </summary>
        internal static unsafe NeziaSend AddSourceToCompressor(NeziaEntityId src, NeziaEffect compressor, NeziaSendPosition position = NeziaSendPosition.Post, float gain = 1.0f)
        {
            if (compressor.Kind != NeziaEffectKind.Compressor)
                throw new ArgumentException("compressor must be NeziaEffectKind.Compressor", nameof(compressor));

            var id = LibNezia.nezia_send_add_source_to_compressor(
                NeziaEngine.RequireHandle(), src, compressor.Id, (Native.NeziaSendPosition)position, gain);
            return new NeziaSend(id);
        }

        public unsafe float Gain
        {
            set
            {
                var r = LibNezia.nezia_send_set_gain(NeziaEngine.RequireHandle(), Id, value);
                NeziaException.ThrowIfError(r, "send set gain");
            }
        }

        public unsafe NeziaSendPosition Position
        {
            set
            {
                var r = LibNezia.nezia_send_set_position(NeziaEngine.RequireHandle(), Id, (Native.NeziaSendPosition)value);
                NeziaException.ThrowIfError(r, "send set position");
            }
        }

        public unsafe void Remove()
        {
            if (!IsValid) return;
            var r = LibNezia.nezia_send_remove(NeziaEngine.RequireHandle(), Id);
            NeziaException.ThrowIfError(r, "send remove");
        }

        public bool Equals(NeziaSend other) => Id.index == other.Id.index && Id.generation == other.Id.generation;
        public override bool Equals(object obj) => obj is NeziaSend s && Equals(s);
        public override int GetHashCode() => unchecked((int)(Id.index * 397 ^ Id.generation));
        public static bool operator ==(NeziaSend a, NeziaSend b) => a.Equals(b);
        public static bool operator !=(NeziaSend a, NeziaSend b) => !a.Equals(b);
    }
}
