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
        /// <summary>アセットパス → ロード済み buffer handle。<see cref="CacheLock"/> 保護。</summary>
        private static readonly Dictionary<string, string> BufferCache = new();

        /// <summary>daemon 上のコンテナ (handle + 子のアセットパス)。</summary>
        private sealed class CachedContainer
        {
            public string Handle;
            public string[] Children;
        }

        /// <summary>コンテナアセットのパス → キャッシュ。<see cref="CacheLock"/> 保護。</summary>
        private static readonly Dictionary<string, CachedContainer> ContainerCache = new();

        /// <summary>
        /// キャッシュ辞書のロック。背景スレッド (Execute) とメインスレッド
        /// (AssetPostprocessor による無効化) の両方から触るため必須。
        /// </summary>
        private static readonly object CacheLock = new();

        /// <summary>daemon にロード済みのミキサー構成 (JSON 全文で同一性を判定)。</summary>
        private static volatile string _loadedMixerJson;

        /// <summary>直近の再生 source handle (Stop ボタン用)。クロススレッド読み書き。</summary>
        private static volatile string _lastSource;

        private static volatile string _lastError;
        private static volatile string _statusText;

        /// <summary>直近のエラーメッセージ (Inspector 表示用)。null = 正常。</summary>
        internal static string LastError
        {
            get => _lastError;
            private set => _lastError = value;
        }

        /// <summary>実行中の状態表示 (「daemon 起動中…」等)。null = アイドル。</summary>
        internal static string StatusText
        {
            get => _statusText;
            private set => _statusText = value;
        }

        /// <summary>再生準備 (daemon 起動 / ロード / 再生) が進行中か。UI のボタン抑止に使う。</summary>
        internal static bool IsBusy => _busy;

        private static volatile bool _busy;
        private static volatile bool _warming;

        /// <summary>
        /// Stop 世代。Stop / StopAll のたびにインクリメントされ、in-flight の再生準備は
        /// 自分の開始時世代と一致しなくなった時点で再生発行を中止する。これが無いと
        /// 「大型ファイルのロード中に Stop → ロード完了後に遅れて鳴り出す」が起きる。
        /// </summary>
        private static int _stopGeneration;

        /// <summary>直近の daemon 起動失敗時刻 (NeziaCliClient.MonotonicMs)。失敗直後の連続再試行を抑止。</summary>
        private static long _daemonFailAtTicks;

        /// <summary>daemon 起動失敗後、再試行を控える時間 (ms)。</summary>
        private const long DaemonRetryBackoffMs = 5000;

        /// <summary>直近の cli 成功からこの時間 (ms) 以内なら生存確認 ping を省略する。</summary>
        private const long PingSkipWindowMs = 3000;

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
                    // Play が進行中なら StatusText はその Play のもの。prewarm 側から
                    // 消すと「ロード中…」等の表示が巻き添えでクリアされてしまう。
                    if (!_busy)
                    {
                        SetStatus(null);
                    }
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
            // 直近数秒以内に cli 成功があれば daemon は生きているとみなし、
            // 生存確認 ping (プロセス spawn 1 回 ≒ 数十 ms) を省略する。
            if (NeziaCliClient.MonotonicMs - NeziaCliClient.LastSuccessTicks < PingSkipWindowMs)
            {
                return true;
            }

            if (NeziaCliClient.Run("ping", 2000).ok)
            {
                return true;
            }

            // 起動失敗直後の再試行はフル起動シーケンス (~3 秒) を繰り返すだけなので、
            // バックオフ時間内は即座に諦める (prewarm 失敗 → Play 失敗の連鎖を短縮)。
            if (NeziaCliClient.MonotonicMs - _daemonFailAtTicks < DaemonRetryBackoffMs)
            {
                LastError ??= "daemon の起動に失敗しました。数秒後に再試行してください。";
                return false;
            }

            SetStatus("daemon 起動中…");

            // パスはメインスレッドでキャッシュ済み (ResolveDaemonPath を off-thread で呼ばない)。
            var daemon = NeziaCliClient.CachedDaemonPath;
            if (daemon == null)
            {
                LastError = "nezia-daemon が見つかりません。パスを指定してください。";
                _daemonFailAtTicks = NeziaCliClient.MonotonicMs;
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
                _daemonFailAtTicks = NeziaCliClient.MonotonicMs;
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
                    _daemonFailAtTicks = 0;
                    return true;
                }
            }
            LastError = "daemon が起動しましたが ping が通りません。";
            _daemonFailAtTicks = NeziaCliClient.MonotonicMs;
            return false;
        }

        private static void InvalidateCaches()
        {
            lock (CacheLock)
            {
                BufferCache.Clear();
                ContainerCache.Clear();
            }
            _loadedMixerJson = null;
            _lastSource = null;
        }

        /// <summary>
        /// 指定アセットに紐づく preview キャッシュを無効化する
        /// (<see cref="NeziaPreviewCacheInvalidator"/> がメインスレッドから呼ぶ)。
        /// これが無いと、音声ファイルの差し替え・再インポート後も daemon には
        /// 旧デコード結果が残り、試聴だけ古い音が鳴り続ける。
        /// コンテナは「自身が変更された」「子のどれかが変更された」の両方で無効化する。
        ///
        /// daemon 側の旧 buffer / container はここでは destroy しない
        /// (メインスレッドを cli 呼び出しでブロックしないため)。孤児は daemon の
        /// 寿命 (= Editor セッション) までの有限リークで、preview 用途では許容する。
        /// </summary>
        internal static void InvalidateAssets(List<string> assetPaths)
        {
            lock (CacheLock)
            {
                var set = new HashSet<string>(assetPaths);
                foreach (var path in assetPaths)
                {
                    BufferCache.Remove(path);
                }

                List<string> dead = null;
                foreach (var entry in ContainerCache)
                {
                    var hit = set.Contains(entry.Key);
                    if (!hit)
                    {
                        foreach (var child in entry.Value.Children)
                        {
                            if (set.Contains(child))
                            {
                                hit = true;
                                break;
                            }
                        }
                    }
                    if (hit)
                    {
                        (dead ??= new List<string>()).Add(entry.Key);
                    }
                }
                if (dead != null)
                {
                    foreach (var key in dead)
                    {
                        ContainerCache.Remove(key);
                    }
                }
            }
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
            plan.StopGeneration = Volatile.Read(ref _stopGeneration);
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

        /// <summary>
        /// Stop 系共通のタイムアウト (ms)。stop はメインスレッドで同期実行するため、
        /// daemon 応答不能時にデフォルト 10 秒も Editor を止めないよう短くする。
        /// </summary>
        private const int StopTimeoutMs = 2000;

        /// <summary>
        /// 直近の preview ソースを停止する。in-flight の再生準備 (ロード中など) も
        /// 世代カウンタ経由でキャンセルされる (完了後に遅れて鳴り出さない)。
        /// </summary>
        internal static void StopLast()
        {
            Interlocked.Increment(ref _stopGeneration);
            var last = _lastSource;
            if (last != null)
            {
                _lastSource = null;
                NeziaCliClient.CacheResolvedPaths();
                NeziaCliClient.Run($"stop {last}", StopTimeoutMs);
            }
        }

        /// <summary>すべての preview 再生を停止する (in-flight の再生準備もキャンセル)。</summary>
        internal static void StopAll()
        {
            Interlocked.Increment(ref _stopGeneration);
            _lastSource = null;
            NeziaCliClient.CacheResolvedPaths();
            NeziaCliClient.Run("stop --all", StopTimeoutMs);
        }

        // ─── plan (メインスレッド) → execute (バックグラウンド) ───────

        /// <summary>この長さ (秒) 以上のクリップはストリーミングロードで試聴する。</summary>
        private const float StreamingThresholdSeconds = 10f;

        /// <summary>長さ不明 (メタデータ欠落) 時のフォールバック: このサイズ以上でストリーミング。</summary>
        private const long StreamingThresholdBytes = 4L * 1024 * 1024;

        /// <summary>再生に必要な情報をメインスレッドで確定させたプラン。</summary>
        private sealed class PlayPlan
        {
            public bool IsContainer;

            // clip
            public string ClipAssetPath;
            public string ClipAbsPath;
            public int ClipLoadTimeoutMs;

            /// <summary>
            /// 長尺クリップをストリーミングバッファでロードする (フルデコードなし・即応答)。
            /// daemon が Play のたびに先頭シーク + ループ同期するため Editor 側の追加管理は不要。
            /// Random Container の子は daemon の container play 経路が streaming の
            /// シーク/ループ同期を持たないため、常に静的ロードする。
            /// </summary>
            public bool ClipStreaming;

            // container
            public string ContainerKey;
            public List<(string assetPath, string absPath, int timeoutMs)> Children;

            // common
            public string MixerJson;      // null = Master 直結
            public string ClipJson;       // clip のみ
            public string PlayArgsSuffix; // "--volume .. --pitch .. [--loop] [--bus ..]"

            /// <summary>開始時点の Stop 世代。ずれたら再生発行を中止する。</summary>
            public int StopGeneration;
        }

        /// <summary>
        /// クリップをストリーミングロードすべきか (メインスレッドで判定)。
        /// 長さがメタデータに焼かれていればそれを使い、欠落時 (総フレーム 0 の
        /// 破損メタ等) はファイルサイズで代替判定する。
        /// </summary>
        private static bool ShouldStream(NeziaAudioClip clip, string absolutePath)
        {
            if (clip.Length > 0f)
            {
                return clip.Length >= StreamingThresholdSeconds;
            }
            try
            {
                return new FileInfo(absolutePath).Length >= StreamingThresholdBytes;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ファイルサイズに応じた load タイムアウト。巨大 BGM のフルデコード
        /// (特に debug ビルドの daemon) は 30 秒では足りないことがある。
        /// </summary>
        private static int LoadTimeoutFor(string absolutePath)
        {
            try
            {
                var mb = new FileInfo(absolutePath).Length / (1024.0 * 1024.0);
                return Mathf.Clamp(30_000 + (int)(mb * 3000), 30_000, 120_000);
            }
            catch
            {
                return 30_000;
            }
        }

        /// <summary>メインスレッドで Unity API を叩き、以降スレッドセーフに扱えるプランへ落とす。</summary>
        private static PlayPlan BuildPlan(NeziaSoundAsset asset)
        {
            // バス名は cli の引数文字列へ埋め込むため、引数列を壊す文字を先に弾く
            // (エスケープはプラットフォーム別の引数パース規則差があるため、拒否が安全)。
            var busName = asset.OutputBusName;
            if (!string.IsNullOrEmpty(busName) && asset.OutputMixerAsset != null &&
                busName.IndexOfAny(new[] { '"', '\\' }) >= 0)
            {
                LastError = $"バス名に使用できない文字 (\" または \\) が含まれています: {busName}";
                return null;
            }

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
                    plan.ClipStreaming = ShouldStream(clip, plan.ClipAbsPath);
                    // streaming はフルデコードしないため即応答する。timeout は既定で十分。
                    plan.ClipLoadTimeoutMs =
                        plan.ClipStreaming ? 10_000 : LoadTimeoutFor(plan.ClipAbsPath);
                    plan.ClipJson = NeziaPreviewJson.BuildClipJson(asset);
                    return plan;
                }
                case NeziaRandomContainer container:
                {
                    var key = AssetDatabase.GetAssetPath(container);
                    var children = new List<(string, string, int)>();
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
                                var abs = Path.GetFullPath(cp);
                                children.Add((cp, abs, LoadTimeoutFor(abs)));
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

        /// <summary>Stop 世代がずれた = 開始後に Stop が押された。</summary>
        private static bool IsCancelled(PlayPlan plan) =>
            plan.StopGeneration != Volatile.Read(ref _stopGeneration);

        /// <summary>プランをバックグラウンドで実行する (cli 呼び出しのみ)。</summary>
        private static void Execute(PlayPlan plan)
        {
            if (!EnsureDaemon())
            {
                return;
            }
            if (IsCancelled(plan))
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
            var buffer = EnsureBuffer(
                plan.ClipAssetPath, plan.ClipAbsPath, plan.ClipLoadTimeoutMs, plan.ClipStreaming);
            if (buffer == null)
            {
                return;
            }
            // ロードは有用なので Stop 後も完了させてキャッシュするが、
            // 再生発行だけは Stop の意図を尊重して中止する。
            if (IsCancelled(plan))
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
            if (IsCancelled(plan))
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

        private static string EnsureBuffer(
            string assetPath, string absolutePath, int timeoutMs, bool streaming)
        {
            lock (CacheLock)
            {
                if (BufferCache.TryGetValue(assetPath, out var cached))
                {
                    return cached;
                }
            }
            var flags = streaming ? " --streaming" : string.Empty;
            var response = NeziaCliClient.Run($"load \"{absolutePath}\"{flags}", timeoutMs);
            if (!response.ok)
            {
                LastError = response.ErrorText;
                return null;
            }
            lock (CacheLock)
            {
                BufferCache[assetPath] = response.buffer;
            }
            return response.buffer;
        }

        private static string EnsureContainer(PlayPlan plan)
        {
            lock (CacheLock)
            {
                if (ContainerCache.TryGetValue(plan.ContainerKey, out var cached))
                {
                    return cached.Handle;
                }
            }

            var buffers = new List<string>();
            var childPaths = new string[plan.Children.Count];
            for (var i = 0; i < plan.Children.Count; i++)
            {
                var (childPath, childAbs, timeoutMs) = plan.Children[i];
                // コンテナ子は常に静的ロード (PlayPlan.ClipStreaming の doc コメント参照)。
                var buffer = EnsureBuffer(childPath, childAbs, timeoutMs, streaming: false);
                if (buffer == null)
                {
                    return null;
                }
                buffers.Add(buffer);
                childPaths[i] = childPath;
            }

            var response = NeziaCliClient.Run($"container create {string.Join(" ", buffers)}");
            if (!response.ok)
            {
                LastError = response.ErrorText;
                return null;
            }
            lock (CacheLock)
            {
                ContainerCache[plan.ContainerKey] = new CachedContainer
                {
                    Handle = response.container,
                    Children = childPaths,
                };
            }
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
            // Editor PID をファイル名に混ぜる: Unity を複数インスタンス起動した場合に
            // 同じ temp ファイルを取り合って他プロジェクトのパラメータで鳴るのを防ぐ。
            var path = Path.Combine(dir, $"{prefix}-{Process.GetCurrentProcess().Id}.json");
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
