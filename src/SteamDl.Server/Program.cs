// 桌面/服务器入口:PC 或 Linux 上直接运行同一套下载服务
// 用法: dotnet run --project src/SteamDl.Server  (环境变量 PORT 可改端口)
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SteamDl.Core;

var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : 8630;

// 先保留原始 stdout 用于服务日志,再接管 Console 给下载引擎
ConsoleRelay.Instance.Install(passthroughToStdout: true);
_ = JobManager.Instance; // 触发事件接线

var picker = CreatePickDirectoryHandler();
if (picker != null) WebApi.PickDirectoryHandler = picker;

var api = new WebApi(port);
api.Start();

Console.Out.Flush();
var banner = $"* Steam Depot Web Downloader (C#): http://127.0.0.1:{port}";
Console.WriteLine(banner);

// 常驻直到进程被终止
System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);

static Func<string> CreatePickDirectoryHandler()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && (FindCommand("powershell") != null || FindCommand("pwsh") != null))
    {
        return PickWindowsDirectory;
    }

    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && FindCommand("osascript") != null)
    {
        return PickMacDirectory;
    }

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        if (FindCommand("zenity") != null) return PickLinuxZenityDirectory;
        if (FindCommand("kdialog") != null) return PickLinuxKdialogDirectory;
    }

    return null;
}

static string PickWindowsDirectory()
{
    var ps = FindCommand("powershell") ?? FindCommand("pwsh");
    var script = "Add-Type -AssemblyName System.Windows.Forms; " +
                 "$d = New-Object System.Windows.Forms.FolderBrowserDialog; " +
                 "$d.Description = '选择 SteamDl 保存目录'; " +
                 "$d.ShowNewFolderButton = $true; " +
                 "if ($d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { [Console]::Write($d.SelectedPath) }";
    return RunCapture(ps, "-NoProfile", "-STA", "-Command", script);
}

static string PickMacDirectory()
{
    return RunCapture("osascript", "-e", "POSIX path of (choose folder with prompt \"选择 SteamDl 保存目录\")")?.TrimEnd(Path.DirectorySeparatorChar, '/');
}

static string PickLinuxZenityDirectory()
{
    return RunCapture("zenity", "--file-selection", "--directory", "--title=选择 SteamDl 保存目录");
}

static string PickLinuxKdialogDirectory()
{
    return RunCapture("kdialog", "--getexistingdirectory", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "选择 SteamDl 保存目录");
}

static string RunCapture(string command, params string[] args)
{
    try
    {
        using var process = Process.Start(new ProcessStartInfo(command, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        });
        if (process == null) return null;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var path = output.Trim();
        return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(path) ? path : null;
    }
    catch
    {
        return null;
    }
}

static string FindCommand(string name)
{
    var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
    var names = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? new[] { name, name + ".exe", name + ".cmd", name + ".bat" }
        : new[] { name };
    foreach (var dir in paths)
    {
        foreach (var candidate in names)
        {
            var full = Path.Combine(dir, candidate);
            if (File.Exists(full)) return full;
        }
    }
    return null;
}
