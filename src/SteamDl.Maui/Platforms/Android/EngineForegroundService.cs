#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;
using Java.Util.Zip;
using File = Java.IO.File;

namespace SteamDl.Maui.Platforms.Android;

[Service(Name = "app.steamdl.EngineService", Exported = false)]
public sealed class EngineForegroundService : Service
{
    const int Port = 8630;
    const string Tag = "SteamDlEngine";
    const string ChannelId = "steamdl";
    const int NotificationId = 1;

    static readonly object Sync = new();
    static System.Diagnostics.Process? Process;
    static bool Supervising;
    static string Status = "idle";
    static string Error = "";
    static string LastOutput = "";

    PowerManager.WakeLock? _wakeLock;
    StreamWriter? _engineStdin;

    public static Dictionary<string, object> StatusSnapshot()
    {
        lock (Sync)
        {
            return new Dictionary<string, object>
            {
                ["status"] = Status,
                ["error"] = Error,
                ["last_output"] = LastOutput,
                ["process_alive"] = Process?.HasExited == false,
            };
        }
    }

    public override void OnCreate()
    {
        base.OnCreate();
        CreateNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        SetStatus("service_started");
        StartForeground(NotificationId, BuildNotification());
        AcquireWakeLock();
        StartSupervisor();
        return StartCommandResult.Sticky;
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        Supervising = false;
        try { _engineStdin?.Close(); } catch { }
        try { Process?.Kill(); } catch { }
        Process = null;
        if (_wakeLock?.IsHeld == true) _wakeLock.Release();
        base.OnDestroy();
    }

    void StartSupervisor()
    {
        if (Supervising) return;
        Supervising = true;
        new Thread(() =>
        {
            while (Supervising)
            {
                try
                {
                    if (Process?.HasExited != false)
                    {
                        SetStatus("starting");
                        LaunchEngine();
                    }
                    Process?.WaitForExit();
                    if (Supervising)
                    {
                        var code = Process?.ExitCode.ToString() ?? "unknown";
                        SetStatus("exited", $"engine exited code={code}");
                        Process = null;
                        Thread.Sleep(2000);
                    }
                }
                catch (ThreadInterruptedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SetStatus("failed", ex.ToString());
                    Log.Error(Tag, $"engine supervise error: {ex.Message}");
                    try { Thread.Sleep(3000); } catch { break; }
                }
            }
        }) { Name = "steamdl-engine-supervisor", IsBackground = true }.Start();
    }

