using System.Diagnostics;

namespace SteamDl.Maui.Services;

public sealed class EngineController : IAsyncDisposable
{
    readonly ApiClient _api;
    readonly IPlatformFeatures _platform;
    Process? _process;
    int? _lastExitCode;
    string _lastOutput = "";
    bool _starting;
    PeriodicTimer? _watchdog;
    CancellationTokenSource? _watchdogCts;

    public EngineController(ApiClient api, IPlatformFeatures platform)
    {
        _api = api;
        _platform = platform;
    }

    public async Task EnsureStartedAsync()
    {
        if (await _api.PingAsync(TimeSpan.FromMilliseconds(700)))
        {
            ArmWatchdog();
            return;
        }
        await StartAsync();
        ArmWatchdog();
    }

    async Task StartAsync()
    {
        if (_starting) return;
        _starting = true;
        try
        {
            if (_platform.IsAndroid)
            {
                await _platform.StartEngineServiceAsync();
            }
            else
            {
                await SpawnDesktopAsync();
            }
            await WaitReadyAsync();
        }
        finally
        {
            _starting = false;
        }
    }

    async Task SpawnDesktopAsync()
    {
        if (_process is { HasExited: false }) return;
        var exeDir = AppContext.BaseDirectory;
        var exeName = OperatingSystem.IsWindows() ? "steamdl-engine.exe" : "steamdl-engine";
        var candidates = new[]
        {
            Path.Combine(exeDir, "engine", exeName),
            Path.Combine(exeDir, exeName),
            Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "engine", exeName),
        };
        var exe = candidates.FirstOrDefault(File.Exists);
        if (exe is null)
        {
            throw new InvalidOperationException($"未找到引擎程序(steamdl-engine)，请检查安装目录: {string.Join(", ", candidates)}");
        }

        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        start.Environment["STEAMDL_SIDECAR"] = "1";
        start.Environment["STEAMDL_BIND_HOST"] = _api.Host;
        start.Environment["PORT"] = _api.Port.ToString();

        _process = Process.Start(start) ?? throw new InvalidOperationException("引擎进程启动失败");
        _lastExitCode = null;
        _lastOutput = "";
        _ = PipeOutputAsync(_process.StandardOutput);
        _ = PipeOutputAsync(_process.StandardError);
        _ = Task.Run(async () =>
        {
            await _process.WaitForExitAsync();
            _lastExitCode = _process.ExitCode;
            _process = null;
        });
    }

    async Task PipeOutputAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            _lastOutput = (_lastOutput + line + Environment.NewLine);
            if (_lastOutput.Length > 3000) _lastOutput = _lastOutput[^3000..];
            Debug.WriteLine(line);
        }
    }

    async Task WaitReadyAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await _api.PingAsync(TimeSpan.FromMilliseconds(500))) return;
            if (_platform.IsAndroid)
            {
                var status = await _platform.EngineServiceStatusAsync();
                var state = status.TryGetValue("status", out var s) ? s?.ToString() ?? "" : "";
                if (state is "failed" or "exited")
                {
                    var error = status.TryGetValue("error", out var e) ? e?.ToString() : "";
                    var output = status.TryGetValue("last_output", out var o) ? o?.ToString() : "";
                    throw new InvalidOperationException($"Android 引擎启动失败({state}){(string.IsNullOrWhiteSpace(error) ? "" : $": {error}")}{(string.IsNullOrWhiteSpace(output) ? "" : Environment.NewLine + output)}");
                }
            }
            else if (_lastExitCode is not null)
            {
                throw new InvalidOperationException($"引擎进程已退出(ExitCode={_lastExitCode}){(string.IsNullOrWhiteSpace(_lastOutput) ? "" : ": " + _lastOutput)}");
            }
            await Task.Delay(200);
        }
        throw new TimeoutException($"引擎启动超时: 12 秒内未响应 http://{_api.Host}:{_api.Port}/api/config");
    }

    void ArmWatchdog()
    {
        if (_watchdog is not null) return;
        _watchdog = new PeriodicTimer(TimeSpan.FromSeconds(5));
        _watchdogCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (await _watchdog.WaitForNextTickAsync(_watchdogCts.Token))
            {
                if (_starting) continue;
                if (!await _api.PingAsync(TimeSpan.FromSeconds(1)))
                {
                    try { await StartAsync(); }
                    catch { }
                }
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _watchdogCts?.Cancel();
        _watchdog?.Dispose();
        if (_process is { HasExited: false })
        {
            try { _process.StandardInput.Close(); } catch { }
            try { _process.Kill(entireProcessTree: true); } catch { }
        }
        await Task.CompletedTask;
    }
}
