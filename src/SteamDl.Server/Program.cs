// 桌面/服务器入口:PC 或 Linux 上直接运行同一套下载服务
// 用法: dotnet run --project src/SteamDl.Server  (环境变量 PORT 可改端口)
using System;
using SteamDl.Core;

var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : 8630;

// 先保留原始 stdout 用于服务日志,再接管 Console 给下载引擎
ConsoleRelay.Instance.Install(passthroughToStdout: true);
_ = JobManager.Instance; // 触发事件接线

var api = new WebApi(port);
api.Start();

Console.Out.Flush();
var banner = $"* Steam Depot Web Downloader (C#): http://127.0.0.1:{port}";
Console.WriteLine(banner);

// 常驻直到进程被终止
System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
