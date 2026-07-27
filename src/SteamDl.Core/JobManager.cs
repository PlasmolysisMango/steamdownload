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
        public string GameName { get; set; }
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

    sealed class LibraryGameItem
    {
        public uint AppId { get; set; }
        public string Name { get; set; } = "";
        public string InstallDir { get; set; } = "";
        public ulong SizeBytes { get; set; }
        public bool IsDownloaded { get; set; }
        public string DownloadedAt { get; set; } = "";
    }

    sealed class LibrarySyncState
    {
        public string Username { get; set; } = "";
        public string State { get; set; } = "idle";
        public string Message { get; set; } = "尚未同步游戏库";
        public string SyncMode { get; set; } = "full";
        public string LastSyncAt { get; set; } = "";
        public int LicenseCount { get; set; }
        public int PackageCount { get; set; }
        public int ResolvedPackageCount { get; set; }
        public int CandidateAppCount { get; set; }
        public int ScannedAppCount { get; set; }
        public HashSet<uint> KnownCandidateAppIds { get; } = [];
        public List<LibraryGameItem> Items { get; } = [];
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
        bool _pauseRequested;
        bool _accountStoreLoaded;
        LoginState _login = new();
        LibrarySyncState _librarySync = new();
        string _lastLoginInput = "";
        DateTime _lastLoginInputAtUtc = DateTime.MinValue;
        bool _lastLoginInputAutoReused;

        JobManager()
        {
            var relay = ConsoleRelay.Instance;
            relay.LineWritten += AppendLog;
            relay.InputRequested += prompt =>
            {
                string autoAnswer = null;
                string autoLabel = null;
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
                        if (CanAutoReuseLoginInput(label))
                        {
                            autoAnswer = _lastLoginInput;
                            autoLabel = label;
                            _lastLoginInputAutoReused = true;
                        }
                        else
                        {
                            _login.State = "waiting_input";
                            _login.Prompt = label;
                            _login.PromptSecret = secret;
                        }
                    }
                }

                if (autoAnswer != null)
                {
                    AppendLoginLog(autoLabel + " ****** (自动复用上次验证码)");
                    Task.Run(() => relay.SupplyInput(autoAnswer));
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

            FillInstallDirNameFromLibraryCache(request);

            lock (_sync)
            {
                if (_busy)
                {
                    error = "已有任务在进行中";
                    return false;
                }

                var outputDir = ResolveOutputDir(request);
                job = _store.CreateJob(request, outputDir);
                StartJobLocked(job, resetProgress: true);
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

                if (job.State == "paused")
                {
                    StartJobLocked(job, resetProgress: false);
                }
                else
                {
                    StartJobLocked(job, resetProgress: true);
                }
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
                StartJobLocked(job, resetProgress: false);
            }
        }

        void StartJobLocked(JobRecord job, bool resetProgress = true)
        {
            _busy = true;
            _cancelRequested = false;
            _pauseRequested = false;
            _currentJobId = job.JobId;
            _store.UpdateState(job.JobId, "starting", percent: resetProgress ? 0 : null, progressText: resetProgress ? "" : null, error: "", prompt: "", promptSecret: false);
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

        public bool Pause(string jobId, out string error)
        {
            error = null;
            lock (_sync)
            {
                if (string.IsNullOrWhiteSpace(jobId))
                {
                    error = "任务不存在";
                    return false;
                }

                var job = _store.GetJob(jobId);
                if (job == null)
                {
                    error = "任务不存在";
                    return false;
                }

                if (jobId == _currentJobId && _busy)
                {
                    _pauseRequested = true;
                    _store.AddLog(jobId, "用户暂停任务，已停止当前下载会话；继续时会从已下载文件恢复。", MaxLogLines);
                    _store.UpdateState(jobId, "paused", error: "");
                    ConsoleRelay.Instance.SupplyInput(string.Empty);
                    Task.Run(ContentDownloader.ShutdownSteam3);
                    return true;
                }

                if (job.State is "queued" or "starting" or "running" or "waiting_input" or "interrupted" or "error" or "cancelled")
                {
                    _store.UpdateState(jobId, "paused", error: "");
                    return true;
                }

                error = "当前任务不能暂停";
                return false;
            }
        }

        public bool Resume(string jobId, out string error)
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

                if (job.State != "paused")
                {
                    error = "只有暂停中的任务可以继续";
                    return false;
                }

                _store.AddLog(jobId, "继续暂停的任务…", MaxLogLines);
                StartJobLocked(job, resetProgress: false);
                return true;
            }
        }

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

        public Task<JsonObject> LibraryJsonAsync(string username)
        {
            StartLibrarySync(username, forceFullSync: false);
            return Task.FromResult(LibrarySyncStatusJson(username));
        }

        public JsonObject StartLibrarySync(string username) => StartLibrarySync(username, forceFullSync: false);

        public JsonObject StartLibrarySync(string username, bool forceFullSync)
        {
            EnsureAccountStoreLoaded();
            username = username?.Trim();
            if (string.IsNullOrWhiteSpace(username))
            {
                lock (_sync)
                {
                    _librarySync = new LibrarySyncState { State = "error", Message = "请先选择已登录账号" };
                    return LibrarySyncJsonLocked();
                }
            }

            if (!AccountSettingsStore.Instance.LoginTokens.TryGetValue(username, out var token) || string.IsNullOrWhiteSpace(token))
            {
                lock (_sync)
                {
                    _librarySync = new LibrarySyncState { Username = username, State = "error", Message = "该账号未登录或 refresh token 已失效，请先在账号页登录" };
                    return LibrarySyncJsonLocked();
                }
            }

            var syncMode = "full";
            List<LibraryGameItem> preservedItems = [];
            HashSet<uint> preservedCandidates = [];
            string lastSyncAt = "";
            lock (_sync)
            {
                if (!forceFullSync && (_librarySync.State != "running") &&
                    (!string.Equals(_librarySync.Username, username, StringComparison.OrdinalIgnoreCase) ||
                     (_librarySync.Items.Count == 0 && _librarySync.KnownCandidateAppIds.Count == 0)))
                {
                    LoadLibraryCacheLocked(username);
                }

                if (_librarySync.State == "running" && string.Equals(_librarySync.Username, username, StringComparison.OrdinalIgnoreCase))
                {
                    return LibrarySyncJsonLocked();
                }

                var canIncremental = !forceFullSync
                    && string.Equals(_librarySync.Username, username, StringComparison.OrdinalIgnoreCase)
                    && (_librarySync.Items.Count > 0 || _librarySync.KnownCandidateAppIds.Count > 0);
                syncMode = canIncremental ? "incremental" : "full";
                if (canIncremental)
                {
                    preservedItems = _librarySync.Items.Select(x => new LibraryGameItem { AppId = x.AppId, Name = x.Name, InstallDir = x.InstallDir, SizeBytes = x.SizeBytes, IsDownloaded = x.IsDownloaded, DownloadedAt = x.DownloadedAt }).ToList();
                    preservedCandidates = _librarySync.KnownCandidateAppIds.ToHashSet();
                    lastSyncAt = _librarySync.LastSyncAt;
                }

                _librarySync = new LibrarySyncState
                {
                    Username = username,
                    State = "running",
                    SyncMode = syncMode,
                    LastSyncAt = lastSyncAt,
                    Message = syncMode == "full" ? "正在全量同步 Steam 游戏库…" : "正在增量同步 Steam 游戏库…",
                };
                foreach (var appId in preservedCandidates) _librarySync.KnownCandidateAppIds.Add(appId);
                foreach (var item in preservedItems) _librarySync.Items.Add(item);
            }

            Task.Run(() => LibrarySyncAsync(username, token, syncMode));
            return LibrarySyncStatusJson(username);
        }

        public JsonObject LibrarySyncStatusJson(string username = null)
        {
            lock (_sync)
            {
                if (!string.IsNullOrWhiteSpace(username) && (_librarySync.State != "running") &&
                    (!string.Equals(_librarySync.Username, username.Trim(), StringComparison.OrdinalIgnoreCase) ||
                     (_librarySync.Items.Count == 0 && _librarySync.KnownCandidateAppIds.Count == 0)))
                {
                    LoadLibraryCacheLocked(username.Trim());
                }

                if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(_librarySync.Username) &&
                    !string.Equals(username.Trim(), _librarySync.Username, StringComparison.OrdinalIgnoreCase))
                {
                    return new JsonObject
                    {
                        ["username"] = username,
                        ["state"] = "idle",
                        ["items"] = new JsonArray(),
                        ["message"] = "尚未同步该账号的游戏库",
                    };
                }
                return LibrarySyncJsonLocked();
            }
        }

        async Task LibrarySyncAsync(string username, string token, string syncMode)
        {
            Steam3Session session = null;
            try
            {
                ConfigureLoginDownloader();
                AppendLoginLog($"开始{(syncMode == "full" ? "全量" : "增量")}同步 {username} 的 Steam 游戏库…");
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
                    SetLibraryError(username, "Steam 登录已失效，请在账号页重新登录后再同步游戏库");
                    return;
                }

                _ = Task.Run(session.TickCallbacks);
                for (var i = 0; i < 60 && session.Licenses == null; i++) await Task.Delay(250).ConfigureAwait(false);

                var packageIds = session.Licenses?.Where(x => x.AccessToken > 0).Select(x => x.PackageID).Distinct().ToList() ?? [];
                var licenseMessage = $"已读取 {session.Licenses?.Count ?? 0} 个 license；其中 {packageIds.Count} 个 package 带授权 token。license 是授权包，单个 package 可能包含多个 app/DLC/tool。";
                AppendLoginLog(licenseMessage);
                UpdateLibrarySync(username, s => { s.LicenseCount = session.Licenses?.Count ?? 0; s.PackageCount = packageIds.Count; s.Message = licenseMessage + " 正在解析 package…"; });

                if (packageIds.Count > 0) await session.RequestPackageInfo(packageIds).ConfigureAwait(false);

                var appIds = new SortedSet<uint>();
                foreach (var package in session.PackageInfo.Values.Where(x => x != null)) AddPackageAppIds(package.KeyValues["appids"], appIds);
                List<uint> appList;
                int skippedKnownCount;
                lock (_sync)
                {
                    var knownIds = syncMode == "incremental" && string.Equals(_librarySync.Username, username, StringComparison.OrdinalIgnoreCase)
                        ? _librarySync.KnownCandidateAppIds.ToHashSet()
                        : new HashSet<uint>();
                    skippedKnownCount = knownIds.Count;
                    appList = appIds.Where(x => !knownIds.Contains(x)).ToList();
                }
                var candidateMessage = syncMode == "incremental"
                    ? $"已从 {session.PackageInfo.Values.Count(x => x != null)}/{packageIds.Count} 个授权 package 展开 {appIds.Count} 个候选应用；已缓存 {skippedKnownCount} 个候选应用，本次检查 {appList.Count} 个新增候选。"
                    : $"已从 {session.PackageInfo.Values.Count(x => x != null)}/{packageIds.Count} 个授权 package 展开 {appList.Count} 个候选应用；候选应用会继续过滤，只显示 type=game 的游戏。";
                AppendLoginLog(candidateMessage);
                UpdateLibrarySync(username, s => { s.ResolvedPackageCount = session.PackageInfo.Values.Count(x => x != null); s.CandidateAppCount = appList.Count; s.ScannedAppCount = 0; s.Message = candidateMessage + " 正在读取游戏详情…"; });

                var appIdBatches = appList.Chunk(20).ToList();
                foreach (var batch in appIdBatches)
                {
                    await session.RequestAppInfo(batch).ConfigureAwait(false);
                    foreach (var appId in batch)
                    {
                        LibraryGameItem item = null;
                        if (session.AppInfo.TryGetValue(appId, out var appInfo) && TryCreateLibraryGameItem(appId, appInfo, out var game)) item = game;
                        UpdateLibrarySync(username, s =>
                        {
                            s.ScannedAppCount++;
                            s.KnownCandidateAppIds.Add(appId);
                            if (item != null && s.Items.All(x => x.AppId != item.AppId)) s.Items.Add(item);
                            s.Message = $"同步中：已检查 {s.ScannedAppCount}/{s.CandidateAppCount} 个应用，找到 {s.Items.Count} 个游戏。";
                        });
                    }
                }

                var doneMessage = $"游戏库{(syncMode == "full" ? "全量" : "增量")}同步完成，共显示 {LibrarySyncItemCount(username)} 个游戏。";
                AppendLoginLog(doneMessage);
                UpdateLibrarySync(username, s => { s.State = "done"; s.LastSyncAt = DateTime.UtcNow.ToString("O"); s.Message = doneMessage; });
                PersistLibraryCache(username);
            }
            catch (Exception ex)
            {
                AppendLoginLog("游戏库同步失败: " + ex.Message);
                SetLibraryError(username, "游戏库同步失败: " + ex.Message);
            }
            finally
            {
                try { session?.Disconnect(); }
                catch { }
            }
        }

        void LoadLibraryCacheLocked(string username)
        {
            var cache = _store.LoadLibraryCache(username);
            if (cache.Items.Count == 0 && cache.CandidateAppIds.Count == 0) return;
            _librarySync = new LibrarySyncState
            {
                Username = username,
                State = "done",
                SyncMode = "incremental",
                LastSyncAt = cache.LastSyncAt,
                CandidateAppCount = cache.CandidateAppIds.Count,
                ScannedAppCount = cache.CandidateAppIds.Count,
                Message = $"已加载上次同步的游戏库，共 {cache.Items.Count} 个游戏。",
            };
            foreach (var appId in cache.CandidateAppIds) _librarySync.KnownCandidateAppIds.Add(appId);
            foreach (var item in cache.Items) _librarySync.Items.Add(item);
        }

        void PersistLibraryCache(string username)
        {
            List<LibraryGameItem> items;
            HashSet<uint> candidates;
            string lastSyncAt;
            lock (_sync)
            {
                if (!string.Equals(_librarySync.Username, username, StringComparison.OrdinalIgnoreCase)) return;
                items = _librarySync.Items.Select(x => new LibraryGameItem { AppId = x.AppId, Name = x.Name, InstallDir = x.InstallDir, SizeBytes = x.SizeBytes, IsDownloaded = x.IsDownloaded, DownloadedAt = x.DownloadedAt }).ToList();
                candidates = _librarySync.KnownCandidateAppIds.ToHashSet();
                lastSyncAt = _librarySync.LastSyncAt;
            }
            _store.SaveLibraryCache(username, items, candidates, lastSyncAt);
        }

        int LibrarySyncItemCount(string username)
        {
            lock (_sync)
            {
                return string.Equals(_librarySync.Username, username, StringComparison.OrdinalIgnoreCase) ? _librarySync.Items.Count : 0;
            }
        }

        void UpdateLibrarySync(string username, Action<LibrarySyncState> update)
        {
            lock (_sync)
            {
                if (!string.Equals(_librarySync.Username, username, StringComparison.OrdinalIgnoreCase)) return;
                update(_librarySync);
            }
        }

        void SetLibraryError(string username, string message)
        {
            UpdateLibrarySync(username, s => { s.State = "error"; s.Message = message; });
        }

        JsonObject LibrarySyncJsonLocked()
        {
            var items = new JsonArray();
            foreach (var item in _librarySync.Items.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(new JsonObject
                {
                    ["app_id"] = item.AppId.ToString(),
                    ["id"] = item.AppId.ToString(),
                    ["name"] = item.Name,
                    ["installdir"] = item.InstallDir,
                    ["install_dir"] = item.InstallDir,
                    ["size_bytes"] = item.SizeBytes,
                    ["size_text"] = item.SizeBytes > 0 ? FormatBytes(item.SizeBytes) : "",
                    ["is_downloaded"] = item.IsDownloaded,
                    ["downloaded_at"] = item.DownloadedAt,
                    ["header_image"] = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{item.AppId}/header.jpg",
                });
            }

            var progress = _librarySync.CandidateAppCount <= 0 ? 0 : Math.Min(100, _librarySync.ScannedAppCount * 100 / _librarySync.CandidateAppCount);
            return new JsonObject
            {
                ["username"] = _librarySync.Username,
                ["state"] = _librarySync.State,
                ["sync_mode"] = _librarySync.SyncMode,
                ["last_sync_at"] = _librarySync.LastSyncAt,
                ["items"] = items,
                ["license_count"] = _librarySync.LicenseCount,
                ["package_count"] = _librarySync.PackageCount,
                ["resolved_package_count"] = _librarySync.ResolvedPackageCount,
                ["app_count"] = _librarySync.CandidateAppCount,
                ["scanned_app_count"] = _librarySync.ScannedAppCount,
                ["item_count"] = _librarySync.Items.Count,
                ["progress"] = progress,
                ["details_pending"] = _librarySync.State == "running",
                ["message"] = _librarySync.Message,
            };
        }

        static bool TryCreateLibraryGameItem(uint appId, SteamApps.PICSProductInfoCallback.PICSProductInfo appInfo, out LibraryGameItem item)
        {
            item = null;
            if (appInfo == null) return false;
            var type = appInfo.KeyValues["common"]["type"].AsString() ?? string.Empty;
            if (!string.Equals(type, "game", StringComparison.OrdinalIgnoreCase)) return false;
            var name = appInfo.KeyValues["common"]["name"].AsString();
            if (string.IsNullOrWhiteSpace(name)) name = $"App {appId}";
            item = new LibraryGameItem
            {
                AppId = appId,
                Name = name,
                InstallDir = appInfo.KeyValues["config"]["installdir"].AsString() ?? string.Empty,
                SizeBytes = TryReadAppSizeBytes(appInfo.KeyValues),
            };
            return true;
        }
        static ulong TryReadAppSizeBytes(KeyValue root)
        {
            if (root == null || root == KeyValue.Invalid) return 0;
            var direct = TryReadUInt64(root["common"]["size"])
                ?? TryReadUInt64(root["common"]["size_bytes"])
                ?? TryReadUInt64(root["extended"]["size"])
                ?? TryReadUInt64(root["extended"]["size_bytes"]);
            if (direct.HasValue) return direct.Value;

            ulong total = 0;
            var depots = root["depots"];
            if (depots == null || depots == KeyValue.Invalid) return 0;
            foreach (var depot in depots.Children)
            {
                var size = TryReadUInt64(depot["maxsize"])
                    ?? TryReadUInt64(depot["size"])
                    ?? TryReadUInt64(depot["download_size"]);
                if (size.HasValue) total += size.Value;
            }
            return total;
        }

        static ulong? TryReadUInt64(KeyValue value)
        {
            if (value == null || value == KeyValue.Invalid) return null;
            var text = value.AsString();
            if (ulong.TryParse(text, out var parsed)) return parsed;
            return null;
        }

        static void AddPackageAppIds(KeyValue node, ISet<uint> appIds)
        {
            if (node == null || node == KeyValue.Invalid) return;
            foreach (var child in node.Children)
            {
                var appId = child.AsUnsignedInteger();
                if (appId > 0) appIds.Add(appId);
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

                ClearLastLoginInputLocked();
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
                RememberLastLoginInputLocked(prompt, answer);
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

        bool CanAutoReuseLoginInput(string prompt)
        {
            if (string.IsNullOrWhiteSpace(_lastLoginInput) || _lastLoginInputAutoReused) return false;
            if ((DateTime.UtcNow - _lastLoginInputAtUtc) > TimeSpan.FromSeconds(45)) return false;
            if (!IsGuardPrompt(prompt) || PromptSaysPreviousCodeWrong(prompt)) return false;
            return true;
        }

        void RememberLastLoginInputLocked(string prompt, string answer)
        {
            if (!IsGuardPrompt(prompt) || PromptSaysPreviousCodeWrong(prompt) || string.IsNullOrWhiteSpace(answer)) return;
            _lastLoginInput = answer.Trim();
            _lastLoginInputAtUtc = DateTime.UtcNow;
            _lastLoginInputAutoReused = false;
        }

        void ClearLastLoginInputLocked()
        {
            _lastLoginInput = "";
            _lastLoginInputAtUtc = DateTime.MinValue;
            _lastLoginInputAutoReused = false;
        }

        static bool IsGuardPrompt(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return false;
            return prompt.Contains("guard", StringComparison.OrdinalIgnoreCase)
                || prompt.Contains("2 factor", StringComparison.OrdinalIgnoreCase)
                || prompt.Contains("2-factor", StringComparison.OrdinalIgnoreCase)
                || prompt.Contains("auth code", StringComparison.OrdinalIgnoreCase)
                || prompt.Contains("authenticator", StringComparison.OrdinalIgnoreCase)
                || prompt.Contains("authentication code", StringComparison.OrdinalIgnoreCase)
                || prompt.Contains("验证码");
        }

        static bool PromptSaysPreviousCodeWrong(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return false;
            return prompt.Contains("incorrect", StringComparison.OrdinalIgnoreCase)
                || prompt.Contains("wrong", StringComparison.OrdinalIgnoreCase)
                || prompt.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                || prompt.Contains("错误")
                || prompt.Contains("无效");
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
                    ClearLastLoginInputLocked();
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
                    ClearLastLoginInputLocked();
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

                if (_pauseRequested)
                {
                    _store.UpdateState(jobId, "paused", error: "");
                }
                else if (_cancelRequested)
                {
                    _store.UpdateState(jobId, "cancelled");
                }
                else
                {
                    _store.UpdateState(jobId, "done", percent: 100, progressText: "已保存到: " + job.OutputDir);
                    _store.MarkDownloaded(job);
                    MarkLibraryGameDownloaded(job);
                }
            }
            catch (OperationCanceledException)
            {
                _store.UpdateState(jobId, _pauseRequested ? "paused" : "cancelled");
            }
            catch (Exception ex)
            {
                if (_pauseRequested)
                {
                    _store.UpdateState(jobId, "paused", error: "");
                }
                else
                {
                    AppendLog(ex.Message);
                    _store.UpdateState(jobId, _cancelRequested ? "cancelled" : "error", error: ex.Message);
                }
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
                    _pauseRequested = false;
                }
            }
        }

        void MarkLibraryGameDownloaded(JobRecord job)
        {
            if (job?.Kind != "app" || string.IsNullOrWhiteSpace(job.Username) || !uint.TryParse(job.ItemId, out var appId)) return;
            lock (_sync)
            {
                if (!string.Equals(_librarySync.Username, job.Username.Trim(), StringComparison.OrdinalIgnoreCase)) return;
                var item = _librarySync.Items.FirstOrDefault(x => x.AppId == appId);
                if (item == null) return;
                item.IsDownloaded = true;
                item.DownloadedAt = DateTime.UtcNow.ToString("O");
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

        static void FillInstallDirNameFromLibraryCache(DownloadRequest request)
        {
            if (request == null || request.Kind != "app" || !uint.TryParse(request.Id, out var appId)) return;
            if (string.IsNullOrWhiteSpace(request.Username)) return;

            LibraryGameItem cached = null;
            lock (Instance._sync)
            {
                if (string.Equals(Instance._librarySync.Username, request.Username.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    cached = Instance._librarySync.Items.FirstOrDefault(x => x.AppId == appId);
                }
            }

            if (cached == null)
            {
                var cache = Instance._store.LoadLibraryCache(request.Username);
                cached = cache.Items.FirstOrDefault(x => x.AppId == appId);
            }

            if (!string.IsNullOrWhiteSpace(cached?.Name) && string.IsNullOrWhiteSpace(request.GameName))
            {
                request.GameName = cached.Name;
            }

            if (!string.IsNullOrWhiteSpace(cached?.InstallDir))
            {
                request.InstallDirName = cached.InstallDir;
            }
            else if (string.IsNullOrWhiteSpace(request.InstallDirName) && !string.IsNullOrWhiteSpace(cached?.Name))
            {
                request.InstallDirName = cached.Name;
            }
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
                GameName = job.Name,
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
