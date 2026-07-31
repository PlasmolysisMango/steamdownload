using System.Diagnostics;

namespace SteamDl.Maui.Services;

public interface IPlatformFeatures
{
    bool IsAndroid { get; }
    Task StartEngineServiceAsync();
    Task<IReadOnlyDictionary<string, object>> EngineServiceStatusAsync();
    Task<string?> PickDirectoryAsync();
    Task RequestPermissionsAsync();
}

public sealed class DefaultPlatformFeatures : IPlatformFeatures
{
    public bool IsAndroid => false;

    public Task StartEngineServiceAsync() => Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, object>> EngineServiceStatusAsync() =>
        Task.FromResult<IReadOnlyDictionary<string, object>>(new Dictionary<string, object>());

    public async Task<string?> PickDirectoryAsync()
    {
        if (OperatingSystem.IsWindows()) return await PickWindowsDirectoryAsync();
        if (OperatingSystem.IsLinux()) return await PickLinuxDirectoryAsync();
        return null;
    }

    public Task RequestPermissionsAsync() => Task.CompletedTask;

    static async Task<string?> PickWindowsDirectoryAsync()
    {
        const string script = "Add-Type -AssemblyName System.Windows.Forms; " +
                              "$d = New-Object System.Windows.Forms.FolderBrowserDialog; " +
                              "$d.Description = '选择 SteamDl 保存目录'; " +
                              "$d.ShowNewFolderButton = $true; " +
                              "if ($d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { [Console]::Write($d.SelectedPath) }";
        var result = await RunProcessAsync("powershell", ["-NoProfile", "-STA", "-Command", script]);
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output) ? result.Output.Trim() : null;
    }

    static async Task<string?> PickLinuxDirectoryAsync()
    {
        var which = await RunProcessAsync("which", ["zenity"]);
        if (which.ExitCode != 0) return null;
        var result = await RunProcessAsync("zenity", ["--file-selection", "--directory", "--title=选择 SteamDl 保存目录"]);
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output) ? result.Output.Trim() : null;
    }

    static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"无法启动进程: {fileName}");
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output);
    }
}
