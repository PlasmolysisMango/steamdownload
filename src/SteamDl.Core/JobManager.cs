// 下载任务管理器:SQLite 持久化单任务队列,兼容旧 /api/status 契约。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DepotDownloader;
using SteamKit2;

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
        public string InstallDirName { get; set; }
    }

    public sealed class LoginState
    {
        public string Username { get; set; }
        public string State { get; set; } = "idle";
        public string Prompt { get; set; } = "";
        public bool PromptSecret { get; set; }
        public string Error { get; set; } = "";
        public string Log { get; set; } = "";
        public bool RememberPassword { get; set; }
    }

    public sealed class JobManager
    {
        public static JobManager Instance { get; } = new();

        const int MaxLogLines = 400;

        readonly object _sync = new();
        readonly JobStore _store = JobStore.Instance;
        string _currentJobId;
        bool _busy;
        bool _cancelRequested;
        bool _accountStoreLoaded;
        LoginState _login = new();

        JobManager()
        {
            var relay = ConsoleRelay.Instance;
            relay.LineWritten += AppendLog;
            relay.InputRequested += prompt =>
            {
                lock (_sync)
                {
                    var label = string.IsNullOrEmpty(prompt) ? "请输入:" : prompt;
                    var secret = label.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                                 label.Contains("密码");
                    if (!string.IsNullOrWhiteSpace(_currentJobId))
                    {
                        _store.UpdateState(_currentJobId, "waiting_input", prompt: label, promptSecret: secret);
                        return;
                    }

                    if (_login.State is "running" or "waiting_input")
                    {
                        _login.State = "waiting_input";
                        _login.Prompt = label;
                        _login.PromptSecret = secret;
                    }
                }
            };
            relay.InputSatisfied += () =>
            {
                lock (_sync)
                {
                    if (!string.IsNullOrWhiteSpace(_currentJobId))
                    {
                        var job = _store.GetJob(_currentJobId);
                        if (job?.State == "waiting_input")
                        {
                            _store.AddLog(_currentJobId, job.Prompt + " ******", MaxLogLines);
                            _store.UpdateState(_currentJobId, "running", prompt: "", promptSecret: false);
                        }
                        return;
                    }

                    if (_login.State == "waiting_input")
                    {
                        AppendLoginLog(_login.Prompt + " ******");
                        _login.State = "running";
                        _login.Prompt = "";
                        _login.PromptSecret = false;
                    }
                }
            };
            Ansi.BytesProgress += (downloaded, total) =>
            {
                lock (_sync)
                {
                    if (total <= 0 || string.IsNullOrWhiteSpace(_currentJobId)) return;
                    var percent = Math.Min(100.0, downloaded * 100.0 / total);
                    var text = $"{FormatBytes(downloaded)} / {FormatBytes(total)}  ({percent:0.0}%)";
                    _store.UpdateState(_currentJobId, "running", percent: percent, progressText: text);
                }
            };

            RecoverInterruptedJobs();
        }

        public static string DefaultDownloadDir() => AppPaths.DefaultDownloadDir();

        public bool TryStart(DownloadRequest request, out string error)
        {
            var job = TryStart(request, out error, out _);
            return job;
        }

        public bool TryStart(DownloadRequest request, out string error, out JobRecord job)
        {
            job = null;
            error = Validate(request);
            if (error != null) return false;

            if (!request.Anonymous && !IsLoggedIn(request.Username))
            {
                if (TryStartSavedPasswordLogin(request.Username, out var loginError))
                {
                    error = "登录已失效，已自动尝试重新登录。请到账号页面完成可能需要的 Guard/2FA 验证后再开始下载。";
                    return false;
                }

                error = loginError ?? "请先在账号页面完成登录";
                return false;
            }

            lock (_sync)
            {
                if (_busy)
                {
                    error = "已有任务在进行中";
                    return false;
                }

                var outputDir = ResolveOutputDir(request);
                job = _store.CreateJob(request, outputDir);
                StartJobLocked(job);
                return true;
            }
        }

        public bool Retry(string jobId, out string error)
        {
            error = null;
            lock (_sync)
            {
                if (_busy)
                {
                    error = "已有任务在进行中";
                    return false;
                }

                var job = _store.GetJob(jobId);
                if (job == null)
                {
                    error = "任务不存在";
                    return false;
                }

                StartJobLocked(job);
                return true;
            }
        }

        public void RecoverInterruptedJobs()
        {
            lock (_sync)
            {
                if (_busy) return;
                var job = _store.GetRecoverableJobs().FirstOrDefault();
                if (job == null) return;
                _store.AddLog(job.JobId, "检测到可恢复任务,服务启动后自动继续…", MaxLogLines);
                StartJobLocked(job);
            }
        }

        void StartJobLocked(JobRecord job)
        {
            _busy = true;
            _cancelRequested = false;
            _currentJobId = job.JobId;
            _store.UpdateState(job.JobId, "starting", percent: 0, progressText: "", error: "", prompt: "", promptSecret: false);
            ConsoleRelay.Instance.DrainPendingInput();
            Task.Run(() => RunAsync(job.JobId));
        }

        public bool SupplyInput(string answer) => SupplyInput(_currentJobId, answer);

        public bool SupplyInput(string jobId, string answer)
        {
            var job = _store.GetJob(jobId);
            if (job?.State != "waiting_input") return false;
            ConsoleRelay.Instance.SupplyInput(answer);
            return true;
        }

        public void Cancel() => Cancel(_currentJobId);

        public bool Cancel(string jobId)
        {
            lock (_sync)
            {
                if (string.IsNullOrWhiteSpace(jobId)) return false;
                var job = _store.GetJob(jobId);
                if (job == null) return false;

                if (jobId == _currentJobId && _busy)
                {
                    _cancelRequested = true;
                    _store.UpdateState(jobId, "cancelled", error: "用户取消任务");
                    ConsoleRelay.Instance.SupplyInput(string.Empty);
                    Task.Run(ContentDownloader.ShutdownSteam3);
                    return true;
                }

                _store.UpdateState(jobId, "cancelled", error: "用户取消任务");
                return true;
            }
        }

        public JsonObject Status()
        {
            var jobId = _currentJobId;
            if (string.IsNullOrWhiteSpace(jobId))
            {
                var latest = _store.ListJobs(limit: 1).FirstOrDefault();
                if (latest == null) return new JsonObject { ["state"] = "idle" };
                return _store.ToJson(latest, includeLog: true);
            }

            var job = _store.GetJob(jobId);
            return job == null ? new JsonObject { ["state"] = "idle" } : _store.ToJson(job, includeLog: true);
        }

        public JsonArray JobsJson(string state = null)
        {
            var arr = new JsonArray();
            foreach (var job in _store.ListJobs(state))
            {
                arr.Add(_store.ToJson(job));
            }
            return arr;
        }

        public JsonObject JobJson(string jobId)
        {
            var job = _store.GetJob(jobId);
            return _store.ToJson(job, includeLog: true);
        }

        public List<string> Accounts()
        {
            EnsureAccountStoreLoaded();
            return AccountSettingsStore.Instance.LoginTokens.Keys.OrderBy(x => x).ToList();
        }

        public JsonArray AccountDetailsJson()
        {
            EnsureAccountStoreLoaded();
            var arr = new JsonArray();
            var records = _store.ListAccounts().ToDictionary(x => x.Username, StringComparer.OrdinalIgnoreCase);
            foreach (var username in Accounts())
            {
                if (!records.ContainsKey(username)) _store.TouchAccount(username);
            }

            records = _store.ListAccounts().ToDictionary(x => x.Username, StringComparer.OrdinalIgnoreCase);
            foreach (var record in records.Values.OrderBy(x => x.Username))
            {
                arr.Add(new JsonObject
                {
                    ["username"] = record.Username,
                    ["logged_in"] = AccountSettingsStore.Instance.LoginTokens.ContainsKey(record.Username),
                    ["remember_password"] = record.RememberPassword,
                    ["has_saved_password"] = record.HasSavedPassword,
                    ["last_used_at"] = record.LastUsedAt,
                });
            }
            return arr;
        }

        public async Task<JsonObject> LibraryJsonAsync(string username)
        {
            EnsureAccountStoreLoaded();
            username = username?.Trim();
            if (string.IsNullOrWhiteSpace(username))
            {
                return new JsonObject
                {
                    ["username"] = username ?? string.Empty,
                    ["items"] = new JsonArray(),
                    ["details_pending"] = false,
                    ["message"] = "请先选择已登录账号",
                };
            }

            if (!AccountSettingsStore.Instance.LoginTokens.TryGetValue(username, out var token) || string.IsNullOrWhiteSpace(token))
            {
                return new JsonObject
                {
                    ["username"] = username,
                    ["items"] = new JsonArray(),
                    ["details_pending"] = false,
                    ["message"] = "该账号未登录或 refresh token 已失效，请先在账号页登录",
                };
            }

            ConfigureLoginDownloader();
            Steam3Session session = null;
            try
            {
                session = new Steam3Session(new SteamUser.LogOnDetails
                {
                    Username = username,
                    ShouldRememberPassword = true,
                    AccessToken = token,
                    LoginID = ContentDownloader.Config.LoginID ?? 0x534B32,
                });

                if (!session.WaitForCredentials())
                {
                    AccountSettingsStore.Instance.LoginTokens.Remove(username);
                    AccountSettingsStore.Save();
                    return new JsonObject
                    {
                        ["username"] = username,
                        ["items"] = new JsonArray(),
                        ["details_pending"] = false,
                        ["message"] = "Steam 登录已失效，请在账号页重新登录后再同步游戏库",
                    };
                }

                _ = Task.Run(session.TickCallbacks);
                for (var i = 0; i < 60 && session.Licenses == null; i++)
                {
                    await Task.Delay(250).ConfigureAwait(false);
                }

                var packageIds = session.Licenses?.Select(x => x.PackageID).Distinct().ToList() ?? [];
                if (packageIds.Count > 0)
                {
                    await session.RequestPackageInfo(packageIds).ConfigureAwait(false);
                }

                var appIds = new SortedSet<uint>();
                foreach (var package in session.PackageInfo.Values.Where(x => x != null))
                {
                    foreach (var child in package.KeyValues["appids"].Children)
                    {
                        var appId = child.AsUnsignedInteger();
                        if (appId > 0) appIds.Add(appId);
                    }
                }

                foreach (var appId in appIds.Take(500))
                {
                    await session.RequestAppInfo(appId).ConfigureAwait(false);
                }

                var items = new JsonArray();
                foreach (var appId in appIds)
                {
                    var name = $"App {appId}";
                    var installDir = string.Empty;
                    if (session.AppInfo.TryGetValue(appId, out var appInfo) && appInfo != null)
                    {
                        var appName = appInfo.KeyValues["common"]["name"].AsString();
                        if (!string.IsNullOrWhiteSpace(appName)) name = appName;
                        installDir = appInfo.KeyValues["config"]["installdir"].AsString() ?? string.Empty;
                    }

                    items.Add(new JsonObject
                    {
                        ["app_id"] = appId.ToString(),
                        ["id"] = appId.ToString(),
                        ["name"] = name,
                        ["installdir"] = installDir,
                        ["install_dir"] = installDir,
                        ["header_image"] = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg",
                    });
                }

                return new JsonObject
                {
                    ["username"] = username,
                    ["items"] = items,
                    ["details_pending"] = appIds.Count > 500,
                    ["message"] = appIds.Count > 500 ? "已同步游戏库，前 500 个应用已加载名称，其余应用会显示 AppID。" : "游戏库同步完成",
                };
            }
            catch (Exception ex)
            {
                return new JsonObject
                {
                    ["username"] = username,
                    ["items"] = new JsonArray(),
                    ["details_pending"] = false,
                    ["message"] = "游戏库同步失败: " + ex.Message,
                };
            }
            finally
            {
                try { session?.Disconnect(); }
                catch { }
            }
        }

        public bool IsLoggedIn(string username)
        {
            EnsureAccountStoreLoaded();
            return !string.IsNullOrWhiteSpace(username) && AccountSettingsStore.Instance.LoginTokens.ContainsKey(username.Trim());
        }

        public LoginState LoginStatus()
        {
            lock (_sync)
            {
                return new LoginState
                {
                    Username = _login.Username,
                    State = _login.State,
                    Prompt = _login.Prompt,
                    PromptSecret = _login.PromptSecret,
                    Error = _login.Error,
                    Log = _login.Log,
                    RememberPassword = _login.RememberPassword,
                };
            }
        }

        public bool StartLogin(string username, string password, out string error) => StartLogin(username, password, false, out error);

        public bool StartLogin(string username, string password, bool rememberPassword, out string error)
        {
            error = null;
            username = username?.Trim();
            if (string.IsNullOrWhiteSpace(username))
            {
                error = "请输入 Steam 用户名";
                return false;
            }

            EnsureAccountStoreLoaded();
            if (!AccountSettingsStore.Instance.LoginTokens.ContainsKey(username) && string.IsNullOrEmpty(password))
            {
                error = "首次登录该账号需要输入密码";
                return false;
            }

            lock (_sync)
            {
                if (_busy)
                {
                    error = "下载任务运行中,请等待完成后再登录账号";
                    return false;
                }

                if (_login.State is "running" or "waiting_input")
                {
                    error = "已有账号登录流程正在进行";
                    return false;
                }

                _login = new LoginState
                {
                    Username = username,
                    State = "running",
                    Log = "开始登录 " + username + "…",
                    RememberPassword = rememberPassword,
                };
                ConsoleRelay.Instance.DrainPendingInput();
                Task.Run(() => LoginAsync(username, password, rememberPassword));
                return true;
            }
        }

        public bool SupplyLoginInput(string answer)
        {
            lock (_sync)
            {
                if (_login.State != "waiting_input") return false;
            }

            ConsoleRelay.Instance.SupplyInput(answer ?? string.Empty);
            return true;
        }

        public async Task<bool> SupplyLoginInputAndWaitAsync(string answer)
        {
            string prompt;
            lock (_sync)
            {
                if (_login.State != "waiting_input") return false;
                prompt = _login.Prompt ?? "";
            }

            ConsoleRelay.Instance.SupplyInput(answer ?? string.Empty);

            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(50).ConfigureAwait(false);
                lock (_sync)
                {
                    if (_login.State != "waiting_input" || !string.Equals(_login.Prompt ?? "", prompt, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return true;
        }

        public bool Relogin(string username, out string error)
        {
            error = null;
            username = username?.Trim();
            var password = _store.GetSavedPassword(username);
            if (string.IsNullOrEmpty(password))
            {
                error = "该账号没有保存密码，请输入密码重新登录";
                return false;
            }
            return StartLogin(username, password, true, out error);
        }

        bool TryStartSavedPasswordLogin(string username, out string error)
        {
            error = null;
            username = username?.Trim();
            var password = _store.GetSavedPassword(username);
            if (string.IsNullOrEmpty(password))
            {
                error = "请先在账号页面完成登录";
                return false;
            }

            return StartLogin(username, password, true, out error);
        }

        public bool Logout(string username)
        {
            EnsureAccountStoreLoaded();
            if (string.IsNullOrWhiteSpace(username)) return false;
            var removed = AccountSettingsStore.Instance.LoginTokens.Remove(username);
            AccountSettingsStore.Instance.GuardData.Remove(username);
            _store.ClearAccountSecret(username);
            if (removed) AccountSettingsStore.Save();
            return removed;
        }

        async Task LoginAsync(string username, string password, bool rememberPassword)
        {
            try
            {
                EnsureAccountStoreLoaded();
                ConfigureLoginDownloader();
                AppendLoginLog("正在连接 Steam…");
                if (!ContentDownloader.InitializeSteam3(username, string.IsNullOrEmpty(password) ? null : password))
                {
                    throw new InvalidOperationException("Steam 登录失败,请检查账号密码/Guard/网络后重试");
                }

                await Task.Delay(300).ConfigureAwait(false);
                ContentDownloader.ShutdownSteam3();
                EnsureAccountStoreLoaded();
                if (!AccountSettingsStore.Instance.LoginTokens.ContainsKey(username))
                {
                    throw new InvalidOperationException("登录已完成但未获得 refresh token,请确认账号授权状态后重试");
                }

                _store.TouchAccount(username);
                _store.SaveAccountPassword(username, password, rememberPassword);

                lock (_sync)
                {
                    _login.State = "done";
                    _login.Prompt = "";
                    _login.PromptSecret = false;
                    _login.Error = "";
                }
                AppendLoginLog(rememberPassword
                    ? "登录成功,refresh token 和加密密码已保存。后续可一键/自动重新登录。"
                    : "登录成功,refresh token 已保存。后续下载将复用该账号。 ");
            }
            catch (Exception ex)
            {
                AppendLoginLog(ex.Message);
                lock (_sync)
                {
                    _login.State = "error";
                    _login.Error = ex.Message;
                    _login.Prompt = "";
                    _login.PromptSecret = false;
                }
                ContentDownloader.ShutdownSteam3();
            }
        }

        async Task RunAsync(string jobId)
        {
            var job = _store.GetJob(jobId);
            if (job == null) return;
            var request = ToRequest(job);

            try
            {
                _store.UpdateState(jobId, "running", prompt: "", promptSecret: false, error: "");

                EnsureAccountStoreLoaded();
                Directory.CreateDirectory(job.OutputDir);
                ConfigureDownloader(request, job.OutputDir);

                var username = request.Anonymous || string.IsNullOrWhiteSpace(request.Username)
                    ? null
                    : request.Username.Trim();

                string password = null;
                if (username != null && !AccountSettingsStore.Instance.LoginTokens.ContainsKey(username))
                {
                    throw new InvalidOperationException("请先在账号页面完成登录");
                }

                AppendLog(username == null ? "使用匿名账户登录(仅限免费内容)…" : $"登录账户 {username}…");

                if (!ContentDownloader.InitializeSteam3(username, password))
                {
                    if (username != null && TryStartSavedPasswordLogin(username, out _))
                    {
                        throw new InvalidOperationException("Steam 登录已失效，已自动尝试重新登录。请到账号页面完成可能需要的 Guard/2FA 验证后重试下载。");
                    }
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

                if (_cancelRequested)
                {
                    _store.UpdateState(jobId, "cancelled");
                }
                else
                {
                    _store.UpdateState(jobId, "done", percent: 100, progressText: "已保存到: " + job.OutputDir);
                }
            }
            catch (OperationCanceledException)
            {
                _store.UpdateState(jobId, "cancelled");
            }
            catch (Exception ex)
            {
                AppendLog(ex.Message);
                _store.UpdateState(jobId, _cancelRequested ? "cancelled" : "error", error: ex.Message);
            }
            finally
            {
                lock (_sync)
                {
                    if (_currentJobId == jobId)
                    {
                        _currentJobId = null;
                    }
                    _busy = false;
                    _cancelRequested = false;
                }
            }
        }

        public void EnsureAccountStoreLoaded()
        {
            lock (_sync)
            {
                if (_accountStoreLoaded) return;
                AccountSettingsStore.LoadFromFile("account.config");
                _accountStoreLoaded = true;
            }
        }

        static void ConfigureLoginDownloader()
        {
            var cfg = ContentDownloader.Config;
            cfg.RememberPassword = true;
            cfg.UseQrCode = false;
            cfg.SkipAppConfirmation = false;
            cfg.CellID = 0;
            cfg.LoginID = null;
            cfg.InstallDirectory = AppPaths.DataDir;
        }

        static void ConfigureDownloader(DownloadRequest request, string installDir)
        {
            var cfg = ContentDownloader.Config;
            var settings = JobStore.Instance.GetSettings();
            cfg.RememberPassword = true;
            cfg.UseQrCode = false;
            cfg.SkipAppConfirmation = false;
            cfg.DownloadManifestOnly = false;
            cfg.CellID = 0;
            cfg.MaxDownloads = settings.MaxDownloads;
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
            if (string.IsNullOrEmpty(line)) return;
            var jobId = _currentJobId;
            if (!string.IsNullOrWhiteSpace(jobId))
            {
                _store.AddLog(jobId, line, MaxLogLines);
                return;
            }

            AppendLoginLog(line);
        }

        void AppendLoginLog(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (_sync)
            {
                if (_login.State == "idle") return;
                var lines = (_login.Log ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
                lines.Add(line);
                if (lines.Count > MaxLogLines) lines = lines.Skip(lines.Count - MaxLogLines).ToList();
                _login.Log = string.Join('\n', lines);
            }
        }

        static string Validate(DownloadRequest request)
        {
            if (request == null) return "请求为空";
            if (!ulong.TryParse(request.Id, out _)) return "无效的 AppID/物品 ID";
            if (request.Kind != "app" && request.Kind != "workshop") return "无效的任务类型";
            return null;
        }

        static string ResolveOutputDir(DownloadRequest request)
        {
            var baseDir = string.IsNullOrWhiteSpace(request.OutputDir)
                ? JobStore.Instance.GetSettings().DefaultDownloadDir
                : request.OutputDir;
            var dirName = request.Kind == "workshop"
                ? "workshop_" + request.Id
                : SafeDirectoryName(request.InstallDirName, "app_" + request.Id);
            return Path.Combine(baseDir, dirName);
        }

        static string SafeDirectoryName(string value, string fallback)
        {
            var name = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Replace('/', '_').Replace('\\', '_').Trim();
            return string.IsNullOrWhiteSpace(name) ? fallback : name;
        }

        static DownloadRequest ToRequest(JobRecord job)
        {
            return new DownloadRequest
            {
                Kind = job.Kind,
                Id = job.ItemId,
                Username = job.Username,
                Anonymous = job.Anonymous,
                Os = job.PlatformOs,
                DepotId = job.DepotId,
                OutputDir = Path.GetDirectoryName(job.OutputDir),
                InstallDirName = Path.GetFileName(job.OutputDir),
            };
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
