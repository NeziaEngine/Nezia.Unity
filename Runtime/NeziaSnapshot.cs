using System;
using Nezia.Native;

namespace Nezia.Unity
{
    /// <summary>
    /// バスゲイン / ミュート / Send ゲイン / エフェクトパラメータをまとめて記憶し、
    /// fade 付きで一括適用するスナップショット。AudioMixer の Snapshot に相当。
    /// </summary>
    public readonly struct NeziaSnapshot : IEquatable<NeziaSnapshot>
    {
        internal readonly NeziaSnapshotId Id;

        internal NeziaSnapshot(NeziaSnapshotId id) { Id = id; }

        public bool IsValid => Id.index != uint.MaxValue;
        public static NeziaSnapshot Invalid => new NeziaSnapshot(new NeziaSnapshotId { index = uint.MaxValue, generation = 0 });

        /// <summary>新しい Snapshot ビルダを開始する。</summary>
        public static unsafe Builder Begin() => new Builder(LibNezia.nezia_snapshot_builder_begin());

        /// <summary>このスナップショットを <paramref name="fadeSeconds"/> かけて適用する（0 で即時）。</summary>
        public unsafe void Apply(float fadeSeconds = 0f)
        {
            var r = LibNezia.nezia_snapshot_apply(NeziaEngine.RequireHandle(), Id, fadeSeconds);
            NeziaException.ThrowIfError(r, "snapshot apply");
        }

        /// <summary>スナップショットを破棄する（進行中の補間には影響しない）。</summary>
        public unsafe void Destroy()
        {
            if (!IsValid) return;
            var r = LibNezia.nezia_snapshot_destroy(NeziaEngine.RequireHandle(), Id);
            NeziaException.ThrowIfError(r, "snapshot destroy");
        }

        public bool Equals(NeziaSnapshot other) => Id.index == other.Id.index && Id.generation == other.Id.generation;
        public override bool Equals(object obj) => obj is NeziaSnapshot s && Equals(s);
        public override int GetHashCode() => unchecked((int)(Id.index * 397 ^ Id.generation));
        public static bool operator ==(NeziaSnapshot a, NeziaSnapshot b) => a.Equals(b);
        public static bool operator !=(NeziaSnapshot a, NeziaSnapshot b) => !a.Equals(b);

        /// <summary>
        /// Snapshot 構築用ビルダ。<see cref="Commit"/> または <see cref="Cancel"/> を必ず呼ぶこと
        /// （未呼び出しはネイティブ側のリークになる）。
        /// </summary>
        public unsafe struct Builder
        {
            private NeziaSnapshotBuilder* _ptr;

            internal Builder(NeziaSnapshotBuilder* ptr) { _ptr = ptr; }

            private void RequirePtr()
            {
                if (_ptr == null)
                    throw new InvalidOperationException("[Nezia] SnapshotBuilder has already been committed or cancelled.");
            }

            public Builder SetBusGain(NeziaBus bus, float gain)
            {
                RequirePtr();
                LibNezia.nezia_snapshot_builder_set_bus_gain(_ptr, bus.Id, gain);
                return this;
            }

            public Builder SetBusMuted(NeziaBus bus, bool muted)
            {
                RequirePtr();
                LibNezia.nezia_snapshot_builder_set_bus_muted(_ptr, bus.Id, muted ? (byte)1 : (byte)0);
                return this;
            }

            public Builder SetSendGain(NeziaSend send, float gain)
            {
                RequirePtr();
                LibNezia.nezia_snapshot_builder_set_send_gain(_ptr, send.Id, gain);
                return this;
            }

            public Builder SetEffectParam(NeziaEffect effect, byte param, float value)
            {
                RequirePtr();
                LibNezia.nezia_snapshot_builder_set_effect_param(_ptr, effect.Id, (byte)effect.Kind, param, value);
                return this;
            }

            /// <summary>builder をコミットして <see cref="NeziaSnapshot"/> を得る。以降このビルダは無効。</summary>
            public NeziaSnapshot Commit()
            {
                RequirePtr();
                var id = LibNezia.nezia_snapshot_builder_commit(NeziaEngine.RequireHandle(), _ptr);
                _ptr = null;
                return new NeziaSnapshot(id);
            }

            /// <summary>builder を破棄してコミットせず終わる。</summary>
            public void Cancel()
            {
                if (_ptr == null) return;
                LibNezia.nezia_snapshot_builder_cancel(_ptr);
                _ptr = null;
            }
        }
    }
}
