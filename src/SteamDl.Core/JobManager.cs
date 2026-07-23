// 下载任务管理器:单任务状态机,与前端 /api/status 状态契约保持一致
// (state/prompt/percent/log 等字段名与取值均对齐,前端零改动)。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DepotDownloader;

namespace SteamDl.Core
{
    public sealed class DownloadRequest
    {
        public string Kind { get; set; } = "app";     // app | workshop
        public string Id { get; set; }
        public string Username { get; set; }
        public bool Anonymous { get; set; }
        public string Os { get; set; } = "windows";   // windows | linux | any
        public string DepotId { get; set; }
        public string OutputDir { get; set; }
    }

    public sealed class JobManager
    {
        public static JobManager Instance { get; } = new();

        const int MaxLogLines = 400;

        readonly object _sync = new();
        readonly List<string> _log = [];
        string _state = "idle";
        string _kind = "";
        string _id = "";
        string _prompt = "";
        bool _promptSecret;
        double _percent;
        string _progressText = "";
        string _error = "";
        string _outputDir = "";
        bool _busy;
        bool _cancelRequested;
        bool _accountStoreLoaded;

        JobManager()
        {
            var relay = ConsoleRelay.Instance;
            relay.LineWritten += AppendLog;
            relay.InputRequested += prompt =>
            {
                lock (_sync)
                {
                    _state = "waiting_input";
                    _prompt = string.IsNullOrEmpty(prompt) ? "请输入:" : prompt;
                    _promptSecret = _prompt.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                                    _prompt.Contains("密码");
                }
            };
            relay.InputSatisfied += () =>
            {
                lock (_sync)
                {
                    if (_state == "waiting_input")
                    {
                        AppendLogLocked(_prompt + " ******");
                        _state = "running";
                        _prompt = "";
                    }
                }
            };
            Ansi.BytesProgress += (downloaded, total) =>
            {
                lock (_sync)
                {
                    if (total > 0)
                    {
                        _percent = Math.Min(100.0, downloaded * 100.0 / total);
                        _progressText = $"{FormatBytes(downloaded)} / {FormatBytes(total)}  ({_percent:0.0}%)";
                    }
                }
            };
        }

        public static string DefaultDownloadDir()
        {
            foreach (var path in new[] { "/sdcard/Download", "/storage/emulated/0/Download" })
            {
                if (Directory.Exists(path))
                {
                    return Path.Combine(path, "steamdl");
                }
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "steamdl");
        }

        /// <summary>启动任务;已有任务进行中时返回 false。</summary>
        public bool TryStart(DownloadRequest request, out string error)
        {
            lock (_sync)
            {
                if (_busy)
                {
                    error = "已有任务在进行中";
                    return false;
                }

                if (!ulong.TryParse(request.Id, out _))
                {
                    error = "无效的 AppID/物品 ID";
                    return false;
                }

                _busy = true;
                _cancelRequested = false;
                _state = "starting";
                _kind = request.Kind;
                _id = request.Id;
                _prompt = "";
                _promptSecret = false;
                _percent = 0;
                _progressText = "";
                _error = "";
                _log.Clear();
                _outputDir = Path.Combine(
                    string.IsNullOrWhiteSpace(request.OutputDir) ? DefaultDownloadDir() : request.OutputDir,
                    (request.Kind == "workshop" ? "workshop_" : "app_") + request.Id);
            }

            ConsoleRelay.Instance.DrainPendingInput();
            Task.Run(() => RunAsync(request));
            error = null;
            return true;
        }

        public bool SupplyInput(string answer)
        {
            lock (_sync)
            {
                if (_state != "waiting_input")
                {
                    return false;
                }
            }

            ConsoleRelay.Instance.SupplyInput(answer);
            return true;
        }

        public void Cancel()
        {
            lock (_sync)
            {
                if (!_busy)
                {
                    return;
                }

                _cancelRequested = true;
            }

            // 若正阻塞在输入上,先放行,再断开会话使下载流程尽快抛出异常终止
            ConsoleRelay.Instance.SupplyInput(string.Empty);
            Task.Run(ContentDownloader.ShutdownSteam3);
        }

        public JsonObject Status()
        {
            lock (_sync)
            {
                if (_state == "idle")
                {
                    return new JsonObject { ["state"] = "idle" };
                }

                var tail = _log.Count > 120 ? _log.GetRange(_log.Count - 120, 120) : _log;
                return new JsonObject
                {
                    ["state"] = _state,
                    ["kind"] = _kind,
                    ["id"] = _id,
                    ["prompt"] = _prompt,
                    ["prompt_secret"] = _promptSecret,
                    ["percent"] = _percent,
                    ["progress_text"] = _progressText,
                    ["log"] = string.Join('\n', tail),
                    ["error"] = _error,
                    ["output_dir"] = _outputDir,
                };
            }
        }

