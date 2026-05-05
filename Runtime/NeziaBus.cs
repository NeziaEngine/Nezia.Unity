using System;
using Nezia.Native;

namespace Nezia.Unity
{
    /// <summary>
    /// バス（ミキサーノード）へのハンドル。
    ///
    /// <para>
    /// マスターバス直下、または既存バス配下に <see cref="Create"/> で生成する。
    /// <see cref="Destroy"/> されない限りエンジン終了時まで生存する。
    /// </para>
    /// </summary>
    public readonly struct NeziaBus : IEquatable<NeziaBus>
    {
        internal readonly NeziaEntityId Id;

        internal NeziaBus(NeziaEntityId id) { Id = id; }

        // ネイティブ側の INVALID は `{ index: u32::MAX, generation: 0 }`。
        // (0, 0) は有効な ID（マスターバス・最初に spawn されたエンティティ等）なので、
        // INVALID 判定の根拠に使ってはいけない。
        public bool IsValid => Id.index != uint.MaxValue;
        public static NeziaBus Invalid => new NeziaBus(new NeziaEntityId { index = uint.MaxValue, generation = 0 });

        /// <summary>マスターバス直下に新しいバスを生成する。</summary>
        public static unsafe NeziaBus Create(float gain = 1.0f)
        {
            var id = LibNezia.nezia_bus_create(NeziaEngine.RequireHandle(), gain);
            return new NeziaBus(id);
        }

        /// <summary>指定した親バス配下に新しいバスを生成する。</summary>
        public static unsafe NeziaBus CreateRouted(NeziaBus parent, float gain = 1.0f)
        {
            var id = LibNezia.nezia_bus_create_routed(NeziaEngine.RequireHandle(), gain, parent.Id);
            return new NeziaBus(id);
        }

        /// <summary>このバスのゲインを設定する。</summary>
        public unsafe float Gain
        {
            set
            {
                var r = LibNezia.nezia_bus_set_gain(NeziaEngine.RequireHandle(), Id, value);
                NeziaException.ThrowIfError(r, "bus set gain");
            }
        }

        /// <summary>このバスのミュートを設定する。</summary>
        public unsafe bool Muted
        {
            set
            {
                var r = LibNezia.nezia_bus_set_muted(NeziaEngine.RequireHandle(), Id, value ? (byte)1 : (byte)0);
                NeziaException.ThrowIfError(r, "bus set muted");
            }
        }

        /// <summary>出力先バスを変更する。循環は <see cref="NeziaErrorCode.BusLoopDetected"/> として例外化される。</summary>
        public unsafe void SetOutput(NeziaBus parent)
        {
            var r = LibNezia.nezia_bus_set_output(NeziaEngine.RequireHandle(), Id, parent.Id);
            NeziaException.ThrowIfError(r, "bus set output");
        }

        /// <summary>このバスのエフェクトチェーン末尾にエフェクトを追加する。</summary>
        public unsafe NeziaEffect AddEffect(NeziaEffectKind kind, NeziaEffectPosition position = NeziaEffectPosition.Post)
        {
            var id = LibNezia.nezia_effect_add(
                NeziaEngine.RequireHandle(),
                Native.NeziaEffectTargetKind.Bus, Id,
                (Native.NeziaEffectKind)(byte)kind,
                (Native.NeziaEffectPosition)(byte)position);
            return new NeziaEffect(id, kind);
        }

        /// <summary>
        /// 追加した Compressor の sidechain 駆動を on/off する。
        /// <see cref="NeziaSend.AddBusToCompressor"/> は内部で自動 on にするため、後から off にする際に使う。
        /// </summary>
        public static unsafe void BindCompressorSidechain(NeziaEffect compressor, bool enabled)
        {
            if (compressor.Kind != NeziaEffectKind.Compressor)
                throw new ArgumentException("compressor must be NeziaEffectKind.Compressor", nameof(compressor));
            var r = LibNezia.nezia_compressor_bind_sidechain(
                NeziaEngine.RequireHandle(), compressor.Id, enabled ? (byte)1 : (byte)0);
            NeziaException.ThrowIfError(r, "compressor bind sidechain");
        }

        /// <summary>このバスを削除する。マスターバスは削除できない。</summary>
        public unsafe void Destroy()
        {
            if (!IsValid) return;
            var r = LibNezia.nezia_bus_destroy(NeziaEngine.RequireHandle(), Id);
            NeziaException.ThrowIfError(r, "bus destroy");
        }

        public bool Equals(NeziaBus other) => Id.index == other.Id.index && Id.generation == other.Id.generation;
        public override bool Equals(object obj) => obj is NeziaBus b && Equals(b);
        public override int GetHashCode() => unchecked((int)(Id.index * 397 ^ Id.generation));
        public static bool operator ==(NeziaBus a, NeziaBus b) => a.Equals(b);
        public static bool operator !=(NeziaBus a, NeziaBus b) => !a.Equals(b);
    }
}
