// 内嵌 HTTP 服务:实现 Web UI 所需的 /api 契约,
// 并从嵌入资源提供同一份 index.html。基于 HttpListener,
// 无 ASP.NET Core 依赖,可同时运行于桌面(.NET)与 Android(Mono)。
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SteamDl.Core
{
    public sealed class WebApi
    {
        readonly HttpListener _listener = new();
        readonly byte[] _indexHtml;
        volatile bool _running;

        // 默认仅监听本机回环地址。Windows 上监听 http://+:port/ 需要 URL ACL 管理员授权，
        // 否则 HttpListener.Start() 会抛出“拒绝访问”。
        public WebApi(int port = 8630, string host = "127.0.0.1")
        {
            _listener.Prefixes.Add($"http://{host}:{port}/");

            using var stream = typeof(WebApi).Assembly.GetManifestResourceStream("index.html")
                ?? throw new InvalidOperationException("嵌入资源 index.html 缺失");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            _indexHtml = ms.ToArray();
        }

        public void Start()
        {
            _running = true;
            _listener.Start();
            Task.Run(AcceptLoopAsync);
        }

        public void Stop()
        {
            _running = false;
            _listener.Stop();
        }

        async Task AcceptLoopAsync()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception) when (!_running)
                {
                    break;
                }
                catch (Exception)
                {
                    continue;
                }

                _ = Task.Run(() => HandleAsync(ctx));
            }
        }

        async Task HandleAsync(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var path = req.Url?.AbsolutePath ?? "/";
            var method = req.HttpMethod;

            try
            {
                switch (method, path)
                {
                    case ("GET", "/"):
                    case ("GET", "/index.html"):
                        await WriteRawAsync(ctx, 200, "text/html; charset=utf-8", _indexHtml);
                        break;

                    case ("GET", "/api/config"):
                        await WriteJsonAsync(ctx, 200, new JsonObject
                        {
                            ["download_dir"] = JobManager.DefaultDownloadDir(),
                            // 引擎已内置(DepotDownloader/SteamKit2),无需外部命令行工具
                            ["engine_ready"] = true,
                            ["engine"] = "DepotDownloader",
                        });
                        break;

                    case ("POST", "/api/parse"):
                    {
                        var body = await ReadJsonAsync(req);
                        var parsed = SteamUrlParser.Parse(body?["url"]?.GetValue<string>());
                        if (parsed == null)
                        {
                            await WriteJsonAsync(ctx, 400, Error("无法识别的链接,支持商店链接/steam://链接/纯 AppID"));
                            break;
                        }

                        await WriteJsonAsync(ctx, 200, new JsonObject
                        {
                            ["kind"] = parsed.Value.Kind,
                            ["id"] = parsed.Value.Id,
                        });
                        break;
                    }

                    case ("GET", _) when path.StartsWith("/api/appinfo/", StringComparison.Ordinal):
                    {
                        if (!uint.TryParse(path["/api/appinfo/".Length..], out var appId))
                        {
                            await WriteJsonAsync(ctx, 400, Error("无效的 AppID"));
                            break;
                        }

                        JsonObject info;
                        try
                        {
                            info = await AppInfoService.GetAppInfoAsync(appId);
                        }
                        catch (Exception ex)
                        {
                            await WriteJsonAsync(ctx, 502, Error("获取游戏信息失败: " + ex.Message));
                            break;
                        }

                        if (info == null)
                        {
                            await WriteJsonAsync(ctx, 404, Error("商店无此 AppID 的信息(不影响下载)"));
                            break;
                        }

                        // JsonObject 不能重复挂载,输出副本
                        await WriteJsonAsync(ctx, 200, JsonNode.Parse(info.ToJsonString()).AsObject());
                        break;
                    }

                    case ("POST", "/api/download"):
                    {
                        var body = await ReadJsonAsync(req);
                        var request = new DownloadRequest
                        {
                            Kind = body?["kind"]?.GetValue<string>() ?? "app",
                            Id = (body?["id"]?.ToString() ?? "").Trim(),
                            Username = body?["username"]?.GetValue<string>(),
                            Anonymous = body?["anonymous"]?.GetValue<bool>() ?? false,
                            Os = body?["os"]?.GetValue<string>() ?? "windows",
                            DepotId = body?["depot"]?.ToString(),
                            OutputDir = body?["output_dir"]?.GetValue<string>(),
                        };

                        if (!JobManager.Instance.TryStart(request, out var error))
                        {
                            await WriteJsonAsync(ctx, error == "已有任务在进行中" ? 409 : 400, Error(error));
                            break;
                        }

                        await WriteJsonAsync(ctx, 200, Ok());
                        break;
                    }

                    case ("POST", "/api/input"):
                    {
                        var body = await ReadJsonAsync(req);
                        var answer = body?["answer"]?.GetValue<string>() ?? "";
                        if (!JobManager.Instance.SupplyInput(answer))
                        {
                            await WriteJsonAsync(ctx, 409, Error("当前没有等待输入的任务"));
                            break;
                        }

                        await WriteJsonAsync(ctx, 200, Ok());
                        break;
                    }

                    case ("POST", "/api/cancel"):
                        JobManager.Instance.Cancel();
                        await WriteJsonAsync(ctx, 200, Ok());
                        break;

                    case ("GET", "/api/status"):
                        await WriteJsonAsync(ctx, 200, JobManager.Instance.Status());
                        break;

                    default:
                        await WriteJsonAsync(ctx, 404, Error("not found"));
                        break;
                }
            }
            catch (Exception ex)
            {
                try
                {
                    await WriteJsonAsync(ctx, 500, Error(ex.Message));
                }
                catch
                {
                    // 连接已断开,忽略
                }
            }
        }

        static JsonObject Ok() => new() { ["ok"] = true };

        static JsonObject Error(string message) => new() { ["error"] = message };

        static async Task<JsonObject> ReadJsonAsync(HttpListenerRequest req)
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(text)?.AsObject();
            }
            catch
            {
                return null;
            }
        }

        static Task WriteJsonAsync(HttpListenerContext ctx, int status, JsonObject payload)
            => WriteRawAsync(ctx, status, "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes(payload.ToJsonString()));

        static async Task WriteRawAsync(HttpListenerContext ctx, int status, string contentType, byte[] payload)
        {
            var resp = ctx.Response;
            resp.StatusCode = status;
            resp.ContentType = contentType;
            resp.ContentLength64 = payload.Length;
            await resp.OutputStream.WriteAsync(payload).ConfigureAwait(false);
            resp.Close();
        }
    }
}
