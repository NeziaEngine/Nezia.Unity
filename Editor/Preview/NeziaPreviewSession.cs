using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Nezia.Unity.Editor.Preview
{
    /// <summary>
    /// IP-6: preview daemon のセッション管理と再生 API。
    ///
    /// daemon は Editor を親 (<c>--parent-pid</c>) として spawn する。Editor が
    /// クラッシュ・終了しても daemon 側の parent PID 監視で自動終了するため、
    /// Editor 側での明示的な kill 管理は不要 (daemon CONCEPT.md §5)。
    /// Domain Reload でキャッシュ (バッファ / ミキサー) は消えるが daemon は生き残り、
    /// 必要になった時点で再ロードされるだけなので状態管理は単純に保てる。
    /// </summary>
    internal static class NeziaPreviewSession
    {
        /// <summary>アセットパス → ロード済み buffer handle。</summary>
        private static readonly Dictionary<string, string> BufferCache = new();

        /// <summary>コンテナアセットの GUID → container handle。</summary>
        private static readonly Dictionary<string, string> ContainerCache = new();

        /// <summary>daemon にロード済みのミキサー構成 (JSON 全文で同一性を判定)。</summary>
        private static string _loadedMixerJson;

        /// <summary>直近の再生 source handle (Stop ボタン用)。</summary>
        private static string _lastSource;

        /// <summary>直近のエラーメッセージ (Inspector 表示用)。null = 正常。</summary>
        internal static string LastError { get; private set; }

        // ─── daemon ライフサイクル ─────────────────────────────────

        /// <summary>
        /// daemon の起動を保証する。未起動なら Editor を親として spawn し、
        /// ping が通るまで待つ (最大 ~3 秒)。
        /// </summary>
        internal static bool EnsureDaemon()
        {
            if (NeziaCliClient.Run("ping", 2000).ok)
            {
                return true;
            }

            var daemon = NeziaCliClient.ResolveDaemonPath();
            if (daemon == null)
            {
                LastError = "nezia-daemon が見つかりません。パスを指定してください。";
                return false;
            }

            var editorPid = Process.GetCurrentProcess().Id;
            // stdout/stderr はリダイレクトしない: Editor 側は読まないし、
            // Editor が先に死ぬと壊れたパイプが daemon 側に残るため
            // (daemon 側にも防御はあるが、読まないパイプはそもそも渡さない)。
            var psi = new ProcessStartInfo
            {
                FileName = daemon,
                Arguments = $"--parent-pid {editorPid}",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            try
            {
                Process.Start(psi);
            }
            catch (System.Exception e)
            {
                LastError = $"daemon の起動に失敗: {e.Message}";
                return false;
            }

            // port file 書き出し + listen 開始を ping リトライで待つ。
            for (var i = 0; i < 15; i++)
            {
                System.Threading.Thread.Sleep(200);
                if (NeziaCliClient.Run("ping", 1000).ok)
                {
                    // daemon が入れ替わったので旧ハンドルはすべて無効。
                    InvalidateCaches();
                    return true;
                }
            }
            LastError = "daemon が起動しましたが ping が通りません。";
            return false;
        }

        private static void InvalidateCaches()
        {
            BufferCache.Clear();
            ContainerCache.Clear();
            _loadedMixerJson = null;
            _lastSource = null;
        }

        // ─── 再生 ─────────────────────────────────────────────────

        /// <summary>
        /// SoundAsset を試聴再生する。クリップ側の音響デフォルト
        /// (volume / pitch / loop / 出力バス / priority / spatial) を適用する。
        /// 失敗時は false を返し <see cref="LastError"/> に理由を残す。
        /// </summary>
        internal static bool Play(NeziaSoundAsset asset)
        {
            LastError = null;
            if (!EnsureDaemon())
            {
                return false;
            }
            if (!EnsureMixer(asset))
            {
                return false;
            }

            return asset switch
            {
                NeziaAudioClip clip => PlayClip(clip),
                NeziaRandomContainer container => PlayContainer(container),
                _ => SetError($"未対応のアセット型: {asset.GetType().Name}"),
            };
        }

        /// <summary>直近の preview ソースを停止する。</summary>
        internal static void StopLast()
        {
            if (_lastSource != null)
            {
                NeziaCliClient.Run($"stop {_lastSource}");
                _lastSource = null;
            }
        }

        /// <summary>すべての preview 再生を停止する。</summary>
        internal static void StopAll()
        {
            NeziaCliClient.Run("stop --all");
            _lastSource = null;
        }

        private static bool PlayClip(NeziaAudioClip clip)
        {
            var buffer = EnsureBuffer(clip);
            if (buffer == null)
            {
                return false;
            }
            var response = NeziaCliClient.Run(BuildPlayArgs($"play {buffer}", clip));
            if (!response.ok)
            {
                return SetError(response.ErrorText);
            }
            _lastSource = response.source;
            return true;
        }

        private static bool PlayContainer(NeziaRandomContainer container)
        {
            var handle = EnsureContainer(container);
            if (handle == null)
            {
                return false;
            }
            // container play は clip params 非対応 (daemon 側の制約)。bus と音量系のみ。
            var args = BuildPlayArgs($"container play {handle}", container, withClip: false);
            var response = NeziaCliClient.Run(args);
            if (!response.ok)
            {
                return SetError(response.ErrorText);
            }
            _lastSource = response.source;
            return true;
        }

        private static string BuildPlayArgs(string prefix, NeziaSoundAsset asset, bool withClip = true)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var args = $"{prefix} --volume {asset.Volume.ToString("0.0###", ci)}" +
                       $" --pitch {asset.Pitch.ToString("0.0###", ci)}";
            if (asset.Loop)
            {
                args += " --loop";
            }
            if (!string.IsNullOrEmpty(asset.OutputBusName) && asset.OutputMixerAsset != null)
            {
                args += $" --bus \"{asset.OutputBusName}\"";
            }
            if (withClip)
            {
                var clipJson = NeziaPreviewJson.BuildClipJson(asset);
                var path = WriteTempJson("clip", clipJson);
                args += $" --clip \"{path}\"";
            }
            return args;
        }

        // ─── リソース準備 ─────────────────────────────────────────

        private static string EnsureBuffer(NeziaAudioClip clip)
        {
            // NeziaAudioClip は ScriptedImporter の産物なので、アセットパス =
            // 元のオーディオファイルそのもの。daemon にはこの絶対パスを渡す。
            var assetPath = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(assetPath))
            {
                SetError("アセットパスを解決できません (未保存のアセット?)");
                return null;
            }
            if (BufferCache.TryGetValue(assetPath, out var cached))
            {
                return cached;
            }
            var absolute = Path.GetFullPath(assetPath);
            var response = NeziaCliClient.Run($"load \"{absolute}\"", 30_000);
            if (!response.ok)
            {
                SetError(response.ErrorText);
                return null;
            }
            BufferCache[assetPath] = response.buffer;
            return response.buffer;
        }

        private static string EnsureContainer(NeziaRandomContainer container)
        {
            var key = AssetDatabase.GetAssetPath(container);
            if (ContainerCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var buffers = new List<string>();
            foreach (var child in container.Children)
            {
                switch (child)
                {
                    case NeziaAudioClip clip:
                        var buffer = EnsureBuffer(clip);
                        if (buffer == null)
                        {
                            return null;
                        }
                        buffers.Add(buffer);
                        break;
                    case null:
                        continue; // 空スロットはスキップ。
                    default:
                        // ネストしたコンテナの preview は daemon 側が未対応。
                        SetError($"ネストした子 ({child.name}) の preview は未対応です。");
                        return null;
                }
            }
            if (buffers.Count == 0)
            {
                SetError("再生可能な子クリップがありません。");
                return null;
            }

            var response = NeziaCliClient.Run($"container create {string.Join(" ", buffers)}");
            if (!response.ok)
            {
                SetError(response.ErrorText);
                return null;
            }
            ContainerCache[key] = response.container;
            return response.container;
        }

        /// <summary>
        /// アセットが参照するミキサーを daemon にロードする (構成が変わったときのみ)。
        /// mixer load は daemon 側で既存構成の破棄 + 全停止を伴うため、同一構成なら送らない。
        /// </summary>
        private static bool EnsureMixer(NeziaSoundAsset asset)
        {
            var mixer = asset.OutputMixerAsset;
            if (mixer == null)
            {
                return true; // Master 直結。
            }
            var json = NeziaPreviewJson.BuildMixerJson(mixer);
            if (json == _loadedMixerJson)
            {
                return true;
            }
            var path = WriteTempJson("mixer", json);
            var response = NeziaCliClient.Run($"mixer load \"{path}\"");
            if (!response.ok)
            {
                return SetError(response.ErrorText);
            }
            _loadedMixerJson = json;
            // mixer load は stop_all + コンテナ以外の再構築を伴わないが、
            // バスハンドルは張り替わる。バッファ / コンテナはバス非依存なので維持できる。
            _lastSource = null;
            return true;
        }

        // ─── ヘルパ ───────────────────────────────────────────────

        private static string WriteTempJson(string prefix, string json)
        {
            var dir = Path.Combine(Path.GetTempPath(), "nezia-preview");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{prefix}.json");
            File.WriteAllText(path, json);
            return path;
        }

        private static bool SetError(string message)
        {
            LastError = message;
            return false;
        }
    }
}
