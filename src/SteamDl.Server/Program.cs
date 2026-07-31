// SteamDl 引擎 sidecar 入口:被 MAUI UI 以子进程/Android 前台服务方式拉起,
// 通过 127.0.0.1 HTTP(/api 契约)提供登录/游戏库/下载任务能力。
// 同一份二进制用于:
//   - Windows/Linux 桌面(win-x64/linux-x64 自包含单文件)
//   - Android(linux-bionic-arm64 松散自包含目录,由前台服务从 asset zip 解包后执行)
// 环境变量: PORT(默认 8630)、STEAMDL_BIND_HOST(默认 127.0.0.1)、
//           STEAMDL_DATA_DIR(数据目录)、STEAMDL_SIDECAR=1(stdin EOF 时自动退出)
using System;
using System.Threading.Tasks;
using SteamDl.Core;

var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : 8630;
var host = Environment.GetEnvironmentVariable("STEAMDL_BIND_HOST");
if (string.IsNullOrWhiteSpace(host)) host = "127.0.0.1";

// 先保留原始 stdout 用于服务日志,再接管 Console 给下载引擎
EngineDiagnostics.BeginSession();
EngineDiagnostics.Log("engine", "SteamDl sidecar starting");
EngineDiagnostics.AttachConsoleRelay(ConsoleRelay.Instance);
ConsoleRelay.Instance.Install(passthroughToStdout: true);

// 先启动 HTTP 健康检查端点,避免任务恢复/账号存储初始化拖慢 UI 启动
var api = new WebApi(port, host);
api.Start();
EngineDiagnostics.Log("engine", $"HTTP API listening on http://{host}:{port}");

Console.Out.Flush();
Console.WriteLine($"* SteamDl engine sidecar: http://{host}:{port}");

_ = Task.Run(() =>
{
    try
    {
        _ = JobManager.Instance; // 触发事件接线 + 恢复中断任务
        EngineDiagnostics.MarkJobManagerReady();
        Console.WriteLine("* SteamDl job manager ready");
    }
    catch (Exception ex)
    {
        EngineDiagnostics.MarkJobManagerError(ex);
        Console.Error.WriteLine("SteamDl job manager init failed: " + ex);
    }
});

if (Environment.GetEnvironmentVariable("STEAMDL_SIDECAR") == "1")
{
    // sidecar 模式:父进程/UI 服务退出会关闭管道,stdin 读到 EOF 即自杀,
    // 避免 UI 崩溃/被强杀后留下孤儿引擎进程占用端口。
    // 注意必须用 OpenStandardInput:Console.In 已被 ConsoleRelay 接管。
    using var stdin = Console.OpenStandardInput();
    var buffer = new byte[256];
    while (true)
    {
        int read;
        try
        {
            read = stdin.Read(buffer, 0, buffer.Length);
        }
        catch
        {
            break;
        }
        if (read <= 0) break;
    }
    api.Stop();
    EngineDiagnostics.Log("engine", "SteamDl sidecar stopped by stdin EOF");
    return;
}

// 常驻直到进程被终止
System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
