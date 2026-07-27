// 内嵌 HTTP 服务:提供 React 静态资源与 /api 契约。基于 HttpListener,
// 无 ASP.NET Core 依赖,可同时运行于桌面(.NET)与 Android(Mono)。
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SteamDl.Core
{
    public sealed class WebApi
    {
        readonly HttpListener _listener = new();
        readonly Assembly _assembly = typeof(WebApi).Assembly;
        volatile bool _running;

        public static Func<string, bool> OpenPathHandler { get; set; }
        public static Func<string> PickDirectoryHandler { get; set; }

        public WebApi(int port = 8630, string host = "+")
        {
            _listener.Prefixes.Add($"http://{host}:{port}/");
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
                if (method == "GET" && !path.StartsWith("/api/", StringComparison.Ordinal))
                {
                    await ServeStaticAsync(ctx, path).ConfigureAwait(false);
                    return;
                }

                switch (method, path)
                {
                    case ("GET", "/api/config"):
                        await WriteJsonAsync(ctx, 200, new JsonObject
                        {
                            ["download_dir"] = JobManager.DefaultDownloadDir(),
                            ["data_dir"] = AppPaths.DataDir,
                            ["database"] = JobStore.Instance.DatabasePath,
                            ["can_pick_directory"] = PickDirectoryHandler != null,
                            ["can_open_output"] = OpenPathHandler != null,
                            ["engine_ready"] = true,
                            ["engine"] = "DepotDownloader",
                        });
                        break;

                    case ("POST", "/api/pick-directory"):
                    {
                        if (PickDirectoryHandler == null)
                        {
                            await WriteJsonAsync(ctx, 501, Error("当前平台不支持系统目录选择器，请手动输入保存目录"));
                            break;
                        }

                        var picked = PickDirectoryHandler();
                        if (string.IsNullOrWhiteSpace(picked))
                        {
                            await WriteJsonAsync(ctx, 400, Error("未选择目录"));
                            break;
                        }

                        var settings = JobStore.Instance.GetSettings();
                        settings.DefaultDownloadDir = picked;
                        JobStore.Instance.SaveSettings(settings);
                        await WriteJsonAsync(ctx, 200, new JsonObject
                        {
                            ["ok"] = true,
                            ["path"] = picked,
                            ["download_dir"] = picked,
                        });
                        break;
                    }

                    case ("POST", "/api/open-output"):
                    {
                        var body = await ReadJsonAsync(req);
                        var pathToOpen = body?["path"]?.GetValue<string>() ?? JobManager.DefaultDownloadDir();
                        if (OpenPathHandler == null || !OpenPathHandler(pathToOpen))
                        {
                            await WriteJsonAsync(ctx, 501, Error("当前平台无法自动打开目录"));
                            break;
                        }
                        await WriteJsonAsync(ctx, 200, Ok());
                        break;
                    }

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

                        await WriteJsonAsync(ctx, 200, JsonNode.Parse(info.ToJsonString()).AsObject());
                        break;
                    }

                    case ("GET", "/api/jobs"):
                    {
                        var state = req.QueryString["state"];
                        await WriteJsonAsync(ctx, 200, new JsonObject { ["jobs"] = JobManager.Instance.JobsJson(state) });
                        break;
                    }

                    case ("POST", "/api/jobs"):
                    case ("POST", "/api/download"):
                    {
                        var request = ToDownloadRequest(await ReadJsonAsync(req));
                        if (!JobManager.Instance.TryStart(request, out var error, out var job))
                        {
                            await WriteJsonAsync(ctx, error == "已有任务在进行中" ? 409 : 400, Error(error));
                            break;
                        }

                        await WriteJsonAsync(ctx, 200, new JsonObject { ["ok"] = true, ["job"] = JobStore.Instance.ToJson(job, includeLog: true) });
                        break;
                    }

                    case ("GET", _) when IsJobPath(path, out var jobId, out var action) && action == null:
                    {
                        var job = JobManager.Instance.JobJson(jobId);
                        if (job == null)
                        {
                            await WriteJsonAsync(ctx, 404, Error("任务不存在"));
                            break;
                        }

                        await WriteJsonAsync(ctx, 200, job);
                        break;
                    }

                    case ("POST", _) when IsJobPath(path, out var jobId, out var action) && action == "input":
                    {
                        var body = await ReadJsonAsync(req);
                        var answer = body?["answer"]?.GetValue<string>() ?? "";
                        if (!JobManager.Instance.SupplyInput(jobId, answer))
                        {
                            await WriteJsonAsync(ctx, 409, Error("当前任务没有等待输入"));
                            break;
                        }

                        await WriteJsonAsync(ctx, 200, Ok());
                        break;
                    }

                    case ("POST", _) when IsJobPath(path, out var jobId, out var action) && action == "cancel":
                        if (!JobManager.Instance.Cancel(jobId))
                        {
                            await WriteJsonAsync(ctx, 404, Error("任务不存在"));
                            break;
                        }
                        await WriteJsonAsync(ctx, 200, Ok());
                        break;

                    case ("POST", _) when IsJobPath(path, out var jobId, out var action) && action == "retry":
                        if (!JobManager.Instance.Retry(jobId, out var retryError))
                        {
                            await WriteJsonAsync(ctx, retryError == "已有任务在进行中" ? 409 : 400, Error(retryError));
                            break;
                        }
                        await WriteJsonAsync(ctx, 200, Ok());
                        break;

                    case ("POST", _) when IsJobPath(path, out var jobId, out var action) && action == "pause":
                        if (!JobManager.Instance.Pause(jobId, out var pauseError))
                        {
                            await WriteJsonAsync(ctx, pauseError == "任务不存在" ? 404 : 409, Error(pauseError));
                            break;
                        }
                        await WriteJsonAsync(ctx, 200, Ok());
                        break;

                    case ("POST", _) when IsJobPath(path, out var jobId, out var action) && action == "resume":
                        if (!JobManager.Instance.Resume(jobId, out var resumeError))
                        {
                            await WriteJsonAsync(ctx, resumeError == "已有任务在进行中" ? 409 : 400, Error(resumeError));
                            break;
                        }
                        await WriteJsonAsync(ctx, 200, Ok());
                        break;

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

                    case ("GET", "/api/accounts"):
                    {
                        var arr = new JsonArray();
                        foreach (var account in JobManager.Instance.Accounts()) arr.Add(account);
                        await WriteJsonAsync(ctx, 200, new JsonObject { ["accounts"] = arr, ["account_details"] = JobManager.Instance.AccountDetailsJson(), ["login"] = LoginJson(JobManager.Instance.LoginStatus()) });
                        break;
                    }

                    case ("GET", "/api/accounts/login/status"):
                    {
                        await WriteJsonAsync(ctx, 200, LoginJson(JobManager.Instance.LoginStatus()));
                        break;
                    }

                    case ("POST", "/api/accounts/login"):
                    {
                        var body = await ReadJsonAsync(req);
                        var username = body?["username"]?.GetValue<string>();
                        var password = body?["password"]?.GetValue<string>();
                        var rememberPassword = body?["remember_password"]?.GetValue<bool>() ?? false;
                        if (!JobManager.Instance.StartLogin(username, password, rememberPassword, out var loginError))
                        {
                            await WriteJsonAsync(ctx, 409, Error(loginError));
                            break;
                        }
                        await WriteJsonAsync(ctx, 200, LoginJson(JobManager.Instance.LoginStatus()));
                        break;
                    }

                    case ("POST", "/api/accounts/login/input"):
                    {
                        var body = await ReadJsonAsync(req);
                        var answer = body?["answer"]?.GetValue<string>() ?? "";
                        if (!await JobManager.Instance.SupplyLoginInputAndWaitAsync(answer))
                        {
                            await WriteJsonAsync(ctx, 409, Error("当前没有等待输入的登录流程"));
                            break;
                        }
                        await WriteJsonAsync(ctx, 200, LoginJson(JobManager.Instance.LoginStatus()));
                        break;
                    }

                    case ("POST", "/api/accounts/relogin"):
                    {
                        var body = await ReadJsonAsync(req);
                        var username = body?["username"]?.GetValue<string>();
                        if (!JobManager.Instance.Relogin(username, out var reloginError))
                        {
                            await WriteJsonAsync(ctx, 409, Error(reloginError));
                            break;
                        }
                        await WriteJsonAsync(ctx, 200, LoginJson(JobManager.Instance.LoginStatus()));
                        break;
                    }

                    case ("POST", "/api/accounts/logout"):
                    {
                        var body = await ReadJsonAsync(req);
                        var username = body?["username"]?.GetValue<string>();
                        if (!JobManager.Instance.Logout(username))
                        {
                            await WriteJsonAsync(ctx, 404, Error("账号不存在"));
                            break;
                        }
                        await WriteJsonAsync(ctx, 200, Ok());
                        break;
                    }

                    case ("GET", "/api/library"):
                    {
                        var username = req.QueryString["username"];
                        await WriteJsonAsync(ctx, 200, await JobManager.Instance.LibraryJsonAsync(username).ConfigureAwait(false));
                        break;
                    }

                    case ("POST", "/api/library/sync"):
                    {
                        var body = await ReadJsonAsync(req);
                        var username = body?["username"]?.GetValue<string>() ?? req.QueryString["username"];
                        var forceFullSync = body?["force_full_sync"]?.GetValue<bool>() == true ||
                            string.Equals(body?["mode"]?.GetValue<string>(), "full", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(req.QueryString["mode"], "full", StringComparison.OrdinalIgnoreCase);
                        await WriteJsonAsync(ctx, 200, JobManager.Instance.StartLibrarySync(username, forceFullSync));
                        break;
                    }

                    case ("GET", "/api/library/status"):
                    {
                        var username = req.QueryString["username"];
                        await WriteJsonAsync(ctx, 200, JobManager.Instance.LibrarySyncStatusJson(username));
                        break;
                    }

                    case ("GET", "/api/settings"):
                        await WriteJsonAsync(ctx, 200, SettingsJson(JobStore.Instance.GetSettings()));
                        break;

                    case ("PUT", "/api/settings"):
                    case ("POST", "/api/settings"):
                    {
                        var settings = ParseSettings(await ReadJsonAsync(req));
                        await WriteJsonAsync(ctx, 200, SettingsJson(JobStore.Instance.SaveSettings(settings)));
                        break;
                    }

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

        async Task ServeStaticAsync(HttpListenerContext ctx, string path)
        {
            var resourcePath = path == "/" || path == "/index.html"
                ? "wwwroot/index.html"
                : "wwwroot" + Uri.UnescapeDataString(path);

            if (resourcePath.Contains("..", StringComparison.Ordinal))
            {
                await WriteJsonAsync(ctx, 400, Error("bad path"));
                return;
            }

            using var stream = _assembly.GetManifestResourceStream(resourcePath);
            if (stream == null)
            {
                using var fallback = _assembly.GetManifestResourceStream("wwwroot/index.html");
                if (fallback == null)
                {
                    await WriteJsonAsync(ctx, 404, Error("static resource not found"));
                    return;
                }

                using var fallbackMs = new MemoryStream();
                await fallback.CopyToAsync(fallbackMs).ConfigureAwait(false);
                await WriteRawAsync(ctx, 200, "text/html; charset=utf-8", fallbackMs.ToArray()).ConfigureAwait(false);
                return;
            }

            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms).ConfigureAwait(false);
            await WriteRawAsync(ctx, 200, ContentType(resourcePath), ms.ToArray()).ConfigureAwait(false);
        }

        static bool IsJobPath(string path, out string jobId, out string action)
        {
            jobId = null;
            action = null;
            if (!path.StartsWith("/api/jobs/", StringComparison.Ordinal)) return false;
            var rest = path["/api/jobs/".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (rest.Length == 0) return false;
            jobId = rest[0];
            if (rest.Length > 1) action = rest[1];
            return true;
        }

        static DownloadRequest ToDownloadRequest(JsonObject body) => new()
        {
            Kind = FirstNonBlank(body?["kind"]?.GetValue<string>(), "app"),
            Id = FirstNonBlank(body?["id"]?.ToString(), body?["item_id"]?.ToString(), "").Trim(),
            Username = body?["username"]?.GetValue<string>(),
            Anonymous = body?["anonymous"]?.GetValue<bool>() ?? false,
            Os = FirstNonBlank(body?["os"]?.GetValue<string>(), "windows"),
            DepotId = FirstNonBlank(body?["depot"]?.ToString(), body?["depot_id"]?.ToString()),
            OutputDir = body?["output_dir"]?.GetValue<string>(),
            GameName = FirstNonBlank(body?["name"]?.GetValue<string>(), body?["title"]?.GetValue<string>()),
            InstallDirName = FirstNonBlank(
                body?["install_dir"]?.GetValue<string>(),
                body?["installdir"]?.GetValue<string>(),
                body?["name"]?.GetValue<string>()),
        };

        
        static string FirstNonBlank(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return null;
        }
        
        static AppSettings ParseSettings(JsonObject body) => new()
        {
            DefaultDownloadDir = body?["default_download_dir"]?.GetValue<string>() ?? JobStore.Instance.GetSettings().DefaultDownloadDir,
            DefaultPlatformOs = body?["default_platform_os"]?.GetValue<string>() ?? "windows",
            MaxDownloads = body?["max_downloads"]?.GetValue<int>() ?? 8,
            AutoResume = body?["auto_resume"]?.GetValue<bool>() ?? true,
        };

        static JsonObject SettingsJson(AppSettings settings) => new()
        {
            ["default_download_dir"] = settings.DefaultDownloadDir,
            ["default_platform_os"] = settings.DefaultPlatformOs,
            ["max_downloads"] = settings.MaxDownloads,
            ["auto_resume"] = settings.AutoResume,
        };

        static JsonObject LoginJson(LoginState login) => new()
        {
            ["username"] = login.Username,
            ["state"] = login.State,
            ["prompt"] = login.Prompt,
            ["prompt_secret"] = login.PromptSecret,
            ["error"] = login.Error,
            ["log"] = login.Log,
            ["remember_password"] = login.RememberPassword,
        };

        static JsonObject Ok() => new() { ["ok"] = true };

        static JsonObject Error(string message) => new() { ["error"] = message };

        static async Task<JsonObject> ReadJsonAsync(HttpListenerRequest req)
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return JsonNode.Parse(text)?.AsObject(); }
            catch { return null; }
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

        static string ContentType(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".html" => "text/html; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".json" => "application/json; charset=utf-8",
                ".svg" => "image/svg+xml",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".ico" => "image/x-icon",
                _ => "application/octet-stream",
            };
        }
    }
}
