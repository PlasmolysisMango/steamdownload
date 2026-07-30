using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SteamDl.Core
{
    public static class EngineDiagnostics
    {
        const int MaxLines = 800;
        static readonly object Sync = new();
        static readonly List<string> Lines = [];
        static bool _relayAttached;

        public static bool JobManagerReady { get; private set; }
        public static string JobManagerError { get; private set; } = "";

        public static string LogPath
        {
            get
            {
                try
                {
                    return Path.Combine(AppPaths.DataDir, "engine.log");
                }
                catch
                {
                    return "engine.log";
                }
            }
        }

        public static void AttachConsoleRelay(ConsoleRelay relay)
        {
            if (relay == null || _relayAttached) return;
            _relayAttached = true;
            relay.LineWritten += line => Log("console", line);
            relay.InputRequested += prompt => Log("input", string.IsNullOrWhiteSpace(prompt) ? "等待输入" : prompt);
            relay.InputSatisfied += () => Log("input", "输入已提交");
        }

        public static void MarkJobManagerReady()
        {
            JobManagerReady = true;
            JobManagerError = "";
            Log("engine", "JobManager ready");
        }

        public static void MarkJobManagerError(Exception ex)
        {
            JobManagerReady = false;
            JobManagerError = ex.ToString();
            Log("engine", "JobManager init failed: " + ex);
        }

        public static void Log(string category, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [{category}] {message}";
            lock (Sync)
            {
                Lines.Add(line);
                if (Lines.Count > MaxLines)
                {
                    Lines.RemoveRange(0, Lines.Count - MaxLines);
                }
            }

            try
            {
                var path = LogPath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // 文件日志失败时至少保留内存日志，避免诊断本身影响引擎。
            }
        }

        public static string ReadText(int maxLines = MaxLines)
        {
            var memoryLines = Snapshot(maxLines);
            try
            {
                var path = LogPath;
                if (File.Exists(path))
                {
                    var fileLines = File.ReadLines(path).TakeLast(maxLines).ToArray();
                    if (fileLines.Length > 0) return string.Join('\n', fileLines);
                }
            }
            catch
            {
                // 读取文件失败则回退内存日志。
            }
            return string.Join('\n', memoryLines);
        }

        static string[] Snapshot(int maxLines)
        {
            lock (Sync)
            {
                return Lines.Skip(Math.Max(0, Lines.Count - Math.Max(1, maxLines))).ToArray();
            }
        }
    }
}
