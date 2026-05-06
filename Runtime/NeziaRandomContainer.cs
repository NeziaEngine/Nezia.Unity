using System.Collections.Generic;
using Nezia.Native;
using UnityEngine;

namespace Nezia.Unity
{
    /// <summary>
    /// 子 <see cref="NeziaSoundAsset"/> 群から擬似ランダムに 1 つ選んで再生するコンテナ。
    ///
    /// <para>
    /// Wwise の Random Container / FMOD の Multi Instrument 相当。Inspector で
    /// 子アセットを並べておけば、<see cref="NeziaAudioSource.sound"/> に直接ドロップして
    /// 単発クリップと同じインターフェースで鳴らせる（Phase 4-2 第一弾）。
    /// </para>
    ///
    /// <para>
    /// 子は <see cref="NeziaSoundAsset"/> 配列としており、現状は <see cref="NeziaAudioClip"/>
    /// のみ受理（非対応の子は <see cref="Resolve"/> で警告 + スキップ）。将来 core 側で
    /// <c>ContainerChild::Container</c> が公開されれば、Inspector 構造を変えずに
    /// ネスト Container にも対応できる。
    /// </para>
    /// </summary>
    [CreateAssetMenu(menuName = "Nezia/Random Container", fileName = "NeziaRandomContainer")]
    public sealed class NeziaRandomContainer : NeziaSoundAsset
    {
        [SerializeField] internal NeziaSoundAsset[] children;

        // ネイティブハンドル。エンジンが作り直されると無効になる。
        // Domain Reload オン時は OnEnable で、オフ時 (Editor 限定) は _resolvedGeneration の
        // 不一致で検知して次の Spawn 時に再 resolve する。ビルドではエンジンが一度しか
        // 初期化されないので #if UNITY_EDITOR で除外する。
        private NeziaContainerId _containerId;
        private bool _resolved;
#if UNITY_EDITOR
        private int _resolvedGeneration;
#endif

        /// <summary>子アセットの読み取り専用ビュー。</summary>
        public IReadOnlyList<NeziaSoundAsset> Children => children;

        // Container は子のいずれかに依存するため、長さは概念的に未確定。0 を返す。
        public override float Length => 0f;

        // 代表的なサンプルレートとして最初の子のものを返す（time 計算の都合）。
        public override int SampleRate
        {
            get
            {
                if (children == null) return 0;
                for (int i = 0; i < children.Length; i++)
                    if (children[i] != null && children[i].SampleRate > 0)
                        return children[i].SampleRate;
                return 0;
            }
        }

        internal override bool SupportsFinishCallback => true;

        internal override unsafe NeziaEntityId Spawn(
            Nezia.Native.NeziaEngine* engineHandle,
            float volume, float pitch,
            NeziaEntityId bus, bool looping,
            delegate* unmanaged[Cdecl]<void*, void> callback, void* userData)
        {
            if (!_resolved) Resolve(engineHandle);
            if (!_resolved)
                return new NeziaEntityId { index = uint.MaxValue, generation = 0 };

            return LibNezia.nezia_container_play_with_handle(
                engineHandle, _containerId, volume, pitch, bus, looping ? (byte)1 : (byte)0,
                callback, userData);
        }

        /// <summary>
        /// 子をロードしてネイティブ Container を確保する。多重呼び出しはキャッシュされる。
        /// 通常は <see cref="NeziaAudioSource.Play"/> 起動時に自動で呼ばれる。
        /// </summary>
        public unsafe void Resolve()
        {
            Resolve(NeziaEngine.RequireHandle());
        }

        /// <summary>
        /// 子を 1 つランダムに選んで fire-and-forget で再生する（制御ハンドル不要なケース）。
        /// <paramref name="bus"/> が <c>default</c> または <see cref="NeziaBus.Invalid"/> の場合は
        /// マスターバスへ送る。
        /// </summary>
        public unsafe void PlayFireAndForget(
            float volume = 1f, float pitch = 1f, NeziaBus bus = default, bool looping = false)
        {
            var engine = NeziaEngine.RequireHandle();
            if (!_resolved) Resolve(engine);
            if (!_resolved) return;

            var busId = bus.IsValid ? bus.Id : LibNezia.nezia_engine_master_bus(engine);
            var r = LibNezia.nezia_container_play(
                engine, _containerId, volume, pitch, busId, looping ? (byte)1 : (byte)0);
            NeziaException.ThrowIfError(r, "container play");
        }

        internal unsafe void Resolve(Nezia.Native.NeziaEngine* engine)
        {
#if UNITY_EDITOR
            // 世代不一致 = 旧エンジンの ContainerId を保持している。旧エンジンは free 済みなので
            // destroy は呼ばずにフィールドだけリセットする。
            if (_resolved && _resolvedGeneration != NeziaEngine.Generation)
            {
                _containerId = default;
                _resolved = false;
            }
#endif
            if (_resolved) return;
            if (children == null || children.Length == 0)
            {
                Debug.LogWarning($"[Nezia] {name}: NeziaRandomContainer has no children.", this);
                return;
            }

            // 子の BufferId 一覧を作る。現状 NeziaAudioClip のみ受理し、それ以外は警告。
            var ids = new List<NeziaBufferId>(children.Length);
            for (int i = 0; i < children.Length; i++)
            {
                var ch = children[i];
                if (ch == null) continue;
                if (ch is NeziaAudioClip clip)
                {
                    var buf = clip.GetOrLoadBuffer();
                    if (buf.IsValid) ids.Add(buf.Id);
                }
                else
                {
                    Debug.LogWarning(
                        $"[Nezia] {name}: child '{ch.name}' is {ch.GetType().Name}; " +
                        "nested containers are not supported yet, skipping.", this);
                }
            }

            if (ids.Count == 0)
            {
                Debug.LogWarning($"[Nezia] {name}: no valid children resolved.", this);
                return;
            }

            var idArray = ids.ToArray();
            fixed (NeziaBufferId* p = idArray)
            {
                _containerId = LibNezia.nezia_container_create_random(engine, p, (nuint)idArray.Length);
            }
            _resolved = _containerId.index != uint.MaxValue;
#if UNITY_EDITOR
            _resolvedGeneration = NeziaEngine.Generation;
#endif
            if (!_resolved)
                Debug.LogWarning($"[Nezia] {name}: nezia_container_create_random returned INVALID.", this);
        }

        // Domain Reload 後はネイティブハンドルは無効。次の Spawn で再 resolve させる。
        private void OnEnable()
        {
            _containerId = default;
            _resolved = false;
        }

        // Container は再生中の子 Source とは独立しているので、SO 破棄時に safely destroy できる。
        private unsafe void OnDisable()
        {
            if (!_resolved || !NeziaEngine.IsInitialized) return;
            try
            {
                LibNezia.nezia_container_destroy(NeziaEngine.RequireHandle(), _containerId);
            }
            catch { /* shutdown 順序によっては無視 */ }
            _containerId = default;
            _resolved = false;
        }
    }
}
