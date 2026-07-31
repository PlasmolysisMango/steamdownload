#if ANDROID
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using SteamDl.Maui.Services;
using File = Java.IO.File;

namespace SteamDl.Maui.Platforms.Android;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public sealed class MainActivity : MauiAppCompatActivity
{
    const int PickDirectoryRequest = 2001;
    const int PermissionRequest = 100;
    TaskCompletionSource<string?>? _pickDirectoryTcs;

    public static MainActivity? Current { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Current = this;
        try { StartEngineService(); } catch { }
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(Current, this)) Current = null;
        base.OnDestroy();
    }

    public void StartEngineService()
    {
        var intent = new Intent(this, typeof(EngineForegroundService));
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            StartForegroundService(intent);
        }
        else
        {
            StartService(intent);
        }
    }

    public void RequestRuntimePermissions()
    {
        var wanted = new List<string>();
        if (CheckSelfPermission(global::Android.Manifest.Permission.WriteExternalStorage) != Permission.Granted)
        {
            wanted.Add(global::Android.Manifest.Permission.WriteExternalStorage);
        }
        if (wanted.Count > 0) RequestPermissions(wanted.ToArray(), PermissionRequest);
    }

    public Task<string?> PickDirectoryAsync()
    {
        if (_pickDirectoryTcs is not null) return _pickDirectoryTcs.Task;
        _pickDirectoryTcs = new TaskCompletionSource<string?>();
        try
        {
            var intent = new Intent(Intent.ActionOpenDocumentTree);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission | ActivityFlags.GrantPrefixUriPermission);
            StartActivityForResult(intent, PickDirectoryRequest);
        }
        catch
        {
            _pickDirectoryTcs.TrySetResult(null);
            _pickDirectoryTcs = null;
        }
        return _pickDirectoryTcs?.Task ?? Task.FromResult<string?>(null);
    }

    protected override void OnActivityResult(int requestCode, [GeneratedEnum] Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != PickDirectoryRequest) return;

        var tcs = _pickDirectoryTcs;
        _pickDirectoryTcs = null;
        if (tcs is null) return;

        var uri = data?.Data;
        if (resultCode != Result.Ok || uri is null)
        {
            tcs.TrySetResult(null);
            return;
        }

        try
        {
            var flags = data!.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
            ContentResolver?.TakePersistableUriPermission(uri, flags);
        }
        catch
        {
        }
        tcs.TrySetResult(ResolveTreeUriToPath(uri));
    }

    string? ResolveTreeUriToPath(Android.Net.Uri uri)
    {
        try
        {
            var docId = DocumentsContract.GetTreeDocumentId(uri);
            if (string.IsNullOrWhiteSpace(docId)) return null;
            var parts = docId.Split(new[] { ':' }, 2);
            var volume = parts[0];
            var relative = parts.Length > 1 ? parts[1].TrimStart('/') : "";
            if (string.Equals(volume, "primary", StringComparison.OrdinalIgnoreCase))
            {
                return new File("/storage/emulated/0", relative).AbsolutePath;
            }
            var storagePath = new File($"/storage/{volume}", relative);
            if (storagePath.Exists()) return storagePath.AbsolutePath;
            var mediaRwPath = new File($"/mnt/media_rw/{volume}", relative);
            return mediaRwPath.Exists() ? mediaRwPath.AbsolutePath : storagePath.AbsolutePath;
        }
        catch
        {
            return null;
        }
    }

    public override void OnBackPressed()
    {
        MoveTaskToBack(true);
    }
}

public sealed class AndroidPlatformFeatures : IPlatformFeatures
{
    public bool IsAndroid => true;

    public Task StartEngineServiceAsync()
    {
        MainActivity.Current?.StartEngineService();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, object>> EngineServiceStatusAsync() =>
        Task.FromResult<IReadOnlyDictionary<string, object>>(EngineForegroundService.StatusSnapshot());

    public Task<string?> PickDirectoryAsync() => MainActivity.Current?.PickDirectoryAsync() ?? Task.FromResult<string?>(null);

    public Task RequestPermissionsAsync()
    {
        MainActivity.Current?.RequestRuntimePermissions();
        return Task.CompletedTask;
    }
}
#endif