        async Task RunAsync(DownloadRequest request)
        {
            try
            {
                lock (_sync)
                {
                    _state = "running";
                }

                EnsureAccountStoreLoaded();
                Directory.CreateDirectory(_outputDir);
                ConfigureDownloader(request, _outputDir);

                var username = request.Anonymous || string.IsNullOrWhiteSpace(request.Username)
                    ? null
                    : request.Username.Trim();

                string password = null;
                if (username != null && !AccountSettingsStore.Instance.LoginTokens.ContainsKey(username))
                {
                    // 首次登录该账户:通过 Web UI 收集密码(不落盘,登录成功后只保存令牌)
                    password = ConsoleRelay.Instance.PromptAndRead($"Enter account password for \"{username}\":");
                    if (string.IsNullOrEmpty(password))
                    {
                        throw new InvalidOperationException("未提供密码,任务终止");
                    }
                }

                AppendLog(username == null ? "使用匿名账户登录(仅限免费内容)…" : $"登录账户 {username}…");

                if (!ContentDownloader.InitializeSteam3(username, password))
                {
                    throw new InvalidOperationException("Steam 登录失败,请检查账号密码/网络后重试");
                }

                try
                {
                    if (request.Kind == "workshop")
                    {
                        var pubFileId = ulong.Parse(request.Id);
                        AppendLog("解析创意工坊物品所属 App…");
                        var appId = await AppInfoService.ResolveWorkshopAppIdAsync(pubFileId).ConfigureAwait(false);
                        AppendLog($"物品属于 App {appId},开始下载…");
                        await ContentDownloader.DownloadPubfileAsync(appId, pubFileId).ConfigureAwait(false);
                    }
                    else
                    {
                        var appId = uint.Parse(request.Id);
                        var depots = new List<(uint depotId, ulong manifestId)>();
                        if (uint.TryParse(request.DepotId, out var depotId) && depotId > 0)
                        {
                            depots.Add((depotId, ContentDownloader.INVALID_MANIFEST_ID));
                        }

                        var os = request.Os == "any" ? null : request.Os;
                        await ContentDownloader.DownloadAppAsync(
                            appId, depots, ContentDownloader.DEFAULT_BRANCH,
                            os, null, null, false, false).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ContentDownloader.ShutdownSteam3();
                }

                lock (_sync)
                {
                    if (_cancelRequested)
                    {
                        _state = "cancelled";
                    }
                    else
                    {
                        _state = "done";
                        _percent = 100;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                lock (_sync)
                {
                    _state = "cancelled";
                }
            }
            catch (Exception ex)
            {
                AppendLog(ex.Message);
                lock (_sync)
                {
                    if (_cancelRequested)
                    {
                        _state = "cancelled";
                    }
                    else
                    {
                        _state = "error";
                        _error = ex.Message;
                    }
                }
            }
            finally
            {
                lock (_sync)
                {
                    _busy = false;
                }
            }
        }

        void EnsureAccountStoreLoaded()
        {
            lock (_sync)
            {
                if (_accountStoreLoaded)
                {
                    return;
                }

                AccountSettingsStore.LoadFromFile("account.config");
                _accountStoreLoaded = true;
            }
        }

        static void ConfigureDownloader(DownloadRequest request, string installDir)
        {
            var cfg = ContentDownloader.Config;
            cfg.RememberPassword = true; // 只保存登录令牌(refresh token),不保存密码
            cfg.UseQrCode = false;
            cfg.SkipAppConfirmation = false; // 允许 Steam 手机 App 直接点确认登录
            cfg.DownloadManifestOnly = false;
            cfg.CellID = 0;
            cfg.MaxDownloads = 8;
            cfg.LoginID = null;
            cfg.InstallDirectory = installDir;
            cfg.UsingFileList = false;
            cfg.FilesToDownload = null;
            cfg.FilesToDownloadRegex = null;
            cfg.VerifyAll = false;
            cfg.BetaPassword = null;
            cfg.DownloadAllPlatforms = request.Os == "any";
            cfg.DownloadAllArchs = false;
            cfg.DownloadAllLanguages = false;
        }

        void AppendLog(string line)
        {
            lock (_sync)
            {
                AppendLogLocked(line);
            }
        }

        void AppendLogLocked(string line)
        {
            _log.Add(line);
            if (_log.Count > MaxLogLines)
            {
                _log.RemoveRange(0, _log.Count - MaxLogLines);
            }
        }

        static string FormatBytes(ulong bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double value = bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:0.0} {units[unit]}";
        }
    }
}
