using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace SteamDl.Core
{
    public sealed class JobRecord
    {
        public string JobId { get; set; }
        public string Kind { get; set; } = "app";
        public string ItemId { get; set; }
        public string Name { get; set; } = "";
        public string Username { get; set; }
        public bool Anonymous { get; set; }
        public string PlatformOs { get; set; } = "windows";
        public string DepotId { get; set; }
        public string OutputDir { get; set; }
        public string State { get; set; } = "queued";
        public string Prompt { get; set; } = "";
        public bool PromptSecret { get; set; }
        public double Percent { get; set; }
        public string ProgressText { get; set; } = "";
        public string LastError { get; set; } = "";
        public bool AutoResume { get; set; } = true;
        public bool Downloaded { get; set; }
        public string CreatedAt { get; set; }
        public string UpdatedAt { get; set; }
    }

    public sealed class AppSettings
    {
        public string DefaultDownloadDir { get; set; } = AppPaths.DefaultDownloadDir();
        public string DefaultPlatformOs { get; set; } = "windows";
        public int MaxDownloads { get; set; } = 8;
        public bool AutoResume { get; set; } = true;
    }

    public sealed class AccountRecord
    {
        public string Username { get; set; }
        public string DisplayName { get; set; }
        public string LastUsedAt { get; set; }
        public bool RememberPassword { get; set; }
        public bool HasSavedPassword { get; set; }
        public string UpdatedAt { get; set; }
    }

    public sealed class JobStore
    {
        public static JobStore Instance { get; } = new();

        readonly object _sync = new();
        readonly string _connectionString;

        JobStore()
        {
            Batteries_V2.Init();
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = AppPaths.DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
            }.ToString();
            Initialize();
        }

        public string DatabasePath => AppPaths.DatabasePath;

        SqliteConnection OpenConnection()
        {
            var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
            return conn;
        }

        void Initialize()
        {
            lock (_sync)
            {
                using var conn = OpenConnection();
                Execute(conn, @"
CREATE TABLE IF NOT EXISTS jobs (
  job_id TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  item_id TEXT NOT NULL,
  name TEXT NOT NULL DEFAULT '',
  username TEXT,
  anonymous INTEGER NOT NULL,
  platform_os TEXT NOT NULL,
  depot_id TEXT,
  output_dir TEXT NOT NULL,
  state TEXT NOT NULL,
  prompt TEXT NOT NULL DEFAULT '',
  prompt_secret INTEGER NOT NULL DEFAULT 0,
  percent REAL NOT NULL DEFAULT 0,
  progress_text TEXT NOT NULL DEFAULT '',
  last_error TEXT NOT NULL DEFAULT '',
  auto_resume INTEGER NOT NULL DEFAULT 1,
  downloaded INTEGER NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS job_logs (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  job_id TEXT NOT NULL,
  line TEXT NOT NULL,
  created_at TEXT NOT NULL,
  FOREIGN KEY(job_id) REFERENCES jobs(job_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_job_logs_job_id_id ON job_logs(job_id, id);
CREATE INDEX IF NOT EXISTS idx_jobs_state_updated ON jobs(state, updated_at);
CREATE TABLE IF NOT EXISTS settings (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS accounts (
  username TEXT PRIMARY KEY,
  display_name TEXT,
  last_used_at TEXT,
  remember_password INTEGER NOT NULL DEFAULT 0,
  saved_password TEXT,
  updated_at TEXT
);
CREATE TABLE IF NOT EXISTS library_games (
  username TEXT NOT NULL,
  app_id INTEGER NOT NULL,
  name TEXT NOT NULL,
  install_dir TEXT NOT NULL DEFAULT '',
  size_bytes INTEGER NOT NULL DEFAULT 0,
  is_downloaded INTEGER NOT NULL DEFAULT 0,
  downloaded_at TEXT,
  updated_at TEXT NOT NULL,
  PRIMARY KEY(username, app_id)
);
CREATE TABLE IF NOT EXISTS library_candidates (
  username TEXT NOT NULL,
  app_id INTEGER NOT NULL,
  updated_at TEXT NOT NULL,
  PRIMARY KEY(username, app_id)
);
CREATE TABLE IF NOT EXISTS library_meta (
  username TEXT PRIMARY KEY,
  last_sync_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_library_games_username_name ON library_games(username, name);
");
                EnsureAccountColumn(conn, "remember_password", "INTEGER NOT NULL DEFAULT 0");
                EnsureAccountColumn(conn, "saved_password", "TEXT");
                EnsureAccountColumn(conn, "updated_at", "TEXT");
                EnsureJobColumn(conn, "name", "TEXT NOT NULL DEFAULT ''");
                EnsureJobColumn(conn, "downloaded", "INTEGER NOT NULL DEFAULT 0");
                EnsureLibraryGameColumn(conn, "size_bytes", "INTEGER NOT NULL DEFAULT 0");
                EnsureLibraryGameColumn(conn, "is_downloaded", "INTEGER NOT NULL DEFAULT 0");
                EnsureLibraryGameColumn(conn, "downloaded_at", "TEXT");
                EnsureDefaultSetting(conn, "default_download_dir", AppPaths.DefaultDownloadDir());
                EnsureDefaultSetting(conn, "default_platform_os", "windows");
                EnsureDefaultSetting(conn, "max_downloads", "8");
                EnsureDefaultSetting(conn, "auto_resume", "true");
            }
        }

        public JobRecord CreateJob(DownloadRequest request, string outputDir)
        {
            var now = Now();
            var job = new JobRecord
            {
                JobId = Guid.NewGuid().ToString("N"),
                Kind = string.IsNullOrWhiteSpace(request.Kind) ? "app" : request.Kind,
                ItemId = request.Id,
                Name = string.IsNullOrWhiteSpace(request.GameName) ? "" : request.GameName.Trim(),
                Username = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim(),
                Anonymous = request.Anonymous,
                PlatformOs = string.IsNullOrWhiteSpace(request.Os) ? "windows" : request.Os,
                DepotId = request.DepotId,
                OutputDir = outputDir,
                State = "queued",
                AutoResume = GetSettings().AutoResume,
                CreatedAt = now,
                UpdatedAt = now,
            };

            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO jobs (job_id, kind, item_id, name, username, anonymous, platform_os, depot_id, output_dir, state, prompt, prompt_secret, percent, progress_text, last_error, auto_resume, downloaded, created_at, updated_at)
VALUES ($job_id, $kind, $item_id, $name, $username, $anonymous, $platform_os, $depot_id, $output_dir, $state, '', 0, 0, '', '', $auto_resume, 0, $created_at, $updated_at);";
                Add(cmd, "$job_id", job.JobId);
                Add(cmd, "$kind", job.Kind);
                Add(cmd, "$item_id", job.ItemId);
                Add(cmd, "$name", job.Name ?? "");
                Add(cmd, "$username", (object)job.Username ?? DBNull.Value);
                Add(cmd, "$anonymous", job.Anonymous ? 1 : 0);
                Add(cmd, "$platform_os", job.PlatformOs);
                Add(cmd, "$depot_id", (object)job.DepotId ?? DBNull.Value);
                Add(cmd, "$output_dir", job.OutputDir);
                Add(cmd, "$state", job.State);
                Add(cmd, "$auto_resume", job.AutoResume ? 1 : 0);
                Add(cmd, "$created_at", job.CreatedAt);
                Add(cmd, "$updated_at", job.UpdatedAt);
                cmd.ExecuteNonQuery();
            }

            return job;
        }

        public List<JobRecord> ListJobs(string state = null, int limit = 100)
        {
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                if (string.IsNullOrWhiteSpace(state))
                {
                    cmd.CommandText = "SELECT * FROM jobs ORDER BY updated_at DESC LIMIT $limit";
                }
                else
                {
                    cmd.CommandText = "SELECT * FROM jobs WHERE state = $state ORDER BY updated_at DESC LIMIT $limit";
                    Add(cmd, "$state", state);
                }
                Add(cmd, "$limit", limit);
                using var reader = cmd.ExecuteReader();
                var jobs = new List<JobRecord>();
                while (reader.Read()) jobs.Add(ReadJob(reader));
                return jobs;
            }
        }

        public JobRecord GetJob(string jobId)
        {
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT * FROM jobs WHERE job_id = $job_id";
                Add(cmd, "$job_id", jobId);
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? ReadJob(reader) : null;
            }
        }

        public bool DeleteJob(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId)) return false;
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var tx = conn.BeginTransaction();
                using (var deleteLogs = conn.CreateCommand())
                {
                    deleteLogs.Transaction = tx;
                    deleteLogs.CommandText = "DELETE FROM job_logs WHERE job_id = $job_id";
                    Add(deleteLogs, "$job_id", jobId);
                    deleteLogs.ExecuteNonQuery();
                }
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM jobs WHERE job_id = $job_id";
                Add(cmd, "$job_id", jobId);
                var deleted = cmd.ExecuteNonQuery() > 0;
                tx.Commit();
                return deleted;
            }
        }

        public int DeleteAllJobs()
        {
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var tx = conn.BeginTransaction();
                using (var deleteLogs = conn.CreateCommand())
                {
                    deleteLogs.Transaction = tx;
                    deleteLogs.CommandText = "DELETE FROM job_logs";
                    deleteLogs.ExecuteNonQuery();
                }
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM jobs";
                var deleted = cmd.ExecuteNonQuery();
                tx.Commit();
                return deleted;
            }
        }

        public List<JobRecord> GetRecoverableJobs()
        {
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT * FROM jobs
WHERE auto_resume = 1 AND state IN ('queued','starting','running','waiting_input','interrupted','error')
ORDER BY updated_at ASC";
                using var reader = cmd.ExecuteReader();
                var jobs = new List<JobRecord>();
                while (reader.Read()) jobs.Add(ReadJob(reader));
                return jobs;
            }
        }

        public void UpdateState(string jobId, string state, double? percent = null, string progressText = null, string error = null, string prompt = null, bool? promptSecret = null)
        {
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
UPDATE jobs SET
  state = $state,
  percent = COALESCE($percent, percent),
  progress_text = COALESCE($progress_text, progress_text),
  last_error = COALESCE($last_error, last_error),
  prompt = COALESCE($prompt, prompt),
  prompt_secret = COALESCE($prompt_secret, prompt_secret),
  updated_at = $updated_at
WHERE job_id = $job_id";
                Add(cmd, "$job_id", jobId);
                Add(cmd, "$state", state);
                Add(cmd, "$percent", percent.HasValue ? percent.Value : DBNull.Value);
                Add(cmd, "$progress_text", progressText == null ? DBNull.Value : progressText);
                Add(cmd, "$last_error", error == null ? DBNull.Value : error);
                Add(cmd, "$prompt", prompt == null ? DBNull.Value : prompt);
                Add(cmd, "$prompt_secret", promptSecret.HasValue ? (promptSecret.Value ? 1 : 0) : DBNull.Value);
                Add(cmd, "$updated_at", Now());
                cmd.ExecuteNonQuery();
            }
        }

        public void AddLog(string jobId, string line, int maxLines = 400)
        {
            if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrEmpty(line)) return;
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var tx = conn.BeginTransaction();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO job_logs (job_id, line, created_at) VALUES ($job_id, $line, $created_at)";
                    Add(cmd, "$job_id", jobId);
                    Add(cmd, "$line", line);
                    Add(cmd, "$created_at", Now());
                    cmd.ExecuteNonQuery();
                }
                using (var trim = conn.CreateCommand())
                {
                    trim.Transaction = tx;
                    trim.CommandText = @"
DELETE FROM job_logs
WHERE job_id = $job_id AND id NOT IN (
  SELECT id FROM job_logs WHERE job_id = $job_id ORDER BY id DESC LIMIT $limit
)";
                    Add(trim, "$job_id", jobId);
                    Add(trim, "$limit", maxLines);
                    trim.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        public string GetLogText(string jobId, int limit = 120)
        {
            if (string.IsNullOrWhiteSpace(jobId)) return "";
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT line FROM (
  SELECT id, line FROM job_logs WHERE job_id = $job_id ORDER BY id DESC LIMIT $limit
) ORDER BY id ASC";
                Add(cmd, "$job_id", jobId);
                Add(cmd, "$limit", limit);
                using var reader = cmd.ExecuteReader();
                var lines = new List<string>();
                while (reader.Read()) lines.Add(reader.GetString(0));
                return string.Join('\n', lines);
            }
        }

        public AppSettings GetSettings()
        {
            lock (_sync)
            {
                using var conn = OpenConnection();
                string Get(string key, string fallback)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT value FROM settings WHERE key = $key";
                    Add(cmd, "$key", key);
                    return cmd.ExecuteScalar() as string ?? fallback;
                }

                return new AppSettings
                {
                    DefaultDownloadDir = Get("default_download_dir", AppPaths.DefaultDownloadDir()),
                    DefaultPlatformOs = Get("default_platform_os", "windows"),
                    MaxDownloads = int.TryParse(Get("max_downloads", "8"), out var n) ? Math.Max(1, n) : 8,
                    AutoResume = bool.TryParse(Get("auto_resume", "true"), out var b) ? b : true,
                };
            }
        }

        public AppSettings SaveSettings(AppSettings settings)
        {
            settings ??= new AppSettings();
            lock (_sync)
            {
                using var conn = OpenConnection();
                UpsertSetting(conn, "default_download_dir", string.IsNullOrWhiteSpace(settings.DefaultDownloadDir) ? AppPaths.DefaultDownloadDir() : settings.DefaultDownloadDir);
                UpsertSetting(conn, "default_platform_os", string.IsNullOrWhiteSpace(settings.DefaultPlatformOs) ? "windows" : settings.DefaultPlatformOs);
                UpsertSetting(conn, "max_downloads", Math.Max(1, settings.MaxDownloads).ToString());
                UpsertSetting(conn, "auto_resume", settings.AutoResume ? "true" : "false");
            }
            return GetSettings();
        }

        public List<AccountRecord> ListAccounts()
        {
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT username, display_name, last_used_at, remember_password, saved_password, updated_at FROM accounts ORDER BY username";
                using var reader = cmd.ExecuteReader();
                var accounts = new List<AccountRecord>();
                while (reader.Read()) accounts.Add(ReadAccount(reader));
                return accounts;
            }
        }

        public AccountRecord GetAccount(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT username, display_name, last_used_at, remember_password, saved_password, updated_at FROM accounts WHERE username = $username";
                Add(cmd, "$username", username.Trim());
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? ReadAccount(reader) : null;
            }
        }

        public void TouchAccount(string username, string displayName = null)
        {
            if (string.IsNullOrWhiteSpace(username)) return;
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO accounts (username, display_name, last_used_at, updated_at)
VALUES ($username, $display_name, $now, $now)
ON CONFLICT(username) DO UPDATE SET
  display_name = COALESCE(excluded.display_name, display_name),
  last_used_at = excluded.last_used_at,
  updated_at = excluded.updated_at";
                Add(cmd, "$username", username.Trim());
                Add(cmd, "$display_name", string.IsNullOrWhiteSpace(displayName) ? DBNull.Value : displayName);
                Add(cmd, "$now", Now());
                cmd.ExecuteNonQuery();
            }
        }

        public void SaveAccountPassword(string username, string password, bool remember)
        {
            if (string.IsNullOrWhiteSpace(username)) return;
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO accounts (username, remember_password, saved_password, updated_at)
VALUES ($username, $remember_password, $saved_password, $now)
ON CONFLICT(username) DO UPDATE SET
  remember_password = excluded.remember_password,
  saved_password = excluded.saved_password,
  updated_at = excluded.updated_at";
                Add(cmd, "$username", username.Trim());
                Add(cmd, "$remember_password", remember ? 1 : 0);
                Add(cmd, "$saved_password", remember && !string.IsNullOrEmpty(password) ? PasswordVault.Protect(password) : DBNull.Value);
                Add(cmd, "$now", Now());
                cmd.ExecuteNonQuery();
            }
        }

        public string GetSavedPassword(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT saved_password FROM accounts WHERE username = $username AND remember_password = 1";
                Add(cmd, "$username", username.Trim());
                var protectedPassword = cmd.ExecuteScalar() as string;
                return PasswordVault.Unprotect(protectedPassword);
            }
        }

        public void ClearAccountSecret(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return;
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE accounts SET remember_password = 0, saved_password = NULL, updated_at = $now WHERE username = $username";
                Add(cmd, "$username", username.Trim());
                Add(cmd, "$now", Now());
                cmd.ExecuteNonQuery();
            }
        }

        internal (List<LibraryGameItem> Items, HashSet<uint> CandidateAppIds, string LastSyncAt) LoadLibraryCache(string username)
        {
            var items = new List<LibraryGameItem>();
            var candidates = new HashSet<uint>();
            if (string.IsNullOrWhiteSpace(username)) return (items, candidates, "");
            username = username.Trim();
            lock (_sync)
            {
                using var conn = OpenConnection();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT app_id, name, install_dir, size_bytes, is_downloaded, downloaded_at FROM library_games WHERE username = $username ORDER BY name";
                    Add(cmd, "$username", username);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        items.Add(new LibraryGameItem
                        {
                            AppId = (uint)reader.GetInt64(0),
                            Name = reader.GetString(1),
                            InstallDir = reader.GetString(2),
                            SizeBytes = (ulong)Math.Max(0, reader.GetInt64(3)),
                            IsDownloaded = reader.GetInt32(4) != 0,
                            DownloadedAt = reader.IsDBNull(5) ? "" : reader.GetString(5),
                        });
                    }
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT app_id FROM library_candidates WHERE username = $username";
                    Add(cmd, "$username", username);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) candidates.Add((uint)reader.GetInt64(0));
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT last_sync_at FROM library_meta WHERE username = $username";
                    Add(cmd, "$username", username);
                    return (items, candidates, cmd.ExecuteScalar() as string ?? "");
                }
            }
        }

        internal void SaveLibraryCache(string username, IEnumerable<LibraryGameItem> items, IEnumerable<uint> candidateAppIds, string lastSyncAt)
        {
            if (string.IsNullOrWhiteSpace(username)) return;
            username = username.Trim();
            var now = Now();
            var itemList = (items ?? []).GroupBy(x => x.AppId).Select(g => g.First()).ToList();
            var candidateList = (candidateAppIds ?? []).Distinct().ToList();
            lock (_sync)
            {
                using var conn = OpenConnection();
                var existingDownloaded = new Dictionary<uint, (bool Downloaded, string DownloadedAt)>();
                using (var existing = conn.CreateCommand())
                {
                    existing.CommandText = "SELECT app_id, is_downloaded, downloaded_at FROM library_games WHERE username = $username";
                    Add(existing, "$username", username);
                    using var reader = existing.ExecuteReader();
                    while (reader.Read())
                    {
                        existingDownloaded[(uint)reader.GetInt64(0)] = (reader.GetInt32(1) != 0, reader.IsDBNull(2) ? "" : reader.GetString(2));
                    }
                }

                using var tx = conn.BeginTransaction();
                using (var deleteGames = conn.CreateCommand())
                {
                    deleteGames.Transaction = tx;
                    deleteGames.CommandText = "DELETE FROM library_games WHERE username = $username";
                    Add(deleteGames, "$username", username);
                    deleteGames.ExecuteNonQuery();
                }
                using (var deleteCandidates = conn.CreateCommand())
                {
                    deleteCandidates.Transaction = tx;
                    deleteCandidates.CommandText = "DELETE FROM library_candidates WHERE username = $username";
                    Add(deleteCandidates, "$username", username);
                    deleteCandidates.ExecuteNonQuery();
                }
                foreach (var item in itemList)
                {
                    existingDownloaded.TryGetValue(item.AppId, out var existingState);
                    var isDownloaded = item.IsDownloaded || existingState.Downloaded;
                    var downloadedAt = !string.IsNullOrWhiteSpace(item.DownloadedAt) ? item.DownloadedAt : existingState.DownloadedAt;
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT INTO library_games (username, app_id, name, install_dir, size_bytes, is_downloaded, downloaded_at, updated_at)
VALUES ($username, $app_id, $name, $install_dir, $size_bytes, $is_downloaded, $downloaded_at, $updated_at)";
                    Add(cmd, "$username", username);
                    Add(cmd, "$app_id", (long)item.AppId);
                    Add(cmd, "$name", string.IsNullOrWhiteSpace(item.Name) ? $"App {item.AppId}" : item.Name);
                    Add(cmd, "$install_dir", item.InstallDir ?? "");
                    Add(cmd, "$size_bytes", item.SizeBytes > long.MaxValue ? long.MaxValue : (long)item.SizeBytes);
                    Add(cmd, "$is_downloaded", isDownloaded ? 1 : 0);
                    Add(cmd, "$downloaded_at", string.IsNullOrWhiteSpace(downloadedAt) ? DBNull.Value : downloadedAt);
                    Add(cmd, "$updated_at", now);
                    cmd.ExecuteNonQuery();
                }
                foreach (var appId in candidateList)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO library_candidates (username, app_id, updated_at) VALUES ($username, $app_id, $updated_at)";
                    Add(cmd, "$username", username);
                    Add(cmd, "$app_id", (long)appId);
                    Add(cmd, "$updated_at", now);
                    cmd.ExecuteNonQuery();
                }
                using (var meta = conn.CreateCommand())
                {
                    meta.Transaction = tx;
                    meta.CommandText = @"INSERT INTO library_meta (username, last_sync_at)
VALUES ($username, $last_sync_at)
ON CONFLICT(username) DO UPDATE SET last_sync_at = excluded.last_sync_at";
                    Add(meta, "$username", username);
                    Add(meta, "$last_sync_at", string.IsNullOrWhiteSpace(lastSyncAt) ? now : lastSyncAt);
                    meta.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        public void MarkDownloaded(JobRecord job)
        {
            if (job == null) return;
            var now = Now();
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var tx = conn.BeginTransaction();
                using (var updateJob = conn.CreateCommand())
                {
                    updateJob.Transaction = tx;
                    updateJob.CommandText = "UPDATE jobs SET downloaded = 1, updated_at = $updated_at WHERE job_id = $job_id";
                    Add(updateJob, "$job_id", job.JobId);
                    Add(updateJob, "$updated_at", now);
                    updateJob.ExecuteNonQuery();
                }

                if (job.Kind == "app" && !string.IsNullOrWhiteSpace(job.Username) && uint.TryParse(job.ItemId, out var appId))
                {
                    using var updateLibrary = conn.CreateCommand();
                    updateLibrary.Transaction = tx;
                    updateLibrary.CommandText = @"INSERT INTO library_games (username, app_id, name, install_dir, size_bytes, is_downloaded, downloaded_at, updated_at)
VALUES ($username, $app_id, $name, $install_dir, 0, 1, $downloaded_at, $updated_at)
ON CONFLICT(username, app_id) DO UPDATE SET
  name = CASE WHEN excluded.name <> '' THEN excluded.name ELSE library_games.name END,
  install_dir = CASE WHEN excluded.install_dir <> '' THEN excluded.install_dir ELSE library_games.install_dir END,
  is_downloaded = 1,
  downloaded_at = excluded.downloaded_at,
  updated_at = excluded.updated_at";
                    Add(updateLibrary, "$username", job.Username.Trim());
                    Add(updateLibrary, "$app_id", (long)appId);
                    Add(updateLibrary, "$name", job.Name ?? "");
                    Add(updateLibrary, "$install_dir", Path.GetFileName(job.OutputDir) ?? "");
                    Add(updateLibrary, "$downloaded_at", now);
                    Add(updateLibrary, "$updated_at", now);
                    updateLibrary.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        public void ClearLibraryDownloaded(string username, uint appId)
        {
            if (string.IsNullOrWhiteSpace(username) || appId == 0) return;
            var now = Now();
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"UPDATE library_games
SET is_downloaded = 0,
    downloaded_at = NULL,
    updated_at = $updated_at
WHERE username = $username AND app_id = $app_id";
                Add(cmd, "$username", username.Trim());
                Add(cmd, "$app_id", (long)appId);
                Add(cmd, "$updated_at", now);
                cmd.ExecuteNonQuery();
            }
        }

        public JsonObject ToJson(JobRecord job, bool includeLog = false)
        {
            if (job == null) return null;
            var obj = new JsonObject
            {
                ["job_id"] = job.JobId,
                ["kind"] = job.Kind,
                ["id"] = job.ItemId,
                ["name"] = job.Name,
                ["title"] = string.IsNullOrWhiteSpace(job.Name) ? $"{job.Kind} {job.ItemId}" : job.Name,
                ["username"] = job.Username,
                ["anonymous"] = job.Anonymous,
                ["os"] = job.PlatformOs,
                ["depot"] = job.DepotId,
                ["output_dir"] = job.OutputDir,
                ["state"] = job.State,
                ["prompt"] = job.Prompt,
                ["prompt_secret"] = job.PromptSecret,
                ["percent"] = job.Percent,
                ["progress_text"] = job.ProgressText,
                ["error"] = job.LastError,
                ["auto_resume"] = job.AutoResume,
                ["downloaded"] = job.Downloaded,
                ["created_at"] = job.CreatedAt,
                ["updated_at"] = job.UpdatedAt,
            };
            if (includeLog) obj["log"] = GetLogText(job.JobId);
            return obj;
        }

        static void Execute(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        static void EnsureDefaultSetting(SqliteConnection conn, string key, string value)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO settings (key, value) VALUES ($key, $value)";
            Add(cmd, "$key", key);
            Add(cmd, "$value", value);
            cmd.ExecuteNonQuery();
        }

        static void EnsureJobColumn(SqliteConnection conn, string name, string definition)
        {
            try
            {
                Execute(conn, $"ALTER TABLE jobs ADD COLUMN {name} {definition}");
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
            {
                // column already exists
            }
        }

        static void EnsureAccountColumn(SqliteConnection conn, string name, string definition)
        {
            try
            {
                Execute(conn, $"ALTER TABLE accounts ADD COLUMN {name} {definition}");
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
            {
                // column already exists
            }
        }

        static void EnsureLibraryGameColumn(SqliteConnection conn, string name, string definition)
        {
            try
            {
                Execute(conn, $"ALTER TABLE library_games ADD COLUMN {name} {definition}");
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
            {
                // column already exists
            }
        }

        static void UpsertSetting(SqliteConnection conn, string key, string value)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            Add(cmd, "$key", key);
            Add(cmd, "$value", value);
            cmd.ExecuteNonQuery();
        }

        static void Add(SqliteCommand cmd, string name, object value)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        static string Now() => DateTimeOffset.UtcNow.ToString("O");

        static JobRecord ReadJob(SqliteDataReader reader)
        {
            return new JobRecord
            {
                JobId = reader.GetString(reader.GetOrdinal("job_id")),
                Kind = reader.GetString(reader.GetOrdinal("kind")),
                ItemId = reader.GetString(reader.GetOrdinal("item_id")),
                Name = reader.GetString(reader.GetOrdinal("name")),
                Username = ReadNullableString(reader, "username"),
                Anonymous = reader.GetInt32(reader.GetOrdinal("anonymous")) != 0,
                PlatformOs = reader.GetString(reader.GetOrdinal("platform_os")),
                DepotId = ReadNullableString(reader, "depot_id"),
                OutputDir = reader.GetString(reader.GetOrdinal("output_dir")),
                State = reader.GetString(reader.GetOrdinal("state")),
                Prompt = reader.GetString(reader.GetOrdinal("prompt")),
                PromptSecret = reader.GetInt32(reader.GetOrdinal("prompt_secret")) != 0,
                Percent = reader.GetDouble(reader.GetOrdinal("percent")),
                ProgressText = reader.GetString(reader.GetOrdinal("progress_text")),
                LastError = reader.GetString(reader.GetOrdinal("last_error")),
                AutoResume = reader.GetInt32(reader.GetOrdinal("auto_resume")) != 0,
                Downloaded = reader.GetInt32(reader.GetOrdinal("downloaded")) != 0,
                CreatedAt = reader.GetString(reader.GetOrdinal("created_at")),
                UpdatedAt = reader.GetString(reader.GetOrdinal("updated_at")),
            };
        }

        static AccountRecord ReadAccount(SqliteDataReader reader)
        {
            return new AccountRecord
            {
                Username = reader.GetString(reader.GetOrdinal("username")),
                DisplayName = ReadNullableString(reader, "display_name"),
                LastUsedAt = ReadNullableString(reader, "last_used_at"),
                RememberPassword = reader.GetInt32(reader.GetOrdinal("remember_password")) != 0,
                HasSavedPassword = !string.IsNullOrEmpty(ReadNullableString(reader, "saved_password")),
                UpdatedAt = ReadNullableString(reader, "updated_at"),
            };
        }

        static string ReadNullableString(SqliteDataReader reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }
    }
}
