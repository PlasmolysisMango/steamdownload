using SteamDl.Maui.Models;
using SteamAppInfo = SteamDl.Maui.Models.AppInfo;

namespace SteamDl.Maui.Services;

public sealed class AppState : IAsyncDisposable
{
    readonly ApiClient _api;
    readonly EngineController _engine;
    readonly IPlatformFeatures _platform;
    PeriodicTimer? _timer;
    CancellationTokenSource? _timerCts;

    public AppState(ApiClient api, EngineController engine, IPlatformFeatures platform)
    {
        _api = api;
        _engine = engine;
        _platform = platform;
    }

    public event Action? Changed;
    public bool EngineReady { get; private set; }
    public bool Busy { get; private set; }
    public string StatusText { get; private set; } = "等待引擎响应";
    public string Toast { get; private set; } = "";
    public string Diagnostics { get; private set; } = "";
    public List<string> Accounts { get; private set; } = [];
    public List<AccountDetail> AccountDetails { get; private set; } = [];
    public LoginState Login { get; private set; } = LoginState.Idle;
    public string SelectedAccount { get; private set; } = "";
    public LibraryStatus Library { get; private set; } = LibraryStatus.Idle;
    public List<Job> Jobs { get; private set; } = [];
    public AppSettings Settings { get; private set; } = AppSettings.Default;

    public async Task InitializeAsync() => await RunAsync(async () =>
    {
        StatusText = "正在启动 .NET 引擎 sidecar...";
        await _platform.RequestPermissionsAsync();
        await _engine.EnsureStartedAsync();
        EngineReady = true;
        StatusText = "引擎已就绪";
        await RefreshAsync(setBusy: false);
        StartPolling();
    }, "应用初始化失败");

    public async Task RefreshAsync(bool setBusy = true) => await RunAsync(async () =>
    {
        await LoadDiagnosticsAsync();
        var accounts = await _api.AccountsAsync();
        Accounts = accounts.Accounts ?? [];
        AccountDetails = accounts.AccountDetails ?? [];
        Login = accounts.Login ?? LoginState.Idle;
        SelectedAccount = accounts.SelectedAccount;
        Settings = await _api.SettingsAsync();
        Jobs = await _api.JobsAsync();
        var user = !string.IsNullOrWhiteSpace(SelectedAccount) ? SelectedAccount : Accounts.FirstOrDefault() ?? "";
        if (!string.IsNullOrWhiteSpace(user))
        {
            var library = await _api.LibraryStatusAsync(user);
            Library = library with { Items = library.Items ?? [] };
        }
        Notify();
    }, "刷新失败", setBusy);

    public async Task LoginAsync(string username, string password, bool rememberPassword) => await RunAsync(async () =>
    {
        Login = await _api.LoginAsync(username, password, rememberPassword);
        Toast = Login.Busy ? "请按提示完成 Steam Guard/2FA 验证" : "登录请求已提交";
        await RefreshAsync(false);
    }, "登录失败");

    public async Task SubmitLoginInputAsync(string answer) => await RunAsync(async () =>
    {
        Login = await _api.LoginInputAsync(answer);
        Toast = "验证信息已提交";
        await RefreshAsync(false);
    }, "提交验证失败");

    public async Task ReloginAsync(string username) => await RunAsync(async () =>
    {
        Login = await _api.ReloginAsync(username);
        Toast = "已请求恢复登录";
        await RefreshAsync(false);
    }, "恢复登录失败");

    public async Task LogoutAsync(string username) => await RunAsync(async () =>
    {
        await _api.LogoutAsync(username);
        Toast = "账号已退出";
        await RefreshAsync(false);
    }, "退出账号失败");

    public async Task SelectAccountAsync(string username) => await RunAsync(async () =>
    {
        await _api.SelectAccountAsync(username);
        SelectedAccount = username;
        Toast = $"已选择账号 {username}";
        await RefreshAsync(false);
    }, "选择账号失败");

    public async Task<ParsedTarget> ParseAsync(string url)
    {
        ParsedTarget result = new("app", "");
        await RunAsync(async () =>
        {
            result = await _api.ParseAsync(url);
            Toast = $"已解析: {result.Kind} {result.Id}";
        }, "解析失败");
        return result;
    }

    public async Task<SteamAppInfo?> LoadAppInfoAsync(string appId)
    {
        try { return await _api.AppInfoAsync(appId); }
        catch { return null; }
    }

