using System;
using System.IO;

namespace SteamDl.Core
{
    public static class AppPaths
    {
        public static string DataDir { get; } = ResolveDataDir();

        public static string DatabasePath => Path.Combine(DataDir, "steamdl.db");

        public static string DefaultDownloadDir()
        {
            foreach (var path in new[] { "/sdcard/Download", "/storage/emulated/0/Download" })
            {
                if (Directory.Exists(path))
                {
                    return Path.Combine(path, "steamdl");
                }
            }

            return Path.Combine(DataDir, "downloads");
        }

        static string ResolveDataDir()
        {
            var explicitDir = Environment.GetEnvironmentVariable("STEAMDL_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(explicitDir))
            {
                Directory.CreateDirectory(explicitDir);
                return explicitDir;
            }

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
            {
                local = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            if (string.IsNullOrWhiteSpace(local))
            {
                local = AppContext.BaseDirectory;
            }

            var dir = Path.Combine(local, "steamdl");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