    void LaunchEngine()
    {
        var engineDir = PrepareEngineBundle();
        var engine = new File(engineDir, "steamdl-engine");
        if (!engine.Exists()) throw new InvalidOperationException($"engine binary missing: {engine.AbsolutePath}");
        engine.SetExecutable(true, false);

        var filesDir = FilesDir!.AbsolutePath;
        var certFile = PrepareCaCertificates();
        var dataDir = new File(filesDir, "steamdl"); dataDir.Mkdirs();
        var bundleDir = new File(filesDir, "bundle"); bundleDir.Mkdirs();

        var start = new System.Diagnostics.ProcessStartInfo(engine.AbsolutePath!)
        {
            WorkingDirectory = engineDir.AbsolutePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        start.Environment["HOME"] = filesDir;
        start.Environment["DOTNET_ROOT"] = engineDir.AbsolutePath;
        start.Environment["TMPDIR"] = CacheDir!.AbsolutePath;
        start.Environment["PORT"] = Port.ToString();
        start.Environment["STEAMDL_BIND_HOST"] = "127.0.0.1";
        start.Environment["STEAMDL_SIDECAR"] = "1";
        start.Environment["STEAMDL_DATA_DIR"] = dataDir.AbsolutePath;
        start.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
        start.Environment["DOTNET_EnableWriteXorExecute"] = "0";
        start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = bundleDir.AbsolutePath;
        start.Environment["LD_LIBRARY_PATH"] = engineDir.AbsolutePath;
        if (certFile is not null) start.Environment["SSL_CERT_FILE"] = certFile.AbsolutePath;

        Log.Info(Tag, $"starting engine: {engine.AbsolutePath}");
        var proc = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("engine process start failed");
        Process = proc;
        _engineStdin = proc.StandardInput;
        SetStatus("process_started");

        new Thread(() => PipeEngineOutput(proc.StandardOutput)) { Name = "steamdl-engine-stdout", IsBackground = true }.Start();
        new Thread(() => PipeEngineOutput(proc.StandardError)) { Name = "steamdl-engine-stderr", IsBackground = true }.Start();
    }

    void PipeEngineOutput(StreamReader reader)
    {
        try
        {
            while (reader.ReadLine() is { } line)
            {
                AppendOutput(line);
                if (line.Contains("HTTP API listening", StringComparison.OrdinalIgnoreCase)) SetStatus("http_listening");
                Log.Info(Tag, line);
            }
        }
        catch
        {
        }
    }

    File PrepareEngineBundle()
    {
        var dir = new File(FilesDir, "engine-bionic");
        var marker = new File(FilesDir, "engine-bionic.version");
        var lastUpdate = PackageManager?.GetPackageInfo(PackageName!, 0)?.LastUpdateTime.ToString() ?? "unknown";
        if (dir.IsDirectory && new File(dir, "steamdl-engine").Exists() && marker.Exists() && ReadAllText(marker) == lastUpdate)
        {
            return dir;
        }

        Log.Info(Tag, $"extracting engine bundle to {dir.AbsolutePath}");
        DeleteRecursively(dir);
        dir.Mkdirs();
        using var input = Assets!.Open("engine-bionic.zip");
        using var zip = new ZipInputStream(input);
        ZipEntry? entry;
        while ((entry = zip.NextEntry) is not null)
        {
            if (!entry.IsDirectory)
            {
                var outFile = new File(dir, entry.Name);
                outFile.ParentFile?.Mkdirs();
                using var output = System.IO.File.Create(outFile.AbsolutePath!);
                zip.CopyTo(output);
                if (entry.Name == "steamdl-engine") outFile.SetExecutable(true, false);
            }
            zip.CloseEntry();
        }
        WriteAllText(marker, lastUpdate);
        return dir;
    }

    File? PrepareCaCertificates()
    {
        try
        {
            var outFile = new File(FilesDir, "cacert.pem");
            using var input = Assets!.Open("cacert.pem");
            using var output = System.IO.File.Create(outFile.AbsolutePath!);
            input.CopyTo(output);
            return outFile;
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"cacert.pem missing or failed to extract: {ex.Message}");
            return null;
        }
    }

    Notification BuildNotification()
    {
        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);
        return builder
            .SetContentTitle("SteamDl 下载服务")
            .SetContentText("下载引擎正在后台运行")
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownloadDone)
            .SetOngoing(true)
            .Build();
    }

    void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var channel = new NotificationChannel(ChannelId, "SteamDl", NotificationImportance.Low);
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.CreateNotificationChannel(channel);
    }

    void AcquireWakeLock()
    {
        if (_wakeLock?.IsHeld == true) return;
        var pm = (PowerManager?)GetSystemService(PowerService);
        _wakeLock = pm?.NewWakeLock(WakeLockFlags.Partial, "SteamDl:Engine");
        _wakeLock?.Acquire();
    }

    static void SetStatus(string value, string message = "")
    {
        lock (Sync)
        {
            Status = value;
            Error = message;
        }
        if (!string.IsNullOrWhiteSpace(message)) Log.Error(Tag, $"engine status={value}: {message}");
    }

    static void AppendOutput(string line)
    {
        lock (Sync)
        {
            var merged = string.IsNullOrWhiteSpace(LastOutput) ? line : LastOutput + Environment.NewLine + line;
            LastOutput = merged.Length > 3000 ? merged[^3000..] : merged;
        }
    }

    static string ReadAllText(File file) => System.IO.File.ReadAllText(file.AbsolutePath!);
    static void WriteAllText(File file, string text) => System.IO.File.WriteAllText(file.AbsolutePath!, text);

    static void DeleteRecursively(File file)
    {
        if (!file.Exists()) return;
        if (file.IsDirectory)
        {
            foreach (var child in file.ListFiles() ?? [])
            {
                DeleteRecursively(child);
            }
        }
        file.Delete();
    }
}
#endif