    public async Task SyncLibraryAsync(string username, bool full) => await RunAsync(async () =>
    {
        var user = !string.IsNullOrWhiteSpace(username) ? username : SelectedAccount;
        if (string.IsNullOrWhiteSpace(user)) throw new InvalidOperationException("请先选择账号");
        var library = await _api.LibrarySyncAsync(user, full);
        Library = library with { Items = library.Items ?? [] };
        Toast = full ? "已开始全量同步" : "已开始增量同步";
    }, "同步游戏库失败");

    public async Task CreateJobAsync(string kind, string id, string username, string os, string depot, string outputDir, string installDir, string name) => await RunAsync(async () =>
    {
        var user = !string.IsNullOrWhiteSpace(username) ? username : SelectedAccount;
        if (string.IsNullOrWhiteSpace(user)) throw new InvalidOperationException("请先选择账号");
        await _api.CreateJobAsync(kind, id, user, os, depot, outputDir, installDir, name);
        Toast = "下载任务已创建";
        Jobs = await _api.JobsAsync();
    }, "创建任务失败");

    public async Task JobActionAsync(string jobId, string action) => await RunAsync(async () =>
    {
        await _api.JobActionAsync(jobId, action);
        Jobs = await _api.JobsAsync();
    }, "任务操作失败");

    public async Task JobInputAsync(string jobId, string answer) => await RunAsync(async () =>
    {
        await _api.JobInputAsync(jobId, answer);
        Jobs = await _api.JobsAsync();
    }, "提交任务输入失败");

    public async Task DeleteJobAsync(string jobId, bool deleteFiles) => await RunAsync(async () =>
    {
        await _api.DeleteJobAsync(jobId, deleteFiles);
        Jobs = await _api.JobsAsync();
        Toast = "任务已删除";
    }, "删除任务失败");

    public async Task DeleteAllJobsAsync(bool deleteFiles) => await RunAsync(async () =>
    {
        await _api.DeleteAllJobsAsync(deleteFiles);
        Jobs = await _api.JobsAsync();
        Toast = "任务已清空";
    }, "清空任务失败");

    public async Task<AppSettings> SaveSettingsAsync(AppSettings settings)
    {
        await RunAsync(async () =>
        {
            Settings = await _api.SaveSettingsAsync(settings);
            Toast = "设置已保存";
        }, "保存设置失败");
        return Settings;
    }

    public async Task<string?> PickDirectoryAsync() => await _platform.PickDirectoryAsync();

    async Task LoadDiagnosticsAsync()
    {
        try
        {
            var diagnostics = await _api.DiagnosticsLogAsync();
            Diagnostics = diagnostics.Lines is { Count: > 0 }
                ? string.Join(Environment.NewLine, diagnostics.Lines)
                : diagnostics.Log ?? "";
            if (!diagnostics.JobManagerReady && !string.IsNullOrWhiteSpace(diagnostics.JobManagerError))
            {
                Diagnostics = diagnostics.JobManagerError + Environment.NewLine + Diagnostics;
            }
        }
        catch
        {
        }
    }

    async Task RunAsync(Func<Task> action, string errorPrefix, bool setBusy = true)
    {
        if (setBusy) Busy = true;
        Notify();
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Toast = $"{errorPrefix}: {ex.Message}";
            StatusText = Toast;
            if (!EngineReady) await LoadDiagnosticsAsync();
        }
        finally
        {
            if (setBusy) Busy = false;
            Notify();
        }
    }

    void StartPolling()
    {
        if (_timer is not null) return;
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        _timerCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (await _timer.WaitForNextTickAsync(_timerCts.Token))
            {
                try
                {
                    Jobs = await _api.JobsAsync();
                    var user = !string.IsNullOrWhiteSpace(SelectedAccount) ? SelectedAccount : Accounts.FirstOrDefault() ?? "";
                    if (!string.IsNullOrWhiteSpace(user))
                    {
                        var library = await _api.LibraryStatusAsync(user);
                        Library = library with { Items = library.Items ?? [] };
                    }
                    Notify();
                }
                catch
                {
                }
            }
        });
    }

    void Notify() => MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke());

    public ValueTask DisposeAsync()
    {
        _timerCts?.Cancel();
        _timer?.Dispose();
        return ValueTask.CompletedTask;
    }
}
