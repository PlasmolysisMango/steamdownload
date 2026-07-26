// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using ProtoBuf;

namespace DepotDownloader
{
    [ProtoContract]
    class AccountSettingsStore
    {
        // Member 1 was a Dictionary<string, byte[]> for SentryData.

        [ProtoMember(2, IsRequired = false)]
        public ConcurrentDictionary<string, int> ContentServerPenalty { get; private set; }

        // Member 3 was a Dictionary<string, string> for LoginKeys.

        [ProtoMember(4, IsRequired = false)]
        public Dictionary<string, string> LoginTokens { get; private set; }

        [ProtoMember(5, IsRequired = false)]
        public Dictionary<string, string> GuardData { get; private set; }

        string FileName;

        AccountSettingsStore()
        {
            ContentServerPenalty = new ConcurrentDictionary<string, int>();
            LoginTokens = new(StringComparer.OrdinalIgnoreCase);
            GuardData = new(StringComparer.OrdinalIgnoreCase);
        }

        static bool Loaded
        {
            get { return Instance != null; }
        }

        public static AccountSettingsStore Instance;

        public static string CurrentFilePath { get; private set; }

        public static void LoadFromFile(string filename)
        {
            if (Loaded)
                throw new Exception("Config already loaded");

            var path = ResolveFilePath(filename);

            if (File.Exists(path))
            {
                try
                {
                    using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var ds = new DeflateStream(fs, CompressionMode.Decompress);
                    Instance = Serializer.Deserialize<AccountSettingsStore>(ds);
                    Instance.EnsureDefaults();
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ProtoException || ex is UnauthorizedAccessException)
                {
                    Console.WriteLine("Failed to load account settings: {0}", ex.Message);
                    Instance = new AccountSettingsStore();
                }
            }
            else
            {
                Instance = new AccountSettingsStore();
            }

            Instance.FileName = path;
            CurrentFilePath = path;
        }

        public static void Save()
        {
            if (!Loaded)
                throw new Exception("Saved config before loading");

            try
            {
                var directory = Path.GetDirectoryName(Instance.FileName);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using var fs = File.Open(Instance.FileName, FileMode.Create, FileAccess.Write, FileShare.None);
                using var ds = new DeflateStream(fs, CompressionMode.Compress);
                Serializer.Serialize(ds, Instance);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Console.WriteLine("Failed to save account settings: {0}", ex.Message);
            }
        }

        static string ResolveFilePath(string filename)
        {
            if (Path.IsPathRooted(filename))
            {
                return filename;
            }

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

            return Path.Combine(baseDir, "SteamDl", filename);
        }

        void EnsureDefaults()
        {
            ContentServerPenalty ??= new ConcurrentDictionary<string, int>();
            LoginTokens ??= new(StringComparer.OrdinalIgnoreCase);
            GuardData ??= new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
