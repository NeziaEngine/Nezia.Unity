using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Nezia.Unity.Editor.Preview
{
    /// <summary>
    /// IP-6: <c>nezia-cli</c> を <see cref="Process"/> 起動して stdout の 1 行 JSON を読む
    /// 薄いクライアント。gRPC / protobuf 依存は cli↔daemon 間に閉じており、
    /// Editor 側の契約は「サブコマンド + stdout の 1 行 JSON」のみ
    /// (nezia-core docs/design/cli/CONCEPT.md)。
    /// </summary>
    internal static class NeziaCliClient
    {
        private const string CliPathPrefsKey = "Nezia.Preview.CliPath";
        private const string DaemonPathPrefsKey = "Nezia.Preview.DaemonPath";

        /// <summary>cli 呼び出しのデフォルトタイムアウト (ms)。load は大きめに取る。</summary>
        private const int DefaultTimeoutMs = 10_000;

        // ─── バイナリ解決 ─────────────────────────────────────────

        /// <summary>
        /// nezia-cli のパスを解決する。優先順:
        /// 1. EditorPrefs (このマシンでの明示指定)
        /// 2. パッケージ同梱 <c>Editor/Bin/{platform}/</c> (リリース配布経路)
        /// 3. 開発用フォールバック: プロジェクト隣接の nezia-core checkout
        /// </summary>
        internal static string ResolveCliPath() =>
            Resolve(CliPathPrefsKey, "nezia-cli", "nezia-cli/bin");

        /// <summary>nezia-daemon のパスを解決する (優先順は cli と同じ)。</summary>
        internal static string ResolveDaemonPath() =>
            Resolve(DaemonPathPrefsKey, "nezia-daemon", "target/debug");

        // ─── メインスレッド解決結果のキャッシュ ─────────────────────
        //
        // ResolveCliPath / ResolveDaemonPath は EditorPrefs / Application /
        // PackageInfo などメインスレッド専用 API に依存するため、バックグラウンド
        // スレッドから直接呼ぶと "can only be called from the main thread" で落ちる。
        // 非同期実行に入る前にメインスレッドで一度解決してここへ焼き、off-thread の
        // Run / daemon spawn はこのキャッシュだけを参照する。

        private static string _cachedCliPath;
        private static string _cachedDaemonPath;

        /// <summary>解決済み cli パス (キャッシュ)。<see cref="CacheResolvedPaths"/> 後に有効。</summary>
        internal static string CachedCliPath => _cachedCliPath;

        /// <summary>解決済み daemon パス (キャッシュ)。<see cref="CacheResolvedPaths"/> 後に有効。</summary>
        internal static string CachedDaemonPath => _cachedDaemonPath;

        /// <summary>
        /// cli / daemon パスをメインスレッドで解決してキャッシュする。非同期処理に
        /// 入る前に必ずメインスレッドから呼ぶこと。
        /// </summary>
        internal static void CacheResolvedPaths()
        {
            _cachedCliPath = ResolveCliPath();
            _cachedDaemonPath = ResolveDaemonPath();
        }

        /// <summary>EditorPrefs へバイナリパスを保存する (Settings UI / HelpBox から使用)。</summary>
        internal static void SetCliPath(string path)
        {
            EditorPrefs.SetString(CliPathPrefsKey, path);
            CacheResolvedPaths();
        }

        internal static void SetDaemonPath(string path)
        {
            EditorPrefs.SetString(DaemonPathPrefsKey, path);
            CacheResolvedPaths();
        }

        private static string Resolve(string prefsKey, string binaryName, string devSubdir)
        {
            var exe = Application.platform == RuntimePlatform.WindowsEditor
                ? binaryName + ".exe"
                : binaryName;

            var prefs = EditorPrefs.GetString(prefsKey, string.Empty);
            if (!string.IsNullOrEmpty(prefs) && File.Exists(prefs))
            {
                return prefs;
            }

            // パッケージ同梱 (Editor/Bin/{platform}/)。リリースパイプラインが配置する。
            var packageRoot = GetPackageRoot();
            if (packageRoot != null)
            {
                var platform = Application.platform switch
                {
                    RuntimePlatform.OSXEditor => "macOS",
                    RuntimePlatform.WindowsEditor => "Windows",
                    _ => "Linux",
                };
                var bundled = Path.Combine(packageRoot, "Editor", "Bin", platform, exe);
                if (File.Exists(bundled))
                {
                    return bundled;
                }
            }

            // 開発用フォールバック: {プロジェクト}/../nezia-core/{devSubdir}/{exe}
            var dev = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "..", "nezia-core", devSubdir, exe));
            if (File.Exists(dev))
            {
                return dev;
            }

            return null;
        }

        private static string GetPackageRoot()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                typeof(NeziaCliClient).Assembly);
            return info?.resolvedPath;
        }

        // ─── コマンド実行 ─────────────────────────────────────────

        /// <summary>cli の 1 行 JSON レスポンス。JsonUtility でパースする。</summary>
        [Serializable]
        internal sealed class Response
        {
            public bool ok;
            public string buffer;
            public string source;
            public string container;
            public string version;
            public int pid;
            public int port;
            public Error error;

            [Serializable]
            internal sealed class Error
            {
                public string code;
                public string msg;
            }

            /// <summary>失敗時の人間可読メッセージ。</summary>
            internal string ErrorText =>
                error == null ? "unknown error" : $"{error.code}: {error.msg}";
        }

        /// <summary>
        /// cli を 1 コマンド実行して stdout の 1 行 JSON をパースする。
        /// 失敗 (バイナリ不在 / タイムアウト / パース不能) は ok=false の Response に畳む。
        /// port discovery は常に <c>--parent-pid {Editor の PID}</c> で行う
        /// (daemon は Editor を親として spawn されるため)。
        /// </summary>
        internal static Response Run(string arguments, int timeoutMs = DefaultTimeoutMs)
        {
            // off-thread から呼ばれるためパス解決 (メインスレッド専用 API) はここでしない。
            // 呼び出し側がメインスレッドで CacheResolvedPaths() 済みである前提。
            var cli = _cachedCliPath;
            if (cli == null)
            {
                return Fail("CLI_NOT_FOUND",
                    "nezia-cli が見つかりません。Project Settings > Nezia か EditorPrefs でパスを指定してください。");
            }

            var editorPid = Process.GetCurrentProcess().Id;
            var psi = new ProcessStartInfo
            {
                FileName = cli,
                Arguments = $"--parent-pid {editorPid} {arguments}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            try
            {
                using var process = Process.Start(psi);

                // stdout / stderr は非同期タスクで排水する。同期 ReadToEnd() だと
                // (a) cli 無応答時にタイムアウトが効かず無限ブロックする
                // (b) 読んでいない stderr のパイプが詰まると cli と相互デッドロックする
                // の 2 つのハング経路があり、StopLast / StopAll 経由ではメインスレッドが
                // 固まるため、WaitForExit(timeout) を唯一の待ち点にする。
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch { /* 既に終了 */ }
                    return Fail("TIMEOUT", $"nezia-cli {arguments} timed out ({timeoutMs}ms)");
                }

                // プロセス終了後はパイプが閉じるので短時間で完了する (保険の 1 秒上限)。
                if (!Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 1000))
                {
                    return Fail("TIMEOUT", $"nezia-cli {arguments}: output drain timed out");
                }

                var line = FirstLine(stdoutTask.Result);
                if (string.IsNullOrEmpty(line))
                {
                    return Fail("EMPTY_OUTPUT", $"nezia-cli {arguments}: no output");
                }
                var response = JsonUtility.FromJson<Response>(line);
                if (response == null)
                {
                    return Fail("PARSE_ERROR", $"unparseable output: {line}");
                }
                return response;
            }
            catch (Exception e)
            {
                return Fail("PROCESS_ERROR", e.Message);
            }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            var newline = text.IndexOf('\n');
            return (newline < 0 ? text : text.Substring(0, newline)).Trim();
        }

        private static Response Fail(string code, string msg) => new()
        {
            ok = false,
            error = new Response.Error { code = code, msg = msg },
        };
    }
}
