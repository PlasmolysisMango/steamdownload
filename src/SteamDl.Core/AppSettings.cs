using System;
using System.IO;
using System.Text.Json.Nodes;

namespace SteamDl.Core
{
    public static class AppSettings
    {
        const string FileName = "settings.json";
        static readonly object Sync = new();
        static bool _loaded;
        static string _downloadDir;

        public static string SettingsPath => Path.Combine(AppDataDir(), FileName);

        public static string GetDownloadDir(string fallback)
        {
            EnsureLoaded();
            return string.IsNullOrWhiteSpace(_downloadDir) ? fallback : _downloadDir;
        }

        public static void SetDownloadDir(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("保存目录不能为空", nameof(path));
            }

            path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
            Directory.CreateDirectory(path);
            EnsureWritable(path);

            lock (Sync)
            {
                _downloadDir = path;
                SaveLocked();
            }
        }

        static void EnsureLoaded()
        {
            lock (Sync)
            {
                if (_loaded)
                {
                    return;
                }

                try
                {
                    if (File.Exists(SettingsPath))
                    {
                        var json = JsonNode.Parse(File.ReadAllText(SettingsPath))?.AsObject();
                        _downloadDir = json?["download_dir"]?.GetValue<string>();
                    }
                }
                catch
                {
                    _downloadDir = null;
                }

                _loaded = true;
            }
        }

        static void SaveLocked()
        {
            Directory.CreateDirectory(AppDataDir());
            var json = new JsonObject
            {
                ["download_dir"] = _downloadDir ?? string.Empty,
            };
            File.WriteAllText(SettingsPath, json.ToJsonString());
        }

        static string AppDataDir()
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                baseDir = AppContext.BaseDirectory;
            }

            return Path.Combine(baseDir, "SteamDl");
        }

        static void EnsureWritable(string directory)
        {
            var testFile = Path.Combine(directory, ".steamdl_write_test");
            File.WriteAllText(testFile, "ok");
            File.Delete(testFile);
        }
    }
}
