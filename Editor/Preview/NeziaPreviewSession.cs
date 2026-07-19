using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
    ///
    /// <para>
    /// 再生は cli の Process 起動 + 応答待ちを伴い、初回は daemon の spawn
    /// (engine 初期化 + cpal open で数百 ms〜数秒) と全バッファのフルデコードが
    /// 走る。これらをメインスレッドで同期実行すると Editor がフリーズするため:
    /// </para>
    /// <list type="bullet">
    ///   <item><see cref="PrewarmAsync"/>: Inspector 表示時などにバックグラウンドで
    ///     daemon を起動しておき、クリック時のコールドスタートを消す。</item>
    ///   <item><see cref="PlayAsync"/>: Unity API 依存の情報 (アセットパス / パラメータ) を
    ///     メインスレッドで集めてから、cli 呼び出しだけをスレッドプールで実行する。</item>
    /// </list>
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

        /// <summary>実行中の状態表示 (「daemon 起動中…」等)。null = アイドル。</summary>
        internal static string StatusText { get; private set; }

        /// <summary>再生準備 (daemon 起動 / ロード / 再生) が進行中か。UI のボタン抑止に使う。</summary>
        internal static bool IsBusy => _busy;

        private static volatile bool _busy;
        private static volatile bool _warming;

        /// <summary>
        /// daemon 起動を直列化するロック。prewarm と Play が同時に走ると、prewarm が
        /// daemon を spawn している最中に Play 側の ping も失敗し、2 匹目の daemon を
        /// spawn して二重に起動待ちする (プリウォームが無意味になり初回が最悪化する)。
        /// バックグラウンドスレッド専用 (メインスレッドでは取らないので UI は固まらない)。
        /// </summary>
        private static readonly object DaemonGate = new();

        /// <summary>進行中の再生の状態変化通知先 (StatusText 遷移のたびにメインスレッドで発火)。</summary>
        private static Action _stateListener;

        /// <summary>
        /// バックグラウンドスレッドから Editor メインスレッドへコールバックを戻すための
        /// <see cref="SynchronizationContext"/>。メインスレッドで呼ばれる API 側で捕捉する。
        /// </summary>
        private static SynchronizationContext _mainCtx;

        // ─── daemon ライフサイクル ─────────────────────────────────

        /// <summary>
        /// daemon をバックグラウンドで起動しておく (プリウォーム)。既に起動済みなら
        /// ping が即通るので安価。Inspector 表示時に呼び、実際の Play クリック時には
        /// コールドスタートが済んでいる状態を狙う。プリウォームの失敗はユーザーに
        /// 提示しない (Play 時に改めて surface する)。
        /// </summary>
        internal static void PrewarmAsync()
        {
            if (_warming || _busy)
            {
                return;
            }
            CaptureMainContext();
            // パス解決はメインスレッド専用 API に依存するのでここ (メインスレッド) で焼く。
            NeziaCliClient.CacheResolvedPaths();
            _warming = true;
            Task.Run(() =>
            {
                var prevError = LastError;
                try
                {
                    if (!EnsureDaemon())
                    {
                        // プリウォームの失敗は握りつぶす (Play 時に再試行 + 提示)。
                        LastError = prevError;
                    }
                }
                catch
                {
                    LastError = prevError;
                }
                finally
                {
                    SetStatus(null);
                    _warming = false;
                }
            });
        }

        /// <summary>
        /// daemon の起動を保証する (直列化)。prewarm 進行中に Play が来た場合は
        /// ここで prewarm の完了を待つだけで済み、2 匹目の spawn をしない。
        /// スレッドプールから呼ばれる。
        /// </summary>
        private static bool EnsureDaemon()
        {
            lock (DaemonGate)
            {
                return EnsureDaemonLocked();
            }
        }

        private static bool EnsureDaemonLocked()
        {
            if (NeziaCliClient.Run("ping", 2000).ok)
            {
                return true;
            }

            SetStatus("daemon 起動中…");

            // パスはメインスレッドでキャッシュ済み (ResolveDaemonPath を off-thread で呼ばない)。
            var daemon = NeziaCliClient.CachedDaemonPath;
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
            catch (Exception e)
            {
                LastError = $"daemon の起動に失敗: {e.Message}";
                return false;
            }

            // port file 書き出し + listen 開始を ping リトライで待つ。
            for (var i = 0; i < 15; i++)
            {
                Thread.Sleep(200);
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

        // ─── 再生 (非同期) ─────────────────────────────────────────

        /// <summary>
        /// SoundAsset を試聴再生する (非同期)。Unity API 依存の情報はこの場
        /// (メインスレッド) で集め、cli 呼び出しだけをスレッドプールで実行する。
        /// 状態が変わるたび <paramref name="onStateChanged"/> をメインスレッドで呼ぶ
        /// (開始で busy=true、完了で busy=false + <see cref="LastError"/> 反映)。
        /// </summary>
        internal static void PlayAsync(NeziaSoundAsset asset, Action onStateChanged)
        {
            if (_busy)
            {
                return;
            }

            CaptureMainContext();
            NeziaCliClient.CacheResolvedPaths();
            LastError = null;

            // ── メインスレッドで Unity API を触るのはここまで ──
            var plan = BuildPlan(asset);
            if (plan == null)
            {
                // BuildPlan が LastError を設定済み。
                onStateChanged?.Invoke();
                return;
            }

            _busy = true;
            StatusText = "再生準備中…";
            _stateListener = onStateChanged;
            onStateChanged?.Invoke();

            Task.Run(() =>
            {
                try
                {
                    Execute(plan);
                }
                catch (Exception e)
                {
                    LastError = $"再生に失敗: {e.Message}";
                }
                finally
                {
                    _busy = false;
                    StatusText = null;
                    _stateListener = null;
                    PostToMain(onStateChanged);
                }
            });
        }

        /// <summary>直近の preview ソースを停止する (即時・軽量なので同期のまま)。</summary>
        internal static void StopLast()
        {
            if (_lastSource != null)
            {
                NeziaCliClient.CacheResolvedPaths();
                NeziaCliClient.Run($"stop {_lastSource}");
                _lastSource = null;
            }
        }

        /// <summary>すべての preview 再生を停止する。</summary>
        internal static void StopAll()
        {
            NeziaCliClient.CacheResolvedPaths();
            NeziaCliClient.Run("stop --all");
            _lastSource = null;
        }

        // ─── plan (メインスレッド) → execute (バックグラウンド) ───────

        /// <summary>再生に必要な情報をメインスレッドで確定させたプラン。</summary>
        private sealed class PlayPlan
        {
            public bool IsContainer;

            // clip
            public string ClipAssetPath;
            public string ClipAbsPath;

            // container
            public string ContainerKey;
            public List<(string assetPath, string absPath)> Children;

            // common
            public string MixerJson;      // null = Master 直結
            public string ClipJson;       // clip のみ
            public string PlayArgsSuffix; // "--volume .. --pitch .. [--loop] [--bus ..]"
        }

        /// <summary>メインスレッドで Unity API を叩き、以降スレッドセーフに扱えるプランへ落とす。</summary>
        private static PlayPlan BuildPlan(NeziaSoundAsset asset)
        {
            var mixer = asset.OutputMixerAsset;
            var plan = new PlayPlan
            {
                MixerJson = mixer != null ? NeziaPreviewJson.BuildMixerJson(mixer) : null,
                PlayArgsSuffix = BuildPlayArgsSuffix(asset),
            };

            switch (asset)
            {
                case NeziaAudioClip clip:
                {
                    var path = AssetDatabase.GetAssetPath(clip);
                    if (string.IsNullOrEmpty(path))
                    {
                        LastError = "アセットパスを解決できません (未保存のアセット?)";
                        return null;
                    }
                    plan.IsContainer = false;
                    plan.ClipAssetPath = path;
                    plan.ClipAbsPath = Path.GetFullPath(path);
                    plan.ClipJson = NeziaPreviewJson.BuildClipJson(asset);
                    return plan;
                }
                case NeziaRandomContainer container:
                {
                    var key = AssetDatabase.GetAssetPath(container);
                    var children = new List<(string, string)>();
                    foreach (var child in container.Children)
                    {
                        switch (child)
                        {
                            case NeziaAudioClip clip:
                                var cp = AssetDatabase.GetAssetPath(clip);
                                if (string.IsNullOrEmpty(cp))
                                {
                                    LastError = $"子クリップ ({clip.name}) のパスを解決できません。";
                                    return null;
                                }
                                children.Add((cp, Path.GetFullPath(cp)));
                                break;
                            case null:
                                continue; // 空スロットはスキップ。
                            default:
                                LastError = $"ネストした子 ({child.name}) の preview は未対応です。";
                                return null;
                        }
                    }
                    if (children.Count == 0)
                    {
                        LastError = "再生可能な子クリップがありません。";
                        return null;
                    }
                    plan.IsContainer = true;
                    plan.ContainerKey = key;
                    plan.Children = children;
                    return plan;
                }
                default:
                    LastError = $"未対応のアセット型: {asset.GetType().Name}";
                    return null;
            }
        }

        /// <summary>プランをバックグラウンドで実行する (cli 呼び出しのみ)。</summary>
        private static void Execute(PlayPlan plan)
        {
            if (!EnsureDaemon())
            {
                return;
            }
            if (plan.MixerJson != null && !EnsureMixer(plan.MixerJson))
            {
                return;
            }

            SetStatus("ロード中…");
            if (plan.IsContainer)
            {
                ExecuteContainer(plan);
            }
            else
            {
                ExecuteClip(plan);
            }
        }

        private static void ExecuteClip(PlayPlan plan)
        {
            var buffer = EnsureBuffer(plan.ClipAssetPath, plan.ClipAbsPath);
            if (buffer == null)
            {
                return;
            }
            var path = WriteTempJson("clip", plan.ClipJson);
            var response = NeziaCliClient.Run($"play {buffer} {plan.PlayArgsSuffix} --clip \"{path}\"");
            if (!response.ok)
            {
                LastError = response.ErrorText;
                return;
            }
            _lastSource = response.source;
        }

        private static void ExecuteContainer(PlayPlan plan)
        {
            var handle = EnsureContainer(plan);
            if (handle == null)
            {
                return;
            }
            // container play は clip params 非対応 (daemon 側の制約)。bus と音量系のみ。
            var response = NeziaCliClient.Run($"container play {handle} {plan.PlayArgsSuffix}");
            if (!response.ok)
            {
                LastError = response.ErrorText;
                return;
            }
            _lastSource = response.source;
        }

        /// <summary>音量 / ピッチ / loop / bus の共通引数。clip params は呼び出し側で付ける。</summary>
        private static string BuildPlayArgsSuffix(NeziaSoundAsset asset)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var args = $"--volume {asset.Volume.ToString("0.0###", ci)}" +
                       $" --pitch {asset.Pitch.ToString("0.0###", ci)}";
            if (asset.Loop)
            {
                args += " --loop";
            }
            if (!string.IsNullOrEmpty(asset.OutputBusName) && asset.OutputMixerAsset != null)
            {
                args += $" --bus \"{asset.OutputBusName}\"";
            }
            return args;
        }

        // ─── リソース準備 (バックグラウンド) ───────────────────────

        private static string EnsureBuffer(string assetPath, string absolutePath)
        {
            if (BufferCache.TryGetValue(assetPath, out var cached))
            {
                return cached;
            }
            var response = NeziaCliClient.Run($"load \"{absolutePath}\"", 30_000);
            if (!response.ok)
            {
                LastError = response.ErrorText;
                return null;
            }
            BufferCache[assetPath] = response.buffer;
            return response.buffer;
        }

        private static string EnsureContainer(PlayPlan plan)
        {
            if (ContainerCache.TryGetValue(plan.ContainerKey, out var cached))
            {
                return cached;
            }

            var buffers = new List<string>();
            foreach (var (childPath, childAbs) in plan.Children)
            {
                var buffer = EnsureBuffer(childPath, childAbs);
                if (buffer == null)
                {
                    return null;
                }
                buffers.Add(buffer);
            }

            var response = NeziaCliClient.Run($"container create {string.Join(" ", buffers)}");
            if (!response.ok)
            {
                LastError = response.ErrorText;
                return null;
            }
            ContainerCache[plan.ContainerKey] = response.container;
            return response.container;
        }

        /// <summary>
        /// ミキサーを daemon にロードする (構成が変わったときのみ)。
        /// mixer load は daemon 側で既存構成の破棄 + 全停止を伴うため、同一構成なら送らない。
        /// </summary>
        private static bool EnsureMixer(string json)
        {
            if (json == _loadedMixerJson)
            {
                return true;
            }
            var path = WriteTempJson("mixer", json);
            var response = NeziaCliClient.Run($"mixer load \"{path}\"");
            if (!response.ok)
            {
                LastError = response.ErrorText;
                return false;
            }
            _loadedMixerJson = json;
            // バスハンドルは張り替わるが、バッファ / コンテナはバス非依存なので維持できる。
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

        private static void CaptureMainContext()
        {
            _mainCtx ??= SynchronizationContext.Current;
        }

        /// <summary>
        /// StatusText を更新し、進行中の再生があればメインスレッドへ状態変化を通知する。
        /// これが無いと「daemon 起動中…」「ロード中…」の遷移が UI に届かず、
        /// 初回コールドスタートの間ずっと「準備中」のままに見える。
        /// </summary>
        private static void SetStatus(string status)
        {
            StatusText = status;
            PostToMain(_stateListener);
        }

        /// <summary>バックグラウンドスレッドからメインスレッドでコールバックを実行する。</summary>
        private static void PostToMain(Action action)
        {
            if (action == null)
            {
                return;
            }
            if (_mainCtx != null)
            {
                _mainCtx.Post(_ => action(), null);
            }
            else
            {
                action();
            }
        }
    }
}
