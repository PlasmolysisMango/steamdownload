using System.Net;
using System.Text;
using System.Text.Json;
using SteamDl.Maui.Models;
using SteamAppInfo = SteamDl.Maui.Models.AppInfo;

namespace SteamDl.Maui.Services;

public sealed class ApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed class ApiClient : IDisposable
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    readonly HttpClient _client;

    public ApiClient(string host = "127.0.0.1", int port = 8630)
    {
        Host = host;
        Port = port;
        _client = new HttpClient
        {
            BaseAddress = new Uri($"http://{host}:{port}"),
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    public string Host { get; }
    public int Port { get; }

    public async Task<bool> PingAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(1));
        try
        {
            await GetAsync<JsonElement>("/api/config", cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task<JsonElement> ConfigAsync(CancellationToken cancellationToken = default) => GetAsync<JsonElement>("/api/config", cancellationToken);
    public Task<DiagnosticsResponse> DiagnosticsLogAsync(CancellationToken cancellationToken = default) => GetAsync<DiagnosticsResponse>("/api/diagnostics/log", cancellationToken);
    public async Task<AccountsResponse> AccountsAsync(CancellationToken cancellationToken = default) => await GetAsync<AccountsResponse>("/api/accounts", cancellationToken) ?? new([], [], LoginState.Idle, "");
    public Task<LoginState> LoginAsync(string username, string password, bool rememberPassword) => PostAsync<LoginState>("/api/accounts/login", new { username, password, remember_password = rememberPassword });
    public Task<LoginState> LoginInputAsync(string answer) => PostAsync<LoginState>("/api/accounts/login/input", new { answer });
    public Task<LoginState> ReloginAsync(string username) => PostAsync<LoginState>("/api/accounts/relogin", new { username });
    public Task LogoutAsync(string username) => PostAsync<JsonElement>("/api/accounts/logout", new { username });
    public Task SelectAccountAsync(string username) => PostAsync<JsonElement>("/api/accounts/select", new { username });
    public Task<ParsedTarget> ParseAsync(string url) => PostAsync<ParsedTarget>("/api/parse", new { url });
    public Task<SteamAppInfo> AppInfoAsync(string appId) => GetAsync<SteamAppInfo>($"/api/appinfo/{Uri.EscapeDataString(appId)}");
    public Task<LibraryStatus> LibraryStatusAsync(string username) => GetAsync<LibraryStatus>($"/api/library/status?username={Uri.EscapeDataString(username)}");
    public Task<LibraryStatus> LibrarySyncAsync(string username, bool full) => PostAsync<LibraryStatus>("/api/library/sync", new { username, mode = full ? "full" : "incremental", force_full_sync = full });

    public async Task<List<Job>> JobsAsync()
    {
        var response = await GetAsync<JobsResponse>("/api/jobs");
        return response?.Jobs ?? [];
    }

    public async Task<Job?> JobAsync(string jobId)
    {
        try { return await GetAsync<Job>($"/api/jobs/{Uri.EscapeDataString(jobId)}"); }
        catch (ApiException ex) when (ex.Status == 404) { return null; }
    }

    public async Task<Job> CreateJobAsync(string kind, string id, string username, string os, string depot, string outputDir, string installDir, string name)
    {
        var data = await PostElementAsync("/api/jobs", new
        {
            kind,
            id,
            username,
            anonymous = false,
            os,
            depot,
            output_dir = outputDir,
            install_dir = installDir,
            installdir = installDir,
            name,
        });
        if (data.TryGetProperty("job", out var jobElement))
        {
            return jobElement.Deserialize<Job>(JsonOptions)!;
        }
        return data.Deserialize<Job>(JsonOptions)!;
    }

    public Task JobActionAsync(string jobId, string action) => PostAsync<JsonElement>($"/api/jobs/{Uri.EscapeDataString(jobId)}/{action}", new { });
    public Task JobInputAsync(string jobId, string answer) => PostAsync<JsonElement>($"/api/jobs/{Uri.EscapeDataString(jobId)}/input", new { answer });
    public Task DeleteJobAsync(string jobId, bool deleteFiles) => SendAsync<JsonElement>(HttpMethod.Delete, $"/api/jobs/{Uri.EscapeDataString(jobId)}", new { delete_files = deleteFiles });
    public Task DeleteAllJobsAsync(bool deleteFiles) => SendAsync<JsonElement>(HttpMethod.Delete, "/api/jobs", new { delete_files = deleteFiles });
    public Task<AppSettings> SettingsAsync() => GetAsync<AppSettings>("/api/settings");
    public Task<AppSettings> SaveSettingsAsync(AppSettings settings) => PostAsync<AppSettings>("/api/settings", settings);

    async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken = default) => await SendAsync<T>(HttpMethod.Get, path, null, cancellationToken);
    async Task<T> PostAsync<T>(string path, object body, CancellationToken cancellationToken = default) => await SendAsync<T>(HttpMethod.Post, path, body, cancellationToken);
    async Task<JsonElement> PostElementAsync(string path, object body, CancellationToken cancellationToken = default) => await SendAsync<JsonElement>(HttpMethod.Post, path, body, cancellationToken);

    async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        }

        using var response = await _client.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException((int)response.StatusCode, ExtractError(text, response.StatusCode));
        }
        if (typeof(T) == typeof(JsonElement) && string.IsNullOrWhiteSpace(text))
        {
            return (T)(object)JsonDocument.Parse("{}").RootElement.Clone();
        }
        return JsonSerializer.Deserialize<T>(string.IsNullOrWhiteSpace(text) ? "{}" : text, JsonOptions)!;
    }

    static string ExtractError(string text, HttpStatusCode statusCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("error", out var error)) return error.GetString() ?? $"HTTP {(int)statusCode}";
        }
        catch
        {
        }
        return $"HTTP {(int)statusCode}";
    }

    public void Dispose() => _client.Dispose();
}
