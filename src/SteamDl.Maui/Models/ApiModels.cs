using System.Text.Json.Serialization;

namespace SteamDl.Maui.Models;

public sealed record Job(
    [property: JsonPropertyName("job_id")] string JobId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("anonymous")] bool Anonymous,
    [property: JsonPropertyName("os")] string Os,
    [property: JsonPropertyName("depot")] string Depot,
    [property: JsonPropertyName("output_dir")] string OutputDir,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("prompt_secret")] bool PromptSecret,
    [property: JsonPropertyName("percent")] double Percent,
    [property: JsonPropertyName("progress_text")] string ProgressText,
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("downloaded")] bool Downloaded,
    [property: JsonPropertyName("log")] string Log,
    [property: JsonPropertyName("updated_at")] string UpdatedAt)
{
    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title : (!string.IsNullOrWhiteSpace(Name) ? Name : $"{Kind} {Id}");
    public bool IsActive => State is "queued" or "starting" or "running" or "waiting_input";
}

public sealed record JobsResponse([property: JsonPropertyName("jobs")] List<Job>? Jobs);

public sealed record AppSettings(
    [property: JsonPropertyName("default_download_dir")] string DefaultDownloadDir,
    [property: JsonPropertyName("default_platform_os")] string DefaultPlatformOs,
    [property: JsonPropertyName("max_downloads")] int MaxDownloads,
    [property: JsonPropertyName("auto_resume")] bool AutoResume,
    [property: JsonPropertyName("selected_account")] string SelectedAccount)
{
    public static AppSettings Default { get; } = new("", "windows", 8, true, "");
}

public sealed record LibraryGame(
    [property: JsonPropertyName("app_id")] string AppId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("header_image")] string HeaderImage,
    [property: JsonPropertyName("install_dir")] string InstallDir,
    [property: JsonPropertyName("size_bytes")] long SizeBytes,
    [property: JsonPropertyName("size_text")] string SizeText,
    [property: JsonPropertyName("is_downloaded")] bool IsDownloaded,
    [property: JsonPropertyName("downloaded_at")] string DownloadedAt);

public sealed record LibraryStatus(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("sync_mode")] string SyncMode,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("items")] List<LibraryGame>? Items,
    [property: JsonPropertyName("app_count")] int AppCount,
    [property: JsonPropertyName("scanned_app_count")] int ScannedAppCount,
    [property: JsonPropertyName("progress")] int Progress)
{
    public static LibraryStatus Idle { get; } = new("", "idle", "full", "", [], 0, 0, 0);
}

public sealed record AccountDetail(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("logged_in")] bool LoggedIn,
    [property: JsonPropertyName("remember_password")] bool RememberPassword,
    [property: JsonPropertyName("has_saved_password")] bool HasSavedPassword,
    [property: JsonPropertyName("last_used_at")] string LastUsedAt);

public sealed record LoginState(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("prompt_secret")] bool PromptSecret,
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("log")] string Log)
{
    public static LoginState Idle { get; } = new("", "idle", "", false, "", "");
    public bool Busy => State is "running" or "waiting_input";
}

public sealed record AccountsResponse(
    [property: JsonPropertyName("accounts")] List<string>? Accounts,
    [property: JsonPropertyName("account_details")] List<AccountDetail>? AccountDetails,
    [property: JsonPropertyName("login")] LoginState? Login,
    [property: JsonPropertyName("selected_account")] string SelectedAccount);

public sealed record ParsedTarget(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string Id);

public sealed record AppInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("install_dir")] string InstallDir,
    [property: JsonPropertyName("header_image")] string HeaderImage,
    [property: JsonPropertyName("size_bytes")] long SizeBytes);

public sealed record DiagnosticsResponse(
    [property: JsonPropertyName("log")] string? Log,
    [property: JsonPropertyName("lines")] List<string>? Lines,
    [property: JsonPropertyName("job_manager_ready")] bool JobManagerReady,
    [property: JsonPropertyName("job_manager_error")] string? JobManagerError);

public static class JobStateText
{
    public static readonly IReadOnlyDictionary<string, string> Values = new Dictionary<string, string>
    {
        ["idle"] = "空闲",
        ["queued"] = "排队中",
        ["starting"] = "启动中",
        ["running"] = "下载中",
        ["paused"] = "已暂停",
        ["waiting_input"] = "等待输入",
        ["interrupted"] = "已中断",
        ["done"] = "完成",
        ["error"] = "出错",
        ["cancelled"] = "已取消",
    };

    public static bool TryGetValue(string state, out string text) => Values.TryGetValue(state, out text!);
}
